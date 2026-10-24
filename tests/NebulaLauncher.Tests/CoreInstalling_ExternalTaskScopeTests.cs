using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 安装链的逐文件进度接入任务面板：外部批次的快照、状态迁移、历史、互斥与失败项重试。
///
/// 这里的「外部批次」就是 <c>NebulaGameInstaller</c> 手里的 <see cref="IDownloadTaskScope"/>：
/// 它不经过 <see cref="DownloadQueue.DownloadAsync"/>（安装器自己调度 8 个下载线程），
/// 但上报后必须与普通批次对同一套快照 / 历史 / 失败项生效。
///
/// 真实 3000 文件安装在本机沙箱没有稳定下载源，所以用两条可复现路径替代：
/// 直接驱动作用域（状态机与计数），以及假 <c>HttpMessageHandler</c>（互斥与普通批次恢复）。
/// </summary>
public class CoreInstalling_ExternalTaskScopeTests
{
    [Fact]
    public void 外部批次的条目出现在快照并完成状态迁移()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        Assert.Empty(queue.Snapshot());

        scope.Report("1.20.1.json", "versions", 1024, 0, DownloadTaskState.Queued);

        var queued = Assert.Single(queue.Snapshot());
        Assert.Equal("1.20.1.json", queued.Name);
        Assert.Equal("versions", queued.Group);
        Assert.Equal(DownloadTaskState.Queued, queued.State);
        Assert.Equal(1024, queued.TotalBytes);

        scope.Report("1.20.1.json", "versions", 1024, 256, DownloadTaskState.Running);

        var running = Assert.Single(queue.Snapshot());
        Assert.Equal(DownloadTaskState.Running, running.State);
        Assert.Equal(256, running.DownloadedBytes);
        Assert.Null(running.Error);

        scope.Report("1.20.1.json", "versions", 1024, 1024, DownloadTaskState.Completed);

        var done = Assert.Single(queue.Snapshot());
        Assert.Equal(DownloadTaskState.Completed, done.State);
        Assert.Equal(1024, done.DownloadedBytes);
        Assert.Equal(1.0, done.Ratio, 3);
        Assert.True(done.IsFinished);

        scope.Complete();
    }

    [Fact]
    public void 完成的外部批次写进历史并保留失败文件()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);
        var failedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nebula-bad.jar");

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("ok.jar", "libraries", 100, 100, DownloadTaskState.Completed);
        scope.Report(
            "bad.jar",
            "libraries",
            200,
            0,
            DownloadTaskState.Failed,
            "403 Forbidden",
            "https://example.invalid/bad.jar",
            failedPath);

        // 失败先出现在活动快照里，且能重建成「只重试这一项」的下载请求。
        var live = Assert.Single(queue.FailedTasks);
        Assert.Equal("403 Forbidden", live.Error);
        Assert.Single(DownloadQueue.BuildRetryItems(queue.FailedTasks));

        scope.Complete();

        var summary = Assert.IsType<DownloadBatchSummary>(queue.LatestSummary);
        Assert.Equal("安装 1.20.1", summary.Name);
        Assert.Equal(2, summary.TotalFiles);
        Assert.Equal(1, summary.CompletedFiles);
        Assert.Equal(1, summary.FailedFiles);
        Assert.Equal(300, summary.TotalBytes);
        Assert.Equal(100, summary.DownloadedBytes);
        Assert.True(summary.IsClosed);
        Assert.False(summary.Success);

        var failedTask = Assert.Single(summary.FailedTasks);
        Assert.Equal("bad.jar", failedTask.Name);
        Assert.Equal("https://example.invalid/bad.jar", failedTask.Url);
        Assert.Equal(failedPath, failedTask.DestinationPath);

        var history = Assert.Single(queue.History);
        Assert.Same(summary, history);

        // 批次收尾后 FailedTasks 回退到最近摘要，重试入口仍然可用。
        Assert.Single(queue.FailedTasks);
    }

    [Fact]
    public void 未完成就Dispose视为取消不写历史且清空快照()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("a.jar", "libraries", 10, 0, DownloadTaskState.Running);
        Assert.Single(queue.Snapshot());

        scope.Dispose();

        Assert.Empty(queue.History);
        Assert.Null(queue.LatestSummary);
        Assert.Empty(queue.Snapshot());
        Assert.Empty(queue.FailedTasks);

        // Dispose 之后的迟到上报不会把已取消的批次复活。
        scope.Report("b.jar", "libraries", 10, 10, DownloadTaskState.Completed);
        Assert.Empty(queue.Snapshot());

        // 重复 Dispose / Complete 都是空操作。
        scope.Dispose();
        scope.Complete();
        Assert.Empty(queue.History);
    }

    [Fact]
    public void 并发两个外部批次被拒绝_前一个收尾后恢复可用()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var first = queue.BeginExternalBatch("安装 A");
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var second = queue.BeginExternalBatch("安装 B");
        });

        first.Dispose();

        using var third = queue.BeginExternalBatch("安装 C");
        third.Report("a.jar", "libraries", 1, 0, DownloadTaskState.Queued);
        Assert.Single(queue.Snapshot());
    }

    [Fact]
    public async Task 外部批次活动时普通下载被拒绝_收尾后恢复()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var item = new DownloadItem
        {
            Url = "https://example.invalid/a.bin",
            DestinationPath = temp.Combine("a.bin"),
            Id = "a",
            Group = "assets",
        };

        using var scope = queue.BeginExternalBatch("安装 1.20.1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DownloadAsync([item]));

        scope.Complete();

        // 外部批次收尾后普通批次恢复正常；外部批次的那条历史仍保留在前面。
        var result = await queue.DownloadAsync([item]);
        Assert.True(result.Success);
        Assert.Equal(2, queue.History.Count);
    }

    [Fact]
    public async Task 普通批次活动时外部批次被拒绝()
    {
        using var temp = new TempDirectory();
        var handler = new GatedHandler();
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var running = queue.DownloadAsync(
        [
            new DownloadItem
            {
                Url = "https://example.invalid/slow.bin",
                DestinationPath = temp.Combine("slow.bin"),
                Id = "slow",
                Group = "assets",
            },
        ]);

        await WaitUntilAsync(() => queue.Snapshot().Count > 0);

        Assert.Throws<InvalidOperationException>(() => queue.BeginExternalBatch("安装 1.20.1"));

        handler.Release();
        Assert.True((await running).Success);

        // 普通批次正常结束后，外部批次可以开始。
        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task 取消的普通批次不阻塞后续外部批次()
    {
        using var client = new HttpClient(new SlowHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queue.DownloadAsync(
                [
                    new DownloadItem
                    {
                        Url = "https://example.invalid/slow.bin",
                        DestinationPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nebula-slow.bin"),
                        Id = "slow",
                        Group = "assets",
                    },
                ],
                cancellationToken: cancellation.Token));

        Assert.Empty(queue.History);

        // 取消路径必须把 tracker 标记为收尾，否则安装会被互斥检查永久挡住。
        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("a.jar", "libraries", 1, 0, DownloadTaskState.Queued);
        Assert.Single(queue.Snapshot());
    }

    [Fact]
    public void 重复上报同一条目是更新不是新增()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("a.jar", "libraries", 1024, 0, DownloadTaskState.Queued);
        scope.Report("a.jar", "libraries", 1024, 512, DownloadTaskState.Running);
        scope.Report("a.jar", "libraries", 1024, 1024, DownloadTaskState.Completed);

        var task = Assert.Single(queue.Snapshot());
        Assert.Equal(DownloadTaskState.Completed, task.State);
        Assert.Equal(1024, task.DownloadedBytes);
        Assert.Equal(1024, task.TotalBytes);
    }

    [Fact]
    public void 空外部批次收尾也写一条零计数历史()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Complete();

        var summary = Assert.IsType<DownloadBatchSummary>(queue.LatestSummary);
        Assert.Equal(0, summary.TotalFiles);
        Assert.True(summary.Success);
        Assert.True(summary.IsClosed);
    }

    [Fact]
    public void 收尾会强制推一次任务快照事件()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var events = 0;
        queue.TasksChanged += (_, _) => Interlocked.Increment(ref events);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("a.jar", "libraries", 10, 0, DownloadTaskState.Queued);
        scope.Complete();

        // 开始（强制）+ 收尾（强制）：Report 在节流窗口内可能被丢弃，不做断言。
        Assert.True(Volatile.Read(ref events) >= 2, $"事件数 {Volatile.Read(ref events)}");
    }

    [Fact]
    public async Task 并发上报三千个文件不丢条目且计数一致()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");

        // 安装器的固定并发是 8；每个线程负责 375 个文件，模拟「排队后完成」两次上报。
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = worker; i < 3000; i += 8)
            {
                var name = $"assets/{i:D5}";
                scope.Report(name, "assets", 100, 0, DownloadTaskState.Queued);
                scope.Report(name, "assets", 100, 100, DownloadTaskState.Completed);
            }
        })));

        scope.Complete();

        var summary = Assert.IsType<DownloadBatchSummary>(queue.LatestSummary);
        Assert.Equal(3000, summary.TotalFiles);
        Assert.Equal(3000, summary.CompletedFiles);
        Assert.Equal(0, summary.SkippedFiles);
        Assert.Equal(0, summary.FailedFiles);
        Assert.Equal(300_000, summary.TotalBytes);
        Assert.True(summary.IsClosed);
        Assert.True(summary.Success);

        // 快照上限是 200，已完成的只留最近 24 条；这里验证截断上限与状态。
        var snapshot = queue.Snapshot();
        Assert.True(snapshot.Count <= DownloadQueue.MaxSnapshotTasks, $"快照 {snapshot.Count} 条");
        Assert.All(snapshot, task => Assert.Equal(DownloadTaskState.Completed, task.State));
    }

    // ---------------- 测试夹具 ----------------

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待条件超时");
    }

    /// <summary>所有请求返回 200 + 固定 16 字节：只需要让普通批次能跑完。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StreamContent(new MemoryStream(new byte[16], writable: false));
            content.Headers.ContentLength = 16;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>卡在响应之前的 handler：用来稳定复现「普通批次正在跑」这个状态。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var content = new StreamContent(new MemoryStream(new byte[16], writable: false));
            content.Headers.ContentLength = 16;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>永不响应的 handler：只用来触发取消路径。</summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
