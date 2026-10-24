using NebulaLauncher.DocsCheck;

namespace NebulaLauncher.Tests;

/// <summary>
/// 文档行数预算（票 #8）：单份上限 400 行、总量上限 1000 行；
/// 不存在的文档标「缺失（跳过）」，不参与总量，也不算越界。
/// </summary>
public sealed class DocsCheck_BudgetTests
{
    [Fact]
    public void 未超过预算时通过()
    {
        using var temp = new TempDirectory();
        WriteLines(temp, "docs/PROJECT.md", 120);
        WriteLines(temp, "docs/CODE.md", 200);

        var result = DocBudgetChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md"]);

        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
        Assert.Equal(320, result.TotalLines);
        Assert.Equal([120, 200], result.Documents.Select(document => document.Lines));
        Assert.Equal(400, result.Limits.MaxLinesPerDocument);
        Assert.Equal(1000, result.Limits.MaxTotalLines);
    }

    [Fact]
    public void 单份超过四百行时报告越界()
    {
        using var temp = new TempDirectory();
        WriteLines(temp, "docs/PROJECT.md", 401);
        WriteLines(temp, "docs/CODE.md", 10);

        var result = DocBudgetChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations);
        Assert.Contains("docs/PROJECT.md", violation, StringComparison.Ordinal);
        Assert.Contains("401", violation, StringComparison.Ordinal);
        Assert.Contains("400", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void 总量超过一千行时报告越界()
    {
        using var temp = new TempDirectory();
        WriteLines(temp, "docs/PROJECT.md", 400);
        WriteLines(temp, "docs/CODE.md", 400);
        WriteLines(temp, "docs/FEATURES.md", 400);

        var result = DocBudgetChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md", "docs/FEATURES.md"]);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations);
        Assert.Contains("1200", violation, StringComparison.Ordinal);
        Assert.Contains("1000", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void 恰好四百行与恰好一千行都通过()
    {
        using var temp = new TempDirectory();
        WriteLines(temp, "docs/PROJECT.md", 400);
        WriteLines(temp, "docs/CODE.md", 400);
        WriteLines(temp, "docs/FEATURES.md", 200);

        var result = DocBudgetChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md", "docs/FEATURES.md"]);

        Assert.True(result.Passed);
        Assert.Equal(1000, result.TotalLines);
    }

    [Fact]
    public void 缺失文档跳过且不计入总量()
    {
        using var temp = new TempDirectory();
        WriteLines(temp, "docs/PROJECT.md", 300);

        var result = DocBudgetChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md", "GLOSSARY.md"]);

        Assert.True(result.Passed);
        Assert.Equal(300, result.TotalLines);
        var document = Assert.Single(result.Documents);
        Assert.Equal("docs/PROJECT.md", document.Document);
        Assert.Equal(["docs/CODE.md", "GLOSSARY.md"], result.MissingDocuments);
    }

    [Fact]
    public void 单份与总量同时越界时两条都报()
    {
        using var temp = new TempDirectory();
        WriteLines(temp, "docs/PROJECT.md", 450);
        WriteLines(temp, "docs/CODE.md", 300);
        WriteLines(temp, "docs/FEATURES.md", 300);

        var result = DocBudgetChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md", "docs/FEATURES.md"]);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(result.Violations, item => item.Contains("docs/PROJECT.md", StringComparison.Ordinal));
        Assert.Contains(result.Violations, item => item.Contains("1050", StringComparison.Ordinal));
    }

    private static string WriteLines(TempDirectory temp, string relativePath, int lineCount)
    {
        var path = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new string[lineCount];
        for (var index = 0; index < lineCount; index++)
        {
            lines[index] = $"第 {index + 1} 行";
        }

        File.WriteAllLines(path, lines);
        return path;
    }
}
