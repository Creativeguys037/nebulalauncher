using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NebulaLauncher.Converters;

/// <summary>
/// 通用展示转换器集合。全部实现 <see cref="IValueConverter"/>，
/// 在 XAML 里通过 <c>{x:Static conv:Converters.BytesToSize}</c> 或静态资源引用。
/// </summary>
public static class Converters
{
    /// <summary>字节数 → <c>1.4 GB</c> / <c>820 MB</c>。</summary>
    public static readonly IValueConverter BytesToSize = new FuncValueConverter<long, string>(
        bytes => FormatSize(bytes));

    /// <summary>0–1 的比例 → 百分比整数串。</summary>
    public static readonly IValueConverter RatioToPercent = new FuncValueConverter<double, string>(
        ratio => $"{(int)Math.Round(Math.Clamp(ratio, 0, 1) * 100)}%");

    /// <summary>字节/秒 → <c>12.4 MB/s</c>。</summary>
    public static readonly IValueConverter SpeedToText = new FuncValueConverter<double, string>(
        speed => speed <= 0 ? "—" : $"{FormatSize((long)speed)}/s");

    /// <summary>秒 → <c>1:23</c> 或 <c>2 小时 5 分</c>。</summary>
    public static readonly IValueConverter SecondsToDuration = new FuncValueConverter<long, string>(
        seconds => FormatDuration(TimeSpan.FromSeconds(Math.Max(0, seconds))));

    /// <summary>时间 → 相对描述（刚刚 / 3 分钟前 / 2 天前）。</summary>
    public static readonly IValueConverter TimeAgo = new FuncValueConverter<DateTimeOffset, string>(
        When => FormatAgo(When));

    /// <summary>布尔值取反，用于「未就绪时禁用」这类绑定。</summary>
    public static readonly IValueConverter InverseBool = new FuncValueConverter<bool, bool>(v => !v);

    /// <summary>null / 空字符串 / 空集合 → false，用于 IsVisible 绑定。</summary>
    public static readonly IValueConverter HasValue = new FuncValueConverter<object?, bool>(
        v => v is not null && (v is not string s || !string.IsNullOrWhiteSpace(s)));

    /// <summary>字节数 → 紧凑格式，用于空间有限的角落。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    /// <summary>时长 → 中文可读形式。</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        }

        return $"{span.Seconds} 秒";
    }

    private static string FormatAgo(DateTimeOffset when)
    {
        var delta = DateTimeOffset.Now - when;
        if (delta < TimeSpan.Zero)
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta < TimeSpan.FromDays(30))
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }
}
