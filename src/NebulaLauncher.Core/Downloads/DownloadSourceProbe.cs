using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 源测速的排序规则。全部是纯函数：给定同一组样本，结论永远相同（无时钟、无网络、无状态）。
///
/// ## 综合分（毫秒，越小越好）
/// <c>首字节 + 传完参考体积的毫秒数</c>。只比首字节会让「首包快但吞吐极低」的源赢；
/// 只比吞吐会让「连上就卡」的源赢。两项相加才是用户真实感受的近似值。
///
/// 参考体积取 1 MB：样本文件只有几十到几百 KB，按 1 MB 换算等于把吞吐的权重放大到
/// 「差一次数量级就一定翻盘」的量级，比按样本真实体积换算更稳（样本体积可以差很多倍）。
///
/// ## 吞吐为 0 的处理
/// 首字节拿到了但一个字节都没读到（空响应 / 流被立刻关掉）时吞吐为 0，
/// 直接除会得到无穷大。这里按「传 1 MB 要花一整个单次超时窗口」罚 3000ms，
/// 让它在排序里必然输给任何正常样本，而不是被当成「最快的源」。
///
/// ## 持平判据
/// 两侧综合分差距 &lt; 30%（分母取较大的那个）时判「差不多」且不给建议。
/// 用较大值作分母而不是较小值：小值作分母会把 10ms 与 13ms 的抖动放大成 30%，判成「明显更快」。
/// </summary>
public static class SourceProbeRanking
{
    /// <summary>判「差不多」的相对容差。</summary>
    public const double TieTolerance = 0.30;

    /// <summary>综合分换算用的参考体积（1 MB）。</summary>
    public const double ReferenceBytes = 1024d * 1024d;

    /// <summary>吞吐为 0（或非有限值）时用于换算的惩罚耗时。</summary>
    public const double MissingThroughputPenaltyMs = 3000d;

    /// <summary>综合分（毫秒）。失败样本不参与比较，调用方应先用 <see cref="SourceProbeSample.Succeeded"/> 过滤。</summary>
    public static double ScoreMs(SourceProbeSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var firstByte = double.IsFinite(sample.FirstByteMs) && sample.FirstByteMs > 0
            ? sample.FirstByteMs
            : 0d;

        var transfer = double.IsFinite(sample.ThroughputKbps) && sample.ThroughputKbps > 0
            ? ReferenceBytes / 1024d / sample.ThroughputKbps * 1000d
            : MissingThroughputPenaltyMs;

        return firstByte + transfer;
    }

    /// <summary>
    /// 排序并给出建议。规则：成功优先；两侧都成功时比综合分；差距 &lt; <see cref="TieTolerance"/>
    /// 判持平不给建议；只有一侧成功就建议那一侧；两侧都没有成功样本返回
    /// <see cref="SourceProbeDecision.BothFailed"/>。
    /// </summary>
    public static SourceProbeDecision Decide(IReadOnlyList<SourceProbeSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var official = Find(samples, DownloadSource.Official);
        var mirror = Find(samples, DownloadSource.Mirror);

        var officialOk = official?.Succeeded == true;
        var mirrorOk = mirror?.Succeeded == true;

        if (!officialOk && !mirrorOk)
        {
            // 一侧样本缺失也算「没有可用源」：这种情况下给建议只会误导。
            return new SourceProbeDecision(null, false, official is not null || mirror is not null);
        }

        if (officialOk && !mirrorOk)
        {
            return new SourceProbeDecision(DownloadSource.Official, false, false);
        }

        if (mirrorOk && !officialOk)
        {
            return new SourceProbeDecision(DownloadSource.Mirror, false, false);
        }

        var officialScore = ScoreMs(official!);
        var mirrorScore = ScoreMs(mirror!);
        var larger = Math.Max(officialScore, mirrorScore);
        var gap = larger <= 0 ? 0d : Math.Abs(officialScore - mirrorScore) / larger;

        if (gap < TieTolerance)
        {
            return new SourceProbeDecision(null, true, false);
        }

        return new SourceProbeDecision(
            officialScore < mirrorScore ? DownloadSource.Official : DownloadSource.Mirror,
            false,
            false);
    }

    /// <summary>
    /// 一行结论文案。格式：<c>官方 320ms · BMCLAPI 镜像 190ms · 建议用 BMCLAPI 镜像</c>。
    /// 文案在 Core 而不是 ViewModel：两处（下载页结果行、截图 demo）都从同一函数取，
    /// 「建议」二字与源名必须永远对得上。
    /// </summary>
    public static string Describe(IReadOnlyList<SourceProbeSample> samples, SourceProbeDecision decision)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(decision);

        if (samples.Count == 0)
        {
            return "尚未测速";
        }

        var builder = new StringBuilder();

        foreach (var source in new[] { DownloadSource.Official, DownloadSource.Mirror })
        {
            var sample = Find(samples, source);
            if (sample is null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" · ");
            }

            builder.Append(sample.Succeeded
                ? $"{DisplayName(source)} {sample.FirstByteMs:0}ms"
                : $"{DisplayName(source)} 失败：{FailureText(sample)}");
        }

        if (decision.BothFailed)
        {
            return builder.Append(" · 两侧都不可用，请检查网络或代理").ToString();
        }

        if (decision.IsTie)
        {
            return builder.Append(" · 两侧差不多，保持当前设置即可").ToString();
        }

        if (decision.SuggestedSource == DownloadSource.Mirror)
        {
            return builder.Append(" · 建议用 BMCLAPI 镜像").ToString();
        }

        if (decision.SuggestedSource == DownloadSource.Official)
        {
            return builder.Append(" · 建议用官方源").ToString();
        }

        return builder.ToString();
    }

    /// <summary>源名。自动模式不该出现在样本里，兜底给「自动选择」。</summary>
    public static string DisplayName(DownloadSource source) => source switch
    {
        DownloadSource.Official => "官方",
        DownloadSource.Mirror => "BMCLAPI 镜像",
        _ => "自动选择",
    };

    /// <summary>失败原因文本。聚合保证非空，这里再兜一层，界面永远不会显示「失败：」后面什么都没有。</summary>
    public static string FailureText(SourceProbeSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return string.IsNullOrWhiteSpace(sample.FailureReason) ? "未知错误" : sample.FailureReason!;
    }

    private static SourceProbeSample? Find(IReadOnlyList<SourceProbeSample> samples, DownloadSource source)
    {
        foreach (var sample in samples)
        {
            if (sample.Source == source)
            {
                return sample;
            }
        }

        return null;
    }
}

/// <summary>
/// 官方 vs 镜像的下载源测速。
///
/// ## 它做什么
/// 取一组「官方地址与镜像地址同路径」的小资源，两侧各量一次：到响应头的时间（首字节）
/// 与读满上限或读完的字节数（吞吐）。结果只用于在下载页显示一行提示——<b>不改设置、
/// 不改 <see cref="DownloadSourceGovernor"/> 的当前源、不落盘</b>。要不要切换由用户自己在设置里决定。
///
/// ## 为什么不用 <see cref="DownloadHttpClientFactory"/> 的 <c>Client</c>
/// 那个客户端挂着 <see cref="MirrorRewritingHandler"/>：它会把官方 URL 按当前策略改写成镜像 URL。
/// 拿它测「官方 vs 镜像」等于两次都打到同一个地址，结论必然相等——测速必须吃「原始地址」。
/// 代理不受影响：<c>ProxyConfigurator</c> 改的是 <see cref="HttpClient.DefaultProxy"/>，
/// 直连构造的客户端同样会走用户配置的代理。
///
/// ## 硬上限
/// 单次请求 3 秒、整次测速 8 秒、并发 2、单目标最多读 256 KB。
/// 全部通过构造参数可换（测试注入短超时与假 handler，不联网、不等待真实时间）。
/// 任何一个目标失败都不抛异常：记 0 分并保留原因，另一边照常出结果。
/// </summary>
public sealed class DownloadSourceProbe : IDownloadSourceProbe, IDisposable
{
    /// <summary>单次请求上限。</summary>
    public static readonly TimeSpan DefaultPerRequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>整次测速的墙钟上限。</summary>
    public static readonly TimeSpan DefaultTotalBudget = TimeSpan.FromSeconds(8);

    /// <summary>并发上限。</summary>
    public const int DefaultMaxConcurrency = 2;

    /// <summary>单个目标最多读多少字节。</summary>
    public const int DefaultMaxBytesPerTarget = 256 * 1024;

    /// <summary>
    /// 默认目标集。两条都是 launchermeta 的小 JSON，官方与 <c>bmclapi2.bangbang93.com</c>
    /// 上路径完全一致（<c>BmclApiMap</c> 里 <c>launchermeta.mojang.com</c> → 镜像根），
    /// 所以两边的比较是同一个文件，不存在「镜像多绕一层路径」的偏差。
    ///
    /// 不选 assets 对象：asset hash 是内容地址，写死一个就等于把一个可能被清理的旧对象
    /// 钉进代码；而版本清单是官方长期维护的入口文件。
    /// </summary>
    public static IReadOnlyList<SourceProbeTarget> DefaultTargets { get; } =
    [
        new SourceProbeTarget("version_manifest_v2.json", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
        new SourceProbeTarget("version_manifest.json", "https://launchermeta.mojang.com/mc/game/version_manifest.json"),
    ];

    private readonly HttpClient _client;
    private readonly IReadOnlyList<SourceProbeTarget> _targets;
    private readonly TimeSpan _perRequestTimeout;
    private readonly TimeSpan _totalBudget;
    private readonly int _maxBytesPerTarget;
    private readonly SemaphoreSlim _gate;

    /// <param name="client">收到的客户端不能挂镜像重写 handler（见类注释）。</param>
    /// <param name="targets">测速目标；null 用 <see cref="DefaultTargets"/>。</param>
    /// <param name="perRequestTimeout">单次请求上限。</param>
    /// <param name="totalBudget">整次测速上限。</param>
    /// <param name="maxConcurrency">并发上限。</param>
    /// <param name="maxBytesPerTarget">单目标读取上限（字节）。</param>
    public DownloadSourceProbe(
        HttpClient client,
        IReadOnlyList<SourceProbeTarget>? targets = null,
        TimeSpan? perRequestTimeout = null,
        TimeSpan? totalBudget = null,
        int maxConcurrency = DefaultMaxConcurrency,
        int maxBytesPerTarget = DefaultMaxBytesPerTarget)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _targets = targets ?? DefaultTargets;
        _perRequestTimeout = perRequestTimeout ?? DefaultPerRequestTimeout;
        _totalBudget = totalBudget ?? DefaultTotalBudget;
        _maxBytesPerTarget = Math.Max(1, maxBytesPerTarget);
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
    }

    /// <summary>
    /// 释放并发闸门。由 DI 容器在宿主退出时调用；<c>HttpClient</c> 由调用方拥有，不在这里释放。
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<SourceProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var officialTargets = new List<(string Name, string Url)>();
        var mirrorTargets = new List<(string Name, string Url)>();

        foreach (var target in _targets)
        {
            if (string.IsNullOrWhiteSpace(target.OfficialUrl))
            {
                continue;
            }

            var mirrorUrl = DownloadMirror.Rewrite(target.OfficialUrl, DownloadSource.Mirror);

            // 不可镜像的地址两侧会打到同一个 URL：那不是「比较」，直接跳过。
            if (string.Equals(mirrorUrl, target.OfficialUrl, StringComparison.Ordinal))
            {
                continue;
            }

            officialTargets.Add((target.Name, target.OfficialUrl));
            mirrorTargets.Add((target.Name, mirrorUrl));
        }

        if (officialTargets.Count == 0)
        {
            return new SourceProbeReport { Summary = "没有可测速的目标（镜像重写表里没有这些地址）" };
        }

        var started = Stopwatch.GetTimestamp();

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(_totalBudget);

        // 两侧并行 + 每侧 1 个目标在途 = 并发 2。两侧串行会让总耗时翻倍，而 8 秒预算等不起。
        var samples = await Task.WhenAll(
                MeasureSideAsync(DownloadSource.Official, officialTargets, budgetCts.Token, cancellationToken),
                MeasureSideAsync(DownloadSource.Mirror, mirrorTargets, budgetCts.Token, cancellationToken))
            .ConfigureAwait(false);

        var decision = SourceProbeRanking.Decide(samples);

        return new SourceProbeReport
        {
            Samples = samples,
            SuggestedSource = decision.SuggestedSource,
            IsTie = decision.IsTie,
            BothFailed = decision.BothFailed,
            ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Summary = SourceProbeRanking.Describe(samples, decision),
        };
    }

    /// <summary>测一侧的全部目标并聚合成一条样本。</summary>
    private async Task<SourceProbeSample> MeasureSideAsync(
        DownloadSource source,
        IReadOnlyList<(string Name, string Url)> targets,
        CancellationToken budgetToken,
        CancellationToken userToken)
    {
        var measurements = new List<TargetMeasurement>(targets.Count);

        foreach (var (_, url) in targets)
        {
            measurements.Add(await ProbeTargetAsync(url, budgetToken, userToken).ConfigureAwait(false));

            if (budgetToken.IsCancellationRequested)
            {
                // 预算用完后不再发请求：把剩下的记成失败，避免「没测」被当成「测得快」。
                while (measurements.Count < targets.Count)
                {
                    measurements.Add(TargetMeasurement.Fail($"测速总耗时超过 {_totalBudget.TotalSeconds:0.#} 秒，未测"));
                }

                break;
            }
        }

        return Aggregate(source, measurements);
    }

    /// <summary>测一个地址。除用户取消外一律返回结果，不抛。</summary>
    private async Task<TargetMeasurement> ProbeTargetAsync(
        string url,
        CancellationToken budgetToken,
        CancellationToken userToken)
    {
        if (userToken.IsCancellationRequested)
        {
            userToken.ThrowIfCancellationRequested();
        }

        var acquired = false;

        try
        {
            await _gate.WaitAsync(budgetToken).ConfigureAwait(false);
            acquired = true;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(userToken, budgetToken);
            timeoutCts.CancelAfter(_perRequestTimeout);

            var started = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                var firstByteMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (!response.IsSuccessStatusCode)
                {
                    return TargetMeasurement.Fail($"HTTP {(int)response.StatusCode}");
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                var buffer = new byte[16 * 1024];
                long total = 0;

                while (total < _maxBytesPerTarget)
                {
                    var want = (int)Math.Min(buffer.Length, _maxBytesPerTarget - total);
                    var read = await stream
                        .ReadAsync(buffer.AsMemory(0, want), timeoutCts.Token)
                        .ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                // 1ms 下限：极快时 elapsed-firstByte 可能是 0，除零会让吞吐变无穷大。
                var transferMs = Math.Max(1d, elapsedMs - firstByteMs);
                var throughput = total > 0 ? total / 1024d / (transferMs / 1000d) : 0d;

                return new TargetMeasurement(true, firstByteMs, throughput, total, transferMs, null);
            }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return TargetMeasurement.Fail(budgetToken.IsCancellationRequested
                    ? $"测速总耗时超过 {_totalBudget.TotalSeconds:0.#} 秒"
                    : $"超时（{_perRequestTimeout.TotalSeconds:0.#} 秒）");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                return TargetMeasurement.Fail(Shorten(ex.Message));
            }
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return TargetMeasurement.Fail($"测速总耗时超过 {_totalBudget.TotalSeconds:0.#} 秒");
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    /// <summary>把同一侧的目标结果聚合成一条样本。首字节取中位数，吞吐按总字节 / 总传输时间。</summary>
    private static SourceProbeSample Aggregate(DownloadSource source, List<TargetMeasurement> measurements)
    {
        var ok = measurements.Where(m => m.Succeeded).ToArray();
        var failed = measurements.Count - ok.Length;

        if (ok.Length == 0)
        {
            return new SourceProbeSample
            {
                Source = source,
                SourceName = SourceProbeRanking.DisplayName(source),
                Succeeded = false,
                FirstByteMs = 0,
                ThroughputKbps = 0,
                BytesRead = 0,
                TargetsOk = 0,
                TargetsFailed = failed,
                FailureReason = FirstFailure(measurements),
            };
        }

        var firstBytes = ok.Select(m => m.FirstByteMs).OrderBy(ms => ms).ToArray();
        var median = firstBytes.Length % 2 == 1
            ? firstBytes[firstBytes.Length / 2]
            : (firstBytes[(firstBytes.Length / 2) - 1] + firstBytes[firstBytes.Length / 2]) / 2d;

        var totalBytes = ok.Sum(m => m.BytesRead);
        var totalTransferMs = ok.Sum(m => m.TransferMs);

        // 传输时间为 0（一个字节都没读到）时不能除：吞吐记 0，由 ScoreMs 统一按惩罚值处理。
        var throughput = totalTransferMs > 0 && totalBytes > 0
            ? totalBytes / 1024d / (totalTransferMs / 1000d)
            : 0d;

        return new SourceProbeSample
        {
            Source = source,
            SourceName = SourceProbeRanking.DisplayName(source),
            Succeeded = true,
            FirstByteMs = median,
            ThroughputKbps = throughput,
            BytesRead = totalBytes,
            TargetsOk = ok.Length,
            TargetsFailed = failed,
            FailureReason = null,
        };
    }

    /// <summary>第一条非空失败原因；都没有时给「未知错误」（界面不能显示空原因）。</summary>
    private static string FirstFailure(List<TargetMeasurement> measurements)
    {
        foreach (var measurement in measurements)
        {
            if (!string.IsNullOrWhiteSpace(measurement.FailureReason))
            {
                return measurement.FailureReason!;
            }
        }

        return "未知错误";
    }

    /// <summary>异常消息可能带上完整 URL 与堆栈前缀，截断到一行能放进状态行的长度。</summary>
    private static string Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "请求失败";
        }

        var single = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 120 ? single : single[..120] + "…";
    }

    /// <summary>单个目标的测量结果（内部用，不对外暴露）。</summary>
    private readonly record struct TargetMeasurement(
        bool Succeeded,
        double FirstByteMs,
        double ThroughputKbps,
        long BytesRead,
        double TransferMs,
        string? FailureReason)
    {
        public static TargetMeasurement Fail(string reason) =>
            new(false, 0, 0, 0, 0, reason);
    }
}

/// <summary>下载源测速的 DI 注册入口。</summary>
public static class DownloadSourceProbeServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IDownloadSourceProbe"/>。
    ///
    /// 这里**故意**自己造一个不带 <see cref="MirrorRewritingHandler"/> 的客户端：
    /// 公用客户端会把官方 URL 改写成镜像 URL，两端测成同一个地址。代理仍然生效
    /// （<c>ProxyConfigurator</c> 改的是 <see cref="HttpClient.DefaultProxy"/>）。
    /// </summary>
    public static IServiceCollection AddDownloadSourceProbe(this IServiceCollection services)
    {
        services.AddSingleton<IDownloadSourceProbe>(_ =>
        {
            var client = new HttpClient
            {
                // 超时由 probe 自己的令牌控制，客户端超时只会让「哪个上限生效」变得不可预期。
                Timeout = Timeout.InfiniteTimeSpan,
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NebulaLauncher/1.0");
            return new DownloadSourceProbe(client);
        });

        return services;
    }
}