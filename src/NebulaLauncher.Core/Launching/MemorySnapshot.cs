using System.Globalization;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 一次内存占用的快照，用来在设置页画「总量 / 其它占用 / 分配给游戏」的三段条。
///
/// ## 为什么不是三项独立的数
/// Windows 上拿不到「本机物理总量」的可靠 API（<c>GlobalMemoryStatusEx</c> 给的是含页面文件的
/// 可用量，任务管理器里的"已用"又是另一套口径）。唯一可信的两个数是：
/// <list type="bullet">
/// <item><c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> —— 本进程能分配的上限；</item>
/// <item>同一处的 <c>MemoryLoadBytes</c>（本进程已用）。</item>
/// </list>
/// 游戏将要占的那一段是用户自己设的 <c>-Xmx</c>，也不是"已用"。
/// 所以这里只暴露「上限 + 已用（由上限减可用求差）+ 将要分配给游戏」三个数，
/// 并且把文案口径写成「可分配」，不写成「本机内存」——容器 / WSL / 受限沙箱里上限是 cgroup 限制，
/// 不是物理内存，写成"本机"会直接说错。
///
/// ## 为什么比例要夹
/// 「已用 &gt; 上限」在采集时刻之间是可能出现的（探测两次、中间有分配）。比例不夹到 0..1
/// 会让进度条画出负数宽度，或者把第三段挤到看不见。夹住之后最坏情况是"看起来满了"，
/// 这在语义上是对的。
/// </summary>
public sealed record MemorySnapshot
{
    /// <summary>本进程可分配的内存上限（字节）。&lt;= 0 表示探测不到。</summary>
    public long TotalBytes { get; init; }

    /// <summary>当前剩余可分配内存（字节）。</summary>
    public long AvailableBytes { get; init; }

    /// <summary>是否拿到了可用的探测结果。</summary>
    public bool HasValue => TotalBytes > 0;

    /// <summary>已占用（字节）。由「上限 减 剩余」求差，不做四舍五入——它是比例计算的分母之一。</summary>
    public long UsedBytes => Math.Max(0, TotalBytes - Math.Max(0, AvailableBytes));

    /// <summary>已占用比例，恒在 0..1。</summary>
    public double UsedRatio => Ratio(UsedBytes);

    /// <summary>
    /// 把用户设定的游戏堆（MB）折算成占「上限」的比例，恒在 0..1。
    ///
    /// 它和三段条里「其它占用」是并列关系：游戏堆是**将要**占的，不是当前已占的，
    /// 所以两者相加可能超过 1 —— 这正好是「内存设太大了」的可见信号，调用方不要去夹两者的和。
    /// </summary>
    public double GameRatio(long gameMegabytes)
    {
        if (gameMegabytes <= 0)
        {
            return 0d;
        }

        return Ratio(gameMegabytes * 1024L * 1024L);
    }

    /// <summary>「1.9 GB」这类紧凑写法。探测不到时返回「未知」，不返回 0 GB。</summary>
    public static string FormatGigabytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知";
        }

        var gigabytes = bytes / (1024d * 1024d * 1024d);

        // 小于 1 GB 时给一位小数的 MB 更直观：0.4 GB 不如「410 MB」好读。
        return gigabytes < 1d
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} MB", bytes / (1024d * 1024d))
            : string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", gigabytes);
    }

    /// <summary>
    /// 采集当前进程的快照。读不到（<c>TotalAvailableMemoryBytes &lt;= 0</c>）时返回
    /// <see cref="HasValue"/> 为 false 的空快照，由调用方决定显示什么。
    /// </summary>
    public static MemorySnapshot Read()
    {
        var info = GC.GetGCMemoryInfo();
        return Create(info.TotalAvailableMemoryBytes, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
    }

    /// <summary>
    /// 用给定数值造快照。负数按 0 处理，剩余量大于上限时按上限处理——这两条都是为了
    /// 让比例计算永远拿得到合法输入，而不是在 UI 绑定里出现 NaN。
    /// </summary>
    public static MemorySnapshot Create(long totalBytes, long availableBytes)
    {
        if (totalBytes <= 0)
        {
            return new MemorySnapshot { TotalBytes = 0, AvailableBytes = 0 };
        }

        var available = Math.Clamp(availableBytes, 0, totalBytes);
        return new MemorySnapshot { TotalBytes = totalBytes, AvailableBytes = available };
    }

    private double Ratio(long bytes) =>
        TotalBytes <= 0 ? 0d : Math.Clamp(bytes / (double)TotalBytes, 0d, 1d);
}