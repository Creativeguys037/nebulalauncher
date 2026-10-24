using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Versions;
using NebulaLauncher.Services;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 模组列表的状态筛选档位。对应 PCL2 <c>PageInstanceMod.xaml.vb:427-440 CanPassFilter</c>：
/// 全部 / 已启用 / 已禁用 / 可更新。
///
/// 「可更新」不是独立的数据源，而是从最近一次检查更新的结果派生——
/// 没有检查过时它恒为空，界面据此把这一档置灰。
/// </summary>
public enum ModsFilter
{
    All = 0,
    Enabled = 1,
    Disabled = 2,
    CanUpdate = 3,
}

/// <summary>
/// 模组 / 资源包 / 光影包管理页：左栏「内容目标」+ 右栏「内容网格」。
///
/// 为什么重做：旧实现把扫描根目录写死成 <see cref="ILauncherPaths.MinecraftDirectory"/>，
/// 实例目录里的 mods 永远读不到；而且 Fabric / Forge 的 jar 混在同一份列表里，
/// 用户看不出「这个文件对哪个版本有效」。
///
/// 现在的模型是「内容目标 = 一份独立的游戏目录」：左栏按加载器分组列出
/// <see cref="IInstanceContext.ListTargetsAsync"/> 给出的全部目标（全局 .minecraft + 每个实例），
/// 右栏只扫描当前选中的目标；切换目标或切换分类只重扫右栏，不重建左栏。
///
/// 服务可能缺失（DI 未接线 / 单测 / 设计器预览）：所有注入字段保持 nullable，
/// <see cref="IContentService"/> 缺失时退回示例卡片，<see cref="IInstanceContext"/> 缺失时
/// 只显示一个全局目标。任何组合都必须渲染出可用界面，不能白屏。
/// </summary>
public sealed partial class ModsViewModel : PageViewModelBase
{
    /// <summary>左栏分组顺序：Fabric → NeoForge → Forge → Quilt → 原版。</summary>
    private static readonly LoaderKind[] LoaderOrder =
    [
        LoaderKind.Fabric,
        LoaderKind.NeoForge,
        LoaderKind.Forge,
        LoaderKind.Quilt,
        LoaderKind.Vanilla,
    ];

    /// <summary>
    /// 与 <c>InstanceContext.GlobalTargetKey</c> 相同的字符串。Core 的常量无法在 XAML / 绑定里引用，
    /// 而这里只需要「全局目标的稳定 Key」，所以复写一份；两处不一致不会崩，只会回落到第一个目标。
    /// </summary>
    private const string GlobalTargetKey = "global";

    private readonly IContentService? _content;
    private readonly IInstanceContext? _instanceContext;
    private readonly ILauncherPaths? _paths;
    private readonly IPlatformService? _platform;
    private readonly IModUpdateService? _updates;
    private readonly IModProjectInfoService? _projects;
    private readonly IGameLauncher? _launcher;
    private readonly INavigationService? _navigation;

    private readonly List<ContentCardViewModel> _all = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _updateCts;

    /// <summary>
    /// 在线信息补全（项目名 / 译名 / 图标）的取消源。与 <see cref="_scanCts"/> 分开：
    /// 扫描结束就该开始补全，但用户切目标时要能单独掐掉在飞的补全请求，
    /// 否则旧目标的几十个请求会继续跑完并把结果写进新目标的卡片上（按引用匹配虽然挡住了，
    /// 但白跑的请求与缓存写入没有意义）。
    /// </summary>
    private CancellationTokenSource? _enrichCts;

    /// <summary>
    /// 页内记忆的上次选中目标（实例 Id 或 <c>global</c>）。只活在本页实例里，不写 settings.json：
    /// 模组页的「看哪份目录」是浏览状态，不是应用级选择；应用级选择由 <see cref="IInstanceContext"/> 管。
    /// </summary>
    private string? _lastTargetKey;

    /// <summary>
    /// 页面自建的占位全局目标（服务缺失 / 读取目标列表失败时兜底）。判据用引用相等：
    /// 它的 <see cref="ContentTarget.Loader"/> 只是字段默认值，不是「原版」的证据。
    /// </summary>
    private ContentTarget? _placeholderTarget;

    /// <summary>
    /// 目标列表读取失败的原因。单独存一份是因为扫描右栏会重置 <see cref="ErrorText"/>，
    /// 不区分来源的话「读不到实例列表，已退回全局目录」这条提示会被随后的扫描清掉。
    /// </summary>
    private string? _targetLoadError;

    public ModsViewModel(IServiceProvider services)
    {
        _content = services.GetService(typeof(IContentService)) as IContentService;
        _instanceContext = services.GetService(typeof(IInstanceContext)) as IInstanceContext;
        _paths = services.GetService(typeof(ILauncherPaths)) as ILauncherPaths;
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;
        _updates = services.GetService(typeof(IModUpdateService)) as IModUpdateService;
        _projects = services.GetService(typeof(IModProjectInfoService)) as IModProjectInfoService;
        _launcher = services.GetService(typeof(IGameLauncher)) as IGameLauncher;
        _navigation = services.GetService(typeof(INavigationService)) as INavigationService;

        // 组合根还没调用 AddCoreModUpdates 时自建一个：更新检查是页面主功能之一，
        // 不该因为少一行 DI 注册就整块消失。依赖仍从容器取，拿不到时服务内部自己降级。
        _updates ??= BuildFallbackUpdateService(services);

        // 在线信息补全同样兜底：拿不到服务只是「卡片上没有中文名 / 描述」，不能白屏。
        _projects ??= BuildFallbackProjectInfoService(services);

        // 组合根尚未接线时，页面依然要能开发与验收：用示例数据渲染完整布局，并在页面上标注「示例数据」。
        IsDemoMode = _content is null;

        // 首帧就摆出全局目标。OnNavigatedToAsync 还没跑（设计器预览、单测）时左栏不能是空的；
        // rememberSelection=false 保证「默认选中当前实例」的选择逻辑不被这个占位选择覆盖。
        RebuildTargetGroups([BuildGlobalTarget()], GlobalTargetKey, rememberSelection: false);
    }

    /// <summary>
    /// <see cref="IModUpdateService"/> 未注册时的兜底构造。只在两个在线服务至少有一个可用时才建，
    /// 否则保持 null，让按钮显示为禁用而不是点了才报错。
    /// </summary>
    private ModUpdateService? BuildFallbackUpdateService(IServiceProvider services)
    {
        var modrinth = services.GetService(typeof(IModrinthService)) as IModrinthService;
        var curseForge = services.GetService(typeof(ICurseForgeService)) as ICurseForgeService;

        if (modrinth is null && curseForge is null)
        {
            return null;
        }

        var log = services.GetService(typeof(IAppLog)) as IAppLog;
        return new ModUpdateService(modrinth, curseForge, _paths, log);
    }

    /// <summary>
    /// <see cref="IModProjectInfoService"/> 未注册时的兜底构造。两个在线服务都没有时返回 null：
    /// 页面照常渲染卡片，只是标题停在文件名上——比每次都发一轮注定失败的请求好。
    /// </summary>
    private ModProjectInfoService? BuildFallbackProjectInfoService(IServiceProvider services)
    {
        var modrinth = services.GetService(typeof(IModrinthService)) as IModrinthService;
        var curseForge = services.GetService(typeof(ICurseForgeService)) as ICurseForgeService;

        if (modrinth is null && curseForge is null)
        {
            return null;
        }

        var log = services.GetService(typeof(IAppLog)) as IAppLog;
        return new ModProjectInfoService(modrinth, curseForge, _paths, log);
    }

    public override string Title => "模组";

    public override string Key => NavigationKeys.Mods;

    private string? _subtitle;

    /// <summary>
    /// 抬头副标题随数据变化。基类只有 getter，所以这里用私有 setter 方法 + 显式通知，
    /// 而不是 override 一个带 setter 的属性（C# 不允许给只读属性补 set）。
    /// </summary>
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

    /// <summary>当前内容分类。切换只重扫右栏，不重建左侧目标列表。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModTabActive))]
    [NotifyPropertyChangedFor(nameof(IsResourcePackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsShaderPackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsDataPackTabActive))]
    [NotifyPropertyChangedFor(nameof(IsDataPackEmpty))]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    private ContentKind _activeKind = ContentKind.Mod;

    public bool IsModTabActive => ActiveKind == ContentKind.Mod;

    public bool IsResourcePackTabActive => ActiveKind == ContentKind.ResourcePack;

    public bool IsShaderPackTabActive => ActiveKind == ContentKind.ShaderPack;

    public bool IsDataPackTabActive => ActiveKind == ContentKind.DataPack;

    /// <summary>数据包分类为空时给「去资源页找数据包」的出口；其它分类的空态不显示这个按钮。</summary>
    public bool IsDataPackEmpty => IsEmpty && ActiveKind == ContentKind.DataPack;

    public string KindLabel => KindLabelOf(ActiveKind);

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDemoMode;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDataPackEmpty))]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private string _toggleAllLabel = "全部禁用";

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private bool _isWarningListVisible;

    [ObservableProperty]
    private ContentCardViewModel? _pendingDelete;

    /// <summary>更新检查结果面板是否可见。检查过一次之后即使 0 条匹配也保留面板，用来说明原因。</summary>
    [ObservableProperty]
    private bool _hasUpdates;

    [ObservableProperty]
    private bool _isUpdateListVisible = true;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateSummary = string.Empty;

    /// <summary>更新检查的整体失败原因（中文）。逐条原因在 <see cref="ModUpdateRowViewModel.ErrorText"/>。</summary>
    [ObservableProperty]
    private string? _updateError;

    /// <summary>
    /// 游戏是否正在运行。运行期间禁用更新检查：此时 jar 可能被 JVM 持有，
    /// 读取哈希会失败，而且用户真正要做的是先关游戏。
    /// </summary>
    [ObservableProperty]
    private bool _isGameRunning;

    /// <summary>当前选中的内容目标。为 null 只在「目标列表为空」时出现，右栏会显示空状态而不是崩溃。</summary>
    [ObservableProperty]
    private TargetItemViewModel? _selectedTarget;

    // ==================== 多选与批量操作 ====================

    /// <summary>
    /// 被勾选参与批量操作的卡片。用 HashSet 而不是 ObservableCollection：
    /// 「全选 / 清除选择 / 扫描后清空」都是整批改，去重与 O(1) 增删比通知集合变化更重要；
    /// 界面只绑 <see cref="SelectedCount"/> 这个数字。
    /// </summary>
    private readonly HashSet<ContentCardViewModel> _selected = [];

    /// <summary>整批置位时抑制逐项回调，避免 700 个卡片触发 700 次属性通知。</summary>
    private bool _suppressSelectionSync;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>批量操作进行中：文件改名放在后台线程，期间禁用全部批量按钮。</summary>
    [ObservableProperty]
    private bool _isBatchBusy;

    /// <summary>批量删除的二次确认面板是否展开。与单个删除的 <see cref="PendingDelete"/> 互不影响。</summary>
    [ObservableProperty]
    private bool _isBatchDeleteConfirmOpen;

    /// <summary>批量操作汇总行，例如「批量禁用完成 · 成功 5 / 失败 1」。</summary>
    [ObservableProperty]
    private string? _batchResultText;

    /// <summary>失败项明细。与 <see cref="BatchResultText"/> 分开，是因为失败项可能多到需要单独换行。</summary>
    [ObservableProperty]
    private string? _batchFailureText;

    /// <summary>批量删除确认面板里的文件清单摘要。</summary>
    [ObservableProperty]
    private string _batchDeleteListText = string.Empty;

    /// <summary>
    /// 批量删除是否绕过回收站。
    ///
    /// PCL2 用「按住 Shift 点删除」表达这个语义（<c>PageInstanceMod.xaml.vb:669</c> 的
    /// <c>IsShiftPressed</c>）。这里改成确认面板里一个显式开关：截图模式点不出键盘状态，
    /// 隐藏的 Shift 快捷键等于测试不到的功能。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchDeleteModeText))]
    private bool _batchDeletePermanent;

    public bool HasSelection => SelectedCount > 0;

    /// <summary>「已选 N 项」。批量条的主文案，没有它用户不知道按钮作用在什么范围。</summary>
    public string SelectionSummary => $"已选 {SelectedCount} 项";

    /// <summary>批量按钮可用判据：有选中项且没有正在跑的批量任务。</summary>
    public bool CanBatch => HasSelection && !IsBatchBusy;

    public bool HasBatchResult => !string.IsNullOrWhiteSpace(BatchResultText);

    public bool HasBatchFailures => !string.IsNullOrWhiteSpace(BatchFailureText);

    /// <summary>批量删除的确认文案。数字必须在点开确认的那一刻就是对的。</summary>
    public string BatchDeleteSummary => $"将删除选中的 {SelectedCount} 个文件。";

    /// <summary>删除模式的说明。默认进回收站，勾选后才彻底删除。</summary>
    public string BatchDeleteModeText => BatchDeletePermanent
        ? "彻底删除，不进回收站，删除后无法恢复。"
        : "移入回收站，可在本页「回收站」里找回。";

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanBatch));
        OnPropertyChanged(nameof(BatchDeleteSummary));
        NotifyUpdateSelectionChanged();
    }

    partial void OnIsBatchBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBatch));
        NotifyUpdateSelectionChanged();
    }

    partial void OnBatchResultTextChanged(string? value) => OnPropertyChanged(nameof(HasBatchResult));

    partial void OnBatchFailureTextChanged(string? value) => OnPropertyChanged(nameof(HasBatchFailures));

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasPendingDelete => PendingDelete is not null;

    public bool HasTargets => GlobalTarget is not null || TargetGroups.Count > 0;

    public bool HasSelectedTarget => SelectedTarget is not null;

    public string SelectedTargetName => SelectedTarget?.DisplayName ?? "未选择目标";

    /// <summary>
    /// 当前目标能不能加载模组。
    ///
    /// 原版版本没有 mod API（Fabric / Quilt / Forge / NeoForge），<c>mods/</c> 里的 jar 一个都不会被加载。
    /// 这是「版本级」的事实，与当前停在哪个内容分类无关。
    ///
    /// **全局目录是例外**：它不属于任何版本，而是所有未隔离版本共用的 <c>.minecraft/mods</c>。
    /// 按加载器把它判成「原版」会直接收起模组入口——用户就再也看不到、也装不了全局模组了。
    ///
    /// 目标尚未解析出来（设计器预览、单测、目标列表读取失败）时按「可以」处理：
    /// 那时没有任何可信的加载器信息，不该让模组入口凭空消失。
    /// </summary>
    public bool CanLoadMods => SelectedTarget is null
                               || IsGlobalTarget(SelectedTarget.Target)
                               || ReferenceEquals(SelectedTarget.Target, _placeholderTarget)
                               || NormalizeLoader(SelectedTarget.Target.Loader) != LoaderKind.Vanilla;

    /// <summary>判据同时看 Key 与 Kind：<see cref="ContentTargetKind.Global"/> 是枚举默认值，
    /// 单测或旧数据里忘记设 Kind 的目标不应被当成全局目录。</summary>
    private static bool IsGlobalTarget(ContentTarget target) =>
        target.Kind == ContentTargetKind.Global
        || string.Equals(target.Key, GlobalTargetKey, StringComparison.Ordinal);

    /// <summary>当前目标的绝对目录，显示在工具行下方，回答「文件到底在哪」。</summary>
    public string TargetDirectory => SelectedTarget?.GameDirectory ?? string.Empty;

    /// <summary>
    /// 左栏顶部的全局目录。它不参与加载器分组，也不绑定任何版本：
    /// 「全局」是 <c>.minecraft</c> 根目录本身，不是「某个版本的原版」。
    /// </summary>
    public TargetItemViewModel? GlobalTarget { get; private set; }

    public bool HasGlobalTarget => GlobalTarget is not null;

    /// <summary>左栏：按加载器分组的实例目标（不含全局目录）。</summary>
    public ObservableCollection<ModsTargetGroup> TargetGroups { get; } = [];

    public ObservableCollection<ContentCardViewModel> Items { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>最近一次更新检查的逐条结果。</summary>
    public ObservableCollection<ModUpdateRowViewModel> Updates { get; } = [];

    public bool HasUpdateError => !string.IsNullOrWhiteSpace(UpdateError);

    /// <summary>更新检查是否可用。游戏运行中或服务未接线时置灰。</summary>
    public bool CanCheckUpdates => _updates is not null && !IsGameRunning && CanLoadMods;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// 扫描结束时补算一次空态。
    ///
    /// <see cref="ApplyFilter"/> 在 <c>IsLoading</c> 还是 true 的时候调用 <see cref="UpdateEmptyState"/>，
    /// 那里把「目录为空」判成「正在加载」，于是首次进入空目录时右侧什么都不显示（连空态卡片都没有）。
    /// 这里在加载结束后重算一次，空态标题与「去资源页找数据包」按钮才有机会出现。
    /// </summary>
    partial void OnIsLoadingChanged(bool value)
    {
        if (!value)
        {
            UpdateEmptyState(SearchText.Trim());
        }
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnUpdateErrorChanged(string? value) => OnPropertyChanged(nameof(HasUpdateError));

    partial void OnIsGameRunningChanged(bool value)
    {
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        NotifyUpdateSelectionChanged();
    }

    partial void OnPendingDeleteChanged(ContentCardViewModel? value) => OnPropertyChanged(nameof(HasPendingDelete));

    partial void OnSelectedTargetChanged(TargetItemViewModel? value)
    {
        // 只通知展示用派生属性；扫描由命令显式触发，避免「赋值即扫描」造成重复请求。
        OnPropertyChanged(nameof(SelectedTargetName));
        OnPropertyChanged(nameof(TargetDirectory));
        OnPropertyChanged(nameof(HasSelectedTarget));
        OnPropertyChanged(nameof(ActiveMinecraftVersion));
        OnPropertyChanged(nameof(GameVersionBadgeText));

        // 「这个版本能不能装模组」随目标变：模组入口的收起 / 展开与检查更新的可用性都挂在它上面。
        // 少了这两条，切到原版后界面还留着上一版本的状态。
        OnPropertyChanged(nameof(CanLoadMods));
        CheckUpdatesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// pack_format 兼容判断用的 Minecraft 版本。优先取目标解析结果（全局目标与实例目标都会带原版版本号），
    /// 拿不到再退回实例上下文；仍为 null 时不做判断，界面显示「未知」。
    /// </summary>
    public string? ActiveMinecraftVersion =>
        SelectedTarget?.Target.MinecraftVersion ?? _instanceContext?.MinecraftVersion;

    /// <summary>目标条上的游戏版本徽标。取不到时明确写「未知」，而不是留空让用户以为是兼容的。</summary>
    public string GameVersionBadgeText => string.IsNullOrWhiteSpace(ActiveMinecraftVersion)
        ? "MC 未知"
        : $"MC {ActiveMinecraftVersion}";

    public override async Task OnNavigatedToAsync()
    {
        ApplyScreenshotKind();

        // 进入页面时刷新一次游戏状态：用户可能刚在首页把游戏启动起来。
        RefreshGameRunningState();
        await ReloadTargetsAsync();
        await ScanCurrentTargetAsync();
        ApplyScreenshotUpdates();
        await RefreshTrashAsync();
    }

    /// <summary>
    /// 截图模式的 <c>-Section updates</c>：给更新面板塞一条示例行。
    ///
    /// 更新面板只在「检查过更新」之后才出现，而截图模式没有点击能力，
    /// 不预置的话「更新选中项」按钮与「可更新」筛选档永远截不到、等于无法验收。
    /// 与 <see cref="BuildSampleCards"/> 同一性质：只在截图模式下生效，正常运行时分支不进入。
    /// </summary>
    private void ApplyScreenshotUpdates()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "updates", StringComparison.OrdinalIgnoreCase)
            || _all.Count == 0)
        {
            return;
        }

        var card = _all.FirstOrDefault(item => item.Item.Kind == ContentKind.Mod) ?? _all[0];

        HasUpdates = true;
        IsUpdateListVisible = true;
        UpdateSummary = "共 7 个 · 1 个有新版本（示例）";

        Updates.Add(new ModUpdateRowViewModel(new ModUpdateInfo
        {
            Item = card.Item,
            Matched = true,
            Source = ContentSourceKind.Modrinth,
            ProjectId = "AANobbMI",
            CurrentVersion = "0.5.8",
            LatestVersion = "0.6.0",
            DownloadUrl = "https://example.invalid/sodium-0.6.0.jar",
            FileName = "sodium-fabric-0.6.0.jar",
            FileSize = 1048576,
            HasUpdate = true,
            Status = ModUpdateService.StatusUpdateAvailable,
        }));

        NotifyUpdateSelectionChanged();
    }

    /// <summary>
    /// 截图模式没有点击能力，用 <c>--section</c> 指定进入页面时停在哪一个内容分类
    /// （与设置页的 <c>--section</c> 同一套约定，解析在 <see cref="ScreenshotMode"/>）。
    ///
    /// 只在截图模式下生效：正常运行既不读命令行，也不会覆盖用户点选的分类。
    /// 必须在 <see cref="ScanCurrentTargetAsync"/> 之前调用，否则扫的还是默认分类。
    /// </summary>
    private void ApplyScreenshotKind()
    {
        if (!ScreenshotMode.IsActive || string.IsNullOrWhiteSpace(ScreenshotMode.SectionKey))
        {
            return;
        }

        ActiveKind = ScreenshotMode.SectionKey switch
        {
            "datapacks" => ContentKind.DataPack,
            "resourcepacks" => ContentKind.ResourcePack,
            "shaderpacks" => ContentKind.ShaderPack,
            "mods" => ContentKind.Mod,
            _ => ActiveKind,
        };
    }

    public override Task OnNavigatedFromAsync()
    {
        _scanCts?.Cancel();
        _updateCts?.Cancel();
        _enrichCts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>数据包空态里的出口：去资源页（在线）找数据包。</summary>
    [RelayCommand]
    private void OpenLibraryForDataPacks() => _navigation?.Navigate(NavigationKeys.Library);

    [RelayCommand]
    private async Task SelectTargetAsync(TargetItemViewModel? item)
    {
        if (item is null || string.Equals(item.Target.Key, SelectedTarget?.Target.Key, StringComparison.Ordinal))
        {
            return;
        }

        SetSelectedTarget(item);
        await ScanCurrentTargetAsync();
    }

    /// <summary>切换分类：只重扫右栏，左侧目标列表保持不变。</summary>
    [RelayCommand]
    private async Task SelectKindAsync(ContentKind kind)
    {
        if (ActiveKind == kind)
        {
            return;
        }

        ActiveKind = kind;
        await ScanCurrentTargetAsync();
    }

    /// <summary>刷新会重新读实例列表：新建 / 删除实例后不刷新就看不到新的目标。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await ReloadTargetsAsync();
        await ScanCurrentTargetAsync();
    }

    [RelayCommand]
    private async Task ToggleAllAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var enable = Items.Any(card => !card.IsEnabled);

        try
        {
            if (_content is not null)
            {
                foreach (var card in Items.ToList())
                {
                    await _content.SetEnabledAsync(card.Item, enable);
                    card.SetEnabledSilently(enable);
                }
            }
            else
            {
                foreach (var card in Items)
                {
                    card.SetEnabledSilently(enable);
                }
            }

            UpdateSubtitle();
            RefreshToggleAllLabel();
        }
        catch (Exception ex)
        {
            ErrorText = $"批量切换失败：{ex.Message}";
        }
    }

    // ==================== 多选与批量操作 ====================

    /// <summary>卡片勾选框变化后回调。整批置位期间被抑制，由调用方统一刷新计数。</summary>
    internal void NotifyCardChecked(ContentCardViewModel card, bool isChecked)
    {
        if (_suppressSelectionSync)
        {
            return;
        }

        if (isChecked)
        {
            _selected.Add(card);
        }
        else
        {
            _selected.Remove(card);
        }

        SelectedCount = _selected.Count;
    }

    /// <summary>全选「当前过滤后可见的项」。搜索时全选不会连带选中看不见的文件。</summary>
    [RelayCommand]
    private void SelectAll()
    {
        var visible = Items.ToList();

        _suppressSelectionSync = true;

        try
        {
            foreach (var card in visible)
            {
                card.IsChecked = true;
            }

            _selected.Clear();
            _selected.UnionWith(visible);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        SelectedCount = _selected.Count;
    }

    [RelayCommand]
    private void ClearSelection() => ClearSelectionCore();

    private void ClearSelectionCore()
    {
        if (_selected.Count > 0)
        {
            // 先复制再改：遍历过程中卡片回调会修改同一个集合。
            foreach (var card in _selected.ToList())
            {
                _suppressSelectionSync = true;

                try
                {
                    card.IsChecked = false;
                }
                finally
                {
                    _suppressSelectionSync = false;
                }
            }

            _selected.Clear();
        }

        SelectedCount = 0;
    }

    /// <summary>
    /// 重扫后剔除已不在列表里的选中项。留着已销毁的卡片会让「已选 N 项」与实际能操作的项对不上。
    /// </summary>
    private void PruneSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var visible = new HashSet<ContentCardViewModel>(Items);

        _suppressSelectionSync = true;

        try
        {
            foreach (var card in _selected.Where(card => !visible.Contains(card)).ToList())
            {
                _selected.Remove(card);
                card.IsChecked = false;
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        SelectedCount = _selected.Count;
    }

    [RelayCommand]
    private async Task EnableSelectedAsync() => await RunBatchToggleAsync(enabled: true);

    [RelayCommand]
    private async Task DisableSelectedAsync() => await RunBatchToggleAsync(enabled: false);

    private async Task RunBatchToggleAsync(bool enabled)
    {
        if (IsBatchBusy || _selected.Count == 0)
        {
            return;
        }

        // 快照：批量期间用户仍可能改勾选，操作对象不能跟着变。
        var cards = _selected.ToList();

        IsBatchBusy = true;
        IsBatchDeleteConfirmOpen = false;
        ClearBatchResult();

        try
        {
            if (_content is null)
            {
                // 示例数据模式：没有服务可调，只同步界面状态，避免用户以为已经改到磁盘上了。
                foreach (var card in cards)
                {
                    card.SetEnabledSilently(enabled);
                }

                SetBatchResult($"演示数据：界面已切换 {cards.Count} 项（未落盘）", null);
            }
            else
            {
                var result = ActiveKind == ContentKind.DataPack
                    ? await RunDataPackBatchToggleAsync(cards, enabled).ConfigureAwait(true)
                    : await ContentBatch.SetEnabledAsync(
                        _content,
                        cards.Select(card => card.Item).ToList(),
                        enabled).ConfigureAwait(true);

                foreach (var card in cards)
                {
                    // 以磁盘为准：ContentService.SetEnabledAsync 把 IOException 吞成日志后正常返回，
                    // 只看返回值会把「文件被游戏占用、改名失败」显示成成功。
                    // 复核用 ContentService 的版本：它同时认文件与目录，数据包的目录形态不会被误判。
                    var onDisk = ContentService.IsEnabledOnDisk(card.Item);

                    if (onDisk is not null)
                    {
                        card.SetEnabledSilently(onDisk.Value);
                    }
                }

                SetBatchResult($"批量{(enabled ? "启用" : "禁用")}完成 · {result.Summary}", result.FailedFiles);
            }

            UpdateSubtitle();
            RefreshToggleAllLabel();
        }
        catch (Exception ex)
        {
            ErrorText = $"批量切换失败：{ex.Message}";
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    /// <summary>
    /// 数据包的批量启停。
    ///
    /// 为什么不复用 <see cref="ContentBatch.SetEnabledAsync"/>：它的成功判据写死 <c>File.Exists</c>，
    /// 目录形态的数据包改名后会一律被计入失败（实际已经成功）。这里沿用同一套
    /// 「逐项容错 + 磁盘复核」，复核改用 <see cref="ContentService.IsEnabledOnDisk"/>（同时认文件与目录）。
    /// </summary>
    private async Task<ContentBatchResult> RunDataPackBatchToggleAsync(
        IReadOnlyList<ContentCardViewModel> cards,
        bool enabled)
    {
        var operation = enabled ? ContentBatchOperation.Enable : ContentBatchOperation.Disable;

        var content = _content;
        if (content is null)
        {
            return ContentBatchResult.Empty(operation);
        }

        // 与 ContentBatch 同样的理由：SetEnabledAsync 是同步实现，700 个条目会把 UI 线程占满。
        return await Task.Run(
            async () =>
            {
                var failed = new List<string>();
                var succeeded = 0;

                foreach (var card in cards)
                {
                    try
                    {
                        await content.SetEnabledAsync(card.Item, enabled).ConfigureAwait(false);

                        if (ContentService.IsEnabledOnDisk(card.Item) == enabled)
                        {
                            succeeded++;
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        // 逐项容错：一个包动不了，其余项继续；失败项进汇总。
                    }

                    failed.Add(card.Item.FileName);
                }

                return new ContentBatchResult(operation, succeeded, failed.Count, failed);
            }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void RequestBatchDelete()
    {
        if (IsBatchBusy || _selected.Count == 0)
        {
            return;
        }

        BatchDeleteListText = ComposeFileList(_selected.Select(card => card.Item));
        BatchDeletePermanent = false;
        IsBatchDeleteConfirmOpen = true;
        ErrorText = null;
    }

    [RelayCommand]
    private void CancelBatchDelete() => IsBatchDeleteConfirmOpen = false;

    [RelayCommand]
    private async Task ConfirmBatchDeleteAsync()
    {
        if (IsBatchBusy)
        {
            return;
        }

        var cards = _selected.ToList();
        IsBatchDeleteConfirmOpen = false;

        if (cards.Count == 0)
        {
            return;
        }

        IsBatchBusy = true;
        ClearBatchResult();

        try
        {
            if (_content is null)
            {
                RemoveCards(cards);
                SetBatchResult($"演示数据：界面已移除 {cards.Count} 项（未删盘）", null);
            }
            else
            {
                var result = await ContentBatch.DeleteAsync(
                    _content,
                    cards.Select(card => card.Item).ToList(),
                    BatchDeletePermanent).ConfigureAwait(true);

                // 删除成功的判据同样是磁盘：两个候选路径都不在了才算删掉。
                var removed = cards.Where(card => ContentBatch.IsEnabledOnDisk(card.Item) is null).ToList();
                RemoveCards(removed);

                SetBatchResult(
                    $"批量删除完成 · {result.Summary}{(BatchDeletePermanent ? "（已彻底删除）" : "（已移入回收站）")}",
                    result.FailedFiles);

                await RefreshTrashAsync().ConfigureAwait(true);
            }

            ApplyFilter();
            UpdateSubtitle();
        }
        catch (Exception ex)
        {
            ErrorText = $"批量删除失败：{ex.Message}";
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private void RemoveCards(IReadOnlyList<ContentCardViewModel> cards)
    {
        _suppressSelectionSync = true;

        try
        {
            foreach (var card in cards)
            {
                _all.Remove(card);
                _selected.Remove(card);
                card.IsChecked = false;
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        SelectedCount = _selected.Count;
    }

    private void SetBatchResult(string text, IReadOnlyList<string>? failures)
    {
        BatchResultText = text;

        if (failures is not { Count: > 0 })
        {
            BatchFailureText = null;
            return;
        }

        // 失败项最多列 5 个：被占用的通常是「游戏正在跑」，一次几十个失败时列全反而看不清重点。
        var head = string.Join("、", failures.Take(5));
        BatchFailureText = failures.Count > 5
            ? $"失败（前 5 个）：{head} 等共 {failures.Count} 个"
            : $"失败：{head}";
    }

    private void ClearBatchResult()
    {
        BatchResultText = null;
        BatchFailureText = null;
    }

    private static string ComposeFileList(IEnumerable<ContentItem> items)
    {
        var names = items
            .Select(item => string.IsNullOrWhiteSpace(item.FileName) ? item.FilePath : item.FileName)
            .ToList();

        if (names.Count == 0)
        {
            return string.Empty;
        }

        var head = string.Join("、", names.Take(3));
        return names.Count > 3 ? $"{head} 等 {names.Count} 个文件" : head;
    }

    [RelayCommand]
    private void CheckConflicts()
    {
        ClearWarnings();
        IsWarningListVisible = true;

        if (Items.Count == 0)
        {
            return;
        }

        try
        {
            var mods = _all.Where(card => card.Item.Kind == ContentKind.Mod).Select(card => card.Item).ToList();

            if (_content is not null && mods.Count > 0)
            {
                foreach (var conflict in _content.DetectConflicts(mods))
                {
                    Warnings.Add($"模组 {conflict.ModId} 存在重复：{conflict.Detail}（{string.Join("、", conflict.Files)}）");
                }

                // 缺前置只提示、不阻断启动：Forge 生态里 modId 与 jar 内的别名历史上不一致
                // （例如 cfm 与 MrCrayfishFurnitureMod），硬拦会把能跑的整合包误杀。
                foreach (var issue in _content.DetectDependencyIssues(mods))
                {
                    var range = string.IsNullOrWhiteSpace(issue.RequiredRange)
                        ? string.Empty
                        : $"（要求 {issue.RequiredRange.Trim()}）";
                    Warnings.Add(issue.Optional
                        ? $"可选前置 {issue.MissingId} 未安装：{string.Join("、", issue.Files)}"
                        : $"缺少前置 {issue.MissingId}{range}：{string.Join("、", issue.Files)} 依赖它");
                }

                // 兼容性判断的依据是「当前目标绑定的 Minecraft 版本」，不是设置页里上次选的版本：
                // 同一个 jar 对 1.20.1 的实例兼容、对 1.21.4 的实例就不兼容。
                var minecraftVersion = SelectedTarget?.Target.MinecraftVersion;

                if (string.IsNullOrWhiteSpace(minecraftVersion))
                {
                    Warnings.Add("当前目标没有解析出 Minecraft 版本，已跳过版本兼容性检查。");
                }
                else
                {
                    foreach (var item in _content.FindVersionMismatch(mods, minecraftVersion))
                    {
                        Warnings.Add(
                            $"{DisplayNameOf(item)} 声明兼容 {item.McVersionRange?.Trim() ?? "未知"}，与当前版本 {minecraftVersion} 不匹配");
                    }
                }
            }

            if (Warnings.Count == 0)
            {
                Warnings.Add("未发现模组冲突、缺前置或版本不匹配。");
            }

            HasWarnings = true;
        }
        catch (Exception ex)
        {
            ErrorText = $"冲突检测失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleWarningList() => IsWarningListVisible = !IsWarningListVisible;

    [RelayCommand]
    private void CloseWarnings()
    {
        ClearWarnings();
        IsWarningListVisible = false;
    }

    // ---------------- 检查更新 ----------------

    /// <summary>
    /// 检查更新：用文件哈希反查在线数据源，判断每个模组有没有新版本。
    ///
    /// 为什么不在这里做「下载并替换」：替换正在被加载的 jar 属于安装流程，
    /// 这一版只回答「有没有新版、是哪个项目的」，把下载留给下载页，避免两处实现覆盖策略不一致。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesAsync()
    {
        var update = _updates;

        if (update is null)
        {
            UpdateError = "更新检查服务尚未就绪，请稍后再试。";
            HasUpdates = true;
            return;
        }

        // 每次点击前重读一次：游戏可能是在本页停留期间被启动的。
        RefreshGameRunningState();

        if (IsGameRunning)
        {
            UpdateError = "游戏正在运行，请先关闭游戏再检查更新。";
            HasUpdates = true;
            return;
        }

        var target = SelectedTarget;

        if (target is null)
        {
            UpdateError = "没有可用的游戏目录，无法检查更新。";
            HasUpdates = true;
            return;
        }

        // 用 _all 而不是 Items：搜索框只是过滤显示，检查的对象应当是当前目标下的全部模组。
        var mods = _all.Where(card => card.Item.Kind == ContentKind.Mod).Select(card => card.Item).ToList();

        if (mods.Count == 0)
        {
            ClearUpdates();
            HasUpdates = true;
            IsUpdateListVisible = true;
            UpdateError = $"「{KindLabel}」分类下没有可检查的内容，请先切换到模组。";
            return;
        }

        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;

        ClearUpdates();
        HasUpdates = true;
        IsUpdateListVisible = true;
        IsCheckingUpdates = true;

        try
        {
            var report = await update.CheckAsync(
                mods,
                target.GameDirectory,
                target.Target.MinecraftVersion,
                target.Target.Loader,
                progress: null,
                token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            foreach (var info in report.Items)
            {
                Updates.Add(new ModUpdateRowViewModel(info));
            }

            UpdateSummary = ComposeUpdateSummary(report);
            UpdateError = report.Error;
            NotifyUpdateSelectionChanged();
        }
        catch (OperationCanceledException)
        {
            // 离开页面或重新点击：旧结果直接丢弃，不写错误。
        }
        catch (Exception ex)
        {
            UpdateError = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsCheckingUpdates = false;
            }
        }
    }

    [RelayCommand]
    private void ToggleUpdateList() => IsUpdateListVisible = !IsUpdateListVisible;

    [RelayCommand]
    private void CloseUpdates() => ClearUpdates();

    // ==================== 一键更新选中项 ====================

    /// <summary>更新进行中：下载 + 替换文件，期间禁用批量按钮与再次更新。</summary>
    [ObservableProperty]
    private bool _isUpdating;

    /// <summary>更新前的二次确认面板（PCL2 <c>UpdateMods</c> 的「可能崩存档」警告）。</summary>
    [ObservableProperty]
    private bool _isUpdateConfirmOpen;

    [ObservableProperty]
    private string _updateConfirmText = string.Empty;

    /// <summary>被勾选且已确认有新版本的条目数。</summary>
    public int UpdatableSelectedCount => SelectedUpdatableRows().Count;

    /// <summary>
    /// 「更新选中项」是否可点。
    ///
    /// 只作用在**已勾选**的条目上（PCL2 <c>BtnSelectUpdate_Click:528-531</c> 的
    /// <c>SelectedMods.Contains(...) AndAlso m.HasUpdate</c>），而不是整个列表：
    /// 一键全量更新在整合包里等于重装，用户不可能想这样。
    /// </summary>
    public bool CanUpdateSelected => UpdatableSelectedCount > 0 && !IsBatchBusy && !IsUpdating && !IsGameRunning;

    public string UpdateSelectedHint => UpdatableSelectedCount == 0
        ? "勾选带「有新版本」标记的条目后可一键更新"
        : $"将更新选中的 {UpdatableSelectedCount} 个模组";

    /// <summary>按钮文案带上条数：不带的话用户不知道这个按钮会动几个文件。</summary>
    public string UpdateButtonText => UpdatableSelectedCount == 0
        ? "更新选中项"
        : $"更新选中项 · {UpdatableSelectedCount}";

    partial void OnIsUpdatingChanged(bool value) => NotifyUpdateSelectionChanged();

    private List<ModUpdateRowViewModel> SelectedUpdatableRows()
    {
        if (_selected.Count == 0 || Updates.Count == 0)
        {
            return [];
        }

        var selected = new HashSet<ContentItem>(_selected.Select(card => card.Item), ReferenceEqualityComparer.Instance);

        return Updates
            .Where(row => row.HasUpdate && selected.Contains(row.Info.Item))
            .ToList();
    }

    private void NotifyUpdateSelectionChanged()
    {
        OnPropertyChanged(nameof(HasKnownUpdates));
        OnPropertyChanged(nameof(UpdatableSelectedCount));
        OnPropertyChanged(nameof(CanUpdateSelected));
        OnPropertyChanged(nameof(UpdateSelectedHint));
        OnPropertyChanged(nameof(UpdateButtonText));
        RequestUpdateSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUpdateSelected))]
    private void RequestUpdateSelected()
    {
        var count = UpdatableSelectedCount;

        if (count == 0)
        {
            return;
        }

        UpdateConfirmText = count == 1
            ? "将下载并替换选中的 1 个模组。旧文件会移入回收站，可在本页找回。"
            : $"将下载并替换选中的 {count} 个模组。旧文件会移入回收站，可在本页找回。";

        IsUpdateConfirmOpen = true;
        ErrorText = null;
    }

    [RelayCommand]
    private void CancelUpdateSelected() => IsUpdateConfirmOpen = false;

    [RelayCommand]
    private async Task ConfirmUpdateSelectedAsync()
    {
        IsUpdateConfirmOpen = false;

        if (IsUpdating)
        {
            return;
        }

        var rows = SelectedUpdatableRows();

        if (rows.Count == 0)
        {
            return;
        }

        var service = _updates;

        if (service is null)
        {
            ErrorText = "更新服务尚未就绪，无法执行更新。";
            return;
        }

        IsUpdating = true;
        ClearBatchResult();

        try
        {
            var result = await service
                .UpdateAsync(rows.Select(row => row.Info).ToList(), TargetDirectory)
                .ConfigureAwait(true);

            // 磁盘已经变了：卡片上的版本、启用态、冲突结论全部过期，必须重扫。
            await ScanCurrentTargetAsync().ConfigureAwait(true);

            SetBatchResult($"模组更新完成 · {result.Summary}", result.FailedFiles);

            if (result.Updated > 0)
            {
                // 旧文件进了回收站：面板要立刻能看到它们，否则「更新崩了想换回来」无处可点。
                await RefreshTrashAsync().ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(result.Error) && result.Updated == 0)
            {
                UpdateError = result.Error;
                HasUpdates = true;
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"更新模组失败：{ex.Message}";
        }
        finally
        {
            IsUpdating = false;
            NotifyUpdateSelectionChanged();
        }
    }

    // ==================== 内容回收站 ====================

    /// <summary>回收站条目，最近删除的在前。</summary>
    public ObservableCollection<ContentTrashRow> TrashEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrashButtonText))]
    private bool _hasTrashEntries;

    [ObservableProperty]
    private bool _isTrashOpen;

    [ObservableProperty]
    private bool _isTrashBusy;

    [ObservableProperty]
    private string _trashSummary = string.Empty;

    /// <summary>最近一次回收站操作的失败原因。</summary>
    [ObservableProperty]
    private string? _trashError;

    public bool HasTrashError => !string.IsNullOrWhiteSpace(TrashError);

    /// <summary>用户手动收起过回收站：之后刷新不再自动展开，否则删一个文件就把面板顶回来。</summary>
    private bool _trashManuallyClosed;

    public string TrashButtonText => TrashEntries.Count == 0 ? "回收站" : $"回收站 · {TrashEntries.Count}";

    partial void OnTrashErrorChanged(string? value) => OnPropertyChanged(nameof(HasTrashError));

    partial void OnHasTrashEntriesChanged(bool value) => OnPropertyChanged(nameof(TrashButtonText));

    /// <summary>
    /// 重新读回收站。条目来自 <c>&lt;data&gt;/trash/contents/</c>，与实例回收站并列但互不影响。
    /// </summary>
    private async Task RefreshTrashAsync()
    {
        var content = _content;

        if (content is null)
        {
            TrashEntries.Clear();
            HasTrashEntries = false;
            TrashSummary = string.Empty;
            return;
        }

        try
        {
            var entries = await content.ListTrashAsync().ConfigureAwait(true);

            TrashEntries.Clear();

            foreach (var entry in entries)
            {
                TrashEntries.Add(new ContentTrashRow(entry, KindLabelOf(entry.Kind)));
            }

            HasTrashEntries = TrashEntries.Count > 0;
            TrashSummary = HasTrashEntries
                ? $"{TrashEntries.Count} 个已删除的文件可恢复，彻底删除后不可恢复"
                : string.Empty;

            // 有条目时默认展开：用户刚删完东西，下一步就是想知道它去了哪。
            if (!HasTrashEntries)
            {
                IsTrashOpen = false;
            }
            else if (!_trashManuallyClosed)
            {
                IsTrashOpen = true;
            }
        }
        catch (Exception ex)
        {
            TrashError = $"读取回收站失败：{ex.Message}";
        }
        finally
        {
            OnPropertyChanged(nameof(TrashButtonText));
        }
    }

    [RelayCommand]
    private void ToggleTrash()
    {
        IsTrashOpen = !IsTrashOpen;
        _trashManuallyClosed = !IsTrashOpen;
    }

    [RelayCommand]
    private async Task RestoreTrashAsync(ContentTrashRow? row)
    {
        if (row is null || _content is null || IsTrashBusy)
        {
            return;
        }

        IsTrashBusy = true;
        TrashError = null;

        try
        {
            var restored = await _content.RestoreAsync(row.Entry.Id).ConfigureAwait(true);

            if (!restored)
            {
                TrashError = $"恢复「{row.Name}」失败：文件可能已被占用或原目录不可写。";
            }

            await RefreshTrashAsync().ConfigureAwait(true);

            // 恢复出来的是文件系统里的新文件，主列表必须重扫才能看到它。
            await ScanCurrentTargetAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TrashError = $"恢复失败：{ex.Message}";
        }
        finally
        {
            IsTrashBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeTrashAsync(ContentTrashRow? row)
    {
        if (row is null || _content is null || IsTrashBusy)
        {
            return;
        }

        IsTrashBusy = true;
        TrashError = null;

        try
        {
            await _content.PurgeTrashAsync(row.Entry.Id).ConfigureAwait(true);
            await RefreshTrashAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TrashError = $"彻底删除失败：{ex.Message}";
        }
        finally
        {
            IsTrashBusy = false;
        }
    }

    /// <summary>
    /// 重新读取游戏运行状态。读失败一律当作「没在运行」：
    /// 这个状态的用途是禁用按钮，误判成运行中会让功能整块不可用。
    /// </summary>
    private void RefreshGameRunningState()
    {
        try
        {
            IsGameRunning = _launcher?.IsGameRunning == true;
        }
        catch (Exception)
        {
            IsGameRunning = false;
        }
    }

    private void ClearUpdates()
    {
        Updates.Clear();
        UpdateSummary = string.Empty;
        UpdateError = null;
        HasUpdates = false;
        IsCheckingUpdates = false;
        IsUpdateConfirmOpen = false;

        // 「可更新」档的数据源没了：继续留在这一档会得到一个永远空、且筛选按钮已置灰的列表。
        if (ActiveFilter == ModsFilter.CanUpdate)
        {
            ActiveFilter = ModsFilter.All;
        }

        NotifyUpdateSelectionChanged();
    }

    private static string ComposeUpdateSummary(ModUpdateReport report)
    {
        if (report.Total == 0)
        {
            return "没有可检查的模组";
        }

        var parts = new List<string> { $"共 {report.Total} 个" };

        if (report.Updatable > 0)
        {
            parts.Add($"{report.Updatable} 个有新版本");
        }

        var failed = report.Items.Count(info => !string.IsNullOrWhiteSpace(info.Error));
        var unidentified = Math.Max(0, report.Total - report.Matched - failed);

        if (unidentified > 0)
        {
            parts.Add($"{unidentified} 个未识别");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} 个检查失败");
        }

        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private void RequestDelete(ContentCardViewModel? card)
    {
        PendingDelete = card;
        ErrorText = null;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private Task ConfirmDeleteAsync() => ConfirmDeleteCoreAsync(permanent: false);

    /// <summary>彻底删除单个文件（不进回收站）。PCL2 里是按住 Shift 的那条分支。</summary>
    [RelayCommand]
    private Task ConfirmDeletePermanentAsync() => ConfirmDeleteCoreAsync(permanent: true);

    private async Task ConfirmDeleteCoreAsync(bool permanent)
    {
        var card = PendingDelete;
        PendingDelete = null;

        if (card is null)
        {
            return;
        }

        try
        {
            if (_content is not null)
            {
                await _content.DeleteAsync(card.Item, permanent).ConfigureAwait(true);
            }

            _all.Remove(card);
            ApplyFilter();
            UpdateSubtitle();

            if (!permanent)
            {
                await RefreshTrashAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RevealAsync(ContentCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            if (_platform is null)
            {
                ErrorText = "无法打开目录：平台服务尚未就绪。";
                return;
            }

            await _platform.RevealInFileManagerAsync(card.Item.FilePath);
        }
        catch (Exception ex)
        {
            ErrorText = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 卡片的右键菜单：复制文件名 / 复制完整路径。
    ///
    /// 为什么要这两个：批量改名 / 去网页搜同名项目 / 反馈问题时贴路径，这三种场景都得先知道文件名，
    /// 而卡片上的名字是 <c>DisplayName</c>（可能是项目名），未必等于磁盘名。
    /// </summary>
    [RelayCommand]
    private async Task CopyFileNameAsync(ContentCardViewModel? card)
    {
        if (card is null || _platform is null)
        {
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(card.Item.FileName))
            {
                ErrorText = "复制文件名失败：剪贴板不可用，详见启动器日志。";
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"复制文件名失败：{ex.Message}";
        }
    }

    /// <summary>复制磁盘上的完整路径。</summary>
    [RelayCommand]
    private async Task CopyPathAsync(ContentCardViewModel? card)
    {
        if (card is null || _platform is null)
        {
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(card.Item.FilePath))
            {
                ErrorText = "复制路径失败：剪贴板不可用，详见启动器日志。";
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"复制路径失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 卡片的右键菜单：带着这个文件的<b>项目名</b>跳到资源页并自动搜索。
    ///
    /// 为什么预填词不能用磁盘文件名：<c>sodium-fabric-0.5.8+mc1.20.1.jar</c> 直接丢进资源站，
    /// 命中率接近零——搜索索引里存的是项目名（「Sodium」），不是构建产物名。
    ///
    /// 为什么走挂起槽而不是「先导航、再设文本、再触发搜索」：那会先按空串搜一次、再按项目名搜一次，
    /// 界面闪两下还多打一次请求。请求写在这里，由资源页在它自己的自动搜索之前取走消费。
    /// </summary>
    [RelayCommand]
    private void LookupInLibrary(ContentCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var query = BuildLookupQuery(card.Item);

        if (query.Length == 0)
        {
            ErrorText = "无法从这个文件推断项目名，已取消跳转。";
            return;
        }

        // 导航服务缺失时不能先发布：槽里的词没有消费方，会污染下一次正常进入资源页的搜索。
        if (_navigation is null)
        {
            ErrorText = "无法跳转到资源页：导航服务尚未就绪。";
            return;
        }

        // 顺序不能反：Publish 必须先于 Navigate，否则资源页的导航生命周期可能先跑完、取到空槽。
        LibraryLookupSlot.Publish(new LibraryLookup(query, card.Item.Kind));
        _navigation.Navigate(NavigationKeys.Library);
    }

    /// <summary>预填词的最短长度：单字符搜出来的东西没有意义。</summary>
    private const int MinLookupLength = 2;

    /// <summary>
    /// 卡片的右键菜单：带着项目名去 MC 百科搜索（PCL2 <c>PageInstanceMod.xaml.vb:772</c>）。
    ///
    /// 为什么需要它：MC 百科收录了大量只在中文社区维护玩法的模组（尤其国产模组），
    /// 资源站搜不到的东西常常能在这里找到教程与前置关系。搜索词与「去资源站查找」共用
    /// <see cref="BuildLookupQuery"/>，两个入口对同一个文件必须给出同一个名字。
    ///
    /// 为什么用浏览器打开而不是内嵌：MC 百科没有公开 API，内嵌 WebView 会多一层依赖与脚本执行面。
    /// </summary>
    [RelayCommand]
    private async Task SearchMcmodAsync(ContentCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (_platform is null)
        {
            ErrorText = "无法打开 MC 百科：平台服务尚未就绪。";
            return;
        }

        var query = BuildLookupQuery(card.Item);

        if (query.Length == 0)
        {
            ErrorText = "无法从这个文件推断项目名，已取消搜索。";
            return;
        }

        try
        {
            await _platform.OpenUrlAsync(McmodSearchUrl(query)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorText = $"打开 MC 百科失败：{ex.Message}";
        }
    }

    /// <summary>
    /// MC 百科搜索地址。参数顺序与 <c>site=all&amp;filter=0</c> 照抄 PCL2，
    /// 少了这两个参数会落到「只搜模组」的子域筛选，搜不到教程与整合包。
    /// </summary>
    public static string McmodSearchUrl(string query) =>
        "https://www.mcmod.cn/s?key=" + Uri.EscapeDataString(query) + "&site=all&filter=0";

    /// <summary>
    /// 从本地条目推出资源站该用的搜索词。
    ///
    /// 优先级：元数据里的项目名 → 文件名剥掉版本 / 加载器段 → modid → 文件名原样。
    /// 元数据名最可靠（<c>fabric.mod.json</c> / <c>mods.toml</c> 的 <c>name</c> 就是作者填的项目名）；
    /// 但老 jar 与解析失败的文件没有元数据，此时 <see cref="ContentItem.DisplayName"/> 会被填成文件名主干，
    /// 带 <c>-1.20.1-forge-15.2.0</c> 这类后缀，必须剥掉才能拿去搜。
    /// </summary>
    public static string BuildLookupQuery(ContentItem item)
    {
        var metadataName = NormalizeCandidate(item.DisplayName);
        var fileStem = NormalizeCandidate(StripFileNameExtension(item.FileName));

        // 与文件名主干逐字符相同（Ordinal）说明 DisplayName 是「文件名主干」兜底值，不是真元数据；
        // 只差大小写时仍算元数据名——作者写的 "Sodium" 比小写主干更适合当搜索词。
        if (metadataName.Length >= MinLookupLength
            && !string.Equals(metadataName, fileStem, StringComparison.Ordinal))
        {
            return metadataName;
        }

        var stripped = StripVersionTail(fileStem);

        if (stripped.Length >= MinLookupLength)
        {
            return stripped;
        }

        // modid 在资源站通常也是有效关键词（sodium / jei / appleskin），但只在没有更好的选择时用：
        // 它是内部标识，未必等于项目名。
        var id = NormalizeCandidate(item.Id);

        if (id.Length >= MinLookupLength && id.IndexOf(' ') < 0)
        {
            return id;
        }

        return fileStem.Length >= MinLookupLength ? fileStem : string.Empty;
    }

    /// <summary>去首尾空白并把连续空白压成一个空格：DisplayName 里可能带换行或制表符。</summary>
    private static string NormalizeCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 取文件名主干。禁用状态是改扩展名（<c>.jar.disabled</c>），
    /// 只剥一层 <see cref="Path.GetExtension(string)"/> 会留下 <c>sodium-fabric-0.5.8.jar</c>。
    /// </summary>
    private static string StripFileNameExtension(string fileName)
    {
        var stem = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

        return Path.GetFileNameWithoutExtension(stem);
    }

    /// <summary>单独出现即代表「这一段的后面是版本 / 加载器标记」的词。大小写不敏感。</summary>
    private static readonly string[] VersionMarkerTokens =
    [
        "forge",
        "fabric",
        "neoforge",
        "quilt",
        "liteloader",
        "mc",
    ];

    /// <summary>
    /// 从第一个「版本段」处截断，例如 <c>jei-1.20.1-forge-15.2.0.27</c> → <c>jei</c>。
    ///
    /// 只认 <c>-</c> / <c>_</c> / <c>+</c> / 空格 作为段分隔符，点不算：<c>1.20.1</c> 与
    /// <c>some.mod</c> 里的点都在段内。
    /// 截断点落在第一段（整个名字就是版本号）时原样返回，剥完变空比不剥更糟。
    /// </summary>
    private static string StripVersionTail(string stem)
    {
        if (stem.Length == 0)
        {
            return stem;
        }

        var segmentStart = 0;

        for (var i = 0; i <= stem.Length; i++)
        {
            var isEnd = i == stem.Length;
            var isSeparator = !isEnd && stem[i] is '-' or '_' or '+' or ' ';

            if (!isEnd && !isSeparator)
            {
                continue;
            }

            if (i > segmentStart && IsVersionSegment(stem.AsSpan(segmentStart, i - segmentStart)))
            {
                if (segmentStart == 0)
                {
                    return stem;
                }

                return stem[..segmentStart].TrimEnd('-', '_', '+', ' ');
            }

            segmentStart = i + 1;
        }

        return stem;
    }

    /// <summary>单个段是否像版本号（<c>1.20.1</c> / <c>v2</c> / <c>mc1.20</c>）或加载器名。</summary>
    private static bool IsVersionSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty)
        {
            return false;
        }

        // 数字开头：1.20.1 / 0.5.8 / 24.2.0
        if (char.IsDigit(segment[0]))
        {
            return true;
        }

        // v2 / V1.2
        if ((segment[0] is 'v' or 'V') && segment.Length > 1 && char.IsDigit(segment[1]))
        {
            return true;
        }

        // mc1.20：前缀后必须紧跟数字，避免把独立的 mc 段当成版本标记。
        if (segment.Length > 2
            && (segment[0] is 'm' or 'M')
            && (segment[1] is 'c' or 'C')
            && char.IsDigit(segment[2]))
        {
            return true;
        }

        foreach (var token in VersionMarkerTokens)
        {
            if (segment.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>右键菜单里的「启用 / 禁用」：与卡片上的开关走同一条链路。</summary>
    [RelayCommand]
    private async Task ToggleEnabledAsync(ContentCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        await ApplyEnabledAsync(card, !card.IsEnabled);
    }

    /// <summary>卡片上的启用开关变化后回调。异常已被内部吞掉并写进 <see cref="ErrorText"/>。</summary>
    internal void ApplyEnabled(ContentCardViewModel card, bool enabled)
    {
        _ = ApplyEnabledAsync(card, enabled);
    }

    private async Task ApplyEnabledAsync(ContentCardViewModel card, bool enabled)
    {
        if (_content is null)
        {
            UpdateSubtitle();
            RefreshToggleAllLabel();
            return;
        }

        try
        {
            await _content.SetEnabledAsync(card.Item, enabled);
            UpdateSubtitle();
            RefreshToggleAllLabel();
        }
        catch (Exception ex)
        {
            card.SetEnabledSilently(!enabled);
            ErrorText = $"切换启用状态失败：{ex.Message}";
        }
    }

    // ---------------- 目标列表 ----------------

    /// <summary>
    /// 重新读取目标列表并重建左栏。失败必须退化成「只有一个全局目标」：
    /// 实例服务读不到时页面仍然要能管理 .minecraft 下的内容。
    /// </summary>
    private async Task ReloadTargetsAsync()
    {
        IReadOnlyList<ContentTarget> targets;

        try
        {
            targets = await LoadTargetsCoreAsync();
            _targetLoadError = null;
        }
        catch (Exception ex)
        {
            _targetLoadError = $"读取游戏目录列表失败，已退回全局目录：{ex.Message}";
            ErrorText = _targetLoadError;
            targets = [BuildGlobalTarget()];
        }

        RebuildTargetGroups(targets, ResolvePreferredTargetKey(), rememberSelection: true);
    }

    /// <summary>
    /// 取全部目标：<see cref="IInstanceContext"/> 负责把「实例列表 + 全局 .minecraft」统一成
    /// 同一套 <see cref="ContentTarget"/>。服务缺失时只给全局目标，绝不抛异常。
    /// </summary>
    private async Task<IReadOnlyList<ContentTarget>> LoadTargetsCoreAsync()
    {
        if (_instanceContext is null)
        {
            return [BuildGlobalTarget()];
        }

        await _instanceContext.LoadAsync();
        return await _instanceContext.ListTargetsAsync();
    }

    /// <summary>切换目标后要落到的 Key：页内记忆 → 当前实例 → 全局。</summary>
    private string ResolvePreferredTargetKey()
    {
        if (!string.IsNullOrWhiteSpace(_lastTargetKey))
        {
            return _lastTargetKey;
        }

        var currentInstanceId = _instanceContext?.Current?.Id;
        return string.IsNullOrWhiteSpace(currentInstanceId) ? GlobalTargetKey : currentInstanceId;
    }

    /// <summary>
    /// 重建左栏：全局目录独立置顶，实例按 <see cref="LoaderOrder"/> 分组。
    ///
    /// 左栏是「先分类版本」那一级，原版实例也要列出来：用户点它才知道「这个版本没有模组可管」。
    /// 但全局目录不是版本，混进「原版」组会让它看起来像某个版本的原版实例——
    /// 它应当出现在最上面，且不显示任何版本 / 加载器标签。
    /// </summary>
    private void RebuildTargetGroups(
        IReadOnlyList<ContentTarget> targets,
        string? preferredKey,
        bool rememberSelection)
    {
        TargetGroups.Clear();

        var global = targets.FirstOrDefault(target => target.Kind == ContentTargetKind.Global);
        GlobalTarget = global is null ? null : new TargetItemViewModel(global);
        OnPropertyChanged(nameof(GlobalTarget));
        OnPropertyChanged(nameof(HasGlobalTarget));

        foreach (var loader in LoaderOrder)
        {
            var items = targets
                .Where(target => target.Kind != ContentTargetKind.Global)
                .Where(target => NormalizeLoader(target.Loader) == loader)
                .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(target => new TargetItemViewModel(target))
                .ToList();

            if (items.Count > 0)
            {
                TargetGroups.Add(new ModsTargetGroup(loader, items));
            }
        }

        OnPropertyChanged(nameof(HasTargets));

        var candidates = new List<TargetItemViewModel>(1 + TargetGroups.Sum(group => group.Items.Count));

        if (GlobalTarget is not null)
        {
            candidates.Add(GlobalTarget);
        }

        candidates.AddRange(TargetGroups.SelectMany(group => group.Items));

        var picked = candidates.FirstOrDefault(item => string.Equals(item.Target.Key, preferredKey, StringComparison.Ordinal))
                     ?? candidates.FirstOrDefault(item => string.Equals(item.Target.Key, GlobalTargetKey, StringComparison.Ordinal))
                     ?? candidates.FirstOrDefault();

        SetSelectedTarget(picked, rememberSelection);
    }

    /// <summary>服务全部缺失时的唯一目标：直接扫全局 .minecraft。</summary>
    private ContentTarget BuildGlobalTarget()
    {
        // 引用存下来供 CanLoadMods 豁免：这个目标的加载器是「不知道」，不是「原版」。
        _placeholderTarget = new ContentTarget
        {
            Key = GlobalTargetKey,
            Kind = ContentTargetKind.Global,
            DisplayName = "全局目录 (.minecraft)",
            GameDirectory = ResolveFallbackDirectory(),
            IsVersionInstalled = true,
        };

        return _placeholderTarget;
    }

    private string ResolveFallbackDirectory()
    {
        var directory = _paths?.MinecraftDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".minecraft");
        }

        try
        {
            return Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            return directory;
        }
    }

    private void SetSelectedTarget(TargetItemViewModel? item, bool remember = true)
    {
        var previous = SelectedTarget;

        if (previous is not null)
        {
            previous.IsSelected = false;
        }

        SelectedTarget = item;

        if (item is null)
        {
            return;
        }

        item.IsSelected = true;

        if (remember)
        {
            _lastTargetKey = item.Target.Key;
        }
    }

    private static LoaderKind NormalizeLoader(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => LoaderKind.Fabric,
        LoaderKind.NeoForge => LoaderKind.NeoForge,
        LoaderKind.Forge => LoaderKind.Forge,
        LoaderKind.Quilt => LoaderKind.Quilt,
        _ => LoaderKind.Vanilla,
    };

    // ---------------- 右栏扫描 ----------------

    /// <summary>只重扫右栏：当前目标 + 当前分类。切换目标 / 切换分类都走这里。</summary>
    private async Task ScanCurrentTargetAsync()
    {
        // 分类要和目标兼容：原版目标下停在「模组」只会得到一个永远空的列表，而模组入口
        // 此时已经收起——用户连手动切走的按钮都没有。落到资源包，那里原版照常可用。
        // 放在扫描前统一对齐，进页面 / 切目标 / 切分类 / 刷新四条路径都不会漏。
        if (!CanLoadMods && ActiveKind == ContentKind.Mod)
        {
            ActiveKind = ContentKind.ResourcePack;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsLoading = true;

        // 只清扫描类的错误；目标列表读取失败的提示要留着，否则用户看到的是一个「正常」的单目标列表。
        ErrorText = _targetLoadError;
        ClearWarnings();

        // 换了目标 / 分类之后旧的更新结果不再对应当前目录，直接丢掉，避免用户拿着过期结论去删文件。
        ClearUpdates();

        try
        {
            // 旧卡片在下一刻就全部作废，勾选状态必须一起清掉：
            // 留着会让「已选 3 项」指向三个已经不在列表里的对象。
            ClearSelectionCore();
            ClearBatchResult();
            _all.Clear();

            if (_content is null)
            {
                _all.AddRange(BuildSampleCards());
            }
            else
            {
                var target = SelectedTarget;

                if (target is null)
                {
                    Items.Clear();
                    IsEmpty = true;
                    EmptyTitle = "没有可用的游戏目录";
                    EmptyHint = "既没有实例也取不到 .minecraft 目录，请到设置页检查游戏目录。";
                    UpdateSubtitle();
                    return;
                }

                var scanned = await _content.ScanAsync(target.GameDirectory, ActiveKind, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _all.AddRange(scanned.Select(item => new ContentCardViewModel(item, this)));
            }

            ApplyFilter();
            UpdateSubtitle();
            RefreshToggleAllLabel();

            // 列表已经渲染出来之后再补全：卡片先用本地元数据出现，名字与描述稍后升级。
            StartProjectEnrichment();
        }
        catch (OperationCanceledException)
        {
            // 切换目标 / 分类时旧扫描被取消属于正常流程。
        }
        catch (Exception ex)
        {
            Items.Clear();
            IsEmpty = true;
            EmptyTitle = "扫描失败";
            EmptyHint = ex.Message;
            ErrorText = $"读取{KindLabel}失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    // ==================== 在线信息补全 ====================

    /// <summary>
    /// 扫描完成后在后台补全在线项目信息（项目名 → MC 百科译名 → 摘要 → 图标）。
    ///
    /// <para/>
    /// 刻意不 await：补全只改卡片上的标题与描述，让列表渲染等几十个网络请求，
    /// 会把「进页面」变成「等 3 秒白屏」。结果回来了再由 <see cref="ApplyProjectInfo"/> 刷新。
    ///
    /// <para/>
    /// 四类内容都触发：资源站对模组 / 资源包 / 光影包 / 数据包都有对应项目，而图标正是这里的主要目的——
    /// 包里自带 <c>pack.png</c> 的只是少数，光影包尤其如此，只在模组分类下补全等于让光影卡片
    /// 永远只有首字母占位。数据包的目录形态算不出哈希，由 Core 侧跳过。
    /// </summary>
    private void StartProjectEnrichment()
    {
        if (_projects is null || IsDemoMode || !SupportsProjectEnrichment(ActiveKind))
        {
            return;
        }

        var target = SelectedTarget;

        if (target is null)
        {
            return;
        }

        var cards = _all.Where(card => card.Item.Kind == ActiveKind).ToList();

        if (cards.Count == 0)
        {
            return;
        }

        _enrichCts?.Cancel();
        _enrichCts?.Dispose();
        _enrichCts = new CancellationTokenSource();

        _ = EnrichProjectsAsync(cards, target.GameDirectory, _enrichCts.Token);
    }

    /// <summary>该分类的内容在资源站上是否有对应项目（决定要不要走哈希反查补全）。</summary>
    private static bool SupportsProjectEnrichment(ContentKind kind) =>
        kind is ContentKind.Mod or ContentKind.ResourcePack or ContentKind.ShaderPack or ContentKind.DataPack;

    private async Task EnrichProjectsAsync(
        IReadOnlyList<ContentCardViewModel> cards,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _projects!
                .EnrichAsync(cards.Select(card => card.Item).ToList(), gameDirectory, cancellationToken)
                .ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 按引用匹配：扫描给每个文件只建一个 ContentItem，补全结果里的就是那一个。
            var byItem = new Dictionary<ContentItem, ModProjectInfo>(ReferenceEqualityComparer.Instance);

            foreach (var info in result.Items)
            {
                byItem[info.Item] = info;
            }

            foreach (var card in cards)
            {
                if (byItem.TryGetValue(card.Item, out var info))
                {
                    card.ApplyProjectInfo(info);
                }
            }

            // 标题换了意味着搜索词可能命中：正在搜索时按新名字重排一次列表。
            // 没有搜索词时不动列表，避免无谓地重建 Items（会打断正在进行的滚动）。
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                ApplyFilter();
            }
        }
        catch (OperationCanceledException)
        {
            // 切目标 / 离开页面导致的取消是正常流程。
        }
        catch (Exception ex)
        {
            // 补全失败不写 ErrorText：本地信息已经够用，把网络问题摆在模组页上只会误导
            // （用户会以为自己的 jar 坏了）。日志里留一条即可。
            _ = ex;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var updatable = UpdatableItems();
        Items.Clear();

        foreach (var card in _all)
        {
            if (query.Length > 0 && !card.Matches(query))
            {
                continue;
            }

            if (!PassesStatusFilter(card, updatable))
            {
                continue;
            }

            Items.Add(card);
        }

        UpdateEmptyState(query);

        // 顺序不能动：先裁掉「已不在可见列表里的勾选」，再刷新按钮状态。
        // 少了这一步，筛选状态下按「删除选中」会连看不见的项一起删掉。
        PruneSelection();
        RefreshToggleAllLabel();
        NotifyUpdateSelectionChanged();
    }

    /// <summary>
    /// 「可更新」集合。从既有 <see cref="Updates"/> 派生，不再发一次网络请求
    /// （PCL2 <c>CanPassFilter</c> 的 <c>CanUpdate</c> 分支读的是同一个 <c>HasUpdate</c>）。
    ///
    /// 用引用相等比较 <see cref="ContentItem"/>：扫描时每个文件只创建一个实例，
    /// 更新结果里的 <c>Item</c> 就是它，按值比较反而会把同名的两个文件（不同目录）混在一起。
    /// </summary>
    private HashSet<ContentItem> UpdatableItems()
    {
        var set = new HashSet<ContentItem>(ReferenceEqualityComparer.Instance);

        foreach (var row in Updates)
        {
            if (row.HasUpdate)
            {
                set.Add(row.Info.Item);
            }
        }

        return set;
    }

    private bool PassesStatusFilter(ContentCardViewModel card, HashSet<ContentItem> updatable) => ActiveFilter switch
    {
        ModsFilter.Enabled => card.IsEnabled,
        ModsFilter.Disabled => !card.IsEnabled,
        ModsFilter.CanUpdate => updatable.Contains(card.Item),
        _ => true,
    };

    // ==================== 状态筛选 ====================

    /// <summary>当前状态筛选档位。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFilterDisabled))]
    [NotifyPropertyChangedFor(nameof(IsFilterCanUpdate))]
    private ModsFilter _activeFilter = ModsFilter.All;

    public bool IsFilterAll => ActiveFilter == ModsFilter.All;

    public bool IsFilterEnabled => ActiveFilter == ModsFilter.Enabled;

    public bool IsFilterDisabled => ActiveFilter == ModsFilter.Disabled;

    public bool IsFilterCanUpdate => ActiveFilter == ModsFilter.CanUpdate;

    /// <summary>「可更新」档在还没检查过更新时没有意义，界面上置灰而不是隐藏（按钮消失会让人以为页面变了）。</summary>
    public bool HasKnownUpdates => Updates.Any(row => row.HasUpdate);

    [RelayCommand]
    private void SetFilter(ModsFilter filter)
    {
        if (ActiveFilter == filter)
        {
            return;
        }

        ActiveFilter = filter;
        ApplyFilter();
    }

    private void UpdateEmptyState(string query)
    {
        IsEmpty = Items.Count == 0 && !IsLoading;

        if (!IsEmpty)
        {
            return;
        }

        // 没有目标时优先说这件事：否则会落到下面「当前目标还没有 mods/ 目录」那条，
        // 把一个不存在的目标写成具体目录，用户照着去找只会更糊涂。
        if (SelectedTarget is null)
        {
            EmptyTitle = "没有可用的游戏目录";
            EmptyHint = "既没有实例也取不到 .minecraft 目录，请到设置页检查游戏目录。";
            return;
        }

        if (query.Length > 0)
        {
            EmptyTitle = "没有匹配的内容";
            EmptyHint = $"「{query}」没有命中任何{KindLabel}，换个关键词试试。";
            return;
        }

        // 状态筛选把列表筛空了：不写清楚的话，用户会以为模组全没了。
        if (ActiveFilter != ModsFilter.All && _all.Count > 0)
        {
            EmptyTitle = ActiveFilter switch
            {
                ModsFilter.Enabled => $"没有已启用的{KindLabel}",
                ModsFilter.Disabled => $"没有已禁用的{KindLabel}",
                _ => "没有可更新的内容",
            };
            EmptyHint = ActiveFilter == ModsFilter.CanUpdate
                ? "先点「检查更新」，识别出有新版本的条目后它们会出现在这里。"
                : "切回「全部」查看所有条目。";
            return;
        }

        var subdirectory = KindDirectory(ActiveKind);

        // 数据包有两个落点（全局 datapacks/ 与各存档的 saves/<world>/datapacks/），
        // 不能套用 ContentTarget.ContentDirectory 的单目录判断，否则「存档里有数据包」会被报成没有目录。
        var directoryExists = ActiveKind == ContentKind.DataPack
            ? DataPackDirectoryExists()
            : SelectedTarget?.Target.ContentDirectory(ActiveKind) is { } directory && Directory.Exists(directory);

        if (!directoryExists && _content is not null)
        {
            EmptyTitle = $"当前目标还没有 {subdirectory}/ 目录";

            var globalDirectory = ActiveKind == ContentKind.DataPack
                ? Path.Combine(TargetDirectory, subdirectory)
                : SelectedTarget?.Target.ContentDirectory(ActiveKind) ?? Path.Combine(TargetDirectory, subdirectory);

            EmptyHint = ActiveKind == ContentKind.DataPack
                ? $"数据包放在 {globalDirectory}（对整个游戏生效），或 saves/<世界>/datapacks/（只对那个世界生效）。这两个目录都可以直接新建。"
                : $"运行一次游戏后会自动创建；也可以把{KindLabel}文件直接放进 {globalDirectory}。";
            return;
        }

        EmptyTitle = $"当前目标没有安装{KindLabel}";
        EmptyHint = ActiveKind == ContentKind.DataPack
            ? "把 .zip 或数据包目录放进 datapacks/；存档级数据包放进 saves/<世界>/datapacks/。"
            : "把文件放进这个目标的目录，或到「资源」页安装后再回来。";
    }

    /// <summary>
    /// 数据包的两个落点里任意一个存在，就算「目录已就绪」。
    /// 只看全局 <c>datapacks/</c> 会把「只有存档级数据包」的目标误判成没有目录。
    /// </summary>
    private bool DataPackDirectoryExists()
    {
        var root = TargetDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        if (Directory.Exists(Path.Combine(root, "datapacks")))
        {
            return true;
        }

        try
        {
            var saves = Path.Combine(root, "saves");
            return Directory.Exists(saves)
                   && Directory.EnumerateDirectories(saves)
                       .Any(world => Directory.Exists(Path.Combine(world, "datapacks")));
        }
        catch (Exception)
        {
            // saves/ 读不到时按「没有目录」处理，空态文案仍然可用。
            return false;
        }
    }

    private void UpdateSubtitle()
    {
        if (_all.Count == 0)
        {
            SetSubtitle(IsDemoMode ? "示例数据" : SelectedTargetName);
            return;
        }

        var enabled = _all.Count(card => card.IsEnabled);
        SetSubtitle($"{SelectedTargetName} · {enabled}/{_all.Count} 已启用");
    }

    private void RefreshToggleAllLabel()
        => ToggleAllLabel = Items.Any(card => !card.IsEnabled) ? "全部启用" : "全部禁用";

    private void ClearWarnings()
    {
        Warnings.Clear();
        HasWarnings = false;
    }

    private IEnumerable<ContentCardViewModel> BuildSampleCards()
    {
        var root = SelectedTarget?.GameDirectory ?? ResolveFallbackDirectory();
        var folder = KindDirectory(ActiveKind);

        string[] names = ActiveKind switch
        {
            ContentKind.Mod =>
            [
                "Sodium", "Lithium", "Iris Shaders", "Fabric API", "Xaero's Minimap",
                "Cloth Config", "Mod Menu", "Indium", "YUNG's Better Mineshafts",
            ],
            ContentKind.ResourcePack =>
            [
                "Faithful 32x", "Fresh Animations", "Stay True", "YUNG's Better Doors",
            ],
            ContentKind.DataPack =>
            [
                "Terralith", "Shrine of the Ancients", "Vanilla Tweaks",
            ],
            _ =>
            [
                "Complementary Reimagined", "BSL Shaders", "MakeUp Ultra Fast",
            ],
        };

        string[] versions = ["0.5.8", "0.12.1", "1.7.2", "0.92.2", "23.9.4", "11.1.106"];
        string[] authors = ["CaffeineMC", "Jellysquid", "IrisShaders", "FabricMC", "xaero96", "shedaniel"];

        for (var i = 0; i < names.Length; i++)
        {
            var item = new ContentItem
            {
                FilePath = Path.Combine(root, folder, $"{names[i].ToLowerInvariant().Replace(' ', '-')}.jar"),
                FileName = $"{names[i].ToLowerInvariant().Replace(' ', '-')}.jar",
                Kind = ActiveKind,
                Id = names[i].ToLowerInvariant().Replace(' ', '-'),
                DisplayName = names[i],
                Version = versions[i % versions.Length],
                Authors = authors[i % authors.Length],
                Description = "示例条目，用于在 Core 服务接线前预览内容卡片布局。",
                Loader = i == 3 ? LoaderKind.Vanilla : LoaderKind.Fabric,
                McVersionRange = ">=1.20 <1.21",
                FileSize = 1_240_000 + (i * 318_000),
                LastModified = DateTimeOffset.Now.AddDays(-i),
                IsEnabled = i != 6,
            };

            yield return new ContentCardViewModel(item, this);
        }
    }

    private static string KindDirectory(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        ContentKind.DataPack => "datapacks",

        // 与 Core 的 ContentService.DirectoryNameFor 保持一致。插件不会走本地扫描，
        // 这里给出 plugins/ 只为「万一有人查路径」时不落到 mods/。
        ContentKind.Plugin => "plugins",
        _ => "saves",
    };

    private static string KindLabelOf(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "模组",
        ContentKind.ResourcePack => "资源包",
        ContentKind.ShaderPack => "光影包",
        ContentKind.DataPack => "数据包",
        ContentKind.Plugin => ServerPluginPolicy.KindLabel,
        _ => "内容",
    };

    /// <summary>加载器展示名。分组头与卡片徽标共用同一张表，避免两处文案漂移。</summary>
    internal static string LoaderLabelOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版",
    };

    private static string DisplayNameOf(ContentItem item)
        => string.IsNullOrWhiteSpace(item.DisplayName) ? item.FileName : item.DisplayName;
}

/// <summary>
/// 「模组更新」面板的一行。
///
/// 只做展示：数据全部来自 <see cref="ModUpdateInfo"/>，不在这里改任何模型字段——
/// 检查结果是一次性快照，下一次检查会整体重建列表。
/// </summary>
public sealed class ModUpdateRowViewModel
{
    public ModUpdateRowViewModel(ModUpdateInfo info)
    {
        Info = info;
        FileName = info.Item.FileName;
        Name = string.IsNullOrWhiteSpace(info.Item.DisplayName) ? info.Item.FileName : info.Item.DisplayName;
        Status = info.Status;

        HasUpdate = info.HasUpdate;
        HasError = !string.IsNullOrWhiteSpace(info.Error);
        IsFailed = HasError;
        IsUnidentified = !info.Matched && !HasError;
        IsUpToDate = info.Matched && !info.HasUpdate;

        var current = FirstNonEmpty(info.CurrentVersion, info.Item.Version);
        VersionText = ComposeVersionText(current, info.LatestVersion);

        SourceText = info.Matched
            ? info.Source == ContentSourceKind.CurseForge ? "CurseForge" : "Modrinth"
            : "—";

        ErrorText = info.Error;

        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(info.ProjectId))
        {
            details.Add($"项目 ID：{info.ProjectId}");
        }

        if (!string.IsNullOrWhiteSpace(info.Changelog))
        {
            details.Add(Shorten(info.Changelog.ReplaceLineEndings(" "), 160));
        }

        DetailText = string.Join(" · ", details);
        HasDetail = details.Count > 0;
    }

    public ModUpdateInfo Info { get; }

    /// <summary>本地文件名。检查结果按文件组织，所以第一行必须是最容易在目录里认出来的名字。</summary>
    public string FileName { get; }

    public string Name { get; }

    public string VersionText { get; }

    public string Status { get; }

    /// <summary>来源数据源；未识别时显示 <c>—</c>。</summary>
    public string SourceText { get; }

    public bool HasUpdate { get; }

    public bool IsFailed { get; }

    public bool IsUnidentified { get; }

    public bool IsUpToDate { get; }

    public bool HasError { get; }

    public string? ErrorText { get; }

    public string DetailText { get; }

    public bool HasDetail { get; }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string ComposeVersionText(string? current, string? latest)
    {
        var hasCurrent = !string.IsNullOrWhiteSpace(current);
        var hasLatest = !string.IsNullOrWhiteSpace(latest);

        if (hasCurrent && hasLatest)
        {
            // 本地版本与远程发布号完全相同时不画箭头，否则一行里会出现「0.5.13 → 0.5.13」。
            return string.Equals(current, latest, StringComparison.OrdinalIgnoreCase)
                ? current!
                : $"{current} → {latest}";
        }

        if (hasLatest)
        {
            return latest!;
        }

        return hasCurrent ? current! : "版本未知";
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>一张内容卡片。包装 <see cref="ContentItem"/>，避免把写操作暴露到 XAML 绑定上。</summary>
public sealed partial class ContentCardViewModel : ObservableObject
{
    private readonly ModsViewModel _owner;
    private bool _initialized;
    private bool _suppressToggle;

    public ContentCardViewModel(ContentItem item, ModsViewModel owner)
    {
        Item = item;
        _owner = owner;
        Name = string.IsNullOrWhiteSpace(item.DisplayName) ? StripExtension(item.FileName) : item.DisplayName;
        Initial = Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

        // 数据包与加载器无关，显示「原版」会误导（原版指的是不装加载器的 jar 目录）。
        DetailText = ComposeDetail(item);
        LoaderText = item.Kind == ContentKind.DataPack ? "数据包" : LoaderLabelOf(item.Loader);
        SourceHint = item.SourceHint;
        HasParseError = !string.IsNullOrWhiteSpace(item.ParseError);
        ParseErrorText = item.ParseError ?? string.Empty;
        (PackFormatBadge, PackFormatDetail) = ComposePackFormatHint(item, owner.ActiveMinecraftVersion);
        _isEnabled = item.IsEnabled;
        _initialized = true;
    }

    public ContentItem Item { get; }

    /// <summary>
    /// 宿主页面。<c>ContextMenu</c> 是弹出层，不在主视觉树里，<c>$parent[UserControl]</c> 解析不到页面；
    /// 右键菜单只能通过卡片拿到页面级命令。
    /// </summary>
    public ModsViewModel Owner => _owner;

    public string Name { get; }

    /// <summary>无图标时的占位字母。</summary>
    public string Initial { get; }

    /// <summary>「版本 · 作者」一行摘要。</summary>
    public string DetailText { get; }

    public string LoaderText { get; }

    /// <summary>存档级数据包的所属世界名；全局条目为 null。</summary>
    public string? SourceHint { get; }

    public bool HasSourceHint => !string.IsNullOrWhiteSpace(SourceHint);

    public string SourceHintText => $"存档：{SourceHint}";

    public bool HasParseError { get; }

    public string ParseErrorText { get; }

    /// <summary>
    /// pack_format 不匹配时的徽标文案，例如「需 1.20.x」。
    /// 兼容、或者当前游戏版本未知时都是 null——兼容不显示以减少噪音，未知不猜。
    /// </summary>
    public string? PackFormatBadge { get; }

    /// <summary>徽标的悬浮说明：为什么判成不匹配（游戏版本、期望 format、包里的 format）。</summary>
    public string? PackFormatDetail { get; }

    public bool HasPackFormatWarning => PackFormatBadge is not null;

    /// <summary>
    /// 只做提示，不参与启用 / 启动判定：资源包用旧 format 游戏通常仍会加载，
    /// 数据包会被游戏拒绝，两种都只值得在卡片上说一句。
    /// </summary>
    private static (string? Badge, string? Detail) ComposePackFormatHint(ContentItem item, string? gameVersion)
    {
        if (item.Kind is not (ContentKind.ResourcePack or ContentKind.DataPack))
        {
            return (null, null);
        }

        // 1.21.9+ 的 min_format / max_format 是区间；有区间时不再解析 Version 里的旧式数字
        // （区间文案形如「format 88–94.1」，按尾部数字解析会取到 1）。
        var range = item.MinPackFormat is null && item.MaxPackFormat is null
            ? null
            : new PackFormatRange(item.MinPackFormat, item.MaxPackFormat);

        var legacyFormat = range is null ? PackFormatTable.ReadPackFormat(item.Kind, item.Version) : null;

        var assessment = PackFormatTable.Evaluate(item.Kind, range, legacyFormat, gameVersion);

        return assessment.IsMismatch && assessment.RequiredRange.Length > 0
            ? ($"需 {assessment.RequiredRange}", assessment.Reason)
            : (null, null);
    }

    /// <summary>
    /// 卡片图标。在线项目图标（资源站 logo）比 jar 内嵌图标更常见也更清晰，
    /// 所以在线图标优先；补全失败时退回 jar 里提取的那张。
    /// </summary>
    public string? DisplayIconPath =>
        !string.IsNullOrWhiteSpace(_project?.IconPath) ? _project!.IconPath : Item.IconPath;

    public string? IconPath => DisplayIconPath;

    public bool HasIcon => !string.IsNullOrWhiteSpace(DisplayIconPath);

    // ==================== 在线项目信息 ====================

    private ModProjectInfo? _project;

    /// <summary>当前这张卡片的在线识别结果；补全完成前为 null。</summary>
    public ModProjectInfo? Project => _project;

    /// <summary>
    /// 主标题：中文名 → 资源站原名 → jar 里的显示名 → 文件名。
    ///
    /// 顺序是刻意的：中文用户第一眼要认得出来这是什么模组，
    /// 而「Industrial Craft 2」对多数人不如「工业时代2」直接。
    /// </summary>
    public string TitleText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_project?.ChineseName))
            {
                return _project!.ChineseName!;
            }

            if (!string.IsNullOrWhiteSpace(_project?.Title))
            {
                return _project!.Title!;
            }

            return Name;
        }
    }

    /// <summary>主标题用了中文名时，资源站原名降级为第二行，方便对照搜索。</summary>
    public string? EnglishNameText =>
        !string.IsNullOrWhiteSpace(_project?.ChineseName) && !string.IsNullOrWhiteSpace(_project?.Title)
            ? _project!.Title
            : null;

    public bool HasEnglishName => !string.IsNullOrWhiteSpace(EnglishNameText);

    /// <summary>
    /// 描述：jar 内元数据优先（作者自己写的那句），没有才用资源站摘要。
    /// 卡片的描述只显示两行，完整内容在 ToolTip 里。
    /// </summary>
    public string? DescriptionText =>
        !string.IsNullOrWhiteSpace(Item.Description) ? Item.Description : _project?.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(DescriptionText);

    /// <summary>在线项目所在的资源站；未识别时为 null。</summary>
    public string? SourceBadgeText => _project?.Source switch
    {
        ContentSourceKind.Modrinth => "Modrinth",
        ContentSourceKind.CurseForge => "CurseForge",
        _ => null,
    };

    public bool HasSourceBadge => SourceBadgeText is not null;

    /// <summary>
    /// 悬浮提示：把卡片上放不下的信息集中起来（完整描述、英文名、项目地址、作者）。
    /// 返回 null 时 Avalonia 不弹空框。
    /// </summary>
    public string? ToolTipText
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(EnglishNameText))
            {
                parts.Add(EnglishNameText!);
            }

            if (!string.IsNullOrWhiteSpace(DescriptionText))
            {
                parts.Add(DescriptionText!);
            }

            if (!string.IsNullOrWhiteSpace(Item.Authors))
            {
                parts.Add($"作者：{Item.Authors}");
            }

            // 在线项目页优先（资源站页面比 jar 里的 contact 更常有效）；
            // jar 内声明的 homepage 是没有识别出来时的唯一出口。
            var webUrl = !string.IsNullOrWhiteSpace(_project?.WebUrl) ? _project!.WebUrl : Item.HomePage;

            if (!string.IsNullOrWhiteSpace(webUrl))
            {
                parts.Add(webUrl!);
            }

            if (!string.IsNullOrWhiteSpace(ParseErrorText))
            {
                parts.Add(ParseErrorText);
            }

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }

    /// <summary>补全服务返回结果后由页面写回；所有派生属性一次性通知。</summary>
    internal void ApplyProjectInfo(ModProjectInfo? info)
    {
        _project = info;

        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(DisplayIconPath));
        OnPropertyChanged(nameof(IconPath));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(EnglishNameText));
        OnPropertyChanged(nameof(HasEnglishName));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(SourceBadgeText));
        OnPropertyChanged(nameof(HasSourceBadge));
        OnPropertyChanged(nameof(ToolTipText));
    }

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    /// 勾选态：只表示「参与批量操作」，与 <see cref="IsEnabled"/>（文件是否启用）是两件事。
    /// 刻意不复用 ListBox 的多选：卡片本身就是可点区域，选中高亮会和悬停操作条打架。
    /// </summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>
    /// 搜索命中。中文名与英文名都要能搜到——用户记住的往往是其中一个，
    /// 只按文件名匹配的话「工业时代2」这个关键词一条都搜不出来。
    /// </summary>
    public bool Matches(string query)
        => TitleText.Contains(query, StringComparison.OrdinalIgnoreCase)
           || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || DetailText.Contains(query, StringComparison.OrdinalIgnoreCase)
           || Item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
           || Item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (EnglishNameText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
           || (DescriptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
           || (Item.SourceHint?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>批量切换时静默改值，不回调服务（服务调用由调用方统一发起）。</summary>
    internal void SetEnabledSilently(bool value)
    {
        _suppressToggle = true;
        try
        {
            IsEnabled = value;
        }
        finally
        {
            _suppressToggle = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_initialized || _suppressToggle)
        {
            return;
        }

        _owner.ApplyEnabled(this, value);
    }

    partial void OnIsCheckedChanged(bool value) => _owner.NotifyCardChecked(this, value);

    private static string ComposeDetail(ContentItem item)
    {
        // 数据包没有作者与语义版本号，`pack_format` 只是适配数字。
        // 详情行优先放 pack.mcmeta 的 description——它是唯一「这个包是什么」的说明。
        if (item.Kind == ContentKind.DataPack)
        {
            var packParts = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                packParts.Add(item.Description);
            }

            if (!string.IsNullOrWhiteSpace(item.Version))
            {
                packParts.Add(item.Version);
            }

            return packParts.Count > 0 ? string.Join(" · ", packParts) : item.FileName;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            parts.Add(item.Version);
        }

        if (!string.IsNullOrWhiteSpace(item.Authors))
        {
            parts.Add(item.Authors);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : item.FileName;
    }

    private static string StripExtension(string fileName)
        => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName[..^".disabled".Length])
            : Path.GetFileNameWithoutExtension(fileName);

    private static string LoaderLabelOf(LoaderKind loader) => ModsViewModel.LoaderLabelOf(loader);
}

/// <summary>
/// 左栏的一个加载器分组（Fabric / NeoForge / Forge / Quilt / 原版）。
/// 分组不可变：目标列表变化时整体重建，避免维持「组内增删」的一致性逻辑。
/// </summary>
public sealed class ModsTargetGroup
{
    public ModsTargetGroup(LoaderKind loader, IReadOnlyList<TargetItemViewModel> items)
    {
        Loader = loader;
        Items = items;
    }

    public LoaderKind Loader { get; }

    public string LoaderLabel => ModsViewModel.LoaderLabelOf(Loader);

    public IReadOnlyList<TargetItemViewModel> Items { get; }

    public string CountText => $"{Items.Count} 个";
}

/// <summary>
/// 左栏的一个内容目标（全局 .minecraft 或某个实例）。
/// 只做展示包装 + 选中态，写操作仍然统一走页面 VM 的 ContentTarget。
/// </summary>
public sealed partial class TargetItemViewModel : ObservableObject
{
    public TargetItemViewModel(ContentTarget target)
    {
        Target = target;

        // 全局目录不属于任何版本：不显示「1.21 · 原版」这类标签，否则用户会以为
        // 它是某个版本的原版实例（旧实现正是这么显示的，还顺手收起了模组入口）。
        if (target.Kind == ContentTargetKind.Global)
        {
            MetaText = "所有版本共用";
            return;
        }

        var version = string.IsNullOrWhiteSpace(target.MinecraftVersion)
            ? target.VersionName
            : target.MinecraftVersion;

        MetaText = string.IsNullOrWhiteSpace(version)
            ? $"版本未确定 · {target.LoaderLabel}"
            : $"{version} · {target.LoaderLabel}";
    }

    public ContentTarget Target { get; }

    public string DisplayName => Target.DisplayName;

    /// <summary>第二行小字：「1.20.1 · Fabric」。</summary>
    public string MetaText { get; }

    public string LoaderLabel => Target.LoaderLabel;

    /// <summary>目标目录的绝对路径，用于 ToolTip 与右侧说明行。</summary>
    public string GameDirectory => Target.GameDirectory;

    /// <summary>版本目录不存在时在左栏打警告徽标：这种目标下的 mods 装了也不会被游戏读到。</summary>
    public bool ShowVersionMissing => !Target.IsVersionInstalled;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 回收站里的一行。包装 <see cref="ContentTrashEntry"/>，把「类型 + 大小 + 删除时间」
/// 拼成一行元信息——三列分开写会在窄窗口下互相挤掉。
/// </summary>
public sealed class ContentTrashRow
{
    public ContentTrashRow(ContentTrashEntry entry, string kindLabel)
    {
        Entry = entry;
        Name = entry.FileName;
        MetaText = $"{kindLabel} · {FormatSize(entry.SizeBytes)} · {entry.DeletedAt.ToLocalTime():MM-dd HH:mm}";
    }

    public ContentTrashEntry Entry { get; }

    public string Name { get; }

    public string MetaText { get; }

    /// <summary>删除前的绝对路径，作为悬停提示：恢复回哪儿必须可预期。</summary>
    public string OriginalPath => Entry.OriginalPath;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "大小未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>
/// 一次「去资源站查找」请求：模组页写入，资源页取走。
///
/// <see cref="ContentKind"/> 一起带过去，是因为模组页也管资源包 / 光影 / 数据包：
/// 用户可能正停在资源包分类，跳到资源页却停在模组分类，搜出来必然是空的。
/// </summary>
public sealed record LibraryLookup(string Query, ContentKind Kind);

/// <summary>
/// 跨页预填的挂起槽。
///
/// 为什么是进程内静态：<see cref="INavigationService.Navigate(string)"/> 只认页面键，没有带参数的通道；
/// 而页面实例由容器的页面缓存持有，模组页拿不到资源页实例（也不该拿——那会把两个页面绑死）。
///
/// 为什么只存一条：这是「去这里搜这个」，用户连点两下时以最后一次为准，
/// 留队列只会让资源页在后续几次进入时反复被旧请求劫持。
/// 取出即清空，所以同一份请求不会被执行两次。
/// </summary>
public static class LibraryLookupSlot
{
    private static LibraryLookup? _pending;

    /// <summary>写入待消费请求。覆盖未消费的旧请求。</summary>
    public static void Publish(LibraryLookup lookup) => Interlocked.Exchange(ref _pending, lookup);

    /// <summary>取走待消费请求并清空槽位。没有待处理请求时返回 null。</summary>
    public static LibraryLookup? TryTake() => Interlocked.Exchange(ref _pending, null);

    /// <summary>丢弃待消费请求。测试与「导航失败」时用。</summary>
    public static void Clear() => Interlocked.Exchange(ref _pending, null);
}
