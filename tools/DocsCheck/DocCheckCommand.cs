using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NebulaLauncher.DocsCheck;

/// <summary>命令行参数解析结果。</summary>
/// <param name="Root">仓库根绝对路径。</param>
/// <param name="Documents">文档相对路径（<c>/</c> 分隔）。</param>
/// <param name="Json">是否输出机读 JSON。</param>
public sealed record DocCheckOptions(string Root, IReadOnlyList<string> Documents, bool Json);

/// <summary>一次检查的汇总。</summary>
/// <param name="Root">仓库根绝对路径。</param>
/// <param name="References">引用校验结果。</param>
/// <param name="Budget">行数预算结果。</param>
/// <param name="Features">功能三态结果。</param>
/// <param name="Tests">测试数统计。</param>
public sealed record DocCheckReport(
    string Root,
    DocReferenceReport References,
    DocBudgetResult Budget,
    FeatureScanReport Features,
    TestCountReport Tests)
{
    /// <summary>引用全部成立且行数未越界即通过。</summary>
    public bool Passed => References.Passed && Budget.Passed;

    /// <summary>通过 0，否则 1。</summary>
    public int ExitCode => Passed ? 0 : 1;
}

/// <summary>
/// 命令行外壳：解析参数、跑四段检查、拼报告、定退出码。判定逻辑都在对应的纯类里。
/// </summary>
public static class DocCheckCommand
{
    /// <summary>缺省扫描的五份文档（票 #8 规定）。</summary>
    public static readonly IReadOnlyList<string> DefaultDocuments =
    [
        "docs/PROJECT.md",
        "docs/CODE.md",
        "docs/FEATURES.md",
        "docs/PITFALLS.md",
        "GLOSSARY.md",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>解析参数。<c>--root</c> 缺省为当前目录，<c>--docs</c> 缺省为 <see cref="DefaultDocuments"/>。</summary>
    public static DocCheckOptions Parse(IReadOnlyList<string> args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(currentDirectory);

        var basePath = Path.GetFullPath(currentDirectory);
        var root = basePath;
        var documents = new List<string>();
        var json = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--root":
                    if (index + 1 < args.Count && !IsOption(args[index + 1]))
                    {
                        root = args[++index];
                    }

                    break;

                case "--docs":
                    while (index + 1 < args.Count && !IsOption(args[index + 1]))
                    {
                        documents.Add(args[++index]);
                    }

                    break;

                case "--json":
                    json = true;
                    break;
            }
        }

        return new DocCheckOptions(
            Path.GetFullPath(root, basePath),
            documents.Count == 0 ? DefaultDocuments : documents,
            json);
    }

    /// <summary>跑四段检查，不做参数解析。</summary>
    public static DocCheckReport Analyze(DocCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var references = DocReferenceChecker.Check(options.Root, options.Documents);
        var budget = DocBudgetChecker.Check(options.Root, options.Documents);
        var features = FeatureStateScanner.Scan(options.Root, LoadBlocks(options.Root));
        var tests = TestCountScanner.Scan(Path.Combine(options.Root, "tests", "NebulaLauncher.Tests"));

        return new DocCheckReport(options.Root, references, budget, features, tests);
    }

    /// <summary>解析 + 检查 + 输出；返回退出码。</summary>
    public static int Execute(IReadOnlyList<string> args, string currentDirectory, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        DocCheckOptions options;
        try
        {
            options = Parse(args, currentDirectory);
        }
        catch (ArgumentException exception)
        {
            output.WriteLine($"参数错误：{exception.Message}");
            return 2;
        }

        var report = Analyze(options);
        output.Write(Render(report, options.Json));
        output.WriteLine();
        return report.ExitCode;
    }

    /// <summary>把报告渲染成文本或 JSON。</summary>
    public static string Render(DocCheckReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);
        return json ? RenderJson(report) : RenderText(report);
    }

    private static bool IsOption(string value) => value.StartsWith("--", StringComparison.Ordinal);

    private static IReadOnlyList<FeatureBlock> LoadBlocks(string root)
    {
        string[] candidates =
        [
            Path.Combine(root, "tools", "DocsCheck", "feature-blocks.json"),
            Path.Combine(AppContext.BaseDirectory, "feature-blocks.json"),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return FeatureBlockCatalog.Load(candidate);
            }
        }

        return [];
    }

    private static string RenderText(DocCheckReport report)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("DocsCheck 报告");
        builder.AppendLine(CultureInfo.InvariantCulture, $"根目录：{report.Root}");
        builder.AppendLine();

        builder.AppendLine("【文档引用】");
        foreach (var document in report.References.ScannedDocuments)
        {
            var count = report.References.References.Count(reference =>
                string.Equals(reference.Document, document, StringComparison.Ordinal));
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {document}：{count} 处引用");
        }

        foreach (var failure in report.References.Failures)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {failure.Document}:{failure.Line} `{failure.Unit}`：{failure.Reason}");
        }

        if (report.References.Passed)
        {
            builder.AppendLine("- 引用全部成立");
        }

        foreach (var missing in report.References.MissingDocuments)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {missing}：缺失（跳过）");
        }

        builder.AppendLine();
        builder.AppendLine("【行数预算】");
        foreach (var document in report.Budget.Documents)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {document.Document}：{document.Lines} 行");
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"- 合计 {report.Budget.TotalLines} / {report.Budget.Limits.MaxTotalLines} 行，单份上限 {report.Budget.Limits.MaxLinesPerDocument} 行");
        foreach (var violation in report.Budget.Violations)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {violation}");
        }

        builder.AppendLine();
        var implemented = report.Features.Blocks.Count(block => block.State == FeatureState.Implemented);
        var pending = report.Features.Blocks.Count(block => block.State == FeatureState.Pending);
        var missingBlocks = report.Features.Blocks.Count(block => block.State == FeatureState.Missing);
        builder.AppendLine(CultureInfo.InvariantCulture, $"【功能三态】已实现 {implemented} / 待验证 {pending} / 不存在 {missingBlocks}");
        foreach (var block in report.Features.Blocks)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- {block.Id}：{FeatureStates.Text(block.State)}（代码探测点 {block.MissingCodeProbes.Count} 缺失，测试探测点 {block.MissingTestProbes.Count} 缺失）");

            foreach (var probe in block.MissingCodeProbes)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - 缺代码探测点：{probe.Path}{SymbolSuffix(probe)}");
            }

            foreach (var probe in block.MissingTestProbes)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - 缺测试探测点：{probe.Path}{SymbolSuffix(probe)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"【测试数】Fact {report.Tests.Facts} + InlineData {report.Tests.InlineData} = {report.Tests.TotalTests}");

        builder.AppendLine();
        builder.AppendLine(report.Passed ? "结果：通过" : "结果：未通过");
        return builder.ToString();
    }

    private static string SymbolSuffix(CodeProbe probe)
        => string.IsNullOrEmpty(probe.Symbol) ? string.Empty : $" {probe.Symbol}";

    private static string RenderJson(DocCheckReport report)
    {
        var payload = new
        {
            root = report.Root,
            passed = report.Passed,
            references = new
            {
                count = report.References.References.Count,
                scanned = report.References.ScannedDocuments,
                missing = report.References.MissingDocuments,
                failures = report.References.Failures.Select(failure => new
                {
                    document = failure.Document,
                    line = failure.Line,
                    unit = failure.Unit,
                    path = failure.Path,
                    reason = failure.Reason,
                }),
            },
            budget = new
            {
                documents = report.Budget.Documents.Select(document => new
                {
                    document = document.Document,
                    lines = document.Lines,
                }),
                missing = report.Budget.MissingDocuments,
                totalLines = report.Budget.TotalLines,
                maxPerDocument = report.Budget.Limits.MaxLinesPerDocument,
                maxTotalLines = report.Budget.Limits.MaxTotalLines,
                violations = report.Budget.Violations,
            },
            features = report.Features.Blocks.Select(block => new
            {
                id = block.Id,
                state = block.State.ToString(),
                text = FeatureStates.Text(block.State),
                codePresent = block.CodePresent,
                testPresent = block.TestPresent,
                missingCode = block.MissingCodeProbes.Select(probe => probe.Path),
                missingTest = block.MissingTestProbes.Select(probe => probe.Path),
            }),
            tests = new
            {
                facts = report.Tests.Facts,
                theories = report.Tests.Theories,
                inlineData = report.Tests.InlineData,
                total = report.Tests.TotalTests,
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
