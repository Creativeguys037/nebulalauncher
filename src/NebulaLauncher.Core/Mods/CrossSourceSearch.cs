using System.Globalization;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 跨数据源合并搜索时用的一行中性项目信息。
///
/// 为什么要在 Core 里再定一个类型：两个后端的模型字段形状不一样（<c>Title</c> / <c>Name</c>、
/// <c>Downloads</c> / <c>DownloadCount</c>、<c>UpdatedAt</c> / <c>DateModified</c>），
/// 而合并与去重的规则必须先看到「两边同一套字段」才能写。App 层的 <c>LibraryProjectViewModel</c>
/// 做的是展示归一（带图标路径、徽标文案），不能反过来当算法输入——它会拖进 UI 依赖。
///
/// 这个类型只带「排序与去重真正用到的字段」：多一个字段就多一处两边要对齐的地方。
/// </summary>
public sealed record SearchProjectInfo
{
    /// <summary>来自哪个后端。合并时唯一的身份前缀，两个后端的 Id 体系不通用。</summary>
    public required ContentSourceKind Source { get; init; }

    /// <summary>后端内部的项目 Id（Modrinth 是 base62 project_id，CurseForge 是数字 id 的字符串形式）。</summary>
    public required string Id { get; init; }

    /// <summary>项目 slug。Modrinth 一定有；CurseForge 官方接口会给，项目链接解析路径来自 URL，也可能为空。</summary>
    public required string Slug { get; init; }

    /// <summary>展示名。</summary>
    public required string Name { get; init; }

    /// <summary>一句话简介。去重时作为最后一个弱判据（PCL2 <c>ResourceProject.IsLike</c> 也拿它比）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>下载量。两边的统计口径不同，只能当「热度」用，不能当精确值比。</summary>
    public long Downloads { get; init; }

    /// <summary>最后更新时间。用于去重的「一周内」判据；任一后端没给就跳过这道判据。</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>支持的 MC 版本。去重时要求两边集合一致——同一个项目在两个站点上支持的版本理应相同。</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 支持的加载器集合，统一小写（<c>fabric</c> / <c>quilt</c> / <c>forge</c> / <c>neoforge</c>）。
    ///
    /// 去重判据之一（PCL2 <c>ResourceProject.IsLike</c> 的「Mod 加载器一致」）。空集合表示「这一侧没给
    /// 加载器信息」，此时跳过该判据——两站都缺信息时拿空集合去比，会把明明相同的项目判成不同。
    /// </summary>
    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();

    /// <summary>内容类型。光影不做版本集合判据（光影对 MC 版本的声明历来不严谨）。</summary>
    public ContentKind Kind { get; init; } = ContentKind.Mod;

    /// <summary>来自「整合包」分区。<see cref="ContentKind"/> 里没有整合包这一类，只能单独带一个标志。</summary>
    public bool IsModPack { get; init; }

    /// <summary>去重键：来源 + Id。同一个后端内的两条结果永远不该被合并掉。</summary>
    public string IdentityKey => Source == ContentSourceKind.CurseForge ? $"cf:{Id}" : $"mr:{Id}";

    /// <summary>从 Modrinth 的搜索结果造一行。</summary>
    public static SearchProjectInfo FromModrinth(ModrinthProject project, bool isModPack = false)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new SearchProjectInfo
        {
            Source = ContentSourceKind.Modrinth,
            Id = project.ProjectId,
            Slug = project.Slug,
            Name = project.Title,
            Description = project.Description,
            Downloads = project.Downloads,
            UpdatedAt = project.UpdatedAt,
            GameVersions = project.GameVersions,
            Loaders = LoadersOf(project.Categories),
            Kind = project.Kind,
            IsModPack = isModPack,
        };
    }

    /// <summary>从 CurseForge 的搜索结果造一行。</summary>
    public static SearchProjectInfo FromCurseForge(CurseForgeProject project, bool isModPack = false)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new SearchProjectInfo
        {
            Source = ContentSourceKind.CurseForge,
            Id = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Slug = project.Slug,
            Name = project.Name,
            Description = project.Summary,
            Downloads = project.DownloadCount,
            UpdatedAt = project.DateModified,
            GameVersions = project.GameVersions,
            Loaders = project.Loaders,
            Kind = project.Kind,
            IsModPack = isModPack || project.IsModpack,
        };
    }

    /// <summary>Modrinth 的 <c>categories</c> 里挑出加载器项（小写去重）。</summary>
    /// <remarks>
    /// PCL2 同时看 <c>loaders</c> 与 <c>categories</c> 两个字段（<c>ResourceProject.vb:302</c>）；
    /// 本仓库的 <see cref="ModrinthProject"/> 只建模了 <c>categories</c>——搜索结果里加载器恰好都在其中，
    /// 所以只读一处。若以后补了 <c>loaders</c> 字段，这里要一起并进来。
    /// </remarks>
    private static IReadOnlyList<string> LoadersOf(IReadOnlyList<string> categories)
    {
        if (categories.Count == 0)
        {
            return Array.Empty<string>();
        }

        List<string>? loaders = null;

        foreach (var category in categories)
        {
            var text = category?.Trim().ToLowerInvariant();

            if (text is not ("fabric" or "quilt" or "forge" or "neoforge"))
            {
                continue;
            }

            loaders ??= new List<string>(2);

            if (!loaders.Contains(text))
            {
                loaders.Add(text);
            }
        }

        return loaders is null ? Array.Empty<string>() : loaders;
    }
}

/// <summary>
/// 双源（CurseForge + Modrinth）合并搜索的排序与去重规则。全部是纯函数。
///
/// 规则来源：PCL2 <c>Modules/Resource/ResourceSearcher.vb</c> 的 <c>Run</c> 里
/// 「提取非重复项」与「将结果排序并添加」两段（第 449-537 行），以及
/// <c>Modules/Resource/ResourceProject.vb</c> 的版本 / 加载器聚合（:179-196）与
/// <c>IsLike</c> 判据（:664-694）。照抄的部分在下面逐条注明，
/// 与 PCL2 不一致的地方单独写明理由——不要因为「PCL2 没这么做」就把差异当成漏实现。
///
/// ## 去重判据的 PCL2 对应关系
/// 顺序为：同源拒绝 → 加载器一致 → 一周内更新 → 非光影要求版本一致 → 名称 / slug / 简介命中。
/// **逐条差异写在 <see cref="IsSameProject"/> 的注释里**，其中最要紧的一条是 PCL2 源码
/// <c>ResourceProject.vb:680</c> 把「要求 MC 版本一致」写成了 <c>SequenceEqual 才拒绝</c>，
/// 与它自己的注释相反；本仓库按注释意图实现（版本不一致才拒绝）。
///
/// ## 为什么合并后还要「按平台加权」
/// 两个站点的下载量口径差一个数量级：同一个模组在 CurseForge 上的下载量通常远高于 Modrinth。
/// 直接按原始下载量排序，结果等于「CurseForge 的单子霸榜」；按内容类型乘不同系数（PCL2 的做法）
/// 才能得到用户想要的那种混排。
///
/// ## 为什么去重要求「更新时间差在一周内」
/// 两个站点上确实可能存在同名但不同来源的条目（转载、冒名、同名的另一个作品）。
/// 名称相同就把它们合成一条，等于替用户做了一个「这两个是同一个东西」的判断。
/// 加上「一周内都更新过」这道判据之后，剩下的误合并基本只剩「同一个项目」（同一作者往两站同步发布）。
/// </summary>
public static class CrossSourceSearch
{
    /// <summary>判定「同一个项目」时允许的更新时间差。PCL2 取 7 天。</summary>
    public static readonly TimeSpan SameProjectUpdateWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// 下载量权重。PCL2 <c>GetDownloadCountMult</c>：模组与整合包偏好 Modrinth（5 : 1），
    /// 数据包偏好 CurseForge（1 : 10），资源包与光影偏好 Modrinth（4 : 1）。
    /// 这不是「哪个站更好」的评价，是「同一份内容在两个站的下载量差多少倍」的经验系数。
    /// </summary>
    public static double DownloadWeight(ContentKind kind, ContentSourceKind source, bool isModPack = false)
    {
        var curseForge = source == ContentSourceKind.CurseForge;

        if (isModPack || kind == ContentKind.Mod)
        {
            return curseForge ? 1d : 5d;
        }

        return kind switch
        {
            ContentKind.DataPack => curseForge ? 10d : 1d,
            ContentKind.ResourcePack or ContentKind.ShaderPack => curseForge ? 1d : 4d,

            // 服务端插件与模组同口径：同一份 jar 往两站同步发布，CurseForge 的下载量同样高一个量级。
            // 它不会进入安装流程，但会出现在「模组」标签的搜索结果里，混排权重不能按兜底值算。
            ContentKind.Plugin => curseForge ? 1d : 5d,

            // 世界存档不在在线资源里（CurseForge 没有对应 classId），走到这里只是兜底。
            _ => 1d,
        };
    }

    /// <summary>
    /// 排序分。分越高越靠前。
    ///
    /// 没有搜索词时就是「下载量 × 权重」，与 PCL2 一致。
    ///
    /// 有搜索词时按 PCL2 的三段式：相关度（最高 1）+ <c>log10(下载量 × 权重) / 9</c>（10 亿下载量封顶 1）。
    /// **差异**：PCL2 的相似度来自它自带的全文搜索引擎，这里没有对应的实现，
    /// 用「名称里是否完整包含搜索词（忽略大小写）」给出 1 或 0 作为相关度。
    /// 这会让名称命中的结果稳稳排在「只有描述命中」的前面，代价是搜索词的轻微拼写差异不再得分——
    /// 但搜索本身是服务端做的，本地这一层只负责把两站结果混排，不追求复刻服务端的相关度。
    /// </summary>
    public static double Score(SearchProjectInfo hit, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(hit);

        var weighted = Math.Max(1d, hit.Downloads * DownloadWeight(hit.Kind, hit.Source, hit.IsModPack));
        var popularity = Math.Log10(weighted) / 9d;

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return weighted;
        }

        var relevance = hit.Name.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase) ? 1d : 0d;
        return relevance + popularity;
    }

    /// <summary>
    /// 两条结果是不是同一个项目。判据顺序与 PCL2 <c>ResourceProject.IsLike</c>
    /// （<c>Modules/Resource/ResourceProject.vb:664-694</c>）一致：
    /// <list type="number">
    /// <item>同一后端：永远不算同一条（PCL2 先比实例 Id，再判 <c>Platform</c>；同一站内重复由分页去重负责）。</item>
    /// <item>两边都有加载器集合且集合不相等：否（PCL2「Mod 加载器一致」）。</item>
    /// <item>两边都有更新时间且差超过 <see cref="SameProjectUpdateWindow"/>：否（PCL2「差距在一周以内」）。</item>
    /// <item>非光影且两边都有版本集合、CurseForge 的集合不是 Modrinth 的子集：否（PCL2 注释「若不为光影，则要求 MC 版本一致」）。</item>
    /// <item>名称规范化相同 OR slug 规范化相同 OR 两边简介非空且完全相同：是（PCL2 的翻译名 / 原名 / 描述 / slug 四路命中）。</item>
    /// </list>
    ///
    /// **版本判据为什么用子集而不是完全相等**：CurseForge 搜索响应只带最近若干文件
    /// （<c>latestFiles</c> / <c>latestFilesIndexes</c>），Modrinth 侧是项目声明的全集。
    /// 同一项目在 CF 上能看到的版本通常是 MR 的子集；要求完全相等会把同一项目判成不同，
    /// 双源列表里就会出现两条同名条目。反过来「CF 有 MR 没有的版本」才是不同项目的强信号。
    ///
    /// **与 PCL2 源码的字面差异（按注释意图实现）**：<c>ResourceProject.vb:680</c> 写的是
    /// <c>If Types &lt;&gt; Shader AndAlso Drops.Ordered.SequenceEqual(...) Then Return False</c>，
    /// 即「版本集合相等才拒绝」，与上一行注释「若不为光影，则要求 MC 版本一致」正好相反。
    /// 这里按注释意图实现：版本集合不一致才拒绝（且只要求 CF ⊆ MR）。
    ///
    /// **其它差异**：
    /// - PCL2 直接比 <c>ModLoaders</c> 位标志，任一侧为 <c>None</c>（两站都没给）时按位不等直接拒绝；
    ///   这里改成「两边都非空才比」，否则 CurseForge 搜索结果没带加载器信息时会把所有配对全部拒掉。
    /// - PCL2 还有 <c>TranslatedName</c>（MCMOD 译名）这一路判据，本仓库没有对应数据源，不实现。
    /// - PCL2 比的是 <c>Drops</c>（MC 版本序数集合），这里直接比版本字符串集合：同一组版本在两站的写法一致，
    ///   而 <c>Drops</c> 需要一张 MC 版本序数表，本仓库没有照搬。
    /// - 规范化后为空的名称 / slug 不当作命中（两个空串证明不了任何事）。
    /// - 名称规范化只留字母与数字并转小写（PCL2 的 <c>GetRaw</c>）：`JEI` / `jei` / `J.E.I` 归一，
    ///   `Just Enough Items` 与 `JEI` 不归一，后者交给 slug 与简介判据兜底。
    /// </summary>
    public static bool IsSameProject(SearchProjectInfo left, SearchProjectInfo right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Source == right.Source)
        {
            return false;
        }

        // Mod 加载器一致（PCL2 :678）。比集合，与顺序、大小写无关。
        if (left.Loaders.Count > 0
            && right.Loaders.Count > 0
            && !SameSet(left.Loaders, right.Loaders))
        {
            return false;
        }

        // 最近更新时间差距在一周以内（PCL2 :682）。任一侧缺时间就跳过这道判据。
        if (left.UpdatedAt is { } leftUpdated
            && right.UpdatedAt is { } rightUpdated
            && (leftUpdated - rightUpdated).Duration() > SameProjectUpdateWindow)
        {
            return false;
        }

        // 若不为光影，则要求 MC 版本一致（PCL2 :679 的注释意图，见上面的差异说明）。
        if (left.Kind != ContentKind.ShaderPack
            && TryGetVersionSets(left, right, out var curseForgeVersions, out var modrinthVersions)
            && curseForgeVersions.Count > 0
            && modrinthVersions.Count > 0
            && !IsSubset(curseForgeVersions, modrinthVersions))
        {
            return false;
        }

        // 原名 / slug 的英文部分 / 描述文本相同（PCL2 :683-686）。
        var leftName = NormalizeName(left.Name);

        if (leftName.Length > 0 && string.Equals(leftName, NormalizeName(right.Name), StringComparison.Ordinal))
        {
            return true;
        }

        var leftSlug = NormalizeName(left.Slug);

        if (leftSlug.Length > 0 && string.Equals(leftSlug, NormalizeName(right.Slug), StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(left.Description)
            && string.Equals(left.Description, right.Description, StringComparison.Ordinal);
    }

    /// <summary>按来源取出「CurseForge 集合」与「Modrinth 集合」；任一侧不是双源配对时返回 false。</summary>
    private static bool TryGetVersionSets(
        SearchProjectInfo left,
        SearchProjectInfo right,
        out IReadOnlyList<string> curseForge,
        out IReadOnlyList<string> modrinth)
    {
        if (left.Source == ContentSourceKind.CurseForge && right.Source == ContentSourceKind.Modrinth)
        {
            curseForge = left.GameVersions;
            modrinth = right.GameVersions;
            return true;
        }

        if (left.Source == ContentSourceKind.Modrinth && right.Source == ContentSourceKind.CurseForge)
        {
            curseForge = right.GameVersions;
            modrinth = left.GameVersions;
            return true;
        }

        curseForge = Array.Empty<string>();
        modrinth = Array.Empty<string>();
        return false;
    }

    /// <summary><paramref name="subset"/> 的每一项是否都出现在 <paramref name="superset"/> 里（忽略大小写）。</summary>
    private static bool IsSubset(IReadOnlyList<string> subset, IReadOnlyList<string> superset)
    {
        foreach (var value in subset)
        {
            if (!superset.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 合并两站的搜索结果：去重 → 排序 → 截断。
    ///
    /// CurseForge 先于 Modrinth 进入列表，与 PCL2 一致：两边的请求是并行的，谁先回来不确定，
    /// 如果按到达顺序拼，同一组关键词两次搜索的排名会不一样。
    ///
    /// 去重保留先出现的那条（CurseForge 优先），并在结果里标注它「另一站也有」——
    /// 标注由调用方根据 <see cref="IsSameProject"/> 自行决定要不要做，这里只负责不重复。
    /// </summary>
    public static IReadOnlyList<SearchProjectInfo> Merge(
        IEnumerable<SearchProjectInfo>? curseForge,
        IEnumerable<SearchProjectInfo>? modrinth,
        string? searchText = null,
        int limit = int.MaxValue)
    {
        var ordered = new List<SearchProjectInfo>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenIdentities = new List<SearchProjectInfo>();

        foreach (var hit in (curseForge ?? Enumerable.Empty<SearchProjectInfo>())
                     .Concat(modrinth ?? Enumerable.Empty<SearchProjectInfo>()))
        {
            if (hit is null)
            {
                continue;
            }

            // 同一后端内的重复（分页边界重叠）按 Id 去掉。
            if (!seenKeys.Add(hit.IdentityKey))
            {
                continue;
            }

            // 跨后端重复：与已经收下的每一条比一次。两站结果通常各 20 条，这里是 400 次字符串比较，
            // 相对一次网络请求可以忽略；换成哈希索引反而要先定义一套规范化键，误合并的风险更大。
            if (seenIdentities.Any(existing => IsSameProject(existing, hit)))
            {
                continue;
            }

            ordered.Add(hit);
            seenIdentities.Add(hit);
        }

        return ordered
            .OrderByDescending(hit => Score(hit, searchText))
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <summary>名称规范化：只留字母与数字，转小写。`JEI!` / `jei` 归一，`Just Enough Items` 不归一。</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 是不是标准的 MC 版本号：<c>1.20.1</c> / <c>1.21</c> / <c>24w14a</c>。
    ///
    /// 口径来自 PCL2 <c>McVersion.IsStandardFormat</c>（<c>PCLCS/Minecraft/McVersion.cs</c>）：
    /// <c>^1\.\d</c> 一律算标准；未来的 <c>26.1</c> 式版本号（首位 2-9、数值 ≥ 26）也算。
    /// **差异**：PCL2 那个函数不收快照（<c>24w14a</c> 会被判 false），本仓库收——
    /// CurseForge 与 Modrinth 的 <c>game_versions</c> 里快照写法一致（都是 <c>24w14a</c>），
    /// 收进来只会让「版本集合一致」这道判据更严格，不会制造误合并。
    /// </summary>
    public static bool IsStandardGameVersion(string? version)
    {
        var text = version?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // 1.20.1 / 1.21（PCL2 的 ^1\.\d）。
        if (text.Length >= 3 && text[0] == '1' && text[1] == '.' && char.IsAsciiDigit(text[2]))
        {
            return true;
        }

        // 快照：24w14a / 23w31a。PCL2 不收这个形态，理由见 <summary>。
        if (text.Length == 6
            && char.IsAsciiDigit(text[0])
            && char.IsAsciiDigit(text[1])
            && text[2] == 'w'
            && char.IsAsciiDigit(text[3])
            && char.IsAsciiDigit(text[4])
            && char.IsAsciiLetterLower(text[5]))
        {
            return true;
        }

        // 未来的 26.1 式版本号（PCL2 用 ^[2-9]\d\.\d+ 加数值 ≥ 26 判）。
        return text.Length > 3
            && text[0] is >= '2' and <= '9'
            && char.IsAsciiDigit(text[1])
            && text[2] == '.'
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)
            && numeric >= 26;
    }

    /// <summary>两个加载器 / 版本集合是否相同（忽略顺序与大小写，重复项不影响结果）。</summary>
    private static bool SameSet(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);

        return leftSet.SetEquals(new HashSet<string>(right, StringComparer.OrdinalIgnoreCase));
    }
}