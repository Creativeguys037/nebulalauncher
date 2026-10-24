namespace NebulaLauncher.DocsCheck;

/// <summary>功能点的三态。</summary>
public enum FeatureState
{
    /// <summary>代码与测试探测点全部成立。</summary>
    Implemented,

    /// <summary>代码探测点全部成立，但测试探测点不全部成立。</summary>
    Pending,

    /// <summary>代码探测点不全部成立（含没有探测点的块）。</summary>
    Missing,
}

/// <summary>状态的中文说法，报告与文档共用。</summary>
public static class FeatureStates
{
    /// <summary>已实现 / 待验证 / 不存在。</summary>
    public static string Text(FeatureState state) => state switch
    {
        FeatureState.Implemented => "已实现",
        FeatureState.Pending => "待验证",
        _ => "不存在",
    };
}

/// <summary>
/// 功能文档表格的四列：入口页面、ViewModel、Core 服务、落盘文件。
/// 内容是 markdown 片段；其中的代码引用按引用契约写，工具会连同文档一起校验。
/// </summary>
/// <param name="Entry">入口页面列。</param>
/// <param name="ViewModel">ViewModel 列。</param>
/// <param name="Core">Core 服务列。</param>
/// <param name="Storage">落盘文件列。</param>
public sealed record FeatureColumns(string Entry, string ViewModel, string Core, string Storage);

/// <summary>一个功能点及其探测点。</summary>
/// <param name="Id">固定的功能点标识（也是名字，取票里那八个）。</param>
/// <param name="CodeProbes">代码探测点。</param>
/// <param name="TestProbes">测试探测点。</param>
/// <param name="Title">块名的括注部分，如「安装与修复」；生成文档标题写成「装得上（安装与修复）」。</param>
/// <param name="Summary">一句用户视角描述，生成文档里跟在标题下。</param>
/// <param name="Columns">四列内容；缺省时生成器留空单元格。</param>
public sealed record FeatureBlock(
    string Id,
    IReadOnlyList<CodeProbe> CodeProbes,
    IReadOnlyList<CodeProbe> TestProbes,
    string Title = "",
    string Summary = "",
    FeatureColumns? Columns = null);

/// <summary>一个功能点的扫描结果。</summary>
/// <param name="Id">功能点标识。</param>
/// <param name="State">三态。</param>
/// <param name="CodePresent">代码探测点是否全部成立（没有探测点视为不成立）。</param>
/// <param name="TestPresent">测试探测点是否全部成立（没有探测点视为不成立）。</param>
/// <param name="MissingCodeProbes">不成立的代码探测点。</param>
/// <param name="MissingTestProbes">不成立的测试探测点。</param>
public sealed record FeatureBlockResult(
    string Id,
    FeatureState State,
    bool CodePresent,
    bool TestPresent,
    IReadOnlyList<CodeProbe> MissingCodeProbes,
    IReadOnlyList<CodeProbe> MissingTestProbes);

/// <summary>三态扫描报告。</summary>
/// <param name="Blocks">按输入顺序的功能点结果。</param>
public sealed record FeatureScanReport(IReadOnlyList<FeatureBlockResult> Blocks);

/// <summary>
/// 三态扫描：代码与测试探测点全部成立 → 已实现；只有代码 → 待验证；代码不成立 → 不存在。
/// 探测点数据来自 <c>tools/DocsCheck/feature-blocks.json</c>，判定只取决于传入的仓库树。
/// </summary>
public static class FeatureStateScanner
{
    /// <summary>扫描一组功能点。</summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="blocks">功能点清单。</param>
    public static FeatureScanReport Scan(string repoRoot, IReadOnlyList<FeatureBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(blocks);

        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<FeatureBlockResult>(blocks.Count);

        foreach (var block in blocks)
        {
            var missingCode = MissingProbes(repoRoot, block.CodeProbes, cache);
            var missingTests = MissingProbes(repoRoot, block.TestProbes, cache);

            var codePresent = block.CodeProbes.Count > 0 && missingCode.Count == 0;
            var testPresent = block.TestProbes.Count > 0 && missingTests.Count == 0;
            var state = !codePresent
                ? FeatureState.Missing
                : testPresent
                    ? FeatureState.Implemented
                    : FeatureState.Pending;

            results.Add(new FeatureBlockResult(block.Id, state, codePresent, testPresent, missingCode, missingTests));
        }

        return new FeatureScanReport(results);
    }

    private static List<CodeProbe> MissingProbes(
        string repoRoot,
        IReadOnlyList<CodeProbe> probes,
        IDictionary<string, string?> cache)
    {
        var missing = new List<CodeProbe>();
        foreach (var probe in probes)
        {
            if (!CodeProbeResolver.Exists(repoRoot, probe.Path, probe.Symbol, cache))
            {
                missing.Add(probe);
            }
        }

        return missing;
    }
}
