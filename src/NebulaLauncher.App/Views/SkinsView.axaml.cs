using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    /// <summary>
    /// 从文件导入本地皮肤（票 42）。文件对话框必须留在视图层：视图模型不碰 Avalonia 的
    /// <c>StorageProvider</c>，这样它在无头测试里也能构造。选完只把本地路径交给视图模型。
    /// </summary>
    private async void OnImportLocalSkinClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SkinsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.LocalErrorText = "当前窗口无法打开文件选择器，请稍后重试。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择皮肤 PNG",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("皮肤图片") { Patterns = ["*.png"] },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await viewModel.ImportLocalSkinAsync(path);
        }
        catch (Exception ex)
        {
            // 事件处理器里的异常没人接，就地兜住并说清失败在哪一步。
            viewModel.LocalErrorText = $"导入本地皮肤失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 上传本地 PNG 作为皮肤站的皮肤（票 43）。与「导入本地皮肤」用的是同一个对话框，
    /// 但去向不同：这个把文件传给皮肤站，那个只拷进本地收藏目录。
    ///
    /// 对话框留在视图层、只把路径交给视图模型，理由同 <see cref="OnImportLocalSkinClick"/>。
    /// </summary>
    private async void OnUploadLocalSkinClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SkinsViewModel viewModel)
            {
                return;
            }

            if (await PickPngAsync("选择要上传的皮肤 PNG") is not { } path)
            {
                return;
            }

            viewModel.PendingUploadPath = path;
            await viewModel.UploadLocalFileToAuthlibCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ReportUploadFailure("上传皮肤", ex);
        }
    }

    /// <summary>上传本地 PNG 作为披风（票 43）。</summary>
    private async void OnUploadCapeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SkinsViewModel viewModel)
            {
                return;
            }

            if (await PickPngAsync("选择要上传的披风 PNG") is not { } path)
            {
                return;
            }

            viewModel.PendingUploadPath = path;
            await viewModel.UploadCapeCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ReportUploadFailure("上传披风", ex);
        }
    }

    /// <summary>弹一次 PNG 选择框，返回本地路径；用户取消或没有桌面集成时返回 null。</summary>
    private async Task<string?> PickPngAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            ReportUploadFailure("打开文件选择器", new InvalidOperationException("当前窗口没有可用的文件选择器"));
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("皮肤图片") { Patterns = ["*.png"] },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>把失败写进皮肤站那条结果区，而不是让它变成一条没人看见的未处理异常。</summary>
    private void ReportUploadFailure(string action, Exception ex)
    {
        if (DataContext is SkinsViewModel viewModel)
        {
            viewModel.AuthlibFeedback = Skinning.SkinUploadFeedback.LocalError($"{action}失败：{ex.Message}");
        }
    }
}
