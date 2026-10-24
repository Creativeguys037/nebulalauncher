using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 退出守卫：还有下载任务时先问一次（PCL2 <c>FormMain.EndProgram</c>：
/// 「还有下载任务尚未完成，是否确定退出？」）。
///
/// 为什么放在壳 VM 而不是窗口 code-behind：确认是界面状态（覆盖层文案/按钮），
/// 窗口只负责一件事——<see cref="ShouldConfirmExit"/> 返回 false 时取消这次关闭。
/// 托盘「退出」不经过这里（ApplicationShutdown 由窗口直接放行），与 PCL2 的
/// <c>EndProgram(SendWarning:=False)</c> 一致。
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>用户已经确认过退出。之后再点关闭不再拦截。</summary>
    private bool _exitConfirmed;

    [ObservableProperty]
    private bool isExitDownloadConfirmOpen;

    /// <summary>覆盖层文案。下载任务数不在文案里：用户关心的是「会不会中断」，不是具体几个文件。</summary>
    public string ExitDownloadConfirmText =>
        "还有下载任务尚未完成。现在退出会中断下载，未完成的文件下次会重新校验；确定退出吗？";

    /// <summary>用户确认退出。窗口订阅它来继续关闭流程（此时 <see cref="ShouldConfirmExit"/> 已放行）。</summary>
    public event EventHandler? ExitConfirmed;

    /// <summary>
    /// 关闭窗口前调用。返回 false 表示这次关闭被拦下（覆盖层已经打开）。
    ///
    /// 没有下载队列（设计器 / 单测 / 无下载模块的宿主）或没有活动批次时一律放行：
    /// 宁可少问一次，也不能把「关不掉窗口」变成新的问题。
    /// </summary>
    public bool ShouldConfirmExit()
    {
        if (_exitConfirmed)
        {
            return true;
        }

        if (Services.GetService(typeof(DownloadQueue)) is not DownloadQueue queue || !queue.HasActiveBatch)
        {
            return true;
        }

        IsExitDownloadConfirmOpen = true;
        return false;
    }

    /// <summary>继续等待下载：只关掉覆盖层，窗口保持打开。</summary>
    [RelayCommand]
    private void CancelExitWithDownloads() => IsExitDownloadConfirmOpen = false;

    /// <summary>确认退出：放行后续关闭，并通知窗口继续（窗口会走正常的保存流程）。</summary>
    [RelayCommand]
    private void ConfirmExitWithDownloads()
    {
        IsExitDownloadConfirmOpen = false;
        _exitConfirmed = true;
        ExitConfirmed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 截图模式专用（<c>--section exit-guard</c>）：直接摆出覆盖层。
    ///
    /// 覆盖层只在「有活动下载 + 用户点关闭」时出现，截图模式不会真的下载，
    /// 没有这个入口就永远截不到它。正常运行零影响。
    /// </summary>
    public void ApplyExitGuardDemo() => IsExitDownloadConfirmOpen = true;
}
