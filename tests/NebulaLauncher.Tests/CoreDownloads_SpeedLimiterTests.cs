using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载限速与进度曲线。
///
/// 限速用例用可控的假 handler 而不是真实网络：限速的验收对象是「读循环放行了多少字节」，
/// 只有把响应体换成内存流才能让 500ms 这个观察窗口稳定复现（真实网络下块大小和到达节奏都不确定）。
/// </summary>
public class CoreDownloads_SpeedLimiterTests
{
    private const int OneMegabyte = 1024 * 1024;

    [Fact]
    public void KB每秒换成字节每秒()
    {
        Assert.Equal(0L, SpeedLimiter.KbToBytesPerSecond(0));
        Assert.Equal(0L, SpeedLimiter.KbToBytesPerSecond(-5));
        Assert.Equal(102400L, SpeedLimiter.KbToBytesPerSecond(100));
        Assert.Equal(1024L * 1024, SpeedLimiter.KbToBytesPerSecond(1024));
    }

    [Fact]
    public async Task 不限速时令牌桶关闭且不等待()
    {
        var limiter = new SpeedLimiter(0);

        Assert.False(limiter.IsEnabled);
        Assert.Equal(0d, limiter.BytesPerSecond);

        var watch = Stopwatch.StartNew();
        await limiter.AcquireAsync(OneMegabyte, CancellationToken.None);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 200, $"不限速时 Acquire 不应等待，实际 {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task 额度不足时按速率等待()
    {
        // 桶容量是「半秒的量」= 50 KiB：先取空整桶，再要 10 KiB 必须等一个补充周期（100ms）。
        // 用 10 KiB 而不是 50 KiB 是刻意的：周期粒度 100ms，两条语句之间的调度抖动
        // 不足以跨过一个周期，断言不会因为机器快慢而偶发。
        var limiter = new SpeedLimiter(100);
        await limiter.AcquireAsync(51200, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        await limiter.AcquireAsync(10240, CancellationToken.None);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds >= 50, $"10 KiB @100 KiB/s 应等待约 100ms，实际 {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void 进度曲线下端点为005() => Assert.Equal(0.05, DownloadQueue.ApplyProgressCurve(0), 3);

    [Fact]
    public void 进度曲线上端点为098() => Assert.Equal(0.98, DownloadQueue.ApplyProgressCurve(1), 3);

    [Fact]
    public void 进度曲线中段滞后()
    {
        // PCL2 的曲线是「先慢后快」：raw=0.5 只显示约 48%，最后一段实际进度迅速填满进度条。
        // 早期判据写成「raw=0.5 必须大于 0.5」是按反了方向，已按 PCL2 原式改正。
        var middle = DownloadQueue.ApplyProgressCurve(0.5);

        Assert.True(middle < 0.5, $"raw=0.5 应滞后于实际比例（先慢后快），实际 {middle}");
        Assert.True(middle > 0.05, $"raw=0.5 不应落到展示下限，实际 {middle}");
    }

    [Fact]
    public void 进度曲线夹住越界与非法输入()
    {
        Assert.Equal(0.05, DownloadQueue.ApplyProgressCurve(-1), 3);
        Assert.Equal(0.05, DownloadQueue.ApplyProgressCurve(double.NaN), 3);
        Assert.Equal(0.98, DownloadQueue.ApplyProgressCurve(2), 3);
    }

    [Fact]
    public void 进度曲线单调不减()
    {
        var previous = 0d;
        for (var raw = 0.0; raw <= 1.0; raw += 0.05)
        {
            var value = DownloadQueue.ApplyProgressCurve(raw);

            Assert.True(value >= previous, $"曲线在 raw={raw} 处回退（{previous} → {value}）");
            Assert.InRange(value, 0.05, 0.98);
            previous = value;
        }
    }

    [Fact]
    public async Task 限速100KB时500毫秒内下载不超过150KB()
    {
        using var temp = new TempDirectory();
        var handler = new CountingHandler(OneMegabyte);
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance) { SpeedLimitKb = 100 };

        var target = Path.Combine(temp.Path, "limited.bin");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        long servedAtCancel = -1;
        using var registration = cancellation.Token.Register(
            () => Interlocked.Exchange(ref servedAtCancel, handler.BytesServed));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.DownloadAsync(
                [new DownloadItem
                {
                    Url = "https://example.invalid/limited.bin",
                    DestinationPath = target,
                    ExpectedSize = OneMegabyte,
                }],
                progress: null,
                cancellation.Token));

        var served = handler.BytesServed;
        Assert.True(
            served <= 150 * 1024,
            $"500 ms 内读出了 {served} 字节（取消时 {servedAtCancel}），超过 150 KiB 上限；读取序列：{string.Join(" ", handler.ReadLog)}");
    }

    [Fact]
    public async Task 限速为0时1MB文件快速完成()
    {
        using var temp = new TempDirectory();
        var handler = new CountingHandler(OneMegabyte);
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);

        Assert.Equal(0, queue.SpeedLimitKb);

        var target = Path.Combine(temp.Path, "fast.bin");
        var watch = Stopwatch.StartNew();
        var result = await queue.DownloadAsync(
            [new DownloadItem
            {
                Url = "https://example.invalid/fast.bin",
                DestinationPath = target,
                ExpectedSize = OneMegabyte,
            }]);
        watch.Stop();

        Assert.True(result.Success);
        Assert.Equal(1, result.CompletedFiles);
        Assert.Equal(OneMegabyte, new FileInfo(target).Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"不限速下载 1 MiB 用了 {watch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task 进度上报走曲线且完成时补满()
    {
        using var temp = new TempDirectory();
        var handler = new CountingHandler(256 * 1024);
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(client, NullAppLog.Instance);
        var progress = new RecordingProgress();

        var target = Path.Combine(temp.Path, "progress.bin");
        var result = await queue.DownloadAsync(
            [new DownloadItem
            {
                Url = "https://example.invalid/progress.bin",
                DestinationPath = target,
                ExpectedSize = 256 * 1024,
            }],
            progress);

        Assert.True(result.Success);
        Assert.NotEmpty(progress.Items);

        // 起始进度留在曲线的下端点，任何时刻都不越过上端点。
        Assert.Equal(0.05, progress.Items[0].Ratio, 3);
        Assert.All(progress.Items, item => Assert.InRange(item.Ratio, 0.05, 1.0));

        // 批次收尾时补满，进度条不会永远停在 98%。
        Assert.Equal(1.0, progress.Items[^1].Ratio, 3);
        Assert.Equal(1, progress.Items[^1].CompletedFiles);

        // 曲线只改展示比例，不改字节计数。
        Assert.Equal(256 * 1024, progress.Items[^1].DownloadedBytes);
    }

    [Fact]
    public void 负的限速值被夹到0()
    {
        using var client = new HttpClient(new CountingHandler(16));
        var queue = new DownloadQueue(client);
        queue.SpeedLimitKb = -7;

        Assert.Equal(0, queue.SpeedLimitKb);
    }

    /// <summary>同步收集进度，避免 <see cref="Progress{T}"/> 的线程池回调把断言变成竞态。</summary>
    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = [];

        public void Report(DownloadProgress value)
        {
            lock (Items)
            {
                Items.Add(value);
            }
        }
    }

    /// <summary>只在内存里产响应体的 handler，顺带统计「被读走了多少字节」。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        private long _bytesServed;

        public CountingHandler(int size)
        {
            _payload = new byte[size];
            for (var i = 0; i < size; i++)
            {
                _payload[i] = (byte)(i % 251);
            }
        }

        public long BytesServed => Interlocked.Read(ref _bytesServed);

        public ConcurrentQueue<string> ReadLog { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var stream = new CountingStream(_payload, read =>
            {
                Interlocked.Add(ref _bytesServed, read);
                ReadLog.Enqueue($"{watch.ElapsedMilliseconds}ms:+{read}");
            });
            var content = new StreamContent(stream);
            content.Headers.ContentLength = _payload.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>
    /// 读多少记多少的只读流。
    ///
    /// 只重写 <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>：下载读循环走的是这个重载。
    /// 同时重写同步的 <c>Read(Span&lt;byte&gt;)</c> 会双重计数（<see cref="MemoryStream"/> 的 ReadAsync 内部就转到它）。
    /// </summary>
    private sealed class CountingStream(byte[] payload, Action<int> onRead) : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            CountAsync(base.ReadAsync(buffer, cancellationToken));

        private async ValueTask<int> CountAsync(ValueTask<int> pending)
        {
            var read = await pending.ConfigureAwait(false);
            onRead(read);
            return read;
        }
    }
}
