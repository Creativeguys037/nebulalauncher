using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// 窗口最小尺寸的「由内容算出」判据（票 #51，票 #85 按样张竖栏改口径）。
///
/// 为什么单独测：真正的遮挡只有手工拖窗口才看得见，而窗口与控件是 Avalonia 类型，
/// 本测试项目不许碰（见 CONVENTIONS.md §8.2）。所以把算尺寸这一步收进 Core 的纯函数，
/// 用它钉住三件事——
/// 1) 导航涨多高，最小高度就跟着涨多少（这正是票 #40 引入遮挡后需要的行为）；
/// 2) 测不出内容时按住兜底下限，不能算出 0 或负数把窗口锁死；
/// 3) 常量永远只是地板，算出值更大时以算出值为准。
/// </summary>
public class CorePlatforms_AdaptiveWindowSizeTests
{
    /// <summary>
    /// 票 #85 的外壳：主列标题栏 46、竖栏 7 项 7×46=322、无独立工具区、竖栏 68。
    /// footer 取 350 只是把公式钉住，不追实测值。
    /// </summary>
    private static SidebarMeasurements Measured() => new(46, 322, 350, 68);

    [Fact]
    public void Compute_SumsTitleBarNavigationAndFooter()
    {
        var size = AdaptiveWindowSize.Compute(Measured());

        // 46 + 322 + 350 = 718。
        Assert.Equal(718, size.Height);
        // 68 + 956 = 1024，正好是设计稿的最小窗口宽。
        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
    }

    [Fact]
    public void Compute_NoFooter_UsesHeightFloor()
    {
        // 票 #85 起竖栏没有独立页脚：FooterHeight=0 是真实值，必须参与求和而不是被当成「测不出」。
        var size = AdaptiveWindowSize.Compute(new SidebarMeasurements(46, 322, 0, 68));

        // 46 + 322 + 0 = 368，低于 660 地板，所以取 660。
        Assert.Equal(AppSettings.MinimumLauncherWindowHeight, size.Height);
        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
    }

    [Fact]
    public void Compute_FollowsNavigationGrowth()
    {
        // 验收里的「往竖栏加一项」：导航涨 24，最小高度必须跟着涨 24，
        // 而不是停在某个写死的数上。这条是「算出来的，不是碰巧对上」的证据。
        var before = AdaptiveWindowSize.Compute(Measured());
        var after = AdaptiveWindowSize.Compute(Measured() with { NavigationHeight = 346 });

        Assert.Equal(before.Height + 24, after.Height);
        Assert.Equal(before.Width, after.Width);
    }

    [Fact]
    public void Compute_FollowsFooterGrowth()
    {
        var before = AdaptiveWindowSize.Compute(Measured());
        var after = AdaptiveWindowSize.Compute(Measured() with { FooterHeight = 374 });

        Assert.Equal(before.Height + 24, after.Height);
    }

    [Fact]
    public void Compute_WidensWithSidebarContent()
    {
        // 竖栏变宽（以后加了更长的文案或更宽的控件）：最小宽度 = 竖栏自然宽 + 内容区最小宽。
        var size = AdaptiveWindowSize.Compute(Measured() with { SidebarWidth = 260 });

        Assert.Equal(260 + AdaptiveWindowSize.ContentMinWidth, size.Width);
    }

    [Fact]
    public void Compute_SidebarBelowFloor_UsesFloor()
    {
        // 竖栏比 68 窄时按 68 算：宽度有下限，不能把导航图标挤扁。
        var size = AdaptiveWindowSize.Compute(Measured() with { SidebarWidth = 30 });

        Assert.Equal(
            AdaptiveWindowSize.SidebarMinWidth + AdaptiveWindowSize.ContentMinWidth,
            size.Width);
        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
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
        var size = AdaptiveWindowSize.Compute(new SidebarMeasurements(46, 100, 100, 68));

        Assert.Equal(AppSettings.MinimumLauncherWindowHeight, size.Height);
        Assert.Equal(AppSettings.MinimumLauncherWindowWidth, size.Width);
    }

    [Fact]
    public void Compute_RoundsFractionalPixelsUp()
    {
        // 布局测量经常带小数；向下取整会让最小尺寸比内容矮一点点，那一点点正好就是裁切。
        var size = AdaptiveWindowSize.Compute(new SidebarMeasurements(46, 322.4, 312.2, 68));

        Assert.Equal(681, size.Height);
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