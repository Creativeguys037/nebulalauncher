using NebulaLauncher.Core.Downloads;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 退出守卫：还有下载任务时关闭窗口必须先问一次（PCL2 <c>FormMain.EndProgram</c> 的等价行为）。
/// 托盘退出不经过这里——它走 ApplicationShutdown，与 PCL2 的 <c>EndProgram(False)</c> 一致。
/// </summary>
public class AppShell_ExitGuardTests
{
    [Fact]
    public void 没有下载队列时直接放行()
    {
        var vm = new MainWindowViewModel(new FixedServices(typeof(DownloadQueue), null));

        Assert.True(vm.ShouldConfirmExit());
        Assert.False(vm.IsExitDownloadConfirmOpen);
    }

    [Fact]
    public void 有活动批次时拦下并展示覆盖层()
    {
        var queue = new DownloadQueue(new HttpClient());
        using var scope = queue.BeginExternalBatch("测试安装");
        var vm = new MainWindowViewModel(new FixedServices(typeof(DownloadQueue), queue));

        Assert.False(vm.ShouldConfirmExit());
        Assert.True(vm.IsExitDownloadConfirmOpen);
    }

    [Fact]
    public void 取消后保持窗口且下次关闭仍会询问()
    {
        var queue = new DownloadQueue(new HttpClient());
        using var scope = queue.BeginExternalBatch("测试安装");
        var vm = new MainWindowViewModel(new FixedServices(typeof(DownloadQueue), queue));

        vm.ShouldConfirmExit();
        vm.CancelExitWithDownloadsCommand.Execute(null);

        Assert.False(vm.IsExitDownloadConfirmOpen);
        Assert.False(vm.ShouldConfirmExit());
        Assert.True(vm.IsExitDownloadConfirmOpen);
    }

    [Fact]
    public void 确认后放行并且事件只触发一次()
    {
        var queue = new DownloadQueue(new HttpClient());
        using var scope = queue.BeginExternalBatch("测试安装");
        var vm = new MainWindowViewModel(new FixedServices(typeof(DownloadQueue), queue));

        var confirmed = 0;
        vm.ExitConfirmed += (_, _) => confirmed++;

        vm.ShouldConfirmExit();
        vm.ConfirmExitWithDownloadsCommand.Execute(null);

        Assert.Equal(1, confirmed);
        Assert.False(vm.IsExitDownloadConfirmOpen);

        // 之后的关闭（保存完成后窗口会再走一次 Closing）不再拦截。
        Assert.True(vm.ShouldConfirmExit());
        Assert.False(vm.IsExitDownloadConfirmOpen);
        Assert.Equal(1, confirmed);
    }

    private sealed class FixedServices(Type serviceType, object? service) : IServiceProvider
    {
        public object? GetService(Type requested) => requested == serviceType ? service : null;
    }
}
