using Avalonia.Controls;
using Avalonia.Input;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class SkinsView : UserControl
{
    public SkinsView() => InitializeComponent();

    /// <summary>
    /// 搜索框回车即搜索。命令本身是异步的（<see cref="SkinsViewModel.SearchCommand"/>），
    /// 所以这里保持同步处理器，不要把整段逻辑写成 async void。
    /// </summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is SkinsViewModel viewModel && viewModel.SearchCommand.CanExecute(null))
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
