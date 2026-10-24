using System.Text.RegularExpressions;
using NebulaLauncher.DocsCheck;
using static NebulaLauncher.Tests.DocsCheckTestPaths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 功能文档生成（票 #13）：<c>docs/FEATURES.md</c> 必须由工具产出，不是手写。
/// 8 块固定、顺序固定，每块一句用户视角描述 + 四列表（入口页面 → ViewModel → Core 服务 → 落盘文件）
/// + 三态列，顶部写生成命令与生成日期；三态来自传入仓库树的探测点扫描，换一棵树文本必须跟着变。
/// </summary>
public sealed class DocsCheck_FeatureDocTests
{
    private static readonly DateOnly FixedDate = new(2026, 10, 24);

    /// <summary>块名与顺序只从 feature-blocks.json 读，测试里不再抄一份名单。</summary>
    private static string[] FullNames
        => LoadRepoBlocks()
            .Select(block => string.IsNullOrWhiteSpace(block.Title) ? block.Id : $"{block.Id}（{block.Title}）")
            .ToArray();

    private const string TableHeader = "| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |";

    [Fact]
    public void 生成内容含固定八块且顺序正确()
    {
        using var temp = new TempDirectory();
        var blocks = LoadRepoBlocks();

        var result = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate);

        Assert.True(result.Passed);
        Assert.Equal(FullNames, Headings(result.Markdown));
    }

    [Fact]
    public void 每块都有一张三态表()
    {
        using var temp = new TempDirectory();
        var blocks = LoadRepoBlocks();

        var result = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate);

        Assert.Equal(FullNames.Length, CountOf(result.Markdown, TableHeader));
        // 空树里代码探测点全不成立，8 块都应判「不存在」。
        Assert.Equal(FullNames.Length, CountOf(result.Markdown, "| 不存在 |"));
    }

    [Fact]
    public void 顶部写生成命令与生成日期()
    {
        using var temp = new TempDirectory();
        var blocks = LoadRepoBlocks();

        var result = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate);

        Assert.Contains("本文件由工具生成，请勿手改", result.Markdown);
        // 命令原文钉死：文档顶部的命令必须能直接跑，改命令行就要同步改这里。
        Assert.Contains(FeatureDocGenerator.GenerateCommand, result.Markdown);
        Assert.Contains("2026-10-24", result.Markdown);
        Assert.True(
            result.Markdown.IndexOf("生成命令", StringComparison.Ordinal)
                < result.Markdown.IndexOf("## 1. ", StringComparison.Ordinal));
    }

    [Fact]
    public void 生成命令的构建配置来自环境变量且缺省为Release()
    {
        // -c 不再写死某一台机器的配置：读 NEBULA_BUILD_CONFIG，没设就用 Release。
        Assert.Equal(
            "dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release -- --emit-features docs/FEATURES.md",
            FeatureDocGenerator.GenerateCommandFor(null));
        Assert.Equal(
            "dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Lead -- --emit-features docs/FEATURES.md",
            FeatureDocGenerator.GenerateCommandFor("Lead"));
        Assert.Equal(FeatureDocGenerator.DefaultBuildConfiguration, "Release");
        Assert.Equal("NEBULA_BUILD_CONFIG", FeatureDocGenerator.BuildConfigurationVariable);
        Assert.Contains("-c ", FeatureDocGenerator.GenerateCommand);
    }

    [Fact]
    public void 顶部写三态定义且由生成器渲染()
    {
        using var temp = new TempDirectory();
        var blocks = LoadRepoBlocks();

        var markdown = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate).Markdown;

        var definition = markdown.IndexOf("## 三态定义", StringComparison.Ordinal);
        Assert.True(definition > 0, "缺少三态定义段");
        Assert.True(
            definition > markdown.IndexOf("生成日期", StringComparison.Ordinal),
            "三态定义段应在生成命令与日期之后");
        Assert.Contains("**已实现**：代码探测点与测试探测点都命中", markdown);
        Assert.Contains("**待验证**：代码探测点命中，测试探测点缺失", markdown);
        Assert.Contains("**不存在**：代码探测点未命中", markdown);
    }

    [Fact]
    public void 标题块数按传入数据渲染()
    {
        using var temp = new TempDirectory();
        var blocks = LoadRepoBlocks();
        var subset = blocks.Take(3).ToArray();

        var markdown = FeatureDocGenerator.Generate(temp.Path, subset, FixedDate).Markdown;

        Assert.StartsWith("# 功能完成度（3 块三态）", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("（8 块三态）", markdown);
        Assert.Equal(subset.Select(block => block.Id).ToArray(), Headings(markdown).Select(name => name.Split('（')[0]).ToArray());
    }

    [Fact]
    public void 三态随输入树变化()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Thing.cs", "public sealed class Thing\n{\n}\n");
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock(
                "装得上",
                [new CodeProbe("src/Core/Thing.cs", "Thing")],
                [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")],
                "安装与修复",
                "安装游戏与加载器",
                new FeatureColumns("`src/Core/Thing.cs Thing`", "`src/Core/Thing.cs Thing`", "`src/Core/Thing.cs Thing`", "`thing.json`")),
        };

        Assert.Contains("| 已实现 |", FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate).Markdown);

        File.Delete(Path.Combine(temp.Path, "tests", "ThingTests.cs"));
        var pending = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate).Markdown;
        Assert.Contains("| 待验证 |", pending);
        Assert.DoesNotContain("| 已实现 |", pending);

        File.Delete(Path.Combine(temp.Path, "src", "Core", "Thing.cs"));
        var missing = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate).Markdown;
        Assert.Contains("| 不存在 |", missing);
        Assert.DoesNotContain("| 已实现 |", missing);
    }

    [Fact]
    public void 超过行数预算时报告失败()
    {
        using var temp = new TempDirectory();
        var blocks = LoadRepoBlocks();

        var result = FeatureDocGenerator.Generate(temp.Path, blocks, FixedDate, maxLines: 5);

        Assert.False(result.Passed);
        Assert.True(result.Lines > 5);
        Assert.Contains(result.Violations, violation => violation.Contains("超过上限"));
    }

    [Fact]
    public void 命令行参数解析出输出路径()
    {
        using var temp = new TempDirectory();

        var options = DocCheckOptions.Parse(["--emit-features", "docs/FEATURES.md"], temp.Path);

        Assert.Equal(Path.GetFullPath("docs/FEATURES.md", temp.Path), options.EmitFeatures);
    }

    [Fact]
    public void 真实功能点数据四列齐全()
    {
        var blocks = LoadRepoBlocks();

        Assert.Equal(8, blocks.Count);
        Assert.All(blocks, block =>
        {
            Assert.False(string.IsNullOrWhiteSpace(block.Title));
            Assert.False(string.IsNullOrWhiteSpace(block.Summary));
            Assert.NotNull(block.Columns);
            Assert.All(
                [block.Columns!.Entry, block.Columns.ViewModel, block.Columns.Core, block.Columns.Storage],
                cell => Assert.False(string.IsNullOrWhiteSpace(cell)));
        });
    }

    private static IReadOnlyList<FeatureBlock> LoadRepoBlocks()
        => FeatureBlockCatalog.Load(Path.Combine(RepoRoot(), "tools", "DocsCheck", "feature-blocks.json"));

    private static string[] Headings(string markdown)
        => Regex.Matches(markdown, @"^## \d+\. (?<name>.+)$", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .ToArray();

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
