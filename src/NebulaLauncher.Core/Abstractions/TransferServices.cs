namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 配置包（收藏 + 服务器 + 白名单设置）的导出与导入。
///
/// 存在意义：换机器时用户真正想带走的是「我收藏了哪些版本、常去哪个服、我偏好什么外观」，
/// 而不是 <c>&lt;data&gt;/</c> 整目录——整目录里躺着 <c>accounts.json</c> 与令牌。
/// 所以这里的导出是**白名单式**的：manifest 里逐条写清「带走了什么、脱敏了什么」，
/// 没被列出的东西（账户、令牌、日志、崩溃报告）永远不会进包。
///
/// 契约约束（与 <see cref="InstanceServices"/> 同款）：实现方只追加成员，新增成员带默认实现。
/// </summary>
public interface IProfileBundleService
{
    /// <summary>
    /// 导出配置包到 <paramref name="targetDirectory"/>，文件名为
    /// <c>nebula-profile-&lt;yyyyMMdd-HHmmss&gt;.zip</c>。同名文件已存在时追加 <c>-2</c>、<c>-3</c>，不覆盖旧包。
    /// 返回包内计数与脱敏字段清单，供 UI 直接展示。
    /// </summary>
    Task<ProfileBundleExportResult> ExportAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只读检查一个配置包：校验 <c>manifest.json</c> 与版本号，读出计数。
    /// 不写入任何文件，用于「导入前先展示摘要」。
    /// </summary>
    Task<ProfileBundleInspection> InspectAsync(
        string bundlePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 导入配置包。
    ///
    /// <paramref name="includeSettings"/> 默认 false：设置里混着机器相关的字段（游戏目录、Java 路径），
    /// 默认合并会在新机器上直接写进指向旧机器的路径。为 true 时也只覆盖白名单内的键。
    ///
    /// 服务器与收藏按 key 合并，不删除现有条目；包内同 key 的条目覆盖本地。
    /// 账户与令牌不论包内有什么都不会被读取。
    /// </summary>
    Task<ProfileBundleImportResult> ImportAsync(
        string bundlePath,
        bool includeSettings = false,
        CancellationToken cancellationToken = default);
}

/// <summary>一次导出的结果。<see cref="FilePath"/> 是写好的 zip 绝对路径。</summary>
public sealed record ProfileBundleExportResult(
    string FilePath,
    int FavoriteCount,
    int HiddenCount,
    int ServerCount,
    int SettingCount,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> RedactedFields);

/// <summary>
/// 导入前的只读摘要。<see cref="IsValid"/> 为 false 时 <see cref="Error"/> 是给用户看的中文原因。
/// </summary>
public sealed record ProfileBundleInspection(
    bool IsValid,
    string? Error,
    DateTimeOffset? ExportedAt,
    string? LauncherVersion,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> RedactedFields,
    int FavoriteCount,
    int HiddenCount,
    int ServerCount,
    int SettingCount)
{
    /// <summary>包里有几项内容会被写进本地。0 表示空包。</summary>
    public int TotalItems => FavoriteCount + HiddenCount + ServerCount + SettingCount;

    public static ProfileBundleInspection Rejected(string error) =>
        new(false, error, null, null, Array.Empty<string>(), Array.Empty<string>(), 0, 0, 0, 0);
}

/// <summary>
/// 一次导入的结果。计数是「包内实际被应用」的条目数：覆盖同 key 的条目也算导入。
/// </summary>
public sealed record ProfileBundleImportResult(
    bool IsValid,
    string? Error,
    int Servers,
    int ServersAdded,
    int Favorites,
    int Hidden,
    int Settings,
    IReadOnlyList<string> SettingKeys)
{
    public static ProfileBundleImportResult Rejected(string error) =>
        new(false, error, 0, 0, 0, 0, 0, Array.Empty<string>());

    /// <summary>「N 个服务器 / M 个收藏 / K 个设置项」。UI 的状态栏直接用它。</summary>
    public string Summary => $"{Servers} 个服务器 / {Favorites} 个收藏 / {Settings} 个设置项";
}
