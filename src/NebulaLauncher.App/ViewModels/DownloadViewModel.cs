using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;
using NebulaLauncher.Services;
using Conv = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 下载安装页：左侧远程版本清单，右侧加载器与安装配置，安装过程用环形进度展示。
///
/// 构造参数全部可空且带默认值，理由同 <see cref="HomeViewModel"/>。
/// </summary>
public partial class DownloadViewModel : PageViewModelBase
{
    /// <summary>任务面板一次最多渲染的行数。与 Core 的快照上限同源，避免两处数字漂移。</summary>
    public const int MaxRenderedTasks = DownloadQueue.MaxSnapshotTasks;

    private readonly IVersionService? _versions;
    private readonly IInstallService? _install;
    private readonly ISettingsService? _settings;
    private readonly INavigationService? _navigation;
    private readonly ILauncherPaths? _paths;

    /// <summary>会话级下载源状态。null 表示组合根没注册（预览/单元宿主），状态行退化成按设置显示。</summary>
    private readonly IDownloadSourceProvider? _sourceProvider;

    /// <summary>下载队列。null 表示组合根没注册（预览/单元宿主），面板退化成空态而不报错。</summary>
    private readonly DownloadQueue? _queue;

    /// <summary>服务端（server.jar）下载。null 表示组合根没注册，服务端区块显示「服务未就绪」。</summary>
    private readonly IServerDownloadService? _serverDownloads;

    /// <summary>复制启动命令用。null 时复制按钮报「平台服务未就绪」。</summary>
    private readonly IPlatformService? _platform;

    /// <summary>
    /// 在线资源库。只用于「同时安装 Fabric API」时查可用版本。
    /// null 表示组合根没注册（预览/单元宿主），开关直接隐藏。
    /// </summary>
    private readonly IModrinthService? _modrinth;

    /// <summary>
    /// 源测速。只用于下载页顶部的一行提示：不参与下载决策、不改设置、不落盘。
    /// null 表示组合根没注册（预览/单元宿主），按钮置灰。
    /// </summary>
    private readonly IDownloadSourceProbe? _sourceProbe;

    /// <summary>键 → 行模型。增量同步时复用行对象，避免每 200ms 重建列表把展开状态和滚动位置冲掉。</summary>
    private readonly Dictionary<string, DownloadTaskRow> _taskRows = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _installCts;
    private int _loaderRequestId;

    /// <summary>测速的取消源：离开页面或再次点「源测速」时取消上一次。</summary>
    private CancellationTokenSource? _probeCts;
    private bool _loadGate;
    private string _subtitle = string.Empty;

    public DownloadViewModel(
        IVersionService? versionService = null,
        IInstallService? installService = null,
        ISettingsService? settingsService = null,
        INavigationService? navigationService = null,
        ILauncherPaths? launcherPaths = null,
        DownloadQueue? downloadQueue = null,
        IDownloadSourceProvider? downloadSourceProvider = null,
        IServerDownloadService? serverDownloadService = null,
        IPlatformService? platformService = null,
        IModrinthService? modrinthService = null,
        IDownloadSourceProbe? downloadSourceProbe = null)
    {
        _versions = versionService;
        _install = installService;
        _settings = settingsService;
        _navigation = navigationService;
        _paths = launcherPaths;
        _queue = downloadQueue;
        _sourceProvider = downloadSourceProvider;
        _serverDownloads = serverDownloadService;
        _platform = platformService;
        _modrinth = modrinthService;
        _sourceProbe = downloadSourceProbe;

        ServiceWarning = DescribeMissingServices();
        UpdateInstallPlan();
        RefreshSourceStatus();

        // 截图模式预选加载器：加载器胶囊只能点，而 Fabric API 区块 / 推荐版徽标只在
        // 特定加载器下出现（详见 ScreenshotMode.LoaderKey 的说明）。
        // 这里放在构造尾部：SelectedLoader 的 setter 会触发一次 RefreshLoaderVersionsAsync，
        // 此时 SelectedRow 还是 null（版本列表尚未加载），等列表加载完设置首行时还会再刷一次。
        if (ScreenshotMode.LoaderKey is { Length: > 0 } loaderKey)
        {
            // 必须走 SetLoader：只赋 SelectedLoader 不会更新 IsLoaderXxx，
            // 界面上的加载器胶囊会停在「原版」高亮上（内容区却已经是 Fabric 的配置）。
            SetLoader(ParseLoaderKey(loaderKey));
        }

        // 自动模式的降级可能发生在下载线程：状态行必须切回 UI 线程再改。
        if (_sourceProvider is not null)
        {
            _sourceProvider.Changed += (_, _) => PostToUi(RefreshSourceStatus);
        }

        // 手动切档位时策略变化不经过 governor 的降级事件，只在保存后由设置变更通知过来。
        if (_settings is not null)
        {
            _settings.Changed += (_, _) => PostToUi(RefreshSourceStatus);
        }

        if (_queue is not null)
        {
            _queue.TasksChanged += OnQueueTasksChanged;
            _queue.BatchCompleted += OnQueueBatchCompleted;

            // 页面可能在批次进行中才第一次被打开：先把现有快照与历史铺上。
            SyncTasks(_queue.Snapshot());
            LoadHistory();
        }
        else
        {
            UpdateTaskPanelState();
        }
    }

    public override string Title => "下载";

    public override string? Subtitle => _subtitle;

    /// <summary>把 <c>--loader</c> 的取值映射成 <see cref="LoaderKind"/>，无法识别时回落到原版。</summary>
    private static LoaderKind ParseLoaderKey(string key) => key.Trim().ToLowerInvariant() switch
    {
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        _ => LoaderKind.Vanilla,
    };

    public override string Key => NavigationKeys.Download;

    // ---------------- 下载源状态 ----------------

    /// <summary>
    /// 页面顶部状态行：「下载源：自动 · 当前 BMCLAPI」/「下载源：自动 · 已降级到官方（原因）」。
    /// 手动档只写档位，不带降级信息——降级是自动模式独有的行为。
    /// </summary>
    [ObservableProperty]
    private string? _sourceStatusText;

    /// <summary>自动模式且当前处于降级窗口：状态行改用警示色，让「怎么突然变慢了」有解释。</summary>
    [ObservableProperty]
    private bool _isSourceDegraded;

    /// <summary>
    /// 手动镜像档的劣化提示（来自 governor 的 <c>ManualDegradeHint</c>）。
    ///
    /// 与 <see cref="IsSourceDegraded"/> 刻意分成两条通道：手动档**没有**换源，
    /// 若把它塞进降级状态，状态行会说「已降级到某源」而实际上一个字节都没换，
    /// 用户按这句话去查日志只会更困惑。这条只说明「镜像看起来坏了，要不要自己改设置」。
    /// </summary>
    [ObservableProperty]
    private string? _sourceManualHint;

    /// <summary>
    /// 手动镜像档且镜像正在连续失败：状态点改用警示色。
    ///
    /// 与 <see cref="IsSourceDegraded"/> 并列而不是复用它：手动档没有换源，
    /// 用「已降级」那套措辞会让用户去找一个不存在的自动切换记录。
    /// </summary>
    public bool IsSourceManualHint => !string.IsNullOrEmpty(SourceManualHint);

    /// <summary>未降级（或手动档）：状态点用中性色。视图里不引用取反转换器。</summary>
    public bool IsSourceNormal => !IsSourceDegraded && !IsSourceManualHint;

    partial void OnIsSourceDegradedChanged(bool value) => OnPropertyChanged(nameof(IsSourceNormal));

    partial void OnSourceManualHintChanged(string? value)
    {
        OnPropertyChanged(nameof(IsSourceManualHint));
        OnPropertyChanged(nameof(IsSourceNormal));
    }

    /// <summary>全部远程版本。</summary>
    public ObservableCollection<RemoteVersionRow> Versions { get; } = new();

    /// <summary>过滤后绑定到列表的版本。</summary>
    public ObservableCollection<RemoteVersionRow> VisibleVersions { get; } = new();

    /// <summary>当前 Minecraft 版本可用的加载器版本（仅 Fabric / Quilt 有远程列表）。</summary>
    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = new();

    // ---------------- 列表状态 ----------------
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _serviceWarning;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isOffline;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _channelFilter = FilterRelease;

    [ObservableProperty]
    private bool _isFilterRelease = true;

    [ObservableProperty]
    private bool _isFilterSnapshot;

    /// <summary>远古版（old_beta / old_alpha）。PCL2 把两者合成一档。</summary>
    [ObservableProperty]
    private bool _isFilterOld;

    /// <summary>愚人节版（<see cref="VersionChannel.Special"/>）。</summary>
    [ObservableProperty]
    private bool _isFilterSpecial;

    [ObservableProperty]
    private bool _isFilterAllChannels;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private RemoteVersionRow? _selectedRow;

    [ObservableProperty]
    private bool _isSelectedInstalled;

    /// <summary>右上角状态徽标文案。之前固定写「已安装」，未安装的版本也会显示成已安装。</summary>
    [ObservableProperty]
    private string _selectedStateText = "未安装";

    [ObservableProperty]
    private string _selectedTitle = "未选择版本";

    [ObservableProperty]
    private string _selectedDetail = "在左侧选择一个 Minecraft 版本";

    // ---------------- 加载器 ----------------
    [ObservableProperty]
    private LoaderKind _selectedLoader = LoaderKind.Vanilla;

    [ObservableProperty]
    private bool _isLoaderVanilla = true;

    [ObservableProperty]
    private bool _isLoaderFabric;

    [ObservableProperty]
    private bool _isLoaderQuilt;

    [ObservableProperty]
    private bool _isLoaderForge;

    [ObservableProperty]
    private bool _isLoaderNeoForge;

    [ObservableProperty]
    private bool _hasLoaderVersions;

    [ObservableProperty]
    private bool _isLoadingLoaderVersions;

    [ObservableProperty]
    private LoaderVersion? _selectedLoaderVersion;

    [ObservableProperty]
    private string _loaderHint = "原版安装不涉及加载器。";

    // ---------------- Fabric API ----------------

    /// <summary>
    /// 「同时安装 Fabric API」。默认开：Fabric 生态里绝大多数模组依赖它，
    /// 漏装的症状是启动时 <c>NoClassDefFoundError</c>，日志里那行类名对新手毫无指向性
    /// （PCL2 <c>PageDownloadInstall.xaml.vb:1014-1019</c> 同样默认勾选）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFabricApiOption))]
    private bool _installFabricApi = true;

    /// <summary>
    /// 是否显示 Fabric API 区块：只有选了 Fabric 才有意义。
    ///
    /// 判据用 <see cref="SelectedLoader"/> 而不是 <see cref="IsLoaderFabric"/>：
    /// 后者由 <c>SyncLoaderFlags</c> 在属性回调里反向同步，时序上比前者慢一拍，
    /// 拿它做可见性会在切换的瞬间闪一下。
    /// </summary>
    public bool ShowFabricApiOption => SelectedLoader == LoaderKind.Fabric;

    /// <summary>Fabric API 区块的说明行。查不到版本时改写原因。</summary>
    [ObservableProperty]
    private string _fabricApiHint = "Fabric API 是绝大多数 Fabric 模组的前置，装 Fabric 时一并装好。";

    /// <summary>候选 Fabric API 版本（按发布时间降序，与 Modrinth 返回一致）。</summary>
    [ObservableProperty]
    private IReadOnlyList<ModrinthVersion> _fabricApiVersions = Array.Empty<ModrinthVersion>();

    /// <summary>
    /// 实际会装的那个 Fabric API 版本。null 表示没查到 / 查失败，此时即使开关打开也不会装。
    /// 不直接暴露给 XAML：界面上只显示版本号文案，不需要让用户从一串版本里挑。
    /// </summary>
    private ModrinthVersion? _selectedFabricApiVersion;

    /// <summary>界面上的版本号文案，例如 <c>0.92.2+1.20.1</c>。</summary>
    [ObservableProperty]
    private string? _fabricApiVersionText;

    /// <summary>
    /// 查当前 Minecraft 版本可用的 Fabric API。失败只改提示文案，不弹错误：
    /// 它是个可选项，查不到时用户的正确做法是「直接安装」，不该被拦在一个可选前置上。
    /// </summary>
    private async Task RefreshFabricApiAsync(int requestId, CancellationToken cancellationToken)
    {
        _selectedFabricApiVersion = null;
        FabricApiVersions = Array.Empty<ModrinthVersion>();
        FabricApiVersionText = null;

        if (!ShowFabricApiOption)
        {
            FabricApiHint = "Fabric API 是绝大多数 Fabric 模组的前置，装 Fabric 时一并装好。";
            return;
        }

        if (_modrinth is null)
        {
            FabricApiHint = "在线资源服务未就绪，无法自动安装 Fabric API。";
            return;
        }

        var minecraftVersion = SelectedRow?.Id;

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            FabricApiHint = "先选择一个 Minecraft 版本。";
            return;
        }

        try
        {
            var list = await _modrinth
                .GetVersionsAsync(InstallService.FabricApiSlug, minecraftVersion, LoaderKind.Fabric, cancellationToken)
                .ConfigureAwait(true);

            if (requestId != _loaderRequestId)
            {
                return;
            }

            FabricApiVersions = list;

            if (list.Count == 0)
            {
                FabricApiHint = $"Modrinth 上没有与 Minecraft {minecraftVersion} 兼容的 Fabric API，这一项已自动跳过。";
                return;
            }

            // 列表已按发布时间降序：第一条就是最新。与 PCL2 的 AutoSelectedFabricApi 同一取舍。
            _selectedFabricApiVersion = list[0];
            FabricApiVersionText = list[0].VersionNumber;
            FabricApiHint = $"将安装 Fabric API {list[0].VersionNumber}（最新兼容版本）。";
        }
        catch (OperationCanceledException)
        {
            // 切换版本 / 加载器导致的取消：忽略。
        }
        catch (Exception ex)
        {
            if (requestId == _loaderRequestId)
            {
                FabricApiHint = $"查询 Fabric API 失败：{ex.Message}";
            }
        }
    }

    // ---------------- 安装进度 ----------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClientProgress))]
    private bool _hasProgress;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "0%";

    [ObservableProperty]
    private string _currentFile = "—";

    /// <summary>
    /// 当前安装阶段（「正在执行 Forge 安装器…」这类文案）。
    /// 只有 Forge / NeoForge 这类分阶段安装会给值，普通下载为 null（状态行随之隐藏）。
    /// </summary>
    [ObservableProperty]
    private string? _stageText;

    [ObservableProperty]
    private string _speedText = "—";

    [ObservableProperty]
    private string _etaText = "—";

    [ObservableProperty]
    private string _filesText = string.Empty;

    [ObservableProperty]
    private string? _statusText;

    /// <summary>安装目标目录（versions/&lt;版本名&gt;），给右侧面板做确认信息。</summary>
    [ObservableProperty]
    private string _installTarget = "—";

    /// <summary>一行安装摘要：Minecraft 版本 · 加载器 · 下载源。</summary>
    [ObservableProperty]
    private string _installSummary = "—";

    // ---------------- 服务端下载 ----------------

    /// <summary>右侧详情区的分段：客户端安装 / 服务端下载。两者互斥，由 partial 方法保持同步。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClientProgress))]
    private bool _isClientMode = true;

    [ObservableProperty]
    private bool _isServerMode;

    /// <summary>可下载服务端的版本（只有带 <c>downloads.server</c> 的版本）。</summary>
    public ObservableCollection<ServerVersionRow> ServerVersions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadServerCommand))]
    private ServerVersionRow? _selectedServerVersion;

    /// <summary>目标目录。默认 <c>&lt;data&gt;/servers/&lt;id&gt;</c>，用户可改（TextBox 可编辑）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadServerCommand))]
    private string _serverDirectory = string.Empty;

    [ObservableProperty]
    private bool _isLoadingServerVersions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadServerCommand))]
    private bool _isDownloadingServer;

    [ObservableProperty]
    private double _serverProgress;

    [ObservableProperty]
    private string _serverProgressText = string.Empty;

    [ObservableProperty]
    private string? _serverResultText;

    [ObservableProperty]
    private string? _serverErrorText;

    /// <summary>启动命令提示（不含 eula，用户要自己同意）。</summary>
    [ObservableProperty]
    private string _serverCommand = string.Empty;

    /// <summary>是否已经从清单里拿到过服务端版本，用于空态与「重试加载」按钮。</summary>
    [ObservableProperty]
    private bool _hasServerVersions;

    [ObservableProperty]
    private bool _hasServerListError;

    /// <summary>
    /// 清单来源提示：「正在读取版本清单（首次约 5–30 秒）…」/「来自缓存（3 分钟前）」/
    /// 「正在后台更新清单…」/「已更新（N 个版本）」。
    /// </summary>
    [ObservableProperty]
    private string _serverListStatusText = string.Empty;

    /// <summary>服务内部正在做 stale-while-revalidate 的后台刷新。</summary>
    [ObservableProperty]
    private bool _isRefreshingServerVersions;

    /// <summary>当前列表来自落盘/内存缓存（而不是本次扫描）。</summary>
    [ObservableProperty]
    private bool _isServerListFromCache;

    /// <summary>「扫描全部版本」的二次确认是否展开。</summary>
    [ObservableProperty]
    private bool _isConfirmingFullScan;

    [ObservableProperty]
    private bool _isScanningAllVersions;

    // ---------------- 任务面板 ----------------

    /// <summary>逐文件任务行（已按 Group 分组、已截断）。</summary>
    public ObservableCollection<DownloadTaskRow> Tasks { get; } = new();

    /// <summary>批次历史，最新的在前。</summary>
    public ObservableCollection<BatchHistoryRow> History { get; } = new();

    [ObservableProperty]
    private bool _hasTasks;

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private bool _hasFailures;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private int _failedTaskCount;

    [ObservableProperty]
    private string _taskPanelNote = string.Empty;

    [ObservableProperty]
    private string _failedBadgeText = "0 个失败";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private bool _isRetrying;

    [ObservableProperty]
    private string? _retryNote;

    // ---------------- 暂停 / 继续 ----------------

    /// <summary>当前是否有活动批次。暂停按钮只在有批次时可点，否则「暂停一个不存在的批次」没有意义。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    private bool _hasActiveBatch;

    /// <summary>
    /// 活动批次是否是安装链的外部批次。外部批次由安装器自己调度，闸门对它没有意义，
    /// 所以按钮禁用并给出一句说明，而不是让用户点下去发现没反应。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    [NotifyPropertyChangedFor(nameof(PauseUnavailableHint))]
    private bool _isExternalBatchActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    [NotifyPropertyChangedFor(nameof(PauseStatusText))]
    private bool _isPaused;

    /// <summary>按钮文案：暂停中显示「继续」，否则「暂停」。</summary>
    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    /// <summary>暂停中的状态行文案；null 表示不显示。</summary>
    public string? PauseStatusText => IsPaused ? "已暂停：在途文件完成后停止" : null;

    /// <summary>安装批次不可暂停时的说明；null 表示不显示。</summary>
    public string? PauseUnavailableHint => IsExternalBatchActive && !IsPaused
        ? "安装批次由安装器自己调度，不能暂停；等安装结束后再用「暂停」。"
        : null;

    /// <summary>
    /// 截图演示中的伪批次。只在 <see cref="ScreenshotMode.DownloadDemo"/> 下为 true，
    /// 让面板看起来「有活动批次」——截图模式没有点击能力，也没有真实下载可跑。
    /// </summary>
    private bool _isDemoBatch;

    // ---------------- 源测速（只提示，不决策） ----------------

    /// <summary>测速进行中：按钮禁用并显示「测速中…」。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeSourceCommand))]
    [NotifyPropertyChangedFor(nameof(ProbeButtonText))]
    private bool _isProbingSource;

    /// <summary>上一次测速的一行结论；null 表示本次会话还没测过。</summary>
    [ObservableProperty]
    private string? _sourceProbeSummary;

    /// <summary>两侧都失败：结果行用警示色，让「都连不上」与「两侧差不多」区分得开。</summary>
    [ObservableProperty]
    private bool _isSourceProbeFailed;

    /// <summary>按钮文案：测速中禁用并改文案，避免连点叠出多组请求。</summary>
    public string ProbeButtonText => IsProbingSource ? "测速中…" : "源测速";

    /// <summary>
    /// 结果行按「正常 / 失败」拆成两个只读文本：视图用不同令牌着色，
    /// 而 <see cref="Converters.Converters.HasValue"/> 只看有没有文本，null 时不占位（不留空行）。
    /// 拆两个属性而不是加转换器：项目里没有「与」组合转换器，VM 侧算清楚更省事。
    /// </summary>
    public string? SourceProbeNormalText => IsSourceProbeFailed ? null : SourceProbeSummary;

    /// <summary>两侧都失败时的结果行文本；正常或未测速时为 null。</summary>
    public string? SourceProbeFailedText => IsSourceProbeFailed ? SourceProbeSummary : null;

    partial void OnSourceProbeSummaryChanged(string? value)
    {
        OnPropertyChanged(nameof(SourceProbeNormalText));
        OnPropertyChanged(nameof(SourceProbeFailedText));
    }

    partial void OnIsSourceProbeFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(SourceProbeNormalText));
        OnPropertyChanged(nameof(SourceProbeFailedText));
    }

    /// <summary>
    /// 测一次官方与镜像，只更新本页的一行提示。
    ///
    /// 明确不做的事：不写 <c>settings.json</c>、不改 <see cref="IDownloadSourceProvider"/> 的当前源、
    /// 不往 governor 回灌样本、不落盘。用户看完结论要不要切换，仍走设置页的三档——
    /// 测速只负责把「哪个快」摆出来，替用户改配置会让「今天镜像慢」变成永久设置（§6.23）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanProbeSource))]
    private async Task ProbeSourceAsync()
    {
        if (_sourceProbe is null)
        {
            return;
        }

        // 连点：取消上一次未完成的测速，只保留最新一次的结果。
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var cts = _probeCts;

        IsProbingSource = true;

        try
        {
            var report = await _sourceProbe.ProbeAsync(cts.Token);

            if (ReferenceEquals(_probeCts, cts))
            {
                SourceProbeSummary = report.Summary;
                IsSourceProbeFailed = report.BothFailed;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面离开或再次点击：保留上一次结论，不清空、不报错。
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
            {
                IsProbingSource = false;
            }
        }
    }

    private bool CanProbeSource() => _sourceProbe is not null && !IsProbingSource;

    /// <summary>
    /// 截图专用：摆出一次成功的测速结论（走 Core 的真实排序与文案，不发请求）。
    /// 与 <see cref="ApplyDownloadDemo"/> 同理：截图模式不能点击，也没有可控网络，
    /// 没有这段就永远看不到结果行的真实排版。
    /// </summary>
    public void ApplySourceProbeDemo()
    {
        if (!ScreenshotMode.IsActive)
        {
            return;
        }

        var samples = new[]
        {
            new SourceProbeSample
            {
                Source = DownloadSource.Official,
                SourceName = SourceProbeRanking.DisplayName(DownloadSource.Official),
                Succeeded = true,
                FirstByteMs = 320,
                ThroughputKbps = 1800,
                BytesRead = 96 * 1024,
                TargetsOk = 2,
            },
            new SourceProbeSample
            {
                Source = DownloadSource.Mirror,
                SourceName = SourceProbeRanking.DisplayName(DownloadSource.Mirror),
                Succeeded = true,
                FirstByteMs = 190,
                ThroughputKbps = 2600,
                BytesRead = 96 * 1024,
                TargetsOk = 2,
            },
        };

        var decision = SourceProbeRanking.Decide(samples);
        SourceProbeSummary = SourceProbeRanking.Describe(samples, decision);
        IsSourceProbeFailed = decision.BothFailed;
    }

    /// <summary>客户端的聚合进度条只在「客户端分段 + 有进度」时显示；服务端区块有自己的进度条。</summary>
    public bool HasClientProgress => HasProgress && IsClientMode;

    public const string FilterRelease = "release";

    public const string FilterSnapshot = "snapshot";

    /// <summary>远古版：<c>old_beta</c> 与 <c>old_alpha</c> 合并一档（PCL2 的「远古版」）。</summary>
    public const string FilterOld = "old";

    /// <summary>愚人节版（PCL2 的「愚人节版」）。</summary>
    public const string FilterSpecial = "special";

    public override async Task OnNavigatedToAsync()
    {
        await LoadRemoteAsync();
    }

    /// <summary>安装流程与测速的取消源在页面被释放时收尾。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _installCts?.Cancel();
            _installCts?.Dispose();
            _installCts = null;

            // 测速最多 8 秒，页面释放后不该继续占着连接：离开页面即取消。
            _probeCts?.Cancel();
            _probeCts?.Dispose();
            _probeCts = null;
        }

        base.Dispose(disposing);
    }

    // ==================== 远程清单 ====================

    [RelayCommand]
    private async Task RefreshRemoteAsync() => await LoadRemoteAsync(forceRefresh: true);

    private async Task LoadRemoteAsync(bool forceRefresh = false)
    {
        if (_loadGate)
        {
            return;
        }

        _loadGate = true;
        IsLoading = true;
        ErrorText = null;
        IsOffline = false;

        try
        {
            if (_versions is null)
            {
                Versions.Clear();
                ApplyFilter();
                UpdateSubtitle();
                return;
            }

            IReadOnlyList<RemoteVersion> remote;
            try
            {
                remote = await _versions.GetRemoteAsync(forceRefresh);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                IsOffline = true;
                ErrorText = $"获取版本清单失败：{ex.Message}";
                return;
            }

            if (remote.Count == 0)
            {
                IsOffline = true;
                ErrorText = "无法获取版本清单。检查网络连接后点「重试」。";
                UpdateSubtitle();
                return;
            }

            var installed = await LoadInstalledNamesAsync();

            var previous = SelectedRow?.Id;
            Versions.Clear();
            foreach (var version in remote)
            {
                Versions.Add(new RemoteVersionRow(version, installed.Contains(version.Id)));
            }

            ApplyFilter();

            var restored = VisibleVersions.FirstOrDefault(v => string.Equals(v.Id, previous, StringComparison.Ordinal));

            // 截图模式指定了游戏版本时优先用它：Forge 的「推荐版」只在部分游戏版本上有，
            // 默认选中的最新正式版往往还没有 recommended 条目，截不到那个徽标。
            var pinned = ScreenshotMode.McVersion is { Length: > 0 } pinnedId
                ? VisibleVersions.FirstOrDefault(v => string.Equals(v.Id, pinnedId, StringComparison.Ordinal))
                : null;

            SelectedRow = pinned ?? restored ?? Versions.FirstOrDefault(v => v.IsRelease) ?? Versions.FirstOrDefault();
            UpdateSubtitle();
        }
        catch (Exception ex)
        {
            ErrorText = $"加载下载页失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _loadGate = false;
        }
    }

    private async Task<HashSet<string>> LoadInstalledNamesAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_versions is null)
        {
            return set;
        }

        try
        {
            var installed = await _versions.GetInstalledAsync();
            foreach (var info in installed)
            {
                set.Add(info.Name);
                if (!string.IsNullOrWhiteSpace(info.MinecraftVersion))
                {
                    set.Add(info.MinecraftVersion);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorText ??= $"读取已安装版本失败：{ex.Message}";
        }

        return set;
    }

    private void UpdateSubtitle()
    {
        var text = Versions.Count == 0 ? "等待版本清单" : $"{Versions.Count} 个可用版本";
        if (text == _subtitle)
        {
            return;
        }

        _subtitle = text;
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnChannelFilterChanged(string value)
    {
        IsFilterRelease = value == FilterRelease;
        IsFilterSnapshot = value == FilterSnapshot;
        IsFilterOld = value == FilterOld;
        IsFilterSpecial = value == FilterSpecial;
        IsFilterAllChannels = value == FilterAll;
        ApplyFilter();
    }

    public const string FilterAll = "all";

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        VisibleVersions.Clear();

        foreach (var row in Versions)
        {
            if (ChannelFilter == FilterRelease && !row.IsRelease)
            {
                continue;
            }

            if (ChannelFilter == FilterSnapshot && !row.IsSnapshot)
            {
                continue;
            }

            if (ChannelFilter == FilterOld && !row.IsOld)
            {
                continue;
            }

            if (ChannelFilter == FilterSpecial && !row.IsSpecial)
            {
                continue;
            }

            if (query.Length > 0 && !row.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            VisibleVersions.Add(row);
        }

        IsEmpty = VisibleVersions.Count == 0;
    }

    [RelayCommand]
    private void SelectChannelFilter(string? kind)
    {
        ChannelFilter = kind switch
        {
            FilterSnapshot => FilterSnapshot,
            FilterOld => FilterOld,
            FilterSpecial => FilterSpecial,
            FilterAll => FilterAll,
            _ => FilterRelease,
        };
    }

    partial void OnSelectedRowChanged(RemoteVersionRow? value)
    {
        if (value is null)
        {
            SelectedTitle = "未选择版本";
            SelectedDetail = "在左侧选择一个 Minecraft 版本";
            IsSelectedInstalled = false;
        }
        else
        {
            SelectedTitle = value.Id;
            SelectedDetail = $"{value.ChannelText} · {value.ReleaseTimeText}";
            IsSelectedInstalled = value.IsInstalled;
        }

        SelectedStateText = IsSelectedInstalled ? "已安装" : "未安装";

        _ = RefreshLoaderVersionsAsync();
        UpdateInstallPlan();
    }

    // ==================== 加载器 ====================

    /// <summary>
    /// ToggleButton 与 <see cref="SelectedLoader"/> 的双向同步。
    /// 用户再次点击已选中的胶囊会把它取消选中，这里强制回到选中态，保证始终有且只有一个加载器被选中。
    /// </summary>
    partial void OnIsLoaderVanillaChanged(bool value) => SyncLoaderFlags(LoaderKind.Vanilla, value);

    partial void OnIsLoaderFabricChanged(bool value) => SyncLoaderFlags(LoaderKind.Fabric, value);

    partial void OnIsLoaderQuiltChanged(bool value) => SyncLoaderFlags(LoaderKind.Quilt, value);

    partial void OnIsLoaderForgeChanged(bool value) => SyncLoaderFlags(LoaderKind.Forge, value);

    partial void OnIsLoaderNeoForgeChanged(bool value) => SyncLoaderFlags(LoaderKind.NeoForge, value);

    private void SyncLoaderFlags(LoaderKind loader, bool value)
    {
        if (!value && SelectedLoader != loader)
        {
            return;
        }

        SetLoader(loader);
    }

    private void SetLoader(LoaderKind loader)
    {
        SelectedLoader = loader;
        IsLoaderVanilla = loader == LoaderKind.Vanilla;
        IsLoaderFabric = loader == LoaderKind.Fabric;
        IsLoaderQuilt = loader == LoaderKind.Quilt;
        IsLoaderForge = loader == LoaderKind.Forge;
        IsLoaderNeoForge = loader == LoaderKind.NeoForge;
    }

    partial void OnSelectedLoaderChanged(LoaderKind value)
    {
        UpdateInstallPlan();
        OnPropertyChanged(nameof(ShowFabricApiOption));
        _ = RefreshLoaderVersionsAsync();
    }

    partial void OnSelectedLoaderVersionChanged(LoaderVersion? value) => UpdateInstallPlan();

    /// <summary>刷新右侧面板里的「安装到」与摘要，让用户在下单前知道会落到哪个目录。</summary>
    private void UpdateInstallPlan()
    {
        var version = SelectedRow?.Id;
        var loader = DescribeLoader(SelectedLoader);
        var loaderVersion = SelectedLoader == LoaderKind.Vanilla ? null : SelectedLoaderVersion?.Version;
        var source = DescribeInstallSource(
            _sourceProvider?.Effective ?? _settings?.Current.DownloadSource ?? DownloadSource.Mirror);

        var parts = new List<string>
        {
            version is null ? "未选择 Minecraft 版本" : $"Minecraft {version}",
            loaderVersion is null ? loader : $"{loader} {loaderVersion}",
            source,
        };

        InstallSummary = string.Join(" · ", parts);

        InstallTarget = version is null || _paths is null
            ? "—"
            : Path.Combine(_paths.VersionsDirectory, version);
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        var requestId = ++_loaderRequestId;

        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        HasLoaderVersions = false;

        // Fabric API 的候选版本与加载器版本互不依赖，但都必须跟着「当前选中的 MC 版本 / 加载器」重查。
        await RefreshFabricApiAsync(requestId, CancellationToken.None).ConfigureAwait(true);

        if (SelectedLoader == LoaderKind.Vanilla)
        {
            LoaderHint = "原版安装不涉及加载器，直接下载游戏文件。";
            return;
        }

        if (SelectedRow is null)
        {
            LoaderHint = "先选择一个 Minecraft 版本。";
            return;
        }

        if (_versions is null)
        {
            LoaderHint = "版本服务未就绪，无法获取加载器版本。";
            return;
        }

        IsLoadingLoaderVersions = true;
        try
        {
            var list = await _versions.GetLoaderVersionsAsync(SelectedLoader, SelectedRow.Id);
            if (requestId != _loaderRequestId)
            {
                return;
            }

            foreach (var item in list)
            {
                LoaderVersions.Add(item);
            }

            HasLoaderVersions = LoaderVersions.Count > 0;

            // 优先推荐版：Forge 的推荐构建是官方挑过的「能正常跑」的那个，
            // 而「最新构建」经常是坏的（PCL2 ModDownloadLib.vb:1434-1458 同样把推荐版排在首位）。
            SelectedLoaderVersion =
                LoaderVersions.FirstOrDefault(v => v.IsRecommended)
                ?? LoaderVersions.FirstOrDefault(v => v.Stable)
                ?? LoaderVersions.FirstOrDefault();

            LoaderHint = BuildLoaderHint();
        }
        catch (OperationCanceledException)
        {
            // 切换选择导致的取消：忽略。
        }
        catch (Exception ex)
        {
            if (requestId == _loaderRequestId)
            {
                LoaderHint = $"获取加载器版本失败：{ex.Message}";
            }
        }
        finally
        {
            if (requestId == _loaderRequestId)
            {
                IsLoadingLoaderVersions = false;
            }
        }
    }

    /// <summary>
    /// 加载器版本列表下方的说明行。三种加载器各有各的「没有列表」的含义，必须分开写：
    /// Fabric/Quilt 列表空 = 该游戏版本还没支持；Forge 列表空 = 没有官方推荐构建，但能装最新；
    /// NeoForge 永远不走这条分支（它的版本由安装器决定）。
    /// </summary>
    private string BuildLoaderHint()
    {
        var loader = DescribeLoader(SelectedLoader);

        if (!HasLoaderVersions)
        {
            return SelectedLoader switch
            {
                // NeoForge 的目录接口偶尔整族不可达（官方源本机超时、BMCLAPI 侧 5xx）。
                // 此时安装本身仍然可用——安装器会自己挑最新构建。
                LoaderKind.NeoForge =>
                    $"没取到 {SelectedRow?.Id} 的 NeoForge 构建列表，直接点「开始安装」会装该 MC 版本的最新构建。",
                LoaderKind.Forge =>
                    $"Forge 官方还没有给 Minecraft {SelectedRow?.Id} 标推荐构建，直接点「开始安装」会装最新构建。",
                _ => $"没有可用的 {loader} 版本，可能该 Minecraft 版本尚未被支持。",
            };
        }

        if (SelectedLoader == LoaderKind.Forge)
        {
            return $"已默认选中 Forge 官方推荐构建 {SelectedLoaderVersion?.Version}。";
        }

        if (SelectedLoader == LoaderKind.NeoForge)
        {
            return $"共 {LoaderVersions.Count} 个可安装构建，默认选最新稳定版（{SelectedLoaderVersion?.Version}）。";
        }

        return $"共 {LoaderVersions.Count} 个加载器版本，默认选稳定版。";
    }

    // ==================== 安装 ====================

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var row = SelectedRow;
        if (row is null)
        {
            return;
        }

        if (_install is null)
        {
            ErrorText = "安装服务未就绪，无法开始安装。";
            return;
        }

        if (SelectedLoader != LoaderKind.Vanilla &&
            SelectedLoader is LoaderKind.Fabric or LoaderKind.Quilt &&
            SelectedLoaderVersion is null)
        {
            ErrorText = "请先选择一个加载器版本。";
            return;
        }

        ErrorText = null;
        StatusText = null;
        IsInstalling = true;
        HasProgress = true;
        Progress = 0;
        ProgressText = "0%";
        CurrentFile = "准备中…";
        StageText = null;
        SpeedText = "—";
        EtaText = "—";
        FilesText = string.Empty;

        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();

        try
        {
            var request = new InstallRequest
            {
                MinecraftVersion = row.Id,
                Loader = SelectedLoader,
                LoaderVersion = SelectedLoader == LoaderKind.Vanilla ? null : SelectedLoaderVersion?.Version,
                // 传给安装器的是「本次会话解析后的具体源」：Installing 层按它选加载器 meta 地址，
                // 传 Auto 会让那边退化成官方优先。降级发生在下载过程中由 handler 自行切换，不受这里影响。
                Source = _sourceProvider?.Effective ?? _settings?.Current.DownloadSource ?? DownloadSource.Mirror,
            };

            var progress = new Progress<DownloadProgress>(ApplyDownloadProgress);

            // 只有 Fabric、开关打开、且确实查到了兼容版本时才带这个 Id：
            // 三者缺一都意味着「不装」，而给一个查不到的 Id 只会让 Core 多做一次网络请求再退化到首项。
            var fabricApiVersionId = SelectedLoader == LoaderKind.Fabric && InstallFabricApi
                ? _selectedFabricApiVersion?.Id
                : null;

            await _install.InstallAsync(request, progress, _installCts.Token, fabricApiVersionId);

            Progress = 100;
            ProgressText = "100%";
            CurrentFile = "安装完成";
            StageText = null;
            EtaText = "—";
            StatusText = $"已安装 {DescribeInstallName(row.Id)}。可以在「版本」页启动。";

            await MarkInstalledAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "安装已取消，未完成的文件会保留在缓存目录。";
            CurrentFile = "已取消";
            StageText = null;
        }
        catch (Exception ex)
        {
            // InstallService 已经在消息里写明「哪个版本 / 哪种失败 / 下一步怎么做」，
            // 这里再拼一次「安装失败：」会变成「安装失败：Fabric 26.3 安装失败：…」。
            ErrorText = ex.Message;
            CurrentFile = "安装失败";
            StageText = null;
        }
        finally
        {
            // 安装结束（成功 / 失败 / 取消）一律清掉任务栏进度与阶段文案。
            ShellProgress.Clear();
            StageText = null;

            IsInstalling = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private bool CanInstall() => !IsInstalling && !IsRetrying && SelectedRow is not null;

    [RelayCommand]
    private void CancelInstall()
    {
        try
        {
            _installCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经结束。
        }
    }

    private void ApplyDownloadProgress(DownloadProgress progress)
    {
        Progress = Math.Clamp(progress.Ratio * 100, 0, 100);
        ProgressText = $"{(int)Math.Round(Progress)}%";

        // 阶段文案只有 Forge / NeoForge 这类分阶段安装才给值；普通下载保持隐藏。
        StageText = string.IsNullOrWhiteSpace(progress.Stage) ? null : progress.Stage;
        CurrentFile = string.IsNullOrWhiteSpace(progress.CurrentFile) ? "准备中…" : progress.CurrentFile;
        SpeedText = progress.SpeedBytesPerSecond > 0
            ? $"{Conv.FormatSize((long)progress.SpeedBytesPerSecond)}/s"
            : "—";

        EtaText = progress.Eta is { } eta
            ? $"剩余 {Conv.FormatDuration(eta)}"
            : "剩余时间计算中";

        FilesText = progress.TotalFiles > 0
            ? progress.FailedFiles > 0
                ? $"{progress.CompletedFiles} / {progress.TotalFiles} 个文件，{progress.FailedFiles} 个失败"
                : $"{progress.CompletedFiles} / {progress.TotalFiles} 个文件"
            : string.Empty;

        // 任务栏进度：窗口最小化后用户只能看任务栏。ShellProgress 自己节流（10 次/秒），
        // 这里保持既有界面逻辑不变，只把比例转发出去。
        ShellProgress.Report(progress.Ratio);
    }

    /// <summary>安装后刷新列表上的「已安装」标记，无需重新拉远程清单。</summary>
    private async Task MarkInstalledAsync()
    {
        var installed = await LoadInstalledNamesAsync();
        foreach (var row in Versions)
        {
            row.IsInstalled = installed.Contains(row.Id);
        }

        if (SelectedRow is not null)
        {
            SelectedRow.IsInstalled = installed.Contains(SelectedRow.Id);
            IsSelectedInstalled = SelectedRow.IsInstalled;
            SelectedStateText = IsSelectedInstalled ? "已安装" : "未安装";
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
            ErrorText = "导航服务未就绪，无法跳转页面。";
            return;
        }

        _navigation.Navigate(key);
    }

    // ==================== 服务端 ====================

    /// <summary>两个分段互斥：客户端安装与服务端下载不能同时进行（都写同一个页面状态）。</summary>
    partial void OnIsClientModeChanged(bool value)
    {
        if (value && IsServerMode)
        {
            IsServerMode = false;
        }
        else if (!value && !IsServerMode)
        {
            IsServerMode = true;
        }
    }

    partial void OnIsServerModeChanged(bool value)
    {
        if (IsClientMode == value)
        {
            IsClientMode = !value;
        }

        if (value)
        {
            // 清单是逐版本查 JSON 得到的（约 120 个请求），只在第一次切到服务端时加载。
            _ = EnsureServerVersionsAsync();
        }
    }

    partial void OnSelectedServerVersionChanged(ServerVersionRow? value)
    {
        UpdateServerCommand();

        if (value is not null)
        {
            ServerDirectory = DefaultServerDirectory(value.Id);
            ServerResultText = null;

            if (!HasServerListError)
            {
                ServerErrorText = null;
            }
        }

        DownloadServerCommand.NotifyCanExecuteChanged();
    }

    /// <summary>首次切到服务端分段时加载清单。失败时把错误留给「重试」按钮。</summary>
    /// <param name="forceRefresh">true 时绕过落盘缓存（「刷新」按钮）。</param>
    private async Task EnsureServerVersionsAsync(bool forceRefresh = false)
    {
        if (_serverDownloads is null)
        {
            ServerErrorText = "服务端下载服务未注册，无法列出可下载版本。";
            HasServerListError = true;
            return;
        }

        if (ServerVersions.Count > 0 && !forceRefresh)
        {
            return;
        }

        IsLoadingServerVersions = true;
        HasServerListError = false;
        ServerErrorText = null;

        if (forceRefresh || ServerVersions.Count == 0)
        {
            ServerListStatusText = "正在读取版本清单（首次约 5–30 秒）…";
        }

        try
        {
            var snapshot = await _serverDownloads.ListSnapshotAsync(forceRefresh);
            ApplyServerSnapshot(snapshot);

            // 缓存过期时服务先返回旧值再后台刷新；这里等它完成，UI 不需要轮询。
            if (snapshot.IsRefreshing)
            {
                var updated = await _serverDownloads.EnsureRefreshedAsync();
                ApplyServerSnapshot(updated);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面被切换/请求被取消：保持现状，不覆盖已有列表。
        }
        catch (Exception ex)
        {
            ServerErrorText = $"获取服务端版本列表失败：{ex.Message}";
            HasServerListError = true;
            ServerListStatusText = string.Empty;
        }
        finally
        {
            IsLoadingServerVersions = false;
        }
    }

    /// <summary>
    /// 把一份快照落到界面：列表、来源提示、选中项。
    ///
    /// 来源提示三分：加载中是「正在读取…」（由调用方先设）、缓存是「来自缓存（x 分钟前）」、
    /// 后台刷新是「正在后台更新清单…」——用户能看出当前这批数据是旧的还是刚扫的。
    /// </summary>
    private void ApplyServerSnapshot(ServerListSnapshot snapshot)
    {
        void Apply()
        {
            var previousId = SelectedServerVersion?.Id;

            ServerVersions.Clear();

            foreach (var info in snapshot.Items)
            {
                ServerVersions.Add(new ServerVersionRow(info));
            }

            HasServerVersions = ServerVersions.Count > 0;
            HasServerListError = !HasServerVersions;
            IsRefreshingServerVersions = snapshot.IsRefreshing;
            IsServerListFromCache = snapshot.Source == ServerListSource.Cache;
            ServerListStatusText = DescribeServerListStatus(snapshot);

            if (!HasServerVersions)
            {
                ServerErrorText = "没有找到可下载的服务端版本（需要联网获取 Mojang 清单）。";
            }

            // 优先保留用户当前选择；首次加载时对齐左侧客户端版本，再退化为第一个。
            ServerVersionRow? preferred = null;
            var wanted = previousId ?? SelectedRow?.Id;

            if (!string.IsNullOrEmpty(wanted))
            {
                foreach (var row in ServerVersions)
                {
                    if (string.Equals(row.Id, wanted, StringComparison.Ordinal))
                    {
                        preferred = row;
                        break;
                    }
                }
            }

            SelectedServerVersion = preferred ?? (ServerVersions.Count > 0 ? ServerVersions[0] : null);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.InvokeAsync(Apply);
        }
    }

    private static string DescribeServerListStatus(ServerListSnapshot snapshot)
    {
        if (snapshot.IsRefreshing)
        {
            return "正在后台更新清单…";
        }

        return snapshot.Source switch
        {
            ServerListSource.Cache when snapshot.CachedAt is { } at => $"来自缓存（{DescribeAgo(at)}）",
            ServerListSource.Cache => "来自缓存",
            ServerListSource.Network when snapshot.CoversAll => $"已扫描全部版本（{snapshot.Items.Count} 个）",
            ServerListSource.Network => $"已更新（{snapshot.Items.Count} 个版本）",
            _ => string.Empty,
        };
    }

    /// <summary>相对时间：缓存时间在 UI 上必须让人一眼看出「多久以前」。</summary>
    private static string DescribeAgo(DateTimeOffset when)
    {
        var delta = DateTimeOffset.Now - when;

        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        return delta.TotalHours < 24
            ? $"{(int)delta.TotalHours} 小时前"
            : $"{(int)delta.TotalDays} 天前";
    }

    [RelayCommand]
    private async Task ReloadServerVersionsAsync() => await EnsureServerVersionsAsync(forceRefresh: true);

    /// <summary>「扫描全部版本」要二次确认：全量扫描是一百多个 JSON 请求，可能几十秒。</summary>
    [RelayCommand]
    private void ScanAllServerVersions()
    {
        IsConfirmingFullScan = true;
        ServerErrorText = null;
    }

    [RelayCommand]
    private void CancelScanAllServerVersions() => IsConfirmingFullScan = false;

    [RelayCommand]
    private async Task ConfirmScanAllServerVersionsAsync()
    {
        IsConfirmingFullScan = false;

        if (_serverDownloads is null)
        {
            return;
        }

        IsScanningAllVersions = true;
        IsLoadingServerVersions = true;
        ServerErrorText = null;
        ServerListStatusText = "正在扫描全部版本（约 10–60 秒）…";

        try
        {
            var snapshot = await _serverDownloads.ListSnapshotAsync(fullScan: true);
            ApplyServerSnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            ServerListStatusText = string.Empty;
        }
        catch (Exception ex)
        {
            ServerErrorText = $"扫描全部版本失败：{ex.Message}";
            ServerListStatusText = string.Empty;
        }
        finally
        {
            IsScanningAllVersions = false;
            IsLoadingServerVersions = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadServer))]
    private async Task DownloadServerAsync()
    {
        var row = SelectedServerVersion;

        if (_serverDownloads is null)
        {
            ServerErrorText = "服务端下载服务未注册，无法下载。";
            return;
        }

        if (row is null)
        {
            ServerErrorText = "请先选择一个服务端版本。";
            return;
        }

        if (string.IsNullOrWhiteSpace(ServerDirectory))
        {
            ServerErrorText = "请先填写目标目录。";
            return;
        }

        ServerErrorText = null;
        ServerResultText = null;
        ServerProgress = 0;
        ServerProgressText = "准备中…";
        IsDownloadingServer = true;

        try
        {
            var progress = new Progress<DownloadProgress>(ApplyServerProgress);
            var result = await _serverDownloads.DownloadAsync(row.Id, ServerDirectory, progress);

            if (!result.Success)
            {
                ServerErrorText = result.Error ?? "下载失败";
                return;
            }

            ServerProgress = 100;
            ServerProgressText = "100%";

            var sha = string.IsNullOrWhiteSpace(result.Sha1)
                ? "—"
                : result.Sha1[..Math.Min(12, result.Sha1.Length)];

            ServerResultText = result.AlreadyExisted
                ? $"已存在且校验通过：{result.FilePath}（{Conv.FormatSize(result.Size)}，SHA1 {sha}）"
                : $"已下载：{result.FilePath}（{Conv.FormatSize(result.Size)}，SHA1 {sha}，耗时 {Conv.FormatDuration(result.Duration)}）";

            // 启动脚本与 jar 同目录同名，由 ServerDownloadService 落盘（GB18030 编码）。
            // 只在文件真的存在时提示：老版本下载的目录里可能没有，也不能给用户一个点不开的路径。
            var scriptPath = Path.Combine(
                Path.GetDirectoryName(result.FilePath) ?? ServerDirectory,
                ServerDownloadService.LaunchScriptFileName);

            if (File.Exists(scriptPath))
            {
                ServerResultText += $"；启动脚本：{scriptPath}";
            }
        }
        catch (OperationCanceledException)
        {
            ServerErrorText = "下载已取消，未完成的文件已删除。";
        }
        catch (Exception ex)
        {
            ServerErrorText = $"下载服务端失败：{ex.Message}";
        }
        finally
        {
            IsDownloadingServer = false;
        }
    }

    private bool CanDownloadServer() =>
        _serverDownloads is not null
        && !IsDownloadingServer
        && SelectedServerVersion is not null
        && !string.IsNullOrWhiteSpace(ServerDirectory);

    [RelayCommand]
    private void UseDefaultServerDirectory()
    {
        if (SelectedServerVersion is { } row)
        {
            ServerDirectory = DefaultServerDirectory(row.Id);
        }
    }

    /// <summary>复制启动命令。eula 不代填，只把命令与提示给到用户。</summary>
    [RelayCommand]
    private async Task CopyServerHintAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerCommand))
        {
            return;
        }

        if (_platform is null)
        {
            ServerErrorText = "平台服务未就绪，无法复制。";
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(ServerCommand))
            {
                ServerErrorText = "复制失败：剪贴板不可用，详见启动器日志。";
            }
        }
        catch (Exception ex)
        {
            ServerErrorText = $"复制失败：{ex.Message}";
        }
    }

    private void ApplyServerProgress(DownloadProgress progress)
    {
        ServerProgress = Math.Clamp(progress.Ratio * 100, 0, 100);
        ServerProgressText = progress.TotalBytes > 0
            ? $"{progress.Ratio * 100:0.#}% · {Conv.FormatSize(progress.DownloadedBytes)} / {Conv.FormatSize(progress.TotalBytes)}"
            : $"{progress.Ratio * 100:0.#}%";
    }

    private string DefaultServerDirectory(string versionId)
    {
        var root = _paths?.RootDirectory;

        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine("servers", versionId)
            : Path.Combine(root, "servers", versionId);
    }

    private void UpdateServerCommand()
    {
        var id = SelectedServerVersion?.Id;

        // 与 Launch Server.bat 的启动行同源：两处各写一遍时曾经不一致
        // （bat 有 -server/UseG1GC/-Xmx6144M，复制按钮只给 -Xmx2G），用户按按钮粘出来
        // 跑出来的服务端和双击 bat 不是一回事。字符串由 Core 的 BuildLaunchCommand 唯一定义。
        ServerCommand = string.IsNullOrEmpty(id)
            ? string.Empty
            : ServerDownloadService.BuildLaunchCommand($"server-{id}.jar");
    }

    // ==================== 任务面板 ====================

    /// <summary>
    /// 暂停 / 继续。语义由 <see cref="DownloadQueue.Pause"/> / <see cref="DownloadQueue.Resume"/> 保证：
    /// 暂停后不再启动新文件，在途文件下完即停；继续立刻放行。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTogglePause))]
    private void TogglePause()
    {
        if (_queue is null)
        {
            return;
        }

        if (IsPaused)
        {
            _queue.Resume();
        }
        else
        {
            _queue.Pause();
        }

        // 以队列的实际状态为准：外部批次期间 Pause 会被忽略，按钮不能凭界面自己猜。
        IsPaused = _queue.IsPaused;
        UpdatePauseState();
    }

    private bool CanTogglePause() => _queue is not null && HasActiveBatch && !IsExternalBatchActive;

    /// <summary>
    /// 从队列同步暂停相关状态，并刷新依赖它的界面（按钮可用性、状态行、排队行文案）。
    ///
    /// 触发点：批次开始 / 结束（快照事件）、暂停切换、页面构造。
    /// 这些事件都在 UI 线程处理，不需要再切线程。
    /// </summary>
    private void UpdatePauseState()
    {
        if (!_isDemoBatch && _queue is not null)
        {
            HasActiveBatch = _queue.HasActiveBatch;
            IsExternalBatchActive = _queue.IsExternalBatchActive;
            IsPaused = _queue.IsPaused;
        }

        // 排队行在暂停时显示「等待」而不是「排队」：两者对用户是不同意思——
        // 「排队」是在等并发槽（马上就会动），「等待」是队列被暂停了（点了继续才会动）。
        foreach (var row in Tasks)
        {
            row.IsPaused = IsPaused;
        }
    }

    /// <summary>
    /// 截图专用：注入一组演示任务，并把面板摆到「有活动批次」的状态。
    ///
    /// 为什么需要：任务面板只有真实下载时才有行，而截图模式既不能点击、也不该真的联网下载
    /// （会污染演示数据、耗时不可控）。没有这里，「暂停 / 继续」按钮与任务列表的布局只能靠代码审查。
    /// 只在 <see cref="ScreenshotMode.IsActive"/> 下由 <c>App.AttachScreenshot</c> 调用，
    /// 不写磁盘、不发请求，正常启动路径不会走到。
    /// </summary>
    /// <param name="paused">是否把演示批次停在「已暂停」，用于截状态行与「等待」行。</param>
    public void ApplyDownloadDemo(bool paused)
    {
        if (!ScreenshotMode.IsActive)
        {
            return;
        }

        Tasks.Clear();
        _taskRows.Clear();

        // 六行覆盖全部状态：完成 / 下载中 / 排队 / 失败都出现，才能看出徽标与颜色是否区分得开。
        var demos = new (string Name, string Group, DownloadTaskState State, long Total, long Done, string? Error)[]
        {
            ("1.20.1.json", "versions", DownloadTaskState.Completed, 1_204_211, 1_204_211, null),
            ("client-1.20.1.jar", "versions", DownloadTaskState.Completed, 9_824_512, 9_824_512, null),
            ("lwjgl-3.3.1-natives-windows.jar", "libraries", DownloadTaskState.Running, 2_884_012, 1_233_004, null),
            ("assets/objects/ab/abcdef", "assets", DownloadTaskState.Queued, 4_115, 0, null),
            ("assets/objects/cd/cdefab", "assets", DownloadTaskState.Queued, 2_097_152, 0, null),
            ("forge-1.20.1-installer.jar", "libraries", DownloadTaskState.Failed, 0, 0,
                "响应 403 Forbidden（已重试 3 次）"),
        };

        for (var i = 0; i < demos.Length; i++)
        {
            var demo = demos[i];

            // 落盘路径只是给展开详情看的示例。用真实的 libraries 根拼，避免在跨平台代码里写死盘符。
            var path = string.IsNullOrEmpty(_paths?.LibrariesDirectory)
                ? demo.Name
                : Path.Combine(_paths!.LibrariesDirectory, demo.Name.Replace('/', Path.DirectorySeparatorChar));

            var row = new DownloadTaskRow(new DownloadTaskInfo
            {
                Id = demo.Name,
                Name = demo.Name,
                Group = demo.Group,
                State = demo.State,
                TotalBytes = demo.Total,
                DownloadedBytes = demo.Done,
                Error = demo.Error,
                Url = demo.State == DownloadTaskState.Failed
                    ? "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar"
                    : null,
                DestinationPath = path,
                Sequence = i,
            });

            Tasks.Add(row);
            _taskRows[row.Key] = row;
        }

        // _isDemoBatch 让后续的 UpdatePauseState 不再从队列读「有没有活动批次」（演示期间没有真批次）。
        _isDemoBatch = true;
        HasActiveBatch = true;
        IsExternalBatchActive = false;
        FailedTaskCount = Tasks.Count(task => task.IsFailed);
        IsPaused = paused;

        UpdateTaskPanelState();
    }

    /// <summary>
    /// 下载线程发来的快照。Core 已把事件节流到 5 次/秒，这里只负责线程切换：
    /// <see cref="ObservableCollection{T}"/> 与可观察属性只能在 UI 线程改。
    /// </summary>
    private void OnQueueTasksChanged(object? sender, DownloadTasksChangedEventArgs e)
    {
        var tasks = e.Tasks;

        if (Dispatcher.UIThread.CheckAccess())
        {
            SyncTasks(tasks);
            return;
        }

        Dispatcher.UIThread.Post(() => SyncTasks(tasks));
    }

    private void OnQueueBatchCompleted(object? sender, DownloadBatchSummary summary)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            LoadHistory();
            return;
        }

        Dispatcher.UIThread.Post(LoadHistory);
    }

    /// <summary>
    /// 重新计算顶部状态行。触发点：构造、会话降级、设置变更。
    ///
    /// 降级窗口是「写时求值」的（governor 没有定时器），所以 10 分钟窗口自然过期后
    /// 界面要等到下一次刷新（再次进入页面 / 下一次降级 / 设置变更）才会变回镜像。
    /// 这里不轮询：为了一个文字行开定时器不划算。
    /// </summary>
    private void RefreshSourceStatus()
    {
        var strategy = _sourceProvider?.Strategy
            ?? _settings?.Current.DownloadSource
            ?? DownloadSource.Mirror;

        var effective = _sourceProvider?.Effective ?? strategy;
        var reason = _sourceProvider?.DegradeReason;
        var degraded = strategy == DownloadSource.Auto && !string.IsNullOrEmpty(reason);

        // 手动镜像档不会换源（PITFALLS D9），所以这里用独立的提示通道；
        // 自动档即便同时拿到提示也不显示：那时候降级原因已经解释得更彻底。
        var manualHint = strategy == DownloadSource.Mirror
            ? _sourceProvider?.ManualDegradeHint
            : null;

        SourceStatusText = strategy switch
        {
            DownloadSource.Auto => degraded
                ? $"下载源：自动 · 已降级到{DescribeSource(effective)}（{reason}）"
                : $"下载源：自动 · 当前 {DescribeSource(effective)}",
            DownloadSource.Official => "下载源：官方源（手动）",
            _ => manualHint is null
                ? "下载源：BMCLAPI 镜像（手动）"
                : "下载源：BMCLAPI 镜像（手动 · 可能不可用）",
        };

        IsSourceDegraded = degraded;
        SourceManualHint = manualHint;
    }

    /// <summary>状态行里的源名：自动模式显示的是本次会话实际在用的那一侧。</summary>
    private static string DescribeSource(DownloadSource source) => source switch
    {
        DownloadSource.Official => "官方",
        DownloadSource.Mirror => "BMCLAPI",
        _ => "自动选择",
    };

    /// <summary>安装摘要里的源名（比状态行更具体：「BMCLAPI 镜像」「官方源」）。</summary>
    private static string DescribeInstallSource(DownloadSource source) => source switch
    {
        DownloadSource.Official => "官方源",
        DownloadSource.Mirror => "BMCLAPI 镜像",
        _ => "自动选择",
    };

    /// <summary>把回调切到 UI 线程（降级事件来自下载线程）。</summary>
    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>
    /// 增量同步任务行：按快照顺序 Insert / Move，只对变化字段发通知。
    ///
    /// 取舍：整表 Clear + Add 代码更短，但每次（最多 5 次/秒）都会让 ListBox 重建容器——
    /// 展开的失败详情会折叠、用户滚到的位置会跳回顶部。复用行对象后这两件事都不会发生。
    /// 代价是 <see cref="ObservableCollection{T}.IndexOf"/> 的 O(n) 查找，200 行量级可忽略。
    /// </summary>
    private void SyncTasks(IReadOnlyList<DownloadTaskInfo> snapshot)
    {
        var capped = snapshot.Count > MaxRenderedTasks
            ? snapshot.Take(MaxRenderedTasks).ToArray()
            : snapshot;

        var index = 0;
        foreach (var info in capped)
        {
            if (!_taskRows.TryGetValue(info.Key, out var row))
            {
                row = new DownloadTaskRow(info);
                _taskRows[info.Key] = row;
                Tasks.Insert(index, row);
            }
            else
            {
                row.Update(info);

                var current = Tasks.IndexOf(row);
                if (current < 0)
                {
                    Tasks.Insert(index, row);
                }
                else if (current != index)
                {
                    Tasks.Move(current, index);
                }
            }

            index++;
        }

        while (Tasks.Count > index)
        {
            var stale = Tasks[index];
            Tasks.RemoveAt(index);
            _taskRows.Remove(stale.Key);
        }

        FailedTaskCount = Tasks.Count(task => task.IsFailed);
        UpdateTaskPanelState();
    }

    private void LoadHistory()
    {
        History.Clear();

        if (_queue is not null)
        {
            foreach (var summary in _queue.History)
            {
                History.Add(new BatchHistoryRow(summary));
            }
        }

        UpdateTaskPanelState();
    }

    private void UpdateTaskPanelState()
    {
        HasTasks = Tasks.Count > 0;
        HasHistory = History.Count > 0;
        HasFailures = FailedTaskCount > 0;
        FailedBadgeText = $"{FailedTaskCount} 个失败";

        TaskPanelNote = Tasks.Count switch
        {
            0 when _queue is null => "下载队列未注册，任务明细不可用。",
            0 => "游戏本体安装、修复、整合包、Java 运行时与在线内容下载会在这里逐文件显示；失败项可以单独重试。",
            >= MaxRenderedTasks => $"只渲染最近 {MaxRenderedTasks} 条；已完成项最多保留 {DownloadQueue.FinishedTaskRetention} 条。",
            _ => $"共 {Tasks.Count} 个文件；已完成项最多保留 {DownloadQueue.FinishedTaskRetention} 条。",
        };

        // 批次开始 / 结束都经过这里，暂停按钮的可用性与状态行随之刷新。
        UpdatePauseState();
    }

    /// <summary>
    /// 只重试失败项：把失败快照重建为子批次再跑一次，成功文件不复用、也不重跑整个安装。
    /// 与安装互斥——两个批次并发会把任务面板的快照互相覆盖。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        if (_queue is null)
        {
            RetryNote = "下载队列未注册，无法重试。";
            return;
        }

        var items = DownloadQueue.BuildRetryItems(_queue.FailedTasks);
        if (items.Count == 0)
        {
            RetryNote = "没有可重试的失败项（缺少下载地址或落盘路径）。";
            return;
        }

        IsRetrying = true;
        RetryNote = $"正在重试 {items.Count} 个文件…";

        try
        {
            // Progress<T> 在 UI 线程构造，回调会被 marshal 回 UI 线程——与安装进度共用同一条进度条。
            var progress = new Progress<DownloadProgress>(ApplyDownloadProgress);
            var result = await _queue.DownloadAsync(items, progress);

            RetryNote = result.Success
                ? $"重试 {items.Count} 个文件，全部成功。"
                : $"重试 {items.Count} 个文件，仍有 {result.FailedFiles} 个失败。";
        }
        catch (OperationCanceledException)
        {
            RetryNote = "重试已取消。";
        }
        catch (Exception ex)
        {
            RetryNote = $"重试失败：{ex.Message}";
        }
        finally
        {
            // 重试也是同一条任务栏进度：结束/失败/取消后清除。
            ShellProgress.Clear();

            IsRetrying = false;
        }
    }

    private bool CanRetryFailed() => !IsInstalling && !IsRetrying && FailedTaskCount > 0;

    // ==================== 工具 ====================

    private string DescribeInstallName(string minecraftVersion)
    {
        var loader = DescribeLoader(SelectedLoader);
        if (SelectedLoader == LoaderKind.Vanilla)
        {
            return minecraftVersion;
        }

        var loaderVersion = SelectedLoaderVersion?.Version;
        return string.IsNullOrWhiteSpace(loaderVersion)
            ? $"{minecraftVersion} + {loader}"
            : $"{minecraftVersion} + {loader} {loaderVersion}";
    }

    private static string DescribeLoader(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版",
    };

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

        return missing.Count == 0
            ? null
            : $"后端服务未注册：{string.Join("、", missing)}。无法获取版本清单与安装（等待组合根合并）。";
    }
}

/// <summary>
/// 任务面板的一行。
///
/// 行对象被复用（<see cref="Key"/> 不变就 <see cref="Update"/>），这样列表刷新时
/// 展开的失败详情与选中态不会被重置。
/// </summary>
public sealed partial class DownloadTaskRow : ObservableObject
{
    public DownloadTaskRow(DownloadTaskInfo info)
    {
        Key = info.Key;
        Update(info);
    }

    /// <summary>稳定键（落盘路径 / Id），增量同步时用于复用行对象。</summary>
    public string Key { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    [ObservableProperty]
    private string _stateText = "排队";

    /// <summary>
    /// 队列是否处于暂停态。暂停时「排队」要显示成「等待」：
    /// 排队意味着马上会轮到自己，等待意味着用户得先点「继续」。
    /// </summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>最近一次快照里的状态。暂停切换要按它重算文案，但又不该为此重建整行。</summary>
    private DownloadTaskState _state = DownloadTaskState.Queued;

    partial void OnIsPausedChanged(bool value) => RefreshStateText();

    private void RefreshStateText() => StateText = IsPaused && _state == DownloadTaskState.Queued
        ? "等待"
        : DownloadTaskPresentation.DescribeState(_state);

    [ObservableProperty]
    private string _percentText = "—";

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleDetailCommand))]
    private bool _isFailed;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _url;

    [ObservableProperty]
    private string? _pathText;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>只有失败项才有可展开的详情（错误原因 + URL + 落盘路径）。</summary>
    public bool HasDetail => IsFailed;

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    public void Update(DownloadTaskInfo info)
    {
        Name = info.Name;
        Group = info.Group;
        _state = info.State;
        RefreshStateText();
        PercentText = DownloadTaskPresentation.DescribePercent(info);
        SizeText = DescribeSize(info);
        IsFailed = info.IsFailed;
        Error = info.Error;
        Url = info.Url;

        // 长文件名在行内是省略号；完整路径放在展开详情里，用户排查「文件落到哪」时不用猜。
        PathText = info.DestinationPath;
    }

    [RelayCommand(CanExecute = nameof(HasDetail))]
    private void ToggleDetail() => IsExpanded = !IsExpanded;

    private static string DescribeSize(DownloadTaskInfo info)
    {
        if (info.TotalBytes <= 0)
        {
            return info.DownloadedBytes > 0 ? Conv.FormatSize(info.DownloadedBytes) : "大小未知";
        }

        return $"{Conv.FormatSize(info.DownloadedBytes)} / {Conv.FormatSize(info.TotalBytes)}";
    }
}

/// <summary>批次历史的一行。数据全部来自 <see cref="DownloadBatchSummary"/>，不再二次统计。</summary>
public sealed class BatchHistoryRow
{
    public BatchHistoryRow(DownloadBatchSummary summary)
    {
        Name = summary.Name;
        TimeText = summary.FinishedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        CountsText = $"完成 {summary.CompletedFiles} · 跳过 {summary.SkippedFiles} · 失败 {summary.FailedFiles}";
        DurationText = $"耗时 {Conv.FormatDuration(summary.Duration)}";
        SizeText = Conv.FormatSize(summary.DownloadedBytes);
        IsSuccess = summary.Success;
        StateText = summary.Success ? "成功" : $"{summary.FailedFiles} 个失败";
    }

    public string Name { get; }

    public string TimeText { get; }

    public string CountsText { get; }

    public string DurationText { get; }

    public string SizeText { get; }

    public bool IsSuccess { get; }

    public string StateText { get; }
}

/// <summary>服务端版本下拉里的一行。</summary>
public sealed class ServerVersionRow
{
    public ServerVersionRow(ServerVersionInfo info)
    {
        Id = info.Id;
        Sha1 = info.Sha1;
        Size = info.Size;
        ChannelText = info.Channel switch
        {
            VersionChannel.Snapshot => "快照",
            VersionChannel.OldBeta => "Beta",
            VersionChannel.OldAlpha => "Alpha",
            VersionChannel.Special => "愚人节",
            _ => "正式",
        };
        SizeText = info.Size > 0 ? Conv.FormatSize(info.Size) : "大小未知";
        ReleaseText = info.ReleaseTime.ToLocalTime()
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        IsSnapshot = info.Channel == VersionChannel.Snapshot;
    }

    public string Id { get; }

    public string? Sha1 { get; }

    public long Size { get; }

    public string ChannelText { get; }

    public string SizeText { get; }

    public string ReleaseText { get; }

    public bool IsSnapshot { get; }
}

/// <summary>远程版本清单的一行。</summary>
public sealed partial class RemoteVersionRow : ObservableObject
{
    public RemoteVersionRow(RemoteVersion version, bool isInstalled)
    {
        Version = version;
        Id = version.Id;
        IsRelease = version.Channel == VersionChannel.Release;
        IsSnapshot = version.Channel == VersionChannel.Snapshot;
        IsOld = version.Channel is VersionChannel.OldBeta or VersionChannel.OldAlpha;
        IsSpecial = version.Channel == VersionChannel.Special;
        ChannelText = version.Channel switch
        {
            VersionChannel.Snapshot => "快照",
            VersionChannel.OldBeta => "Beta",
            VersionChannel.OldAlpha => "Alpha",
            VersionChannel.Special => "愚人节",
            _ => "正式",
        };
        ReleaseTimeText = version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        _isInstalled = isInstalled;
    }

    public RemoteVersion Version { get; }

    public string Id { get; }

    public bool IsRelease { get; }

    public bool IsSnapshot { get; }

    /// <summary>远古版（Beta / Alpha 合成一档）。</summary>
    public bool IsOld { get; }

    /// <summary>愚人节版。</summary>
    public bool IsSpecial { get; }

    public string ChannelText { get; }

    public string ReleaseTimeText { get; }

    /// <summary>右上角徽标文字：已安装优先，否则显示发行渠道。</summary>
    public string BadgeText => IsInstalled ? "已安装" : ChannelText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    private bool _isInstalled;
}
