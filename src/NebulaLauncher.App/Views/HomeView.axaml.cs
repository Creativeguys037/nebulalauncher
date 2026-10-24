using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>最近版本列表双击即启动。命令内部已处理账户缺失、版本缺失与异常。</summary>
    private void OnVersionListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is HomeViewModel viewModel && viewModel.LaunchCommand.CanExecute(null))
        {
            viewModel.LaunchCommand.Execute(null);
        }
    }

    /// <summary>
    /// 选择外部崩溃报告。文件选择需要 TopLevel，只能留在视图层；
    /// 选完把路径交回 ViewModel，由 Core 的 CrashAnalyzer 读取与归因。
    /// </summary>
    private async void OnImportCrashReportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.Notice = "当前窗口无法打开文件选择器，请稍后重试。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择崩溃报告",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("崩溃报告")
                    {
                        Patterns = ["*.txt", "*.log", "*.zip"],
                    },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await viewModel.ImportExternalReportAsync(path);
        }
        catch (Exception ex)
        {
            viewModel.Notice = $"选择崩溃报告失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 选择崩溃报告的导出目录。目录选择需要 TopLevel，只能留在视图层；
    /// 选完把路径交回 ViewModel，导出与脱敏都在 Core 完成。
    /// </summary>
    private async void OnExportCrashClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.Notice = "当前窗口无法打开文件夹选择器，请稍后重试。";
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择崩溃报告的导出目录",
                AllowMultiple = false,
            });

            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await viewModel.ExportCrashReportAsync(path);
        }
        catch (Exception ex)
        {
            viewModel.Notice = $"选择导出目录失败：{ex.Message}";
        }
    }
}
