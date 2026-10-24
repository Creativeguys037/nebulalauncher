using System.Net;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载源测速：排序纯函数 + 一次真实测量链路（假 handler，不联网）。
///
/// 为什么排序用构造样本而不是跑网络：字段之间的相对关系（首字节、吞吐、容差边界）
/// 才是验收对象，真实延迟受调度抖动影响，测不出「30% 边界」这种精确判据。
/// 网络那一层的验收放在后半段：超时、404、取消、读取上限，全部用假 handler 精确控制。
/// </summary>
public class CoreDownloads_SourceProbeTests
{
    // ==================== 排序规则 ====================

    [Fact]
    public void 综合分更低的一侧被建议_官方更快时建议官方()
    {
        // 官方 120ms + 1MB/2000KBps=512ms → 632；镜像 400ms + 683ms → 1083。
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 120, kbps: 2000),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 400, kbps: 1500),
        };

        var decision = SourceProbeRanking.Decide(samples);

        Assert.Equal(DownloadSource.Official, decision.SuggestedSource);
        Assert.False(decision.IsTie);
        Assert.False(decision.BothFailed);
    }

    [Fact]
    public void 综合分更低的一侧被建议_镜像更快时建议镜像()
    {
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 500, kbps: 1000),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 90, kbps: 4000),
        };

        var decision = SourceProbeRanking.Decide(samples);

        Assert.Equal(DownloadSource.Mirror, decision.SuggestedSource);
    }

    [Fact]
    public void 差距小于百分之三十判差不多且不给建议()
    {
        // 综合分 1100 vs 1570：差距 470 / 1570 = 29.9%，落在容差内。
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 100, kbps: 1024),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 570, kbps: 1024),
        };

        var decision = SourceProbeRanking.Decide(samples);
        var summary = SourceProbeRanking.Describe(samples, decision);

        Assert.True(decision.IsTie);
        Assert.Null(decision.SuggestedSource);
        Assert.Contains("差不多", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("建议用", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 差距刚好超过百分之三十时给出建议()
    {
        // 综合分 1100 vs 1580：差距 480 / 1580 = 30.4%，超出容差。
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 100, kbps: 1024),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 580, kbps: 1024),
        };

        var decision = SourceProbeRanking.Decide(samples);

        Assert.False(decision.IsTie);
        Assert.Equal(DownloadSource.Official, decision.SuggestedSource);
    }

    [Fact]
    public void 两侧都失败时不给建议并报不可用()
    {
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: false, reason: "HTTP 403"),
            Sample(DownloadSource.Mirror, ok: false, reason: "超时（3 秒）"),
        };

        var decision = SourceProbeRanking.Decide(samples);
        var summary = SourceProbeRanking.Describe(samples, decision);

        Assert.True(decision.BothFailed);
        Assert.Null(decision.SuggestedSource);
        Assert.Contains("两侧都不可用", summary, StringComparison.Ordinal);
        Assert.Contains("HTTP 403", summary, StringComparison.Ordinal);
        Assert.Contains("超时（3 秒）", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 单侧失败时建议成功的一侧()
    {
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 200, kbps: 800),
            Sample(DownloadSource.Mirror, ok: false, reason: "HTTP 404"),
        };

        var decision = SourceProbeRanking.Decide(samples);
        var summary = SourceProbeRanking.Describe(samples, decision);

        Assert.False(decision.BothFailed);
        Assert.Equal(DownloadSource.Official, decision.SuggestedSource);
        Assert.Contains("建议用官方源", summary, StringComparison.Ordinal);
        Assert.Contains("失败：HTTP 404", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 失败原因为空时回落成未知错误而不是空串()
    {
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: false, reason: null),
            Sample(DownloadSource.Mirror, ok: false, reason: "   "),
        };

        var summary = SourceProbeRanking.Describe(samples, SourceProbeRanking.Decide(samples));

        Assert.Equal("未知错误", SourceProbeRanking.FailureText(samples[0]));
        Assert.Contains("失败：未知错误", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("失败： ", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 吞吐为零时按惩罚值计分而不是无穷大()
    {
        var zero = Sample(DownloadSource.Official, ok: true, firstByteMs: 50, kbps: 0);
        var healthy = Sample(DownloadSource.Mirror, ok: true, firstByteMs: 200, kbps: 1000);

        var score = SourceProbeRanking.ScoreMs(zero);
        var decision = SourceProbeRanking.Decide([zero, healthy]);

        Assert.True(double.IsFinite(score));
        Assert.Equal(50 + SourceProbeRanking.MissingThroughputPenaltyMs, score, 3);
        Assert.Equal(DownloadSource.Mirror, decision.SuggestedSource);
    }

    [Fact]
    public void 建议文案包含源名()
    {
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 600, kbps: 900),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 80, kbps: 3000),
        };

        var summary = SourceProbeRanking.Describe(samples, SourceProbeRanking.Decide(samples));

        Assert.Contains("BMCLAPI 镜像", summary, StringComparison.Ordinal);
        Assert.Contains("建议用 BMCLAPI 镜像", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 排序纯函数幂等()
    {
        var samples = new[]
        {
            Sample(DownloadSource.Official, ok: true, firstByteMs: 300, kbps: 1000),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 330, kbps: 1050),
        };

        var first = SourceProbeRanking.Decide(samples);
        var second = SourceProbeRanking.Decide(samples);

        Assert.Equal(first, second);
        Assert.Equal(
            SourceProbeRanking.Describe(samples, first),
            SourceProbeRanking.Describe(samples, second));
    }

    [Fact]
    public void 报告不可变_with生成新对象不影响原报告()
    {
        var original = new SourceProbeReport
        {
            Samples = new[] { Sample(DownloadSource.Official, ok: true, firstByteMs: 100, kbps: 900) },
            SuggestedSource = DownloadSource.Official,
            Summary = "官方 100ms",
        };

        var copy = original with { Summary = "改过的" };

        Assert.Equal("官方 100ms", original.Summary);
        Assert.Equal("改过的", copy.Summary);
        Assert.Same(original.Samples, copy.Samples);

        // Empty 是共享的只读实例，不能被任何一次测速改写。
        Assert.Empty(SourceProbeReport.Empty.Samples);
        Assert.Equal("尚未测速", SourceProbeReport.Empty.Summary);
        Assert.True(SourceProbeReport.Empty is { BothFailed: false, IsTie: false, SuggestedSource: null });
    }

    [Fact]
    public void 没有样本时结论文案是尚未测速()
    {
        var summary = SourceProbeRanking.Describe(
            Array.Empty<SourceProbeSample>(),
            SourceProbeRanking.Decide(Array.Empty<SourceProbeSample>()));

        Assert.Equal("尚未测速", summary);
    }

    // ==================== 测量链路（假 handler） ====================

    [Fact]
    public async Task 两侧都成功时给出两条样本与一行结论()
    {
        using var handler = new ProbeHandler(payloadSize: 8 * 1024);
        using var client = new HttpClient(handler);
        using var probe = new DownloadSourceProbe(client, targets: TwoTargets());

        var report = await probe.ProbeAsync();

        Assert.Equal(2, report.Samples.Count);
        Assert.All(report.Samples, sample => Assert.True(sample.Succeeded));

        var official = report.Samples.Single(s => s.Source == DownloadSource.Official);
        var mirror = report.Samples.Single(s => s.Source == DownloadSource.Mirror);

        Assert.True(official.FirstByteMs > 0);
        Assert.True(official.ThroughputKbps > 0);
        Assert.Equal(8L * 1024, official.BytesRead);
        Assert.Equal(8L * 1024, mirror.BytesRead);
        Assert.Contains("ms", report.Summary, StringComparison.Ordinal);
        Assert.False(report.BothFailed);
    }

    [Fact]
    public async Task 镜像返回404时建议官方并保留失败原因()
    {
        using var handler = new ProbeHandler(payloadSize: 4 * 1024, mirrorStatus: HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        using var probe = new DownloadSourceProbe(client, targets: TwoTargets());

        var report = await probe.ProbeAsync();

        var mirror = report.Samples.Single(s => s.Source == DownloadSource.Mirror);
        Assert.False(mirror.Succeeded);
        Assert.Contains("404", mirror.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(DownloadSource.Official, report.SuggestedSource);
        Assert.Contains("建议用官方源", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 单次超时记失败且不影响另一侧()
    {
        using var handler = new ProbeHandler(
            payloadSize: 4 * 1024,
            mirrorDelay: TimeSpan.FromSeconds(5));
        using var client = new HttpClient(handler);

        // 60ms 上限：镜像要挂 5 秒，必然超时；官方立即返回。
        using var probe = new DownloadSourceProbe(
            client,
            targets: TwoTargets(),
            perRequestTimeout: TimeSpan.FromMilliseconds(60),
            totalBudget: TimeSpan.FromSeconds(5));

        var report = await probe.ProbeAsync();

        var mirror = report.Samples.Single(s => s.Source == DownloadSource.Mirror);
        var official = report.Samples.Single(s => s.Source == DownloadSource.Official);

        Assert.False(mirror.Succeeded);
        Assert.Contains("超时", mirror.FailureReason!, StringComparison.Ordinal);
        Assert.True(official.Succeeded);
        Assert.Equal(DownloadSource.Official, report.SuggestedSource);
    }

    [Fact]
    public async Task 单目标读取受字节上限截断()
    {
        using var handler = new ProbeHandler(payloadSize: 64 * 1024);
        using var client = new HttpClient(handler);
        using var probe = new DownloadSourceProbe(
            client,
            targets: TwoTargets(),
            maxBytesPerTarget: 512);

        var report = await probe.ProbeAsync();

        Assert.All(report.Samples, sample => Assert.Equal(512L, sample.BytesRead));
    }

    [Fact]
    public async Task 不可镜像的地址不参与测速()
    {
        using var handler = new ProbeHandler(payloadSize: 1024);
        using var client = new HttpClient(handler);
        using var probe = new DownloadSourceProbe(
            client,
            targets: [new SourceProbeTarget("本地文件", "https://example.invalid/not-mirrored.bin")]);

        var report = await probe.ProbeAsync();

        Assert.Empty(report.Samples);
        Assert.Contains("没有可测速的目标", report.Summary, StringComparison.Ordinal);
        Assert.Equal(0, handler.Hits);
    }

    [Fact]
    public async Task 用户取消时抛出而不是记成失败()
    {
        using var handler = new ProbeHandler(payloadSize: 4 * 1024, officialDelay: TimeSpan.FromSeconds(5));
        using var client = new HttpClient(handler);
        using var probe = new DownloadSourceProbe(
            client,
            targets: TwoTargets(),
            perRequestTimeout: TimeSpan.FromSeconds(10),
            totalBudget: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(cts.Token));
    }

    [Fact]
    public async Task 默认实现返回空报告_界面据此置灰()
    {
        IDownloadSourceProbe probe = new EmptyProbe();
        var report = await probe.ProbeAsync();

        Assert.Same(SourceProbeReport.Empty, report);
        Assert.Equal("尚未测速", report.Summary);
    }

    // ==================== 辅助 ====================

    private static SourceProbeSample Sample(
        DownloadSource source,
        bool ok,
        double firstByteMs = 0,
        double kbps = 0,
        string? reason = null) => new()
    {
        Source = source,
        SourceName = SourceProbeRanking.DisplayName(source),
        Succeeded = ok,
        FirstByteMs = ok ? firstByteMs : 0,
        ThroughputKbps = ok ? kbps : 0,
        BytesRead = ok ? 1024 : 0,
        TargetsOk = ok ? 1 : 0,
        TargetsFailed = ok ? 0 : 1,
        FailureReason = ok ? null : reason,
    };

    /// <summary>两条同路径可镜像的官方地址：镜像地址由 <c>DownloadMirror.Rewrite</c> 现算。</summary>
    private static IReadOnlyList<SourceProbeTarget> TwoTargets() =>
    [
        new SourceProbeTarget(
            "version_manifest_v2.json",
            "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
    ];

    /// <summary>满足 <see cref="IDownloadSourceProbe"/> 的默认实现（DIM）路径。</summary>
    private sealed class EmptyProbe : IDownloadSourceProbe
    {
    }

    /// <summary>
    /// 假 handler：按「是否镜像主机」决定延迟与状态码，返回固定长度内容。
    /// 不联网、不读磁盘，超时与失败都能精确复现。
    /// </summary>
    private sealed class ProbeHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly TimeSpan _officialDelay;
        private readonly TimeSpan _mirrorDelay;
        private readonly HttpStatusCode _mirrorStatus;
        private int _hits;

        public ProbeHandler(
            int payloadSize,
            TimeSpan? officialDelay = null,
            TimeSpan? mirrorDelay = null,
            HttpStatusCode mirrorStatus = HttpStatusCode.OK)
        {
            _payload = new byte[payloadSize];
            _officialDelay = officialDelay ?? TimeSpan.Zero;
            _mirrorDelay = mirrorDelay ?? TimeSpan.Zero;
            _mirrorStatus = mirrorStatus;
        }

        public int Hits => _hits;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _hits);

            var url = request.RequestUri?.ToString() ?? string.Empty;
            var isMirror = url.Contains("bangbang93", StringComparison.OrdinalIgnoreCase);
            var delay = isMirror ? _mirrorDelay : _officialDelay;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (isMirror && _mirrorStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(_mirrorStatus);
            }

            var content = new ByteArrayContent(_payload);
            content.Headers.ContentLength = _payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}