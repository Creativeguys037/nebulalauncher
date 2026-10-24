using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 首页「取消等待后移交后台」的两道闸门（REVIEW-R34-R36 P3-3 / P3-14）。
///
/// 这两个判定很窄，但错一边就会出用户可见的问题：
/// 会话判错 → 旧会话退出把新启动的面板改成「已退出」；代次判错 → 旧进度覆盖新面板。
/// 用纯函数测，避免为了测两个 if 去拉整套启动流程。
/// </summary>
public class AppHome_DetachedExitTests
{
    [Theory]
    [InlineData("sess-a", "sess-a", true)]
    [InlineData("sess-a", "sess-b", false)]
    [InlineData("sess-a", "", false)]
    [InlineData(null, "sess-a", false)]
    [InlineData("", "sess-a", false)]
    public void 会话判定_只接受与当前记住的会话一致(string? current, string incoming, bool expected)
    {
        Assert.Equal(expected, HomeViewModel.ShouldApplyDetachedExit(current, incoming));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    [InlineData(4, 3, false)]
    [InlineData(0, 0, true)]
    public void 代次判定_只有相等才是当前代(int expected, int current, bool matches)
    {
        Assert.Equal(matches, HomeViewModel.IsCurrentLaunchGeneration(expected, current));
    }
}
