using System.Diagnostics;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 下载限速器：高水位令牌桶，按字节速率放行读循环。
///
/// 为什么是「按需补充」而不是后台 <see cref="Timer"/>：
/// 定时器会在线程池上长期排一个 100ms 的任务，下载空闲时也在空转；
/// 按需补充只在真正读数据时结算，空闲零成本，令牌行为完全等价
/// （PCL2 的 <c>ModNet</c> 也是每 100ms 补 <c>High/10</c>，见 B §1.4）。
///
/// 为什么桶容量只取半个块的时间（<see cref="BurstSeconds"/>）：容量决定「可以瞬间突发多少字节」。
/// 取满 1 秒会让「上限 100 KB/s」在头 500ms 就能放行 1 秒的量（100 KB 初始 + 50 KB 补充 = 150 KB），
/// 实测在 500ms 窗口里能读到 160 KB，和「限速」的直观预期不符；
/// 取 0.5 秒后 500ms 窗口内最多 50 KB + 50 KB = 100 KB，突发被压到半秒。
/// 容量小于一个读块（80 KiB）时额度按补充周期分片消费，不会死等。
///
/// 线程安全：所有状态都在 <see cref="_gate"/> 内，多个并发下载共享同一个实例
/// 就是「全局总带宽上限」的语义。
/// </summary>
public sealed class SpeedLimiter
{
    /// <summary>令牌补充周期。与 PCL2 的 100ms 一致，粒度太细会让等待循环空转。</summary>
    private const long RefillPeriodMs = 100;

    /// <summary>桶容量 = 速率 × 这个秒数。见类注释里为什么不是 1 秒。</summary>
    private const double BurstSeconds = 0.5;

    private readonly object _gate = new();
    private readonly double _bytesPerSecond;
    private readonly double _capacity;
    private readonly double _refillPerPeriod;

    private double _tokens;
    private long _lastRefillTimestamp;

    /// <param name="limitKbPerSecond">速度上限（KB/s）。小于等于 0 表示不限速。</param>
    public SpeedLimiter(int limitKbPerSecond)
    {
        _bytesPerSecond = KbToBytesPerSecond(limitKbPerSecond);
        _capacity = _bytesPerSecond * BurstSeconds;
        _refillPerPeriod = _bytesPerSecond / (1000.0 / RefillPeriodMs);

        // 满桶启动：t=0 空等一个补充周期会让「刚开始下载」看起来像卡住。
        _tokens = _capacity;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>限速值（字节/秒）。0 表示不限速。</summary>
    public double BytesPerSecond => _bytesPerSecond;

    /// <summary>是否真正限速。调用方据此在热路径上跳过 <see cref="AcquireAsync"/>。</summary>
    public bool IsEnabled => _bytesPerSecond > 0;

    /// <summary>把设置里的 KB/s 换成 B/s；0 或负数仍然是 0（不限速）。</summary>
    public static long KbToBytesPerSecond(int limitKbPerSecond) =>
        limitKbPerSecond <= 0 ? 0 : limitKbPerSecond * 1024L;

    /// <summary>
    /// 申请 <paramref name="bytes"/> 个字节的发送额度；额度不足时异步等待。
    ///
    /// 不限速时直接返回已完成任务（调用方也只在 <see cref="IsEnabled"/> 时才调这里）。
    /// 等待被切成不超过一个补充周期的片段，保证取消能在一个周期内生效。
    ///
    /// 单次请求可能大于桶容量（限速低于读块大小时必然如此），所以额度是分片消费的：
    /// 每次先把桶里现有的令牌全部取走，剩下的等下一个补充周期，不会死等一个永远凑不满的桶。
    /// </summary>
    public async Task AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (!IsEnabled || bytes <= 0)
        {
            return;
        }

        var remaining = (double)bytes;
        while (true)
        {
            double waitMilliseconds;

            lock (_gate)
            {
                RefillLocked();

                if (_tokens > 0)
                {
                    var take = Math.Min(_tokens, remaining);
                    _tokens -= take;
                    remaining -= take;

                    if (remaining <= 0)
                    {
                        return;
                    }
                }

                waitMilliseconds = remaining / _bytesPerSecond * 1000.0;
            }

            var delay = Math.Clamp(waitMilliseconds, 1, RefillPeriodMs);
            await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 退还多扣的额度。读循环按整块大小预扣、按实际读到的字节结算，
    /// 不退还的话「小块读取」（小文件、网络碎片）会被按整块计费，实际速率远低于设置值。
    /// </summary>
    public void Refund(int bytes)
    {
        if (!IsEnabled || bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _tokens = Math.Min(_capacity, _tokens + bytes);
        }
    }

    /// <summary>把「上次结算之后经过的整周期」补成令牌，不足一个周期的零头留到下次。</summary>
    private void RefillLocked()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastRefillTimestamp) * 1000.0 / Stopwatch.Frequency;

        if (elapsedMs < RefillPeriodMs)
        {
            return;
        }

        var periods = Math.Floor(elapsedMs / RefillPeriodMs);
        _tokens = Math.Min(_capacity, _tokens + (periods * _refillPerPeriod));

        // 只结算整周期，余数留给下一次，避免高频调用把零头反复抹平导致少补令牌。
        _lastRefillTimestamp += (long)(periods * RefillPeriodMs * Stopwatch.Frequency / 1000.0);
    }
}
