using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();

    /// <summary>
    /// 目标下拉的选中变化 → 重扫列表。
    ///
    /// 绑的是 <c>Mode=OneWay</c>，因为「赋值 SelectedTarget」本身不触发扫描
    /// （见 ModsViewModel.OnSelectedTargetChanged 的注释），扫描由命令显式做。
    /// 早退判据用 <see cref="ReferenceEquals"/>：首次加载时 ComboBox 会从 null 补成
    /// 已选中的那一项，那不是用户操作，不该再扫一遍。
    /// </summary>
    private void OnTargetSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || DataContext is not ModsViewModel viewModel)
        {
            return;
        }

        if (combo.SelectedItem is not TargetItemViewModel item
            || ReferenceEquals(item, viewModel.SelectedTarget))
        {
            return;
        }

        viewModel.SelectTargetCommand.Execute(item);
    }

    /// <summary>
    /// 「从文件安装」：选文件 → 交给 ViewModel 复制并重扫。
    ///
    /// 选择器留在 View：ViewModel 只接路径，不碰 <c>IStorageFile</c>（CONVENTIONS §8.2
    /// 要求测试不触碰 Avalonia 类型）。异常在这里收住，因为 async void 抛出去会带走进程。
    /// </summary>
    private async void OnInstallFromFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModsViewModel viewModel)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);

            if (topLevel?.StorageProvider is null)
            {
                viewModel.ErrorText = "当前窗口不支持文件选择。";
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要安装的文件",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("内容文件")
                    {
                        Patterns = ["*.jar", "*.zip", "*.mrpack", "*.mcpack"],
                    },
                ],
            });

            if (files.Count == 0)
            {
                return;
            }

            await viewModel.InstallFromFilesAsync(
                files.Select(file => file.Path.LocalPath).ToList());
        }
        catch (Exception ex)
        {
            viewModel.ErrorText = $"打开文件选择器失败：{ex.Message}";
        }
    }

    /// <summary>关闭页面级错误条。ViewModel 的错误字段是单向展示，清空动作留在页面侧。</summary>
    private void OnDismissErrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ModsViewModel viewModel)
        {
            viewModel.ErrorText = null;
        }
    }
}

/// <summary>
/// 本地图片路径 → <see cref="Bitmap"/>。加载失败返回 null，让下层首字母占位透出来，
/// 这样即使模组图标损坏卡片也不会空一块。
///
/// 缓存的意义：每次筛选/刷新都会重建卡片，重复解码同一批 png 会明显卡顿；
/// 图标文件名由 Core 按内容 hash 生成，内容不会变，所以缓存不会读到过期图片。
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
            {
                bitmap = new Bitmap(path);
            }
        }
        catch (Exception)
        {
            // 图片损坏或格式不受支持时保留占位，不打断扫描流程。
            bitmap = null;
        }

        lock (Cache)
        {
            Cache[path] = bitmap;
        }

        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
