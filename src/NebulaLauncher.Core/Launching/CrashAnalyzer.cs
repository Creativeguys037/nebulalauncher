using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 游戏日志尾部（最后 N 行）的崩溃归因，以及 crash report 文件的定位与解析。
///
/// 判定分三层短路，规则表见 <see cref="CrashRules"/>：
/// <list type="number">
/// <item>Crit1 精确字符串 → 命中即出结论；</item>
/// <item>Crit2 需要上下文（同一症状在不同上下文下结论不同，或要从报错里抽主语）；</item>
/// <item>堆栈归因 —— 从 crash report 的 <c>Description:</c> 堆栈里抽可疑包名，再反查 Mod List 里的 jar；</item>
/// <item>Crit3 低置信度兜底。</item>
/// </list>
/// 某一层出结论后不再往下判，避免把十几个猜想一起堆给用户。
///
/// 包名黑名单、报告定位标记、报告截取区间与 HMCL 同源：
/// <list type="bullet">
/// <item><c>CRASH_REPORT_LOCATION_PATTERN</c>（CrashReportAnalyzer.java:182）</item>
/// <item><c>extractCrashReport</c>（194-199 行）</item>
/// <item><c>PACKAGE_KEYWORD_BLACK_LIST</c>（204-216 行）与 <c>findKeywordsFromCrashReport</c>（218-250 行）</item>
/// </list>
///
/// 本类无状态：所有成员的入参相同则出参相同，可并发调用。
/// </summary>
public static class CrashAnalyzer
{
    /// <summary>分析时默认只取日志尾部这么多行。</summary>
    public const int DefaultTailLines = 200;

    /// <summary>
    /// 大文件截断时默认保留的头部行数（对齐 PCL2 对 <c>latest.log</c> 的 1500 / 500 策略：
    /// 崩溃前因常在日志开头，纯靠尾部会漏）。
    /// </summary>
    public const int LargeLogHeadLines = 1500;

    /// <summary>大文件截断时默认保留的尾部行数。</summary>
    public const int LargeLogTailLines = 500;

    /// <summary>单次分析最多给出的建议条数。一条日志命中多条规则时按严重度取前几条，避免刷屏。</summary>
    public const int MaxSuggestions = 3;

    /// <summary>
    /// 建议里可以携带的动作标识，由 UI 决定绑到哪个按钮。
    /// 未识别的值按「没有动作」处理。
    /// </summary>
    public static class Actions
    {
        public const string OpenJavaSettings = "OpenJavaSettings";
        public const string OpenModsFolder = "OpenModsFolder";
        public const string RepairVersion = "RepairVersion";
        public const string OpenCrashReport = "OpenCrashReport";
        public const string OpenLogFolder = "OpenLogFolder";

        /// <summary>
        /// 禁用某个模组（<see cref="CrashSuggestion.ActionArgument"/> 为 jar 文件名）。
        ///
        /// 只在**恰好定位到一个**肇事文件时才给这个动作：二分法要求一次只改一个变量，
        /// 一次禁用五个等于把玩家的模组包砸掉，而且很可能禁到无辜的那个。
        /// </summary>
        public const string DisableMod = "DisableMod";
    }

    /// <summary>装载的规则总条数（三层合计），供启动日志与回归测试断言。</summary>
    public static int RuleCount => CrashRules.All.Length;

    /// <summary>Crit1（精确字符串）规则条数。</summary>
    public static int Crit1RuleCount => CrashRules.Crit1.Length;

    /// <summary>Crit2（需要上下文）规则条数。</summary>
    public static int Crit2RuleCount => CrashRules.Crit2.Length;

    /// <summary>Crit3（低置信度兜底）规则条数。</summary>
    public static int Crit3RuleCount => CrashRules.Crit3.Length;

    /// <summary>出现这些字样即视为「发生过崩溃」，即使一条规则都没命中。</summary>
    private static readonly string[] CrashMarkers =
    [
        "---- Minecraft Crash Report ----",
        "A fatal error has been detected",
        "Exception in thread",
        "FATAL ERROR in native method",
        "The game crashed",
    ];

    /// <summary>
    /// 允许做堆栈归因的前置条件：日志里得能看出用的是模组加载器，或者带了崩溃报告的固定标记。
    /// 原版日志里也会出现 <c>at com.xxx</c> 这种行，不设门槛会把正常启动当成模组崩溃。
    /// </summary>
    private static readonly string[] ModAttributionGates =
    [
        "orge",          // Forge / NeoForge
        "abric",         // Fabric
        "uilt",          // Quilt
        "iteloader",     // LiteLoader
        "---- Minecraft Crash Report ----",
        "The game crashed whilst",
        "A fatal error has been detected",
    ];

    // ==================== 崩溃报告定位 ====================

    /// <summary>游戏自报的崩溃报告路径，对应 HMCL 的 <c>CRASH_REPORT_LOCATION_PATTERN</c>。</summary>
    private static readonly Regex CrashReportLocationRegex =
        new(@"#@!@# Game crashed! Crash report saved to: #@!@# (?<path>.*)", RegexOptions.None);

    /// <summary>报告起始标记；HMCL 用 <c>---- Minecraft Crash Report ----</c> 与结束标记夹出正文。</summary>
    private const string CrashSectionBeginMarker = "---- Minecraft Crash Report ----";

    private const string CrashSectionEndMarker = "#@!@# Game crashed! Crash report saved to";

    // ==================== hs_err 解析 ====================

    /// <summary>hs_err 文件的固定首行前缀，也是判断「这是原生层崩溃报告」的依据。</summary>
    private const string HsErrFatalMarker = "A fatal error has been detected by the Java Runtime Environment";

    /// <summary>
    /// <c># Problematic frame:</c> 段与其后的帧行。帧行形如 <c># C  [nvoglv64.dll+0x9a1234]</c>，
    /// Windows 用 <c>C</c>（C 栈帧）、Linux 用 <c>C</c> / <c>V</c>（VM 帧），纯 Java 帧是 <c>J</c>。
    /// </summary>
    private static readonly Regex HsErrFrameRegex =
        new(@"Problematic frame:[^\r\n]*\r?\n[ \t]*#[ \t]*(?<value>[^\r\n]+)", RegexOptions.IgnoreCase);

    /// <summary>帧行里的模块名：<c>C  [nvoglv64.dll+0x9a1234]</c> → <c>nvoglv64.dll</c>。</summary>
    private static readonly Regex HsErrModuleRegex =
        new(@"\[(?<value>[^\[\]\r\n+]+)", RegexOptions.None);

    /// <summary>信号 / 异常类型：Linux 的 <c>SIGSEGV</c>、Windows 的 <c>EXCEPTION_*</c>、JVM 的 <c>Internal Error</c>。</summary>
    private static readonly Regex HsErrSignalRegex =
        new(@"(?<value>SIG[A-Z0-9]+|EXCEPTION_[A-Z0-9_]+|Internal Error|Out of Memory Error)", RegexOptions.None);

    // ==================== 肇事模组定位 ====================

    /// <summary>从 <c>Description:</c> 之后截取堆栈块，对应 HMCL 的 <c>CRASH_REPORT_STACK_TRACE_PATTERN</c>。</summary>
    private static readonly Regex StackTraceBlockRegex =
        new(@"Description: (.*?)[\n\r]+(?<stacktrace>[\w\W\n\r]+)A detailed walkthrough of the error", RegexOptions.None);

    /// <summary>单行堆栈，对应 HMCL 的 <c>STACK_TRACE_LINE_PATTERN</c>。</summary>
    private static readonly Regex StackTraceLineRegex =
        new(@"at (?<method>.*?)\((?<sourcefile>.*?)\)", RegexOptions.None);

    /// <summary>从一行日志里抠出 jar 文件名（不含目录），用于把包名反查成具体文件。</summary>
    private static readonly Regex JarNameRegex =
        new(@"[^\s\\/:*?""<>|()\[\]]+\.jar", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 包名前缀黑名单，20 条，抄自 PCL2 的 <c>AnalyzeStackKeyword</c>。
    /// 命中的整条候选直接丢弃——这些前缀下的类永远属于运行时 / 原版 / 加载器自身，不可能是肇事模组。
    /// </summary>
    private static readonly string[] PackagePrefixBlackList =
    [
        "java", "sun", "javax", "jdk", "oolloo",
        "org.lwjgl", "com.sun", "net.minecraftforge", "paulscode.sound", "com.mojang",
        "net.minecraft", "cpw.mods", "com.google", "org.apache", "org.spongepowered",
        "net.fabricmc", "com.mumfrey", "com.electronwill.nightconfig", "it.unimi.dsi",
        "MojangTricksIntelDriversForPerformance_javaw",
    ];

    /// <summary>
    /// 通用词黑名单，105 条，抄自 PCL2 的 <c>AnalyzeStackKeyword</c>
    /// （PCL2 的移植文档标称 106 条，但源码清单实际列了 105 个词，这里按源码为准）。
    /// 按段（<c>.</c> 分隔）逐个比对：命中说明这一段是 com / net / core / util 这类没有辨识度的词。
    /// </summary>
    private static readonly HashSet<string> GenericPackageWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "asm", "fml", "mod", "jar", "sun", "lib", "map",
        "gui", "dev", "nio", "api", "dsi", "top", "mcp", "core", "init", "mods",
        "main", "file", "game", "load", "read", "done", "util", "tile", "item", "base",
        "fake", "oshi", "impl", "data", "pool", "task", "forge", "setup", "block", "model",
        "mixin", "event", "unimi", "netty", "world", "lwjgl", "fakes", "fabric", "gitlab", "common",
        "server", "config", "mixins", "compat", "loader", "launch", "script", "entity", "assist", "client",
        "plugin", "modapi", "mojang", "shader", "events", "github", "recipe", "render", "packet", "preinit",
        "preload", "machine", "reflect", "channel", "general", "handler", "content", "systems", "modules", "service",
        "scripts", "network", "fastutil", "optifine", "internal", "platform", "override", "fabricmc", "neoforge", "external",
        "injection", "listeners", "scheduler", "minecraft", "universal", "multipart", "neoforged", "microsoft", "transformer", "transformers",
        "minecraftforge", "blockentity", "spongepowered", "electronwill", "concurrent",
    };

    // ==================== 公共 API ====================

    /// <summary>
    /// 从日志里取出游戏自报的崩溃报告路径。
    /// 匹配 <c>#@!@# Game crashed! Crash report saved to: #@!@# &lt;path&gt;</c>，
    /// 路径允许包含空格，取到行尾。一次日志里出现多次时取最后一次。
    /// </summary>
    /// <param name="log">完整日志或日志尾部。</param>
    /// <returns>报告路径；没有该标记时返回 <c>null</c>。</returns>
    public static string? ExtractCrashReportPath(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return null;
        }

        var matches = CrashReportLocationRegex.Matches(log);

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            // `.*` 在 .NET 里不跨 \n，但会吃进 \r，所以要显式去掉行尾回车。
            var path = matches[i].Groups["path"].Value.TrimEnd('\r', '\n').Trim();

            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// 从日志里截出 crash report 正文：<c>---- Minecraft Crash Report ----</c> 到
    /// <c>#@!@# Game crashed! Crash report saved to</c> 之间，避免把整段调试日志当成报告。
    /// </summary>
    /// <param name="log">完整日志或日志尾部。</param>
    /// <returns>报告正文；缺少任一标记或顺序颠倒时返回 <c>null</c>。</returns>
    public static string? ExtractCrashSection(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return null;
        }

        // 与 HMCL 一致用 lastIndexOf：多轮启动拼接的日志只取最后一轮的正文。
        var begin = log.LastIndexOf(CrashSectionBeginMarker, StringComparison.Ordinal);
        var end = log.LastIndexOf(CrashSectionEndMarker, StringComparison.Ordinal);

        if (begin < 0 || end < 0 || begin >= end)
        {
            return null;
        }

        return log.Substring(begin, end - begin);
    }

    /// <summary>
    /// 从 <c>hs_err_pid*.log</c> 里抽出原生层崩溃的关键证据：信号 / 异常类型、Problematic frame、
    /// 以及帧里的 native 模块名。
    ///
    /// 只做纯文本解析，不判断成因——成因由 <see cref="CrashRules"/> 的 hs_err 规则给出。
    /// 判断这是不是 hs_err 用两个标记之一：JVM 致命错误头，或 <c>Problematic frame:</c> 段；
    /// 两者都没有时返回 <c>null</c>，调用方按普通日志处理。
    /// </summary>
    /// <param name="hsErr">hs_err 的完整文本或截断后的文本。</param>
    /// <returns>解析结果；不像 hs_err 时返回 <c>null</c>。</returns>
    public static HsErrInfo? ParseHsErr(string? hsErr)
    {
        if (string.IsNullOrWhiteSpace(hsErr))
        {
            return null;
        }

        var hasFatalMarker = hsErr.Contains(HsErrFatalMarker, StringComparison.OrdinalIgnoreCase);
        var frameMatch = HsErrFrameRegex.Match(hsErr);

        if (!hasFatalMarker && !frameMatch.Success)
        {
            return null;
        }

        var frame = frameMatch.Success ? frameMatch.Groups["value"].Value.Trim() : null;

        if (string.IsNullOrWhiteSpace(frame))
        {
            frame = null;
        }

        string? module = null;

        if (frame is not null)
        {
            var moduleMatch = HsErrModuleRegex.Match(frame);

            if (moduleMatch.Success && !string.IsNullOrWhiteSpace(moduleMatch.Groups["value"].Value))
            {
                module = moduleMatch.Groups["value"].Value.Trim();
            }
        }

        return new HsErrInfo(ExtractHsErrSignal(hsErr), frame, module);
    }

    /// <summary>
    /// 从 crash report 的堆栈里猜「疑似肇事 mod 的包名」。
    ///
    /// 取 <c>Description:</c> 之后的 <c>at &lt;method&gt;(&lt;file&gt;)</c> 行，把方法全名按 <c>.</c> / <c>/</c>
    /// 切段，丢掉最后两段（类名与方法名），剩下的段先按「整条包路径是否命中 20 条前缀黑名单」整行否决，
    /// 再逐段过滤（长度 ≤ 2、<c>func_</c> 开头、命中 105 条通用词），按出现顺序去重。
    ///
    /// <b>结论只能是「疑似」</b>：黑名单是启发式的，玩家的 <c>com.foo</c> 也可能被留下；
    /// 本方法也不做 PCL2 的「候选超过 10 条就整体丢弃」，因为调用方（UI / 归因文案）需要稳定拿到候选列表。
    /// 报告里没有 <c>A detailed walkthrough of the error</c> 收尾时退回扫描全文。
    /// </summary>
    /// <param name="crashReport">crash report 正文，或包含堆栈的日志片段。</param>
    /// <returns>按出现顺序去重的候选包名段；识别不到时返回空集合。</returns>
    public static IReadOnlyList<string> FindSuspectPackageNames(string? crashReport)
    {
        if (string.IsNullOrWhiteSpace(crashReport))
        {
            return Array.Empty<string>();
        }

        var block = StackTraceBlockRegex.Match(crashReport);
        var body = block.Success ? block.Groups["stacktrace"].Value : crashReport;

        var suspects = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in body.Split('\n'))
        {
            var lineMatch = StackTraceLineRegex.Match(line);

            if (!lineMatch.Success)
            {
                continue;
            }

            var segments = lineMatch.Groups["method"].Value.Split('.', '/');

            // 最后两段是类名与方法名；只把它们之前的段当包名（与 HMCL 的 length - 2 一致）。
            var packageLength = segments.Length - 2;

            if (packageLength <= 0)
            {
                continue;
            }

            var packagePath = string.Join('.', segments[..packageLength]).Trim();

            // 整条包路径落在运行时 / 原版 / 加载器前缀下时，整行都不是候选。
            if (packagePath.Length == 0 || IsBlacklistedPackage(packagePath))
            {
                continue;
            }

            for (var i = 0; i < packageLength; i++)
            {
                var segment = segments[i].Trim();

                if (segment.Length <= 2
                    || segment.StartsWith("func_", StringComparison.OrdinalIgnoreCase)
                    || GenericPackageWords.Contains(segment))
                {
                    continue;
                }

                if (seen.Add(segment))
                {
                    suspects.Add(segment);
                }
            }
        }

        return suspects;
    }

    /// <summary>
    /// 按候选包名反查「具体是哪个 .jar」。
    ///
    /// 两个来源（对应 PCL2 <c>AnalyzeModName</c>）：
    /// <list type="bullet">
    /// <item>crash report 的 Mod List 行：形如 <c>\tjei (jei-1.20.1.jar)</c>；</item>
    /// <item>debug 日志的 <c>valid mod file … with …</c> 行。</item>
    /// </list>
    /// 关键字与整行做不区分大小写的子串匹配，命中后返回该行的 jar 文件名（不含目录）。
    /// 同样是启发式：文件名和 modId 对不上时可能查不到，返回空集合即可。
    /// </summary>
    /// <param name="text">crash report 正文或 debug 日志文本。</param>
    /// <param name="keywords">候选包名 / modId 列表，通常来自 <see cref="FindSuspectPackageNames"/>。</param>
    /// <returns>按出现顺序去重的 jar 文件名；查不到时返回空集合。</returns>
    public static IReadOnlyList<string> FindSuspectModFiles(string? text, IReadOnlyList<string>? keywords)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords is null || keywords.Count == 0)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (!line.Contains(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var keywordHit = keywords.Any(keyword =>
                !string.IsNullOrWhiteSpace(keyword)
                && line.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (!keywordHit)
            {
                continue;
            }

            var fileName = ExtractJarFileName(line);

            if (fileName is not null && seen.Add(fileName))
            {
                results.Add(fileName);
            }
        }

        return results;
    }

    // ==================== 日志归因 ====================

    /// <summary>分析一整段日志文本。只取尾部 <see cref="DefaultTailLines"/> 行。</summary>
    /// <param name="log">完整日志或日志尾部；为 null / 空白时返回「没有可分析的日志内容」。</param>
    public static CrashReport Analyze(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return EmptyReport;
        }

        return Analyze(log.Split('\n'));
    }

    /// <summary>
    /// 带日志入口的分析：先把规则装载情况与命中的成因写进 Info 日志，再返回结果。
    /// 启动器侧（GameLauncher / UI）可以直接换成这个重载，日志里就能看到本次分析用了多少条规则。
    /// </summary>
    /// <param name="log">完整日志或日志尾部。</param>
    /// <param name="appLog">启动器日志；为 null 时与 <see cref="Analyze(string?)"/> 等价。</param>
    public static CrashReport Analyze(string? log, IAppLog? appLog)
    {
        if (appLog is null)
        {
            return Analyze(log);
        }

        LogRuleLoad(appLog);

        var report = Analyze(log);

        LogHits(appLog, report);

        return report;
    }

    /// <summary>
    /// 三类输入一起分析：进程日志尾部 + crash report 正文 + hs_err 正文。
    ///
    /// 这是「崩溃现场」的完整入口：Java 层崩溃只产出 crash report，JVM 原生层崩溃只产出
    /// <c>hs_err_pid*.log</c>，二者都要能进同一套规则。已有的 <see cref="Analyze(string?)"/>
    /// 重载保持行为不变，本重载只是把三类文本拼在一起，并对 hs_err 额外解析出
    /// <see cref="HsErrInfo"/> 写进结论摘要。
    /// </summary>
    /// <param name="log">游戏标准输出尾部；没有时传 null。</param>
    /// <param name="crashReport">crash report 正文；没有时传 null。</param>
    /// <param name="hsErr">hs_err 正文（可以是被截断的头尾合并文本）；没有时传 null。</param>
    /// <param name="appLog">启动器日志；为 null 时不写日志。</param>
    /// <returns>分析结果。三类输入都为空时返回「没有可分析的内容」，不抛异常。</returns>
    public static CrashReport Analyze(string? log, string? crashReport, string? hsErr, IAppLog? appLog = null)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(log))
        {
            parts.Add(log!);
        }

        if (!string.IsNullOrWhiteSpace(crashReport))
        {
            parts.Add(crashReport!);
        }

        if (!string.IsNullOrWhiteSpace(hsErr))
        {
            parts.Add(hsErr!);
        }

        if (parts.Count == 0)
        {
            appLog?.Info("崩溃归因：三类输入（日志 / crash report / hs_err）都为空，跳过分析");
            return EmptyReport;
        }

        LogRuleLoad(appLog);

        var hsInfo = ParseHsErr(hsErr);
        var report = AnalyzeText(string.Join(Environment.NewLine, parts));

        if (hsInfo is not null)
        {
            // hs_err 是「JVM 自己承认崩了」的直接证据，即使规则一条都没命中也要标记为崩溃。
            report = report with
            {
                IsCrash = true,
                Summary = DescribeHsErr(hsInfo) + report.Summary,
            };
        }

        LogHits(appLog, report);

        return report;
    }

    /// <summary>分析日志行集合；只会看最后 <paramref name="tailLines"/> 行。</summary>
    /// <param name="lines">日志按行拆分后的集合。</param>
    /// <param name="tailLines">参与分析的尾部行数，小于 1 时按 1 行处理。</param>
    public static CrashReport Analyze(IReadOnlyList<string>? lines, int tailLines = DefaultTailLines)
    {
        if (lines is null || lines.Count == 0)
        {
            return EmptyReport;
        }

        return AnalyzeText(JoinTail(lines, tailLines));
    }

    /// <summary>
    /// 分析大日志：保留头部 <paramref name="headLines"/> 行 + 尾部 <paramref name="tailLines"/> 行。
    ///
    /// 只取尾部会漏掉「崩溃前因」——模组加载失败、Java 版本、启动参数都打在日志开头。
    /// 默认 1500 / 500 与 PCL2 对 <c>latest.log</c> 的处理一致。
    /// </summary>
    /// <param name="lines">日志按行拆分后的集合。</param>
    /// <param name="headLines">保留的头部行数。</param>
    /// <param name="tailLines">保留的尾部行数。</param>
    public static CrashReport AnalyzeHeadTail(
        IReadOnlyList<string>? lines,
        int headLines = LargeLogHeadLines,
        int tailLines = LargeLogTailLines)
    {
        if (lines is null || lines.Count == 0)
        {
            return EmptyReport;
        }

        return AnalyzeText(JoinHeadTail(lines, headLines, tailLines));
    }

    /// <summary>
    /// 取日志尾部若干行拼成文本。行数超过总行数时全取。
    /// </summary>
    /// <param name="lines">日志行集合。</param>
    /// <param name="tailLines">保留的尾部行数。</param>
    public static string JoinTail(IReadOnlyList<string> lines, int tailLines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var tailCount = Math.Clamp(tailLines, 1, lines.Count);

        return string.Join('\n', lines.Skip(lines.Count - tailCount));
    }

    /// <summary>
    /// 取「头部 N 行 + 中间省略标记 + 尾部 M 行」拼成文本，对应 PCL2 的 <c>GetHeadTailLines</c>。
    /// 总行数不超过 N + M 时原样拼接，不留省略标记。
    /// </summary>
    /// <param name="lines">日志行集合。</param>
    /// <param name="headLines">保留的头部行数，负数按 0 处理。</param>
    /// <param name="tailLines">保留的尾部行数，负数按 0 处理。</param>
    public static string JoinHeadTail(IReadOnlyList<string> lines, int headLines, int tailLines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        headLines = Math.Max(headLines, 0);
        tailLines = Math.Max(tailLines, 0);

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        if (lines.Count <= headLines + tailLines)
        {
            return string.Join('\n', lines);
        }

        var sb = new StringBuilder();

        for (var i = 0; i < headLines; i++)
        {
            sb.Append(lines[i]).Append('\n');
        }

        sb.Append("…（此处省略 ").Append(lines.Count - headLines - tailLines).Append(" 行）…\n");

        for (var i = lines.Count - tailLines; i < lines.Count; i++)
        {
            sb.Append(lines[i]).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>把分析结果压成一段可直接展示的中文文本。</summary>
    /// <param name="report">分析结果。</param>
    /// <param name="maxSuggestions">最多展示几条建议。</param>
    public static string Format(CrashReport report, int maxSuggestions = MaxSuggestions)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append(report.Summary);

        foreach (var suggestion in report.Suggestions.Take(maxSuggestions))
        {
            sb.AppendLine();
            sb.Append(" · ").Append(suggestion.Title).Append('：').Append(suggestion.Detail);
        }

        return sb.ToString();
    }

    // ==================== 外部报告导入 ====================

    /// <summary>
    /// 读取用户手动导入的崩溃报告。
    ///
    /// 支持三类来源：
    /// <list type="bullet">
    /// <item><c>.txt</c>：Minecraft / 模组导出的 crash report；</item>
    /// <item><c>.log</c>：<c>hs_err_pid*.log</c> 或日志尾部；</item>
    /// <item><c>.zip</c>：取压缩包里第一个 <c>crash-*.txt</c>，没有则取第一个 <c>hs_err_pid*.log</c>。</item>
    /// </list>
    /// 文件过大时只保留头 <see cref="LargeLogHeadLines"/> 行与尾 <see cref="LargeLogTailLines"/> 行，
    /// 与 <see cref="AnalyzeHeadTail"/> 的策略一致：崩溃前因常在开头，症状在结尾。
    /// 读取失败不抛异常：<see cref="ExternalReportContent.Error"/> 写明原因，由调用方展示。
    /// </summary>
    /// <param name="path">用户选择的文件绝对路径。</param>
    /// <returns>读取结果；失败时 <see cref="ExternalReportContent.IsSuccess"/> 为 false。</returns>
    public static ExternalReportContent ReadExternalReport(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ExternalReportContent(string.Empty, string.Empty, false, "没有选择报告文件。");
        }

        var fileName = SafeFileName(path);

        try
        {
            if (!File.Exists(path))
            {
                return new ExternalReportContent(fileName, string.Empty, false, $"找不到报告文件：{path}");
            }

            if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                return ReadZipReport(path, fileName);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadHeadTail(stream, fileName);
        }
        catch (Exception ex)
        {
            return new ExternalReportContent(fileName, string.Empty, false, $"读取报告失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 分析用户手动导入的外部崩溃报告。
    ///
    /// 与游戏崩溃走同一套 <see cref="CrashRules"/> 与三层短路；区别只在结论摘要前面强制带上
    /// 「来源：文件名」，让用户明确这是别人的 / 历史的报告，而不是本机本次崩溃。
    /// 读取失败时返回 <c>IsCrash=false</c> 的结论，摘要写明具体原因（不静默）。
    /// </summary>
    /// <param name="path">用户选择的文件绝对路径。</param>
    /// <param name="appLog">启动器日志；为 null 时不写日志。</param>
    public static CrashReport AnalyzeExternalReport(string? path, IAppLog? appLog = null)
    {
        ExternalReportContent content;

        try
        {
            content = ReadExternalReport(path);
        }
        catch (Exception ex)
        {
            return new CrashReport(false, $"读取报告失败：{ex.Message}", Array.Empty<CrashSuggestion>());
        }

        if (!content.IsSuccess)
        {
            var prefix = string.IsNullOrWhiteSpace(content.FileName) ? string.Empty : $"来源：{content.FileName}。";
            return new CrashReport(
                false,
                prefix + (content.Error ?? "没有可分析的内容。"),
                Array.Empty<CrashSuggestion>());
        }

        var source = content.Truncated
            ? $"来源：{content.FileName}（文件过大，仅分析头 {LargeLogHeadLines} 行与尾 {LargeLogTailLines} 行）。"
            : $"来源：{content.FileName}。";

        try
        {
            var isHsErr = content.Text.Contains(HsErrFatalMarker, StringComparison.OrdinalIgnoreCase)
                || content.Text.Contains("Problematic frame:", StringComparison.OrdinalIgnoreCase);

            var report = isHsErr
                ? Analyze(null, null, content.Text, appLog)
                : Analyze(null, content.Text, null, appLog);

            return report with { Summary = source + report.Summary };
        }
        catch (Exception ex)
        {
            return new CrashReport(false, source + $"分析失败：{ex.Message}", Array.Empty<CrashSuggestion>());
        }
    }

    // ==================== 内部实现 ====================

    private static readonly CrashReport EmptyReport =
        new(false, "没有可分析的日志内容。", Array.Empty<CrashSuggestion>(), null);

    /// <summary>
    /// 从 hs_err 里取信号 / 异常类型。
    /// 优先只看致命错误头之后的三行——完整 hs_err 的 Dynamic libraries / Threads 段里
    /// 也会出现 <c>Out of Memory Error</c> 之类的词，全文扫描会误判。
    /// </summary>
    private static string? ExtractHsErrSignal(string text)
    {
        var markerIndex = text.IndexOf(HsErrFatalMarker, StringComparison.OrdinalIgnoreCase);

        if (markerIndex >= 0)
        {
            var lines = text[markerIndex..].Split('\n', 4);

            for (var i = 1; i < lines.Length; i++)
            {
                var match = HsErrSignalRegex.Match(lines[i]);

                if (match.Success)
                {
                    return match.Groups["value"].Value;
                }
            }

            // 有致命错误头但头三行里没有信号：不要再扫全文，
            // 完整 hs_err 的 Dynamic libraries / Threads 段会把 "Out of Memory Error" 之类的词带进来。
            return null;
        }

        var fallback = HsErrSignalRegex.Match(text);

        return fallback.Success ? fallback.Groups["value"].Value : null;
    }

    /// <summary>把 hs_err 的关键证据压成一句摘要前缀。</summary>
    private static string DescribeHsErr(HsErrInfo info)
    {
        if (info.Signal is not null && info.NativeModule is not null)
        {
            return $"hs_err 显示 {info.Signal}（{info.NativeModule}）。";
        }

        if (info.Signal is not null)
        {
            return $"hs_err 显示 {info.Signal}。";
        }

        if (info.NativeModule is not null)
        {
            return $"hs_err 指向 {info.NativeModule}。";
        }

        if (info.ProblematicFrame is not null)
        {
            return $"hs_err 的 Problematic frame：{info.ProblematicFrame}。";
        }

        return "hs_err 缺少 Problematic frame。";
    }

    /// <summary>规则装载情况写进日志，便于现场核对用的是哪一版规则表。</summary>
    private static void LogRuleLoad(IAppLog? appLog)
    {
        appLog?.Info(
            $"崩溃规则装载完成：共 {RuleCount} 条（Crit1 {Crit1RuleCount} / Crit2 {Crit2RuleCount} / Crit3 {Crit3RuleCount}）");
    }

    /// <summary>命中结果写进日志；未命中也要留一条。</summary>
    private static void LogHits(IAppLog? appLog, CrashReport report)
    {
        if (appLog is null)
        {
            return;
        }

        appLog.Info(report.Suggestions.Count == 0
            ? "崩溃归因：未命中任何规则"
            : $"崩溃归因命中 {report.Suggestions.Count} 条：{string.Join("、", report.Suggestions.Select(s => s.Title))}");
    }

    /// <summary>从 zip 里按「crash-*.txt 优先、hs_err_pid*.log 兜底」取第一个报告。</summary>
    private static ExternalReportContent ReadZipReport(string path, string fileName)
    {
        using var archive = ZipFile.OpenRead(path);

        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .ToList();

        var entry = entries.FirstOrDefault(candidate => IsCrashTextEntry(candidate.Name))
            ?? entries.FirstOrDefault(candidate => IsHsErrEntry(candidate.Name));

        if (entry is null)
        {
            return new ExternalReportContent(
                fileName,
                string.Empty,
                false,
                "压缩包里没有 crash-*.txt 或 hs_err_pid*.log，请先解压后选择具体报告。");
        }

        using var stream = entry.Open();
        var entryName = string.IsNullOrWhiteSpace(entry.FullName) ? entry.Name : entry.FullName;

        return ReadHeadTail(stream, $"{fileName} / {entryName}");
    }

    /// <summary>流式读取文本并只保留头 N 行 + 尾 M 行，避免几百 MB 的报告整份进内存。</summary>
    private static ExternalReportContent ReadHeadTail(Stream stream, string fileName)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: true);

        var head = new List<string>(LargeLogHeadLines);
        var tail = new Queue<string>(LargeLogTailLines);
        var total = 0;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            total++;

            if (head.Count < LargeLogHeadLines)
            {
                head.Add(line);
            }
            else
            {
                tail.Enqueue(line);

                if (tail.Count > LargeLogTailLines)
                {
                    tail.Dequeue();
                }
            }
        }

        if (total <= LargeLogHeadLines + LargeLogTailLines)
        {
            // 没超过窗口：尾队列里的行直接接在头后面，不留省略标记。
            head.AddRange(tail);
            return new ExternalReportContent(fileName, string.Join('\n', head), false);
        }

        var omitted = total - head.Count - tail.Count;
        var sb = new StringBuilder();

        foreach (var item in head)
        {
            sb.Append(item).Append('\n');
        }

        sb.Append("…（此处省略 ").Append(omitted).Append(" 行）…\n");

        foreach (var item in tail)
        {
            sb.Append(item).Append('\n');
        }

        return new ExternalReportContent(fileName, sb.ToString(), true);
    }

    /// <summary>取文件名；路径非法时退回原字符串，保证错误文案里仍有可识别的来源。</summary>
    private static string SafeFileName(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool IsCrashTextEntry(string name) =>
        name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static bool IsHsErrEntry(string name) =>
        name.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    /// <summary>对已截断的文本做三层判定，并按严重度截断结果。</summary>
    private static CrashReport AnalyzeText(string text)
    {
        var suggestions = CollectSuggestions(text);

        // 一条日志可能命中多条规则：Error 优先，同严重度保持规则声明顺序（OrderByDescending 是稳定排序）。
        var ranked = suggestions
            .OrderByDescending(suggestion => suggestion.Severity)
            .Take(MaxSuggestions)
            .ToList();

        var hasCrashMarker = CrashMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var summary = suggestions.Count switch
        {
            0 when hasCrashMarker => "日志含崩溃标记，但没有匹配到已知成因，请查看 crash-report 详情。",
            0 => "未在日志尾部发现崩溃特征。",
            1 => $"检测到：{ranked[0].Title}。",
            _ => $"检测到 {suggestions.Count} 个可能成因：{string.Join("、", ranked.Select(s => s.Title))}。",
        };

        return new CrashReport(suggestions.Count > 0 || hasCrashMarker, summary, ranked, text);
    }

    /// <summary>
    /// 三层短路收集：Crit1 出结论就不跑 Crit2，Crit2 出结论就不做堆栈归因，都没有才轮到 Crit3。
    /// 同一层内按标题去重，标题相同的规则只保留先命中的那条。
    /// </summary>
    private static List<CrashSuggestion> CollectSuggestions(string text)
    {
        var matched = new List<CrashSuggestion>();
        var titles = new HashSet<string>(StringComparer.Ordinal);

        void AddRule(CrashRules.CrashRule rule)
        {
            if (!rule.Matches(text) || !titles.Add(rule.Title))
            {
                return;
            }

            var detail = rule.Detail;
            var extracted = rule.Extract(text);

            if (!string.IsNullOrWhiteSpace(extracted))
            {
                detail += Environment.NewLine + "报错指向：" + extracted;
            }

            matched.Add(new CrashSuggestion(rule.Title, detail, rule.Severity, rule.ActionId));
        }

        void AddLayer(CrashRules.CrashRule[] rules)
        {
            foreach (var rule in rules)
            {
                AddRule(rule);
            }
        }

        void AddModAttribution()
        {
            if (!ModAttributionGates.Any(gate => text.Contains(gate, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var suspects = FindSuspectPackageNames(text);

            if (suspects.Count == 0)
            {
                return;
            }

            var detail = new StringBuilder();
            detail.Append("崩溃堆栈里出现了原版之外的包名：")
                  .Append(string.Join("、", suspects.Take(5)))
                  .Append("。请先在 mods 目录里一次禁一半做二分，定位到崩溃随之消失的那个 .jar，")
                  .Append("再换用与当前 Minecraft / 加载器版本匹配的构建。");

            var files = FindSuspectModFiles(text, suspects);

            if (files.Count > 0)
            {
                detail.Append(" 报告 Mod 列表里对应的文件：").Append(string.Join("、", files.Take(5))).Append('。');
            }

            // 只定位到一个文件才给「禁用」按钮：二分法一次只改一个变量。
            // 多个候选时仍只提供「打开模组目录」，让玩家自己做减法。
            var single = files.Count == 1 ? files[0] : null;

            if (single is not null)
            {
                detail.Append(" 可以直接禁用这个文件再启动一次，崩溃消失即确认。");
            }

            matched.Add(new CrashSuggestion(
                "疑似模组导致崩溃",
                detail.ToString(),
                CrashSeverity.Warning,
                single is null ? Actions.OpenModsFolder : Actions.DisableMod,
                single));
        }

        AddLayer(CrashRules.Crit1);

        if (matched.Count == 0)
        {
            AddLayer(CrashRules.Crit2);
        }

        if (matched.Count == 0)
        {
            AddModAttribution();
        }

        if (matched.Count == 0)
        {
            AddLayer(CrashRules.Crit3);
        }

        return matched;
    }

    /// <summary>整条包路径是否落在运行时 / 原版 / 加载器的前缀黑名单下（按段边界匹配，避免 <c>javafx</c> 命中 <c>java</c>）。</summary>
    private static bool IsBlacklistedPackage(string packagePath)
    {
        foreach (var prefix in PackagePrefixBlackList)
        {
            if (packagePath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (packagePath.Length > prefix.Length
                && packagePath[prefix.Length] == '.'
                && packagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>从一行 Mod List / valid mod file 日志里取出 jar 文件名；取不到返回 <c>null</c>。</summary>
    private static string? ExtractJarFileName(string line)
    {
        var candidate = line;

        // debug.log：`… valid mod file C:\games\mods\some mod-1.0.jar with …`
        // 先按 " with " 截断，才能容纳文件名里的空格。
        var markerIndex = line.IndexOf("valid mod file ", StringComparison.OrdinalIgnoreCase);

        if (markerIndex >= 0)
        {
            candidate = line[(markerIndex + "valid mod file ".Length)..];

            var cut = candidate.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);

            if (cut >= 0)
            {
                candidate = candidate[..cut];
            }
        }

        var matches = JarNameRegex.Matches(candidate);

        return matches.Count == 0 ? null : matches[matches.Count - 1].Value;
    }
}
