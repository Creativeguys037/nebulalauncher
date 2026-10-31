using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;
using NebulaLauncher.Services;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 皮肤页（票 41）：左边翻网络皮肤库，右边 3D 预览选中的那套。
///
/// 三态与断网：加载中显示进度条，失败在顶部挂一条可关闭的错误条，
/// 列表**保留**上一次拿到的行（这就是「已缓存内容仍可看」），只有第一次加载失败才出现空态。
///
/// 浏览皮肤库不需要登录，所以顶部账户区只做展示（跟随当前账户），不参与取数。
/// 在途请求分三条线各自收尾：列表、缩略图、3D 预览；切页时统一取消，回页时补拉打断的缩略图。
/// </summary>
public sealed partial class SkinsViewModel : PageViewModelBase
{
    /// <summary>缩略图并发上限。20 行一次全甩出去会把站点的连接与解码同时打满。</summary>
    private const int MaxConcurrentThumbnails = 4;

    private readonly SkinLibraryClient _library;

    private readonly ILauncherPaths? _paths;

    /// <summary>
    /// 公开皮肤库的纹理落盘缓存（票 54）。数据目录没注册时是 null，预览退化成只走网络。
    /// 分区名用站点主机名，换站不会互相顶掉。
    /// </summary>
    private readonly SkinTextureStore? _store;

    /// <summary>落盘分区名：站点主机名。解析不出来时退回 unknown，落盘本身不会抛。</summary>
    private readonly string _libraryScope;

    private IAccountService? _accounts;

    private IAppLog? _log;

    private CancellationTokenSource? _pageCancellation;

    private CancellationTokenSource? _thumbnailsCancellation;

    private CancellationTokenSource? _previewCancellation;

    /// <summary>缩略图按 tid 缓存：翻页回来不重拉。位图随页面一起释放。</summary>
    private readonly Dictionary<int, Bitmap> _thumbnailCache = [];

    /// <summary>像素缓存，3D 预览在同一页里来回点不重复下载。</summary>
    private readonly Dictionary<int, ArgbSkinTexture> _previewCache = [];

    private bool _subscribed;

    /// <summary>当前错误是不是「根本连不上」。决定错误条写「无网络」还是「皮肤站异常」。</summary>
    private bool _lastErrorIsNetwork;

    private string? _subtitle;

    // ==================== 本地皮肤（票 42） ====================

    /// <summary>本地皮肤库。数据目录没注册时是 null，页面显示「库不可用」而不是白屏。</summary>
    private LocalSkinStore? _localStore;

    private IPlatformService? _platform;

    /// <summary>本地目录的「打开目录 / 扫描」在途标记。与网络那三条取消线分开，互不打断。</summary>
    private CancellationTokenSource? _localCancellation;

    /// <summary>本地行的平面小图，按文件名缓存。文件被删后同名新图会命中旧缓存，但只要改名规则带序号就能避开。</summary>
    private readonly Dictionary<string, Bitmap> _localThumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本地列表「至少扫过一次」。没扫过不显示空态，否则进页面会先闪一下「还没有皮肤」。</summary>
    private bool _hasLoadedLocalOnce;

    // ==================== 官方换肤（票 44） ====================

    /// <summary>官方换肤客户端。容器里没注册时自建，跟随 <see cref="SkinsViewModel"/> 的生命周期释放。</summary>
    private MojangSkinClient? _mojang;

    /// <summary>「皮肤已变更」广播：换肤成功后侧边栏那张 3D 卡靠它刷新（票 44 AC2）。</summary>
    private SkinChangeNotifier? _skinChanges;

    private INavigationService? _navigation;

    /// <summary>429 / 5xx 的重试冷却。UI 层节流，客户端一次调用只发一次请求。</summary>
    private readonly SkinUploadThrottle _throttle = new();

    /// <summary>上一次可重试的操作。重试按钮只重放进这一条，不会顺手重跑别的。</summary>
    private Func<CancellationToken, Task<SkinUploadResult>>? _officialRetry;

    private CancellationTokenSource? _officialCancellation;

    /// <summary>冷却到期的刷新任务。冷却期间「重试」按钮是隐藏的，到期要让它自己冒出来。</summary>
    private CancellationTokenSource? _cooldownCancellation;

    public SkinsViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _log = services.GetService(typeof(IAppLog)) as IAppLog;
        _accounts = services.GetService(typeof(IAccountService)) as IAccountService;

        // 优先用容器注册的实例（测试与将来的多站配置都靠它）；组合根还没注册时自己造一个，
        // 这样页面独立可用。票 41 里记录了「建议在容器里注册 SkinLibraryClient」。
        _library = services.GetService(typeof(SkinLibraryClient)) as SkinLibraryClient
                   ?? new SkinLibraryClient(baseAddress: ResolveBaseAddress(), log: _log);

        // 本地皮肤库跟着数据目录走；容器里没注册路径时退化成「不可用」，不抛异常。
        _paths = services.GetService(typeof(ILauncherPaths)) as ILauncherPaths;
        _localStore = _paths is null ? null : new LocalSkinStore(_paths, _log);
        _store = _paths is null ? null : new SkinTextureStore(_paths, _log);
        // 分区名取站点主机名；地址解析不出来就传 null，由 NormalizeScope 归到 unknown 分区。
        _libraryScope = SkinTextureStore.NormalizeScope(
            Uri.TryCreate(_library.BaseAddress, UriKind.Absolute, out var libraryUri) ? libraryUri.Host : null);
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;

        // 官方换肤：容器里注册了就用注册的（测试可注入假 HttpClient）；
        // 「皮肤已变更」同理，没注册时落到进程级默认实例，保证与侧边栏订阅的是同一个。
        _mojang = services.GetService(typeof(MojangSkinClient)) as MojangSkinClient
                  ?? new MojangSkinClient(log: _log);
        _skinChanges = services.GetService(typeof(SkinChangeNotifier)) as SkinChangeNotifier
                       ?? SkinChangeBus.Current;
        _navigation = services.GetService(typeof(INavigationService)) as INavigationService;

        // 皮肤站通道（票 43）：同样容器优先、缺省自建。
        _authlibClient = services.GetService(typeof(AuthlibSkinClient)) as AuthlibSkinClient
                   ?? new AuthlibSkinClient(log: _log);

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(PageSummary));
            OnPropertyChanged(nameof(ShowLoadingPlaceholder));
        };

        LocalItems.CollectionChanged += (_, _) => NotifyLocalState();

        PreviewVisual = SkinVisual.Default;
        ApplyAccount(_accounts?.ActiveAccount);
        ApplyScreenshotFeedbackDemo();
    }

    /// <summary>
    /// 截图模式专用：把一条真实的 <see cref="SkinUploadFeedback"/> 摆进结果区。
    ///
    /// 走的是同一套 <c>For(result, …)</c> 映射，所以「重试」与「去账户页」按钮的
    /// 出现与否仍然由 <see cref="SkinUploadResult"/> 决定，只是这个结果来自构造样本。
    /// 非截图模式下这个属性恒为 null，这段代码不执行。
    /// </summary>
    private void ApplyScreenshotFeedbackDemo()
    {
        var demo = ScreenshotMode.SkinUploadFeedbackDemo;
        if (string.IsNullOrWhiteSpace(demo))
        {
            return;
        }

        var result = demo.ToLowerInvariant() switch
        {
            "unauthorized" => SkinUploadResult.Fail("登录已过期，请到账户页刷新令牌。", isAuthError: true),
            "badrequest" => SkinUploadResult.Fail("皮肤尺寸必须是 64×64 或 64×32，且必须是 PNG 格式。（服务端：Invalid skin file）"),
            "throttled" => SkinUploadResult.Fail("无网络：官方服务暂时不可用，请稍后重试。", isRetryable: true),
            _ => SkinUploadResult.Fail("换肤失败，原因未知。"),
        };

        OfficialFeedback = SkinUploadFeedback.For(result, "官方换肤成功。");

        if (result.IsRetryable)
        {
            _throttle.MarkRetryableFailure();
            OfficialCooldownText = _throttle.BlockedText;
            StartCooldownRefresh();
        }
    }

    public override string Title => "皮肤";

    public override string Key => NavigationKeys.Skins;

    public override string? Subtitle => _subtitle;

    /// <summary>皮肤库地址：默认 littleskin.cn，可用环境变量指到自建站或本地 stub。</summary>
    public string LibraryAddress => _library.BaseAddress;

    public ObservableCollection<SkinEntryViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isNetworkTab = true;

    public bool IsLocalTab => !IsNetworkTab;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>非空即显示错误条。断网、状态码异常、返回的不是 JSON 都落到这里。</summary>
    [ObservableProperty]
    private string? _errorText;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>错误条的徽标文案。把 HTTP 500 说成「无网络」会让人白折腾网络设置。</summary>
    public string ErrorBadge => _lastErrorIsNetwork ? "无网络" : "皮肤站异常";

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private int _pageNumber = 1;

    [ObservableProperty]
    private bool _isLastPage = true;

    /// <summary>是否已经成功或失败地加载过一页。只影响空态判定，不需要单独通知 UI。</summary>
    private bool _hasLoadedOnce;

    public bool CanGoNext => !IsLastPage && !IsLoading;

    public bool CanGoPrev => PageNumber > 1 && !IsLoading;

    public string PageLabel => $"第 {PageNumber} 页";

    public string PageSummary => Items.Count == 0 ? "没有皮肤" : $"本页 {Items.Count} 套";

    public bool HasItems => Items.Count > 0;

    /// <summary>空态只在「加载过 + 不在加载 + 没有行」时出现，避免首屏闪一下空状态。</summary>
    public bool IsEmpty => _hasLoadedOnce && !IsLoading && Items.Count == 0;

    /// <summary>首屏加载占位：正在读、手上一行都没有。断网重试时若已有旧行就不遮挡。</summary>
    public bool ShowLoadingPlaceholder => IsLoading && Items.Count == 0;

    /// <summary>空态说明随失败原因换口径：断网时说「换个关键字」等于甩锅给用户的搜索词。</summary>
    public string EmptyHint => HasError
        ? "检查网络后点上面的重试；之前加载过的那一页会照样列出来。"
        : "换个关键字，或者清空搜索看整页。";

    [ObservableProperty]
    private string _accountName = "还没有账户";

    [ObservableProperty]
    private string _accountBadge = "未登录";

    [ObservableProperty]
    private string _accountDetail = "皮肤库不用登录也能逛";

    [ObservableProperty]
    private bool _hasAccount;

    [ObservableProperty]
    private SkinEntryViewModel? _selected;

    [ObservableProperty]
    private SkinVisual? _previewVisual;

    [ObservableProperty]
    private string _previewName = "还没有选中皮肤";

    [ObservableProperty]
    private string _previewDetail = "点左侧任意一行，这里立刻换成它";

    // ==================== 本地页签的状态与列表（票 42） ====================

    /// <summary>本地皮肤行。跟 <see cref="Items"/> 是两条独立的列表：一边是站点的，一边是磁盘的。</summary>
    public ObservableCollection<LocalSkinEntryViewModel> LocalItems { get; } = [];

    /// <summary>本地皮肤目录的绝对路径，页面上直接显示给用户看。</summary>
    public string LocalDirectory => _localStore?.DirectoryPath ?? "数据目录未配置";

    [ObservableProperty]
    private LocalSkinEntryViewModel? _selectedLocal;

    [ObservableProperty]
    private bool _isLocalLoading;

    /// <summary>本地页签自己的错误文案。跟页面顶部的网络错误条分开，免得「打不开文件夹」被说成「皮肤站异常」。</summary>
    [ObservableProperty]
    private string? _localErrorText;

    public bool HasLocalError => !string.IsNullOrWhiteSpace(LocalErrorText);

    public bool HasLocalItems => LocalItems.Count > 0;

    /// <summary>扫过一次、扫完了、结果为空——三个条件齐了才显示空态，避免进页面先闪一下。</summary>
    public bool IsLocalEmpty => _hasLoadedLocalOnce && !IsLocalLoading && LocalItems.Count == 0;

    public bool ShowLocalLoadingPlaceholder => IsLocalLoading && LocalItems.Count == 0;

    public bool IsLocalDirectoryAvailable => _localStore is not null;

    public string LocalSummary => LocalItems.Count == 0 ? "本地还没有皮肤" : $"本地 {LocalItems.Count} 套皮肤";

    // ==================== 官方换肤的状态与命令（票 44） ====================

    /// <summary>官方换肤区此刻的按钮可用性。随当前账户变化。</summary>
    [ObservableProperty]
    private OfficialSkinCapability _official = OfficialSkinActions.Of(null);

    /// <summary>官方换肤的一次操作结果。成功用绿字，失败用红字；null 表示还没操作过。</summary>
    [ObservableProperty]
    private SkinUploadFeedback? _officialFeedback;

    /// <summary>有没有结果要显示。</summary>
    public bool HasOfficialFeedback => OfficialFeedback is not null;

    /// <summary>这条结果是不是错误（决定用危险色还是成功色）。</summary>
    public bool IsOfficialFeedbackError => OfficialFeedback?.IsError ?? false;

    /// <summary>失败时给「重试」按钮的条件：可重试，且不在冷却中。</summary>
    public bool CanRetryOfficial => OfficialFeedback?.CanRetry == true && !_throttle.IsBlocked;

    /// <summary>令牌过期时给「去账户页」按钮。</summary>
    public bool GoToAccountPageVisible => OfficialFeedback?.GoToAccountPage == true;

    public string OfficialFeedbackText => OfficialFeedback?.Text ?? string.Empty;

    /// <summary>冷却中的倒计时文案。冷却之外为 null。</summary>
    [ObservableProperty]
    private string? _officialCooldownText;

    public bool HasOfficialCooldown => !string.IsNullOrWhiteSpace(OfficialCooldownText);

    /// <summary>正在上传/重置。按钮在此期间禁用，防连点发出两次相同请求。</summary>
    [ObservableProperty]
    private bool _isOfficialBusy;

    /// <summary>AC8 的常驻说明。</summary>
    public string OfficialHint => OfficialSkinActions.JavaOnlyHint;

    public string OfficialCapeText => Official.CapeText;

    /// <summary>离线账户的体型切换：两个选项，只有 <see cref="OfficialSkinCapability.CanPickModel"/> 时才显示。</summary>
    public bool IsClassicModel => SkinModelMapping.FromAccountValue(_accounts?.ActiveAccount?.SkinModel) == LocalSkinModel.Classic;

    public bool IsSlimModel => !IsClassicModel;

    /// <summary>
    /// 「应用到官方账户」（AC1）：把当前选中的那一套（网络行或本地行）传到官方档案。
    ///
    /// 取字节的两条路不同：网络行要先把原图下回来，本地行直接读磁盘。
    /// 无论哪条路，到了上传前都走同一套本地校验与失败呈现（AC9 / AC6）。
    /// </summary>
    [RelayCommand]
    private Task ApplyToOfficialAsync() => RunOfficialAsync(UploadSelectedToOfficialAsync, "官方换肤");

    /// <summary>「重置为默认皮肤」（AC3）。</summary>
    [RelayCommand]
    private Task ResetOfficialSkinAsync() => RunOfficialAsync(ResetOfficialCoreAsync, "重置官方皮肤");

    /// <summary>失败态上的「重试」：只重放上一次那条操作，且必须先过冷却。</summary>
    [RelayCommand]
    private Task RetryOfficialAsync()
    {
        var retry = _officialRetry;
        return retry is null ? Task.CompletedTask : RunOfficialAsync(retry, "官方换肤重试");
    }

    /// <summary>取消失败态，把结果区收起来。</summary>
    [RelayCommand]
    private void DismissOfficialFeedback()
    {
        OfficialFeedback = null;
        _officialRetry = null;
    }

    /// <summary>令牌过期时去账户页刷新（AC6：401 引导到账户页，不原地重试）。</summary>
    [RelayCommand]
    private void GoToAccountPage()
    {
        if (_navigation is null)
        {
            OfficialFeedback = SkinUploadFeedback.LocalError("打不开账户页，请从左侧导航手动进入。");
            return;
        }

        _navigation.Navigate(NavigationKeys.Accounts);
    }

    /// <summary>离线账户在 Steve / Alex 之间切换（AC5）。走 <see cref="IAccountService"/> 落盘。</summary>
    [RelayCommand]
    private async Task SetClassicModelAsync() => await SwitchModelAsync(LocalSkinModel.Classic).ConfigureAwait(true);

    [RelayCommand]
    private async Task SetSlimModelAsync() => await SwitchModelAsync(LocalSkinModel.Slim).ConfigureAwait(true);

    private async Task SwitchModelAsync(LocalSkinModel model)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_accounts?.ActiveAccount is not { } account)
        {
            OfficialFeedback = SkinUploadFeedback.LocalError("先添加一个账户，再切换体型。");
            return;
        }

        var value = SkinModelMapping.ToAccountValue(model);
        if (string.Equals(account.SkinModel, value, StringComparison.Ordinal))
        {
            return;
        }

        var result = await _accounts.SetSkinModelAsync(account.Id, value).ConfigureAwait(true);
        if (!result.Success)
        {
            OfficialFeedback = SkinUploadFeedback.LocalError(result.Message ?? "体型没能保存。");
            return;
        }

        // 体型落盘之后账户事件会发一次，ApplyAccount 会把两个按钮的选中态切过去。
        // 这句文案只说「记下来了」：目前唯一读这个字段的是 SkinProfileParser 的兜底体型，
        // 离线账户没有皮肤档案，所以它暂时不会改变任何渲染结果。说得比实际多就是骗人。
        OfficialFeedback = new SkinUploadFeedback(
            model == LocalSkinModel.Slim ? "已记下 Alex 体型，写进账户文件。" : "已记下 Steve 体型，写进账户文件。",
            IsError: false,
            CanRetry: false,
            GoToAccountPage: false);
    }

    /// <summary>
    /// 所有官方操作的共同外壳：防重入、节流、统一把结果翻成 <see cref="SkinUploadFeedback"/>。
    ///
    /// <paramref name="operation"/> 由调用方传进来，是为了让「重试」只重放它自己那一条：
    /// 把所有官方操作都塞进同一个入口，会让「重置失败后点重试」变成又上传一次。
    /// </summary>
    private async Task RunOfficialAsync(Func<CancellationToken, Task<SkinUploadResult>> operation, string logName)
    {
        if (IsDisposed || IsOfficialBusy)
        {
            return;
        }

        // UI 层节流：429 之后立刻再点等于把限流当成网络抖动（AC6）。
        if (_throttle.IsBlocked)
        {
            OfficialCooldownText = _throttle.BlockedText;
            OnPropertyChanged(nameof(CanRetryOfficial));
            return;
        }

        IsOfficialBusy = true;
        OfficialFeedback = null;
        _officialRetry = null;

        var cancellation = new CancellationTokenSource();
        Replace(ref _officialCancellation, cancellation);

        try
        {
            var result = await operation(cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            if (result.Succeeded)
            {
                _throttle.MarkSuccess();
                OfficialCooldownText = null;
                OfficialFeedback = SkinUploadFeedback.For(result, $"{logName}成功。{OfficialSkinActions.JavaOnlyHint}");

                // AC2：换肤不动 Account 字段，AccountsChanged 不会发，必须显式广播一次。
                _skinChanges?.Notify(logName);
            }
            else
            {
                if (result.IsRetryable)
                {
                    _throttle.MarkRetryableFailure();
                    OfficialCooldownText = _throttle.BlockedText;
                    StartCooldownRefresh();
                }
                else
                {
                    OfficialCooldownText = null;
                }

                OfficialFeedback = SkinUploadFeedback.For(result, logName + "成功。");

                // 只有可重试的失败才留住重试入口；401 的入口是「去账户页」，400 是「换一张图」。
                _officialRetry = result.IsRetryable && !result.IsAuthError ? operation : null;
            }
        }
        catch (OperationCanceledException)
        {
            // 离开页面：结果作废，不覆盖界面。
        }
        catch (Exception ex)
        {
            _log?.Warn($"{logName}失败", ex);
            OfficialFeedback = SkinUploadFeedback.LocalError($"{logName}失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_officialCancellation, cancellation))
            {
                IsOfficialBusy = false;
            }

            OnPropertyChanged(nameof(CanRetryOfficial));
        }
    }

    /// <summary>
    /// 冷却到期之后把「重试」按钮放出来。
    ///
    /// 只更新可见性、不自动重发请求：自动重试会把一次 429 放大成两次 429，
    /// 而且用户可能已经去改了别的东西。这里只保证按钮不会因为「没人通知」而永远不出现。
    /// </summary>
    private void StartCooldownRefresh()
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _cooldownCancellation, cancellation);
        _ = RefreshCooldownAsync(cancellation);
    }

    /// <summary>后台任务没有观察者，异常必须就地消化（PITFALLS 2.8）。</summary>
    private async Task RefreshCooldownAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var remaining = _throttle.Remaining + TimeSpan.FromMilliseconds(250);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellation.Token).ConfigureAwait(true);
            }

            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            OfficialCooldownText = _throttle.BlockedText;
            OnPropertyChanged(nameof(CanRetryOfficial));
        }
        catch (OperationCanceledException)
        {
            // 又发了一次请求 / 离开页面：这次刷新作废。
        }
        catch (Exception ex)
        {
            _log?.Warn("刷新官方换肤冷却状态失败", ex);
        }
    }

    /// <summary>「应用到官方账户」的实做：取选中那套的 PNG 字节 → 本地校验 → 上传。</summary>
    private async Task<SkinUploadResult> UploadSelectedToOfficialAsync(CancellationToken cancellationToken)
    {
        if (Official.CanUpload is false)
        {
            return SkinUploadResult.Fail(Official.UnavailableText);
        }

        if (_accounts?.ActiveAccount is not { } account || _mojang is null)
        {
            return SkinUploadResult.Fail("当前账户不支持官方换肤。");
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return SkinUploadResult.Fail("这个账户缺少 Minecraft 令牌，请到账户页重新登录。", isAuthError: true);
        }

        var source = await ReadSelectedSkinAsync(cancellationToken).ConfigureAwait(true);
        if (source is null)
        {
            return SkinUploadResult.Fail(IsLocalTab
                ? "先选中一套本地皮肤再上传。"
                : "先选中一套网络皮肤再上传。");
        }

        if (source.ErrorText is { } sourceError)
        {
            // 取原图失败多半是网络问题：标成可重试，用户换张图或者等一会儿都行。
            return SkinUploadResult.Fail(sourceError, isRetryable: true);
        }

        // AC9：本地先拦一次，不合格就不发请求。官方只认 64×64 与 64×32，
        // 而 LocalSkinFormat 允许的尺寸更宽，所以这一步必须在客户端之前。
        if (SkinUploadPreflight.Check(source.Png, source.Model) is { } error)
        {
            return SkinUploadResult.Fail(error);
        }

        return await _mojang
            .UploadSkinAsync(account.AccessToken, SkinModelMapping.ToKind(source.Model), source.Png!, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>「重置为默认皮肤」的实做。</summary>
    private async Task<SkinUploadResult> ResetOfficialCoreAsync(CancellationToken cancellationToken)
    {
        if (Official.CanReset is false)
        {
            return SkinUploadResult.Fail(Official.UnavailableText);
        }

        if (_accounts?.ActiveAccount is not { } account || _mojang is null)
        {
            return SkinUploadResult.Fail("当前账户不支持官方换肤。");
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return SkinUploadResult.Fail("这个账户缺少 Minecraft 令牌，请到账户页重新登录。", isAuthError: true);
        }

        return await _mojang.ResetSkinAsync(account.AccessToken, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 取当前页签选中那一套的原始 PNG 字节与体型。
    /// 网络行要先下原图（列表里只有缩略图）；本地行直接读磁盘。
    ///
    /// 取不到字节不抛异常，而是回一个带 <see cref="SkinSource.ErrorText"/> 的对象：
    /// 这里失败的原因（读文件失败、详情拿不到、图下载失败）都要原样显示给用户，
    /// 而不是变成一句笼统的「换肤失败」。
    /// </summary>
    private async Task<SkinSource?> ReadSelectedSkinAsync(CancellationToken cancellationToken)
    {
        if (IsLocalTab)
        {
            if (SelectedLocal is not { } local)
            {
                return null;
            }

            try
            {
                var bytes = await Task.Run(() => File.ReadAllBytes(local.FilePath), cancellationToken).ConfigureAwait(true);
                return new SkinSource(bytes, local.Model, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Warn($"读取本地皮肤失败：{local.FilePath}", ex);
                return new SkinSource(null, local.Model, $"读不了这个皮肤文件：{ex.Message}");
            }
        }

        if (Selected is not { } entry || _library is null)
        {
            return null;
        }

        // 预览缓存里已经有解码后的像素，但上传要的是原始字节：重新下原图更省心，
        // 且一次点击只多一个请求。
        var info = await _library.InfoAsync(entry.Tid, cancellationToken).ConfigureAwait(true);
        if (!info.Succeeded || info.Value is not { } detail)
        {
            return new SkinSource(null, SkinModelMapping.ToLocal(entry.Model), info.ErrorText ?? "拿不到这套皮肤的详情。");
        }

        var png = await _library.DownloadAsync(_library.TextureUrl(detail.Hash), cancellationToken).ConfigureAwait(true);
        return png is null
            ? new SkinSource(null, SkinModelMapping.ToLocal(entry.Model), "皮肤图下载失败。")
            : new SkinSource(png, SkinModelMapping.ToLocal(entry.Model), null);
    }

    /// <summary>
     /// 一次上传需要的全部输入：字节 + 体型，<paramref name="ErrorText"/> 非空表示没取到字节。
     /// </summary>
    private sealed record SkinSource(byte[]? Png, LocalSkinModel Model, string? ErrorText);

    // ==================== 皮肤站换肤（票 43） ====================

    /// <summary>皮肤站材质客户端。容器里没注册时自建，跟随本页生命周期释放。</summary>
    private AuthlibSkinClient? _authlibClient;

    /// <summary>皮肤站通道自己的冷却。与官方通道分开：一条通道被 429 不该连累另一条。</summary>
    private readonly SkinUploadThrottle _authlibThrottle =
        new(channelName: SkinUploadThrottle.AuthlibChannelName);

    private Func<CancellationToken, Task<SkinUploadResult>>? _authlibRetry;

    private CancellationTokenSource? _authlibCancellation;

    private CancellationTokenSource? _authlibCooldownCancellation;

    private CancellationTokenSource? _authlibProfileCancellation;

    /// <summary>皮肤站换肤区此刻的按钮可用性。随当前账户与服务端能力变化。</summary>
    [ObservableProperty]
    private AuthlibSkinCapability _authlib = AuthlibSkinActions.Of(null);

    /// <summary>皮肤站通道的一次操作结果。null 表示还没操作过。</summary>
    [ObservableProperty]
    private SkinUploadFeedback? _authlibFeedback;

    public bool HasAuthlibFeedback => AuthlibFeedback is not null;

    public bool IsAuthlibFeedbackError => AuthlibFeedback?.IsError ?? false;

    public bool CanRetryAuthlib => AuthlibFeedback?.CanRetry == true && !_authlibThrottle.IsBlocked;

    /// <summary>皮肤站通道的令牌过期也走账户页，但按钮与官方那条互不影响。</summary>
    public bool GoToAccountPageFromAuthlibVisible => AuthlibFeedback?.GoToAccountPage == true;

    public string AuthlibFeedbackText => AuthlibFeedback?.Text ?? string.Empty;

    /// <summary>皮肤站通道冷却中的倒计时文案。</summary>
    [ObservableProperty]
    private string? _authlibCooldownText;

    public bool HasAuthlibCooldown => !string.IsNullOrWhiteSpace(AuthlibCooldownText);

    /// <summary>皮肤站通道正在上传/删除。</summary>
    [ObservableProperty]
    private bool _isAuthlibBusy;

    /// <summary>
    /// 「应用到皮肤站账户」（AC1）：把当前选中的那一套（网络行或本地行）传到皮肤站。
    ///
    /// 取字节复用官方那条路（<see cref="ReadSelectedSkinAsync"/>），但校验不同：
    /// 官方只收 64×64 与 64×32，皮肤站按 <see cref="LocalSkinFormat"/> 的通用规则，
    /// 所以两边各走各的前置检查，不能共用一个。
    /// </summary>
    [RelayCommand]
    private Task ApplyToAuthlibAsync() => RunAuthlibAsync(UploadSelectedToAuthlibAsync, "皮肤站换肤");

    /// <summary>把一张本地文件作为新皮肤上传（AC3）。与「应用选中行」的区别是来源可以是任意 PNG。</summary>
    [RelayCommand]
    private Task UploadLocalFileToAuthlibAsync() => RunAuthlibAsync(UploadPickedFileToAuthlibAsync, "上传皮肤");

    /// <summary>上传披风（AC4）。按钮只有在服务端能力允许时才会出现。</summary>
    [RelayCommand]
    private Task UploadCapeAsync() => RunAuthlibAsync(UploadCapeCoreAsync, "上传披风");

    /// <summary>删除当前皮肤（AC5）。</summary>
    [RelayCommand]
    private Task DeleteAuthlibSkinAsync() => RunAuthlibAsync(DeleteSkinCoreAsync, "删除皮肤");

    /// <summary>删除披风（AC5）。</summary>
    [RelayCommand]
    private Task DeleteAuthlibCapeAsync() => RunAuthlibAsync(DeleteCapeCoreAsync, "删除披风");

    /// <summary>失败态上的「重试」：只重放上一次那条操作。</summary>
    [RelayCommand]
    private Task RetryAuthlibAsync()
    {
        var retry = _authlibRetry;
        return retry is null ? Task.CompletedTask : RunAuthlibAsync(retry, "皮肤站重试");
    }

    [RelayCommand]
    private void DismissAuthlibFeedback()
    {
        AuthlibFeedback = null;
        _authlibRetry = null;
    }

    /// <summary>皮肤站通道的「去账户页」。</summary>
    [RelayCommand]
    private void GoToAccountPageFromAuthlib()
    {
        if (_navigation is null)
        {
            AuthlibFeedback = SkinUploadFeedback.LocalError("打不开账户页，请从左侧导航手动进入。");
            return;
        }

        _navigation.Navigate(NavigationKeys.Accounts);
    }

    /// <summary>
    /// 读当前角色的 <c>uploadableTextures</c>，决定披风按钮显不显示（AC4）。
    ///
    /// 读失败不清空已知能力：断网时把「本来能传披风」降级成「不能传」，
    /// 用户会以为权限被收走了。只有明确的响应才更新判断。
    /// </summary>
    private async Task LoadAuthlibCapabilitiesAsync(Account? account, CancellationToken cancellationToken)
    {
        if (account is not { Type: AccountType.Authlib } || _authlibClient is null)
        {
            return;
        }

        // 读不到（null）就保持现状：断网时把「本来能传披风」降级成「不能传」，
        // 用户会以为权限被收走了。只有真正读到的响应才更新判断。
        var capabilities = await _authlibClient.GetCapabilitiesAsync(account, cancellationToken).ConfigureAwait(true);
        if (capabilities is null || cancellationToken.IsCancellationRequested || IsDisposed)
        {
            return;
        }

        Authlib = AuthlibSkinActions.Of(account, capabilities);
    }

    /// <summary>「应用到皮肤站账户」的实做。</summary>
    private async Task<SkinUploadResult> UploadSelectedToAuthlibAsync(CancellationToken cancellationToken)
    {
        if (Authlib.CanUploadSkin is false)
        {
            return SkinUploadResult.Fail(Authlib.UnavailableText);
        }

        if (_accounts?.ActiveAccount is not { } account || _authlibClient is null)
        {
            return SkinUploadResult.Fail("当前账户不支持皮肤站换肤。");
        }

        var source = await ReadSelectedSkinAsync(cancellationToken).ConfigureAwait(true);
        if (source is null)
        {
            return SkinUploadResult.Fail(IsLocalTab
                ? "先选中一套本地皮肤再上传。"
                : "先选中一套网络皮肤再上传。");
        }

        if (source.ErrorText is { } sourceError)
        {
            return SkinUploadResult.Fail(sourceError, isRetryable: true);
        }

        // 皮肤站的尺寸规则就是 LocalSkinFormat 那套（客户端里也会再拦一次）。
        if (CheckSkinBytes(source.Png!, source.Model) is { } error)
        {
            return SkinUploadResult.Fail(error);
        }

        return await _authlibClient
            .UploadSkinAsync(account, SkinModelMapping.ToKind(source.Model), source.Png!, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>「上传本地文件」的实做：路径由 View 的文件对话框拿到后塞进 <see cref="PendingUploadPath"/>。</summary>
    private async Task<SkinUploadResult> UploadPickedFileToAuthlibAsync(CancellationToken cancellationToken)
    {
        if (Authlib.CanUploadSkin is false)
        {
            return SkinUploadResult.Fail(Authlib.UnavailableText);
        }

        if (_accounts?.ActiveAccount is not { } account || _authlibClient is null)
        {
            return SkinUploadResult.Fail("当前账户不支持皮肤站换肤。");
        }

        if (string.IsNullOrWhiteSpace(PendingUploadPath) || !File.Exists(PendingUploadPath))
        {
            return SkinUploadResult.Fail("没有选到文件。");
        }

        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => File.ReadAllBytes(PendingUploadPath), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SkinUploadResult.Fail($"读不了这个文件：{ex.Message}");
        }

        // 上传的是自己的图，体型用界面上选中的那个（默认跟随账户）。
        var model = SkinModelMapping.FromAccountValue(account.SkinModel);
        if (CheckSkinBytes(bytes, model) is { } error)
        {
            return SkinUploadResult.Fail(error);
        }

        return await _authlibClient
            .UploadSkinAsync(account, SkinModelMapping.ToKind(model), bytes, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>上传披风的实做。路径同样来自文件对话框。</summary>
    private async Task<SkinUploadResult> UploadCapeCoreAsync(CancellationToken cancellationToken)
    {
        if (Authlib.CanUploadCape is false)
        {
            return SkinUploadResult.Fail(Authlib.CapeText);
        }

        if (_accounts?.ActiveAccount is not { } account || _authlibClient is null)
        {
            return SkinUploadResult.Fail("当前账户不支持皮肤站换肤。");
        }

        if (string.IsNullOrWhiteSpace(PendingUploadPath) || !File.Exists(PendingUploadPath))
        {
            return SkinUploadResult.Fail("没有选到披风文件。");
        }

        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => File.ReadAllBytes(PendingUploadPath), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SkinUploadResult.Fail($"读不了这个文件：{ex.Message}");
        }

        return await _authlibClient.UploadCapeAsync(account, bytes, cancellationToken).ConfigureAwait(true);
    }

    private async Task<SkinUploadResult> DeleteSkinCoreAsync(CancellationToken cancellationToken)
    {
        if (Authlib.CanDeleteSkin is false)
        {
            return SkinUploadResult.Fail(Authlib.UnavailableText);
        }

        if (_accounts?.ActiveAccount is not { } account || _authlibClient is null)
        {
            return SkinUploadResult.Fail("当前账户不支持皮肤站换肤。");
        }

        return await _authlibClient.DeleteSkinAsync(account, cancellationToken).ConfigureAwait(true);
    }

    private async Task<SkinUploadResult> DeleteCapeCoreAsync(CancellationToken cancellationToken)
    {
        if (Authlib.CanDeleteCape is false)
        {
            return SkinUploadResult.Fail(Authlib.CapeText);
        }

        if (_accounts?.ActiveAccount is not { } account || _authlibClient is null)
        {
            return SkinUploadResult.Fail("当前账户不支持皮肤站换肤。");
        }

        return await _authlibClient.DeleteCapeAsync(account, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 所有皮肤站操作的共同外壳：与官方那条同形（防重入、节流、统一翻成反馈条、成功即广播）。
    ///
    /// 不复用同一个 <see cref="SkinUploadThrottle"/> 实例：一条通道被限流，
    /// 不该让另一条也点不动。
    /// </summary>
    private async Task RunAuthlibAsync(Func<CancellationToken, Task<SkinUploadResult>> operation, string logName)
    {
        if (IsDisposed || IsAuthlibBusy)
        {
            return;
        }

        if (_authlibThrottle.IsBlocked)
        {
            AuthlibCooldownText = _authlibThrottle.BlockedText;
            OnPropertyChanged(nameof(CanRetryAuthlib));
            return;
        }

        IsAuthlibBusy = true;
        AuthlibFeedback = null;
        _authlibRetry = null;

        var cancellation = new CancellationTokenSource();
        Replace(ref _authlibCancellation, cancellation);

        try
        {
            var result = await operation(cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            if (result.Succeeded)
            {
                _authlibThrottle.MarkSuccess();
                AuthlibCooldownText = null;
                AuthlibFeedback = SkinUploadFeedback.For(result, $"{logName}成功。");

                // AC2：换肤不动 Account 字段，必须显式广播，侧边栏才会重取。
                _skinChanges?.Notify(logName);
            }
            else
            {
                if (result.IsRetryable)
                {
                    _authlibThrottle.MarkRetryableFailure();
                    AuthlibCooldownText = _authlibThrottle.BlockedText;
                    StartAuthlibCooldownRefresh();
                }
                else
                {
                    AuthlibCooldownText = null;
                }

                AuthlibFeedback = SkinUploadFeedback.For(result, logName + "成功。");
                _authlibRetry = result.IsRetryable && !result.IsAuthError ? operation : null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Warn($"{logName}失败", ex);
            AuthlibFeedback = SkinUploadFeedback.LocalError($"{logName}失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_authlibCancellation, cancellation))
            {
                IsAuthlibBusy = false;
            }

            OnPropertyChanged(nameof(CanRetryAuthlib));
        }
    }

    private void StartAuthlibCooldownRefresh()
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _authlibCooldownCancellation, cancellation);
        _ = RefreshAuthlibCooldownAsync(cancellation);
    }

    private async Task RefreshAuthlibCooldownAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var remaining = _authlibThrottle.Remaining + TimeSpan.FromMilliseconds(250);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellation.Token).ConfigureAwait(true);
            }

            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            AuthlibCooldownText = null;
            OnPropertyChanged(nameof(CanRetryAuthlib));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>View 层选完文件后放这里；空串表示没有任何待上传的文件。</summary>
    [ObservableProperty]
    private string? _pendingUploadPath;

    /// <summary>
    /// 上传前的本地校验：先确认是 PNG，再把宽高交给 <see cref="LocalSkinFormat.Validate"/>。
    ///
    /// 两步分开是必要的：<see cref="LocalSkinFormat.Validate"/> 只认宽高，
    /// 而「这根本不是一张 PNG」要由 <see cref="LocalSkinFormat.TryReadPngSize"/> 判定。
    /// 不合格就在这里返回中文原因，一个请求都不发（票 #43 AC6）。
    /// </summary>
    private static string? CheckSkinBytes(byte[] png, LocalSkinModel model)
    {
        if (!LocalSkinFormat.TryReadPngSize(png, out var width, out var height))
        {
            return "这不是有效的 PNG 图片。";
        }

        return LocalSkinFormat.Validate(width, height, model);
    }

    public override async Task OnNavigatedToAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        AttachAccounts();
        ApplyAccount(_accounts?.ActiveAccount);

        // 每次进页面都重扫本地目录：用户可能刚把图丢进文件夹（票 42 的 AC4）。
        // 只读文件头，代价是几十毫秒，比「必须手动刷新」符合直觉。
        await LoadLocalAsync().ConfigureAwait(true);

        if (!_hasLoadedOnce)
        {
            await LoadPageAsync(1).ConfigureAwait(true);
            return;
        }

        // 回到页面：补拉上次切页时被打断的缩略图，不重发列表请求。
        StartThumbnailLoading();
    }

    /// <summary>离开页面：取消在途请求并退订账户事件（PITFALLS 2.7：离开与销毁都要退订）。</summary>
    public override Task OnNavigatedFromAsync()
    {
        DetachAccounts();
        Cancel(ref _pageCancellation);
        Cancel(ref _thumbnailsCancellation);
        Cancel(ref _previewCancellation);
        Cancel(ref _officialCancellation);
        Cancel(ref _cooldownCancellation);
        Cancel(ref _authlibCancellation);
        Cancel(ref _authlibCooldownCancellation);
        Cancel(ref _authlibProfileCancellation);
        return Task.CompletedTask;
    }

    private void DetachAccounts()
    {
        if (_subscribed && _accounts is not null)
        {
            _accounts.AccountsChanged -= OnAccountsChanged;
            _subscribed = false;
        }
    }

    [RelayCommand]
    private void SelectNetworkTab()
    {
        IsNetworkTab = true;
        CloseError();
    }

    [RelayCommand]
    private void SelectLocalTab()
    {
        IsNetworkTab = false;
        CloseError();

        // 切到本地页签顺手重扫：手上没有「刷新」按钮的旧用户也能看到新丢进来的图。
        _ = LoadLocalAsync();
    }

    // ==================== 本地皮肤的命令（票 42） ====================

    [RelayCommand]
    private Task RefreshLocalAsync() => LoadLocalAsync();

    /// <summary>「打开目录」：目录不存在就先建出来，别让资源管理器报错。</summary>
    [RelayCommand]
    private async Task OpenLocalDirectoryAsync()
    {
        if (_localStore is null)
        {
            LocalErrorText = "本地皮肤库没有初始化，打不开目录。";
            return;
        }

        try
        {
            Directory.CreateDirectory(_localStore.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Warn($"创建本地皮肤目录失败：{_localStore.DirectoryPath}", ex);
            LocalErrorText = $"建不了本地皮肤目录：{ex.Message}";
            return;
        }

        if (_platform is null)
        {
            LocalErrorText = "当前环境没有可用的桌面集成，打不开目录。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(_localStore.DirectoryPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log?.Warn($"打开本地皮肤目录失败：{_localStore.DirectoryPath}", ex);
            LocalErrorText = "打开本地皮肤目录失败。";
        }
    }

    /// <summary>删除一行本地皮肤。文件删不掉时保留这一行，并把原因写在页签里。</summary>
    [RelayCommand]
    private async Task DeleteLocalAsync(LocalSkinEntryViewModel? entry)
    {
        if (entry is null || _localStore is null || IsDisposed)
        {
            return;
        }

        var result = await _localStore.DeleteAsync(entry.FileName).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            LocalErrorText = result.ErrorText;
            return;
        }

        LocalErrorText = null;
        _localThumbnailCache.Remove(entry.FileName);

        var index = LocalItems.IndexOf(entry);
        if (ReferenceEquals(SelectedLocal, entry))
        {
            SelectedLocal = null;
        }

        LocalItems.Remove(entry);

        // 删完顺手把选中挪到下一行，用户连删时不用每次重新点。
        if (index >= 0 && LocalItems.Count > 0)
        {
            SelectedLocal = LocalItems[Math.Min(index, LocalItems.Count - 1)];
        }
    }

    /// <summary>网络页签那一行的「下载到本地」：拉原图、存进本地目录、刷新本地列表。</summary>
    [RelayCommand]
    private async Task DownloadToLocalAsync(SkinEntryViewModel? entry)
    {
        if (entry is null || _localStore is null || IsDisposed || entry.IsSavingToLocal)
        {
            if (entry is not null && _localStore is null)
            {
                entry.LocalState = "本地皮肤库不可用";
            }

            return;
        }

        entry.IsSavingToLocal = true;
        entry.LocalState = "正在保存…";
        var token = _pageCancellation?.Token ?? CancellationToken.None;

        try
        {
            var info = await _library.InfoAsync(entry.Tid, token).ConfigureAwait(true);
            if (!info.Succeeded || info.Value is not { } detail)
            {
                entry.LocalState = info.ErrorText ?? "拿不到这套皮肤的详情";
                return;
            }

            var png = await _library.DownloadAsync(_library.TextureUrl(detail.Hash), token).ConfigureAwait(true);
            if (png is null)
            {
                entry.LocalState = "皮肤图下载失败";
                return;
            }

            var fileName = string.IsNullOrWhiteSpace(entry.Name) ? "skin" : entry.Name;
            var model = entry.Model == SkinModelKind.Alex ? LocalSkinModel.Slim : LocalSkinModel.Classic;
            var result = await _localStore.SaveAsync(fileName, png, model).ConfigureAwait(true);

            // 站点把细手臂皮肤标成 Alex，但图可能不是 64×64：退成经典模型仍然能显示，别让用户点了个寂寞。
            if (!result.IsSuccess && model == LocalSkinModel.Slim)
            {
                result = await _localStore.SaveAsync(fileName, png, LocalSkinModel.Classic).ConfigureAwait(true);
            }

            if (!result.IsSuccess)
            {
                entry.LocalState = result.ErrorText;
                return;
            }

            entry.LocalState = "已存到本地";
            await LoadLocalAsync().ConfigureAwait(true);
            if (result.Entry is { } saved)
            {
                SelectedLocal = LocalItems.FirstOrDefault(
                    item => string.Equals(item.FileName, saved.FileName, StringComparison.OrdinalIgnoreCase)) ?? SelectedLocal;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"下载皮肤到本地失败：{entry.Name}", ex);
            entry.LocalState = "保存失败";
        }
        finally
        {
            entry.IsSavingToLocal = false;
        }
    }

    /// <summary>视图选完文件后调这里。路径由视图层从文件选择器拿到（VM 不碰 Avalonia 的对话框）。</summary>
    public async Task ImportLocalSkinAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsDisposed)
        {
            return;
        }

        if (_localStore is null)
        {
            LocalErrorText = "本地皮肤库没有初始化，看看数据目录配置。";
            return;
        }

        var result = await _localStore.ImportAsync(path).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            LocalErrorText = result.ErrorText;
            return;
        }

        LocalErrorText = null;
        await LoadLocalAsync().ConfigureAwait(true);

        if (result.Entry is { } imported)
        {
            SelectedLocal = LocalItems.FirstOrDefault(
                item => string.Equals(item.FileName, imported.FileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task LoadLocalAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_localStore is null)
        {
            _hasLoadedLocalOnce = true;
            LocalErrorText = "本地皮肤库没有初始化，看看数据目录配置。";
            NotifyLocalState();
            return;
        }

        var cancellation = new CancellationTokenSource();
        Replace(ref _localCancellation, cancellation);
        IsLocalLoading = true;

        try
        {
            var result = await _localStore.ScanAsync(cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            _hasLoadedLocalOnce = true;
            if (result.HasError)
            {
                LocalErrorText = result.ErrorText;
                NotifyLocalState();
                return;
            }

            LocalErrorText = null;
            ReplaceLocalItems(result.Entries);
            NotifyLocalState();
            StartLocalThumbnailLoading();
        }
        catch (OperationCanceledException)
        {
            // 切页或关页面：结果作废，不覆盖当前列表。
        }
        finally
        {
            if (ReferenceEquals(_localCancellation, cancellation))
            {
                IsLocalLoading = false;
            }
        }
    }

    private void ReplaceLocalItems(IReadOnlyList<LocalSkinEntry> entries)
    {
        // 重扫会换一批视图模型，选中项按文件名接回去：同名同图，用户不该感觉「选中没了」。
        var selectedName = SelectedLocal?.FileName;

        LocalItems.Clear();
        foreach (var entry in entries)
        {
            LocalItems.Add(new LocalSkinEntryViewModel(entry));
        }

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            SelectedLocal = LocalItems.FirstOrDefault(
                item => string.Equals(item.FileName, selectedName, StringComparison.OrdinalIgnoreCase));
        }

        // 只有停在本地页签时才替用户选第一套：从网络页签进页面时本地列表也在后台扫完，
        // 这时候抢预览会把用户正在看的网络皮肤顶掉。
        if (IsLocalTab && SelectedLocal is null)
        {
            SelectedLocal = LocalItems.FirstOrDefault();
        }
    }

    private void StartLocalThumbnailLoading()
    {
        foreach (var item in LocalItems.ToList())
        {
            _ = LoadLocalThumbnailAsync(item);
        }
    }

    private async Task LoadLocalThumbnailAsync(LocalSkinEntryViewModel item)
    {
        if (_localThumbnailCache.TryGetValue(item.FileName, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }

        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(item.FilePath)).ConfigureAwait(true);
            if (IsDisposed || !LocalItems.Contains(item))
            {
                return;
            }

            var bitmap = DecodeBitmap(bytes);
            if (bitmap is null)
            {
                return;
            }

            _localThumbnailCache[item.FileName] = bitmap;
            item.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单张图读不了不影响列表：那一行继续显示占位块。
            _log?.Warn($"读取本地皮肤缩略图失败：{item.FileName}", ex);
        }
    }

    /// <summary>选中本地一行：右侧 3D 卡直接读磁盘上的原图，不走网络。</summary>
    private async Task PreviewLocalAsync(LocalSkinEntryViewModel? item)
    {
        if (item is null || IsDisposed)
        {
            return;
        }

        // 与网络预览共用同一条取消线：最后点的那一行赢。
        var cancellation = new CancellationTokenSource();
        Replace(ref _previewCancellation, cancellation);

        PreviewName = item.Name;
        PreviewDetail = item.DetailLabel;
        IsPreviewLoading = true;

        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(item.FilePath), cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            var texture = SkinTextureLoader.DecodePng(bytes, _log);
            if (texture is null)
            {
                PreviewDetail = "皮肤图无法解码";
                return;
            }

            PreviewVisual = SkinTextureLoader.CreateVisual(texture, item.Model == LocalSkinModel.Slim);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PreviewDetail = "读不了这个皮肤文件";
            _log?.Warn($"读取本地皮肤失败：{item.FilePath}", ex);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    private void NotifyLocalState()
    {
        OnPropertyChanged(nameof(HasLocalItems));
        OnPropertyChanged(nameof(IsLocalEmpty));
        OnPropertyChanged(nameof(ShowLocalLoadingPlaceholder));
        OnPropertyChanged(nameof(LocalSummary));
    }

    [RelayCommand]
    private void CloseError() => ErrorText = null;

    [RelayCommand]
    private Task RetryAsync() => LoadPageAsync(Math.Max(1, PageNumber));

    [RelayCommand]
    private Task SearchAsync()
    {
        PageNumber = 1;
        return LoadPageAsync(1);
    }

    [RelayCommand]
    private Task NextPageAsync() => IsLastPage ? Task.CompletedTask : LoadPageAsync(PageNumber + 1);

    [RelayCommand]
    private Task PreviousPageAsync() => PageNumber <= 1 ? Task.CompletedTask : LoadPageAsync(PageNumber - 1);

    private static string ResolveBaseAddress()
    {
        var configured = Environment.GetEnvironmentVariable("NEBULA_SKIN_LIBRARY");
        return string.IsNullOrWhiteSpace(configured) ? SkinLibraryClient.DefaultBaseAddress : configured.Trim();
    }

    /// <summary>
    /// 拉一页。失败时**不清空**列表：断网时已缓存的行还要能看、能点，
    /// 错误只以顶部错误条的形式出现（票 41 的三态要求）。
    /// </summary>
    private async Task LoadPageAsync(int page)
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _pageCancellation, cancellation);

        IsLoading = true;
        ErrorText = null;

        try
        {
            var result = await _library
                .ListAsync(page, SkinLibraryFilter.All, Keyword, "time", cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded || result.Value is not { } paged)
            {
                _hasLoadedOnce = true;
                _lastErrorIsNetwork = result.IsNetworkError;
                ErrorText = result.ErrorText ?? "连不上皮肤站，检查网络后重试。";
                OnPropertyChanged(nameof(ErrorBadge));
                OnPropertyChanged(nameof(IsEmpty));

                if (!result.IsNetworkError)
                {
                    // 站点连上了但不像皮肤库：把 /api 报的版本带出来，比只说「异常」有用得多。
                    // 这也是票面要求的「接入自建站前先看 blessing_skin 版本」的落点。
                    var version = await _library.ProbeVersionAsync(cancellation.Token).ConfigureAwait(true);
                    if (!cancellation.IsCancellationRequested && version is not null)
                    {
                        ErrorText = $"{ErrorText} 站点 API 版本 {version}。";
                    }
                }

                return;
            }

            ReplaceItems(paged);
            PageNumber = paged.Page;
            IsLastPage = paged.IsLastPage;
            _hasLoadedOnce = true;
            SetSubtitle(paged.Items.Count == 0
                ? "这一页没有皮肤"
                : $"{LibraryAddress} · 每页 {SkinLibraryClient.PageSize} 套");

            SyncSelectionAfterLoad();
            StartThumbnailLoading();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_pageCancellation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    private void ReplaceItems(SkinLibraryPage page)
    {
        Items.Clear();

        foreach (var item in page.Items)
        {
            var entry = new SkinEntryViewModel(item);
            if (_thumbnailCache.TryGetValue(item.Tid, out var thumbnail))
            {
                entry.Thumbnail = thumbnail;
            }

            Items.Add(entry);
        }
    }

    /// <summary>
    /// 翻页或搜索之后，选中行必须落在新一页里：旧 entry 已经不在 <see cref="Items"/> 中，
    /// 若放着不管，右侧 3D 卡会一直显示上一页的皮肤（判据 8 要求点任一行立刻换成它）。
    /// </summary>
    private void SyncSelectionAfterLoad()
    {
        if (Items.Count == 0)
        {
            Selected = null;
            PreviewName = "还没有选中皮肤";
            PreviewDetail = "点左侧任意一行，这里立刻换成它";
            PreviewVisual = SkinVisual.Default;
            return;
        }

        if (Selected is null || !Items.Contains(Selected))
        {
            Selected = Items[0];
        }
    }

    private void StartThumbnailLoading()
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _thumbnailsCancellation, cancellation);
        _ = RunThumbnailLoadingAsync(cancellation.Token);
    }

    /// <summary>后台任务没有观察者，异常必须在这里就地消化（PITFALLS 2.8）。</summary>
    private async Task RunThumbnailLoadingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadThumbnailsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 切页 / 翻页打断，正常路径。
        }
        catch (Exception ex)
        {
            _log?.Warn("加载皮肤缩略图失败", ex);
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentThumbnails);
        var pending = Items.Where(entry => !entry.HasThumbnail).ToList();

        var tasks = pending.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);

            try
            {
                await LoadThumbnailAsync(entry, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn($"加载皮肤缩略图失败：{entry.Tid}", ex);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task LoadThumbnailAsync(SkinEntryViewModel entry, CancellationToken cancellationToken)
    {
        // 站点的 /preview/{tid} 是最省事的缩略图（一张图搞定）。解不出来（比如 WebP 不被支持）
        // 就退回原始纹理 PNG —— 至少配色认得出来，比一行空白强。
        var bitmap = await TryLoadBitmapAsync(_library.PreviewUrl(entry.Tid), cancellationToken).ConfigureAwait(true);
        bitmap ??= await TryLoadSkinTextureBitmapAsync(entry.Tid, cancellationToken).ConfigureAwait(true);

        if (bitmap is null || cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        entry.Thumbnail = bitmap;
        _thumbnailCache[entry.Tid] = bitmap;
    }

    private async Task<Bitmap?> TryLoadSkinTextureBitmapAsync(int tid, CancellationToken cancellationToken)
    {
        var info = await _library.InfoAsync(tid, cancellationToken).ConfigureAwait(true);
        if (!info.Succeeded || info.Value is not { } detail)
        {
            return null;
        }

        return await TryLoadBitmapAsync(_library.TextureUrl(detail.Hash), cancellationToken).ConfigureAwait(true);
    }

    private async Task<Bitmap?> TryLoadBitmapAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await _library.DownloadAsync(url, cancellationToken).ConfigureAwait(true);
        return bytes is null ? null : DecodeBitmap(bytes);
    }

    private Bitmap? DecodeBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            // 缩略图解不出来不是错误态：行里继续显示占位块。
            _log?.Debug($"缩略图解码失败：{ex.GetType().Name}");
            return null;
        }
    }

    private async Task PreviewAsync(SkinEntryViewModel? entry)
    {
        if (entry is null || IsDisposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        Replace(ref _previewCancellation, cancellation);

        PreviewName = entry.Name;
        PreviewDetail = $"{entry.ModelLabel} · {entry.LikesLabel}";

        if (_previewCache.TryGetValue(entry.Tid, out var cachedTexture))
        {
            PreviewVisual = BuildVisual(entry, cachedTexture);
            IsPreviewLoading = false;
            return;
        }

        IsPreviewLoading = true;

        try
        {
            // 先看落盘：断网时站点详情与纹理都拉不到，但上一次预览过的那套还能直接从磁盘画出来。
            if (await TryUseStoredTextureAsync(entry, cancellation.Token).ConfigureAwait(true))
            {
                return;
            }

            var info = await _library.InfoAsync(entry.Tid, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!info.Succeeded || info.Value is not { } detail)
            {
                PreviewDetail = info.ErrorText ?? "拿不到这套皮肤的详情";
                return;
            }

            var png = await _library.DownloadAsync(_library.TextureUrl(detail.Hash), cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (png is null)
            {
                PreviewDetail = "皮肤图下载失败";
                return;
            }

            var texture = SkinTextureLoader.DecodePng(png, _log);
            if (texture is null)
            {
                PreviewDetail = "皮肤图无法解码";
                return;
            }

            _previewCache[entry.Tid] = texture;

            // 落盘与预览解耦：写不进去只是下次还得重下，不能让预览跟着失败。
            if (_store is not null)
            {
                await _store.SaveTidAsync(
                    _libraryScope,
                    entry.Tid,
                    detail.Hash,
                    png,
                    new SkinTextureMeta(detail.Hash, ModelTextOf(entry), _library.TextureUrl(detail.Hash)),
                    cancellation.Token).ConfigureAwait(true);
            }

            PreviewVisual = BuildVisual(entry, texture);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>
    /// 试读落盘纹理：命中就直接出预览。详情栏已经在选行时写好了模型与点赞数，
    /// 所以断网也能画出来，连详情请求都不用发。磁盘上没有或文件已坏一律返 false，交回网络流程。
    /// </summary>
    private async Task<bool> TryUseStoredTextureAsync(SkinEntryViewModel entry, CancellationToken cancellationToken)
    {
        var path = _store?.TryGetTidPath(_libraryScope, entry.Tid);
        if (path is null)
        {
            return false;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
            var texture = SkinTextureLoader.DecodePng(bytes, _log);
            if (texture is null)
            {
                return false;
            }

            _previewCache[entry.Tid] = texture;
            PreviewVisual = BuildVisual(entry, texture);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Debug($"离线皮肤纹理读取失败：{entry.Tid}（{ex.GetType().Name}）");
            return false;
        }
    }

    private static string ModelTextOf(SkinEntryViewModel entry) =>
        entry.Model == SkinModelKind.Alex ? "slim" : "classic";

    private static SkinVisual BuildVisual(SkinEntryViewModel entry, ArgbSkinTexture texture) =>
        SkinTextureLoader.CreateVisual(texture, entry.Model == SkinModelKind.Alex);

    private void AttachAccounts()
    {
        if (_subscribed || _accounts is null)
        {
            return;
        }

        _accounts.AccountsChanged += OnAccountsChanged;
        _subscribed = true;
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyAccount(_accounts?.ActiveAccount));
            return;
        }

        ApplyAccount(_accounts?.ActiveAccount);
    }

    private void ApplyAccount(Account? account)
    {
        HasAccount = account is not null;

        // 没有账户时标题与徽标不能都写「未登录」，那样同一行重复两遍同一个词；
        // 标题说状态，徽标说类型位还在（票面要求这一行永远显示类型徽标的位置）。
        AccountName = account is null || string.IsNullOrWhiteSpace(account.Username) ? "还没有账户" : account.Username!;
        AccountBadge = account is null ? "未登录" : AccountTypeDisplay.ShortBadgeOf(account.Type);
        AccountDetail = account is null
            ? "皮肤库不用登录也能逛"
            : AccountTypeDisplay.DetailOf(account, $"UUID {ShortUuid(account.Uuid)}");

        // 换人（或换类型）之后，官方区的按钮可用性与体型单选都必须跟着变，
        // 否则会出现「切到离线账户了，上传按钮还亮着」。
        Official = OfficialSkinActions.Of(account);
        OnPropertyChanged(nameof(IsClassicModel));
        OnPropertyChanged(nameof(IsSlimModel));
        OnPropertyChanged(nameof(OfficialCapeText));

        // 皮肤站区同理。换账户先回到「能力未知」：上一个角色的 uploadableTextures
        // 不能拿来给新角色点亮披风按钮（那是越权猜测）。
        Authlib = AuthlibSkinActions.Of(account);
        StartAuthlibCapabilityLoad(account);
    }

    /// <summary>
    /// 账户变化后重读一次角色能力。在途请求先取消：切账户时上一个账户的响应
    /// 回来后会把新账户的按钮点亮成它的权限。
    /// </summary>
    private void StartAuthlibCapabilityLoad(Account? account)
    {
        Cancel(ref _authlibProfileCancellation);

        if (account is not { Type: AccountType.Authlib })
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _authlibProfileCancellation = cancellation;
        _ = LoadAuthlibCapabilitiesAsyncSafely(account, cancellation);
    }

    /// <summary>后台任务没有观察者，异常必须就地消化（PITFALLS 2.8）。</summary>
    private async Task LoadAuthlibCapabilitiesAsyncSafely(Account account, CancellationTokenSource cancellation)
    {
        try
        {
            await LoadAuthlibCapabilitiesAsync(account, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Warn("读取皮肤站角色能力失败", ex);
        }
    }

    private static string ShortUuid(string? uuid) =>
        string.IsNullOrWhiteSpace(uuid) ? "未记录" : uuid[..Math.Min(8, uuid.Length)];

    private void SetSubtitle(string? value)
    {
        if (string.Equals(_subtitle, value, StringComparison.Ordinal))
        {
            return;
        }

        _subtitle = value;
        OnPropertyChanged(nameof(Subtitle));
    }

    private static void Replace(ref CancellationTokenSource? field, CancellationTokenSource next)
    {
        var previous = Interlocked.Exchange(ref field, next);
        previous?.Cancel();
        previous?.Dispose();
    }

    private static void Cancel(ref CancellationTokenSource? field)
    {
        var previous = Interlocked.Exchange(ref field, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    partial void OnIsNetworkTabChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocalTab));

        // 从本地切回网络：把预览卡还给网络列表当前选中的那套，别让右侧停在本地皮肤上。
        if (value)
        {
            _ = PreviewAsync(Selected);
        }
        else
        {
            _ = PreviewLocalAsync(SelectedLocal);
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingPlaceholder));
    }

    partial void OnPageNumberChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(PageLabel));
    }

    partial void OnIsLastPageChanged(bool value) => OnPropertyChanged(nameof(CanGoNext));

    partial void OnErrorTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(EmptyHint));
    }

    partial void OnSelectedChanged(SkinEntryViewModel? value)
    {
        // 预览卡只有一张，而两个页签的列表都会在后台加载完并设选中项。
        // 用当前页签决定谁有资格写它，否则成了「谁后到谁赢」——
        // 进页面时先扫本地、后拉网络，右侧就会停在网络皮肤上。
        if (IsNetworkTab)
        {
            _ = PreviewAsync(value);
        }
    }

    partial void OnSelectedLocalChanged(LocalSkinEntryViewModel? value)
    {
        if (IsLocalTab)
        {
            _ = PreviewLocalAsync(value);
        }
    }

    partial void OnIsLocalLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocalEmpty));
        OnPropertyChanged(nameof(ShowLocalLoadingPlaceholder));
    }

    partial void OnLocalErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasLocalError));

    partial void OnOfficialChanged(OfficialSkinCapability value)
    {
        OnPropertyChanged(nameof(OfficialCapeText));
        OnPropertyChanged(nameof(IsClassicModel));
        OnPropertyChanged(nameof(IsSlimModel));
    }

    partial void OnOfficialFeedbackChanged(SkinUploadFeedback? value)
    {
        OnPropertyChanged(nameof(HasOfficialFeedback));
        OnPropertyChanged(nameof(IsOfficialFeedbackError));
        OnPropertyChanged(nameof(CanRetryOfficial));
        OnPropertyChanged(nameof(GoToAccountPageVisible));
        OnPropertyChanged(nameof(OfficialFeedbackText));
    }

    partial void OnOfficialCooldownTextChanged(string? value) => OnPropertyChanged(nameof(HasOfficialCooldown));

    partial void OnIsOfficialBusyChanged(bool value)
    {
        // 在途时连重试按钮也一起禁掉：否则一次点击会同时发出两条请求。
        OnPropertyChanged(nameof(CanRetryOfficial));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachAccounts();

            Cancel(ref _pageCancellation);
            Cancel(ref _thumbnailsCancellation);
            Cancel(ref _previewCancellation);
            Cancel(ref _localCancellation);
            Cancel(ref _officialCancellation);
            Cancel(ref _cooldownCancellation);

            // 皮肤站通道的四条在途线也要各自收尾，否则关页面后还在发请求。
            Cancel(ref _authlibCancellation);
            Cancel(ref _authlibCooldownCancellation);
            Cancel(ref _authlibProfileCancellation);

            _library.Dispose();
            _mojang?.Dispose();
            _authlibClient?.Dispose();

            foreach (var bitmap in _thumbnailCache.Values)
            {
                bitmap.Dispose();
            }

            foreach (var bitmap in _localThumbnailCache.Values)
            {
                bitmap.Dispose();
            }

            _thumbnailCache.Clear();
            _previewCache.Clear();
            _localThumbnailCache.Clear();

            _accounts = null;
            _log = null;
            _localStore = null;
            _platform = null;
            _mojang = null;
            _authlibClient = null;
            _skinChanges = null;
            _navigation = null;
            _officialRetry = null;
            _authlibRetry = null;
        }

        base.Dispose(disposing);
    }
}
