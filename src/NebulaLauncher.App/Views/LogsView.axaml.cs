using System;
using Avalonia.Controls;
using Avalonia.Threading;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// 「自动跟随」要滚到底部，而滚动位置不是可绑定的单向状态
    /// （双向绑定 Offset 会在用户手动滚动后立刻被 ViewModel 的值拉回去），
    /// 所以由 ViewModel 发请求、视图在这里执行。文件选择一类的交互同样留在 code-behind。
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _viewModel = DataContext as LogsViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        // 正文刚刚换过，等本次布局完成再滚，否则滚到的是旧内容的高度。
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
