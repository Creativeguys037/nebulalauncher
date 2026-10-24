namespace NebulaLauncher.Core.Models;

/// <summary>
/// Modrinth 搜索结果中的一个项目，对应 <c>GET /v2/search</c> 的 <c>hits[]</c> 元素。
///
/// 只保留 UI 用得到的字段：完整响应里还有 <c>gallery</c> / <c>license</c> / <c>color</c> 等，
/// 展示层不用，解析了也只是多分配内存。
/// </summary>
public sealed class ModrinthProject
{
    /// <summary>项目 ID（base62），下载版本时用它请求 <c>/v2/project/{id}/version</c>。</summary>
    public required string ProjectId { get; init; }

    /// <summary>网址标识，例如 <c>sodium</c>。</summary>
    public required string Slug { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>搜索结果里的作者显示名（不是 UUID）。</summary>
    public string? Author { get; init; }

    public long Downloads { get; init; }

    public long Follows { get; init; }

    /// <summary>96px 缩略图地址，落在 cdn.modrinth.com；可能为 webp。</summary>
    public string? IconUrl { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>该项目声明支持的 Minecraft 版本（搜索结果里直接给出，省一次请求）。</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    public DateTimeOffset? UpdatedAt { get; init; }

    public ContentKind Kind { get; init; } = ContentKind.Mod;

    /// <summary>
    /// 项目网页地址，供「在浏览器打开」使用。
    ///
    /// 插件要单独走 <c>/plugin/</c>：Modrinth 的 API 把插件的 <c>project_type</c> 报成 <c>mod</c>
    /// （见 <see cref="ProjectTypeOf"/>），但**网站路由**把插件放在 <c>/plugin/{slug}</c>。
    /// 用 API 的值去拼网址会得到一个 404 或错误重定向的模组页。
    /// </summary>
    public string WebUrl => Kind == ContentKind.Plugin
        ? $"https://modrinth.com/plugin/{Slug}"
        : $"https://modrinth.com/{ProjectTypeOf(Kind)}/{Slug}";

    /// <summary>
    /// 项目正文（Markdown 原文）。搜索结果里没有这个字段，
    /// 只有 <c>GET /v2/project/{id}</c> 才返回；空字符串表示还没拉或项目没写正文。
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>图库图片地址（详情接口的 <c>gallery[].url</c>）；搜索结果为空。</summary>
    public IReadOnlyList<string> GalleryUrls { get; init; } = Array.Empty<string>();

    /// <summary>问题追踪页；作者没填时为 null。</summary>
    public string? IssuesUrl { get; init; }

    /// <summary>源码仓库页；作者没填时为 null。</summary>
    public string? SourceUrl { get; init; }

    /// <summary>许可标识；作者没填时为 null。</summary>
    public string? LicenseId { get; init; }

    /// <summary>项目页能直接展示的元信息是否已加载（由详情接口回填）。</summary>
    public bool HasDetails => Body.Length > 0 || GalleryUrls.Count > 0 || IssuesUrl is not null || SourceUrl is not null;

    /// <summary>
    /// Modrinth 的 <c>project_type</c> 取值：mod / resourcepack / shader / datapack。
    ///
    /// <see cref="ContentKind.Plugin"/> 刻意映射回 <c>"mod"</c>：这是 API 的真实取值
    /// （插件项目的 <c>project_type</c> 就是 <c>mod</c>，区分靠 loaders）。
    /// 搜索 facet 里写 <c>project_type:plugin</c> 会得不到任何结果。
    /// </summary>
    internal static string ProjectTypeOf(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.ShaderPack => "shader",
        ContentKind.DataPack => "datapack",
        _ => "mod",
    };
}

/// <summary>
/// 数据包的一个安装落点。
///
/// 数据包与模组不同：它既可以对整个游戏生效（<c>&lt;gameDir&gt;/datapacks/</c>），
/// 也可以只对某个存档生效（<c>&lt;gameDir&gt;/saves/&lt;world&gt;/datapacks/</c>）。
/// 这两个落点目录不同、生效范围不同，所以安装前必须让用户明确选一个，不能默认猜。
/// </summary>
/// <param name="Title">选择列表里的展示名。</param>
/// <param name="GameDirectory">要交给安装器的「游戏目录」：全局是 <c>.minecraft</c>，存档级是该存档目录。</param>
/// <param name="WorldName">存档名；全局目标为 null。</param>
public sealed record DataPackInstallTarget(string Title, string GameDirectory, string? WorldName)
{
    /// <summary>true 表示全局目标（对整个游戏生效）。</summary>
    public bool IsGlobal => string.IsNullOrEmpty(WorldName);

    /// <summary>最终落盘目录：<see cref="GameDirectory"/> 下的 <c>datapacks</c>。</summary>
    public string DataPackDirectory => Path.Combine(GameDirectory, "datapacks");
}

/// <summary>
/// 一个可下载的项目版本，对应 <c>GET /v2/project/{id}/version</c> 的元素。
///
/// Modrinth 的一个版本可以挂多个文件（例如 sources jar、dev jar），
/// 这里只取 <c>primary == true</c> 的那个，取不到时退回第一个。
/// </summary>
public sealed class ModrinthVersion
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    /// <summary>面向人的版本名，例如 <c>Sodium 0.5.13 for Fabric</c>。</summary>
    public required string Name { get; init; }

    /// <summary>机器可读版本号，例如 <c>mc1.20.1-0.5.13-fabric</c>。</summary>
    public required string VersionNumber { get; init; }

    /// <summary>release / beta / alpha。</summary>
    public string VersionType { get; init; } = "release";

    public DateTimeOffset? PublishedAt { get; init; }

    public long Downloads { get; init; }

    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();

    /// <summary>落盘用的文件名（由 Modrinth 指定，可能包含 <c>+</c> 与空格）。</summary>
    public required string FileName { get; init; }

    public required string DownloadUrl { get; init; }

    public long FileSize { get; init; }

    /// <summary>文件 SHA1，下载后用于校验。</summary>
    public string? Sha1 { get; init; }

    public string? Changelog { get; init; }

    public bool IsRelease => string.Equals(VersionType, "release", StringComparison.OrdinalIgnoreCase);

    public bool IsBeta => string.Equals(VersionType, "beta", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 该版本声明的依赖（Modrinth <c>dependencies[]</c>）。
    ///
    /// 老实现整段丢弃这个数组，于是「装 JEI 忘了 fabric-api」只能等启动崩在
    /// <c>ModResolutionException</c>。解析后前置才能在安装前算出来。
    /// </summary>
    public IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = Array.Empty<ModrinthDependency>();
}

/// <summary>Modrinth <c>dependency_type</c> 的四种取值。</summary>
public enum ModrinthDependencyType
{
    /// <summary>必须安装，缺了加载器会拒绝启动。</summary>
    Required = 0,

    /// <summary>可选，缺了只影响部分功能。</summary>
    Optional = 1,

    /// <summary>互斥，同时存在会崩。</summary>
    Incompatible = 2,

    /// <summary>已被内嵌进父模组，不需要单独安装。</summary>
    Embedded = 3,
}

/// <summary>
/// 一条依赖。<paramref name="VersionId"/> 与 <paramref name="ProjectId"/> 至少一个非空：
/// 只给 version_id 时需要再查一次 <c>/v2/version/{id}</c> 才能知道项目，解析成本高，
/// 调用方应把这种依赖降级为可选而不是静默丢弃。
/// </summary>
/// <param name="VersionId">精确版本 Id；null 表示只约束项目。</param>
/// <param name="ProjectId">项目 Id；null 表示只知道版本。</param>
/// <param name="FileName">Modrinth 给的建议文件名，仅用于展示。</param>
/// <param name="Type">依赖类型。</param>
public sealed record ModrinthDependency(
    string? VersionId,
    string? ProjectId,
    string? FileName,
    ModrinthDependencyType Type);

/// <summary>
/// 「安装这个版本需要先装哪些」。由 <c>IModrinthService.PlanInstallAsync</c> 产出，
/// UI 在确认框里把 <see cref="Required"/> 全部列出来再让用户点确认。
/// </summary>
public sealed class ModrinthInstallPlan
{
    /// <summary>用户点选的那个版本。</summary>
    public required ModrinthVersion Root { get; init; }

    /// <summary>递归展开的必要前置（已按依赖顺序排列，去重）。</summary>
    public IReadOnlyList<ModrinthVersion> Required { get; init; } = Array.Empty<ModrinthVersion>();

    /// <summary>可选依赖：列出但不自动安装。</summary>
    public IReadOnlyList<ModrinthVersion> Optional { get; init; } = Array.Empty<ModrinthVersion>();

    /// <summary>
    /// 阻断原因（互斥依赖命中、依赖成环、只给 version_id 无法解析等）。非空时 UI 禁止继续，
    /// 因为强行安装大概率得到一个崩在启动期的实例。
    /// </summary>
    public IReadOnlyList<string> Blocked { get; init; } = Array.Empty<string>();

    /// <summary>降级提示（例如可选依赖解析失败），不阻断。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>本次会落盘的 jar 总数（根版本 + 必要前置）。</summary>
    public int TotalFiles => 1 + Required.Count;

    /// <summary>预计下载字节数。</summary>
    public long TotalBytes => Root.FileSize + Required.Sum(version => version.FileSize);

    public bool CanInstall => Blocked.Count == 0;
}

/// <summary>按计划批量安装的结果。<see cref="Installed"/> 与计划顺序一致，逐个独立成败。</summary>
public sealed class ModrinthPlanInstallResult
{
    public bool Success { get; init; }

    public IReadOnlyList<ModrinthPlanFile> Files { get; init; } = Array.Empty<ModrinthPlanFile>();

    /// <summary>整体失败原因（准备阶段失败才有值）；逐文件失败在 <see cref="ModrinthPlanFile.Error"/>。</summary>
    public string? Error { get; init; }

    public int InstalledCount => Files.Count(file => string.IsNullOrEmpty(file.Error) && file.FilePath is not null);

    public int FailedCount => Files.Count(file => !string.IsNullOrEmpty(file.Error));
}

/// <summary>计划里一个文件的落盘结果。</summary>
public sealed class ModrinthPlanFile
{
    public required ModrinthVersion Version { get; init; }

    /// <summary>该文件是否作为前置被安装（false 表示它就是用户点选的那个）。</summary>
    public bool IsDependency { get; init; }

    public string? FilePath { get; init; }

    public long Bytes { get; init; }

    public string? Error { get; init; }
}

/// <summary>一次安装的结果。失败原因已经是中文可展示文本，UI 直接显示即可。</summary>
public sealed class ModrinthInstallResult
{
    public bool Success { get; init; }

    /// <summary>最终落盘的绝对路径；失败为 null。</summary>
    public string? FilePath { get; init; }

    public string? FileName { get; init; }

    /// <summary>实际写入的字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>目标目录里已有同名文件，本次以 <c>xxx (1).jar</c> 落盘。</summary>
    public bool Renamed { get; init; }

    /// <summary>失败原因（中文）。成功为 null。</summary>
    public string? Error { get; init; }

    public static ModrinthInstallResult Ok(string filePath, long bytes, bool renamed) => new()
    {
        Success = true,
        FilePath = filePath,
        FileName = Path.GetFileName(filePath),
        Bytes = bytes,
        Renamed = renamed,
    };

    public static ModrinthInstallResult Fail(string error) => new()
    {
        Success = false,
        Error = error,
    };
}
