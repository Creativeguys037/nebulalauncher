using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Java;

/// <summary>
/// Minecraft 版本 → 所需 Java 主版本。
///
/// 优先级：本地版本 JSON 的 <c>javaVersion.majorVersion</c>（权威，随版本清单下发）→ 本表兜底。
/// 表只覆盖「拿不到版本 JSON」的场景（未安装、下载页选择阶段、损坏的版本目录）。
///
/// 规则来源：
/// - ≤1.16 → Java 8
/// - 1.17 → Java 16（Mojang java-runtime-alpha）
/// - 1.18～1.20.4 → Java 17（java-runtime-gamma）
/// - 1.20.5～25.x → Java 21（java-runtime-delta）
/// - 26.1 起 → Java 25（java-runtime-epsilon，Mojang 自 2026 年改用年份版本号）
///
/// <see cref="Get"/> 只给下界（UI 文案用）；上下界与加载器条件见 <see cref="AllowedRange"/>、
/// <see cref="Evaluate"/> 与规则表 <see cref="All"/>。
/// </summary>
public static class JavaRequirementTable
{
    /// <summary>沿 inheritsFrom 链最多向上找几层（加载器 → 原版，通常 1 层）。</summary>
    private const int MaxInheritanceDepth = 4;

    /// <summary>游戏版本键的进制：minor 与 patch 各占独立的十进制位（1.20.4 → 20 * 1000 + 4）。</summary>
    private const int GameKeyScale = 1000;

    /// <summary>Java 区间无上界。</summary>
    private const int NoUpperBound = int.MaxValue;

    /// <summary>
    /// <c>GAME_JSON</c> 生效的最小游戏版本键：1.7.10。
    /// 见 JavaVersionConstraint.java:52-53——1.7.2 + Forge 在版本 JSON 里被记成 Java 8 并不准确，HMCL 因此跳过 1.7.10 以前。
    /// </summary>
    private const int GameJsonMinGameKey = 7010;

    /// <summary>
    /// HMCL <c>JavaVersionConstraint</c>（JavaVersionConstraint.java:33-223）里能量化成
    /// 「游戏版本 × Java 主版本 × 加载器」的 8 条规则；数组顺序即 <see cref="Evaluate"/> 的报错优先级。
    ///
    /// 其余 3 条没有单列，原因：
    /// <list type="bullet">
    /// <item><c>CLEANROOM</c>（JavaVersionConstraint.java:108）：<see cref="LoaderKind"/> 没有 Cleanroom 值，
    /// 加了也只能永远不匹配，等真正支持 Cleanroom 时再补；</item>
    /// <item><c>VANILLA_LINUX_JAVA_8</c>（:145）：生效条件是「Linux x64 且 ≤1.12.999 → Java ≤8」，
    /// 与 <c>LAUNCH_WRAPPER</c> 的区间完全重合，合并进后者；</item>
    /// <item><c>VANILLA_X86</c>（:161）：判据是 CPU 架构（ARM64 必须换 x86 Java）而不是 Java 主版本，
    /// 留给启动前置检查用 <see cref="JavaInfo.Is64Bit"/> / 架构信息判断。</item>
    /// </list>
    ///
    /// <c>VANILLA</c>（:33）与 <c>GAME_JSON</c>（:47）是下界规则，下界随游戏版本变化，
    /// 由 <see cref="Evaluate"/> / <see cref="AllowedRange"/> 运行时构造，不放进这张静态表。
    /// </summary>
    public static readonly JavaConstraint[] All =
    [
        // JavaVersionConstraint.java:130 LAUNCH_WRAPPER（mandatory）。
        // LaunchWrapper ≤1.12 假定系统类加载器是 URLClassLoader（Java 8 才有），Java 9+ 直接 ClassCastException。
        // HMCL 的原始判据还要求 mainClass == net.minecraft.launchwrapper.Launch 且 launchwrapper < 1.13；
        // 我们手里只有游戏版本号，而 1.13 之前的原版与 Forge 都走 LaunchWrapper，于是按版本线收敛成「≤1.12.999 → Java ≤8」。
        new(Mandatory: true, Game: (0, 12999), Java: (0, 8), Id: "LAUNCH_WRAPPER"),

        // JavaVersionConstraint.java:180 MODLAUNCHER_8。HMCL 里它其实是 suggested（枚举第二个参数 false），
        // 但 1.16.3～1.17.1 的 Forge 在 Java 18+ 上会因 JDK-8273826 崩在 modlauncher，
        // 这里收成 mandatory 上界 17：Java 8/11/16/17 全放行，只挡 18+。
        // 注意上界不能收到 16——1.17 Forge + Java 17 是合法组合。
        new(Mandatory: true, Game: (16003, 17001), Java: (0, 17), RequiresLoader: LoaderKind.Forge, Id: "MODLAUNCHER_8"),

        // JavaVersionConstraint.java:142 VANILLA_JAVA_8_51（suggested）：≥1.13 在 Java [1.8,1.8.0_51) 上建世界会崩。
        // 我们只记主版本，1.8.0_51 这个补丁号粒度表达不出来，退化成「≥1.13 建议 Java ≥8」。
        new(Mandatory: false, Game: (13000, NoUpperBound), Java: (8, NoUpperBound), Id: "VANILLA_JAVA_8_51"),

        // JavaVersionConstraint.java:68 MODDED_JAVA_7（suggested）：≤1.7.2 的 Forge 需要 Java ≤7，
        // 但 LegacyModFixer 能修好，所以只建议不阻断。
        new(Mandatory: false, Game: (0, 7002), Java: (0, 7), RequiresLoader: LoaderKind.Forge, Id: "MODDED_JAVA_7"),

        // JavaVersionConstraint.java:76 MODDED_JAVA_8（suggested）：1.7.10～1.16.999 的 Forge 建议 Java 8。
        // 只是建议：Java 17 跑 1.16.5 Forge 能起来的场景不该被拦（本任务的硬性要求）。
        new(Mandatory: false, Game: (7010, 16999), Java: (8, 8), RequiresLoader: LoaderKind.Forge, Id: "MODDED_JAVA_8"),

        // JavaVersionConstraint.java:84 MODDED_JAVA_16（suggested）：1.17.x 的 Forge 建议 Java 16，
        // 但 Java 17 也能跑，所以这里绝不能是 mandatory。
        new(Mandatory: false, Game: (17000, 17999), Java: (16, 16), RequiresLoader: LoaderKind.Forge, Id: "MODDED_JAVA_16"),

        // JavaVersionConstraint.java:92 MODDED_JAVA_17（suggested）：1.18～1.20.4 的 Forge 建议 Java 17。
        new(Mandatory: false, Game: (18000, 20004), Java: (17, 17), RequiresLoader: LoaderKind.Forge, Id: "MODDED_JAVA_17"),

        // JavaVersionConstraint.java:100 MODDED_JAVA_21（suggested）：≥1.20.5 的 Forge 建议 Java 21。
        new(Mandatory: false, Game: (20005, NoUpperBound), Java: (21, 21), RequiresLoader: LoaderKind.Forge, Id: "MODDED_JAVA_21"),
    ];

    /// <summary>
    /// 加载器版本级的上限规则（PCL2 <c>Modules/Minecraft/ModJava.vb:GetJavaRequirement</c> 的专属上限段）。
    ///
    /// 与 <see cref="All"/> 的分工：<see cref="All"/> 只按「游戏版本 + 加载器种类」判，
    /// 这张表再按「加载器自己的版本号」细分。两个维度必须叠在一起看——
    /// 同为 Forge 1.16.5，build 36.2.25 与 36.2.26 的 Java 上限差着一整个大版本。
    ///
    /// <b>为什么补丁号级的上限只能做建议、不能阻断：</b>
    /// <c>8u320</c> 这类上限是「当时社区验证过的最高的一个补丁号」，不是一道硬边界。
    /// 用户在 <c>8u322</c> 上跑 1.16.5 Forge 通常完全正常，写成 mandatory 会在能跑的环境上
    /// 直接拦掉启动，而用户手里没有绕过手段——他不能为了一个建议去专门装回旧补丁号。
    /// 大版本级的边界（<c>&lt; J17</c> / <c>&lt; J22</c>）是另一回事：那是 modlauncher 在
    /// 字节码 / 反射层面的失败，越过去必然起不来，拦下来是替用户省一次崩溃。
    ///
    /// 区间一律「下界含、上界不含」，这样「45.0.21–45.0.65 含 65」与「&lt; 0.17.0 不含 0.17.0」
    /// 能用同一套字段表达，不必为开闭区间各加一个 flag。
    /// 数组顺序即匹配优先级：同游戏版本的两条相接区间（如 36.2.25 / 36.2.26）必须先细分后粗分。
    /// </summary>
    private static readonly LoaderLimitRule[] LoaderRules =
    [
        // Forge 1.16.3–1.16.5，build ≤ 36.2.25 → 最高 8u320（建议级：补丁号粒度）。
        new(LoaderKind.Forge, 16003, 16005, BuildMin: null, BuildMaxExclusive: [36, 2, 26],
            MaxMajor: 8, MaxPatch: "8u320", IsBlocking: false, Id: "FORGE_1_16_BUILD_8U320",
            Reason: "该 Forge 版本验证到 Java 8u320 为止；更高补丁号的 Java 8 通常也能用，建议优先 8u320。"),

        // Forge 1.16.3–1.16.5，build ≥ 36.2.26 → < J24（阻断级：大版本粒度）。
        // 36.2.26 修掉了旧版在 Java 18+ 上的 modlauncher 崩溃，但仍撑不到 Java 24。
        new(LoaderKind.Forge, 16005, 16005, BuildMin: [36, 2, 26], BuildMaxExclusive: [37, 0, 0],
            MaxMajor: 23, MaxPatch: null, IsBlocking: true, Id: "FORGE_1_16_BUILD_J24",
            Reason: "Forge 36.2.26 起的 1.16.x 版本在 Java 24 及以上会崩在 modlauncher，最高用 Java 23。"),

        // Forge 1.17.1，build 37.0.0–37.0.79 → < J17。
        // 比表里的 MODLAUNCHER_8（上界 17）更严一档：这一段 build 连 Java 17 都撑不住。
        new(LoaderKind.Forge, 17001, 17001, BuildMin: [37, 0, 0], BuildMaxExclusive: [37, 0, 80],
            MaxMajor: 16, MaxPatch: null, IsBlocking: true, Id: "FORGE_1_17_BUILD_J17",
            Reason: "Forge 37.0.79 及更早的 1.17.1 版本在 Java 17 及以上会崩在 modlauncher，最高用 Java 16。"),

        // Forge 1.19.4，build 45.0.21–45.0.65 → < J20。
        new(LoaderKind.Forge, 19004, 19004, BuildMin: [45, 0, 21], BuildMaxExclusive: [45, 0, 66],
            MaxMajor: 19, MaxPatch: null, IsBlocking: true, Id: "FORGE_1_19_4_BUILD_J20",
            Reason: "Forge 45.0.65 及更早的 1.19.4 版本在 Java 20 及以上会崩，最高用 Java 19。"),

        // Forge 1.19.4，build 45.0.66–47.4.8 → < J22。
        new(LoaderKind.Forge, 19004, 20001, BuildMin: [45, 0, 66], BuildMaxExclusive: [47, 4, 9],
            MaxMajor: 21, MaxPatch: null, IsBlocking: true, Id: "FORGE_1_19_4_BUILD_J22",
            Reason: "Forge 45.0.66–47.4.8 的 1.19.4–1.20.1 版本在 Java 22 及以上会崩，最高用 Java 21。"),

        // NeoForge 1.20.1 → < J22。这一代的 NeoForge 沿用 Forge 编号（47.1.x），
        // 所以不能靠 build 号区间认，只能按游戏版本认。
        new(LoaderKind.NeoForge, 20001, 20001, BuildMin: null, BuildMaxExclusive: null,
            MaxMajor: 21, MaxPatch: null, IsBlocking: true, Id: "NEOFORGE_1_20_1_J22",
            Reason: "NeoForge 1.20.1 在 Java 22 及以上会崩，最高用 Java 21。"),

        // NeoForge ≤ 20.2.62-beta → < J22（1.20.2 起 NeoForge 改用「<mc 次版本>.<补丁>」编号）。
        new(LoaderKind.NeoForge, 0, NoUpperBound, BuildMin: null, BuildMaxExclusive: [20, 2, 63],
            MaxMajor: 21, MaxPatch: null, IsBlocking: true, Id: "NEOFORGE_EARLY_J22",
            Reason: "NeoForge 20.2.62-beta 及更早在 Java 22 及以上会崩，最高用 Java 21。"),

        // Fabric Loader < 0.17.0 → < J25。
        // Fabric 的 loader 版本与游戏版本完全解耦，因此这条不限定游戏版本区间。
        new(LoaderKind.Fabric, 0, NoUpperBound, BuildMin: null, BuildMaxExclusive: [0, 17, 0],
            MaxMajor: 24, MaxPatch: null, IsBlocking: true, Id: "FABRIC_LOADER_J25",
            Reason: "Fabric Loader 0.17.0 之前在 Java 25 上会崩，最高用 Java 24；升级 loader 后即可用 Java 25。"),
    ];

    /// <summary>
    /// 加载器版本 → 额外 Java 上限；这一组条件下没有专属上限时返回 null。
    ///
    /// 游戏版本解析不出来、或者加载器是原版，直接返回 null——「不知道」比「假装没有上限」更安全，
    /// 调用方据此保持原有行为。
    ///
    /// 加载器版本号缺失时：只看游戏版本的规则（NeoForge 1.20.1）仍然命中，
    /// 需要在 build 号上比区间的规则一律跳过。这样老实例（<c>instance.json</c> 里没有
    /// loader 版本）不会丢掉本该有的上限，也不会被一个猜出来的区间误判。
    /// </summary>
    /// <param name="minecraftVersion">原版版本号，例如 <c>1.16.5</c>。</param>
    /// <param name="loader">加载器种类。</param>
    /// <param name="loaderVersion">
    /// 加载器自身版本号。接受 <c>36.2.25</c>（Forge build）、<c>14.23.5.2859</c>（老 Forge）、
    /// <c>0.16.9</c>（Fabric）、<c>20.2.62-beta</c> / <c>21.1.77</c>（NeoForge），
    /// 以及被错误地连带原版号写进去的 <c>1.20.1-47.1.3</c>。
    /// </param>
    public static JavaLoadLimit? GetLoaderLimit(
        string? minecraftVersion,
        LoaderKind loader,
        string? loaderVersion)
    {
        if (loader == LoaderKind.Vanilla || !TryGetGameKey(minecraftVersion, out var gameKey))
        {
            return null;
        }

        // 加载器版本号可能是空 / 脏的（老实例、手工改过目录名）。这时仍然允许命中
        // 「只看游戏版本」的规则（例如 NeoForge 1.20.1），但不能拿去比区间——
        // 「不知道 build 号」不等于「build 号落在区间里」。
        int[]? parts = null;

        if (!string.IsNullOrWhiteSpace(loaderVersion)
            && TryParseLoaderVersion(loaderVersion, minecraftVersion, out var parsed))
        {
            parts = parsed;
        }

        foreach (var rule in LoaderRules)
        {
            if (rule.Loader != loader || gameKey < rule.GameMin || gameKey > rule.GameMax)
            {
                continue;
            }

            if (rule.RequiresBuild && parts is null)
            {
                continue;
            }

            if (parts is not null && !rule.MatchesBuild(parts))
            {
                continue;
            }

            return new JavaLoadLimit(rule.Id, rule.Reason, rule.MaxMajor, rule.MaxPatch, rule.IsBlocking);
        }

        return null;
    }

    /// <summary>
    /// 把加载器版本号拆成可比较的数字段：<c>36.2.25</c> → <c>[36,2,25]</c>，
    /// <c>20.2.62-beta</c> → <c>[20,2,62]</c>（后缀不参与比较，视为该版本本身）。
    ///
    /// 容错处理的两种脏数据：
    /// <list type="bullet">
    /// <item>原版版本号被连带写进来（<c>1.20.1-47.1.3</c>）：先按 <paramref name="minecraftVersion"/>
    /// 剥掉前缀，否则第一段会变成 1，与 Forge 的 47 完全无法比较；</item>
    /// <item>预发布后缀（<c>-beta</c>、<c>-alpha</c>）：直接丢弃数字段之外的一切，
    /// 于是 <c>20.2.62-beta</c> 与 <c>20.2.62</c> 等价——这与 PCL2 表里「≤ 20.2.62-beta」的写法一致。</item>
    /// </list>
    /// </summary>
    internal static bool TryParseLoaderVersion(
        string? loaderVersion,
        string? minecraftVersion,
        out int[] parts)
    {
        parts = [];

        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            return false;
        }

        var text = loaderVersion.Trim();

        if (!string.IsNullOrWhiteSpace(minecraftVersion))
        {
            var prefix = minecraftVersion.Trim() + "-";

            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
            }
        }

        var matches = Regex.Matches(text, @"\d+");

        if (matches.Count == 0)
        {
            return false;
        }

        var parsed = new int[matches.Count];

        for (var i = 0; i < matches.Count; i++)
        {
            if (!int.TryParse(matches[i].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            parsed[i] = value;
        }

        parts = parsed;
        return true;
    }

    /// <summary>逐段比较版本号，短的一方按 0 补齐（<c>45.0.21 &lt; 45.0.21.0</c> 视为相等）。</summary>
    internal static int CompareParts(int[] left, int[] right)
    {
        var length = Math.Max(left.Length, right.Length);

        for (var i = 0; i < length; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;

            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    public static JavaRequirement? Get(string? minecraftVersion)
    {
        // 26.1 / 26.3 这类年份版本号：2026 年起 Mojang 要求 Java 25。
        if (TryParseYearRelease(minecraftVersion, out var year))
        {
            return year >= 26
                ? new JavaRequirement(25, $"{minecraftVersion} 属于 26.x 版本线，需要 Java 25")
                : null;
        }

        if (!TryParseRelease(minecraftVersion, out var minor, out var patch))
        {
            return null;
        }

        if (minor <= 16)
        {
            return new JavaRequirement(8, $"{minecraftVersion} 属于 1.16 及更早的版本线，需要 Java 8");
        }

        if (minor == 17)
        {
            return new JavaRequirement(16, $"{minecraftVersion} 属于 1.17 版本线，需要 Java 16");
        }

        if (minor < 20 || (minor == 20 && patch <= 4))
        {
            return new JavaRequirement(17, $"{minecraftVersion} 属于 1.18～1.20.4 版本线，需要 Java 17");
        }

        return new JavaRequirement(21, $"{minecraftVersion} 属于 1.20.5～25.x 版本线，需要 Java 21");
    }

    /// <summary>
    /// 某个「游戏版本 + 加载器」允许的 Java 主版本区间（只算 mandatory 规则的交集）。
    ///
    /// 未知版本（快照、Beta、空串）返回 null：拿不到权威信息时不装作知道。
    /// 区间可能被压成 <c>Min &gt; Max</c>（当前规则表不会出现），调用方按「没有可用 Java」处理即可。
    /// </summary>
    /// <param name="minecraftVersion">原版版本号，例如 <c>1.20.1</c>、<c>26.1</c>。</param>
    /// <param name="loader">加载器种类；Forge 才吃 <c>MODLAUNCHER_8</c> 上界。</param>
    /// <param name="jsonJavaMajor">本地版本 JSON 的 <c>javaVersion.majorVersion</c>；有值时作为下界来源（GAME_JSON）。</param>
    public static (int Min, int Max)? AllowedRange(
        string? minecraftVersion,
        LoaderKind loader,
        int? jsonJavaMajor = null)
        => AllowedRangeCore(minecraftVersion, loader, jsonJavaMajor, null);

    private static (int Min, int Max)? AllowedRangeCore(
        string? minecraftVersion,
        LoaderKind loader,
        int? jsonJavaMajor,
        JavaLoadLimit? loaderLimit)
    {
        if (!TryGetGameKey(minecraftVersion, out var gameKey))
        {
            return null;
        }

        var min = 0;
        var max = NoUpperBound;

        if (TryGetMandatoryLowerBound(minecraftVersion, gameKey, jsonJavaMajor, out var lower) && lower > min)
        {
            min = lower;
        }

        foreach (var constraint in All)
        {
            if (!constraint.Mandatory || !constraint.MatchesGame(gameKey) || !constraint.MatchesLoader(loader))
            {
                continue;
            }

            if (constraint.Id == "MODLAUNCHER_8" && loaderLimit?.Id == "FORGE_1_16_BUILD_J24")
            {
                continue;
            }

            if (constraint.Java.Min > min)
            {
                min = constraint.Java.Min;
            }

            if (constraint.Java.Max < max)
            {
                max = constraint.Java.Max;
            }
        }

        return (min, max);
    }

    /// <summary>
    /// <see cref="AllowedRange(string?, LoaderKind, int?)"/> 的加载器版本感知版：
    /// 在原有区间之上再叠一层阻断级的加载器专属上限。
    ///
    /// 只合并 <see cref="JavaLoadLimit.IsBlocking"/> 为 true 的限。
    /// 建议级（<c>8u320</c> 这种补丁号）不进区间——区间会被拿去挑运行时、拦启动，
    /// 把一个建议当硬边界就等于替用户否掉一个能跑的组合。
    ///
    /// 单独重载而不是改原签名：原签名的三个参数是既有调用方（<c>JavaService</c>、前置检查、
    /// 一批用例）共用的，加可选参数会让「有没有传加载器版本」在调用点看不出来。
    /// </summary>
    public static (int Min, int Max)? AllowedRange(
        string? minecraftVersion,
        LoaderKind loader,
        int? jsonJavaMajor,
        string? loaderVersion)
    {
        var limit = GetLoaderLimit(minecraftVersion, loader, loaderVersion);
        var range = AllowedRangeCore(minecraftVersion, loader, jsonJavaMajor, limit);

        if (range is null)
        {
            return null;
        }

        if (limit is not { IsBlocking: true, MaxMajor: int loadMaxMajor })
        {
            return range;
        }

        var (min, max) = range.Value;

        return (min, Math.Min(max, loadMaxMajor));
    }

    /// <summary>
    /// 判断一个具体 Java 主版本是否落在某规则集内，返回**第一条**被违反的 mandatory 规则与 suggested 规则。
    ///
    /// 两级的语义与 HMCL <c>findSuitableJava</c>（JavaManager.java:318-360）一致：
    /// mandatory 违规 → 这个 Java 不可用；suggested 违规 → 只降权，不阻断。
    /// 两者都为 null 表示完全通过。
    /// </summary>
    public static (JavaConstraint? Mandatory, JavaConstraint? Suggested) Evaluate(
        string? minecraftVersion,
        LoaderKind loader,
        int? jsonJavaMajor,
        int javaMajor)
        => EvaluateCore(minecraftVersion, loader, jsonJavaMajor, javaMajor, null);

    private static (JavaConstraint? Mandatory, JavaConstraint? Suggested) EvaluateCore(
        string? minecraftVersion,
        LoaderKind loader,
        int? jsonJavaMajor,
        int javaMajor,
        JavaLoadLimit? loaderLimit)
    {
        if (javaMajor <= 0 || !TryGetGameKey(minecraftVersion, out var gameKey))
        {
            // 未知游戏版本或读不到版本号：没有权威约束，不做判断。
            return (null, null);
        }

        JavaConstraint? mandatory = null;
        JavaConstraint? suggested = null;

        // 1) 下界。GAME_JSON（版本 JSON 的 javaVersion）与 VANILLA（内置表）互斥，
        //    与 JavaVersionConstraint.java:35-38 的「JSON 声明优先」一致。
        if (TryGetMandatoryLowerBound(minecraftVersion, gameKey, jsonJavaMajor, out var lower) && javaMajor < lower)
        {
            var id = jsonJavaMajor is > 0 && gameKey >= GameJsonMinGameKey ? "GAME_JSON" : "VANILLA";
            mandatory = new JavaConstraint(true, (gameKey, gameKey), (lower, NoUpperBound), null, id);
        }

        // 2) 规则表里的 mandatory 上界（LAUNCH_WRAPPER / MODLAUNCHER_8）与 suggested 建议。
        foreach (var constraint in All)
        {
            // 适用但 Java 版本不在区间内 = 违反；不适用或刚好满足都跳过。
            if (!constraint.AppliesTo(gameKey, loader) || constraint.MatchesJava(javaMajor))
            {
                continue;
            }

            if (constraint.Id == "MODLAUNCHER_8" && loaderLimit?.Id == "FORGE_1_16_BUILD_J24")
            {
                continue;
            }

            if (constraint.Mandatory)
            {
                mandatory ??= constraint;
            }
            else
            {
                suggested ??= constraint;
            }
        }

        return (mandatory, suggested);
    }

    /// <summary>
    /// <see cref="Evaluate(string?, LoaderKind, int?, int)"/> 的加载器版本感知版。
    ///
    /// 加载器专属上限按级并入：阻断级的违规进 mandatory（这个 Java 不可用），
    /// 建议级的违规只进 suggested（降权，不阻断）——与原有 8 条规则同一套语义，
    /// UI 因此不需要为「加载器上限」再写一套判断。
    ///
    /// 补丁号级别（<c>8u320</c>）在这里判不了：本方法的入参只有主版本号。
    /// 需要那一层时用 <see cref="JavaLoadLimit.Exceeds"/> 配 <c>JavaInfo</c> 的补丁号判，
    /// 这样「只知道主版本」的调用点不会被一个无从判断的补丁号逻辑拖住。
    /// </summary>
    public static (JavaConstraint? Mandatory, JavaConstraint? Suggested) Evaluate(
        string? minecraftVersion,
        LoaderKind loader,
        int? jsonJavaMajor,
        int javaMajor,
        string? loaderVersion)
    {
        var limit = GetLoaderLimit(minecraftVersion, loader, loaderVersion);
        var result = EvaluateCore(minecraftVersion, loader, jsonJavaMajor, javaMajor, limit);

        if (limit is null || !TryGetGameKey(minecraftVersion, out var gameKey))
        {
            return result;
        }

        // 主版本是否越界：阻断级 → mandatory，建议级 → suggested。
        // MaxMajor 为 null 的规则（纯补丁号）在这一层没有结论，留给 Exceeds。
        if (limit.MaxMajor is not int maxMajor || javaMajor <= maxMajor)
        {
            return result;
        }

        var constraint = new JavaConstraint(
            Mandatory: limit.IsBlocking,
            Game: (gameKey, gameKey),
            Java: (0, maxMajor),
            RequiresLoader: loader,
            Id: limit.Id);

        return limit.IsBlocking
            ? (result.Mandatory ?? constraint, result.Suggested)
            : (result.Mandatory, result.Suggested ?? constraint);
    }

    /// <summary>
    /// 把 <c>1.20.5</c> / <c>26.1</c> 归一成可比较的整数键：<c>minor * 1000 + patch</c>。
    /// 1.x 的 minor 目前最大 21、年份版本号从 26 起，两套编号落在同一个整数空间不会撞车。
    /// </summary>
    public static bool TryGetGameKey(string? minecraftVersion, out int gameKey)
    {
        gameKey = 0;

        if (TryParseRelease(minecraftVersion, out var minor, out var patch))
        {
            gameKey = GameKey(minor, patch);
            return true;
        }

        // 26.1 这类年份版本号：^1\. 的正则匹配不到，改用 TryParseYearRelease 判定，
        // 次版本号直接取点后第一段（26.1 → 1）。
        if (!TryParseYearRelease(minecraftVersion, out var year))
        {
            return false;
        }

        var match = Regex.Match(minecraftVersion!.Trim(), @"^\d{2}\.(\d{1,2})");
        var minorPart = match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;

        gameKey = GameKey(year, minorPart);
        return true;
    }

    /// <summary>把 Java 主版本区间写成日志 / 提示文案：<c>Java 8</c>、<c>Java 8–17</c>、<c>Java 21+</c>。</summary>
    public static string DescribeJavaRange((int Min, int Max) javaRange)
    {
        if (javaRange.Max == NoUpperBound)
        {
            return $"Java {javaRange.Min}+";
        }

        if (javaRange.Min <= 0)
        {
            return $"Java ≤{javaRange.Max}";
        }

        return javaRange.Min == javaRange.Max
            ? $"Java {javaRange.Max}"
            : $"Java {javaRange.Min}–{javaRange.Max}";
    }

    /// <summary>
    /// 从本地 <c>versions/&lt;name&gt;/&lt;name&gt;.json</c> 读取 <c>javaVersion.majorVersion</c>。
    /// 加载器版本自身通常没有该字段，沿 <c>inheritsFrom</c> 找原版。读不到返回 null。
    /// </summary>
    public static JavaRequirement? ReadFromDisk(string versionsDirectory, string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionsDirectory) || string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        var current = versionName;

        for (var depth = 0; depth < MaxInheritanceDepth && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var file = Path.Combine(versionsDirectory, current!, current + ".json");

            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                var root = document.RootElement;

                if (root.TryGetProperty("javaVersion", out var javaVersion)
                    && javaVersion.ValueKind == JsonValueKind.Object
                    && javaVersion.TryGetProperty("majorVersion", out var majorNode)
                    && majorNode.TryGetInt32(out var major)
                    && major > 0)
                {
                    var component = javaVersion.TryGetProperty("component", out var componentNode)
                                    && componentNode.ValueKind == JsonValueKind.String
                        ? componentNode.GetString()
                        : null;

                    var reason = string.IsNullOrWhiteSpace(component)
                        ? $"{current} 的版本 JSON 声明需要 Java {major}"
                        : $"{current} 的版本 JSON 声明需要 Java {major}（{component}）";

                    return new JavaRequirement(major, reason);
                }

                current = root.TryGetProperty("inheritsFrom", out var inherits)
                          && inherits.ValueKind == JsonValueKind.String
                    ? inherits.GetString()
                    : null;
            }
            catch (Exception)
            {
                // 版本 JSON 损坏时退回查表，不让启动因为一个坏文件直接失败。
                return null;
            }
        }

        return null;
    }

    /// <summary>解析形如 <c>1.20.5</c> / <c>1.20</c> 的正式版本号；快照与年份版本返回 false。</summary>
    public static bool TryParseRelease(string? version, out int minor, out int patch)
    {
        minor = 0;
        patch = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var match = Regex.Match(version.Trim(), @"^1\.(\d{1,2})(?:\.(\d{1,2}))?(?![0-9])");
        if (!match.Success)
        {
            return false;
        }

        minor = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (match.Groups[2].Success)
        {
            patch = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return true;
    }

    /// <summary>解析形如 <c>26.3</c> 的年份版本号（Mojang 2026 年起的命名方式）。</summary>
    public static bool TryParseYearRelease(string? version, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var match = Regex.Match(version.Trim(), @"^(\d{2})\.\d{1,2}(?:\.\d{1,2})?(?![0-9])");
        if (!match.Success)
        {
            return false;
        }

        year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return year >= 26;
    }

    /// <summary>
    /// 下界来源：版本 JSON 的 <c>javaVersion.majorVersion</c>（GAME_JSON，JavaVersionConstraint.java:47-65）优先，
    /// 拿不到再用内置表（VANILLA，对应 GameJavaVersion.getMinimumJavaVersion:42-54）。
    /// </summary>
    private static bool TryGetMandatoryLowerBound(
        string? minecraftVersion,
        int gameKey,
        int? jsonJavaMajor,
        out int lower)
    {
        if (jsonJavaMajor is > 0 && gameKey >= GameJsonMinGameKey)
        {
            lower = jsonJavaMajor.Value;
            return true;
        }

        var baseline = Get(minecraftVersion);
        if (baseline is not null)
        {
            lower = baseline.RequiredMajor;
            return true;
        }

        lower = 0;
        return false;
    }

    /// <summary>游戏版本键：<c>minor * 1000 + patch</c>，patch 压到两位数以内（游戏版本补丁号不会超过 99）。</summary>
    private static int GameKey(int minor, int patch) =>
        (minor * GameKeyScale) + Math.Min(Math.Max(patch, 0), GameKeyScale - 1);
}

/// <summary>
/// 一条加载器版本级的上限规则。
///
/// 区间统一「下界含、上界不含」：<c>BuildMaxExclusive = [36,2,26]</c> 表示「最高 36.2.25」。
/// 这样「≤ 36.2.25」与「&lt; 0.17.0」两类写法能共用同一组字段，不必额外加开闭标志。
/// </summary>
/// <param name="Loader">适用的加载器种类。</param>
/// <param name="GameMin">游戏版本键下界（含）。</param>
/// <param name="GameMax">游戏版本键上界（含）。</param>
/// <param name="BuildMin">加载器版本号下界（含）；null 表示无下界。</param>
/// <param name="BuildMaxExclusive">加载器版本号上界（不含）；null 表示无上界。</param>
/// <param name="MaxMajor">允许的最高 Java 主版本；null 表示这条规则只限补丁号。</param>
/// <param name="MaxPatch">允许的最高 Java 补丁号，形如 <c>8u320</c>；null 表示不限补丁号。</param>
/// <param name="IsBlocking">true = 违反即不可用；false = 只是建议，见 <see cref="JavaRequirementTable.GetLoaderLimit"/> 的注释。</param>
/// <param name="Id">规则标识，与 <see cref="JavaConstraint.Id"/> 同一套命名，便于日志与测试定位。</param>
/// <param name="Reason">给用户看的中文说明。</param>
internal readonly record struct LoaderLimitRule(
    LoaderKind Loader,
    int GameMin,
    int GameMax,
    int[]? BuildMin,
    int[]? BuildMaxExclusive,
    int? MaxMajor,
    string? MaxPatch,
    bool IsBlocking,
    string Id,
    string Reason)
{
    /// <summary>加载器版本号是否落在本规则的区间内。</summary>
    public bool MatchesBuild(int[] parts) =>
        (BuildMin is null || JavaRequirementTable.CompareParts(parts, BuildMin) >= 0)
        && (BuildMaxExclusive is null || JavaRequirementTable.CompareParts(parts, BuildMaxExclusive) < 0);

    /// <summary>
    /// 这条规则是否必须拿到加载器版本号才能判。
    /// 两条区间都为 null 的规则（NeoForge 1.20.1）只看游戏版本，版本号缺失时也能给出结论。
    /// </summary>
    public bool RequiresBuild => BuildMin is not null || BuildMaxExclusive is not null;
}

/// <summary>
/// 某个「游戏版本 + 加载器 + 加载器版本」组合的额外 Java 上限，由
/// <see cref="JavaRequirementTable.GetLoaderLimit"/> 产出。
///
/// 上限分成两级，用法不同：
/// <list type="bullet">
/// <item><see cref="IsBlocking"/> 为 true：超出即不可用，会并入 <c>AllowedRange</c> 的上界，
/// 选择运行时 / 前置检查应当拒绝这个组合；</item>
/// <item>为 false：只是建议，绝不能拦启动。判据见 <see cref="Exceeds"/>，
/// 文案用 <see cref="Reason"/> 提示即可——用户手上的 <c>8u322</c> 大概率能跑，
/// 拦下来只会让他卡在一个本来能跑的游戏前面。</item>
/// </list>
/// </summary>
/// <param name="Id">规则标识。</param>
/// <param name="Reason">给用户看的中文说明。</param>
/// <param name="MaxMajor">允许的最高 Java 主版本；null 表示这条限制不涉及主版本。</param>
/// <param name="MaxPatch">允许的最高 Java 补丁号，形如 <c>8u320</c>；null 表示不限补丁号。</param>
/// <param name="IsBlocking">是否属于「超出即不可用」的阻断级上限。</param>
public sealed record JavaLoadLimit(
    string Id,
    string Reason,
    int? MaxMajor = null,
    string? MaxPatch = null,
    bool IsBlocking = false)
{
    /// <summary><see cref="MaxPatch"/> 里的主版本（<c>8u320</c> → 8）；没有补丁上限时为 null。</summary>
    public int? MaxPatchMajor => TryParsePatch(MaxPatch, out var major, out _) ? major : null;

    /// <summary><see cref="MaxPatch"/> 里的更新号（<c>8u320</c> → 320）；没有补丁上限时为 null。</summary>
    public int? MaxPatchUpdate => TryParsePatch(MaxPatch, out _, out var update) ? update : null;

    /// <summary>
    /// 这个 Java 是否超出上限。<paramref name="javaUpdate"/> 是补丁号（<c>1.8.0_322</c> → 322），
    /// 拿不到时传 null。
    ///
    /// 补丁号判定的分寸：只知道主版本、不知道补丁号时<b>不</b>算超出——
    /// 这条上限本来就是建议级，宁可漏一次提示，也不要拿「未知」当「确定违规」去骚扰用户。
    /// </summary>
    public bool Exceeds(int javaMajor, int? javaUpdate = null)
    {
        if (MaxMajor is int maxMajor && javaMajor > maxMajor)
        {
            return true;
        }

        if (!TryParsePatch(MaxPatch, out var patchMajor, out var patchUpdate))
        {
            return false;
        }

        if (javaMajor > patchMajor)
        {
            return true;
        }

        return javaMajor == patchMajor
               && javaUpdate is int update
               && update > patchUpdate;
    }

    /// <summary>解析 <c>8u320</c> 形式的补丁号上限。</summary>
    private static bool TryParsePatch(string? maxPatch, out int major, out int update)
    {
        major = 0;
        update = 0;

        if (string.IsNullOrWhiteSpace(maxPatch))
        {
            return false;
        }

        var match = Regex.Match(maxPatch.Trim(), @"^(\d+)u(\d+)$", RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return false;
        }

        major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        update = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }
}
