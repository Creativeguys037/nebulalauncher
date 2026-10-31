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
    /// InlineData 的精确基线。改测试时同步这里。
    /// 票 #27 记录两次变化：把「缺版本描述」的两条 Fact 合并成一条 Theory + 两个
    /// InlineData（两档共用同一份说明），净增 2；随后为「工厂把 maxParallel 交给
    /// 安装器」补了一条 4 个 InlineData 的 Theory，净增 4。
    /// 票 #28 把账户类型文案收敛成共享映射，新增 10 个 InlineData
    /// （三态映射表 3 + API 根非法或缺失 4 + 账户列表行 3），净增 10。
    /// 票 #40 给六方向纯色探针加了 6 个 InlineData（正/背/左/右/顶/底各一行），净增 6。
    /// 票 #48 给皮肤头像服务加了 7 个 InlineData（皮肤 PNG 两种历史尺寸 2 + 模型归一化 5），净增 7。
    /// 票 #51 给窗口最小尺寸的测量兜底加了 3 个 InlineData（NaN / +∞ / 负数各一行），净增 3。
    /// 票 #44 给官方换肤的失败态映射加了 3 个 InlineData（429 / 500 / 503 各一行），净增 3。
    /// 票 #43 给皮肤站换肤的 model 取值与失败态映射加了 9 个 InlineData，净增 9。
    /// 票 #54 给纹理落盘缓存加了 8 个 InlineData（哈希取值规则 4 + 分区名归一 4），净增 8。
    ///
    /// 票 #48 的 7 条 InlineData 曾经只落在工作树里、没有进任何提交，master 上扫不到，
    /// 所以 782 → 794 这段基线不含它们。现在那批测试文件（CoreAccounts_SkinAvatarTests.cs）
    /// 已随 WIP 提交真正落地，基线再加回这 7 条，为 801；加上票 #54 的 8 条，为 809。
    /// </summary>
    private const int BaselineInlineData = 809;

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
