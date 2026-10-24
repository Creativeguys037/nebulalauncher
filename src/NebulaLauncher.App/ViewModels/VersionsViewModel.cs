using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;
using NebulaLauncher.Services;
using Conv = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 版本管理页：已安装版本列表、搜索与筛选、启动 / 修复 / 打开目录 / 删除（二次确认）。
///
/// 构造参数全部可空且带默认值，理由同 <see cref="HomeViewModel"/>：组合根与 Core 并行开发，
/// 服务缺失时页面降级为「服务未就绪」而不是在构造期抛出。
/// </summary>
public partial class VersionsViewModel : PageViewModelBase
{
    private readonly IVersionService? _versions;
    private readonly IInstallService? _install;
    private readonly IGameLauncher? _launcher;
    private readonly IAccountService? _accounts;
    private readonly ISettingsService? _settings;
    private readonly IPlatformService? _platform;
    private readonly IInstanceService? _instances;
    private readonly IInstanceContext? _instanceContext;
    private readonly ILauncherPaths? _paths;
    private readonly INavigationService? _navigation;
    private readonly IVersionFavorites? _favorites;
    private readonly IComponentInspector? _componentInspector;

    /// <summary>
    /// 实例运行锁。页面只读锁状态，不自己判进程存活：
    /// 判活规则（PID + 启动时间、僵死接管）只能有一份，写在 Core 的 <c>InstanceLockService</c> 里。
    /// </summary>
    private readonly IInstanceLockService? _instanceLocks;

    /// <summary>
    /// 实例目录的快捷入口清单（存档 / 模组 / 资源包 / 截图 / 日志）。
    /// 只读磁盘、不写盘：目录由 <see cref="OpenInstanceSubfolderAsync"/> 在打开前创建。
    /// </summary>
    private readonly IInstanceFolderService? _instanceFolders;

    /// <summary>
    /// Java 运行时服务。版本页的启动失败只写一行 <see cref="StatusText"/>，
    /// 而「版本页点启动」是最常见路径：缺 Java 时必须和首页一样能一键装，否则用户被丢一句
    /// 「请在设置里下载 Java 17」自己去找。判定规则在 Core 的 <c>JavaProvisionPlanner</c>，这里只渲染结论。
    /// </summary>
    private readonly IJavaService? _java;

    private bool _loadGate;

    public VersionsViewModel(
        IVersionService? versionService = null,
        IInstallService? installService = null,
        IGameLauncher? gameLauncher = null,
        IAccountService? accountService = null,
        ISettingsService? settingsService = null,
        IPlatformService? platformService = null,
        IInstanceService? instanceService = null,
        ILauncherPaths? launcherPaths = null,
        INavigationService? navigationService = null,
        IInstanceContext? instanceContext = null,
        IVersionFavorites? favoriteService = null,
        IComponentInspector? componentInspector = null,
        IInstanceLockService? instanceLockService = null,
        IInstanceFolderService? instanceFolderService = null,
        IJavaService? javaService = null)
    {
        _versions = versionService;
        _install = installService;
        _launcher = gameLauncher;
        _accounts = accountService;
        _settings = settingsService;
        _platform = platformService;
        _instances = instanceService;
        _paths = launcherPaths;
        _navigation = navigationService;
        _instanceContext = instanceContext;
        _favorites = favoriteService;
        _componentInspector = componentInspector;
        _instanceLocks = instanceLockService;
        _instanceFolders = instanceFolderService;
        _java = javaService;

        // 与首页共用同一套状态机实现，但各持一个实例：两页的「缺 Java」是两次独立的
        // 启动失败上下文，共享一份状态会让一边的失败清掉另一边正在下载的进度。
        JavaInstall = new JavaInstallState(javaService, JavaInstallWording.Versions);
        JavaInstall.PropertyChanged += OnJavaInstallChanged;

        // 收藏 / 隐藏的变动必须立刻反映到列表（星标、置顶、隐藏过滤），不能等页面重进。
        if (_favorites is not null)
        {
            _favorites.Changed += OnFavoritesChanged;
        }

        // 组件面板里两段列表的可见性与「只修复缺失文件」的可用性都依赖集合内容。
        ComponentsMissingFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMissingFiles));
            OnPropertyChanged(nameof(CanRepairMissingFiles));
        };
        ComponentsModifiedFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasModifiedFiles));

        // 清理范围默认「全部文件」：面板一打开就该有合法值，而不是空的下拉框。
        _cleanupRange = CleanupRanges[0];

        ServiceWarning = DescribeMissingServices();
    }

    public override string Title => "版本";

    public override string? Subtitle => _subtitle;

    public override string Key => NavigationKeys.Versions;

    /// <summary>全量版本（未过滤）。</summary>
    public ObservableCollection<VersionRow> Versions { get; } = new();

    /// <summary>过滤后绑定到列表的版本。</summary>
    public ObservableCollection<VersionRow> VisibleVersions { get; } = new();

    /// <summary>已创建的实例（独立游戏目录）。与版本列表来自同一个 <see cref="IInstanceService"/>。</summary>
    public ObservableCollection<InstanceRow> Instances { get; } = new();

    private string _subtitle = "正在读取已安装版本…";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _serviceWarning;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _filterKind = FilterAll;

    [ObservableProperty]
    private bool _isFilterAll = true;

    [ObservableProperty]
    private bool _isFilterVanilla;

    [ObservableProperty]
    private bool _isFilterLoader;

    [ObservableProperty]
    private bool _isFilterFavorite;

    /// <summary>「显示已隐藏」。默认关：被隐藏的版本不出现在列表里。</summary>
    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isFilteredEmpty;

    /// <summary>版本全都处于隐藏状态时的空态，与「筛选无结果」分开给文案与出口。</summary>
    [ObservableProperty]
    private bool _isAllHidden;

    /// <summary>工具行里的版本计数。抬头区副标题在部分窗口尺寸下不显示，这里自给自足一份。</summary>
    [ObservableProperty]
    private string _versionCountText = string.Empty;

    [ObservableProperty]
    private VersionRow? _selectedRow;

    [ObservableProperty]
    private VersionRow? _pendingDelete;

    [ObservableProperty]
    private bool _isConfirmOpen;

    // 多开之后「启动中」不再是一个页面级布尔：它只是一个按实例的集合，
// 见 _launchingInstances。留一个谁都不读的属性只会让人以为准入还看它。

    /// <summary>
    /// 正在启动中的实例目录（多开：按实例记，不再是一个全局布尔）。
    ///
    /// 它的作用只有两个：挡住「同一个实例」的重复启动，以及让「停止游戏」知道
    /// 该等哪一次启动收尾。不同实例各占各的键，所以两行可以同时启动。
    /// </summary>
    private readonly HashSet<string> _launchingInstances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这个目录现在能不能发起一次启动。
    ///
    /// 多开的准入就是这一条，两个条件都必须按实例：本页没有它的启动在进行、
    /// 启动器里也没有它的会话。页面级布尔（旧的 IsLaunching）在运行期恒为 true，
    /// 拿它当闸门等于「同时只准开一个」。
    ///
    /// internal 是为了让单测直接验证这条判据：跑通 <c>LaunchVersionAsync</c> 需要
    /// 账户服务、路径服务与一个真能返回的启动器，而这里要验证的只是准入。
    /// </summary>
    internal bool CanStartInstance(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || _launcher is null)
        {
            return false;
        }

        return !_launchingInstances.Contains(RunningGameInfo.KeyFor(gameDirectory))
               && !_launcher.IsInstanceRunning(gameDirectory);
    }

    // 多开之后不存在「页面上有没有游戏在跑」这种单一状态了：每个实例卡片各问各的，
    // 记在 InstanceRow.IsSessionRunning 上（见 RefreshInstanceSessionStates）。
    // 页面级布尔在这里只会变成第二个真相来源，索性不留。

    [ObservableProperty]
    private bool _hasInstances;

    /// <summary>实例区标题右侧的说明：当前有几个实例、启动会落到哪个目录。</summary>
    [ObservableProperty]
    private string _instanceSummary = string.Empty;

    [ObservableProperty]
    private InstanceRow? _selectedInstance;

    [ObservableProperty]
    private InstanceRow? _pendingDeleteInstance;

    [ObservableProperty]
    private bool _isInstanceConfirmOpen;

    /// <summary>创建 / 删除实例进行中，用于禁用实例区按钮。</summary>
    [ObservableProperty]
    private bool _isInstanceBusy;

    /// <summary>筛选键：全部。</summary>
    public const string FilterAll = "all";

    /// <summary>筛选键：原版。</summary>
    public const string FilterVanilla = "vanilla";

    /// <summary>筛选键：加载器。</summary>
    public const string FilterLoader = "loader";

    /// <summary>筛选键：只看收藏。</summary>
    public const string FilterFavorite = "favorite";

    public override async Task OnNavigatedToAsync()
    {
        await LoadAsync();
    }

    /// <summary>
    /// 离开页面时停掉在途的 Java 下载。下载动辄几十 MB，用户切走页面之后没有理由继续占用带宽；
    /// 页面对象与它的 CTS 随后都会被丢弃。
    /// </summary>
    public override Task OnNavigatedFromAsync()
    {
        CancelJavaInstall();
        return Task.CompletedTask;
    }

    // ==================== 加载 ====================

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loadGate)
        {
            return;
        }

        _loadGate = true;
        IsLoading = true;
        ErrorText = null;
        try
        {
            if (_versions is null)
            {
                Versions.Clear();
                ApplyFilter();
                UpdateEmptyState();
                UpdateSubtitle();
                await LoadInstancesAsync();
                return;
            }

            // 收藏 / 隐藏是独立文件，先读它再建行：否则首帧会先显示「无收藏」再跳变。
            if (_favorites is not null)
            {
                await _favorites.LoadAsync();
            }

            var installed = await _versions.GetInstalledAsync();

            // 首次进入页面时优先恢复上次启动的版本；没有记录才退回列表第一条。
            // 不这样做的话，「启动使用哪个实例」会取决于版本名的排序，用户看不出规律。
            var previous = SelectedRow?.Name ?? _settings?.Current.LastSelectedVersion;
            Versions.Clear();
            foreach (var info in installed)
            {
                Versions.Add(new VersionRow(info));
            }

            ApplyFavoriteState();

            // 隐藏的版本默认不可见，不能让它成为「选中但看不见」的启动目标。
            SelectedRow = Versions.FirstOrDefault(r => !r.IsHidden && string.Equals(r.Name, previous, StringComparison.Ordinal))
                          ?? Versions.FirstOrDefault(r => !r.IsHidden)
                          ?? Versions.FirstOrDefault();

            ApplyFilter();
            UpdateEmptyState();
            UpdateSubtitle();

            // 实例要等版本列表就位后再加载：选中哪个实例取决于当前选中的版本。
            await LoadInstancesAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"读取已安装版本失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _loadGate = false;
        }
    }

    // ==================== 实例 ====================

    /// <summary>
    /// 重新读实例列表。实例服务不会在导航时自动加载，必须显式调用 <c>LoadAsync</c>，
    /// 否则 <c>Instances</c> 永远是空的（实例数据只在磁盘上）。
    /// </summary>
    private async Task LoadInstancesAsync()
    {
        if (_instances is null)
        {
            Instances.Clear();
            SelectedInstance = null;
            UpdateInstanceState();
            await RefreshTrashAsync();
            return;
        }

        var previousId = SelectedInstance?.Info.Id;

        try
        {
            await _instances.LoadAsync();

            Instances.Clear();
            foreach (var info in _instances.Instances)
            {
                var row = new InstanceRow(info);

                // 芯片的「存在 / 不存在」是磁盘快照，必须跟实例列表一起重建：
                // 单建一次会让「刚点开截图目录」的实例在下次刷新前一直显示弱化态。
                FillInstanceFolders(row);
                Instances.Add(row);
            }

            SelectedInstance =
                Instances.FirstOrDefault(i => string.Equals(i.Info.Id, previousId, StringComparison.Ordinal))
                ?? Instances.FirstOrDefault(i => SelectedRow is not null &&
                                                  string.Equals(i.Info.VersionName, SelectedRow.Name, StringComparison.Ordinal))
                ?? Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorText = $"读取实例失败：{ex.Message}";
        }

        UpdateInstanceState();

        // 回收站与实例列表同源：删完实例要立刻能在下面看到「可恢复」。
        await RefreshTrashAsync();

        // 运行状态必须按磁盘事实重查：用户可能在另一个启动器窗口里启动过，
        // 也可能刚在任务管理器里结束掉游戏；只信内存状态会让卡片上的「运行中」一直错着。
        await RefreshRunningStateAsync();

        await ApplyScreenshotStateAsync();
    }

    // ==================== 运行状态（实例运行锁） ====================

    /// <summary>
    /// 重新查询每个实例目录与每个版本对应目录的运行锁，把结果写到行上的 <c>IsRunning</c>。
    ///
    /// 版本行也要查：删除 / 修复 / 组件检查动的是 <c>versions/&lt;名&gt;/</c> 下的文件，
    /// 游戏正跑着的时候替换这些文件会得到一次「起不来的崩溃」，而且现场很难解释。
    /// </summary>
    private async Task RefreshRunningStateAsync()
    {
        var lockService = _instanceLocks;

        if (lockService is null)
        {
            foreach (var row in Instances)
            {
                row.IsRunning = false;
            }

            foreach (var row in Versions)
            {
                row.IsRunning = false;
            }

            RefreshInstanceActionStates();

            // 没有锁服务不代表没有进程：会话状态来自启动器的内存表，与锁文件是两条独立的判定。
            RefreshInstanceSessionStates();
            return;
        }

        foreach (var row in Instances)
        {
            var directory = GetInstanceGameDirectory(row);
            row.IsRunning = directory is not null && await QueryRunningAsync(lockService, directory);
        }

        var globalDirectory = _paths?.MinecraftDirectory;

        foreach (var row in Versions)
        {
            // 版本行对应「启动这个版本时会用到的目录」：优先绑定它的实例，没有实例就是全局目录。
            var instance = Instances.FirstOrDefault(i =>
                string.Equals(i.Info.VersionName, row.Name, StringComparison.Ordinal));

            var directory = instance is null ? globalDirectory : GetInstanceGameDirectory(instance);
            row.IsRunning = directory is not null && await QueryRunningAsync(lockService, directory);
        }

        RefreshInstanceActionStates();

        // 每个实例卡片各问各的：多开时列表里可以同时出现两行「运行中」，
        // 停止按钮也跟着各自出现，不再由页面级单布尔决定。
        RefreshInstanceSessionStates();

        // 组件面板可能正开着同一个版本：运行状态一变，面板里的「只修复缺失文件」也要跟着变灰。
        if (ComponentsRow is not null)
        {
            OnPropertyChanged(nameof(CanRepairMissingFiles));
        }
    }

    /// <summary>
    /// 把「本会话有没有在这个实例目录里跑」写到每一行上。
    ///
    /// 唯一的来源是 <see cref="IGameLauncher.RunningGames"/>（进程真实状态）。它与行上的
    /// <c>IsRunning</c>（磁盘运行锁）是两条独立判定：锁可能是另一个启动器实例持的，
    /// 那种进程本会话停不掉，按钮就不该出现；反过来锁文件还没来得及写的瞬间，
    /// 会话已经在了，也不能让按钮消失。
    /// </summary>
    private void RefreshInstanceSessionStates()
    {
        var running = _launcher?.RunningGames;

        foreach (var row in Instances)
        {
            var directory = GetInstanceGameDirectory(row);
            var key = directory is null ? null : RunningGameInfo.KeyFor(directory);
            RunningGameInfo? match = null;

            if (running is not null && key is not null)
            {
                foreach (var game in running)
                {
                    if (string.Equals(game.InstanceKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        match = game;
                        break;
                    }
                }
            }

            row.RunningSessionId = match?.SessionId;
            row.IsSessionRunning = match is not null;
        }

        // 截图模式：进程状态没法真的造出来，只能补。补在这里而不是 ApplyScreenshotStateAsync，
        // 是因为后者之后还会被 LoadAsync 触发的刷新覆盖掉，按钮会变灰。
        if (_screenshotRunningSessions)
        {
            for (var i = 0; i < Math.Min(2, Instances.Count); i++)
            {
                Instances[i].RunningSessionId = $"screenshot-{i}";
                Instances[i].IsSessionRunning = true;
            }
        }

        // 行的 IsSessionRunning 变了不会自动通知命令：按钮的 CanExecute 只在
        // CanExecuteChanged 时重算，否则会出现「可见但永远灰」的按钮。
        StopGameCommand.NotifyCanExecuteChanged();
    }

    private static async Task<bool> QueryRunningAsync(IInstanceLockService lockService, string directory)
    {
        try
        {
            return await lockService.QueryAsync(directory) is not null;
        }
        catch (Exception)
        {
            // 查询失败只是「状态未知」：不能因此把整页变成错误页，也不能当成运行中把按钮全锁死。
            return false;
        }
    }

    /// <summary>实例目录可能因为 Id 非法而抛（<see cref="IInstanceService.GetGameDirectory"/> 会校验），不能让它带崩页面。</summary>
    private string? GetInstanceGameDirectory(InstanceRow row)
    {
        try
        {
            return _instances?.GetGameDirectory(row.Info.Id);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 给实例卡填目录芯片（存档 / 模组 / 资源包 / 截图 / 日志）。
    ///
    /// 目录来自 <see cref="IInstanceService.GetGameDirectory"/>——即该实例真实的游戏目录；
    /// **不能用实例名拼**：实例名可改，目录名永远是 Id。服务未注册时芯片留空，
    /// 卡片其余入口（打开目录 / 启动 / 删除）照常可用。
    /// </summary>
    private void FillInstanceFolders(InstanceRow row)
    {
        row.Folders.Clear();

        if (_instanceFolders is null)
        {
            return;
        }

        var directory = GetInstanceGameDirectory(row);

        if (directory is null)
        {
            return;
        }

        foreach (var folder in _instanceFolders.List(directory))
        {
            row.Folders.Add(new InstanceFolderChip(row.Info.Id, folder));
        }
    }

    /// <summary>
    /// 实例卡片上破坏性按钮的可用性：运行中与实例区忙碌都会禁用。
    /// 克隆不在其中——它只读源目录，游戏开着也能安全地复制一份。
    /// </summary>
    private void RefreshInstanceActionStates()
    {
        var busy = IsInstanceBusy;

        foreach (var row in Instances)
        {
            row.ActionsEnabled = !busy && !row.IsRunning;
        }
    }

    partial void OnIsInstanceBusyChanged(bool value) => RefreshInstanceActionStates();

    /// <summary>
    /// 截图模式专用：「换加载器」面板只能从实例卡片的溢出菜单点开，而截图模式没有点击能力。
    ///
    /// 用现成的 <c>--section</c> 驱动，不新增命令行开关：<c>tools/shot.ps1</c> 已经把
    /// <c>-Section</c> 透传成 <c>--section</c>，复用它可以不碰宿主截图入口
    /// （<c>App.axaml.cs</c> 里那段按页面类型逐条判断的代码）。
    /// 只在 <see cref="ScreenshotMode.IsActive"/> 时生效，正常运行路径不受影响。
    ///
    /// 键值：<c>loader</c> 面板常态、<c>loader-confirm</c> 二次确认、<c>running</c> 实例运行中。
    /// </summary>
    private async Task ApplyScreenshotStateAsync()
    {
        // instance-editor / instance-notes：这两个面板只能从实例卡片上点开，截图模式没有点击能力。
        // 开关由 --instance-editor / --instance-notes 解析（tools/shot.ps1 已有对应参数），
        // 走 App.axaml.cs 的既有宿主入口，所以在这里消费而不是新加一条截图专用代码路径。
        if (ScreenshotMode.IsActive
            && !_screenshotStateApplied
            && (ScreenshotMode.OpenInstanceEditor || ScreenshotMode.OpenInstanceNotes)
            && Instances.Count > 0)
        {
            _screenshotStateApplied = true;

            // 取第一个实例：它是「为选中版本新建实例」的产物，设置项最全，面板不会只剩空框。
            var editorRow = Instances[0];

            if (ScreenshotMode.OpenInstanceNotes)
            {
                OpenNotes(editorRow);
            }
            else
            {
                OpenInstanceEditor(editorRow);
            }

            return;
        }
        // running：让第一个实例目录真的被当前进程持锁，再重查一次状态。
        // 「运行中」徽标必须来自真实的锁判定（截图进程自己就是活着的 PID，启动时间也对得上），
        // 在 ViewModel 里造一个假布尔会让这张图无法证明判定链是通的。
        // 锁不在这里释放：截图进程退出后它自然变成僵死锁，只存在于截图专用数据目录里。
        if (ScreenshotMode.IsActive
            && string.Equals(ScreenshotMode.SectionKey, "running", StringComparison.OrdinalIgnoreCase)
            && !_screenshotStateApplied)
        {
            _screenshotStateApplied = true;

            // 必须在下面的刷新之前置位：刷新结束时会给前两行补上会话状态，
            // 之后 LoadAsync 再触发刷新也会继续补，按钮不会变灰。
            _screenshotRunningSessions = true;

            if (_instanceLocks is not null && Instances.Count > 0)
            {
                // 多开验证：给前两个实例各抢一把锁，让列表里同时出现两行「运行中」。
                // 抢不到锁说明目录被真进程占着，那就只补会话状态，不影响截图。
                var demoCount = Math.Min(2, Instances.Count);

                for (var i = 0; i < demoCount; i++)
                {
                    var directory = GetInstanceGameDirectory(Instances[i]);

                    if (directory is not null)
                    {
                        await _instanceLocks.TryAcquireAsync(directory, Instances[i].Info.VersionName);
                    }
                }

                await RefreshRunningStateAsync();
            }

            return;
        }

        // loader = 面板常态（含兼容性提示条）；loader-confirm = 再展开二次确认条。
        // 两态分两张图，因为两者叠在一起会顶出 800px 高的截图窗口。
        var key = ScreenshotMode.SectionKey;
        var wantPanel = string.Equals(key, "loader", StringComparison.OrdinalIgnoreCase);
        var wantConfirm = string.Equals(key, "loader-confirm", StringComparison.OrdinalIgnoreCase);

        if (_screenshotStateApplied
            || !ScreenshotMode.IsActive
            || (!wantPanel && !wantConfirm)
            || Instances.Count == 0
            || IsLoaderSwitchOpen)
        {
            return;
        }

        _screenshotStateApplied = true;

        // 优先挑一个已经装了加载器的实例：换成另一个加载器时会亮出「模组兼容性会变」提示条，
        // 一张图能同时验证提示条、加载器胶囊与版本下拉三处布局。
        var demoRow = Instances.FirstOrDefault(i => i.Info.Loader != LoaderKind.Vanilla) ?? Instances[0];
        _ = OpenLoaderSwitchAsync(demoRow);

        // 面板默认停在当前加载器上（正常使用时多数人只是打开看一眼），
        // 但截图要一次性展示「兼容性提示条」与「加载器版本下拉」，所以再切到一个不同的目标。
        var demoTarget = demoRow.Info.Loader == LoaderKind.Vanilla ? LoaderKind.Forge : LoaderKind.Fabric;
        SetSwitchTargetCommand.Execute(demoTarget);

        // 二次确认条：截图模式没有点击能力，只能靠这里把状态摆出来。
        if (wantConfirm)
        {
            IsLoaderSwitchConfirmOpen = true;
        }
    }

    private bool _screenshotStateApplied;

    /// <summary>
    /// 截图模式下是否要伪造「本会话在前两个实例里跑着」。
    ///
    /// 仅用于 <c>--section running</c> 出图：截图进程拉不起真游戏，而按钮的可见性取决于
    /// 会话状态。置位后每次刷新都会重新补上，避免被后续 LoadAsync 覆盖成「没在跑」。
    /// </summary>
    private bool _screenshotRunningSessions;

    private void UpdateInstanceState()
    {
        HasInstances = Instances.Count > 0;

        // 只有实例绑定的版本与当前选中版本一致时，启动才会用它的目录，
        // 摘要必须如实反映这一点，否则用户会以为选了实例就换了目录。
        var effective = SelectedInstance is not null
                        && SelectedRow is not null
                        && string.Equals(SelectedInstance.Info.VersionName, SelectedRow.Name, StringComparison.Ordinal)
            ? SelectedInstance
            : null;

        InstanceSummary = Instances.Count == 0
            ? "还没有实例，启动直接使用全局 .minecraft 目录"
            : effective is not null
                ? $"共 {Instances.Count} 个 · 启动使用「{effective.Name}」的目录"
                : $"共 {Instances.Count} 个 · 当前版本没有实例，启动使用全局 .minecraft";
    }

    partial void OnSelectedRowChanged(VersionRow? value)
    {
        if (value is not null)
        {
            var match = Instances.FirstOrDefault(i =>
                string.Equals(i.Info.VersionName, value.Name, StringComparison.Ordinal));

            if (match is not null && !ReferenceEquals(match, SelectedInstance))
            {
                SelectedInstance = match;
            }
        }

        UpdateInstanceState();
    }

    partial void OnSelectedInstanceChanged(InstanceRow? value) => UpdateInstanceState();

    private void UpdateSubtitle()
    {
        var text = Versions.Count == 0 ? "尚未安装版本" : $"{Versions.Count} 个已安装版本";
        if (text == _subtitle)
        {
            return;
        }

        _subtitle = text;
        OnPropertyChanged(nameof(Subtitle));
    }

    private void UpdateEmptyState()
    {
        IsEmpty = Versions.Count == 0;

        // 「全部都被隐藏了」和「筛选没结果」的出口不同：前者给「显示已隐藏」按钮，后者让用户改筛选。
        IsAllHidden = Versions.Count > 0
                      && !ShowHidden
                      && VisibleVersions.Count == 0
                      && Versions.All(row => row.IsHidden);

        IsFilteredEmpty = Versions.Count > 0 && VisibleVersions.Count == 0 && !IsAllHidden;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    partial void OnFilterKindChanged(string value)
    {
        IsFilterAll = value == FilterAll;
        IsFilterVanilla = value == FilterVanilla;
        IsFilterLoader = value == FilterLoader;
        IsFilterFavorite = value == FilterFavorite;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        VisibleVersions.Clear();

        foreach (var row in Versions)
        {
            // 隐藏默认不可见；打开「显示已隐藏」后才参与后面的筛选。
            if (!ShowHidden && row.IsHidden)
            {
                continue;
            }

            if (FilterKind == FilterVanilla && row.Info.Loader != LoaderKind.Vanilla)
            {
                continue;
            }

            if (FilterKind == FilterLoader && row.Info.Loader == LoaderKind.Vanilla)
            {
                continue;
            }

            if (FilterKind == FilterFavorite && !row.IsFavorite)
            {
                continue;
            }

            if (query.Length > 0 && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            VisibleVersions.Add(row);
        }

        SortVisibleVersions();

        VersionCountText = BuildCountText();

        UpdateEmptyState();
    }

    /// <summary>
    /// 收藏组置顶：收藏的行排在最前（组内按收藏时间倒序），其余保持 <see cref="Versions"/> 的原有顺序。
    /// 排序规则在 Core 的 <see cref="VersionFavoriteOrdering"/> 里（有单测），这里只做集合替换。
    /// </summary>
    private void SortVisibleVersions()
    {
        if (VisibleVersions.Count < 2)
        {
            return;
        }

        var favorites = _favorites?.Favorites ?? Array.Empty<string>();
        var ordered = VersionFavoriteOrdering.Apply(VisibleVersions.ToList(), row => row.Name, favorites);

        VisibleVersions.Clear();
        foreach (var row in ordered)
        {
            VisibleVersions.Add(row);
        }
    }

    private string BuildCountText()
    {
        if (Versions.Count == 0)
        {
            return string.Empty;
        }

        var hidden = Versions.Count(row => row.IsHidden);
        var baseText = VisibleVersions.Count == Versions.Count
            ? $"共 {Versions.Count} 个"
            : $"显示 {VisibleVersions.Count}/{Versions.Count}";

        // 1024 宽下工具条很挤，隐藏数用短格式；文案太长会把筛选 chip 顶到计数文本上。
        return hidden == 0 ? baseText : $"{baseText} · 隐藏 {hidden}";
    }

    [RelayCommand]
    private void SelectFilter(string? kind)
    {
        FilterKind = kind switch
        {
            FilterVanilla => FilterVanilla,
            FilterLoader => FilterLoader,
            FilterFavorite => FilterFavorite,
            _ => FilterAll,
        };
    }

    // ==================== 收藏 / 隐藏 ====================

    /// <summary>「全部被隐藏」空态与工具条共用：切换「显示已隐藏」。</summary>
    [RelayCommand]
    private void ToggleShowHidden() => ShowHidden = !ShowHidden;

    [RelayCommand]
    private async Task ToggleFavoriteAsync(VersionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_favorites is null)
        {
            StatusText = "收藏服务未就绪，无法修改收藏。";
            return;
        }

        try
        {
            await _favorites.ToggleFavoriteAsync(row.Name);

            // Changed 事件同样会刷新；这里先同步一次，避免事件绕一圈 UI 线程时的星标延迟。
            ApplyFavoriteState();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"修改收藏失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleHiddenAsync(VersionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_favorites is null)
        {
            StatusText = "收藏服务未就绪，无法修改隐藏状态。";
            return;
        }

        try
        {
            await _favorites.SetHiddenAsync(row.Name, !row.IsHidden);

            ApplyFavoriteState();
            ApplyFilter();

            StatusText = row.IsHidden
                ? $"已隐藏「{row.Name}」：文件没有删除，点工具条的「显示已隐藏」可以再看到它。"
                : $"已取消隐藏「{row.Name}」。";
        }
        catch (Exception ex)
        {
            StatusText = $"修改隐藏状态失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把收藏 / 隐藏状态同步到现有行。
    /// 只遍历 <see cref="Versions"/>（已安装的版本）：favorites.json 里已删版本的残留条目不参与 UI。
    /// </summary>
    private void ApplyFavoriteState()
    {
        if (_favorites is null)
        {
            foreach (var row in Versions)
            {
                row.IsFavorite = false;
                row.IsHidden = false;
            }

            return;
        }

        var favoriteSet = new HashSet<string>(_favorites.Favorites, StringComparer.Ordinal);

        foreach (var row in Versions)
        {
            row.IsFavorite = favoriteSet.Contains(row.Name);
            row.IsHidden = _favorites.IsHidden(row.Name);
        }
    }

    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        // 写盘续体在后台线程（Core 里用 ConfigureAwait(false)），改 ObservableCollection 必须回 UI 线程。
        Dispatcher.UIThread.Post(() =>
        {
            ApplyFavoriteState();
            ApplyFilter();
        });
    }

    // ==================== 每行操作 ====================

    [RelayCommand]
    private async Task LaunchVersionAsync(VersionRow? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedRow = row;

        // 上一轮的「缺 Java」结论不能挂在新一轮上：那条提示说的是上一次失败的原因。
        ClearMissingJavaState();

        if (_launcher is null || _accounts is null)
        {
            StatusText = "启动服务未就绪：账户或游戏启动模块尚未注册。";
            return;
        }

        if (_accounts.ActiveAccount is null)
        {
            StatusText = "还没有账户。先到「账户」页添加一个离线账户即可启动。";
            return;
        }

        // 解析「这次用哪个目录启动」：已有实例优先，其次按隔离策略自动建实例，最后退回全局目录。
        // 以前这里只查已有实例，用户没手动建过实例就永远用全局 .minecraft，
        // 于是 Fabric 与 Forge 版本的 mods 挤在同一个 mods/ 里互相打架。
        var resolution = await ResolveTargetAsync(row.Name);
        var gameDirectory = resolution.GameDirectory;
        var instanceId = resolution.Instance?.Id;

        // 多开之后准入必须按「这个实例」判断，不能用页面级的 IsLaunching：
        // 那个布尔覆盖整段游戏运行期（LaunchAsync 一直 await 到退出），
        // 拿它当闸门等于「同时只准开一个」，多开就永远发生不了。
        var instanceKey = RunningGameInfo.KeyFor(gameDirectory);

        if (!CanStartInstance(gameDirectory))
        {
            StatusText = _launcher.IsInstanceRunning(gameDirectory)
                ? $"实例 {row.Name} 已经在运行，请先停止它。"
                : $"实例 {row.Name} 已经在启动中，等它起来或先停止它。";

            return;
        }

        _launchingInstances.Add(instanceKey);

        if (resolution.Created)
        {
            StatusText = $"已按版本隔离为 {row.Name} 建立独立目录：{resolution.Instance?.Name}";
        }

        await RememberSelectedVersionAsync(row.Name);

        StatusText = $"正在启动 {row.Name}…";
        try
        {
            var account = await _accounts.EnsureValidActiveAccountAsync();
            if (account is null)
            {
                StatusText = "账户令牌已失效，请到账户页重新登录。";
                return;
            }

            // 与首页共用同一份合成规则（内存 / Java / 参数 / 版本类型 / 自动内存）。
            // 这里以前手写，只带了少数字段，于是「版本页启动」与「首页启动」对同一个实例
            // 会得到不同的内存与参数——同一份配置在两个入口表现不同，最难向用户解释。
            var options = LaunchOptionsFactory.Build(
                account,
                row.Name,
                resolution,
                _settings?.Current);

            var progress = new Progress<LaunchProgress>(p => StatusText = p.Message);
            var result = await _launcher.LaunchAsync(options, progress);

            if (!result.Started)
            {
                StatusText = $"启动失败：{result.FailureReason ?? "未收到原因"}";

                // 缺 Java 是可以就地解决的：把主版本交给 planner，界面上出现安装按钮。
                if (result.MissingJavaMajor is int missingMajor)
                {
                    await ApplyMissingJavaAsync(row, missingMajor).ConfigureAwait(true);
                }
            }
            else if (result.Detached)
            {
                // 取消等待但游戏仍在跑：不算失败，也不该回写游玩时间（还没退出）。
                StatusText = "已取消等待，游戏仍在运行；可点「停止」结束。";
            }
            else if (result.Verdict == GameExitVerdict.Crashed
                     || (result.Verdict is null && result.ExitCode != 0))
            {
                // verdict 优先：exit 0 也可能是加载未完成即被杀（崩溃）。
                StatusText = $"游戏异常退出（{result.ExitCode}）：{result.FailureReason ?? "详见崩溃日志"}";
            }
            else
            {
                StatusText = $"游戏已退出，本次运行 {Conv.FormatDuration(result.Duration)}。";
            }

            // 只有真正启动过、且已经结束的实例才回写游玩时间；失败、取消与后台移交都不污染统计。
            if (result.Started && !result.Detached && instanceId is not null)
            {
                await RecordInstancePlayAsync(instanceId, result.Duration);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "启动已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败：{ex.Message}";
        }
        finally
        {
            _launchingInstances.Remove(instanceKey);
            RefreshInstanceSessionStates();
            await LoadAsync();
        }
    }

    /// <summary>
    /// 停止某一行的游戏进程。入口在实例卡片「运行中」徽标旁边。
    ///
    /// 按会话停（<see cref="IGameLauncher.KillGame"/>），不按「全局那一个进程」停：
    /// 多开时列表里可能有两行都在跑，停 A 不能顺手把 B 一起停掉。
    /// 卡片上的「运行中」来自磁盘运行锁，可能是另一个启动器实例拉起的，那种进程本会话
    /// 没有任何句柄——此时按钮不出现（<see cref="InstanceRow.IsSessionRunning"/> 为 false），
    /// 绝不退化成按进程名扫 <c>javaw</c> 把用户别的 Java 程序一起干掉。
    ///
    /// 不弹二次确认：卡死时这是救援动作；但文案写「停止游戏」，用户要知道会杀进程。
    /// </summary>
    /// <param name="row">要停止的实例行；XAML 通过 <c>CommandParameter="{Binding}"</c> 传入。</param>
    [RelayCommand(CanExecute = nameof(CanStopGame))]
    private async Task StopGameAsync(InstanceRow? row)
    {
        var launcher = _launcher;

        if (launcher is null || row is null)
        {
            return;
        }

        // 先按行刷新一次：进程可能已经自己退了（或被任务管理器结束）。
        RefreshInstanceSessionStates();

        var sessionId = row.RunningSessionId;

        if (sessionId is null)
        {
            StatusText = "游戏进程已经不在了。";
            await LoadAsync();
            return;
        }

        StatusText = "正在停止游戏进程…";

        bool requested;

        try
        {
            // Kill 是同步阻塞的（TerminateProcess + 收句柄），放线程池避免卡住 UI 线程。
            requested = await Task.Run(() => launcher.KillGame(sessionId)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // KillGame 内部已吞掉杀进程本身的失败，这里兜 Task 调度层。
            StatusText = $"停止游戏失败：{ex.Message}";
            RefreshInstanceSessionStates();
            return;
        }

        // 等「这个会话」从在跑列表里消失，而不是等「全局没有游戏在跑」：多开时后者要等所有实例都退出。
        var exited = !requested
                     || await ProcessWait
                         .UntilAsync(
                             () => !launcher.RunningGames.Any(game =>
                                 string.Equals(game.SessionId, sessionId, StringComparison.Ordinal)),
                             TimeSpan.FromSeconds(8))
                         .ConfigureAwait(true);

        // 进程退出后 LaunchAsync 还要跑崩溃分析、退出后命令，最后才释放实例锁；
        // 不等这一步，下面重查锁会读到仍然活着的锁，「运行中」徽标摘不掉。
        // 等的是「这一行的启动流程」而不是页面级 IsLaunching：多开时后者会被别的实例占着，
        // 白等满 10 秒还会让「运行中」徽标多挂一会儿。
        if (exited)
        {
            var stoppingDirectory = GetInstanceGameDirectory(row);
            var stoppingKey = stoppingDirectory is null ? null : RunningGameInfo.KeyFor(stoppingDirectory);

            await ProcessWait
                .UntilAsync(
                    () => stoppingKey is null || !_launchingInstances.Contains(stoppingKey),
                    TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
        }

        RefreshInstanceSessionStates();

        if (exited)
        {
            // 立刻把这一行恢复成「没在跑」：等 LoadAsync 整页重建会有一帧延迟，
            // 而用户刚点完停止，最想看的就是徽标消失。
            row.RunningSessionId = null;
            row.IsSessionRunning = false;
            StatusText = "已停止游戏进程。";
        }
        else
        {
            StatusText = "已发出停止请求，但进程仍未退出：请到任务管理器结束它。";
        }

        // 徽标与按钮可用性都来自磁盘运行锁，必须按磁盘事实重查一次。
        await LoadAsync();
    }

    private bool CanStopGame(InstanceRow? row) => _launcher is not null && row?.IsSessionRunning == true;

    // ==================== 缺 Java：一键安装 ====================
    //
    // 版本页是「点启动」最常用的入口，但它的失败展示只有一行 StatusText。缺 Java 时用户看到的是
    // 「请在设置里下载 Java 25」，还得自己回设置页找下拉框。这一段把它接成首页同样的闭环：
    // LaunchResult.MissingJavaMajor → Core 的 planner → 按钮 → 下载 → 写回设置 → 强制重扫。
    //
    // 判定一个字都不在这里写：能不能装、装哪个版本由 JavaProvisionPlanner 决定，
    // 上界冲突（1.12.2 Forge 配 Java 21）和没有下载包的主版本都必须退回手动配置。

    /// <summary>本次启动缺的主版本；null 表示没有缺 Java 的问题。</summary>
    public JavaInstallState JavaInstall { get; }

    /// <summary>装完要重试的那个版本。失败时记下来，装完一键再启动，不用用户再找一遍。</summary>
    private VersionRow? _pendingJavaRow;

    /// <summary>
    /// 是否显示「缺 Java」区块。三种情形都要显示：
    /// ① planner 说能装；② 判定失败（没有 plan）但需求已知——仍要给「打开 Java 设置」的出口；
    /// ③ 已经装好且记着要重试的那个版本。
    /// 唯一不显示的是「planner 判出了结论但装不了」（上界冲突 / 无下载包）：这时区块里没有任何可点的东西。
    /// </summary>
    public bool HasMissingJava =>
        (JavaInstall.InstallMajor is > 0 && !JavaInstall.IsInstalled)
        || (JavaInstall.Plan is null && JavaInstall.HasContext && !JavaInstall.IsInstalled)
        || (JavaInstall.IsInstalled && _pendingJavaRow is not null);

    /// <summary>安装按钮文案：装之前写体积估算，装之后变成重启入口。</summary>
    public string JavaInstallButtonText
    {
        get
        {
            if (JavaInstall.IsInstalled)
            {
                return JavaInstall.InstalledMajor is int done ? $"已安装 Java {done}，重新启动" : "重新启动";
            }

            if (JavaInstall.Plan is not { CanAutoInstall: true, InstallMajor: int major })
            {
                return "打开 Java 设置";
            }

            var estimate = JavaProvisionPlanner.EstimateDownloadBytes(major);
            return $"安装 Java {major}（约 {Math.Max(1, estimate / (1024 * 1024))} MB）";
        }
    }

    /// <summary>
    /// 安装状态一变，版本页的三个派生属性与命令可用性都要重算。
    /// 合并前这里是靠逐个 observable 字段的 <c>NotifyCanExecuteChangedFor</c> 拼出来的，
    /// 漏一个就是「按钮灰着不亮」；现在统一挂在状态机的事件上。
    /// </summary>
    private void OnJavaInstallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMissingJava));
        OnPropertyChanged(nameof(JavaInstallButtonText));
        InstallMissingJavaCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 启动失败后调用：算出「装不装、装哪个」，把结论摆到界面上。任何一步失败都只降级为文案，不抛。
    /// </summary>
    private async Task ApplyMissingJavaAsync(VersionRow row, int requiredMajor)
    {
        _pendingJavaRow = row;
        JavaInstall.Reset();

        try
        {
            IReadOnlyList<JavaInfo> installed = _java is null
                ? Array.Empty<JavaInfo>()
                : await _java.DetectAsync().ConfigureAwait(true);

            // 加载器会改上界（1.12.2 Forge 只允许 Java ≤8），所以区间必须带加载器算。
            // 加载器版本名解析不出原版版本号时这里是 null，planner 会退化为「只按需求主版本」——
            // 退化只会让判据更宽松，不会给出装了也起不来的建议。
            var versionName = string.IsNullOrWhiteSpace(row.Info.MinecraftVersion)
                ? row.Name
                : row.Info.MinecraftVersion;

            // 第三个参数是加载器自身版本（Forge build / NeoForge / Fabric Loader）：
            // 同一代游戏里，45.0.40 的 Forge 只能到 Java 19、47.0.1 能到 21，
            // 不带它算出来的区间会让一键安装推荐一个装上也起不来的 Java。
            var range = _java?.GetAllowedRange(versionName, row.Info.Loader, row.Info.LoaderVersion);

            JavaInstall.Begin(requiredMajor, JavaProvisionPlanner.Plan(requiredMajor, installed, range));
        }
        catch (Exception ex)
        {
            // 启动已经失败了，探测再炸一次不能让页面跟着崩。
            JavaInstall.Begin(
                requiredMajor,
                plan: null,
                summary: $"检测 Java 失败：{ex.Message}。请到「设置 → Java」手动指定。");
        }
    }

    /// <summary>清掉缺 Java 状态。每次启动前调用，避免上一轮的结果挂在新一轮上。</summary>
    private void ClearMissingJavaState()
    {
        _pendingJavaRow = null;
        JavaInstall.Reset();
        CancelJavaInstall();
    }

    /// <summary>
    /// 一键装 Java / 装完重启。一个命令两种行为：
    /// 装之前是「下载」，装之后是「重新启动同一版本」——中间不需要用户再点一次版本列表。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallButtonBePressed))]
    private async Task InstallMissingJavaAsync()
    {
        if (JavaInstall.IsInstalled)
        {
            var row = _pendingJavaRow;
            if (row is not null)
            {
                await LaunchVersionAsync(row).ConfigureAwait(true);
            }

            return;
        }

        if (JavaInstall.Plan is not { CanAutoInstall: true, InstallMajor: int major } || _java is null)
        {
            // 装不了（上界冲突 / 没有下载包）：给出出口——设置页的 Java 分区。
            _navigation?.Navigate("settings");
            StatusText = JavaInstall.Summary ?? "请在「设置 → Java」手动指定 Java 路径。";
            return;
        }

        var progress = new Progress<DownloadProgress>(JavaInstall.ApplyProgress);

        // 写回顺序由状态机固定（写回 → 保存 → 强制重扫），版本页与首页用同一种落点。
        var result = await JavaInstall.InstallAsync(major, JavaInstallSink.SettingsFile(_settings), progress)
            .ConfigureAwait(true);

        if (result.Succeeded)
        {
            StatusText = $"Java {major} 已安装。点「重新启动」直接用新运行时再启动一次。";
        }
    }

    /// <summary>命令可用性：装完之后永远可点（重启），装之前只有 planner 说能装才可点。</summary>
    private bool CanInstallButtonBePressed() =>
        JavaInstall.IsInstalled || (JavaInstall.Plan is { CanAutoInstall: true } && JavaInstall.CanStart);

    /// <summary>装不了的时候的出口：跳到设置页的 Java 分区，并说明为什么要手动指定。</summary>
    [RelayCommand]
    private void OpenJavaSettings()
    {
        _navigation?.Navigate("settings");
        StatusText = JavaInstall.Summary ?? "请在「设置 → Java」手动指定 Java 路径。";
    }

    /// <summary>
    /// 截图专用：把版本页摆到「启动失败 + 缺 Java」的状态。
    ///
    /// 只在 <c>ScreenshotMode.IsActive</c> 下调用。文案与按钮由真实 planner 算出（喂一个空的已装列表），
    /// 摆的只是「启动失败了、这次需要一个 Java 25」这个上下文——不联网、不下载。
    /// </summary>
    internal void ApplyMissingJavaDemo()
    {
        const int required = 25;
        var plan = JavaProvisionPlanner.Plan(required, Array.Empty<JavaInfo>());

        _pendingJavaRow = SelectedRow ?? (Versions.Count > 0 ? Versions[0] : null);
        StatusText = $"启动失败：Minecraft 26.1 需要 Java {required} 或更高，本机没有找到满足要求的运行时。";

        JavaInstall.Reset();
        JavaInstall.Begin(required, plan);
    }

    /// <summary>页面离开时停掉在途下载；页面对象随后会被丢弃，不能留着 CTS 继续跑。</summary>
    private void CancelJavaInstall() => JavaInstall.Cancel();

    // ==================== 修复（两档） ====================

    // 单档修复只能发现「文件缺失」，发现不了「文件在但被截断 / 被替换」。
    // 两档把「秒级补文件」与「分钟级逐文件校验」分开，让用户自己决定代价。

    [ObservableProperty]
    private bool _isRepairBusy;

    [ObservableProperty]
    private bool _isRepairConfirmOpen;

    [ObservableProperty]
    private VersionRow? _pendingRepairRow;

    /// <summary>
    /// 「保存客户端 jar」的重入标志。0 = 空闲，1 = 有任务在跑。
    /// 用 <see cref="Interlocked"/> 而不是 bool 字段：这个入口由菜单点击直接触发，
    /// 两次点击可能落在同一个调度回合里，bool 的判断-赋值不是原子操作。
    /// </summary>
    private int _saveClientJarBusy;

    /// <summary>下拉里的「快速修复」：只补缺失文件与 0 字节 jar。</summary>
    [RelayCommand]
    private async Task RepairVersionAsync(VersionRow? row) => await RepairWithDepthAsync(row, RepairDepth.Quick);

    /// <summary>
    /// 两档修复入口，供下拉菜单调用。
    /// 深度档先弹确认：逐文件校验 libraries 与 assets 可能跑几分钟，不该点一下就默默锁住界面。
    /// </summary>
    public async Task RepairWithDepthAsync(VersionRow? row, RepairDepth depth)
    {
        if (row is null)
        {
            return;
        }

        if (_install is null)
        {
            StatusText = "安装服务未就绪，无法修复。";
            return;
        }

        // 界面已经禁用了入口，这里再挡一次：命令也可能被双击、快捷键或别的页面直接调用。
        if (row.IsRunning)
        {
            StatusText = $"{row.Name} 正在运行，先停止游戏（实例卡片上的「停止游戏」）再修复。";
            return;
        }

        if (depth == RepairDepth.Deep)
        {
            PendingRepairRow = row;
            IsRepairConfirmOpen = true;
            return;
        }

        await RunRepairAsync(row, depth);
    }

    [RelayCommand]
    private async Task ConfirmDeepRepairAsync()
    {
        var row = PendingRepairRow;
        IsRepairConfirmOpen = false;
        PendingRepairRow = null;

        if (row is null)
        {
            return;
        }

        await RunRepairAsync(row, RepairDepth.Deep);
    }

    [RelayCommand]
    private void CancelRepairConfirm()
    {
        IsRepairConfirmOpen = false;
        PendingRepairRow = null;
    }

    private async Task RunRepairAsync(VersionRow row, RepairDepth depth)
    {
        if (row.IsBusy || IsRepairBusy || _install is null)
        {
            return;
        }

        IsRepairBusy = true;
        row.IsBusy = true;
        row.BusyText = depth == RepairDepth.Deep ? "深度校验中…" : "校验中…";

        StatusText = depth == RepairDepth.Deep
            ? $"正在深度修复 {row.Name}：逐个校验 libraries 与 assets…"
            : $"正在快速修复 {row.Name}：检查缺失文件…";

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
                row.BusyText = $"{(depth == RepairDepth.Deep ? "校验" : "检查")} {(int)Math.Round(p.Ratio * 100)}%");

            var report = await _install.RepairAsync(row.Name, depth, progress);

            // 计数直接来自 RepairReport，UI 不做二次统计。
            StatusText = report.ChangedAnything
                ? $"修复完成：缺失 {report.Missing} · 校验失败 {report.Mismatched} · 已重建 {report.Redownloaded} · 无法修复 {report.Failed.Count}。"
                : report.Success
                    ? $"{row.Name} 文件完整（{(depth == RepairDepth.Deep ? "深度校验通过" : "未发现缺失")}）。"
                    : $"修复未完成：{report.Failed.Count} 项失败，首个原因：{report.Failed[0]}";

            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "修复已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"修复失败：{ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            row.BusyText = null;
            IsRepairBusy = false;
        }
    }

    // ==================== 版本组件（安装清单比对） ====================

    /// <summary>
    /// 组件面板是否展开。放在版本页内的浮层里：新增页面要改导航键与三处注册，
    /// 而这是「对着某个版本做的一次检查」，属于版本页的动作而不是独立目的地。
    /// </summary>
    [ObservableProperty]
    private bool _isComponentsOpen;

    [ObservableProperty]
    private VersionRow? _componentsRow;

    [ObservableProperty]
    private bool _isComponentsBusy;

    /// <summary>顶部汇总。完好时是「全部完好」，异常时带缺失 / 改动数，无清单时是原因。</summary>
    [ObservableProperty]
    private string _componentsSummary = string.Empty;

    /// <summary>清单缺失等导致「没有可检查内容」的原因；有清单时为 null。</summary>
    [ObservableProperty]
    private string? _componentsMessage;

    [ObservableProperty]
    private bool _componentsHealthy;

    [ObservableProperty]
    private bool _componentsHasFindings;

    /// <summary>问题文件超过 50 条时的提示（列表截断，计数完整）。</summary>
    [ObservableProperty]
    private string? _componentsTruncationNote;

    /// <summary>缺失文件总数（完整计数，不受列表截断影响）。</summary>
    private int _missingFileCount;

    public ObservableCollection<ComponentRow> Components { get; } = new();

    public ObservableCollection<string> ComponentsMissingFiles { get; } = new();

    public ObservableCollection<string> ComponentsModifiedFiles { get; } = new();

    public string ComponentsTitle => ComponentsRow is null ? "版本组件" : $"组件 · {ComponentsRow.Name}";

    public bool HasComponentsMessage => !string.IsNullOrWhiteSpace(ComponentsMessage);

    public bool HasMissingFiles => ComponentsMissingFiles.Count > 0;

    public bool HasModifiedFiles => ComponentsModifiedFiles.Count > 0;

    public bool HasTruncationNote => !string.IsNullOrWhiteSpace(ComponentsTruncationNote);

    /// <summary>「重新检查」可用条件：检查器已接入且没有正在跑的任务。</summary>
    public bool CanInspectComponents => _componentInspector is not null && !IsComponentsBusy;

    /// <summary>
    /// 「只修复缺失文件」可用条件：确实有缺失才可点。
    /// 只有改动（文件在但 mtime 不符）不给这个入口——那要靠重装或深度修复，不是补缺失。
    ///
    /// 运行中的版本同样不可点：面板可能是在启动之前打开的，游戏起来后这里必须跟着变灰。
    /// </summary>
    public bool CanRepairMissingFiles =>
        _install is not null && !IsComponentsBusy && _missingFileCount > 0 && ComponentsRow?.IsRunning != true;

    partial void OnComponentsRowChanged(VersionRow? value)
    {
        OnPropertyChanged(nameof(ComponentsTitle));
        OnPropertyChanged(nameof(CanRepairMissingFiles));
    }

    partial void OnIsComponentsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInspectComponents));
        OnPropertyChanged(nameof(CanRepairMissingFiles));
    }

    partial void OnComponentsMessageChanged(string? value) => OnPropertyChanged(nameof(HasComponentsMessage));

    partial void OnComponentsTruncationNoteChanged(string? value) => OnPropertyChanged(nameof(HasTruncationNote));

    /// <summary>打开组件面板并立刻检查一次（面板打开即有内容，不用再点一下）。</summary>
    [RelayCommand]
    private async Task OpenComponentsAsync(VersionRow? row)
    {
        var target = row ?? SelectedRow;

        if (target is null)
        {
            StatusText = "先选择一个版本。";
            return;
        }

        if (target.IsRunning)
        {
            StatusText = $"{target.Name} 正在运行，先停止游戏（实例卡片上的「停止游戏」）再检查组件。";
            return;
        }

        ComponentsRow = target;
        IsComponentsOpen = true;
        await RefreshComponentsAsync();
    }

    [RelayCommand]
    private void CloseComponents()
    {
        IsComponentsOpen = false;
        ComponentsRow = null;
        Components.Clear();
        ComponentsMissingFiles.Clear();
        ComponentsModifiedFiles.Clear();
        ComponentsSummary = string.Empty;
        ComponentsMessage = null;
        ComponentsTruncationNote = null;
        ComponentsHealthy = false;
        ComponentsHasFindings = false;
        _missingFileCount = 0;
        OnPropertyChanged(nameof(CanRepairMissingFiles));
    }

    /// <summary>重新读盘检查。只读清单与 mtime，不联网、不改任何文件。</summary>
    [RelayCommand]
    private async Task RefreshComponentsAsync()
    {
        var row = ComponentsRow;

        if (row is null)
        {
            return;
        }

        if (_componentInspector is null)
        {
            Components.Clear();
            ComponentsMissingFiles.Clear();
            ComponentsModifiedFiles.Clear();
            ComponentsHealthy = false;
            ComponentsHasFindings = false;
            ComponentsTruncationNote = null;
            _missingFileCount = 0;
            ComponentsSummary = "组件检查服务未就绪，无法检查。";
            ComponentsMessage = ComponentsSummary;
            OnPropertyChanged(nameof(CanRepairMissingFiles));
            return;
        }

        IsComponentsBusy = true;

        try
        {
            var report = await _componentInspector.InspectAsync(row.Name);

            Components.Clear();
            foreach (var status in report.Components)
            {
                Components.Add(new ComponentRow(status));
            }

            ComponentsMissingFiles.Clear();
            foreach (var file in report.MissingFiles)
            {
                ComponentsMissingFiles.Add(file);
            }

            ComponentsModifiedFiles.Clear();
            foreach (var file in report.ModifiedFiles)
            {
                ComponentsModifiedFiles.Add(file);
            }

            _missingFileCount = report.MissingCount;
            ComponentsSummary = report.Summary;
            ComponentsMessage = report.HasManifest ? null : report.Message;
            ComponentsHealthy = report.IsHealthy;
            ComponentsHasFindings = report.HasFindings;
            ComponentsTruncationNote = report.IsTruncated
                ? "问题文件较多：下面只列前 50 条，完整计数见上方汇总。"
                : null;
        }
        catch (OperationCanceledException)
        {
            ComponentsSummary = "检查已取消。";
        }
        catch (Exception ex)
        {
            ComponentsSummary = $"组件检查失败：{ex.Message}";
            ComponentsMessage = ComponentsSummary;
        }
        finally
        {
            IsComponentsBusy = false;
            OnPropertyChanged(nameof(CanRepairMissingFiles));
        }
    }

    /// <summary>
    /// 只修复缺失文件：复用版本页已有的快速修复入口（<see cref="RepairWithDepthAsync"/>），
    /// 面板不实现任何修复逻辑，修完再重新检查一次把结果刷新出来。
    /// </summary>
    [RelayCommand]
    private async Task RepairMissingComponentsAsync()
    {
        var row = ComponentsRow;

        if (row is null || _install is null)
        {
            return;
        }

        await RepairWithDepthAsync(row, RepairDepth.Quick);

        // 修复内部会重建版本列表：ComponentsRow 指向的是旧对象，但版本名仍然有效。
        await RefreshComponentsAsync();
    }

    [RelayCommand]
    private async Task OpenVersionFolderAsync(VersionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_platform is null || _paths is null)
        {
            StatusText = "平台服务未就绪，无法打开目录。";
            return;
        }

        try
        {
            var directory = Path.Combine(_paths.VersionsDirectory, row.Name);
            await _platform.OpenFolderAsync(directory);
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把某个版本的客户端 jar 另存到用户选的目录，**不安装、不改动版本目录**。
    ///
    /// 目录由 code-behind 用文件夹选择器取（存储选择器在 Avalonia 里属于视图层），
    /// 这里只负责下载与状态反馈。同名文件已存在且 SHA1 对得上时直接跳过，不重下。
    /// </summary>
    public async Task SaveClientJarAsync(VersionRow? row, string targetDirectory)
    {
        if (row is null || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return;
        }

        if (_versions is null)
        {
            StatusText = "版本服务未就绪，无法保存客户端。";
            return;
        }

        // 一个实例里只允许一次：菜单在下载期间仍可再点，两次同时写同一个目标文件会互相踩。
        if (Interlocked.CompareExchange(ref _saveClientJarBusy, 1, 0) != 0)
        {
            StatusText = "已经有一个保存任务在进行，请等它结束。";
            return;
        }

        StatusText = $"正在保存 {row.Name} 的客户端 jar…";

        try
        {
            var result = await _versions
                .SaveClientJarAsync(row.Name, targetDirectory)
                .ConfigureAwait(true);

            StatusText = result.Success
                ? result.AlreadyExisted
                    ? $"目标目录里已有同一份客户端 jar：{result.FilePath}"
                    : $"已保存客户端 jar（{FormatSize(result.Size)}）：{result.FilePath}"
                : $"保存客户端 jar 失败：{result.Error}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存客户端 jar 失败：{ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _saveClientJarBusy, 0);
        }
    }

    /// <summary>把字节数写成人类可读的大小。只用于状态行，不参与任何判定。</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "未知大小",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    [RelayCommand]
    private void RequestDelete(VersionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsRunning)
        {
            StatusText = $"{row.Name} 正在运行，先停止游戏（实例卡片上的「停止游戏」）再删除。";
            return;
        }

        PendingDelete = row;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsConfirmOpen = false;
        PendingDelete = null;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var row = PendingDelete;
        IsConfirmOpen = false;
        PendingDelete = null;

        if (row is null)
        {
            return;
        }

        if (_versions is null)
        {
            StatusText = "版本服务未就绪，无法删除。";
            return;
        }

        try
        {
            await _versions.DeleteAsync(row.Name);
            StatusText = $"已删除 {row.Name}。";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void NavigateTo(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_navigation is null)
        {
            StatusText = "导航服务未就绪，无法跳转页面。";
            return;
        }

        _navigation.Navigate(key);
    }

    // ==================== 实例操作 ====================

    // ---- 实例设置面板：对应 PCL2 的 VersionAdvance* / VersionArgument* 实例级设置 ----

    /// <summary>实例设置面板是否展开。</summary>
    [ObservableProperty]
    private bool _isInstanceEditorOpen;

    [ObservableProperty]
    private InstanceRow? _editingInstance;

    /// <summary>null 表示跟随全局；空字符串表示显式清空（PCL2 的语义区分）。</summary>
    [ObservableProperty]
    private string _editorJvmArguments = string.Empty;

    [ObservableProperty]
    private string _editorGameArguments = string.Empty;

    [ObservableProperty]
    private string _editorWindowTitle = string.Empty;

    [ObservableProperty]
    private string _editorBeforeCommand = string.Empty;

    [ObservableProperty]
    private string _editorAfterCommand = string.Empty;

    [ObservableProperty]
    private bool _editorWaitCommands = true;

    [ObservableProperty]
    private string _editorServerAddress = string.Empty;

    /// <summary>实例备注（PCL2 的 <c>VersionArgumentInfo</c>）。空表示没有备注，落盘写成 null。</summary>
    [ObservableProperty]
    private string _editorNotes = string.Empty;

    // ---- 登录服务器：对 PCL2 的 VersionServerAuth* 三项 ----
    //
    // 与上面的「服务器地址」（直连服务器）不是一回事：那个决定进游戏后连哪个服，
    // 这一组决定客户端认哪套验证服（authlib-injector 注入哪个 API 根）。

    /// <summary>
    /// 实例级认证服务器地址。三态见 <see cref="FollowAccountAuthServer"/>；
    /// 落盘规则见 <see cref="ResolveInstanceAuthServer"/>。
    /// </summary>
    [ObservableProperty]
    private string _editorAuthServerUrl = string.Empty;

    /// <summary>验证服务器的显示名，纯展示（面板上写「登录服务器：XX 服」）。</summary>
    [ObservableProperty]
    private string _editorAuthServerName = string.Empty;

    /// <summary>注册链接，只用于显示「注册」按钮，不参与启动参数。</summary>
    [ObservableProperty]
    private string _editorAuthRegisterUrl = string.Empty;

    /// <summary>
    /// 勾上表示本实例不覆盖验证服地址（落盘为 null，启动时用账户的）；
    /// 取消勾选后：框里留空 = 明确不注入 authlib-injector，有值 = 用这个地址。
    ///
    /// 与 <see cref="FollowGlobalVersionType"/> 完全同一套三态，理由也一样：
    /// 少了「跟随账户」与「明确关闭」的区分，用户在这个实例上清空输入框只会把账户地址
    /// 又拿回来，就永远关不掉。
    /// </summary>
    [ObservableProperty]
    private bool _followAccountAuthServer = true;

    partial void OnFollowAccountAuthServerChanged(bool value) => RaiseAuthServerPresentation();

    partial void OnEditorAuthServerUrlChanged(string value) => RaiseAuthServerPresentation();

    partial void OnEditorAuthRegisterUrlChanged(string value) => RaiseAuthServerPresentation();

    private void RaiseAuthServerPresentation()
    {
        OnPropertyChanged(nameof(EditorAuthServerHint));
        OnPropertyChanged(nameof(HasEditorAuthRegisterLink));
        OnPropertyChanged(nameof(EditorAuthRegisterHint));
    }

    /// <summary>面板里那行灰字：把「这次到底会不会注入、用哪个地址」说清楚，不让用户自己推断三态。</summary>
    public string EditorAuthServerHint => FollowAccountAuthServer
        ? DescribeAccountAuthServer()
        : string.IsNullOrWhiteSpace(EditorAuthServerUrl)
            ? "本实例不注入 authlib-injector（即使账户是第三方验证账户也直接裸连）。"
            : $"本实例用「{EditorAuthServerUrl.Trim()}」登录，覆盖账户里的地址。";

    private string DescribeAccountAuthServer()
    {
        var account = _accounts?.ActiveAccount;

        if (account is null)
        {
            return "跟随账户：当前没有选中账户。";
        }

        if (account.Type != AccountType.Authlib)
        {
            return $"跟随账户：当前账户「{account.Username}」不是第三方验证账户，启动时不会注入 authlib-injector。";
        }

        return string.IsNullOrWhiteSpace(account.AuthServerUrl)
            ? "跟随账户：当前账户是第三方验证账户但没填 API 根，启动时不会注入。"
            : $"跟随账户：本次用账户里的「{account.AuthServerUrl!.Trim()}」。";
    }

    /// <summary>
    /// 注册按钮是否显示。判据是 PCL2 的 <c>ValidateHttp</c>：只认 <c>http://</c> 与 <c>https://</c>。
    /// 用户少打了 scheme 时不显示按钮，改为在提示行里说清楚要补什么。
    /// </summary>
    public bool HasEditorAuthRegisterLink => AuthServerAddressPolicy.IsHttpUrl(EditorAuthRegisterUrl);

    public string EditorAuthRegisterHint
    {
        get
        {
            var raw = EditorAuthRegisterUrl?.Trim();

            if (string.IsNullOrEmpty(raw))
            {
                return "填完整网址后，这里会出现一个「注册」按钮。";
            }

            return AuthServerAddressPolicy.IsHttpUrl(raw)
                ? "点击「注册」会用系统浏览器打开。"
                : "需要以 http:// 或 https:// 开头才会显示「注册」按钮。";
        }
    }

    /// <summary>用系统浏览器打开实例绑定的注册链接。地址非法时只更新面板状态行，不弹浏览器。</summary>
    [RelayCommand]
    private async Task OpenAuthRegisterAsync()
    {
        var url = EditorAuthRegisterUrl?.Trim();

        if (_platform is null)
        {
            InstanceEditorStatus = "当前环境无法打开浏览器。";
            return;
        }

        if (!AuthServerAddressPolicy.IsHttpUrl(url))
        {
            InstanceEditorStatus = "注册链接需要以 http:// 或 https:// 开头。";
            return;
        }

        await _platform.OpenUrlAsync(url!).ConfigureAwait(true);
    }

    /// <summary>
    /// 实例级版本信息（<c>--versionType</c>）。本框的编辑器分三态：
    /// <list type="bullet">
    /// <item><see cref="FollowGlobalVersionType"/> 勾上 → 存 <c>null</c>，启动时用全局值。</item>
    /// <item>不勾、框里留空 → 存 <c>""</c>，明确不显示（压掉全局值）。</item>
    /// <item>不勾、框里有值 → 存该值。</item>
    /// </list>
    /// 这个三步不是冗余：只有「跟随全局」与「明确关闭」分开，用户才能在全局填了整合包名的
    /// 前提下，把某个实例单独恢复成不显示。PCL2 只有一层（实例优先、全局兜底），
    /// 在它那里清空输入框等于回到全局值，关不掉。
    /// </summary>
    [ObservableProperty]
    private string _editorVersionType = string.Empty;

    /// <summary>勾上表示不覆盖全局（落盘为 null）；取消勾选才能用下面这个框。</summary>
    [ObservableProperty]
    private bool _followGlobalVersionType = true;

    partial void OnFollowGlobalVersionTypeChanged(bool value) =>
        OnPropertyChanged(nameof(EditorVersionTypeHint));

    partial void OnEditorVersionTypeChanged(string value) =>
        OnPropertyChanged(nameof(EditorVersionTypeHint));

    /// <summary>面板里那行灰字：把「现在到底会显示什么」说清楚，而不是让用户自己推断三态。</summary>
    public string EditorVersionTypeHint => FollowGlobalVersionType
        ? DescribeGlobalVersionType()
        : string.IsNullOrWhiteSpace(EditorVersionType)
            ? "本实例不显示版本名（即使全局填了也不显示）。"
            : $"本实例显示「{EditorVersionType.Trim()}」。";

    private string DescribeGlobalVersionType()
    {
        var global = _settings?.Current.VersionArgumentInfo;

        return string.IsNullOrWhiteSpace(global)
            ? "跟随全局：全局留空，主菜单不显示版本名。"
            : $"跟随全局：全局值是「{global.Trim()}」。";
    }

    [ObservableProperty]
    private GcOption? _editorGcOption;

    [ObservableProperty]
    private bool _isInstanceEditorBusy;

    [ObservableProperty]
    private string? _instanceEditorStatus;

    /// <summary>GC 选项。中文说明写在卡片里，避免用户对着 <c>-XX:+UseG1GC</c> 猜哪个适合自己。</summary>
    public IReadOnlyList<GcOption> GcOptions { get; } =
    [
        new(GcKind.Auto, "自动", "不追加 GC 参数，由 JVM 决定（旧版本 Java 更稳）。"),
        new(GcKind.G1, "G1", "现代默认，兼顾吞吐与停顿；Java 8 及以上可用。"),
        new(GcKind.Z, "ZGC", "低延迟，适合大堆；需要 Java 15 及以上。"),
        new(GcKind.Parallel, "Parallel", "吞吐优先，低配或单核机器更合适。"),
        new(GcKind.Serial, "Serial", "单线程，内存很小的环境使用。"),
    ];

    public string InstanceEditorTitle => EditingInstance is null
        ? "实例设置"
        : $"实例设置 · {EditingInstance.Name}";

    public bool HasInstanceEditorStatus => !string.IsNullOrWhiteSpace(InstanceEditorStatus);

    partial void OnEditingInstanceChanged(InstanceRow? value) => OnPropertyChanged(nameof(InstanceEditorTitle));

    partial void OnInstanceEditorStatusChanged(string? value) => OnPropertyChanged(nameof(HasInstanceEditorStatus));

    // ---- 完整备注：卡片上只有一行省略文本，长备注要能看全文 ----

    /// <summary>备注浮层是否展开。只在实例确实有备注时才可能为 true。</summary>
    [ObservableProperty]
    private bool _isNotesOpen;

    [ObservableProperty]
    private InstanceRow? _notesInstance;

    public string NotesTitle => NotesInstance is null ? "实例备注" : $"备注 · {NotesInstance.Name}";

    public string NotesBody => NotesInstance?.NoteText ?? string.Empty;

    partial void OnNotesInstanceChanged(InstanceRow? value)
    {
        OnPropertyChanged(nameof(NotesTitle));
        OnPropertyChanged(nameof(NotesBody));
    }

    /// <summary>打开完整备注。没有备注的实例不弹窗——空浮层比没有浮层更令人困惑。</summary>
    [RelayCommand]
    private void OpenNotes(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null || !target.HasNote)
        {
            return;
        }

        NotesInstance = target;
        IsNotesOpen = true;
    }

    [RelayCommand]
    private void CloseNotes()
    {
        IsNotesOpen = false;
        NotesInstance = null;
    }

    /// <summary>打开实例设置面板并回填当前值。</summary>
    [RelayCommand]
    private void OpenInstanceEditor(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null)
        {
            StatusText = "先在列表里选择一个实例。";
            return;
        }

        EditingInstance = target;
        var info = target.Info;

        EditorJvmArguments = info.JvmArguments ?? string.Empty;
        EditorGameArguments = info.GameArguments ?? string.Empty;
        EditorWindowTitle = info.WindowTitle ?? string.Empty;
        EditorBeforeCommand = info.LaunchBeforeCommand ?? string.Empty;
        EditorAfterCommand = info.LaunchAfterCommand ?? string.Empty;
        EditorWaitCommands = info.WaitForLaunchCommands;
        EditorServerAddress = info.ServerAddress ?? string.Empty;
        EditorNotes = info.Notes ?? string.Empty;

        // 回填三态：null 才是「跟随全局」，空串是「明确关闭」——不能把两者都读成空框。
        var versionType = ParseInstanceVersionType(info.VersionType);
        FollowGlobalVersionType = versionType.FollowGlobal;
        EditorVersionType = versionType.Value;

        // 登录服务器同样是三态，同样不能把 null 与空串都读成「空框」：
        // 前者是「跟随账户」，后者是「本实例不注入 authlib」。
        var authServer = ParseInstanceAuthServer(info.AuthServerUrl);
        FollowAccountAuthServer = authServer.FollowAccount;
        EditorAuthServerUrl = authServer.Value;
        EditorAuthServerName = info.AuthServerName ?? string.Empty;
        EditorAuthRegisterUrl = info.AuthRegisterUrl ?? string.Empty;

        EditorGcOption = GcOptions.FirstOrDefault(option => option.Kind == info.GcKind) ?? GcOptions[0];

        InstanceEditorStatus = null;
        IsInstanceEditorOpen = true;
    }

    [RelayCommand]
    private void CloseInstanceEditor()
    {
        IsInstanceEditorOpen = false;
        EditingInstance = null;
        InstanceEditorStatus = null;
    }

    /// <summary>把面板里的值写回实例并落盘。</summary>
    [RelayCommand]
    private async Task SaveInstanceEditorAsync()
    {
        var row = EditingInstance;

        if (row is null || _instances is null)
        {
            InstanceEditorStatus = "实例服务未就绪，无法保存。";
            return;
        }

        IsInstanceEditorBusy = true;

        try
        {
            var info = row.Info;

            // 空字符串统一写成 null：null = 跟随全局，空串会让「跟随全局」与「显式清空」没法区分。
            info.JvmArguments = Blank(EditorJvmArguments);
            info.GameArguments = Blank(EditorGameArguments);
            info.WindowTitle = Blank(EditorWindowTitle);
            info.LaunchBeforeCommand = Blank(EditorBeforeCommand);
            info.LaunchAfterCommand = Blank(EditorAfterCommand);
            info.WaitForLaunchCommands = EditorWaitCommands;
            info.ServerAddress = Blank(EditorServerAddress);
            info.Notes = Blank(EditorNotes);
            info.GcKind = EditorGcOption?.Kind ?? GcKind.Auto;

            // 版本信息是唯一不走 Blank() 的字段：Blank() 把空串归一成 null，
            // 而这里 null 与空串是两种语义（跟随全局 / 明确不显示），见 InstanceInfo.VersionType。
            info.VersionType = ResolveInstanceVersionType(FollowGlobalVersionType, EditorVersionType);

            // 登录服务器地址是第二个这样的字段：null = 跟随账户，空串 = 明确不注入 authlib。
            // 注册链接与服务器名只是展示文本，空就写 null（Blank 的常规语义）。
            info.AuthServerUrl = ResolveInstanceAuthServer(FollowAccountAuthServer, EditorAuthServerUrl);
            info.AuthServerName = Blank(EditorAuthServerName);
            info.AuthRegisterUrl = Blank(EditorAuthRegisterUrl);

            await _instances.SaveAsync(info);

            await LoadInstancesAsync();
            SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.Info.Id, info.Id, StringComparison.Ordinal))
                               ?? SelectedInstance;

            InstanceEditorStatus = $"已保存 · {DateTime.Now:HH:mm:ss}";
            StatusText = $"实例「{info.Name}」的设置已保存。";
        }
        catch (OperationCanceledException)
        {
            InstanceEditorStatus = "已取消。";
        }
        catch (Exception ex)
        {
            InstanceEditorStatus = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsInstanceEditorBusy = false;
        }
    }

    /// <summary>
    /// 导出该实例的启动脚本。落点是启动器数据目录下的 exports/，不在实例目录里——
    /// 删掉实例时不应该连脚本一起丢（PCL2 把 LatestLaunch.bat 写在 PCL 自己的目录）。
    /// </summary>
    [RelayCommand]
    private async Task ExportInstanceScriptAsync()
    {
        var row = EditingInstance;

        if (row is null || _launcher is null || _paths is null)
        {
            InstanceEditorStatus = "启动服务未就绪，无法导出脚本。";
            return;
        }

        IsInstanceEditorBusy = true;

        try
        {
            var account = await ResolveAccountAsync().ConfigureAwait(true);

            if (account is null)
            {
                InstanceEditorStatus = "没有可用账户，无法生成启动命令。";
                return;
            }

            var settings = _settings?.Current ?? new AppSettings();
            var directory = Path.Combine(_paths.RootDirectory, "exports");
            var safeName = MakeSafeFileName(row.Info.Name);
            var extension = OperatingSystem.IsWindows() ? ".bat" : ".sh";
            var target = Path.Combine(directory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");

            // 导出脚本必须与真实启动用完全相同的合成规则：脚本能跑、启动器不能跑
            // 是最难排查的一类问题。所以这里也走构建器，只把「导出」这个前提补进解析结果：
            // 目录与真实启动时一致（同一个实例目录），实例就是当前正在编辑的这一行。
            var resolution = new InstanceResolution(
                _instances?.GetGameDirectory(row.Info.Id) ?? string.Empty,
                row.Info,
                Isolated: true,
                Created: false);

            var options = LaunchOptionsFactory.Build(
                account,
                row.Info.VersionName,
                resolution,
                settings,
                row.Info.ServerAddress);

            var path = await _launcher.ExportLaunchScriptAsync(options, target).ConfigureAwait(true);

            InstanceEditorStatus = path is null
                ? "导出失败：构建启动命令失败，请先确认该版本能正常启动。"
                : $"已导出（accessToken 已打码）：{path}";
        }
        catch (OperationCanceledException)
        {
            InstanceEditorStatus = "已取消。";
        }
        catch (Exception ex)
        {
            InstanceEditorStatus = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsInstanceEditorBusy = false;
        }
    }

    private async Task<Account?> ResolveAccountAsync()
    {
        if (_accounts is null)
        {
            return null;
        }

        try
        {
            return await _accounts.EnsureValidActiveAccountAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"获取账户失败：{ex.Message}";
            return null;
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 面板三态 → 落盘值。<c>null</c>（跟随全局）/ <c>""</c>（明确不显示）/ 具体值。
    ///
    /// 抽成静态函数是为了能单测：<see cref="VersionsViewModel"/> 构造会碰 Avalonia 图标静态初始化，
    /// 测试里造不出来。「跟随全局」这个分支最容易写错成「空串也算跟随」，值得一条独立用例守着。
    /// 顺带把长度收口放在这里（去空白、截到 32 字符）：手改 <c>instance.json</c> 之后再打开面板保存，
    /// 不该把超长值原样写回去，也不能只靠 <c>TextBox.MaxLength</c>。
    /// </summary>
    internal static string? ResolveInstanceVersionType(bool followGlobal, string? editorValue) =>
        followGlobal ? null : SettingsViewModel.ClipVersionArgumentInfo(editorValue);

    /// <summary>
    /// 落盘值 → 面板三态。只有 <c>null</c> 才是「跟随全局」，空串必须回填成「不跟随 + 空框」，
    /// 否则用户在实例上关掉的版本名会在下次打开面板时被重新打开。
    /// </summary>
    internal static (bool FollowGlobal, string Value) ParseInstanceVersionType(string? stored) =>
        (stored is null, stored ?? string.Empty);

    /// <summary>
    /// 登录服务器面板三态 → 落盘值。勾「跟随账户」写 <c>null</c>；
    /// 不勾时 trim 后原样写，空串保留为空串（= 明确不注入 authlib-injector）。
    ///
    /// 与 <see cref="ResolveInstanceVersionType"/> 同一套理由：空串若被归一成 null，
    /// 用户在实例上清空输入框只会把账户地址又拿回来，这个实例就永远关不掉注入。
    /// </summary>
    internal static string? ResolveInstanceAuthServer(bool followAccount, string? editorValue) =>
        followAccount ? null : (editorValue ?? string.Empty).Trim();

    /// <summary>
    /// 落盘值 → 面板三态。只有 <c>null</c> 才是「跟随账户」，空串必须回填成
    /// 「不跟随 + 空框」，否则「明确不注入」在下次打开面板时会被重新打开。
    /// </summary>
    internal static (bool FollowAccount, string Value) ParseInstanceAuthServer(string? stored) =>
        (stored is null, stored ?? string.Empty);

    /// <summary>
    /// 版本名 → 目录名。走 <see cref="Core.Paths.PathSafety"/>：非法字符、Windows 保留名（<c>CON</c> 之类）、
    /// 结尾的点与空格、长度上限都由它统一处理，调用点不要再各写一套。
    /// </summary>
    private static string MakeSafeFileName(string name) =>
        NebulaLauncher.Core.Paths.PathSafety.SanitizeDirectoryName(name, "instance");

    /// <summary>
    /// 为选中版本新建实例。
    ///
    /// 一个版本可以有多个实例，但每个实例必须独占一个版本目录：
    /// 版本已有实例时走克隆（新版本目录 <c>&lt;版本名&gt;-副本</c>，复制版本本体、不带玩家内容），
    /// 没有实例时才直接建。这样「同一版本两个实例」表现为两个不同版本目录，而不是共用目录互相覆盖。
    /// </summary>
    [RelayCommand]
    private async Task CreateInstanceAsync(VersionRow? row)
    {
        if (IsInstanceBusy)
        {
            return;
        }

        var target = row ?? SelectedRow;
        if (target is null)
        {
            StatusText = "先在列表里选择一个版本，再新建实例。";
            return;
        }

        if (_instances is null)
        {
            StatusText = "实例服务未就绪，无法创建实例。";
            return;
        }

        IsInstanceBusy = true;
        try
        {
            var existing = Instances.FirstOrDefault(instance =>
                string.Equals(instance.Info.VersionName, target.Name, StringComparison.Ordinal));

            if (existing is not null)
            {
                // 同版本第二个实例：版本目录不能复用，克隆出新版本目录，内容从零开始。
                var clone = await _instances.CloneAsync(existing.Info.Id, new InstanceCloneOptions
                {
                    IncludeMods = false,
                    IncludeConfig = false,
                    IncludeSaves = false,
                    IncludeResourcePacks = false,
                    IncludeShaderPacks = false,
                });

                if (clone is null)
                {
                    StatusText = $"为「{target.Name}」新建实例失败，详见日志。";
                    return;
                }

                StatusText = $"「{target.Name}」已有实例，已新建独立实例「{clone.Name}」，版本目录 {clone.VersionName}（mods / saves 从空开始）。";

                await LoadInstancesAsync();
                SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.Info.Id, clone.Id, StringComparison.Ordinal))
                                   ?? SelectedInstance;
                return;
            }

            var name = InstanceLocator.MakeUniqueName(target.Name, Instances.Select(i => i.Info.Name));
            var created = await _instances.CreateAsync(name, target.Name);

            StatusText = $"已创建实例「{created.Name}」，它的 mods / saves / config 与其它版本隔离。";

            await LoadInstancesAsync();
            SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.Info.Id, created.Id, StringComparison.Ordinal))
                               ?? SelectedInstance;
        }
        catch (OperationCanceledException)
        {
            StatusText = "创建实例已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"创建实例失败：{ex.Message}";
        }
        finally
        {
            IsInstanceBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenInstanceFolderAsync(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;
        if (target is null)
        {
            StatusText = "还没有选中实例。";
            return;
        }

        if (_instances is null || _platform is null)
        {
            StatusText = "平台服务未就绪，无法打开实例目录。";
            return;
        }

        try
        {
            var directory = _instances.GetGameDirectory(target.Info.Id);
            Directory.CreateDirectory(directory);
            await _platform.OpenFolderAsync(directory);
        }
        catch (Exception ex)
        {
            StatusText = $"打开实例目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 打开实例的某个子目录（存档 / 模组 / 资源包 / 截图 / 日志）。
    ///
    /// 与「打开目录」的区别是**先建目录再打开**：<c>screenshots/</c> 在玩家第一次按 F2
    /// 之前根本不存在，直接交给资源管理器会得到「系统找不到指定的路径」。PCL2 的目录按钮
    /// 也是先 <c>DirectoryUtils.Create</c> 再 <c>OpenExplorer</c>。
    ///
    /// 路径只从该实例真实的游戏目录拼（<see cref="IInstanceService.GetGameDirectory"/> + 相对名），
    /// 越界拼接由 Core 的 <see cref="IInstanceFolderService.ResolvePath"/> 拒绝。
    /// 打开目录是只读操作，实例运行中不受影响。
    /// </summary>
    [RelayCommand]
    private async Task OpenInstanceSubfolderAsync(InstanceFolderChip? chip)
    {
        if (chip is null)
        {
            return;
        }

        if (_instances is null || _platform is null || _instanceFolders is null)
        {
            StatusText = "平台服务未就绪，无法打开目录。";
            return;
        }

        try
        {
            var gameDirectory = _instances.GetGameDirectory(chip.InstanceId);
            var path = _instanceFolders.ResolvePath(gameDirectory, chip.Key);

            if (path is null)
            {
                StatusText = $"无法定位「{chip.DisplayName}」目录：实例目录不合法。";
                return;
            }

            Directory.CreateDirectory(path);

            // 点过之后就不再是「还不存在」，不用等整页刷新。
            chip.Exists = true;

            await _platform.OpenFolderAsync(path);
        }
        catch (Exception ex)
        {
            StatusText = $"打开「{chip.DisplayName}」目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RequestDeleteInstance(InstanceRow? row)
    {
        if (row is null)
        {
            return;
        }

        // 移入回收站要移动整个目录：游戏开着的时候搬走 mods/ 与 saves/，
        // 结果是游戏运行中丢档（Windows 上还会因为文件被占用搬到一半）。
        if (row.IsRunning)
        {
            StatusText = $"「{row.Name}」正在运行，先停止游戏（实例卡片上的「停止游戏」）再移入回收站。";
            return;
        }

        PendingDeleteInstance = row;
        IsInstanceConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelDeleteInstance()
    {
        IsInstanceConfirmOpen = false;
        PendingDeleteInstance = null;
    }

    [RelayCommand]
    private async Task ConfirmDeleteInstanceAsync()
    {
        var row = PendingDeleteInstance;
        IsInstanceConfirmOpen = false;
        PendingDeleteInstance = null;

        if (row is null)
        {
            return;
        }

        if (_instances is null)
        {
            StatusText = "实例服务未就绪，无法删除实例。";
            return;
        }

        IsInstanceBusy = true;
        try
        {
            // 不直接删目录：先移入启动器回收站，用户还有一次反悔机会。
            var entry = await _instances.MoveToTrashAsync(row.Info.Id);

            if (entry is null)
            {
                StatusText = $"移入回收站失败：「{row.Name}」的目录可能被游戏进程或资源管理器占用，详见日志。";
                return;
            }

            // 删完自动展开回收站面板：这时候用户最想知道的就是「还能不能恢复」。
            _trashManuallyClosed = false;
            StatusText = $"「{row.Name}」已移入回收站，可在回收站面板恢复。";

            await LoadInstancesAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "删除已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"删除实例失败：{ex.Message}";
        }
        finally
        {
            IsInstanceBusy = false;
        }
    }

    // ==================== 克隆 / 重命名 / 图标 / 清理 ====================

    [ObservableProperty]
    private bool _isRenameOpen;

    [ObservableProperty]
    private InstanceRow? _renamingInstance;

    [ObservableProperty]
    private string _renameDraft = string.Empty;

    [ObservableProperty]
    private string? _renameError;

    [ObservableProperty]
    private bool _isRenameBusy;

    [ObservableProperty]
    private bool _isIconPickerOpen;

    [ObservableProperty]
    private InstanceRow? _iconEditingInstance;

    [ObservableProperty]
    private bool _isIconBusy;

    [ObservableProperty]
    private bool _isCleanupOpen;

    [ObservableProperty]
    private InstanceRow? _cleanupInstance;

    [ObservableProperty]
    private bool _cleanupLogs = true;

    [ObservableProperty]
    private bool _cleanupCrashReports = true;

    [ObservableProperty]
    private bool _cleanupDebugProfile = true;

    [ObservableProperty]
    private bool _cleanupScreenshots;

    [ObservableProperty]
    private CleanupRangeOption? _cleanupRange;

    [ObservableProperty]
    private bool _isCleanupBusy;

    [ObservableProperty]
    private string? _cleanupStatus;

    public bool HasRenameError => !string.IsNullOrWhiteSpace(RenameError);

    public bool HasCleanupStatus => !string.IsNullOrWhiteSpace(CleanupStatus);

    public string RenameTitle => RenamingInstance is null ? "重命名实例" : $"重命名「{RenamingInstance.Name}」";

    public string IconPickerTitle => IconEditingInstance is null ? "实例图标" : $"选择图标 · {IconEditingInstance.Name}";

    public string CleanupTitle => CleanupInstance is null ? "清理实例目录" : $"清理「{CleanupInstance.Name}」";

    /// <summary>
    /// 12 种内置图标。自绘线条路径，不引图片资源。
    ///
    /// 用属性而不是字段初始化器：<see cref="InstanceIcons"/> 的静态构造会解析 Geometry，
    /// 需要已初始化的 Avalonia 平台。构造 ViewModel 时不该付这个代价——那会让本页的
    /// 非 UI 逻辑（例如「停止游戏」的接线）没法在没有图形栈的测试里跑。
    /// </summary>
    public IReadOnlyList<IconChoice> IconChoices => InstanceIcons.All;

    /// <summary>清理范围。0 天 = 全部。</summary>
    public IReadOnlyList<CleanupRangeOption> CleanupRanges { get; } =
    [
        new(0, "全部文件"),
        new(7, "只清 7 天前的"),
        new(30, "只清 30 天前的"),
        new(90, "只清 90 天前的"),
    ];

    partial void OnRenameErrorChanged(string? value) => OnPropertyChanged(nameof(HasRenameError));

    partial void OnCleanupStatusChanged(string? value) => OnPropertyChanged(nameof(HasCleanupStatus));

    partial void OnRenamingInstanceChanged(InstanceRow? value) => OnPropertyChanged(nameof(RenameTitle));

    partial void OnIconEditingInstanceChanged(InstanceRow? value) => OnPropertyChanged(nameof(IconPickerTitle));

    partial void OnCleanupInstanceChanged(InstanceRow? value) => OnPropertyChanged(nameof(CleanupTitle));

    /// <summary>复制一份实例：内容独立，版本沿用源实例。「换加载器试模组」的标准动作。</summary>
    [RelayCommand]
    private async Task CloneInstanceAsync(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null)
        {
            StatusText = "先在列表里选择一个实例。";
            return;
        }

        if (_instances is null)
        {
            StatusText = "实例服务未就绪，无法克隆。";
            return;
        }

        if (IsInstanceBusy)
        {
            return;
        }

        IsInstanceBusy = true;
        StatusText = $"正在克隆「{target.Name}」…（模组越多越慢）";

        try
        {
            var clone = await _instances.CloneAsync(target.Info.Id, new InstanceCloneOptions());

            if (clone is null)
            {
                StatusText = $"克隆「{target.Name}」失败，详见日志。";
                return;
            }

            StatusText = $"已克隆为「{clone.Name}」：新版本目录 {clone.VersionName}，mods / saves / config 都是独立的一份。";

            await LoadInstancesAsync();
            SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.Info.Id, clone.Id, StringComparison.Ordinal))
                               ?? SelectedInstance;
        }
        catch (OperationCanceledException)
        {
            StatusText = "克隆已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"克隆失败：{ex.Message}";
        }
        finally
        {
            IsInstanceBusy = false;
        }
    }

    [RelayCommand]
    private void OpenRename(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null)
        {
            StatusText = "先在列表里选择一个实例。";
            return;
        }

        RenamingInstance = target;
        RenameDraft = target.Name;
        RenameError = null;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenameOpen = false;
        RenamingInstance = null;
        RenameError = null;
    }

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        var row = RenamingInstance;

        if (row is null || _instances is null)
        {
            return;
        }

        var name = RenameDraft.Trim();

        if (name.Length == 0)
        {
            // 服务端也会拒绝，但这里先给即时反馈，省一次磁盘往返。
            RenameError = "名称不能为空。";
            return;
        }

        IsRenameBusy = true;
        RenameError = null;

        try
        {
            await _instances.RenameAsync(row.Info.Id, name);

            IsRenameOpen = false;
            RenamingInstance = null;
            StatusText = $"实例「{row.Name}」已改名为「{name}」。";

            await LoadInstancesAsync();
            SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.Info.Id, row.Info.Id, StringComparison.Ordinal))
                               ?? SelectedInstance;
        }
        catch (ArgumentException ex)
        {
            RenameError = ex.Message;
        }
        catch (Exception ex)
        {
            RenameError = $"改名失败：{ex.Message}";
        }
        finally
        {
            IsRenameBusy = false;
        }
    }

    [RelayCommand]
    private void OpenIconPicker(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null)
        {
            StatusText = "先在列表里选择一个实例。";
            return;
        }

        IconEditingInstance = target;
        IsIconPickerOpen = true;
    }

    [RelayCommand]
    private void CancelIconPicker()
    {
        IsIconPickerOpen = false;
        IconEditingInstance = null;
    }

    [RelayCommand]
    private async Task ApplyIconAsync(IconChoice? choice)
    {
        var row = IconEditingInstance;

        if (row is null || choice is null || _instances is null || IsIconBusy)
        {
            return;
        }

        IsIconBusy = true;

        try
        {
            await _instances.SetIconAsync(row.Info.Id, choice.Kind, null);

            IsIconPickerOpen = false;
            IconEditingInstance = null;
            StatusText = $"「{row.Name}」的图标已改为「{choice.Title}」。";

            await LoadInstancesAsync();
            SelectedInstance = Instances.FirstOrDefault(i => string.Equals(i.Info.Id, row.Info.Id, StringComparison.Ordinal))
                               ?? SelectedInstance;
        }
        catch (Exception ex)
        {
            StatusText = $"设置图标失败：{ex.Message}";
        }
        finally
        {
            IsIconBusy = false;
        }
    }

    [RelayCommand]
    private void OpenCleanup(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null)
        {
            StatusText = "先在列表里选择一个实例。";
            return;
        }

        if (target.IsRunning)
        {
            StatusText = $"「{target.Name}」正在运行，先停止游戏（实例卡片上的「停止游戏」）再清理目录。";
            return;
        }

        CleanupInstance = target;
        CleanupLogs = true;
        CleanupCrashReports = true;
        CleanupDebugProfile = true;
        CleanupScreenshots = false;
        CleanupRange = CleanupRanges[0];
        CleanupStatus = null;
        IsCleanupOpen = true;
    }

    [RelayCommand]
    private void CancelCleanup()
    {
        IsCleanupOpen = false;
        CleanupInstance = null;
        CleanupStatus = null;
    }

    [RelayCommand]
    private async Task ConfirmCleanupAsync()
    {
        var row = CleanupInstance;

        if (row is null || _instances is null || IsCleanupBusy)
        {
            return;
        }

        var options = new InstanceCleanupOptions
        {
            IncludeLogs = CleanupLogs,
            IncludeCrashReports = CleanupCrashReports,
            IncludeDebugProfile = CleanupDebugProfile,
            IncludeScreenshots = CleanupScreenshots,
            OlderThanDays = CleanupRange?.Days ?? 0,
        };

        if (!options.IncludeLogs && !options.IncludeCrashReports && !options.IncludeDebugProfile && !options.IncludeScreenshots)
        {
            CleanupStatus = "至少要勾选一类文件。";
            return;
        }

        IsCleanupBusy = true;

        try
        {
            var report = await _instances.CleanupAsync(row.Info.Id, options);

            CleanupStatus = report.FilesDeleted == 0
                ? "没有符合条件、可以清理的文件。"
                : $"已删除 {report.FilesDeleted} 个文件，释放 {Conv.FormatSize(report.BytesFreed)}。"
                  + (report.Failed.Count > 0 ? $" {report.Failed.Count} 个文件删不掉（可能正被游戏写入）。" : string.Empty);

            StatusText = $"已清理「{row.Name}」：{CleanupStatus}";
        }
        catch (OperationCanceledException)
        {
            CleanupStatus = "清理已取消。";
        }
        catch (Exception ex)
        {
            CleanupStatus = $"清理失败：{ex.Message}";
        }
        finally
        {
            IsCleanupBusy = false;
        }
    }

    // ==================== 换加载器 ====================

    /// <summary>换加载器面板。与实例设置面板并排但互斥：两个都开会让页面变成表单堆。</summary>
    [ObservableProperty]
    private bool _isLoaderSwitchOpen;

    /// <summary>正在给哪个实例换。</summary>
    [ObservableProperty]
    private InstanceRow? _loaderSwitchInstance;

    /// <summary>「当前 1.20.1 · Fabric 0.15.0」这样的一行摘要。</summary>
    [ObservableProperty]
    private string _loaderSwitchCurrentText = string.Empty;

    /// <summary>目标加载器。默认停在当前加载器上——多数人打开面板是为了看，不是为了改。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchTargetVanilla))]
    [NotifyPropertyChangedFor(nameof(IsSwitchTargetFabric))]
    [NotifyPropertyChangedFor(nameof(IsSwitchTargetQuilt))]
    [NotifyPropertyChangedFor(nameof(IsSwitchTargetForge))]
    [NotifyPropertyChangedFor(nameof(IsSwitchTargetNeoForge))]
    [NotifyPropertyChangedFor(nameof(NeedsLoaderVersionPick))]
    [NotifyPropertyChangedFor(nameof(LoaderSwitchNeedsConfirm))]
    [NotifyPropertyChangedFor(nameof(LoaderSwitchConsequence))]
    private LoaderKind _switchTarget = LoaderKind.Vanilla;

    /// <summary>目标改动前的加载器（面板打开时快照），用于算出「会不会让 mods 失效」。</summary>
    private LoaderKind _switchSource = LoaderKind.Vanilla;

    public bool IsSwitchTargetVanilla => SwitchTarget == LoaderKind.Vanilla;

    public bool IsSwitchTargetFabric => SwitchTarget == LoaderKind.Fabric;

    public bool IsSwitchTargetQuilt => SwitchTarget == LoaderKind.Quilt;

    public bool IsSwitchTargetForge => SwitchTarget == LoaderKind.Forge;

    public bool IsSwitchTargetNeoForge => SwitchTarget == LoaderKind.NeoForge;

    /// <summary>原版不需要选加载器版本。</summary>
    public bool NeedsLoaderVersionPick => SwitchTarget != LoaderKind.Vanilla;

    /// <summary>可选加载器版本。</summary>
    public ObservableCollection<LoaderVersionOption> LoaderSwitchVersions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoaderSwitchVersion))]
    private LoaderVersionOption? _selectedLoaderSwitchVersion;

    public bool HasLoaderSwitchVersion => SelectedLoaderSwitchVersion is not null;

    [ObservableProperty]
    private bool _hasLoaderSwitchVersions;

    [ObservableProperty]
    private bool _isLoadingLoaderSwitchVersions;

    /// <summary>版本列表下方的说明（「该版本还没有 Fabric 支持」这类）。</summary>
    [ObservableProperty]
    private string _loaderSwitchVersionHint = string.Empty;

    /// <summary>
    /// 这次更换会不会改变现有 mods 的可用性。界面据此显示确认条并拦一次点击。
    /// 判定是纯函数（<see cref="InstanceLoaderAdvice.RequiresModConfirmation"/>），与 Core 一致。
    /// </summary>
    public bool LoaderSwitchNeedsConfirm => InstanceLoaderAdvice.RequiresModConfirmation(_switchSource, SwitchTarget);

    public string? LoaderSwitchConsequence => InstanceLoaderAdvice.DescribeConsequence(_switchSource, SwitchTarget);

    /// <summary>危险操作说明条是否要显示（只要需要确认就显示，不等用户点了才出现）。</summary>
    [ObservableProperty]
    private bool _isLoaderSwitchConfirmOpen;

    [ObservableProperty]
    private bool _isLoaderSwitchBusy;

    /// <summary>安装进度文案。换加载器要下载几十 MB，没有进度用户会以为卡死。</summary>
    [ObservableProperty]
    private string _loaderSwitchProgressText = string.Empty;

    [ObservableProperty]
    private string _loaderSwitchStatus = string.Empty;

    public bool HasLoaderSwitchStatus => !string.IsNullOrWhiteSpace(LoaderSwitchStatus);

    partial void OnLoaderSwitchStatusChanged(string value) => OnPropertyChanged(nameof(HasLoaderSwitchStatus));

    /// <summary>面板里「更换」按钮是否可点。</summary>
    public bool CanConfirmLoaderSwitch => !IsLoaderSwitchBusy
                                          && LoaderSwitchInstance is not null
                                          && (!NeedsLoaderVersionPick || SelectedLoaderSwitchVersion is not null);

    partial void OnIsLoaderSwitchBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirmLoaderSwitch));

    partial void OnLoaderSwitchInstanceChanged(InstanceRow? value) => OnPropertyChanged(nameof(CanConfirmLoaderSwitch));

    partial void OnSelectedLoaderSwitchVersionChanged(LoaderVersionOption? value) =>
        OnPropertyChanged(nameof(CanConfirmLoaderSwitch));

    /// <summary>打开面板。默认目标停在当前加载器：多数人先看再决定改不改。</summary>
    [RelayCommand]
    private async Task OpenLoaderSwitchAsync(InstanceRow? row)
    {
        var target = row ?? SelectedInstance;

        if (target is null)
        {
            StatusText = "先选择一个实例，再换加载器。";
            return;
        }

        // 换加载器会往 versions/ 里装新版本并改实例的绑定：游戏正跑着时换掉绑定，
        // 用户下次启动会拿到与本次完全不同的环境，属于最难复现的一类问题。
        if (target.IsRunning)
        {
            StatusText = $"「{target.Name}」正在运行，先停止游戏（实例卡片上的「停止游戏」）再换加载器。";
            return;
        }

        LoaderSwitchInstance = target;
        LoaderSwitchStatus = string.Empty;
        LoaderSwitchProgressText = string.Empty;
        IsLoaderSwitchConfirmOpen = false;

        _switchSource = target.Info.Loader;

        // 缓存字段可能是空的（从别的启动器迁入、或老版本写下的 instance.json）。
        // 这时不能直接判定「无法更换」——版本目录里就有答案，读一次磁盘。
        var mcVersion = target.Info.MinecraftVersion;

        if (string.IsNullOrWhiteSpace(mcVersion)
            && _versions is not null
            && !string.IsNullOrWhiteSpace(target.Info.VersionName))
        {
            try
            {
                var resolved = await _versions.GetVersionAsync(target.Info.VersionName).ConfigureAwait(true);
                mcVersion = resolved?.MinecraftVersion;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 读不到就按空处理，面板会给出「先修复版本目录」的说明。
                StatusText = $"读取版本信息失败：{ex.Message}";
            }
        }

        _loaderSwitchMcVersion = mcVersion;

        LoaderSwitchCurrentText = string.IsNullOrWhiteSpace(mcVersion)
            ? $"当前加载器：{InstanceLoaderAdvice.Label(_switchSource)} · 版本 {target.VersionText}"
            : $"当前 {mcVersion} · {InstanceLoaderAdvice.Label(_switchSource)}";

        SwitchTarget = _switchSource;
        IsLoaderSwitchOpen = true;

        await RefreshLoaderSwitchVersionsAsync();
    }

    /// <summary>面板打开时解析出的 Minecraft 版本号，供版本列表与安装请求共用。</summary>
    private string? _loaderSwitchMcVersion;

    [RelayCommand]
    private void SetSwitchTarget(LoaderKind kind)
    {
        if (SwitchTarget == kind)
        {
            return;
        }

        SwitchTarget = kind;
        IsLoaderSwitchConfirmOpen = false;
        _ = RefreshLoaderSwitchVersionsAsync();
    }

    [RelayCommand]
    private void CloseLoaderSwitch()
    {
        IsLoaderSwitchOpen = false;
        IsLoaderSwitchConfirmOpen = false;
        LoaderSwitchInstance = null;
        LoaderSwitchVersions.Clear();
        HasLoaderSwitchVersions = false;
    }

    /// <summary>
    /// 拉目标加载器在该 MC 版本下的可选版本。
    ///
    /// 复用 <see cref="IVersionService.GetLoaderVersionsAsync"/>——与下载页同一条链路，
    /// Forge 的「推荐版」标记也由它带来，不再另造一套。
    /// </summary>
    private async Task RefreshLoaderSwitchVersionsAsync()
    {
        var requestId = ++_loaderSwitchRequestId;

        LoaderSwitchVersions.Clear();
        SelectedLoaderSwitchVersion = null;
        HasLoaderSwitchVersions = false;

        if (SwitchTarget == LoaderKind.Vanilla)
        {
            // 提前返回也必须复位 loading：上一个请求（Forge/NeoForge）的 finally 因 id 不匹配
            // 不会复位它，否则转圈与下拉禁用会一直停在这里。
            IsLoadingLoaderSwitchVersions = false;
            LoaderSwitchVersionHint = "原版不需要加载器版本。";
            return;
        }

        var mcVersion = _loaderSwitchMcVersion;

        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            IsLoadingLoaderSwitchVersions = false;
            LoaderSwitchVersionHint = "无法确定该实例的 Minecraft 版本，换加载器前请先修复它的版本目录。";
            return;
        }

        if (_versions is null)
        {
            IsLoadingLoaderSwitchVersions = false;
            LoaderSwitchVersionHint = "版本服务未就绪，无法获取加载器版本列表。";
            return;
        }

        IsLoadingLoaderSwitchVersions = true;

        try
        {
            var list = await _versions.GetLoaderVersionsAsync(SwitchTarget, mcVersion).ConfigureAwait(true);

            if (requestId != _loaderSwitchRequestId)
            {
                return;
            }

            // 把本机已装的同加载器版本并进候选：Forge 上游只公开推荐版，
            // 不合并的话用户换到新版后就再也切不回自己装过的旧构建（等同 PCL2 的组件「还原」）。
            try
            {
                var installed = await _versions.GetInstalledAsync().ConfigureAwait(true);
                list = LoaderVersionCatalog.MergeInstalled(list, installed, SwitchTarget, mcVersion);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 读不到已安装列表只影响「回滚到旧构建」的候选，不阻断换加载器。
            }

            if (requestId != _loaderSwitchRequestId)
            {
                return;
            }

            foreach (var item in list)
            {
                LoaderSwitchVersions.Add(new LoaderVersionOption(item));
            }

            HasLoaderSwitchVersions = LoaderSwitchVersions.Count > 0;

            // 与下载页同一优先级：推荐版 → 稳定版 → 首项。
            SelectedLoaderSwitchVersion =
                LoaderSwitchVersions.FirstOrDefault(v => v.IsRecommended)
                ?? LoaderSwitchVersions.FirstOrDefault(v => v.Stable)
                ?? LoaderSwitchVersions.FirstOrDefault();

            LoaderSwitchVersionHint = HasLoaderSwitchVersions
                ? string.Empty
                : DescribeMissingLoaderVersions(SwitchTarget, mcVersion);
        }
        catch (OperationCanceledException)
        {
            // 切换目标导致的取消：忽略。
        }
        catch (Exception ex)
        {
            if (requestId == _loaderSwitchRequestId)
            {
                LoaderSwitchVersionHint = $"获取加载器版本失败：{ex.Message}";
            }
        }
        finally
        {
            if (requestId == _loaderSwitchRequestId)
            {
                IsLoadingLoaderSwitchVersions = false;
            }
        }
    }

    /// <summary>
    /// 列表为空时的说明。三种加载器各有各的「空」：
    /// Fabric/Quilt 空 = 该 MC 版本还没支持；Forge 空 = 没有官方推荐构建但仍能装最新；
    /// NeoForge 的版本由安装器决定，列表永远空。
    /// </summary>
    private static string DescribeMissingLoaderVersions(LoaderKind loader, string mcVersion) => loader switch
    {
        LoaderKind.NeoForge => $"NeoForge 的具体版本由安装器决定，这里直接装该 Minecraft 版本的最新版。",
        LoaderKind.Forge => $"没有查到 Minecraft {mcVersion} 的 Forge 构建列表，可以直接装最新版。",
        _ => $"{InstanceLoaderAdvice.Label(loader)} 还没有支持 Minecraft {mcVersion} 的版本。",
    };

    /// <summary>面板底部「更换」按钮：需要确认时先展开确认条，用户再点一次才真装。</summary>
    [RelayCommand]
    private void BeginSwitchLoader()
    {
        if (!CanConfirmLoaderSwitch)
        {
            return;
        }

        if (LoaderSwitchNeedsConfirm && !IsLoaderSwitchConfirmOpen)
        {
            IsLoaderSwitchConfirmOpen = true;
            return;
        }

        _ = ExecuteSwitchLoaderAsync();
    }

    [RelayCommand]
    private void CancelSwitchLoader() => IsLoaderSwitchConfirmOpen = false;

    private async Task ExecuteSwitchLoaderAsync()
    {
        var row = LoaderSwitchInstance;

        if (row is null || _instances is null || IsLoaderSwitchBusy)
        {
            return;
        }

        var kind = SwitchTarget;
        var loaderVersion = NeedsLoaderVersionPick ? SelectedLoaderSwitchVersion?.VersionText : null;

        IsLoaderSwitchBusy = true;
        IsLoaderSwitchConfirmOpen = false;
        LoaderSwitchStatus = string.Empty;
        LoaderSwitchProgressText = "准备安装…";

        try
        {
            var progress = new Progress<DownloadProgress>(
                p => LoaderSwitchProgressText = DescribeLoaderSwitchProgress(p));

            // 不传 targetMinecraftVersion：界面没有「换 MC 版本」的入口，恒定同版本，
// 传实例的缓存字段反而会误拒——用户手动换过版本目录时缓存已经过期，
// 而 Core 侧本来就会以磁盘为准重新解析并用它作为安装目标。
            var result = await _instances
                .SwitchLoaderAsync(row.Info.Id, kind, loaderVersion, progress)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                LoaderSwitchStatus = result.Error ?? "更换加载器失败。";
                return;
            }

            LoaderSwitchStatus = result.AlreadyMatches
                ? $"这个实例已经是 {InstanceLoaderAdvice.Label(kind)}，没有做任何改动。"
                : $"已换成 {InstanceLoaderAdvice.Label(kind)}"
                  + (string.IsNullOrWhiteSpace(result.LoaderVersion) ? string.Empty : $" {result.LoaderVersion}")
                  + $"，版本目录 {result.NewVersionName}。"
                  + (string.IsNullOrWhiteSpace(result.ConfirmationMessage)
                      ? string.Empty
                      : " " + result.ConfirmationMessage);

            StatusText = LoaderSwitchStatus;

            // 实例的 VersionName / Loader 都变了：卡片与分组必须重读，否则界面显示的还是旧加载器。
            await LoadInstancesAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            LoaderSwitchStatus = "更换加载器已取消。";
        }
        catch (Exception ex)
        {
            LoaderSwitchStatus = $"更换加载器失败：{ex.Message}";
        }
        finally
        {
            IsLoaderSwitchBusy = false;
            LoaderSwitchProgressText = string.Empty;
        }
    }

    private static string DescribeLoaderSwitchProgress(DownloadProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.CurrentFile))
        {
            return $"正在下载 {progress.CurrentFile}（{progress.CompletedFiles}/{progress.TotalFiles}）";
        }

        return progress.TotalFiles > 0
            ? $"正在安装 {progress.CompletedFiles}/{progress.TotalFiles}"
            : "正在安装…";
    }

    private int _loaderSwitchRequestId;

    // ==================== 回收站 ====================

    /// <summary>回收站里的实例。与实例列表分开显示：它们还不能直接启动。</summary>
    public ObservableCollection<TrashRow> TrashEntries { get; } = new();

    [ObservableProperty]
    private bool _hasTrashEntries;

    [ObservableProperty]
    private bool _isTrashOpen;

    [ObservableProperty]
    private bool _isTrashBusy;

    [ObservableProperty]
    private string _trashSummary = string.Empty;

    [ObservableProperty]
    private bool _isPurgeConfirmOpen;

    [ObservableProperty]
    private TrashRow? _pendingPurge;

    /// <summary>用户手动关过面板后，本次停留期间不再自动展开。</summary>
    private bool _trashManuallyClosed;

    public string TrashPanelTitle => TrashEntries.Count == 0 ? "回收站" : $"回收站 · {TrashEntries.Count}";

    /// <summary>实例区标题行上的回收站开关文案。带计数，让用户知道里面有东西。</summary>
    public string TrashButtonText => TrashEntries.Count == 0 ? "回收站" : $"回收站 · {TrashEntries.Count}";

    partial void OnHasTrashEntriesChanged(bool value) => OnPropertyChanged(nameof(TrashPanelTitle));

    /// <summary>重新读回收站目录。条目来自 <c>&lt;data&gt;/trash/instances/</c>。</summary>
    private async Task RefreshTrashAsync()
    {
        if (_instances is null)
        {
            TrashEntries.Clear();
            HasTrashEntries = false;
            TrashSummary = string.Empty;
            IsTrashOpen = false;
            return;
        }

        try
        {
            var entries = await _instances.ListTrashAsync();

            TrashEntries.Clear();
            foreach (var entry in entries)
            {
                TrashEntries.Add(new TrashRow(entry));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"读取回收站失败：{ex.Message}";
        }

        HasTrashEntries = TrashEntries.Count > 0;
        TrashSummary = HasTrashEntries
            ? $"{TrashEntries.Count} 个实例可恢复，彻底删除后不可恢复"
            : "回收站是空的";

        // 有条目时默认展开：删完实例后面板就在这里，不需要用户再找入口。
        if (!HasTrashEntries)
        {
            IsTrashOpen = false;
        }
        else if (!_trashManuallyClosed)
        {
            IsTrashOpen = true;
        }

        OnPropertyChanged(nameof(TrashPanelTitle));
        OnPropertyChanged(nameof(TrashButtonText));
    }

    [RelayCommand]
    private void ToggleTrash()
    {
        IsTrashOpen = !IsTrashOpen;
        _trashManuallyClosed = !IsTrashOpen;
    }

    [RelayCommand]
    private async Task RestoreTrashAsync(TrashRow? row)
    {
        if (row is null || _instances is null || IsTrashBusy)
        {
            return;
        }

        IsTrashBusy = true;

        try
        {
            var restored = await _instances.RestoreFromTrashAsync(row.Entry.Id);

            StatusText = restored
                ? $"已恢复实例「{row.Name}」，它回到了列表里。"
                : $"恢复「{row.Name}」失败：原实例目录已被占用，或回收站条目已损坏。";

            // LoadInstancesAsync 内部会刷新回收站，无需单独再刷一次。
            await LoadInstancesAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "恢复已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"恢复失败：{ex.Message}";
        }
        finally
        {
            IsTrashBusy = false;
        }
    }

    [RelayCommand]
    private void RequestPurge(TrashRow? row)
    {
        if (row is null)
        {
            return;
        }

        PendingPurge = row;
        IsPurgeConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelPurge()
    {
        IsPurgeConfirmOpen = false;
        PendingPurge = null;
    }

    [RelayCommand]
    private async Task ConfirmPurgeAsync()
    {
        var row = PendingPurge;
        IsPurgeConfirmOpen = false;
        PendingPurge = null;

        if (row is null || _instances is null)
        {
            return;
        }

        IsTrashBusy = true;

        try
        {
            await _instances.PurgeTrashAsync(row.Entry.Id);
            StatusText = $"已彻底删除「{row.Name}」，其目录已从磁盘移除。";

            await RefreshTrashAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"彻底删除失败：{ex.Message}";
        }
        finally
        {
            IsTrashBusy = false;
        }
    }

    /// <summary>
    /// 记住本次启动的版本名（<see cref="AppSettings.LastSelectedVersion"/> 是既有字段，不新增持久化状态）。
    /// 下次进版本页会用同一个版本默认选中，实例选择也随之稳定。
    /// </summary>
    private async Task RememberSelectedVersionAsync(string versionName)
    {
        if (_settings is null || string.IsNullOrWhiteSpace(versionName))
        {
            return;
        }

        if (string.Equals(_settings.Current.LastSelectedVersion, versionName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _settings.Current.LastSelectedVersion = versionName;
            await _settings.SaveAsync();
        }
        catch (Exception)
        {
            // 记住选择失败不影响启动；设置服务已把错误写进日志。
        }
    }

    /// <summary>把本次运行时长写回实例描述文件，供实例列表显示「最后启动」。</summary>
    private async Task RecordInstancePlayAsync(string instanceId, TimeSpan duration)
    {
        if (_instances is null)
        {
            return;
        }

        try
        {
            var instance = _instances.Instances
                .FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));

            if (instance is null)
            {
                return;
            }

            instance.LastPlayedAt = DateTimeOffset.UtcNow;
            instance.TotalPlayTimeSeconds += (long)Math.Max(0, duration.TotalSeconds);
            await _instances.SaveAsync(instance);
        }
        catch (Exception)
        {
            // 回写游玩统计失败不影响启动结果，错误已由 InstanceService 写进日志。
        }
    }

    /// <summary>
    /// 解析本次启动的游戏目录。
    /// 有匹配实例时用实例目录（mods / saves / config 隔离），否则退回全局 .minecraft。
    /// <paramref name="instanceId"/> 回传命中的实例 id，供启动结束后回写游玩时长。
    /// </summary>
    private string? ResolveGameDirectory(string? versionName, out string? instanceId)
    {
        instanceId = null;

        if (_instances is not null)
        {
            try
            {
                var instance = InstanceLocator.FindForVersion(
                    _instances.Instances,
                    versionName,
                    SelectedInstance?.Info.Id);

                if (instance is not null)
                {
                    var directory = _instances.GetGameDirectory(instance.Id);
                    instanceId = instance.Id;
                    return directory;
                }
            }
            catch (Exception)
            {
                // 实例信息损坏（id 非法、目录不可读）时退回默认目录，不让启动整体失败。
            }
        }

        return _paths?.MinecraftDirectory;
    }

    /// <summary>
    /// 解析启动目标：优先走 <see cref="IInstanceContext"/>（含隔离策略与自动建实例），
    /// 服务缺失时退回旧的「只查已有实例」逻辑，保证页面在未接线时仍可启动。
    /// </summary>
    private async Task<InstanceResolution> ResolveTargetAsync(string versionName)
    {
        if (_instanceContext is not null)
        {
            try
            {
                return await _instanceContext.ResolveLaunchTargetAsync(versionName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StatusText = $"按实例解析目录失败，改用全局目录：{ex.Message}";
            }
        }

        var directory = ResolveGameDirectory(versionName, out var fallbackInstanceId);

        return new InstanceResolution(
            directory ?? _paths?.MinecraftDirectory ?? string.Empty,
            Instance: null,
            Isolated: fallbackInstanceId is not null,
            Created: false);
    }

    private string? DescribeMissingServices()
    {
        var missing = new List<string>();
        if (_versions is null)
        {
            missing.Add("版本");
        }

        if (_install is null)
        {
            missing.Add("安装");
        }

        if (_launcher is null)
        {
            missing.Add("启动");
        }

        if (_accounts is null)
        {
            missing.Add("账户");
        }

        return missing.Count == 0
            ? null
            : $"后端服务未注册：{string.Join("、", missing)}。版本列表将为空（等待组合根合并）。";
    }
}

/// <summary>版本列表的一行。展示字段在构造时算好，XAML 只做纯绑定。</summary>
public sealed partial class VersionRow : ObservableObject
{
    public VersionRow(VersionInfo info)
    {
        Info = info;
        Name = info.Name;
        ChannelText = info.Channel switch
        {
            VersionChannel.Snapshot => "快照",
            VersionChannel.OldBeta => "Beta",
            VersionChannel.OldAlpha => "Alpha",
            VersionChannel.Special => "愚人节",
            _ => "正式",
        };
        IsRelease = info.Channel == VersionChannel.Release;
        IsSnapshot = info.Channel == VersionChannel.Snapshot;
        LoaderText = info.Loader switch
        {
            LoaderKind.Fabric => string.IsNullOrEmpty(info.LoaderVersion) ? "Fabric" : $"Fabric {info.LoaderVersion}",
            LoaderKind.Forge => string.IsNullOrEmpty(info.LoaderVersion) ? "Forge" : $"Forge {info.LoaderVersion}",
            LoaderKind.NeoForge => string.IsNullOrEmpty(info.LoaderVersion) ? "NeoForge" : $"NeoForge {info.LoaderVersion}",
            LoaderKind.Quilt => string.IsNullOrEmpty(info.LoaderVersion) ? "Quilt" : $"Quilt {info.LoaderVersion}",
            _ => "原版",
        };
        IsModded = info.Loader != LoaderKind.Vanilla;
        LastPlayedText = info.LastPlayedAt is null
            ? "从未启动"
            : $"最后游玩 {RelativeTimeText.DescribeAgo(info.LastPlayedAt.Value)}";
        // SizeOnDisk 为 -1 表示没有统计（版本服务按需计算），显示破折号会被读成「空值」。
        SizeText = info.SizeOnDisk < 0 ? "大小未知" : Conv.FormatSize(info.SizeOnDisk);
        MinecraftHint = string.IsNullOrWhiteSpace(info.MinecraftVersion) || info.Loader == LoaderKind.Vanilla
            ? string.Empty
            : $"基于 {info.MinecraftVersion}";
        HasMinecraftHint = MinecraftHint.Length > 0;
        IsBroken = !string.IsNullOrWhiteSpace(info.BrokenReason);
        BrokenReason = info.BrokenReason;
    }

    public VersionInfo Info { get; }

    public string Name { get; }

    public string ChannelText { get; }

    public bool IsRelease { get; }

    public bool IsSnapshot { get; }

    public string LoaderText { get; }

    public bool IsModded { get; }

    public string LastPlayedText { get; }

    public string SizeText { get; }

    public string MinecraftHint { get; }

    public bool HasMinecraftHint { get; }

    public bool IsBroken { get; }

    public string? BrokenReason { get; }

    /// <summary>修复 / 校验进行中，用于禁用该行按钮。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    private bool _isBusy;

    /// <summary>
    /// 该版本对应的游戏目录里有活着的游戏进程。
    ///
    /// 删除 / 修复 / 组件检查动的是 <c>versions/&lt;名&gt;/</c> 下的文件：游戏正读着那些 jar 时
    /// 替换它们，用户会得到一次「昨天还能玩、今天起不来」的崩溃，而且现场很难解释。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    private bool _isRunning;

    /// <summary>
    /// 「修复」下拉的可用条件：本行没有正在跑的修复任务，且游戏没在运行。
    /// 合成一个属性而不是在 XAML 里叠两个绑定：编译绑定只校验单个路径，叠起来挡不住写错。
    /// </summary>
    public bool CanRepair => !IsBusy && !IsRunning;

    [ObservableProperty]
    private string? _busyText;

    /// <summary>是否收藏。收藏行会置顶，星标图标与它对应（实心 / 描边）。</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>是否被用户隐藏。默认不出现在列表里，打开「显示已隐藏」才可见。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideActionText))]
    private bool _isHidden;

    /// <summary>行内「隐藏 / 取消隐藏」按钮的文案。</summary>
    public string HideActionText => IsHidden ? "取消隐藏" : "隐藏";
}

/// <summary>实例列表的一行。展示字段在构造时算好，XAML 只做纯绑定。</summary>
public sealed partial class InstanceRow : ObservableObject
{
    public InstanceRow(InstanceInfo info)
    {
        Info = info;
        Name = string.IsNullOrWhiteSpace(info.Name) ? "未命名实例" : info.Name;
        VersionText = string.IsNullOrWhiteSpace(info.VersionName) ? "未绑定版本" : info.VersionName;
        LastPlayedText = info.LastPlayedAt is null
            ? "从未启动"
            : $"最后启动 {RelativeTimeText.DescribeAgo(info.LastPlayedAt.Value)}";
        NoteText = info.Notes?.Trim() ?? string.Empty;
        HasNote = NoteText.Length > 0;
        LoaderLabel = InstanceLoaderAdvice.Label(info.Loader);
    }

    public InstanceInfo Info { get; }

    public string Name { get; }

    /// <summary>实例绑定的版本目录名，同时是「启动时是否复用该实例」的匹配键。</summary>
    public string VersionText { get; }

    /// <summary>
    /// 实例当前加载器的展示名。取自 <see cref="InstanceInfo.Loader"/>——它可能与
    /// <see cref="VersionText"/> 指向的版本目录实际不符（用户手动换过文件），
    /// 但换加载器面板会以磁盘为准重新解析，这里的文案只用于「一眼看出现在是什么」。
    /// </summary>
    public string LoaderLabel { get; }

    public string LastPlayedText { get; }

    /// <summary>
    /// 实例备注（已 trim）。卡片上单行省略显示，完整文本由「查看备注」浮层承载——
    /// 备注可能很长，塞进卡片会把操作按钮挤走。
    /// </summary>
    public string NoteText { get; }

    public bool HasNote { get; }

    /// <summary>
    /// 图标徽标的线条路径。首次访问时才解析（懒求值）：<see cref="InstanceIcons"/> 的静态构造
    /// 会调 <c>Geometry.Parse</c>，那要求 Avalonia 平台已初始化，在无图形栈的单测里构造
    /// <see cref="InstanceRow"/> 会直接抛 <c>TypeInitializationException</c>。
    /// 真实界面第一次绑定就在 UI 线程上，行为不变。
    /// </summary>
    public Geometry IconGeometry => _iconGeometry ??= InstanceIcons.For(Info.IconKind);

    private Geometry? _iconGeometry;

    /// <summary>
    /// 目录快捷芯片（存档 / 模组 / 资源包 / 截图 / 日志）。
    /// 由 <see cref="VersionsViewModel"/> 在重建实例列表时按磁盘事实填充，
    /// 所以「目录还不存在」的弱化态与列表同寿。
    /// </summary>
    public ObservableCollection<InstanceFolderChip> Folders { get; } = new();

    /// <summary>
    /// 该实例的游戏目录里有活着的游戏进程（锁文件判定为活）。
    /// 卡片据此显示「运行中」徽标，并禁用删除 / 清理 / 换加载器。
    /// </summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// 本会话在该实例目录里有没有活着的进程。停止按钮的可见性与可用性都看这个。
    ///
    /// 与 <see cref="IsRunning"/> 是两条独立判定：后者来自磁盘运行锁（可能是别的启动器实例
    /// 持的，本会话停不掉），前者来自启动器内存里的会话表。多开时列表里可以同时出现
    /// 两行 <see cref="IsSessionRunning"/> 为 true 的卡片。
    /// </summary>
    [ObservableProperty]
    private bool _isSessionRunning;

    /// <summary>
    /// 该行当前会话的标识，停止时按它精确匹配。可能为 null（不是本会话启动的）。
    /// 会随 <see cref="IsSessionRunning"/> 一起由 <c>RefreshInstanceSessionStates</c> 刷新。
    /// </summary>
    public string? RunningSessionId { get; set; }

    /// <summary>
    /// 破坏性入口（移入回收站 / 清理 / 换加载器）的可用性：运行中或实例区忙碌都为 false。
    /// 由 <see cref="VersionsViewModel"/> 统一刷新——单一布尔比在 XAML 里叠两个绑定更难绑错。
    /// </summary>
    [ObservableProperty]
    private bool _actionsEnabled = true;
}

/// <summary>
/// 实例卡上的一个目录快捷芯片（存档 / 模组 / 资源包 / 截图 / 日志）。
///
/// 带 <see cref="InstanceId"/> 而不是整个 <see cref="InstanceRow"/>：点芯片时要用
/// 「该实例此刻的真实游戏目录」重新解析一次路径，而不是复用一个可能已经过期的字符串。
/// </summary>
public sealed partial class InstanceFolderChip : ObservableObject
{
    public InstanceFolderChip(string instanceId, InstanceFolder folder)
    {
        InstanceId = instanceId;
        Folder = folder;
        _exists = folder.Exists;
    }

    public string InstanceId { get; }

    public InstanceFolder Folder { get; }

    /// <summary>命令参数：稳定标识（<c>saves</c> / <c>mods</c> / …）。</summary>
    public string Key => Folder.Key;

    public string DisplayName => Folder.DisplayName;

    /// <summary>
    /// 目录当前是否存在。点开一次后置 <c>true</c>：弱化态立刻消失，
    /// 不必等整页刷新（刷新会重建实例列表，卡片会跳一下）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _exists;

    /// <summary>芯片的悬停说明。缺目录时说清「点了会先创建」，免得看起来像失效入口。</summary>
    public string Tip => Exists
        ? $"打开 {Folder.RelativePath} 目录"
        : $"{Folder.RelativePath} 目录还不存在，点击会先创建再打开";
}

/// <summary>组件面板的一行。展示字段在构造时算好，XAML 只做纯绑定。</summary>
public sealed class ComponentRow
{
    public ComponentRow(ComponentStatus status)
    {
        Name = string.IsNullOrWhiteSpace(status.Name) ? "未命名组件" : status.Name;
        Kind = status.Kind;
        VersionText = string.IsNullOrWhiteSpace(status.Version) ? "版本未知" : status.Version!;
        FileCountText = $"{status.FileCount} 个文件";
        MissingText = $"{status.MissingCount} 缺失";
        ModifiedText = $"{status.ModifiedCount} 改动";
        HasMissing = status.MissingCount > 0;
        HasModified = status.ModifiedCount > 0;
        IsHealthy = status.IsHealthy;

        // 「记录于」用的是清单里该组件名下最新的 mtime：它同时说明了「这份记录有多旧」，
        // 比单纯显示组件版本号更有用（版本号已经在第二行）。
        DetailText = status.LastModifiedUtc is null
            ? $"{VersionText} · {FileCountText}"
            : $"{VersionText} · {FileCountText} · 记录于 {RelativeTimeText.DescribeAgo(status.LastModifiedUtc.Value)}";
    }

    public string Name { get; }

    /// <summary>组件种类的中文标签（原版 / 加载器 / 库 / 资源 / 模组 / 其他）。</summary>
    public string Kind { get; }

    public string VersionText { get; }

    public string FileCountText { get; }

    public string MissingText { get; }

    public string ModifiedText { get; }

    public bool HasMissing { get; }

    public bool HasModified { get; }

    public bool IsHealthy { get; }

    public string DetailText { get; }
}

/// <summary>回收站列表的一行。</summary>
public sealed class TrashRow(InstanceTrashEntry entry)
{
    public InstanceTrashEntry Entry { get; } = entry;

    public string Name => string.IsNullOrWhiteSpace(Entry.DisplayName) ? "未命名实例" : Entry.DisplayName;

    public string SizeText { get; } = Conv.FormatSize(entry.SizeBytes);

    public string DeletedText { get; } = $"{RelativeTimeText.DescribeAgo(entry.DeletedAt)}移入";
}

/// <summary>图标选择器的一项。</summary>
public sealed class IconChoice(InstanceIconKind kind, string title, Geometry geometry)
{
    public InstanceIconKind Kind { get; } = kind;

    public string Title { get; } = title;

    public Geometry Geometry { get; } = geometry;
}

/// <summary>清理范围选项。<see cref="Days"/> 为 0 表示全部。</summary>
public sealed record CleanupRangeOption(int Days, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// 换加载器面板里的「加载器版本」下拉项。
///
/// 包一层而不是直接绑 <see cref="LoaderVersion"/>：下拉里要显示「推荐」后缀，
/// 而 <see cref="LoaderVersion"/> 是纯数据模型，不该带展示文案。
/// </summary>
public sealed class LoaderVersionOption(LoaderVersion version)
{
    public LoaderVersion Model { get; } = version;

    public string VersionText => Model.Version;

    public bool Stable => Model.Stable;

    public bool IsRecommended => Model.IsRecommended;

    /// <summary>下拉与列表里的完整文案：推荐版必须能一眼看出来（Forge 的 latest 经常是坏的）。</summary>
    public string DisplayText
    {
        get
        {
            var text = Model.IsRecommended ? $"{Model.Version}（推荐）" : Model.Version;

            // 「已安装」是回滚入口的判据：用户要能区分「装过的旧构建」与「未装的候选」。
            return Model.IsInstalled ? $"{text}（已安装）" : text;
        }
    }

    public override string ToString() => DisplayText;
}

/// <summary>
/// 实例图标的线条路径表。<see cref="InstanceIconKind"/> 的 12 个内置图标各一条 24×24 路径。
///
/// 自绘而不是引图片：路径在任意 DPI 下都清晰，且不需要往发布包里塞 12 张 PNG；
/// 视觉上沿用仓库的线条徽标风格（1.4px 描边 + 圆角端点），不照搬 PCL 的像素图标。
/// </summary>
public static class InstanceIcons
{
    private static readonly Dictionary<InstanceIconKind, Geometry> Paths = new()
    {
        [InstanceIconKind.Default] = Geometry.Parse(
            "M12 2.5 21 7v10l-9 4.5L3 17V7zM12 12l9-5M12 12v9.5M12 12 3 7"),
        [InstanceIconKind.Grass] = Geometry.Parse(
            "M12 21V9M12 9c0-3 2.4-5.2 5.6-6M12 9c0-3-2.4-5.2-5.6-6M12 14c0-2.4 1.8-4.2 4.4-5M12 14c0-2.4-1.8-4.2-4.4-5"),
        [InstanceIconKind.Chest] = Geometry.Parse(
            "M3.5 9h17v11h-17zM3.5 9 6 4.5h12L20.5 9M3.5 12.5h17M10.5 12.5v3h3v-3z"),
        [InstanceIconKind.Command] = Geometry.Parse(
            "M9.5 9.5h5v5h-5zM5 5h2.2v2.2H5zM16.8 5H19v2.2h-2.2zM5 16.8h2.2V19H5zM16.8 16.8H19V19h-2.2zM11 3v3.6M13 3v3.6M11 17.4V21M13 17.4V21M3 11h3.6M3 13h3.6M17.4 11H21M17.4 13H21"),
        [InstanceIconKind.CraftTable] = Geometry.Parse(
            "M4 4h16v16H4zM4 9.3h16M4 14.6h16M9.3 4v16M14.6 4v16"),
        [InstanceIconKind.Furnace] = Geometry.Parse(
            "M4 4h16v16H4zM4 9.5h16M8.5 20v-4.2h7V20M10.5 15.8c0-1.4 1.5-2.4 1.5-3.8 0 1.4 1.5 2.4 1.5 3.8"),
        [InstanceIconKind.Chicken] = Geometry.Parse(
            "M16.2 7.4a4.2 4.2 0 1 1-8.4 0 4.2 4.2 0 0 1 8.4 0zM16.6 6.6 20 8l-3.4 1.8M12 11.6V21M9.4 21h5.2M8 7.6h1.2"),
        [InstanceIconKind.OptiFine] = Geometry.Parse(
            "M12 5.6c4.2 0 7.4 2.9 7.4 6.4S16.2 18.4 12 18.4 4.6 15.5 4.6 12 7.8 5.6 12 5.6zM12 9.4a2.6 2.6 0 1 0 0 5.2 2.6 2.6 0 0 0 0-5.2z"),
        [InstanceIconKind.Fabric] = Geometry.Parse(
            "M4 8.4c4.4-3.1 11.6-3.1 16 0M4 15.6c4.4 3.1 11.6 3.1 16 0M8 4.4v15.2M16 4.4v15.2"),
        [InstanceIconKind.Quilt] = Geometry.Parse(
            "M12 3 21 12l-9 9-9-9zM12 8l4 4-4 4-4-4z"),
        [InstanceIconKind.Forge] = Geometry.Parse(
            "M4 9h16M6 9v3.4h12V9M8.4 12.4 6.2 20h11.6l-2.2-7.6M7.4 5.4h9.2"),
        [InstanceIconKind.NeoForge] = Geometry.Parse(
            "M4 10.4h16M6 10.4v3.2h12v-3.2M8.4 13.6 6.4 20h11.2l-2-6.4M12 3.4l1.4 3h-2.8z"),
    };

    /// <summary>按 <see cref="InstanceIconKind"/> 的声明顺序排列，供选择器直接绑定。</summary>
    public static IReadOnlyList<IconChoice> All { get; } =
    [
        new(InstanceIconKind.Default, "默认", Paths[InstanceIconKind.Default]),
        new(InstanceIconKind.Grass, "草方块", Paths[InstanceIconKind.Grass]),
        new(InstanceIconKind.Chest, "箱子", Paths[InstanceIconKind.Chest]),
        new(InstanceIconKind.Command, "命令方块", Paths[InstanceIconKind.Command]),
        new(InstanceIconKind.CraftTable, "工作台", Paths[InstanceIconKind.CraftTable]),
        new(InstanceIconKind.Furnace, "熔炉", Paths[InstanceIconKind.Furnace]),
        new(InstanceIconKind.Chicken, "鸡", Paths[InstanceIconKind.Chicken]),
        new(InstanceIconKind.OptiFine, "OptiFine", Paths[InstanceIconKind.OptiFine]),
        new(InstanceIconKind.Fabric, "Fabric", Paths[InstanceIconKind.Fabric]),
        new(InstanceIconKind.Quilt, "Quilt", Paths[InstanceIconKind.Quilt]),
        new(InstanceIconKind.Forge, "Forge", Paths[InstanceIconKind.Forge]),
        new(InstanceIconKind.NeoForge, "NeoForge", Paths[InstanceIconKind.NeoForge]),
    ];

    /// <summary>取图标路径；未知值（含 <see cref="InstanceIconKind.Custom"/>）回落到默认立方体。</summary>
    public static Geometry For(InstanceIconKind kind) =>
        Paths.TryGetValue(kind, out var geometry) ? geometry : Paths[InstanceIconKind.Default];
}

/// <summary>实例设置面板里的 GC 选项。带中文说明，避免用户对着 <c>-XX:+UseG1GC</c> 猜该选哪个。</summary>
public sealed class GcOption(GcKind kind, string title, string description)
{
    public GcKind Kind { get; } = kind;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public override string ToString() => Title;
}

/// <summary>版本与实例共用的「多久以前」文案。放在这里是因为两个列表都要显示同一种相对时间。</summary>
internal static class RelativeTimeText{
    public static string DescribeAgo(DateTimeOffset when)
    {
        var delta = DateTimeOffset.Now - when;
        if (delta < TimeSpan.Zero || delta < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta < TimeSpan.FromDays(30))
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
