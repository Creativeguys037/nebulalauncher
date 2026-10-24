namespace NebulaLauncher.DocsCheck;

/// <summary>一次检查的汇总。</summary>
/// <param name="Root">仓库根绝对路径。</param>
/// <param name="References">引用校验结果（含必需文档是否齐全）。</param>
/// <param name="Links">站内 markdown 链接校验结果。</param>
/// <param name="Budget">行数预算结果。</param>
/// <param name="Features">功能三态结果。</param>
/// <param name="Tests">测试数统计。</param>
/// <param name="Evidence">证据链文档（只校验引用与链接、不进行数预算）；没有则为空。</param>
public sealed record DocCheckReport(
    string Root,
    DocReferenceReport References,
    DocLinkReport Links,
    DocBudgetResult Budget,
    FeatureScanReport Features,
    TestCountReport Tests,
    IReadOnlyList<string>? Evidence = null)
{
    /// <summary>证据链文档清单，没传 <c>--evidence</c> 时为空。</summary>
    public IReadOnlyList<string> EvidenceDocuments => Evidence ?? [];

    /// <summary>退出码表：报告末尾与 <c>--help</c> 共用这一份文案，改文案只改这里。</summary>
    public const string ExitCodeLegend =
        "退出码：0 全部通过；1 引用/链接/必需文档有问题；2 行数预算越界；3 两者都有。";

    /// <summary>引用与站内链接全部成立。</summary>
    public bool ReferencesAndLinksPassed => References.Passed && Links.Passed;

    /// <summary>
    /// 引用、链接、行数三者都通过。名字写明判定范围：三态与测试数只是观测，不参与判定；
    /// 原来的 <c>Passed</c> 只算 References+Budget，叫人以为整份报告都过了。
    /// </summary>
    public bool ReferencesAndBudgetPassed => ReferencesAndLinksPassed && Budget.Passed;

    /// <summary>0 全过；1 引用/链接/必需文档有问题；2 行数预算越界；3 两者都有。</summary>
    public int ExitCode => (ReferencesAndLinksPassed ? 0 : 1) + (Budget.Passed ? 0 : 2);
}
