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
/// <param name="MissingDocuments">不存在的文档。</param>
/// <param name="AllowMissing">是否允许文档缺失（<c>--allow-missing</c>）。</param>
public sealed record DocReferenceReport(
    IReadOnlyList<DocReference> References,
    IReadOnlyList<DocReferenceFailure> Failures,
    IReadOnlyList<string> ScannedDocuments,
    IReadOnlyList<string> MissingDocuments,
    bool AllowMissing)
{
    /// <summary>
    /// 所有引用都成立，且（除非允许缺失）传入的文档全都存在。
    /// 默认那五份是必需文档：删掉一份就是文档体系破了，不能安静跳过。
    /// </summary>
    public bool Passed => Failures.Count == 0 && (AllowMissing || MissingDocuments.Count == 0);
}

/// <summary>
/// 文档引用校验：只认定第一个 token 为仓库相对路径的反引号单元，其它反引号内容（命令、配置键、
/// 用户数据路径、代码片段）一律跳过，不产生错误。
/// </summary>
public static class DocReferenceChecker
{
    /// <summary>
    /// 校验一组文档里的引用。
    /// </summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="documents">文档相对路径（<c>/</c> 分隔）。</param>
    /// <param name="allowMissing">
    /// 为 true 时缺失文档记「缺失（跳过）」且不算失败；默认 false，缺失文档算失败（必需文档缺失必须退非零）。
    /// </param>
    public static DocReferenceReport Check(
        string repoRoot,
        IReadOnlyList<string> documents,
        bool allowMissing = false)
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
            var fullPath = RepoPaths.Resolve(repoRoot, document);
            if (!File.Exists(fullPath))
            {
                missing.Add(document);
                continue;
            }

            scanned.Add(document);

            foreach (var prose in MarkdownScanner.ProseLines(fullPath))
            {
                foreach (var unit in MarkdownScanner.BacktickUnits(prose.Text))
                {
                    if (!MarkdownScanner.TryParseUnit(unit, out var path, out var lineHint, out var symbols))
                    {
                        continue;
                    }

                    if (!ReferenceExists(repoRoot, path))
                    {
                        // 以 src/、tests/、tools/、docs/ 开头但文件不存在：算引用，报路径不存在。
                        // 只有以 .md 结尾的路径允许「不存在即跳过」，因为它本来就不一定是引用。
                        if (MarkdownScanner.HasRepoPrefix(path))
                        {
                            failures.Add(new DocReferenceFailure(
                                document,
                                prose.Number,
                                unit,
                                path,
                                DocReferenceFailure.PathMissing));
                        }

                        continue;
                    }

                    if (SymbolsMissing(repoRoot, path, symbols, fileTextCache))
                    {
                        failures.Add(new DocReferenceFailure(
                            document,
                            prose.Number,
                            unit,
                            path,
                            DocReferenceFailure.SymbolMissing));
                        continue;
                    }

                    references.Add(new DocReference(document, prose.Number, unit, path, lineHint, symbols));
                }
            }
        }

        return new DocReferenceReport(references, failures, scanned, missing, allowMissing);
    }

    /// <summary>路径存在（文件或目录）。</summary>
    private static bool ReferenceExists(string repoRoot, string path)
    {
        var fullPath = RepoPaths.Resolve(repoRoot, path);
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    /// <summary>符号判定交给 <see cref="CodeProbeResolver"/>：引用与三态扫描共用同一份符号规则。</summary>
    private static bool SymbolsMissing(
        string repoRoot,
        string referencePath,
        IReadOnlyList<string> symbols,
        Dictionary<string, string?> fileTextCache)
    {
        foreach (var symbol in symbols)
        {
            if (!CodeProbeResolver.Exists(repoRoot, referencePath, symbol, fileTextCache))
            {
                return true;
            }
        }

        return false;
    }
}
