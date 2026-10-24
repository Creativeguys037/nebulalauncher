using System.Globalization;
using System.Text;

namespace NebulaLauncher.DocsCheck;

/// <summary>一次功能文档生成的结果。</summary>
/// <param name="Markdown">整份文档内容，行尾是 <c>\n</c>。</param>
/// <param name="Lines">行数，计法与 <see cref="DocBudgetChecker"/> 一致（尾随换行不算新行）。</param>
/// <param name="MaxLines">本次使用的行数上限。</param>
/// <param name="Violations">越界说明；空表示通过。</param>
public sealed record FeatureDocResult(
    string Markdown,
    int Lines,
    int MaxLines,
    IReadOnlyList<string> Violations)
{
    /// <summary>没有越界即通过。</summary>
    public bool Passed => Violations.Count == 0;
}

/// <summary>
/// 生成 <c>docs/FEATURES.md</c>：块按输入顺序排，每块一句用户视角描述 + 四列表
/// （入口页面 → ViewModel → Core 服务 → 落盘文件）+ 三态列，顶部写生成命令、生成日期与三态定义。
///
/// 三态来自 <see cref="FeatureStateScanner"/>，只取决于传入的仓库树；四列内容来自
/// <c>feature-blocks.json</c>，渲染代码里不出现任何具体路径——换一份数据就换一份文档。
/// 块数同样由数据决定，标题与正文都不写死数字。
///
/// 行数上限 191 是票 #13 的硬约束：其余四份文档 809 行 + 本份 191 行 = 工具总量上限 1000 行。
/// </summary>
public static class FeatureDocGenerator
{
    /// <summary>本份文档的行数上限（票 #13）。</summary>
    public const int MaxLines = 191;

    /// <summary>生成命令里 <c>-c</c> 取的环境变量名。</summary>
    public const string BuildConfigurationVariable = "NEBULA_BUILD_CONFIG";

    /// <summary>环境变量没设时的构建配置。</summary>
    public const string DefaultBuildConfiguration = "Release";

    /// <summary>
    /// 写进文档顶部的生成命令。改命令行必须同步改这里：文档里的命令要能直接跑。
    /// <c>-c</c> 从 <see cref="BuildConfigurationVariable"/> 读，缺省 <see cref="DefaultBuildConfiguration"/>，
    /// 别再把某一台机器的构建配置写进文档。
    /// </summary>
    public static string GenerateCommand =>
        GenerateCommandFor(Environment.GetEnvironmentVariable(BuildConfigurationVariable));

    /// <summary>按指定构建配置拼生成命令；<paramref name="buildConfiguration"/> 为 null 或空白时用缺省值。</summary>
    /// <param name="buildConfiguration">构建配置名，如 <c>Release</c>、<c>Lead</c>。</param>
    public static string GenerateCommandFor(string? buildConfiguration)
        => $"dotnet run --project tools/DocsCheck/DocsCheck.csproj -c {ConfigurationOrDefault(buildConfiguration)} -- --emit-features docs/FEATURES.md";

    private const string TableHeader = "| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |";

    private const string TableDivider = "| --- | --- | --- | --- | --- |";

    /// <summary>按输入顺序生成整份 markdown，并检查行数上限。</summary>
    /// <param name="repoRoot">仓库根绝对路径；三态只看这棵树。</param>
    /// <param name="blocks">功能点清单，顺序即文档顺序。</param>
    /// <param name="generatedOn">生成日期，格式化成 <c>yyyy-MM-dd</c> 写进文档。</param>
    /// <param name="maxLines">行数上限，缺省 191。</param>
    public static FeatureDocResult Generate(
        string repoRoot,
        IReadOnlyList<FeatureBlock> blocks,
        DateOnly generatedOn,
        int maxLines = MaxLines)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(blocks);

        var scan = FeatureStateScanner.Scan(repoRoot, blocks);
        var markdown = Render(blocks, scan, generatedOn);
        var lines = CountLines(markdown);

        var violations = new List<string>();
        if (lines > maxLines)
        {
            violations.Add($"docs/FEATURES.md 生成 {lines} 行，超过上限 {maxLines} 行");
        }

        return new FeatureDocResult(markdown, lines, maxLines, violations);
    }

    private static string Render(
        IReadOnlyList<FeatureBlock> blocks,
        FeatureScanReport scan,
        DateOnly generatedOn)
    {
        var builder = new StringBuilder();

        // 块数由数据决定：加一块就写 9 块，不改代码。
        Line(builder, $"# 功能完成度（{blocks.Count} 块三态）");
        Line(builder, string.Empty);
        Line(builder, "本文件由工具生成，请勿手改。");
        Line(builder, string.Empty);
        Line(builder, $"- 生成命令：`{GenerateCommand}`");
        Line(builder, $"- 生成日期：{generatedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        Line(builder, string.Empty);
        Line(builder, "## 三态定义");
        Line(builder, string.Empty);
        Line(builder, "- **已实现**：代码探测点与测试探测点都命中。");
        Line(builder, "- **待验证**：代码探测点命中，测试探测点缺失。");
        Line(builder, "- **不存在**：代码探测点未命中。");

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var state = scan.Blocks[index].State;
            var stateText = FeatureStates.Text(state);

            Line(builder, string.Empty);
            Line(builder, $"## {index + 1}. {DisplayName(block)}");
            Line(builder, string.Empty);
            Line(builder, $"{Summary(block)} —— 三态：{stateText}");
            Line(builder, string.Empty);
            Line(builder, TableHeader);
            Line(builder, TableDivider);
            Line(builder, $"| {Cell(block.Columns?.Entry)} | {Cell(block.Columns?.ViewModel)} | {Cell(block.Columns?.Core)} | {Cell(block.Columns?.Storage)} | {stateText} |");
        }

        return builder.ToString();
    }

    /// <summary>块名：有括注时写「装得上（安装与修复）」，没有就只写 id。</summary>
    private static string DisplayName(FeatureBlock block)
        => string.IsNullOrWhiteSpace(block.Title) ? block.Id : $"{block.Id}（{block.Title}）";

    private static string Summary(FeatureBlock block)
        => string.IsNullOrWhiteSpace(block.Summary) ? block.Id : block.Summary;

    private static string Cell(string? value) => value ?? string.Empty;

    private static string ConfigurationOrDefault(string? buildConfiguration)
        => string.IsNullOrWhiteSpace(buildConfiguration) ? DefaultBuildConfiguration : buildConfiguration.Trim();

    private static void Line(StringBuilder builder, string text)
        => builder.Append(text).Append('\n');

    /// <summary>行数：<c>\n</c> 的个数；不以换行结尾时补一行。</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var newlines = 0;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                newlines++;
            }
        }

        return text.EndsWith('\n') ? newlines : newlines + 1;
    }
}
