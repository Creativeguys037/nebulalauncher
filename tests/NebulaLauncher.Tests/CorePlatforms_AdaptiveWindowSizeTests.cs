using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// 窗口最小尺寸的「由内容算出」判据（票 #51）。
///
/// 为什么单独测：真正的遮挡只有手工拖窗口才看得见，而窗口与控件是 Avalonia 类型，
/// 本测试项目不许碰（见 CONVENTIONS.md §8.2）。所以把算尺寸这一步收进 Core 的纯函数，
/// 用它钉住三件事——
/// 1) 加高 footer 会让最小高度跟着涨（这正是票 #40 引入遮挡后需要的行为）；
/// 2) 测不出内容时按住兜底下限，不能算出 0 或负数把窗口锁死；
/// 3) 常量永远只是地板，算出值更大时以算出值为准。
/// </summary>
public class CorePlatforms_AdaptiveWindowSizeTests
{
    /// <summary>
    /// 票 #51 分诊时给出的那组整数（标题栏 46、导航 346、footer 312、侧边栏 228）。
    /// 真机实测会更大（导航 404、footer 329.3 —— 侧边栏项数比票面估算时多），这里只求一组整数
    /// 把公式钉住，不追实测值。
    /// </summary>
    private static SidebarMeasurements Measured() => new(46, 346, 312, 228);

    [Fact]
    public void Compute_SumsTitleBarNavigationAndFooter()
    {
        var size = AdaptiveWindowSize.Compute(Measured());

        // 46 + 346 + 312 = 704：比常量 660 多 44，多出来的正是「世界」那项被压掉的那截。
        Assert.Equal(704, size.Height);
        Assert.Equal(1024, size.Width);
    }

    [Fact]
    public void Compute_FollowsSidebarContentGrowth()
    {
        // 验收里的「往 footer 临时加一行文字」：footer 涨 24，最小高度必须跟着涨 24，
        // 而不是停在某个写死的数上。这条是「算出来的，不是碰巧对上」的证据。
        var before = AdaptiveWindowSize.Compute(Measured());
        var after = AdaptiveWindowSize.Compute(Measured() with { FooterHeight = 336 });

        Assert.Equal(before.Height + 24, after.Height);
        Assert.Equal(before.Width, after.Width);
    }

    [Fact]
    public void Compute_WidensWithSidebarContent()
    {
        // 侧边栏变宽（以后加了更长的文案或更宽的控件）：最小宽度 = 侧边栏自然宽 + 内容区最小宽。
        var size = AdaptiveWindowSize.Compute(Measured() with { SidebarWidth = 260 });

        Assert.Equal(260 + 796, size.Width);
    }

    [Fact]
    public void Compute_SidebarBelowFloor_UsesFloor()
    {
        // 侧边栏内容比 228 窄时按 228 算：宽度有下限，不能把内容挤扁。
        var size = AdaptiveWindowSize.Compute(Measured() with { SidebarWidth = 120 });

        Assert.Equal(AdaptiveWindowSize.SidebarMinWidth + AdaptiveWindowSize.ContentMinWidth, size.Width);
    }

    [Fact]
    public void Compute_NarrowerThanContentBaseline_StillUsesFloor()
    {
        var size = AdaptiveWindowSize.Compute(Measured() with { SidebarWidth = 0 });

        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
    }

    [Fact]
    public void Compute_UnmeasuredContent_FallsBackToConstants()
    {
        // 首帧之前、或元素还没进可视树时拿到的都是 0：必须回落到兜底下限，
        // 否则算出 0 会把窗口锁成一条线。
        var size = AdaptiveWindowSize.Compute(default);

        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
        Assert.Equal(AppSettings.MinimumLauncherWindowHeight, size.Height);
    }

    [Fact]
    public void Compute_MeasuredBelowConstants_KeepsConstants()
    {
        // 内容比常量小的时候常量仍是地板：常量语义是「兜底下限」，不是「上一次的实测值」。
        var size = AdaptiveWindowSize.Compute(new SidebarMeasurements(46, 100, 100, 228));

        Assert.Equal(AppSettings.MinimumLauncherWindowHeight, size.Height);
        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
    }

    [Fact]
    public void Compute_RoundsFractionalPixelsUp()
    {
        // 布局测量经常带小数；向下取整会让最小尺寸比内容矮一点点，那一点点正好就是裁切。
        var size = AdaptiveWindowSize.Compute(new SidebarMeasurements(46, 346.4, 312.2, 228));

        Assert.Equal(705, size.Height);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Compute_UnusableMeasurement_FallsBackToConstants(double value)
    {
        var size = AdaptiveWindowSize.Compute(Measured() with { NavigationHeight = value });

        Assert.Equal(AppSettings.MinimumLauncherWindowHeight, size.Height);
        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
    }
}
