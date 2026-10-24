using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Services;

// 命名空间 NebulaLauncher.Converters 里有同名静态类 Converters，用别名避免二义。
using Conv = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 在线资源页：按玩家在页内显式选择的<b>安装目标</b>（实例 / 已安装版本 / 全局目录）的 Minecraft 版本与加载器
/// 搜索 Modrinth / CurseForge，把选中的版本装进该目标的游戏目录。
///
/// 为什么目标必须在页内选：旧实现从「当前实例」或 <see cref="AppSettings.LastSelectedVersion"/> 反推筛选条件，
/// 玩家在资源页没有做过任何选择，却会看到「筛选：Minecraft 1.7.10」——筛选来源不可见、也无法在页内改。
/// 现在「将装到」是一个下拉框：条目来自 <see cref="IInstanceContext.ListTargetsAsync"/>（实例 + 全局目录）
/// 与 <see cref="IVersionService.GetInstalledAsync"/>（尚未建实例的已安装版本）。
/// 选实例 → 装进实例目录；选未建实例的版本 → 该版本没有独立游戏目录，装到全局 <c>.minecraft/&lt;内容目录&gt;</c>
/// 并在同一行写明落点；选全局目录 → 不按版本与加载器筛选（全局目录本身没有版本身份）。
///
/// 与 <see cref="ModsViewModel"/> 的分工：那个页面管「已经在本地的文件」，这个页面负责「把文件拿下来」。
/// 两者都必须经 <see cref="IInstanceContext"/> 取游戏目录：直接读 <see cref="ILauncherPaths.MinecraftDirectory"/>
/// 会把文件装进全局 <c>.minecraft/mods</c>，而实例启动时用的是实例目录，那份 mods 永远读不到。
///
/// 「整合包」分区是例外：它装成独立实例，与任何既有目标无关；且<b>搜索不按游戏版本筛选</b>，
/// 版本筛选放在选中包之后的版本列表里（见 <see cref="ModPackVersionFilters"/>）。
///
/// 所有服务参数可空：组合根尚未接线时页面仍要能渲染，此时退化为示例模式 / 全局目录。
/// </summary>
public sealed partial class LibraryViewModel : PageViewModelBase, IDisposable
{
    /// <summary>一次搜索取回多少条。分页用「加载更多」追加。</summary>
    private const int PageSize = 20;

    /// <summary>
    /// 双源模式下，一次「加载更多」新增不足一页时最多再补取几页（PCL2 的 TargetResultCount 循环）。
    ///
    /// 取 2 而不是无限：跨站去重通常只吃掉首页的一部分；补两页足以把「整页重复」的极端情况越过。
    /// 无上限时，一个满是重复项的搜索词会让一次点击打出成串请求。
    /// </summary>
    private const int BothSourcesMaxExtraPages = 2;

    /// <summary>图标下载并发上限。图标只是装饰，不值得占用太多连接。</summary>
    private const int IconConcurrency = 4;

    /// <summary>详情正文最多展示多少字符。Modrinth 正文动辄上万字，右栏放不下全文。</summary>
    private const int BodyPreviewLength = 2000;

    /// <summary>图库最多展示几张。4 张刚好铺一行，再多会把下面的版本列表挤出首屏。</summary>
    private const int GalleryPreviewCount = 4;

    /// <summary>没有实例时的目标名。<see cref="ContentTarget.DisplayName"/> 对全局目录用同一文案。</summary>
    private const string GlobalTargetLabel = "全局目录";

    /// <summary>全局目标在 <see cref="InstallTargets"/> 里的 Key。与 <c>InstanceContext.GlobalTargetKey</c> 同值。</summary>
    private const string GlobalTargetKey = "global";

    /// <summary>「未建实例的已安装版本」在 <see cref="InstallTargets"/> 里的 Key 前缀。</summary>
    private const string VersionTargetPrefix = "version:";

    private readonly IModrinthService? _modrinth;
    private readonly ICurseForgeService? _curseforge;
    private readonly IInstanceContext? _instanceContext;
    private readonly INavigationService? _navigation;
    private readonly ILauncherPaths? _paths;
    private readonly ISettingsService? _settings;
    private readonly IVersionService? _versions;
    private readonly IPlatformService? _platform;

    /// <summary>日志。安装链的未知异常只在这里留一份可诊断的堆栈，界面仍给一句可读文案。</summary>
    private readonly IAppLog? _log;

    /// <summary>整合包导入服务。「整合包」分区装的是实例，不是往当前实例里塞文件。</summary>
    private readonly IModpackService? _modpack;

    /// <summary>整合包下载走普通下载批次：.mrpack 会出现在下载任务面板里，与其它下载同一条链路。</summary>
    private readonly DownloadQueue? _downloadQueue;

    /// <summary>整合包在线目录。搜索按 <c>project_type:modpack</c>，与 <see cref="ContentKind"/> 无关。</summary>
    private readonly ModpackCatalogClient? _modpackCatalog;

    /// <summary>
    /// <see cref="_modpackCatalog"/> 是否由本页创建。注册表里给了替身（测试注入假 handler）时不能释放它：
    /// 容器持有的是单例，页面只是使用者。
    /// </summary>
    private readonly bool _ownsModpackCatalog;

    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _versionCts;
    private CancellationTokenSource? _detailCts;

    private bool _settingsLoaded;
    private bool _sourceInitialized;
    private bool _initialized;
    private string? _gameVersion;
    private LoaderKind _loader = LoaderKind.Vanilla;

    /// <summary>单源搜索的分页偏移（Modrinth 的 offset / CurseForge 的 index 语义一致）。</summary>
    private int _offset;

    /// <summary>
    /// 双源分页偏移：两站各自按<b>实际返回条数</b>推进，与 PCL2 的
    /// <c>CurseForgeOffset</c> / <c>ModrinthOffset</c> 一致（ResourceSearcher.vb:333/376）。
    ///
    /// 为什么不能共用 <see cref="_offset"/>：某一侧提前返回不足一页时，共用偏移会让另一侧
    /// 跳着取，漏掉中间结果。分开记之后，每侧都从自己上次停下的位置继续。
    /// </summary>
    private int _modrinthOffset;
    private int _curseForgeOffset;

    /// <summary>
    /// 某一侧返回过不足一页就说明它没有后续了，下一页不必再为它发请求。
    /// 两站的耗尽状态必须分开记：一侧走完时另一侧还要继续。
    /// </summary>
    private bool _modrinthExhausted;

    private bool _curseForgeExhausted;

    /// <summary>当前生效的游戏目录（实例目录或全局 .minecraft）。页面进入时解析一次。</summary>
    private string? _gameDirectory;

    /// <summary>当前生效目标的展示名；null 表示全局目录。</summary>
    private string? _targetName;

    public LibraryViewModel(IServiceProvider services)
    {
        _modrinth = services.GetService(typeof(IModrinthService)) as IModrinthService;
        _curseforge = services.GetService(typeof(ICurseForgeService)) as ICurseForgeService;
        _instanceContext = services.GetService(typeof(IInstanceContext)) as IInstanceContext;
        _navigation = services.GetService(typeof(INavigationService)) as INavigationService;
        _paths = services.GetService(typeof(ILauncherPaths)) as ILauncherPaths;
        _settings = services.GetService(typeof(ISettingsService)) as ISettingsService;
        _versions = services.GetService(typeof(IVersionService)) as IVersionService;
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;
        _log = services.GetService(typeof(IAppLog)) as IAppLog;

        // 整合包分区只复用已有的导入链路与下载队列，不引入第二套进度模型：
        // 缺任何一个服务都让该分区退化为「示例数据」而不是抛异常。
        _modpack = services.GetService(typeof(IModpackService)) as IModpackService;
        _downloadQueue = services.GetService(typeof(DownloadQueue)) as DownloadQueue;

        // 测试要能注入带假 handler 的目录客户端（否则每条整合包搜索用例都会打真实网络）。
        // 生产路径没有这项注册，照旧自己 new 一个。
        var catalog = services.GetService(typeof(ModpackCatalogClient)) as ModpackCatalogClient;
        _modpackCatalog = catalog ?? (_modpack is null ? null : new ModpackCatalogClient());
        _ownsModpackCatalog = catalog is null;

        IsDemoMode = _modrinth is null;

        // 初始值直接赋字段，不走 setter：走属性会在构造期触发一次网络请求。
        _selectedSort = SortOptions[0];
        _sortInitialized = true;
    }

    public override string Title => "资源";

    public override string Key => NavigationKeys.Library;

    private string? _subtitle;

    /// <summary>基类 Subtitle 只有 getter，这里用私有方法 + 显式通知更新。</summary>
    public override string? Subtitle => _subtitle;

    private void SetSubtitle(string? value)
    {
        if (string.Equals(_subtitle, value, StringComparison.Ordinal))
        {
            return;
        }

        _subtitle = value;
        OnPropertyChanged(nameof(Subtitle));
    }

    // ---------------- 分类与筛选 ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModTabActive))]
    [NotifyPropertyChangedFor(nameof(IsResourcePackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsShaderPackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsDataPackTabActive))]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    [NotifyPropertyChangedFor(nameof(InstallTargetHint))]
    [NotifyPropertyChangedFor(nameof(InstallTargetPath))]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private ContentKind _activeKind = ContentKind.Mod;

    /// <summary>
    /// 是否停在「整合包」分区。
    ///
    /// 刻意<b>不</b>给 <see cref="ContentKind"/> 加 <c>ModPack</c> 值：那个枚举是本地内容目录的键，
    /// 加一个值要同步改 <c>DirectoryNameFor</c>、<c>ContentTarget.DirectoryNameOf</c>、扫描、
    /// 版本不匹配检查等 6 处 switch（HANDOVER §9），而整合包根本不落在内容目录里。
    /// 它是「装成实例」的，与另外四个分类的落点模型不同，所以单独一个分区标志。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModTabActive))]
    [NotifyPropertyChangedFor(nameof(IsResourcePackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsShaderPackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsDataPackTabActive))]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    [NotifyPropertyChangedFor(nameof(InstallTargetHint))]
    [NotifyPropertyChangedFor(nameof(InstallTargetPath))]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyPropertyChangedFor(nameof(InstallTargetName))]
    [NotifyPropertyChangedFor(nameof(ShowsModPackSearchNotice))]
    [NotifyPropertyChangedFor(nameof(ShowsModPackVersionFilter))]
    [NotifyPropertyChangedFor(nameof(FilterLimitedHint))]
    [NotifyPropertyChangedFor(nameof(SupportsSort))]
    private bool _isModPackTabActive;

    public bool IsModTabActive => !IsModPackTabActive && ActiveKind == ContentKind.Mod;

    public bool IsResourcePackTabActive => !IsModPackTabActive && ActiveKind == ContentKind.ResourcePack;

    public bool IsShaderPackTabActive => !IsModPackTabActive && ActiveKind == ContentKind.ShaderPack;

    /// <summary>数据包分类。它的安装位置在点击安装后再选（全局或某个存档）。</summary>
    public bool IsDataPackTabActive => !IsModPackTabActive && ActiveKind == ContentKind.DataPack;

    public string KindLabel => IsModPackTabActive ? "整合包" : KindLabelOf(ActiveKind);

    /// <summary>安装目标目录的相对名，例如 <c>mods/</c>。整合包装成版本目录，不在内容子目录里。</summary>
    public string InstallTargetHint => IsModPackTabActive ? "versions/" : KindDirectoryOf(ActiveKind) + "/";

    /// <summary>安装目标的完整绝对路径，例如 <c>D:\Minecraft\.minecraft\versions\1.20.1\mods</c>。界面上必须显示完整路径。</summary>
    public string InstallTargetPath
    {
        get
        {
            if (IsModPackTabActive)
            {
                var versionsDirectory = _paths?.VersionsDirectory;

                return string.IsNullOrWhiteSpace(versionsDirectory)
                    ? "（尚未确定游戏目录）"
                    : versionsDirectory;
            }

            var directory = ResolveGameDirectory();

            return directory is null
                ? "（尚未确定游戏目录）"
                : Path.Combine(directory, KindDirectoryOf(ActiveKind));
        }
    }

    /// <summary>安装目标名：实例名；没有实例时是「全局目录」。整合包装成新实例，与当前实例无关。</summary>
    public string InstallTargetName => IsModPackTabActive
        ? "新实例"
        : string.IsNullOrWhiteSpace(_targetName) ? GlobalTargetLabel : _targetName;

    /// <summary>安装按钮文案。CurseForge 未开放第三方下载的文件只能去网页。</summary>
    public string InstallButtonText => SelectedVersion is { IsDownloadable: false }
        ? "去网页下载"
        : IsModPackTabActive
            ? "下载并导入为新实例"
            : ActiveKind == ContentKind.DataPack
                ? "下载并选择安装位置…"
                : $"下载并安装到 {InstallTargetHint}";

    // ---------------- 安装目标（页内显式选择） ----------------

    /// <summary>
    /// 可选的安装目标：实例（来自 <see cref="IInstanceContext.ListTargetsAsync"/>）、
    /// 尚未建实例的已安装版本（来自 <see cref="IVersionService.GetInstalledAsync"/>）、全局目录。
    ///
    /// 三者都是「玩家看得见、选得到」的条目。旧实现把「当前实例 / 上次版本」当成隐式输入，
    /// 页面上只有一句「筛选：Minecraft 1.7.10」——玩家既不知道它从哪来，也没法在页内改。
    /// </summary>
    public ObservableCollection<LibraryInstallTargetViewModel> InstallTargets { get; } = [];

    /// <summary>
    /// 全部目标（含原版）。<see cref="InstallTargets"/> 是它按当前分区过滤后的投影：
    /// 模组与光影分区不列原版目标，因为原版游戏没有加载器，装了也不会被读取。
    /// </summary>
    private readonly List<LibraryInstallTargetViewModel> _allInstallTargets = [];

    /// <summary>
    /// 当前选中的安装目标。切换后筛选条件与落盘目录一起跟着变，并按新条件重搜。
    /// 只在本次会话内记忆（页面实例被缓存复用，跨启动不保留）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallTargetName))]
    [NotifyPropertyChangedFor(nameof(InstallTargetPath))]
    [NotifyPropertyChangedFor(nameof(InstallTargetDetail))]
    private LibraryInstallTargetViewModel? _selectedInstallTarget;

    /// <summary>选中目标的落点说明：实例目录 / 全局 <c>.minecraft</c> / 未建实例的版本。</summary>
    public string InstallTargetDetail => SelectedInstallTarget?.Detail ?? string.Empty;

    /// <summary>setter 触发前的目标 Key；用于区分「真的换了目标」与「同一目标换了包装」。</summary>
    private string? _installTargetKeyBeforeChange;

    /// <summary>
    /// 重建 <see cref="InstallTargets"/> 期间抑制目标变化处理。
    ///
    /// ComboBox 在 ItemsSource 被清空时会把 SelectedItem 写回 null，这个中间态会被当成「用户取消了选择」：
    /// 不做抑制就会在每次进页面时触发一次搜索。抑制期间只重建集合，恢复后由调用方统一重新选中。
    /// </summary>
    private bool _suppressInstallTargetChanges;

    /// <summary>
    /// 过滤目标列表期间抑制「换目标 → 重搜」：分区的搜索由切换分区的命令自己发起，
    /// 过滤顺带改选的目标不应再打一次请求。
    /// </summary>
    private bool _suppressTargetSearch;

    /// <summary>重建结束后用来判断「恢复出来的目标是不是原来那个」。</summary>
    private string? _installTargetKeyToRestore;

    partial void OnSelectedInstallTargetChanging(LibraryInstallTargetViewModel? value)
    {
        if (!_suppressInstallTargetChanges)
        {
            _installTargetKeyBeforeChange = SelectedInstallTarget?.Key;
        }
    }

    partial void OnSelectedInstallTargetChanged(LibraryInstallTargetViewModel? value)
    {
        if (_suppressInstallTargetChanges)
        {
            return;
        }

        ApplyInstallTarget(value);
        UpdateFilterText();

        // 换目标后旧清单一律作废：清单是按旧目标的版本 / 加载器算出来的。
        ClosePlan();
        CloseDataPackTargetPicker();

        var sameTarget = string.Equals(_installTargetKeyBeforeChange, value?.Key, StringComparison.Ordinal)
                         || string.Equals(_installTargetKeyToRestore, value?.Key, StringComparison.Ordinal);

        _installTargetKeyBeforeChange = value?.Key;
        _installTargetKeyToRestore = null;

        if (!_initialized || sameTarget || _suppressTargetSearch)
        {
            return;
        }

        _searchCts?.Cancel();
        _versionCts?.Cancel();
        _ = SearchCoreAsync(reset: true);
    }

    /// <summary>
    /// 把选中目标摊到筛选与落点字段上。
    ///
    /// 全局目录没有版本身份（<see cref="ContentTarget.MinecraftVersion"/> 恒为 null），
    /// 所以它以「不筛选版本与加载器」参与：这不是缺陷，而是「全局 mods 由所有版本共用」的如实反映。
    /// </summary>
    private void ApplyInstallTarget(LibraryInstallTargetViewModel? target)
    {
        if (target is null)
        {
            _gameDirectory = _paths?.MinecraftDirectory;
            _targetName = null;
            _gameVersion = null;
            _loader = LoaderKind.Vanilla;
        }
        else
        {
            _gameDirectory = string.IsNullOrWhiteSpace(target.GameDirectory)
                ? _paths?.MinecraftDirectory
                : target.GameDirectory;
            _targetName = target.IsGlobal ? null : target.DisplayName;
            _gameVersion = string.IsNullOrWhiteSpace(target.MinecraftVersion) ? null : target.MinecraftVersion;
            _loader = target.Loader;
        }

        OnPropertyChanged(nameof(InstallTargetName));
        OnPropertyChanged(nameof(InstallTargetPath));
        OnPropertyChanged(nameof(InstallTargetHint));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(InstallTargetDetail));
    }

    /// <summary>
    /// 版本 / 加载器筛选摘要。没有确定游戏版本时会明确写「未过滤」，
    /// 而不是假装结果已经适配——装错版本的模组只会让游戏启动失败。
    /// </summary>
    [ObservableProperty]
    private string _filterText = "正在读取安装目标…";

    /// <summary>未确定版本 / 加载器时为 true，界面显示警告条。</summary>
    [ObservableProperty]
    private bool _isFilterLimited;

    /// <summary>
    /// 「结果没有按版本过滤」警告条的文案。整合包分区的搜索本来就不按版本筛，
    /// 用同一句话会与它自己的说明条重复，所以按分区给不同的句子。
    /// </summary>
    public string FilterLimitedHint => IsModPackTabActive
        ? "整合包搜索不按游戏版本筛选：先找到想要的包，再用右侧的「游戏版本」挑构建。"
        : "结果没有按版本与加载器筛选：在「将装到」里选择一个实例或已安装版本后会自动精确筛选。";

    /// <summary>实例上下文读取失败的提示（不影响页面渲染）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetNotice))]
    private string? _targetNoticeText;

    public bool HasTargetNotice => !string.IsNullOrWhiteSpace(TargetNoticeText);

    // ---------------- 数据源 ----------------

    /// <summary>当前搜索后端。默认取 <see cref="AppSettings.ContentSource"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModrinthSourceActive))]
    [NotifyPropertyChangedFor(nameof(IsCurseForgeSourceActive))]
    [NotifyPropertyChangedFor(nameof(SourceLabel))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    [NotifyPropertyChangedFor(nameof(CanInstallNow))]
    [NotifyPropertyChangedFor(nameof(SupportsSort))]
    private ContentSourceKind _activeSource = ContentSourceKind.Modrinth;

    /// <summary>
    /// 双源模式：同时查 Modrinth 与 CurseForge，合并去重后按热度混排。
    ///
    /// 为什么单独一个布尔而不是给 <see cref="ContentSourceKind"/> 加第三个值：那个枚举同时是
    /// 设置文件里的持久化取值（<c>"modrinth"</c> / <c>"curseforge"</c>）与安装分派依据
    /// （<see cref="LibraryVersionViewModel.Source"/> 决定调哪个服务的 <c>InstallAsync</c>）。
    /// 往里加一个「不是后端」的值，这两处都得为一个不存在的安装目标写分支。
    ///
    /// 不持久化：它是本次会话的搜索姿势而不是偏好，每次进页面从默认数据源重新推导。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModrinthSourceActive))]
    [NotifyPropertyChangedFor(nameof(IsCurseForgeSourceActive))]
    [NotifyPropertyChangedFor(nameof(SourceLabel))]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    [NotifyPropertyChangedFor(nameof(SupportsSort))]
    private bool _isBothSources;

    public bool IsModrinthSourceActive => !IsBothSources && ActiveSource == ContentSourceKind.Modrinth;

    public bool IsCurseForgeSourceActive => !IsBothSources && ActiveSource == ContentSourceKind.CurseForge;

    /// <summary>双源标签。两站的展示名都在这里拼，界面与摘要不会各写一份。</summary>
    public const string BothSourcesLabel = "Modrinth + CurseForge";

    /// <summary>
    /// 双源档位是否可用。两个后端都在才可用：CurseForge 的 key 已内置，
    /// 整合包分区同样支持双源（Modrinth 目录客户端 + CurseForge classId=4471 合并）。
    /// </summary>
    public bool CanUseBothSources => _curseforge is not null;

    /// <summary>双源档位的悬停说明。</summary>
    public string BothSourcesHint => "同时搜索 Modrinth 与 CurseForge，去重合并后按热度混排";

    /// <summary>当前数据源的展示名。双源不是单个后端，这里直接写两站的名字。</summary>
    public string SourceLabel => IsBothSources ? BothSourcesLabel : SourceLabelOf(ActiveSource);

    /// <summary>搜索框占位文案。</summary>
    public string SearchWatermark => "搜索名称 / 描述，留空看热门";

    partial void OnActiveSourceChanged(ContentSourceKind value) => InstallCommand.NotifyCanExecuteChanged();

    // ---------------- 排序 ----------------

    /// <summary>
    /// 排序选项。键是 Modrinth 的 <c>index</c> 取值；
    /// <c>ModrinthService.NormalizeSortIndex</c> 会做白名单校验，所以键可以放心送进查询串。
    /// </summary>
    public IReadOnlyList<LibrarySortOption> SortOptions { get; } =
    [
        new("relevance", "相关度"),
        new("downloads", "下载量"),
        new("follows", "关注数"),
        new("newest", "最新发布"),
        new("updated", "最近更新"),
    ];

    private LibrarySortOption? _selectedSort;

    private bool _sortInitialized;

    /// <summary>当前排序。切换后立即重查第一页——排序变了却要再点一次「搜索」不符合直觉。</summary>
    public LibrarySortOption? SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (ReferenceEquals(_selectedSort, value) || value is null)
            {
                return;
            }

            _selectedSort = value;
            OnPropertyChanged();

            if (_sortInitialized && SupportsSort)
            {
                _searchCts?.Cancel();
                _ = SearchCoreAsync(reset: true);
            }
        }
    }

    /// <summary>
    /// 排序只在 Modrinth 上生效：CurseForge 客户端固定 <c>sortField=2</c>（下载量），接口不接受排序键；
    /// 整合包分区走的是另一条客户端（也不接受排序）。对它们显示排序控件只会骗人。
    /// </summary>
    public bool SupportsSort => IsModrinthSourceActive && !IsModPackTabActive;

    // ---------------- 列表状态 ----------------

    public ObservableCollection<LibraryProjectViewModel> Results { get; } = [];

    public ObservableCollection<LibraryVersionViewModel> Versions { get; } = [];

    /// <summary>整合包版本列表的「游戏版本」筛选项：第一项是「全部版本」，其余来自项目声明的 MC 版本。</summary>
    public ObservableCollection<ModPackVersionFilterOption> ModPackVersionFilters { get; } = [];

    /// <summary>整合包版本列表的未过滤全集；筛选只是对它的投影，不重新请求网络。</summary>
    private readonly List<LibraryVersionViewModel> _allModPackVersions = [];

    private ModPackVersionFilterOption? _selectedModPackVersionFilter;

    /// <summary>
    /// 当前选中的游戏版本筛选项。为 null / 选中「全部版本」时显示全部构建。
    ///
    /// 为什么筛选放在这里而不是搜索：玩家找整合包的顺序是「先挑主题，再看这个包有没有我要的版本」。
    /// 搜索层按版本筛会把别的版本的同主题包全部藏起来，玩家连「有这个包」都看不到。
    /// </summary>
    public ModPackVersionFilterOption? SelectedModPackVersionFilter
    {
        get => _selectedModPackVersionFilter;
        set
        {
            if (ReferenceEquals(_selectedModPackVersionFilter, value))
            {
                return;
            }

            _selectedModPackVersionFilter = value;
            OnPropertyChanged();

            if (IsModPackTabActive)
            {
                ApplyModPackVersionFilter();
            }
        }
    }

    /// <summary>是否显示整合包的游戏版本筛选。只有一个「全部」时不必占位。</summary>
    public bool ShowsModPackVersionFilter => IsModPackTabActive && ModPackVersionFilters.Count > 1;

    /// <summary>「全部版本」这一项的文案。</summary>
    public const string AllModPackVersionsLabel = "全部版本";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoadingVersions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isDemoMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string? _infoText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private LibraryProjectViewModel? _selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallNow))]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private LibraryVersionViewModel? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionError))]
    private string? _versionErrorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionNotice))]
    private string? _versionNoticeText;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private string _emptyTitle = "搜索在线资源";

    [ObservableProperty]
    private string _emptyHint = "输入关键词后回车或点「搜索」；留空直接搜索会按下载量列出热门项目。";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasInfo => !string.IsNullOrWhiteSpace(InfoText);

    public bool HasVersionError => !string.IsNullOrWhiteSpace(VersionErrorText);

    public bool HasVersionNotice => !string.IsNullOrWhiteSpace(VersionNoticeText);

    public bool HasSelection => SelectedProject is not null;

    // ---------------- 项目详情 ----------------

    /// <summary>详情正文（已去 Markdown、已截断到 <see cref="BodyPreviewLength"/>）。空字符串表示还没拉到或项目没写正文。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectBody))]
    [NotifyPropertyChangedFor(nameof(HasProjectDetails))]
    private string _projectBody = string.Empty;

    /// <summary>
    /// 图库缩略图，最多 <see cref="GalleryPreviewCount"/> 张。
    /// 元素是本地缓存后的绝对路径（远程 URL 不能直接喂给 Image），由 PathToImageConverter 解码。
    /// </summary>
    public ObservableCollection<string> ProjectGallery { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssuesUrl))]
    [NotifyPropertyChangedFor(nameof(HasProjectDetails))]
    private string? _issuesUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceUrl))]
    [NotifyPropertyChangedFor(nameof(HasProjectDetails))]
    private string? _sourceUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLicenseId))]
    [NotifyPropertyChangedFor(nameof(HasProjectDetails))]
    private string? _licenseId;

    /// <summary>详情请求在途。它只影响详情区，不阻塞版本列表与安装按钮。</summary>
    [ObservableProperty]
    private bool _isLoadingDetails;

    public bool HasProjectBody => !string.IsNullOrWhiteSpace(ProjectBody);

    public bool HasProjectGallery => ProjectGallery.Count > 0;

    public bool HasIssuesUrl => !string.IsNullOrWhiteSpace(IssuesUrl);

    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(SourceUrl);

    public bool HasLicenseId => !string.IsNullOrWhiteSpace(LicenseId);

    /// <summary>详情区是否值得占位：四项全空时（CurseForge 项目、示例数据、详情接口失败）整块隐藏。</summary>
    public bool HasProjectDetails => HasProjectBody || HasProjectGallery || HasIssuesUrl || HasSourceUrl || HasLicenseId;

    /// <summary>没有结果（含请求失败）时显示引导卡片：错误原因由上面的提示条给出，卡片负责说明下一步。</summary>
    public bool IsEmpty => Results.Count == 0 && !IsSearching && !IsDemoMode;

    /// <summary>安装按钮可用性。服务缺失（示例模式）时不允许点；「去网页下载」只需要平台服务。</summary>
    public bool CanInstallNow
    {
        get
        {
            if (IsInstalling || SelectedVersion is null)
            {
                return false;
            }

            // 服务端插件没有「安装」这个动作：按钮直接灰掉，用户点不到才不会白等一次下载。
            // 真正的原因由 SelectedProject 的徽标（「服务端插件」）与安装区提示给出。
            if (SelectedProject?.IsServerPlugin == true)
            {
                return false;
            }

            if (!SelectedVersion.IsDownloadable)
            {
                return _platform is not null;
            }

            // 整合包需要「下载队列 + 导入服务」这一对，缺一个都装不成实例。
            if (IsModPackTabActive)
            {
                return _modpack is not null && _downloadQueue is not null;
            }

            return SelectedVersion.Source == ContentSourceKind.CurseForge
                ? _curseforge is not null
                : _modrinth is not null;
        }
    }

    /// <summary>
    /// 当前选中项是服务端插件时给用户的一行说明；否则为 null（界面据此隐藏提示条）。
    ///
    /// 与 <see cref="ServerPluginPolicy.InstallRejectedMessage"/> 同一句话：按钮灰着但不说原因，
    /// 用户只会以为启动器坏了，然后去点「去网页下载」。
    /// </summary>
    public string? ServerPluginNotice =>
        SelectedProject?.IsServerPlugin == true ? ServerPluginPolicy.InstallRejectedMessage : null;

    public bool HasServerPluginNotice => ServerPluginNotice is not null;

    // ---------------- 安装状态 ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallNow))]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _installStatus = string.Empty;

    partial void OnIsInstallingChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        ConfirmPlanCommand.NotifyCanExecuteChanged();
        ConfirmDataPackTargetCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVersionChanged(LibraryVersionViewModel? value)
    {
        InstallCommand.NotifyCanExecuteChanged();

        // 换版本后旧清单就作废了：留着会让用户对着 A 的清单确认 B 的安装。
        ClosePlan();
    }

    partial void OnSelectedProjectChanged(LibraryProjectViewModel? value)
    {
        InstallCommand.NotifyCanExecuteChanged();

        // 换成插件项目时安装按钮要立刻变灰：CanInstallNow 依赖 SelectedProject，
        // 漏了这两行会让按钮停留在上一个（可安装的）项目上的状态。
        OnPropertyChanged(nameof(CanInstallNow));
        OnPropertyChanged(nameof(ServerPluginNotice));
        OnPropertyChanged(nameof(HasServerPluginNotice));

        _ = LoadVersionsAsync(value);
        _ = LoadDetailsAsync(value);
    }

    // ---------------- 整合包导入结果 ----------------

    /// <summary>
    /// 导入结果文案。整合包导入是多阶段的（装游戏 → 解压 → 下 index 文件 → 写实例），
    /// 只说「已导入」会让用户错过「加载器没装上」这类非致命提示。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModPackResult))]
    private string? _modPackResultText;

    /// <summary>导入出来的实例目录；用于「打开实例目录」入口。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportedModPack))]
    private string? _importedModPackDirectory;

    public bool HasModPackResult => !string.IsNullOrWhiteSpace(ModPackResultText);

    public bool HasImportedModPack => !string.IsNullOrWhiteSpace(ImportedModPackDirectory);

    partial void OnIsModPackTabActiveChanged(bool value)
    {
        // 分区的落点模型不同（实例 vs 内容子目录），按钮可用性与目标路径都要重算。
        OnPropertyChanged(nameof(CanInstallNow));
        OnPropertyChanged(nameof(ModPackSearchNotice));
        OnPropertyChanged(nameof(ShowsModPackSearchNotice));
        OnPropertyChanged(nameof(ShowsModPackVersionFilter));
        InstallCommand.NotifyCanExecuteChanged();

        // 双源在两个分区都可用（整合包走 Modrinth 目录客户端 + CurseForge classId=4471）。
        OnPropertyChanged(nameof(CanUseBothSources));
        OnPropertyChanged(nameof(BothSourcesHint));
    }

    /// <summary>
    /// 整合包分区的搜索口径说明。这里必须写清「搜索不按版本筛」，
    /// 否则玩家会以为「搜不到某个版本的包」是功能坏了；版本筛选在选中包之后的版本列表里。
    /// </summary>
    public const string ModPackSearchNoticeText =
        "整合包搜索不按游戏版本筛选：先找到想要的整合包，再在右侧用「游戏版本」挑构建。";

    public string ModPackSearchNotice => ModPackSearchNoticeText;

    public bool ShowsModPackSearchNotice => IsModPackTabActive;

    /// <summary>跳到版本页看刚导入的实例。实例列表由 <see cref="IInstanceContext"/> 统一维护。</summary>
    [RelayCommand]
    private void GoToVersions() => _navigation?.Navigate(NavigationKeys.Versions);

    [RelayCommand]
    private async Task OpenModPackFolderAsync()
    {
        var directory = ImportedModPackDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        if (_platform is null)
        {
            ErrorText = "无法打开目录：平台服务尚未就绪。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(directory);
        }
        catch (Exception)
        {
            ErrorText = "打开实例目录失败。";
        }
    }

    /// <summary>切换分区 / 重新安装前清掉上一次的导入结果，避免旧路径挂在新的选择上。</summary>
    private void ClearModPackState()
    {
        ModPackResultText = null;
        ImportedModPackDirectory = null;
    }

    // ---------------- 数据包安装位置 ----------------

    /// <summary>
    /// 数据包可选落点：全局 <c>datapacks/</c> + 每个存档的 <c>saves/&lt;world&gt;/datapacks/</c>。
    /// 点击安装后填充，选完再下载——装错地方等于没装，不能靠默认值猜。
    /// </summary>
    public ObservableCollection<DataPackTargetOption> DataPackTargets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmDataPackTarget))]
    [NotifyPropertyChangedFor(nameof(HasDataPackScopeHint))]
    private bool _isDataPackTargetOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmDataPackTarget))]
    [NotifyPropertyChangedFor(nameof(SelectedDataPackTargetPath))]
    private DataPackTargetOption? _selectedDataPackTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataPackTargetError))]
    private string? _dataPackTargetError;

    /// <summary>等待选择落点的版本；确认安装时用它，避免用户在浮层开着时换了版本导致装错。</summary>
    private LibraryVersionViewModel? _pendingDataPackVersion;

    public bool CanConfirmDataPackTarget =>
        IsDataPackTargetOpen && !IsInstalling && SelectedDataPackTarget is not null;

    public bool HasDataPackTargetError => !string.IsNullOrWhiteSpace(DataPackTargetError);

    /// <summary>「没有存档时只给全局」这条规则要在界面上说清楚，否则用户会以为漏了功能。</summary>
    public bool HasDataPackScopeHint => IsDataPackTargetOpen && DataPackTargets.Count <= 1;

    /// <summary>选中落点的完整目录，选择区直接显示，用户不必猜文件会去哪儿。</summary>
    public string SelectedDataPackTargetPath =>
        SelectedDataPackTarget?.Directory ?? "（请选择一个安装位置）";

    partial void OnSelectedDataPackTargetChanged(DataPackTargetOption? value) =>
        ConfirmDataPackTargetCommand.NotifyCanExecuteChanged();

    partial void OnIsDataPackTargetOpenChanged(bool value)
    {
        ConfirmDataPackTargetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasDataPackScopeHint));
    }

    /// <summary>本次会话装过内容：给出「去模组页查看」的出口。</summary>
    [ObservableProperty]
    private bool _hasInstalledContent;

    /// <summary>跳到模组页查看刚装好的文件。</summary>
    [RelayCommand]
    private void GoToMods() => _navigation?.Navigate(NavigationKeys.Mods);

    [RelayCommand]
    private void RefreshDataPackTargets()
    {
        var directory = ResolveGameDirectory();

        if (directory is null)
        {
            DataPackTargetError = "没有取到游戏目录，请先在设置页或实例页指定游戏目录后再安装。";
            return;
        }

        DataPackTargetError = null;
        ReloadDataPackTargets(directory);
    }

    [RelayCommand]
    private void CancelDataPackTarget()
    {
        CloseDataPackTargetPicker();
        InstallStatus = "已取消";
    }

    /// <summary>确认落点后开始下载。存档在选择期间被删掉时把原因留在选择区，不让用户白等一次下载。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmDataPackTarget))]
    private async Task ConfirmDataPackTargetAsync()
    {
        var version = _pendingDataPackVersion;
        var option = SelectedDataPackTarget;

        if (version is null || option is null || IsInstalling)
        {
            return;
        }

        var invalid = ModrinthService.ValidateDataPackTarget(option.Target);

        if (invalid is not null)
        {
            var directory = ResolveGameDirectory();
            if (directory is not null)
            {
                ReloadDataPackTargets(directory);
            }

            DataPackTargetError = invalid;
            return;
        }

        CloseDataPackTargetPicker();
        await RunDataPackInstallAsync(version, option.Target);
    }

    /// <summary>把可选的落点铺到选择区。预览数据为空（目录都还没建）时只会有全局一项。</summary>
    private void OpenDataPackTargetPicker(LibraryVersionViewModel version, string gameDirectory)
    {
        ClosePlan();
        _pendingDataPackVersion = version;
        DataPackTargetError = null;
        ReloadDataPackTargets(gameDirectory);
        IsDataPackTargetOpen = true;
        InstallStatus = "请选择安装位置";
    }

    private void ReloadDataPackTargets(string gameDirectory)
    {
        var previousTitle = SelectedDataPackTarget?.Title;

        DataPackTargets.Clear();

        foreach (var target in ModrinthService.ResolveDataPackTargets(gameDirectory))
        {
            DataPackTargets.Add(new DataPackTargetOption(target));
        }

        // 刷新后尽量保持原选择；找不到就回到第一个（全局）。
        SelectedDataPackTarget = DataPackTargets.FirstOrDefault(item =>
                                       string.Equals(item.Title, previousTitle, StringComparison.Ordinal))
                                   ?? DataPackTargets.FirstOrDefault();

        OnPropertyChanged(nameof(HasDataPackScopeHint));
    }

    private void CloseDataPackTargetPicker()
    {
        IsDataPackTargetOpen = false;
        DataPackTargets.Clear();
        SelectedDataPackTarget = null;
        DataPackTargetError = null;
        _pendingDataPackVersion = null;
        OnPropertyChanged(nameof(HasDataPackScopeHint));
    }

    /// <summary>
    /// 数据包单文件安装。数据包之间没有 <c>dependencies</c> 语义，所以不走依赖计划，
    /// 选好落点后直接下载；SHA1 校验、同名改名仍复用 <see cref="IModrinthService.InstallAsync"/>。
    /// </summary>
    private async Task RunDataPackInstallAsync(LibraryVersionViewModel version, DataPackInstallTarget target)
    {
        if (_modrinth is null || version.Version is null)
        {
            InfoText = "示例模式：Core 服务未就绪，没有执行安装。";
            return;
        }

        ClosePlan();
        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = $"准备下载到 {target.DataPackDirectory}…";
        ErrorText = null;
        InfoText = null;

        var progress = new Progress<DownloadProgress>(report =>
        {
            InstallProgress = Math.Clamp(report.Ratio, 0, 1);
            InstallStatus = report.TotalBytes > 0
                ? $"{Conv.FormatSize(report.DownloadedBytes)} / {Conv.FormatSize(report.TotalBytes)} · {Conv.FormatSize((long)report.SpeedBytesPerSecond)}/s"
                : Conv.FormatSize(report.DownloadedBytes);
        });

        var token = _pageCts?.Token ?? CancellationToken.None;

        try
        {
            var result = await _modrinth.InstallAsync(
                version.Version,
                target.GameDirectory,
                ContentKind.DataPack,
                progress,
                token);

            ApplyInstallOutcome(result.Success, result.FilePath, result.Renamed, result.Error);

            if (result.Success)
            {
                InstallStatus = target.IsGlobal
                    ? "已安装到全局数据包目录"
                    : $"已安装到存档「{target.WorldName}」";

                // InfoText 里已经有绝对路径；这里再点明生效范围，避免「存档级」被当成全局。
                HasInstalledContent = true;
            }
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "已取消";
        }
        catch (Exception ex)
        {
            InstallStatus = "失败";
            ErrorText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    // ---------------- 安装清单（依赖确认） ----------------

    /// <summary>当前待确认 / 已执行的计划。每次重新规划都会替换。</summary>
    private ModrinthInstallPlan? _plan;

    /// <summary>清单里的一行对应一个待落盘文件：前置在前，用户点选的本体在最后。</summary>
    public ObservableCollection<LibraryPlanFileViewModel> PlanFiles { get; } = [];

    /// <summary>确认区是否可见。执行完后保持可见，用来展示逐文件结果。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlanConfirmVisible))]
    private bool _isPlanOpen;

    /// <summary>计划已执行完：保留结果行，隐藏确认 / 取消按钮，防止重复安装。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlanConfirmVisible))]
    [NotifyPropertyChangedFor(nameof(CanConfirmPlan))]
    private bool _isPlanFinished;

    /// <summary>提示区（Warnings + 可选依赖）的展开状态，默认折叠。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanNoticeToggleText))]
    private bool _isPlanNoticesVisible;

    [ObservableProperty]
    private string _planHeadline = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanBlocked))]
    private string? _planBlockedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanWarnings))]
    private string? _planWarningsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanOptional))]
    [NotifyPropertyChangedFor(nameof(PlanNoticeCount))]
    [NotifyPropertyChangedFor(nameof(HasPlanNotices))]
    [NotifyPropertyChangedFor(nameof(PlanNoticeToggleText))]
    private string? _planOptionalText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanWarnings))]
    [NotifyPropertyChangedFor(nameof(PlanNoticeCount))]
    [NotifyPropertyChangedFor(nameof(HasPlanNotices))]
    [NotifyPropertyChangedFor(nameof(PlanNoticeToggleText))]
    private int _planWarningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanResult))]
    private string? _planResultText;

    /// <summary>确认按钮文案。数字写在按钮上，用户不必回头数清单。</summary>
    [ObservableProperty]
    private string _planConfirmText = "确认安装";

    public bool HasPlanBlocked => !string.IsNullOrWhiteSpace(PlanBlockedText);

    public bool HasPlanWarnings => PlanWarningCount > 0;

    public bool HasPlanOptional => !string.IsNullOrWhiteSpace(PlanOptionalText);

    /// <summary>折叠区里的条目数：提示 + 可选依赖各算一条。</summary>
    public int PlanNoticeCount => PlanWarningCount + (HasPlanOptional ? 1 : 0);

    public bool HasPlanNotices => PlanNoticeCount > 0;

    public bool HasPlanResult => !string.IsNullOrWhiteSpace(PlanResultText);

    /// <summary>清单未执行完时才有确认 / 取消按钮。</summary>
    public bool IsPlanConfirmVisible => IsPlanOpen && !IsPlanFinished;

    /// <summary>Blocked 非空时不允许确认：装了大概率得到一个启动即崩的实例。</summary>
    public bool CanConfirmPlan => IsPlanOpen && !IsPlanFinished && !IsInstalling && _plan is { CanInstall: true };

    public string PlanNoticeToggleText =>
        IsPlanNoticesVisible ? "收起提示" : $"展开提示（{PlanNoticeCount} 条）";

    partial void OnIsPlanOpenChanged(bool value) => ConfirmPlanCommand.NotifyCanExecuteChanged();

    partial void OnIsPlanFinishedChanged(bool value) => ConfirmPlanCommand.NotifyCanExecuteChanged();

    // ---------------- 生命周期 ----------------

    public override async Task OnNavigatedToAsync()
    {
        ApplyScreenshotKind();

        _pageCts?.Dispose();
        _pageCts = new CancellationTokenSource();
        var token = _pageCts.Token;

        // 跨页预填：模组页的「去资源站查找」把项目名写进挂起槽，这里取走。
        //
        // 取出点必须在 SearchCoreAsync 之前：若先按原来的空串搜一次、再改文本搜第二次，
        // 界面会闪两下、服务端还多挨一次请求。「先导航、后设文本、再触发搜索」正是要避开的写法。
        var lookup = LibraryLookupSlot.TryTake();

        // 先确定「装给谁」，再解析筛选条件——筛选跟着玩家选中的目标走，不再从其它页面推断。
        await EnsureInstanceContextAsync(token);
        await ResolveInstallTargetsAsync(token);

        // 有预填请求时不能走下面「已初始化就直接返回」那条路：那样搜索框里是新词，
        // 列表却是上一次的结果。
        if (lookup is not null)
        {
            ApplyLookup(lookup);

            _initialized = true;
            await SearchCoreAsync(reset: true);

            // SearchCoreAsync 开头会把 InfoText 清空，所以这句提示只能等搜索结束后写。
            if (!HasError)
            {
                InfoText = $"已按项目名称搜索「{SearchText}」。";
            }

            return;
        }

        if (_initialized && Results.Count > 0)
        {
            // 页面实例被缓存复用：详情请求可能在离开时被取消，回到本页补一次。
            // 服务层有 5 分钟缓存，项目本来就没有详情时这次重复调用也不会真的发请求。
            if (SelectedProject is not null && !HasProjectDetails && !IsLoadingDetails)
            {
                _ = LoadDetailsAsync(SelectedProject);
            }

            return;
        }

        _initialized = true;
        await SearchCoreAsync(reset: true);
    }

    /// <summary>
    /// 消费一次「去资源站查找」：把预填词写进搜索框，并把分类切到它对应的那一个。
    ///
    /// 分类必须跟着切：模组页也管资源包 / 光影 / 数据包，用模组名停在资源包分类里搜只会得到空列表，
    /// 看起来像「这个项目搜不到」。版本与加载器筛选刻意不动——那是当前实例的属性，
    /// 不是这次跳转的输入；清掉它就会搜出一堆装不上的版本。
    /// </summary>
    private void ApplyLookup(LibraryLookup lookup)
    {
        SearchText = lookup.Query;

        IsModPackTabActive = false;
        ActiveKind = lookup.Kind;

        // CurseForge 没有数据包分类（classId 不存在），沿用切换分类时的处理：回到 Modrinth。
        // 刻意不写回 settings 的数据源偏好：这是「为了让这次搜索有结果」的临时回退，
        // 与用户主动切分类不同，不该悄悄改掉他在设置里的选择。双源同样要退出，理由同上。
        if (lookup.Kind == ContentKind.DataPack
            && (IsBothSources || ActiveSource == ContentSourceKind.CurseForge))
        {
            IsBothSources = false;
            ActiveSource = ContentSourceKind.Modrinth;
        }

        // 旧列表属于上一个分类 / 上一次搜索，先清掉，避免「搜索中」的头几帧还挂着旧结果。
        ResetResultState();
        ApplyInstallTargetFilter();
        UpdateFilterText();
    }

    public override Task OnNavigatedFromAsync()
    {
        // 页面实例被缓存复用，离开只是隐藏：不取消在途请求会继续占连接、还会在回来时覆盖新结果。
        _pageCts?.Cancel();
        _searchCts?.Cancel();
        _versionCts?.Cancel();
        _detailCts?.Cancel();

        // 详情请求取消后 finally 不会复位这个标记（token 已取消），不手动复位的话
        // 回到本页会一直显示「正在加载项目详情…」。
        IsLoadingDetails = false;

        // 安装同理，而且后果更重：取消后安装链的 finally 刻意不在取消态复位 IsInstalling
        // （否则旧任务会盖掉下一次安装），复位点必须在这里——否则切页打断一次导入，
        // 回到本页安装按钮与进度条会永久停在「安装中」，只能重启恢复。
        IsInstalling = false;

        return Task.CompletedTask;
    }

    /// <summary>
    /// 截图模式没有点击能力，用 <c>--section</c> 指定进入页面时停在哪一个内容分类
    /// （与模组页同一套约定：mods / resourcepacks / shaderpacks / datapacks）。
    ///
    /// 只在截图模式下生效：正常运行既不读命令行，也不会覆盖用户点选的分类。
    /// 必须在首次搜索之前调用，否则搜的还是默认分类。
    /// </summary>
    private void ApplyScreenshotKind()
    {
        if (!ScreenshotMode.IsActive || string.IsNullOrWhiteSpace(ScreenshotMode.SectionKey))
        {
            return;
        }

        // 整合包是独立分区（不是 ContentKind 值），单独处理后再走分类映射。
        if (string.Equals(ScreenshotMode.SectionKey, "modpack", StringComparison.OrdinalIgnoreCase))
        {
            IsModPackTabActive = true;
            return;
        }

        IsModPackTabActive = false;

        ActiveKind = ScreenshotMode.SectionKey switch
        {
            "datapacks" => ContentKind.DataPack,
            "resourcepacks" => ContentKind.ResourcePack,
            "shaderpacks" => ContentKind.ShaderPack,
            "mods" => ContentKind.Mod,
            _ => ActiveKind,
        };
    }

    /// <summary>
    /// 释放本页持有的 5 个 CancellationTokenSource 与整合包目录缓存。
    ///
    /// 走 <see cref="PageViewModelBase.Dispose(bool)"/> 的覆写点，而不是自己再声明一个
    /// <c>public void Dispose()</c>：后者会把基类的公开方法隐藏掉（CS0108），
    /// 而容器按 <see cref="IDisposable"/> 调用时命中的仍是基类版本，子类逻辑就被静默跳过了。
    /// 字段置 null 是基类注释里的要求——<see cref="OnNavigatedFromAsync"/> 里还会用 <c>?.</c> 访问它们。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pageCts?.Cancel();
            _pageCts?.Dispose();
            _pageCts = null;

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            _versionCts?.Dispose();
            _versionCts = null;

            _detailCts?.Dispose();
            _detailCts = null;

            // 注册表注入的目录客户端归容器所有；自建的那份必须在这里释放，否则 HttpClient 泄漏。
            if (_ownsModpackCatalog)
            {
                _modpackCatalog?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    // ---------------- 命令 ----------------

    [RelayCommand]
    private async Task SelectKindAsync(ContentKind kind)
    {
        if (!IsModPackTabActive && ActiveKind == kind)
        {
            return;
        }

        IsModPackTabActive = false;
        ActiveKind = kind;
        ResetResultState();

        // 模组 / 光影分区不列原版目标：原版没有加载器，装了也不会被读取。
        ApplyInstallTargetFilter();
        UpdateFilterText();

        // CurseForge 没有数据包的 classId（搜出来必然为空），切到数据包时回到 Modrinth 并说明原因。
        // 双源里的 CurseForge 侧同样没有数据包：这里连双源一起退出，而不是留一个半边空着的档位。
        var switchedSource = kind == ContentKind.DataPack
                             && (IsBothSources || ActiveSource == ContentSourceKind.CurseForge);

        if (switchedSource)
        {
            IsBothSources = false;
            ActiveSource = ContentSourceKind.Modrinth;
            UpdateFilterText();
        }

        await SearchCoreAsync(reset: true);

        if (switchedSource)
        {
            InfoText = "数据包只支持从 Modrinth 搜索与安装，已自动切换到 Modrinth。";
            await PersistSourceAsync(ActiveSource, _pageCts?.Token ?? CancellationToken.None);
        }
    }

    /// <summary>
    /// 切到「整合包」分区。
    ///
    /// 与四个内容分类不同：这里搜的是整合包项目（Modrinth 的 <c>project_type:modpack</c>、
    /// CurseForge 的 classId=4471），装出来的东西是一个独立实例（走 <see cref="IModpackService.ImportAsync"/>），
    /// 不是往当前实例的 <c>mods/</c> 里塞文件。
    ///
    /// 搜索<b>不按游戏版本筛选</b>：玩家先找主题包、再在包里挑构建，见 <see cref="SelectedModPackVersionFilter"/>。
    /// </summary>
    [RelayCommand]
    private async Task SelectModPackAsync()
    {
        if (IsModPackTabActive)
        {
            return;
        }

        IsModPackTabActive = true;
        ResetResultState();
        ApplyInstallTargetFilter();
        UpdateFilterText();

        await SearchCoreAsync(reset: true);
    }

    /// <summary>
    /// 切换搜索后端。清空在飞请求与现有结果，写回偏好，再按新后端重搜。
    ///
    /// 从双源点回某个单源档位也走这里：双源不是 <see cref="ContentSourceKind"/> 的取值，
    /// 所以早退判据必须先看 <see cref="IsBothSources"/>——否则「点同一个单源 chip 想退出双源」
    /// 会被 <c>ActiveSource == source</c> 吃掉，用户点了没反应。
    /// </summary>
    [RelayCommand]
    private async Task SelectSourceAsync(ContentSourceKind source)
    {
        if (!IsBothSources && ActiveSource == source)
        {
            return;
        }

        IsBothSources = false;
        ActiveSource = source;

        // 必须先取消：旧后端的结果晚到会覆盖新后端的列表。
        _searchCts?.Cancel();
        _versionCts?.Cancel();

        ResetResultState();
        UpdateFilterText();

        await SearchCoreAsync(reset: true);
        await PersistSourceAsync(source, _pageCts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// 切到「双源」。
    ///
    /// 不可用时只说明原因、不改当前选择：静默退化成单源会让用户以为两站都查过了，
    /// 这比明说「缺 key」更糟。
    /// </summary>
    [RelayCommand]
    private async Task SelectBothSourcesAsync()
    {
        if (IsBothSources)
        {
            return;
        }

        if (!CanUseBothSources)
        {
            InfoText = "当前无法同时搜索两站：CurseForge 服务未接线。";
            return;
        }

        IsBothSources = true;

        // 与切换单源同理：单源的旧结果晚到会盖掉双源合并后的列表。
        _searchCts?.Cancel();
        _versionCts?.Cancel();

        ResetResultState();
        UpdateFilterText();

        await SearchCoreAsync(reset: true);
    }

    [RelayCommand]
    private async Task SearchAsync() => await SearchCoreAsync(reset: true);

    [RelayCommand]
    private async Task LoadMoreAsync() => await SearchCoreAsync(reset: false);

    /// <summary>刷新筛选条件后重搜：用户在版本页换了版本、实例页换了实例都靠这个入口。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _settingsLoaded = false;

        if (_pageCts is null)
        {
            _pageCts = new CancellationTokenSource();
        }

        await EnsureInstanceContextAsync(_pageCts.Token);
        await ResolveInstallTargetsAsync(_pageCts.Token);
        await SearchCoreAsync(reset: true);
    }

    /// <summary>打开搜索结果卡片对应的项目主页。</summary>
    [RelayCommand]
    private Task OpenProjectAsync(LibraryProjectViewModel? project) =>
        project is null ? Task.CompletedTask : OpenProjectWebAsync(project.WebUrl);

    /// <summary>
    /// 打开详情区里的任意链接（源码 / 问题 / 项目页）。
    ///
    /// URL 来自 Modrinth 响应，为空时直接忽略——按钮本身已经按 HasXxxUrl 控制可见性，
    /// 这里是第二道防线，避免作者清空链接后命令仍能带着 null 走到平台层。
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectWebAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (_platform is null)
        {
            ErrorText = "无法打开浏览器：平台服务尚未就绪。";
            return;
        }

        try
        {
            await _platform.OpenUrlAsync(url.Trim());
        }
        catch (Exception ex)
        {
            ErrorText = $"打开浏览器失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallAsync()
    {
        var project = SelectedProject;
        var version = SelectedVersion;

        if (project is null || version is null || IsInstalling)
        {
            return;
        }

        // 兜底拦截：CanInstallNow 已经把按钮灰掉，但命令还能被快捷键 / 代码路径调到。
        // 这是最后一道闸门——插件一旦落进 mods/，客户端下次启动就崩，事后清理对用户是负担。
        if (project.IsServerPlugin)
        {
            ErrorText = ServerPluginPolicy.InstallRejectedMessage;
            return;
        }

        // 没有直链不是错误：CurseForge 作者关闭了第三方分发，只能让用户去网页下载。
        if (!version.IsDownloadable)
        {
            await OpenProjectAsync(project);
            return;
        }

        // 整合包装成独立实例，与当前实例的 mods/ 无关，所以既不需要游戏目录，也不走内容安装链。
        if (IsModPackTabActive)
        {
            await InstallModPackAsync(project, version);
            return;
        }

        if (version.Source == ContentSourceKind.CurseForge && _curseforge is null)
        {
            InfoText = "CurseForge 服务未就绪，没有执行安装。";
            return;
        }

        if (version.Source == ContentSourceKind.Modrinth && _modrinth is null)
        {
            InfoText = "示例模式：Core 服务未就绪，没有执行安装。";
            return;
        }

        var gameDirectory = ResolveGameDirectory();
        if (gameDirectory is null)
        {
            ErrorText = "没有取到游戏目录，请先在设置页或实例页指定游戏目录后再安装。";
            return;
        }

        // 数据包有两个落点（全局 / 某个存档），必须先让用户选；它也没有依赖可解析，选完直装。
        if (ActiveKind == ContentKind.DataPack)
        {
            if (version.Source != ContentSourceKind.Modrinth)
            {
                InfoText = "数据包目前只支持从 Modrinth 安装。";
                return;
            }

            OpenDataPackTargetPicker(version, gameDirectory);
            return;
        }

        // CurseForge 的接口不返回依赖，保持原有的单文件直装。
        if (version.Source == ContentSourceKind.CurseForge)
        {
            await InstallCurseForgeAsync(version, gameDirectory);
            return;
        }

        await PrepareModrinthPlanAsync(version);
    }

    /// <summary>确认清单后执行安装。Blocked 非空时 <see cref="CanConfirmPlan"/> 为 false。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmPlan))]
    private async Task ConfirmPlanAsync()
    {
        if (_plan is null || !_plan.CanInstall || IsInstalling)
        {
            return;
        }

        await RunPlanInstallAsync(_plan);
    }

    [RelayCommand]
    private void CancelPlan() => ClosePlan();

    [RelayCommand]
    private void TogglePlanNotices() => IsPlanNoticesVisible = !IsPlanNoticesVisible;

    /// <summary>
    /// 先算依赖再让用户确认。
    ///
    /// 递归展开会在用户不知情时多下几十 MB，所以清单必须先摊开再落盘；
    /// 但一个必然只有 1 个文件的安装不该多要一次点击，那种情况直接退回直装。
    /// </summary>
    private async Task PrepareModrinthPlanAsync(LibraryVersionViewModel version)
    {
        var source = version.Version;

        if (source is null)
        {
            ErrorText = "这个版本缺少 Modrinth 版本信息，无法解析前置。";
            return;
        }

        ClosePlan();
        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = "正在解析前置…";
        ErrorText = null;
        InfoText = null;

        var token = _pageCts?.Token ?? CancellationToken.None;

        try
        {
            var plan = await _modrinth!.PlanInstallAsync(source, _gameVersion, EffectiveLoader, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (plan.Required.Count == 0 && plan.Optional.Count == 0 && plan.Blocked.Count == 0 && plan.Warnings.Count == 0)
            {
                await RunPlanInstallAsync(plan);
                return;
            }

            ApplyPlan(plan);
            InstallStatus = plan.CanInstall ? "等待确认" : "已阻断";
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "已取消";
        }
        catch (Exception ex)
        {
            InstallStatus = "失败";
            ErrorText = $"解析前置失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsInstalling = false;
            }
        }
    }

    /// <summary>把计划铺到确认区：总数、总大小、逐文件清单与阻断 / 提示文案一次性备好。</summary>
    private void ApplyPlan(ModrinthInstallPlan plan)
    {
        _plan = plan;

        PlanFiles.Clear();

        // Required 已是「前置在前」的顺序，直接按它渲染；本体放最后。
        foreach (var dependency in plan.Required)
        {
            PlanFiles.Add(new LibraryPlanFileViewModel(dependency, isDependency: true));
        }

        PlanFiles.Add(new LibraryPlanFileViewModel(plan.Root, isDependency: false));

        PlanHeadline = plan.Required.Count > 0
            ? $"将同时安装 {plan.Required.Count} 个前置 · 共 {plan.TotalFiles} 个文件 · 总大小 {Conv.FormatSize(plan.TotalBytes)}"
            : $"共 {plan.TotalFiles} 个文件 · 总大小 {Conv.FormatSize(plan.TotalBytes)}";

        PlanBlockedText = plan.Blocked.Count == 0 ? null : string.Join(Environment.NewLine, plan.Blocked);
        PlanWarningsText = plan.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, plan.Warnings);
        PlanWarningCount = plan.Warnings.Count;
        PlanOptionalText = plan.Optional.Count == 0
            ? null
            : $"另有 {plan.Optional.Count} 个可选依赖不会自动安装：{string.Join("、", plan.Optional.Select(item => item.Name))}";
        PlanConfirmText = plan.CanInstall ? $"确认安装 {plan.TotalFiles} 个文件" : "无法安装";
        PlanResultText = null;
        IsPlanNoticesVisible = false;
        IsPlanFinished = false;
        IsPlanOpen = true;

        ConfirmPlanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>执行计划：前置先装、本体最后；结果与进度都落到确认区。</summary>
    private async Task RunPlanInstallAsync(ModrinthInstallPlan plan)
    {
        var gameDirectory = ResolveGameDirectory();

        if (gameDirectory is null || _modrinth is null)
        {
            ErrorText = "没有取到游戏目录，请先在设置页或实例页指定游戏目录后再安装。";
            return;
        }

        foreach (var file in PlanFiles)
        {
            file.Reset();
        }

        // 直装路径（无依赖）不打开确认区，但清单仍要填一份：结果按行回填后才有地方展示。
        if (PlanFiles.Count == 0)
        {
            PlanFiles.Add(new LibraryPlanFileViewModel(plan.Root, isDependency: false));
        }

        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = "准备下载…";
        ErrorText = null;
        InfoText = null;
        PlanResultText = null;

        var progress = CreatePlanProgress();
        var token = _pageCts?.Token ?? CancellationToken.None;

        try
        {
            var result = await _modrinth.InstallPlanAsync(plan, gameDirectory, ActiveKind, progress, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplyPlanResults(result);
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "已取消";
        }
        catch (Exception ex)
        {
            InstallStatus = "失败";
            ErrorText = $"安装失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsInstalling = false;

                // 只有确认区开着才切到「已完成」：直装的单文件不需要留着清单。
                IsPlanFinished = IsPlanOpen;
            }
        }
    }

    /// <summary>把逐文件结果回填到清单行，并给出整体的完成 / 部分失败摘要。</summary>
    private void ApplyPlanResults(ModrinthPlanInstallResult result)
    {
        for (var index = 0; index < result.Files.Count && index < PlanFiles.Count; index++)
        {
            PlanFiles[index].ApplyResult(result.Files[index]);
        }

        // 准备阶段失败（目录不可写、计划被阻断）：逐行结果为空，原因只在 overall Error 里。
        if (result.Error is not null)
        {
            InstallStatus = "失败";
            ErrorText = result.Error;
            return;
        }

        var installed = result.InstalledCount;
        var failed = result.FailedCount;

        PlanResultText = failed == 0
            ? $"已安装 {installed} 个文件。"
            : $"已安装 {installed} 个文件，{failed} 个失败，原因见对应行。";
        InstallProgress = failed == 0 ? 1 : InstallProgress;
        InstallStatus = failed == 0 ? "已完成" : "部分失败";

        if (!IsPlanOpen && PlanFiles.Count == 1)
        {
            // 无依赖直装的路径没有确认区，结果仍走原来的提示条。
            var file = PlanFiles[0];

            if (file.HasError)
            {
                ErrorText = file.ErrorText;
            }
            else if (file.SavedPath is not null)
            {
                InfoText = $"已安装 {file.FileName}：{file.SavedPath}";
            }
            else
            {
                InfoText = $"已安装 {file.FileName}。";
            }
        }
    }

    /// <summary>计划安装的进度：整批字节 + 当前第几个文件，避免用户以为卡在同一个文件上。</summary>
    private Progress<DownloadProgress> CreatePlanProgress() => new(report =>
    {
        InstallProgress = Math.Clamp(report.Ratio, 0, 1);

        var counter = report.TotalFiles > 0 ? $" · 第 {report.CompletedFiles + 1}/{report.TotalFiles} 个" : string.Empty;
        var bytes = report.TotalBytes > 0
            ? $"{Conv.FormatSize(report.DownloadedBytes)} / {Conv.FormatSize(report.TotalBytes)} · {Conv.FormatSize((long)report.SpeedBytesPerSecond)}/s"
            : Conv.FormatSize(report.DownloadedBytes);

        InstallStatus = string.IsNullOrWhiteSpace(report.CurrentFile)
            ? bytes + counter
            : $"{report.CurrentFile} · {bytes}{counter}";
    });

    /// <summary>收起并清空清单。切换项目 / 页面时调用，避免旧清单挂在新选择上。</summary>
    private void ClosePlan()
    {
        _plan = null;

        PlanFiles.Clear();
        PlanHeadline = string.Empty;
        PlanBlockedText = null;
        PlanWarningsText = null;
        PlanWarningCount = 0;
        PlanOptionalText = null;
        PlanResultText = null;
        PlanConfirmText = "确认安装";
        IsPlanNoticesVisible = false;
        IsPlanFinished = false;
        IsPlanOpen = false;

        ConfirmPlanCommand.NotifyCanExecuteChanged();
    }

    // ---------------- 整合包：下载 .mrpack → 导入成实例 ----------------

    /// <summary>
    /// 在线整合包的安装：把归档读到临时文件，再交给
    /// <see cref="IModpackService.ImportAsync"/> 装成一个独立实例。
    /// 两个数据源都走这条路：Modrinth 下 <c>.mrpack</c>，CurseForge 下带 <c>manifest.json</c> 的 <c>.zip</c>，
    /// 导入链按包内结构识别格式，不看扩展名。
    ///
    /// 为什么分两步而不是直接「下载并通过流导入」：导入是多阶段的（先装游戏与加载器，
    /// 再解压覆盖层，再按 index 下载文件），失败时要能回滚成「什么都没留下」；
    /// 这一步只有在归档文件已经在磁盘上才成立。下载走 <see cref="DownloadQueue"/> 的普通批次，
    /// 因此归档会出现在下载任务面板里，与其它下载共用同一套进度与失败重试。
    /// </summary>
    private async Task InstallModPackAsync(LibraryProjectViewModel project, LibraryVersionViewModel version)
    {
        if (_modpack is null || _downloadQueue is null)
        {
            InfoText = "整合包服务尚未接线，没有执行安装。";
            return;
        }

        var download = DescribeModPackDownload(version);

        if (string.IsNullOrWhiteSpace(download.Url))
        {
            ErrorText = "这个版本的整合包没有可用的下载地址。";
            return;
        }

        var instanceName = ModPackInstanceName(project.Title, download.VersionNumber);

        if (instanceName.Length == 0)
        {
            ErrorText = "无法从项目名生成实例名，请换一个整合包。";
            return;
        }

        ClosePlan();
        CloseDataPackTargetPicker();
        ClearModPackState();

        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = $"正在下载整合包 {download.FileName}…";
        ErrorText = null;
        InfoText = null;

        var archivePath = ResolveModPackArchivePath(_paths?.CacheDirectory, download.FileName, instanceName);
        var token = _pageCts?.Token ?? CancellationToken.None;
        var progress = CreateModPackProgress();

        try
        {
            var directory = Path.GetDirectoryName(archivePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var batch = await _downloadQueue
                .DownloadAsync(
                    [
                        new DownloadItem
                        {
                            Url = download.Url,
                            DestinationPath = archivePath,
                            Id = download.FileName,
                            Group = "modpack",
                            ExpectedSize = download.Size,
                            ExpectedSha1 = download.Sha1,
                        },
                    ],
                    progress,
                    token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!batch.Success || !File.Exists(archivePath))
            {
                InstallStatus = "失败";
                ErrorText = "整合包下载失败：文件没有取到。检查网络或代理后重试。";
                return;
            }

            InstallStatus = "正在导入整合包（安装游戏 → 解压 → 补全文件）…";
            InstallProgress = 0;

            // 导入时顺带装游戏与加载器：关掉会得到一个指向不存在版本的实例，
            // 玩家还得自己去版本页再装一次，且版本号常装错（PCL2-GAP 第 1 条的同一问题）。
            _modpack.InstallGameOnImport = true;

            var instanceDirectory = await _modpack
                .ImportAsync(archivePath, instanceName, progress, token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplyModPackImport(_modpack.LastImport, instanceName, instanceDirectory);

            // 导入会往 versions/<版本名>/ 里写新实例：让实例列表重读一次，版本页进入时才能看到它。
            await RefreshInstancesAfterImportAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "已取消";
        }
        catch (InvalidOperationException ex)
        {
            // 导入链自己抛的都是中文可展示原因（例如「已有下载批次在进行」）。
            InstallStatus = "失败";
            ErrorText = $"导入整合包失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            // 未知异常在界面上只能给一句可读文案；把堆栈留给日志，否则现场无法定位。
            _log?.Error($"整合包导入失败：{project.Title}", ex);
            InstallStatus = "失败";
            ErrorText = "导入整合包失败：服务异常，详见日志。";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsInstalling = false;
            }

            // 归档只是中转文件：几百 MB 留着只会白占磁盘。
            TryDeleteModPackArchive(archivePath);
        }
    }

    /// <summary>把导入结果摊到界面：成功 / 部分失败的差别必须看得出来，警告不能丢。</summary>
    private void ApplyModPackImport(ModpackImportResult? result, string instanceName, string? instanceDirectory)
    {
        InstallProgress = 1;

        ModPackResultText = DescribeModPackImport(result, instanceName);

        var directory = result?.InstanceDirectory ?? instanceDirectory;

        if (!string.IsNullOrWhiteSpace(directory))
        {
            ImportedModPackDirectory = directory;
        }

        var warning = result?.Warning;

        InstallStatus = string.IsNullOrWhiteSpace(warning) ? "已完成" : "已完成（有提示）";

        if (result is { GameInstalled: false })
        {
            // 只解压了文件：实例能显示，但启动会找不到版本。这条必须进错误条而不是成功条。
            ErrorText = $"整合包「{instanceName}」的文件已就位，但游戏没有装上：到版本页完成安装后才能启动。";
        }
    }

    /// <summary>整合包的进度：下载 .mrpack 与导入共用一条进度条，文案跟着阶段走。</summary>
    private Progress<DownloadProgress> CreateModPackProgress() => new(report =>
    {
        InstallProgress = Math.Clamp(report.Ratio, 0, 1);

        var bytes = report.TotalBytes > 0
            ? $"{Conv.FormatSize(report.DownloadedBytes)} / {Conv.FormatSize(report.TotalBytes)}"
            : Conv.FormatSize(report.DownloadedBytes);

        var stage = string.IsNullOrWhiteSpace(report.Stage) ? null : report.Stage;

        InstallStatus = stage is not null
            ? $"{stage} · {bytes}"
            : string.IsNullOrWhiteSpace(report.CurrentFile)
                ? bytes
                : $"{report.CurrentFile} · {bytes}";
    });

    /// <summary>
    /// 导入后让实例列表重读一次。复用 <see cref="IInstanceContext"/> 这条既有通道，
    /// 不额外造事件：版本页进入时走的也是同一个 <c>LoadAsync</c>。
    /// </summary>
    private async Task RefreshInstancesAfterImportAsync(CancellationToken token)
    {
        if (_instanceContext is null)
        {
            return;
        }

        try
        {
            await _instanceContext.LoadAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面已切走；导入本身已经完成。
        }
        catch (Exception)
        {
            // 列表刷新失败不影响「已装好」这个事实：版本页进入时会再读一次目录。
        }
    }

    private static void TryDeleteModPackArchive(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件删不掉不是错误，下一次导入会覆盖同名文件。
        }
    }

    /// <summary>
    /// 整合包临时归档路径。<c>cache/modpacks/</c> 而不是系统临时目录：数据根可配，
    /// 用户换数据目录时不会把几百 MB 的中转文件留在 C 盘。
    /// </summary>
    internal static string ResolveModPackArchivePath(string? cacheDirectory, string? fileName, string instanceName)
    {
        var root = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(Path.GetTempPath(), "NebulaLauncher", "modpacks")
            : Path.Combine(cacheDirectory, "modpacks");

        var baseName = SanitizeModPackFileName(fileName);

        if (baseName.Length == 0)
        {
            baseName = SanitizeModPackFileName(instanceName);
        }

        if (baseName.Length == 0)
        {
            baseName = "modpack";
        }

        // 整合包通常就是 .mrpack；个别项目挂的是 .zip，保留原后缀比强行改成 .mrpack 更诚实。
        if (Path.GetExtension(baseName).Length == 0)
        {
            baseName += ".mrpack";
        }

        return Path.Combine(root, baseName);
    }

    /// <summary>
    /// 把文件名里的路径分隔符、Windows 非法字符、保留名与结尾的点/空格处理掉。
    /// 空输入返回空串（调用点会自己套默认名），委托给 <see cref="Core.Paths.PathSafety"/>。
    /// </summary>
    internal static string SanitizeModPackFileName(string? value) =>
        NebulaLauncher.Core.Paths.PathSafety.SanitizeFileName(value, string.Empty);

    /// <summary>
    /// 实例名：项目名 + 版本号。实例会出现在版本页，名字必须让人一眼认出是哪个包。
    /// 版本号已经包含在项目名里时不再重复拼接。
    /// </summary>
    internal static string ModPackInstanceName(string? title, string? versionNumber)
    {
        const int MaxLength = 60;

        var name = (title ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            name = "整合包";
        }

        var version = (versionNumber ?? string.Empty).Trim();

        var combined = version.Length == 0 || name.Contains(version, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + " " + version;

        return combined.Length <= MaxLength ? combined : combined[..MaxLength].TrimEnd();
    }

    /// <summary>
    /// 把版本项归一成「下载整合包所需」的那几个字段。两个后端的字段名不同，
    /// 归一化只做在这一处，下载与导入逻辑里不再写按数据源分叉的 if。
    /// </summary>
    internal static (string? Url, string FileName, long Size, string? Sha1, string VersionNumber) DescribeModPackDownload(
        LibraryVersionViewModel version)
    {
        if (version.Source == ContentSourceKind.CurseForge)
        {
            var file = version.File;

            return (
                file?.DownloadUrl,
                string.IsNullOrWhiteSpace(file?.FileName) ? "modpack.zip" : file!.FileName,
                file?.FileLength ?? 0,
                file?.Sha1,
                version.Name);
        }

        var source = version.Version;

        return (
            source?.DownloadUrl,
            string.IsNullOrWhiteSpace(source?.FileName) ? "modpack.mrpack" : source!.FileName,
            source?.FileSize ?? 0,
            source?.Sha1,
            string.IsNullOrWhiteSpace(version.Name) ? source?.VersionNumber ?? string.Empty : version.Name);
    }

    /// <summary>
    /// 导入结果 → 一句可展示的中文。三种情形必须分开说：
    /// 装完能玩、装完但有非致命问题、只解压了文件（启动会找不到版本）。
    /// </summary>
    internal static string DescribeModPackImport(ModpackImportResult? result, string instanceName)
    {
        if (result is null)
        {
            return $"整合包「{instanceName}」已装成独立实例，已在版本页可见。";
        }

        // InstalledVersionName 是实例自己的版本目录名，通常与实例名相同；相同时不再括注，
        // 否则文案会变成「整合包「Pack A」已装成独立实例（版本 Pack A）」。重名加序号时才有信息量。
        var installed = string.IsNullOrWhiteSpace(result.InstalledVersionName)
            || string.Equals(result.InstalledVersionName, instanceName, StringComparison.Ordinal)
            ? string.Empty
            : $"（实例版本 {result.InstalledVersionName}）";

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            return $"整合包「{instanceName}」已装成独立实例{installed}，已在版本页可见。{result.Warning}";
        }

        return result.GameInstalled
            ? $"整合包「{instanceName}」已装成独立实例{installed}，游戏与加载器一并安装，已在版本页可见。"
            : $"整合包「{instanceName}」已装成独立实例{installed}，但未安装游戏：到版本页完成安装后才能启动。";
    }

    /// <summary>CurseForge 单文件安装：接口不返回依赖，保持原有的一次下载一次落盘。</summary>
    private async Task InstallCurseForgeAsync(LibraryVersionViewModel version, string gameDirectory)
    {
        ClosePlan();
        IsInstalling = true;
        InstallProgress = 0;
        InstallStatus = "准备下载…";
        ErrorText = null;
        InfoText = null;

        var progress = new Progress<DownloadProgress>(report =>
        {
            InstallProgress = Math.Clamp(report.Ratio, 0, 1);
            InstallStatus = report.TotalBytes > 0
                ? $"{Conv.FormatSize(report.DownloadedBytes)} / {Conv.FormatSize(report.TotalBytes)} · {Conv.FormatSize((long)report.SpeedBytesPerSecond)}/s"
                : Conv.FormatSize(report.DownloadedBytes);
        });

        var token = _pageCts?.Token ?? CancellationToken.None;

        try
        {
            var result = await _curseforge!.InstallAsync(version.File!, gameDirectory, ActiveKind, progress, token);
            ApplyInstallOutcome(result.Success, result.FilePath, result.Renamed, result.Error);
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "已取消";
        }
        catch (Exception ex)
        {
            InstallStatus = "失败";
            ErrorText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>两个后端的安装结果字段一致，统一在 UI 侧收口。</summary>
    private void ApplyInstallOutcome(bool success, string? filePath, bool renamed, string? error)
    {
        if (!success)
        {
            InstallStatus = "失败";
            ErrorText = string.IsNullOrWhiteSpace(error) ? "安装失败。" : error;
            return;
        }

        InstallProgress = 1;
        InstallStatus = "已完成";
        HasInstalledContent = true;

        var fileName = string.IsNullOrWhiteSpace(filePath) ? "文件" : Path.GetFileName(filePath);

        InfoText = renamed
            ? $"已安装 {fileName}（同名文件已存在，原文件保留）：{filePath}"
            : $"已安装 {fileName}：{filePath}";
    }

    // ---------------- 实例上下文 ----------------

    /// <summary>
    /// 页面进入时读一次实例列表。失败不阻断渲染：目录退回全局 .minecraft，
    /// 原因写进 <see cref="TargetNoticeText"/>，用户仍能看到列表与安装按钮。
    /// </summary>
    private async Task EnsureInstanceContextAsync(CancellationToken token)
    {
        if (_instanceContext is null)
        {
            return;
        }

        try
        {
            await _instanceContext.LoadAsync(token);

            if (!token.IsCancellationRequested)
            {
                TargetNoticeText = null;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已经切走。
        }
        catch (Exception ex)
        {
            TargetNoticeText = $"读取当前实例失败，已退回全局目录：{ex.Message}";
        }
    }

    /// <summary>当前生效的游戏目录。实例 → 上下文 → 全局 .minecraft，逐级兜底。</summary>
    private string? ResolveGameDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_gameDirectory))
        {
            return _gameDirectory;
        }

        var fromContext = _instanceContext?.GameDirectory;

        if (!string.IsNullOrWhiteSpace(fromContext))
        {
            return fromContext;
        }

        var fromPaths = _paths?.MinecraftDirectory;

        return string.IsNullOrWhiteSpace(fromPaths) ? null : fromPaths;
    }

    // ---------------- 安装目标（页内枚举与筛选） ----------------

    /// <summary>
    /// 读一次设置、实例列表与已安装版本，重建「将装到」下拉框。
    ///
    /// 失败不阻断渲染：实例列表读不到时仍会列出已安装版本与全局目录，
    /// 原因写进 <see cref="TargetNoticeText"/>；玩家仍能选目标并下载。
    /// </summary>
    private async Task ResolveInstallTargetsAsync(CancellationToken token)
    {
        await EnsureSettingsLoadedAsync(token);
        EnsureSourceInitialized();

        if (token.IsCancellationRequested)
        {
            return;
        }

        var previousKey = SelectedInstallTarget?.Key;
        var targets = new List<ContentTarget>();

        try
        {
            if (_instanceContext is not null)
            {
                targets.AddRange(await _instanceContext.ListTargetsAsync(token));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            TargetNoticeText = $"读取实例列表失败，已退回全局目录：{ex.Message}";
        }

        var versions = new List<VersionInfo>();

        if (_versions is not null)
        {
            try
            {
                versions.AddRange(await _versions.GetInstalledAsync(token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                TargetNoticeText ??= $"读取已安装版本失败，只列实例与全局目录：{ex.Message}";
            }
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var options = BuildInstallTargets(targets, versions, _paths?.MinecraftDirectory);

        _allInstallTargets.Clear();
        _allInstallTargets.AddRange(options);

        // 重新进入页面时列表会重建出新对象，但目标本身没变：过滤时按 Key 判断是否真的换了目标，
        // 未变化时不重搜（否则每次进页面都会多打一次请求）。
        _installTargetKeyToRestore = previousKey;
        ApplyInstallTargetFilter();

        // setter 里已经做过落点与文案更新；这里兜底一次，覆盖「服务全缺、preferred 为 null」的情形。
        ApplyInstallTarget(SelectedInstallTarget);
        UpdateFilterText();
    }

    /// <summary>
    /// 按当前分区过滤「将装到」的条目：模组与光影分区不列原版目标（原版没有加载器，装了也不会被读取）。
    ///
    /// 过滤后当前选中项不可用时改选全局目录（不筛版本，任何内容都能装），
    /// 再没有就选第一个可用项。重建集合期间抑制 ComboBox 的 SelectedItem 回写。
    /// </summary>
    private void ApplyInstallTargetFilter()
    {
        var previousKey = SelectedInstallTarget?.Key;

        var visible = new List<LibraryInstallTargetViewModel>(_allInstallTargets.Count);

        foreach (var target in _allInstallTargets)
        {
            if (!ShowsVanillaTargets && !target.IsGlobal && target.Loader == LoaderKind.Vanilla)
            {
                continue;
            }

            visible.Add(target);
        }

        _suppressInstallTargetChanges = true;

        try
        {
            InstallTargets.Clear();

            foreach (var target in visible)
            {
                InstallTargets.Add(target);
            }
        }
        finally
        {
            _suppressInstallTargetChanges = false;
        }

        var preferred = visible.FirstOrDefault(item => string.Equals(item.Key, previousKey, StringComparison.Ordinal))
                        ?? DefaultInstallTarget(visible);

        _installTargetKeyToRestore = previousKey;

        _suppressTargetSearch = true;

        try
        {
            SelectedInstallTarget = preferred;
        }
        finally
        {
            _suppressTargetSearch = false;
        }

        // setter 可能在「同一对象」或抑制分支早退；落点与文案在这里统一刷新。
        ApplyInstallTarget(SelectedInstallTarget);
        UpdateFilterText();
    }

    /// <summary>
    /// 是否列出原版目标。模组与光影分区不列：原版游戏没有加载器，下载的 mod / 光影不会被加载；
    /// 资源包与数据包原版可用，整合包分区不显示目标下拉。
    /// </summary>
    private bool ShowsVanillaTargets => IsModPackTabActive
                                        || ActiveKind is not (ContentKind.Mod or ContentKind.ShaderPack);

    /// <summary>读一次 <c>settings.json</c>。失败只记状态行，页面继续跑。</summary>
    private async Task EnsureSettingsLoadedAsync(CancellationToken token)
    {
        if (_settings is null || _settingsLoaded)
        {
            return;
        }

        try
        {
            await _settings.LoadAsync(token);
            _settingsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // 页面切走，本次不加载。
        }
        catch (Exception ex)
        {
            ErrorText = $"读取设置失败：{ex.Message}";
        }
    }

    /// <summary>默认目标：当前实例 → 全局目录 → 第一项。会话内切换过的目标优先。</summary>
    private LibraryInstallTargetViewModel? DefaultInstallTarget(IReadOnlyList<LibraryInstallTargetViewModel> options)
    {
        var currentId = _instanceContext?.Current?.Id;

        if (!string.IsNullOrWhiteSpace(currentId))
        {
            var byInstance = options.FirstOrDefault(item =>
                string.Equals(item.InstanceKey, currentId, StringComparison.Ordinal));

            if (byInstance is not null)
            {
                return byInstance;
            }
        }

        for (var index = 0; index < options.Count; index++)
        {
            if (options[index].IsGlobal)
            {
                return options[index];
            }
        }

        return options.Count == 0 ? null : options[0];
    }

    /// <summary>
    /// 组装「将装到」下拉项：实例 → 尚未建实例的已安装版本 → 全局目录。
    ///
    /// 实例与版本之间用「该实例绑定的版本目录名」去重：同一份版本已经是一个实例时，
    /// 再列一条同名的「版本」会让玩家看到两个落点不同却长得一样的条目。
    ///
    /// 未建实例的版本，落点是全局 <c>.minecraft</c>：它没有自己的隔离目录，游戏实际读的就是全局内容目录。
    /// 这一点必须写进副标题，否则玩家会以为文件进了 <c>versions/&lt;名字&gt;/</c>。
    /// </summary>
    internal static IReadOnlyList<LibraryInstallTargetViewModel> BuildInstallTargets(
        IReadOnlyList<ContentTarget> targets,
        IReadOnlyList<VersionInfo> installedVersions,
        string? minecraftDirectory)
    {
        var options = new List<LibraryInstallTargetViewModel>(targets.Count + installedVersions.Count + 1);
        var coveredVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (target.Kind == ContentTargetKind.Global)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(target.VersionName))
            {
                coveredVersions.Add(target.VersionName);
            }

            options.Add(new LibraryInstallTargetViewModel
            {
                Key = target.Key,
                InstanceKey = target.InstanceId,
                DisplayName = target.DisplayName,
                Detail = DescribeTargetDetail(target.MinecraftVersion, target.Loader, "实例目录"),
                GameDirectory = target.GameDirectory,
                MinecraftVersion = target.MinecraftVersion,
                Loader = target.Loader,
            });
        }

        foreach (var version in installedVersions)
        {
            if (string.IsNullOrWhiteSpace(version.Name) || coveredVersions.Contains(version.Name))
            {
                continue;
            }

            options.Add(new LibraryInstallTargetViewModel
            {
                Key = VersionTargetPrefix + version.Name,
                DisplayName = version.Name,
                Detail = DescribeTargetDetail(version.MinecraftVersion, version.Loader, "全局 .minecraft（该版本未建实例）"),
                GameDirectory = minecraftDirectory,
                MinecraftVersion = version.MinecraftVersion,
                Loader = version.Loader,
            });
        }

        options.Add(new LibraryInstallTargetViewModel
        {
            Key = GlobalTargetKey,
            DisplayName = "全局目录 (.minecraft)",
            Detail = "不筛选版本与加载器 · 全局 mods 由所有未隔离版本共用",
            GameDirectory = minecraftDirectory,
            MinecraftVersion = null,
            Loader = LoaderKind.Vanilla,
            IsGlobal = true,
        });

        return options;
    }

    /// <summary>「Minecraft 1.20.1 · Fabric · 实例目录」这样的副标题；读不出原版版本号时写「版本未知」。</summary>
    internal static string DescribeTargetDetail(string? minecraftVersion, LoaderKind loader, string directoryLabel)
    {
        var version = string.IsNullOrWhiteSpace(minecraftVersion) ? "版本未知" : $"Minecraft {minecraftVersion}";
        var loaderLabel = loader == LoaderKind.Vanilla ? "原版" : LoaderLabelOf(loader);

        return $"{version} · {loaderLabel} · {directoryLabel}";
    }

    private void UpdateFilterText()
    {
        // 双源也要被正确称呼：这里用 SourceLabel（双源时是两站名），不能用 SourceLabelOf(ActiveSource)。
        var source = SourceLabel;

        if (IsModPackTabActive)
        {
            // 搜索不按版本筛（玩家先找包、再在版本列表里挑构建），所以这里不出现「未过滤」警告。
            IsFilterLimited = false;
            FilterText = $"整合包搜索不按游戏版本筛选 · 数据源 {source}";
            return;
        }

        if (SelectedInstallTarget is null)
        {
            IsFilterLimited = true;
            FilterText = $"未选择安装目标：结果不按版本与加载器筛选 · 数据源 {source}";
            return;
        }

        if (SelectedInstallTarget.IsGlobal)
        {
            // 全局目录是显式选择的不筛版本：全局 mods 由所有未隔离版本共用，没有单一版本身份。
            IsFilterLimited = false;
            FilterText = $"全局目录不筛选版本与加载器 · 数据源 {source}";
            return;
        }

        if (_gameVersion is null)
        {
            IsFilterLimited = true;
            FilterText = $"「{SelectedInstallTarget.DisplayName}」的游戏版本未知：结果不按版本筛选 · 数据源 {source}";
            return;
        }

        IsFilterLimited = false;

        FilterText = ActiveKind == ContentKind.Mod
            ? $"筛选：Minecraft {_gameVersion} · {LoaderLabelOf(_loader)} · 数据源 {source}"
            : $"筛选：Minecraft {_gameVersion} · 数据源 {source}";
    }

    /// <summary>
    /// 搜索实际使用的加载器。非 Mod 分类一律传 <c>Vanilla</c>（= 不过滤）：
    /// 资源包/光影的 loaders 是 minecraft，按 Fabric 过滤会把结果清空；
    /// 整合包同理——它自带加载器，筛加载器等于把别的加载器的包藏起来。
    /// </summary>
    private LoaderKind EffectiveLoader => ActiveKind == ContentKind.Mod && !IsModPackTabActive ? _loader : LoaderKind.Vanilla;

    // ---------------- 数据源偏好 ----------------

    /// <summary>首次进入时读一次 <see cref="AppSettings.ContentSource"/>；之后以用户当前选择为准。</summary>
    private void EnsureSourceInitialized()
    {
        if (_sourceInitialized)
        {
            return;
        }

        _sourceInitialized = true;

        var configured = _settings?.Current.ContentSource;
        ActiveSource = string.Equals(configured?.Trim(), "curseforge", StringComparison.OrdinalIgnoreCase)
            ? ContentSourceKind.CurseForge
            : ContentSourceKind.Modrinth;
    }

    /// <summary>写回数据源偏好。失败只进状态栏，不阻断本次搜索。</summary>
    private async Task PersistSourceAsync(ContentSourceKind source, CancellationToken token)
    {
        if (_settings is null || ActiveSource != source)
        {
            return;
        }

        var name = source == ContentSourceKind.CurseForge ? "curseforge" : "modrinth";

        if (string.Equals(_settings.Current.ContentSource, name, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Current.ContentSource = name;

        try
        {
            await _settings.SaveAsync(token);
        }
        catch (OperationCanceledException)
        {
            // 页面切走，本次选择只在内存里生效。
        }
        catch (Exception ex)
        {
            InfoText = $"数据源偏好没能保存：{ex.Message}（本次会话仍然生效）";
        }
    }

    // ---------------- 搜索实现 ----------------

    /// <summary>切换分类 / 数据源时统一清空右侧详情与列表状态。</summary>
    private void ResetResultState()
    {
        Results.Clear();
        Versions.Clear();
        SelectedProject = null;
        SelectedVersion = null;
        VersionErrorText = null;
        VersionNoticeText = null;
        InstallStatus = string.Empty;
        InstallProgress = 0;
        ClosePlan();
        CloseDataPackTargetPicker();
        ClearModPackState();
        ResetModPackVersionFilter();
        _offset = 0;
        HasMore = false;
    }

    /// <summary>清空整合包的游戏版本筛选与未过滤全集。切分区 / 换项目 / 换数据源时调用。</summary>
    private void ResetModPackVersionFilter()
    {
        _allModPackVersions.Clear();

        if (ModPackVersionFilters.Count > 0)
        {
            ModPackVersionFilters.Clear();
        }

        _selectedModPackVersionFilter = null;
        OnPropertyChanged(nameof(SelectedModPackVersionFilter));
        OnPropertyChanged(nameof(ShowsModPackVersionFilter));
    }

    private async Task SearchCoreAsync(bool reset)
    {
        if (IsDemoMode)
        {
            BuildSampleResults();
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None);
        var token = _searchCts.Token;

        // 整合包分区：搜索返回的是 project_type=modpack 的项目，与四个内容分类互不影响。
        if (IsModPackTabActive)
        {
            await SearchModPacksAsync(reset, token);
            return;
        }

        // 空搜索在 CurseForge 上是「浏览热门」（sortField=2），在 Modrinth 上是热门页，
        // 两边都不需要拦截；粘贴项目链接时由 Core 走精确解析。
        if (reset)
        {
            _offset = 0;
            _modrinthOffset = 0;
            _curseForgeOffset = 0;
            _modrinthExhausted = false;
            _curseForgeExhausted = false;
            Results.Clear();
            VersionErrorText = null;
            InstallStatus = string.Empty;
            InstallProgress = 0;
        }

        IsSearching = true;
        ErrorText = null;
        InfoText = null;

        try
        {
            // 双源按 PCL2 的口径补取：一次「加载更多」的目标是至少新增一页。
            // 跨站去重会把整页吃掉（HasMore 仍为 true 但列表没变），所以新增不足一页时继续取，
            // 直到够一页、两站都走到头或补取次数用完。单源不补：它的去重只发生在分页边界。
            var extraPages = 0;

            // 单源偏移：reset 时从头开始；补取循环只对双源生效，所以本轮内它不需要推进。
            var singleOffset = reset ? 0 : _offset;

            while (true)
            {
                var fetched = IsBothSources
                    ? await SearchBothSourcesAsync(token)
                    : ActiveSource == ContentSourceKind.CurseForge
                        ? await SearchCurseForgeAsync(singleOffset, token)
                        : await SearchModrinthAsync(singleOffset, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                // 分页边界可能重复（服务端结果在两次请求之间会变），按来源 + 项目标识去重。
                var known = Results.Select(item => item.ProjectId).ToHashSet(StringComparer.Ordinal);
                var added = new List<LibraryProjectViewModel>();

                foreach (var card in fetched)
                {
                    if (known.Add(card.ProjectId))
                    {
                        Results.Add(card);
                        added.Add(card);
                    }
                }

                if (reset && Results.Count == 0)
                {
                    UpdateEmptyState();
                }

                _ = DownloadIconsAsync(added, token);

                if (!IsBothSources || added.Count >= PageSize || !HasMore || extraPages >= BothSourcesMaxExtraPages)
                {
                    break;
                }

                extraPages++;
            }
        }
        catch (OperationCanceledException)
        {
            // 切换分类 / 数据源 / 连续搜索 / 离开页面都会打断上一次请求，属正常路径。
        }
        catch (Exception ex)
        {
            ErrorText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
                OnPropertyChanged(nameof(IsEmpty));
                UpdateSummary();
            }
        }
    }

    private async Task<IReadOnlyList<LibraryProjectViewModel>> SearchModrinthAsync(int offset, CancellationToken token)
    {
        if (_modrinth is null)
        {
            return [];
        }

        var projects = await _modrinth.SearchSortedAsync(
            ActiveKind,
            SearchText,
            _gameVersion,
            EffectiveLoader,
            offset,
            PageSize,
            SelectedSort?.Key,
            token);

        if (token.IsCancellationRequested)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(_modrinth.LastError))
        {
            ErrorText = _modrinth.LastError;
        }

        _offset = offset + projects.Count;
        HasMore = projects.Count >= PageSize;

        return projects.Select(project => new LibraryProjectViewModel(project)).ToList();
    }

    private async Task<IReadOnlyList<LibraryProjectViewModel>> SearchCurseForgeAsync(int index, CancellationToken token)
    {
        if (_curseforge is null)
        {
            ErrorText = "CurseForge 服务尚未接线，无法搜索。";
            return [];
        }

        var projects = await _curseforge.SearchAsync(
            ActiveKind,
            SearchText,
            _gameVersion,
            EffectiveLoader,
            index,
            PageSize,
            token);

        if (token.IsCancellationRequested)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(_curseforge.LastError))
        {
            ErrorText = _curseforge.LastError;
        }

        _offset = index + projects.Count;

        // 官方搜索接口按 index + pageSize 翻页，与 Modrinth 的单源路径一致。
        HasMore = projects.Count >= PageSize;

        return projects.Select(project => new LibraryProjectViewModel(project)).ToList();
    }

    /// <summary>
    /// 双源搜索：并行查两站 → 各转成中性行 → <see cref="CrossSourceSearch.Merge"/> 去重排序 →
    /// 按合并顺序把每一行映射回卡片。
    ///
    /// 为什么用「键回查」而不是在合并结果里直接造卡片：合并与排序只认
    /// <see cref="SearchProjectInfo"/>（Core 类型，字段与展示无关），而图标、徽标、原始模型都在 App 侧。
    /// 两边用 <see cref="SearchProjectInfo.IdentityKey"/>（<c>cf:{id}</c> / <c>mr:{id}</c>）对应，
    /// 这个键在同一后端内唯一，跨后端不会撞（<see cref="CrossSourceSearch.IsSameProject"/> 先比来源，
    /// 所以两条同 Id 的不同站项目仍各自保留）。
    ///
    /// 排序规则不在这里实现：全部复用 Core 的 Merge（Lead 已用 17 条单测锁定语义）。
    /// </summary>
    private async Task<IReadOnlyList<LibraryProjectViewModel>> SearchBothSourcesAsync(CancellationToken token)
    {
        // 某一侧已经返回过不足一页时不再请求它：让走完的那一侧空转只是白花请求。
        var searchCurseForge = _curseforge is not null && !_curseForgeExhausted;

        // 两站并行：两站都不快，串行等于把延迟相加。谁先回来不影响结果，Merge 按分数排。
        // 两站各用各的偏移（PCL2 的 CurseForgeOffset / ModrinthOffset），
        // 一侧提前耗尽时另一侧不会跳着取。
        var curseForgeSearch = searchCurseForge
            ? _curseforge!.SearchAsync(ActiveKind, SearchText, _gameVersion, EffectiveLoader, _curseForgeOffset, PageSize, token)
            : Task.FromResult<IReadOnlyList<CurseForgeProject>>([]);

        var searchModrinth = _modrinth is not null && !_modrinthExhausted;

        var modrinthSearch = !searchModrinth
            // 双源不使用排序下拉：排序键只作用于 Modrinth 的结果集，而合并后的顺序由
            // CrossSourceSearch.Score 决定。这里传 null 让 Modrinth 侧按「有词用相关度、无词用下载量」取，
            // 与 PCL2 双源一致；否则 Modrinth 侧返回的「排序键下最热的 20 条」会先被排序键扭曲，
            // 再被合并层按热度重排，得到的不是两站最热的 20 条。
            ? Task.FromResult<IReadOnlyList<ModrinthProject>>([])
            : _modrinth!.SearchSortedAsync(
                ActiveKind,
                SearchText,
                _gameVersion,
                EffectiveLoader,
                _modrinthOffset,
                PageSize,
                sort: null,
                token);

        var curseForgeProjects = await curseForgeSearch.ConfigureAwait(true);
        var modrinthProjects = await modrinthSearch.ConfigureAwait(true);

        if (token.IsCancellationRequested)
        {
            return [];
        }

        // 只有真的发过请求时才搬 LastError：某一侧已耗尽被跳过时，那里留着的是上一页（与本页无关）的旧信息。
        if (searchModrinth && !string.IsNullOrWhiteSpace(_modrinth?.LastError))
        {
            ErrorText = _modrinth.LastError;
        }

        // 只有真的发过 CurseForge 请求时才搬它的 LastError：没 key 时那里留着的是与本次搜索无关的旧信息。
        if (searchCurseForge && !string.IsNullOrWhiteSpace(_curseforge!.LastError))
        {
            ErrorText = _curseforge.LastError;
        }

        // 耗尽标记与偏移都按「实际返回条数」推进（PCL2 同口径）：
        // 某一侧返回不足一页即视为走到头；否则从自己上次停下的位置继续。
        _modrinthExhausted = modrinthProjects.Count < PageSize;
        _curseForgeExhausted = curseForgeProjects.Count < PageSize;

        _modrinthOffset += modrinthProjects.Count;
        _curseForgeOffset += curseForgeProjects.Count;

        // HasMore：任一侧还有后续都算 true。两站都走到头才停。
        HasMore = !_modrinthExhausted || !_curseForgeExhausted;

        var cards = new Dictionary<string, LibraryProjectViewModel>(StringComparer.Ordinal);
        var curseForgeInfos = new List<SearchProjectInfo>(curseForgeProjects.Count);
        var modrinthInfos = new List<SearchProjectInfo>(modrinthProjects.Count);

        foreach (var project in curseForgeProjects)
        {
            var info = SearchProjectInfo.FromCurseForge(project);
            curseForgeInfos.Add(info);
            cards[info.IdentityKey] = new LibraryProjectViewModel(project);
        }

        foreach (var project in modrinthProjects)
        {
            var info = SearchProjectInfo.FromModrinth(project, IsModPackTabActive);
            modrinthInfos.Add(info);
            cards[info.IdentityKey] = new LibraryProjectViewModel(project, IsModPackTabActive);
        }

        // 跨站配对要在 Merge 之前算：Merge 只保留一条，被丢掉的那条在合并结果里已经看不见了，
        // 而「另一站也有」徽标恰恰要指向它。
        var counterpartKeys = BuildCounterpartKeys(curseForgeInfos, modrinthInfos);

        // limit 取两页之和而不是 PageSize：去重只会减少条目，取两页之和保证本页一条都不被裁掉。
        // 若按 PageSize 截断，被裁掉的条目在下一页的请求里已经跳过，等于永久丢失。
        var merged = CrossSourceSearch.Merge(curseForgeInfos, modrinthInfos, SearchText, PageSize * 2);

        var result = new List<LibraryProjectViewModel>(merged.Count);

        foreach (var info in merged)
        {
            if (!cards.TryGetValue(info.IdentityKey, out var card))
            {
                continue;
            }

            if (counterpartKeys.TryGetValue(info.IdentityKey, out var otherKey)
                && cards.TryGetValue(otherKey, out var other))
            {
                card.SetCounterpart($"{SourceLabelOf(other.Source)} 也有", other.WebUrl);
            }

            result.Add(card);
        }

        return result;
    }

    /// <summary>
    /// 找出每一项「另一站也有」的对象，返回 本项键 → 另一站那一项的键。
    ///
    /// 判据直接用 <see cref="CrossSourceSearch.IsSameProject"/>，不另写一套：合并层用它去重，
    /// 徽标用它指向被丢掉的那条——两处判据必须一致，否则会出现「标着另一站也有，合并时却没合并」。
    /// 两站各 20 条，两两比较 400 次，相对一次网络请求可以忽略。
    ///
    /// 只在「本页」范围内配对：对方项目落在另一站的下一页时这里看不到，那一条就不会带徽标。
    /// 这是分页的固有限制（PCL2 也是按页去重），比「把每一页的对方都拉下来」便宜得多。
    /// </summary>
    private static Dictionary<string, string> BuildCounterpartKeys(
        IReadOnlyList<SearchProjectInfo> curseForge,
        IReadOnlyList<SearchProjectInfo> modrinth)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var left in curseForge)
        {
            foreach (var right in modrinth)
            {
                if (!CrossSourceSearch.IsSameProject(left, right))
                {
                    continue;
                }

                pairs[left.IdentityKey] = right.IdentityKey;
                pairs[right.IdentityKey] = left.IdentityKey;
            }
        }

        return pairs;
    }

    /// <summary>
    /// 整合包搜索。两站各有自己的整合包通道：
    /// Modrinth 走 <see cref="ModpackCatalogClient"/>（<c>project_type:modpack</c>）；
    /// CurseForge 走 <see cref="ICurseForgeService.SearchModpacksAsync"/>（<c>classId=4471</c>）。
    ///
    /// 两条通道都<b>不传游戏版本与加载器</b>：玩家先找主题包，再在版本列表里按游戏版本挑构建。
    /// </summary>
    private async Task SearchModPacksAsync(bool reset, CancellationToken token)
    {
        if (IsBothSources && (_modpackCatalog is null || _curseforge is null))
        {
            IsSearching = false;
            ErrorText = "双源整合包搜索需要 Modrinth 与 CurseForge 两个服务，当前至少缺一个。";
            return;
        }

        if (!IsBothSources && ActiveSource == ContentSourceKind.Modrinth && _modpackCatalog is null)
        {
            IsSearching = false;
            ErrorText = "整合包服务尚未接线，无法搜索。";
            return;
        }

        if (reset)
        {
            _offset = 0;
            _modrinthOffset = 0;
            _curseForgeOffset = 0;
            _modrinthExhausted = false;
            _curseForgeExhausted = false;
            Results.Clear();
            VersionErrorText = null;
            InstallStatus = string.Empty;
            InstallProgress = 0;
            ClearModPackState();
        }

        IsSearching = true;
        ErrorText = null;
        InfoText = null;

        try
        {
            // 与内容分类的双源路径同一口径：新增不足一页就继续取，最多补 BothSourcesMaxExtraPages 页。
            var extraPages = 0;
            var singleOffset = reset ? 0 : _offset;

            while (true)
            {
                var cards = IsBothSources
                    ? await SearchModPacksBothSourcesAsync(token)
                    : ActiveSource == ContentSourceKind.CurseForge
                        ? await SearchModPacksCurseForgeAsync(singleOffset, token)
                        : await SearchModPacksModrinthAsync(singleOffset, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                // 分页边界可能重复（服务端结果在两次请求之间会变），按项目标识去重。
                var known = Results.Select(item => item.ProjectId).ToHashSet(StringComparer.Ordinal);
                var added = new List<LibraryProjectViewModel>();

                foreach (var card in cards)
                {
                    if (!known.Add(card.ProjectId))
                    {
                        continue;
                    }

                    Results.Add(card);
                    added.Add(card);
                }

                if (reset && Results.Count == 0)
                {
                    UpdateEmptyState();
                }

                _ = DownloadIconsAsync(added, token);

                if (!IsBothSources || added.Count >= PageSize || !HasMore || extraPages >= BothSourcesMaxExtraPages)
                {
                    break;
                }

                extraPages++;
            }
        }
        catch (OperationCanceledException)
        {
            // 切分区 / 连续搜索 / 离开页面都会打断上一次请求，属正常路径。
        }
        catch (Exception)
        {
            // 原始异常（响应体、堆栈）对用户没有信息量，统一成一句可操作的提示。
            ErrorText = "搜索整合包失败：网络或服务异常，稍后重试。";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
                OnPropertyChanged(nameof(IsEmpty));
                UpdateSummary();
            }
        }
    }

    /// <summary>Modrinth 整合包单源搜索。</summary>
    private async Task<IReadOnlyList<LibraryProjectViewModel>> SearchModPacksModrinthAsync(int offset, CancellationToken token)
    {
        if (_modpackCatalog is null)
        {
            ErrorText = "整合包服务尚未接线，无法搜索。";
            return [];
        }

        var projects = await _modpackCatalog.SearchAsync(SearchText, offset, PageSize, token).ConfigureAwait(true);

        if (token.IsCancellationRequested)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(_modpackCatalog.LastError))
        {
            ErrorText = _modpackCatalog.LastError;
        }

        _offset = offset + projects.Count;
        HasMore = projects.Count >= PageSize;

        return projects.Select(project => new LibraryProjectViewModel(project, isModPack: true)).ToList();
    }

    /// <summary>CurseForge 整合包单源搜索（classId=4471，无版本 / 加载器过滤）。</summary>
    private async Task<IReadOnlyList<LibraryProjectViewModel>> SearchModPacksCurseForgeAsync(int offset, CancellationToken token)
    {
        if (_curseforge is null)
        {
            ErrorText = "CurseForge 服务尚未接线，无法搜索整合包。";
            return [];
        }

        var projects = await _curseforge.SearchModpacksAsync(SearchText, offset, PageSize, token).ConfigureAwait(true);

        if (token.IsCancellationRequested)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(_curseforge.LastError))
        {
            ErrorText = _curseforge.LastError;
        }

        _offset = offset + projects.Count;
        HasMore = projects.Count >= PageSize;

        return projects.Select(project => new LibraryProjectViewModel(project, isModPack: true)).ToList();
    }

    /// <summary>
    /// 双源整合包搜索：两站并行 → <see cref="CrossSourceSearch.Merge"/> 去重混排 → 映射回卡片。
    ///
    /// 与内容分类的双源路径共用同一套合并规则与分页口径：两站各用各的偏移，
    /// 按实际返回条数推进；新增不足一页时由调用方继续补取（PCL2 的 TargetResultCount 循环）。
    /// </summary>
    private async Task<IReadOnlyList<LibraryProjectViewModel>> SearchModPacksBothSourcesAsync(CancellationToken token)
    {
        var searchCurseForge = _curseforge is not null && !_curseForgeExhausted;
        var searchModrinth = _modpackCatalog is not null && !_modrinthExhausted;

        var curseForgeTask = searchCurseForge
            ? _curseforge!.SearchModpacksAsync(SearchText, _curseForgeOffset, PageSize, token)
            : Task.FromResult<IReadOnlyList<CurseForgeProject>>([]);

        var modrinthTask = searchModrinth
            ? _modpackCatalog!.SearchAsync(SearchText, _modrinthOffset, PageSize, token)
            : Task.FromResult<IReadOnlyList<ModrinthProject>>([]);

        var curseForgeProjects = await curseForgeTask.ConfigureAwait(true);
        var modrinthProjects = await modrinthTask.ConfigureAwait(true);

        if (token.IsCancellationRequested)
        {
            return [];
        }

        if (searchModrinth && !string.IsNullOrWhiteSpace(_modpackCatalog?.LastError))
        {
            ErrorText = _modpackCatalog.LastError;
        }

        // 只有真的发过 CurseForge 请求时才搬它的 LastError：没发时那里留着与本次搜索无关的旧信息。
        if (searchCurseForge && !string.IsNullOrWhiteSpace(_curseforge!.LastError))
        {
            ErrorText = _curseforge.LastError;
        }

        // 与内容双源同一口径：按实际返回条数推进，任一侧还有后续就算 HasMore。
        _modrinthExhausted = modrinthProjects.Count < PageSize;
        _curseForgeExhausted = curseForgeProjects.Count < PageSize;

        _modrinthOffset += modrinthProjects.Count;
        _curseForgeOffset += curseForgeProjects.Count;

        HasMore = !_modrinthExhausted || !_curseForgeExhausted;

        var cards = new Dictionary<string, LibraryProjectViewModel>(StringComparer.Ordinal);
        var curseForgeInfos = new List<SearchProjectInfo>(curseForgeProjects.Count);
        var modrinthInfos = new List<SearchProjectInfo>(modrinthProjects.Count);

        foreach (var project in curseForgeProjects)
        {
            var info = SearchProjectInfo.FromCurseForge(project, isModPack: true);
            curseForgeInfos.Add(info);
            cards[info.IdentityKey] = new LibraryProjectViewModel(project, isModPack: true);
        }

        foreach (var project in modrinthProjects)
        {
            var info = SearchProjectInfo.FromModrinth(project, isModPack: true);
            modrinthInfos.Add(info);
            cards[info.IdentityKey] = new LibraryProjectViewModel(project, isModPack: true);
        }

        var counterpartKeys = BuildCounterpartKeys(curseForgeInfos, modrinthInfos);
        var merged = CrossSourceSearch.Merge(curseForgeInfos, modrinthInfos, SearchText, PageSize * 2);

        var result = new List<LibraryProjectViewModel>(merged.Count);

        foreach (var info in merged)
        {
            if (!cards.TryGetValue(info.IdentityKey, out var card))
            {
                continue;
            }

            if (counterpartKeys.TryGetValue(info.IdentityKey, out var otherKey)
                && cards.TryGetValue(otherKey, out var other))
            {
                card.SetCounterpart($"{SourceLabelOf(other.Source)} 也有", other.WebUrl);
            }

            result.Add(card);
        }

        return result;
    }

    private void UpdateEmptyState()
    {
        if (IsModPackTabActive)
        {
            var modPackSource = IsBothSources ? BothSourcesLabel : SourceLabelOf(ActiveSource);

            if (ErrorText is not null)
            {
                EmptyTitle = "暂时拿不到整合包列表";
                EmptyHint = "检查网络或代理后点「刷新」重试。若只有一侧失败，另一侧的结果仍会显示。";
                return;
            }

            var modPackQuery = SearchText.Trim();

            EmptyTitle = modPackQuery.Length > 0 ? "没有找到匹配的整合包" : "这里还没有整合包";
            EmptyHint = modPackQuery.Length > 0
                ? $"「{modPackQuery}」在 {modPackSource} 的整合包里没有结果。整合包搜索不按游戏版本筛选，换个关键词再试。"
                : $"输入关键词搜索 {modPackSource} 上的整合包，留空则按下载量列出热门整合包。";
            return;
        }

        var isCurseForge = IsCurseForgeSourceActive;

        // 双源的空状态不能用单源的文案：说「Modrinth 上没有结果」会让用户以为另一站没查过。
        if (IsBothSources)
        {
            if (ErrorText is not null)
            {
                EmptyTitle = "暂时拿不到资源列表";
                EmptyHint = "检查网络或代理后点「刷新」重试。若只有一侧失败，另一侧的结果仍会显示。";
                return;
            }

            var bothQuery = SearchText.Trim();

            EmptyTitle = bothQuery.Length > 0 ? "两站都没有找到匹配的资源" : "这里还没有内容";
            EmptyHint = bothQuery.Length > 0
                ? $"「{bothQuery}」在 Modrinth 与 CurseForge 的{KindLabel}里都没有结果。换个关键词再试。"
                : $"输入关键词同时搜索 Modrinth 与 CurseForge 的{KindLabel}，留空则按热度混排两站的热门项目。";
            return;
        }

        if (ErrorText is not null)
        {
            EmptyTitle = "暂时拿不到资源列表";
            EmptyHint = isCurseForge
                ? "点「刷新」重试；也可以直接把 CurseForge 项目页链接粘贴到搜索框。"
                : "检查网络或代理后点「刷新」重试。Modrinth 偶尔会限流，稍等几秒即可。";
            return;
        }

        var query = SearchText.Trim();

        if (isCurseForge)
        {
            EmptyTitle = query.Length > 0 ? "没有找到匹配的资源" : "这里还没有内容";
            EmptyHint = query.Length > 0
                ? $"「{query}」在 CurseForge 的{KindLabel}里没有结果。换个关键词，或直接粘贴项目页链接。"
                : $"输入关键词搜索 CurseForge 的{KindLabel}，留空则按下载量列出热门项目。";

            return;
        }

        if (query.Length > 0)
        {
            EmptyTitle = "没有找到匹配的资源";
            EmptyHint = $"「{query}」在 Modrinth 的{KindLabel}里没有结果。换个关键词，或在实例页选一个已安装的版本后回来刷新。";
            return;
        }

        EmptyTitle = "这里还没有内容";
        EmptyHint = $"输入关键词搜索 Modrinth 上的{KindLabel}，留空搜索则按下载量列出热门项目。";
    }

    private void UpdateSummary()
    {
        // 双源也要被正确称呼：这里用 SourceLabel（双源时是两站名）。
        var source = SourceLabel;

        if (IsSearching)
        {
            SetSubtitle("搜索中…");
            return;
        }

        if (Results.Count == 0)
        {
            SetSubtitle(IsDemoMode ? "示例数据" : $"暂无结果 · {source}");
            return;
        }

        SetSubtitle(HasMore
            ? $"{Results.Count}+ 个结果 · {source}"
            : $"{Results.Count} 个结果 · {source}");
    }

    private async Task DownloadIconsAsync(IReadOnlyList<LibraryProjectViewModel> cards, CancellationToken token)
    {
        if (cards.Count == 0 || (_modrinth is null && _curseforge is null))
        {
            return;
        }

        using var gate = new SemaphoreSlim(IconConcurrency, IconConcurrency);

        try
        {
            var tasks = cards
                .Where(card => card.HasPendingIcon)
                .Select(async card =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(true);

                    try
                    {
                        var path = await CacheIconAsync(card, token).ConfigureAwait(true);

                        if (path is not null && !token.IsCancellationRequested)
                        {
                            card.SetIconPath(path);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 列表已经换了，图标不再需要。
        }
        catch (Exception)
        {
            // 图标只是装饰：任何失败都让首字母占位继续生效，不上报成页面错误。
        }
    }

    /// <summary>两个后端的图标缓存走各自的服务；对应服务缺失时返回 null。</summary>
    private async Task<string?> CacheIconAsync(LibraryProjectViewModel card, CancellationToken token)
    {
        var url = card.IconUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (card.Source == ContentSourceKind.CurseForge)
        {
            return _curseforge is null ? null : await _curseforge.CacheIconAsync(url, token);
        }

        return _modrinth is null ? null : await _modrinth.CacheIconAsync(url, token);
    }

    // ---------------- 项目详情 ----------------

    /// <summary>
    /// 选中项目后加载详情（正文 / 图库 / 源码 / 问题 / 许可）。
    ///
    /// 与版本列表并发：详情要等正文与 4 张缩略图，不能让它把版本下拉拖住。
    /// 切换项目时取消上一次请求；迟到的响应靠 token 丢弃，不会把 A 的正文贴到 B 上。
    /// 详情是增强信息，失败保持空白——版本列表与安装按钮仍然可用（原因由 Core 写日志）。
    /// </summary>
    private async Task LoadDetailsAsync(LibraryProjectViewModel? project)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        ResetDetails();

        if (project is null || IsDemoMode || _modrinth is null)
        {
            return;
        }

        // 契约里的详情接口只有 Modrinth：CurseForge 项目没有可拉的正文与图库。
        if (project.Source != ContentSourceKind.Modrinth)
        {
            return;
        }

        _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None);
        var token = _detailCts.Token;

        IsLoadingDetails = true;

        try
        {
            var detail = await _modrinth.GetProjectAsync(project.ProjectId, token);

            if (token.IsCancellationRequested || detail is null)
            {
                return;
            }

            ApplyDetails(detail);

            // 正文已经能看了，先收起加载条；图库继续在后台补，不让 4 张缩略图拖慢可读内容。
            IsLoadingDetails = false;

            await LoadGalleryAsync(detail.GalleryUrls, token);
        }
        catch (OperationCanceledException)
        {
            // 切到别的项目 / 离开页面，属正常路径。
        }
        catch (Exception)
        {
            // 详情只是增强：失败不写 ErrorText，否则会把版本列表和安装按钮一起标红。
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingDetails = false;
            }
        }
    }

    /// <summary>把详情填进右栏。正文先过 Markdown 清理再截断，链接逐项去空白。</summary>
    private void ApplyDetails(ModrinthProject detail)
    {
        ProjectBody = Truncate(StripMarkdown(detail.Body), BodyPreviewLength);
        IssuesUrl = NormalizeUrl(detail.IssuesUrl);
        SourceUrl = NormalizeUrl(detail.SourceUrl);
        LicenseId = string.IsNullOrWhiteSpace(detail.LicenseId) ? null : detail.LicenseId.Trim();
    }

    /// <summary>清空上一次项目的详情。切换项目 / 切分类 / 清空选择都走这里。</summary>
    private void ResetDetails()
    {
        ProjectBody = string.Empty;
        IssuesUrl = null;
        SourceUrl = null;
        LicenseId = null;
        IsLoadingDetails = false;

        if (ProjectGallery.Count > 0)
        {
            ProjectGallery.Clear();
        }

        // 集合不是可观察属性，Count / 内容变化要手动通知两个派生布尔值。
        OnPropertyChanged(nameof(HasProjectGallery));
        OnPropertyChanged(nameof(HasProjectDetails));
    }

    /// <summary>
    /// 下载图库缩略图到本地缓存再填进 <see cref="ProjectGallery"/>。
    ///
    /// 复用 <see cref="IModrinthService.CacheIconAsync"/>：它按 URL 内容哈希落盘，
    /// 同一张图第二次选中直接命中缓存，因此不需要再写一条图片下载通道。
    /// 单张失败只是少一张缩略图，不影响其余三张。
    /// </summary>
    private async Task LoadGalleryAsync(IReadOnlyList<string> urls, CancellationToken token)
    {
        if (urls.Count == 0 || _modrinth is null)
        {
            return;
        }

        var targets = urls.Take(GalleryPreviewCount).ToList();
        using var gate = new SemaphoreSlim(IconConcurrency, IconConcurrency);

        IReadOnlyList<string?> paths;

        try
        {
            paths = await Task.WhenAll(targets.Select(async url =>
            {
                await gate.WaitAsync(token).ConfigureAwait(true);

                try
                {
                    return await _modrinth.CacheIconAsync(url, token).ConfigureAwait(true);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                ProjectGallery.Add(path);
            }
        }

        OnPropertyChanged(nameof(HasProjectGallery));
        OnPropertyChanged(nameof(HasProjectDetails));
    }

    private static string? NormalizeUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>
    /// Markdown → 纯文本。实现在 Core 的 <see cref="ModrinthService.StripMarkdown"/>：
    /// 测试项目只引用 Core，纯文本清理逻辑必须放在那里才能直接单测。
    /// </summary>
    internal static string StripMarkdown(string? markdown) => ModrinthService.StripMarkdown(markdown);

    // ---------------- 版本列表 ----------------

    private async Task LoadVersionsAsync(LibraryProjectViewModel? project)
    {
        _versionCts?.Cancel();
        _versionCts?.Dispose();
        _versionCts = null;

        Versions.Clear();
        _allModPackVersions.Clear();
        ResetModPackVersionFilter();
        SelectedVersion = null;
        VersionErrorText = null;
        VersionNoticeText = null;

        if (project is null)
        {
            return;
        }

        if (IsDemoMode)
        {
            VersionErrorText = "示例模式：未接线在线服务，无法查询版本。";
            return;
        }

        _versionCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None);
        var token = _versionCts.Token;

        IsLoadingVersions = true;

        try
        {
            if (IsModPackTabActive)
            {
                await FillModPackVersionsAsync(project, token);
            }
            else if (project.Source == ContentSourceKind.CurseForge)
            {
                await FillCurseForgeVersionsAsync(project, token);
            }
            else
            {
                await FillModrinthVersionsAsync(project, token);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 整合包：不按安装目标筛版本，全部构建进列表，再由玩家用「游戏版本」下拉筛选。
            if (IsModPackTabActive)
            {
                RebuildModPackVersionFilters();
                ApplyModPackVersionFilter();

                if (_allModPackVersions.Count == 0 && VersionErrorText is null)
                {
                    VersionErrorText = "这个整合包没有可下载的构建。";
                    return;
                }

                var notice = "整合包不按安装目标筛选：下面是该项目的全部构建，用上面的「游戏版本」挑选。";

                if (_allModPackVersions.Any(item => !item.IsDownloadable))
                {
                    notice += " 部分文件未开放第三方下载：选中这类文件时安装按钮会变成「去网页下载」。";
                }

                VersionNoticeText = notice;
                return;
            }

            // 默认选最新稳定版：发布时间最新的往往是 snapshot / beta，一键安装不该默认装测试版。
            // CurseForge 优先挑能直连下载的那个，否则按钮一进来就是「去网页下载」。
            SelectedVersion = Versions.FirstOrDefault(item => item.IsRelease && item.IsDownloadable)
                ?? Versions.FirstOrDefault(item => item.IsDownloadable)
                ?? Versions.FirstOrDefault();

            if (Versions.Count == 0 && VersionErrorText is null)
            {
                VersionErrorText = _gameVersion is null
                    ? "这个项目没有可下载的版本。"
                    : $"这个项目没有适配 Minecraft {_gameVersion} 的版本，换一个项目试试。";
            }
            else if (_gameVersion is null && Versions.Count > 0)
            {
                VersionNoticeText = "未确定游戏版本：下面是该项目的全部构建，请挑与你的游戏版本匹配的那个。";
            }
        }
        catch (OperationCanceledException)
        {
            // 切换到别的项目，旧请求作废。
        }
        catch (Exception ex)
        {
            VersionErrorText = $"读取版本失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingVersions = false;
            }
        }
    }

    /// <summary>
    /// 整合包版本列表：<b>不按安装目标过滤</b>，取项目的全部构建。
    /// Modrinth 走 <c>project/{id}/version</c>（不传 game_versions / loaders）；
    /// CurseForge 走 <see cref="ICurseForgeService.GetModpackFilesAsync"/>（不传 gameVersion / modLoaderType）。
    /// </summary>
    private async Task FillModPackVersionsAsync(LibraryProjectViewModel project, CancellationToken token)
    {
        if (project.Source == ContentSourceKind.CurseForge)
        {
            if (_curseforge is null)
            {
                VersionErrorText = "CurseForge 服务未就绪：无法查询整合包文件列表。";
                return;
            }

            var files = await _curseforge.GetModpackFilesAsync(project.CurseForgeId, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_curseforge.LastError))
            {
                VersionErrorText = _curseforge.LastError;
            }

            foreach (var file in files)
            {
                _allModPackVersions.Add(new LibraryVersionViewModel(file, showGameVersions: true));
            }

            return;
        }

        if (_modrinth is null)
        {
            VersionErrorText = "示例模式：未接线在线服务，无法查询版本。";
            return;
        }

        var versions = await _modrinth.GetVersionsAsync(project.ProjectId, null, LoaderKind.Vanilla, token);

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_modrinth.LastError))
        {
            VersionErrorText = _modrinth.LastError;
        }

        foreach (var version in versions)
        {
            _allModPackVersions.Add(new LibraryVersionViewModel(version, showGameVersions: true));
        }
    }

    /// <summary>
    /// 重建「游戏版本」筛选项：第一项永远是「全部版本」，其余来自项目声明的 MC 版本（去重、降序）。
    /// 尽量保留玩家上一次的选择；那个版本在新项目里不存在时退回「全部版本」。
    /// </summary>
    private void RebuildModPackVersionFilters()
    {
        var previous = _selectedModPackVersionFilter?.Version;

        ModPackVersionFilters.Clear();
        ModPackVersionFilters.Add(new ModPackVersionFilterOption(null, AllModPackVersionsLabel));

        foreach (var gameVersion in CollectGameVersionOptions(_allModPackVersions))
        {
            ModPackVersionFilters.Add(new ModPackVersionFilterOption(gameVersion, gameVersion));
        }

        _selectedModPackVersionFilter = ModPackVersionFilters.FirstOrDefault(item =>
                                           string.Equals(item.Version, previous, StringComparison.Ordinal))
                                       ?? ModPackVersionFilters[0];

        OnPropertyChanged(nameof(SelectedModPackVersionFilter));
        OnPropertyChanged(nameof(ShowsModPackVersionFilter));
    }

    /// <summary>把未过滤全集按当前游戏版本筛选项投影到 <see cref="Versions"/>，并尽量保留原选择。</summary>
    private void ApplyModPackVersionFilter()
    {
        var previousName = SelectedVersion?.Name;
        var wanted = _selectedModPackVersionFilter?.Version;

        Versions.Clear();

        foreach (var version in _allModPackVersions)
        {
            if (!string.IsNullOrEmpty(wanted) && !version.SupportsGameVersion(wanted))
            {
                continue;
            }

            Versions.Add(version);
        }

        SelectedVersion = Versions.FirstOrDefault(item =>
                               previousName is not null
                               && string.Equals(item.Name, previousName, StringComparison.Ordinal))
                           ?? Versions.FirstOrDefault(item => item.IsRelease && item.IsDownloadable)
                           ?? Versions.FirstOrDefault(item => item.IsDownloadable)
                           ?? Versions.FirstOrDefault();

        OnPropertyChanged(nameof(ShowsModPackVersionFilter));
    }

    /// <summary>
    /// 从一批版本里归纳「按游戏版本筛选」的取值：只保留 Minecraft 版本形态的标签
    /// （<c>1.20.1</c>、<c>24w14a</c>），排除 CurseForge 混在 <c>gameVersions</c> 里的
    /// <c>Forge</c> / <c>Fabric</c> / <c>Client</c> / <c>Server</c>。
    /// </summary>
    internal static IReadOnlyList<string> CollectGameVersionOptions(IEnumerable<LibraryVersionViewModel> versions)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            foreach (var gameVersion in version.GameVersions)
            {
                var trimmed = gameVersion.Trim();

                if (trimmed.Length > 0 && LooksLikeMinecraftVersion(trimmed))
                {
                    values.Add(trimmed);
                }
            }
        }

        var sorted = values.ToList();
        sorted.Sort(CompareMinecraftVersions);

        return sorted;
    }

    /// <summary>「1.20.1」/「1.7」这类发布版本号，或「24w14a」这类快照号。</summary>
    internal static bool LooksLikeMinecraftVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // 快照：两位年 + w + 两位周 + 至少一位后缀字符（24w14a / 23w13a_or_b）。
        if (text.Length >= 5
            && char.IsAsciiDigit(text[0])
            && char.IsAsciiDigit(text[1])
            && text[2] is 'w' or 'W'
            && char.IsAsciiDigit(text[3])
            && char.IsAsciiDigit(text[4]))
        {
            return text.Length == 5 || char.IsLetter(text[5]) || text[5] == '_';
        }

        var parts = text.Split('.');

        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 游戏版本比较：发布版本按 主.次.修订 降序，且发布版本整体排在快照之前；
    /// 两个快照之间按名字倒序（近似发布时间）。只用于筛选项排序，不参与兼容性判断。
    /// </summary>
    internal static int CompareMinecraftVersions(string? left, string? right)
    {
        var leftRelease = ParseReleaseVersion(left);
        var rightRelease = ParseReleaseVersion(right);

        if (leftRelease is not null && rightRelease is not null)
        {
            return rightRelease.Value.CompareTo(leftRelease.Value);
        }

        if (leftRelease is not null)
        {
            return -1;
        }

        if (rightRelease is not null)
        {
            return 1;
        }

        return string.CompareOrdinal(right ?? string.Empty, left ?? string.Empty);
    }

    private static long? ParseReleaseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split('.');

        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        var patch = 0L;

        if (parts.Length == 3
            && !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return null;
        }

        return (major * 1_000_000) + (minor * 1_000) + patch;
    }

    private async Task FillModrinthVersionsAsync(LibraryProjectViewModel project, CancellationToken token)
    {
        if (_modrinth is null)
        {
            VersionErrorText = "示例模式：未接线在线服务，无法查询版本。";
            return;
        }

        var versions = await _modrinth.GetVersionsAsync(project.ProjectId, _gameVersion, EffectiveLoader, token);

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_modrinth.LastError))
        {
            VersionErrorText = _modrinth.LastError;
        }

        foreach (var version in versions)
        {
            Versions.Add(new LibraryVersionViewModel(version));
        }
    }

    private async Task FillCurseForgeVersionsAsync(LibraryProjectViewModel project, CancellationToken token)
    {
        if (_curseforge is null)
        {
            VersionErrorText = "CurseForge 服务未就绪：无法查询文件列表。";
            return;
        }

        var files = await _curseforge.GetFilesAsync(project.CurseForgeId, _gameVersion, EffectiveLoader, token);

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_curseforge.LastError))
        {
            VersionErrorText = _curseforge.LastError;
        }

        foreach (var file in files)
        {
            Versions.Add(new LibraryVersionViewModel(file));
        }

        if (Versions.Any(item => !item.IsDownloadable))
        {
            VersionNoticeText = "部分文件未开放第三方下载：选中这类文件时安装按钮会变成「去网页下载」。";
        }
    }

    // ---------------- 示例数据 ----------------

    /// <summary>
    /// Core 未接线时的占位数据。真实运行时 <see cref="IsDemoMode"/> 为 false，这里不会被调用。
    /// 保留它是为了让「页面单独可用」这条约定在 Core 注册被注释掉时依然成立。
    /// </summary>
    private void BuildSampleResults()
    {
        Results.Clear();
        _offset = 0;
        HasMore = false;
        IsSearching = false;
        ErrorText = null;

        (string Title, string Slug, string Author, string Description, long Downloads)[] samples = IsModPackTabActive
            ?
            [
                ("Fabulously Optimized", "fabulously-optimized", "robotkoer", "以帧率为第一目标的 Fabric 整合包，装上就能玩。", 32_118_402),
                ("Better MC [FORGE]", "better-mc-forge-bmc1", "LunaPixelStudios", "带任务线与冒险内容的 Forge 整合包。", 21_884_117),
            ]
            : ActiveKind switch
        {
            ContentKind.ResourcePack =>
            [
                ("Fresh Animations", "fresh-animations", "FreshLX", "重制动物的默认动画，保留原版手感。", 51_236_521),
                ("Translations for Sodium", "translations-for-sodium", "Robotkoer", "给 Sodium 的视频设置界面补上多语言。", 23_089_729),
            ],
            ContentKind.ShaderPack =>
            [
                ("Complementary Shaders - Reimagined", "complementary-reimagined", "EminGT", "画面与性能平衡得最好的光影之一。", 72_118_338),
                ("BSL Shaders", "bsl-shaders", "capttatsu", "柔和的光照与体积云。", 46_797_808),
            ],
            ContentKind.DataPack =>
            [
                ("Terralith", "terralith", "Starmute", "用原版方块重写地形生成，新增 100+ 生物群系。", 41_337_120),
                ("Vanilla Tweaks", "vanilla-tweaks", "Vanilla Tweaks", "一组原版体验增强的数据包集合。", 28_915_402),
            ],
            _ =>
            [
                ("Sodium", "sodium", "jellysquid3", "重写渲染管线，大幅提升帧率并减少卡顿。", 250_585_660),
                ("Sodium Extra", "sodium-extra", "FlashyReese", "补上 Sodium 没有开关的那些选项。", 105_327_434),
                ("Reese's Sodium Options", "reeses-sodium-options", "FlashyReese", "Sodium 的替代设置界面。", 87_754_130),
            ],
        };

        foreach (var (title, slug, author, description, downloads) in samples)
        {
            Results.Add(new LibraryProjectViewModel(
                new ModrinthProject
                {
                    ProjectId = "sample-" + slug,
                    Slug = slug,
                    Title = title,
                    Description = description,
                    Author = author,
                    Downloads = downloads,
                    Kind = ActiveKind,
                },
                isModPack: IsModPackTabActive));
        }

        EmptyTitle = "示例数据";
        EmptyHint = "Core 服务尚未接线，以上条目仅用于预览布局。";
        OnPropertyChanged(nameof(IsEmpty));
        UpdateSummary();
    }

    private static string KindLabelOf(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "资源包",
        ContentKind.ShaderPack => "光影包",
        ContentKind.DataPack => "数据包",
        ContentKind.World => "世界",
        _ => "模组",
    };

    /// <summary>内容子目录名。直接复用 Core 的映射，避免两处对「mods / resourcepacks」各写一份。</summary>
    private static string KindDirectoryOf(ContentKind kind) => ContentTarget.DirectoryNameOf(kind);

    private static string LoaderLabelOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版（不按加载器过滤）",
    };

    private static string SourceLabelOf(ContentSourceKind source) =>
        source == ContentSourceKind.CurseForge ? "CurseForge" : "Modrinth";
}

/// <summary>
/// 搜索结果里的一张卡片。
///
/// 两个后端（<see cref="ModrinthProject"/> / <see cref="CurseForgeProject"/>）字段形状不同，
/// 归一在这里完成，页面与 XAML 只认这套展示属性。
/// </summary>
public sealed partial class LibraryProjectViewModel : ObservableObject
{
    private static readonly string[] LoaderCategories = ["fabric", "quilt", "forge", "neoforge"];

    private readonly ModrinthProject? _modrinth;
    private readonly CurseForgeProject? _curseForge;

    /// <param name="isModPack">
    /// 这张卡片来自「整合包」分区。<see cref="ModrinthProject.Kind"/> 里没有整合包这一类
    /// （见 <see cref="ContentKind"/> 的说明），所以类型徽标与项目页链接要靠这个标志修正。
    /// </param>
    public LibraryProjectViewModel(ModrinthProject project, bool isModPack = false)
    {
        _modrinth = project ?? throw new ArgumentNullException(nameof(project));
        IsModPack = isModPack;
        Source = ContentSourceKind.Modrinth;
        Initial = FirstCharOf(project.Title);
    }

    public LibraryProjectViewModel(CurseForgeProject project, bool isModPack = false)
    {
        _curseForge = project ?? throw new ArgumentNullException(nameof(project));
        IsModPack = isModPack || project.IsModpack;
        Source = ContentSourceKind.CurseForge;
        Initial = FirstCharOf(project.Name);
    }

    /// <summary>这张卡片来自哪个数据源。</summary>
    public ContentSourceKind Source { get; }

    /// <summary>
    /// 这张卡片来自「整合包」分区。<see cref="ModrinthProject.Kind"/> 里没有整合包这一类，
    /// 所以徽标与项目页链接要靠它修正。
    /// </summary>
    public bool IsModPack { get; }

    /// <summary>Modrinth 原始项目；CurseForge 卡片为 null。</summary>
    public ModrinthProject? Project => _modrinth;

    /// <summary>归一后的内容类型。整合包不参与（它不属于 <see cref="ContentKind"/>）。</summary>
    public ContentKind Kind => _modrinth?.Kind ?? _curseForge?.Kind ?? ContentKind.Mod;

    /// <summary>
    /// 这张卡片是服务端插件。搜索标签是「模组」时也可能拿到（Modrinth 把插件的
    /// <c>project_type</c> 报成 <c>mod</c>），界面据此禁掉安装并给出说明。
    /// </summary>
    public bool IsServerPlugin => !IsModPack && ServerPluginPolicy.IsServerPlugin(Kind);

    /// <summary>CurseForge 原始项目；Modrinth 卡片为 null。</summary>
    public CurseForgeProject? CurseForge => _curseForge;

    /// <summary>CurseForge 的数字项目 Id；Modrinth 卡片为 0。</summary>
    public long CurseForgeId => _curseForge?.Id ?? 0;

    /// <summary>去重键。两个后端的 Id 体系不通用，加前缀避免同一个列表里互相吞掉。</summary>
    public string ProjectId => _modrinth?.ProjectId ?? $"cf:{_curseForge!.Id}";

    public string Title => _modrinth?.Title ?? _curseForge!.Name;

    /// <summary>
    /// 项目主页。整合包在 Modrinth 上是独立的 <c>/modpack/</c> 路径，CurseForge 上是
    /// <c>/minecraft/modpacks/</c>（由 <see cref="CurseForgeProject.WebUrl"/> 按 IsModpack 分派）；
    /// 复用 <see cref="ModrinthProject.WebUrl"/> 会把它错指到 <c>/mod/</c>。
    /// </summary>
    public string WebUrl => IsModPack
        ? _modrinth is not null
            ? $"https://modrinth.com/modpack/{_modrinth.Slug}"
            : _curseForge!.WebUrl
        : _modrinth?.WebUrl ?? _curseForge!.WebUrl;

    /// <summary>无图标时的占位字母。</summary>
    public string Initial { get; }

    /// <summary>卡片上只放一行描述，完整文本交给 ToolTip。</summary>
    public string Description => _modrinth?.Description ?? _curseForge?.Summary ?? string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string? IconUrl => _modrinth?.IconUrl ?? _curseForge?.IconUrl;

    /// <summary>图标已缓存到本地的绝对路径。为 null 时显示首字母占位。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private string? _iconPath;

    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);

    /// <summary>还需要下载图标（有地址、尚未缓存）。</summary>
    public bool HasPendingIcon => string.IsNullOrWhiteSpace(IconPath) && !string.IsNullOrWhiteSpace(IconUrl);

    public string AuthorText
    {
        get
        {
            var author = _modrinth?.Author ?? _curseForge?.Authors;

            return string.IsNullOrWhiteSpace(author) ? "未知作者" : author;
        }
    }

    /// <summary>卡片右上角的类型徽标。</summary>
    public string KindLabel => IsModPack
        ? "整合包"
        : Kind switch
        {
            ContentKind.ResourcePack => "资源包",
            ContentKind.ShaderPack => "光影",
            ContentKind.DataPack => "数据包",

            // 「服务端插件」四个字本身就是第一道提示：用户在列表里就能看出这个 jar
            // 不属于客户端，不必等点了安装才被告知。
            ContentKind.Plugin => ServerPluginPolicy.KindLabel,
            _ => "模组",
        };

    /// <summary>「作者 · 1.2 亿次下载」这种一行元信息。</summary>
    public string MetaText => $"{AuthorText} · {FormatCount(DownloadCount)} 次下载";

    private long DownloadCount => _modrinth?.Downloads ?? _curseForge?.DownloadCount ?? 0;

    /// <summary>分类徽标，最多展示 3 个。Mod 的加载器标签已在分类选项卡上体现，这里去掉避免重复。</summary>
    public IReadOnlyList<string> Badges => _modrinth is null
        ? []
        : _modrinth.Categories
            .Where(category => _modrinth.Kind != ContentKind.Mod
                               || !LoaderCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            .Take(3)
            .ToList();

    public bool HasBadges => Badges.Count > 0;

    /// <summary>「另一站也有」徽标文案；null 表示只有当前站收录。</summary>
    private string? _counterpartLabel;

    /// <summary>另一站的项目页地址，点徽标直接跳过去。</summary>
    private string? _counterpartUrl;

    /// <summary>
    /// 另一站（CurseForge 或 Modrinth）的收录提示。只有双源搜索会回填：
    /// 单源结果里不存在「另一站」，留着这个徽标会让人以为漏了数据源。
    /// </summary>
    public string? CounterpartLabel => _counterpartLabel;

    public bool HasCounterpart => !string.IsNullOrWhiteSpace(_counterpartLabel);

    public string? CounterpartUrl => _counterpartUrl;

    /// <summary>
    /// 由 <see cref="LibraryViewModel"/> 在合并去重时回填。
    /// 不做成构造参数：卡片先构造（图标 / 徽标都靠它），配对要等两站结果都回来才知道。
    /// </summary>
    internal void SetCounterpart(string label, string? url)
    {
        _counterpartLabel = label;
        _counterpartUrl = url;

        OnPropertyChanged(nameof(CounterpartLabel));
        OnPropertyChanged(nameof(HasCounterpart));
        OnPropertyChanged(nameof(CounterpartUrl));
    }

    internal void SetIconPath(string path) => IconPath = path;

    private static string FirstCharOf(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "?" : title[..1].ToUpperInvariant();

    /// <summary>下载量 → 中文紧凑写法。这里不用字节单位：2.5 亿次下载不是「238 MB」。</summary>
    internal static string FormatCount(long count)
    {
        if (count < 0)
        {
            return "—";
        }

        if (count < 10_000)
        {
            return count.ToString("N0", CultureInfo.CurrentCulture);
        }

        if (count < 100_000_000)
        {
            return $"{count / 10_000d:0.#} 万";
        }

        return $"{count / 100_000_000d:0.#} 亿";
    }
}

/// <summary>
/// 版本下拉里的一项。Modrinth 用 <see cref="ModrinthVersion"/>，CurseForge 用
/// <see cref="CurseForgeFile"/>，展示字段与安装分派都由这里统一。
///
/// <paramref name="showGameVersions"/> 只在整合包分区为 true：那里的版本列表不按安装目标过滤，
/// 玩家要靠每项的「支持版本」判断该装哪个构建。
/// </summary>
public sealed class LibraryVersionViewModel
{
    private readonly ModrinthVersion? _modrinth;
    private readonly CurseForgeFile? _curseForge;

    public LibraryVersionViewModel(ModrinthVersion version, bool showGameVersions = false)
    {
        _modrinth = version ?? throw new ArgumentNullException(nameof(version));
        Source = ContentSourceKind.Modrinth;
        ShowGameVersions = showGameVersions;
    }

    public LibraryVersionViewModel(CurseForgeFile file, bool showGameVersions = false)
    {
        _curseForge = file ?? throw new ArgumentNullException(nameof(file));
        Source = ContentSourceKind.CurseForge;
        ShowGameVersions = showGameVersions;
    }

    /// <summary>这个版本来自哪个数据源，安装时据此分派。</summary>
    public ContentSourceKind Source { get; }

    /// <summary>Modrinth 原始版本；CurseForge 版本为 null。</summary>
    public ModrinthVersion? Version => _modrinth;

    /// <summary>CurseForge 原始文件；Modrinth 版本为 null。</summary>
    public CurseForgeFile? File => _curseForge;

    public string Name => _modrinth is not null
        ? (string.IsNullOrWhiteSpace(_modrinth.Name) ? _modrinth.VersionNumber : _modrinth.Name)
        : _curseForge!.Label;

    /// <summary>版本号 / 文件标签，用于给导入出来的实例命名。</summary>
    public string VersionNumber => _modrinth?.VersionNumber ?? _curseForge?.Label ?? Name;

    /// <summary>这个构建支持的游戏版本（原样，含 CurseForge 的 Forge / Fabric 等标签）。</summary>
    public IReadOnlyList<string> GameVersions => _modrinth?.GameVersions ?? _curseForge?.GameVersions ?? [];

    /// <summary>是否在版本项里显示支持的游戏版本（整合包分区为 true）。</summary>
    public bool ShowGameVersions { get; }

    /// <summary>「支持 1.20.1、1.20.2、1.20.3 等 5 个版本」这类副标题；没有版本信息时为空串。</summary>
    public string GameVersionText
    {
        get
        {
            var versions = GameVersions
                .Where(value => LibraryViewModel.LooksLikeMinecraftVersion(value))
                .ToList();

            if (versions.Count == 0)
            {
                return string.Empty;
            }

            var shown = string.Join("、", versions.Take(3));

            return versions.Count > 3
                ? $"支持 {shown} 等 {versions.Count} 个版本"
                : $"支持 {shown}";
        }
    }

    public bool HasGameVersionText => ShowGameVersions && GameVersionText.Length > 0;

    /// <summary>
    /// 这个构建是否声明支持某个游戏版本。版本集合为空时按「未知」放行——
    /// 拿不到声明不应当被当作「不支持」，否则筛选会把无法判断的条目全部藏掉。
    /// </summary>
    public bool SupportsGameVersion(string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion) || GameVersions.Count == 0)
        {
            return true;
        }

        return GameVersions.Contains(gameVersion.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public string ChannelLabel => IsRelease ? "稳定" : IsBeta ? "测试" : "实验";

    public bool IsRelease => _modrinth?.IsRelease ?? _curseForge?.IsRelease ?? false;

    public bool IsBeta => _modrinth?.IsBeta ?? _curseForge?.IsBeta ?? false;

    /// <summary>文件是否可直连下载。CurseForge 作者关闭第三方分发时为 false。</summary>
    public bool IsDownloadable => _curseForge?.IsDownloadable ?? true;

    public string SizeText => Conv.FormatSize(_modrinth?.FileSize ?? _curseForge?.FileLength ?? 0);

    public string DateText
    {
        get
        {
            var date = _modrinth?.PublishedAt ?? _curseForge?.FileDate;

            return date?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
        }
    }

    /// <summary>「稳定 · 2025-03-03 · 949 KB」。</summary>
    public string Summary => $"{ChannelLabel} · {DateText} · {SizeText}";

    public string FileName => _modrinth?.FileName ?? _curseForge?.FileName ?? string.Empty;
}

/// <summary>
/// 「将装到」下拉里的一项。三种来源（实例 / 未建实例的已安装版本 / 全局目录）在界面上
/// 都必须能被区分：落点不同、筛选条件也不同。
/// </summary>
public sealed class LibraryInstallTargetViewModel
{
    /// <summary>稳定标识：实例 Id / <c>version:&lt;版本名&gt;</c> / <c>global</c>。</summary>
    public required string Key { get; init; }

    /// <summary>实例 Id；非实例项为 null。用于默认选中当前实例。</summary>
    public string? InstanceKey { get; init; }

    /// <summary>主行文案：实例名 / 版本名 / 全局目录。</summary>
    public required string DisplayName { get; init; }

    /// <summary>副行文案：游戏版本 · 加载器 · 落点说明。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>实际游戏目录。未建实例的版本与全局目录都是全局 <c>.minecraft</c>。</summary>
    public string? GameDirectory { get; init; }

    /// <summary>该目标对应的原版 Minecraft 版本；null 表示不按版本筛选（全局目录 / 版本未知）。</summary>
    public string? MinecraftVersion { get; init; }

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>全局目录没有版本身份，筛选时按「不筛选版本与加载器」处理。</summary>
    public bool IsGlobal { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>整合包版本列表的「游戏版本」筛选项。<see cref="Version"/> 为 null 表示「全部版本」。</summary>
public sealed class ModPackVersionFilterOption(string? version, string label)
{
    public string? Version { get; } = version;

    public string Label { get; } = label;

    public override string ToString() => Label;
}

/// <summary>
/// 数据包安装位置的一个选项。包一层是为了给 XAML 提供「全局 / 存档」徽标与可读目录，
/// 不把 Core 的 <see cref="DataPackInstallTarget"/> 直接当绑定模型。
/// </summary>
public sealed class DataPackTargetOption(DataPackInstallTarget target)
{
    public DataPackInstallTarget Target { get; } = target ?? throw new ArgumentNullException(nameof(target));

    public string Title => Target.Title;

    /// <summary>实际落盘目录：<c>…/datapacks</c>。</summary>
    public string Directory => Target.DataPackDirectory;

    public bool IsGlobal => Target.IsGlobal;

    /// <summary>列表里的范围徽标：全局 / 存档。</summary>
    public string ScopeLabel => Target.IsGlobal ? "全局" : "存档";

    public override string ToString() => Title;
}

/// <summary>
/// 安装确认区里的一行：这次会落盘的文件、角色（前置 / 本体）、体积，执行后追加结果状态。
///
/// 单独一个视图模型是为了让「待安装 → 已安装 / 失败」只影响这一行，
/// 不必因为某个前置失败就整块重绘清单。
/// </summary>
public sealed partial class LibraryPlanFileViewModel : ObservableObject
{
    private readonly ModrinthVersion _version;

    public LibraryPlanFileViewModel(ModrinthVersion version, bool isDependency)
    {
        _version = version ?? throw new ArgumentNullException(nameof(version));
        IsDependency = isDependency;
    }

    /// <summary>true 表示它是被连带装上的前置。</summary>
    public bool IsDependency { get; }

    public string RoleLabel => IsDependency ? "前置" : "本体";

    public string FileName => string.IsNullOrWhiteSpace(_version.FileName)
        ? (string.IsNullOrWhiteSpace(_version.Name) ? _version.Id : _version.Name)
        : _version.FileName;

    public string VersionText => _version.VersionNumber;

    public string SizeText => Conv.FormatSize(_version.FileSize);

    /// <summary>落盘后的绝对路径；未安装或失败时为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePathText))]
    private string? _savedPath;

    public string FilePathText => SavedPath ?? string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private string _statusText = "待安装";

    [ObservableProperty]
    private bool _isDone;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    internal void ApplyResult(ModrinthPlanFile file)
    {
        SavedPath = file.FilePath;
        ErrorText = file.Error;
        IsDone = file.FilePath is not null && string.IsNullOrEmpty(file.Error);
        StatusText = IsDone ? "已安装" : "失败";
    }

    internal void Reset()
    {
        SavedPath = null;
        ErrorText = null;
        IsDone = false;
        StatusText = "待安装";
    }
}

/// <summary>
/// 排序下拉的一项。<see cref="Key"/> 直接进 Modrinth 查询串的 <c>index</c>，
/// 客户端侧由 <c>ModrinthService.NormalizeSortIndex</c> 做白名单校验。
/// </summary>
public sealed class LibrarySortOption(string key, string label)
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public override string ToString() => Label;
}

