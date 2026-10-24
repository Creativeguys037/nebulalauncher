using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载页的四档筛选接线：分类结果必须落到「哪一行归哪一档」上。
///
/// 分类器本身由 <see cref="CoreVersions_ChannelClassifierTests"/> 钉住；这里只验 App 侧的映射——
/// 谓词写错（例如把愚人节版也算进「正式版」）在分类器全绿的情况下同样会发生。
///
/// 不触碰任何 Avalonia 类型（见 CONVENTIONS §7.1）。
/// </summary>
public class AppDownloads_ChannelFilterTests
{
    private static readonly DateTimeOffset OrdinaryDay = new(2024, 6, 13, 10, 0, 0, TimeSpan.Zero);

    private static RemoteVersion Make(string id, string type, DateTimeOffset? releaseTime = null) => new()
    {
        Id = id,
        Type = type,
        Url = $"https://example.invalid/{id}.json",
        ReleaseTime = releaseTime ?? OrdinaryDay,
    };

    [Fact]
    public void 愚人节版只落在特殊档()
    {
        var row = new RemoteVersionRow(Make("24w14potato", "snapshot"), isInstalled: false);

        Assert.True(row.IsSpecial);
        Assert.False(row.IsRelease);
        Assert.False(row.IsSnapshot);
        Assert.False(row.IsOld);
        Assert.Equal("愚人节", row.ChannelText);
    }

    [Theory]
    [InlineData("b1.7.3", "old_beta")]
    [InlineData("a1.2.6", "old_alpha")]
    public void 远古两档都算远古(string id, string type)
    {
        var row = new RemoteVersionRow(Make(id, type), isInstalled: false);

        Assert.True(row.IsOld);
        Assert.False(row.IsSpecial);
        Assert.False(row.IsRelease);
    }

    [Fact]
    public void Mojang误标的正式版不落快照档()
    {
        var row = new RemoteVersionRow(Make("1.20.1", "snapshot"), isInstalled: false);

        Assert.True(row.IsRelease);
        Assert.False(row.IsSnapshot);
        Assert.Equal("正式", row.ChannelText);
    }

    [Fact]
    public void 真快照只落快照档()
    {
        var row = new RemoteVersionRow(Make("24w01a", "snapshot"), isInstalled: false);

        Assert.True(row.IsSnapshot);
        Assert.False(row.IsRelease);
        Assert.False(row.IsOld);
        Assert.False(row.IsSpecial);
    }

    [Fact]
    public void 四档筛选参数互不相同()
    {
        // 参数是 View 与 ViewModel 之间的契约：重复会让两个按钮筛出同一个结果。
        var filters = new[] { DownloadViewModel.FilterRelease, DownloadViewModel.FilterSnapshot, DownloadViewModel.FilterOld, DownloadViewModel.FilterSpecial };

        Assert.Equal(4, filters.Distinct(StringComparer.Ordinal).Count());
    }
}