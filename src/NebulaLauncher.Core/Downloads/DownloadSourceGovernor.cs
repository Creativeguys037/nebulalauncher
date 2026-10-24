using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 会话级下载源劣化状态机（自动模式的大脑）。
///
/// ## 为什么需要它
/// BMCLAPI 在国内通常更快，但它会间歇性 404 / 302 到不可达的第三方 CDN。
/// 用户选了「自动」时不该被这种间歇性故障卡住：前几个文件走镜像，一旦判定镜像劣化，
/// 本次会话剩下的请求直接走官方，并把原因显示在下载页上。重启启动器即重新评估，
/// 不做持久化——镜像的可用性按天变化，把「昨天劣化」写进设置只会让用户不明所以。
///
/// ## 判据（每次请求结束回灌一次 <see cref="DownloadAttemptResult"/>）
/// 1. 只有策略为 <see cref="DownloadSource.Auto"/>、且当前处于镜像优先阶段时才采样；
///    手动档（Official / Mirror）完全不采样、不受影响。
/// 2. 首选源不是镜像的样本不参与镜像判据。
/// 3. 镜像失败（含「镜像失败后回退成功」）记一次**连续失败**；镜像直接成功则把连续失败清零。
/// 4. 连续失败 ≥ <see cref="MirrorFailureStreakThreshold"/>（3）次 → 降级，原因「镜像连续失败 3 次」。
/// 5. 「镜像放弃前耗时 − 官方回退成功耗时 &gt; <see cref="SlowComparisonMargin"/>（2 秒）」记一次慢速样本；
///    ≥ <see cref="MirrorSlowSampleThreshold"/>（2）次 → 降级，原因「镜像比官方慢 2 秒以上 2 次」。
/// 6. 降级后 <see cref="MirrorRetryWindow"/>（10 分钟）内 <see cref="Effective"/> 固定为官方且不采样；
///    窗口过期的第一次求值自动回到镜像优先并清空计数（半开重试）。
/// 7. 切换策略会清空本会话的全部劣化状态——用户重新选了方向，旧结论不再适用。
///
/// ## 时间
/// 所有时间都来自构造函数注入的 <c>Func&lt;DateTimeOffset&gt;</c>，绝不直接读
/// <see cref="DateTimeOffset.UtcNow"/>：否则「10 分钟窗口」这类判据只能靠真实等待，无法单测。
/// </summary>
public sealed class DownloadSourceGovernor : IDownloadSourceProvider, IDownloadSourceTelemetry
{
    /// <summary>连续多少次镜像失败判定劣化。</summary>
    public const int MirrorFailureStreakThreshold = 3;

    /// <summary>多少个「镜像明显慢于官方」的样本判定劣化。</summary>
    public const int MirrorSlowSampleThreshold = 2;

    /// <summary>降级后多久之内不再尝试镜像。</summary>
    public static readonly TimeSpan MirrorRetryWindow = TimeSpan.FromMinutes(10);

    /// <summary>「慢于官方」的判定阈值。</summary>
    public static readonly TimeSpan SlowComparisonMargin = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    private DownloadSource _strategy;
    private int _mirrorFailureStreak;
    private int _mirrorSlowSamples;
    private DateTimeOffset? _degradedAtUtc;
    private string? _reason;

    /// <param name="clock">时间源。测试注入可控时钟；生产用 <see cref="DateTimeOffset.UtcNow"/>。</param>
    /// <param name="strategy">
    /// 初始策略。默认 <see cref="DownloadSource.Mirror"/>，与 <see cref="AppSettings.DownloadSource"/>
    /// 的历史默认值一致；真实策略由设置服务在 DI 注册后写入。
    /// </param>
    public DownloadSourceGovernor(Func<DateTimeOffset>? clock = null, DownloadSource strategy = DownloadSource.Mirror)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _strategy = NormalizeStrategy(strategy);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public DownloadSource Strategy
    {
        get
        {
            lock (_gate)
            {
                return _strategy;
            }
        }
    }

    /// <inheritdoc />
    public DownloadSource Effective
    {
        get
        {
            lock (_gate)
            {
                return EvaluateLocked();
            }
        }
    }

    /// <summary>
    /// <see cref="Effective"/> 的别名：状态机的对外输出就叫「本次会话首选源」。
    /// 两者必须同源，保留两个名字只是为了接口（Effective）与业务语义（PreferredSource）都好读。
    /// </summary>
    public DownloadSource PreferredSource => Effective;

    /// <inheritdoc />
    public string? DegradeReason
    {
        get
        {
            lock (_gate)
            {
                return IsDegradedLocked() ? _reason : null;
            }
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? DegradedAtUtc
    {
        get
        {
            lock (_gate)
            {
                return IsDegradedLocked() ? _degradedAtUtc : null;
            }
        }
    }

    /// <summary>
    /// 设置策略（来自设置服务）。策略变化会清空本会话的劣化状态并触发 <see cref="Changed"/>。
    /// 非法值（枚举越界）按镜像优先处理。
    /// </summary>
    public void SetStrategy(DownloadSource strategy)
    {
        var normalized = NormalizeStrategy(strategy);

        lock (_gate)
        {
            if (_strategy == normalized)
            {
                return;
            }

            _strategy = normalized;
            ResetLocked();
        }

        RaiseChanged();
    }

    /// <summary>
    /// 回灌一次请求结果。手动档、已降级期间、以及首选源不是镜像的样本都会被忽略。
    /// </summary>
    public void Report(DownloadAttemptResult result)
    {
        string? degradedReason = null;

        lock (_gate)
        {
            if (_strategy != DownloadSource.Auto)
            {
                return;
            }

            // 已降级期间不做镜像采样：这段时间的请求本来就走官方，结果不反映镜像质量。
            if (EvaluateLocked() != DownloadSource.Mirror)
            {
                return;
            }

            if (result.PrimarySource != DownloadSource.Mirror)
            {
                return;
            }

            if (result.PrimarySucceeded)
            {
                _mirrorFailureStreak = 0;
                return;
            }

            _mirrorFailureStreak++;

            if (result.FallbackUsed && result.FallbackElapsed > TimeSpan.Zero)
            {
                var margin = result.PrimaryElapsed - result.FallbackElapsed;
                if (margin > SlowComparisonMargin)
                {
                    _mirrorSlowSamples++;
                }
            }

            if (_mirrorFailureStreak >= MirrorFailureStreakThreshold)
            {
                degradedReason = $"镜像连续失败 {MirrorFailureStreakThreshold} 次";
            }
            else if (_mirrorSlowSamples >= MirrorSlowSampleThreshold)
            {
                degradedReason = $"镜像比官方慢 {SlowComparisonMargin.TotalSeconds:0} 秒以上 {MirrorSlowSampleThreshold} 次";
            }

            if (degradedReason is not null)
            {
                _degradedAtUtc = _clock();
                _reason = degradedReason;
                _mirrorFailureStreak = 0;
                _mirrorSlowSamples = 0;
            }
        }

        if (degradedReason is not null)
        {
            RaiseChanged();
        }
    }

    /// <summary>清空本次会话的劣化状态（保留策略）。诊断/测试用。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            ResetLocked();
        }

        RaiseChanged();
    }

    /// <summary>求值当前首选源；顺手处理「降级窗口已过期」的重置。调用方必须持有 <see cref="_gate"/>。</summary>
    private DownloadSource EvaluateLocked()
    {
        if (_strategy != DownloadSource.Auto)
        {
            return _strategy;
        }

        if (IsDegradedLocked())
        {
            return DownloadSource.Official;
        }

        // 窗口过期（或从未降级）：回到镜像优先，清掉上一轮的计数做半开重试。
        if (_degradedAtUtc is not null)
        {
            ResetLocked();
        }

        return DownloadSource.Mirror;
    }

    /// <summary>当前是否处于降级窗口内。调用方必须持有 <see cref="_gate"/>。</summary>
    private bool IsDegradedLocked()
    {
        if (_strategy != DownloadSource.Auto || _degradedAtUtc is not { } at)
        {
            return false;
        }

        return _clock() - at < MirrorRetryWindow;
    }

    private void ResetLocked()
    {
        _degradedAtUtc = null;
        _reason = null;
        _mirrorFailureStreak = 0;
        _mirrorSlowSamples = 0;
    }

    /// <summary>把任意枚举值收敛到三个合法值之一（越界值按镜像优先）。</summary>
    private static DownloadSource NormalizeStrategy(DownloadSource strategy) => strategy switch
    {
        DownloadSource.Auto => DownloadSource.Auto,
        DownloadSource.Official => DownloadSource.Official,
        _ => DownloadSource.Mirror,
    };

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 界面订阅者抛异常不能把下载线程带崩：状态机已经更新完毕，通知失败只是 UI 少刷新一次。
        }
    }
}
