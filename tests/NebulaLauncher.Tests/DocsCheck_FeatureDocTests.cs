using System.Text.RegularExpressions;
using NebulaLauncher.DocsCheck;

namespace NebulaLauncher.Tests;

/// <summary>
/// 功能文档生成（票 #13）：<c>docs/FEATURES.md</c> 必须由工具产出，不是手写。
/// 8 块固定、顺序固定，每块一句用户视角描述 + 四列表（入口页面 → ViewModel → Core 服务 → 落盘文件）
/// + 三态列，顶部写生成命令与生成日期；三态来自传入仓库树的探测点扫描，换一棵树文本必须跟着变。
/// </summary>
public sealed class DocsCheck_FeatureDocTests
{
    private static readonly DateOnly FixedDate = new(2026, 10, 24);

    /// <summary>票面钉死的 8 块完整名，顺序即渲染顺序。</summary>
    private static readonly string[] FullNames =
    [
        "装得上（安装与修复）",
        "下得动（下载与换源）",
        "开得起来（启动与运行时）",
        "管得住内容（模组与整合包）",
        "存得下（实例与世界）",
        "连得上（账户与服务器）",
        "看得见（日志与崩溃）",
        "改得动（设置与数据）",
    ];

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
        Assert.Contains(
            "dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Lead -- --emit-features docs/FEATURES.md",
            result.Markdown);
        Assert.Contains("2026-10-24", result.Markdown);
        Assert.True(
            result.Markdown.IndexOf("生成命令", StringComparison.Ordinal)
                < result.Markdown.IndexOf("## 1. ", StringComparison.Ordinal));
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

        var options = DocCheckCommand.Parse(["--emit-features", "docs/FEATURES.md"], temp.Path);

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

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NebulaLauncher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>在临时树里写文件，自动建父目录。</summary>
    private static string Write(TempDirectory temp, string relativePath, string content)
    {
        var path = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
