using System.Globalization;
using System.Text.RegularExpressions;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 模组声明的 Minecraft 版本区间。支持三种现实里最常见的写法：
/// <list type="bullet">
/// <item><c>[1.20,1.21)</c> 这类 Maven 区间（Forge / NeoForge 的 versionRange）</item>
/// <item><c>&gt;=1.20 &lt;1.21</c> 这类比较式（部分 Fabric mod 的 depends.minecraft）</item>
/// <item><c>1.20.x</c> / <c>1.20</c> 这类通配（资源包、部分老模组）</item>
/// </list>
/// 解析失败时视为「不限制」，避免把合法模组误报为不兼容。
/// </summary>
public sealed class McVersionRange
{
    private readonly List<Constraint> _constraints;

    private McVersionRange(List<Constraint> constraints, bool unbounded)
    {
        _constraints = constraints;
        IsUnbounded = unbounded;
    }

    /// <summary>没有任何限制（<c>*</c> 或解析失败）。</summary>
    public bool IsUnbounded { get; }

    public static McVersionRange Everything { get; } = new([], unbounded: true);

    public static McVersionRange Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Everything;
        }

        var value = text.Trim();
        if (value is "*" or "x" or "any" or "~")
        {
            return Everything;
        }

        try
        {
            if (value.StartsWith('[') || value.StartsWith('('))
            {
                return ParseMavenStyle(value);
            }

            if (value.Contains('>') || value.Contains('<') || value.Contains('='))
            {
                return ParseComparisons(value);
            }

            return ParseWildcard(value);
        }
        catch (Exception)
        {
            return Everything;
        }
    }

    public bool Contains(string? minecraftVersion)
    {
        if (IsUnbounded)
        {
            return true;
        }

        if (!McVersion.TryParse(minecraftVersion, out var version))
        {
            return true;
        }

        foreach (var constraint in _constraints)
        {
            if (!constraint.Matches(version))
            {
                return false;
            }
        }

        return true;
    }

    private static McVersionRange ParseMavenStyle(string value)
    {
        var lowerInclusive = value[0] == '[';
        var upperInclusive = value[^1] == ']';
        var inner = value[1..^1].Trim();

        if (inner.Length == 0)
        {
            return Everything;
        }

        // [1.20.1] 表示单点等于。
        if (!inner.Contains(','))
        {
            return McVersion.TryParse(inner, out var exact)
                ? new McVersionRange([Constraint.Equal(exact)], false)
                : Everything;
        }

        var parts = inner.Split(',', 2);
        var constraints = new List<Constraint>(2);

        if (McVersion.TryParse(parts[0], out var lower))
        {
            constraints.Add(lowerInclusive ? Constraint.AtLeast(lower) : Constraint.Above(lower));
        }

        if (McVersion.TryParse(parts[1], out var upper))
        {
            constraints.Add(upperInclusive ? Constraint.AtMost(upper) : Constraint.Below(upper));
        }

        return constraints.Count == 0 ? Everything : new McVersionRange(constraints, false);
    }

    private static McVersionRange ParseComparisons(string value)
    {
        var normalized = value.Replace(",", " ", StringComparison.Ordinal);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var constraints = new List<Constraint>(tokens.Length);

        foreach (var token in tokens)
        {
            var match = Regex.Match(token, "^(>=|<=|>|<|=)?\\s*(.+)$");
            if (!match.Success)
            {
                continue;
            }

            if (!McVersion.TryParse(match.Groups[2].Value, out var version))
            {
                continue;
            }

            constraints.Add(match.Groups[1].Value switch
            {
                ">=" => Constraint.AtLeast(version),
                ">" => Constraint.Above(version),
                "<=" => Constraint.AtMost(version),
                "<" => Constraint.Below(version),
                _ => Constraint.Equal(version),
            });
        }

        return constraints.Count == 0 ? Everything : new McVersionRange(constraints, false);
    }

    private static McVersionRange ParseWildcard(string value)
    {
        var trimmed = value.Trim().TrimEnd('.');
        if (trimmed.EndsWith(".x", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".*", StringComparison.Ordinal)
            || trimmed.EndsWith(".X", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^2];
        }

        if (!McVersion.TryParse(trimmed, out var baseVersion))
        {
            return Everything;
        }

        // 通配按「前缀匹配」处理：1.20 / 1.20.x 命中 1.20.1、1.20.4 等。
        return new McVersionRange([Constraint.Prefix(baseVersion)], false);
    }

    private sealed record Constraint(ComparisonKind Kind, McVersion Value)
    {
        public static Constraint AtLeast(McVersion value) => new(ComparisonKind.AtLeast, value);

        public static Constraint Above(McVersion value) => new(ComparisonKind.Above, value);

        public static Constraint AtMost(McVersion value) => new(ComparisonKind.AtMost, value);

        public static Constraint Below(McVersion value) => new(ComparisonKind.Below, value);

        public static Constraint Equal(McVersion value) => new(ComparisonKind.Equal, value);

        public static Constraint Prefix(McVersion value) => new(ComparisonKind.Prefix, value);

        public bool Matches(McVersion candidate) => Kind switch
        {
            ComparisonKind.AtLeast => candidate.CompareTo(Value) >= 0,
            ComparisonKind.Above => candidate.CompareTo(Value) > 0,
            ComparisonKind.AtMost => candidate.CompareTo(Value) <= 0,
            ComparisonKind.Below => candidate.CompareTo(Value) < 0,
            ComparisonKind.Prefix => candidate.StartsWith(Value),
            _ => candidate.CompareTo(Value) == 0,
        };
    }

    private enum ComparisonKind
    {
        AtLeast,
        Above,
        AtMost,
        Below,
        Equal,
        Prefix,
    }
}

/// <summary>Minecraft 版本号的三段数值表示，解析时忽略 <c>-pre1</c> 之类的后缀。</summary>
public readonly record struct McVersion(int Major, int Minor, int Patch) : IComparable<McVersion>
{
    public static bool TryParse(string? text, out McVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Regex.Match(text.Trim(), "^[vV]?(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?");
        if (!match.Success)
        {
            return false;
        }

        version = new McVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        return true;
    }

    public bool StartsWith(McVersion prefix) =>
        Major == prefix.Major && Minor == prefix.Minor
        && (prefix.Patch == 0 || Patch == prefix.Patch);

    public static bool operator <(McVersion left, McVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(McVersion left, McVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(McVersion left, McVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(McVersion left, McVersion right) => left.CompareTo(right) >= 0;

    public int CompareTo(McVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }
}
