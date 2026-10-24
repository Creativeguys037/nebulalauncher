using System.Globalization;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Versions;

/// <summary>pack_format 兼容判定结果。</summary>
public enum PackFormatVerdict
{
    /// <summary>判不了：游戏版本未知、包里没有 format、或该版本不在对照表覆盖范围内。</summary>
    Unknown = 0,

    /// <summary>包里声明的 format / 区间包含当前游戏要求的 format。</summary>
    Compatible = 1,

    /// <summary>包比当前游戏旧（游戏要求的 format 更大）。资源包通常仍能加载，数据包会被拒绝。</summary>
    TooOld = 2,

    /// <summary>包比当前游戏新（游戏要求的 format 更小），旧版本读不了新格式。</summary>
    TooNew = 3,
}

/// <summary>
/// 包里声明的 format 区间，对应 1.21.9 起的 <c>pack.min_format</c> / <c>pack.max_format</c>。
///
/// 语义：
/// - 两端都有 → <c>[MinVersion, MaxVersion]</c>；
/// - 只有 <c>MinVersion</c> → 上界开放（wiki 推荐的 1.21.9+ 写法里 max 缺省即「不设上限」）；
/// - 只有 <c>MaxVersion</c> → 下界开放，用于只声明「最高支持到哪」的老包。
///
/// 数值是官方格式号，可带小数（88.0 / 94.1），用 <see cref="double"/> 表示；
/// 旧的整数 <c>pack_format</c> 仍然可以塞进 Min = Max。
/// </summary>
public sealed record PackFormatRange(double? MinVersion, double? MaxVersion)
{
    /// <summary>至少声明了一个边界。</summary>
    public bool HasBounds => MinVersion is not null || MaxVersion is not null;

    /// <summary>判定某个游戏要求的 format 是否落在这个区间里（缺省的边界视为不限）。</summary>
    public bool Contains(double value) =>
        (MinVersion is null || value >= MinVersion) &&
        (MaxVersion is null || value <= MaxVersion);
}

/// <summary>一次兼容判定的完整结论。<see cref="Reason"/> 是可直接展示给用户的中文说明。</summary>
public sealed class PackFormatAssessment
{
    public PackFormatVerdict Verdict { get; init; }

    /// <summary>包里声明的旧式整数 pack_format；没有声明（或已是区间包）时为 null。</summary>
    public int? ActualFormat { get; init; }

    /// <summary>包里声明的 format 区间（min_format / max_format）；旧式单个数字的包为 null。</summary>
    public PackFormatRange? ActualRange { get; init; }

    /// <summary>当前游戏版本要求的 format（可带小数，例如 88.0 / 94.1）；判不出来时为 null。</summary>
    public double? ExpectedPackFormat { get; init; }

    /// <summary>当前游戏版本要求的整数 format；要求值不是整数时为 null。</summary>
    public int? ExpectedFormat
    {
        get
        {
            if (ExpectedPackFormat is not { } value || value != Math.Floor(value))
            {
                return null;
            }

            return value <= int.MaxValue ? (int)value : null;
        }
    }

    /// <summary>从实例 / 目标里识别出的 Minecraft 版本，例如 <c>1.21.9</c>；识别不出为 null。</summary>
    public string? MinecraftVersion { get; init; }

    /// <summary>
    /// 包声明的 format 对应的游戏版本范围，用于「需 1.20.x」「需 1.21.9+」「需 ≤1.21.8」这类文案。
    /// 判不出来时为空字符串。
    /// </summary>
    public string RequiredRange { get; init; } = string.Empty;

    /// <summary>中文理由，任何分支都非空。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>true 表示「不兼容方向」的结论（TooOld / TooNew），UI 只在此时显示提示徽标。</summary>
    public bool IsMismatch => Verdict is PackFormatVerdict.TooOld or PackFormatVerdict.TooNew;
}

/// <summary>
/// 资源包 / 数据包的 <c>pack_format</c> 对照表与兼容判定。
///
/// 数据来源：Minecraft Wiki 的 Pack format 页面
/// （<c>https://minecraft.wiki/w/Pack_format</c> 的 Data pack format history / Resource pack format history 两张表，
/// 通过 MediaWiki API 取渲染后的表格核对）。**最后核对：2026-10**。
///
/// 覆盖范围（Java 版）：
/// - 资源包 1.6.1 – 26.3；数据包 1.13 – 26.3（1.13 才有数据包）。
/// - 1.6.1–1.21.8 使用整数 <c>pack_format</c>；1.21.9 起官方改用 <c>min_format</c> / <c>max_format</c>，
///   格式号带小数（资源包 69.0 / 75.0 / …；数据包 88.0 / 94.1 / …），本表按 wiki 的原始数值记录，
///   没有强行折算成整数。
/// - 2026 起的版本号是 <c>26.1</c> 这种日期版本：只有「整串就是版本号」时才识别，
///   混在实例名里的 <c>47.4.28</c> 之类（Forge 版本）不猜。
/// - 表里查不到的版本（例如晚于 26.3 的版本）返回 <see cref="PackFormatVerdict.Unknown"/>，不猜。
///
/// 判定只用于提示：不阻止启用，也不影响启动。资源包用旧 format 时游戏通常仍会加载并标注「为旧版本制作」；
/// 数据包 format 不匹配则会被游戏拒绝，两种情况都值得在界面上说一句。
/// </summary>
public static class PackFormatTable
{
    /// <summary>
    /// 裸版本号：整串就是一个版本（<c>1.20.1</c> / <c>26.1</c>）。2026 起的 26.x 只在这个形态下接受——
    /// 混在目录名里的 <c>47.4.28</c>（Forge）之类无法与大版本号区分，不猜。
    /// </summary>
    private static readonly Regex BareMcVersionPattern = new(
        @"^(\d{1,3})\.(\d{1,2})(?:\.(\d{1,2}))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 嵌在实例名 / 目录名里的 Minecraft 版本：<c>fabric-loader-0.16.9-1.21.4</c> → <c>1.21.4</c>。
    /// 只认 <c>1.x</c>，这样 <c>0.16.9</c>（fabric-loader）、<c>47.4.28</c>（forge）、
    /// <c>21.4.50</c>（neoforge）都不会被误认成 Minecraft 版本。
    /// </summary>
    private static readonly Regex EmbeddedMcVersionPattern = new(
        @"(?<![\d.])1\.(\d{1,2})(?:\.(\d{1,2}))?(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Java 版一个版本在表里的位置。26.x 起大版本不再是 1，必须整体比较。</summary>
    private readonly record struct McVersion(int Major, int Minor, int Patch) : IComparable<McVersion>
    {
        public int CompareTo(McVersion other)
        {
            if (Major != other.Major)
            {
                return Major.CompareTo(other.Major);
            }

            return Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
        }

        public override string ToString() =>
            Patch == 0 ? $"{Major}.{Minor}" : $"{Major}.{Minor}.{Patch}";
    }

    private readonly record struct FormatRange(McVersion From, McVersion To, double Format);

    /// <summary>资源包（source: minecraft.wiki Resource pack format history，1.6.1 – 26.3）。</summary>
    private static readonly FormatRange[] ResourcePackRanges =
    [
        new(new(1, 6, 1), new(1, 8, 9), 1),
        new(new(1, 9, 0), new(1, 10, 2), 2),
        new(new(1, 11, 0), new(1, 12, 2), 3),
        new(new(1, 13, 0), new(1, 14, 4), 4),
        new(new(1, 15, 0), new(1, 16, 1), 5),
        new(new(1, 16, 2), new(1, 16, 5), 6),
        new(new(1, 17, 0), new(1, 17, 1), 7),
        new(new(1, 18, 0), new(1, 18, 2), 8),
        new(new(1, 19, 0), new(1, 19, 2), 9),
        new(new(1, 19, 3), new(1, 19, 3), 12),
        new(new(1, 19, 4), new(1, 19, 4), 13),
        new(new(1, 20, 0), new(1, 20, 1), 15),
        new(new(1, 20, 2), new(1, 20, 2), 18),
        new(new(1, 20, 3), new(1, 20, 4), 22),
        new(new(1, 20, 5), new(1, 20, 6), 32),
        new(new(1, 21, 0), new(1, 21, 1), 34),
        new(new(1, 21, 2), new(1, 21, 3), 42),
        new(new(1, 21, 4), new(1, 21, 4), 46),
        new(new(1, 21, 5), new(1, 21, 5), 55),
        new(new(1, 21, 6), new(1, 21, 6), 63),
        new(new(1, 21, 7), new(1, 21, 8), 64),

        // 1.21.9 起改用 min_format / max_format，格式号带小数；数值按 wiki 原样记录。
        new(new(1, 21, 9), new(1, 21, 10), 69.0),
        new(new(1, 21, 11), new(1, 21, 11), 75.0),
        new(new(26, 1, 0), new(26, 1, 2), 84.0),
        new(new(26, 2, 0), new(26, 2, 0), 88.0),
        new(new(26, 3, 0), new(26, 3, 0), 97.1),
    ];

    /// <summary>数据包（source: minecraft.wiki Data pack format history，1.13 – 26.3）。</summary>
    private static readonly FormatRange[] DataPackRanges =
    [
        new(new(1, 13, 0), new(1, 14, 4), 4),
        new(new(1, 15, 0), new(1, 16, 1), 5),
        new(new(1, 16, 2), new(1, 16, 5), 6),
        new(new(1, 17, 0), new(1, 17, 1), 7),
        new(new(1, 18, 0), new(1, 18, 1), 8),
        new(new(1, 18, 2), new(1, 18, 2), 9),
        new(new(1, 19, 0), new(1, 19, 3), 10),
        new(new(1, 19, 4), new(1, 19, 4), 12),
        new(new(1, 20, 0), new(1, 20, 1), 15),
        new(new(1, 20, 2), new(1, 20, 2), 18),
        new(new(1, 20, 3), new(1, 20, 4), 26),
        new(new(1, 20, 5), new(1, 20, 6), 41),
        new(new(1, 21, 0), new(1, 21, 1), 48),
        new(new(1, 21, 2), new(1, 21, 3), 57),
        new(new(1, 21, 4), new(1, 21, 4), 61),
        new(new(1, 21, 5), new(1, 21, 5), 71),
        new(new(1, 21, 6), new(1, 21, 6), 80),
        new(new(1, 21, 7), new(1, 21, 8), 81),

        // 1.21.9 起改用 min_format / max_format，格式号带小数；数值按 wiki 原样记录。
        new(new(1, 21, 9), new(1, 21, 10), 88.0),
        new(new(1, 21, 11), new(1, 21, 11), 94.1),
        new(new(26, 1, 0), new(26, 1, 2), 101.1),
        new(new(26, 2, 0), new(26, 2, 0), 107.1),
        new(new(26, 3, 0), new(26, 3, 0), 121.0),
    ];

    /// <summary>旧式整数 <c>pack_format</c> 覆盖到的最后一个版本（含）。1.21.9+ 改用区间语义。</summary>
    private static readonly McVersion LastLegacyVersion = new(1, 21, 8);

    /// <summary>数据包从 1.13 才开始有 pack_format。</summary>
    private static readonly McVersion FirstDataPackVersion = new(1, 13, 0);

    /// <summary>资源包从 1.6.1 才进入当前这套 format 编号。</summary>
    private static readonly McVersion FirstResourcePackVersion = new(1, 6, 1);

    /// <summary>
    /// 旧式整数入口：把 1.6.1–1.21.8 的游戏版本映射到整数 <c>pack_format</c>。
    /// 1.21.9+ 的格式号可能带小数，一律用 <see cref="TryGetExpected"/>；这里对 1.21.9+ 返回 false，
    /// 避免调用方拿到被截断的整数。
    /// </summary>
    public static bool TryGet(ContentKind kind, string? gameVersion, out int format)
    {
        format = 0;

        if (!TryGetExpected(kind, gameVersion, out var expected, out _))
        {
            return false;
        }

        if (expected != Math.Floor(expected) || expected > int.MaxValue)
        {
            return false;
        }

        if (TryParseMcVersion(gameVersion, out var version) && version.CompareTo(LastLegacyVersion) > 0)
        {
            return false;
        }

        format = (int)expected;
        return true;
    }

    /// <summary>
    /// 把游戏版本映射到它要求的 pack format（可带小数，1.21.9+ 起为 88.0 / 94.1 这类值）。
    /// 版本号可从版本目录名 / 实例名里抽取；kind 不是资源包或数据包、或版本不在表范围内时返回 false。
    /// </summary>
    public static bool TryGetExpected(ContentKind kind, string? gameVersion, out double format)
        => TryGetExpected(kind, gameVersion, out format, out _);

    /// <summary>
    /// 判定包声明的单个整数 format 是否适应当前游戏版本（旧式 <c>pack_format</c>）。
    /// </summary>
    public static PackFormatAssessment Evaluate(ContentKind kind, int actualFormat, string? gameVersion)
        => Evaluate(kind, range: null, actualFormat, gameVersion);

    /// <summary>
    /// 判定包声明的 format 是否适应当前游戏版本。
    ///
    /// <paramref name="range"/> 非空且至少有一个边界时按区间语义（1.21.9+ 的 min_format / max_format）判定；
    /// 否则按 <paramref name="legacyFormat"/> 的单个整数判定（与 1.21.8 及更早的行为一致）。
    /// 任何失败分支都返回 <see cref="PackFormatVerdict.Unknown"/> 并带一句可读理由，不抛异常。
    /// </summary>
    public static PackFormatAssessment Evaluate(
        ContentKind kind,
        PackFormatRange? range,
        int? legacyFormat,
        string? gameVersion)
    {
        if (kind is not (ContentKind.ResourcePack or ContentKind.DataPack))
        {
            return new PackFormatAssessment
            {
                Verdict = PackFormatVerdict.Unknown,
                ActualFormat = legacyFormat,
                ActualRange = range,
                Reason = "只有资源包与数据包有 pack_format。",
            };
        }

        var kindLabel = kind == ContentKind.DataPack ? "数据包" : "资源包";
        var hasRange = range is { HasBounds: true };

        if (!hasRange && legacyFormat is not > 0)
        {
            return new PackFormatAssessment
            {
                Verdict = PackFormatVerdict.Unknown,
                ActualFormat = legacyFormat,
                Reason = $"{kindLabel}的 pack.mcmeta 里没有可用的 pack_format / min_format / max_format。",
            };
        }

        var extracted = ExtractMinecraftVersion(gameVersion);

        if (extracted is null)
        {
            return new PackFormatAssessment
            {
                Verdict = PackFormatVerdict.Unknown,
                ActualFormat = legacyFormat,
                ActualRange = range,
                Reason = string.IsNullOrWhiteSpace(gameVersion)
                    ? "当前游戏版本未知，未做 pack_format 判断。"
                    : $"无法从「{gameVersion}」识别 Minecraft 版本，未做 pack_format 判断。",
            };
        }

        if (!TryGetExpected(kind, gameVersion, out var expected, out var lookupReason))
        {
            return new PackFormatAssessment
            {
                Verdict = PackFormatVerdict.Unknown,
                ActualFormat = legacyFormat,
                ActualRange = range,
                MinecraftVersion = extracted,
                RequiredRange = hasRange
                    ? DescribePackRange(kind, range!)
                    : DescribeFormatRange(kind, legacyFormat!.Value) ?? string.Empty,
                Reason = lookupReason ?? "当前游戏版本不在 pack_format 对照表覆盖范围内。",
            };
        }

        if (hasRange)
        {
            return EvaluateRange(kind, kindLabel, range!, expected, extracted);
        }

        var actual = legacyFormat!.Value;
        var requiredRange = DescribeFormatRange(kind, actual) ?? $"format {actual}";

        if (actual == expected)
        {
            return new PackFormatAssessment
            {
                Verdict = PackFormatVerdict.Compatible,
                ActualFormat = actual,
                ExpectedPackFormat = expected,
                MinecraftVersion = extracted,
                RequiredRange = requiredRange,
                Reason = $"format {actual} 与 Minecraft {extracted} 一致。",
            };
        }

        var direction = actual < expected ? PackFormatVerdict.TooOld : PackFormatVerdict.TooNew;
        var hint = direction == PackFormatVerdict.TooOld
            ? "包的格式比当前游戏旧"
            : "包的格式比当前游戏新";

        return new PackFormatAssessment
        {
            Verdict = direction,
            ActualFormat = actual,
            ExpectedPackFormat = expected,
            MinecraftVersion = extracted,
            RequiredRange = requiredRange,
            Reason = $"{kindLabel} format {actual}（适用于 {requiredRange}）与 Minecraft {extracted} 不匹配"
                     + $"（当前需要 {FormatNumber(expected)}）：{hint}。",
        };
    }

    /// <summary>
    /// 把包声明拼成卡片上的一行展示文案：<c>format 61</c> / <c>format 88–94.1</c> / <c>format 88+</c> / <c>format ≤81</c>。
    /// 两个来源（数据包目录 / 资源包 zip）共用，避免两处各写一份格式。
    /// </summary>
    public static string DescribeDeclaration(int? legacyFormat, PackFormatRange? range)
    {
        if (range is { HasBounds: true })
        {
            var min = range.MinVersion is { } minValue ? FormatNumber(minValue) : null;
            var max = range.MaxVersion is { } maxValue ? FormatNumber(maxValue) : null;

            return (min, max) switch
            {
                (not null, not null) => $"format {min}–{max}",
                (not null, null) => $"format {min}+",
                (null, not null) => $"format ≤{max}",
                _ => string.Empty,
            };
        }

        return legacyFormat is > 0 ? $"format {legacyFormat}" : string.Empty;
    }

    /// <summary>
    /// 从 <see cref="ContentItem.Version"/> 里取旧式整数 pack_format。
    /// 数据包存的是 <c>format 48</c>，资源包存的是 <c>15</c>（见 ContentService / ModMetadataReader）。
    /// 区间文案（<c>format 88–94.1</c> 等）不是单个数字，这里返回 null——区间请读 ContentItem 的 Min/MaxPackFormat。
    /// </summary>
    public static int? ReadPackFormat(ContentKind kind, string? versionText)
    {
        if (kind is not (ContentKind.ResourcePack or ContentKind.DataPack) || string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        // 只认「单个整数」或「format 单个整数」；区间文案带 – + ≤ 与小数，不能按尾部数字解析。
        var match = Regex.Match(
            versionText.Trim(),
            @"^(?:format\s+)?(\d+)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        return match.Success && int.TryParse(match.Groups[1].Value, out var value) && value > 0
            ? value
            : null;
    }

    /// <summary>
    /// 从版本目录名 / 实例名里抽 Minecraft 版本，例如 <c>fabric-loader-0.16.9-1.21.4</c> → <c>1.21.4</c>。
    /// 抽不出返回 null——不要猜。
    /// </summary>
    public static string? ExtractMinecraftVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();

        var bare = BareMcVersionPattern.Match(trimmed);
        if (bare.Success && IsMinecraftMajor(bare.Groups[1].Value))
        {
            return bare.Value;
        }

        var embedded = EmbeddedMcVersionPattern.Match(trimmed);
        return embedded.Success ? embedded.Value : null;
    }

    /// <summary>
    /// 旧式整数 format 对应的游戏版本范围，用于「需 1.20.x」这类文案。
    /// 同一 minor 内的区间压缩成 <c>1.20.x</c>，单版本写成 <c>1.21.4</c>，跨 minor 写成 <c>1.20.5–1.20.6</c>。
    /// 表里查不到这个 format 时返回 null。
    /// </summary>
    public static string? DescribeFormatRange(ContentKind kind, int format)
    {
        var ranges = RangesOf(kind);

        if (ranges is null)
        {
            return null;
        }

        foreach (var range in ranges)
        {
            if (range.Format != format)
            {
                continue;
            }

            return DescribeVersionRange(range);
        }

        return null;
    }

    /// <summary>
    /// 区间包（min_format / max_format）对应的版本文案：
    /// <c>1.21.9–1.21.11</c> / <c>1.21.9+</c> / <c>≤1.21.8</c>。
    /// 边界值在表里查不到对应版本时，如实写成 <c>format 88.5</c>，不猜版本号。
    /// </summary>
    public static string DescribePackRange(ContentKind kind, PackFormatRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        var min = range.MinVersion is { } minFormat
            ? EarliestVersionOfFormat(kind, minFormat) ?? $"format {FormatNumber(minFormat)}"
            : null;
        var max = range.MaxVersion is { } maxFormat
            ? LatestVersionOfFormat(kind, maxFormat) ?? $"format {FormatNumber(maxFormat)}"
            : null;

        return (min, max) switch
        {
            (not null, not null) => $"{min}–{max}",
            (not null, null) => $"{min}+",
            (null, not null) => $"≤{max}",
            _ => string.Empty,
        };
    }

    // ---------------- 内部实现 ----------------

    private static PackFormatAssessment EvaluateRange(
        ContentKind kind,
        string kindLabel,
        PackFormatRange range,
        double expected,
        string extracted)
    {
        var requiredRange = DescribePackRange(kind, range);

        if (range.Contains(expected))
        {
            return new PackFormatAssessment
            {
                Verdict = PackFormatVerdict.Compatible,
                ActualRange = range,
                ExpectedPackFormat = expected,
                MinecraftVersion = extracted,
                RequiredRange = requiredRange,
                Reason = $"包声明的 format 区间 {requiredRange} 覆盖 Minecraft {extracted}"
                         + $"（需要 {FormatNumber(expected)}）。",
            };
        }

        // 游戏要求低于包的 min → 包要求更新；高于 max → 包只适合更老的游戏。
        var tooOld = range.MinVersion is { } min && expected < min;
        var verdict = tooOld ? PackFormatVerdict.TooOld : PackFormatVerdict.TooNew;
        var hint = tooOld ? "包要求的游戏版本比当前新" : "包只支持比当前更老的游戏版本";

        return new PackFormatAssessment
        {
            Verdict = verdict,
            ActualRange = range,
            ExpectedPackFormat = expected,
            MinecraftVersion = extracted,
            RequiredRange = requiredRange,
            Reason = $"{kindLabel}声明的 format 区间是 {requiredRange}，Minecraft {extracted} 需要"
                     + $" {FormatNumber(expected)}：{hint}。",
        };
    }

    private static bool TryGetExpected(
        ContentKind kind,
        string? gameVersion,
        out double format,
        out string? reason)
    {
        format = 0;
        reason = null;

        var ranges = RangesOf(kind);

        if (ranges is null)
        {
            reason = "只有资源包与数据包有 pack_format。";
            return false;
        }

        if (!TryParseMcVersion(gameVersion, out var version))
        {
            reason = string.IsNullOrWhiteSpace(gameVersion)
                ? "当前游戏版本未知，未做 pack_format 判断。"
                : $"无法从「{gameVersion}」识别 Minecraft 版本，未做 pack_format 判断。";
            return false;
        }

        var first = kind == ContentKind.DataPack ? FirstDataPackVersion : FirstResourcePackVersion;

        if (version.CompareTo(first) < 0)
        {
            reason = kind == ContentKind.DataPack
                ? $"Minecraft {version} 还没有数据包 pack_format（1.13 才引入）。"
                : $"Minecraft {version} 还没有当前编号体系的资源包 pack_format（1.6.1 起才有）。";
            return false;
        }

        foreach (var range in ranges)
        {
            if (version.CompareTo(range.From) >= 0 && version.CompareTo(range.To) <= 0)
            {
                format = range.Format;
                return true;
            }
        }

        // 表覆盖连续时走不到这里：晚于最后一条的版本、或表有洞，都如实报告而不是拿相邻版本顶替。
        reason = $"对照表里没有 Minecraft {version} 的{KindLabelOf(kind)} pack format。";
        return false;
    }

    private static FormatRange[]? RangesOf(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => ResourcePackRanges,
        ContentKind.DataPack => DataPackRanges,
        _ => null,
    };

    private static string KindLabelOf(ContentKind kind) => kind == ContentKind.DataPack ? "数据包" : "资源包";

    private static bool TryParseMcVersion(string? text, out McVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // 先看整串是否就是版本号（26.x 只在这个形态下接受）。
        var bare = BareMcVersionPattern.Match(trimmed);
        if (bare.Success && IsMinecraftMajor(bare.Groups[1].Value))
        {
            return TryBuildVersion(bare, out version);
        }

        // 再从「实例名 / 目录名」里找 1.x。
        var embedded = EmbeddedMcVersionPattern.Match(trimmed);
        return embedded.Success && TryBuildVersion(embedded, out version);
    }

    private static bool TryBuildVersion(Match match, out McVersion version)
    {
        version = default;

        if (!int.TryParse(match.Groups[1].ValueSpan, out var major)
            || !int.TryParse(match.Groups[2].ValueSpan, out var minor))
        {
            return false;
        }

        var patch = 0;

        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].ValueSpan, out patch))
        {
            return false;
        }

        version = new McVersion(major, minor, patch);
        return true;
    }

    /// <summary>只有 <c>1.x</c>（历史）与 <c>26+</c>（2026 起的日期版本）能当 Minecraft 版本。</summary>
    private static bool IsMinecraftMajor(string majorText) =>
        int.TryParse(majorText, out var major) && (major == 1 || major >= 26);

    private static string? EarliestVersionOfFormat(ContentKind kind, double format)
    {
        var ranges = RangesOf(kind);

        if (ranges is null)
        {
            return null;
        }

        foreach (var range in ranges)
        {
            if (range.Format == format)
            {
                return range.From.ToString();
            }
        }

        return null;
    }

    private static string? LatestVersionOfFormat(ContentKind kind, double format)
    {
        var ranges = RangesOf(kind);

        if (ranges is null)
        {
            return null;
        }

        foreach (var range in ranges)
        {
            if (range.Format == format)
            {
                return range.To.ToString();
            }
        }

        return null;
    }

    private static string DescribeVersionRange(FormatRange range)
    {
        if (range.From == range.To)
        {
            return range.From.ToString();
        }

        // 从 1.20.0 开始的同 minor 区间等价于「整个 1.20.x」。
        if (range.From.Major == range.To.Major
            && range.From.Minor == range.To.Minor
            && range.From.Patch == 0)
        {
            return $"{range.From.Major}.{range.From.Minor}.x";
        }

        return $"{range.From}–{range.To}";
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
