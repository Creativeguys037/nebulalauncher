using System.Text.RegularExpressions;

namespace NebulaLauncher.DocsCheck;

/// <summary>markdown 正文的一行（围栏代码块之外），带 1 起始行号。</summary>
/// <param name="Number">行号，从 1 开始。</param>
/// <param name="Text">该行原文，不含行尾换行。</param>
public readonly record struct MarkdownLine(int Number, string Text);

/// <summary>
/// markdown 逐行解析的共用规则：围栏代码块里的内容不参与任何校验；反引号单元与行内代码都不跨行。
/// 引用校验（反引号单元）与链接校验（<c>[文本](相对路径.md)</c>）都从这里取行，规则只写一份。
/// </summary>
public static class MarkdownScanner
{
    /// <summary>仓库相对路径的根目录前缀（<c>/</c> 分隔，区分大小写）。</summary>
    private static readonly string[] PathPrefixes = ["src/", "tests/", "tools/", "docs/"];

    /// <summary>紧跟路径的行号提示：<c>:105</c> 或 <c>:10-24</c>。</summary>
    private static readonly Regex LineHintSuffix = new(
        @"^(?<path>.+?):(?<hint>[0-9]+(?:-[0-9]+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 写在符号后面的括号行号，是规格定下的形态（<c>Foo（:105）</c>）：行号只作提示、不作定位依据。
    /// 同时认 <c>(:105)</c>、<c>（:105-107）</c> 与 <c>（:17/18/21-22）</c>。
    /// </summary>
    private static readonly Regex TrailingLineHint = new(
        @"^(?<rest>.*?)[（(]\s*:?\s*(?<hint>[0-9]+(?:[-/][0-9]+)*)\s*[）)]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>反引号单元（不跨行）。</summary>
    private static readonly Regex BacktickUnit = new(
        "`([^`\\n]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>围栏代码块的开闭行（``` 或 ~~~）。</summary>
    private static readonly Regex FenceLine = new(
        @"^\s*(```|~~~)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>读出一份文档里参与校验的行：围栏代码块整段跳过。</summary>
    /// <param name="fullPath">文档绝对路径。</param>
    public static IReadOnlyList<MarkdownLine> ProseLines(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        var lines = File.ReadAllLines(fullPath);
        var prose = new List<MarkdownLine>(lines.Length);
        var inFence = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (FenceLine.IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                prose.Add(new MarkdownLine(index + 1, line));
            }
        }

        return prose;
    }

    /// <summary>一行里的反引号单元内容（已 trim，不含反引号）。</summary>
    public static IReadOnlyList<string> BacktickUnits(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var units = new List<string>();
        foreach (Match match in BacktickUnit.Matches(line))
        {
            units.Add(match.Groups[1].Value.Trim());
        }

        return units;
    }

    /// <summary>把行内代码替换成空格：链接校验不能把反引号里的示例当成真链接。</summary>
    public static string WithoutCodeSpans(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return BacktickUnit.Replace(line, " ");
    }

    /// <summary>
    /// 把单元拆成路径、行号提示与符号列表；第一个 token 不是仓库相对路径时返回 false（跳过）。
    /// 行号两种写法都认：紧跟路径的 <c>path:105</c>，或写在符号后的括号里 <c>Symbol（:105）</c>。
    /// </summary>
    /// <param name="unit">反引号单元内容，不含反引号。</param>
    /// <param name="path">去掉行号提示后的仓库相对路径。</param>
    /// <param name="lineHint">行号提示原文，没有则为 null。</param>
    /// <param name="symbols">符号列表；允许为空。</param>
    public static bool TryParseUnit(
        string unit,
        out string path,
        out string? lineHint,
        out IReadOnlyList<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(unit);

        path = string.Empty;
        lineHint = null;
        symbols = [];

        var tokens = unit.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        // 括号行号先剥掉，剩下的 token 才当路径与符号；行号只作提示，两种写法等价。
        var stripped = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            var trailing = TrailingLineHint.Match(token);
            if (!trailing.Success)
            {
                stripped.Add(token);
                continue;
            }

            lineHint ??= trailing.Groups["hint"].Value;
            var rest = trailing.Groups["rest"].Value;
            if (rest.Length > 0)
            {
                stripped.Add(rest);
            }
        }

        if (stripped.Count == 0)
        {
            return false;
        }

        var first = stripped[0];
        var hintMatch = LineHintSuffix.Match(first);
        if (hintMatch.Success)
        {
            path = hintMatch.Groups["path"].Value;
            lineHint ??= hintMatch.Groups["hint"].Value;
        }
        else
        {
            path = first;
        }

        if (!LooksLikeRepoRelativePath(path))
        {
            path = string.Empty;
            lineHint = null;
            return false;
        }

        symbols = stripped.Skip(1).ToList();
        return true;
    }

    /// <summary>形如 <c>src/</c>、<c>tests/</c>、<c>tools/</c>、<c>docs/</c> 开头，或以 <c>.md</c> 结尾。</summary>
    public static bool LooksLikeRepoRelativePath(string path) =>
        HasRepoPrefix(path) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>以 <c>src/</c>、<c>tests/</c>、<c>tools/</c>、<c>docs/</c> 开头：一定算引用，不存在就报错。</summary>
    public static bool HasRepoPrefix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var prefix in PathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
