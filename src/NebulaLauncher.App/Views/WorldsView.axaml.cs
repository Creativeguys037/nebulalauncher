using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class WorldsView : UserControl
{
    public WorldsView()
    {
        InitializeComponent();
    }

    /// <summary>整合包导入：文件选择属于窗口能力，路径拿到后交给 ViewModel 解析。</summary>
    private async void OnImportModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorldsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ReportError("当前窗口无法打开文件选择器，请稍后重试。");
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择整合包",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Minecraft 整合包")
                    {
                        Patterns = ["*.zip", "*.mrpack"],
                    },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                await viewModel.BeginImportAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportError($"选择整合包失败：{ex.Message}");
        }
    }

    /// <summary>导出整合包：保存位置属于窗口能力，路径拿到后交给 ViewModel。</summary>
    private async void OnChooseExportArchiveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorldsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ReportError("当前窗口无法打开文件选择器，请稍后重试。");
                return;
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出整合包",
                SuggestedFileName = SuggestFileName(viewModel.ExportName),
                DefaultExtension = "mrpack",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("Modrinth 整合包")
                    {
                        Patterns = ["*.mrpack"],
                    },
                ],
            });

            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                viewModel.SetExportArchivePath(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportError($"选择保存位置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 整合包名来自用户输入，可能含 Windows 非法字符；建议名必须先清掉，否则选择器会直接失败。
    /// 清洗走 <see cref="NebulaLauncher.Core.Paths.PathSafety"/>，保留名（<c>CON</c>）与结尾点号也一并处理。
    /// </summary>
    private static string SuggestFileName(string? exportName)
    {
        var safe = NebulaLauncher.Core.Paths.PathSafety.SanitizeFileName(exportName, string.Empty);

        return safe.Length == 0 ? "modpack.mrpack" : $"{safe}.mrpack";
    }

    /// <summary>还原备份需要目标游戏目录：用文件夹选择器拿到后交给 ViewModel。</summary>
    private async void OnRestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorldsViewModel viewModel)
        {
            return;
        }

        if ((sender as Control)?.DataContext is not BackupCardViewModel backup)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ReportError("当前窗口无法打开文件夹选择器，请稍后重试。");
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择还原到的游戏目录（备份会解压到该目录的 saves 下）",
                AllowMultiple = false,
            });

            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                await viewModel.RestoreAsync(backup, path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportError($"选择还原目录失败：{ex.Message}");
        }
    }
}
