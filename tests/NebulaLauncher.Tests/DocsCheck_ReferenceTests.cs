using NebulaLauncher.DocsCheck;
using static NebulaLauncher.Tests.DocsCheckTestPaths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 引用契约（票 #8 定死，后续 4 份文档按它写）：
/// 一个反引号单元 = 仓库相对路径 + 若干 <c>Type</c> / <c>Type.Member</c> 符号；
/// 行号只是提示后缀，不作定位依据。
///
/// 这组测试锁的是「哪些反引号单元算引用」与「算引用之后怎么判成立」：
/// 只按第一个 token 是否为仓库相对路径判定，命令、配置键、用户数据路径一律不算引用。
/// </summary>
public sealed class DocsCheck_ReferenceTests
{
    [Fact]
    public void 路径与符号都存在时引用成立()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/App/Foo.cs", "public sealed class Foo\n{\n    public void Bar() { }\n}\n");
        Write(temp, "docs/PROJECT.md", "见 `src/App/Foo.cs:2 Foo.Bar`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);

        Assert.Empty(report.Failures);
        var reference = Assert.Single(report.References);
        Assert.Equal("docs/PROJECT.md", reference.Document);
        Assert.Equal(1, reference.Line);
        Assert.Equal("src/App/Foo.cs", reference.Path);
        Assert.Equal("2", reference.LineHint);
        var symbol = Assert.Single(reference.Symbols);
        Assert.Equal("Foo.Bar", symbol);
    }

    [Fact]
    public void 路径不存在时报告路径不存在()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/PROJECT.md", "第一行\n见 `src/App/Missing.cs Foo.Bar`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);

        Assert.Empty(report.References);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("docs/PROJECT.md", failure.Document);
        Assert.Equal(2, failure.Line);
        Assert.Equal("src/App/Missing.cs Foo.Bar", failure.Unit);
        Assert.Equal(DocReferenceFailure.PathMissing, failure.Reason);
    }

    [Fact]
    public void 路径存在但符号改名后报告符号不存在()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/App/Foo.cs", "public sealed class Foo\n{\n    public void Renamed() { }\n}\n");
        Write(temp, "docs/CODE.md", "`src/App/Foo.cs Foo.Bar`\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        var failure = Assert.Single(report.Failures);
        Assert.Equal("src/App/Foo.cs", failure.Path);
        Assert.Equal(DocReferenceFailure.SymbolMissing, failure.Reason);
    }

    [Fact]
    public void 类型名在文件里完全不出现时报告符号不存在()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/App/Foo.cs", "public sealed class Other\n{\n}\n");
        Write(temp, "docs/CODE.md", "`src/App/Foo.cs Foo`\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        var failure = Assert.Single(report.Failures);
        Assert.Equal(DocReferenceFailure.SymbolMissing, failure.Reason);
    }

    [Fact]
    public void 符号只出现在使用点而未在该文件声明时引用成立()
    {
        // 文档引用的是使用点：StartupState 的声明在别的文件，被引文件里只有调用。
        using var temp = new TempDirectory();
        Write(temp, "src/App/App.axaml.cs", "AppBuilder.Configure<App>();\nStartupState.LoadBeforeFirstFrame(Services);\n");
        Write(temp, "docs/PITFALLS.md", "见 `src/App/App.axaml.cs:56 StartupState`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PITFALLS.md"]);

        Assert.True(report.Passed, string.Join("；", report.Failures.Select(f => f.Reason)));
    }

    [Fact]
    public void 多段符号按段判定且非源码文件同样匹配()
    {
        // csproj 里引的是包名 CmlLib.Core，没有类型声明，也不该要求声明。
        using var temp = new TempDirectory();
        Write(
            temp,
            "src/Core/Core.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"CmlLib.Core\" Version=\"4.1.0\" />\n  </ItemGroup>\n</Project>\n");
        Write(temp, "docs/PROJECT.md", "见 `src/Core/Core.csproj:3 CmlLib.Core`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);

        Assert.True(report.Passed, string.Join("；", report.Failures.Select(f => f.Reason)));

        Write(temp, "docs/PROJECT.md", "见 `src/Core/Core.csproj:3 CmlLib.Other`。\n");
        var renamed = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);
        Assert.Equal(DocReferenceFailure.SymbolMissing, Assert.Single(renamed.Failures).Reason);
    }

    [Fact]
    public void 同一行多个引用逐个判定()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/App/Foo.cs", "public sealed class Foo\n{\n}\n");
        Write(temp, "docs/CODE.md", "`src/App/Foo.cs Foo` 与 `src/App/Gone.cs Foo`\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        Assert.Single(report.References);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("src/App/Gone.cs Foo", failure.Unit);
        Assert.Equal(DocReferenceFailure.PathMissing, failure.Reason);
    }

    [Fact]
    public void 文档不含任何引用时通过()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/PROJECT.md", "# 项目\n\n纯叙述，没有路径引用。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);

        Assert.Empty(report.References);
        Assert.Empty(report.Failures);
        Assert.True(report.Passed);
        Assert.Equal(["docs/PROJECT.md"], report.ScannedDocuments);
        Assert.Empty(report.MissingDocuments);
    }

    [Fact]
    public void 命令配置键与用户数据路径不算引用()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/PROJECT.md",
            "`dotnet build -c Lead`\n" +
            "`ParallelDownloads`\n" +
            "`%APPDATA%\\NebulaLauncher`\n" +
            "`settings.json`\n");
        Write(temp, "settings.json", "{}");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);

        Assert.Empty(report.References);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void 以md结尾且存在的文件算引用而不存在的跳过()
    {
        using var temp = new TempDirectory();
        Write(temp, "GLOSSARY.md", "# 术语表\n");
        Write(temp, "docs/PROJECT.md", "`GLOSSARY.md` 与 `MISSING.md`\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md"]);

        var reference = Assert.Single(report.References);
        Assert.Equal("GLOSSARY.md", reference.Path);
        Assert.Empty(reference.Symbols);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void 允许只有路径没有符号()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/research/code-map.md", "# 代码地图\n");
        Write(temp, "docs/CODE.md", "`docs/research/code-map.md`\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        var reference = Assert.Single(report.References);
        Assert.Equal("docs/research/code-map.md", reference.Path);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void 缺失文档默认算失败()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/PROJECT.md", "`src/App/Foo.cs`\n");
        Write(temp, "src/App/Foo.cs", "public sealed class Foo { }\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md"]);

        Assert.Empty(report.Failures);
        Assert.False(report.Passed);
        Assert.False(report.AllowMissing);
        Assert.Equal(["docs/CODE.md"], report.MissingDocuments);
        Assert.Equal(["docs/PROJECT.md"], report.ScannedDocuments);
    }

    [Fact]
    public void 允许缺失时缺失文档不算失败()
    {
        using var temp = new TempDirectory();
        Write(temp, "docs/PROJECT.md", "`src/App/Foo.cs`\n");
        Write(temp, "src/App/Foo.cs", "public sealed class Foo { }\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/PROJECT.md", "docs/CODE.md"], allowMissing: true);

        Assert.Empty(report.Failures);
        Assert.True(report.Passed);
        Assert.True(report.AllowMissing);
        Assert.Equal(["docs/CODE.md"], report.MissingDocuments);
    }

    [Fact]
    public void 行号区间提示后缀不影响路径判定()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/App/Foo.cs", "public sealed class Foo { }\n");
        Write(temp, "docs/CODE.md", "`src/App/Foo.cs:10-24 Foo`\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        var reference = Assert.Single(report.References);
        Assert.Equal("10-24", reference.LineHint);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void 目录引用存在时成立()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Inner.cs", "namespace X;\n");
        Write(temp, "docs/CODE.md", "见 `src/Core/`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        Assert.True(report.Passed);
        var reference = Assert.Single(report.References);
        Assert.Equal("src/Core/", reference.Path);
    }

    [Fact]
    public void 目录引用带符号时报告符号不存在()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/Core/Inner.cs", "namespace X;\n");
        Write(temp, "docs/CODE.md", "见 `src/Core/ SomeType`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        Assert.False(report.Passed);
        Assert.Equal(DocReferenceFailure.SymbolMissing, Assert.Single(report.Failures).Reason);
    }

    [Fact]
    public void 符号带括号时按名字判定()
    {
        using var temp = new TempDirectory();
        Write(temp, "src/App/Foo.cs", "public sealed class Foo\n{\n    public void Run() { }\n}\n");
        Write(temp, "docs/CODE.md", "见 `src/App/Foo.cs Foo.Run()`。\n");

        var report = DocReferenceChecker.Check(temp.Path, ["docs/CODE.md"]);

        Assert.True(report.Passed, string.Join("；", report.Failures.Select(f => f.Reason)));
    }
}
