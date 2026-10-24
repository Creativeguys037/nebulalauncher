using System.Collections.Concurrent;
using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载队列的暂停 / 继续。
///
/// 验收对象是「暂停期间会不会偷偷开始新文件」这类调度行为，所以全部用可控的假 handler：
/// 一个 URL 挂起代表「在途文件」，其余 URL 立刻返回，请求次数就是「有没有开始」的直接证据。
/// 真实网络在沙箱里既不可控也不可复现，测不出「暂停后一个请求都没发」。
/// </summary>
public class CoreDownloads_PauseTests
{
    [Fact]
    public async Task 暂停后不再启动新文件_继续后全部完成()
    {
        using var temp = new TempDirectory();
        const string GateUrl = "https://example.invalid/inflight.bin";

        using var handler = new GatedHandler(GateUrl);
        using var client = new HttpClient(handler);

        // 并发 1：保证「在途」只有一个，排队文件确实排在它后面，
        // 否则并发 64 时它们会在暂停之前就把请求发完，测不到任何东西。
        var queue = new DownloadQueue(client, NullAppLog.Instance, maxConcurrency: 1);

        var items = new List<DownloadItem>
        {
            Item(GateUrl, temp.Combine("inflight.bin"), "inflight"),
            Item("https://example.invalid/q1.bin", temp.Combine("q1.bin"), "q1"),
            Item("https://example.invalid/q2.bin", temp.Combine("q2.bin"), "q2"),
            Item("https://example.invalid/q3.bin", temp.Combine("q3.bin"), "q3"),
        };

        var batch = queue.DownloadAsync(items);
        await handler.GateStarted.WaitAsync(TimeSpan.FromSeconds(10));

        queue.Pause();
        Assert.True(queue.IsPaused);

        // 给排队文件足够时间「如果会动的话早就动了」。
        await Task.Delay(250);

        var snapshot = queue.Snapshot();
        Assert.Equal(DownloadTaskState.Running, snapshot.Single(task => task.Id == "inflight").State);
        foreach (var id in new[] { "q1", "q2", "q3" })
        {
            Assert.Equal(DownloadTaskState.Queued, snapshot.Single(task => task.Id == id).State);
            Assert.Equal(0, handler.Hits($"https://example.invalid/{id}.bin"));
        }

        queue.Resume();
        Assert.False(queue.IsPaused);
        handler.Release();

        var result = await batch.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Success);
        Assert.Equal(4, result.CompletedFiles);
        Assert.Equal(4, result.TotalFiles);

        // 继续之后排队文件都真的下了，而不只是状态回滚。
        foreach (var id in new[] { "q1", "q2", "q3" })
        {
            Assert.Equal(1, handler.Hits($"https://example.invalid/{id}.bin"));
            Assert.True(File.Exists(temp.Combine($"{id}.bin")));
        }
    }

    [Fact]
    public async Task 暂停中取消能正常收尾且闸门复位()
    {
        using var temp = new TempDirectory();
        const string GateUrl = "https://example.invalid/inflight.bin";

        using var handler = new GatedHandler(GateUrl);
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance, maxConcurrency: 1);

        var items = new List<DownloadItem>
        {
            Item(GateUrl, temp.Combine("inflight.bin"), "inflight"),
            Item("https://example.invalid/q1.bin", temp.Combine("q1.bin"), "q1"),
        };

        using var cancellation = new CancellationTokenSource();
        var batch = queue.DownloadAsync(items, progress: null, cancellation.Token);
        await handler.GateStarted.WaitAsync(TimeSpan.FromSeconds(10));

        queue.Pause();
        await Task.Delay(150);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await batch.WaitAsync(TimeSpan.FromSeconds(10)));

        // 取消的批次不写历史、不占「活动批次」，暂停态也必须复位——
        // 否则下一次安装会被互斥检查拒掉，下一次下载会永久卡在闸门前。
        Assert.Empty(queue.History);
        Assert.Null(queue.LatestSummary);
        Assert.False(queue.HasActiveBatch);
        Assert.False(queue.IsPaused);

        // 闸门确实放行了：不用再调 Resume，下一批就该跑完。
        var next = await queue
            .DownloadAsync([Item("https://example.invalid/next.bin", temp.Combine("next.bin"), "next")])
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(next.Success);
        Assert.True(File.Exists(temp.Combine("next.bin")));
    }

    [Fact]
    public async Task 新批次开始时暂停态自动复位()
    {
        using var temp = new TempDirectory();
        using var handler = new GatedHandler("https://example.invalid/never.bin");
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        // 无批次时暂停：不抛，但也不会把下一次下载永久卡住。
        queue.Pause();
        Assert.True(queue.IsPaused);
        Assert.False(queue.HasActiveBatch);

        var result = await queue
            .DownloadAsync([Item("https://example.invalid/a.bin", temp.Combine("a.bin"), "a")])
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success);
        Assert.False(queue.IsPaused);
    }

    [Fact]
    public void 重复暂停与继续是幂等的()
    {
        using var handler = new GatedHandler("https://example.invalid/never.bin");
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        queue.Pause();
        queue.Pause();
        Assert.True(queue.IsPaused);

        queue.Resume();
        queue.Resume();
        Assert.False(queue.IsPaused);

        // 再走一轮：闸门在反复开关后仍处于放行态（关闭后没被留下）。
        queue.Pause();
        queue.Resume();
        Assert.False(queue.IsPaused);
    }

    [Fact]
    public void 外部批次不接受暂停()
    {
        using var handler = new GatedHandler("https://example.invalid/never.bin");
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");

        Assert.True(queue.HasActiveBatch);
        Assert.True(queue.IsExternalBatchActive);

        // 安装链自己调度，闸门对它没有意义：Pause 被忽略，界面据 IsExternalBatchActive 置灰按钮。
        queue.Pause();
        Assert.False(queue.IsPaused);

        scope.Complete();

        Assert.False(queue.HasActiveBatch);
        Assert.False(queue.IsExternalBatchActive);
    }

    [Fact]
    public async Task 暂停期间上报的进度带暂停标记()
    {
        using var temp = new TempDirectory();
        const string GateUrl = "https://example.invalid/inflight.bin";

        using var handler = new GatedHandler(GateUrl);
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance, maxConcurrency: 1);

        var progress = new RecordingProgress();
        var items = new List<DownloadItem>
        {
            Item(GateUrl, temp.Combine("inflight.bin"), "inflight"),
            Item("https://example.invalid/q1.bin", temp.Combine("q1.bin"), "q1"),
        };

        var batch = queue.DownloadAsync(items, progress);
        await handler.GateStarted.WaitAsync(TimeSpan.FromSeconds(10));

        queue.Pause();

        // 等过节流间隔（ProgressReporter 的最小上报间隔 100ms），
        // 否则在途文件完成时那次上报会被丢掉，测不到标记。
        await Task.Delay(200);
        handler.Release();

        await WaitUntilAsync(() => progress.Snapshot().Any(item => item.IsPaused), TimeSpan.FromSeconds(5));

        var paused = progress.Snapshot().Where(item => item.IsPaused).ToArray();
        Assert.NotEmpty(paused);
        Assert.All(paused, item => Assert.True(item.IsPaused));

        // 收尾：继续后批次正常结束，暂停标记随之消失。
        queue.Resume();
        var result = await batch.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Success);
        Assert.False(progress.Snapshot()[^1].IsPaused);
    }

    // ---------------- 测试夹具 ----------------

    private static DownloadItem Item(string url, string path, string id) => new()
    {
        Url = url,
        DestinationPath = path,
        Id = id,
        Group = "g",
    };

    /// <summary>轮询等待条件成立；超时交给调用方的断言报错，助手本身不抛。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>按需收集进度回调（同步执行，不依赖 SynchronizationContext）。</summary>
    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        private readonly object _gate = new();
        private readonly List<DownloadProgress> _items = [];

        public void Report(DownloadProgress value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }

        public DownloadProgress[] Snapshot()
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    /// <summary>
    /// 假 handler：<c>gateUrl</c> 的请求会挂起，直到测试调用 <see cref="Release"/>——
    /// 这代表「已在途的文件」，暂停不应打断它。
    /// 其余 URL 立刻返回 200 + 固定字节，并记录每个 URL 的请求次数。
    /// </summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly string _gateUrl;
        private readonly int _size;
        private readonly TaskCompletionSource _gateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

        public GatedHandler(string gateUrl, int size = 64)
        {
            _gateUrl = gateUrl;
            _size = size;
        }

        /// <summary>gate 请求已经到达 handler（即「有文件在途」）。</summary>
        public Task GateStarted => _gateStarted.Task;

        public void Release() => _gateRelease.TrySetResult();

        public int Hits(string url) => _hits.TryGetValue(url, out var count) ? count : 0;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            _hits.AddOrUpdate(url, 1, (_, current) => current + 1);

            if (string.Equals(url, _gateUrl, StringComparison.Ordinal))
            {
                _gateStarted.TrySetResult();

                // 跟随取消令牌：暂停中取消要能让在途请求一起收尾，否则批次永远结束不了。
                await _gateRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var payload = new byte[_size];
            var content = new StreamContent(new MemoryStream(payload, writable: false));
            content.Headers.ContentLength = payload.Length;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}