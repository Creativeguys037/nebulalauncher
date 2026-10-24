using System.Text.Encodings.Web;
using System.Text.Json;

namespace NebulaLauncher.DocsCheck;

/// <summary>JSON 报告渲染：字段名与文本报告同义，供脚本消费。</summary>
public static class JsonReportRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把报告渲染成 JSON 文本。</summary>
    /// <param name="report">检查结果。</param>
    public static string Render(DocCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var payload = new
        {
            root = report.Root,
            // 证据链文档只校验引用与链接，不进行数预算；字段单列，免得看 JSON 的人以为预算漏算了它们。
            evidence = report.EvidenceDocuments,
            // 判定范围的字段名照实写：三态与测试数只是观测，不参与判定。
            referencesAndBudgetPassed = report.ReferencesAndBudgetPassed,
            referencesAndLinksPassed = report.ReferencesAndLinksPassed,
            exitCode = report.ExitCode,
            exitCodeLegend = DocCheckReport.ExitCodeLegend,
            references = new
            {
                count = report.References.References.Count,
                scanned = report.References.ScannedDocuments,
                missing = report.References.MissingDocuments,
                allowMissing = report.References.AllowMissing,
                passed = report.References.Passed,
                failures = report.References.Failures.Select(failure => new
                {
                    document = failure.Document,
                    line = failure.Line,
                    unit = failure.Unit,
                    path = failure.Path,
                    reason = failure.Reason,
                }),
            },
            links = new
            {
                count = report.Links.Links.Count,
                passed = report.Links.Passed,
                failures = report.Links.Failures.Select(failure => new
                {
                    document = failure.Document,
                    line = failure.Line,
                    text = failure.Text,
                    target = failure.Target,
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
                passed = report.Budget.Passed,
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

        return JsonSerializer.Serialize(payload, Options);
    }
}
