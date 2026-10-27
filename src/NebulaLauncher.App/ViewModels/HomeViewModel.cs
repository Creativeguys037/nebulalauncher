using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;
using Conv = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 首页仪表盘：当前账户 / 当前版本 / 最近使用 / 统计磁贴 / 启动进度与崩溃建议。
///
/// 构造参数全部可空且带默认值：组合根（App.axaml.cs 的 BuildServices）与 Core 模块并行开发，
/// 服务尚未注册时本页仍能构造并显示「服务未就绪」，而不是在构造期抛异常把整个窗口带崩。
/// DI 就绪后 <c>ActivatorUtilities</c> 会注入真实实例，本文件无需改动。
/// </summary>
public partial class HomeViewModel : PageViewModelBase
{
    /// <summary>崩溃分析只取最后 400 行，避免长时间运行时日志无限增长。</summary>
    private const int LaunchLogLimit = 400;

    private readonly IAccountService? _accounts;
    private readonly IVersionService? _versions;
    private readonly ISettingsService? _settings;
    private readonly IInstanceService? _instances;
    private readonly IInstanceContext? _instanceContext;
    private readonly IGameLauncher? _launcher;
    private readonly IContentService? _content;
    private readonly IBackupService? _backups;
    private readonly ILauncherPaths? _paths;
    private readonly IPlatformService? _platform;
    private readonly INavigationService? _navigation;
    private readonly IWindowActions? _windowActions;
    private readonly ICrashExportService? _crashExport;
    private readonly IInstallService? _install;
    private readonly IJavaService? _java;
    private readonly SkinAvatarLoader? _skinLoader;

    private readonly List<string> _launchLog = new(LaunchLogLimit);
    private readonly object _launchLogGate = new();

    /// <summary>
    /// 最近一次崩溃归因的完整结论。导出崩溃报告需要它——
    /// <see cref="CrashText"/> 只有摘要，建议列表与报告路径都在结论对象里。
    /// </summary>
    private CrashReport? _lastCrashReport;

    /// <summary>
    /// 最近一次崩溃读到的 hs_err 文本（每份以 <c>===== 文件名 =====</c> 开头）。
    /// 来自 <see cref="LaunchResult.HsErrLogs"/>；导出时拼进 zip 的 <c>hs_err.txt</c>。
    /// </summary>
    private IReadOnlyList<string> _lastHsErrLogs = Array.Empty<string>();

    private CancellationTokenSource? _launchCts;

    /// <summary>
    /// 启动代次：每次 <see cref="LaunchAsync"/> 自增。进度回调带着它创建时代的值，
    /// 旧会话（Detach 后仍有进度/退出事件）不能改新一次启动的面板。
    /// </summary>
    private int _launchGeneration;

    /// <summary>
    /// 最近一次「取消等待并移交后台」的会话标识。后台会话退出事件只处理与它匹配的那一条；
    /// 用户已发起新启动时这里会被清空，旧结果直接丢弃（P3-3 与 P3-14 共用这个闸门）。
    /// </summary>
    private string? _detachedSessionId;

    private bool _instancesLoaded;
    private string _subtitle = string.Empty;
    private bool _isLoading;

    /// <summary>是否已订阅 <see cref="IAccountService.AccountsChanged"/>；订阅只挂在页面激活期间。</summary>
    private bool _accountEventsAttached;

    /// <summary>「启动后对窗口做什么」每次启动只能执行一次，否则用户重新打开窗口会被立刻再关掉。</summary>
    private bool _visibilityHandled;

    public HomeViewModel(
        IAccountService? accountService = null,
        IVersionService? versionService = null,
        ISettingsService? settingsService = null,
        IInstanceService? instanceService = null,
        IGameLauncher? gameLauncher = null,
        IContentService? contentService = null,
        IBackupService? backupService = null,
        ILauncherPaths? launcherPaths = null,
        IPlatformService? platformService = null,
        INavigationService? navigationService = null,
        IInstanceContext? instanceContext = null,
        IWindowActions? windowActions = null,
        ICrashExportService? crashExportService = null,
        IInstallService? installService = null,
        IJavaService? javaService = null,
        SkinAvatarLoader? skinAvatarLoader = null,
        IServiceProvider? services = null)
    {
        _accounts = accountService;
        _versions = versionService;
        _settings = settingsService;
        _instances = instanceService;
        _launcher = gameLauncher;
        _content = contentService;
        _backups = backupService;
        _paths = launcherPaths;
        _platform = platformService;
        _navigation = navigationService;
        _instanceContext = instanceContext;
        _windowActions = windowActions;
        _crashExport = crashExportService;
        _install = installService;
        _java = javaService;
        // 头像位图缓存不进容器注册表（App.axaml.cs 是并行协作热点）：容器没给就自己拿一个。
        _skinLoader = skinAvatarLoader ?? (services is null ? null : new SkinAvatarLoader(services));

        // Detach 之后 LaunchAsync 已经返回，后台会话的崩溃结论只能靠事件回首页。
        if (_launcher is not null)
        {
            _launcher.SessionExited += OnDetachedSessionExited;
        }

        // 首页与版本页各持一个实例：两者的「缺 Java」是两次独立的启动失败上下文，
        // 共享一份状态会让一边的失败清掉另一边正在下载的进度。
        JavaInstall = new JavaInstallState(javaService, JavaInstallWording.Home);
        JavaInstall.PropertyChanged += OnJavaInstallChanged;

        ServiceWarning = DescribeMissingServices();

        // 截图模式下 <c>--page home</c> 是当前页，「导航到同一页」不会触发 OnNavigatedToAsync，
        // 所以这里也要跑一次；方法自身幂等。
        ApplyScreenshotCrashState();
        ApplyScreenshotLaunchState();
    }

    public override string Title => "首页";

    public override string? Subtitle => _subtitle;

    public override string Key => NavigationKeys.Home;

    // ---------------- 游戏更新提示（PCL2 ToolUpdateRelease） ----------------

    /// <summary>是否显示「有新版本」提示条。</summary>
    [ObservableProperty]
    private bool _hasGameUpdate;

    /// <summary>提示文案。为空表示没有可提示的更新。</summary>
    [ObservableProperty]
    private string? _gameUpdateNotice;

    /// <summary>待提示的版本号，用于「知道了」时写回 settings。</summary>
    private string? _gameUpdateVersion;

    /// <summary>最近使用的版本（最多 5 条），点击选中、双击启动。</summary>
    public ObservableCollection<HomeVersionItem> RecentVersions { get; } = new();

    /// <summary>崩溃成因建议，最多 3 条。包装成 <see cref="CrashSuggestionItem"/> 是为了带上动作按钮。</summary>
    public ObservableCollection<CrashSuggestionItem> Suggestions { get; } = new();

    // ---------------- 账户 ----------------
    [ObservableProperty]
    private string _accountName = "未登录";

    [ObservableProperty]
    private string _accountInitial = "?";

    /// <summary>当前账户的皮肤纹理；为空时首页头像位置显示占位色块。</summary>
    [ObservableProperty]
    private Bitmap? _accountAvatar;

    [ObservableProperty]
    private string _accountBadge = "无账户";

    [ObservableProperty]
    private string _accountDetail = "添加账户后即可开始游戏";

    [ObservableProperty]
    private bool _hasAccount;

    // ---------------- 当前版本 ----------------
    [ObservableProperty]
    private HomeVersionItem? _selectedItem;

    [ObservableProperty]
    private string _selectedVersionName = "未选择版本";

    [ObservableProperty]
    private string _selectedVersionDetail = "在左侧选择一个已安装版本";

    // ---------------- 统计 ----------------
    [ObservableProperty]
    private int _installedVersionCount;

    [ObservableProperty]
    private int _modCount;

    [ObservableProperty]
    private int _worldCount;

    [ObservableProperty]
    private long _diskUsageBytes;

    [ObservableProperty]
    private bool _isRecentEmpty = true;

    // ---------------- 提示 ----------------
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private string? _serviceWarning;

    [ObservableProperty]
    private string? _errorText;

    // ---------------- 启动进度 ----------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCancelLaunch))]
    private bool _isLaunching;

    /// <summary>
    /// 「最近一次启动」的会话是否还在跑。
    ///
    /// 首页的进度面板只能显示一条启动（进度条、阶段文案都是单条语义），所以这里问的是
    /// 「我这次起的那个进程还在吗」，而不是「启动器有没有游戏在跑」——后者是多开时
    /// <see cref="IGameLauncher.IsGameRunning"/> 的语义（任一在跑），会跟进度面板对不上。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopGameCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCancelLaunch))]
    private bool _isLaunchSessionRunning;

    /// <summary>最近一次启动的实例目录键，用来在 <see cref="IGameLauncher.RunningGames"/> 里认回自己。</summary>
    private string? _lastLaunchInstanceKey;

    /// <summary>
    /// 记录「本次启动落在哪个游戏目录」。多开之后首页只负责自己那一次启动，
    /// 所以必须先记住目录才能从会话表里认出自己——认得不准，停止按钮就会去停别人的实例。
    ///
    /// internal 是为了让单测能模拟这一步：<c>LaunchAsync</c> 真跑起来要 JVM，
    /// 而这里要验证的只是「认回自己」这条判定。
    /// </summary>
    internal void TrackLaunchInstance(string? gameDirectory) =>
        _lastLaunchInstanceKey = string.IsNullOrWhiteSpace(gameDirectory)
            ? null
            : RunningGameInfo.KeyFor(gameDirectory);

    /// <summary>上面那个目录当前的会话标识。停止按钮按它杀，不会误停别的实例。</summary>
    private string? _latestSessionId;

    /// <summary>除本次启动之外还有几个实例在跑（多开提示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherRunningGames))]
    [NotifyPropertyChangedFor(nameof(OtherRunningText))]
    private int _otherRunningCount;

    /// <summary>「另有 N 个实例在运行」的可见条件。</summary>
    public bool HasOtherRunningGames => OtherRunningCount > 0;

    /// <summary>
    /// 多开提示文案。首页不做成列表（那是重做首页），只告诉用户「还有别的东西在跑」，
    /// 免得他以为「停止游戏」能把所有实例一起停掉。
    /// </summary>
    public string OtherRunningText => $"另有 {OtherRunningCount} 个实例在运行";

    /// <summary>
    /// 「取消」按钮的可见条件：启动流程在跑、但进程还没起来。
    ///
    /// 进程起来之后，<c>GameLauncher</c> 的「取消等待」会移交后台（<c>Detached</c>，游戏继续运行），
    /// 那是给探针 / 未来入口用的 API 能力；界面上运行期只留「停止游戏」，避免用户以为
    /// 「取消」能结束游戏、或在游戏运行中按 Esc 静默改变启动面板状态。
    /// </summary>
    public bool ShowCancelLaunch => IsLaunching && !IsLaunchSessionRunning;

    [ObservableProperty]
    private bool _hasLaunchPanel;

    [ObservableProperty]
    private string _launchPhaseText = "准备中";

    [ObservableProperty]
    private string _launchDetail = string.Empty;

    [ObservableProperty]
    private string _launchSpeedText = "—";

    [ObservableProperty]
    private double _launchRatio;

    [ObservableProperty]
    private string _launchPercentText = "0%";

    [ObservableProperty]
    private bool _hasFailure;

    [ObservableProperty]
    private string? _failureMessage;

    // ---------------- 缺失 Java 一键安装 ----------------
    //
    // 状态机只有一份（<see cref="JavaInstallState"/>），首页与版本页各持一个实例；
    // 这里只保留首页特有的渲染决定：装好之后安装按钮禁用、另起一个「重新启动」按钮。

    /// <summary>「缺 Java」的安装状态。</summary>
    public JavaInstallState JavaInstall { get; }

    /// <summary>本次启动是否因缺 Java 失败。控制整块安装入口的可见性。</summary>
    public bool HasMissingJava => JavaInstall.HasContext;

    /// <summary>是否已经装好了（按钮要换成「重新启动」）。</summary>
    public bool HasInstalledJava => JavaInstall.IsInstalled;

    /// <summary>提示文案：Core 的 planner 给的那句话，拿不到时退回一句保守说明。</summary>
    public string MissingJavaHint => JavaInstall.Summary
        ?? "本机没有可用的 Java，请在「设置 → Java」安装对应版本，或手动指定路径。";

    /// <summary>「安装 Java N（约 xx MB）」按钮文案；不可一键安装时为 null。</summary>
    public string? JavaInstallButtonText =>
        JavaInstall.Plan is { CanAutoInstall: true, InstallMajor: { } major }
            // 体积是 planner 的估算值（不联网查、不误报精确数字），文案里写「约」。
            ? $"安装 Java {major}（约 {Conv.FormatSize(JavaInstall.Plan.EstimatedDownloadBytes)}）"
            : null;

    /// <summary>「已安装 Java N，重新启动」按钮文案。</summary>
    public string JavaRestartButtonText => JavaInstall.InstalledMajor is { } major
        ? $"已安装 Java {major}，重新启动"
        : "重新启动";

    /// <summary>安装按钮是否可用：能一键装、没在下、也还没装好。</summary>
    public bool CanInstallMissingJava =>
        JavaInstall.Plan is { CanAutoInstall: true } && JavaInstall.CanStart && !JavaInstall.IsInstalled;

    /// <summary>
    /// 安装状态一变，首页所有派生属性都要重算。它们是属性而不是字段，不会自己发通知；
    /// 漏掉任何一个就会复现「哪个页面的进度条/按钮不刷新」这类问题，正是这次合并要消灭的。
    /// </summary>
    private void OnJavaInstallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMissingJava));
        OnPropertyChanged(nameof(HasInstalledJava));
        OnPropertyChanged(nameof(MissingJavaHint));
        OnPropertyChanged(nameof(JavaInstallButtonText));
        OnPropertyChanged(nameof(JavaRestartButtonText));
        OnPropertyChanged(nameof(CanInstallMissingJava));
        InstallMissingJavaCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashSummary))]
    [NotifyPropertyChangedFor(nameof(CanExportCrash))]
    private string? _crashText;

    [ObservableProperty]
    private bool _hasSuggestions;

    /// <summary>是否已经有崩溃结论可供导出。为空表示本次会话还没崩过 / 还没产生结论。</summary>
    public bool HasCrashSummary => !string.IsNullOrWhiteSpace(CrashText);

    // ---------------- 崩溃报告导出（脱敏 zip） ----------------

    /// <summary>本次导出的 zip 绝对路径；导出成功后才有值。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportPath))]
    private string? _exportPath;

    /// <summary>导出中：用于禁用按钮，避免连点产生多个 zip。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportCrash))]
    private bool _isExportingCrash;

    public bool HasExportPath => !string.IsNullOrWhiteSpace(ExportPath);

    /// <summary>没有崩溃结论或正在导出时按钮不可用——不弹错误框，静默禁用。</summary>
    public bool CanExportCrash => HasCrashSummary && !IsExportingCrash;
    /// <summary>本次崩溃归因实际读到的报告文件路径；供「打开崩溃报告」按钮使用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashReportPath))]
    [NotifyCanExecuteChangedFor(nameof(OpenCrashReportCommand))]
    private string? _crashReportPath;

    /// <summary>是否定位到了崩溃报告文件。false 时「打开崩溃报告」按钮静默禁用。</summary>
    public bool HasCrashReportPath => !string.IsNullOrWhiteSpace(CrashReportPath);

    // ---------------- 外部崩溃报告（手动导入，与本机崩溃状态分开） ----------------

    /// <summary>
    /// 手动导入的报告给出的建议。
    ///
    /// 刻意不写进 <see cref="Suggestions"/>：那一组对应用户本机的「最近一次崩溃」，
    /// 导入的是别人的 / 历史的报告，混在一起会让用户以为本机刚崩了同款问题。
    /// </summary>
    public ObservableCollection<CrashSuggestionItem> ImportedSuggestions { get; } = new();

    /// <summary>导入报告的结论摘要（已含来源文件名）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportedCrash))]
    [NotifyPropertyChangedFor(nameof(HasNoImportedCrash))]
    private string? _importedCrashSummary;

    /// <summary>导入文件的显示名（zip 显示外层文件名，具体条目名在摘要里）。</summary>
    [ObservableProperty]
    private string? _importedCrashSource;

    /// <summary>导入报告是否给出了建议。</summary>
    [ObservableProperty]
    private bool _hasImportedSuggestions;

    /// <summary>正在读文件 / 分析，用来禁用入口按钮，避免连点。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportCrash))]
    private bool _isImportingCrash;

    /// <summary>是否已有导入结果。控制首页主区域切换成结果面板。</summary>
    public bool HasImportedCrash => !string.IsNullOrWhiteSpace(ImportedCrashSummary);

    /// <summary>没有导入结果时显示常规仪表盘。</summary>
    public bool HasNoImportedCrash => !HasImportedCrash;

    /// <summary>导入入口是否可用。</summary>
    public bool CanImportCrash => !IsImportingCrash;

    public override async Task OnNavigatedToAsync()
    {
        // 账户变化（登录 / 切换 / 移除 / 令牌续期）要立刻反映到首页卡片：
        // 首页是常驻缓存的页面，停在本页时不会重新加载，登录完成也看不到（R59）。
        // 订阅只挂在页面激活期间——离开时退订，避免「缓存被丢弃的旧页面还挂在账户服务上」。
        AttachAccountEvents();

        await LoadAsync();
        ApplyScreenshotCrashState();
    }

    /// <summary>
    /// 离开首页时停掉在途的 Java 下载：50 MB 的运行时在后台白下完，
    /// 结果用户回来只看到「已安装」却没有进度，既耗流量又难解释。
    /// 启动流程不在这里取消——游戏/文件补齐是有副作用的长任务，取消要用户显式点。
    /// </summary>
    public override Task OnNavigatedFromAsync()
    {
        DetachAccountEvents();

        // 取消源在 <see cref="JavaInstallState"/> 内部，与启动流程的 CTS 分开：
        // 取消下载不该连带打断启动。
        JavaInstall.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>启动流程与 Java 下载的取消源在页面被释放时收尾。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _launchCts?.Cancel();
            _launchCts?.Dispose();
            _launchCts = null;

            DetachAccountEvents();

            JavaInstall.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 截图模式专用：喂一段真实的崩溃输给归因器，让首页崩溃面板（含「导出崩溃报告」按钮）出现。
    ///
    /// 截图模式不能真的启动一次游戏并崩溃，而这一段走的是 <see cref="CrashAnalyzer"/> 的真实规则，
    /// 结论与建议都是真产物，不是硬编码文案。只在 <c>--section crash</c> 下生效，正常运行零影响。
    /// </summary>
    private void ApplyScreenshotCrashState()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "crash", StringComparison.Ordinal)
            || HasCrashSummary)
        {
            return;
        }

        string[] sample =
        [
            "[13:40:00] [main/INFO]: Setting user: Steve",
            "[13:40:01] [main/INFO]: ModLauncher running: args [--version, 1.21.4, --gameDir, D:\\Games\\minecraft]",
            "[13:40:03] [main/INFO]: Found mod file fabric-api-0.92.2.jar",
            "[13:40:03] [main/INFO]: Found mod file sodium-0.5.8.jar",
            "[13:40:05] [main/INFO]: Loaded 6 mods, 1 mod failed to load",
            "[13:40:06] [main/ERROR]: java.lang.NoSuchMethodError: 'void net.example.BrokenApi.init()'",
            "   at com.brokenmod.BrokenMod.onInitialize(BrokenMod.java:42)",
            "---- Minecraft Crash Report ----",
            "Description: Initializing game",
        ];

        try
        {
            var report = CrashAnalyzer.Analyze(sample);

            _lastCrashReport = report;
            CrashText = report.Summary;

            // 崩溃面板的可见性挂在 HasFailure 上（真崩溃时启动一定失败），不是挂在 CrashText 上。
            HasFailure = true;
            FailureMessage = "游戏在启动阶段崩溃退出（退出码 1）";

            Suggestions.Clear();
            foreach (var suggestion in report.Suggestions.Take(CrashAnalyzer.MaxSuggestions))
            {
                Suggestions.Add(WrapSuggestion(suggestion));
            }

            HasSuggestions = Suggestions.Count > 0;

            // 演示路径用 Public 而不是某个真实用户名，截图里也不会出现本机账户。
            CrashReportPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ".minecraft",
                "crash-reports",
                "crash-2026-10-22_13.40.00-client.txt");
        }
        catch (Exception)
        {
            // 截图辅助失败不影响页面渲染。
        }
    }

    /// <summary>
    /// 截图模式专用：把首页摆成「游戏运行中」，让启动面板与「停止游戏」按钮进入画面。
    ///
    /// 截图进程没法真的拉起一个游戏，所以这里只能把「本会话有进程」这个布尔置上；
    /// 它是界面状态的快照，不进启动流程——按钮点下去仍然要经过
    /// <see cref="IGameLauncher.KillCurrentGame"/>，没有进程时是空操作。
    /// 只在 <c>--section running</c> 下生效，正常运行零影响。
    /// </summary>
    private void ApplyScreenshotLaunchState()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "running", StringComparison.OrdinalIgnoreCase)
            || HasLaunchPanel)
        {
            return;
        }

        HasFailure = false;
        HasLaunchPanel = true;
        IsLaunching = true;

        // 截图进程里没有真进程也没有会话：把「本次会话在跑」与目录键一起补上，
        // 否则停止按钮（CanStopGame 读 IsLaunchSessionRunning）不会出现。
        _lastLaunchInstanceKey = null;
        _latestSessionId = null;
        IsLaunchSessionRunning = true;
        LaunchPhaseText = "游戏运行中";
        LaunchDetail = "已进入游戏";
        LaunchSpeedText = "—";
        LaunchRatio = 100;
    }

    // ==================== 加载 ====================

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        ErrorText = null;
        try
        {
            LoadAccount();

            await EnsureInstancesLoadedAsync();

            var installed = await LoadVersionsAsync();
            await LoadStatisticsAsync(installed);
            await DetectGameUpdatesAsync(installed);

            UpdateSubtitle();
        }
        catch (Exception ex)
        {
            ErrorText = $"首页数据加载失败：{ex.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// 账户服务改完状态后可能在后台线程发事件（登录成功、令牌续期、移除账户），
    /// 统一切回 UI 线程再动绑定属性——与账户页 <c>OnAccountsChanged</c> 同一处理方式。
    /// 页面已释放时直接丢弃：排队的回调可能在切页之后才轮到（同 <c>LogsViewModel</c> 的 IsDisposed 守卫）。
    /// </summary>
    private void OnAccountsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            LoadAccount();
            UpdateSubtitle();
        });

    private void AttachAccountEvents()
    {
        if (_accounts is null || _accountEventsAttached)
        {
            return;
        }

        _accounts.AccountsChanged += OnAccountsChanged;
        _accountEventsAttached = true;
    }

    private void DetachAccountEvents()
    {
        if (_accounts is null || !_accountEventsAttached)
        {
            return;
        }

        _accounts.AccountsChanged -= OnAccountsChanged;
        _accountEventsAttached = false;
    }

    private void LoadAccount()
    {
        if (_accounts is null)
        {
            HasAccount = false;
            AccountName = "未登录";
            AccountInitial = "?";
            AccountAvatar = null;
            AccountBadge = "无账户";
            AccountDetail = "账户服务未就绪";
            return;
        }

        var account = _accounts.ActiveAccount;
        HasAccount = account is not null;

        if (account is null)
        {
            AccountName = "未登录";
            AccountInitial = "+";
            AccountAvatar = null;
            AccountBadge = "无账户";
            AccountDetail = "点击添加账户后即可开始游戏";
            return;
        }

        AccountName = string.IsNullOrWhiteSpace(account.Username) ? "未命名账户" : account.Username;
        AccountInitial = AccountName[..1].ToUpperInvariant();
        AccountBadge = AccountTypeDisplay.BadgeOf(account.Type);
        AccountDetail = account.IsExpired
            ? "登录令牌已过期，启动时会自动刷新"
            : AccountTypeDisplay.DetailOf(account, $"UUID {ShortUuid(account.Uuid)}");

        _ = LoadAccountAvatarAsync(account);
    }

    /// <summary>取当前账户头像；失败时清空，占位色块顶上。</summary>
    /// <param name="refresh">true 时绕过缓存重新拉皮肤。</param>
    private async Task LoadAccountAvatarAsync(Account? account, bool refresh = false)
    {
        if (_skinLoader is null || account is null)
        {
            AccountAvatar = null;
            return;
        }

        try
        {
            var bitmap = await _skinLoader.LoadAsync(account, refresh);
            if (bitmap is not null || !refresh)
            {
                AccountAvatar = bitmap;
            }
        }
        catch (Exception)
        {
            if (!refresh)
            {
                AccountAvatar = null;
            }
        }
    }

    /// <summary>右键首页头像 → 刷新：丢弃本地缓存重新查皮肤。</summary>
    [RelayCommand]
    private async Task RefreshAccountAvatarAsync()
    {
        if (_accounts?.ActiveAccount is not { } account)
        {
            return;
        }

        await LoadAccountAvatarAsync(account, refresh: true);
    }

    private async Task<IReadOnlyList<VersionInfo>> LoadVersionsAsync()
    {
        if (_versions is null)
        {
            RecentVersions.Clear();
            IsRecentEmpty = true;
            InstalledVersionCount = 0;
            return Array.Empty<VersionInfo>();
        }

        IReadOnlyList<VersionInfo> installed;
        try
        {
            installed = await _versions.GetInstalledAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"读取已安装版本失败：{ex.Message}";
            IsRecentEmpty = RecentVersions.Count == 0;
            return Array.Empty<VersionInfo>();
        }

        InstalledVersionCount = installed.Count;

        var previous = SelectedItem?.Info.Name;
        RecentVersions.Clear();
        foreach (var info in installed
                     .OrderByDescending(v => v.LastPlayedAt ?? DateTimeOffset.MinValue)
                     .ThenByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(5))
        {
            RecentVersions.Add(new HomeVersionItem(info));
        }

        IsRecentEmpty = RecentVersions.Count == 0;

        // 优先恢复本页上次选中，其次配置里记录的「上次启动版本」，最后取「最近使用」第一条。
        // 记住上次启动的版本，首页的启动目录才会稳定落在同一个实例上。
        var remembered = _settings?.Current.LastSelectedVersion;
        var fallback = RecentVersions.FirstOrDefault(i => string.Equals(i.Info.Name, previous, StringComparison.Ordinal))
                       ?? RecentVersions.FirstOrDefault(i => string.Equals(i.Info.Name, remembered, StringComparison.Ordinal))
                       ?? RecentVersions.FirstOrDefault();
        SelectedItem = fallback;

        return installed;
    }

    private async Task LoadStatisticsAsync(IReadOnlyList<VersionInfo> installed)
    {
        var gameDirectory = ResolveGameDirectory(SelectedItem?.Info.Name);

        ModCount = await CountContentAsync(gameDirectory);
        WorldCount = await CountWorldsAsync(gameDirectory);

        DiskUsageBytes = installed.Where(v => v.SizeOnDisk > 0).Sum(v => v.SizeOnDisk);
        if (gameDirectory is not null)
        {
            var modsDirectory = Path.Combine(gameDirectory, "mods");
            var modBytes = await Task.Run(() => MeasureDirectory(modsDirectory)).ConfigureAwait(true);
            DiskUsageBytes += modBytes;
        }
    }

    private async Task<int> CountContentAsync(string? gameDirectory)
    {
        if (_content is null || gameDirectory is null)
        {
            return 0;
        }

        try
        {
            var items = await _content.ScanAsync(gameDirectory, ContentKind.Mod);
            return items.Count;
        }
        catch (Exception ex)
        {
            ErrorText ??= $"扫描模组失败：{ex.Message}";
            return 0;
        }
    }

    private async Task<int> CountWorldsAsync(string? gameDirectory)
    {
        if (_backups is null || gameDirectory is null)
        {
            return 0;
        }

        try
        {
            var worlds = await _backups.ListWorldsAsync(gameDirectory);
            return worlds.Count;
        }
        catch (Exception ex)
        {
            ErrorText ??= $"读取存档失败：{ex.Message}";
            return 0;
        }
    }

    /// <summary>
    /// 实例服务不会在构造或导航时自动加载，首页第一次用到实例前必须显式读一次磁盘。
    /// 只读一次：实例增删都发生在版本页，两页共享同一个单例服务。
    /// </summary>
    private async Task EnsureInstancesLoadedAsync()
    {
        if (_instancesLoaded || _instances is null)
        {
            return;
        }

        _instancesLoaded = true;

        try
        {
            await _instances.LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText ??= $"读取实例失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 按当前版本找实例目录：该版本有实例时用实例目录（mods / saves / config 隔离），
    /// 否则退回 .minecraft。与版本页共用 <see cref="InstanceLocator"/>，保证两个入口落到同一目录。
    /// </summary>
    private string? ResolveGameDirectory(string? versionName)
    {
        if (_instances is not null)
        {
            try
            {
                var instance = InstanceLocator.FindForVersion(_instances.Instances, versionName);
                if (instance is not null)
                {
                    return _instances.GetGameDirectory(instance.Id);
                }
            }
            catch (Exception)
            {
                // 实例信息损坏时退回默认 .minecraft，不让首页整体失败。
            }
        }

        return _paths?.MinecraftDirectory;
    }

    /// <summary>当前版本命中的实例名，用于首页提示「启动会写进哪个目录」。</summary>
    private string? DescribeActiveInstance(string? versionName)
    {
        if (_instances is null)
        {
            return null;
        }

        try
        {
            return InstanceLocator.FindForVersion(_instances.Instances, versionName)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析启动目标：优先走 <see cref="IInstanceContext"/>（含隔离策略与自动建实例），
    /// 服务缺失时退回旧的「只查已有实例」逻辑。
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
                LaunchDetail = $"按实例解析目录失败，改用全局目录：{ex.Message}";
            }
        }

        var directory = ResolveGameDirectory(versionName);

        return new InstanceResolution(
            directory ?? _paths?.MinecraftDirectory ?? string.Empty,
            Instance: null,
            Isolated: DescribeActiveInstance(versionName) is not null,
            Created: false);
    }

    private void UpdateSubtitle()
    {
        var who = HasAccount ? AccountName : "未登录";
        var text = InstalledVersionCount == 0
            ? $"{who} · 尚未安装版本"
            : $"{who} · {InstalledVersionCount} 个版本可用";

        if (text == _subtitle)
        {
            return;
        }

        _subtitle = text;
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnSelectedItemChanged(HomeVersionItem? value)
    {
        if (value is null)
        {
            SelectedVersionName = InstalledVersionCount == 0 ? "还没有版本" : "未选择版本";
            SelectedVersionDetail = "在左侧选择一个已安装版本";
            return;
        }

        SelectedVersionName = value.Name;

        // 首页没有实例选择器，启动目录由「该版本的实例」决定。把结果显示出来，
        // 否则用户在版本页建了实例，却看不出首页启动会落到哪个目录。
        var instanceName = DescribeActiveInstance(value.Name);
        SelectedVersionDetail = instanceName is null
            ? $"{value.Detail} · 全局目录"
            : $"{value.Detail} · 实例 {instanceName}";

        Notice = null;
    }

    partial void OnLaunchRatioChanged(double value)
    {
        LaunchPercentText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)Math.Round(value)}%");
    }

    // ==================== 启动 ====================

    [RelayCommand(CanExecute = nameof(CanStartLaunch))]
    private async Task LaunchAsync()
    {
        // 新一代启动：从这里起，上一次（尤其是 Detach 后仍在跑的）会话的进度与退出事件全部失效。
        var generation = ++_launchGeneration;
        _detachedSessionId = null;

        HasFailure = false;
        FailureMessage = null;
        CrashText = null;
        CrashReportPath = null;
        Suggestions.Clear();
        HasSuggestions = false;

        // 缺 Java 的入口属于「上一次启动」：新一次启动必须先收掉，
        // 否则用户装好 Java 再启动失败于别的原因时，界面还挂着「安装 Java 25」。
        ClearMissingJavaState();

        // 上一次崩溃的结论与导出路径属于上一次运行，新一次启动前必须清掉，
        // 否则「导出崩溃报告」会导出上一局的结论。
        _lastCrashReport = null;
        _lastHsErrLogs = Array.Empty<string>();
        ExportPath = null;

        var version = SelectedItem?.Info;
        if (version is null)
        {
            Notice = InstalledVersionCount == 0
                ? "还没有安装任何版本，先去「下载」页装一个。"
                : "请先在左侧选择一个版本。";
            return;
        }

        if (_launcher is null || _accounts is null)
        {
            Notice = "启动服务未就绪：账户或游戏启动模块尚未注册。";
            return;
        }

        if (!HasAccount)
        {
            Notice = "还没有账户。去「账户」页添加一个离线账户即可开始游戏。";
            return;
        }

        _launchCts?.Dispose();
        _launchCts = new CancellationTokenSource();
        lock (_launchLogGate)
        {
            _launchLog.Clear();
        }

        IsLaunching = true;
        HasLaunchPanel = true;
        HasFailure = false;
        _visibilityHandled = false;
        LaunchPhaseText = "正在准备";
        LaunchDetail = "检查账户有效性…";
        LaunchSpeedText = "—";
        LaunchRatio = 0;
        Notice = null;

        _launcher.OutputReceived += OnGameOutput;

        try
        {
            var account = await _accounts.EnsureValidActiveAccountAsync(_launchCts.Token);
            if (account is null)
            {
                Fail("账户令牌已失效，请到账户页重新登录后再启动。");
                return;
            }

            // 与版本页保持一致：这次从哪个实例目录启动，模组页 / 资源页 / 世界页就跟着切到哪个目录。
            // 目录由 InstanceContext 按隔离策略解析（已有实例优先，其次自动建，最后全局）。
            var resolution = await ResolveTargetAsync(version.Name);

            // 记住这次的目录：停止按钮要靠它认回自己的会话（多开时不能按「全局某一个」去杀）。
            TrackLaunchInstance(resolution.GameDirectory);

            // 合成规则只有一份，四个入口共用；字段级的「为什么」写在 LaunchOptionsFactory 里。
            // 首页此前是唯一一处手写构造，漏了 VersionArgumentInfo（实例的 --versionType 不生效），
            // 这类偏差不会报错，只会让同一个设置在某个入口悄悄失效，所以不再在入口里拼字段。
            var options = LaunchOptionsFactory.Build(
                account,
                version.Name,
                resolution,
                _settings?.Current);

            var progress = new Progress<LaunchProgress>(p => ApplyProgress(generation, p));
            var result = await _launcher.LaunchAsync(options, progress, _launchCts.Token);

            if (!result.Started)
            {
                Fail(string.IsNullOrWhiteSpace(result.FailureReason) ? "启动失败，未收到具体原因。" : result.FailureReason);

                // 「缺 Java」是唯一能就地修好的失败类别：把安装入口摆出来，用户不必
                // 自己从文案里抠出版本号、再切到设置页选一遍。其余失败照旧只显示结论。
                await ApplyMissingJavaAsync(result.MissingJavaMajor, version).ConfigureAwait(true);
                AnalyzeOutput(result);
                return;
            }

            if (result.Detached)
            {
                // 取消等待但游戏还在跑：不是失败，也不能报「已退出」。
                // 记住会话标识：它退出时 SessionExited 事件只处理这一条。
                _detachedSessionId = result.SessionId;
                LaunchPhaseText = "游戏运行中";
                LaunchDetail = "已取消等待，游戏仍在后台运行；可随时点「停止游戏」。";
                LaunchRatio = 100;
                return;
            }

            // 最终判定以 Core 的 verdict 为准：exit 0 也可能是加载到一半被杀（崩溃），
            // 2012 年前的老版本正常关闭也可能返回非零码。Verdict 为 null 的旧路径仍看退出码兜底。
            var crashed = result.Verdict == GameExitVerdict.Crashed
                          || (result.Verdict is null && result.ExitCode != 0);

            if (crashed)
            {
                Fail($"游戏异常退出（退出码 {result.ExitCode}，运行 {Conv.FormatDuration(result.Duration)}）。");
                AnalyzeOutput(result);
                return;
            }

            LaunchPhaseText = "游戏已退出";
            LaunchDetail = $"本次运行 {Conv.FormatDuration(result.Duration)}";
            LaunchRatio = 100;
        }
        catch (OperationCanceledException)
        {
            LaunchPhaseText = "已取消";
            LaunchDetail = "启动流程被取消。";
        }
        catch (Exception ex)
        {
            Fail($"启动过程出错：{ex.Message}");
        }
        finally
        {
            _launcher.OutputReceived -= OnGameOutput;

            // 启动流程走完（成功、失败、取消）都清掉任务栏进度：
            // 游戏已经在跑时任务栏还挂着「正在启动」会让人以为启动器卡住了。
            ShellProgress.Clear();

            IsLaunching = false;
            RefreshRunningSessions();
            UpdateSubtitle();
        }
    }

    private bool CanStartLaunch() => !IsLaunching;

    [RelayCommand]
    private void CancelLaunch()
    {
        try
        {
            _launchCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 启动已结束，取消是空操作。
        }
    }

    /// <summary>
    /// 停止「最近一次启动」的游戏进程。
    ///
    /// 按会话停（<see cref="IGameLauncher.KillGame"/>），不按进程名扫 <c>javaw</c>：
    /// 多开时按名扫会把用户另一个实例、甚至别的 Java 程序一起干掉。
    /// 启动器重启之后本会话没有任何句柄，<see cref="IGameLauncher.RunningGames"/> 里查不到我们，
    /// 这里就判定为「没有可停的进程」，绝不退化成按名匹配。
    ///
    /// 不弹二次确认：卡死时这是救援动作，多一次点击就多一次卡顿；
    /// 但文案写「停止游戏」而不是「关闭」，用户得知道这会杀掉进程。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopGame))]
    private async Task StopGameAsync()
    {
        var launcher = _launcher;

        if (launcher is null)
        {
            return;
        }

        // 先刷新一次：进程可能已经自己退了（或被任务管理器结束）。
        // 这种「已经停了」不是错误，刷新状态就够，不必报错。
        RefreshRunningSessions();

        var sessionId = _latestSessionId;

        if (sessionId is null)
        {
            return;
        }

        LaunchDetail = "正在停止游戏进程…";

        bool requested;

        try
        {
            // Kill 是同步阻塞的（TerminateProcess + 收句柄），放线程池避免卡住 UI 线程。
            requested = await Task.Run(() => launcher.KillGame(sessionId)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // KillGame 内部已吞掉杀进程本身的失败，这里兜 Task 调度层。
            LaunchDetail = $"停止游戏失败：{ex.Message}";
            RefreshRunningSessions();
            return;
        }

        // 等「这个会话」从在跑列表里消失，而不是等「全局没有游戏在跑」：
        // 多开时后者要等所有实例都退出，用户会觉得按了没反应。
        var exited = !requested
                     || await ProcessWait
                         .UntilAsync(
                             () => !launcher.RunningGames.Any(game =>
                                 string.Equals(game.SessionId, sessionId, StringComparison.Ordinal)),
                             TimeSpan.FromSeconds(8))
                         .ConfigureAwait(true);

        // 进程退出后 LaunchAsync 还要跑崩溃分析、退出后命令，最后才释放实例锁。
        // 不等这一步，用户紧接着点启动会撞上「该实例已有游戏在运行」。
        if (exited)
        {
            await ProcessWait
                .UntilAsync(() => !IsLaunching, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
        }

        RefreshRunningSessions();

        if (exited && !IsLaunchSessionRunning)
        {
            LaunchPhaseText = "游戏已停止";
            LaunchDetail = "已停止游戏进程。";
        }
        else
        {
            LaunchDetail = "已发出停止请求，但进程仍未退出：请到任务管理器结束它。";
        }
    }

    private bool CanStopGame() => _launcher is not null && IsLaunchSessionRunning;

    /// <summary>
    /// 从启动器同步「本次会话是否还在跑」与「另有几个实例在跑」。
    ///
    /// 唯一的状态来源是 <see cref="IGameLauncher.RunningGames"/>：它在读进程真实状态，
    /// 而界面里的布尔只是缓存。这样「用户在任务管理器里杀了进程」也能被下一次刷新纠正。
    /// </summary>
    private void RefreshRunningSessions()
    {
        var running = _launcher?.RunningGames;

        if (running is null || running.Count == 0)
        {
            _latestSessionId = null;
            IsLaunchSessionRunning = false;
            OtherRunningCount = 0;
            return;
        }

        RunningGameInfo? mine = null;

        if (_lastLaunchInstanceKey is not null)
        {
            foreach (var game in running)
            {
                if (string.Equals(game.InstanceKey, _lastLaunchInstanceKey, StringComparison.OrdinalIgnoreCase))
                {
                    mine = game;
                    break;
                }
            }
        }

        // 没有记录过目录（本页还没启动过东西）就不认领任何会话：
        // 认「最近启动的那个」会让首页在用户在版本页启动后，把别人的实例当成自己的会话。

        _latestSessionId = mine?.SessionId;
        IsLaunchSessionRunning = mine is not null;
        OtherRunningCount = Math.Max(0, running.Count - (mine is null ? 0 : 1));
    }

    private void ApplyProgress(int generation, LaunchProgress progress)
    {
        // 旧会话（Detach 后仍在跑）的进度不能改新一次启动的面板。
        if (!IsCurrentLaunchGeneration(generation, _launchGeneration))
        {
            return;
        }

        LaunchDetail = progress.Message;

        switch (progress.Phase)
        {
            case LaunchPhase.Preparing:
                LaunchPhaseText = "正在准备";
                break;
            case LaunchPhase.CheckingFiles:
                LaunchPhaseText = "检查游戏文件";
                break;
            case LaunchPhase.DownloadingAssets:
                LaunchPhaseText = "下载缺失文件";
                break;
            case LaunchPhase.StartingProcess:
                LaunchPhaseText = "启动进程";
                break;
            case LaunchPhase.Running:
                LaunchPhaseText = "游戏运行中";
                LaunchRatio = 100;

                // 问启动器要答案而不是直接置 true：进度回调可能晚到一步，
                // 那时进程也许已经退出，置 true 会留下一个点不动的「停止游戏」。
                RefreshRunningSessions();
                ApplyVisibilityAfterLaunch();
                break;
            case LaunchPhase.Exited:
                LaunchPhaseText = "游戏已退出";
                LaunchRatio = 100;
                RefreshRunningSessions();
                break;
            case LaunchPhase.Failed:
                LaunchPhaseText = "启动失败";
                Fail(progress.Message);
                break;
        }

        if (progress.Phase is LaunchPhase.DownloadingAssets or LaunchPhase.CheckingFiles && progress.TotalBytes > 0)
        {
            LaunchRatio = Math.Clamp(progress.Ratio * 100, 0, 100);
        }

        // 进程起来之后（Running）还有几十秒的黑屏期。PCL2 用 5 级日志里程碑把这段时间说清楚，
        // 这里照抄同一套判据，让首页显示「材质加载完成」而不是一句「游戏运行中」。
        if (progress.StartupStage != GameStartupStage.None)
        {
            LaunchDetail = DescribeStartupStage(progress.StartupStage);
        }

        LaunchSpeedText = progress.SpeedBytesPerSecond > 0
            ? $"{Conv.FormatSize(progress.SpeedBytesPerSecond)}/s"
            : "—";

        // 任务栏进度：进程起来之后就没有可量化的比例了（黑屏期只能按日志里程碑描述），
        // 这里直接清除，避免任务栏挂着一条不动的进度。ShellProgress 内部会过滤非法值。
        if (progress.Phase is LaunchPhase.Running or LaunchPhase.Exited or LaunchPhase.Failed)
        {
            ShellProgress.Clear();
        }
        else
        {
            ShellProgress.Report(progress.Ratio);
        }
    }

    /// <summary>
    /// 后台移交会话的退出结果（只有 Detach 的会话会触发）。
    ///
    /// 事件来自后台线程：切回 UI 线程，并且只处理「仍属于当前面板」的那一条——
    /// 用户可能已经发起新启动，旧会话的崩溃结论不该盖掉新面板。
    /// </summary>
    private void OnDetachedSessionExited(object? sender, SessionExitedEventArgs e)
    {
        if (!ShouldApplyDetachedExit(_detachedSessionId, e.SessionId))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyDetachedExit(e));
    }

    private void ApplyDetachedExit(SessionExitedEventArgs e)
    {
        // 排队期间用户可能已经发起新启动（那会清空 _detachedSessionId）：二次校验。
        if (!ShouldApplyDetachedExit(_detachedSessionId, e.SessionId))
        {
            return;
        }

        _detachedSessionId = null;
        var result = e.Result;

        var crashed = result.Verdict == GameExitVerdict.Crashed
                      || (result.Verdict is null && result.ExitCode != 0);

        if (crashed)
        {
            Fail("取消等待后游戏在后台运行，最终异常退出。");
            AnalyzeOutput(result);
        }
        else
        {
            LaunchPhaseText = "游戏已退出";
            LaunchDetail = $"本次运行 {Conv.FormatDuration(result.Duration)}";
            LaunchRatio = 100;
        }

        RefreshRunningSessions();
        UpdateSubtitle();
    }

    /// <summary>
    /// 后台退出事件是否属于当前面板的会话：只有「仍记得的那个会话」才处理。
    /// 空值一律不匹配——没有记住任何会话时，任何后台退出都不该改首页。
    /// </summary>
    internal static bool ShouldApplyDetachedExit(string? currentSessionId, string incomingSessionId) =>
        !string.IsNullOrEmpty(currentSessionId)
        && string.Equals(currentSessionId, incomingSessionId, StringComparison.Ordinal);

    /// <summary>进度回调的启动代次是否仍是当前代：旧会话（Detach 后）的进度必须丢弃。</summary>
    internal static bool IsCurrentLaunchGeneration(int expectedGeneration, int currentGeneration) =>
        expectedGeneration == currentGeneration;

    /// <summary>把 PCL2 的日志里程碑翻成用户能看懂的进度文案。</summary>
    private static string DescribeStartupStage(GameStartupStage stage) => stage switch
    {
        GameStartupStage.Launched => "进程已启动，等待游戏输出…",
        GameStartupStage.UserSet => "已注入登录会话",
        GameStartupStage.LwjglReady => "图形栈就绪，正在加载资源",
        GameStartupStage.AudioReady => "音频系统就绪，正在加载材质",
        GameStartupStage.TexturesLoaded => "材质加载完成，即将进入主菜单",
        GameStartupStage.MainMenu => "已进入游戏",
        _ => "正在启动…",
    };

    /// <summary>
    /// 游戏进程起来之后按设置处理启动器窗口（PCL2 <c>LaunchArgumentVisible</c>）。
    ///
    /// 只在第一次进入 Running 时执行：进度会持续刷新 Running，
    /// 重复执行会在用户重新打开窗口后立刻又把它关掉。
    /// </summary>
    private void ApplyVisibilityAfterLaunch()
    {
        if (_visibilityHandled)
        {
            return;
        }

        _visibilityHandled = true;

        if (_windowActions is not { IsAvailable: true })
        {
            return;
        }

        try
        {
            switch (_settings?.Current.LauncherVisibilityOnLaunch)
            {
                case "minimize":
                    _windowActions.Minimize();
                    break;
                case "close":
                    _windowActions.Close();
                    break;
            }
        }
        catch (Exception)
        {
            // 窗口动作失败只影响观感，绝不能让它冒泡成「启动失败」——游戏已经在跑了。
        }
    }

    private static bool HasCrash(LaunchResult result) =>
        !string.IsNullOrWhiteSpace(result.CrashAnalysis) || result.CrashReport is { IsCrash: true };

    /// <summary>
    /// 检查「官方正式版出了新的、而本机没装」。只提示正式版，不提示快照——
    /// 快照几乎每次都有更新，提示它会变成常驻噪音（PCL2 也把两种渠道分开开关）。
    ///
    /// 同一个版本只提示一次：写回 <see cref="AppSettings.LastNotifiedGameVersion"/> 后不再重复。
    /// 版本清单拉取失败时静默跳过，首页不因为联网失败而多一条错误。
    /// </summary>
    private async Task DetectGameUpdatesAsync(IReadOnlyList<VersionInfo> installed)
    {
        HasGameUpdate = false;
        GameUpdateNotice = null;
        _gameUpdateVersion = null;

        if (_versions is null || installed.Count == 0)
        {
            return;
        }

        try
        {
            var remote = await _versions.GetRemoteAsync().ConfigureAwait(true);
            var latest = remote.FirstOrDefault(version => version.Channel == VersionChannel.Release);

            if (latest is null)
            {
                return;
            }

            var installedNames = new HashSet<string>(
                installed.Select(version => version.Name),
                StringComparer.OrdinalIgnoreCase);

            if (installedNames.Contains(latest.Id))
            {
                return;
            }

            if (string.Equals(_settings?.Current.LastNotifiedGameVersion, latest.Id, StringComparison.Ordinal))
            {
                return;
            }

            _gameUpdateVersion = latest.Id;
            GameUpdateNotice = $"Minecraft {latest.Id} 已发布（正式版），本机还没有安装。";
            HasGameUpdate = true;
        }
        catch (Exception)
        {
            // 更新提示是锦上添花：拿不到清单就不提示，绝不把它变成首页错误。
        }
    }

    [RelayCommand]
    private async Task AcknowledgeGameUpdateAsync()
    {
        HasGameUpdate = false;

        if (_settings is null || _gameUpdateVersion is null)
        {
            return;
        }

        try
        {
            _settings.Current.LastNotifiedGameVersion = _gameUpdateVersion;
            await _settings.SaveAsync();
        }
        catch (Exception)
        {
            // 记录失败只意味着下次还会提示一次，不影响其它功能。
        }
    }

    private void Fail(string message)
    {
        HasLaunchPanel = true;
        HasFailure = true;
        FailureMessage = message;
        LaunchPhaseText = "启动失败";
    }

    // ==================== 缺失 Java 一键安装 ====================

    /// <summary>
    /// 启动失败且原因是缺 Java 时，把「装哪个版本、能不能一键装」算出来并摆到界面上。
    ///
    /// 判断交给 Core 的 <see cref="JavaProvisionPlanner"/>：那份规则与设置页的下载选项同源，
    /// 界面这边只做两件事——按结果决定按钮，和把装好的路径写回设置。
    /// 探测与取允许区间都可能失败，但这里绝不能抛：启动已经失败了，界面不能再崩一次。
    /// </summary>
    private async Task ApplyMissingJavaAsync(int? missingMajor, VersionInfo? version)
    {
        if (missingMajor is not > 0)
        {
            return;
        }

        JavaInstall.Reset();

        if (_java is null)
        {
            // 判不出来：保留需求主版本让入口仍然显示（给「打开 Java 设置」的出口），
            // 但不给安装按钮——装不成还摆按钮等于把用户送去多下一次。
            JavaInstall.Begin(missingMajor.Value, plan: null);
            JavaInstall.StatusText = "Java 服务未就绪，请到「设置 → Java」手动指定路径。";
            return;
        }

        IReadOnlyList<JavaInfo> installed = Array.Empty<JavaInfo>();
        (int Min, int Max)? allowedRange = null;

        try
        {
            installed = await _java.DetectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(true);

            // 上界是加载器带来的硬约束（1.12.2 Forge 只能 Java ≤8）。拿不到区间
            // （加载器版本号不是原版版本号、快照等）就退回「只按需要的主版本判断」，
            // 这比给一个装了也起不来的版本好——但比它更好的是能判断时不猜。
            //
            // 第三个参数是加载器自身版本：这里拿不到 InstanceResolution（启动已经失败，
            // 只剩这次要启动的版本对象），而 VersionInfo.LoaderVersion 就是同一个版本目录
            // 解析出来的值——它把「1.19.4 Forge 45.0.40 只能到 Java 19」这类上限带进来，
            // 否则一键安装会推荐一个装上也起不来的 Java。
            if (version is not null && !string.IsNullOrWhiteSpace(version.Name))
            {
                allowedRange = _java.GetAllowedRange(version.Name, version.Loader, version.LoaderVersion);
            }
        }
        catch (Exception ex)
        {
            Notice = $"读取本机 Java 列表失败，只能按需要的版本给建议：{ex.Message}";
        }

        JavaInstall.Begin(missingMajor.Value, JavaProvisionPlanner.Plan(missingMajor.Value, installed, allowedRange));
    }

    /// <summary>清掉上一轮「缺 Java」的全部状态（新一次启动、或装好之后重新走启动流程）。</summary>
    private void ClearMissingJavaState() => JavaInstall.Reset();

    /// <summary>
    /// 下载并安装缺失的 Java 运行时，成功后写回设置并强制重扫，让「重新启动」真的能起来。
    ///
    /// 下载 → 写回 → 保存 → 强制重扫的顺序由 <see cref="JavaInstallState.InstallAsync"/> 固定，
    /// 页面这边只决定「路径接到哪里」（首页与版本页相同：写 settings.json 并立即落盘）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallMissingJava))]
    private async Task InstallMissingJavaAsync()
    {
        if (JavaInstall.Plan is not { CanAutoInstall: true, InstallMajor: { } major })
        {
            return;
        }

        var progress = new Progress<DownloadProgress>(JavaInstall.ApplyProgress);
        await JavaInstall.InstallAsync(major, JavaInstallSink.SettingsFile(_settings), progress).ConfigureAwait(true);
    }

    /// <summary>装好之后直接重来一次启动，省掉「切到版本页再点启动」。</summary>
    [RelayCommand]
    private async Task RelaunchAfterJavaInstallAsync()
    {
        if (IsLaunching)
        {
            return;
        }

        await LaunchAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 截图专用：把首页推进「缺少 Java 25」的失败态。
    ///
    /// 真实路径要真跑一次启动、真探测本机 Java；截图模式要确定性，所以这里只摆状态：
    /// planner 仍然走真实规则（空列表 + 需求 25），得到的按钮文案与正常路径一致。
    /// 不写盘、不联网、不探测。
    /// </summary>
    public void ApplyMissingJavaDemo()
    {
        if (!ScreenshotMode.IsActive)
        {
            return;
        }

        HasLaunchPanel = true;
        HasFailure = true;
        LaunchPhaseText = "启动失败";
        LaunchDetail = "正在选择 Java 运行时…";
        FailureMessage =
            "Minecraft 26.1 需要 Java 25 或更高（26.1 属于 26.x 版本线，需要 Java 25），"
            + "本机没有找到满足要求的运行时。请在「设置 → Java」下载 Java 25，或手动指定路径。";

        JavaInstall.Reset();
        JavaInstall.Begin(25, JavaProvisionPlanner.Plan(25, Array.Empty<JavaInfo>()));
    }

    private void OnGameOutput(object? sender, GameOutputLine line)
    {
        lock (_launchLogGate)
        {
            if (_launchLog.Count >= LaunchLogLimit)
            {
                _launchLog.RemoveAt(0);
            }

            _launchLog.Add(line.Text);
        }
    }

    /// <summary>
    /// 优先用启动器给出的结论，否则对抓到的最后若干行做本地规则匹配。
    /// 报告路径无论走哪条分支都要记下来，否则「打开崩溃报告」按钮永远是灰的。
    /// </summary>
    private void AnalyzeOutput(LaunchResult result)
    {
        // hs_err 在后面的分支里可能被各种 early return 漏掉，先无条件收下：
        // 它只影响导出，不影响结论展示。
        _lastHsErrLogs = result.HsErrLogs;

        var reportPath = result.CrashReport?.CrashReportPath;

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            CrashReportPath = reportPath;
        }

        if (HasSuggestions)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.CrashAnalysis))
        {
            CrashText = result.CrashAnalysis;

            // 启动器给了结论文本时也可能带着完整结论对象；能拿就拿，导出要用其中的建议与报告路径。
            _lastCrashReport = result.CrashReport;
            return;
        }

        string[] lines;
        lock (_launchLogGate)
        {
            lines = _launchLog.ToArray();
        }

        if (lines.Length == 0)
        {
            return;
        }

        try
        {
            // 启动器没给路径时，本地日志里也可能留着游戏自报的标记。
            if (string.IsNullOrWhiteSpace(CrashReportPath))
            {
                var markerPath = CrashAnalyzer.ExtractCrashReportPath(string.Join(Environment.NewLine, lines));
                if (!string.IsNullOrWhiteSpace(markerPath))
                {
                    CrashReportPath = markerPath;
                }
            }

            var report = CrashAnalyzer.Analyze(lines);
            foreach (var suggestion in report.Suggestions.Take(3))
            {
                Suggestions.Add(WrapSuggestion(suggestion));
            }

            HasSuggestions = Suggestions.Count > 0;
            _lastCrashReport = report;
            CrashText = report.Summary;
        }
        catch (Exception ex)
        {
            CrashText = $"崩溃日志分析失败：{ex.Message}";
        }
    }

    // ==================== 外部报告导入 ====================

    /// <summary>
    /// 分析用户手动导入的 <c>crash-*.txt</c> / <c>hs_err_pid*.log</c> / zip。
    ///
    /// 文件选择在 code-behind 里做（ViewModel 拿不到 TopLevel），选完把路径交给这里。
    /// 结论写进 <see cref="ImportedSuggestions"/> 与 <see cref="ImportedCrashSummary"/>，
    /// 不改动本机崩溃状态（<see cref="CrashText"/> / <see cref="Suggestions"/> / <see cref="CrashReportPath"/>）。
    /// 读取失败的文案由 Core 给出（带来源文件名），这里只兜底未预料的异常。
    /// </summary>
    /// <param name="path">用户选择的文件绝对路径。</param>
    public async Task ImportExternalReportAsync(string? path)
    {
        if (IsImportingCrash)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            Notice = "没有选择报告文件。";
            return;
        }

        IsImportingCrash = true;

        try
        {
            var report = await Task.Run(() => CrashAnalyzer.AnalyzeExternalReport(path));

            ImportedCrashSource = ShortFileName(path);
            ImportedCrashSummary = report.Summary;

            ImportedSuggestions.Clear();
            foreach (var suggestion in report.Suggestions.Take(CrashAnalyzer.MaxSuggestions))
            {
                ImportedSuggestions.Add(WrapSuggestion(suggestion));
            }

            HasImportedSuggestions = ImportedSuggestions.Count > 0;

            if (!report.IsCrash)
            {
                Notice = "这份报告里没有识别到崩溃特征，可能是正常日志或被截断的文件。";
            }
        }
        catch (Exception ex)
        {
            ImportedCrashSource = ShortFileName(path);
            ImportedCrashSummary = $"分析失败：{ex.Message}";
            ImportedSuggestions.Clear();
            HasImportedSuggestions = false;
        }
        finally
        {
            IsImportingCrash = false;
        }
    }

    /// <summary>关闭导入结果，主区域回到常规仪表盘。不影响本机崩溃建议。</summary>
    [RelayCommand]
    private void ClearImportedCrash()
    {
        ImportedCrashSummary = null;
        ImportedCrashSource = null;
        ImportedSuggestions.Clear();
        HasImportedSuggestions = false;
    }

    // ==================== 崩溃报告导出（脱敏 zip） ====================

    /// <summary>
    /// 把本次崩溃的结论、游戏输出、启动器日志与系统快照导出成一个脱敏 zip。
    ///
    /// 目录选择在 code-behind 里做（ViewModel 拿不到 TopLevel），选完把路径交给这里。
    /// 导出内容全部经过 <c>LogRedactor</c>：令牌、账户名、本机路径用户段都不会出现在 zip 里。
    /// </summary>
    /// <param name="directory">用户选择的导出目录。为空时只更新提示，不弹错误框。</param>
    public async Task ExportCrashReportAsync(string? directory)
    {
        if (IsExportingCrash)
        {
            return;
        }

        if (_crashExport is null)
        {
            Notice = "崩溃导出服务尚未就绪，请稍后再试。";
            return;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            Notice = "没有选择导出目录。";
            return;
        }

        if (!HasCrashSummary)
        {
            Notice = "当前没有崩溃记录可导出。";
            return;
        }

        IsExportingCrash = true;

        try
        {
            string[] lines;
            lock (_launchLogGate)
            {
                lines = _launchLog.ToArray();
            }

            var (mods, modsError) = await CollectModsAsync().ConfigureAwait(true);
            var hsErrText = _lastHsErrLogs.Count == 0
                ? null
                : string.Join(Environment.NewLine, _lastHsErrLogs);

            var request = new CrashExportRequest(
                _lastCrashReport ?? BuildFallbackReport(),
                lines,
                HsErrText: hsErrText,
                System: BuildDiagnosticsInput(),
                Mods: mods,
                ModsError: modsError);

            var result = await _crashExport.ExportAsync(request, directory).ConfigureAwait(true);

            if (!result.Success)
            {
                Notice = result.Error ?? "导出失败。";
                return;
            }

            ExportPath = result.ArchivePath;

            // 状态栏给完整路径：用户要把它贴到群里或发给别人。
            Notice =
                $"崩溃报告已导出（{result.Entries?.Count ?? 0} 个文件，{Conv.FormatSize(result.Bytes)}）：{result.ArchivePath}";
        }
        catch (Exception ex)
        {
            Notice = $"导出崩溃报告失败：{ex.Message}";
        }
        finally
        {
            IsExportingCrash = false;
        }
    }

    /// <summary>打开上次导出 zip 所在目录。没导出过时静默提示，不弹错误框。</summary>
    [RelayCommand]
    private async Task OpenExportFolderAsync()
    {
        if (_platform is null)
        {
            Notice = "平台服务尚未接入，无法打开目录。";
            return;
        }

        var directory = string.IsNullOrWhiteSpace(ExportPath) ? null : SafeDirectoryOf(ExportPath!);

        if (string.IsNullOrWhiteSpace(directory))
        {
            Notice = "还没有导出过崩溃报告。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(directory);
            Notice = $"已打开：{directory}";
        }
        catch (Exception ex)
        {
            Notice = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 没有完整结论对象时用界面上的内容拼一个最小结论。
    ///
    /// 启动器走 <c>CrashAnalysis</c> 文本分支时不会产生 <see cref="CrashReport"/>，
    /// 但用户看到的摘要与建议仍然应该进导出文件——导出「比界面上少一截」是最难解释的差异。
    /// </summary>
    private CrashReport BuildFallbackReport() =>
        new(
            IsCrash: true,
            Summary: CrashText ?? "（没有结论）",
            Suggestions: Suggestions.Select(item => item.Suggestion).ToList(),
            CrashReportPath: CrashReportPath);

    /// <summary>
    /// 收集当前实例 mods 目录的快照供导出使用。
    ///
    /// 目录必须经 <see cref="IInstanceContext.GameDirectory"/>——实例隔离时模组装在实例目录，
    /// 直接读全局 <c>.minecraft/mods</c> 会导出一份与本次崩溃无关的列表。
    /// 读目录失败只返回错误文案，不抛异常：mods 列表缺失不该让整份报告导不出去。
    ///
    /// <c>internal</c> 供测试直接驱动：本方法依赖 <see cref="IInstanceContext"/>，留在 App 层；
    /// 返回值按文件名不区分大小写排序，导出服务与测试都不需要再排一次。
    /// </summary>
    internal async Task<(IReadOnlyList<ModSnapshot> Mods, string? Error)> CollectModsAsync()
    {
        if (_instanceContext is null)
        {
            return (Array.Empty<ModSnapshot>(), "读取 mods 目录失败：实例上下文尚未就绪。");
        }

        string gameDirectory;

        try
        {
            gameDirectory = _instanceContext.GameDirectory;
        }
        catch (Exception ex)
        {
            return (Array.Empty<ModSnapshot>(), $"读取 mods 目录失败：{ex.Message}");
        }

        var modsDirectory = Path.Combine(gameDirectory, "mods");

        // 同名文件占住了 mods 路径：这不是「没有模组」，而是目录结构被破坏，必须给出文案。
        if (File.Exists(modsDirectory))
        {
            return (Array.Empty<ModSnapshot>(), $"读取 mods 目录失败：{modsDirectory} 不是目录。");
        }

        if (!Directory.Exists(modsDirectory))
        {
            return (Array.Empty<ModSnapshot>(), null);
        }

        // 优先用内容服务：它能从 jar 元数据里解析版本号，列表比裸文件名更有用。
        if (_content is not null)
        {
            try
            {
                var items = await _content
                    .ScanAsync(gameDirectory, ContentKind.Mod)
                    .ConfigureAwait(true);

                var snapshots = items
                    .Select(item => new ModSnapshot(
                        item.FileName,
                        item.IsEnabled,
                        item.FileSize,
                        string.IsNullOrWhiteSpace(item.Version) ? null : item.Version))
                    .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return (snapshots, null);
            }
            catch (Exception ex)
            {
                return (Array.Empty<ModSnapshot>(), $"读取 mods 目录失败：{ex.Message}");
            }
        }

        // 没有内容服务（组合根未合并 / 单测）时退化为直接枚举：至少给出文件名、启用态与大小。
        try
        {
            var snapshots = new List<ModSnapshot>();

            foreach (var file in Directory.EnumerateFiles(modsDirectory))
            {
                var name = Path.GetFileName(file);
                bool enabled;

                if (name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                }
                else if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = true;
                }
                else
                {
                    continue;
                }

                long size;

                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // 文件在枚举后被移走：保留条目不写大小。
                    size = 0;
                }

                snapshots.Add(new ModSnapshot(name, enabled, size));
            }

            return (snapshots
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList(), null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<ModSnapshot>(), $"读取 mods 目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 系统快照输入。字段集合与设置页的「复制诊断信息」一致（同一个 <see cref="DiagnosticsSnapshot"/>），
    /// 这里只补设置页拿不到的两项：游戏版本与加载器。
    /// </summary>
    private DiagnosticsInput BuildDiagnosticsInput()
    {
        string? minecraftVersion = null;
        string? loader = null;

        if (_instanceContext is not null)
        {
            try
            {
                minecraftVersion = _instanceContext.MinecraftVersion;
                loader = _instanceContext.Loader.ToString();
            }
            catch (Exception)
            {
                // 实例上下文异常不影响导出：这两项只是附加信息。
            }
        }

        return new DiagnosticsInput
        {
            LauncherVersion = DiagnosticsSnapshot.DisplayVersionOf(typeof(HomeViewModel).Assembly),
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            PlatformName = _platform?.PlatformName ?? "未知",
            DataDirectory = _paths?.RootDirectory,
            GameDirectory = SafeGameDirectory(),
            LogFilePath = SafeLogFilePath(),
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
        };
    }

    private string? SafeGameDirectory()
    {
        try
        {
            var directory = _instanceContext?.GameDirectory;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }
        catch (Exception)
        {
            // 退回全局目录。
        }

        try
        {
            return _paths?.MinecraftDirectory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? SafeLogFilePath()
    {
        try
        {
            return _paths is null ? null : Path.Combine(_paths.LogsDirectory, "launcher-YYYYMMDD.log");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeDirectoryOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ShortFileName(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch (Exception)
        {
            return path;
        }
    }

    // ==================== 命令 ====================

    [RelayCommand]
    private void NavigateTo(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_navigation is null)
        {
            Notice = "导航服务未就绪，无法跳转页面。";
            return;
        }

        _navigation.Navigate(key);
    }

    [RelayCommand]
    private async Task OpenGameFolderAsync()
    {
        var directory = ResolveGameDirectory(SelectedItem?.Info.Name);
        if (_platform is null || directory is null)
        {
            Notice = "打开目录需要平台服务，当前未就绪。";
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            await _platform.OpenFolderAsync(directory);
        }
        catch (Exception ex)
        {
            ErrorText = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 在文件管理器里定位本次崩溃的报告文件。
    /// 路径缺失或平台服务未注册时静默禁用（<see cref="HasCrashReportPath"/> 控制可见性）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenCrashReport))]
    private async Task OpenCrashReportAsync()
    {
        var path = CrashReportPath;

        if (string.IsNullOrWhiteSpace(path) || _platform is null)
        {
            Notice = "本次崩溃没有定位到报告文件，请打开游戏目录下的 crash-reports 翻看。";
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                Notice = $"崩溃报告已被移动或删除：{path}";
                return;
            }

            await _platform.RevealInFileManagerAsync(path);
        }
        catch (Exception ex)
        {
            ErrorText = $"打开崩溃报告失败：{ex.Message}";
        }
    }

    private bool CanOpenCrashReport() =>
        _platform is not null && !string.IsNullOrWhiteSpace(CrashReportPath);

    /// <summary>打开当前实例（或全局 .minecraft）下的 mods 目录，目录不存在时先建再打开。</summary>
    [RelayCommand]
    private async Task OpenModsFolderAsync()
    {
        var directory = ResolveGameDirectory(SelectedItem?.Info.Name);

        if (_platform is null || directory is null)
        {
            Notice = "打开 mods 目录需要平台服务，当前未就绪。";
            return;
        }

        var modsDirectory = Path.Combine(directory, "mods");

        try
        {
            Directory.CreateDirectory(modsDirectory);
            await _platform.RevealInFileManagerAsync(modsDirectory);
        }
        catch (Exception ex)
        {
            ErrorText = $"打开 mods 目录失败：{ex.Message}";
        }
    }

    // ==================== 崩溃建议的动作 ====================
    // Core 的 CrashAnalyzer 给每条建议都带了 ActionId，界面层此前一直没接。
    // 这一段就是接线：把一处点击翻译成导航 / 打开目录 / 修版本 / 禁用模组。

    /// <summary>把 Core 的建议包成带动作按钮的条目。填充点有四处，统一走这里。</summary>
    private CrashSuggestionItem WrapSuggestion(CrashSuggestion suggestion) =>
        new(suggestion, RunCrashActionAsync);

    /// <summary>
    /// 执行一条崩溃建议自带动作。
    ///
    /// 不用弹窗报告结果：这类动作的结果都能在界面上直接看到（目录开了、版本在修、模组变成 .old），
    /// 弹一个「已执行」只会多一次点击。失败写进 <see cref="ErrorText"/> / <see cref="Notice"/>。
    /// </summary>
    internal async Task RunCrashActionAsync(CrashSuggestion suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion.ActionId))
        {
            return;
        }

        try
        {
            switch (suggestion.ActionId)
            {
                case CrashAnalyzer.Actions.OpenJavaSettings:
                    // 只跳到设置页：「Java」分区在设置页左栏第一屏就能看到，
                    // 为此给导航服务加「锚点」参数属于过度设计。
                    NavigateTo("settings");
                    Notice = "Java 设置：设置 → Java，可切换已安装的运行时或填自定义路径。";
                    break;

                case CrashAnalyzer.Actions.OpenModsFolder:
                    await OpenModsFolderAsync().ConfigureAwait(true);
                    break;

                case CrashAnalyzer.Actions.OpenLogFolder:
                    await RevealGameSubfolderAsync("logs").ConfigureAwait(true);
                    break;

                case CrashAnalyzer.Actions.OpenCrashReport:
                    await OpenCrashReportAsync().ConfigureAwait(true);
                    break;

                case CrashAnalyzer.Actions.RepairVersion:
                    await RepairSelectedVersionAsync().ConfigureAwait(true);
                    break;

                case CrashAnalyzer.Actions.DisableMod:
                    await DisableModFileAsync(suggestion.ActionArgument).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"执行建议失败：{ex.Message}";
        }
    }

    /// <summary>打开游戏目录下的某个子目录（logs / crash-reports 这类），不存在时先建。</summary>
    private async Task RevealGameSubfolderAsync(string name)
    {
        var directory = ResolveGameDirectory(SelectedItem?.Info.Name);

        if (_platform is null || directory is null)
        {
            Notice = "打开目录需要平台服务，当前未就绪。";
            return;
        }

        var target = Path.Combine(directory, name);
        Directory.CreateDirectory(target);
        await _platform.RevealInFileManagerAsync(target);
    }

    /// <summary>
    /// 对「当前选中的版本」做一次快速修复（只补缺失文件与 0 字节 jar，不重下整个版本）。
    /// 崩溃建议里的「修复该版本」在 90% 情况下对应的是文件被删 / 下载中断，快速修复够用。
    /// </summary>
    private async Task RepairSelectedVersionAsync()
    {
        var versionName = SelectedItem?.Info.Name;

        if (_install is null || string.IsNullOrWhiteSpace(versionName))
        {
            Notice = "还没有选中版本（或安装服务未就绪），无法修复。";
            return;
        }

        Notice = $"正在修复 {versionName}…";
        var report = await _install.RepairAsync(versionName, RepairDepth.Quick).ConfigureAwait(true);

        if (report.Failed.Count > 0)
        {
            Notice = $"修复 {versionName} 未完成：{string.Join("；", report.Failed.Take(3))}";
        }
        else if (report.ChangedAnything)
        {
            Notice = $"已修复 {versionName}：补下 {report.Redownloaded} 个文件，修正 {report.Fixed.Count} 个。";
        }
        else
        {
            Notice = $"{versionName} 文件完整，不需要修复。";
        }
    }

    /// <summary>
    /// 禁用崩溃归因定位到的那个 jar（改扩展名，与模组页的开关同一条链路）。
    ///
    /// 找不到对应文件时**不静默**：可能是报告来自别的机器（导入的崩溃报告就是这种情况），
    /// 也可能是玩家已经手动删了。两种情况都要说出来，否则按钮点了没反应最容易被当成 bug。
    /// </summary>
    private async Task DisableModFileAsync(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            Notice = "这条建议没有给出具体文件名，请打开模组目录手动处理。";
            return;
        }

        var directory = ResolveGameDirectory(SelectedItem?.Info.Name);

        if (_content is null || directory is null)
        {
            Notice = "模组服务未就绪，无法自动禁用。";
            return;
        }

        var items = await _content.ScanAsync(directory, ContentKind.Mod).ConfigureAwait(true);

        var target = items.FirstOrDefault(item =>
            string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            Notice = $"模组目录里找不到 {fileName}，可能已被删除，或这份报告来自另一台机器。";
            return;
        }

        if (!target.IsEnabled)
        {
            Notice = $"{target.FileName} 已经是禁用状态。";
            return;
        }

        await _content.SetEnabledAsync(target, false).ConfigureAwait(true);

        Notice = $"已禁用 {target.FileName}，重新启动一次看看崩溃是否消失。" +
                 " 若仍然崩溃，说明归因不准，请把该文件改回来再换下一个候选。";
    }

    [RelayCommand]
    private async Task LaunchSelectedAsync(HomeVersionItem? item)
    {
        if (item is not null)
        {
            SelectedItem = item;
        }

        await LaunchAsync();
    }

    // ==================== 工具 ====================

    private string? DescribeMissingServices()
    {
        var missing = new List<string>();
        if (_accounts is null)
        {
            missing.Add("账户");
        }

        if (_versions is null)
        {
            missing.Add("版本");
        }

        if (_launcher is null)
        {
            missing.Add("启动");
        }

        if (_content is null)
        {
            missing.Add("内容");
        }

        if (_backups is null)
        {
            missing.Add("备份");
        }

        if (_paths is null)
        {
            missing.Add("路径");
        }

        return missing.Count == 0
            ? null
            : $"后端服务未注册：{string.Join("、", missing)}。页面可以浏览，但数据为空（等待组合根合并）。";
    }

    private static string ShortUuid(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return "—";
        }

        return uuid.Length <= 8 ? uuid : uuid[..8] + "…";
    }

    /// <summary>只读遍历目录大小。用于首页「磁盘占用」磁贴，失败时返回 0 而不是抛。</summary>
    private static long MeasureDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                    // 文件在遍历期间被删除：跳过。
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读：按 0 处理，不影响其他统计。
        }

        return total;
    }
}

/// <summary>首页「最近使用」列表的一行。把 Core 模型转成可直接绑定的展示字段，避免 XAML 里处理 null。</summary>
public sealed class HomeVersionItem
{
    public HomeVersionItem(VersionInfo info)
    {
        Info = info;
        Name = info.Name;
        LoaderText = DescribeLoader(info.Loader);
        ChannelText = info.Channel switch
        {
            VersionChannel.Snapshot => "快照",
            VersionChannel.OldBeta => "旧版 Beta",
            VersionChannel.OldAlpha => "旧版 Alpha",
            VersionChannel.Special => "愚人节",
            _ => "正式",
        };
        Detail = $"{DescribeLastPlayed(info.LastPlayedAt)} · {DescribeSize(info.SizeOnDisk)}";
        IsBroken = !string.IsNullOrEmpty(info.BrokenReason);
    }

    public VersionInfo Info { get; }

    public string Name { get; }

    public string LoaderText { get; }

    public string ChannelText { get; }

    public string Detail { get; }

    public bool IsBroken { get; }

    private static string DescribeLoader(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Quilt => "Quilt",
        _ => "原版",
    };

    /// <summary>「最近使用」第二行的措辞。从未启动时不要拼出「最后游玩 从未 · —」这种半截句子。</summary>
    private static string DescribeLastPlayed(DateTimeOffset? when)
    {
        if (when is null)
        {
            return "从未启动";
        }

        var delta = DateTimeOffset.Now - when.Value;
        if (delta < TimeSpan.Zero || delta < TimeSpan.FromMinutes(1))
        {
            return "刚刚玩过";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} 分钟前玩过";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} 小时前玩过";
        }

        if (delta < TimeSpan.FromDays(30))
        {
            return $"{(int)delta.TotalDays} 天前玩过";
        }

        return $"玩过 {when.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    private static string DescribeSize(long bytes) => bytes < 0 ? "大小未知" : Conv.FormatSize(bytes);
}
