namespace NebulaLauncher.Core.Platforms;

/// <summary>
/// 「记住启动器窗口尺寸」的关盘判据。
///
/// 与 <see cref="CloseBehavior"/> 同一套路：判断集中在 Core 的纯函数里（可单测），
/// 窗口侧只负责执行，避免「启动恢复」与「关闭写回」两条路径各写一套分支而漂移。
/// </summary>
public static class WindowMemory
{
    /// <summary>
    /// 关闭（含隐藏到托盘）时是否把当前窗口尺寸写回设置。
    ///
    /// 两种情况下都不写：
    /// 1. 用户关掉「记住窗口大小」——此时启动与关闭两端都不许碰窗口字段；
    /// 2. 无人值守截图模式——截图进程随后就 <c>Environment.Exit</c>，写盘既没必要，
    ///    又可能把 <c>--size</c> 的验证尺寸写进真实设置。
    /// </summary>
    /// <param name="rememberWindowSize">设置页「记住窗口大小」是否打开。</param>
    /// <param name="isScreenshotMode">是否处于 <c>--screenshot</c> 无人值守截图模式。</param>
    public static bool ShouldPersist(bool rememberWindowSize, bool isScreenshotMode)
        => rememberWindowSize && !isScreenshotMode;
}
