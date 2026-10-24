using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 缺口 R29-3：下载源按「拿响应头超时」换源。
///
/// 这些用例全部离线：<see cref="MirrorRewritingHandler"/> 的内层 handler 被换成
/// <see cref="StubHandler"/>，超时阈值被调到几十毫秒，所以既能验证换源时序，
/// 又不会让整套用例等上 5 秒 / 30 秒。
///
/// 三个必须钉住的语义：
/// <list type="bullet">
/// <item>首选源挂住 → 阈值到点换下一个候选，而不是等到 HttpClient 的 10 分钟兜底。</item>
/// <item>响应头正常、**首块**也正常，但 body 整体很慢 → **不**算超时（超时只覆盖等头与首块）。</item>
/// <item>用户主动取消 → 原样抛取消，不触发换源。</item>
/// </list>
/// R42 补充：响应头到了但 body 首块停滞（镜像回 200 后 0 字节）现在也换源，见 body首块停滞 用例。
/// </summary>
public sealed class CoreDownloads_SourceTimeoutTests
{
    /// <summary>元数据请求：会被改写成 BMCLAPI 镜像，因此有两个候选。</summary>
    private const string MetadataUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>资源文件：同样有镜像候选。</summary>
    private const string AssetUrl = "https://resources.download.minecraft.net/ab/abcdef0123456789abcdef0123456789abcdef01";

    private const string MirrorHost = "bangbang93.com";

    // ==================== 换源 ====================

    /// <summary>首选（镜像）挂住 → 到点换官方源并拿到结果。</summary>
    [Fact]
    public async Task 首选源挂住_超时后换下一个候选并成功()
    {
        var stub = new StubHandler((request, token) =>
            request.RequestUri!.Host.Contains(MirrorHost, StringComparison.OrdinalIgnoreCase)
                ? HangAsync(token)
                : OkAsync("official-payload"));

        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 150);

        var stopwatch = Stopwatch.StartNew();
        var text = await client.GetStringAsync(MetadataUrl);
        stopwatch.Stop();

        Assert.Equal("official-payload", text);

        // 两个候选各被请求一次：镜像挂住后必须真的换过去，而不是重试同一个地址。
        Assert.Equal(1, stub.CountOf(MirrorHost));
        Assert.Equal(1, stub.CountOf("mojang.com"));

        // 阈值是 20ms：整轮必须在百毫秒量级完成，绝不能退化成 HttpClient 的 10 分钟兜底。
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"换源耗时 {stopwatch.ElapsedMilliseconds} ms，超时阈值没有生效");
    }

    /// <summary>
    /// 只有单个候选时也必须有自己的超时。
    /// 改造前这种请求完全没有窗口，镜像挂住就一直等到 10 分钟兜底。
    /// </summary>
    [Fact]
    public async Task 单候选挂住_也按阈值超时()
    {
        // 这个域名不在任何镜像改写表里 → Candidates 只返回它自己。
        const string url = "https://example.invalid/data.json";
        var stub = new StubHandler((_, token) => HangAsync(token));

        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 150);

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(url));
        stopwatch.Stop();

        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"单候选耗时 {stopwatch.ElapsedMilliseconds} ms，说明没有应用响应头超时");
    }

    /// <summary>
    /// 所有候选都挂住 → 抛出的仍然是 <see cref="HttpRequestException"/>。
    /// 上层（DownloadQueue 的 MaxAttempts、各 service 的 catch）都按这个类型判网络失败，
    /// 换成 OperationCanceledException 会被当成「用户取消」直接终止整个批次。
    /// </summary>
    [Fact]
    public async Task 全部候选挂住_抛HttpRequestException()
    {
        var stub = new StubHandler((_, token) => HangAsync(token));
        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 150);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(MetadataUrl));

        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
        // 两个候选都试过了才算失败。
        Assert.Equal(1, stub.CountOf(MirrorHost));
        Assert.Equal(1, stub.CountOf("mojang.com"));
    }

    // ==================== body 慢不算超时 ====================

    /// <summary>
    /// 响应头立刻返回、首块立刻返回、后续 body 慢慢吐：后续读取不计入换源窗口。
    ///
    /// 适用于全部声明 <see cref="HttpCompletionOption.ResponseHeadersRead"/> 的调用方
    /// （<see cref="DownloadQueue"/> 与所有大文件下载路径）：首块探测之后窗口就停了，
    /// 10 MB 文件在 100 KB/s 下慢慢传不会被判超时。
    ///
    /// 判据是：换源次数为 0，且整段内容完整读回。
    /// </summary>
    [Fact]
    public async Task body慢于阈值_不换源也不判超时()
    {
        const string payload = "0123456789";
        var stub = new StubHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                // 流式返回（不缓冲）：ReadAsStreamAsync 直接拿到这条慢流，
                // 首块在 20 ms 窗口内返回，后续 9 字节继续慢慢来（45 ms）。
                Content = new StreamContent(new SlowStream(payload, delayMs: 5)),
                RequestMessage = request,
            }));

        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 40);

        var text = await client.GetStringAsync(MetadataUrl);

        Assert.Equal(payload, text);
        Assert.Single(stub.Requests);
        Assert.Equal(1, stub.CountOf(MirrorHost));
    }

    /// <summary>
    /// 响应头正常返回、body 首块永久停滞：必须换下一个候选。
    ///
    /// R42 实测到的真实形态：镜像回 200 之后 8 分钟 0 字节，旧实现只能等 HttpClient 的 10 分钟兜底。
    /// 首块探测把「头到了、body 不动」纳入换源窗口。
    /// </summary>
    [Fact]
    public async Task body首块停滞_超时后换源()
    {
        var stub = new StubHandler((request, _) =>
        {
            if (!request.RequestUri!.Host.Contains(MirrorHost, StringComparison.OrdinalIgnoreCase))
            {
                return OkAsync("official-payload");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream()),
                RequestMessage = request,
            });
        });

        using var client = CreateClient(stub, metadataTimeoutMs: 30, payloadTimeoutMs: 200);

        var stopwatch = Stopwatch.StartNew();
        var text = await client.GetStringAsync(MetadataUrl);
        stopwatch.Stop();

        Assert.Equal("official-payload", text);
        Assert.Equal(1, stub.CountOf(MirrorHost));
        Assert.Equal(1, stub.CountOf("mojang.com"));
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"换源耗时 {stopwatch.ElapsedMilliseconds} ms，body 首块超时没有生效");
    }

    /// <summary>预读首块后，调用方拿到的 Content-Length 与内容都不变。</summary>
    [Fact]
    public async Task 预读首块后_长度与内容保持完整()    {
        const string payload = "0123456789abcdef";
        var stub = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        });

        using var client = CreateClient(stub, metadataTimeoutMs: 500, payloadTimeoutMs: 500);

        using var response = await client.GetAsync(MetadataUrl);
        var lengthBeforeRead = response.Content.Headers.ContentLength;
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(payload, text);
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), lengthBeforeRead);
    }

    /// <summary>
    /// 首块正常、后续停滞：按 BodyIdleTimeout 抛 IOException，并把这次请求上报为镜像失败
    /// （自动模式下连续 3 次即降级，后续文件换官方）。
    /// </summary>
    [Fact]
    public async Task body首块之后停滞_空闲超时并上报镜像失败()
    {
        var stub = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            // 首块立刻给 1 字节，之后不再产出：R42 实测的镜像形态。
            Content = new StreamContent(new StallAfterFirstChunkStream()),
            RequestMessage = request,
        }));

        var telemetry = new RecordingTelemetry();
        using var client = CreateClient(
            stub,
            metadataTimeoutMs: 500,
            payloadTimeoutMs: 500,
            idleTimeoutMs: 30,
            telemetry: telemetry);

        // HttpClient 会把「读内容时的 IOException」包成 HttpRequestException（Info：读取失败），
        // 这正是上层换源/重试所期待的类型；空闲超时的原因在 InnerException 里。
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(MetadataUrl));

        Assert.Contains("空闲超时", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            telemetry.Results,
            result => !result.Succeeded && result.PrimarySource == DownloadSource.Mirror);
    }

    // ==================== 用户取消 ====================

    /// <summary>
    /// 用户主动取消 → 原样抛 <see cref="OperationCanceledException"/>，且不换源。
    ///
    /// 这条区分很要紧：把用户取消当成「镜像劣化」会导致状态机误降级，
    /// 还会让「取消」变成「再等 30 秒试下一个源」。
    /// </summary>
    [Fact]
    public async Task 用户取消_抛取消且不换源()
    {
        var stub = new StubHandler((_, token) => HangAsync(token));
        // 阈值调得远大于取消时间：能抛出来只可能是外部取消，不是超时。
        using var client = CreateClient(stub, metadataTimeoutMs: 10_000, payloadTimeoutMs: 10_000);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetStringAsync(MetadataUrl, cancellation.Token));

        Assert.Single(stub.Requests);
        Assert.Equal(1, stub.CountOf(MirrorHost));
    }

    // ==================== 阈值分档 ====================

    /// <summary>
    /// 镜像候选与官方候选的窗口不同：镜像元数据 20 ms 到点、官方元数据 200 ms 到点。
    ///
    /// 判据是总耗时的量级——如果官方候选也用 20 ms，整轮只会有 40 ms 左右。
    /// 官方档必须更宽，因为 handler 层看不到上层要的是 headers 还是完整 body，
    /// 而官方站点的版本清单本身就常常要十几秒才能下完（用 5 秒会直接判死）。
    /// </summary>
    [Fact]
    public async Task 元数据请求_镜像走短档_官方走宽档()
    {
        var stub = new StubHandler((_, token) => HangAsync(token));
        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 200);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(MetadataUrl));
        stopwatch.Stop();

        // 20 ms（镜像）+ 200 ms（官方）≈ 220 ms；两个候选都走 200 ms 会到 400 ms 以上。
        Assert.InRange(stopwatch.ElapsedMilliseconds, 150, 350);
    }

    /// <summary>镜像上的元数据请求走 5 秒档：单候选也到点就放弃。</summary>
    [Fact]
    public async Task 镜像元数据_走短档()
    {
        // 已经是镜像地址 → 不可再改写 → 只有一个候选，超时档位仍然要按镜像算。
        const string url = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
        var stub = new StubHandler((_, token) => HangAsync(token));
        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 200);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(url));
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 150,
            $"镜像元数据耗时 {stopwatch.ElapsedMilliseconds} ms，没走短档");
    }

    /// <summary>镜像上的资源文件走 30 秒档：首包慢是合法的，不能按元数据档卡。</summary>
    [Fact]
    public async Task 镜像资源_走宽档()
    {
        const string url = "https://bmclapi2.bangbang93.com/assets/ab/abcdef0123456789abcdef0123456789abcdef01";
        var stub = new StubHandler((_, token) => HangAsync(token));
        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 200);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(url));
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"资源耗时 {stopwatch.ElapsedMilliseconds} ms，疑似走了元数据档");
    }

    /// <summary>
    /// 带 Range 的请求一律归宽档，即使 URL 是 <c>.json</c>——那是断点续传的资源文件，不是文档。
    /// </summary>
    [Fact]
    public async Task 带Range的请求_归宽档()
    {
        var stub = new StubHandler((_, token) => HangAsync(token));
        using var client = CreateClient(stub, metadataTimeoutMs: 20, payloadTimeoutMs: 200);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");
        request.Headers.Range = new RangeHeaderValue(0, 1023);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead));
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"耗时 {stopwatch.ElapsedMilliseconds} ms：带 Range 的请求被误判成元数据");
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 构造一个挂着 <see cref="MirrorRewritingHandler"/>、内层被换成假 handler 的客户端。
    /// 阈值调到毫秒级，用例才能在毫秒级跑完。
    /// </summary>
    private static HttpClient CreateClient(
        StubHandler stub,
        int metadataTimeoutMs,
        int payloadTimeoutMs,
        int idleTimeoutMs = 60_000,
        IDownloadSourceTelemetry? telemetry = null)
    {
        var rewriting = telemetry is null
            ? new MirrorRewritingHandler(DownloadSource.Mirror)
            : new MirrorRewritingHandler(new FixedSourceProvider(), telemetry);

        rewriting.MetadataHeaderTimeout = TimeSpan.FromMilliseconds(metadataTimeoutMs);
        rewriting.PayloadHeaderTimeout = TimeSpan.FromMilliseconds(payloadTimeoutMs);
        rewriting.BodyIdleTimeout = TimeSpan.FromMilliseconds(idleTimeoutMs);

        // 构造函数里已经建了一个真实的 HttpClientHandler（会真连网），这里换成假的。
        // 它没有被任何人持有，必须自己释放，否则每个用例泄漏一个 handler。
        var original = rewriting.InnerHandler;
        rewriting.InnerHandler = stub;
        original?.Dispose();

        return new HttpClient(rewriting, disposeHandler: true);
    }

    private static Task<HttpResponseMessage> OkAsync(string payload) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
        });

    /// <summary>挂住不返回，直到 token 被取消（超时或用户取消）。</summary>
    private static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("不可达：Delay 只用取消结束。");
    }

    /// <summary>记录每个请求的 host，并按 responder 回应的假 handler。</summary>
    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        public int CountOf(string host) =>
            Requests.Count(url => url.Contains(host, StringComparison.OrdinalIgnoreCase));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri?.Host ?? string.Empty);
            return responder(request, cancellationToken);
        }
    }

    /// <summary>按固定间隔逐字节吐内容的流，用来模拟「响应头秒回、首块到了、后续 body 很慢」。</summary>
    private sealed class SlowStream(string text, int delayMs) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _bytes.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("只支持异步读取。");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _bytes.Length)
            {
                return 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            buffer.Span[0] = _bytes[_offset++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>固定首选源（测试里只关心上报，不关心状态机）。</summary>
    private sealed class FixedSourceProvider : IDownloadSourceProvider
    {
        public DownloadSource Strategy => DownloadSource.Mirror;

        public DownloadSource Effective => DownloadSource.Mirror;

        public string? DegradeReason => null;

        public DateTimeOffset? DegradedAtUtc => null;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>记录 handler 上报结果的假 telemetry。</summary>
    private sealed class RecordingTelemetry : IDownloadSourceTelemetry
    {
        public ConcurrentQueue<DownloadAttemptResult> Results { get; } = new();

        public void Report(DownloadAttemptResult result) => Results.Enqueue(result);
    }

    /// <summary>第一次读返回 1 字节，之后挂住：模拟「首块之后 body 停住」。</summary>
    private sealed class StallAfterFirstChunkStream : Stream
    {
        private bool _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                buffer.Span[0] = (byte)'x';
                return ValueTask.FromResult(1);
            }

            return new ValueTask<int>(HangAsync(cancellationToken));
        }

        private static async Task<int> HangAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("不可达：Delay 只用取消结束。");
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    /// <summary>永远不产出字节的流：模拟「响应头回来了、body 卡死」的镜像。</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(HangAsync(cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            HangAsync(cancellationToken);

        private static async Task<int> HangAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("不可达：Delay 只用取消结束。");
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

