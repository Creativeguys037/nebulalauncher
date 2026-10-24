using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace NebulaLauncher.Views;

public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();
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
