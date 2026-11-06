using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaLauncher.ViewModels;

/// <summary>版本页的三个同级视图。</summary>
public enum VersionsViewKind
{
    /// <summary>本机已安装的版本。</summary>
    Installed = 0,

    /// <summary>Mojang 版本清单里可下载的原版。</summary>
    Available = 1,

    /// <summary>Modrinth 上可在线获取的整合包。</summary>
    Modpacks = 2,
}

/// <summary>
/// 一个视图的私有交互状态：筛选、搜索、选中行。
///
/// 为什么三个视图各持一份而不是共用：票 #87 的硬边界是「三个视图不得共用同一份筛选状态」。
/// 共用时在「可下载」里搜 <c>1.20</c>，切到「已安装」会看到被过滤过的列表，
/// 用户会以为版本丢了。
///
/// 滚动位置**不**在这里：它是 <c>ScrollViewer.Offset</c>（一个 <c>Vector</c>），
/// 属于视图层，放进 ViewModel 会把布局细节漏进业务层。实际实现见
/// <c>Views/VersionsView.axaml.cs</c> 的 <c>_scrollOffsets</c>。
/// </summary>
public abstract partial class VersionsViewStateBase : ObservableObject
{
    protected VersionsViewStateBase(VersionsViewKind kind) => Kind = kind;

    public VersionsViewKind Kind { get; }

    /// <summary>搜索框文本。三个视图各存各的。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>筛选键。<see cref="VersionsViewModel.FilterAll"/> 表示不筛。</summary>
    [ObservableProperty]
    private string _filterKind = VersionsViewModel.FilterAll;

    /// <summary>本视图正在加载第一屏数据。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>列表为空（加载完成且没有任何行），用于视图专属空态。</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>有行但被筛选条件滤空，与「一条都没有」的出口不同。</summary>
    [ObservableProperty]
    private bool _isFilteredEmpty;

    /// <summary>本视图专属的错误文案（网络失败等），只影响本视图。</summary>
    [ObservableProperty]
    private string? _errorText;
}

/// <summary>
/// 「已安装」视图状态。
///
/// 与另两个视图的差别：它的数据在磁盘上，随时可能被外部改变（下载页装完、整合包导入完），
/// 所以它的 <see cref="Rows"/> 会被整体重建；重建时必须保住选中行与滚动位置，
/// 否则用户在「可下载」装完一个版本回来，发现自己站的位置全没了。
/// </summary>
public sealed partial class InstalledViewState : VersionsViewStateBase
{
    public InstalledViewState()
        : base(VersionsViewKind.Installed)
    {
    }

    /// <summary>全量已安装版本（未过滤）。</summary>
    public ObservableCollection<VersionRow> Rows { get; } = new();

    /// <summary>过滤后绑定到列表的版本。</summary>
    public ObservableCollection<VersionRow> VisibleRows { get; } = new();

    [ObservableProperty]
    private VersionRow? _selectedRow;

    /// <summary>「显示已隐藏」。默认关：被隐藏的版本不出现在列表里。</summary>
    [ObservableProperty]
    private bool _showHidden;

    /// <summary>版本全都处于隐藏状态时的空态，与「筛选无结果」分开给文案与出口。</summary>
    [ObservableProperty]
    private bool _isAllHidden;
}

/// <summary>
/// 「可下载」视图状态：Mojang 清单里的原版版本。
///
/// 它不缓存选中行的安装状态到行上——安装完成后要重拉清单才能刷新「已安装」徽标，
/// 而徽标是行的展示字段，所以行对象每次重拉都新建（见 <see cref="RemoteVersionRow"/>）。
/// </summary>
public sealed partial class AvailableViewState : VersionsViewStateBase
{
    public AvailableViewState()
        : base(VersionsViewKind.Available)
    {
    }

    /// <summary>可下载的版本（已按渠道与搜索词过滤）。</summary>
    public ObservableCollection<RemoteVersionRow> Rows { get; } = new();

    /// <summary>当前可见的行（过滤后）。与 <see cref="Rows"/> 分开，筛选不改原始清单。</summary>
    public ObservableCollection<RemoteVersionRow> VisibleRows { get; } = new();

    /// <summary>
    /// 是否只看正式版。默认开：Mojang 清单里快照的数量是正式版的十几倍，
    /// 不筛的话打开这一页看到的全是 snapshot，用户找不到自己想要的 1.20.1。
    /// </summary>
    [ObservableProperty]
    private bool _releaseOnly = true;

    [ObservableProperty]
    private RemoteVersionRow? _selectedRow;

    /// <summary>清单是否已经拉到过（区分「还没拉」与「拉完是空」）。</summary>
    [ObservableProperty]
    private bool _isLoaded;
}

/// <summary>
/// 「整合包」视图状态：Modrinth 的 <c>project_type:modpack</c> 项目。
/// </summary>
public sealed partial class ModpacksViewState : VersionsViewStateBase
{
    public ModpacksViewState()
        : base(VersionsViewKind.Modpacks)
    {
    }

    /// <summary>搜到的整合包项目。</summary>
    public ObservableCollection<ModpackRow> Rows { get; } = new();

    [ObservableProperty]
    private ModpackRow? _selectedRow;

    /// <summary>是否已经搜过一次（区分「还没搜」与「搜完没有结果」）。</summary>
    [ObservableProperty]
    private bool _isLoaded;

    /// <summary>翻页游标：下一次「加载更多」从第几条开始。</summary>
    [ObservableProperty]
    private int _nextOffset;
}

/// <summary>
/// 整合包目录里的一行。
///
/// 展示字段在构造时算好，XAML 只做纯绑定——与 <see cref="VersionRow"/> 同一口径，
/// 避免在模板里写转换成串的表达式（编译绑定校验不到，写错只在运行期静默为空）。
/// </summary>
public sealed partial class ModpackRow : ObservableObject
{
    public ModpackRow(Core.Models.ModrinthProject project)
    {
        Project = project;
        Title = string.IsNullOrWhiteSpace(project.Title) ? "未命名整合包" : project.Title;

        // 描述可能很长且带换行：列表行只显示一行，完整文本进详情。
        DescriptionText = (project.Description ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        AuthorText = string.IsNullOrWhiteSpace(project.Author) ? "未知作者" : project.Author!;

        // 下载量用万 / 亿缩写，与内容页同一口径。
        DownloadsText = project.Downloads switch
        {
            >= 100_000_000 => $"{(project.Downloads / 100_000_000d):0.#} 亿次下载",
            >= 10_000 => $"{(project.Downloads / 10_000d):0.#} 万次下载",
            _ => $"{project.Downloads} 次下载",
        };

        // 支持的游戏版本只列前几个：列表行放不下完整列表，详情面板里给全。
        GameVersionsText = project.GameVersions.Count == 0
            ? "未声明游戏版本"
            : string.Join(" · ", project.GameVersions.Take(3))
              + (project.GameVersions.Count > 3 ? $" 等 {project.GameVersions.Count} 个" : string.Empty);
    }

    public Core.Models.ModrinthProject Project { get; }

    public string Title { get; }

    public string DescriptionText { get; }

    public string AuthorText { get; }

    public string DownloadsText { get; }

    public string GameVersionsText { get; }

    /// <summary>
    /// 该包最近一次导入的状态文案。空串表示没有正在进行或已结束的导入。
    /// 只影响行尾那一列，不进详情以外的任何统计。
    /// </summary>
    [ObservableProperty]
    private string? _importStatusText;

    /// <summary>导入进度 0..1，仅在 <see cref="IsImporting"/> 时有意义。</summary>
    [ObservableProperty]
    private double _importProgress;

    [ObservableProperty]
    private bool _isImporting;

    /// <summary>
    /// 行尾状态列的文案。优先级：导入中的阶段文案 > 导入后的结果 > 下载量。
    ///
    /// 必须有这条优先级链：导入是多阶段的（读版本 → 下载 → 装游戏 → 解压），
    /// 只显示百分比的话「下载失败」和「已导入」在行尾长得一样，用户看不出这一步到底成没成。
    /// </summary>
    public string StatusText => IsImporting
        ? string.IsNullOrWhiteSpace(ImportStatusText) ? $"{ImportProgress * 100:0}%" : ImportStatusText!
        : string.IsNullOrWhiteSpace(ImportStatusText) ? DownloadsText : ImportStatusText!;

    /// <summary>
    /// 本次导入使用的中转归档路径。导入结束要删它，而删的必须是这一个文件——
    /// 用一个局部变量穿过 try/finally 会被取消路径漏掉。
    /// </summary>
    public string? ArchivePath { get; set; }

    partial void OnIsImportingChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnImportProgressChanged(double value) => OnPropertyChanged(nameof(StatusText));

    partial void OnImportStatusTextChanged(string? value) => OnPropertyChanged(nameof(StatusText));
}
