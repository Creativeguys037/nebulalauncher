using System.Globalization;
using System.Text;

namespace NebulaLauncher.DocsCheck;

/// <summary>文本报告渲染：给人看的报告与 <c>--help</c> 用法说明都从这里出。</summary>
public static class TextReportRenderer
{
    /// <summary>用法说明。<c>--help</c> 打印它，末尾是那张退出码表。</summary>
    public static string Usage()
    {
        var builder = new StringBuilder();
        builder.AppendLine("用法：DocsCheck [--root <仓库根>] [--docs <文档>...] [--evidence <文档>...] [--json] [--allow-missing] [--emit-features <输出路径>] [--help]");
        builder.AppendLine();
        builder.AppendLine("  --root <路径>           仓库根，缺省为当前目录");
        builder.AppendLine("  --docs <路径>...        要校验的文档，缺省为五份必需文档：");
        foreach (var document in DocCheckOptions.DefaultDocuments)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"                          {document}");
        }

        builder.AppendLine("  --evidence <路径>...    证据链文档：只校验引用与链接，不进行数预算（如 docs/research/）");
        builder.AppendLine("  --json                  输出机读 JSON");
        builder.AppendLine("  --allow-missing         允许必需文档缺失：缺失记「跳过」而不算失败");
        builder.AppendLine("  --emit-features <路径>  重新生成功能三态文档，如 docs/FEATURES.md");
        builder.AppendLine("  --help                  打印本页");
        builder.AppendLine();
        builder.AppendLine(DocCheckReport.ExitCodeLegend);
        builder.AppendLine("参数错误另计，退出码 64。");
        return builder.ToString();
    }

    /// <summary>渲染一份检查报告。</summary>
    /// <param name="report">检查结果。</param>
    public static string Render(DocCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("DocsCheck 报告");
        builder.AppendLine(CultureInfo.InvariantCulture, $"根目录：{report.Root}");
        builder.AppendLine();

        AppendReferences(builder, report.References, report.EvidenceDocuments);
        AppendLinks(builder, report.Links);
        AppendBudget(builder, report.Budget, report.EvidenceDocuments);
        AppendVersions(builder, report.VersionConsistency);
        AppendFeatures(builder, report.Features);
        AppendTests(builder, report.Tests);

        builder.AppendLine();
        builder.AppendLine(report.AllCheckedPassed
            ? "结果：通过"
            : $"结果：未通过（退出码 {report.ExitCode}）");
        builder.AppendLine(DocCheckReport.ExitCodeLegend);
        return builder.ToString();
    }

    private static void AppendReferences(
        StringBuilder builder,
        DocReferenceReport references,
        IReadOnlyList<string> evidence)
    {
        builder.AppendLine("【文档引用】");
        foreach (var document in references.ScannedDocuments)
        {
            var count = references.References.Count(reference =>
                string.Equals(reference.Document, document, StringComparison.Ordinal));
            var scope = evidence.Contains(document, StringComparer.Ordinal) ? "（证据链，不计预算）" : string.Empty;
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {document}：{count} 处引用{scope}");
        }

        foreach (var failure in references.Failures)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- {failure.Document}:{failure.Line} `{failure.Unit}`：{failure.Reason}");
        }

        if (references.Passed)
        {
            builder.AppendLine("- 引用全部成立");
        }

        foreach (var missing in references.MissingDocuments)
        {
            // 默认缺失即失败（删掉一份必需文档就是文档体系破了）；--allow-missing 才降级成跳过。
            var missingState = references.AllowMissing ? "缺失（跳过）" : "缺失（失败）";
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {missing}：{missingState}");
        }

        builder.AppendLine();
    }

    private static void AppendLinks(StringBuilder builder, DocLinkReport links)
    {
        builder.AppendLine("【站内链接】");
        foreach (var group in links.Links.GroupBy(link => link.Document, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}：{group.Count()} 处链接");
        }

        foreach (var failure in links.Failures)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- {failure.Document}:{failure.Line} [{failure.Text}]({failure.Target})：{failure.Reason}");
        }

        if (links.Passed)
        {
            builder.AppendLine("- 链接全部成立");
        }

        builder.AppendLine();
    }

    private static void AppendBudget(
        StringBuilder builder,
        DocBudgetResult budget,
        IReadOnlyList<string> evidence)
    {
        builder.AppendLine("【行数预算】");
        foreach (var document in budget.Documents)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {document.Document}：{document.Lines} 行");
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"- 合计 {budget.TotalLines} / {budget.Limits.MaxTotalLines} 行，单份上限 {budget.Limits.MaxLinesPerDocument} 行");
        foreach (var violation in budget.Violations)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {violation}");
        }

        foreach (var missing in budget.MissingDocuments)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {missing}：缺失，不计入总量");
        }

        if (evidence.Count > 0)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- 另有 {evidence.Count} 份证据链文档（--evidence）：只校验引用与链接，不计入行数预算");
        }

        builder.AppendLine();
    }

    private static void AppendVersions(StringBuilder builder, VersionConsistencyReport versions)
    {
        builder.AppendLine("【版本口径】");
        if (!versions.Checked)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- 未检查（根目录既没有 {VersionConsistencyChecker.BuildPropsPath}，也没有解决方案文件）");
            builder.AppendLine();
            return;
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"- 版本号：{OrMissing(versions.Version)}（来源：{VersionConsistencyChecker.BuildPropsPath}）");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"- 发布 tag：{OrMissing(versions.ReleaseTag)}（来源：{VersionConsistencyChecker.ConventionsPath}）");

        foreach (var failure in versions.Failures)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {failure}");
        }

        if (versions.Passed)
        {
            builder.AppendLine("- 三处一致，release notes 四节齐全，无旧口径残留");
        }

        builder.AppendLine();
    }

    private static void AppendFeatures(StringBuilder builder, FeatureScanReport features)
    {
        var implemented = features.Blocks.Count(block => block.State == FeatureState.Implemented);
        var pending = features.Blocks.Count(block => block.State == FeatureState.Pending);
        var missing = features.Blocks.Count(block => block.State == FeatureState.Missing);
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"【功能三态】已实现 {implemented} / 待验证 {pending} / 不存在 {missing}");

        foreach (var block in features.Blocks)
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
    }

    private static void AppendTests(StringBuilder builder, TestCountReport tests)
    {
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"【测试数】Fact {tests.Facts} + InlineData {tests.InlineData} = {tests.TotalTests}");
    }

    private static string SymbolSuffix(CodeProbe probe)
        => string.IsNullOrEmpty(probe.Symbol) ? string.Empty : $" {probe.Symbol}";

    private static string OrMissing(string value)
        => string.IsNullOrEmpty(value) ? "读不到" : value;
}
