namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 极简 TOML 读取器：只解析 <c>mods.toml</c> / <c>neoforge.mods.toml</c> 用到的
/// 表、表数组与字符串/布尔/数字键值。不引入 TOML 库，也不支持内联表与数组。
/// </summary>
internal sealed class TomlDocument
{
    private readonly List<TomlSection> _sections = [];

    public IReadOnlyList<TomlSection> Sections => _sections;

    public TomlSection? FirstSection(string name) =>
        _sections.FirstOrDefault(section => string.Equals(section.Name, name, StringComparison.Ordinal));

    public IReadOnlyList<TomlSection> SectionsNamed(string name) =>
        _sections.Where(section => string.Equals(section.Name, name, StringComparison.Ordinal)).ToList();

    public static TomlDocument Parse(string content)
    {
        var document = new TomlDocument();
        TomlSection current = new(string.Empty);
        document._sections.Add(current);

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                current = new TomlSection(line[2..^2].Trim());
                document._sections.Add(current);
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new TomlSection(line[1..^1].Trim());
                document._sections.Add(current);
                continue;
            }

            var separator = FindAssignment(line);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim().Trim('"', '\'');
            var rawValue = line[(separator + 1)..].Trim();
            var value = ReadValue(rawValue, lines, ref i);
            current.Set(key, value);
        }

        return document;
    }

    private static int FindAssignment(string line)
    {
        var inString = false;
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == quote && (i == 0 || line[i - 1] != '\\'))
                {
                    inString = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c == '=')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>读取值；三引号多行字符串会继续消费后续行。</summary>
    private static string ReadValue(string rawValue, string[] lines, ref int index)
    {
        if (rawValue.StartsWith("\"\"\"", StringComparison.Ordinal)
            || rawValue.StartsWith("'''", StringComparison.Ordinal))
        {
            var delimiter = rawValue[..3];
            if (rawValue.Length > 6 && rawValue.EndsWith(delimiter, StringComparison.Ordinal))
            {
                return rawValue[3..^3].Trim();
            }

            var builder = new System.Text.StringBuilder(rawValue[3..]);
            while (++index < lines.Length)
            {
                var line = lines[index];
                if (line.TrimEnd().EndsWith(delimiter, StringComparison.Ordinal))
                {
                    builder.Append('\n').Append(line.TrimEnd()[..^3]);
                    break;
                }

                builder.Append('\n').Append(line);
            }

            return builder.ToString().Trim();
        }

        if (rawValue.Length >= 2
            && ((rawValue[0] == '"' && rawValue[^1] == '"') || (rawValue[0] == '\'' && rawValue[^1] == '\'')))
        {
            return rawValue[1..^1];
        }

        var commentIndex = rawValue.IndexOf('#');
        if (commentIndex > 0)
        {
            rawValue = rawValue[..commentIndex].Trim();
        }

        return rawValue;
    }
}

internal sealed class TomlSection(string name)
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; } = name;

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Set(string key, string value) => _values[key] = value;

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public bool GetBool(string key, bool fallback = false) =>
        _values.TryGetValue(key, out var value)
            ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            : fallback;
}
