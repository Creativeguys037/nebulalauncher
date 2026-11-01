using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

/// <summary>云端衣柜的视图层：只做文件选择与键盘事件，业务都在 <see cref="CloudClosetViewModel"/>。</summary>
public partial class CloudClosetView : UserControl
{
    private static readonly string[] PngPatterns = ["*.png"];

    public CloudClosetView()
    {
        InitializeComponent();
    }

    private async void OnUploadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not CloudClosetViewModel viewModel)
        {
            return;
        }

        var title = viewModel.IsCapeCategory ? "选择披风 PNG" : "选择皮肤 PNG";
        var path = await PickPngAsync(title);
        if (path is null)
        {
            return;
        }

        await viewModel.UploadAsync(path);
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not CloudClosetViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        if (viewModel.SearchCommand.CanExecute(null))
        {
            await viewModel.SearchCommand.ExecuteAsync(null);
        }
    }

    private async Task<string?> PickPngAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("PNG 图片") { Patterns = PngPatterns },
            },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
