using NebulaLauncher.DocsCheck;
using static NebulaLauncher.Tests.DocsCheckTestPaths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 测试数统计（票 #8）：扫 <c>tests/NebulaLauncher.Tests/*.cs</c> 顶层，计 Fact 属性数
/// 加 Theory 下每个 InlineData 数，排除 obj / bin。
///
/// 票面记录的口径快照是 1605 Fact + 757 InlineData = 2362（本票新增测试之前）；
/// 本票自己也新增 Fact 测试，所以这里锁 InlineData 精确值、Fact 只锁「不低于基线」的下限。
///
/// 注意：样例文本一律用 <see cref="Attribute(string, string)"/> 拼接，不在测试源码里写字面属性——
/// 扫描按字面匹配，写进去会虚增真实仓库的统计。
/// </summary>
public sealed class DocsCheck_CountTests
{
    /// <summary>票 #8 记录的口径真值（本票新增测试之后、含 DocsCheck 自身的 Fact）。</summary>
    private const int BaselineFacts = 1658;

    /// <summary>
    /// InlineData 的精确基线。改测试时同步这里：票 #27 把「缺版本描述」的两条 Fact
    /// 合并成一条 Theory + 两个 InlineData（两档共用同一份说明），净增 2。
    /// </summary>
    private const int BaselineInlineData = 759;

    [Fact]
    public void 按Fact与InlineData计数()
    {
        using var temp = new TempDirectory();
        Write(temp, "A.cs", Attribute("Fact") + "\npublic void A1() { }\n" + Attribute("Fact") + "\npublic void A2() { }\n");
        Write(
            temp,
            "B.cs",
            Attribute("Theory") + "\n"
            + Attribute("InlineData", "(1)") + "\n"
            + Attribute("InlineData", "(2)") + "\n"
            + Attribute("InlineData", "(3)") + "\n"
            + "public void B1(int value) { }\n");

        var report = TestCountScanner.Scan(temp.Path);

        Assert.Equal(2, report.Facts);
        Assert.Equal(1, report.Theories);
        Assert.Equal(3, report.InlineData);
        Assert.Equal(5, report.TotalTests);
        Assert.Equal(["A.cs", "B.cs"], report.ScannedFiles);
    }

    [Fact]
    public void 排除obj与bin目录()
    {
        using var temp = new TempDirectory();
        Write(temp, "A.cs", Attribute("Fact") + "\npublic void A1() { }\n");
        Write(
            temp,
            "obj/Debug/Generated.cs",
            Attribute("Fact") + "\npublic void G1() { }\n" + Attribute("Fact") + "\npublic void G2() { }\n");
        Write(temp, "bin/Release/Generated.cs", Attribute("Fact") + "\npublic void R1() { }\n");

        var report = TestCountScanner.Scan(temp.Path);

        Assert.Equal(1, report.Facts);
        Assert.Single(report.ScannedFiles);
    }

    [Fact]
    public void 真实仓库的InlineData与基线一致()
    {
        var report = TestCountScanner.Scan(
            Path.Combine(DocsCheckTestPaths.RepoRoot(), "tests", "NebulaLauncher.Tests"));

        Assert.Equal(BaselineInlineData, report.InlineData);
        Assert.True(report.Facts >= BaselineFacts, $"Fact 数 {report.Facts} 低于基线 {BaselineFacts}");
        Assert.Equal(report.Facts + report.InlineData, report.TotalTests);
        Assert.NotEmpty(report.ScannedFiles);
    }

    [Fact]
    public void 空目录计零()
    {
        using var temp = new TempDirectory();

        var report = TestCountScanner.Scan(temp.Path);

        Assert.Equal(0, report.Facts);
        Assert.Equal(0, report.InlineData);
        Assert.Equal(0, report.TotalTests);
        Assert.Empty(report.ScannedFiles);
    }

    /// <summary>拼出属性文本，避免测试源码自己出现字面属性而被算进真实仓库统计。</summary>
    private static string Attribute(string name, string arguments = "") => "[" + name + arguments + "]";
}
