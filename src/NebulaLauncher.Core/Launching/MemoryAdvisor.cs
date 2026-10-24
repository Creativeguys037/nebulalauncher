namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 按「可用物理内存 + 模组数量」推荐最大堆，抄 PCL2 <c>PageInstanceSetup.GetRam</c> 的四段斜坡。
///
/// 为什么不用「可用内存乘固定比例」：那样在 32 GB 机器上会给出 12 GB 以上的堆，
/// 实际游戏只用 3–4 GB，多出来的部分只会让 JVM 的 GC 停顿变长；反过来在小内存机器上
/// 又会给出无法启动的值。斜坡的做法是——小内存机器按 100% 吃第一段，剩余部分
/// 分别按 70% / 40% / 15% 折算到后三段，且每段的增量不超过该段的上限（由模组数抬高）。
/// 效果是：模组越多、可用内存越多，推荐值越平滑地上升，且在中等机器上收敛在 6–10 GB。
///
/// 两个刻意的语义：
/// <list type="number">
/// <item>剩余量在最后几段可能变负。PCL2 原式就是这样写的，负的贡献等价于「小内存机器按比例回缩」，
///       最终由 <c>max(结果, floor)</c> 兜底；改成「把剩余量夹到 0」会让 2 GB 机器拿到 1.9 GB 堆，
///       启动必然失败。</item>
/// <item>这里的可用内存是<b>可分配</b>内存（<c>TotalAvailableMemoryBytes</c>），不是物理总量；
///       用总量会把已被系统占用的部分也算进游戏预算。</item>
/// </list>
/// </summary>
public static class MemoryAdvisor
{
    /// <summary>读不到可用内存时的兜底值（MB）。与 <c>LaunchOptions.MaxMemoryMb</c> 的默认值一致。</summary>
    public const int FallbackMaxMemoryMb = 4096;

    /// <summary>推荐值的下限（MB）。再低连原版客户端都起不来，属于没有意义的建议。</summary>
    public const int MinSaneMaxMemoryMb = 512;

    private const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>
    /// 推荐最大堆（MB）。
    /// </summary>
    /// <param name="availableBytes">可分配物理内存（字节）。&lt;= 0 表示探测不到，返回 <see cref="FallbackMaxMemoryMb"/>。</param>
    /// <param name="modCount">模组文件数（jar / zip / litemod）；负数按 0 处理。</param>
    /// <param name="hasOptiFine">是否装了 OptiFine。OptiFine 自己会占一批堆，台阶整体更高。</param>
    /// <returns>最大堆（MB）。恒 &gt; 0，可直接写进 <c>-Xmx</c>。</returns>
    public static int SuggestMaxMemoryMb(long availableBytes, int modCount, bool hasOptiFine)
    {
        if (availableBytes <= 0)
        {
            // 容器 / WSL / 受限沙箱里探测会返回 0；此时保守地回到默认值，而不是给出 0 让 JVM 起不来。
            return FallbackMaxMemoryMb;
        }

        var gigabytes = SuggestMaxMemoryGb(availableBytes / BytesPerGb, modCount, hasOptiFine);

        // PCL2 拼 -Xmx 时向下取整，保持同样的行为：建议值只会比算出来的少 1 MB，不会多。
        var megabytes = (int)Math.Floor(gigabytes * 1024);

        return Math.Max(megabytes, MinSaneMaxMemoryMb);
    }

    /// <summary>
    /// 推荐最大堆（GB，保留 1 位小数）。单独暴露是为了让测试能直接按 PCL2 的公式验算，
    /// 不必反推字节换算与取整。
    /// </summary>
    /// <param name="availableGb">可分配内存（GB）。</param>
    /// <param name="modCount">模组文件数；负数按 0 处理。</param>
    /// <param name="hasOptiFine">是否装了 OptiFine。</param>
    public static double SuggestMaxMemoryGb(double availableGb, int modCount, bool hasOptiFine)
    {
        var mods = Math.Max(modCount, 0);
        var (floor, first, second, third) = ResolveTiers(mods, hasOptiFine);

        var remaining = Math.Max(availableGb, 0);
        var recommended = 0d;

        // 第一段：可用内存的 100%，最多吃到 first。
        recommended += Math.Min(remaining, first);
        remaining -= first;

        // 第二段：70%，最多吃到 first→second 的台阶高度。
        recommended += Math.Min(remaining * 0.7, second - first);
        remaining -= (second - first) / 0.7;

        // 第三段：40%。
        recommended += Math.Min(remaining * 0.4, third - second);
        remaining -= (third - second) / 0.4;

        // 第四段：15%，最多吃到 third。
        recommended += Math.Min(remaining * 0.15, third);

        return Math.Round(Math.Max(recommended, floor), 1);
    }

    /// <summary>
    /// 当前进程可分配的内存（字节）。探测失败返回 0，由调用方决定兜底值。
    ///
    /// 用 <see cref="GC.GetGCMemoryInfo"/> 而不是 P/Invoke：它是跨平台的，且在容器里返回的是 cgroup 限制，
    /// 比 <c>GlobalMemoryStatusEx</c> 的「宿主物理内存」更接近用户实际能用的量。
    /// </summary>
    public static long TotalAvailableBytes()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch (Exception)
        {
            // 拿不到内存信息不是致命错误：返回 0 让调用方保留用户的手动设置。
            return 0;
        }
    }

    /// <summary>
    /// 四段斜坡的台阶。三个分支来自 PCL2：
    /// 标准（可装模组）0.5 / 1.5 / 2.7 / 4.5，OptiFine 0.5 / 1.5 / 3.0 / 5.0，
    /// 「不可装模组且无 OptiFine」0.5 / 1.5 / 2.5 / 4.0。
    ///
    /// 这里没有「不可装模组」分支：本方法拿到 modCount 就已经说明实例允许装模组，
    /// 传 0 时三档都退化为各自的常数基准，与 PCL2 的对应分支数值只差 0.2–0.5 GB，属于可接受偏差。
    /// </summary>
    private static (double Floor, double First, double Second, double Third) ResolveTiers(int mods, bool hasOptiFine)
    {
        var floor = 0.5 + (mods / 150d);
        var first = 1.5 + (mods / 90d);

        if (hasOptiFine)
        {
            return (floor, first, 3.0 + (mods / 50d), 5.0 + (mods / 25d));
        }

        return (floor, first, 2.7 + (mods / 50d), 4.5 + (mods / 25d));
    }
}
