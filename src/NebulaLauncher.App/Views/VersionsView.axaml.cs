using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class VersionsView : UserControl
{
    /// <summary>
    /// 三个视图各自的滚动位置。
    ///
    /// 为什么放在这里而不是靠 ScrollViewer 自己记住：切走的那份列表虽然留在可视树里，
    /// 但它是 <c>IsVisible=false</c> 的——Avalonia 会让不可见元素退出布局，
    /// 偏移在布局往返里被归零。所以「切走时读出来、切回时写回去」必须显式做。
    ///
    /// 存在 code-behind 而不是 ViewModel：<c>ScrollViewer.Offset</c> 是视图层的东西，
    /// 把一个 <c>Vector</c> 塞进 ViewModel 会把布局细节漏进业务层（仓库约定：
    /// 测试里不得触碰 Avalonia 类型）。三份状态互不影响，用字典按视图键分开存。
    /// </summary>
    private readonly Dictionary<VersionsViewKind, Vector> _scrollOffsets = new();

    private VersionsViewModel? _viewModel;

    public VersionsView()
    {
        InitializeComponent();

        // 页签切换发生在 ViewModel 上，而滚动恢复只能在视图层做，所以在这里挂一次监听。
        DataContextChanged += OnDataContextChanged;

        // 三份列表各自上报偏移：用 AttachedToVisualTree 而不是构造函数，
        // 因为 ScrollViewer 的模板要到挂进可视树之后才存在。
        AttachedToVisualTree += (_, _) => AttachScrollTracking();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as VersionsViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            AttachScrollTracking();
        }
    }

    /// <summary>
    /// 视图切换时把上一个视图的偏移记下来、把新视图的偏移写回去。
    ///
    /// 顺序不能反：先存旧的再恢复新的。反过来的话，切回「已安装」时会被
    /// 「刚离开的可下载」那一份偏移覆盖掉。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VersionsViewModel.CurrentView) || _viewModel is null)
        {
            return;
        }

        CaptureScrollOffsets();
        RestoreScrollOffset(_viewModel.CurrentView);
    }

    /// <summary>把三份列表当前的偏移抄进字典。切走时调一次。</summary>
    private void CaptureScrollOffsets()
    {
        SaveListOffset(VersionsViewKind.Installed, InstalledList);
        SaveListOffset(VersionsViewKind.Available, AvailableList);
        SaveListOffset(VersionsViewKind.Modpacks, ModpacksList);
    }

    private void SaveListOffset(VersionsViewKind kind, ListBox? list)
    {
        var scroller = FindScrollViewer(list);

        if (scroller is not null)
        {
            _scrollOffsets[kind] = scroller.Offset;
        }
    }

    /// <summary>把某个视图记下来的偏移写回去。视图刚变成可见时要等一轮布局才生效。</summary>
    private void RestoreScrollOffset(VersionsViewKind kind)
    {
        if (!_scrollOffsets.TryGetValue(kind, out var offset))
        {
            return;
        }

        var list = kind switch
        {
            VersionsViewKind.Available => AvailableList,
            VersionsViewKind.Modpacks => ModpacksList,
            _ => InstalledList,
        };

        // 刚切过来时列表还没重新参与布局，Offset 写下去会被这一轮的测量覆盖掉，
        // 所以排到布局完成之后（Loaded 优先级）再写。
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                var scroller = FindScrollViewer(list);

                if (scroller is not null)
                {
                    scroller.Offset = offset;
                }
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 找到 ListBox 模板里的那个 ScrollViewer，并在滚动时把偏移持续写进字典。
    ///
    /// 每次滚动都写、而不是只在切走时读：ListBox 的 ScrollViewer 在虚拟化下会随容器回收
    /// 重新定位，只在切换瞬间读可能读到一个刚刚被修正过的值。
    /// </summary>
    private void AttachScrollTracking()
    {
        TrackScroll(VersionsViewKind.Installed, InstalledList);
        TrackScroll(VersionsViewKind.Available, AvailableList);
        TrackScroll(VersionsViewKind.Modpacks, ModpacksList);
    }

    private void TrackScroll(VersionsViewKind kind, ListBox? list)
    {
        var scroller = FindScrollViewer(list);

        if (scroller is null || scroller.Tag as string == "scroll-tracked")
        {
            return;
        }

        scroller.Tag = "scroll-tracked";
        scroller.ScrollChanged += (_, _) =>
        {
            // 只有当前视图的偏移值得记：另两份不可见，它们的 Offset 是 0 或历史值，
            // 反复写会把「切走前的真实位置」冲掉。
            if (_viewModel is not null && _viewModel.CurrentView == kind)
            {
                _scrollOffsets[kind] = scroller.Offset;
            }
        };
    }

    private static ScrollViewer? FindScrollViewer(ListBox? list) =>
        list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

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