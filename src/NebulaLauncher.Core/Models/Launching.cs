using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Core.Models;

/// <summary>一次游戏启动的完整参数。由 ViewModel 构造，<c>IGameLauncher</c> 消费。</summary>
public sealed class LaunchOptions
{
    public required Account Account { get; init; }

    public required string VersionName { get; init; }

    /// <summary>游戏目录；null 表示使用默认 <c>.minecraft</c>。</summary>
    public string? GameDirectory { get; init; }

    /// <summary>Java 可执行文件路径；null 表示自动选择。</summary>
    public string? JavaPath { get; init; }

    /// <summary>
    /// 这次要启动的加载器自身版本号（Forge build <c>36.2.25</c> / NeoForge <c>20.2.62-beta</c> /
    /// Fabric Loader <c>0.16.9</c>）；null 表示不知道。
    ///
    /// <b>为什么它必须跟着 <see cref="LaunchOptions"/> 走，而不是作为 <c>LaunchAsync</c> 的参数</b>：
    /// 前置检查里的 Java 上限判定发生在启动流程内部（<c>GameLauncher.CheckJavaPreflight</c>），
    /// 那里只拿得到 <see cref="LaunchOptions"/>。做成方法参数就得让「首页 / 版本页启动 /
    /// 版本页导出脚本 / 服务器页」四个入口各传一遍，而合成规则只允许存在一份——
    /// 少传一个入口的表现是「同一个实例在 A 页会因为 Java 太新被拦下、在 B 页直接崩」，
    /// 这类偏差不报错，只悄悄分叉。放进这里，工厂是唯一填它的地方。
    ///
    /// null 时 Java 上限判定退回「只按游戏版本 + 加载器种类」，与加这个字段之前完全一致。
    /// </summary>
    public string? LoaderVersion { get; init; }

    public int MinMemoryMb { get; init; } = 512;

    public int MaxMemoryMb { get; init; } = 4096;

    public int WindowWidth { get; init; } = 854;

    public int WindowHeight { get; init; } = 480;

    public bool Fullscreen { get; init; }

    /// <summary>启动后直连的服务器，格式 <c>host[:port]</c>。</summary>
    public string? ServerAddress { get; init; }

    /// <summary>额外 JVM 参数，空格分隔，允许包含 <c>-Dkey=value</c>。</summary>
    public string? ExtraJvmArguments { get; init; }

    /// <summary>追加到游戏参数末尾的原始参数。</summary>
    public string? ExtraGameArguments { get; init; }

    /// <summary>
    /// 实例级版本信息（<c>--versionType</c>，PCL2 <c>VersionArgumentInfo</c>），
    /// 显示在 Minecraft 主菜单与 F3 屏上。
    ///
    /// <b>三态，两层默认各自有意义：</b>
    /// <list type="bullet">
    /// <item><c>null</c> = 本实例没设置，由 <see cref="NebulaLauncher.Core.Launching.GameLauncher"/>
    /// 回落到全局 <see cref="AppSettings.VersionArgumentInfo"/>。</item>
    /// <item><c>""</c>（空串）= 本实例明确要求「不显示版本名」，连全局值也一起压掉。
    /// 少了这一层，用户在实例上清空输入框只会退回全局值，永远关不掉全局设置。</item>
    /// <item>其它字符串 = 直接用这个值。</item>
    /// </list>
    ///
    /// 这里不提前解析全局值：调用方填进来的是「实例的原始意图」，全局设置在启动那一刻
    /// 才读得到最新值，提前解析会把「实例没设置」固化成启动页打开时的旧值。
    /// </summary>
    public string? VersionArgumentInfo { get; init; }

    /// <summary>
    /// 实例级第三方验证服务器地址（PCL2 <c>VersionServerAuthServer</c>）。
    ///
    /// <b>三态，且空串有独立含义：</b>
    /// <list type="bullet">
    /// <item><c>null</c> = 本实例没设置，由 <see cref="NebulaLauncher.Core.Launching.GameLauncher"/>
    /// 回落到 <see cref="Account.AuthServerUrl"/>。</item>
    /// <item><c>""</c>（空串或纯空白）= 本实例明确要求不注入 authlib-injector，
    /// 即使账户是第三方验证账户也裸连。</item>
    /// <item>其它字符串 = 用这个地址覆盖账户值。</item>
    /// </list>
    ///
    /// 叫 <c>Instance*</c> 而不是直接叫 <c>AuthServerUrl</c>：<see cref="Account.AuthServerUrl"/>
    /// 就在同一个对象里，同名字段读代码时极易误判成「账户那个」。
    /// </summary>
    public string? InstanceAuthServerUrl { get; init; }

    /// <summary>是否以 Demo 模式启动。</summary>
    public bool DemoMode { get; init; }

    /// <summary>
    /// 是否按本机可用内存自动推算 <see cref="MaxMemoryMb"/>（PCL2 <c>LaunchArgumentRam</c>）。
    ///
    /// 调用方必须在「实例没有显式设置内存」时才传 true：用户给某个大整合包单独调过值，
    /// 被自动推荐悄悄覆盖是最难排查的一类配置问题。
    /// </summary>
    public bool AutoMaxMemory { get; init; }

    /// <summary>
    /// 垃圾回收器偏好。由调用方按「实例覆盖 &gt; 全局」解析后传入；
    /// <see cref="GcKind.Auto"/> 时不追加任何 GC 参数。
    /// </summary>
    public GcKind GcKind { get; init; } = GcKind.Auto;

    /// <summary>
    /// 游戏窗口标题模板，支持 <c>{version}</c> / <c>{user}</c> / <c>{date}</c> / <c>{time}</c>。
    /// null 表示不改标题。非 Windows 平台忽略。
    /// </summary>
    public string? WindowTitle { get; init; }

    /// <summary>窗口出现后自动最大化。</summary>
    public bool MaximizeWindow { get; init; }

    /// <summary>启动前执行的命令行；null 表示跳过。</summary>
    public string? LaunchBeforeCommand { get; init; }

    /// <summary>退出后执行的命令行；null 表示跳过。</summary>
    public string? LaunchAfterCommand { get; init; }

    /// <summary>启动前/后命令是否阻塞等待。</summary>
    public bool WaitForLaunchCommands { get; init; } = true;

    /// <summary>
    /// 游戏进程优先级（PCL2 <c>LaunchArgumentPriority</c>：0 高 / 1 中 / 2 低）。
    /// 设置失败只写日志，不影响启动。
    /// </summary>
    public GameProcessPriority ProcessPriority { get; init; } = GameProcessPriority.Normal;

    /// <summary>
    /// 启动前把 <c>options.txt</c> 的 <c>lang</c> 改成简体中文（PCL2 <c>ToolHelpChinese</c>）。
    ///
    /// 只在文件已存在、且当前语言不是中文时改写；不新建 <c>options.txt</c>，
    /// 因为「第一次启动」时游戏自己会生成一份完整默认值，启动器插一脚反而会丢设置项。
    /// </summary>
    public bool AutoChineseLanguage { get; init; } = true;

    /// <summary>
    /// 崩溃后把 crash-report 与 <c>logs/latest.log</c> 打成一个 zip 归档，
    /// 让用户能把证据一次性发出来（PCL2 <c>ModCrash</c> 的报告归档）。
    /// </summary>
    public bool ArchiveCrashReport { get; init; } = true;

    /// <summary>
    /// 是否注入放宽 Forge / 命令行校验的三条 <c>-D</c>（PCL2 <c>LaunchAdvanceJvm</c> 的默认值）。
    ///
    /// 默认 <c>true</c>：不配置也能装模组，与 <see cref="AppSettings.RelaxForgeChecks"/> 一致。
    /// 值为 <c>false</c>（用户在设置页关掉）时仍保留 <c>-XX:-OmitStackTraceInFastThrow</c>，
    /// 只是不再替用户放宽校验——所以这个开关不能省成「构建器里的常量」。
    /// </summary>
    public bool RelaxForgeChecks { get; init; } = true;
}

/// <summary>
/// 轮询等待一个条件成立，用于「停止游戏」之后复核进程是否真的退出。
///
/// 进程退出只有一个来源：<c>IGameLauncher.IsGameRunning</c>，而它读的是
/// <c>Process.HasExited</c>——只能轮询，没有可订阅的退出事件对外暴露。
/// 首页与版本页都要做同一件事，判定语义必须一样（超时、轮询间隔、最后一次复核），
/// 所以放在这里而不是两个 ViewModel 各写一份。
/// </summary>
public static class ProcessWait
{
    /// <summary>
    /// 反复检查 <paramref name="condition"/> 直到成立或超时；返回超时后的最后一次判定，
    /// 因此调用方拿到 <c>false</c> 就是「确实还没退出」，不是「没轮到检查」。
    /// </summary>
    public static async Task<bool> UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        int pollIntervalMs = 100)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(Math.Max(1, pollIntervalMs)).ConfigureAwait(false);
        }

        // 最后一次复核：等待期间进程可能刚好退出，不能用循环里读到的旧值下结论。
        return condition();
    }
}

/// <summary>游戏进程优先级。数值不影响序列化，顺序只是可读性。</summary>
public enum GameProcessPriority
{
    /// <summary>正常优先级（默认）。</summary>
    Normal = 0,

    /// <summary>高于正常：帧生成更稳，代价是抢占启动器与后台程序。</summary>
    High = 1,

    /// <summary>低于正常：给录屏 / 直播留出 CPU。</summary>
    Low = 2,
}

/// <summary>
/// 设置里的优先级字符串 → 强类型。首页 / 版本页 / 服务器页三处都要构造启动选项，
/// 解析规则只能有一份，否则「设置页选高、某个入口还是正常」这种不一致极难发现。
/// </summary>
public static class GameProcessPriorityParser
{
    /// <summary>大小写与空白不敏感；未知值一律按「正常」，坏配置不该影响启动。</summary>
    public static GameProcessPriority Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => GameProcessPriority.High,
        "low" => GameProcessPriority.Low,
        _ => GameProcessPriority.Normal,
    };
}

/// <summary>启动结果。进程退出后由 <c>IGameLauncher</c> 回填。</summary>
/// <param name="CrashAnalysis">已格式化的中文结论；UI 直接绑定这一段。</param>
/// <param name="CrashReport">结构化分析结果，含报告文件路径，供「打开崩溃报告」等按钮使用。</param>
/// <param name="MissingJavaMajor">
/// 因「没有可用的 Java」而失败时，这次启动需要的主版本号；其余失败路径为 null。
///
/// 只给文案是不够的：用户看到「需要 Java 25」之后还得自己找到设置页、选版本、等下载。
/// 带上主版本号，界面才能在失败处直接给「安装 Java 25」按钮（<c>JavaProvisionPlanner</c> 判可否一键装）。
/// </param>
public sealed record LaunchResult(
    bool Started,
    int ExitCode,
    TimeSpan Duration,
    string? FailureReason = null,
    string? CrashAnalysis = null,
    CrashReport? CrashReport = null,
    int? MissingJavaMajor = null)
{
    /// <summary>
    /// 本次崩溃读到的 hs_err 文本，每个元素对应一份文件，并以
    /// <c>===== &lt;文件名&gt; =====</c> 开头（导出时原样拼进 <c>hs_err.txt</c>）。
    ///
    /// 非原生崩溃、启动失败、取消等没有 hs_err 的路径是空集合。
    /// 用属性而不是构造参数追加：既保持已有 `new LaunchResult(...)` 调用不变，又保证默认值不是 null。
    /// </summary>
    public IReadOnlyList<string> HsErrLogs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Core 的最终退出判定（<see cref="GameWatcher.JudgeExit"/> 的结果）。只有「进程真正退出」
    /// 这条路径有值：启动失败、取消、等待移交后台都是 null。
    ///
    /// UI 必须用它而不是退出码决定「正常退出 / 崩溃」：<c>exit 0</c> 也可能是在加载到可玩阶段前
    /// 被外部结束（判崩溃），2012 年前的老版本正常关闭也可能返回非零码（判正常）。
    /// 以前三个入口各自看 <c>ExitCode</c>，就会把这两种情况报反。
    /// </summary>
    public GameExitVerdict? Verdict { get; init; }

    /// <summary>
    /// 启动等待被取消、但游戏进程仍在运行：会话与实例锁已移交给后台，退出收尾由后台完成。
    ///
    /// 调用方看到它为 true 时不要按「失败 / 异常退出」渲染，也不要刷新成空闲状态——
    /// 游戏还在跑，停止入口仍然可用。
    /// </summary>
    public bool Detached { get; init; }

    /// <summary>
    /// 本次启动的会话标识。只有真正起来过进程的路径有值。
    ///
    /// 用途：Detach 之后后台会话退出时会发 <c>SessionExited</c> 事件，界面靠它对上
    /// 「这条退出结果属于哪一次启动」——用户可能已经发起了下一次启动，旧会话的结果不能
    /// 覆盖新面板（REVIEW-R34-R36 P3-3/P3-14）。
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>启动过程中的实时状态，供 UI 绑定。</summary>
public enum LaunchPhase
{
    Preparing = 0,
    CheckingFiles = 1,
    DownloadingAssets = 2,
    StartingProcess = 3,
    Running = 4,
    Exited = 5,
    Failed = 6,
}

/// <summary>启动器向前端推送的进度快照。</summary>
public sealed record LaunchProgress(
    LaunchPhase Phase,
    string Message,
    double Ratio = 0,
    long SpeedBytesPerSecond = 0,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    GameStartupStage StartupStage = GameStartupStage.None);

/// <summary>
/// 游戏客户端的启动阶段。抄 PCL2 <c>ModWatcher.GameLog</c> 的 5 级里程碑，
/// 用日志特征判断「游戏到底走到哪一步」，而不是只看进程还活着。
///
/// 用途有两个：
/// 1. 首页在游戏启动过程中显示「正在加载材质（4/5）」这类可读进度；
/// 2. 退出时区分「加载到一半就没了」与「进过主菜单后正常退出」——
///    前者一定是崩溃，后者 exitCode 为 0 时不该报崩溃。
/// </summary>
public enum GameStartupStage
{
    /// <summary>尚未产出任何可识别的日志。</summary>
    None = 0,

    /// <summary>进程已拉起。</summary>
    Launched = 1,

    /// <summary>出现 <c>Setting user:</c>，账号已注入。</summary>
    UserSet = 2,

    /// <summary>出现 LWJGL 版本行，图形栈已初始化。</summary>
    LwjglReady = 3,

    /// <summary>OpenAL / SoundSystem 就绪。</summary>
    AudioReady = 4,

    /// <summary>材质 atlas 加载完成。</summary>
    TexturesLoaded = 5,

    /// <summary>观察到游戏窗口，视为已进主菜单。</summary>
    MainMenu = 6,
}

/// <summary>崩溃日志分析结论。</summary>
/// <param name="CrashReportPath">本次归因实际读到的 crash report 文件路径；没有定位到文件时为 null。</param>
public sealed record CrashReport(
    bool IsCrash,
    string Summary,
    IReadOnlyList<CrashSuggestion> Suggestions,
    string? RawTail = null,
    string? CrashReportPath = null);

/// <summary>一条崩溃成因提示。</summary>
/// <param name="ActionId">
/// 可执行动作的标识，取值见 <c>CrashAnalyzer.Actions</c>：
/// <c>OpenJavaSettings</c> / <c>OpenModsFolder</c> / <c>RepairVersion</c> /
/// <c>OpenCrashReport</c> / <c>OpenLogFolder</c> / <c>DisableMod</c>。没有对应动作时为 null。
/// </param>
/// <param name="ActionArgument">
/// 动作的参数。<c>DisableMod</c> 传要禁用的 jar 文件名（不含目录）；
/// 其余动作为 null——它们的落点由启动器自己决定，调用方不需要给。
/// </param>
public sealed record CrashSuggestion(
    string Title,
    string Detail,
    CrashSeverity Severity,
    string? ActionId = null,
    string? ActionArgument = null);

public enum CrashSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// 从 <c>hs_err_pid*.log</c> 里抽出的原生层崩溃关键行。
///
/// hs_err 由 JVM 自己写，结构与 crash report 完全不同：真正有价值的是
/// <c>Problematic frame</c> 那一行，它是唯一能区分「显卡驱动 / 输入法钩子 / JVM 自身」的证据。
/// 所有字段都可能是 null：报告被截断时只有文件头，没有 Problematic frame。
/// </summary>
/// <param name="Signal">信号或异常类型，例如 <c>SIGSEGV</c> / <c>EXCEPTION_ACCESS_VIOLATION</c> / <c>Internal Error</c>。</param>
/// <param name="ProblematicFrame">Problematic frame 段的原始行，例如 <c>C  [nvoglv64.dll+0x9a1234]</c>。</param>
/// <param name="NativeModule">帧方括号里的模块名，例如 <c>nvoglv64.dll</c>；纯 Java 帧或没有该段时为 null。</param>
public sealed record HsErrInfo(
    string? Signal,
    string? ProblematicFrame,
    string? NativeModule);

/// <summary>
/// 读取外部崩溃报告（用户手动导入的 txt / log / zip）的结果。
/// 读取失败不抛异常：<see cref="Error"/> 写明原因，由调用方直接展示给用户。
/// </summary>
/// <param name="FileName">来源标识；zip 里取出的报告形如 <c>foo.zip / crash-2026-10-22.txt</c>。</param>
/// <param name="Text">已按头 1500 行 + 尾 500 行截断的正文；读取失败时为空串。</param>
/// <param name="Truncated">是否因为行数过多做过截断。</param>
/// <param name="Error">读取失败的原因；成功时为 null。</param>
public sealed record ExternalReportContent(
    string FileName,
    string Text,
    bool Truncated,
    string? Error = null)
{
    /// <summary>是否成功读到可分析的正文。</summary>
    public bool IsSuccess => Error is null && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// 导出崩溃报告时附带的模组快照。字段刻意保持最小：接收方只需要知道
/// 「装了哪些文件、哪些当时是禁用态、各自多大、版本号是什么」。
/// </summary>
/// <param name="FileName">mods 目录里的文件名（含 <c>.jar.disabled</c> 扩展名）。</param>
/// <param name="Enabled">是否处于启用态；禁用态实现为 <c>.jar.disabled</c>。</param>
/// <param name="SizeBytes">文件字节数；读不到时为 0。</param>
/// <param name="VersionText">从模组元数据解析出的版本号；解析不到时为 null。</param>
public sealed record ModSnapshot(
    string FileName,
    bool Enabled,
    long SizeBytes,
    string? VersionText = null);

// ==================== 启动前置检查 ====================

/// <summary>前置检查结果的严重度。</summary>
public enum PreflightSeverity
{
    /// <summary>仅供日志，不影响启动。</summary>
    Info = 0,

    /// <summary>能启动，但大概率出问题；推给 UI 的提示条。</summary>
    Warning = 1,

    /// <summary>注定起不来；阻止启动并给出中文原因。</summary>
    Blocker = 2,
}

/// <summary>一条启动前置检查结果。</summary>
/// <param name="Title">短标题，UI 用作提示条的主文本。</param>
/// <param name="Detail">中文原因与建议，含具体路径 / 版本号 / 字节数。</param>
/// <param name="ActionId">
/// 可执行动作标识，与 <see cref="CrashSuggestion.ActionId"/> 同一套取值：
/// <c>OpenJavaSettings</c> / <c>OpenModsFolder</c> / <c>RepairVersion</c> / <c>OpenLogFolder</c>。
/// 没有对应动作时为 null。
/// </param>
public sealed record PreflightItem(
    PreflightSeverity Severity,
    string Title,
    string Detail,
    string? ActionId = null);

/// <summary>一次启动前置检查的汇总。检查项顺序稳定，便于 UI 直接渲染。</summary>
public sealed record PreflightReport(IReadOnlyList<PreflightItem> Items)
{
    /// <summary>是否命中阻断项。true 时调用方不得启动进程。</summary>
    public bool HasBlocker => Items.Any(item => item.Severity == PreflightSeverity.Blocker);

    /// <summary>是否命中警告项。</summary>
    public bool HasWarning => Items.Any(item => item.Severity == PreflightSeverity.Warning);

    /// <summary>空报告，用于服务缺失或检查整体失败时。</summary>
    public static PreflightReport Empty { get; } = new(Array.Empty<PreflightItem>());
}
