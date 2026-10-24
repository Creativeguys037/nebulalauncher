using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Tests;

/// <summary>
/// 自动内存推荐（PCL2 <c>PageInstanceSetup.GetRam</c> 的四段斜坡）。
///
/// 四组基准值的推导（GB → MB 的换算见 <see cref="MemoryAdvisor.SuggestMaxMemoryMb"/>：
/// 先按公式四舍五入到 1 位小数，再向下取整到 MB）：
///
/// 一、16 GiB + 100 mods（标准档）
///   floor = 0.5 + 100/150 = 1.167   t1 = 1.5 + 100/90 = 2.611
///   t2    = 2.7 + 100/50  = 4.700   t3 = 4.5 + 100/25 = 8.500
///   第 1 段 min(16, 2.611) = 2.611，剩余 13.389
///   第 2 段 min(13.389×0.7 = 9.372, 4.700-2.611 = 2.089) = 2.089，剩余 13.389 - 2.089/0.7 = 10.405
///   第 3 段 min(10.405×0.4 = 4.162, 8.500-4.700 = 3.800) = 3.800，剩余 10.405 - 3.800/0.4 = 0.905
///   第 4 段 min(0.905×0.15 = 0.136, 8.500) = 0.136
///   合计 8.636 → 8.6 GB → 8806 MB
///
/// 二、32 GiB + 0 mods
///   floor=0.5  t1=1.5  t2=2.7  t3=4.5
///   第 1 段 1.500，剩余 30.500
///   第 2 段 min(21.350, 1.200) = 1.200，剩余 30.500 - 1.200/0.7 = 28.786
///   第 3 段 min(11.514, 1.800) = 1.800，剩余 28.786 - 1.800/0.4 = 24.286
///   第 4 段 min(24.286×0.15 = 3.643, 4.500) = 3.643
///   合计 8.143 → 8.1 GB → 8294 MB（PCL2 的自动档本来保守：0 mod 时三段台阶只到 4.5 GB）
///
/// 三、8 GiB + 0 mods
///   第 1 段 1.500，剩余 6.500
///   第 2 段 min(4.550, 1.200) = 1.200，剩余 6.500 - 1.714 = 4.786
///   第 3 段 min(1.914, 1.800) = 1.800，剩余 4.786 - 4.500 = 0.286
///   第 4 段 min(0.286×0.15 = 0.043, 4.500) = 0.043
///   合计 4.543 → 4.5 GB → 4608 MB
///
/// 四、availableBytes = 0（容器 / WSL 探测不到内存）→ 4096 MB 兜底。
/// </summary>
public class CoreLaunch_MemoryAdvisorTests
{
    private const long Gib = 1024L * 1024 * 1024;

    // ==================== 验收基准四组 ====================

    [Fact]
    public void SuggestMaxMemoryMb_16GiBWith100Mods_IsInExpectedRange()
    {
        var mb = MemoryAdvisor.SuggestMaxMemoryMb(16 * Gib, 100, hasOptiFine: false);

        Assert.InRange(mb, 8600, 8900);
        Assert.Equal(8806, mb);
    }

    [Fact]
    public void SuggestMaxMemoryMb_32GiBNoMods_IsInExpectedRange()
    {
        var mb = MemoryAdvisor.SuggestMaxMemoryMb(32 * Gib, 0, hasOptiFine: false);

        Assert.InRange(mb, 8000, 8600);
        Assert.Equal(8294, mb);
    }

    [Fact]
    public void SuggestMaxMemoryMb_8GiBNoMods_MatchesFormula()
    {
        var mb = MemoryAdvisor.SuggestMaxMemoryMb(8 * Gib, 0, hasOptiFine: false);

        // 4.543 GB → 4.5 GB → 4608 MB。8 GB 机器上四段斜坡只走到第三段，
        // 第四段几乎吃不到东西——这是 PCL2 原式的保守之处，不是实现偏差。
        Assert.Equal(4608, mb);
    }

    [Fact]
    public void SuggestMaxMemoryMb_AvailableBytesZero_ReturnsFallback()
    {
        Assert.Equal(MemoryAdvisor.FallbackMaxMemoryMb, MemoryAdvisor.SuggestMaxMemoryMb(0, 0, false));
        Assert.Equal(4096, MemoryAdvisor.SuggestMaxMemoryMb(0, 0, false));
    }

    [Fact]
    public void SuggestMaxMemoryMb_AvailableBytesNegative_ReturnsFallback()
    {
        Assert.Equal(MemoryAdvisor.FallbackMaxMemoryMb, MemoryAdvisor.SuggestMaxMemoryMb(-1, 50, true));
    }

    // ==================== 曲线行为 ====================

    [Fact]
    public void SuggestMaxMemoryGb_MoreMods_RecommendsMore()
    {
        var none = MemoryAdvisor.SuggestMaxMemoryGb(16, 0, false);
        var some = MemoryAdvisor.SuggestMaxMemoryGb(16, 100, false);
        var many = MemoryAdvisor.SuggestMaxMemoryGb(16, 300, false);

        Assert.True(some > none, $"100 个模组应比 0 个多：{none} → {some}");
        Assert.True(many > some, $"300 个模组应比 100 个多：{some} → {many}");
    }

    [Fact]
    public void SuggestMaxMemoryGb_MoreMemory_RecommendsMore()
    {
        var small = MemoryAdvisor.SuggestMaxMemoryGb(8, 0, false);
        var large = MemoryAdvisor.SuggestMaxMemoryGb(32, 0, false);

        Assert.True(large > small, $"{small} → {large}");
    }

    [Fact]
    public void SuggestMaxMemoryGb_NegativeModCount_TreatedAsZero()
    {
        Assert.Equal(
            MemoryAdvisor.SuggestMaxMemoryGb(16, 0, false),
            MemoryAdvisor.SuggestMaxMemoryGb(16, -5, false));
    }

    [Fact]
    public void SuggestMaxMemoryGb_OptiFine_RecommendsMoreThanVanilla()
    {
        var vanilla = MemoryAdvisor.SuggestMaxMemoryGb(16, 20, false);
        var optifine = MemoryAdvisor.SuggestMaxMemoryGb(16, 20, true);

        Assert.True(optifine > vanilla, $"OptiFine 档台阶更高：{vanilla} → {optifine}");
    }

    [Fact]
    public void SuggestMaxMemoryGb_TinyMemory_FallsBackToFloor()
    {
        // 1 GB 可用时四段斜坡的增量全为负，结果由 max(结果, floor) 兜到 0.5 GB。
        Assert.Equal(0.5, MemoryAdvisor.SuggestMaxMemoryGb(1, 0, false));
    }

    [Fact]
    public void SuggestMaxMemoryGb_RoundedToOneDecimal()
    {
        var gb = MemoryAdvisor.SuggestMaxMemoryGb(16, 100, false);

        Assert.Equal(Math.Round(gb, 1), gb);
    }

    [Fact]
    public void SuggestMaxMemoryMb_AlwaysAtLeastSaneMinimum()
    {
        // 1 字节可用内存也会被抬到 512 MB：更小的值连原版客户端都起不来。
        Assert.Equal(MemoryAdvisor.MinSaneMaxMemoryMb, MemoryAdvisor.SuggestMaxMemoryMb(1, 0, false));
        Assert.True(MemoryAdvisor.SuggestMaxMemoryMb(1, 0, false) > 0);
    }

    // ==================== 探测 ====================

    [Fact]
    public void TotalAvailableBytes_IsNotNegative()
    {
        // 具体数值取决于机器；这里只要求「不抛异常且不为负」，
        // 探测不到时返回 0，由调用方兜底。
        Assert.True(MemoryAdvisor.TotalAvailableBytes() >= 0);
    }
}
