using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class VersionsView : UserControl
{
    public VersionsView()
    {
        InitializeComponent();
    }

    /// <summary>双击版本行启动。命令内部处理账户缺失、重复启动与异常。</summary>
    private void OnVersionsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        var row = viewModel.SelectedRow;
        if (row is null || !viewModel.LaunchVersionCommand.CanExecute(row))
        {
            return;
        }

        viewModel.LaunchVersionCommand.Execute(row);
    }

    // 修复档位下拉的两个菜单项。
    //
    // 为什么走 code-behind 而不是命令绑定：MenuFlyout 的内容挂在独立的弹出层里，
    // 不在页面的可视树中，XAML 里那条 `$parent[ItemsControl].DataContext` 的绑定路径到不了
    // VersionsViewModel。菜单项的 DataContext 仍是它挂着的那个 VersionRow，所以从这里
    // 取出行、再用页面自己的 DataContext 调命令，是唯一在编译期就能验证的路子。
    private void OnRepairQuickClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RepairFromFlyout(sender, RepairDepth.Quick);

    private void OnRepairDeepClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RepairFromFlyout(sender, RepairDepth.Deep);

    /// <summary>
    /// 实例卡片溢出菜单里的「换加载器…」。
    ///
    /// 同样走 code-behind：菜单挂在 <c>MenuFlyout</c> 的独立弹出层里，页面那条
    /// <c>$parent[ItemsControl].DataContext</c> 绑定路径到不了 ViewModel。菜单项的 DataContext
    /// 是它挂着的那个 <see cref="InstanceRow"/>，从中取行再调页面自己的命令。
    /// </summary>
    private void OnSwitchLoaderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        var row = (sender as MenuItem)?.DataContext as InstanceRow ?? viewModel.SelectedInstance;

        _ = viewModel.OpenLoaderSwitchCommand.ExecuteAsync(row);
    }

    private void RepairFromFlyout(object? sender, RepairDepth depth)
    {
        if (DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        var row = (sender as MenuItem)?.DataContext as VersionRow ?? viewModel.SelectedRow;

        // 深度档在 ViewModel 里先弹确认；这里只负责把档位递过去。
        _ = viewModel.RepairWithDepthAsync(row, depth);
    }

    /// <summary>
    /// 把某个版本的客户端 jar 另存到用户选的目录。
    ///
    /// 为什么也在 code-behind：目录选择需要 <see cref="TopLevel"/>，那是视图层的东西；
    /// 下载本身在 <c>IVersionService.SaveClientJarAsync</c>，这里只把两条接起来。
    /// </summary>
    private async void OnSaveClientJarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        var row = (sender as MenuItem)?.DataContext as VersionRow ?? viewModel.SelectedRow;

        if (row is null)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);

            if (topLevel?.StorageProvider is null)
            {
                viewModel.StatusText = "当前窗口无法打开文件夹选择器，请稍后重试。";
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"选择 {row.Name} 客户端 jar 的保存目录",
                AllowMultiple = false,
            });

            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await viewModel.SaveClientJarAsync(row, path);
        }
        catch (Exception ex)
        {
            // 选择器本身在个别平台上可能抛（没有 portal、权限不足）。
            viewModel.StatusText = $"选择保存目录失败：{ex.Message}";
        }
    }
}