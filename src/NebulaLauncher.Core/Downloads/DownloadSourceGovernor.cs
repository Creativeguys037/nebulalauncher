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
/// 1. <see cref="DownloadSource.Auto"/> 档在当前处于镜像优先阶段时采样，用于降级判定；
///    手动镜像档只采样镜像失败次数、用于产出 <see cref="ManualDegradeHint"/>，
///    **不改变 <see cref="Effective"/>、不产出 <see cref="DegradeReason"/>**；
///    手动官方档完全不采样（用户明确不要镜像，镜像好坏与他无关）。
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

    /// <summary>
    /// 手动镜像档下的连续失败计数。与 <see cref="_mirrorFailureStreak"/> 分开：
    /// 手动档的计数只用来决定「要不要提醒用户」，永远不参与降级判定，混在一起会让
    /// 「用户自己选的镜像」被自动逻辑牵着走（PITFALLS D9）。
    /// </summary>
    private int _manualMirrorFailureStreak;

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

    /// <inheritdoc />
    /// <remarks>
    /// 只在手动 <see cref="DownloadSource.Mirror"/> 档产出。文案里必须给出可执行的下一步：
    /// 这条提示的自变量是「用户没有让启动器替他决定」，所以唯一的出路就是提示他去改设置；
    /// 只说「镜像坏了」而不说「能怎么办」等于把问题丢回去。
    /// </remarks>
    public string? ManualDegradeHint
    {
        get
        {
            lock (_gate)
            {
                if (_strategy != DownloadSource.Mirror)
                {
                    return null;
                }

                if (_manualMirrorFailureStreak < MirrorFailureStreakThreshold)
                {
                    return null;
                }

                return $"镜像连续失败 {MirrorFailureStreakThreshold} 次，可能已不可用：可在设置里改用自动或官方源";
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
    /// 回灌一次请求结果。首选源不是镜像的样本、手动官方档、以及自动档已降级期间的样本都会被忽略。
    /// </summary>
    public void Report(DownloadAttemptResult result)
    {
        bool changed;

        lock (_gate)
        {
            if (result.PrimarySource != DownloadSource.Mirror)
            {
                return;
            }

            if (_strategy == DownloadSource.Mirror)
            {
                changed = ReportManualLocked(result);
            }
            else if (_strategy == DownloadSource.Auto)
            {
                changed = ReportAutoLocked(result);
            }
            else
            {
                // 手动官方档：用户已经明确不要镜像，镜像质量与他的下载无关，采样只会白建计数。
                return;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>自动档的降级判定。返回「是否刚发生降级」。调用方必须持有 <see cref="_gate"/>。</summary>
    private bool ReportAutoLocked(DownloadAttemptResult result)
    {
        // 已降级期间不做镜像采样：这段时间的请求本来就走官方，结果不反映镜像质量。
        if (EvaluateLocked() != DownloadSource.Mirror)
        {
            return false;
        }

        if (result.PrimarySucceeded)
        {
            _mirrorFailureStreak = 0;
            return false;
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

        string? degradedReason = null;

        if (_mirrorFailureStreak >= MirrorFailureStreakThreshold)
        {
            degradedReason = $"镜像连续失败 {MirrorFailureStreakThreshold} 次";
        }
        else if (_mirrorSlowSamples >= MirrorSlowSampleThreshold)
        {
            degradedReason = $"镜像比官方慢 {SlowComparisonMargin.TotalSeconds:0} 秒以上 {MirrorSlowSampleThreshold} 次";
        }

        if (degradedReason is null)
        {
            return false;
        }

        _degradedAtUtc = _clock();
        _reason = degradedReason;
        _mirrorFailureStreak = 0;
        _mirrorSlowSamples = 0;
        return true;
    }

    /// <summary>
    /// 手动镜像档的计数。返回「是否需要刷新界面」。
    ///
    /// 只数连续失败，不数慢速样本：慢速判据的意义是「自动模式该不该换源」，
    /// 而手动档换不换是用户的事，提示他只说最确定的那件事——请求正在失败。
    /// 调用方必须持有 <see cref="_gate"/>。
    /// </summary>
    private bool ReportManualLocked(DownloadAttemptResult result)
    {
        if (result.PrimarySucceeded)
        {
            var hintWasVisible = _manualMirrorFailureStreak >= MirrorFailureStreakThreshold;
            _manualMirrorFailureStreak = 0;

            // 计数归零后提示消失，界面得知道，否则会留着一条过时的警告。
            return hintWasVisible;
        }

        _manualMirrorFailureStreak++;

        // 只在「刚好越过阈值」的那一次通知：之后每条失败都通知会让状态行无谓重绘。
        return _manualMirrorFailureStreak == MirrorFailureStreakThreshold;
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
        _manualMirrorFailureStreak = 0;
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
