using System.Collections.Concurrent;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 启动前置检查：在拉起 Java 进程之前，把「注定起不来」和「大概率出问题」的组合挑出来。
///
/// 拆成两段，顺序由调用方（<c>GameLauncher</c>）编排：
/// <list type="number">
/// <item><see cref="CheckEnvironmentAsync"/>：不依赖 Java 的部分（目录、磁盘、路径字符、内存、加载器完整性、模组）。
///       带 2 秒总预算——这段只有文件系统与 jar 元数据读取，没有子进程探测。</item>
/// <item>调用方自己选 Java（保持它已有的「需要 Java X 本机只有 Y」文案）。</item>
/// <item><see cref="CheckJava"/>：拿已选中的 <see cref="JavaInfo"/> 与算好的区间做纯判定，同步、零探测、不受预算影响。</item>
/// </list>
///
/// 为什么不能把 Java 探测塞进第一段：冷启动时 `DetectAsync` 要扫上百个候选并起子进程，
/// 2 秒预算会把它砍掉，导致「1.12.2 Forge + Java 17」这个最有价值的 Blocker 在首次启动时形同虚设。
///
/// 三条兜底策略：
/// <list type="number">
/// <item>物理内存探测不可用时（容器 / WSL 下 <c>TotalAvailableMemoryBytes</c> 可能为 0）跳过内存项，不误判 Blocker；</item>
/// <item>环境段有 2 秒总预算，超时就返回已收集到的项，绝不拖慢启动；</item>
/// <item>任何单项抛异常都吞掉并跳过——一次探测失败不能变成「游戏打不开」。</item>
/// </list>
/// </summary>
public static class PreflightChecker
{
    /// <summary>磁盘剩余低于这个值直接阻止启动。</summary>
    public const long BlockerFreeBytes = 200L * 1024 * 1024;

    /// <summary>磁盘剩余低于这个值给警告。</summary>
    public const long WarnFreeBytes = 1024L * 1024 * 1024;

    /// <summary>32 位 Java 的堆上限参考值（MB）。超过它就一定能分配失败。</summary>
    public const int LargeHeapOn32BitMb = 1536;

    /// <summary>请求堆超过可用内存的这个比例时给警告。堆与模组实际占用没有因果关系，只能警告。</summary>
    public const double MemoryWarningRatio = 0.70;

    /// <summary>环境段的时间预算。超时返回已收集项。Java 段不在这个预算内。</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(2);

    /// <summary>目录写探测的单次预算。真实磁盘往返放在启动路径上不能太长。</summary>
    public static readonly TimeSpan WriteProbeTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan WriteProbeCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Failure)> WriteProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    // ==================== 第一段：不依赖 Java ====================

    /// <summary>
    /// 环境段：目录可写、磁盘空间、路径特殊字符、<c>-Xmx</c> 与物理内存、加载器完整性、模组冲突。
    ///
    /// 这一段<b>不接收也不调用 <see cref="IJavaService"/></b>，所以 2 秒预算只约束文件系统操作，
    /// 不会因为 Java 探测太慢而丢掉 Java 相关的 Blocker。
    /// </summary>
    /// <param name="options">启动参数；<see cref="LaunchOptions.MaxMemoryMb"/> 与 <see cref="LaunchOptions.GameDirectory"/> 参与判定。</param>
    /// <param name="versions">用于读取 <see cref="VersionInfo.BrokenReason"/> 与检查加载器 jar。</param>
    /// <param name="content">模组扫描与冲突检测；null 时跳过模组项。</param>
    /// <param name="paths">用于版本目录与默认游戏目录；null 时用 <paramref name="platform"/> 或 <see cref="LaunchOptions.GameDirectory"/>。</param>
    /// <param name="platform">平台服务，仅用于游戏目录兜底。</param>
    /// <param name="minecraftVersion">解析后的原版版本号，用于模组版本匹配；null 时退回 <see cref="LaunchOptions.VersionName"/>。</param>
    /// <param name="cancellationToken">外部取消标记。取消会向上抛，2 秒超时不会。</param>
    public static async Task<PreflightReport> CheckEnvironmentAsync(
        LaunchOptions options,
        IVersionService versions,
        IContentService? content,
        ILauncherPaths? paths = null,
        IPlatformService? platform = null,
        string? minecraftVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(versions);

        var gameDirectory = ResolveGameDirectory(options, paths, platform);
        var version = string.IsNullOrWhiteSpace(minecraftVersion) ? options.VersionName : minecraftVersion!;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TotalTimeout);
        var token = timeoutCts.Token;

        // 便宜的检查放前面：时间预算被慢扫描吃掉时，至少目录 / 内存这些关键项已经跑完。
        var checks = new List<Func<CancellationToken, Task<IReadOnlyList<PreflightItem>>>>
        {
            ct => CheckWritableAsync(gameDirectory, ct),
            _ => Task.FromResult(CheckFreeSpace(gameDirectory)),
            _ => Task.FromResult(CheckPathCharacters(gameDirectory)),
            _ => Task.FromResult(CheckMemory(options.MaxMemoryMb)),
            ct => CheckLoaderAsync(versions, paths, options, ct),
            ct => CheckModsAsync(content, gameDirectory, version, ct),
        };

        var items = new List<PreflightItem>();

        foreach (var check in checks)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                items.AddRange(await check(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 2 秒总预算用完：保留已收集项，不再往下跑。
                break;
            }
            catch (Exception)
            {
                // 兜底策略 3：单项探测失败只跳过该项。
            }
        }

        return new PreflightReport(items);
    }

    // ==================== 第二段：依赖已选中的 Java（纯函数） ====================

    /// <summary>
    /// Java 段：32 位运行时的堆上限、Java 主版本是否落在允许区间内。
    ///
    /// <b>同步纯函数，不做任何探测</b>：<paramref name="java"/> 是调用方已经选好的运行时，
    /// <paramref name="allowedRange"/> 是调用方用 <see cref="IJavaService.GetAllowedRange"/> 算好的区间。
    /// 因此这一段的结论与机器上装了几个 Java、首次启动有没有缓存都无关。
    /// </summary>
    /// <param name="options">读取 <see cref="LaunchOptions.MaxMemoryMb"/> 与 <see cref="LaunchOptions.VersionName"/>（仅用于文案）。</param>
    /// <param name="java">已选中的运行时。</param>
    /// <param name="allowedRange">允许的主版本闭区间；null 表示不限或未知版本。</param>
    /// <param name="loadLimit">
    /// 加载器小版本带来的额外上限（<see cref="JavaRequirementTable.GetLoaderLimit"/> 的结果）。
    /// null 表示这一层没有结论，行为与加这个参数之前完全一致。
    /// </param>
    public static PreflightReport CheckJava(
        LaunchOptions options,
        JavaInfo java,
        (int Min, int Max)? allowedRange,
        JavaLoadLimit? loadLimit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(java);

        var items = new List<PreflightItem>();
        items.AddRange(Check32BitHeap(java, options.MaxMemoryMb));

        // 加载器级判定的文案比「区间不匹配」具体得多（能说出是哪个 Forge build 卡在哪一代 Java），
        // 命中阻断时就用它，不再叠一条泛化的区间提示——否则用户会看到两条说的是同一件事。
        var loaderItems = CheckLoaderLimit(java, loadLimit);
        items.AddRange(loaderItems);

        if (!loaderItems.Any(item => item.Severity == PreflightSeverity.Blocker))
        {
            items.AddRange(CheckJavaRange(java, allowedRange, options.VersionName));
        }

        return new PreflightReport(items);
    }

    /// <summary>
    /// 加载器小版本专属上限：阻断级违规是 Blocker，建议级违规只是 Warning。
    ///
    /// 为什么补丁号级只能警告：<c>8u320</c> 是「当时社区验证过的最高补丁号」，不是硬边界。
    /// 用户手上是 <c>8u322</c> 时 1.16.5 Forge 通常照样能跑，把这种情况做成 Blocker
    /// 会让他无处可去——他没法为了一个建议去装一个更旧的 JDK。大版本边界（&lt; J17 / &lt; J22）
    /// 则是 modlauncher 的字节码 / 反射失败，越过去必崩，那时阻断反而省下一次崩溃。
    /// </summary>
    public static IReadOnlyList<PreflightItem> CheckLoaderLimit(JavaInfo? java, JavaLoadLimit? loadLimit)
    {
        if (java is null || loadLimit is null)
        {
            return [];
        }

        // 更新号只从版本串里来：JavaInfo 上的 MajorVersion 是探测时算好的，以它为准；
        // 版本串解析不出来更新号时 Exceeds 会判「未超出」——建议级信息不足就少提示一次。
        var (parsedMajor, update) = JavaService.ParseJavaVersion(java.Version);
        var major = java.MajorVersion > 0 ? java.MajorVersion : parsedMajor;

        if (!loadLimit.Exceeds(major, update))
        {
            return [];
        }

        var installed = string.IsNullOrWhiteSpace(java.Version)
            ? $"Java {major}"
            : $"{java.DisplayName}";

        return loadLimit.IsBlocking
            ?
            [
                new PreflightItem(
                    PreflightSeverity.Blocker,
                    "Java 版本超出该加载器支持的范围",
                    $"{loadLimit.Reason}当前用的是 {installed}，越过后会在启动阶段直接崩溃"
                    + "（modlauncher 的字节码校验 / 反射调用失败，不是模组的问题）。"
                    + "请在「设置 → Java」里指定符合要求的运行时。",
                    CrashAnalyzer.Actions.OpenJavaSettings),
            ]
            :
            [
                new PreflightItem(
                    PreflightSeverity.Warning,
                    "Java 版本比该加载器验证过的更新",
                    $"{loadLimit.Reason}当前用的是 {installed}。这个组合通常能启动，"
                    + "但若出现启动失败或崩溃，优先换成建议范围内的 Java 再复现一次。",
                    CrashAnalyzer.Actions.OpenJavaSettings),
            ];
    }

    // ==================== 单项检查（纯函数为主，便于单测） ====================

    /// <summary>游戏目录可写。不可写是 Blocker。</summary>
    private static async Task<IReadOnlyList<PreflightItem>> CheckWritableAsync(string gameDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return [];
        }

        var failure = await ProbeWritableAsync(gameDirectory, ct).ConfigureAwait(false);

        if (failure is null)
        {
            return [];
        }

        return
        [
            new PreflightItem(
                PreflightSeverity.Blocker,
                "游戏目录不可写",
                $"没有写入权限（{failure}）。请把游戏目录移出 Program Files，或给启动器加杀软白名单。",
                CrashAnalyzer.Actions.OpenLogFolder),
        ];
    }

    /// <summary>磁盘剩余空间。&lt; 200 MB 阻断，&lt; 1 GB 警告。</summary>
    public static IReadOnlyList<PreflightItem> CheckFreeSpace(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return [];
        }

        if (PathProbe.CheckFreeSpace(gameDirectory, BlockerFreeBytes) is { } blocker)
        {
            return [new PreflightItem(PreflightSeverity.Blocker, "磁盘可用空间不足", blocker)];
        }

        if (PathProbe.CheckFreeSpace(gameDirectory, WarnFreeBytes) is { } warning)
        {
            return [new PreflightItem(PreflightSeverity.Warning, "磁盘可用空间偏低", warning)];
        }

        return [];
    }

    /// <summary>路径里出现会破坏命令行的字符。任何启动器拼命令行都会踩这个坑。</summary>
    public static IReadOnlyList<PreflightItem> CheckPathCharacters(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return [];
        }

        var hits = new List<string>(2);

        if (gameDirectory.Contains('!', StringComparison.Ordinal))
        {
            hits.Add("!");
        }

        if (gameDirectory.Contains(';', StringComparison.Ordinal))
        {
            hits.Add(";");
        }

        if (hits.Count == 0)
        {
            return [];
        }

        return
        [
            new PreflightItem(
                PreflightSeverity.Blocker,
                "游戏目录路径含特殊字符",
                $"路径含有会破坏命令行的字符 {string.Join(" ", hits.Select(c => $"`{c}`"))}：{gameDirectory}。"
                + "请把游戏目录改到不含这些字符的路径（例如 D:\\Minecraft）。"),
        ];
    }

    /// <summary>
    /// <c>-Xmx</c> 与物理内存的关系。
    ///
    /// 超过物理内存必然分配失败 → Blocker；超过 70% 只给 Warning——堆大小与模组实际占用
    /// 没有因果关系，把 Warning 升成 Blocker 会误伤大量能正常玩的配置。
    /// </summary>
    public static IReadOnlyList<PreflightItem> CheckMemory(int maxMemoryMb)
    {
        if (maxMemoryMb <= 0)
        {
            return [];
        }

        long available;

        try
        {
            available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch (Exception)
        {
            return [];
        }

        // 兜底策略 1：容器 / WSL 下可能返回 0，判断不了就跳过，不能据此判 Blocker。
        if (available <= 0)
        {
            return [];
        }

        var requested = maxMemoryMb * 1024L * 1024;

        if (requested > available)
        {
            return
            [
                new PreflightItem(
                    PreflightSeverity.Blocker,
                    "内存设置超出物理内存",
                    $"为游戏分配了 {maxMemoryMb} MB，本机可用内存只有 {PathProbe.FormatSize(available)}，"
                    + "JVM 会因无法预留堆空间而启动失败。请把最大内存调到可用内存的一半左右。",
                    CrashAnalyzer.Actions.OpenJavaSettings),
            ];
        }

        var softLimit = (long)(available * MemoryWarningRatio);

        if (requested > softLimit)
        {
            return
            [
                new PreflightItem(
                    PreflightSeverity.Warning,
                    "内存设置偏高",
                    $"为游戏分配了 {maxMemoryMb} MB，已超过可用内存 {PathProbe.FormatSize(available)} 的 70%。"
                    + $"建议不超过 {PathProbe.FormatSize(softLimit)}，否则系统可能因换页而卡顿。",
                    CrashAnalyzer.Actions.OpenJavaSettings),
            ];
        }

        return [];
    }

    /// <summary>32 位 Java 请求超过 1.5 GB 堆。32 位进程地址空间封顶，一定分配失败。</summary>
    public static IReadOnlyList<PreflightItem> Check32BitHeap(JavaInfo? java, int maxMemoryMb)
    {
        if (java is null || java.Is64Bit || maxMemoryMb <= LargeHeapOn32BitMb)
        {
            return [];
        }

        return
        [
            new PreflightItem(
                PreflightSeverity.Blocker,
                "32 位 Java 无法使用这么大的内存",
                $"{java.DisplayName} 是 32 位运行时，单进程堆上限约 1.5 GB，当前设置为 {maxMemoryMb} MB。"
                + "请在「设置 → Java」换成 64 位 Java，或把最大内存降到 1536 MB 以内。",
                CrashAnalyzer.Actions.OpenJavaSettings),
        ];
    }

    /// <summary>具体 Java 是否落在版本的允许区间内。只做判定，不探测。</summary>
    public static IReadOnlyList<PreflightItem> CheckJavaRange(
        JavaInfo? java,
        (int Min, int Max)? allowedRange,
        string? versionText)
    {
        if (java is null || allowedRange is not { } range)
        {
            return [];
        }

        if (java.MajorVersion >= range.Min && java.MajorVersion <= range.Max)
        {
            return [];
        }

        var expected = JavaRequirementTable.DescribeJavaRange(range);
        var target = string.IsNullOrWhiteSpace(versionText) ? "该版本" : versionText;

        return
        [
            new PreflightItem(
                PreflightSeverity.Blocker,
                "Java 版本与游戏版本不匹配",
                $"当前 Java {java.MajorVersion}，{target} 允许的区间是 {expected}。"
                + "高版本 JVM 在老版本 Forge 上会因 LaunchWrapper / 字节码校验直接崩溃，"
                + "请在「设置 → Java」指定符合区间的运行时。",
                CrashAnalyzer.Actions.OpenJavaSettings),
        ];
    }

    // ==================== 环境段里需要异步依赖的检查 ====================

    private static async Task<IReadOnlyList<PreflightItem>> CheckLoaderAsync(
        IVersionService versions,
        ILauncherPaths? paths,
        LaunchOptions options,
        CancellationToken ct)
    {
        VersionInfo? info;

        try
        {
            info = await versions.GetVersionAsync(options.VersionName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }

        if (info is null)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(info.BrokenReason))
        {
            return
            [
                new PreflightItem(
                    PreflightSeverity.Blocker,
                    "版本文件损坏",
                    $"{options.VersionName} 的版本 json 无法解析（{info.BrokenReason}）。"
                    + "请在版本页对该版本执行修复，或重新安装。",
                    CrashAnalyzer.Actions.RepairVersion),
            ];
        }

        if (info.Loader == LoaderKind.Vanilla || paths is null)
        {
            return [];
        }

        // 加载器版本的 <id>.jar 是 CmlLib 拼 classpath 时的一环；缺失时启动阶段才报底层异常。
        var versionJar = Path.Combine(paths.VersionsDirectory, options.VersionName, options.VersionName + ".jar");

        if (File.Exists(versionJar))
        {
            return [];
        }

        return
        [
            new PreflightItem(
                PreflightSeverity.Warning,
                "加载器安装可能不完整",
                $"{options.VersionName}（{info.Loader}）缺少 {options.VersionName}.jar，"
                + "启动时可能因找不到主类或库而失败。建议在版本页执行修复。",
                CrashAnalyzer.Actions.RepairVersion),
        ];
    }

    private static async Task<IReadOnlyList<PreflightItem>> CheckModsAsync(
        IContentService? content,
        string gameDirectory,
        string minecraftVersion,
        CancellationToken ct)
    {
        if (content is null || string.IsNullOrWhiteSpace(gameDirectory))
        {
            return [];
        }

        var modsDirectory = Path.Combine(gameDirectory, "mods");

        // 没有 mods 目录就是原版环境，没有任何可检查的。
        if (!Directory.Exists(modsDirectory))
        {
            return [];
        }

        IReadOnlyList<ContentItem> mods;

        try
        {
            mods = await content.ScanAsync(gameDirectory, ContentKind.Mod, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }

        if (mods.Count == 0)
        {
            return [];
        }

        var items = new List<PreflightItem>();

        try
        {
            var conflicts = content.DetectConflicts(mods);

            foreach (var conflict in conflicts)
            {
                items.Add(new PreflightItem(
                    PreflightSeverity.Warning,
                    "存在重复 modid 的模组",
                    $"{conflict.ModId}：{conflict.Detail}。同一 modid 只会加载一个，另一个会被静默忽略或导致崩溃。",
                    CrashAnalyzer.Actions.OpenModsFolder));
            }
        }
        catch (Exception)
        {
            // 单项失败跳过。
        }

        try
        {
            // 可选依赖缺失不提示：模组自己声明了「没有也能跑」。
            var missing = content.DetectDependencyIssues(mods)
                .Where(issue => !issue.Optional)
                .ToList();

            if (missing.Count > 0)
            {
                var lines = string.Join(
                    "；",
                    missing.Take(3).Select(issue => $"{issue.ModId} 需要 {issue.MissingId}"
                        + (string.IsNullOrWhiteSpace(issue.RequiredRange) ? string.Empty : $" {issue.RequiredRange}")));

                var suffix = missing.Count > 3 ? $"（共 {missing.Count} 项）" : string.Empty;

                items.Add(new PreflightItem(
                    PreflightSeverity.Warning,
                    "缺少前置模组",
                    $"{lines}{suffix}。缺少强制前置时加载器会在启动阶段直接失败。",
                    CrashAnalyzer.Actions.OpenModsFolder));
            }
        }
        catch (Exception)
        {
            // 单项失败跳过。
        }

        try
        {
            var mismatched = content.FindVersionMismatch(mods, minecraftVersion);

            if (mismatched.Count > 0)
            {
                var names = string.Join("、", mismatched.Take(3).Select(item => item.FileName));
                var suffix = mismatched.Count > 3 ? $" 等 {mismatched.Count} 个" : string.Empty;

                items.Add(new PreflightItem(
                    PreflightSeverity.Warning,
                    "模组与游戏版本不匹配",
                    $"{names}{suffix} 声明支持的 Minecraft 版本不包含 {minecraftVersion}，"
                    + "启动时可能因找不到目标类而崩溃。",
                    CrashAnalyzer.Actions.OpenModsFolder));
            }
        }
        catch (Exception)
        {
            // 单项失败跳过。
        }

        return items;
    }

    // ==================== 工具 ====================

    /// <summary>带缓存和超时的目录可写探测。返回 null 表示可写（或探测没给出结论）。</summary>
    private static async Task<string?> ProbeWritableAsync(string directory, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (WriteProbeCache.TryGetValue(directory, out var cached) && now - cached.At < WriteProbeCacheTtl)
        {
            return cached.Failure;
        }

        string? failure;

        try
        {
            // 真实写探测可能被杀软拦几百毫秒；放在启动路径上必须有上限。
            failure = await Task
                .Run(() => PathProbe.TestWritable(directory), ct)
                .WaitAsync(WriteProbeTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 兜底：探测超时视为「没有证据」，不判不可写。
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        WriteProbeCache[directory] = (now, failure);
        return failure;
    }

    private static string ResolveGameDirectory(LaunchOptions options, ILauncherPaths? paths, IPlatformService? platform)
    {
        if (!string.IsNullOrWhiteSpace(options.GameDirectory))
        {
            return options.GameDirectory!;
        }

        if (!string.IsNullOrWhiteSpace(paths?.MinecraftDirectory))
        {
            return paths!.MinecraftDirectory;
        }

        return platform?.DefaultMinecraftDirectory ?? string.Empty;
    }
}

/// <summary>
/// <see cref="IPreflightChecker"/> 的实现：内部按「环境段 → 选 Java → Java 段」依次执行。
///
/// 与 <c>GameLauncher</c> 的区别：这里没有外部已经选好的 Java，
/// 所以要自己 <c>ResolveForVersionAsync</c> 一次——这次探测在环境段的 2 秒预算之外，
/// 不会让 Java 相关的 Blocker 被砍掉。
/// </summary>
internal sealed class PreflightCheckerService : IPreflightChecker
{
    private readonly ILauncherPaths _paths;
    private readonly IJavaService _java;
    private readonly IContentService? _content;
    private readonly IPlatformService? _platform;
    private readonly IVersionService _versions;

    public PreflightCheckerService(
        ILauncherPaths paths,
        IVersionService versions,
        IJavaService java,
        IContentService? content = null,
        IPlatformService? platform = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _content = content;
        _platform = platform;
    }

    public async Task<PreflightReport> CheckAsync(
        LaunchOptions options,
        string? minecraftVersion = null,
        LoaderKind loader = LoaderKind.Vanilla,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var environment = await PreflightChecker
            .CheckEnvironmentAsync(options, _versions, _content, _paths, _platform, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (environment.HasBlocker)
        {
            return environment;
        }

        var version = string.IsNullOrWhiteSpace(minecraftVersion) ? options.VersionName : minecraftVersion!;

        JavaInfo? java;

        try
        {
            java = await _java
                .ResolveForVersionAsync(version, loader, options.JavaPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            java = null;
        }

        if (java is null)
        {
            return environment;
        }

        (int Min, int Max)? allowedRange;

        try
        {
            allowedRange = _java.GetAllowedRange(version, loader, loaderVersion);
        }
        catch (Exception)
        {
            allowedRange = null;
        }

        // 加载器级上限单独取一次：它带「阻断 / 建议」分级与面向用户的原因，
        // 而区间只能表达一个数字上界。loaderVersion 为空时 GetLoaderLimit 返回 null，
        // CheckJava 退回只做区间判定的行为。
        var loadLimit = JavaRequirementTable.GetLoaderLimit(version, loader, loaderVersion);

        var javaItems = PreflightChecker.CheckJava(options, java, allowedRange, loadLimit);

        return new PreflightReport([.. environment.Items, .. javaItems.Items]);
    }
}
