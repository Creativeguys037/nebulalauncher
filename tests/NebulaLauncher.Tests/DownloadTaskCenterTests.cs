using System;
using System.Linq;
using NebulaLauncher.Core.Downloads;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 统一任务中心的行为测试。
///
/// 只测「外部可观察的结果」：快照内容、状态迁移、收尾后不再被改。
/// 不测事件节流的具体毫秒数——那依赖墙钟，断言它会变成 flaky。
/// </summary>
public sealed class DownloadTaskCenterTests
{
    [Fact]
    public void Register_PutsTaskInQueuedState()
    {
        var center = new DownloadTaskCenter();

        var handle = center.Register("安装 1.21.1 · Fabric", LauncherTaskKind.Install);

        var snapshot = center.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("安装 1.21.1 · Fabric", snapshot[0].Title);
        Assert.Equal(LauncherTaskKind.Install, snapshot[0].Kind);
        Assert.Equal(LauncherTaskState.Queued, snapshot[0].State);
        Assert.Equal(handle.Id, snapshot[0].Id);
    }

    [Fact]
    public void BeginAndReport_TrackBytesAndRatio()
    {
        var center = new DownloadTaskCenter();
        var handle = center.Register("装载测试", LauncherTaskKind.Install);

        handle.Begin("下载模组…");
        handle.Report(downloadedBytes: 25, totalBytes: 100, speedBytesPerSecond: 10);

        var task = center.Snapshot()[0];
        Assert.Equal(LauncherTaskState.Running, task.State);
        Assert.Equal(25, task.DownloadedBytes);
        Assert.Equal(100, task.TotalBytes);
        Assert.Equal(0.25, task.Ratio, 3);
        Assert.Equal(10, task.SpeedBytesPerSecond);
        Assert.Equal("下载模组…", task.Detail);
    }

    [Fact]
    public void Complete_MarksDoneAndFillsBytes()
    {
        var center = new DownloadTaskCenter();
        var handle = center.Register("备份世界", LauncherTaskKind.Backup);

        handle.Begin();
        handle.Report(30, 100);
        handle.Complete("备份完成");

        var task = center.Snapshot()[0];
        Assert.Equal(LauncherTaskState.Completed, task.State);
        Assert.Equal(100, task.DownloadedBytes);
        Assert.Equal(0, task.SpeedBytesPerSecond);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("备份完成", task.Detail);
    }

    [Fact]
    public void Fail_KeepsErrorAndZeroesSpeed()
    {
        var center = new DownloadTaskCenter();
        var handle = center.Register("安装 NeoForge", LauncherTaskKind.Install);

        handle.Begin();
        handle.Report(5, 100, speedBytesPerSecond: 42);
        handle.Fail("响应 403 Forbidden");

        var task = center.Snapshot()[0];
        Assert.Equal(LauncherTaskState.Failed, task.State);
        Assert.Equal("响应 403 Forbidden", task.Error);
        Assert.Equal(0, task.SpeedBytesPerSecond);
    }

    [Fact]
    public void RequestCancel_WinsOverLateReports()
    {
        var center = new DownloadTaskCenter();
        var handle = center.Register("下载资源包", LauncherTaskKind.Resource);

        handle.Begin();
        center.RequestCancel(handle.Id);
        handle.Report(50, 100);
        handle.Complete();

        var task = center.Snapshot()[0];
        Assert.Equal(LauncherTaskState.Canceled, task.State);
        Assert.Equal(0, task.DownloadedBytes);
    }

    [Fact]
    public void DisposeWithoutFinishing_MarksCanceled()
    {
        var center = new DownloadTaskCenter();
        var handle = center.Register("中途放弃", LauncherTaskKind.Java);

        handle.Begin();
        handle.Dispose();

        Assert.Equal(LauncherTaskState.Canceled, center.Snapshot()[0].State);
    }

    [Fact]
    public void Snapshot_OrdersActiveBeforeFinished()
    {
        var center = new DownloadTaskCenter();

        center.Register("先注册已完成", LauncherTaskKind.Backup).Complete();
        var running = center.Register("后注册进行中", LauncherTaskKind.Install);
        running.Begin();

        var snapshot = center.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("后注册进行中", snapshot[0].Title);
        Assert.Equal("先注册已完成", snapshot[1].Title);
    }

    [Fact]
    public void ClearFinished_KeepsActiveAndFailed()
    {
        var center = new DownloadTaskCenter();

        center.Register("顺利完成", LauncherTaskKind.Backup).Complete();
        center.Register("失败项", LauncherTaskKind.Install).Fail("网络超时");
        var running = center.Register("仍在跑", LauncherTaskKind.Resource);
        running.Begin();

        center.ClearFinished();

        var snapshot = center.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, task => task.Title == "失败项");
        Assert.Contains(snapshot, task => task.Title == "仍在跑");
    }

    [Fact]
    public void FormatBytes_HandlesZeroAndUnits()
    {
        Assert.Equal("0 B", LauncherTaskPresentation.FormatBytes(0));
        Assert.Equal("512 B", LauncherTaskPresentation.FormatBytes(512));
        Assert.Equal("1 KB", LauncherTaskPresentation.FormatBytes(1024));
        Assert.Equal("1.5 MB", LauncherTaskPresentation.FormatBytes((long)(1.5 * 1024 * 1024)));
        Assert.Equal("—", LauncherTaskPresentation.FormatBytes(-1));
    }

    [Fact]
    public void FormatSpeed_ShowsDashWhenIdle()
    {
        Assert.Equal("—", LauncherTaskPresentation.FormatSpeed(0));
        Assert.Equal("—", LauncherTaskPresentation.FormatSpeed(-5));
        Assert.Equal("2 MB/s", LauncherTaskPresentation.FormatSpeed(2 * 1024 * 1024));
    }

    [Fact]
    public void FormatEta_HidesUnknownAndRollsOverHours()
    {
        Assert.Equal("—", LauncherTaskPresentation.FormatEta(null));
        Assert.Equal("—", LauncherTaskPresentation.FormatEta(TimeSpan.Zero));
        Assert.Equal("02:10", LauncherTaskPresentation.FormatEta(TimeSpan.FromSeconds(130)));
        Assert.Equal("1:02:10", LauncherTaskPresentation.FormatEta(TimeSpan.FromSeconds(3730)));
    }

    [Fact]
    public void DescribeDetail_OmitsUnknownTotal()
    {
        Assert.Equal("准备中…", LauncherTaskPresentation.DescribeDetail(null, 0, 0));

        // 总量未知时只说已下字节与条目数，不编一个「/0 B」。
        var unknownTotal = LauncherTaskPresentation.DescribeDetail("下载模组", 1024, 0, items: 12);
        Assert.Equal("下载模组 · 1 KB · 12 项", unknownTotal);

        var knownTotal = LauncherTaskPresentation.DescribeDetail("下载模组", 1024, 2048);
        Assert.Equal("下载模组 · 1 KB/2 KB", knownTotal);
    }

    [Fact]
    public void ActiveCountAndTotalSpeed_IgnoreFinishedTasks()
    {
        var center = new DownloadTaskCenter();

        var first = center.Register("一", LauncherTaskKind.Install);
        first.Begin();
        first.Report(10, 100, speedBytesPerSecond: 1000);

        var second = center.Register("二", LauncherTaskKind.Resource);
        second.Begin();
        second.Report(10, 100, speedBytesPerSecond: 500);
        second.Complete();

        Assert.Equal(1, center.ActiveCount);
        Assert.Equal(1000, center.TotalSpeed);
        Assert.True(center.HasActiveTasks);
    }
}