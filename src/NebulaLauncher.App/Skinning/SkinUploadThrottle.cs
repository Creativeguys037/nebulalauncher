namespace NebulaLauncher.Skinning;

/// <summary>
/// 换肤请求的界面层节流（票 #44 AC6：429 / 5xx 可重试，但要限速）。
/// 冷却提示按通道给称呼（票 #52）：官方通道与皮肤站通道各有一个实例，说的话不该混。
///
/// 限速放在这里而不是客户端：客户端一次调用只发一次请求（见 <see cref="MojangSkinClient"/>），
/// 「刚被限流还要不要立刻再点」是纯界面的决定。也不放在 HTTP 层的自动重试里——
/// 自动重试会把一次 429 放大成两次 429。
///
/// 时钟是注入的：测试要能把「15 秒之后」瞬移过去，而真的 <c>Task.Delay</c> 在单元测试里既慢又不稳。
/// </summary>
public sealed class SkinUploadThrottle
{
    /// <summary>默认冷却 15 秒。够短到用户愿意等，够长到不像在打服务端。</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(15);

    /// <summary>官方通道的称呼。它是缺省值，所以官方侧的提示与加通道名之前逐字一致。</summary>
    public const string OfficialChannelName = "官方服务";

    /// <summary>皮肤站通道的称呼。第三方皮肤站被限流时不该说成「官方服务」（票 #52）。</summary>
    public const string AuthlibChannelName = "皮肤站";

    private readonly Func<DateTimeOffset> _clock;

    /// <summary>这条通道在冷却提示里的称呼。</summary>
    private readonly string _channelName;

    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <param name="cooldown">一次冷却的时长，缺省 <see cref="DefaultCooldown"/>。</param>
    /// <param name="clock">取当前时间的时钟，缺省 <c>DateTimeOffset.UtcNow</c>。</param>
    /// <param name="channelName">这条通道的称呼，见 <see cref="OfficialChannelName"/> 与 <see cref="AuthlibChannelName"/>。</param>
    public SkinUploadThrottle(
        TimeSpan? cooldown = null,
        Func<DateTimeOffset>? clock = null,
        string channelName = OfficialChannelName)
    {
        Cooldown = cooldown ?? DefaultCooldown;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _channelName = channelName;
    }

    /// <summary>一次冷却的时长。</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>现在还在冷却里吗。</summary>
    public bool IsBlocked => Remaining > TimeSpan.Zero;

    /// <summary>冷却还剩多久；不在冷却时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan Remaining
    {
        get
        {
            var remaining = _blockedUntil - _clock();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>冷却中给界面看的一句话；不在冷却时为 null。</summary>
    public string? BlockedText
    {
        get
        {
            if (!IsBlocked)
            {
                return null;
            }

            // 向上取整：剩 0.4 秒时说「1 秒」比说「0 秒」合理，说 0 秒会让人以为能点了。
            var seconds = (int)Math.Ceiling(Remaining.TotalSeconds);
            return seconds <= 1
                ? $"{_channelName}刚拒绝了一次请求，再等 1 秒就能重试。"
                : $"{_channelName}刚拒绝了一次请求，再等 {seconds} 秒就能重试。";
        }
    }

    /// <summary>
    /// 记一次「可重试」的失败（429 / 5xx / 断网），开始冷却。
    /// 不可重试的失败（401 / 400）不该调这里：那是「改了才可能成功」，等一会儿没有意义。
    /// </summary>
    public void MarkRetryableFailure() => _blockedUntil = _clock() + Cooldown;

    /// <summary>成功一次就解除冷却：上一条结果已经不描述现在的服务端了。</summary>
    public void MarkSuccess() => _blockedUntil = DateTimeOffset.MinValue;
}
