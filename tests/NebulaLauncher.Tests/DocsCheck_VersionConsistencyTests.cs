using NebulaLauncher.DocsCheck;
using static NebulaLauncher.Tests.DocsCheckTestPaths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本口径漂移（票 #25）：<c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>、
/// <c>CONVENTIONS.md</c> 的发布 tag、<c>docs/PROJECT.md</c> 的版本口径三处必须一致；
/// release notes 必须有四节；三处文档不得留旧口径。
/// </summary>
public sealed class DocsCheck_VersionConsistencyTests
{
    [Fact]
    public void 三处版本一致时通过()
    {
        using var temp = new TempDirectory();
        WriteRepository(temp);

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.True(report.Passed, string.Join('\n', report.Failures));
        Assert.Empty(report.Failures);
        Assert.Equal("1.0.0", report.Version);
        Assert.Equal("v1.0.0", report.ReleaseTag);
    }

    [Fact]
    public void releaseNotes缺节时报出具体缺哪一节()
    {
        using var temp = new TempDirectory();
        WriteRepository(
            temp,
            releaseNotes:
            "# Nebula Launcher v1.0.0\n\n"
            + "## 一、概览\n\n首个正式版。\n\n"
            + "## 二、安装与体积\n\n体积见表。\n\n"
            + "## 四、证据链与签名说明\n\n未签名。\n");

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("docs/release-notes/v1.0.0.md", failure, StringComparison.Ordinal);
        Assert.Contains("已知限制", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void releaseNotes文件不存在时单独报一条()
    {
        using var temp = new TempDirectory();
        WriteRepository(temp);
        File.Delete(Path.Combine(temp.Path, "docs", "release-notes", "v1.0.0.md"));

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("docs/release-notes/v1.0.0.md", failure, StringComparison.Ordinal);
        Assert.Contains("文件不存在", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void 文档残留体积旧口径时失败()
    {
        using var temp = new TempDirectory();
        WriteRepository(temp);
        File.AppendAllText(
            Path.Combine(temp.Path, "docs", "release-notes", "v1.0.0.md"),
            "\n体积以本表为准。\n");

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("docs/release-notes/v1.0.0.md", failure, StringComparison.Ordinal);
        Assert.Contains("体积以本表为准", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void 文档残留轮次号旧口径时失败()
    {
        using var temp = new TempDirectory();
        WriteRepository(temp);
        File.AppendAllText(
            Path.Combine(temp.Path, "docs", "PROJECT.md"),
            "\n旧口径：引用轮次号而不是编。\n");

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("docs/PROJECT.md", failure, StringComparison.Ordinal);
        Assert.Contains("引用轮次号而不是编", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void 发布tag与版本不一致时两个值都报()
    {
        using var temp = new TempDirectory();
        WriteRepository(temp, tag: "v9.9.9");

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("CONVENTIONS.md", failure, StringComparison.Ordinal);
        Assert.Contains("v9.9.9", failure, StringComparison.Ordinal);
        Assert.Contains("1.0.0", failure, StringComparison.Ordinal);
        Assert.Equal("v9.9.9", report.ReleaseTag);
        Assert.Equal("1.0.0", report.Version);
    }

    [Fact]
    public void 项目文档发布号不一致时报出实际值()
    {
        using var temp = new TempDirectory();
        WriteRepository(temp, projectVersion: "0.9.0");

        var report = VersionConsistencyChecker.Check(temp.Path);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("docs/PROJECT.md", failure, StringComparison.Ordinal);
        Assert.Contains("v1.0.0", failure, StringComparison.Ordinal);
        Assert.Contains("v0.9.0", failure, StringComparison.Ordinal);
    }

    /// <summary>造一整套最小仓库文件：版本声明、发布命令、项目口径与四节 release notes。</summary>
    private static void WriteRepository(
        TempDirectory temp,
        string version = "1.0.0",
        string tag = "v1.0.0",
        string projectVersion = "",
        string? releaseNotes = null)
    {
        var project = projectVersion.Length == 0 ? version : projectVersion;

        Write(temp, "Directory.Build.props", $"<Project>\n  <PropertyGroup>\n    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n");
        Write(temp, "CONVENTIONS.md", $"```powershell\ngh release create {tag} --draft --title \"Nebula Launcher {tag}\"\n```\n");
        Write(
            temp,
            "docs/PROJECT.md",
            $"版本号的唯一来源是 `Directory.Build.props` 的 `<Version>{version}</Version>`。\n对外描述版本一律用发布号 `v{project}`。\n");
        Write(temp, $"docs/release-notes/v{version}.md", releaseNotes ?? ReleaseNotes);
    }

    private const string ReleaseNotes =
        "# Nebula Launcher v1.0.0\n\n"
        + "## 一、概览\n\n首个正式版。\n\n"
        + "## 二、安装与体积\n\n体积见表。\n\n"
        + "## 三、已知限制\n\n共 8 条。\n\n"
        + "## 四、证据链与签名说明\n\n未签名。\n";
}
