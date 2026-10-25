using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 基于 CmlLib.Core 的游戏启动器：补齐文件 → 建进程 → 转发输出 → 等退出 → 崩溃归因。
/// 对外约定是不抛异常，所有失败都通过 <see cref="LaunchResult"/> 返回。
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    private readonly ILauncherPaths _paths;
    private readonly IAppLog _log;
    private readonly IJavaService _java;
    private readonly IVersionService _versions;
    private readonly IContentService? _content;
    private readonly IPlatformService? _platform;
    private readonly IAuthlibInjectorService? _authlib;
    private readonly IBackupService? _backups;

    /// <summary>
    /// 只有「启动前把 Java 注册为高性能 GPU」这一个用途。可空：单元测试的替身不关心注册表，
    /// 缺失时该步骤直接跳过（等同于用户关掉了开关）。
    /// </summary>
    private readonly ISettingsService? _settings;

    /// <summary>
    /// 实例运行锁。可空：单测与临时宿主的替身不关心锁文件，
    /// 缺失时退化成「不做重复启动检查」，而不是把游戏拦下来。
    /// </summary>
    private readonly IInstanceLockService? _instanceLocks;

    /// <summary>
    /// 统一下载链（带镜像重写与换源）。可空：单测与探针宿主没有下载源状态机，
    /// 缺失时启动补全退回 CmlLib 默认 HttpClient——但生产路径必须注入，
    /// 否则设置里的代理 / 下载源对「启动前补齐文件」不生效。
    /// </summary>
    private readonly DownloadHttpClientFactory? _http;

    /// <summary>
    /// 在跑的会话集合。锁在它内部，所以 <see cref="GameLauncher"/> 不再自己维护 _sync：
    /// 单开时代那个锁只保护「一个进程 + 一条尾巴」，多开之后保护面变成「一张表 + 各自尾巴」，
    /// 拆成独立对象才能让「同目录拒第二次」这条判定被单独测到。
    /// </summary>
    private readonly RunningGameRegistry _running = new();

    /// <summary>
    /// 正在做退出收尾的实例（键与 <see cref="RunningGameRegistry"/> 相同）。
    ///
    /// 进程已退出、<c>_running</c> 已注销，但自动备份 / 崩溃归档 / 退出后命令还在写同一个目录，
    /// 实例锁要到 finally 才释放。这几百毫秒内如果放行启动，用户会撞锁并看到
    /// 「游戏在运行」这种错误文案。入口据此拒绝并说明「正在收尾」。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _finishing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>窗口探测间隔。400 ms 足够让标题在窗口出现后一秒内改掉，也不会占住 CPU。</summary>
    private static readonly TimeSpan WindowProbeInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>窗口探测次数上限：400 ms × 150 ≈ 60 秒，覆盖慢机器加载大型整合包的时间。</summary>
    private const int WindowProbeAttempts = 150;

    /// <summary>
    /// CmlLib 为「空 versionType」拼出的固定片段。只匹配空串形态，绝不会误伤真实值：
    /// 非空值一定是 <c>--versionType "某个值"</c>，引号里非空。
    /// </summary>
    private static readonly Regex EmptyVersionTypePattern = new(
        "\\s*--versionType\\s+\"\"",
        RegexOptions.CultureInvariant);

    /// <summary>1.13+ 使用的语言代码（全小写）；1.1~1.10 要用大写的 <c>zh_CN</c>。</summary>
    private const string ChineseLanguage = "zh_cn";

    /// <summary>Yosbr mod 里表示「忽略默认语言」的标记值。</summary>
    private const string IgnoreLanguage = "none";

    /// <summary>
    /// 版本号解析不出来时按哪个档位处理：13 代表「1.13 及以后」。
    /// 解析失败的是快照（24w14a）或自定义目录名，它们比 1.13 更晚，用大写代码反而会让语言显示成英文。
    /// </summary>
    private const int ModernVersionMinor = 13;

    /// <summary>沿 <c>inheritsFrom</c> 找 <c>releaseTime</c> 的最大层数（加载器版本 → 原版，通常 1–2 层）。</summary>
    private const int VersionInheritDepth = 4;

    public GameLauncher(
        ILauncherPaths paths,
        IAppLog log,
        IJavaService java,
        IVersionService versions,
        IContentService? content = null,
        IPlatformService? platform = null,
        IAuthlibInjectorService? authlib = null,
        ISettingsService? settings = null,
        IInstanceLockService? instanceLocks = null,
        IBackupService? backups = null,
        DownloadHttpClientFactory? http = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _content = content;
        _platform = platform;
        _authlib = authlib;
        _settings = settings;
        _instanceLocks = instanceLocks;
        _backups = backups;
        _http = http;
    }

    /// <inheritdoc />
    public bool IsGameRunning
    {
        // 「任一在跑」：首页与托盘问的是「启动器现在有没有游戏」，
        // 它们不关心是哪个实例——按实例的判定走 IsInstanceRunning。
        get => _running.AnyRunning;
    }

    /// <inheritdoc />
    public IReadOnlyList<RunningGameInfo> RunningGames => _running.Snapshot();

    /// <inheritdoc />
    public bool IsInstanceRunning(string instanceKey)
    {
        // 规范化在 GameLauncher 这一侧做：Core 的调用方（App）只该传目录，
        // 不该知道「键要不要去尾部分隔符」这种实现细节。
        return !string.IsNullOrWhiteSpace(instanceKey)
               && _running.IsInstanceRunning(RunningGameInfo.KeyFor(instanceKey));
    }

    /// <inheritdoc />
    public bool KillGame(string sessionId) => !string.IsNullOrWhiteSpace(sessionId)
                                               && _running.KillBySession(sessionId, _log);

    /// <inheritdoc />
    public event EventHandler<GameOutputLine>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<SessionExitedEventArgs>? SessionExited;

    /// <inheritdoc />
    public async Task<string> BuildCommandLineAsync(
        LaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            // 「查看启动命令」显示的内存必须与真正启动时一致，否则用户照着导出的 .bat 跑会得到不同堆。
            options = ApplyAutoMemory(options, null);

            var (minecraftVersion, loader) = await ResolveMinecraftVersionAsync(options, cancellationToken).ConfigureAwait(false);

            // 「查看命令」也要提前暴露问题，但只记日志、不阻断。
            await RunEnvironmentPreflightAsync(options, minecraftVersion, null, cancellationToken).ConfigureAwait(false);

            var java = await _java
                .ResolveForVersionAsync(minecraftVersion, loader, options.JavaPath, cancellationToken)
                .ConfigureAwait(false);

            if (java is null)
            {
                _log.Warn("构建命令行失败：没有可用的 Java");
                return string.Empty;
            }

            CheckJavaPreflight(options, java, minecraftVersion, loader, null);

            // 「查看启动命令」与导出脚本必须带上注入参数，否则用户照着导出的 .bat 跑，
            // 游戏连不上第三方验证服务器、拿不到角色。失败时返回空串（调用方按构建失败处理）。
            var (authlibRequired, authlibArguments) = await ResolveAuthlibArgumentsAsync(options, null, cancellationToken)
                .ConfigureAwait(false);

            if (authlibRequired && authlibArguments is null)
            {
                _log.Warn($"构建命令行失败：{DescribeAuthlibFailure()}");
                return string.Empty;
            }

            var launcher = MinecraftLauncherFactory.Create(
                BuildMinecraftPath(options), _http?.Client, maxParallel: ResolveMaxParallel());
            var launchOption = BuildLaunchOption(options, java, launcher.MinecraftPath, authlibArguments, ResolveVersionArgumentInfo(options));

            using var process = await launcher
                .BuildProcessAsync(options.VersionName, launchOption, cancellationToken)
                .ConfigureAwait(false);

            return Compose(process.StartInfo.FileName, StripEmptyVersionType(process.StartInfo.Arguments));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"构建命令行失败：{options.VersionName}", ex);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<LaunchResult> LaunchAsync(
        LaunchOptions options,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 多开的准入判据是「这个游戏目录是否已有会话」，不是「全局是否有游戏」。
        // 同一目录仍然只能一个 JVM：内存里的集合挡本进程的并发点击，
        // 实例锁（InstanceLockService）挡另一个启动器实例——两道都不能少。
        var gameDirectory = ResolveGameDirectory(options);
        var instanceKey = RunningGameInfo.KeyFor(gameDirectory);

        if (IsInstanceRunning(instanceKey))
        {
            var reason = $"这个实例已经在运行（{options.VersionName}）。同一个游戏目录不能同时开两个，请先停止它。";
            _log.Warn(reason);
            progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
            return new LaunchResult(false, -1, TimeSpan.Zero, reason);
        }

        if (_finishing.ContainsKey(instanceKey))
        {
            // 上一个会话已经退出，但备份/归档还在写这个目录，锁要到收尾结束才放。
            // 这里必须和「游戏在跑」分开说，否则用户会去任务管理器找一个不存在的东西。
            const string reason = "上一个会话正在收尾（自动备份 / 崩溃归档），请稍候几秒再启动。";
            _log.Info(reason);
            progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
            return new LaunchResult(false, -1, TimeSpan.Zero, reason);
        }

        // 会话标识在这里就定下来：既要给 RunningGame，也要给 App 层「停哪一个」用。
        var sessionId = Guid.NewGuid().ToString("N");

        // 版本目录缺失时 CmlLib 报的是底层文件异常，用户看不出「其实还没装」。
        var versionJson = Path.Combine(_paths.VersionsDirectory, options.VersionName, options.VersionName + ".json");
        if (!File.Exists(versionJson))
        {
            var reason = $"版本 {options.VersionName} 尚未安装（缺少 {versionJson}）。请先在「下载」页选中该版本并安装。";
            _log.Warn(reason);
            progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
            return new LaunchResult(false, -1, TimeSpan.Zero, reason);
        }

        var stopwatch = Stopwatch.StartNew();
        var processStarted = false;

        // 「取消等待」与「停止游戏」是两件事：前者只把界面还回去，后者才杀进程。
        // 移交后台之后，本方法的 finally 不能再注销会话 / 释放实例锁 / 释放进程对象，
        // 否则游戏还在跑、锁却没了——下一个启动者立刻插进来写同一份存档。
        var detached = false;

        // 进程在 try 之外声明：取消等待的 catch 分支要把进程对象交给后台任务，
        // 而 using 声明会在方法返回时立刻 Dispose（后台的 WaitForExitAsync 随即失效）。
        Process? process = null;

        // 退出收尾要用的信息也在 try 之外声明：后台移交路径同样需要它们，
        // 局部变量在 catch 里不可见。
        JavaInfo? java = null;
        var releaseYear = 0;
        var launchStartedUtc = DateTime.UtcNow;

        // 日志监视器同样要跨 catch 使用：移交后台的会话靠它判崩溃与阶段。
        GameWatcher? watcher = null;

        // 会话对象在进程真正 Start 之后才注册进集合，但要先声明在这里：
        // finally 要按引用比较，只清掉自己的那一条（同目录并发时不会误删后来者）。
        RunningGame? runningGame = null;

        // 锁必须在 finally 里释放：LaunchAsync 会一直等到游戏退出，
        // 中途任何一条 return 路径（取消 / 前置检查失败 / 进程起不来）都不能把锁留下。
        IInstanceLockHandle? instanceLock = null;

        try
        {
            progress?.Report(new LaunchProgress(LaunchPhase.Preparing, "正在准备启动参数…"));

            // 自动内存必须放在前置检查之前：否则用户手工填的超大内存会先被判成 Blocker，
            // 而实际上本次根本不会用那个值。
            options = ApplyAutoMemory(options, progress);

            var (minecraftVersion, loader) = await ResolveMinecraftVersionAsync(options, cancellationToken).ConfigureAwait(false);

            // 第 2 步：环境段（不碰 Java，2 秒预算只约束文件系统操作）。
            progress?.Report(new LaunchProgress(LaunchPhase.Preparing, "正在检查启动前置条件…"));

            var environment = await RunEnvironmentPreflightAsync(options, minecraftVersion, progress, cancellationToken)
                .ConfigureAwait(false);

            if (environment.HasBlocker)
            {
                var reason = DescribeBlockers(environment);
                _log.Warn($"前置检查未通过：{reason}");
                progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
                return new LaunchResult(false, -1, stopwatch.Elapsed, reason);
            }

            // 第 3 步：选 Java。失败文案保持原样，它已经写明「需要 Java X，本机只有 Y」。
            progress?.Report(new LaunchProgress(LaunchPhase.Preparing, "正在选择 Java 运行时…"));

            java = await _java
                .ResolveForVersionAsync(minecraftVersion, loader, options.JavaPath, cancellationToken)
                .ConfigureAwait(false);

            if (java is null)
            {
                // 26.x 需要 Java 25、1.20.5+ 需要 21：报错里必须写明具体主版本，
                // 否则用户不知道「设置里明明有两个 Java」为什么还是起不来。
                var requirement = _java.GetRequirement(minecraftVersion);
                var reason = requirement is null
                    ? $"没有找到满足 Minecraft {minecraftVersion} 要求的 Java。请在「设置 → Java」安装或手动指定 Java 路径。"
                    : $"Minecraft {minecraftVersion} 需要 Java {requirement.RequiredMajor} 或更高（{requirement.Reason}），"
                      + $"本机没有找到满足要求的运行时。请在「设置 → Java」下载 Java {requirement.RequiredMajor}，或手动指定路径。";

                _log.Warn(reason);
                progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));

                // 文案之外再带上主版本号：界面据此在失败处直接给「安装 Java N」按钮，
                // 用户不必自己从这段话里抠出版本号再去设置页找。
                var missingMajor = ResolveMissingJavaMajor(minecraftVersion, loader, requirement);
                return new LaunchResult(false, -1, stopwatch.Elapsed, reason, MissingJavaMajor: missingMajor);
            }

            // 第 4 步：Java 段（纯判定，零探测）——1.12.2 Forge + Java 17 这类必崩组合在这里拦下。
            var javaPreflight = CheckJavaPreflight(options, java, minecraftVersion, loader, progress);

            if (javaPreflight.HasBlocker)
            {
                var reason = DescribeBlockers(javaPreflight);
                _log.Warn($"前置检查未通过：{reason}");
                progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
                return new LaunchResult(false, -1, stopwatch.Elapsed, reason);
            }

            _log.Info($"使用 Java {java.Version}：{java.Path}");

            // 第三方验证账户必须带 authlib-injector 才能拿到角色：注入失败就阻断，
            // 否则游戏能起来但一直停在「登录中」，用户看到的现象是「启动器坏了」。
            var (authlibRequired, authlibArguments) = await ResolveAuthlibArgumentsAsync(options, progress, cancellationToken)
                .ConfigureAwait(false);

            if (authlibRequired && authlibArguments is null)
            {
                var reason = DescribeAuthlibFailure();
                _log.Warn(reason);
                progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
                return new LaunchResult(false, -1, stopwatch.Elapsed, reason);
            }

            // 退出判定要用版本发行年（2012 年前的老版本非零退出码是正常行为）。
            // 版本 JSON 在磁盘上，读失败只是拿不到年份，不该阻断启动。
            releaseYear = await ResolveReleaseYearAsync(options, cancellationToken).ConfigureAwait(false);

            // 实例运行锁必须拿在「补齐文件」之前：补齐可能下载几百 MB、耗时几分钟，
            // 而重复点击最容易就发生在这段时间里——那正是两个 JVM 同时写同一份
            // saves/ 与 mods/ 的窗口，坏的是存档。锁的位置由游戏目录决定，
            // 所以必须等目录解析完（实例目录或全局 .minecraft）。
            if (_instanceLocks is not null)
            {
                // 目录在方法入口已经解析过（准入判定要用），这里复用同一个字符串：
                // 再解析一次不产生新信息，却可能因为 options.GameDirectory 被后续步骤改过而漂移。
                instanceLock = await _instanceLocks
                    .TryAcquireAsync(gameDirectory, options.VersionName, cancellationToken)
                    .ConfigureAwait(false);

                if (instanceLock is null)
                {
                    var reason = await DescribeLockConflictAsync(gameDirectory).ConfigureAwait(false);
                    _log.Warn(reason);
                    progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));
                    return new LaunchResult(false, -1, stopwatch.Elapsed, reason);
                }
            }

            progress?.Report(new LaunchProgress(LaunchPhase.CheckingFiles, "正在检查游戏文件…"));

            var launcher = MinecraftLauncherFactory.Create(
                BuildMinecraftPath(options), _http?.Client, maxParallel: ResolveMaxParallel());
            var launchOption = BuildLaunchOption(options, java, launcher.MinecraftPath, authlibArguments, ResolveVersionArgumentInfo(options));

            var fileProgress = new SyncProgress<InstallerProgressChangedEventArgs>(args =>
            {
                var ratio = args.TotalTasks > 0 ? (double)args.ProgressedTasks / args.TotalTasks : 0;
                progress?.Report(new LaunchProgress(
                    LaunchPhase.DownloadingAssets,
                    $"正在补齐文件（{args.ProgressedTasks}/{args.TotalTasks}）：{args.Name}",
                    Math.Clamp(ratio, 0, 1)));
            });

            var byteProgress = new SyncProgress<ByteProgress>(value =>
            {
                var ratio = value.TotalBytes > 0 ? (double)value.ProgressedBytes / value.TotalBytes : 0;
                progress?.Report(new LaunchProgress(
                    LaunchPhase.DownloadingAssets,
                    $"正在下载文件（{value.ProgressedBytes / 1024 / 1024} / {value.TotalBytes / 1024 / 1024} MB）",
                    Math.Clamp(ratio, 0, 1),
                    DownloadedBytes: value.ProgressedBytes,
                    TotalBytes: value.TotalBytes));
            });

            process = await launcher
                .InstallAndBuildProcessAsync(options.VersionName, launchOption, fileProgress, byteProgress, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // 启动前命令：文件已经补齐，此时跑脚本才能看到完整的 mods / config 目录。
            // 失败不影响启动（用户脚本写错不该让游戏打不开），只记日志。
            await RunLaunchCommandAsync(
                    options.LaunchBeforeCommand,
                    "启动前",
                    options,
                    java,
                    exitCode: 0,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new LaunchProgress(LaunchPhase.StartingProcess, "正在启动游戏进程…"));

            // 语言与优先级都必须在进程起来之前/之后各做一次，且都不能阻断启动：
            // 写 options.txt 失败只是回落到游戏自己的语言，设优先级失败只是调度策略不同。
            ApplyChineseLanguage(options, minecraftVersion);

            // 显卡首选项也放在进程起来之前：Windows 只在创建进程时读这个键，
            // 启动之后再写对本次运行无效（PCL2 ModLaunch.vb:1746-1766 同样在 McLaunchPrerun 里做）。
            ApplyGpuPreference(java);

            watcher = new GameWatcher();
            watcher.StageAdvanced += (_, stage) => ReportStartupStage(progress, stage);
            watcher.CrashSignal += (_, line) => _log.Warn($"检测到崩溃特征行：{line}");

            // 会话对象必须在 PrepareProcess 之前建好：输出事件是绑在它身上的
            // （见 PrepareProcess 里的 OutputDataReceived），进程一启动就可能吐日志，
            // 晚一步创建会让最初几行掉进「没有归属」的空档，多开时正是崩溃特征行最常出现的位置。
            runningGame = new RunningGame(
                sessionId,
                instanceKey: instanceKey,
                instanceName: DescribeInstanceName(gameDirectory),
                versionName: options.VersionName,
                process: process,
                watcher: watcher,
                startedAt: DateTimeOffset.Now);

            var session = runningGame;

            PrepareProcess(process, session);

            // 先占位再 Start：入口的准入检查与这里隔着前置检查、选 Java、补齐文件
            // （可能几分钟），两次点击完全可能同时通过入口判定。占位失败说明另一次启动
            // 抢先了，此时进程还没起来，直接放弃即可——不需要杀任何东西。
            if (!_running.TryRegister(session))
            {
                const string conflict = "这个实例已经在运行了（另一次启动抢先了），请先停止它。";
                _log.Warn(conflict);
                progress?.Report(new LaunchProgress(LaunchPhase.Failed, conflict));
                runningGame = null;
                return new LaunchResult(false, -1, stopwatch.Elapsed, conflict);
            }

            // hs_err 是进程运行期才写出来的；用启动时刻过滤，避免把上一次崩溃留下的
            // hs_err_pid*.log 当成这一次的现场（同一目录连续崩两次时最容易误判）。
            launchStartedUtc = DateTime.UtcNow;

            // CmlLib 没有办法「不生成」--versionType，只能在它拼好命令行之后把空值的那两个 token 摘掉；
            // 与 BuildCommandLineAsync 走同一份处理，保证「查看启动命令」显示的就是真正会执行的参数。
            process.StartInfo.Arguments = StripEmptyVersionType(process.StartInfo.Arguments);

            if (!process.Start())
            {
                const string reason = "无法启动 Java 进程，请检查 Java 路径是否有效。";
                _log.Error(reason);
                progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));

                // 进程没起来，占位要立刻撤掉，否则这个实例会被永久判成「运行中」。
                _running.Unregister(session);
                runningGame = null;
                return new LaunchResult(false, -1, stopwatch.Elapsed, reason);
            }

            processStarted = true;

            // 锁的持有者从「启动器」改成「游戏」：启动器退出后重开时，判活看的是游戏 PID，
            // 不会把仍在运行的游戏误判成僵死锁而放行第二个 JVM。
            if (instanceLock is not null
                && !instanceLock.UpdateProcessId(process.Id, TryGetProcessStartUtc(process)))
            {
                _log.Warn("实例锁未能改写为游戏进程：启动器退出后该实例可能被误判为僵死锁");
            }

            // 从这里开始 IsAlive 改看真实进程，占位阶段结束。
            session.MarkStarted();

            ApplyProcessPriority(process, options.ProcessPriority);

            watcher.AdvanceTo(GameStartupStage.Launched);

            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                // 拿不到输出不影响游戏本体，只是控制台面板没有内容。
                _log.Warn("无法接管游戏进程输出", ex);
            }

            // 阶段里程碑由 watcher 的事件推送（StartupStage 有值），这条只负责报 Java 版本：
            // HomeViewModel 会用 StartupStage 覆盖 Detail，这里再带一次会把版本号盖掉。
            progress?.Report(new LaunchProgress(LaunchPhase.Running, $"游戏已启动（Java {java.MajorVersion}）"));

            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var windowTask = TakeOverGameWindowAsync(process, options, watcher, windowCts.Token);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // 进程退出后窗口轮询就没有意义了：立刻收尾，别让它在 LaunchAsync 返回后继续跑。
                windowCts.Cancel();
                await AwaitQuietlyAsync(windowTask).ConfigureAwait(false);
            }

            return await CompleteExitAsync(
                    process, session, options, java, watcher, stopwatch, releaseYear,
                    gameDirectory, launchStartedUtc, progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (process is { } cancelledProcess
                                               && ShouldDetachOnCancel(
                                                   processStarted,
                                                   HasExited(cancelledProcess),
                                                   runningGame is not null))
        {
            // 游戏已经在跑：取消等待不等于停止游戏。用户点「取消」想让界面别卡在启动面板，
            // 进程本身仍然是用户要的——直接杀进程会丢存档，提前放锁会让另一个启动器插进来。
            // 所以把退出收尾移交后台，会话与实例锁一起交给它；本方法立刻返回。
            //
            // 注意顺序：先移交、再置 detached。进度/排程抛异常时 detached 仍为 false，
            // finally 会照常注销并释放锁，而不是留下「游戏在跑、锁已经没了」（REVIEW P3-1）。
            // 也不在这里 Stop 秒表：后台 CompleteExitAsync 要用它记录真实游玩时长（P3-2）。
            _log.Info("已取消等待，游戏继续运行；退出收尾移交后台会话");

            try
            {
                progress?.Report(new LaunchProgress(LaunchPhase.Running, "已取消等待，游戏仍在运行；可随时点「停止游戏」。"));
            }
            catch (Exception ex)
            {
                _log.Warn("取消等待时推送进度失败（不影响移交）", ex);
            }

            DetachRunningGame(
                cancelledProcess,
                runningGame!,
                instanceLock,
                options,
                java,
                watcher!,
                stopwatch,
                releaseYear,
                gameDirectory,
                launchStartedUtc,
                progress);

            detached = true;
            runningGame = null;
            instanceLock = null;

            return new LaunchResult(true, -1, stopwatch.Elapsed)
            {
                Detached = true,
                SessionId = sessionId,
            };
        }
        catch (OperationCanceledException) when (processStarted
                                               && process is { } exitedProcess
                                               && HasExited(exitedProcess)
                                               && runningGame is not null
                                               && java is not null
                                               && watcher is not null)
        {
            // 取消令牌恰好落在进程退出那一刻：此时按「启动已取消」返回 -1 会跳过
            // 崩溃归档、自动备份、退出后命令与时长记录，UI 还会把 -1 误报成「游戏异常退出」。
            // 进程已经退出，走与正常退出相同的收尾。
            _log.Info("取消与进程退出同时发生，按已退出路径完成收尾");
            return await CompleteExitAsync(
                    exitedProcess, runningGame!, options, java!, watcher!, stopwatch, releaseYear,
                    gameDirectory, launchStartedUtc, progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _log.Info("启动已取消");
            progress?.Report(new LaunchProgress(LaunchPhase.Failed, "启动已取消。"));

            return new LaunchResult(
                processStarted,
                -1,
                stopwatch.Elapsed,
                processStarted ? null : "启动已取消。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Error($"启动失败：{options.VersionName}", ex);

            var reason = TranslateStartupException(ex);
            progress?.Report(new LaunchProgress(LaunchPhase.Failed, reason));

            return new LaunchResult(processStarted, -1, stopwatch.Elapsed, reason);
        }
        finally
        {
            if (runningGame is not null)
            {
                _running.Unregister(runningGame);
            }

            // 锁要放到进程退出、崩溃归档与退出后命令都跑完之后再放：
            // 这几步还在写同一个目录，提前放锁会让下一个启动者立刻插进来。
            instanceLock?.Dispose();

            // 收尾结束，入口恢复放行（后台移交的会话由后台 finally 移除）。
            _finishing.TryRemove(RunningGameInfo.KeyFor(gameDirectory), out _);

            // 移交后台的会话自己持有进程对象（后台 finally 里 Dispose）：
            // 这里释放会让后台的 WaitForExitAsync 与输出事件失效。
            if (!detached)
            {
                process?.Dispose();
            }
        }
    }

    /// <summary>
    /// 「取消等待」是否要移交后台。三个条件必须同时成立：
    /// 进程已经 Start 过、此刻还活着、并且确实登记了会话。
    ///
    /// 抽成纯函数是为了能直接测：真实路径要拉起 JVM 才能在取消与退出之间制造竞态，
    /// 这种用例在当前测试宿主里跑不稳，而这条判定错了的后果是「游戏还在跑、实例锁没了」。
    /// </summary>
    internal static bool ShouldDetachOnCancel(bool processStarted, bool processExited, bool hasSession) =>
        processStarted && !processExited && hasSession;

    /// <summary>游戏进程的启动时间（UTC）。取不到时退回当前时间：与 PID 一起只为排除 PID 复用。</summary>
    private static DateTimeOffset TryGetProcessStartUtc(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// 进程退出后的完整收尾：取退出码 → 判崩溃 → 归档 hs_err / crash report →
    /// 自动备份 → 退出后命令 → 退出进度 → 组装结果。
    ///
    /// 正常退出与「取消等待后移交后台」两条路径必须共用这一份：分开写会让
    /// 「取消等待」这一局跳过存档备份与崩溃归档，而这恰恰是用户最需要现场的时候。
    /// </summary>
    private async Task<LaunchResult> CompleteExitAsync(
        Process process,
        RunningGame session,
        LaunchOptions options,
        JavaInfo java,
        GameWatcher watcher,
        Stopwatch stopwatch,
        int releaseYear,
        string gameDirectory,
        DateTime launchStartedUtc,
        IProgress<LaunchProgress>? progress)
    {
        // WaitForExitAsync 不保证输出事件已排空：判崩溃前补一次同步等待，
        // 否则最后几行（往往正是崩溃特征行）可能还在读取线程手里，导致漏判。
        WaitForOutputDrain(process);

        // 从这里到调用方释放锁之前，同一实例的启动会被入口拒绝并提示「正在收尾」。
        _finishing[RunningGameInfo.KeyFor(gameDirectory)] = 0;

        stopwatch.Stop();

        var exitCode = TryGetExitCode(process);
        _log.Info($"游戏退出，退出码 {exitCode}，运行 {stopwatch.Elapsed}");

        try
        {
            await _versions
                .RecordLaunchAsync(options.VersionName, stopwatch.Elapsed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("记录启动时长失败", ex);
        }

        string? crashAnalysis = null;
        CrashReport? crashReport = null;
        IReadOnlyList<string> hsErrLogs = Array.Empty<string>();

        // 双判据：日志关键词优先，其次「没走到可玩阶段就退出」，最后才是退出码 + 发行年。
        var stage = watcher.Phase;
        var signal = watcher.CrashDetected
            ? GameLogSignal.Crashed
            : watcher.CloseRequested ? GameLogSignal.Closing : GameLogSignal.None;
        var verdict = GameWatcher.JudgeExit(exitCode, stage, releaseYear, signal);

        if (verdict == GameExitVerdict.Crashed)
        {
            _log.Warn($"判定为崩溃：阶段 {stage}，退出码 {exitCode}，发行年 {releaseYear}");

            // hs_err 与 crash report 是两个独立的现场来源：Java 层崩溃只产出 crash report，
            // JVM 原生层崩溃（显卡驱动 / JNI / 栈溢出）只产出 hs_err_pid*.log。
            var hsErrFiles = FindRecentHsErrLogs(options, launchStartedUtc);
            hsErrLogs = ReadHsErrSections(hsErrFiles, _log);
            crashReport = AnalyzeCrash(session, options, hsErrLogs);

            if (crashReport is { IsCrash: true })
            {
                crashAnalysis = CrashAnalyzer.Format(crashReport);
            }

            ArchiveCrashArtifacts(options, crashReport, hsErrFiles);
        }

        await AutomaticWorldBackup.RunAsync(
            _backups, _settings?.Current, gameDirectory, verdict == GameExitVerdict.Crashed, _log, progress)
            .ConfigureAwait(false);

        // 退出后命令要拿得到真实退出码，所以放在判定之后、返回之前。
        await RunLaunchCommandAsync(
                options.LaunchAfterCommand,
                "退出后",
                options,
                java,
                exitCode,
                CancellationToken.None)
            .ConfigureAwait(false);

        // 退出文案必须留在 Detail 上：带 StartupStage 的话 HomeViewModel 会把它覆盖成阶段描述。
        progress?.Report(new LaunchProgress(LaunchPhase.Exited, DescribeExit(exitCode, verdict)));

        return BuildExitResult(exitCode, stopwatch.Elapsed, crashAnalysis, crashReport, hsErrLogs, verdict)
            with
            {
                SessionId = session.SessionId,
            };
    }

    /// <summary>
    /// 把「等待退出 + 退出收尾 + 注销会话 + 释放实例锁」整体交给后台。
    ///
    /// 为什么必须是独立任务而不是 <c>await</c>：<see cref="LaunchAsync"/> 的调用方是 UI 线程，
    /// 阻塞在这里会让「停止游戏」按钮永远点不到；游戏可能要跑几个小时。
    /// </summary>
    private void DetachRunningGame(
        Process process,
        RunningGame session,
        IInstanceLockHandle? instanceLock,
        LaunchOptions options,
        JavaInfo? java,
        GameWatcher watcher,
        Stopwatch stopwatch,
        int releaseYear,
        string gameDirectory,
        DateTime launchStartedUtc,
        IProgress<LaunchProgress>? progress)
    {
        _ = Task.Run(async () =>
        {
            var finished = false;

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

                if (java is null)
                {
                    // 进程都起来了 Java 一定解析过；这里只防御极端时序，不做完整收尾。
                    _log.Warn("后台会话缺少 Java 信息，跳过退出收尾");
                }
                else
                {
                    var result = await CompleteExitAsync(
                            process, session, options, java, watcher, stopwatch, releaseYear,
                            gameDirectory, launchStartedUtc, progress)
                        .ConfigureAwait(false);

                    // 后台结果没有别的通道回界面：发事件让首页能显示崩溃结论/建议。
                    // 订阅方抛异常不能影响收尾（锁与会话的释放在 finally）。
                    try
                    {
                        SessionExited?.Invoke(this, new SessionExitedEventArgs(session.SessionId, result));
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("通知后台会话退出失败", ex);
                    }
                }

                finished = true;
            }
            catch (Exception ex)
            {
                _log.Warn("后台会话退出收尾失败", ex);
            }
            finally
            {
                // 无论收尾成功与否，会话与锁都必须在这里释放：这是它们唯一的所有权终点。
                _running.Unregister(session);
                instanceLock?.Dispose();
                process.Dispose();
                _finishing.TryRemove(RunningGameInfo.KeyFor(gameDirectory), out _);

                if (!finished)
                {
                    _log.Warn("后台会话未完成收尾，已释放实例锁与会话");
                }
            }
        });
    }

    /// <summary>
    /// 抢不到运行锁时给用户的话。必须带 PID 与版本名：只说「启动不了」的话，
    /// 用户不知道该去任务管理器里结束哪一个（尤其是托盘里还有另一个启动器窗口时）。
    /// </summary>
    private async Task<string> DescribeLockConflictAsync(string gameDirectory)
    {
        const string fallback = "该实例已有游戏在运行。可以到任务管理器结束它，或等它退出。";

        try
        {
            var running = await _instanceLocks!
                .QueryAsync(gameDirectory, CancellationToken.None)
                .ConfigureAwait(false);

            return running is null
                ? fallback
                : $"该实例已有游戏在运行（PID {running.Pid}，版本 {running.VersionText}）。"
                  + "可以到任务管理器结束它，或等它退出。";
        }
        catch (Exception ex)
        {
            // 查询失败不影响「拒绝启动」这个结论：抢不到锁本身就是拒绝的理由。
            _log.Warn("读取实例运行锁状态失败", ex);
            return fallback;
        }
    }

    /// <inheritdoc />
    public void KillCurrentGame() => _running.KillAll(_log);

    /// <summary>
    /// 导出可执行启动脚本。命令行与「查看启动命令」同源，避免两处拼参数。
    /// 注入参数由 <see cref="BuildCommandLineAsync"/> 一并带出，所以导出的脚本可以直接跑。
    /// 构建失败（没有可用 Java、第三方验证注入准备失败等）时返回 null，不抛异常。
    /// </summary>
    public async Task<string?> ExportLaunchScriptAsync(
        LaunchOptions options,
        string targetPath,
        bool maskAccessToken = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        string commandLine;

        try
        {
            commandLine = await BuildCommandLineAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("导出启动脚本失败：构建命令行出错", ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var token = maskAccessToken ? options.Account.AccessToken : null;
        var path = await LaunchScriptExporter
            .ExportAsync(
                commandLine,
                ResolveGameDirectory(options),
                targetPath,
                token,
                $"{options.VersionName} - NebulaLauncher",
                cancellationToken)
            .ConfigureAwait(false);

        if (path is null)
        {
            _log.Warn($"导出启动脚本失败：{targetPath}");
        }
        else
        {
            _log.Info($"已导出启动脚本：{path}{(maskAccessToken ? "（accessToken 已打码）" : string.Empty)}");
        }

        return path;
    }

    /// <summary>
    /// 组装 <see cref="MinecraftPath"/>。
    /// 实例目录只承担「运行期数据目录」（saves / mods / config）的角色，
    /// 库、版本、资源仍指向全局 .minecraft，避免每个实例重复几十 GB。
    /// </summary>
    private MinecraftPath BuildMinecraftPath(LaunchOptions options)
    {
        var gameDirectory = string.IsNullOrWhiteSpace(options.GameDirectory)
            ? _paths.MinecraftDirectory
            : options.GameDirectory!;

        var path = new MinecraftPath(gameDirectory);

        if (!string.Equals(gameDirectory, _paths.MinecraftDirectory, StringComparison.OrdinalIgnoreCase))
        {
            path.Library = _paths.LibrariesDirectory;
            path.Versions = _paths.VersionsDirectory;
            path.Assets = _paths.AssetsDirectory;
            path.Resource = Path.Combine(gameDirectory, "resources");
        }

        return path;
    }

    private static MLaunchOption BuildLaunchOption(
        LaunchOptions options,
        JavaInfo java,
        MinecraftPath minecraftPath,
        string? authlibArguments = null,
        string? versionArgumentInfo = null)
    {
        var launchOption = new MLaunchOption
        {
            Path = minecraftPath,
            JavaPath = java.Path,
            Session = new MSession(options.Account.Username, options.Account.AccessToken ?? "-", options.Account.Uuid),
            MaximumRamMb = options.MaxMemoryMb,
            MinimumRamMb = options.MinMemoryMb,
            ScreenWidth = options.WindowWidth,
            ScreenHeight = options.WindowHeight,
            FullScreen = options.Fullscreen,
            IsDemo = options.DemoMode,
            GameLauncherName = "NebulaLauncher",
            GameLauncherVersion = typeof(GameLauncher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",

            // 空值不能留 null：CmlLib 会把 null 当成「没设置」并填默认的 release，
            // 而 PCL2 对「没配置」的处理是把 --versionType 整段删掉（否则主菜单显示空字符串）。
            // 这里统一传空串，让 CmlLib 生成固定形态 --versionType ""，随后由
            // StripEmptyVersionType 精确摘掉这两个 token。
            VersionType = string.IsNullOrWhiteSpace(versionArgumentInfo) ? string.Empty : versionArgumentInfo!.Trim(),
        };

        var jvmArguments = ComposeJvmArguments(
            authlibArguments,
            options.ExtraJvmArguments,
            options.GcKind,
            java.MajorVersion,
            options.RelaxForgeChecks);

        if (jvmArguments.Count > 0)
        {
            launchOption.ExtraJvmArguments = jvmArguments.Select(a => new MArgument(a)).ToArray();
        }

        var gameArguments = DeduplicateGameArguments(options, launchOption);
        if (gameArguments.Count > 0)
        {
            launchOption.ExtraGameArguments = gameArguments.Select(a => new MArgument(a)).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            var (host, port) = SplitServerAddress(options.ServerAddress!);

            if (!string.IsNullOrEmpty(host))
            {
                launchOption.ServerIp = host;
                launchOption.ServerPort = port;
            }
        }

        return launchOption;
    }

    /// <summary>
    /// 最终生效的版本信息（PCL2 <c>LaunchArgumentInfo</c>），用于 <c>--versionType</c>。
    ///
    /// 解析顺序是「实例优先、全局兜底」（PCL2 <c>ModLaunch.vb:1531-1532</c>），
    /// 再叠一层 PCL2 没有的语义：实例的<b>空串</b>表示「明确不显示」。
    /// <see cref="LaunchOptions.VersionArgumentInfo"/> 为 null 才回落全局——
    /// 用 <c>??</c> 而不是 <c>string.IsNullOrWhiteSpace</c> 判断，就是为了让空串能压掉全局值。
    ///
    /// 为什么不复用 <see cref="InstanceSettings.Notes"/>：那是「实例备注」，显示在实例卡片上，
    /// 复用会把用户给自己看的备忘直接印到游戏主菜单标题上。
    /// 设置服务缺失（单测、临时宿主）时按「未配置」处理，即删掉 <c>--versionType</c>。
    /// </summary>
    private string? ResolveVersionArgumentInfo(LaunchOptions options) =>
        options.VersionArgumentInfo ?? _settings?.Current.VersionArgumentInfo;

    /// <summary>
    /// 启动前补齐文件（以及命令行构建）时的逐文件并发上限。
    ///
    /// 与 <c>InstallService.MaxParallel</c> 同源同口径：都是设置页那根「并发下载数」滑块。
    /// 只接安装页不接启动链的话，用户把它从 64 调到 8 只影响安装，而「点启动时补齐那 788 MB」
    /// 仍按 8 个一批走——这正是票 27 缺陷 B 说的「游戏本体逐文件下载并发恒为 8」。
    /// 设置服务缺失（单测、临时宿主）时回落到工厂默认值。
    /// </summary>
    private int ResolveMaxParallel() =>
        _settings is null ? 8 : Math.Clamp(_settings.Current.DownloadThreads, 1, 256);

    /// <summary>
    /// 摘掉空值 <c>--versionType ""</c> 的两个 token（PCL2 <c>ModLaunch.vb:1538-1544</c>）。
    ///
    /// 为什么必须删除而不是留空串：MC 会把 <c>--versionType</c> 的值原样显示在主菜单标题与 F3 屏上，
    /// 空串会让那一行变成空白，而 CmlLib 的默认值 <c>release</c> 又会把用户的整合包名盖掉。
    /// 这个动作只能在 CmlLib 拼好命令行之后做——它没有任何「不生成该参数」的开关（实测 4.1.0）。
    /// </summary>
    private static string StripEmptyVersionType(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return arguments;
        }

        var stripped = EmptyVersionTypePattern.Replace(arguments, string.Empty);

        // 没命中就返回原串：既省一次 Trim，也避免无意改掉首尾空白（调用方可能依赖它）。
        return stripped.Length == arguments.Length ? arguments : stripped.Trim();
    }

    /// <summary>
    /// 额外游戏参数整理：先按 PCL2 语义去重（游戏参数后出现者覆盖），再把 CmlLib 一定会自己生成的
    /// 窗口类参数（<c>--width</c> / <c>--height</c> / <c>--fullscreen</c> / <c>--demo</c>）写回
    /// <paramref name="launchOption"/> 的属性，返回仍然需要追加到命令行的部分。
    ///
    /// 两步的先后不能反：去重必须在切分之后（否则比的是原始字符串里的占位符与引号，不是最终的键），
    /// 吸收必须在去重之后（否则 <c>--width 1280 --width 1920</c> 会按遍历顺序写两次属性，
    /// 「后出现者覆盖」就退化成「先出现者保留」）。
    /// </summary>
    private static List<string> DeduplicateGameArguments(LaunchOptions options, MLaunchOption launchOption)
    {
        var deduplicated = LaunchArgumentDeduplicator.Deduplicate(
            SplitArguments(options.ExtraGameArguments),
            isJvmArguments: false);

        if (!string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            deduplicated = LaunchArgumentDeduplicator.RemoveServerArguments(deduplicated);
        }

        var absorbed = LaunchArgumentDeduplicator.AbsorbKnownOptions(deduplicated);

        if (absorbed.WindowWidth is { } width)
        {
            launchOption.ScreenWidth = width;
        }

        if (absorbed.WindowHeight is { } height)
        {
            launchOption.ScreenHeight = height;
        }

        if (absorbed.Fullscreen is { } fullscreen)
        {
            launchOption.FullScreen = fullscreen;
        }

        if (absorbed.Demo is { } demo)
        {
            launchOption.IsDemo = demo;
        }

        return [.. absorbed.Remaining];
    }

    /// <summary>
    /// JVM 参数最终顺序：注入参数 → 用户参数（先清掉旧 GC）→ 启动器生成的 GC / 安全 / 编码参数。
    ///
    /// 注入的 <c>-javaagent</c> 放在最前，与 PCL2 一致；更关键的是它必须在用户可能塞进来的
    /// <c>-jar</c> / 主类之前，否则 javaagent 不会生效（JVM 只认主类之前的 agent 参数）。
    /// GC 参数仍然压在最末：JVM 对重复的 <c>-XX</c> 取最后一个，反序会让设置页的选择失效。
    ///
    /// 纯函数，单独暴露给测试：真正拼进进程的列表与这里返回的必须完全一致。
    /// </summary>
    /// <param name="authlibArguments">第三方验证注入片段；null / 空串表示本次不需要注入。</param>
    /// <param name="extraJvmArguments">用户（或实例）自定义 JVM 参数原文。</param>
    /// <param name="gcKind">垃圾回收器偏好。</param>
    /// <param name="javaMajorVersion">选中的 Java 主版本。</param>
    /// <param name="relaxForgeChecks">
    /// 是否注入放宽 Forge / 命令行校验的三条 <c>-D</c>，来自 <see cref="LaunchOptions.RelaxForgeChecks"/>。
    /// 默认 <c>true</c> 只是兼容既有调用点（测试直接调这个方法）；真正启动时一定显式传值。
    /// </param>
    /// <remarks>
    /// <b><c>-Xmx</c> / <c>-Xms</c> 与内存设置的关系（实测 CmlLib.Core 4.1.0）</b>：
    /// CmlLib 在按 <see cref="LaunchOptions.MaxMemoryMb"/> / <see cref="LaunchOptions.MinMemoryMb"/>
    /// 生成内存参数之前，会先看<b>额外 JVM 参数</b>里有没有同名项；有就不再生成自己那条。
    /// 实测命令行里用户在自定义参数里写的 <c>-Xmx4G</c> 只出现一次，启动器那条根本没生成；
    /// 而用户没写的 <c>-Xms</c> 仍按设置生成。所以这里<b>不要</b>自己把用户的
    /// <c>-Xmx</c> / <c>-Xms</c> 摘出来改写成内存字段——那会让「用户显式写的值」输给设置页的数字。
    /// 例证：<c>CoreLaunch_LaunchOptionsCloneTests.RealLaunch_UserXmx_NotDuplicatedAndWins</c>。
    /// </remarks>
    public static IReadOnlyList<string> ComposeJvmArguments(
        string? authlibArguments,
        string? extraJvmArguments,
        GcKind gcKind,
        int javaMajorVersion,
        bool relaxForgeChecks = true)
    {
        var arguments = new List<string>(12);

        arguments.AddRange(SplitAuthlibArguments(authlibArguments));
        arguments.AddRange(SplitArguments(JvmArgumentBuilder.StripGcArguments(extraJvmArguments)));
        arguments.AddRange(SplitArguments(JvmArgumentBuilder.Build(gcKind, javaMajorVersion, extraJvmArguments, relaxForgeChecks)));

        // JVM 侧只去「键和值都完全相同」的余项：覆盖会改变 -Xmx / -javaagent / -cp 的相对位置，
        // 那套顺序本身就是 JVM 的生效依据（详见 LaunchArgumentDeduplicator 的类注释）。
        return LaunchArgumentDeduplicator.Deduplicate(arguments, isJvmArguments: true);
    }

    /// <summary>
    /// 把注入片段切成参数列表。
    ///
    /// 不能复用 <see cref="SplitArguments"/>：CmlLib 的命令行解析器把反斜杠当转义符，
    /// <c>-javaagent:D:\launcher root\a.jar=...</c> 会被解析成 <c>D:launcher roota.jar</c>，
    /// Windows 上注入直接失效。这里只按空白切分并识别引号，反斜杠原样保留；
    /// 切完再去掉 javaagent 里 jar 路径两侧的引号，交给 CmlLib 在拼命令行时按需重新加
    /// （它只在整个参数含空白时加引号，正好是 Windows 命令行需要的形式）。
    /// </summary>
    private static List<string> SplitAuthlibArguments(string? arguments)
    {
        var result = new List<string>(3);

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var text = arguments!;
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            var inQuotes = false;

            while (index < text.Length)
            {
                var current = text[index];

                if (current == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && char.IsWhiteSpace(current))
                {
                    break;
                }

                index++;
            }

            result.Add(UnquoteJavaAgentJar(text[start..index]));
        }

        return result;
    }

    /// <summary>去掉 <c>-javaagent:"&lt;jar&gt;"=...</c> 里 jar 路径两侧的引号，其余部分不动。</summary>
    private static string UnquoteJavaAgentJar(string token)
    {
        const string prefix = "-javaagent:";

        if (!token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return token;
        }

        var rest = token[prefix.Length..];

        if (rest.Length < 2 || rest[0] != '"')
        {
            return token;
        }

        var closing = rest.IndexOf('"', 1);

        return closing < 0
            ? token
            : prefix + rest[1..closing] + rest[(closing + 1)..];
    }

    /// <summary>
    /// 解析本次启动需要的注入参数。
    /// 返回 <c>(false, null)</c> 表示「不是第三方验证账户，不需要注入」；
    /// 返回 <c>(true, null)</c> 表示「需要注入但没准备好」，由调用方阻断启动并给出原因。
    ///
    /// 不抛异常：注入失败的用户可见文案统一走 <see cref="DescribeAuthlibFailure"/>。
    /// </summary>
    private async Task<(bool Required, string? Arguments)> ResolveAuthlibArgumentsAsync(
        LaunchOptions options,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 本次要注入的 API 根：实例非 null 就是实例自己的选择（含空串 = 明确不注入），
        // 否则跟随账户。用 ?? 而不是 FirstNonEmpty：空串是「关闭」的意思，不能被当成「没设置」
        // 而回落到账户地址——那正是用户想关却关不掉的那类 bug。
        var apiRoot = options.InstanceAuthServerUrl ?? options.Account.AuthServerUrl;

        // 账户类型仍然是硬门槛：离线 / 微软账户注入 javaagent 换不来角色，
        // 却会连带触发一次网络下载与元数据预取，失败时还会阻断启动。
        if (options.Account.Type != AccountType.Authlib || string.IsNullOrWhiteSpace(apiRoot))
        {
            return (false, null);
        }

        if (_authlib is null)
        {
            _log.Warn("第三方验证账户没有可用的 authlib-injector 服务");
            return (true, null);
        }

        _log.Info($"正在准备 authlib-injector 注入：{apiRoot}");
        progress?.Report(new LaunchProgress(LaunchPhase.Preparing, "正在准备第三方验证注入…"));

        // 下载进度转成启动进度：cookie 下载量要能显示在首页进度条上，否则用户会以为卡住了。
        var downloadProgress = progress is null
            ? null
            : new SyncProgress<DownloadProgress>(value => progress.Report(new LaunchProgress(
                LaunchPhase.Preparing,
                string.IsNullOrEmpty(value.CurrentFile)
                    ? "正在准备第三方验证注入…"
                    : $"正在下载 {value.CurrentFile}…",
                Math.Clamp(value.Ratio, 0, 1),
                (long)value.SpeedBytesPerSecond,
                value.DownloadedBytes,
                value.TotalBytes)));

        try
        {
            var injection = await _authlib
                .BuildInjectionAsync(apiRoot!, downloadProgress, cancellationToken)
                .ConfigureAwait(false);

            return injection is null ? (true, null) : (true, injection.JvmArguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("准备 authlib-injector 注入失败", ex);
            return (true, null);
        }
    }

    /// <summary>注入失败的用户可读原因（中文），前缀固定，UI 与日志共用同一句。</summary>
    private string DescribeAuthlibFailure()
    {
        var detail = string.IsNullOrWhiteSpace(_authlib?.LastError)
            ? "authlib-injector 没有准备好，请检查网络后重试。"
            : _authlib!.LastError!;

        return $"第三方验证注入失败：{detail}";
    }

    /// <summary>
    /// 解析启动目标：返回原版版本号与加载器类型。
    ///
    /// 加载器必须一起带出来：Java 允许区间依赖它（1.12.2 Forge 要求 ≤ Java 8，
    /// 1.16.5 Forge 要求 ≤ Java 17），只按版本目录名猜会把 Forge 实例判给 Java 21。
    /// </summary>
    private async Task<(string MinecraftVersion, LoaderKind Loader)> ResolveMinecraftVersionAsync(
        LaunchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = await _versions.GetVersionAsync(options.VersionName, cancellationToken).ConfigureAwait(false);

            if (info is not null)
            {
                var minecraftVersion = string.IsNullOrEmpty(info.MinecraftVersion)
                    ? options.VersionName
                    : info.MinecraftVersion!;

                return (minecraftVersion, info.Loader);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"解析版本 {options.VersionName} 的原版版本号失败，按版本名处理", ex);
        }

        return (options.VersionName, LoaderKind.Vanilla);
    }

    /// <summary>
    /// 前置检查第一段：环境项（目录、磁盘、路径字符、内存、加载器完整性、模组）。
    /// 这一段不碰 Java，2 秒预算不会因为 Java 探测太慢而丢掉后面的 Java 段。
    /// Warning 推到进度条、Info 只写日志；失败返回空报告（不阻断）。
    /// </summary>
    private async Task<PreflightReport> RunEnvironmentPreflightAsync(
        LaunchOptions options,
        string minecraftVersion,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await PreflightChecker
                .CheckEnvironmentAsync(
                    options,
                    _versions,
                    _content,
                    _paths,
                    _platform,
                    minecraftVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            ReportPreflight(report, progress, "环境");
            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("环境前置检查失败，跳过", ex);
            return PreflightReport.Empty;
        }
    }

    /// <summary>
    /// 前置检查第二段：Java 项。传进来的 <paramref name="java"/> 是刚选好的运行时，
    /// 区间由 <see cref="IJavaService.GetAllowedRange"/> 现算——本方法内部零探测、同步返回。
    ///
    /// 加载器版本从 <see cref="LaunchOptions.LoaderVersion"/> 取，不再单开一个参数：
    /// <paramref name="options"/> 本来就在签名里，同一个值有两个来源迟早会对不上
    /// （改了一处忘了另一处，症状是「有时拦得住、有时拦不住」，最难查）。
    /// </summary>
    private PreflightReport CheckJavaPreflight(
        LaunchOptions options,
        JavaInfo java,
        string minecraftVersion,
        LoaderKind loader,
        IProgress<LaunchProgress>? progress)
    {
        (int Min, int Max)? allowedRange;

        try
        {
            allowedRange = _java.GetAllowedRange(minecraftVersion, loader, options.LoaderVersion);
        }
        catch (Exception ex)
        {
            _log.Warn("读取 Java 允许区间失败，跳过版本区间判定", ex);
            allowedRange = null;
        }

        // 加载器小版本的上限单独取：它带「阻断 / 建议」分级与给用户看的原因，
        // 而区间只能表达一个数字上界。拿不到（版本号为空或没有对应规则）时为 null，
        // CheckJava 就退回只做区间判定的老行为。
        var loadLimit = JavaRequirementTable.GetLoaderLimit(minecraftVersion, loader, options.LoaderVersion);

        var report = PreflightChecker.CheckJava(options, java, allowedRange, loadLimit);
        ReportPreflight(report, progress, "Java");
        return report;
    }

    /// <summary>把检查结果分流：Blocker 写日志（由调用方阻断），Warning 推进度条，Info 只写日志。</summary>
    private void ReportPreflight(PreflightReport report, IProgress<LaunchProgress>? progress, string stage)
    {
        foreach (var item in report.Items)
        {
            switch (item.Severity)
            {
                case PreflightSeverity.Blocker:
                    _log.Warn($"前置检查阻断（{stage}）：{item.Title}：{item.Detail}");
                    break;
                case PreflightSeverity.Warning:
                    _log.Info($"前置检查提醒（{stage}）：{item.Title}：{item.Detail}");
                    progress?.Report(new LaunchProgress(
                        LaunchPhase.Preparing,
                        $"{item.Title}：{item.Detail}"));
                    break;
                default:
                    _log.Debug($"前置检查（{stage}）：{item.Title}：{item.Detail}");
                    break;
            }
        }
    }

    /// <summary>
    /// 「没有可用 Java」时该装哪个主版本：优先版本 JSON / 内置表给出的需求，
    /// 拿不到需求（未知版本、快照）时退回允许区间的下界——区间下界同样是「装这个就能起」的答案。
    /// 两条都拿不到返回 null：界面据此不显示一键安装按钮，只留「设置 → Java」入口。
    ///
    /// 这里只读表与本地 JSON，零探测、零网络，失败也不抛——启动已经失败了，不能因为
    /// 「猜版本」这一步再炸一次。
    /// </summary>
    private int? ResolveMissingJavaMajor(
        string minecraftVersion,
        LoaderKind loader,
        JavaRequirement? requirement)
    {
        if (requirement is { RequiredMajor: > 0 } declared)
        {
            return declared.RequiredMajor;
        }

        try
        {
            var range = _java.GetAllowedRange(minecraftVersion, loader);
            return range is { Min: > 0 } allowed ? allowed.Min : null;
        }
        catch (Exception ex)
        {
            _log.Warn("读取 Java 允许区间失败，无法给出可安装的主版本", ex);
            return null;
        }
    }

    /// <summary>把 Blocker 项拼成一句给用户看的中文原因。</summary>
    private static string DescribeBlockers(PreflightReport report)
    {
        var blockers = report.Items
            .Where(item => item.Severity == PreflightSeverity.Blocker)
            .Select(item => $"{item.Title}（{item.Detail}）")
            .ToList();

        return "启动前置检查未通过：" + string.Join("；", blockers);
    }

    /// <summary>
    /// 把本次启动用的 Java 注册为「高性能 GPU」（PCL2 <c>LaunchAdvanceGraphicCard</c> +
    /// <c>ModMain.vb:802-813 SetGPUPreference</c>）。
    ///
    /// 三条判据：
    /// 1. 开关关掉就什么都不做——有的用户自己已经在显卡控制面板里配好了，不想让启动器改注册表；
    /// 2. 拿不到平台服务（测试替身 / 非 Windows）直接跳过；
    /// 3. **失败不阻断启动**。注册表写不进去（组策略、HKCU 被锁）只是少了一次性能优化，
    ///    把游戏拦在这里是本末倒置——PCL2 也只是记日志并尝试提权重启自身。
    /// </summary>
    private void ApplyGpuPreference(Models.JavaInfo? java)
    {
        if (_platform is null || java is null || string.IsNullOrWhiteSpace(java.Path))
        {
            return;
        }

        if (_settings is not null && !_settings.Current.RegisterGpuPreference)
        {
            _log.Debug("已关闭「启动前注册高性能 GPU」，跳过显卡首选项写入");
            return;
        }

        try
        {
            _ = _platform.TrySetGpuPreference(java.Path);
        }
        catch (Exception ex)
        {
            // TrySetGpuPreference 的契约是不抛，这里是最后一道兜底。
            _log.Warn("注册显卡首选项时出现异常（不影响启动）", ex);
        }
    }

    /// <summary>
    /// 把 <c>options.txt</c> 的语言改成简体中文（PCL2 <c>ToolHelpChinese</c> / <c>ModLaunch.vb:1844-1864</c>）。
    ///
    /// 三条规则与 PCL2 一致，缺一条都会出真问题：
    /// <list type="number">
    /// <item>语言代码按 MC 版本分档：1.1~1.10 必须 <c>zh_CN</c>（1.1~5 写成 <c>zh_cn</c> 直接 NPE 崩溃），
    ///       1.11+ 必须 <c>zh_cn</c>（写成 <c>zh_CN</c> 会让语言设置显示成英文）。</item>
    /// <item>是否存在 <c>saves</c> 决定「这是不是首次设置」：整合包可能自带 options.txt 并把 lang 写成
    ///       en_us，只看当前语言会把玩家的选择改掉。</item>
    /// <item>写入分两次（先 <c>-</c> 再目标值），否则游戏的语言缓存不刷新。</item>
    /// </list>
    /// 任何失败都只记 warn、不阻断启动：最坏情况是游戏用英文，比打不开好。
    /// </summary>
    /// <param name="minecraftVersion">原版 Minecraft 版本号（<c>1.20.1</c> 这类），用于版本分档。</param>
    private void ApplyChineseLanguage(LaunchOptions options, string minecraftVersion)
    {
        if (!options.AutoChineseLanguage)
        {
            return;
        }

        try
        {
            var gameDirectory = ResolveGameDirectory(options);

            // 解析不出来的是快照（24w14a）或自定义目录名：它们几乎都晚于 1.13。
            // 这时候按现代版本处理——反过来误判成 1.1~1.10 会让 1.13+ 的语言显示成英文。
            var vanillaMinor = JavaRequirementTable.TryParseRelease(minecraftVersion, out var minor, out _)
                ? minor
                : ModernVersionMinor;

            var file = ApplyChineseLanguageCore(gameDirectory, vanillaMinor);

            if (file is not null)
            {
                _log.Info($"已把游戏语言设为简体中文（按 MC 1.{vanillaMinor} 的语言代码规则）：{file}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("设置游戏语言失败", ex);
        }
    }

    /// <summary>
    /// <see cref="ApplyChineseLanguage"/> 的实际实现，单独暴露以便直接单测
    /// （不需要构造 GameLauncher、不需要真启动游戏）。
    /// </summary>
    /// <param name="gameDirectory">游戏目录（PCL2 的 <c>PathIndie</c>）：<c>options.txt</c> 与 <c>saves</c> 都相对它。</param>
    /// <param name="vanillaMinor">原版次版本号：1.20 → 20、1.5 → 5；0 表示 1.0 及更早。</param>
    /// <param name="onWrite">每次写入 lang 后的回调，供测试核对「先 - 再目标值」的顺序。</param>
    /// <returns>实际改写的文件路径；无需改动或没有可改文件时为 null。</returns>
    /// <exception cref="IOException">写完读回的内容与目标不一致（文件只读、被占用）。</exception>
    public static string? ApplyChineseLanguageCore(
        string gameDirectory,
        int vanillaMinor,
        Action<string>? onWrite = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        // 1.0 及更早根本没有语言选项（PCL2 注释第一行）：往里写 lang 只会留下一行死键。
        if (vanillaMinor <= 0)
        {
            return null;
        }

        var file = Path.Combine(gameDirectory, "options.txt");

        if (!File.Exists(file))
        {
            // Yosbr mod 兼容（PCL2 #2385，curseforge.com/minecraft/mc-mods/yosbr）：
            // 它把默认 options.txt 放在 config/yosbr/ 下并在启动时覆盖根目录那份，只改根目录等于没改。
            // 先写 none 是 PCL2 的标记——「忽略默认语言」，表示下面要以启动器的判断为准。
            var yosbr = Path.Combine(gameDirectory, "config", "yosbr", "options.txt");

            if (!File.Exists(yosbr))
            {
                // 没有文件就不新建：第一次启动时游戏会写一份完整默认值，启动器插一脚会丢字段。
                return null;
            }

            file = yosbr;
            WriteLanguage(file, IgnoreLanguage, onWrite);
        }

        var current = ReadLanguage(file) ?? IgnoreLanguage;

        // 为什么判据是 saves 而不是「当前语言是不是英文」（PCL2 #3844）：整合包可以自带 options.txt
        // 并预先把 lang 写成 en_us，此时「当前是英文」不代表玩家想要英文；而存档目录存在说明
        // 这个实例已经被玩过，那语言就是玩家自己选的，必须保留。
        var hasSaves = Directory.Exists(Path.Combine(gameDirectory, "saves"));

        var required = current == IgnoreLanguage || !hasSaves
            ? ChineseLanguage
            : current.ToLowerInvariant();

        // 1.1 ~ 1.10 的语言代码必须大写结尾（PCL2 实测表）：
        //   1.1~5 ：zh_cn 会让游戏 NPE 崩溃——这是本条规则最硬的判据，不是风格问题；
        //   1.6~10：zh_cn 会被游戏静默回落成英文。
        // 1.11 ~ 12 用 zh_cn；1.13+ 用 zh_cn（写 zh_CN 会显示「语言 = 英文」）。
        if (vanillaMinor <= 10 && required.Length >= 2)
        {
            required = required[..^2] + required[^2..].ToUpperInvariant();
        }

        if (string.Equals(current, required, StringComparison.Ordinal))
        {
            return null;
        }

        // 分两次写：先写成 "-" 再写目标值。游戏按内容缓存语言列表，直接把同长度的值覆盖
        // 不会触发刷新，表现为「options.txt 已经是 zh_cn，进游戏还是英文」（PCL2 :1861-1862）。
        WriteLanguage(file, "-", onWrite);
        WriteLanguage(file, required, onWrite);

        // 读回确认：只读文件 / 被占用时写入会静默失败，不校验就等于报告「已设置」却什么都没改。
        if (!string.Equals(ReadLanguage(file), required, StringComparison.Ordinal))
        {
            throw new IOException($"写入语言失败：{file} 写回后读到的不是 {required}");
        }

        return file;
    }

    /// <summary>
    /// 读 <c>options.txt</c> 风格的 <c>lang:</c> 行。没有该行返回 null（调用方按 none 处理）。
    /// 只取第一处，与游戏自己的解析顺序一致。
    /// </summary>
    private static string? ReadLanguage(string file)
    {
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith("lang:", StringComparison.Ordinal))
            {
                return line["lang:".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// 只改 <c>lang:</c> 那一行，其余行原样保留。没有该行时追加——不能整文件重写，
    /// options.txt 里有上百个键，丢任何一个都会让玩家的按键 / 音量 / 图形设置回到默认。
    /// </summary>
    /// <param name="onWrite">写入成功后的回调；只在真的落盘后触发。</param>
    private static void WriteLanguage(string file, string language, Action<string>? onWrite)
    {
        var lines = File.Exists(file)
            ? File.ReadAllLines(file).ToList()
            : [];

        var replaced = false;

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("lang:", StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = "lang:" + language;
            replaced = true;
            break;
        }

        if (!replaced)
        {
            lines.Add("lang:" + language);
        }

        File.WriteAllLines(file, lines);
        onWrite?.Invoke(language);
    }

    /// <summary>
    /// 设置子进程优先级。非 Windows 上 <see cref="Process.PriorityClass"/> 支持有限，
    /// 失败时只记调试日志，不影响游戏运行。
    /// </summary>
    private void ApplyProcessPriority(Process process, GameProcessPriority priority)
    {
        if (priority == GameProcessPriority.Normal)
        {
            return;
        }

        try
        {
            process.PriorityClass = priority == GameProcessPriority.High
                ? ProcessPriorityClass.High
                : ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            _log.Debug($"设置游戏进程优先级失败（{priority}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 崩溃后把现场打成 zip：崩溃报告 + <c>logs/latest.log</c> + <c>logs/debug.log</c> + hs_err。
    ///
    /// 归档落在 <c>crash-reports/archive/crash-&lt;时间&gt;.zip</c>：用户发崩溃求助时
    /// 只需要拖一个文件，不用在三个目录里翻。启动器自己的日志不进包（另有位置留存）。
    /// </summary>
    private void ArchiveCrashArtifacts(
        LaunchOptions options,
        CrashReport? report,
        IReadOnlyList<string>? hsErrFiles = null)
    {
        if (!options.ArchiveCrashReport)
        {
            return;
        }

        string temp = string.Empty;

        try
        {
            var gameDirectory = ResolveGameDirectory(options);
            var archiveDirectory = Path.Combine(gameDirectory, "crash-reports", "archive");
            Directory.CreateDirectory(archiveDirectory);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(archiveDirectory, $"crash-{stamp}.zip");
            temp = target + ".tmp";

            var files = new List<string>(5);

            if (!string.IsNullOrWhiteSpace(report?.CrashReportPath) &&
                File.Exists(report!.CrashReportPath!))
            {
                files.Add(report.CrashReportPath!);
            }

            foreach (var relative in new[] { "latest.log", "debug.log" })
            {
                var candidate = Path.Combine(gameDirectory, "logs", relative);

                if (File.Exists(candidate))
                {
                    files.Add(candidate);
                }
            }

            if (hsErrFiles is not null)
            {
                foreach (var hsErr in hsErrFiles)
                {
                    if (File.Exists(hsErr))
                    {
                        files.Add(hsErr);
                    }
                }
            }

            if (files.Count == 0)
            {
                return;
            }

            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var entryName = BuildArchiveEntryName(gameDirectory, file, entryNames);

                    try
                    {
                        zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                    }
                    catch (Exception ex)
                    {
                        // 单个文件被别的进程占用不该让整个归档失败。
                        _log.Debug($"归档跳过 {entryName}：{ex.Message}");
                    }
                }
            }

            File.Move(temp, target, overwrite: true);
            _log.Info($"崩溃现场已归档：{target}");
        }
        catch (Exception ex)
        {
            _log.Warn("归档崩溃现场失败", ex);

            try
            {
                if (temp.Length > 0 && File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception cleanup)
            {
                _log.Debug($"清理残缺归档失败：{cleanup.Message}");
            }
        }
    }

    /// <summary>
    /// 归档条目名：游戏目录内的文件用相对路径；目录外的文件只保留文件名。
    /// 不用 <see cref="Path.GetRelativePath"/> 直接拼目录外文件——那会写出带 <c>..</c> 的条目名。
    /// 重名时追加序号，避免 zip 里同名条目互相覆盖。
    /// </summary>
    private static string BuildArchiveEntryName(string gameDirectory, string file, HashSet<string> used)
    {
        var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');

        var entryName = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? Path.GetFileName(file)
            : relative;

        if (used.Add(entryName))
        {
            return entryName;
        }

        var directory = Path.GetDirectoryName(entryName)?.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(entryName);
        var extension = Path.GetExtension(entryName);

        for (var i = 2; ; i++)
        {
            var candidate = string.IsNullOrEmpty(directory)
                ? $"{name}-{i}{extension}"
                : $"{directory}/{name}-{i}{extension}";

            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private void PrepareProcess(Process process, RunningGame game)
    {
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        try
        {
            // 现代游戏输出是 UTF-8；旧版 Java 在中文 Windows 上会走 GBK，解码失败时只是显示乱码。
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        catch (Exception ex)
        {
            _log.Debug($"设置输出编码失败：{ex.Message}");
        }

        // 事件绑到会话对象而不是「当前进程」这个隐含全局：多开时读取线程是并发的，
        // 靠字段去找「现在是谁」必然串行——A 的行会被写进 B 的尾巴里。
        process.OutputDataReceived += (_, args) => OnLine(game, args.Data, isError: false);
        process.ErrorDataReceived += (_, args) => OnLine(game, args.Data, isError: true);
    }

    private void OnLine(RunningGame game, string? line, bool isError)
    {
        if (line is null)
        {
            return;
        }

        // 尾巴自己持锁（见 RunningGame.AppendOutput）：多开时两个读取线程各写各的。
        game.AppendOutput(line);

        // 先喂监视器再转发给 UI：崩溃特征行要在「游戏退出」判定之前落到 watcher 里，
        // 否则短命进程可能在最后几行输出还没被消化时就退出了。
        game.Watcher.Feed(line);

        try
        {
            OutputReceived?.Invoke(this, new GameOutputLine(line, isError, DateTimeOffset.Now)
            {
                SessionId = game.SessionId,
            });
        }
        catch (Exception ex)
        {
            // 订阅方抛异常不能拖垮游戏进程的读取线程。
            _log.Warn("游戏输出订阅方抛出异常", ex);
        }
    }

    /// <summary>
    /// 退出码非 0 时归因。
    ///
    /// 取报告的顺序：
    /// <list type="number">
    /// <item>游戏自报的路径（stdout 里的 <c>#@!@# Game crashed! Crash report saved to: #@!@# &lt;path&gt;</c>）；</item>
    /// <item>实例目录 <c>crash-reports</c> 下最新的 txt（拿不到自报路径时的兜底）；</item>
    /// <item>按修改时间取近期的 <c>hs_err_pid*.log</c>（JVM 原生层崩溃不会产出 crash report）。</item>
    /// </list>
    /// 自报路径才能避开「同一实例连续崩两次、或用户手动复制了旧报告」时读错文件的问题。
    /// </summary>
    /// <param name="game">
    /// 本次启动的会话。日志尾巴从它身上取，不能取「当前进程」——多开时那会把
    /// A 的崩溃现场拿去给 B 归因，是最难排查的一类串台。
    /// </param>
    /// <param name="options">本次启动参数，用于解析游戏目录。</param>
    /// <param name="hsErrLogs">本次崩溃读到的 hs_err 文本（每份已带文件名分隔行）。</param>
    /// <returns>分析结果；日志与两类报告都没有时返回 <c>null</c>。</returns>
    private CrashReport? AnalyzeCrash(RunningGame? game, LaunchOptions options, IReadOnlyList<string> hsErrLogs)
    {
        // 只取本会话的尾巴：多开时拿「当前进程」的日志会把 A 的崩溃现场算到 B 头上。
        var tail = game?.OutputSnapshot() ?? [];

        var tailText = tail.Length > 0 ? string.Join(Environment.NewLine, tail) : null;
        var gameDirectory = ResolveGameDirectory(options);

        var reportPath = ResolveReportPath(CrashAnalyzer.ExtractCrashReportPath(tailText), gameDirectory);
        var reportText = TryReadReportFile(reportPath);

        if (reportText is null)
        {
            // 自报路径没命中（老版本客户端 / 日志被截断）才退回「目录里最新的 txt」。
            (reportText, reportPath) = TryReadLatestCrashReport(gameDirectory);
        }

        var hsErrText = hsErrLogs.Count == 0
            ? null
            : string.Join(Environment.NewLine, hsErrLogs);

        if (string.IsNullOrWhiteSpace(tailText)
            && string.IsNullOrWhiteSpace(reportText)
            && string.IsNullOrWhiteSpace(hsErrText))
        {
            return null;
        }

        var report = CrashAnalyzer.Analyze(tailText, reportText, hsErrText, _log);

        return report with { CrashReportPath = reportPath };
    }

    /// <summary>
    /// 找本次崩溃写出的 hs_err。搜索顺序对应 JVM 可能的工作目录：
    /// <list type="number">
    /// <item>游戏目录（正常启动时 JVM 的 cwd，绝大多数 hs_err 落在这里）；</item>
    /// <item>实例目录 / 启动器数据根（隔离实例与自定义目录的上级）；</item>
    /// <item>启动器进程自己的工作目录与可执行文件目录（少数整合包会改 cwd）。</item>
    /// </list>
    /// 只收修改时间不早于启动时刻的（留 2 秒余量给文件系统时间戳精度），按修改时间倒序取最近 2 个。
    /// 目录不存在、无权限、文件正被占用都只记日志，不影响归因。
    /// </summary>
    private List<string> FindRecentHsErrLogs(LaunchOptions options, DateTime launchStartedUtc)
    {
        var directories = new List<string>(5);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string full;

            try
            {
                full = Path.GetFullPath(directory);
            }
            catch (Exception ex)
            {
                _log.Debug($"跳过无法解析的 hs_err 搜索目录 {directory}：{ex.Message}");
                return;
            }

            if (seenDirectories.Add(full))
            {
                directories.Add(full);
            }
        }

        AddDirectory(ResolveGameDirectory(options));
        AddDirectory(_paths.VersionsDirectory);
        AddDirectory(_paths.RootDirectory);
        AddDirectory(AppContext.BaseDirectory);
        AddDirectory(Environment.CurrentDirectory);

        var threshold = launchStartedUtc.AddSeconds(-2);
        var found = new List<FileInfo>();

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in new DirectoryInfo(directory).EnumerateFiles(
                             "hs_err_pid*.log",
                             SearchOption.TopDirectoryOnly))
                {
                    if (file.LastWriteTimeUtc >= threshold)
                    {
                        found.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"搜索 hs_err 失败（{directory}）：{ex.Message}");
            }
        }

        var result = found
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(file => file.FullName)
            .ToList();

        if (result.Count > 0)
        {
            _log.Info($"发现 {result.Count} 个本次崩溃的 hs_err：{string.Join("、", result)}");
        }

        return result;
    }

    /// <summary>
    /// 读取最多 2 个 hs_err，每份前面加一行 <c>===== 文件名 =====</c>。
    ///
    /// 分隔行由这里加而不是导出服务加：只有这一层知道 hs_err 的原始文件名，
    /// 而导出服务收到的是拼好的文本（<see cref="LaunchResult.HsErrLogs"/>）。
    /// 单个文件读取失败只跳过该份，不影响另一份，也不影响启动归因。
    /// </summary>
    /// <param name="hsErrFiles">hs_err 文件绝对路径。</param>
    /// <param name="log">启动器日志；为 null 时不写日志（单测直接调用）。</param>
    internal static IReadOnlyList<string> ReadHsErrSections(IReadOnlyList<string> hsErrFiles, IAppLog? log = null)
    {
        if (hsErrFiles.Count == 0)
        {
            return Array.Empty<string>();
        }

        var sections = new List<string>(hsErrFiles.Count);

        foreach (var file in hsErrFiles)
        {
            var content = CrashAnalyzer.ReadExternalReport(file);

            if (!content.IsSuccess)
            {
                log?.Warn($"读取 hs_err 失败（{file}）：{content.Error}");
                continue;
            }

            var fileName = Path.GetFileName(file);
            sections.Add($"===== {fileName} =====" + Environment.NewLine + content.Text);
            log?.Info($"已读取 hs_err：{file}" + (content.Truncated ? "（文件过大，已截断）" : string.Empty));
        }

        return sections;
    }

    /// <summary>
    /// 进程正常退出路径的 <see cref="LaunchResult"/> 构造。抽出来是为了让「崩溃场景带 hs_err」
    /// 有一个可直接单测的组装点——<see cref="LaunchAsync"/> 本身依赖真实进程，无法在单测里跑。
    /// 非崩溃时 <paramref name="hsErrLogs"/> 传空集合。
    ///
    /// <paramref name="verdict"/> 必须原样带出：UI 用它而不是退出码决定文案，
    /// <c>exit 0 但没走到可玩阶段</c> 这类崩溃只有它能表达。
    /// </summary>
    internal static LaunchResult BuildExitResult(
        int exitCode,
        TimeSpan duration,
        string? crashAnalysis,
        CrashReport? crashReport,
        IReadOnlyList<string> hsErrLogs,
        GameExitVerdict? verdict = null) =>
        new(true, exitCode, duration, null, crashAnalysis, crashReport)
        {
            HsErrLogs = hsErrLogs,
            Verdict = verdict,
        };

    /// <summary>报告路径解析：相对路径按实例目录展开，绝对路径原样返回。</summary>
    private static string? ResolveReportPath(string? rawPath, string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var candidate = rawPath.Trim().Trim('"');

        if (candidate.Length == 0)
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            // 游戏打印的是相对路径时以实例目录为准，而不是启动器进程的工作目录。
            return gameDirectory is null
                ? null
                : Path.GetFullPath(Path.Combine(gameDirectory, candidate));
        }
        catch (Exception)
        {
            // 路径里有非法字符：当作没拿到路径，交给兜底逻辑。
            return null;
        }
    }

    /// <summary>读指定报告文件；不存在、过大或读失败都返回 <c>null</c>。</summary>
    private string? TryReadReportFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);

            // 超过 4 MB 的报告通常是日志刷屏，读了也只会拖慢归因。
            if (!info.Exists || info.Length > 4 * 1024 * 1024)
            {
                return null;
            }

            _log.Info($"已读取崩溃报告：{info.FullName}");
            return File.ReadAllText(info.FullName);
        }
        catch (Exception ex)
        {
            _log.Warn("读取崩溃报告失败", ex);
            return null;
        }
    }

    /// <summary>实例目录（或全局 .minecraft，当 <see cref="LaunchOptions.GameDirectory"/> 为空）。</summary>
    private string ResolveGameDirectory(LaunchOptions options) =>
        string.IsNullOrWhiteSpace(options.GameDirectory)
            ? _paths.MinecraftDirectory
            : options.GameDirectory!;

    /// <summary>
    /// 会话的显示名：Core 只认目录，真实实例名是 App 层的概念。
    /// 用目录名兜底足够区分「哪个实例在跑」，也避免为它给 <see cref="LaunchOptions"/> 加字段
    /// （那会连带要求 <c>CloneWithMaxMemory</c> 与三处构造点同步）。
    /// </summary>
    private static string DescribeInstanceName(string gameDirectory)
    {
        var name = Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "游戏目录" : name;
    }

    /// <summary>兜底：取 <c>crash-reports</c> 下最新的 txt，并把它当作最终报告路径。</summary>
    private (string? Text, string? Path) TryReadLatestCrashReport(string gameDirectory)
    {
        try
        {
            var directory = Path.Combine(gameDirectory, "crash-reports");
            if (!Directory.Exists(directory))
            {
                return (null, null);
            }

            var latest = new DirectoryInfo(directory)
                .GetFiles("*.txt")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            // 超过 4 MB 的报告通常是日志刷屏，读了也只会拖慢归因。
            if (latest is null || latest.Length > 4 * 1024 * 1024)
            {
                return (null, null);
            }

            _log.Info($"已读取崩溃报告：{latest.FullName}");
            return (File.ReadAllText(latest.FullName), latest.FullName);
        }
        catch (Exception ex)
        {
            _log.Warn("读取崩溃报告失败", ex);
            return (null, null);
        }
    }

    // ==================== 自动内存 ====================

    /// <summary>
    /// 按可用内存自动换算最大堆（PCL2 <c>LaunchArgumentRam</c> 的自动档）。
    ///
    /// 只在 <see cref="LaunchOptions.AutoMaxMemory"/> 为真时生效。调用方（App 层）已保证
    /// 「实例显式设置过内存」时传 false，所以这里不再判断实例——两处都判反而容易互相覆盖。
    /// 读不到可用内存时保留原值：宁可让用户手动设置生效，也不要猜出一个可能撑爆内存的数。
    /// </summary>
    private LaunchOptions ApplyAutoMemory(LaunchOptions options, IProgress<LaunchProgress>? progress)
    {
        if (!options.AutoMaxMemory)
        {
            return options;
        }

        var availableBytes = MemoryAdvisor.TotalAvailableBytes();
        if (availableBytes <= 0)
        {
            _log.Warn("读不到可用内存，保留设置里的最大内存");
            return options;
        }

        var (modCount, hasOptiFine) = CountModsForMemory(options);
        var suggested = MemoryAdvisor.SuggestMaxMemoryMb(availableBytes, modCount, hasOptiFine);

        if (suggested <= 0)
        {
            return options;
        }

        // 建议值低于最小堆时 JVM 会拒绝 -Xms > -Xmx 的组合，抬到最小堆。
        var effective = Math.Max(suggested, options.MinMemoryMb);
        var availableGb = availableBytes / 1024d / 1024d / 1024d;

        _log.Info(
            $"按可用内存 {availableGb:0.0} GB、{modCount} 个模组自动分配 {effective} MB"
            + (hasOptiFine ? "（含 OptiFine）" : string.Empty));

        progress?.Report(new LaunchProgress(
            LaunchPhase.Preparing,
            $"已按可用内存自动分配 {effective / 1024d:0.0} GB"));

        return CloneWithMaxMemory(options, effective);
    }

    /// <summary>
    /// 复制一份只改 <see cref="LaunchOptions.MaxMemoryMb"/> 的启动参数。
    ///
    /// <see cref="LaunchOptions"/> 是 class + init 属性，没有 <c>with</c> 表达式，只能逐字段复制。
    /// <b>给 LaunchOptions 加字段时必须同步这里</b>，否则该字段在「自动内存开启」的启动路径上
    /// 会被静默重置成默认值——这类问题只在开了自动内存的机器上出现，最难排查。
    /// </summary>
    private static LaunchOptions CloneWithMaxMemory(LaunchOptions options, int maxMemoryMb) =>
        new()
        {
            Account = options.Account,
            VersionName = options.VersionName,
            GameDirectory = options.GameDirectory,
            JavaPath = options.JavaPath,

            // 加载器版本不是启动参数，是 Java 上限判定的输入。漏拷的后果是「开了自动内存的机器上，
            // 1.19.4 Forge 45.0.40 + Java 20 不再被前置检查拦下」，且只在这条路径复现。
            // 守护用例：CoreLaunch_LaunchOptionsCloneTests（反射逐属性比对）。
            LoaderVersion = options.LoaderVersion,
            MinMemoryMb = options.MinMemoryMb,
            MaxMemoryMb = maxMemoryMb,
            WindowWidth = options.WindowWidth,
            WindowHeight = options.WindowHeight,
            Fullscreen = options.Fullscreen,
            ServerAddress = options.ServerAddress,
            ExtraJvmArguments = options.ExtraJvmArguments,
            ExtraGameArguments = options.ExtraGameArguments,
            DemoMode = options.DemoMode,
            AutoMaxMemory = options.AutoMaxMemory,
            GcKind = options.GcKind,
            WindowTitle = options.WindowTitle,
            MaximizeWindow = options.MaximizeWindow,
            LaunchBeforeCommand = options.LaunchBeforeCommand,
            LaunchAfterCommand = options.LaunchAfterCommand,
            WaitForLaunchCommands = options.WaitForLaunchCommands,

            // 这三项曾经漏在这里：开了「自动分配内存」的启动路径上，它们会被静默重置成默认值
            // （优先级回到普通、中文语言失效、崩溃报告不再归档），而用户界面上开关明明还开着。
            // 守护用例：CoreLaunch_LaunchOptionsCloneTests 用反射逐属性比对，漏一个就红。
            ProcessPriority = options.ProcessPriority,
            AutoChineseLanguage = options.AutoChineseLanguage,
            ArchiveCrashReport = options.ArchiveCrashReport,

            // 加在后面的新字段同样必须复制：这一行漏掉，用户关掉「放宽校验」之后，
            // 只要开了自动内存，三条 -D 又会悄悄回来。
            RelaxForgeChecks = options.RelaxForgeChecks,

            // 实例级版本信息：漏拷的表现是「实例里填了版本名，开了自动内存的机器上主菜单又变回全局值」，
            // 而且只在开了自动内存时复现。守护用例：CoreLaunch_LaunchOptionsCloneTests。
            VersionArgumentInfo = options.VersionArgumentInfo,

            // 实例级登录服务器：三态值原样搬运（null 跟随账户 / 空串明确关闭 / 值覆盖）。
            // 漏这一行，开了自动内存的机器上「实例绑定的验证服」会静默失效、退回账户的全局地址。
            InstanceAuthServerUrl = options.InstanceAuthServerUrl,
        };

    /// <summary>
    /// 数模组文件与 OptiFine。只扫顶层（mods 里嵌套目录里的 jar 不会被游戏加载，数进来只会虚高）。
    /// 目录不存在、枚举中途被占用都按「数到的部分」返回，不影响启动。
    /// </summary>
    private (int ModCount, bool HasOptiFine) CountModsForMemory(LaunchOptions options)
    {
        var modsDirectory = Path.Combine(ResolveGameDirectory(options), "mods");

        if (!Directory.Exists(modsDirectory))
        {
            return (0, false);
        }

        var modCount = 0;
        var hasOptiFine = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(modsDirectory))
            {
                var name = Path.GetFileName(file);

                if (IsModFileName(name))
                {
                    modCount++;
                }

                // 认不出就一律 false：宁可少给内存（后果是卡顿）也不要多给（后果是 OOM 崩溃）。
                if (!hasOptiFine && name.Contains("optifine", StringComparison.OrdinalIgnoreCase))
                {
                    hasOptiFine = true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"枚举模组目录失败：{ex.Message}");
        }

        return (modCount, hasOptiFine);
    }

    private static bool IsModFileName(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    // ==================== 版本发行年 ====================

    /// <summary>
    /// 读版本的发行年份，供退出判定区分「老版本正常非零退出」与「现代版本崩溃」。
    ///
    /// 加载器版本（Forge / Fabric）的 JSON 常常不带 <c>releaseTime</c>，要沿 <c>inheritsFrom</c>
    /// 回溯到原版那一份。读不到返回 0：按 PCL2 语义，未知发行年不据此判崩溃。
    /// </summary>
    private async Task<int> ResolveReleaseYearAsync(LaunchOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var name = options.VersionName;

            for (var depth = 0; depth < VersionInheritDepth && !string.IsNullOrWhiteSpace(name); depth++)
            {
                var jsonPath = Path.Combine(_paths.VersionsDirectory, name, name + ".json");

                if (!File.Exists(jsonPath))
                {
                    break;
                }

                var json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var rawReleaseTime = TryGetStringProperty(root, "releaseTime");
                if (rawReleaseTime is not null &&
                    DateTimeOffset.TryParse(
                        rawReleaseTime,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var releaseTime))
                {
                    return releaseTime.Year;
                }

                name = TryGetStringProperty(root, "inheritsFrom") ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            // 版本 JSON 可能正在被另一个进程改写；拿不到年份只影响退出判定的严格程度。
            _log.Debug($"读取版本发行时间失败：{ex.Message}");
        }

        return 0;
    }

    private static string? TryGetStringProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ==================== 阶段与退出文案 ====================

    private void ReportStartupStage(IProgress<LaunchProgress>? progress, GameStartupStage stage)
    {
        try
        {
            progress?.Report(new LaunchProgress(
                LaunchPhase.Running,
                GameWatcher.DescribeStage(stage),
                StartupStage: stage));
        }
        catch (Exception ex)
        {
            // 进度推送失败不能影响游戏输出线程。
            _log.Debug($"推送启动阶段失败：{ex.Message}");
        }
    }

    private static string DescribeExit(int exitCode, GameExitVerdict verdict)
    {
        if (verdict == GameExitVerdict.Crashed)
        {
            return exitCode == 0
                ? "游戏在加载完成前退出，已按崩溃处理。"
                : $"游戏异常退出（退出码 {exitCode}）。";
        }

        return exitCode == 0 ? "游戏已正常退出。" : $"游戏已退出（退出码 {exitCode}）。";
    }

    // ==================== 窗口接管 ====================

    /// <summary>
    /// 轮询查找游戏窗口，找到后按设置改标题 / 最大化，并把启动阶段推到
    /// <see cref="GameStartupStage.MainMenu"/>。
    ///
    /// 即使不改标题也要找：窗口出现是「真的进主菜单了」的唯一凭据，比日志里程碑更可靠。
    /// 整个方法不抛异常——窗口操作失败只影响标题，不该影响启动结果。
    /// </summary>
    private async Task TakeOverGameWindowAsync(
        Process process,
        LaunchOptions options,
        GameWatcher watcher,
        CancellationToken cancellationToken)
    {
        if (_platform is null)
        {
            return;
        }

        for (var attempt = 0; attempt < WindowProbeAttempts; attempt++)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested || HasExited(process))
                {
                    return;
                }

                await Task.Delay(WindowProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }

            IntPtr? handle;

            try
            {
                handle = _platform.FindGameWindow(process.Id);
            }
            catch (Exception ex)
            {
                _log.Debug($"查找游戏窗口失败：{ex.Message}");
                return;
            }

            if (handle is null)
            {
                continue;
            }

            watcher.AdvanceTo(GameStartupStage.MainMenu);

            try
            {
                if (options.MaximizeWindow && !_platform.TryMaximizeWindow(handle.Value))
                {
                    _log.Debug("最大化游戏窗口失败");
                }

                var title = ExpandWindowTitle(options.WindowTitle, options);
                if (title is not null && !_platform.TrySetWindowTitle(handle.Value, title))
                {
                    _log.Debug($"设置游戏窗口标题失败：{title}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("窗口接管失败", ex);
            }

            return;
        }

        _log.Debug("超时未找到游戏窗口，跳过标题 / 最大化");
    }

    /// <summary>展开窗口标题模板。支持 <c>{version}</c> / <c>{user}</c> / <c>{date}</c> / <c>{time}</c>。</summary>
    private static string? ExpandWindowTitle(string? template, LaunchOptions options)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var title = template
            .Replace("{version}", options.VersionName, StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", options.Account.Username, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            // 进程句柄已释放 / 从未启动：都等价于「已经不在跑」。
            return true;
        }
    }

    /// <summary>
    /// 等 stdout/stderr 的异步读取回调处理完。
    /// <see cref="Process.WaitForExit()"/>（无超时重载）会等待 <c>BeginOutputReadLine</c> 挂起的事件，
    /// 进程已经退出时它立刻返回。
    /// </summary>
    /// <remarks>读取线程可能已经释放进程句柄，失败就当排空完成。</remarks>
    private static void WaitForOutputDrain(Process process)
    {
        try
        {
            process.WaitForExit();
        }
        catch (Exception)
        {
            // 输出排空失败只影响最后几行的判定，不改变启动结果。
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 窗口轮询是可选增强，任何异常都在内部处理过；这里只是兜底，避免未观察异常。
        }
    }

    // ==================== 启动前 / 退出后命令 ====================

    /// <summary>
    /// 执行用户配置的启动前 / 退出后命令（PCL2 <c>VersionAdvanceRun</c>）。
    ///
    /// 规则：
    /// <list type="bullet">
    /// <item>用系统 shell（Windows <c>cmd /c</c>，其他平台 <c>/bin/sh -c</c>），用户可写管道与重定向；</item>
    /// <item>工作目录固定为游戏目录，脚本里写相对路径指向 mods / config 才有意义；</item>
    /// <item>非零退出码只记日志：脚本失败通常是「没拷到文件」，把它当启动失败会拦住本来能跑的游戏；</item>
    /// <item><see cref="LaunchOptions.WaitForLaunchCommands"/> 为 false 时投递即返回，与 PCL2 的「不等待」语义一致。</item>
    /// </list>
    /// 取消启动时抛 <see cref="OperationCanceledException"/>，由 <c>LaunchAsync</c> 统一转成「启动已取消」。
    /// </summary>
    private async Task RunLaunchCommandAsync(
        string? command,
        string stage,
        LaunchOptions options,
        JavaInfo? java,
        int exitCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var gameDirectory = ResolveGameDirectory(options);
        var expanded = ExpandCommandPlaceholders(
            command!,
            gameDirectory,
            options.VersionName,
            java?.Path,
            exitCode);

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return;
        }

        var (fileName, arguments) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("cmd.exe", $"/c {expanded}")
            : ("/bin/sh", $"-c \"{expanded}\"");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = gameDirectory,
                },
            };

            if (!process.Start())
            {
                _log.Warn($"{stage}命令无法启动：{expanded}");
                return;
            }

            _log.Info($"已执行{stage}命令：{expanded}");

            if (!options.WaitForLaunchCommands)
            {
                // 不等：Process 释放后子进程继续运行，等价于用户在资源管理器里双击脚本。
                return;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _log.Warn($"{stage}命令退出码 {process.ExitCode}：{expanded}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"执行{stage}命令失败：{expanded}", ex);
        }
    }

    private static string ExpandCommandPlaceholders(
        string command,
        string gameDirectory,
        string versionName,
        string? javaPath,
        int exitCode) =>
        command
            .Replace("{gameDir}", gameDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", versionName, StringComparison.OrdinalIgnoreCase)
            .Replace("{java}", javaPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{exitCode}", exitCode.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static string TranslateStartupException(Exception ex)
    {
        var message = ex.Message;

        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "版本文件缺失或路径无效，请重新安装该版本。";
        }

        if (ex is UnauthorizedAccessException)
        {
            return "没有权限访问游戏目录，请检查杀毒软件或目录权限。";
        }

        if (message.Contains("version", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "找不到指定版本，请先安装它。";
        }

        if (ex is HttpRequestException or TaskCanceledException)
        {
            return "下载游戏文件失败，请检查网络或更换下载源。";
        }

        return $"启动失败：{message}";
    }

    private static string Compose(string fileName, string arguments) =>
        string.IsNullOrEmpty(arguments) ? fileName : $"{fileName} {arguments}";

    /// <summary>
    /// 切分用户填写的参数串。走 <see cref="LaunchArgumentSplitter"/>：
    /// CmlLib 的解析器会把 Windows 路径里的反斜杠当转义符吃掉。
    /// </summary>
    private static List<string> SplitArguments(string? input) => LaunchArgumentSplitter.Split(input);

    private static (string Host, int Port) SplitServerAddress(string address)
    {
        // 规则只留在 ServerAddressParser：服务器页 Ping 与这里曾经各写一套，
        // 对 `[::1]:port` 的处理不一致。
        var parsed = NebulaLauncher.Core.Servers.ServerAddressParser.Parse(address);
        return (parsed.Host, parsed.Port);
    }

    /// <summary>
    /// 同步转发的 <see cref="IProgress{T}"/>：CmlLib 的进度回调来自工作线程，
    /// 用 <see cref="Progress{T}"/> 会被投递到线程池，导致 UI 收到乱序的进度。
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>
/// 一个在跑的游戏会话：进程句柄 + 它自己的输出尾巴与日志监视器。
///
/// 为什么这三样必须绑成一个对象：它们同生共死。单开时代「同时只有一个进程」隐式保证了
/// 「最后 200 行就是这次的崩溃现场」；多开之后这条假设不成立，A 的行会混进 B 的尾巴，
/// 崩溃归因就会把 A 的现场算到 B 头上——这是本缺口里最难排查的一类错误。
/// </summary>
internal sealed class RunningGame
{
    private readonly Func<bool> _hasExited;
    private readonly Action _kill;
    private readonly Queue<string> _outputTail = new();
    private readonly object _tailSync = new();

    /// <summary>
    /// 进程是否已经真正 Start 过。注册会话发生在 Start 之前（先占位再起进程，
    /// 否则两个并发启动会一起走到 Start），那段时间 <see cref="Process"/>.HasExited
    /// 会抛 <see cref="InvalidOperationException"/>——按「已占位即视为在跑」处理，
    /// 让占位本身成为准入闸门。
    /// </summary>
    private volatile bool _started;

    /// <param name="hasExited">
    /// 判活委托。生产路径传 null（用 <paramref name="process"/> 的真实状态）；
    /// 单测传自己的委托，才能在不拉起 JVM 的前提下验证多开的准入与按会话停止。
    /// </param>
    /// <param name="kill">终止委托，理由同上。</param>
    public RunningGame(
        string sessionId,
        string instanceKey,
        string instanceName,
        string versionName,
        Process? process,
        GameWatcher watcher,
        DateTimeOffset startedAt,
        Func<bool>? hasExited = null,
        Action? kill = null)
    {
        SessionId = sessionId;
        InstanceKey = instanceKey;
        InstanceName = instanceName;
        VersionName = versionName;
        Process = process;
        Watcher = watcher;
        StartedAt = startedAt;

        _hasExited = hasExited ?? (() => Process is null || Process.HasExited);
        _kill = kill ?? (() =>
        {
            if (Process is not null && !Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        });
    }

    /// <summary>本次启动的会话标识，按会话停止时用它。</summary>
    public string SessionId { get; }

    /// <summary>规范化后的游戏目录，同时也是集合里的键。</summary>
    public string InstanceKey { get; }

    /// <summary>给界面看的实例名（Core 侧只能拿目录名兜底）。</summary>
    public string InstanceName { get; }

    public string VersionName { get; }

    /// <summary>真实进程；测试构造的会话为 null。</summary>
    public Process? Process { get; }

    /// <summary>本会话的日志监视器，阶段与崩溃信号都只属于它。</summary>
    public GameWatcher Watcher { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// 还活着吗。取真实 <c>HasExited</c>，不用「已经注册过」这种内存标志——
    /// 用户在任务管理器里结束进程时，启动器不会收到任何通知。
    /// </summary>
    public bool IsAlive
    {
        get
        {
            // 还没 Start：占位阶段，视为活着（见 _started 的注释）。
            if (!_started)
            {
                return true;
            }

            try
            {
                return !_hasExited();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 从未 Start 过或已被 Dispose 的 Process，两个异常都等价于「没在跑」。
                return false;
            }
        }
    }

    /// <summary>进程已成功 Start，此后 <see cref="IsAlive"/> 改看真实进程状态。</summary>
    public void MarkStarted() => _started = true;

    /// <summary>往本会话的尾巴里追加一行，并维持长度上限。</summary>
    public void AppendOutput(string line)
    {
        // 尾巴自己持锁：多开时两个读取线程会并发写各自的尾巴，
        // 集合的锁管不到这里，也不该为了写一行日志去抢全局锁。
        lock (_tailSync)
        {
            _outputTail.Enqueue(line);

            while (_outputTail.Count > CrashAnalyzer.DefaultTailLines)
            {
                _outputTail.Dequeue();
            }
        }
    }

    /// <summary>本会话尾巴的快照。</summary>
    public string[] OutputSnapshot()
    {
        lock (_tailSync)
        {
            return _outputTail.ToArray();
        }
    }

    /// <summary>
    /// 结束本进程。返回是否真的发出了终止请求：已经退出或句柄失效时返回 false，
    /// 调用方据此刷新界面而不是报错。
    /// </summary>
    public bool Kill(IAppLog? log)
    {
        if (!IsAlive)
        {
            return false;
        }

        try
        {
            _kill();
            log?.Info($"已强制结束游戏进程（会话 {SessionId}，版本 {VersionName}）");
            return true;
        }
        catch (Exception ex)
        {
            // 杀不掉是用户可见的失败（界面要说「请到任务管理器结束」），不能静默吞掉。
            log?.Warn($"结束游戏进程失败（会话 {SessionId}，版本 {VersionName}）", ex);
            return false;
        }
    }

    public RunningGameInfo ToInfo() => new(SessionId, InstanceKey, InstanceName, VersionName, StartedAt);
}

/// <summary>
/// 在跑的会话表：键 = 规范化游戏目录，大小写不敏感。
///
/// 为什么单独成类而不是继续用 <see cref="GameLauncher"/> 的字段：
/// 多开的核心判定（同目录拒第二次、按会话只杀一个、IsGameRunning 是「任一」）
/// 全部落在这张表上，而 <see cref="GameLauncher.LaunchAsync"/> 依赖真实 JVM 无法在单测里跑。
/// 把表抽出来，这几条判定就能脱离进程直接验证。
/// </summary>
internal sealed class RunningGameRegistry
{
    private readonly object _sync = new();

    // Windows 路径大小写不敏感：用 Ordinal 会让 D:\Game 与 d:\game 变成两个键，
    // 「同一目录只能一个 JVM」当场失效。
    private readonly Dictionary<string, RunningGame> _games = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否有任一会话在跑（含正在启动、尚未 Start 的占位会话）。</summary>
    public bool AnyRunning
    {
        get
        {
            lock (_sync)
            {
                return _games.Values.Any(game => game.IsAlive);
            }
        }
    }

    /// <summary>在跑会话的只读投影，按启动时刻升序。</summary>
    public IReadOnlyList<RunningGameInfo> Snapshot()
    {
        lock (_sync)
        {
            return _games.Values
                .Where(game => game.IsAlive)
                .OrderBy(game => game.StartedAt)
                .Select(game => game.ToInfo())
                .ToArray();
        }
    }

    /// <summary>该目录是否已有会话在跑。</summary>
    public bool IsInstanceRunning(string instanceKey)
    {
        lock (_sync)
        {
            return _games.TryGetValue(instanceKey, out var game) && game.IsAlive;
        }
    }

    /// <summary>
    /// 占位。同一个键已被活会话占用时返回 false。
    ///
    /// 这是并发启动的第一道真闸门：LaunchAsync 入口的准入检查与这里之间隔着
    /// 前置检查、选 Java、补齐文件（可能几分钟），两次点击完全可能同时通过入口判定。
    /// </summary>
    public bool TryRegister(RunningGame game)
    {
        lock (_sync)
        {
            if (_games.TryGetValue(game.InstanceKey, out var existing) && existing.IsAlive)
            {
                return false;
            }

            _games[game.InstanceKey] = game;
            return true;
        }
    }

    /// <summary>
    /// 注销。按引用比较：同目录理论上不会并存两个会话，但「启动失败后立刻又发起一次」
    /// 这类时序下直接按 key 删有概率删掉后来者，让它变成看不见的幽灵进程。
    /// </summary>
    public bool Unregister(RunningGame game)
    {
        lock (_sync)
        {
            if (_games.TryGetValue(game.InstanceKey, out var registered) && ReferenceEquals(registered, game))
            {
                return _games.Remove(game.InstanceKey);
            }

            return false;
        }
    }

    /// <summary>按会话停止。返回 false = 该会话不存在或已经退出。</summary>
    public bool KillBySession(string sessionId, IAppLog? log)
    {
        RunningGame? target;

        lock (_sync)
        {
            target = _games.Values.FirstOrDefault(game =>
                string.Equals(game.SessionId, sessionId, StringComparison.Ordinal));
        }

        // 杀进程放在锁外：TerminateProcess 可能阻塞几百毫秒，持锁期间
        // UI 线程问 IsGameRunning / RunningGames 会跟着卡住，看起来像界面死了。
        return target is not null && target.Kill(log);
    }

    /// <summary>结束全部在跑的会话，返回真正发出终止请求的个数。</summary>
    public int KillAll(IAppLog? log)
    {
        RunningGame[] games;

        lock (_sync)
        {
            games = _games.Values.ToArray();
        }

        var killed = 0;
        foreach (var game in games)
        {
            if (game.Kill(log))
            {
                killed++;
            }
        }

        return killed;
    }
}
