using System.Collections.Concurrent;
using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载任务面板的 Core 侧：逐文件快照、批次历史、失败项重试、事件节流。
///
/// 全部用内存假 handler：任务面板的验收对象是「每个文件的状态与失败原因有没有被正确记录」，
/// 与网络速度无关；真实 3000 文件安装在本机沙箱里也没有可用的下载源。
/// </summary>
public class CoreDownloads_TaskSnapshotTests
{
    private static readonly string[] ExpectedGroupOrder = ["A", "A", "B", "B"];

    private static readonly string[] ExpectedTaskIdOrder = ["a1", "a3", "b1", "b2"];

    private static readonly string[] ExpectedSnapshotGroupOrder = ["assets", "libraries"];

    [Fact]
    public void 快照按分组与声明顺序排列()
    {
        var tasks = new List<DownloadTaskInfo>
        {
            MakeTask("b2", "B", sequence: 2),
            MakeTask("a1", "A", sequence: 1),
            MakeTask("a3", "A", sequence: 3),
            MakeTask("b1", "B", sequence: 0),
        };

        var ordered = DownloadQueue.OrderTasks(tasks, finishedRetention: 10, maxTasks: 0);

        Assert.Equal(ExpectedGroupOrder, ordered.Select(task => task.Group));
        Assert.Equal(ExpectedTaskIdOrder, ordered.Select(task => task.Id));
    }

    [Fact]
    public void 已结束项只保留最近的N条()
    {
        var tasks = new List<DownloadTaskInfo>();
        for (var i = 0; i < 100; i++)
        {
            var task = MakeTask($"done-{i}", "g", i);
            task.State = DownloadTaskState.Completed;
            tasks.Add(task);
        }

        tasks.Add(MakeTask("running", "g", 200, DownloadTaskState.Running));
        tasks.Add(MakeTask("failed", "g", 201, DownloadTaskState.Failed));

        var ordered = DownloadQueue.OrderTasks(tasks, finishedRetention: 24, maxTasks: 0);

        // 2 条未结束 + 最近 24 条已完成。
        Assert.Equal(26, ordered.Count);
        Assert.Contains(ordered, task => task.Id == "running");
        Assert.Contains(ordered, task => task.Id == "failed");
        Assert.Contains(ordered, task => task.Id == "done-99");
        Assert.DoesNotContain(ordered, task => task.Id == "done-75");
    }

    [Fact]
    public void 超过总条数上限时失败项优先保留()
    {
        var tasks = new List<DownloadTaskInfo>();
        for (var i = 0; i < 300; i++)
        {
            tasks.Add(MakeTask($"queued-{i}", "g", i));
        }

        for (var i = 0; i < 5; i++)
        {
            tasks.Add(MakeTask($"failed-{i}", "g", 1000 + i, DownloadTaskState.Failed));
        }

        var ordered = DownloadQueue.OrderTasks(tasks, finishedRetention: 24, maxTasks: 50);

        Assert.Equal(50, ordered.Count);
        Assert.Equal(5, ordered.Count(task => task.IsFailed));
        Assert.Equal(45, ordered.Count(task => task.State == DownloadTaskState.Queued));
    }

    [Fact]
    public void 状态文案与百分比覆盖全部状态()
    {
        Assert.Equal("排队", DownloadTaskPresentation.DescribeState(DownloadTaskState.Queued));
        Assert.Equal("下载中", DownloadTaskPresentation.DescribeState(DownloadTaskState.Running));
        Assert.Equal("完成", DownloadTaskPresentation.DescribeState(DownloadTaskState.Completed));
        Assert.Equal("失败", DownloadTaskPresentation.DescribeState(DownloadTaskState.Failed));
        Assert.Equal("跳过", DownloadTaskPresentation.DescribeState(DownloadTaskState.Skipped));

        var half = MakeTask("half", "g", 0, DownloadTaskState.Running);
        half.TotalBytes = 1000;
        half.DownloadedBytes = 500;
        Assert.Equal("50%", DownloadTaskPresentation.DescribePercent(half));

        var unknown = MakeTask("unknown", "g", 0, DownloadTaskState.Running);
        Assert.Equal("—", DownloadTaskPresentation.DescribePercent(unknown));

        var done = MakeTask("done", "g", 0, DownloadTaskState.Completed);
        Assert.Equal("100%", DownloadTaskPresentation.DescribePercent(done));
    }

    [Fact]
    public void 节流判定按最小间隔放行()
    {
        // 首次上报（last = -1）无条件放行。
        Assert.True(DownloadQueue.ShouldRaiseTasksChanged(-1, 1000, force: false));

        // 间隔内丢弃。
        Assert.False(DownloadQueue.ShouldRaiseTasksChanged(1000, 1100, force: false));
        Assert.False(DownloadQueue.ShouldRaiseTasksChanged(1000, 1199, force: false));

        // 到点放行。
        Assert.True(DownloadQueue.ShouldRaiseTasksChanged(1000, 1200, force: false));

        // 强制上报忽略间隔。
        Assert.True(DownloadQueue.ShouldRaiseTasksChanged(1000, 1000, force: true));

        // 时钟回退时按到期处理，避免永远不再上报。
        Assert.True(DownloadQueue.ShouldRaiseTasksChanged(1000, 900, force: false));
    }

    [Fact]
    public void 失败项重试入队判定()
    {
        var noUrl = MakeTask("no-url", "g", 1, DownloadTaskState.Failed);

        var failed = MakeTask(
            "bad", "g", 2, DownloadTaskState.Failed,
            url: "https://example.invalid/bad.bin",
            path: "/tmp/bad.bin",
            totalBytes: 4096,
            sha1: "ABCD");

        var duplicate = MakeTask(
            "bad-dup", "g", 3, DownloadTaskState.Failed,
            url: "https://example.invalid/bad.bin",
            path: "/tmp/bad.bin");

        var ok = MakeTask(
            "ok", "g", 4, DownloadTaskState.Completed,
            url: "https://example.invalid/ok.bin",
            path: "/tmp/ok.bin");

        var items = DownloadQueue.BuildRetryItems([noUrl, failed, duplicate, ok]);

        var item = Assert.Single(items);
        Assert.Equal("https://example.invalid/bad.bin", item.Url);
        Assert.Equal("/tmp/bad.bin", item.DestinationPath);
        Assert.Equal(4096, item.ExpectedSize);
        Assert.Equal("ABCD", item.ExpectedSha1);
    }

    [Fact]
    public async Task 真实批次快照记录成功与失败文件()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new RouteHandler(
            ("https://example.invalid/good.bin", 200, 512),
            ("https://example.invalid/bad.bin", 403, 0)));
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var good = Path.Combine(temp.Path, "good.bin");
        var bad = Path.Combine(temp.Path, "bad.bin");

        var result = await queue.DownloadAsync(
        [
            Item("https://example.invalid/good.bin", good, "assets/objects/aa", "assets"),
            Item("https://example.invalid/bad.bin", bad, "libraries/bad.jar", "libraries"),
        ]);

        Assert.False(result.Success);
        Assert.Equal(1, result.CompletedFiles);
        Assert.Equal(1, result.FailedFiles);

        var snapshot = queue.Snapshot();
        Assert.Equal(2, snapshot.Count);

        // 分组在前（assets < libraries，Ordinal 比较）。
        Assert.Equal(ExpectedSnapshotGroupOrder, snapshot.Select(task => task.Group));

        var done = snapshot[0];
        Assert.Equal(DownloadTaskState.Completed, done.State);
        Assert.Equal(512, done.DownloadedBytes);
        Assert.Equal(good, done.DestinationPath);

        var failed = snapshot[1];
        Assert.Equal(DownloadTaskState.Failed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.Equal("https://example.invalid/bad.bin", failed.Url);
        Assert.Equal(bad, failed.DestinationPath);

        // 失败项可从快照直接重建重试请求。
        var retry = DownloadQueue.BuildRetryItems(queue.FailedTasks);
        Assert.Single(retry);
    }

    [Fact]
    public async Task 批次摘要计数与耗时和返回值一致()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new RouteHandler(
            ("https://example.invalid/new.bin", 200, 256),
            ("https://example.invalid/bad.bin", 403, 0)));
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var newFile = Path.Combine(temp.Path, "new.bin");
        var existing = Path.Combine(temp.Path, "existing.bin");
        var badFile = Path.Combine(temp.Path, "bad.bin");

        // 已存在且大小一致的视为跳过。
        await File.WriteAllBytesAsync(existing, new byte[128]);

        var result = await queue.DownloadAsync(
        [
            Item("https://example.invalid/new.bin", newFile, "assets/new", "assets"),
            Item("https://example.invalid/existing.bin", existing, "assets/existing", "assets", expectedSize: 128),
            Item("https://example.invalid/bad.bin", badFile, "libraries/bad", "libraries"),
        ]);

        var summary = queue.LatestSummary;
        Assert.NotNull(summary);
        Assert.Equal("assets", summary.Name);
        Assert.Equal(result.TotalFiles, summary.TotalFiles);
        Assert.Equal(result.CompletedFiles, summary.CompletedFiles);
        Assert.Equal(result.SkippedFiles, summary.SkippedFiles);
        Assert.Equal(result.FailedFiles, summary.FailedFiles);
        Assert.Equal(result.TotalBytes, summary.TotalBytes);
        Assert.Equal(result.DownloadedBytes, summary.DownloadedBytes);
        Assert.True(summary.IsClosed);
        Assert.True(summary.Duration >= TimeSpan.Zero);
        Assert.Single(summary.FailedTasks);
        Assert.Equal("https://example.invalid/bad.bin", summary.FailedTasks[0].Url);

        var history = queue.History;
        Assert.Single(history);
        Assert.Same(summary, history[0]);
    }

    [Fact]
    public async Task 历史最多保留20条且最新在前()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new RouteHandler(
            Enumerable.Range(0, 25)
                .Select(i => ($"https://example.invalid/f{i}.bin", 200, 16))
                .ToArray()));
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        for (var i = 0; i < 25; i++)
        {
            await queue.DownloadAsync(
            [
                Item($"https://example.invalid/f{i}.bin", Path.Combine(temp.Path, $"f{i}.bin"), $"g{i}", $"g{i}", expectedSize: 16),
            ]);
        }

        var history = queue.History;
        Assert.Equal(DownloadQueue.HistoryCapacity, history.Count);
        Assert.Equal("g24", history[0].Name);
        Assert.Equal("g5", history[^1].Name);
    }

    [Fact]
    public async Task 重试只下载失败项不重跑整批()
    {
        using var temp = new TempDirectory();

        // 首次批次的 3 次尝试全部 403，之后的请求成功。队列自身最多重试 3 次，
        // 所以失败项必须跨批次重试才会成功——这正是「重试失败项」要验证的路径。
        var handler = new FlakyHandler("https://example.invalid/bad.bin", 403, 200, 256, failTimes: 3);
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var good = Path.Combine(temp.Path, "good.bin");
        var bad = Path.Combine(temp.Path, "bad.bin");

        var first = await queue.DownloadAsync(
        [
            Item("https://example.invalid/good.bin", good, "good", "g", expectedSize: 64),
            Item("https://example.invalid/bad.bin", bad, "bad", "g", expectedSize: 256),
        ]);

        Assert.Equal(1, first.FailedFiles);

        Assert.True(
            handler.Hits("https://example.invalid/good.bin") == 1,
            $"good 应只请求 1 次，实际 {handler.Hits("https://example.invalid/good.bin")}；"
            + $"completed={first.CompletedFiles} skipped={first.SkippedFiles} failed={first.FailedFiles}");

        var retryItems = DownloadQueue.BuildRetryItems(queue.FailedTasks);
        Assert.Single(retryItems);

        var retried = await queue.DownloadAsync(retryItems);

        Assert.True(retried.Success);
        Assert.True(File.Exists(bad));
        Assert.Equal(256, new FileInfo(bad).Length);

        // 成功文件没有被重新请求——重试的是子批次，不是整个安装。
        Assert.Equal(1, handler.Hits("https://example.invalid/good.bin"));
    }

    [Fact]
    public async Task 事件被节流且最后一次快照是完成态()
    {
        using var temp = new TempDirectory();

        // 4 MiB / 80 KiB 每块 = 52 次读；如果每块都推事件，UI 线程会被打爆。
        const int payload = 4 * 1024 * 1024;
        using var client = new HttpClient(new RouteHandler(("https://example.invalid/big.bin", 200, payload)));
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var events = new List<DownloadTasksChangedEventArgs>();
        queue.TasksChanged += (_, args) =>
        {
            lock (events)
            {
                events.Add(args);
            }
        };

        var target = Path.Combine(temp.Path, "big.bin");
        var result = await queue.DownloadAsync(
        [
            Item("https://example.invalid/big.bin", target, "big", "g", expectedSize: payload),
        ]);

        Assert.True(result.Success);

        lock (events)
        {
            Assert.InRange(events.Count, 1, 51);
            Assert.Contains(events, args => args.Tasks.Any(task => task.State == DownloadTaskState.Completed));
            Assert.NotNull(events[^1].Summary);
            Assert.True(events[^1].Summary!.Success);
        }
    }

    [Fact]
    public async Task 取消的批次不写历史()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new SlowHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.DownloadAsync(
            [
                Item("https://example.invalid/slow.bin", Path.Combine(temp.Path, "slow.bin"), "slow", "g"),
            ],
                progress: null,
                cancellation.Token));

        Assert.Empty(queue.History);
        Assert.Null(queue.LatestSummary);
    }

    [Fact]
    public async Task 空清单不产生历史与事件()
    {
        using var client = new HttpClient(new RouteHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        var events = 0;
        queue.TasksChanged += (_, _) => Interlocked.Increment(ref events);

        var result = await queue.DownloadAsync([]);

        Assert.True(result.Success);
        Assert.Empty(queue.Snapshot());
        Assert.Empty(queue.History);
        Assert.Null(queue.LatestSummary);
        Assert.Equal(0, Volatile.Read(ref events));
    }

    // ---------------- 测试夹具 ----------------

    private static DownloadTaskInfo MakeTask(
        string id,
        string group,
        int sequence,
        DownloadTaskState state = DownloadTaskState.Queued,
        string? url = null,
        string? path = null,
        long totalBytes = 0,
        string? sha1 = null) => new()
        {
            Id = id,
            Name = id,
            Group = group,
            Sequence = sequence,
            State = state,
            Url = url,
            DestinationPath = path,
            TotalBytes = totalBytes,
            Sha1 = sha1,
        };

    private static DownloadItem Item(
        string url,
        string path,
        string id,
        string group,
        long expectedSize = 0) => new()
        {
            Url = url,
            DestinationPath = path,
            Id = id,
            Group = group,
            ExpectedSize = expectedSize,
        };

    /// <summary>按 URL 路由的内存响应：非 200 直接返回状态码，200 返回指定长度的零字节流。</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, int Size)> _routes;
        private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

        public RouteHandler(params (string Url, int Status, int Size)[] routes)
        {
            _routes = new Dictionary<string, (HttpStatusCode, int)>(StringComparer.Ordinal);
            foreach (var (url, status, size) in routes)
            {
                _routes[url] = ((HttpStatusCode)status, size);
            }
        }

        public int Hits(string url) => _hits.TryGetValue(url, out var count) ? count : 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            _hits.AddOrUpdate(url, 1, (_, current) => current + 1);

            if (!_routes.TryGetValue(url, out var route))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (route.Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(route.Status));
            }

            var payload = new byte[route.Size];
            var content = new StreamContent(new MemoryStream(payload, writable: false));
            content.Headers.ContentLength = payload.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>前 <c>failTimes</c> 次返回失败状态码、之后返回成功的 handler。</summary>
    private sealed class FlakyHandler : HttpMessageHandler
    {
        private readonly string _flakyUrl;
        private readonly HttpStatusCode _firstStatus;
        private readonly HttpStatusCode _thenStatus;
        private readonly int _size;
        private readonly int _failTimes;
        private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

        public FlakyHandler(string flakyUrl, int firstStatus, int thenStatus, int size, int failTimes = 1)
        {
            _flakyUrl = flakyUrl;
            _firstStatus = (HttpStatusCode)firstStatus;
            _thenStatus = (HttpStatusCode)thenStatus;
            _size = size;
            _failTimes = Math.Max(1, failTimes);
        }

        public int Hits(string url) => _hits.TryGetValue(url, out var count) ? count : 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var hit = _hits.AddOrUpdate(url, 1, (_, current) => current + 1);

            if (!string.Equals(url, _flakyUrl, StringComparison.Ordinal))
            {
                return Task.FromResult(Success(64));
            }

            return Task.FromResult(hit <= _failTimes
                ? new HttpResponseMessage(_firstStatus)
                : Success(_size));
        }

        private HttpResponseMessage Success(int size)
        {
            var payload = new byte[size];
            var content = new StreamContent(new MemoryStream(payload, writable: false));
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(_thenStatus) { Content = content };
        }
    }

    /// <summary>永不响应的 handler：只用来验证「取消时不写历史」。</summary>
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
