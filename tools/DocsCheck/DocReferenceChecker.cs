using System.Text.RegularExpressions;

namespace NebulaLauncher.DocsCheck;

/// <summary>
/// 一条被识别出来的代码引用。
///
/// 契约（票 #8 定死）：引用是一个反引号单元，第一个 token 是仓库相对路径（可带 <c>:行号</c> 或
/// <c>:起始-结束</c> 提示后缀），其后每个 token 是符号（<c>Type</c> 或 <c>Type.Member</c>）。
/// </summary>
/// <param name="Document">文档相对路径（与传入时一致）。</param>
/// <param name="Line">所在行号，从 1 开始。</param>
/// <param name="Unit">原始反引号单元的内容，不含反引号。</param>
/// <param name="Path">去掉行号提示后的仓库相对路径。</param>
/// <param name="LineHint">行号提示原文（如 <c>105</c>、<c>10-24</c>），没有则为 null。只作提示，不参与定位。</param>
/// <param name="Symbols">符号列表；允许为空（只有路径的引用）。</param>
public sealed record DocReference(
    string Document,
    int Line,
    string Unit,
    string Path,
    string? LineHint,
    IReadOnlyList<string> Symbols);

/// <summary>引用不成立的一条记录。</summary>
/// <param name="Document">文档相对路径。</param>
/// <param name="Line">行号，从 1 开始。</param>
/// <param name="Unit">原始反引号单元。</param>
/// <param name="Path">引用里的仓库相对路径。</param>
/// <param name="Reason">失败原因，取值为 <see cref="PathMissing"/> 或 <see cref="SymbolMissing"/>。</param>
public sealed record DocReferenceFailure(
    string Document,
    int Line,
    string Unit,
    string Path,
    string Reason)
{
    /// <summary>路径在仓库根下不存在。</summary>
    public const string PathMissing = "路径不存在";

    /// <summary>路径存在，但类型或成员在该文件里找不到。</summary>
    public const string SymbolMissing = "符号不存在";
}

/// <summary><see cref="DocReferenceChecker.Check"/> 的结果。</summary>
/// <param name="References">成立的引用（路径与符号都在）。</param>
/// <param name="Failures">不成立的引用。</param>
/// <param name="ScannedDocuments">实际读到的文档（相对路径，保持传入顺序）。</param>
/// <param name="MissingDocuments">不存在、按「缺失（跳过）」处理的文档。</param>
public sealed record DocReferenceReport(
    IReadOnlyList<DocReference> References,
    IReadOnlyList<DocReferenceFailure> Failures,
    IReadOnlyList<string> ScannedDocuments,
    IReadOnlyList<string> MissingDocuments)
{
    /// <summary>所有引用都成立即为通过；零引用文档天然通过。</summary>
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// 文档引用校验：只认定第一个 token 为仓库相对路径的反引号单元，其它反引号内容（命令、配置键、
/// 用户数据路径、代码片段）一律跳过，不产生错误。
/// </summary>
public static class DocReferenceChecker
{
    /// <summary>仓库相对路径的根目录前缀（<c>/</c> 分隔，区分大小写）。</summary>
    private static readonly string[] PathPrefixes = ["src/", "tests/", "tools/", "docs/"];

    /// <summary>行号提示后缀：<c>:105</c> 或 <c>:10-24</c>。</summary>
    private static readonly Regex LineHintSuffix = new(
        @"^(?<path>.+?):(?<hint>[0-9]+(?:-[0-9]+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>反引号单元（不跨行）。</summary>
    private static readonly Regex BacktickUnit = new(
        "`([^`\\n]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>围栏代码块的开闭行（``` 或 ~~~）。</summary>
    private static readonly Regex FenceLine = new(
        @"^\s*(```|~~~)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 校验一组文档里的引用。
    /// </summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="documents">文档相对路径（<c>/</c> 分隔）。不存在的文档记入 <see cref="DocReferenceReport.MissingDocuments"/>，不算失败。</param>
    public static DocReferenceReport Check(string repoRoot, IReadOnlyList<string> documents)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(documents);

        var references = new List<DocReference>();
        var failures = new List<DocReferenceFailure>();
        var scanned = new List<string>();
        var missing = new List<string>();
        var fileTextCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in documents)
        {
            var fullPath = Path.Combine(repoRoot, document.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                missing.Add(document);
                continue;
            }

            scanned.Add(document);

            var lines = File.ReadAllLines(fullPath);
            var inFence = false;

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (FenceLine.IsMatch(line))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                foreach (Match match in BacktickUnit.Matches(line))
                {
                    var unit = match.Groups[1].Value.Trim();
                    if (!TryParseUnit(unit, out var path, out var lineHint, out var symbols))
                    {
                        continue;
                    }

                    var referencePath = ResolveReferencePath(repoRoot, path);
                    if (referencePath is null)
                    {
                        // 以 src/、tests/、tools/、docs/ 开头但文件不存在：算引用，报路径不存在。
                        // 只有以 .md 结尾的路径允许「不存在即跳过」，因为它本来就不一定是引用。
                        if (HasRepoPrefix(path))
                        {
                            failures.Add(new DocReferenceFailure(
                                document,
                                index + 1,
                                unit,
                                path,
                                DocReferenceFailure.PathMissing));
                        }

                        continue;
                    }

                    var failuresBefore = failures.Count;
                    foreach (var symbol in symbols)
                    {
                        if (!SymbolExists(repoRoot, referencePath, symbol, fileTextCache))
                        {
                            failures.Add(new DocReferenceFailure(
                                document,
                                index + 1,
                                unit,
                                path,
                                DocReferenceFailure.SymbolMissing));
                            break;
                        }
                    }

                    if (failures.Count == failuresBefore)
                    {
                        references.Add(new DocReference(document, index + 1, unit, path, lineHint, symbols));
                    }
                }
            }
        }

        return new DocReferenceReport(references, failures, scanned, missing);
    }

    /// <summary>
    /// 把单元拆成路径、行号提示与符号列表；第一个 token 不是仓库相对路径时返回 false（跳过）。
    /// </summary>
    private static bool TryParseUnit(
        string unit,
        out string path,
        out string? lineHint,
        out IReadOnlyList<string> symbols)
    {
        path = string.Empty;
        lineHint = null;
        symbols = [];

        if (unit.Length == 0)
        {
            return false;
        }

        var tokens = unit.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var first = tokens[0];
        var hintMatch = LineHintSuffix.Match(first);
        if (hintMatch.Success)
        {
            path = hintMatch.Groups["path"].Value;
            lineHint = hintMatch.Groups["hint"].Value;
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

        var symbolTokens = new List<string>(tokens.Length - 1);
        for (var index = 1; index < tokens.Length; index++)
        {
            symbolTokens.Add(tokens[index]);
        }

        symbols = symbolTokens;
        return true;
    }

    /// <summary>形如 <c>src/</c>、<c>tests/</c>、<c>tools/</c>、<c>docs/</c> 开头，或以 <c>.md</c> 结尾。</summary>
    private static bool LooksLikeRepoRelativePath(string path) =>
        HasRepoPrefix(path) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>以 <c>src/</c>、<c>tests/</c>、<c>tools/</c>、<c>docs/</c> 开头：一定算引用，不存在就报错。</summary>
    private static bool HasRepoPrefix(string path)
    {
        foreach (var prefix in PathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>路径存在（文件或目录）则返回仓库相对路径，否则返回 null。</summary>
    private static string? ResolveReferencePath(string repoRoot, string path)
    {
        var fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath) || Directory.Exists(fullPath) ? path : null;
    }

    /// <summary>
    /// 符号判定交给 <see cref="CodeProbeResolver"/>：引用与三态扫描共用同一份符号规则。
    /// </summary>
    private static bool SymbolExists(
        string repoRoot,
        string referencePath,
        string symbol,
        Dictionary<string, string?> fileTextCache) =>
        CodeProbeResolver.Exists(repoRoot, referencePath, symbol, fileTextCache);
}
