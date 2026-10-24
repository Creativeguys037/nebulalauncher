using System.Text.RegularExpressions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 崩溃判定规则表。三层结构，对齐 PCL2 的 <c>AnalyzeCrit1/2/3</c>：
/// <list type="number">
/// <item><b>Crit1</b>：精确字符串匹配。症状本身足够特异，命中即可下结论，不需要上下文。</item>
/// <item><b>Crit2</b>：需要上下文。同一个症状在不同上下文下结论不同（例如
/// <c>Could not reserve enough space</c> 要看报错里有没有 <c>object heap</c>：有则是 32 位 Java 的地址空间上限，
/// 没有则更像物理内存不足），或者结论必须从报错正文里抽出主语（mod 名、方块 ID、配置文件路径）。</item>
/// <item><b>Crit3</b>：低置信度兜底。只在前面各层都没有结论时才使用，文案必须写「疑似」。</item>
/// </list>
/// 层内是并集（同层可同时命中多条，由 <see cref="CrashAnalyzer"/> 按严重度截断），
/// 层间是短路（上一层出结论就停止往下判），避免把十几个猜想一起堆给用户。
///
/// 规则是纯数据 + 只读正则，分析过程不写任何共享状态，可并发调用。
/// 文案约定：必须给出具体版本号 / 路径 / 按钮名，不写「请检查配置」这类无法执行的话；
/// 证据不够确凿的结论一律写「疑似」。
/// </summary>
internal static class CrashRules
{
    /// <summary>
    /// 一条崩溃判定规则。
    ///
    /// 命中条件 = <see cref="Patterns"/> 任一命中 且 <see cref="AllOf"/> 全命中 且 <see cref="Excludes"/> 全未命中。
    /// 所有字符串比较都是不区分大小写的子串匹配，行尾的 <c>\r</c> 不影响匹配。
    /// </summary>
    /// <param name="Patterns">
    /// 主特征，任一命中即算「疑似命中」，多个写法覆盖不同加载器的措辞。
    /// <b>允许为空数组</b>：空表示本规则没有主特征，命中与否完全由
    /// <see cref="AllOf"/> / <see cref="Excludes"/> / <see cref="MaxLength"/> 决定。
    /// 「游戏没能启动」这类规则靠的就是「什么都没有」——它没有可匹配的症状。
    /// </param>
    /// <param name="Title">结论标题，UI 用作小标题；同一层内标题相同的规则只保留先命中的那条。</param>
    /// <param name="Detail">中文建议，必须含可执行动作（改哪个设置、删哪个文件、升到哪个版本）。</param>
    /// <param name="Severity">严重度，Error 优先展示。</param>
    /// <param name="ActionId">可选动作标识，取值见 <see cref="CrashAnalyzer.Actions"/>。</param>
    /// <param name="AllOf">额外必须全部命中的特征，用于「症状 + 上下文」的组合判定。</param>
    /// <param name="Excludes">命中任一即否决本规则，用于把同症状的两种情况拆开。</param>
    /// <param name="ExtractPattern">可选抽取正则，抓组名 <c>value</c>（没有命名组时取整个匹配）拼到文案末尾。</param>
    /// <param name="MaxLength">
    /// 可选的长度上限（开区间）：只有文本字符数<b>小于</b>该值时才可能命中。
    /// 对应 PCL2 <c>AnalyzeCrit3</c> 的 <c>LogMc.Length &lt; 100</c> 判据——
    /// 「程序几乎没输出」这件事本身没有关键词可匹配，只能量长度。
    /// </param>
    internal sealed record CrashRule(
        string[] Patterns,
        string Title,
        string Detail,
        CrashSeverity Severity,
        string? ActionId = null,
        string[]? AllOf = null,
        string[]? Excludes = null,
        string? ExtractPattern = null,
        int? MaxLength = null)
    {
        /// <summary>判断一段日志文本是否命中本规则。</summary>
        /// <param name="text">待分析的日志文本，通常是日志尾部 + crash report 正文。</param>
        public bool Matches(string text)
        {
            // 长度先判：长度上限是最便宜的一次比较，超出就直接否决，不必再扫几遍全文。
            if (MaxLength is { } maxLength && text.Length >= maxLength)
            {
                return false;
            }

            // 空 Patterns = 没有主特征（见构造参数的说明），不能当成「一条都没命中」。
            if (Patterns.Length > 0 && !Patterns.Any(pattern => Contains(text, pattern)))
            {
                return false;
            }

            if (AllOf is not null && !AllOf.All(pattern => Contains(text, pattern)))
            {
                return false;
            }

            return Excludes is null || !Excludes.Any(pattern => Contains(text, pattern));
        }

        /// <summary>
        /// 按 <see cref="ExtractPattern"/> 从报错正文里抽出主语（mod 名 / 方块 ID / 配置文件名）。
        /// 抽不到返回 <c>null</c>，调用方据此保持原始文案不变。
        /// </summary>
        /// <param name="text">待分析的日志文本。</param>
        public string? Extract(string text)
        {
            if (ExtractPattern is null)
            {
                return null;
            }

            // 只有命中规则后才走到这里，单次分析最多几条，不值得为它缓存 Regex 实例。
            var match = Regex.Match(text, ExtractPattern, RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return null;
            }

            var value = match.Groups["value"] is { Success: true } group ? group.Value : match.Value;
            value = value.Trim();

            return value.Length == 0 ? null : value;
        }

        private static bool Contains(string text, string pattern) =>
            text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== Crit1：精确字符串 ====================

    /// <summary>
    /// 第一层：精确字符串匹配。既有规则的标题与文案被单测按字符串断言，除「补 pattern」外不要改动。
    ///
    /// 排列约定（顺序有意义，因为同层按声明顺序收集）：
    /// <list type="bullet">
    /// <item>同族规则里「更具体的」必须排在「更泛的」之前，例如「模组要求特定 MC 版本」先于「模组依赖缺失」；</item>
    /// <item>新规则优先插进它所属的族里（Forge 安装问题挨着 Forge 安装不完整），这样既有规则之间的相对顺序不变；</item>
    /// <item>没有明显族属的才追加到末尾。</item>
    /// </list>
    /// 只要不调换既有规则的相对次序，插队是安全的；「一律只追加到末尾」会把新规则排到泛规则之后，
    /// 反而让更泛的结论先占掉 <see cref="CrashAnalyzer.MaxSuggestions"/> 的名额。
    /// </summary>
    internal static readonly CrashRule[] Crit1 =
    [
        // ---------------- 既有 30 条 ----------------

        // 内存不足（OutOfMemoryError 的通用写法）
        new(
            ["java.lang.OutOfMemoryError"],
            "内存不足",
            "游戏分配到的堆内存不够。请在设置里调高最大内存（建议 4–8 GB），"
            + "并检查是否有模组在泄漏内存。32 位 Java 最多只能用约 1.5 GB，请改用 64 位 Java。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // Java 版本过低
        new(
            ["UnsupportedClassVersionError"],
            "Java 版本过低",
            "当前 Java 无法加载游戏或模组编译出的字节码。请换成更高版本的 Java："
            + "1.17 需要 Java 16，1.18–1.20.4 需要 Java 17，1.20.5–25.x 需要 Java 21，26.x 需要 Java 25。"
            + "可在「设置 → Java」里直接下载。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // 模组版本不匹配
        new(
            ["NoSuchMethodError", "NoSuchFieldError", "ClassNotFoundException", "NoClassDefFoundError"],
            "模组版本不匹配",
            "某个模组调用了当前游戏版本中不存在的类或方法，通常是模组与 Minecraft 版本、"
            + "或其他模组版本不一致。请逐个禁用最近新增的模组，确认是哪一个。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // Mixin 冲突
        new(
            ["Mixin apply failed", "MixinApplyError", "Mixin apply failed:", "org.spongepowered.asm.mixin.transformer"],
            "Mixin 冲突",
            "多个模组改写了同一处字节码，或某个模组的 Mixin 与当前加载器版本不兼容。"
            + "请更换冲突模组的版本，必要时翻 crash-report 里第一个 Mixin 报错定位来源。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenCrashReport),

        // 显卡驱动问题
        new(
            ["Pixel format not accelerated", "Failed to create GLFW window", "GLFW error", "The driver does not appear to support OpenGL"],
            "显卡驱动问题",
            "游戏无法创建 OpenGL 上下文。请更新显卡驱动，确认启动的是独立显卡而不是集成显卡，"
            + "并关闭强制使用软件渲染的选项。",
            CrashSeverity.Warning),

        // 文件权限被拒绝
        new(
            ["AccessDeniedException", "Access is denied", "java.nio.file.AccessDeniedException", "拒绝访问"],
            "文件权限被拒绝",
            "游戏目录或 Java 安装目录不可写，常见原因是杀毒软件锁定、目录在受保护位置，"
            + "或启动器没有权限。请把游戏目录移出 Program Files，并给启动器加杀软白名单。",
            CrashSeverity.Warning),

        // Forge 依赖缺失
        new(
            ["Missing or unsupported mandatory dependencies", "Missing mandatory dependencies"],
            "Forge 依赖缺失",
            "某个 Forge 模组要求的前置模组缺失或版本过低。请按报错里列出的 modId 安装对应前置模组。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 32 位 Java 或分配内存过大。带上下文的那一半（Could not reserve enough space）
        // 挪到 Crit2：它要看有没有 object heap 才能区分 32 位与物理内存不足。
        new(
            ["The specified size exceeds the maximum representable size", "Invalid maximum heap size"],
            "32 位 Java 或分配内存过大",
            "JVM 无法预留请求的堆内存：常见原因是装了 32 位 Java（单进程上限约 1.5 GB），"
            + "或最大内存设置超过了物理内存。请在「设置 → Java」换成 64 位 Java，并把最大内存降到物理内存的一半以内。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // Java 版本低于模组要求
        new(
            ["UnsupportedClassVersionError: "],
            "Java 版本低于模组要求",
            "某个模组用更高版本的 Java 编译，当前 JVM 读不了它的字节码。"
            + "class file version 与 Java 的对应关系：52 → 8，55 → 11，61 → 17，65 → 21。请换成对应或更高版本的 Java。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // Java 版本过高
        new(
            [
                "Unable to make protected final java.lang.Class java.lang.ClassLoader.defineClass",
                "java.lang.NoSuchFieldException: ucp",
                "Unsupported class file major version",
                "because module java.base does not export",
                "java.lang.ClassNotFoundException: jdk.nashorn.api.scripting.NashornScriptEngineFactory",
                "Cannot read the array length because \"urls\" is null",
                "java.lang.ClassNotFoundException: java.lang.invoke.LambdaMetafactory",
            ],
            "Java 版本过高",
            "老版本 Minecraft（尤其 1.12.2 及更早的 Forge）只支持 Java 8，用 Java 17/21 启动会因模块系统"
            + "与反射限制直接失败。请改用 Java 8；多版本共存时在「设置 → Java」里手动指定它的 javaw.exe。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // 模组文件被解压
        new(
            ["The directories below appear to be extracted jar files", "Extracted mod jars found, loading will NOT continue"],
            "模组文件被解压",
            "mods 目录里出现了被解压成文件夹的模组。请把它们重新打包成 .jar，或直接删掉解压出来的文件夹，只保留 .jar 文件。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // Forge 安装不完整
        new(
            [
                "Invalid paths argument, contained no existing paths",
                "Cannot find launch target fmlclient, unable to launch",
                "Could not find net/minecraft/client/Minecraft.class in classloader SecureModuleClassLoader",
                "Failed to find Minecraft resource version",
            ],
            "Forge 安装不完整",
            "Forge 的库文件或版本 json 缺失，启动目标找不到。请在版本页对该版本执行「修复」，或重新安装一次 Forge。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.RepairVersion),

        // PCL: 版本Json中存在多个Forge（ModCrash.vb:491）。
        // 用精确整串而不是「fml.forgeVersion」这类短片段：Forge 这条文案从 1.13 到 1.20.x 一直没变，
        // 而短片段会被正常启动参数 --fml.forgeVersion 命中，等于把每份 Forge 日志都判成安装损坏。
        // 必须放在 Crit1：这段日志的主要出路是掉进堆栈归因，被判成「疑似模组导致崩溃」，
        // 用户就永远看不到「两个 Forge 版本挤在同一个版本目录里」这个真正的根因。
        new(
            ["Found multiple arguments for option fml.forgeVersion, but you asked for only one"],
            "版本 JSON 中存在多个 Forge",
            "versions\\<id>\\<id>.json 的 arguments 里出现了重复的 --fml.forgeVersion，"
            + "Forge 启动器认为同时指定了两个 Forge 版本，于是在加载任何模组之前就退出了。"
            + "通常是把两个 Forge 版本的 json 合并进了同一个版本目录，或反复安装 Forge 时覆盖了同一个版本名。"
            + "请打开 .minecraft\\versions\\<id>\\<id>.json，在 arguments.jvm / arguments.game 里"
            + "删掉重复的那一组 --fml.forgeVersion（连同成对的 --fml.forgeGroup 与 --fml.mcVersion）；"
            + "更稳妥的做法是直接删掉 versions 目录里这个同名版本，再在版本页重新安装一次 Forge。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.RepairVersion),

        // 存在重复的模组
        new(
            [
                "Found a duplicate mod ",
                "Duplicate mods found",
                "DuplicateModsFoundException",
                "ModResolutionException: Duplicate",
            ],
            "存在重复的模组",
            "同一个模组在 mods 目录里有多个副本（通常是文件名不同、版本不同）。请只保留其中一个。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenModsFolder),

        // Forge 发现重复模组
        new(
            ["Found duplicate mods:"],
            "Forge 发现重复模组",
            "Forge 在 mods 目录里发现同一 modId 的多个文件。请删掉旧版本，只留一个。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 模组要求特定的 Minecraft 版本（比下一条更具体，必须排在前面）
        new(
            ["requires {minecraft @"],
            "模组要求特定的 Minecraft 版本",
            "某个模组只支持报错里写出的那几个 Minecraft 版本。请换用适配当前游戏版本的模组文件，或改用对应版本的整合包。",
            CrashSeverity.Error),

        // 模组依赖缺失
        new(
            ["Could not find required mod"],
            "模组依赖缺失",
            "某个模组要求的前置模组没有安装。请按报错里的「X requires Y」装上 Y，并注意它标注的版本范围。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 模组集合依赖冲突（比下一条具体）
        new(
            ["Could not resolve valid mod collection"],
            "模组集合依赖冲突",
            "Fabric 无法为当前这组模组找到互相兼容的版本组合。请先移除最近加入的模组，再看报错里「X requires Y」指向哪一对。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 模组之间互相冲突
        new(
            ["Found conflicting mods:"],
            "模组之间互相冲突",
            "两个模组声明了互斥的规则（报错里的「X conflicts with Y」）。请二选一，或换用兼容版本。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // Fabric 模组解析失败（兜底，放本组最后）
        new(
            ["ModResolutionException:"],
            "Fabric 模组解析失败",
            "Fabric 在启动前解析模组依赖时失败，真正的成因通常在同一段报错的下方："
            + "可能是缺前置、版本范围不满足，或两个模组互斥。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 模组在加载阶段崩溃
        new(
            [
                "LoaderExceptionModCrash: Caught exception from",
                "Failed to create mod instance. ModID:",
                "Failed to create mod instance.",
            ],
            "模组在加载阶段崩溃",
            "Forge 在构建模组实例时捕获到异常，报错里会给出 ModID。请先禁用该模组，或换一个支持当前加载器版本的构建。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 模组初始化阶段崩溃
        new(
            ["Could not execute entrypoint stage"],
            "模组初始化阶段崩溃",
            "Fabric 调用某个模组的入口点时抛异常，报错里引号中的 modId 就是触发者。请禁用它或换成兼容版本。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 字节码校验失败
        new(
            ["java.lang.VerifyError"],
            "字节码校验失败",
            "JVM 在加载类时发现字节码不合法，通常是模组 jar 损坏、下载被截断，或两个模组改写了同一个类。请重新下载该模组。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 模组文件无效
        new(
            ["is not a valid mod file", "Invalid mod file"],
            "模组文件无效",
            "加载器读不出某个 .jar 的模组元数据（mods.toml / fabric.mod.json 缺失或损坏）。"
            + "请确认它确实是模组而不是资源包或整合包，必要时重新下载。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // OpenJ9 虚拟机不受支持
        new(
            ["Open J9 is not supported", "OpenJ9 is incompatible", ".J9VMInternals."],
            "OpenJ9 虚拟机不受支持",
            "Minecraft 与 Forge / Fabric 依赖 HotSpot 的具体行为，OpenJ9 上会直接失败。"
            + "请改用 Adoptium / Microsoft / Zulu 等基于 HotSpot 的 Java。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // 本地库加载失败
        new(
            ["java.lang.UnsatisfiedLinkError"],
            "本地库加载失败",
            "游戏或模组需要的 .dll / .so 缺失或位数不匹配。请确认 Java 与操作系统的位数一致，"
            + "并检查杀毒软件是否隔离了本地库文件。",
            CrashSeverity.Error),

        // 方块或物品 ID 已耗尽
        new(
            ["maximum id range exceeded"],
            "方块或物品 ID 已耗尽",
            "1.12.2 及更早版本的方块 / 物品 ID 空间有限，装的模组太多会耗尽。请减少模组数量。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 文件已存在，需要手动删除
        new(
            ["java.nio.file.FileAlreadyExistsException"],
            "文件已存在，需要手动删除",
            "某个模组要创建的文件已存在且无法覆盖。请按报错里的路径删掉那个文件后重启游戏。",
            CrashSeverity.Warning),

        // OptiFine 与当前加载器不兼容
        new(
            [
                "TRANSFORMER/net.optifine",
                "NoSuchMethodError: 'java.lang.Class sun.misc.Unsafe.defineAnonymousClass",
                "Invalid Services found OptiFine",
            ],
            "OptiFine 与当前加载器不兼容",
            "这个 OptiFine 构建不支持当前的 Forge / NeoForge 版本。请换成对应的 OptiFine for Forge 构建，"
            + "或改用 Embeddium / Sodium 等替代的优化模组。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // 系统物理内存不足
        new(
            ["There is insufficient memory for the Java Runtime Environment to continue", "The system is out of physical RAM"],
            "系统物理内存不足",
            "整机可用内存不足，光调小堆内存也可能继续失败。请关闭其它占内存的程序，"
            + "并确认虚拟内存（分页文件）没有被禁用。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // 模组配置文件损坏
        new(
            ["com.electronwill.nightconfig.core.io.ParsingException"],
            "模组配置文件损坏",
            "某个模组的 .toml 配置解析失败，通常是文件被截断或写坏。请删掉 config 目录下报错里指向的那个文件，"
            + "让模组重新生成默认配置。",
            CrashSeverity.Warning),

        // ---------------- 新增 26 条（PCL2 规则全集里此前缺失的结论） ----------------

        // PCL: Java虚拟机参数有误
        new(
            ["Unrecognized option:"],
            "Java 虚拟机参数有误",
            "JVM 启动参数里有它不认识的选项。请在「设置 → Java → JVM 参数」里删掉自填的参数（或用「清空」恢复默认），"
            + "只保留启动器自动生成的 -Xmx / -D 项，再启动一次。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // PCL: 使用JDK
        new(
            ["java.lang.ClassCastException: java.base/jdk", "java.lang.ClassCastException: class jdk."],
            "使用了 JDK 而不是 JRE",
            "启动器指向的是 JDK 的 javaw.exe，Java 9 以后的 JDK 在模块化后与老版本 Forge 的反射调用不兼容。"
            + "请在「设置 → Java」里改选 JRE 路径（例如 …\\jdk8u402-b06\\jre\\bin\\javaw.exe），或直接装一个 Java 8 的 JRE。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // PCL: Java版本不兼容
        new(
            ["Unsupported major.minor version"],
            "Java 版本不兼容",
            "报错里的 class 文件版本与当前 Java 对不上：52 → Java 8，55 → Java 11，61 → Java 17，65 → Java 21。"
            + "请把「设置 → Java」里选中的 Java 换成报错要求的那个版本。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // PCL: Java版本不兼容（ASM 分支）
        new(
            ["Level is not supported by the active JRE or ASM version"],
            "ASM 不支持当前 Java 版本",
            "加载器自带的 ASM 读不了当前 Java 的字节码，常见于用 Java 17 / 21 启动 1.12.2–1.16.5 的 Forge。"
            + "请改用 Java 8；如果这个整合包必须用高版本 Java，请把 Forge 升级到官方声明支持该 Java 的构建。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // PCL: Mod需要Java11
        new(
            ["(class file version 55.0), this version of the Java Runtime only recognizes class file versions up to"],
            "模组需要 Java 11",
            "报错明确要求 class file version 55（也就是 Java 11）。请在「设置 → Java」里添加并选中一个 Java 11 的 javaw.exe，"
            + "再启动这个实例；本机没有 Java 11 的话启动器可以直接下载。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // PCL: 低版本Forge与高版本Java不兼容
        new(
            [
                "java.lang.NoSuchMethodError: sun.security.util.ManifestEntryVerifier",
                "'void sun.security.util.ManifestEntryVerifier",
            ],
            "低版本 Forge 与高版本 Java 不兼容",
            "老版本 Forge（1.16.5 的 36.2.x 早期构建）在 Java 8u321 及以上会因 ManifestEntryVerifier 签名变化崩溃。"
            + "请把 Forge 升级到 36.2.26 或更高版本，或换用低于 1.8.0.320 的 Java 8。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // PCL: MixinBootstrap缺失
        new(
            ["java.lang.ClassNotFoundException: org.spongepowered.asm.launch.MixinTweaker"],
            "缺少 MixinBootstrap",
            "Forge 1.12.2 及更早版本加载 Mixin 模组需要 MixinBootstrap。请把 MixinBootstrap 的 .jar 放进 mods 目录；"
            + "装好后仍报同样的错，就在它的文件名前加一个英文感叹号（!）强制最先加载。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: 显卡驱动不支持导致无法设置像素格式
        new(
            ["Couldn't set pixel format"],
            "显卡驱动无法设置像素格式",
            "OpenGL 上下文创建失败，多是显卡驱动过旧或退回了微软基础显示驱动。请到显卡厂商官网装最新驱动"
            + "（NVIDIA / AMD / Intel 的官方驱动，不要用系统自动更新），确认显示器接在独显接口上，再启动。",
            CrashSeverity.Warning),

        // PCL: 光影或资源包导致OpenGL1282错误
        new(
            ["1282: Invalid operation"],
            "疑似光影或资源包导致 OpenGL 1282 错误",
            "OpenGL 1282 通常是着色器 / 资源包给显卡驱动喂了非法数据。请先把 shaderpacks 目录里的光影全部移出，"
            + "再逐个启用资源包，定位到具体那一个后换用与当前 MC 版本匹配的版本。",
            CrashSeverity.Warning),

        // PCL: 材质过大或显卡配置不足
        new(
            ["Maybe try a lower resolution resourcepack?"],
            "材质分辨率过高或显存不足",
            "报错直接建议降低资源包分辨率。请把 128x / 256x / 512x 的材质换成 64x 或 32x，"
            + "或调低「视频设置 → 渲染距离」「模拟距离」，再启动。",
            CrashSeverity.Warning),

        // PCL: Fabric报错并给出解决方案
        new(
            ["A potential solution has been determined"],
            "Fabric 已给出解决方案",
            "Fabric 在报错里直接写了修复步骤（A potential solution 下面那段）。请按它列出的操作执行；"
            + "如果它要求换某个模组的版本，就按它给出的版本号去装。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenCrashReport),

        // PCL: Forge报错
        new(
            ["An exception was thrown, the game will display an error screen and halt."],
            "Forge 报错并中止加载",
            "Forge 在注册阶段抛异常并主动停止。把同一段日志往下翻，第一个「Caused by:」就是根因；"
            + "按那里出现的类名到 mods 目录里定位对应的 .jar，先禁用再启动。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenCrashReport),

        // PCL: Mod加载器报错
        new(
            ["Mod resolution failed"],
            "模组解析失败",
            "加载器在启动前解析模组时就失败了，游戏还没开始加载。真正的成因在同一段报错的下方："
            + "缺前置、版本范围不满足，或两个模组互斥。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: 确定Mod导致游戏崩溃（Caught exception from）
        new(
            ["Caught exception from "],
            "确定是模组导致的崩溃",
            "加载器直接点名了肇事模组。请在 mods 目录里禁用这个模组后再启动一次，确认问题消失，"
            + "然后到它的发布页换一个与当前 MC / 加载器匹配的构建。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder,
            ExtractPattern: @"(?<=Caught exception from )[^\s:(\r\n]+"),

        // PCL: 确定Mod导致游戏崩溃（注册表键冲突）
        new(
            ["Multiple entries with same key: "],
            "确定是模组导致的崩溃（注册表冲突）",
            "两个模组注册了同一个 id，后者注册时直接抛错。请把报错里那个 id 记下来，"
            + "在 mods 目录里一次禁用一半模组做二分，直到定位到注册同一 id 的两个 .jar 并删掉其中一个。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: 确定Mod导致游戏崩溃（加载器点名）
        new(
            ["due to errors, provided by '"],
            "确定是模组导致的崩溃（加载器点名）",
            "加载器在依赖解析阶段点名了肇事模组。请禁用报错里 provided by 后面那个模组，"
            + "或换一个满足报错列出的全部依赖版本范围的构建。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: Mod配置文件导致游戏崩溃
        new(
            ["Failed loading config file "],
            "模组配置文件加载失败",
            "某个模组的配置文件读不出来。请删掉报错里给出的那个文件（通常在 config 目录下），"
            + "让模组下次启动时重新生成默认配置。",
            CrashSeverity.Warning,
            ExtractPattern: @"(?<=Failed loading config file )[^\r\n]+"),

        // PCL: Mod互不兼容
        new(
            ["Incompatible mods found!"],
            "模组互不兼容",
            "报错里的 Incompatible mods found 后面会列出互斥的两个模组。请按它列出的组合二选一，"
            + "或者把两个都换到能共存的版本。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: Mod名称包含特殊字符
        new(
            ["Invalid module name: '' is not a Java identifier", "is not a Java identifier"],
            "模组名包含非法字符",
            "某个模组的 modId 里有 Java 标识符不允许的字符（空格、连字符等）。"
            + "请把 mods 目录里那个 .jar 换回官方发布的原版文件名，不要手动改名。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: 文件或内容校验失败
        new(
            ["signer information does not match signer information of other classes in the same package"],
            "文件或内容校验失败",
            "class 文件的签名信息不一致，通常是整合包或模组包被动过、下载被截断。"
            + "请删掉整个 mods 目录重新下载模组，下载时不要用会改写文件内容的加速器。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: OptiFine与Forge不兼容（方法签名分支）
        new(
            [
                "java.lang.NoSuchMethodError: 'void net.minecraft.client.renderer.texture.SpriteContents.<init>",
                "java.lang.NoSuchMethodError: 'java.lang.String com.mojang.blaze3d.systems.RenderSystem.getBackendDescription",
                "java.lang.NoSuchMethodError: 'void net.minecraft.client.renderer.block.model.BakedQuad.<init>",
                "java.lang.NoSuchMethodError: 'void net.minecraftforge.client.gui.overlay.ForgeGui.renderSelectedItemName",
                "java.lang.NoSuchMethodError: 'void net.minecraft.server.level.DistanceManager",
                "java.lang.NoSuchMethodError: 'net.minecraft.network.chat.FormattedText net.minecraft.client.gui.Font.ellipsize",
            ],
            "OptiFine 与 Forge 方法签名不兼容",
            "OptiFine 依赖的原版方法在当前 Forge / MC 版本里改了签名。请到 optifine.net/downloads 下载与"
            + "「当前 MC 版本 + 当前 Forge 版本」都对应的 OptiFine，或改用 Embeddium / Sodium + Iris。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: ShadersMod与OptiFine同时安装
        new(
            ["Shaders Mod detected. Please remove it"],
            "ShadersMod 与 OptiFine 重复安装",
            "OptiFine 已经内置了光影支持。请把 mods 目录里的 ShadersMod（Shaders-Mod / GLSL Shaders）删掉，只保留 OptiFine。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder),

        // PCL: 玩家手动触发调试崩溃
        new(
            ["Manually triggered debug crash"],
            "玩家手动触发的调试崩溃",
            "这条崩溃是手动触发调试（F3 + C）造成的，游戏本身没有问题。正常启动即可，不需要改配置。",
            CrashSeverity.Info),

        // HMCL: hs_err 里的原生层 OOM。与第一条同名「内存不足」，同层按标题去重，不会重复展示。
        new(
            ["Out of Memory Error"],
            "内存不足",
            "hs_err 报告里出现 Out of Memory Error，说明 JVM 在原生层就没拿到内存。"
            + "请把最大内存降到物理内存的一半以内，并确认分页文件（虚拟内存）没有被关闭。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // 找不到主类：版本 json 或 client.jar 缺失
        new(
            ["Could not find or load main class", "Error: Main method not found in class net.minecraft"],
            "找不到游戏主类",
            "Java 找不到 net.minecraft.client.main.Main，通常是版本 json 或 client.jar 缺失、下载不完整。"
            + "请在版本页对该版本点「修复」，或重新下载一次该版本。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.RepairVersion),

        // 环境变量覆盖内存参数（真实高频成因，报错里带 Picked up 证据）
        new(
            ["Picked up _JAVA_OPTIONS", "Picked up JAVA_TOOL_OPTIONS"],
            "环境变量覆盖了 JVM 参数",
            "系统环境变量 _JAVA_OPTIONS / JAVA_TOOL_OPTIONS 会覆盖启动器传入的 -Xmx，日志里「Picked up …」就是证据。"
            + "请在「系统属性 → 高级 → 环境变量」里删掉这两项，然后重启启动器。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenJavaSettings),

        // ---------------- hs_err 专属 6 条（只追加，不动上面 69 条） ----------------

        // hs_err：Problematic frame 落在显卡驱动模块里。
        // 模块名后面必须是 "+0x"：hs_err 的 Dynamic libraries 段也会列出这些 DLL，
        // 只看模块名会把「恰好加载了显卡驱动」的任何 hs_err 都判成驱动崩溃。
        new(
            [
                "nvoglv64.dll+0x", "nvoglv32.dll+0x", "atio6axx.dll+0x", "ig8icd64.dll+0x",
                "igdumdim64.dll+0x", "amdvlk64.dll+0x", "opengl32.dll+0x", "vulkan-1.dll+0x",
                "libnvidia-glcore",
            ],
            "疑似显卡驱动崩溃",
            "hs_err 的 Problematic frame 指向显卡驱动模块，崩溃点在原生层而不是模组。"
            + "请到 NVIDIA / AMD / Intel 官网安装最新驱动（不要用系统自动更新）。"
            + "启动器默认已在启动前把 javaw.exe 注册为「高性能 GPU」；若在「设置 → 游戏」里关掉了这个开关，"
            + "请重新打开，或到系统「图形设置」手动指定 javaw.exe。"
            + "然后关闭 Discord / RTSS / 显卡控制面板的覆盖层再启动。",
            CrashSeverity.Warning,
            AllOf: ["Problematic frame"]),

        // hs_err：Java 堆之外的原生内存申请失败（malloc / mmap）。
        new(
            ["Native memory allocation (malloc) failed", "Out of Memory Error (", "native memory exhausted"],
            "疑似原生内存耗尽",
            "hs_err 显示 JVM 在 Java 堆之外申请内存失败。请把最大内存降到物理内存的一半以内，"
            + "确认系统分页文件处于「系统管理的大小」；32 位 Java 的地址空间只有约 4 GB，请改用 64 位 Java。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings,
            AllOf: ["A fatal error has been detected"]),

        // hs_err：栈溢出（常见于递归世界生成 / 方块实体更新的模组）。
        new(
            ["StackOverflowError", "Stack guard pages", "stack overflow"],
            "疑似栈溢出",
            "hs_err 的 Current thread 段显示栈空间被耗尽。请先禁用最近新增的模组（递归世界生成 / 方块实体更新最容易触发），"
            + "再在「设置 → Java → JVM 参数」里临时加 -Xss2m 复测；仍然溢出就换 64 位 Java 并逐个排查模组。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenModsFolder,
            AllOf: ["A fatal error has been detected"]),

        // hs_err：JNI 符号缺失 / 本地库加载失败。
        new(
            ["UnsatisfiedLinkError", "JNI_ERR", "Failed to find JNI", "jni_FindClass", "wrong ELF class"],
            "疑似 JNI 本地符号缺失",
            "hs_err 显示 JVM 找不到本地库里的 JNI 符号，或本地库本身无法加载。"
            + "请确认 Java 位数与系统一致、杀毒软件没有隔离本地库、游戏路径不含特殊字符；"
            + "如果是某个模组自带的 .dll / .so，先禁用它并换用对应平台的构建。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings,
            AllOf: ["A fatal error has been detected"]),

        // hs_err：EXCEPTION_ACCESS_VIOLATION 且崩溃点不在已知显卡驱动里。
        new(
            ["EXCEPTION_ACCESS_VIOLATION"],
            "疑似未知 native 模块访问越界",
            "hs_err 的 Problematic frame 落在未知 native 模块里，不是已知的显卡驱动。"
            + "这类崩溃常见于输入法钩子（中文输入法注入）、杀毒软件 / 反作弊注入、Discord / RTSS / FPS 显示等覆盖层。"
            + "请逐个关闭这些注入后再启动；仍崩溃就把 Problematic frame 那一行连同 hs_err 发到求助渠道。",
            CrashSeverity.Warning,
            AllOf: ["Problematic frame"],
            Excludes: ["nvoglv", "atio6axx", "ig8icd", "igdumdim", "amdvlk", "opengl32.dll", "vulkan-1.dll", "libnvidia"]),

        // hs_err：有 JVM 致命错误头，却没有 Problematic frame 段（报告被截断或崩在 JVM 初始化早期）。
        // 作为兜底规则：已经有专属结论的症状（栈溢出 / 原生内存 / JNI）不再重复报「报告不完整」。
        new(
            ["A fatal error has been detected by the Java Runtime Environment"],
            "疑似 hs_err 报告不完整",
            "hs_err 里没有 Problematic frame 段，说明文件被截断，或崩溃发生在 JVM 初始化早期。"
            + "请提供完整的 hs_err_pid*.log（从第一行到末尾），并先确认 Java 是 64 位、最大内存不超过物理内存的一半、分页文件没有被关闭。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenLogFolder,
            Excludes:
            [
                "Problematic frame", "StackOverflowError", "stack overflow", "Out of Memory Error",
                "Native memory allocation", "JNI_ERR", "UnsatisfiedLinkError",
            ]),
    ];

    // ==================== Crit2：需要上下文 ====================

    /// <summary>
    /// 第二层：上下文判定。只有 Crit1 完全没有结论时才跑；
    /// 层内按声明顺序收集，同一症状的互斥分支靠 <see cref="CrashRule.Excludes"/> 拆开。
    /// </summary>
    internal static readonly CrashRule[] Crit2 =
    [
        // PCL: 使用32位Java导致JVM无法分配足够多的内存。
        // 请求固定大小堆却预留失败，说明卡在 32 位进程的 4 GB 地址空间上限上。
        new(
            ["Could not reserve enough space"],
            "32 位 Java 或分配内存过大",
            "JVM 没能预留出请求的堆：报错里给出了具体堆大小（object heap），说明卡在 32 位进程的约 4 GB 地址空间上限。"
            + "请在「设置 → Java」换成 64 位 Java（javaw.exe 路径里通常带 x64 / 不带 x86），并把最大内存设到物理内存的一半以内。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings,
            AllOf: ["object heap"]),

        // PCL: 内存不足。没写 object heap 时，失败原因更可能是整机内存 / 分页文件，
        // 此时把锅甩给 32 位 Java 会误导用户。
        new(
            ["Could not reserve enough space"],
            "内存不足（JVM 预留内存失败）",
            "JVM 连请求的堆都预留不出来，且报错里没有 object heap 字样，通常是整机可用内存或分页文件不足。"
            + "请关闭浏览器 / 其它游戏等占内存的程序，在「设置 → 启动」把最大内存调低到物理内存的一半以内，"
            + "并确认系统分页文件处于「系统管理的大小」。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenJavaSettings,
            Excludes: ["object heap"]),

        // PCL: Intel驱动不兼容导致EXCEPTION_ACCESS_VIOLATION
        new(
            ["EXCEPTION_ACCESS_VIOLATION"],
            "Intel 显卡驱动导致 EXCEPTION_ACCESS_VIOLATION",
            "崩溃点落在 Intel 显卡驱动模块（ig8icd64.dll / igdumdim64.dll）里，不是模组的问题。"
            + "请到 Intel 官网装最新的核显驱动。"
            + "若本机有独立显卡，确认「设置 → 游戏 → 启动前注册高性能 GPU」是打开的（默认打开），"
            + "启动器会在启动前把 javaw.exe 交给独显运行。",
            CrashSeverity.Error,
            AllOf: ["# C  [ig"]),

        // PCL: AMD驱动不兼容导致EXCEPTION_ACCESS_VIOLATION
        new(
            ["EXCEPTION_ACCESS_VIOLATION"],
            "AMD 显卡驱动导致 EXCEPTION_ACCESS_VIOLATION",
            "崩溃点落在 AMD 显卡驱动模块（atio6axx.dll）里，不是模组的问题。"
            + "请到 AMD 官网装最新的 Adrenalin 驱动；装完仍然崩溃就回退到上一个大版本驱动，并关闭驱动的超频 / 风扇自定义档。",
            CrashSeverity.Error,
            AllOf: ["# C  [atio"]),

        // PCL: Nvidia驱动不兼容导致EXCEPTION_ACCESS_VIOLATION
        new(
            ["EXCEPTION_ACCESS_VIOLATION"],
            "NVIDIA 显卡驱动导致 EXCEPTION_ACCESS_VIOLATION",
            "崩溃点落在 NVIDIA 显卡驱动模块（nvoglv64.dll）里，不是模组的问题。"
            + "请到 NVIDIA 官网装最新的 Game Ready 驱动；仍崩溃就回退到上一个大版本，或在驱动面板里关掉「线程优化」。",
            CrashSeverity.Error,
            AllOf: ["# C  [nvoglv"]),

        // PCL: OptiFine与Forge不兼容（依赖声明分支）
        new(
            ["has mods that were not found"],
            "OptiFine 与 Forge 不兼容（缺少依赖声明）",
            "报错指出 OptiFine 的 jar 里声明了找不到的依赖，说明这个 OptiFine 构建与当前 Forge 版本不配套。"
            + "请到 optifine.net/downloads 下载与当前 MC + Forge 对应的构建，或改用 Embeddium / Sodium + Iris。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder,
            AllOf: ["OptiFine"]),

        // PCL: OptiFine导致无法加载世界
        new(
            ["ChunkManager$ProxyTicketManager.shouldForceTicks"],
            "OptiFine 导致无法加载世界",
            "这个 NoSuchMethodError 只在特定 OptiFine 版本里出现，换一个 OptiFine 构建即可。"
            + "建议先换到与当前 Forge 同期的 OptiFine 版本（optifine.net/downloads 里按 MC 版本选），不要混用不同 MC 版本的 jar。",
            CrashSeverity.Error,
            CrashAnalyzer.Actions.OpenModsFolder,
            AllOf: ["OptiFine"]),

        // PCL: 怀疑Mod导致游戏崩溃
        new(
            ["Suspected Mod"],
            "怀疑模组导致崩溃",
            "崩溃报告列出了一个「疑似肇事」的模组清单。请先禁用清单里第一个模组再启动，"
            + "确认问题消失后到它的发布页换一个与当前 MC / 加载器匹配的构建。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenModsFolder,
            Excludes: ["Suspected Mods: None"],
            ExtractPattern: @"\((?<value>[^)\r\n]+)\)"),

        // PCL: 特定方块导致崩溃
        new(
            ["\tBlock location: World: "],
            "特定方块导致崩溃",
            "崩溃发生在某个具体方块的 tick / 渲染里。请按报告里「Block location: World: (x, y, z)」的坐标"
            + "用 /setblock 或创造模式把它挖掉再进世界；如果它是某个模组的方块，禁用那个模组即可。",
            CrashSeverity.Warning,
            ExtractPattern: @"Block: Block\{(?<value>[^}\r\n]+)\}"),

        // PCL: 特定实体导致崩溃
        new(
            ["\tEntity's Exact location: "],
            "特定实体导致崩溃",
            "崩溃发生在某个实体的 tick 里。请按报告里「Entity's Exact location」的坐标把那个实体清掉"
            + "（创造模式直接击杀，或用 /kill @e[type=实体ID,distance=..8]）；如果是模组实体，禁用对应模组。",
            CrashSeverity.Warning,
            ExtractPattern: @"Entity Type: (?<value>[^\s\r\n(]+)"),
    ];

    // ==================== Crit3：低置信度兜底 ====================

    /// <summary>
    /// 第三层：兜底。走到这里说明前两层与堆栈归因都没有结论，
    /// 只能给出方向性提示，文案必须写「疑似」，不能让用户以为已经定位到根因。
    /// </summary>
    internal static readonly CrashRule[] Crit3 =
    [
        new(
            ["The game crashed whilst "],
            "游戏崩溃，成因未归类",
            "游戏确实崩了，但报错里没有本启动器认识的成因。请把 crash-reports 目录里最新的 .txt 完整内容发到求助渠道，"
            + "并同时说明装了哪些模组、用的是哪个 Java 版本。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenCrashReport),

        new(
            ["A fatal error has been detected by the Java Runtime Environment"],
            "疑似 JVM 原生层崩溃",
            "这类 hs_err 崩溃发生在 JVM / 本地库层面，模组只可能是诱因。请先更新显卡驱动与 Java（用 64 位 Java 8 / 17），"
            + "再把 hs_err_pid*.log 的「Problematic frame」那一行发出去定位。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenLogFolder),

        new(
            ["---- Minecraft Crash Report ----"],
            "崩溃报告未匹配到已知成因",
            "报告结构完整，但其中的特征本启动器还没有对应规则。请打开 crash-report，"
            + "从「Description:」往下找第一个属于模组包名的 at 行，那里通常就是肇事模组。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenCrashReport),

        // PCL: 崩溃原因未能归类 / 极短输出（ModCrash.vb:AnalyzeCrit3 :649-660）。
        //
        // 判据是五个条件<b>同时</b>成立，缺一个都不判「没能启动」：
        //   （1）没有 at net. —— 没有堆栈，游戏没跑到 Java 代码；
        //   （2）没有 INFO] —— 没有完整日志格式，说明连日志框架都没初始化；
        //   （3）没有 hs_err —— 不是 JVM 原生层崩溃（那种有专属规则）；
        //   （4）没有 crash report —— 不是 Java 层崩溃（那种也有专属规则）；
        //   （5）程序输出不足 100 字符。
        //
        // 为什么不能只看长度：Forge 1.17+ 把加载日志改写到别的文件，latest.log 尾部常常只剩
        // 几十个字符，但游戏其实正常起来了——那种日志里一定有 INFO]。反过来，
        // 「Java 版本不对 / 内存参数非法 / 加载器 jar 缺失」这三种启动即死的场景，
        // Java 进程往往只吐一行错误就退出，正好是这五个条件都成立的样子。
        //
        // 必须排在 Crit3 最后一个：它是兜底，前面任何一条规则命中都说明能说出更具体的成因，
        // 不能让「没能启动」把这些结论盖掉。
        new(
            [],
            "游戏没能启动",
            "程序几乎没有输出就退出了，Minecraft 还没走到打印日志的阶段——这不是「未知崩溃」，"
            + "而是进程在启动阶段就失败了。按出现概率排："
            + "（1）Java 版本与游戏版本不匹配（1.12.2 需要 8，1.17+ 需要 17，1.20.5+ 需要 21，26.x 需要 25）；"
            + "（2）最大内存超过物理内存，JVM 连堆都开不出来；"
            + "（3）versions\\<id> 里的 client.jar 或加载器 jar 缺失、下载不完整。"
            + "下一步：先到「设置 → Java」确认选中的 Java 版本与游戏版本匹配（也可以在启动器的日志窗口"
            + "直接看 Java 进程打印的原始错误）；再到「设置 → 启动」把最大内存调到物理内存的一半以内，"
            + "自动分配内存开着的先关掉手动填一个数；然后在版本页对该版本点「修复」，最后启动一次。",
            CrashSeverity.Warning,
            CrashAnalyzer.Actions.OpenJavaSettings,
            Excludes:
            [
                // 条件（1）（2）：有堆栈或完整日志格式，说明游戏 / 加载器真的跑起来过。
                "at net.",
                "INFO]",
                // 条件（3）（4）：原生层崩溃与结构完整的崩溃报告各有专属规则，不该被这条抢走。
                "A fatal error has been detected",
                "---- Minecraft Crash Report ----",
                "#@!@#",
                // 同层已能给出结论时不要再补一条矛盾的「没能启动」：
                // 原版崩溃提示本身就证明游戏起来了并且崩了。
                "The game crashed whilst",
                // 极端情况：外部导入的 crash report 被截断到不足 100 字符。
                // 它仍然是报告而不是空输出，按「报告不完整」处理更接近事实。
                "Description:",
                "A detailed walkthrough",
            ],
            MaxLength: 100),
    ];

    /// <summary>三层规则的全集，顺序为 Crit1 → Crit2 → Crit3，用于统计与自检。</summary>
    internal static readonly CrashRule[] All = [.. Crit1, .. Crit2, .. Crit3];
}
