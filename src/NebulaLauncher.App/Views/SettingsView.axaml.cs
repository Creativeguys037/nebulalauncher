using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 选择 Java 可执行文件。页面 ViewModel 拿不到 TopLevel，
    /// 所以文件选择这类窗口相关操作留在视图层，选完只把路径写回 ViewModel。
    /// </summary>
    /// <summary>
    /// 选择启动器背景图。只把路径交给 ViewModel，解码与应用在 MainWindowViewModel.ApplyBackgroundImage 里做
    /// （那里能同时处理「文件失效」与「换图释放旧位图」）。
    /// </summary>
    private async void OnPickBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ErrorMessage = "当前窗口无法打开文件选择器。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择启动器背景图片",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"],
                    },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                viewModel.BackgroundImagePath = path;
            }
        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = $"选择背景图片失败：{ex.Message}";
        }
    }

    private void OnClearBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.ClearBackgroundImageCommand.Execute(null);
        }
    }

    private async void OnBrowseJavaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ErrorMessage = "当前窗口无法打开文件选择器，请直接输入 Java 路径。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Java 可执行文件",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Java 可执行文件")
                    {
                        Patterns = ["java.exe", "javaw.exe", "java", "javaw"],
                    },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                viewModel.JavaPath = path;
            }
        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = $"选择 Java 失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 添加一个不在 PATH / 官方 runtime 里的 Java（PCL2 <c>ModJava.ManuallyImportJava</c>）。
    /// 选完只把路径交给 ViewModel：探测、置顶、写 <c>java-manual.json</c> 都在 Core。
    /// </summary>
    private async void OnAddJavaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.JavaError = "当前窗口无法打开文件选择器，请直接输入 Java 路径。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 java.exe（通常在 JDK 的 bin 目录里）",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Java 可执行文件")
                    {
                        Patterns = ["java.exe", "javaw.exe", "java", "javaw"],
                    },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                await viewModel.AddJavaAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.JavaError = $"添加 Java 失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 选择游戏目录（.minecraft）。与 Java 选择同理：TopLevel 只在视图层拿得到，
    /// 选完把路径写回 ViewModel，由 ViewModel 做可写性探测。
    /// </summary>
    private async void OnBrowseGameDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ErrorMessage = "当前窗口无法打开目录选择器，请直接输入游戏目录。";
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 Minecraft 游戏目录（.minecraft）",
                AllowMultiple = false,
            });

            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                viewModel.SetGameDirectory(path);
            }        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = $"选择游戏目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出配置包：先选目录，再把目录交给 ViewModel。
    /// 导出本身（含脱敏与写 zip）在 Core，视图层只负责拿到用户选的路径。
    /// </summary>
    private async void OnExportBundleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ActionMessage = "当前窗口无法打开目录选择器，无法导出配置。";
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择配置包导出目录",
                AllowMultiple = false,
                SuggestedStartLocation = await TryGetStartFolderAsync(topLevel, viewModel.DefaultBundleDirectory),
            });

            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                await viewModel.ExportProfileBundleAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ActionMessage = $"导出配置失败：{ex.Message}";
        }
    }

    /// <summary>导入配置包：选 zip，摘要与结果都写进关于分区的状态行，不弹窗。</summary>
    private async void OnImportBundleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                viewModel.ActionMessage = "当前窗口无法打开文件选择器，无法导入配置。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择配置包（nebula-profile-*.zip）",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Nebula 配置包")
                    {
                        Patterns = ["nebula-profile-*.zip", "*.zip"],
                    },
                ],
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                await viewModel.ImportProfileBundleAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ActionMessage = $"导入配置失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 选择器的默认落点：导出目录（数据根下的 exports/）。
    /// 目录还没建出来时返回 null，让选择器用系统默认位置——比抛异常或弹错误更合适。
    /// </summary>
    private static async Task<IStorageFolder?> TryGetStartFolderAsync(TopLevel topLevel, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
