using System;
using System.IO;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 单个文件下载失败的包装异常。
///
/// 底层是 <see cref="System.Net.Http.HttpRequestException"/> 之类的异常时，消息里没有 URL，
/// 用户只看到「下载失败：404」无法判断是哪个文件、该换源还是该换版本。
/// 这里带上文件名与地址，UI 的失败提示与日志都能直接定位。
/// </summary>
public sealed class InstallerFileException : IOException
{
    public InstallerFileException(string fileName, string? url, Exception inner)
        : base($"下载 {fileName} 失败（{DescribeUrl(url)}）：{inner.Message}", inner)
    {
        FileName = fileName;
        Url = url;
    }

    /// <summary>失败的文件显示名。</summary>
    public string FileName { get; }

    /// <summary>该文件的下载地址；可能为 null。</summary>
    public string? Url { get; }

    private static string DescribeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "无地址";
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Host}{uri.AbsolutePath}"
            : url;
    }
}
