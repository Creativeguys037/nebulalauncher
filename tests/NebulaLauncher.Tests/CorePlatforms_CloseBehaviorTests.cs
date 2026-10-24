using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「关闭到托盘」的关闭判据。
///
/// 三种输入各有一种会把进程判死的走法：托盘建失败时假装隐藏 → 幽灵进程；
/// 截图模式下拦截关闭 → 无人值守脚本永远等不到退出；开关关掉却还在拦截 → 用户关不掉启动器。
/// 所以 2×2×2 全表都要钉住，不能只测「理想路径」。
/// </summary>
public sealed class CorePlatforms_CloseBehaviorTests
{
    [Theory]
    [InlineData(false, false, false, true)] // 开关关闭：退出
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]  // 开关打开但托盘没建出来：必须退出，不能留下无入口的隐藏窗口
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]  // 唯一会隐藏窗口的组合
    [InlineData(true, true, true, true)]    // 截图模式优先：一律不拦截
    public void ShouldExitOnClose_CoversAllSwitchCombinations(
        bool closeToTray,
        bool trayAvailable,
        bool isScreenshotMode,
        bool expected)
    {
        Assert.Equal(expected, CloseBehavior.ShouldExitOnClose(closeToTray, trayAvailable, isScreenshotMode));
    }

    [Fact]
    public void ShouldExitOnClose_OnlyHidesWhenTrayUsableAndEnabled()
    {
        // 反向断言：除「开关开 + 托盘可用 + 非截图」外，其余七种组合都必须放行关闭。
        var combinations = new List<(bool CloseToTray, bool TrayAvailable, bool Screenshot)>
        {
            (false, false, false),
            (false, false, true),
            (false, true, false),
            (false, true, true),
            (true, false, false),
            (true, false, true),
            (true, true, false),
            (true, true, true),
        };

        var hiding = combinations
            .Where(c => !CloseBehavior.ShouldExitOnClose(c.CloseToTray, c.TrayAvailable, c.Screenshot))
            .ToList();

        var only = Assert.Single(hiding);
        Assert.Equal((true, true, false), only);
    }
}
