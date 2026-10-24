using System.Text.Json;
using NebulaLauncher.DocsCheck;
using static NebulaLauncher.Tests.DocsCheckTestPaths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 命令行外壳（票 #8）：参数解析、报告文本 / JSON、退出码。
/// 探测点数据用临时树里的副本，判定只看临时树，不依赖真实仓库内容。
/// </summary>
public sealed class DocsCheck_CommandTests
{
    [Fact]
    public void 解析root与docs参数()
    {
        using var temp = new TempDirectory();
        var options = DocCheckOptions.Parse(
            ["--root", "sample-root", "--docs", "docs/A.md", "GLOSSARY.md"],
            temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "sample-root"), options.Root);
        Assert.Equal(["docs/A.md", "GLOSSARY.md"], options.Documents);
        Assert.False(options.Json);
        Assert.False(options.AllowMissing);
        Assert.False(options.Help);
        Assert.Null(options.EmitFeatures);
    }

    [Fact]
    public void 缺省参数是当前目录与固定五份文档()
    {
        using var temp = new TempDirectory();
        var options = DocCheckOptions.Parse([], temp.Path);

        Assert.Equal(temp.Path, options.Root);
        Assert.Equal(
            ["docs/PROJECT.md", "docs/CODE.md", "docs/FEATURES.md", "docs/PITFALLS.md", "GLOSSARY.md"],
            options.Documents);
        Assert.False(options.Json);
    }

    [Fact]
    public void 解析json与允许缺失开关()
    {
        using var temp = new TempDirectory();
        var options = DocCheckOptions.Parse(["--json", "--allow-missing", "--docs", "docs/A.md"], temp.Path);

        Assert.True(options.Json);
        Assert.True(options.AllowMissing);
        Assert.Equal(["docs/A.md"], options.Documents);
    }

    [Fact]
    public void 文档零引用时退出码为零()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/A.md", "# 标题\n\n命令 `dotnet build` 不算引用。\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(0, exitCode);
        Assert.Contains("结果：通过", output);
    }

    [Fact]
    public void 路径不存在时退出码为一并报原因()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/A.md", "见 `src/Missing.cs SomeType`。\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(1, exitCode);
        Assert.Contains("路径不存在", output);
        Assert.Contains("docs/A.md:1", output);
        Assert.Contains("`src/Missing.cs SomeType`", output);
        Assert.Contains("结果：未通过", output);
    }

    [Fact]
    public void 符号不存在时退出码为一()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/A.cs", "namespace X;\n\npublic sealed class A\n{\n}\n");
        Write(temp, "docs/A.md", "见 `src/A.cs A.Missing`。\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(1, exitCode);
        Assert.Contains("符号不存在", output);
    }

    [Fact]
    public void 行数越界时退出码为二()
    {
        using var temp = new TempDirectory();
        var lines = new List<string> { "# 标题" };
        for (var i = 0; i < 400; i++)
        {
            lines.Add("正文");
        }

        Write(temp, "docs/A.md", string.Join('\n', lines) + "\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(2, exitCode);
        Assert.Contains("超过单份上限 400 行", output);
        Assert.Contains("结果：未通过（退出码 2）", output);
    }

    [Fact]
    public void 引用与预算同时出问题时退出码为三()
    {
        using var temp = new TempDirectory();
        var lines = new List<string> { "见 `src/Missing.cs SomeType`。" };
        for (var i = 0; i < 400; i++)
        {
            lines.Add("正文");
        }

        Write(temp, "docs/A.md", string.Join('\n', lines) + "\n");

        var (exitCode, _) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void 必需文档缺失时退出码为一()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/CODE.md", "# 代码\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/CODE.md", "docs/PROJECT.md");

        Assert.Equal(1, exitCode);
        Assert.Contains("缺失（失败）", output);
        Assert.Contains("docs/PROJECT.md", output);
    }

    [Fact]
    public void 允许缺失开关把缺失降级为跳过()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/CODE.md", "# 代码\n");

        var (exitCode, output) = Run(temp, "--allow-missing", "--docs", "docs/CODE.md", "docs/PROJECT.md");

        Assert.Equal(0, exitCode);
        Assert.Contains("缺失（跳过）", output);
    }

    [Fact]
    public void 站内链接目标不存在时退出码为一并与引用分列()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/A.md", "见 [术语](../GLOSSARY.md) 与 [丢了](GONE.md)。\n");
        Write(temp, "GLOSSARY.md", "# 术语表\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(1, exitCode);
        Assert.Contains("【站内链接】", output);
        Assert.Contains("链接目标不存在", output);
        Assert.Contains("docs/A.md:1 [丢了](GONE.md)", output);

        // 链接问题进【站内链接】，不进【文档引用】：两段各管各的。
        var linksSection = output[output.IndexOf("【站内链接】", StringComparison.Ordinal)..];
        var referencesSection = output[output.IndexOf("【文档引用】", StringComparison.Ordinal)..output.IndexOf("【站内链接】", StringComparison.Ordinal)];
        Assert.Contains("链接目标不存在", linksSection);
        Assert.DoesNotContain("链接目标不存在", referencesSection);
    }

    [Fact]
    public void 站内链接目标存在时通过()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/A.md", "见 [术语](../GLOSSARY.md) 与 [自己](#顶部)。\n");
        Write(temp, "GLOSSARY.md", "# 术语表\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(0, exitCode);
        Assert.Contains("链接全部成立", output);
    }

    [Fact]
    public void help打印退出码表且退出码为零()
    {
        using var temp = new TempDirectory();

        var (exitCode, output) = Run(temp, "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains(DocCheckReport.ExitCodeLegend, output);
        Assert.Contains("--emit-features", output);
        Assert.Contains("--allow-missing", output);
        Assert.Contains("--json", output);
    }

    [Fact]
    public void root目录不存在时按参数错误返回()
    {
        using var temp = new TempDirectory();
        var writer = new StringWriter();

        var exitCode = DocCheckCommand.Execute(
            ["--root", Path.Combine(temp.Path, "nowhere"), "--docs", "docs/A.md"],
            temp.Path,
            writer);

        Assert.Equal(DocCheckCommand.UsageError, exitCode);
        Assert.Contains("参数错误", writer.ToString());
    }

    [Fact]
    public void json输出可解析且含各段()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/A.md", "见 `src/Missing.cs`。\n");
        CopyFeatureBlocks(temp);

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md", "--json");

        Assert.Equal(1, exitCode);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.False(root.GetProperty("referencesAndBudgetPassed").GetBoolean());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, root.GetProperty("references").GetProperty("failures").GetArrayLength());
        Assert.Equal("路径不存在", root.GetProperty("references").GetProperty("failures")[0].GetProperty("reason").GetString());
        Assert.True(root.GetProperty("budget").GetProperty("totalLines").GetInt32() > 0);
        Assert.Equal(0, root.GetProperty("links").GetProperty("failures").GetArrayLength());
        Assert.Equal(8, root.GetProperty("features").GetArrayLength());
        Assert.Equal("装得上", root.GetProperty("features")[0].GetProperty("id").GetString());
        Assert.Equal("不存在", root.GetProperty("features")[0].GetProperty("text").GetString());
        Assert.True(root.GetProperty("tests").GetProperty("total").GetInt32() >= 0);
    }

    [Fact]
    public void 文本输出含三态表与测试统计()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/A.md", "# 无引用\n");
        CopyFeatureBlocks(temp);
        var blocks = RepoBlocks();

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(0, exitCode);

        // 块名单只在 feature-blocks.json 里有一份真相，测试不重抄。
        foreach (var block in blocks)
        {
            Assert.Contains(block.Id, output);
        }

        Assert.Contains($"已实现 0 / 待验证 0 / 不存在 {blocks.Count}", output);
        Assert.Contains("测试数", output);
    }

    private static (int ExitCode, string Output) Run(TempDirectory temp, params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = DocCheckCommand.Execute(["--root", temp.Path, .. args], temp.Path, writer);
        return (exitCode, writer.ToString());
    }

    /// <summary>仓库里的探测点数据；块名单与块数都从它读，不在测试里另抄一份。</summary>
    private static IReadOnlyList<FeatureBlock> RepoBlocks()
        => FeatureBlockCatalog.Load(FeatureBlocksPath());

    private static string FeatureBlocksPath()
        => Path.Combine(RepoRoot(), "tools", "DocsCheck", "feature-blocks.json");

    private static void CopyFeatureBlocks(TempDirectory temp)
    {
        var target = Path.Combine(temp.Path, "tools", "DocsCheck", "feature-blocks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(FeatureBlocksPath(), target);
    }
}
