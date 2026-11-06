using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

/// <summary>
/// 实例详情面板的 code-behind。
///
/// 只剩一件必须留在这一层的事：溢出菜单里的「换加载器…」。
/// 那个菜单挂在 <see cref="MenuFlyout"/> 的独立弹出层里，页面那条
/// <c>$parent[ItemsControl].DataContext</c> 的绑定路径到不了 ViewModel，
/// 只能在这里从菜单项的 DataContext 取行再调命令。
///
/// 面板本身是只读的（身份 / 信息 / 入口），内存与 Java 的编辑在实例设置浮层里，
/// 所以这里没有任何输入控件需要代码介入。
/// </summary>
public partial class InstanceDetailPane : UserControl
{
    public InstanceDetailPane()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 溢出菜单里的「换加载器…」。
    ///
    /// 走 code-behind 是因为菜单挂在 <see cref="MenuFlyout"/> 的独立弹出层里，
    /// 页面那条 <c>$parent[ItemsControl].DataContext</c> 的绑定路径到不了 ViewModel。
    /// 菜单项的 DataContext 仍是它挂着的那个 <see cref="InstanceRow"/>，从中取行再调命令。
    /// </summary>
    private void OnSwitchLoaderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        var row = (sender as MenuItem)?.DataContext as InstanceRow ?? viewModel.Detail.Row;

        _ = viewModel.OpenLoaderSwitchCommand.ExecuteAsync(row);
    }

}
