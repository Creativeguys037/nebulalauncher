using System.Text.Json;
using NebulaLauncher.DocsCheck;

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
        var options = DocCheckCommand.Parse(
            ["--root", "sample-root", "--docs", "docs/A.md", "GLOSSARY.md"],
            temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "sample-root"), options.Root);
        Assert.Equal(["docs/A.md", "GLOSSARY.md"], options.Documents);
        Assert.False(options.Json);
    }

    [Fact]
    public void 缺省参数是当前目录与固定五份文档()
    {
        using var temp = new TempDirectory();
        var options = DocCheckCommand.Parse([], temp.Path);

        Assert.Equal(temp.Path, options.Root);
        Assert.Equal(
            ["docs/PROJECT.md", "docs/CODE.md", "docs/FEATURES.md", "docs/PITFALLS.md", "GLOSSARY.md"],
            options.Documents);
        Assert.False(options.Json);
    }

    [Fact]
    public void 解析json开关()
    {
        using var temp = new TempDirectory();
        var options = DocCheckCommand.Parse(["--json", "--docs", "docs/A.md"], temp.Path);

        Assert.True(options.Json);
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
    public void 行数越界时退出码为一()
    {
        using var temp = new TempDirectory();
        var lines = new List<string> { "# 标题" };
        for (var i = 0; i < 400; i++)
        {
            lines.Add("正文");
        }

        Write(temp, "docs/A.md", string.Join('\n', lines) + "\n");

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(1, exitCode);
        Assert.Contains("超过单份上限 400 行", output);
    }

    [Fact]
    public void 缺失文档标记为跳过且不失败()
    {
        using var temp = new TempDirectory();

        var (exitCode, output) = Run(temp, "--docs", "docs/Missing.md");

        Assert.Equal(0, exitCode);
        Assert.Contains("缺失（跳过）", output);
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
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(1, root.GetProperty("references").GetProperty("failures").GetArrayLength());
        Assert.Equal("路径不存在", root.GetProperty("references").GetProperty("failures")[0].GetProperty("reason").GetString());
        Assert.True(root.GetProperty("budget").GetProperty("totalLines").GetInt32() > 0);
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

        var (exitCode, output) = Run(temp, "--docs", "docs/A.md");

        Assert.Equal(0, exitCode);
        foreach (var id in new[] { "装得上", "下得动", "开得起来", "管得住内容", "存得下", "连得上", "看得见", "改得动" })
        {
            Assert.Contains(id, output);
        }

        Assert.Contains("已实现 0 / 待验证 0 / 不存在 8", output);
        Assert.Contains("测试数", output);
    }

    private static (int ExitCode, string Output) Run(TempDirectory temp, params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = DocCheckCommand.Execute(["--root", temp.Path, .. args], temp.Path, writer);
        return (exitCode, writer.ToString());
    }

    private static void CopyFeatureBlocks(TempDirectory temp)
    {
        var source = Path.Combine(DocsCheckTestPaths.RepoRoot(), "tools", "DocsCheck", "feature-blocks.json");
        var target = Path.Combine(temp.Path, "tools", "DocsCheck", "feature-blocks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    private static string Write(TempDirectory temp, string relativePath, string content)
    {
        var path = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
