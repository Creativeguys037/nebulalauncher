using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>模型层的最小自检，确保序列化契约没被改坏。</summary>
public class ModelSmokeTests
{
    [Fact]
    public void AppSettings_Defaults_AreSane()
    {
        var settings = new AppSettings();

        Assert.Equal(4096, settings.MaxMemoryMb);
        Assert.True(settings.MinMemoryMb <= settings.MaxMemoryMb);
        Assert.Equal("dark", settings.Theme);
        Assert.True(settings.EnableAnimations);
        Assert.Equal(DownloadSource.BmclApi, settings.DownloadSource);
    }

    [Fact]
    public void RemoteVersion_Channel_MapsFromTypeString()
    {
        Assert.Equal(VersionChannel.Release, Make("release").Channel);

        // 快照用例必须用一个真快照 id：<c>1.20.1</c> 被标成 snapshot 属于 Mojang 误分类，
        // 会被 VersionChannelClassifier 纠正回正式版（PCL2 PageDownloadInstall.xaml.vb:457-463）。
        Assert.Equal(VersionChannel.Snapshot, Make("snapshot", "24w01a").Channel);
        Assert.Equal(VersionChannel.Release, Make("snapshot", "1.20.1").Channel);

        Assert.Equal(VersionChannel.OldBeta, Make("old_beta").Channel);
        Assert.Equal(VersionChannel.OldAlpha, Make("old_alpha").Channel);
        Assert.Equal(VersionChannel.Release, Make("whatever").Channel);
    }

    [Fact]
    public void DownloadProgress_RatioAndEta_ClampCorrectly()
    {
        // 速度必须 > 1024 B/s 才给出 ETA —— 低于这个量级算出来的剩余时间没有参考价值。
        var p = new DownloadProgress(10, 5, 0, 10000, 5000, 2000, "a.jar", 0.5);
        Assert.Equal(0.5, p.Ratio);
        Assert.Equal(2.5, p.Eta!.Value.TotalSeconds);

        var done = new DownloadProgress(10, 10, 0, 1000, 1000, 100, null, 1);
        Assert.Null(done.Eta);

        // 速度过低同样不给 ETA。
        var slow = new DownloadProgress(10, 1, 0, 10000, 1000, 512, "b.jar", 0.1);
        Assert.Null(slow.Eta);
    }

    private static RemoteVersion Make(string type, string id = "1.20.1") => new()
    {
        Id = id,
        Type = type,
        Url = "https://example.invalid/v.json",
        ReleaseTime = DateTimeOffset.UnixEpoch,
    };
}
