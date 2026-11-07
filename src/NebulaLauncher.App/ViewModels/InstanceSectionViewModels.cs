using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using Formatting = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 实例详情面板的「世界」分区（票 #89）。
///
/// 与独立的 <see cref="WorldsViewModel"/> 是两套入口、同一套服务：
/// 那一个是「在全局范围里看所有实例的存档」，目标来自 <see cref="IInstanceContext"/>；
/// 这一个是「看当前选中的这一个实例」，目标由详情面板的选中行显式推进来
/// （<see cref="SetTargetAsync"/>）。两条入口不合并，因为「选中一行」不等于
/// 「把全局上下文切过去」——前者只是浏览，后者会改写 settings.json 的 LastInstanceId。
///
/// 服务全部可选：DI 未接入时分区显示明确提示，而不是让版本页整页崩掉。
/// </summary>
public sealed partial class InstanceWorldsSectionViewModel : ObservableObject
{
    private const string MissingServiceMessage = "备份服务未接入，世界与备份暂不可用。";

    private readonly IBackupService? _backupService;
    private readonly IWorldIconService? _worldIcons;
    private readonly IPlatformService? _platform;

    /// <summary>本轮缩略图加载的取消源：换目标或刷新时取消，旧结果不回填到新行上。</summary>
    private CancellationTokenSource? _iconCts;

    private string? _targetDirectory;
    private string? _targetName;

    public InstanceWorldsSectionViewModel(
        IBackupService? backupService = null,
        IWorldIconService? worldIcons = null,
        IPlatformService? platform = null)
    {
        _backupService = backupService;
        _worldIcons = worldIcons;
        _platform = platform;
    }

    public ObservableCollection<WorldRowItem> Worlds { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _resultText;

    /// <summary>目标实例名，只用于空态与提示文案。</summary>
    public string TargetText => string.IsNullOrWhiteSpace(_targetName) ? "这个实例" : _targetName;

    public bool HasService => _backupService is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    public bool HasWorlds => Worlds.Count > 0;

    /// <summary>空态：读完了、没出错、一个存档都没有。</summary>
    public bool IsEmpty => !IsLoading && !HasError && Worlds.Count == 0;

    /// <summary>
    /// 切到另一个实例。
    ///
    /// 幂等：同一个目录重复调用不重新读盘——详情面板每次选中变化都会调用它，
    /// 不幂等的话列表滚动时会反复触发磁盘扫描。
    /// 异常在这里收住：调用方是选中变化的副作用，不 await，未观察的任务异常会在 GC 时带走进程。
    /// </summary>
    public async Task SetTargetAsync(string? gameDirectory, string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            _targetDirectory = null;
            _targetName = null;
            Worlds.Clear();
            NotifyState();
            return;
        }

        var changed = !string.Equals(_targetDirectory, gameDirectory, StringComparison.Ordinal);

        _targetDirectory = gameDirectory;
        _targetName = instanceName;

        if (!changed)
        {
            return;
        }

        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorText = $"读取这个实例的存档失败：{ex.Message}";
            NotifyState();
        }
    }

    /// <summary>重新读存档与备份清单。命令与目标切换共用。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorText = null;
        ResultText = null;
        NotifyState();

        try
        {
            if (_backupService is null || string.IsNullOrWhiteSpace(_targetDirectory))
            {
                ErrorText = MissingServiceMessage;
                return;
            }

            var worlds = await _backupService.ListWorldsAsync(_targetDirectory).ConfigureAwait(false);

            // 备份清单是全局的（服务没有按目录过滤的入参），按世界名归并到行上。
            // 一份备份的 WorldName 就是存档目录名，所以这个匹配关系是稳定的。
            var backups = await _backupService.ListBackupsAsync().ConfigureAwait(false);
            var byWorld = backups
                .GroupBy(entry => entry.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(entry => entry.CreatedAt).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            Worlds.Clear();

            foreach (var world in worlds.OrderByDescending(w => w.LastWriteTime))
            {
                var row = new WorldRowItem(world);

                if (byWorld.TryGetValue(world.Name, out var entries))
                {
                    row.SetBackups(entries);
                }

                Worlds.Add(row);
            }

            StartIconLoads();
        }
        catch (Exception ex)
        {
            ErrorText = $"读取世界与备份失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyState();
        }
    }

    /// <summary>
    /// 备份一个存档。备份是安全动作，不需要二次确认；
    /// 进度显示在行内，结果写进分区的结果条（契约规则 10：进度不走弹窗）。
    /// </summary>
    [RelayCommand]
    private async Task BackupWorldAsync(WorldRowItem? row)
    {
        if (row is null || _backupService is null || IsLoading)
        {
            return;
        }

        row.BeginBusy("正在备份…");

        try
        {
            var progress = new Progress<double>(value => row.Progress = value);
            var entry = await _backupService.CreateAsync(
                row.Directory,
                note: $"来自实例详情 · {TargetText}",
                progress: progress).ConfigureAwait(false);

            row.AddBackup(entry);
            row.CompleteBusy($"已备份到 {Formatting.FormatSize(entry.SizeBytes)}");
            ResultText = $"已备份「{row.Name}」（{Formatting.FormatSize(entry.SizeBytes)}）";
        }
        catch (Exception ex)
        {
            row.FailBusy($"备份失败：{ex.Message}");
            ErrorText = null;
            ResultText = null;
            OnPropertyChanged(nameof(HasResult));
            ResultText = $"备份「{row.Name}」失败：{ex.Message}";
        }
        finally
        {
            NotifyState();
        }
    }

    /// <summary>
    /// 展开某个存档的备份清单。还原是危险动作，必须先看到「要还原到哪一份」，
    /// 所以这里只展开候选，真正的写入在 <see cref="ConfirmRestoreAsync"/>。
    /// </summary>
    [RelayCommand]
    private void RequestRestore(WorldRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        // 同一时刻只允许一行处于待还原：两份存档同时写 saves 会互相覆盖。
        foreach (var other in Worlds.Where(w => !ReferenceEquals(w, row)))
        {
            other.ResetRestore();
        }

        row.ToggleRestorePicker();
    }

    /// <summary>
    /// 确认还原。调用方必须先由用户在行内点一次「还原这份」——<see cref="WorldRowItem.PendingRestore"/>
    /// 不为空就是这个二次确认的结果，文案里带着备份时间，用户知道自己会覆盖掉什么。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRestoreAsync(WorldRowItem? row)
    {
        if (row?.PendingRestore is not { } entry || _backupService is null || IsLoading)
        {
            return;
        }

        var backupTime = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

        row.BeginBusy("正在还原…");

        try
        {
            var progress = new Progress<double>(value => row.Progress = value);
            await _backupService
                .RestoreAsync(entry, row.SourceWorldDirectory ?? _targetDirectory ?? string.Empty, progress)
                .ConfigureAwait(false);

            row.CompleteBusy("已还原");
            row.ResetRestore();
            ResultText = $"已把「{row.Name}」还原到 {backupTime} 的备份";
        }
        catch (Exception ex)
        {
            row.FailBusy($"还原失败：{ex.Message}");
            row.ResetRestore();
            ResultText = $"还原「{row.Name}」失败：{ex.Message}";
        }
        finally
        {
            NotifyState();
        }
    }

    /// <summary>取消行内的还原选择，不执行任何写入。</summary>
    [RelayCommand]
    private void CancelRestore(WorldRowItem? row) => row?.ResetRestore();

    /// <summary>在文件管理器里打开存档目录。</summary>
    [RelayCommand]
    private async Task OpenFolderAsync(WorldRowItem? row)
    {
        var directory = row?.Directory ?? _targetDirectory;

        if (string.IsNullOrWhiteSpace(directory) || _platform is null)
        {
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(directory).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ResultText = $"打开目录失败：{ex.Message}";
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>给外部（View 层文件对话框失败时）报告一句可见的错误。</summary>
    public void ReportError(string message) => ResultText = message;

    /// <summary>关掉结果条（用户点它的关闭按钮）。</summary>
    public void ClearResult() => ResultText = null;

    /// <summary>
    /// 缩略图异步补：列表先落位、图标随后逐张填。
    /// 并发压到 3——一次刷新几十个存档时放开并发会把磁盘 IO 排队成毛刺。
    /// </summary>
    private void StartIconLoads()
    {
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = new CancellationTokenSource();

        var token = _iconCts.Token;

        _ = LoadIconsAsync(token);
    }

    private async Task LoadIconsAsync(CancellationToken cancellationToken)
    {
        if (_worldIcons is null)
        {
            return;
        }

        var snapshot = Worlds.ToList();

        using var gate = new SemaphoreSlim(3, 3);

        foreach (var row in snapshot)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        var path = await _worldIcons
                            .GetIconPathAsync(row.Directory, cancellationToken)
                            .ConfigureAwait(false);

                        if (!cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(path))
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => row.IconPath = path);
                        }
                    }
                    catch (Exception)
                    {
                        // 缩略图只是装饰：坏图标、超大图标只该让那一行显示占位块。
                    }
                    finally
                    {
                        gate.Release();
                    }
                },
                cancellationToken);
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasService));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TargetText));
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>
/// 世界里的一行（票 #89）。行高 48、圆角 12 由 XAML 给，这里只管状态。
///
/// 与 <see cref="WorldCardViewModel"/> 的区别：那个是 300 宽的卡片（独立页面的网格布局），
/// 这个是 340 宽详情列里的紧凑行，所以动作收在行内、备份清单就地展开而不是另开一栏。
/// </summary>
public sealed partial class WorldRowItem : ObservableObject
{
    public WorldRowItem(WorldInfo source) => Source = source;

    public WorldInfo Source { get; }

    public string Name => Source.Name;

    public string Directory => Source.Directory;

    /// <summary>备份里的 <c>SourceWorldDirectory</c>：还原时优先写回它，避免把存档解到别的目录。</summary>
    public string? SourceWorldDirectory { get; private set; }

    public string SizeText => Formatting.FormatSize(Source.SizeBytes);

    public string LastWriteTimeText => Source.LastWriteTime.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string VersionText => string.IsNullOrWhiteSpace(Source.VersionName) ? "版本未知" : Source.VersionName!;

    [ObservableProperty]
    private string? _iconPath;

    public bool HasIconImage => !string.IsNullOrWhiteSpace(IconPath);

    public ObservableCollection<BackupRowItem> Backups { get; } = new();

    public string BackupCountText => Backups.Count == 0 ? "无备份" : $"{Backups.Count} 份备份";

    public bool HasBackups => Backups.Count > 0;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _statusText;

    /// <summary>行内还原候选是否展开。</summary>
    [ObservableProperty]
    private bool _isRestorePickerOpen;

    /// <summary>用户点了「还原这份」但还没确认的那一份。</summary>
    [ObservableProperty]
    private BackupEntry? _pendingRestore;

    public bool ShowProgress => IsBusy;

    public bool ShowActions => !IsBusy && !IsRestorePickerOpen;

    public bool ShowRestorePicker => IsRestorePickerOpen && !IsBusy;

    public bool ShowRestoreConfirm => PendingRestore is not null && !IsBusy;

    public bool RestorePickerEmpty => IsRestorePickerOpen && Backups.Count == 0;

    public void SetBackups(IReadOnlyList<BackupEntry> entries)
    {
        Backups.Clear();

        foreach (var entry in entries)
        {
            Backups.Add(new BackupRowItem(entry));
        }

        SourceWorldDirectory = entries.FirstOrDefault()?.SourceWorldDirectory;

        OnPropertyChanged(nameof(BackupCountText));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(RestorePickerEmpty));
    }

    /// <summary>刚备份出来的一份插到最前（清单是时间倒序）。</summary>
    public void AddBackup(BackupEntry entry) => SetBackups(Backups.Select(b => b.Source).Prepend(entry).ToList());

    public void BeginBusy(string status)
    {
        IsBusy = true;
        Progress = 0;
        StatusText = status;
        NotifyLayout();
    }

    public void CompleteBusy(string status)
    {
        IsBusy = false;
        Progress = 1;
        StatusText = status;
        NotifyLayout();
    }

    public void FailBusy(string status)
    {
        IsBusy = false;
        Progress = 0;
        StatusText = status;
        NotifyLayout();
    }

    public void ToggleRestorePicker() => IsRestorePickerOpen = !IsRestorePickerOpen;

    public void ResetRestore()
    {
        IsRestorePickerOpen = false;
        PendingRestore = null;
        NotifyLayout();
    }

    /// <summary>用户在候选清单里点了某一份：进入「确认还原这一份」状态。</summary>
    [RelayCommand]
    private void PickRestore(BackupEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        PendingRestore = entry;
        NotifyLayout();
    }

    partial void OnIsBusyChanged(bool value) => NotifyLayout();

    partial void OnIsRestorePickerOpenChanged(bool value) => NotifyLayout();

    partial void OnPendingRestoreChanged(BackupEntry? value) => NotifyLayout();

    partial void OnIconPathChanged(string? value) => OnPropertyChanged(nameof(HasIconImage));

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ShowRestorePicker));
        OnPropertyChanged(nameof(ShowRestoreConfirm));
        OnPropertyChanged(nameof(RestorePickerEmpty));
    }
}

/// <summary>备份清单里的一项（行内展开用）。</summary>
public sealed class BackupRowItem
{
    public BackupRowItem(BackupEntry source) => Source = source;

    public BackupEntry Source { get; }

    public string CreatedText => Source.CreatedAt.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string SizeText => Formatting.FormatSize(Source.SizeBytes);

    public string NoteText => string.IsNullOrWhiteSpace(Source.Note) ? string.Empty : Source.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Source.Note);

    public bool IsAutomatic => Source.IsAutomatic;
}

/// <summary>
/// 实例详情面板的「整合包」分区（票 #89）。
///
/// 两条动作的方向相反，语义要分清：
/// 导出 = 把**这个实例**打成 <c>.mrpack</c>（和详情面板的目标一致）；
/// 导入 = 把一个 <c>.mrpack</c> 装成**新实例**（不写进当前实例，所以确认文案必须说明这一点）。
/// </summary>
public sealed partial class InstanceModpacksSectionViewModel : ObservableObject
{
    private const string MissingServiceMessage = "整合包服务未接入，导入与导出暂不可用。";

    private readonly IModpackService? _modpack;
    private readonly IPlatformService? _platform;

    private string? _targetDirectory;
    private string? _targetName;

    public InstanceModpacksSectionViewModel(
        IModpackService? modpackService = null,
        IPlatformService? platform = null)
    {
        _modpack = modpackService;
        _platform = platform;
    }

    public string TargetText => string.IsNullOrWhiteSpace(_targetName) ? "这个实例" : _targetName;

    public bool HasService => _modpack is not null;

    /// <summary>导出预览：打包文件名与条数，确认前先让用户看到范围。</summary>
    [ObservableProperty]
    private ModpackExportPreview? _exportPreview;

    public bool HasExportPreview => ExportPreview is not null;

    /// <summary>导入预览：选中的 <c>.mrpack</c> 解析结果。</summary>
    [ObservableProperty]
    private ModpackPreview? _importPreview;

    [ObservableProperty]
    private string? _importArchivePath;

    public bool HasImportPreview => ImportPreview is not null;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _resultText;

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    public bool ShowProgress => IsBusy;

    public bool CanExport => !IsBusy && _modpack is not null && !string.IsNullOrWhiteSpace(_targetDirectory);

    public bool CanImport => !IsBusy && _modpack is not null && ImportPreview is not null && !string.IsNullOrEmpty(ImportArchivePath);

    /// <summary>导入需要用户给一个实例名：默认用整合包自己的名字。</summary>
    [ObservableProperty]
    private string _importInstanceName = "整合包实例";

    /// <summary>导出选项：默认不带存档（存档通常几十 GB 且含个人信息）。</summary>
    [ObservableProperty]
    private bool _exportIncludeSaves;

    [ObservableProperty]
    private bool _exportIncludeConfig = true;

    partial void OnExportPreviewChanged(ModpackExportPreview? value)
    {
        OnPropertyChanged(nameof(HasExportPreview));
    }

    partial void OnImportPreviewChanged(ModpackPreview? value)
    {
        OnPropertyChanged(nameof(HasImportPreview));
        OnPropertyChanged(nameof(CanImport));
    }

    partial void OnImportArchivePathChanged(string? value) => OnPropertyChanged(nameof(CanImport));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImport));
    }

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    /// <summary>切到另一个实例：导出目标跟着换，导入预览清空（那是上一个实例的上下文）。</summary>
    public void SetTarget(string? gameDirectory, string? instanceName)
    {
        _targetDirectory = gameDirectory;
        _targetName = instanceName;

        ImportPreview = null;
        ImportArchivePath = null;

        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(TargetText));
    }

    /// <summary>解析选中的整合包，不落盘。失败给出可读原因。</summary>
    public async Task InspectAsync(string archivePath)
    {
        if (_modpack is null)
        {
            ResultText = MissingServiceMessage;
            return;
        }

        try
        {
            var preview = await _modpack.InspectAsync(archivePath).ConfigureAwait(false);

            ImportArchivePath = archivePath;
            ImportPreview = preview;
            ImportInstanceName = string.IsNullOrWhiteSpace(preview.Name) ? "整合包实例" : preview.Name;
            ResultText = null;
        }
        catch (Exception ex)
        {
            ImportPreview = null;
            ImportArchivePath = null;
            ResultText = $"读取整合包失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把预览过的整合包装成一个新实例。
    /// 失败不留半成品目录是服务层的约定（见 <see cref="IModpackService.ImportAsync"/> 的注释）。
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_modpack is null || ImportPreview is null || string.IsNullOrEmpty(ImportArchivePath) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        ResultText = null;
        StatusText = "正在导入…";

        try
        {
            var progress = new Progress<DownloadProgress>(value => Progress = value.Ratio);

            var directory = await _modpack
                .ImportAsync(ImportArchivePath, ImportInstanceName, progress)
                .ConfigureAwait(false);

            ResultText = $"已导入为实例：{directory}";
            StatusText = "导入完成";
            ImportPreview = null;
            ImportArchivePath = null;
        }
        catch (Exception ex)
        {
            ResultText = $"导入失败：{ex.Message}";
            StatusText = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 导出预览：先让用户确认「要打包什么」，再让他在文件对话框里选落点。
    /// 预览不落盘，所以可以直接在 VM 里算。
    /// </summary>
    [RelayCommand]
    private void PreviewExport()
    {
        if (string.IsNullOrWhiteSpace(_targetDirectory))
        {
            ResultText = "实例还没有可导出的目录。";
            return;
        }

        ExportPreview = new ModpackExportPreview(
            string.IsNullOrWhiteSpace(_targetName) ? "整合包" : _targetName,
            ExportIncludeSaves,
            ExportIncludeConfig);
    }

    /// <summary>用户取消导出预览。</summary>
    [RelayCommand]
    private void CancelExport() => ExportPreview = null;

    /// <summary>打包到用户选好的路径。</summary>
    public async Task ExportAsync(string targetArchivePath)
    {
        if (_modpack is null || ExportPreview is null || string.IsNullOrWhiteSpace(_targetDirectory) || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        ResultText = null;
        StatusText = "正在打包…";

        try
        {
            var options = new ModpackExportOptions
            {
                Name = ExportPreview.Name,
                IncludeSaves = ExportIncludeSaves,
                IncludeConfig = ExportIncludeConfig,
            };

            var progress = new Progress<DownloadProgress>(value => Progress = value.Ratio);

            var result = await _modpack
                .ExportAsync(_targetDirectory, targetArchivePath, options, progress)
                .ConfigureAwait(false);

            if (result.Success)
            {
                var unresolved = result.UnresolvedMods > 0
                    ? $"；其中 {result.UnresolvedMods} 个 jar 无法联网识别，已原样打包"
                    : string.Empty;

                ResultText = $"已导出 {result.Files} 个文件（{Formatting.FormatSize(result.Bytes)}）{unresolved}";
                StatusText = "导出完成";
                ExportPreview = null;
            }
            else
            {
                ResultText = $"导出失败：{result.Error}";
                StatusText = null;
            }
        }
        catch (Exception ex)
        {
            ResultText = $"导出失败：{ex.Message}";
            StatusText = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>给外部（View 层文件对话框失败时）报告一句可见的错误。</summary>
    public void ReportError(string message) => ResultText = message;

    /// <summary>关掉结果条（用户点它的关闭按钮）。</summary>
    public void ClearResult() => ResultText = null;
}

/// <summary>导出确认卡的数据：用户在看到实际打包前先知道会带上什么。</summary>
public sealed class ModpackExportPreview
{
    public ModpackExportPreview(string name, bool includeSaves, bool includeConfig)
    {
        Name = name;
        IncludeSaves = includeSaves;
        IncludeConfig = includeConfig;
    }

    public string Name { get; }

    public bool IncludeSaves { get; }

    public bool IncludeConfig { get; }

    public string ScopeText => IncludeSaves ? "含存档（saves）" : "不含存档";
}