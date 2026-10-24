namespace NebulaLauncher.Core.Models;

/// <summary>
/// 数据来自哪条链路。搜索与文件列表走官方 <c>api.curseforge.com</c>（key 已内置）；
/// 只有「粘贴项目链接」那条精确解析走 cfwidget + forgecdn。
/// </summary>
public enum CurseForgeSource
{
    /// <summary>官方 <c>api.curseforge.com</c>（带内置的 x-api-key）。</summary>
    OfficialApi = 0,

    /// <summary>cfwidget 项目详情 + 按 fileId 推导的 forgecdn 直链（项目链接精确解析用）。</summary>
    CfWidget = 1,
}

/// <summary>
/// CurseForge 搜索结果中的一个项目，对应 <c>GET /v1/mods/search</c> 的 <c>data[]</c> 元素。
///
/// 字段命名与 <see cref="ModrinthProject"/> 保持形状一致（Id / Slug / Title / IconUrl …），
/// 但 <see cref="Id"/> 是 CurseForge 的数字 projectId，与 Modrinth 的 base62 id 不通用。
/// </summary>
public sealed class CurseForgeProject
{
    /// <summary>CurseForge 的数字项目 ID，下载文件时用它请求 <c>/v1/mods/{id}/files</c>。</summary>
    public required long Id { get; init; }

    /// <summary>网址标识，例如 <c>jei</c>。</summary>
    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string Summary { get; init; } = string.Empty;

    /// <summary>作者名（多个用「、」连接）。搜索结果里 <c>authors[]</c> 是数组。</summary>
    public string? Authors { get; init; }

    public long DownloadCount { get; init; }

    /// <summary>图标地址，来自 <c>logo.thumbnailUrl</c>，可能为空。</summary>
    public string? IconUrl { get; init; }

    public DateTimeOffset? DateModified { get; init; }

    /// <summary>
    /// 该项目的 MC 版本集合，只保留标准版本号（<c>1.20.1</c> / <c>1.21</c> / <c>24w14a</c>）。
    ///
    /// 来源：搜索响应的 <c>latestFiles[].gameVersions</c>，缺失时退回
    /// <c>latestFilesIndexes[].gameVersion</c>。PCL2 在 <c>ResourceProject.vb:179-196</c> 里同样把
    /// 这两处并进版本集合（两处包含的文件并不相同，见 PCL2 #3599），再交给 <c>IsLike</c> 做版本比对。
    /// 必须去掉 <c>Forge</c> / <c>Client</c> / <c>Server</c> 之类的标签：跨站去重要求两边的版本集合一致，
    /// 把加载器混进来会让同一组版本的集合永远不相等。
    /// </summary>
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 该项目的加载器集合，统一小写（<c>fabric</c> / <c>quilt</c> / <c>forge</c> / <c>neoforge</c>）。
    ///
    /// 来源同 <see cref="GameVersions"/>：<c>latestFiles[].gameVersions</c> 里的加载器标签，
    /// 或 <c>latestFilesIndexes[].modLoader</c> 的数字（1=Forge、4=Fabric、5=Quilt、6=NeoForge）。
    /// PCL2 在 <c>ResourceProject.vb:185</c> 用 <c>ModLoaders Or</c> 把同一份信息聚合成位标志。
    /// </summary>
    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();

    /// <summary>对应 <c>classId</c> 推导出的内容类型。</summary>
    public ContentKind Kind { get; init; } = ContentKind.Mod;

    /// <summary>
    /// 作者是否允许第三方客户端直接下载文件。
    /// 为 <c>false</c> 时 <c>downloadUrl</c> 为空，安装必须引导用户去网页，
    /// 这不是错误，是 CurseForge 的分发策略。
    /// </summary>
    public bool AllowModDistribution { get; init; } = true;

    /// <summary>数据来源。<see cref="CurseForgeSource.CfWidget"/> 表示这条来自项目链接精确解析。</summary>
    public CurseForgeSource Source { get; init; } = CurseForgeSource.OfficialApi;

    /// <summary>
    /// 这是不是一条整合包条目。<see cref="ContentKind"/> 刻意没有 ModPack 值（安装落点、目录名、
    /// 详情页路由等 6 处 switch 依赖它），所以整合包条目的 <see cref="Kind"/> 仍然是
    /// <see cref="ContentKind.Mod"/>；这个标志只用于展示与「装到哪个落点」的分流，不参与内容类型判断。
    /// </summary>
    public bool IsModpack { get; init; }

    /// <summary>
    /// 随项目一起返回的文件列表。cfwidget 在一次响应里就带回全量文件，过滤后放在这里，
    /// 不必再调 <c>GET /v1/mods/{id}/files</c>。官方搜索路径下为空集合。
    /// </summary>
    public IReadOnlyList<CurseForgeFile> Files { get; init; } = Array.Empty<CurseForgeFile>();

    /// <summary>项目网页地址。整合包用 modpacks 段，其余按内容类型分。</summary>
    public string WebUrl => IsModpack
        ? $"https://www.curseforge.com/minecraft/modpacks/{Slug}"
        : Kind switch
        {
            ContentKind.ResourcePack => $"https://www.curseforge.com/minecraft/texture-packs/{Slug}",
            ContentKind.ShaderPack => $"https://www.curseforge.com/minecraft/shaders/{Slug}",
            _ => $"https://www.curseforge.com/minecraft/mc-mods/{Slug}",
        };
}

/// <summary>
/// 一个可下载的文件，对应 <c>GET /v1/mods/{modId}/files</c> 的 <c>data[]</c> 元素。
/// </summary>
public sealed class CurseForgeFile
{
    public required long Id { get; init; }

    public required long ModId { get; init; }

    /// <summary>落盘用的文件名。</summary>
    public required string FileName { get; init; }

    /// <summary>面向人的显示名，可能为空（服务端允许为空字符串）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 直链。<b>可能为 null</b>：作者关闭第三方分发时 CurseForge 不返回地址，
    /// 此时只能让用户去网页下载。
    /// </summary>
    public string? DownloadUrl { get; init; }

    public long FileLength { get; init; }

    public DateTimeOffset? FileDate { get; init; }

    /// <summary>1 = Release，2 = Beta，3 = Alpha。</summary>
    public int ReleaseType { get; init; } = 1;

    /// <summary>该文件声明的游戏版本，例如 <c>1.20.1</c>、<c>Forge</c>、<c>Fabric</c>。</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    /// <summary>依赖关系。<c>RelationType == 3</c> 是前置信（RequiredDependency）。</summary>
    public IReadOnlyList<CurseForgeDependency> Dependencies { get; init; } = Array.Empty<CurseForgeDependency>();

    /// <summary>由 <c>hashes[]</c> 里 <c>algo == 1</c>（SHA1）取出的校验值；没有为 null。</summary>
    public string? Sha1 { get; init; }

    /// <summary>数据来源。cfwidget（项目链接解析）不提供哈希，<see cref="Sha1Available"/> 为 false。</summary>
    public CurseForgeSource Source { get; init; } = CurseForgeSource.OfficialApi;

    /// <summary>是否有可用的 SHA1 校验值。为 false 时安装流程跳过校验（而不是报错）。</summary>
    public bool Sha1Available => !string.IsNullOrWhiteSpace(Sha1);

    public bool IsRelease => ReleaseType == 1;

    public bool IsBeta => ReleaseType == 2;

    /// <summary>是否可直连下载。</summary>
    public bool IsDownloadable => !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>面向人的版本标签：显示名优先，退化为文件名。</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? FileName : DisplayName;
}

/// <summary>一条依赖关系。<see cref="RelationType"/> 见 CurseForge <c>FileRelationType</c> 枚举。</summary>
public sealed record CurseForgeDependency(long ModId, int RelationType)
{
    /// <summary>RequiredDependency。</summary>
    public const int Required = 3;

    /// <summary>OptionalDependency。</summary>
    public const int Optional = 2;

    /// <summary>Incompatible。</summary>
    public const int Incompatible = 5;

    public bool IsRequired => RelationType == Required;

    public bool IsIncompatible => RelationType == Incompatible;
}

/// <summary>
/// <c>POST /v1/fingerprints</c> 的一条命中结果，对应 <c>data.exactMatches[]</c> 元素。
///
/// 关联必须以 <see cref="Fingerprint"/> 为准：本地 jar 可能被用户改过名，而「同名不同文件」
/// 在 <c>mods/</c> 里也很常见。<see cref="FileName"/> 只作为拿不到指纹时的兼容兜底。
/// </summary>
public sealed class CurseForgeFingerprintMatch
{
    /// <summary>命中的文件 Id（<c>exactMatches[].id</c>）。</summary>
    public long FileId { get; init; }

    /// <summary>命中的项目 Id（<c>file.modId</c>）。</summary>
    public long ModId { get; init; }

    /// <summary>命中的文件名。<b>不等于</b>被命中的本地文件名，不可用于身份判定。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// 命中的 CurseForge 指纹（<c>file.fileFingerprint</c>），与本地
    /// <see cref="ModFileHashes.CurseForgeFingerprint"/> 同一 32 位无符号语义。
    /// 为 0 表示这条响应没带指纹（旧格式 / 精简响应 / 由调用方构造的替身），
    /// 此时才允许退回按文件名关联。
    /// </summary>
    public uint Fingerprint { get; init; }

    /// <summary>该项目最近的文件列表（<c>latestFiles[]</c>），用于判断有没有新版。</summary>
    public IReadOnlyList<CurseForgeFile> LatestFiles { get; init; } = Array.Empty<CurseForgeFile>();
}
