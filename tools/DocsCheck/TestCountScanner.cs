using System.Text.RegularExpressions;

namespace NebulaLauncher.DocsCheck;

/// <summary>测试数统计结果。</summary>
/// <param name="Facts"><c>[Fact]</c> 个数。</param>
/// <param name="Theories"><c>[Theory]</c> 个数（诊断用，不计入总数）。</param>
/// <param name="InlineData"><c>[InlineData]</c> 个数。</param>
/// <param name="ScannedFiles">扫到的测试文件（文件名，按序数排序）。</param>
public sealed record TestCountReport(
    int Facts,
    int Theories,
    int InlineData,
    IReadOnlyList<string> ScannedFiles)
{
    /// <summary>测试方法总数 = Fact 数 + InlineData 数。</summary>
    public int TotalTests => Facts + InlineData;
}

/// <summary>
/// 测试数统计：扫测试目录顶层的 <c>*.cs</c>，计 <c>[Fact]</c> 与 <c>[Theory]</c> 下每个
/// <c>[InlineData]</c>；<c>obj</c> / <c>bin</c> 下的文件不参与。
/// </summary>
public static class TestCountScanner
{
    /// <summary>
    /// 测试项目在仓库里的相对位置。仓库约定常量而非配置项：测试只可能在 <c>tests/</c> 下，
    /// 让它可配只会给「扫错目录却安静通过」留后门。
    /// </summary>
    public const string DefaultRelativePath = "tests/NebulaLauncher.Tests";

    /// <summary>
    /// 按字面出现计数（不限定行首）：与历史口径 <c>grep -o '\[Fact\]'</c> 一致，
    /// 这样统计结果能对上票面记录的基线条数。
    /// </summary>
    private static readonly Regex FactPattern = new(
        @"\[Fact\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TheoryPattern = new(
        @"\[Theory\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InlineDataPattern = new(
        @"\[InlineData\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>统计一个测试目录。</summary>
    /// <param name="testDirectory">测试目录绝对路径；不存在时返回全零。</param>
    public static TestCountReport Scan(string testDirectory)
    {
        ArgumentNullException.ThrowIfNull(testDirectory);

        if (!Directory.Exists(testDirectory))
        {
            return new TestCountReport(0, 0, 0, []);
        }

        var facts = 0;
        var theories = 0;
        var inlineData = 0;
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testDirectory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (IsExcluded(testDirectory, file))
            {
                continue;
            }

            files.Add(Path.GetFileName(file));

            var text = File.ReadAllText(file);
            facts += FactPattern.Matches(text).Count;
            theories += TheoryPattern.Matches(text).Count;
            inlineData += InlineDataPattern.Matches(text).Count;
        }

        files.Sort(StringComparer.Ordinal);
        return new TestCountReport(facts, theories, inlineData, files);
    }

    /// <summary>路径里出现 <c>obj</c> 或 <c>bin</c> 目录段的文件不算。</summary>
    private static bool IsExcluded(string testDirectory, string file)
    {
        var relative = Path.GetRelativePath(testDirectory, file);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
