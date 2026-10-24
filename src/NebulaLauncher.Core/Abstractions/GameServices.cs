namespace NebulaLauncher.Core.Abstractions;

/// <summary>游戏进程的启动、监控与崩溃分析。</summary>
public interface IGameLauncher
{
    /// <summary>构建最终命令行但不启动，用于「查看启动命令 / 调试」。</summary>
    Task<string> BuildCommandLineAsync(Models.LaunchOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// 补齐文件、启动进程并等待退出。游戏运行期间持续通过 <paramref name="progress"/> 推送状态。
    /// 不抛异常：所有失败通过 <see cref="Models.LaunchResult"/> 返回。
    /// </summary>
    Task<Models.LaunchResult> LaunchAsync(
        Models.LaunchOptions options,
        IProgress<Models.LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>当前是否有游戏在运行（任一实例）。首页与托盘只需要这一个布尔。</summary>
    bool IsGameRunning { get; }

    /// <summary>强制结束本会话启动的<b>全部</b>游戏进程（多开时逐个结束）。</summary>
    void KillCurrentGame();

    /// <summary>
    /// 在跑的会话快照，按启动时刻升序。多开时 UI 靠它区分「哪几个实例在跑」。
    ///
    /// 不含 <c>Process</c>：调用方不该拿到别人的进程句柄。停进程只能走
    /// <see cref="KillGame"/>，这样「只杀本会话 / 本实例」的约束才收得住在实现里。
    /// </summary>
    IReadOnlyList<RunningGameInfo> RunningGames { get; }

    /// <summary>
    /// 某个游戏目录是否已有会话在跑。传游戏目录本身即可，规范化与大小写比较由实现负责
    /// （Windows 路径大小写不敏感，各调用方各拼一遍迟早拼出两个键）。
    /// </summary>
    bool IsInstanceRunning(string instanceKey);

    /// <summary>
    /// 按会话标识结束对应进程。返回 <c>false</c> 表示该会话不存在或已经退出——
    /// 调用方据此刷新界面，而不是把「已经停了」当成失败。
    /// </summary>
    bool KillGame(string sessionId);

    /// <summary>
    /// 把完整启动命令写成可执行脚本（Windows <c>.bat</c> / Unix <c>.sh</c>）。
    ///
    /// 抄 PCL2 的 <c>LatestLaunch.bat</c>：命令行由 <see cref="BuildCommandLineAsync"/> 生成，
    /// 脚本带编码声明、<c>cd</c> 到游戏目录、以及（Windows）末尾 <c>pause</c>，
    /// 用户报错时可以直接把这个文件贴给别人复现。
    ///
    /// 命令里含账户 <c>accessToken</c>，因此默认把它替换成等长的 <c>*</c>；
    /// 需要真正可运行的脚本时传 <paramref name="maskAccessToken"/> = false。
    /// </summary>
    /// <returns>写入成功的绝对路径；构建命令行失败或写盘失败时为 null。</returns>
    Task<string?> ExportLaunchScriptAsync(
        Models.LaunchOptions options,
        string targetPath,
        bool maskAccessToken = true,
        CancellationToken cancellationToken = default);

    /// <summary>实时游戏输出（stdout + stderr 合并）。UI 用于「控制台」面板。</summary>
    event EventHandler<GameOutputLine>? OutputReceived;

    /// <summary>
    /// 「取消等待后移交后台」的会话真正退出时触发，携带完整 <see cref="Models.LaunchResult"/>
    /// （崩溃分析、hs_err、verdict 都在里面）。
    ///
    /// 为什么需要它：<see cref="LaunchAsync"/> 在 Detach 时已经返回，后台收尾的结果没有
    /// 别的通道回到界面——不补这个事件，取消等待后游戏崩溃时首页没有崩溃面板与建议。只在后台移交的会话触发；正常启动路径的结果由
    /// <see cref="LaunchAsync"/> 的返回值带出。
    /// </summary>
    event EventHandler<SessionExitedEventArgs>? SessionExited;
}

/// <summary>后台移交会话的退出结果。<see cref="SessionId"/> 与 <see cref="Models.LaunchResult.SessionId"/> 一致。</summary>
public sealed class SessionExitedEventArgs(string sessionId, Models.LaunchResult result) : EventArgs
{
    public string SessionId { get; } = sessionId;

    public Models.LaunchResult Result { get; } = result;
}

/// <summary>游戏的一行输出。</summary>
public sealed record GameOutputLine(string Text, bool IsError, DateTimeOffset Timestamp)
{
    /// <summary>
    /// 产出这行输出的会话标识。多开时控制台靠它分流；单开时忽略即可。
    /// 用 init 属性而不是构造参数追加，是为了不改既有事件签名——老订阅方一行都不用动。
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// 一个在跑的游戏会话的只读投影。
///
/// 不给 <c>Process</c> / <c>GameWatcher</c>：那两个是启动器自己的状态，
/// 漏出去之后调用方就能绕过 <see cref="IGameLauncher.KillGame"/> 去杀别人的进程。
/// </summary>
/// <param name="SessionId">本次启动的会话标识，按会话停止时用它。</param>
/// <param name="InstanceKey">游戏目录的规范化路径；同一目录只允许一个会话。</param>
/// <param name="InstanceName">实例显示名；界面自己知道真实名字，这里给目录名兜底。</param>
/// <param name="VersionName">版本目录名。</param>
/// <param name="StartedAt">进程启动时刻，用于判定「最近一次启动」。</param>
public sealed record RunningGameInfo(
    string SessionId,
    string InstanceKey,
    string InstanceName,
    string VersionName,
    DateTimeOffset StartedAt)
{
    /// <summary>
    /// 游戏目录 → 比较用的键，与 <see cref="InstanceKey"/> 同形（绝对路径、无尾部分隔符）。
    ///
    /// 公开这条规则是为了让调用方能自己把「我这次要启动的目录」对上 <see cref="IGameLauncher.RunningGames"/>，
    /// 而不必各自拼一遍 <c>TrimEnd</c>——各拼一遍迟早拼出两个键，「同一目录」的判定就漏了。
    /// </summary>
    public static string KeyFor(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 非法路径：宁可判不准也不能在启动路径上抛，原样去掉尾部分隔符返回。
            return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}

/// <summary>
/// 启动前置检查。在拉起游戏进程之前判定「注定起不来 / 大概率出问题」的组合，
/// 让调用方在不启动进程的情况下拿到中文原因与可执行动作。
/// </summary>
public interface IPreflightChecker
{
    /// <summary>
    /// 执行全部检查项。实现必须遵守：不抛异常（外部取消除外）、单项失败跳过、
    /// 整体有时间上限，且物理内存探测不可用时跳过内存项。
    /// </summary>
    /// <param name="options">启动参数，读取 <c>MaxMemoryMb</c> / <c>JavaPath</c> / <c>GameDirectory</c>。</param>
    /// <param name="minecraftVersion">解析后的原版版本号；null 时实现退回 <c>options.VersionName</c>。</param>
    /// <param name="loader">加载器类型。Java 允许区间依赖它，只传原版版本号判不出来。</param>
    /// <param name="loaderVersion">
    /// 加载器自身版本号（Forge build / NeoForge / Fabric Loader）。为空时 Java 上限判定
    /// 退回「只按游戏版本 + 加载器种类」，与没有这个参数时一致。
    /// </param>
    /// <param name="cancellationToken">外部取消标记。</param>
    Task<Models.PreflightReport> CheckAsync(
        Models.LaunchOptions options,
        string? minecraftVersion = null,
        Models.LoaderKind loader = Models.LoaderKind.Vanilla,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Java 运行时探测与下载。</summary>
public interface IJavaService
{
    /// <summary>枚举本机所有可用 Java，按主版本降序。</summary>
    Task<IReadOnlyList<Models.JavaInfo>> DetectAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>校验一个路径是否是有效 Java 并读取版本信息。</summary>
    Task<Models.JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default);

    /// <summary>某 Minecraft 版本需要的最低 Java 主版本。</summary>
    Models.JavaRequirement? GetRequirement(string minecraftVersion);

    /// <summary>
    /// 某 Minecraft 版本 + 加载器允许的 Java 主版本闭区间；null 表示不限。
    ///
    /// 加载器会改变区间：Forge 1.12.2 走 LaunchWrapper，要求 Java ≤ 8；
    /// 1.16.5 Forge 走 MODLAUNCHER_8，要求 Java ≤ 17。只传原版版本号是判不出来的。
    /// </summary>
    (int Min, int Max)? GetAllowedRange(string minecraftVersion, Models.LoaderKind loader);

    /// <summary>
    /// 同上，额外合并「加载器自身小版本」带来的上限。
    ///
    /// Forge 的 build 号、NeoForge 的版本号、Fabric Loader 的版本号会把上界压到比游戏版本更紧的位置
    /// （1.19.4 Forge 45.0.40 只能到 Java 19、Fabric Loader &lt; 0.17.0 只能到 Java 24），
    /// 只按「游戏版本 + 加载器种类」算会漏掉这一层。
    /// <paramref name="loaderVersion"/> 为 null / 空白时结果与上面那个重载完全一致——拿不到版本号
    /// 就退回粗粒度判定，不猜区间。
    ///
    /// <b>给了默认实现而不是抽象成员</b>：这个接口有十来个测试替身，它们不关心加载器小版本，
    /// 让每个替身都补一个转发方法只会把「我实现了吗」变成噪音；默认退回粗粒度重载恰好就是
    /// 「这个实现不知道加载器版本」的正确语义。真实实现 <c>JavaService</c> 覆盖它。
    /// </summary>
    (int Min, int Max)? GetAllowedRange(
        string minecraftVersion,
        Models.LoaderKind loader,
        string? loaderVersion) => GetAllowedRange(minecraftVersion, loader);

    /// <summary>为指定版本挑选最合适的 Java：满足要求且版本号最小，避免高版本 JVM 的兼容问题。</summary>
    Task<Models.JavaInfo?> ResolveForVersionAsync(
        string minecraftVersion,
        string? preferredPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 带加载器的挑 Java。能拿到 <see cref="Models.LoaderKind"/> 的调用方应当用这个重载：
    /// 不带加载器的版本会按版本目录名去猜，猜命名不如直接传准。
    /// </summary>
    Task<Models.JavaInfo?> ResolveForVersionAsync(
        string minecraftVersion,
        Models.LoaderKind loader,
        string? preferredPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>下载并解压一个 Java 运行时，返回解压后的 java 可执行文件路径。</summary>
    Task<string?> InstallRuntimeAsync(
        Models.JavaRuntimePackage package,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载并安装指定主版本的 Java 运行时（Adoptium Temurin JRE），返回可执行文件路径；失败返回 null。
    ///
    /// 用于「本机没有满足要求的 Java」：26.x 需要 Java 25，而绝大多数机器只装了 8/17/21。
    /// </summary>
    Task<string?> DownloadRuntimeAsync(
        int majorVersion,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    // ---------------- 手动管理（用户数据） ----------------
    //
    // 下面五个成员是「用户自己管 Java 列表」的能力，对应 PCL2 ModJava 的
    // ManuallyImportJava / ManuallyRemoveJava / JavaRemovedList。
    // 默认实现全部是空操作：契约先落地，实现方（JavaService）后跟上，中间态必须可编译。

    /// <summary>
    /// 把一个本机 Java 加入候选列表。接受 <c>java.exe</c> 或 JDK 根目录；探测失败返回 null。
    ///
    /// 成功后该 Java 排在探测结果最前，并从排除列表里撤销——否则「先移除再添加」会得到一个
    /// 永远不出现在结果里的条目。
    /// </summary>
    Task<Models.JavaInfo?> AddManualAsync(string javaPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<Models.JavaInfo?>(null);

    /// <summary>把一条 Java 移出候选列表，并记进排除列表（下次探测不再出现）。</summary>
    Task RemoveAsync(string javaPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// 当前排除列表（用户数据，落盘在数据根目录的 <c>java-excluded.json</c>）。
    /// 按写入顺序返回，归一化后的绝对路径。只读快照，改它不会影响落盘。
    /// </summary>
    Task<IReadOnlyList<string>> GetExcludedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// 从排除列表里撤销一条（「已忽略」的一键恢复）。返回是否真的移除了记录。
    /// 只改排除列表，**不重新探测**：调用方拿到 true 之后自己决定什么时候刷新候选。
    /// </summary>
    Task<bool> RestoreAsync(string javaPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>清空排除列表并重新扫描一次（被忽略的 Java 全部回到候选里）。</summary>
    Task ClearExcludedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
