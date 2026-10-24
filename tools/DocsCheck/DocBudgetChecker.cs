namespace NebulaLauncher.DocsCheck;

/// <summary>文档行数预算。</summary>
/// <param name="MaxLinesPerDocument">单份上限；超过即越界（等于上限不算）。</param>
/// <param name="MaxTotalLines">总量上限；超过即越界（等于上限不算）。</param>
public sealed record DocBudgetLimits(int MaxLinesPerDocument = 400, int MaxTotalLines = 1000);

/// <summary>一份文档的行数。</summary>
/// <param name="Document">文档相对路径。</param>
/// <param name="Lines">行数。</param>
public sealed record DocLineCount(string Document, int Lines);

/// <summary>行数预算结果。</summary>
/// <param name="Documents">存在的文档及行数，保持传入顺序。</param>
/// <param name="MissingDocuments">不存在的文档（「缺失（跳过）」）。</param>
/// <param name="TotalLines">存在的文档行数之和；缺失文档不计入。</param>
/// <param name="Limits">本次使用的上限。</param>
/// <param name="Violations">越界说明；空表示通过。</param>
public sealed record DocBudgetResult(
    IReadOnlyList<DocLineCount> Documents,
    IReadOnlyList<string> MissingDocuments,
    int TotalLines,
    DocBudgetLimits Limits,
    IReadOnlyList<string> Violations)
{
    /// <summary>没有越界即通过。</summary>
    public bool Passed => Violations.Count == 0;
}

/// <summary>
/// 文档行数预算检查：单份 &gt; 400 行、总量 &gt; 1000 行即越界；缺失文档跳过且不计入总量。
/// </summary>
public static class DocBudgetChecker
{
    /// <summary>检查一组文档的行数预算。</summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="documents">文档相对路径（<c>/</c> 分隔）。</param>
    /// <param name="limits">可选上限；默认 400 / 1000。</param>
    public static DocBudgetResult Check(
        string repoRoot,
        IReadOnlyList<string> documents,
        DocBudgetLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(documents);

        limits ??= new DocBudgetLimits();
        var counted = new List<DocLineCount>(documents.Count);
        var missing = new List<string>();
        var violations = new List<string>();

        foreach (var document in documents)
        {
            var fullPath = Path.Combine(repoRoot, document.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                missing.Add(document);
                continue;
            }

            var lines = File.ReadAllLines(fullPath).Length;
            counted.Add(new DocLineCount(document, lines));

            if (lines > limits.MaxLinesPerDocument)
            {
                violations.Add($"{document} {lines} 行，超过单份上限 {limits.MaxLinesPerDocument} 行");
            }
        }

        var total = 0;
        foreach (var document in counted)
        {
            total += document.Lines;
        }

        if (total > limits.MaxTotalLines)
        {
            violations.Add($"文档总量 {total} 行，超过上限 {limits.MaxTotalLines} 行");
        }

        return new DocBudgetResult(counted, missing, total, limits, violations);
    }
}
