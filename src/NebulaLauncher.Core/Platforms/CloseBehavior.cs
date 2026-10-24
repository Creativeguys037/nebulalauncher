namespace NebulaLauncher.Core.Platforms;

/// <summary>
/// 「关闭到托盘」的关闭判据。
///
/// 为什么抽成 Core 里的纯函数：判错的两种后果都很难查——要么用户点关闭后进程直接没了，
/// 要么窗口藏起来、托盘却没建出来，留下一个只能去任务管理器结束的幽灵进程。
/// 这里不碰窗口也不碰 Avalonia，四个开关组合都能在单测里钉住。
/// </summary>
public static class CloseBehavior
{
    /// <summary>
    /// 关闭主窗口时是否直接退出进程。
    /// </summary>
    /// <param name="closeToTray">设置里的「关闭到托盘」是否打开。</param>
    /// <param name="trayAvailable">
    /// 托盘图标是否真的创建成功。建失败时不能假装隐藏：窗口一藏就没有任何入口能把它叫回来。
    /// </param>
    /// <param name="isScreenshotMode">
    /// 无人值守截图模式。该模式下窗口关闭必须结束进程，否则截图脚本会等一个永远不退出的进程。
    /// </param>
    /// <returns>
    /// <c>true</c> 表示放行关闭（进程退出）；<c>false</c> 表示取消关闭并隐藏到托盘。
    /// 只有「开关打开 + 托盘可用 + 非截图模式」这一种组合会返回 <c>false</c>。
    /// </returns>
    public static bool ShouldExitOnClose(bool closeToTray, bool trayAvailable, bool isScreenshotMode)
        => isScreenshotMode || !closeToTray || !trayAvailable;
}
