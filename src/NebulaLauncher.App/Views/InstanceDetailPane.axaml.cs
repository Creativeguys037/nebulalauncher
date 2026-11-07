using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

/// <summary>
/// 实例详情面板的 code-behind。
///
/// 留在这一层的只有两类事：
/// 1. 溢出菜单里的「换加载器…」——菜单挂在 <see cref="MenuFlyout"/> 的独立弹出层里，
///    页面那条 <c>$parent[ItemsControl].DataContext</c> 的绑定路径到不了 ViewModel；
/// 2. 文件与文件夹选择器——那是窗口能力，ViewModel 拿不到 TopLevel。
///
/// 面板本身除这些入口外是只读的（身份 / 信息 / 观察），内存与 Java 的编辑在实例设置浮层里。
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

    /// <summary>关掉分区里的结果条。两个分区共用同一个处理：点哪条的关闭按钮都清掉自己那条。</summary>
    private void OnDismissResultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel)
        {
            return;
        }

        viewModel.WorldsSection?.ClearResult();
        viewModel.ModpacksSection?.ClearResult();
    }

    /// <summary>整合包导入：文件选择属于窗口能力，路径拿到后交给 ViewModel 解析（不落盘）。</summary>
    private async void OnPickModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel || viewModel.ModpacksSection is not { } section)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                section.ReportError("当前窗口无法打开文件选择器，请稍后重试。");
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
                await section.InspectAsync(path);
            }
        }
        catch (Exception ex)
        {
            section.ReportError($"选择整合包失败：{ex.Message}");
        }
    }

    /// <summary>整合包导出：保存位置属于窗口能力，路径拿到后交给 ViewModel 打包。</summary>
    private async void OnExportModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VersionsViewModel viewModel || viewModel.ModpacksSection is not { } section)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null)
            {
                section.ReportError("当前窗口无法打开文件选择器，请稍后重试。");
                return;
            }

            var suggested = SuggestFileName(section.ExportPreview?.Name ?? section.TargetText);

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出整合包",
                SuggestedFileName = suggested,
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
                await section.ExportAsync(path);
            }
        }
        catch (Exception ex)
        {
            section.ReportError($"选择保存位置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 整合包名来自用户输入或实例名，可能含 Windows 非法字符；建议名必须先清掉，
    /// 否则选择器会直接失败。清洗走 <see cref="NebulaLauncher.Core.Paths.PathSafety"/>。
    /// </summary>
    private static string SuggestFileName(string? exportName)
    {
        var safe = NebulaLauncher.Core.Paths.PathSafety.SanitizeFileName(exportName, string.Empty);

        return safe.Length == 0 ? "modpack.mrpack" : $"{safe}.mrpack";
    }
}