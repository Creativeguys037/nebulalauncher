namespace NebulaLauncher.Core.Abstractions;

/// <summary>本地已安装版本与远程版本清单。</summary>
public interface IVersionService
{
    /// <summary>扫描 <c>versions/</c> 目录。结果按版本号降序。</summary>
    Task<IReadOnlyList<Models.VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>拉取 Mojang 版本清单。带本地缓存，离线时回退缓存。</summary>
    Task<IReadOnlyList<Models.RemoteVersion>> GetRemoteAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>解析单个版本的详情，包含继承链合并后的结果。</summary>
    Task<Models.VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default);

    Task DeleteAsync(string versionName, CancellationToken cancellationToken = default);

    /// <summary>记录一次启动，用于「最近使用」排序。</summary>
    Task RecordLaunchAsync(string versionName, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>列出某 Minecraft 版本可用的 Fabric / Quilt 加载器版本。</summary>
    Task<IReadOnlyList<Models.LoaderVersion>> GetLoaderVersionsAsync(
        Models.LoaderKind loader,
        string minecraftVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只下载某个已安装版本的客户端 jar 到指定目录，**不写 <c>versions/</c>、不改 <c>instance.json</c>**。
    ///
    /// 用途是「给别人拷一份客户端」「离线留档」「只想拿同版本的 jar 喂给别的工具」。
    /// 现在只能先安装再拷出去，而安装会顺带补一大堆 libraries 与 assets。
    ///
    /// 只处理 <c>downloads.client</c> 这一个文件：PCL2 的「另存为」
    /// （<c>ModDownloadLib.vb:McDownloadMenuSave</c>）会连版本 JSON 一起落盘，
    /// 这里不做——一个目录里多出一个同名 JSON 只会让人以为那是可安装的版本。
    ///
    /// 默认实现返回失败结果，不抛异常；调用方拿到的是可展示的中文原因。
    /// </summary>
    /// <param name="versionName">本地版本目录名。</param>
    /// <param name="targetDirectory">保存目录；不存在时创建。</param>
    /// <param name="progress">单文件下载进度。</param>
    /// <param name="cancellationToken">取消令牌；取消时半截文件会被删除。</param>
    Task<ClientJarSaveResult> SaveClientJarAsync(
        string versionName,
        string targetDirectory,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ClientJarSaveResult.Fail(versionName, targetDirectory, "当前版本服务未实现「仅下载客户端」"));
}

/// <summary>版本安装：原版与各类模组加载器。</summary>
public interface IInstallService
{
    /// <summary>安装过程中持续回报进度。完成后本地版本目录即出现。</summary>
    /// <param name="request">安装目标。</param>
    /// <param name="progress">聚合进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="fabricApiVersionId">
    /// 可选：随 Fabric 一起装进 <c>mods/</c> 的 Fabric API 的 Modrinth 版本 Id。
    /// 只在 <paramref name="request"/> 的加载器是 Fabric 时有意义；为 null 表示不装。
    /// 为什么走参数而不是加到 <see cref="Models.InstallRequest"/>：那个模型是「装什么游戏」的描述，
    /// 而 Fabric API 是「装完加载器顺便下个模组」，生命周期不同，混进同一份请求会让
    /// 「重装加载器」和「重下模组」两个动作都变得没有明确的输入。
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "CancellationToken 是既有签名的第三参，所有实现与测试桩按位置传它；为了一个可选参数整体移位会让每一处调用的语义都变得依赖位置")]
    Task InstallAsync(
        Models.InstallRequest request,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? fabricApiVersionId = null);

    /// <summary>检查某版本缺少哪些文件，用于「修复」功能。</summary>
    Task<bool> VerifyAsync(string versionName, IProgress<Models.DownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 安装一个加载器版本，并返回装出来的版本目录名。
    ///
    /// 与 <see cref="InstallAsync"/> 的两点差别，正是「给已有实例换加载器」需要的：
    /// 1. **返回版本名**。<see cref="InstallAsync"/> 是 <c>Task</c>，而 Forge / NeoForge 的
    ///    版本目录名由安装器自己决定（<c>1.20.1-forge-47.2.0</c>），调用方无从推算——换加载器
    ///    必须把实例的 <c>VersionName</c> 指到这个真实名字上；
    /// 2. **<paramref name="overwriteExisting"/> 允许覆盖**。正常安装走
    ///    <c>SkipIfAlreadyInstalled = true</c>（装过就跳过），而换加载器的语义是「按这个目标
    ///    重新装一遍」，遇到残缺的同名版本目录必须重装而不是跳过。
    ///
    /// 默认实现退化为调 <see cref="InstallAsync"/> 并返回 null（无法确定版本名）——
    /// 「服务没实现」由调用方检查返回值判定，不会静默成功。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "与既有 InstallAsync 的既有约定一致：CancellationToken 保持在靠前位置，可选参数追加在它之后")]
    async Task<string?> InstallLoaderAsync(
        Models.InstallRequest request,
        IProgress<Models.DownloadProgress>? progress = null,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        await InstallAsync(request, progress, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// 按档位修复一个版本。
    ///
    /// <see cref="Models.RepairDepth.Quick"/> 只补缺失文件与 0 字节 jar（复用安装流程）；
    /// <see cref="Models.RepairDepth.Deep"/> 额外逐文件校验 libraries 与 assets 的 SHA1。
    /// 默认实现退化为「补缺失文件」，即与 <see cref="VerifyAsync"/> 等价，不抛异常。
    /// </summary>
    async Task<Models.RepairReport> RepairAsync(
        string versionName,
        Models.RepairDepth depth,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 默认退化为「补缺失文件」：没有专门实现时行为与 VerifyAsync 一致，不会静默什么都不做。
        var ok = await VerifyAsync(versionName, progress, cancellationToken).ConfigureAwait(false);

        return ok
            ? Models.RepairReport.Empty
            : new Models.RepairReport(0, 0, 0, Array.Empty<string>(), RepairMessages.NotVerified);
    }

    /// <summary>当前下载源。</summary>
    Models.DownloadSource Source { get; set; }
}

/// <summary>接口默认实现用的固定文案。放常量数组是为了避免每次调用都新建数组（CA1861）。</summary>
internal static class RepairMessages
{
    internal static readonly string[] NotVerified = ["该版本未通过校验，请在版本页重试或重新安装。"];
}

/// <summary>
/// 一次「仅下载客户端 jar」的结果。
///
/// 放在契约文件里而不是 <c>Models/</c>：它只服务 <see cref="IVersionService.SaveClientJarAsync"/>，
/// 与 <c>ServerDownloadResult</c> 同一个理由——独立出来不让内容 / 备份模块的模型改动牵连到它。
/// </summary>
public sealed class ClientJarSaveResult
{
    /// <summary>来源版本目录名。</summary>
    public required string VersionName { get; init; }

    /// <summary>目标文件路径（即使失败也会填，让用户知道尝试写到了哪里）。</summary>
    public required string FilePath { get; init; }

    /// <summary>落盘后的实际大小；0 表示没写成功。</summary>
    public long Size { get; init; }

    /// <summary>实际计算出的 SHA1；失败或未下载时为 null。</summary>
    public string? Sha1 { get; init; }

    /// <summary>目标文件已存在且 SHA1 对得上，本次没有下载。</summary>
    public bool AlreadyExisted { get; init; }

    public bool Success { get; init; }

    /// <summary>失败原因（中文，可直接展示）。</summary>
    public string? Error { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>构造一个失败结果。成功路径由实现方直接填各个字段。</summary>
    public static ClientJarSaveResult Fail(string versionName, string targetDirectory, string error) => new()
    {
        VersionName = versionName ?? string.Empty,
        FilePath = string.IsNullOrWhiteSpace(targetDirectory)
            ? string.Empty
            : Path.Combine(targetDirectory, (versionName ?? string.Empty) + ".jar"),
        Success = false,
        Error = error,
    };
}
