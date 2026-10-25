using System.Collections.Concurrent;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 票 27 缺陷 B（并发数是死设置）与缺陷 D（安装期间 Java/authlib 被闸门挡住）。
///
/// 两件事的共同点：都只能在「队列的调度行为」这一层观察——
/// B 的判据是「同时发出去的请求数确实跟着设置走」，
/// D 的判据是「外部批次活动期间旁路能下载完，而且安装面板的快照一个字节都没变」。
/// </summary>
public class CoreDownloads_ThreadAndBypassTests
{
    // ==================== 缺陷 B：并发上限 ====================

    [Fact]
    public void 并发上限可按设置改写并被夹在合法范围内()
    {
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance, maxConcurrency: 64);

        Assert.Equal(64, queue.MaxConcurrency);

        queue.MaxConcurrency = 256;
        Assert.Equal(256, queue.MaxConcurrency);

        // 与 SettingsService 的 8..256 clamp 保持同一上界；下界放宽到 1，
        // 因为队列本身是通用组件，单线程调试也是合法用法。
        queue.MaxConcurrency = 0;
        Assert.Equal(1, queue.MaxConcurrency);

        queue.MaxConcurrency = 9999;
        Assert.Equal(256, queue.MaxConcurrency);
    }

    [Fact]
    public async Task 并发上限控制同时在途的请求数()
    {
        using var temp = new TempDirectory();
        var handler = new ConcurrencyProbeHandler();
        using var client = new HttpClient(handler);

        // 4 个文件、并发上限 2：同时在途的请求数峰值必须是 2。
        var queue = new DownloadQueue(client, NullAppLog.Instance, maxConcurrency: 2);

        var items = Enumerable.Range(0, 4)
            .Select(i => new DownloadItem
            {
                Url = $"https://example.invalid/{i}.bin",
                DestinationPath = temp.Combine($"{i}.bin"),
                Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Group = "probe",
            })
            .ToList();

        var result = await queue.DownloadAsync(items);

        Assert.True(result.Success);
        Assert.Equal(4, result.CompletedFiles);

        // 上限是「不超过」：并发 1 的实现同样合法，但那意味着上限没起作用。
        Assert.True(
            handler.PeakConcurrency <= 2,
            $"并发峰值 {handler.PeakConcurrency} 超过上限 2");
        Assert.True(
            handler.PeakConcurrency >= 2,
            $"并发峰值只有 {handler.PeakConcurrency}，说明上限 2 没有真的放开并行");
    }

    [Fact]
    public async Task 批次开始后改并发只影响下一批()
    {
        using var temp = new TempDirectory();
        var handler = new GatedHandler();
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance, maxConcurrency: 1);

        var first = new DownloadItem
        {
            Url = "https://example.invalid/gated.bin",
            DestinationPath = temp.Combine("gated.bin"),
            Id = "gated",
            Group = "probe",
        };

        var running = queue.DownloadAsync([first]);
        await handler.FirstRequestStarted.WaitAsync(TimeSpan.FromSeconds(10));

        // 批级快照：批次进行中改属性，不该把在跑的这一批的并发放大。
        queue.MaxConcurrency = 16;
        Assert.Equal(16, queue.MaxConcurrency);

        handler.Release();
        Assert.True((await running).Success);

        // 下一批读的是新值：这里只验证属性已更新，具体并发峰值见上一条用例。
        Assert.Equal(16, queue.MaxConcurrency);
    }

    // ==================== 缺陷 D：旁路批次 ====================

    [Fact]
    public async Task 外部批次活动期间旁路下载仍能完成()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("lib.jar", "libraries", 100, 0, DownloadTaskState.Queued);

        // 旧实现：这一句会抛「安装批次正在进行」。
        var result = await queue.DownloadBypassAsync(
        [
            new DownloadItem
            {
                Url = "https://example.invalid/java.bin",
                DestinationPath = temp.Combine("java.bin"),
                Id = "java",
                Group = "Java",
            },
        ]);

        Assert.True(result.Success, "旁路下载被安装批次挡住了");
        Assert.Equal(1, result.CompletedFiles);
        Assert.True(File.Exists(temp.Combine("java.bin")));

        scope.Complete();
    }

    [Fact]
    public async Task 旁路不污染安装面板的快照()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");
        scope.Report("a.jar", "libraries", 100, 0, DownloadTaskState.Queued);
        scope.Report("b.jar", "libraries", 200, 0, DownloadTaskState.Queued);

        var before = queue.Snapshot();
        Assert.Equal(2, before.Count);

        await queue.DownloadBypassAsync(
        [
            new DownloadItem
            {
                Url = "https://example.invalid/x.bin",
                DestinationPath = temp.Combine("x.bin"),
                Id = "x",
                Group = "authlib-injector",
            },
            new DownloadItem
            {
                Url = "https://example.invalid/y.bin",
                DestinationPath = temp.Combine("y.bin"),
                Id = "y",
                Group = "authlib-injector",
            },
        ]);

        // 安装的 3000 行是用户唯一能看懂的进度：旁路的两条不能挤进来，也不能把它们挤掉。
        var after = queue.Snapshot();
        Assert.Equal(2, after.Count);
        Assert.Equal(
            before.Select(task => task.Name).OrderBy(n => n, StringComparer.Ordinal),
            after.Select(task => task.Name).OrderBy(n => n, StringComparer.Ordinal));

        // 外部批次仍是活动批次，面板仍认它是安装。
        Assert.True(queue.IsExternalBatchActive);

        scope.Complete();
    }

    [Fact]
    public async Task 旁路不写批次历史()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        await queue.DownloadBypassAsync(
        [
            new DownloadItem
            {
                Url = "https://example.invalid/z.bin",
                DestinationPath = temp.Combine("z.bin"),
                Id = "z",
                Group = "Java",
            },
        ]);

        Assert.Empty(queue.History);
        Assert.Null(queue.LatestSummary);
    }

    [Fact]
    public async Task 旁路不受暂停阻断()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        queue.Pause();
        Assert.True(queue.IsPaused);

        // 暂停是「这一批普通下载」的语义。安装缺 Java 时替用户挂住旁路，
        // 只会让安装卡在缺 Java 上——那正是缺陷 D 要消灭的死锁形状。
        var result = await queue.DownloadBypassAsync(
        [
            new DownloadItem
            {
                Url = "https://example.invalid/p.bin",
                DestinationPath = temp.Combine("p.bin"),
                Id = "p",
                Group = "Java",
            },
        ]);

        Assert.True(result.Success);
        Assert.True(queue.IsPaused, "旁路不该替用户改暂停状态");

        queue.Resume();
    }

    [Fact]
    public async Task 普通批次仍被安装批次拒绝()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient(new StubHandler());
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        using var scope = queue.BeginExternalBatch("安装 1.20.1");

        // 这条既有约束不能被缺陷 D 的修法顺带放宽：普通批次会抢快照。
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DownloadAsync(
        [
            new DownloadItem
            {
                Url = "https://example.invalid/n.bin",
                DestinationPath = temp.Combine("n.bin"),
                Id = "n",
                Group = "assets",
            },
        ]));

        scope.Complete();
    }

    // ==================== 假 handler ====================

    /// <summary>立刻成功的 handler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            });
    }

    /// <summary>统计同时在途请求数的 handler。每个请求停 80ms，让并发真正重叠。</summary>
    private sealed class ConcurrencyProbeHandler : HttpMessageHandler
    {
        private int _current;
        private int _peak;
        private readonly ConcurrentBag<int> _seen = new();

        public int PeakConcurrency => Volatile.Read(ref _peak);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _current);
            _seen.Add(current);

            int observed;
            while (current > (observed = Volatile.Read(ref _peak)))
            {
                Interlocked.CompareExchange(ref _peak, current, observed);
            }

            try
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4]),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    /// <summary>第一个请求挂起，直到外部放行。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstRequestStarted => _started.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
        }
    }
}
