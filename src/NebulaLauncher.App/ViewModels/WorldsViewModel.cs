using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;
using Formatting = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 世界页：存档列表、备份/还原、整合包导入。
///
/// 存档目录取当前生效目标的游戏目录（<see cref="IInstanceContext.GameDirectory"/>）下的 saves：
/// 选了实例就读实例目录，没选实例才是全局 <c>.minecraft</c>。副标题标明当前目标，
/// 否则用户在实例里存的档和全局目录里的档看起来一模一样，分不清页面在读哪一份。
/// 依赖可选，Core 的 DI 未接入时页面给出明确提示而不是让导航抛异常。
/// </summary>
public sealed partial class WorldsViewModel : PageViewModelBase
{
    private const string MissingServiceMessage = "核心服务尚未接入：世界、备份与整合包导入暂不可用。";

    /// <summary>同时加载的缩略图数量上限：一次刷新几十个档时，放开并发会把磁盘 IO 排队成毛刺。</summary>
    private const int IconLoadConcurrency = 3;

    private readonly IBackupService? _backupService;
    private readonly IModpackService? _modpackService;
    private readonly IContentService? _contentService;
    private readonly IPlatformService? _platform;
    private readonly ILauncherPaths? _paths;
    private readonly IInstanceContext? _instanceContext;
    private readonly IWorldIconService? _worldIcons;

    private string? _pendingArchivePath;

    /// <summary>本轮缩略图加载的取消源；刷新或离开页面时取消，旧结果不再回填到新卡片上。</summary>
    private CancellationTokenSource? _iconLoadCts;

    /// <summary>副标题（含当前存档目标），见 <see cref="UpdateSubtitle"/>。</summary>
    private string _subtitle = "存档、备份与整合包导入 · 全局 (.minecraft)";

    public WorldsViewModel(
        IBackupService? backupService = null,
        IModpackService? modpackService = null,
        IContentService? contentService = null,
        IPlatformService? platform = null,
        ILauncherPaths? paths = null,
        IInstanceContext? instanceContext = null,
        IWorldIconService? worldIcons = null)
    {
        _backupService = backupService;
        _modpackService = modpackService;
        _contentService = contentService;
        _platform = platform;
        _paths = paths;
        _instanceContext = instanceContext;
        _worldIcons = worldIcons;
    }

    public override string Title => "世界";

    public override string? Subtitle => _subtitle;

    public override string Key => NavigationKeys.Worlds;

    /// <summary>
    /// 存档读取目录：当前实例的目录优先，实例上下文缺失或损坏时退回全局 <c>.minecraft</c>。
    ///
    /// 不再直接用 <see cref="ILauncherPaths.MinecraftDirectory"/>：那样在实例里存的世界
    /// 永远读不到，页面会显示成「没有存档」。
    /// </summary>
    public string GameDirectory
    {
        get
        {
            if (_instanceContext is not null)
            {
                try
                {
                    var directory = _instanceContext.GameDirectory;
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        return directory;
                    }
                }
                catch (Exception)
                {
                    // 实例上下文异常时按全局目录渲染，不能让整页白屏。
                }
            }

            return _paths?.MinecraftDirectory ?? string.Empty;
        }
    }

    public ObservableCollection<WorldCardViewModel> Worlds { get; } = new();

    public ObservableCollection<BackupCardViewModel> Backups { get; } = new();

    // ---------------- 页面状态 ----------------

    [ObservableProperty]
    private string _tab = "worlds";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _summaryText = "尚未加载";

    public bool IsWorldsTab => Tab == "worlds";

    public bool IsBackupsTab => Tab == "backups";

    public bool HasWorlds => Worlds.Count > 0;

    public bool HasBackups => Backups.Count > 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool WorldsEmpty => !IsLoading && !HasError && Worlds.Count == 0;

    public bool BackupsEmpty => !IsLoading && !HasError && Backups.Count == 0;

    public string BackupsSummaryText => Backups.Count == 0 ? "暂无备份" : $"{Backups.Count} 个备份";

    public bool HasService => _backupService is not null;

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsWorldsTab));
        OnPropertyChanged(nameof(IsBackupsTab));
    }

    partial void OnIsLoadingChanged(bool value) => NotifyListState();

    partial void OnErrorMessageChanged(string? value) => NotifyListState();

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (tab is "worlds" or "backups")
        {
            Tab = tab;
        }
    }

    public override async Task OnNavigatedToAsync()
    {
        // 先让实例上下文跟上「现在选的是哪个实例」，再按它的目录读存档。
        var instanceError = await LoadInstanceContextAsync();

        await RefreshAsync();

        // 导出要选实例，和存档列表同时刷新；失败只影响导出区，不覆盖世界列表的状态。
        await RefreshExportTargetsAsync();

        // 有错就压在「已刷新」之上：RefreshAsync 会先清空状态栏，顺序反了这条提示就看不见。
        if (instanceError is not null)
        {
            StatusMessage = instanceError;
        }

        ApplyScreenshotImportResult();
        ApplyScreenshotEditor();
    }

    /// <summary>
    /// 截图模式的 <c>-Section imported</c>：把「导入完成 + 待手动安装清单」摆出来。
    ///
    /// 导入完成卡片只在真正导入过一次之后出现，而截图模式没有点击能力，
    /// 不预置的话「N 个 mod 需要手动安装」这句文案与「查看清单」面板永远截不到、等于无法验收。
    /// 只在截图模式下生效：正常运行时分支不进入（与 <c>ModsViewModel.ApplyScreenshotUpdates</c> 同一约定）。
    /// </summary>
    private void ApplyScreenshotImportResult()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "imported", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string instanceName = "例：1.12.2 整合包";

        PendingAddons.Clear();
        PendingAddons.Add(new ModpackPendingAddon
        {
            Name = "jei-1.12.2-4.16.1.302.jar",
            ProjectId = "238222",
            FileId = "3835743",
            Reason = "CurseForge 与 Modrinth 都没有查到可下载的文件",
            WebUrl = "https://www.curseforge.com/projects/238222/files/3835743",
        });
        PendingAddons.Add(new ModpackPendingAddon
        {
            Name = "betterfps-1.4.8.jar",
            ProjectId = "betterfps",
            FileId = "1.4.8",
            Reason = "作者关闭了第三方分发，只能去网页手动下载",
            WebUrl = "https://modrinth.com/mod/betterfps/version/1.4.8",
        });
        PendingAddons.Add(new ModpackPendingAddon
        {
            Name = "oldlib-core-1.0.jar",
            ProjectId = "oldlib-core",
            FileId = "1.0",
            Reason = "项目已从 CurseForge 删除",
            WebUrl = "https://modrinth.com/mod/oldlib-core",
        });

        OnPropertyChanged(nameof(HasPendingAddons));

        ImportResult = $"整合包「{instanceName}」已导入，游戏与加载器已一并安装"
                       + $"；还有 {PendingAddons.Count} 个 mod 需要手动安装（{BuildPreviewOfPendingAddons()}）";
        ImportedDirectory = Path.Combine(_paths?.VersionsDirectory ?? @"C:\NebulaLauncher\versions", "1.12.2 整合包");
        IsPendingAddonListVisible = true;
    }

    /// <summary>
    /// 截图模式的 <c>-Section editor</c>：打开第一个存档的编辑面板。
    ///
    /// 编辑面板默认收起，不预置的话「编辑存档」这块在截图里永远不可见，等于无法验收。
    /// 只在截图模式下生效（与 <see cref="ApplyScreenshotImportResult"/> 同一约定）。
    /// </summary>
    private void ApplyScreenshotEditor()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "editor", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var first = Worlds.FirstOrDefault();
        if (first is not null)
        {
            OpenWorldEditor(first);
        }
    }

    private string BuildPreviewOfPendingAddons()
    {
        var names = string.Join("、", PendingAddons.Take(3).Select(item => item.Name));
        return PendingAddons.Count > 3 ? $"{names} 等 {PendingAddons.Count} 个" : names;
    }

    // ---------------- 当前目标 ----------------

    /// <summary>
    /// 读取当前实例。失败只回传原因，不抛出：页面照常用全局目录渲染，
    /// 和「实例被删了但页面还要能用」的既有约定一致。
    /// </summary>
    private async Task<string?> LoadInstanceContextAsync()
    {
        if (_instanceContext is null)
        {
            UpdateSubtitle();
            return null;
        }

        string? error = null;
        try
        {
            await _instanceContext.LoadAsync();
        }
        catch (Exception ex)
        {
            error = $"读取当前实例失败，已按全局目录读取存档：{ex.Message}";
        }

        UpdateSubtitle();
        return error;
    }

    /// <summary>目标名：实例名，或未隔离时的「全局 (.minecraft)」。</summary>
    private string DescribeTarget()
    {
        if (_instanceContext is not null)
        {
            try
            {
                var current = _instanceContext.Current;
                if (current is not null)
                {
                    return string.IsNullOrWhiteSpace(current.Name) ? "实例" : $"实例 {current.Name}";
                }
            }
            catch (Exception)
            {
                // 上下文损坏时按全局目录展示，与 GameDirectory 的退化路径保持一致。
            }
        }

        return "全局 (.minecraft)";
    }

    private void UpdateSubtitle()
    {
        var text = $"存档、备份与整合包导入 · {DescribeTarget()}";

        if (string.Equals(text, _subtitle, StringComparison.Ordinal))
        {
            return;
        }

        _subtitle = text;
        OnPropertyChanged(nameof(Subtitle));
    }

    // ---------------- 列表加载 ----------------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            if (_backupService is null)
            {
                ErrorMessage = MissingServiceMessage;
                return;
            }

            var worlds = await _backupService.ListWorldsAsync(GameDirectory);
            Worlds.Clear();
            foreach (var world in worlds)
            {
                Worlds.Add(new WorldCardViewModel(world));
            }

            // 缩略图异步补：列表先落位，图标随后逐张填，刷新不会被图标解码卡住。
            StartIconLoads();

            var backups = await _backupService.ListBackupsAsync();
            Backups.Clear();
            foreach (var entry in backups)
            {
                Backups.Add(new BackupCardViewModel(entry));
            }

            StatusMessage = $"已刷新 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"读取世界与备份失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyListState();
        }
    }

    /// <summary>
    /// 取消上一轮缩略图加载并丢弃结果。
    ///
    /// 页面是 DI 单例：切页不销毁实例，若不取消，离开页面后仍在解码的图标会把路径写回已经不显示的卡片；
    /// 刷新时新卡片与旧卡片是两个对象，旧结果也不能回填。
    /// </summary>
    public override Task OnNavigatedFromAsync()
    {
        _iconLoadCts?.Cancel();
        return base.OnNavigatedFromAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconLoadCts?.Cancel();
            _iconLoadCts?.Dispose();
            _iconLoadCts = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 为当前列表里带图标的存档启动异步加载。
    ///
    /// 只对 <see cref="WorldInfo.HasIcon"/> 为 true 的卡片发起请求：没有图标的卡（绝大多数演示/新建世界）
    /// 连一次磁盘读都不该有。没有任何卡片有图标时直接返回，不建取消源。
    /// </summary>
    private void StartIconLoads()
    {
        _iconLoadCts?.Cancel();
        _iconLoadCts?.Dispose();
        _iconLoadCts = null;

        if (_worldIcons is null || Worlds.Count == 0)
        {
            return;
        }

        var cards = Worlds.Where(card => card.Source.HasIcon).ToArray();
        if (cards.Length == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _iconLoadCts = cts;

        // 故意不 await：缩略图不是页面可用的前置条件，失败也只是留占位块。
        // LoadIconAsync 内部把异常全部吞成「不填路径」，不会产生未观察的任务异常。
        _ = LoadIconsAsync(cards, cts.Token);
    }

    /// <summary>并发上限 3 地取缓存路径。路径写回 ViewModel 属性必须在 UI 线程，所以不写 ConfigureAwait(false)。</summary>
    private async Task LoadIconsAsync(IReadOnlyList<WorldCardViewModel> cards, CancellationToken cancellationToken)
    {
        var service = _worldIcons;
        if (service is null)
        {
            return;
        }

        using var gate = new SemaphoreSlim(IconLoadConcurrency);
        var tasks = cards.Select(card => LoadIconAsync(service, card, gate, cancellationToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private static async Task LoadIconAsync(
        IWorldIconService service,
        WorldCardViewModel card,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var path = await service.GetIconPathAsync(card.Directory, cancellationToken);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    card.IconPath = path;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 刷新 / 切页正常取消。
        }
        catch (Exception)
        {
            // 失败静默：卡片本来就留着占位块，弹错误反而打断列表浏览。
        }
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(WorldsEmpty));
        OnPropertyChanged(nameof(BackupsEmpty));
        OnPropertyChanged(nameof(BackupsSummaryText));
        SummaryText = Worlds.Count == 0
            ? "暂无存档"
            : $"{Worlds.Count} 个存档 · 共 {Formatting.FormatSize(Worlds.Sum(world => world.Source.SizeBytes))}";
    }

    // ---------------- 单个世界操作 ----------------

    [RelayCommand]
    private async Task BackupWorldAsync(WorldCardViewModel? world)
    {
        if (world is null || world.IsBusy)
        {
            return;
        }

        if (_backupService is null)
        {
            world.Status = "备份服务尚未接入";
            return;
        }

        world.IsBusy = true;
        world.Progress = 0;
        world.Status = "正在打包存档…";
        try
        {
            var progress = new Progress<double>(value => world.Progress = Math.Clamp(value, 0, 1));
            var entry = await _backupService.CreateAsync(world.Directory, null, progress);
            Backups.Insert(0, new BackupCardViewModel(entry));
            world.Progress = 1;
            world.Status = $"已备份 · {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"世界「{world.Name}」已备份到备份目录";
            NotifyListState();
        }
        catch (Exception ex)
        {
            world.Status = $"备份失败：{ex.Message}";
        }
        finally
        {
            world.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenWorldFolderAsync(WorldCardViewModel? world)
    {
        if (world is null)
        {
            return;
        }

        if (_platform is null)
        {
            world.Status = "平台服务尚未接入";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(world.Directory);
        }
        catch (Exception ex)
        {
            world.Status = $"打开目录失败：{ex.Message}";
        }
    }

    // ---------------- 存档编辑（level.dat） ----------------

    /// <summary>游戏模式下拉项，索引即 NBT 的 <c>GameType</c>。</summary>
    public IReadOnlyList<string> GameModeOptions { get; } = ["生存", "创造", "冒险", "旁观"];

    /// <summary>难度下拉项，索引即 NBT 的 <c>Difficulty</c>。</summary>
    public IReadOnlyList<string> DifficultyOptions { get; } = ["和平", "简单", "普通", "困难"];

    /// <summary>正在编辑的世界；面板收起时为 null。</summary>
    [ObservableProperty]
    private WorldCardViewModel? _editingWorld;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _editLevelName = string.Empty;

    /// <summary>种子用字符串承载：<see cref="long"/> 输入框在 Avalonia 里会先把非法输入吞成 0。</summary>
    [ObservableProperty]
    private string _editSeed = string.Empty;

    [ObservableProperty]
    private int _editGameModeIndex;

    [ObservableProperty]
    private int _editDifficultyIndex = 2;

    [ObservableProperty]
    private bool _editAllowCommands;

    [ObservableProperty]
    private bool _editKeepInventory;

    [ObservableProperty]
    private bool _isSavingEdit;

    [ObservableProperty]
    private string? _editError;

    public string EditTargetText => EditingWorld is null ? string.Empty : $"正在编辑「{EditingWorld.Name}」";

    partial void OnEditingWorldChanged(WorldCardViewModel? value) => OnPropertyChanged(nameof(EditTargetText));

    [RelayCommand]
    private void OpenWorldEditor(WorldCardViewModel? world)
    {
        if (world is null)
        {
            return;
        }

        EditingWorld = world;
        EditError = null;
        EditLevelName = world.Name;
        EditSeed = world.Source.Seed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EditGameModeIndex = Math.Clamp(world.Source.GameType ?? 0, 0, GameModeOptions.Count - 1);
        EditDifficultyIndex = Math.Clamp(world.Source.Difficulty ?? 2, 0, DifficultyOptions.Count - 1);
        EditAllowCommands = world.Source.AllowCommands ?? false;
        EditKeepInventory = world.Source.KeepInventory ?? false;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelWorldEdit()
    {
        IsEditorOpen = false;
        EditingWorld = null;
        EditError = null;
    }

    [RelayCommand]
    private async Task SaveWorldEditAsync()
    {
        if (EditingWorld is null || IsSavingEdit)
        {
            return;
        }

        if (_backupService is null)
        {
            EditError = MissingServiceMessage;
            return;
        }

        long? seed = null;
        var seedText = EditSeed.Trim();
        if (seedText.Length > 0)
        {
            if (!long.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                EditError = "种子必须是整数（可为负数，范围 -9223372036854775808 ～ 9223372036854775807）。";
                return;
            }

            seed = parsed;
        }

        // 具名属性在 await 之后不再保证非空，先落到局部变量。
        var target = EditingWorld;
        var levelName = EditLevelName.Trim();
        IsSavingEdit = true;
        EditError = null;
        try
        {
            var result = await _backupService.UpdateWorldAsync(
                target.Directory,
                new WorldEditRequest
                {
                    Seed = seed,
                    GameMode = EditGameModeIndex,
                    Difficulty = EditDifficultyIndex,
                    AllowCommands = EditAllowCommands,
                    KeepInventory = EditKeepInventory,
                    LevelName = levelName.Length == 0 ? null : levelName,
                });

            if (!result.Success)
            {
                EditError = string.IsNullOrWhiteSpace(result.Error) ? "保存失败。" : result.Error;
                return;
            }

            var editedName = target.Name;
            IsEditorOpen = false;
            EditingWorld = null;
            StatusMessage = $"已保存世界「{editedName}」的 level.dat · {DateTime.Now:HH:mm:ss}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            EditError = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsSavingEdit = false;
        }
    }

    [RelayCommand]
    private void RequestDeleteWorld(WorldCardViewModel? world)
    {
        if (world is not null)
        {
            world.IsConfirmingDelete = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteWorld(WorldCardViewModel? world)
    {
        if (world is not null)
        {
            world.IsConfirmingDelete = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmDeleteWorldAsync(WorldCardViewModel? world)
    {
        if (world is null || world.IsBusy)
        {
            return;
        }

        if (_contentService is null)
        {
            world.Status = "内容服务尚未接入";
            world.IsConfirmingDelete = false;
            return;
        }

        world.IsBusy = true;
        world.Status = "正在删除…";
        try
        {
            await _contentService.DeleteAsync(new ContentItem
            {
                FilePath = world.Directory,
                FileName = Path.GetFileName(world.Directory),
                Kind = ContentKind.World,
            });

            // ContentService 内部会吞掉 IO 异常只记日志，所以这里用目录是否还在来判定结果。
            if (Directory.Exists(world.Directory))
            {
                world.Status = "删除失败：目录仍存在，可能被游戏进程占用";
                return;
            }

            Worlds.Remove(world);
            StatusMessage = $"已删除世界「{world.Name}」";
            NotifyListState();
        }
        catch (Exception ex)
        {
            world.Status = $"删除失败：{ex.Message}";
        }
        finally
        {
            world.IsBusy = false;
            world.IsConfirmingDelete = false;
        }
    }

    // ---------------- 备份操作 ----------------

    [RelayCommand]
    private void RequestDeleteBackup(BackupCardViewModel? backup)
    {
        if (backup is not null)
        {
            backup.IsConfirmingDelete = true;
        }
    }

    [RelayCommand]
    private void CancelDeleteBackup(BackupCardViewModel? backup)
    {
        if (backup is not null)
        {
            backup.IsConfirmingDelete = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmDeleteBackupAsync(BackupCardViewModel? backup)
    {
        if (backup is null || backup.IsBusy)
        {
            return;
        }

        if (_backupService is null)
        {
            backup.Status = "备份服务尚未接入";
            backup.IsConfirmingDelete = false;
            return;
        }

        backup.IsBusy = true;
        try
        {
            await _backupService.DeleteAsync(backup.Source);
            if (File.Exists(backup.Source.FilePath))
            {
                backup.Status = "删除失败：备份文件仍存在";
                return;
            }

            Backups.Remove(backup);
            StatusMessage = $"已删除备份「{backup.WorldName}」";
            NotifyListState();
        }
        catch (Exception ex)
        {
            backup.Status = $"删除失败：{ex.Message}";
        }
        finally
        {
            backup.IsBusy = false;
            backup.IsConfirmingDelete = false;
        }
    }

    /// <summary>
    /// 还原备份到用户选择的游戏目录。目标目录由视图层的文件夹选择器提供，
    /// ViewModel 不接触 TopLevel。
    /// </summary>
    public async Task RestoreAsync(BackupCardViewModel? backup, string? targetGameDirectory)
    {
        if (backup is null || backup.IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(targetGameDirectory))
        {
            return;
        }

        if (_backupService is null)
        {
            backup.Status = "备份服务尚未接入";
            return;
        }

        backup.IsBusy = true;
        backup.Progress = 0;
        backup.Status = "正在还原…";
        try
        {
            var progress = new Progress<double>(value => backup.Progress = Math.Clamp(value, 0, 1));
            await _backupService.RestoreAsync(backup.Source, targetGameDirectory, progress);
            backup.Progress = 1;

            // 还原目标是当前 .minecraft 时，列表立刻刷新出新的存档。
            if (PathsEqual(targetGameDirectory, GameDirectory))
            {
                await RefreshAsync();
            }

            backup.Status = $"已还原到 {targetGameDirectory}";
            StatusMessage = $"备份「{backup.WorldName}」已还原到 {targetGameDirectory}";
        }
        catch (Exception ex)
        {
            backup.Status = $"还原失败：{ex.Message}";
        }
        finally
        {
            backup.IsBusy = false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- 整合包导出 ----------------

    /// <summary>可导出的实例目标（已排除全局 <c>.minecraft</c>：它不是一个完整实例）。</summary>
    public ObservableCollection<ContentTarget> ExportTargets { get; } = new();

    [ObservableProperty]
    private ContentTarget? _selectedExportTarget;

    [ObservableProperty]
    private string _exportName = string.Empty;

    [ObservableProperty]
    private string _exportVersion = "1.0.0";

    [ObservableProperty]
    private string _exportArchivePath = string.Empty;

    [ObservableProperty]
    private bool _exportIncludeSaves;

    [ObservableProperty]
    private bool _exportIncludeConfig = true;

    [ObservableProperty]
    private bool _exportIncludeResourcePacks = true;

    [ObservableProperty]
    private bool _exportIncludeShaderPacks = true;

    [ObservableProperty]
    private bool _exportIncludeOptions = true;

    [ObservableProperty]
    private bool _exportIncludeDefaultConfigs = true;

    [ObservableProperty]
    private bool _exportIncludeServerList;

    [ObservableProperty]
    private bool _exportPreferModrinthHashes = true;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _exportProgress;

    [ObservableProperty]
    private string _exportProgressText = string.Empty;

    [ObservableProperty]
    private string? _exportError;

    [ObservableProperty]
    private string? _exportResult;

    public bool HasExportResult => !string.IsNullOrWhiteSpace(ExportResult);

    public string ExportTargetHint
    {
        get
        {
            if (_modpackService is null)
            {
                return MissingServiceMessage;
            }

            return ExportTargets.Count == 0
                ? "还没有可导出的实例：先在版本页创建或启动一个实例。"
                : "只打包实例的 mods / config 等目录，不带 libraries 与 assets。";
        }
    }

    public bool CanExport =>
        !IsExporting
        && _modpackService is not null
        && SelectedExportTarget is not null
        && !string.IsNullOrWhiteSpace(ExportArchivePath);

    partial void OnSelectedExportTargetChanged(ContentTarget? value)
    {
        if (value is not null)
        {
            ExportName = value.DisplayName;
        }

        NotifyExportCanExecute();
    }

    partial void OnExportArchivePathChanged(string value) => NotifyExportCanExecute();

    partial void OnIsExportingChanged(bool value) => NotifyExportCanExecute();

    partial void OnExportResultChanged(string? value) => OnPropertyChanged(nameof(HasExportResult));

    private void NotifyExportCanExecute() => ExportModpackCommand.NotifyCanExecuteChanged();

    /// <summary>工具行入口：从备份页点过来时切回存档页，导出卡片就在存档页顶部。</summary>
    [RelayCommand]
    private void FocusExportTab()
    {
        Tab = "worlds";
        ExportError = null;
    }

    /// <summary>由视图层拿到保存路径后调用：文件选择属于窗口能力，ViewModel 不接触 TopLevel。</summary>
    public void SetExportArchivePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            ExportArchivePath = path.Trim();
        }
    }

    /// <summary>
    /// 重新读取实例目标。失败只写 <see cref="ExportError"/>：世界列表仍应能正常显示，
    /// 不能因为实例目录损坏就让整页变错误态。
    /// </summary>
    private async Task RefreshExportTargetsAsync()
    {
        ExportTargets.Clear();
        SelectedExportTarget = null;

        if (_modpackService is null || _instanceContext is null)
        {
            OnPropertyChanged(nameof(ExportTargetHint));
            NotifyExportCanExecute();
            return;
        }

        try
        {
            var targets = await _instanceContext.ListTargetsAsync();

            foreach (var target in targets.Where(target => target.Kind == ContentTargetKind.Instance))
            {
                ExportTargets.Add(target);
            }

            var currentId = _instanceContext.Current?.Id;
            SelectedExportTarget =
                ExportTargets.FirstOrDefault(target => string.Equals(target.Key, currentId, StringComparison.Ordinal))
                ?? ExportTargets.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ExportError = $"读取实例列表失败：{ex.Message}";
        }

        OnPropertyChanged(nameof(ExportTargetHint));
        NotifyExportCanExecute();
    }

    /// <summary>执行导出。结果里的「N 个文件 · X MB · M 个模组未识别」直接来自 Core 的返回值。</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportModpackAsync()
    {
        if (_modpackService is null || SelectedExportTarget is null || IsExporting)
        {
            return;
        }

        var target = SelectedExportTarget;
        var archivePath = ExportArchivePath.Trim();

        if (archivePath.Length == 0)
        {
            ExportError = "请先选择整合包的保存位置。";
            return;
        }

        IsExporting = true;
        ExportError = null;
        ExportResult = null;
        ExportProgress = 0;
        ExportProgressText = "正在扫描实例文件…";

        try
        {
            var options = new ModpackExportOptions
            {
                Name = string.IsNullOrWhiteSpace(ExportName) ? target.DisplayName : ExportName.Trim(),
                Version = string.IsNullOrWhiteSpace(ExportVersion) ? "1.0.0" : ExportVersion.Trim(),
                IncludeSaves = ExportIncludeSaves,
                IncludeConfig = ExportIncludeConfig,
                IncludeResourcePacks = ExportIncludeResourcePacks,
                IncludeShaderPacks = ExportIncludeShaderPacks,
                IncludeOptions = ExportIncludeOptions,
                IncludeDefaultConfigs = ExportIncludeDefaultConfigs,
                IncludeServerList = ExportIncludeServerList,
                PreferModrinthHashes = ExportPreferModrinthHashes,
                MinecraftVersion = target.MinecraftVersion,
            };

            var progress = new Progress<DownloadProgress>(value =>
            {
                ExportProgress = Math.Clamp(value.Ratio, 0, 1);
                ExportProgressText = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? $"{value.CompletedFiles}/{value.TotalFiles} 个文件"
                    : value.CurrentFile;
            });

            var result = await _modpackService.ExportAsync(target.GameDirectory, archivePath, options, progress);

            if (!result.Success)
            {
                ExportError = string.IsNullOrWhiteSpace(result.Error) ? "导出失败：未知原因。" : result.Error;
                return;
            }

            ExportProgress = 1;
            var unresolved = result.UnresolvedMods > 0
                ? $" · {result.UnresolvedMods} 个模组未识别已原样打包"
                : string.Empty;
            ExportResult = $"{result.Files} 个文件 · {Formatting.FormatSize(result.Bytes)}{unresolved}";
            ExportArchivePath = result.ArchivePath ?? archivePath;
            StatusMessage = $"整合包已导出：{ExportArchivePath}";
        }
        catch (Exception ex)
        {
            ExportError = $"导出整合包失败：{ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    // ---------------- 整合包导入 ----------------

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    private ModpackPreviewCard? _importPreview;

    [ObservableProperty]
    private string _importInstanceName = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private double _importProgress;

    [ObservableProperty]
    private string _importProgressText = string.Empty;

    [ObservableProperty]
    private string? _importError;

    [ObservableProperty]
    private string? _importResult;

    [ObservableProperty]
    private string? _importedDirectory;

    /// <summary>
    /// 导入时是否顺带安装整合包声明的游戏与加载器。
    ///
    /// 默认开：只解压不解压出来的实例指向一个不存在的版本，玩家必须自己去版本页再装一次，
    /// 且版本号常装错（PCL2 的五条安装链都内置了这一步）。
    /// </summary>
    [ObservableProperty]
    private bool _installGameWithModpack = true;

    /// <summary>
    /// MCBBS 整合包 addons 里查不到直链、需要玩家手动装回来的条目。
    ///
    /// 与 <see cref="ImportResult"/> 分开：文案只占一行，清单可能几十条，塞进一句话会撑爆横幅。
    /// </summary>
    public ObservableCollection<ModpackPendingAddon> PendingAddons { get; } = [];

    [ObservableProperty]
    private bool _isPendingAddonListVisible;

    public bool HasImportPreview => ImportPreview is not null;

    /// <summary>当前预览的整合包是否声明了可安装的 MC 版本；否时不显示安装开关。</summary>
    public bool CanInstallGame => ImportPreview?.CanInstallGame == true;

    public bool CanConfirmImport =>
        !IsImporting && ImportPreview is not null && !string.IsNullOrWhiteSpace(ImportInstanceName);

    public bool HasImportResult => !string.IsNullOrWhiteSpace(ImportResult);

    /// <summary>有一条以上需要手动安装的 addon；导入完成卡片据此显示「查看清单」出口。</summary>
    public bool HasPendingAddons => PendingAddons.Count > 0;

    public string PendingAddonsToggleText => IsPendingAddonListVisible ? "收起清单" : "查看清单";

    partial void OnImportPreviewChanged(ModpackPreviewCard? value)
    {
        OnPropertyChanged(nameof(HasImportPreview));
        OnPropertyChanged(nameof(CanInstallGame));
        NotifyImportCanExecute();
    }

    partial void OnImportInstanceNameChanged(string value) => NotifyImportCanExecute();

    partial void OnIsImportingChanged(bool value) => NotifyImportCanExecute();

    partial void OnImportResultChanged(string? value) => OnPropertyChanged(nameof(HasImportResult));

    partial void OnIsPendingAddonListVisibleChanged(bool value) => OnPropertyChanged(nameof(PendingAddonsToggleText));

    private void NotifyImportCanExecute() => ConfirmImportCommand.NotifyCanExecuteChanged();

    /// <summary>由视图层拿到文件路径后调用：先解析出预览，再让用户确认。</summary>
    public async Task BeginImportAsync(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        if (_modpackService is null)
        {
            ErrorMessage = MissingServiceMessage;
            return;
        }

        Tab = "worlds";
        IsInspecting = true;
        ImportError = null;
        ImportResult = null;
        ImportedDirectory = null;
        ImportPreview = null;
        try
        {
            var preview = await _modpackService.InspectAsync(archivePath);
            ImportPreview = new ModpackPreviewCard(preview);
            ImportInstanceName = preview.Name;
            _pendingArchivePath = archivePath;
        }
        catch (Exception ex)
        {
            ImportError = $"读取整合包失败：{ex.Message}";
        }
        finally
        {
            IsInspecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmImport))]
    private async Task ConfirmImportAsync()
    {
        if (_pendingArchivePath is null || _modpackService is null || IsImporting)
        {
            return;
        }

        var instanceName = ImportInstanceName.Trim();
        if (instanceName.Length == 0)
        {
            ImportError = "实例名不能为空";
            return;
        }

        IsImporting = true;
        ImportError = null;
        ImportProgress = 0;
        PendingAddons.Clear();
        IsPendingAddonListVisible = false;
        OnPropertyChanged(nameof(HasPendingAddons));
        ImportProgressText = InstallGameWithModpack && CanInstallGame
            ? "正在安装游戏与加载器…"
            : "正在解压覆盖文件…";
        try
        {
            // 开关的取舍在导入开始这一刻落定：中途改勾选不会影响已经在跑的这一次。
            _modpackService.InstallGameOnImport = InstallGameWithModpack && CanInstallGame;

            var progress = new Progress<DownloadProgress>(value =>
            {
                ImportProgress = Math.Clamp(value.Ratio, 0, 1);
                ImportProgressText = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? $"{value.CompletedFiles}/{value.TotalFiles} 个文件"
                    : $"正在下载 {value.CurrentFile}";
            });

            var directory = await _modpackService.ImportAsync(_pendingArchivePath, instanceName, progress);
            ImportedDirectory = directory;

            // 导入是多阶段的，成功后仍可能有「装上了但加载器版本没标注」这类问题，
            // 只说一句「已导入」会让玩家错过它。
            var result = _modpackService.LastImport;

            PendingAddons.Clear();
            if (result is { HasPendingAddons: true })
            {
                foreach (var pendingAddon in result.PendingAddons)
                {
                    PendingAddons.Add(pendingAddon);
                }
            }

            OnPropertyChanged(nameof(HasPendingAddons));

            var message = result switch
            {
                { Warning.Length: > 0 } => $"整合包「{instanceName}」已导入为独立实例。{result.Warning}",
                { GameInstalled: true } => $"整合包「{instanceName}」已导入，游戏与加载器已一并安装",
                _ => $"整合包「{instanceName}」已导入为独立实例（未安装游戏，请到版本页安装后启动）",
            };

            // addons 反查有查不到的部分时必须在结果里说出来：否则玩家进游戏发现少一堆 mod，
            // 却不知道是包坏了还是启动器没下。清单本身在「查看清单」里。
            if (result is { HasPendingAddons: true })
            {
                message += $"；还有 {result.AddonsPending} 个 mod 需要手动安装（{result.PendingAddonsPreview}）";
            }

            ImportResult = message;

            ImportPreview = null;
            ImportProgress = 1;
            _pendingArchivePath = null;
            StatusMessage = ImportResult;
        }
        catch (Exception ex)
        {
            ImportError = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        ImportPreview = null;
        ImportError = null;
        ImportProgress = 0;
        ImportProgressText = string.Empty;
        ImportInstanceName = string.Empty;
        PendingAddons.Clear();
        IsPendingAddonListVisible = false;
        OnPropertyChanged(nameof(HasPendingAddons));
        _pendingArchivePath = null;
    }

    /// <summary>展开 / 收起「待手动安装」清单。</summary>
    [RelayCommand]
    private void TogglePendingAddons() => IsPendingAddonListVisible = !IsPendingAddonListVisible;

    /// <summary>在浏览器里打开某条待装内容的下载页。没有可用地址的条目不出按钮。</summary>
    [RelayCommand]
    private async Task OpenPendingAddonUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (_platform is null)
        {
            StatusMessage = "平台服务尚未接入，无法打开网页。";
            return;
        }

        try
        {
            await _platform.OpenUrlAsync(url);
        }
        catch (Exception ex)
        {
            // 此刻预览卡片已经隐藏，ImportError 没人渲染；错误走页面级状态行。
            StatusMessage = $"打开网页失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenImportedFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportedDirectory))
        {
            return;
        }

        if (_platform is null)
        {
            ImportError = "平台服务尚未接入，无法打开目录。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(ImportedDirectory);
        }
        catch (Exception ex)
        {
            ImportError = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>供视图层在文件选择器不可用时写入页面级错误。</summary>
    public void ReportError(string message) => ErrorMessage = message;
}

/// <summary>存档卡片。删除确认与备份进度都是「每张卡」的状态，所以包一层而不是塞进 Core 模型。</summary>
public sealed partial class WorldCardViewModel : ObservableObject
{
    public WorldCardViewModel(WorldInfo source) => Source = source;

    public WorldInfo Source { get; }

    public string Name => Source.Name;

    public string Directory => Source.Directory;

    public string SizeText => Formatting.FormatSize(Source.SizeBytes);

    public string VersionText => string.IsNullOrWhiteSpace(Source.VersionName) ? "版本未知" : Source.VersionName!;

    public string GameModeText => string.IsNullOrWhiteSpace(Source.GameMode) ? "—" : Source.GameMode!;

    public DateTimeOffset LastWriteTime => Source.LastWriteTime;

    /// <summary>
    /// 缩略图缓存文件路径；加载完成前、以及没有图标/加载失败时都为 null。
    ///
    /// 暴露路径而不是 <c>Bitmap</c>：位图解码留在视图层的转换器里（与模组页图标同一套机制），
    /// ViewModel 不持有需要手工释放的原生资源，缓存命中与失效也只由 Core 的缓存文件决定。
    /// </summary>
    [ObservableProperty]
    private string? _iconPath;

    /// <summary>是否已经有可显示的缩略图；为 false 时视图显示令牌色占位块。</summary>
    public bool HasIconImage => !string.IsNullOrWhiteSpace(IconPath);

    partial void OnIconPathChanged(string? value) => OnPropertyChanged(nameof(HasIconImage));

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _status;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool ShowActions => !IsBusy && !IsConfirmingDelete;

    public bool ShowConfirm => !IsBusy && IsConfirmingDelete;

    public bool ShowProgress => IsBusy;

    partial void OnIsConfirmingDeleteChanged(bool value) => NotifyLayout();

    partial void OnIsBusyChanged(bool value) => NotifyLayout();

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ShowConfirm));
        OnPropertyChanged(nameof(ShowProgress));
    }
}

/// <summary>备份卡片。还原/删除同样需要每张卡独立的忙碌与确认状态。</summary>
public sealed partial class BackupCardViewModel : ObservableObject
{
    public BackupCardViewModel(BackupEntry source) => Source = source;

    public BackupEntry Source { get; }

    public string WorldName => Source.WorldName;

    public string SizeText => Formatting.FormatSize(Source.SizeBytes);

    public DateTimeOffset CreatedAt => Source.CreatedAt;

    public string NoteText => string.IsNullOrWhiteSpace(Source.Note) ? "无备注" : Source.Note;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _status;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool ShowActions => !IsBusy && !IsConfirmingDelete;

    public bool ShowConfirm => !IsBusy && IsConfirmingDelete;

    public bool ShowProgress => IsBusy;

    partial void OnIsConfirmingDeleteChanged(bool value) => NotifyLayout();

    partial void OnIsBusyChanged(bool value) => NotifyLayout();

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ShowConfirm));
        OnPropertyChanged(nameof(ShowProgress));
    }
}

/// <summary>整合包预览卡片的展示投影。</summary>
public sealed class ModpackPreviewCard
{
    private readonly ModpackPreview _preview;

    public ModpackPreviewCard(ModpackPreview preview) => _preview = preview;

    public string Name => _preview.Name;

    public string GameVersionText =>
        string.IsNullOrWhiteSpace(_preview.MinecraftVersion) ? "未标注" : _preview.MinecraftVersion;

    public string LoaderText => _preview.Loader switch
    {
        LoaderKind.Fabric => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "Fabric" : $"Fabric {_preview.LoaderVersion}",
        LoaderKind.Quilt => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "Quilt" : $"Quilt {_preview.LoaderVersion}",
        LoaderKind.Forge => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "Forge" : $"Forge {_preview.LoaderVersion}",
        LoaderKind.NeoForge => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "NeoForge" : $"NeoForge {_preview.LoaderVersion}",
        _ => "原版",
    };

    public string FormatText => _preview.Format switch
    {
        ModpackFormat.ModrinthModpack => "Modrinth 整合包",
        ModpackFormat.CurseForgeZip => "CurseForge 整合包",
        ModpackFormat.MultiMcZip => "MultiMC 实例包",
        ModpackFormat.McbbsZip => "MCBBS 整合包",
        ModpackFormat.HmclZip => "HMCL 整合包",
        // 外层是「带启动器的分发包」，内层才是真正的整合包；预览阶段就要说清楚，
        // 不能显示「未知格式 / 只能解压」而导入入口又拒绝（REVIEW-R34-R36 P3-12）。
        ModpackFormat.LauncherPack => "带启动器的整合包",
        ModpackFormat.PlainZip => "普通压缩包",
        _ => "未知格式",
    };

    /// <summary>该整合包是否声明了可安装的 MC 版本（决定导入页是否显示「同时安装游戏」开关）。</summary>
    public bool CanInstallGame => _preview.RequiresGameInstall;

    /// <summary>
    /// 导入时会安装什么。写清楚「装的是哪个组合」比只显示「加载器：Fabric」有用——
    /// 加载器版本缺失时这句会明说「最新版」，玩家才知道装完可能与包不匹配。
    /// </summary>
    public string InstallPlanText
    {
        get
        {
            if (_preview.Format == ModpackFormat.LauncherPack)
            {
                // 与导入入口的拒绝文案保持一致：下一步是「先解压、再导入内层文件」。
                return "这是带启动器的分发包：请先解压，再导入里面的 modpack 文件";
            }

            if (!CanInstallGame)
            {
                return "未声明游戏版本，只能解压文件";
            }

            var loader = _preview.Loader switch
            {
                LoaderKind.Vanilla => "原版",
                LoaderKind.Fabric => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "Fabric 最新版" : $"Fabric {_preview.LoaderVersion}",
                LoaderKind.Quilt => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "Quilt 最新版" : $"Quilt {_preview.LoaderVersion}",
                LoaderKind.Forge => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "Forge 最新版" : $"Forge {_preview.LoaderVersion}",
                LoaderKind.NeoForge => string.IsNullOrWhiteSpace(_preview.LoaderVersion) ? "NeoForge 最新版" : $"NeoForge {_preview.LoaderVersion}",
                _ => "原版",
            };

            return $"将安装 Minecraft {_preview.MinecraftVersion} + {loader}";
        }
    }

    public string FilesText =>
        $"{_preview.FileCount} 个文件 · {Formatting.FormatSize(_preview.TotalBytes)}";

    public string VersionText => string.IsNullOrWhiteSpace(_preview.Version) ? "未标注版本" : _preview.Version;

    public string? Description => _preview.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}
