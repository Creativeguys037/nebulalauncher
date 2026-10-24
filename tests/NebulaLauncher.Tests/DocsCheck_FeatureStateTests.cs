using NebulaLauncher.DocsCheck;

namespace NebulaLauncher.Tests;

/// <summary>
/// 三态扫描（票 #8）：功能点 = 一组代码探测点 + 一组测试探测点。
/// 代码与测试探测点全部成立 → 已实现；只有代码 → 待验证；代码都不成立 → 不存在。
///
/// 探测点数据放在 <c>tools/DocsCheck/feature-blocks.json</c>，测试里用临时树换输入，
/// 判定必须跟着输入树变——这正是「三态扫描」区别于一次性清单的地方。
/// </summary>
public sealed class DocsCheck_FeatureStateTests
{
    private static readonly string[] FixedNames =
    [
        "装得上",
        "下得动",
        "开得起来",
        "管得住内容",
        "存得下",
        "连得上",
        "看得见",
        "改得动",
    ];

    [Fact]
    public void 代码与测试探测点都在时判为已实现()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Thing.cs", "public sealed class Thing\n{\n    public void Run() { }\n}\n");
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock(
                "装得上",
                [new CodeProbe("src/Core/Thing.cs", "Thing.Run")],
                [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")]),
        };

        var report = FeatureStateScanner.Scan(temp.Path, blocks);

        var block = Assert.Single(report.Blocks);
        Assert.Equal("装得上", block.Id);
        Assert.Equal(FeatureState.Implemented, block.State);
        Assert.True(block.CodePresent);
        Assert.True(block.TestPresent);
        Assert.Empty(block.MissingCodeProbes);
        Assert.Empty(block.MissingTestProbes);
    }

    [Fact]
    public void 只有代码探测点时判为待验证()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Thing.cs", "public sealed class Thing\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock("装得上", [new CodeProbe("src/Core/Thing.cs", "Thing")], []),
        };

        var report = FeatureStateScanner.Scan(temp.Path, blocks);

        var block = Assert.Single(report.Blocks);
        Assert.Equal(FeatureState.Pending, block.State);
        Assert.True(block.CodePresent);
        Assert.False(block.TestPresent);
    }

    [Fact]
    public void 代码探测点都不在时判为不存在()
    {
        using var temp = new TempDirectory();
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock(
                "装得上",
                [new CodeProbe("src/Core/Gone.cs", "Gone")],
                [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")]),
        };

        var report = FeatureStateScanner.Scan(temp.Path, blocks);

        var block = Assert.Single(report.Blocks);
        Assert.Equal(FeatureState.Missing, block.State);
        Assert.False(block.CodePresent);
        Assert.Single(block.MissingCodeProbes);
    }

    [Fact]
    public void 判定随输入树变化()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Thing.cs", "public sealed class Thing\n{\n}\n");
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock(
                "装得上",
                [new CodeProbe("src/Core/Thing.cs", "Thing")],
                [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")]),
        };

        Assert.Equal(FeatureState.Implemented, FeatureStateScanner.Scan(temp.Path, blocks).Blocks[0].State);

        File.Delete(Path.Combine(temp.Path, "tests", "ThingTests.cs"));
        Assert.Equal(FeatureState.Pending, FeatureStateScanner.Scan(temp.Path, blocks).Blocks[0].State);

        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");
        Assert.Equal(FeatureState.Implemented, FeatureStateScanner.Scan(temp.Path, blocks).Blocks[0].State);
    }

    [Fact]
    public void 部分代码探测点缺失时记录缺失项并降级()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Thing.cs", "public sealed class Thing\n{\n}\n");
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock(
                "装得上",
                [new CodeProbe("src/Core/Thing.cs", "Thing"), new CodeProbe("src/Core/Gone.cs", "Gone")],
                [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")]),
        };

        var block = Assert.Single(FeatureStateScanner.Scan(temp.Path, blocks).Blocks);

        Assert.Equal(FeatureState.Missing, block.State);
        var missing = Assert.Single(block.MissingCodeProbes);
        Assert.Equal("src/Core/Gone.cs", missing.Path);
    }

    [Fact]
    public void 探测点符号写错时判为缺失()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Thing.cs", "public sealed class Thing\n{\n}\n");
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock(
                "装得上",
                [new CodeProbe("src/Core/Thing.cs", "Thing.Renamed")],
                [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")]),
        };

        var block = Assert.Single(FeatureStateScanner.Scan(temp.Path, blocks).Blocks);

        Assert.Equal(FeatureState.Missing, block.State);
        Assert.Equal("Thing.Renamed", Assert.Single(block.MissingCodeProbes).Symbol);
    }

    [Fact]
    public void 没有代码探测点的块判为不存在()
    {
        using var temp = new TempDirectory();
        Write(temp, "tests/ThingTests.cs", "public sealed class DocsCheck_ThingTests\n{\n}\n");

        var blocks = new[]
        {
            new FeatureBlock("装得上", [], [new CodeProbe("tests/ThingTests.cs", "DocsCheck_ThingTests")]),
        };

        var block = Assert.Single(FeatureStateScanner.Scan(temp.Path, blocks).Blocks);

        Assert.Equal(FeatureState.Missing, block.State);
        Assert.False(block.CodePresent);
    }

    [Fact]
    public void 从json加载探测点数据()
    {
        using var temp = new TempDirectory();
        Write(temp, "feature-blocks.json",
            """
            {
              "blocks": [
                {
                  "id": "装得上",
                  "code": [ { "path": "src/Core/Thing.cs", "symbol": "Thing" } ],
                  "tests": [ { "path": "tests/ThingTests.cs" } ]
                },
                {
                  "id": "看得见",
                  "code": [ { "path": "src/App/View.axaml" } ],
                  "tests": []
                }
              ]
            }
            """);

        var blocks = FeatureBlockCatalog.Load(Path.Combine(temp.Path, "feature-blocks.json"));

        Assert.Equal(2, blocks.Count);
        Assert.Equal("装得上", blocks[0].Id);
        var code = Assert.Single(blocks[0].CodeProbes);
        Assert.Equal("src/Core/Thing.cs", code.Path);
        Assert.Equal("Thing", code.Symbol);
        Assert.Null(Assert.Single(blocks[1].CodeProbes).Symbol);
        Assert.Empty(blocks[1].TestProbes);
    }

    [Fact]
    public void 仓库里的探测点数据含固定八块()
    {
        var catalogPath = Path.Combine(RepoRoot(), "tools", "DocsCheck", "feature-blocks.json");

        var blocks = FeatureBlockCatalog.Load(catalogPath);

        Assert.Equal(FixedNames, blocks.Select(block => block.Id));
        Assert.All(blocks, block => Assert.InRange(block.CodeProbes.Count, 5, 8));
        Assert.All(blocks, block => Assert.NotEmpty(block.TestProbes));
    }

    [Fact]
    public void 状态文本是三种中文说法()
    {
        Assert.Equal("已实现", FeatureStates.Text(FeatureState.Implemented));
        Assert.Equal("待验证", FeatureStates.Text(FeatureState.Pending));
        Assert.Equal("不存在", FeatureStates.Text(FeatureState.Missing));
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
