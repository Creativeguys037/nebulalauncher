using System.Globalization;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// JVM 参数装配：GC 族参数 + 编码参数（抄 PCL2 <c>McLaunchArgumentsJVM</c> 的 5.3 / 5.4 两节）。
///
/// 为什么参数不由调用方自己拼：GC 参数和编码参数都必须在「用户自定义 JVM 参数之后、游戏主类之前」
/// 生效，而且都要先清掉用户历史设置里留下的同类参数。放在这一个纯函数里，才能保证
/// 「设置页显示的值」与「真正传给 JVM 的值」永远一致，也才能不用 JVM 就能写测试。
///
/// 三条硬规则：
/// <list type="number">
/// <item><see cref="GcKind.Auto"/> 不追加任何 GC 参数——由 JVM 自己按版本选默认值，比启动器猜更稳。</item>
/// <item>追加前先按正则清掉用户已有的 <c>-XX:...GC</c> 一族参数，否则会出现
///       <c>+UseG1GC</c> 与 <c>+UseSerialGC</c> 并存，JVM 取最后一个，设置页的选择形同虚设。</item>
/// <item>编码参数只在用户没写同类 <c>-D</c> 时补：用户显式设过编码就尊重用户，启动器不覆盖。</item>
/// </list>
///
/// 除了 GC 与编码，本类还负责 PCL2 会默认注入的另外两组参数（Nebula 缺口的 G2 / G4 / G6）：
/// <list type="bullet">
/// <item>兼容放宽三条（<c>fml.ignore*</c> 与 <c>allowAmbiguousCommands</c>）：受
///       <see cref="AppSettings.RelaxForgeChecks"/> 控制，默认开。</item>
/// <item><c>-XX:-OmitStackTraceInFastThrow</c>：无条件加，见 <see cref="FastThrowDiagnosticsOption"/> 里的代价说明。</item>
/// <item><c>-XX:+UseCompactObjectHeaders</c>：Java 24+ 才加，见 <see cref="BuildCompactObjectHeadersArguments"/>。</item>
/// </list>
///
/// <para>
/// <b>为什么 <c>-XX:-OmitStackTraceInFastThrow</c> 要默认加（有运行期代价也加）</b>：
/// 打开 fast-throw 后，HotSpot 对反复抛出的同一异常会改抛一个<b>没有堆栈</b>的预分配实例。
/// Minecraft 的日志里于是出现整片 <c>NullPointerException</c> 且 <c>at ...</c> 一行都没有，
/// 崩溃归因（<c>CrashAnalyzer</c>）与模组冲突排查都直接失效。代价是每次抛异常都要走完整的
/// 堆栈填充与符号化，量级是「异常密集型模组的帧率略降」，而不是全局变慢——对正常运行几乎无感，
/// 对已经出问题的实例换来可诊断的日志，这笔交易在启动器侧是划算的。
/// </para>
/// </summary>
public static class JvmArgumentBuilder
{
    /// <summary>G1 从 Java 7 起存在；早于此版本加 <c>UseG1GC</c> 会让 JVM 拒绝启动。</summary>
    public const int G1MinJavaMajor = 7;

    /// <summary>ZGC 在 Java 15 转正；之前只有实验版，参数名不同。</summary>
    public const int ZgcMinJavaMajor = 15;

    /// <summary>分代 ZGC 只在 Java 21 / 22 需要显式打开（23 起成为默认）。</summary>
    public const int GenerationalZgcMinJavaMajor = 21;

    /// <summary>分代 ZGC 显式开关的上界：Java 23 起默认开启，再传会告警。</summary>
    public const int GenerationalZgcMaxJavaMajor = 22;

    /// <summary>Java 18 起 <c>file.encoding</c> 默认 UTF-8，需要 COMPAT 才能保持旧的 ANSI 行为。</summary>
    public const int FileEncodingCompatMinJavaMajor = 18;

    /// <summary>Java 19 起 stdout/stderr 的编码属性改名（去掉 <c>sun.</c> 前缀）。</summary>
    public const int StdEncodingRenameMinJavaMajor = 19;

    /// <summary>Java 12 起 <c>MaxHeapFreeRatio</c> 低于 70 才会被 G1 真正采纳。</summary>
    public const int HeapFreeRatioMinJavaMajor = 12;

    /// <summary>Java 8 需要显式打开并行引用处理，否则 G1 的 Ref Proc 是单线程。</summary>
    public const int ParallelRefProcJavaMajor = 8;

    /// <summary>
    /// 关闭 log4j2 的消息查找（Log4Shell / CVE-2021-44228）。
    ///
    /// 1.7–1.17 的原版客户端自带 log4j 2.0-beta9…2.14.1，Java 版客户端同样是攻击面；
    /// 这个 -D 对所有版本无害（旧版 log4j2 只是不认识这个键），所以无条件加。
    /// </summary>
    public const string Log4jSecurityOption = "-Dlog4j2.formatMsgNoLookups=true";

    /// <summary>
    /// 关掉 HotSpot 的 fast-throw 优化，保住异常堆栈。PCL2 <c>LaunchAdvanceJvm</c> 的默认值之一。
    ///
    /// 运行期代价与「为什么仍要默认加」写在类注释里：没有堆栈的 <c>NullPointerException</c>
    /// 会让崩溃分析与模组排查彻底失明，而代价只在异常密集的实例上体现为小幅掉帧。
    /// 用户若不需要，写进自定义 JVM 参数即可覆盖（本项不提供设置页开关）。
    /// </summary>
    public const string FastThrowDiagnosticsOption = "-XX:-OmitStackTraceInFastThrow";

    /// <summary>
    /// 放宽 JDK 对进程命令行的转义校验，PCL2 <c>LaunchAdvanceJvm</c> 的默认值之一。
    /// 旧版 Forge / OptiFine 安装器与部分模组会在子进程里拼未转义的路径，严格模式下直接失败。
    /// </summary>
    public const string AmbiguousCommandOption = "-Djdk.lang.Process.allowAmbiguousCommands=True";

    /// <summary>
    /// 让 FML 忽略 Minecraft jar 的签名校验，PCL2 <c>LaunchAdvanceJvm</c> 的默认值之一。
    ///
    /// 取的是「信任本地文件」：离线启动、镜像源、手动替换 jar 都会让签名对不上，
    /// 不放宽就是「模组文件看似损坏」。代价是本地 jar 被改动不再被拦，所以受
    /// <see cref="AppSettings.RelaxForgeChecks"/> 控制，用户可关。
    /// </summary>
    public const string ForgeInvalidCertificatesOption = "-Dfml.ignoreInvalidMinecraftCertificates=True";

    /// <summary>
    /// 让 FML 忽略补丁差异（class 文件与 binpatches 不匹配），默认值之一，与
    /// <see cref="ForgeInvalidCertificatesOption"/> 同一取舍，同受 <see cref="AppSettings.RelaxForgeChecks"/> 控制。
    /// </summary>
    public const string ForgePatchDiscrepanciesOption = "-Dfml.ignorePatchDiscrepancies=True";

    /// <summary>
    /// 压缩对象头（JEP 450 / JEP 519）。Minecraft 的对象图里有海量小对象
    /// （方块状态、<c>ItemStack</c>、实体），对象头从 12 字节降到 8 字节后堆占用与 GC 停顿都明显下降。
    /// </summary>
    public const string CompactObjectHeadersOption = "-XX:+UseCompactObjectHeaders";

    /// <summary>
    /// 压缩对象头可用的最低 Java 主版本：Java 24（PCL2 <c>ModLaunch.vb:1340</c> 同为 <c>&gt;= 24</c>）。
    /// 低于它加这个标志，JVM 会以 <c>Unrecognized VM option</c> 直接拒绝启动。
    /// </summary>
    public const int CompactObjectHeadersMinJavaMajor = 24;

    /// <summary>
    /// 仍是实验特性的最后一个版本（24）；Java 25 起转正，不再需要
    /// <c>-XX:+UnlockExperimentalVMOptions</c>。
    /// </summary>
    public const int CompactObjectHeadersExperimentalMaxJavaMajor = 24;

    /// <summary>
    /// 用户 JVM 参数里「GC 族」的识别正则。覆盖 <c>-XX:[+-]?Use*GC</c>、G1/ZGC 的调参项、
    /// 堆空闲比、<c>ZGenerational</c> 等——这些都由本类统一生成，用户旧值必须先清掉。
    ///
    /// <para>
    /// <b>刻意不包含 <c>UseCompactObjectHeaders</c></b>（PCL2 <c>ModLaunch.vb:1337</c> 的清理正则含它）：
    /// PCL2 每次都按 Java 版本重新决定加不加，Nebula 只在 Java 24+ 加。若清掉它而版本不够
    /// （或用户在 24+ 上自己写了一条），用户显式配置就会凭空消失。保留它在用户参数里、
    /// 启动器这边按版本追加，两边都不会互相覆盖。回归用例见
    /// <c>CoreLaunch_JvmDefaultsTests.StripGcArguments_KeepsCompactObjectHeaders</c>。
    /// </para>
    /// </summary>
    private static readonly Regex GcFlagPattern = new(
        @"-XX:[+\-]?(?:\w*GC|ZGenerational|G1\w+|MaxGCPauseMillis|MinHeapFreeRatio|MaxHeapFreeRatio|ParallelRefProcEnabled|PerfDisableSharedMem|MaxPermSize)(?:=\S+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedWhitespace = new(@"\s{2,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// 装配后要追加到用户 JVM 参数末尾的那一段（空格分隔，可能为空串）。
    ///
    /// 调用方应当把返回值拼在<b>清洗后</b>的用户参数之后：清掉旧 GC 参数保证不冲突，
    /// 放在末尾保证 JVM 采信启动器的选择。
    /// </summary>
    /// <param name="gcKind">垃圾回收器偏好；<see cref="GcKind.Auto"/> 不追加 GC 参数。</param>
    /// <param name="javaMajorVersion">Java 主版本；&lt;= 0 表示未知，按「现代 JVM」处理（只加基础参数，不加版本专属项）。</param>
    /// <param name="extraJvmArguments">用户自定义 JVM 参数原文，用于判断编码类 <c>-D</c> 是否已被用户设置。</param>
    /// <param name="relaxForgeChecks">
    /// 是否注入放宽 Forge / 命令行校验的三条 <c>-D</c>（PCL2 默认值）。
    /// 默认 <c>true</c> 保持「不配置也能开模组」；设置页关掉后只剩其余参数。
    /// </param>
    /// <returns>待追加的参数串；没有要加的项时返回空串。</returns>
    public static string Build(
        GcKind gcKind,
        int javaMajorVersion,
        string? extraJvmArguments,
        bool relaxForgeChecks = true)
    {
        var userArguments = StripGcArguments(extraJvmArguments);

        var parts = new List<string>(16);
        AppendTokens(parts, BuildGcArguments(gcKind, javaMajorVersion));
        AppendTokens(parts, BuildCompactObjectHeadersArguments(javaMajorVersion, userArguments));
        AppendTokens(parts, BuildSecurityArguments(userArguments));
        AppendTokens(parts, BuildCompatibilityArguments(relaxForgeChecks, userArguments));
        AppendTokens(parts, BuildEncodingArguments(javaMajorVersion, userArguments));
        return string.Join(' ', OrderedDistinct(parts));
    }

    /// <summary>
    /// 兼容类参数：诊断堆栈 + 放宽 Forge / 命令行校验（PCL2 <c>LaunchAdvanceJvm</c> 四条默认值里的三条）。
    ///
    /// 三条放宽项逐条判断用户是否已显式设置同名键：用户写了 <c>-Dfml.ignorePatchDiscrepancies=False</c>
    /// 就说明他要严格校验，启动器不能悄悄再补一条 <c>=True</c> 把它顶掉。
    /// </summary>
    /// <param name="relaxForgeChecks">是否注入放宽项；<c>false</c> 时仍保留 <see cref="FastThrowDiagnosticsOption"/>。</param>
    /// <param name="extraJvmArguments">用户自定义 JVM 参数原文（已清掉 GC 族）。</param>
    /// <returns>空格分隔的参数串；始终至少含 <see cref="FastThrowDiagnosticsOption"/>。</returns>
    public static string BuildCompatibilityArguments(bool relaxForgeChecks, string? extraJvmArguments)
    {
        var user = extraJvmArguments ?? string.Empty;
        var parts = new List<string>(4) { FastThrowDiagnosticsOption };

        if (relaxForgeChecks)
        {
            if (!ContainsOption(user, "allowAmbiguousCommands"))
            {
                parts.Add(AmbiguousCommandOption);
            }

            if (!ContainsOption(user, "fml.ignoreInvalidMinecraftCertificates"))
            {
                parts.Add(ForgeInvalidCertificatesOption);
            }

            if (!ContainsOption(user, "fml.ignorePatchDiscrepancies"))
            {
                parts.Add(ForgePatchDiscrepanciesOption);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 压缩对象头。只在 Java 24+ 加。
    ///
    /// 这里<b>不能</b>走 <see cref="AtLeast"/> 的「未知版本按现代处理」兜底：该标志在 24 以下不存在，
    /// 探测失败（主版本 0）时猜错一次就是 JVM <c>Unrecognized VM option</c> 起不来，
    /// 而少加它只是少一点性能收益。
    ///
    /// 用户已写同名项（含显式 <c>-XX:-UseCompactObjectHeaders</c>）时不再追加，
    /// 否则最终命令行里同一条参数出现两次，用户以为自己关掉了却仍被打开。
    /// </summary>
    /// <param name="javaMajorVersion">Java 主版本；&lt;= 0 视为未知，不加。</param>
    /// <param name="extraJvmArguments">用户自定义 JVM 参数原文，用于判断是否已显式设置。</param>
    /// <returns>空格分隔的参数串；版本不够时为空串。</returns>
    public static string BuildCompactObjectHeadersArguments(int javaMajorVersion, string? extraJvmArguments)
    {
        if (javaMajorVersion < CompactObjectHeadersMinJavaMajor)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);

        // 24 上它是实验特性，缺 UnlockExperimentalVMOptions 会被 JVM 拒绝；25 起转正。
        // 解锁开关即使由用户自己写过也不冲突（Build 末尾还有有序去重兜底）。
        if (javaMajorVersion <= CompactObjectHeadersExperimentalMaxJavaMajor)
        {
            parts.Add("-XX:+UnlockExperimentalVMOptions");
        }

        if (!ContainsOption(extraJvmArguments ?? string.Empty, "UseCompactObjectHeaders"))
        {
            parts.Add(CompactObjectHeadersOption);
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 安全类参数：目前只有 Log4Shell 止血开关。用户显式设置过同类 <c>-D</c> 时让位。
    /// </summary>
    /// <param name="extraJvmArguments">用户自定义 JVM 参数原文。</param>
    /// <returns>待追加的参数串；无需追加时为空串。</returns>
    public static string BuildSecurityArguments(string? extraJvmArguments)
    {
        return ContainsOption(extraJvmArguments ?? string.Empty, "log4j2.formatMsgNoLookups")
            ? string.Empty
            : Log4jSecurityOption;
    }

    /// <summary>
    /// 清掉用户参数里的 GC 一族参数，其余原样保留（包括带空格的引号段）。
    /// 不用「按空格切分再拼回」是因为 <c>-Djava.library.path="C:\a b"</c> 这类参数会被切坏。
    /// </summary>
    /// <param name="extraJvmArguments">用户自定义 JVM 参数原文。</param>
    /// <returns>清洗后的参数串；入参为空时返回空串。</returns>
    public static string StripGcArguments(string? extraJvmArguments)
    {
        if (string.IsNullOrWhiteSpace(extraJvmArguments))
        {
            return string.Empty;
        }

        var stripped = GcFlagPattern.Replace(extraJvmArguments!, " ");
        return RepeatedWhitespace.Replace(stripped, " ").Trim();
    }

    /// <summary>
    /// 按 GC 族生成参数（不含编码参数）。单独暴露是为了让调用方与测试能只看 GC 部分。
    /// </summary>
    /// <param name="gcKind">垃圾回收器偏好。</param>
    /// <param name="javaMajorVersion">Java 主版本；&lt;= 0 视为未知。</param>
    /// <returns>空格分隔的参数串；不适用时返回空串。</returns>
    public static string BuildGcArguments(GcKind gcKind, int javaMajorVersion)
    {
        return gcKind switch
        {
            GcKind.G1 => BuildG1Arguments(javaMajorVersion),
            GcKind.Z => BuildZgcArguments(javaMajorVersion),
            GcKind.Parallel => "-XX:+UseParallelGC",
            GcKind.Serial => "-XX:+UseSerialGC",
            // Auto：JVM 自己按堆大小与版本选（现代版本默认 G1），启动器不插手。
            _ => string.Empty,
        };
    }

    /// <summary>
    /// 编码参数：<c>stdout/stderr</c> 的输出字符集 + Java 18+ 的 <c>file.encoding=COMPAT</c>。
    ///
    /// 为什么必须显式指定：中文 Windows 上 JVM 的默认字符集与启动器解码用的字符集不一致时，
    /// 游戏输出会整片乱码，崩溃分析读到的日志也跟着失效。
    /// </summary>
    /// <param name="javaMajorVersion">Java 主版本。</param>
    /// <param name="extraJvmArguments">用户自定义 JVM 参数原文，用于判断是否已被用户覆盖。</param>
    /// <returns>空格分隔的参数串；用户已设置同类参数时对应项省略。</returns>
    public static string BuildEncodingArguments(int javaMajorVersion, string? extraJvmArguments)
    {
        var user = extraJvmArguments ?? string.Empty;
        var encodingName = ResolveSystemEncodingName();
        var parts = new List<string>(3);

        // 四个名字里任何一个出现都视为用户自己管编码：stdout/stderr 是一对，拆开设置没有意义。
        if (!ContainsOption(user, "stdout.encoding") && !ContainsOption(user, "stderr.encoding"))
        {
            if (AtLeast(javaMajorVersion, StdEncodingRenameMinJavaMajor))
            {
                parts.Add($"-Dstdout.encoding={encodingName}");
                parts.Add($"-Dstderr.encoding={encodingName}");
            }
            else
            {
                parts.Add($"-Dsun.stdout.encoding={encodingName}");
                parts.Add($"-Dsun.stderr.encoding={encodingName}");
            }
        }

        if (AtLeast(javaMajorVersion, FileEncodingCompatMinJavaMajor) && !ContainsOption(user, "file.encoding"))
        {
            parts.Add("-Dfile.encoding=COMPAT");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 当前系统的 Java 字符集名（取 Windows ANSI 代码页）。取不到时回落到 <c>UTF-8</c>。
    /// </summary>
    public static string ResolveSystemEncodingName()
    {
        try
        {
            return NormalizeEncodingName(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception)
        {
            return "UTF-8";
        }
    }

    /// <summary>
    /// ANSI 代码页 → Java 字符集名。GBK（936）统一换成 GB18030：
    /// 它是 GBK 的超集，GBK 编不出来的生僻字在这里能正常输出，而老版本游戏也不会因此乱码。
    /// </summary>
    /// <param name="ansiCodePage">Windows ANSI 代码页。</param>
    public static string NormalizeEncodingName(int ansiCodePage) => ansiCodePage switch
    {
        936 => "GB18030",
        950 or 951 => "Big5",
        932 => "MS932",
        949 => "MS949",
        874 => "x-windows-874",
        >= 1250 and <= 1258 => $"windows-{ansiCodePage}",
        // 65001（UTF-8）与 0（未知）都按 UTF-8；其余冷门代码页也回落到 UTF-8，
        // 拿不准的字符集名会让 JVM 直接启动失败，不如给一个一定存在的。
        _ => "UTF-8",
    };

    private static string BuildG1Arguments(int javaMajorVersion)
    {
        if (!AtLeast(javaMajorVersion, G1MinJavaMajor))
        {
            return string.Empty;
        }

        var parts = new List<string>(8)
        {
            // PCL2 在所有 GC 分支前都加它：ZGenerational 等开关在部分版本上仍是实验选项，
            // 缺了这行 JVM 会直接报 Unrecognized VM option。
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+UseG1GC",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:G1HeapRegionSize=32M",
            "-XX:MaxGCPauseMillis=50",
        };

        if (javaMajorVersion == ParallelRefProcJavaMajor)
        {
            // Java 8 的 G1 默认单线程处理引用，大堆上 Young GC 会明显变长。
            parts.Add("-XX:+ParallelRefProcEnabled");
        }

        if (AtLeast(javaMajorVersion, HeapFreeRatioMinJavaMajor))
        {
            // Java 12 起 MaxHeapFreeRatio 低于 70 才生效；限定在 40 让 G1 及时归还堆给系统。
            parts.Add("-XX:MinHeapFreeRatio=25");
            parts.Add("-XX:MaxHeapFreeRatio=40");
        }

        return string.Join(' ', parts);
    }

    private static string BuildZgcArguments(int javaMajorVersion)
    {
        if (!AtLeast(javaMajorVersion, ZgcMinJavaMajor))
        {
            return string.Empty;
        }

        var parts = new List<string>(3)
        {
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+UseZGC",
        };

        if (javaMajorVersion is >= GenerationalZgcMinJavaMajor and <= GenerationalZgcMaxJavaMajor)
        {
            // 21 / 22 上分代 ZGC 需要显式打开；23 起已是默认值，再传会被 JVM 告警。
            parts.Add("-XX:+ZGenerational");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 「至少这个主版本」。未知版本（&lt;= 0）按现代 JVM 处理：这些参数在近十年所有 JVM 上都合法，
    /// 而「只有某个版本才该加」的开关（ParallelRefProcEnabled、ZGenerational）在调用点用精确判断，
    /// 不会因为这条兜底被误加。
    /// </summary>
    private static bool AtLeast(int javaMajorVersion, int required) =>
        javaMajorVersion <= 0 || javaMajorVersion >= required;

    private static bool ContainsOption(string arguments, string option) =>
        arguments.Contains(option, StringComparison.OrdinalIgnoreCase);

    private static void AppendTokens(List<string> target, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return;
        }

        target.AddRange(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// 有序去重。Java 24 的 <c>-XX:+UnlockExperimentalVMOptions</c> 会被 GC 分支与压缩对象头分支各产生一次，
    /// 不去重就把同一个 <c>-XX</c> 传给 JVM 两次（JVM 不报错，但「查看启动命令」里看起来像重复注入）。
    /// 用有序去重而不是 <c>HashSet</c>：参数顺序有语义，GC 选择必须留在用户参数之后。
    /// </summary>
    private static IEnumerable<string> OrderedDistinct(List<string> parts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            if (seen.Add(part))
            {
                yield return part;
            }
        }
    }
}
