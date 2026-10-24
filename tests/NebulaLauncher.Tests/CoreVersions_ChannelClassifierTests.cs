using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本渠道分类（PCL2 <c>PageDownloadInstall.xaml.vb:452-478</c>）。
///
/// 三条容易写错的判据，各有用例钉住：
/// 1. Mojang 把 1.x 的部分快照标成 <c>snapshot</c>，但它们属于正式版序列——判据是
///    「以 <c>1.</c> 开头且不含 combat / rc / experimental / pre」，顺序不能反；
/// 2. 愚人节版先按 id 白名单、再按 4 月 1 日发布日期兜底，两个来源缺一都会漏；
/// 3. 日期换算用 UTC+2（欧洲时间），不是 UTC——用 UTC 会让 4/1 上午发布的版本落到 3/31。
/// </summary>
public class CoreVersions_ChannelClassifierTests
{
    private static readonly DateTimeOffset OrdinaryDay = new(2023, 8, 2, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("release", "1.20.1")]
    [InlineData("release", "1.0")]
    [InlineData("snapshot", "1.20.1")]      // Mojang 误标
    [InlineData("snapshot", "1.14")]
    [InlineData("snapshot", "1.21.9")]
    public void 正式版_含_Mojang_误标成快照的_1x(string type, string id)
    {
        Assert.Equal(VersionChannel.Release, VersionChannelClassifier.Classify(id, type, OrdinaryDay));
    }

    [Theory]
    [InlineData("1.20.1-pre1")]        // 预发布
    [InlineData("1.20.1-rc1")]         // 候选版
    [InlineData("1.14.4-combat")]      // 战斗测试
    [InlineData("1.19.4-experimental")]// 实验性版本
    [InlineData("24w01a")]             // 普通周更快照
    public void 快照_四类例外不能当正式版(string id)
    {
        Assert.Equal(VersionChannel.Snapshot, VersionChannelClassifier.Classify(id, "snapshot", OrdinaryDay));
    }

    [Theory]
    [InlineData("20w14infinite")]
    [InlineData("20w14∞")]
    [InlineData("3d shareware v1.34")]
    [InlineData("1.RV-Pre1")]
    [InlineData("15w14a")]
    [InlineData("2.0")]
    [InlineData("22w13oneblockatatime")]
    [InlineData("23w13a_or_b")]
    [InlineData("24w14potato")]
    [InlineData("25w14craftmine")]
    [InlineData("26w14a")]
    public void 愚人节版_id_白名单(string id)
    {
        Assert.Equal(VersionChannel.Special, VersionChannelClassifier.Classify(id, "snapshot", OrdinaryDay));
    }

    [Fact]
    public void 愚人节版_按发布日期兜底()
    {
        // 未知 id，但发布日期是 4 月 1 日（UTC 10:00 → UTC+2 的 4/1 12:00）。
        var released = new DateTimeOffset(2020, 4, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(VersionChannel.Special, VersionChannelClassifier.Classify("20w14a", "snapshot", released));
    }

    [Theory]
    // UTC+2 的 4/1 00:00 是边界：UTC 3/31 22:00 刚好落进 4/1，21:59 仍在 3/31。
    [InlineData(22, 0, true)]
    [InlineData(21, 59, false)]
    public void 愚人节版_日期边界用_UTC加2(int hour, int minute, bool isAprilFirst)
    {
        var releaseTime = new DateTimeOffset(2020, 3, 31, hour, minute, 0, TimeSpan.Zero);

        var channel = VersionChannelClassifier.Classify("20w14a", "snapshot", releaseTime);

        Assert.Equal(isAprilFirst ? VersionChannel.Special : VersionChannel.Snapshot, channel);
    }

    [Theory]
    [InlineData("old_beta", VersionChannel.OldBeta)]
    [InlineData("old_alpha", VersionChannel.OldAlpha)]
    [InlineData("special", VersionChannel.Special)]
    public void 其余类型直接映射(string type, VersionChannel expected)
    {
        Assert.Equal(expected, VersionChannelClassifier.Classify("b1.7.3", type, OrdinaryDay));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown_type")]
    public void 未知类型按正式版处理(string type)
    {
        Assert.Equal(VersionChannel.Release, VersionChannelClassifier.Classify("1.20.1", type, OrdinaryDay));
    }

    [Fact]
    public void id_为空的快照不算误标()
    {
        Assert.Equal(VersionChannel.Snapshot, VersionChannelClassifier.Classify(null, "snapshot", OrdinaryDay));
    }

    [Fact]
    public void 清单记录走同一个分类器()
    {
        var potato = new RemoteVersion
        {
            Id = "24w14potato",
            Type = "snapshot",
            Url = "https://example.invalid/24w14potato.json",
            ReleaseTime = new DateTimeOffset(2024, 4, 1, 9, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal(VersionChannel.Special, potato.Channel);
    }
}