using System.Globalization;

namespace NebulaLauncher.DocsCheck;

/// <summary>
/// 命令行编排：读文档 → 跑引用/链接/预算/三态四个 checker → 渲染。
/// 参数解析在 <see cref="DocCheckOptions"/>，文本与 JSON 渲染在
/// <see cref="TextReportRenderer"/> / <see cref="JsonReportRenderer"/>。
/// </summary>
public static class DocCheckCommand
{
    /// <summary>参数错误（未知参数、<c>--root</c> 缺值等）的退出码，避开 0–3 的检查语义。</summary>
    public const int UsageError = 64;

    /// <summary>执行检查或按 <c>--emit-features</c> 生成功能文档。</summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="currentDirectory">当前目录，用于解析相对路径。</param>
    /// <param name="writer">报告输出目标。</param>
    /// <returns>退出码：0 全过；1 引用/链接/必需文档有问题；2 行数预算越界；3 两者都有。</returns>
    public static int Execute(IReadOnlyList<string> args, string currentDirectory, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(writer);

        var options = DocCheckOptions.Parse(args, currentDirectory);

        if (options.Help)
        {
            writer.Write(TextReportRenderer.Usage());
            return 0;
        }

        if (!Directory.Exists(options.Root))
        {
            writer.WriteLine($"参数错误：--root 指向的目录不存在：{options.Root}");
            writer.WriteLine(TextReportRenderer.Usage());
            return UsageError;
        }

        if (options.EmitFeatures is { } target)
        {
            return EmitFeatures(options, target, writer);
        }

        var report = Analyze(options);
        writer.Write(options.Json ? JsonReportRenderer.Render(report) : TextReportRenderer.Render(report));
        return report.ExitCode;
    }

    /// <summary>跑四个 checker 并汇总；不做渲染、不落盘。</summary>
    /// <param name="options">解析好的参数。</param>
    public static DocCheckReport Analyze(DocCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var blocks = LoadBlocks(options.Root);
        return new DocCheckReport(
            options.Root,
            DocReferenceChecker.Check(options.Root, options.Documents, options.AllowMissing),
            DocLinkChecker.Check(options.Root, options.Documents),
            DocBudgetChecker.Check(options.Root, options.Documents),
            FeatureStateScanner.Scan(options.Root, blocks),
            TestCountScanner.Scan(RepoPaths.Resolve(options.Root, TestCountScanner.DefaultRelativePath)));
    }

    /// <summary>重新生成功能三态文档；越界或数据缺失时不写盘并返回 1。</summary>
    private static int EmitFeatures(DocCheckOptions options, string target, TextWriter writer)
    {
        var blocks = LoadBlocks(options.Root);
        if (blocks.Count == 0)
        {
            writer.WriteLine("生成失败：读不到 feature-blocks.json，无法确定功能点。");
            return 1;
        }

        var result = FeatureDocGenerator.Generate(
            options.Root,
            blocks,
            DateOnly.FromDateTime(DateTime.Now));
        if (!result.Passed)
        {
            foreach (var violation in result.Violations)
            {
                writer.WriteLine($"- {violation}");
            }

            return 1;
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(target, result.Markdown);
        var display = Path.GetRelativePath(options.Root, target).Replace(Path.DirectorySeparatorChar, '/');
        writer.WriteLine($"已生成 {display}：{result.Lines} 行（上限 {result.MaxLines} 行）");
        return 0;
    }

    /// <summary>
    /// 读探测点数据：仓库内的约定位置优先，其次是产物目录里的副本（工具单独拷出去用时）；
    /// 都没有就返回空清单，三态会全部判成「不存在」而不是崩掉。
    /// </summary>
    private static IReadOnlyList<FeatureBlock> LoadBlocks(string root)
    {
        string[] candidates =
        [
            RepoPaths.Resolve(root, FeatureBlockCatalog.DefaultRelativePath),
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
}
