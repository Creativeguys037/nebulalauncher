using System.Runtime.InteropServices;
using System.Text.Json;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Core.Java;

/// <summary>清单里的一个条目（文件 / 目录 / 符号链接）。</summary>
/// <param name="RelativePath">相对运行时根目录的路径，清单里用 <c>/</c> 分隔。</param>
/// <param name="Url">下载地址；目录与链接为 null。</param>
/// <param name="Sha1">期望 SHA1；清单缺这个字段时为 null（跳过校验而不是失败）。</param>
/// <param name="Size">字节数；未知为 0。</param>
/// <param name="IsDirectory">目录条目。</param>
/// <param name="IsExecutable">可执行文件，Unix 上要补可执行位。</param>
/// <param name="LinkTarget">符号链接目标。</param>
public sealed record MojangRuntimeFile(
    string RelativePath,
    string? Url,
    string? Sha1,
    long Size,
    bool IsDirectory,
    bool IsExecutable,
    string? LinkTarget);

/// <summary>
/// Mojang 官方 Java 运行时清单。
///
/// 为什么不用 Adoptium 一条路：<c>api.adoptium.net</c> 不在镜像表里，国内经常直接超时；
/// 而 <c>piston-meta.mojang.com</c> 已经被 <c>DownloadMirror</c> 映射到 BMCLAPI，
/// 下载 Mojang runtime 天然吃镜像。Adoptium 保留为兜底（Java 11、22 这类中间版本
/// Mojang 根本不发 runtime）。
///
/// 两个解析函数都是纯函数，不碰网络，便于单测。
/// </summary>
public static class MojangRuntimeCatalog
{
    /// <summary>官方 products 清单（含全部组件与平台）。</summary>
    public const string AllProductsUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>Java 主版本 → Mojang 组件名。Mojang 只发布这五个组件。</summary>
    public static string ComponentFor(int majorVersion) => majorVersion switch
    {
        8 => "jre-legacy",
        16 => "java-runtime-alpha",
        17 => "java-runtime-beta",
        21 => "java-runtime-delta",
        25 => "java-runtime-epsilon",
        _ => string.Empty,
    };

    /// <summary>当前机器的清单平台键（与 Mojang / HMCL 的键名一致）。</summary>
    public static string CurrentPlatformKey() => PlatformKeyFor(
        PlatformService.IsWindows,
        PlatformService.IsMacOs,
        Environment.Is64BitProcess,
        RuntimeInformation.OSArchitecture == Architecture.Arm64);

    /// <summary>平台键。单独暴露纯函数，便于在任意主机上测三种平台组合。</summary>
    public static string PlatformKeyFor(bool isWindows, bool isMacOs, bool is64Bit, bool isArm64)
    {
        if (isWindows)
        {
            return isArm64 ? "windows-arm64" : is64Bit ? "windows-x64" : "windows-x86";
        }

        if (isMacOs)
        {
            return isArm64 ? "mac-os-arm64" : "mac-os";
        }

        return is64Bit ? "linux" : "linux-i386";
    }

    /// <summary>
    /// 从 <c>all.json</c> 里取某个组件的 manifest 地址。
    /// 结构：<c>{ "&lt;platform&gt;": { "&lt;component&gt;": [ { "manifest": { "url": ... } } ] } }</c>。
    /// </summary>
    /// <returns>找不到组件 / 平台 / 数组为空时返回 null。</returns>
    public static string? ParseManifestUrl(string? allProductsJson, string platformKey, string component)
    {
        if (string.IsNullOrWhiteSpace(allProductsJson) ||
            string.IsNullOrWhiteSpace(platformKey) ||
            string.IsNullOrWhiteSpace(component))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(allProductsJson);

            if (!document.RootElement.TryGetProperty(platformKey, out var platform) ||
                !platform.TryGetProperty(component, out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("manifest", out var manifest) &&
                    manifest.TryGetProperty("url", out var url) &&
                    url.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析单个组件的 manifest：<c>{ "files": { "bin/java": { "type": "file", "downloads": { "raw": {...} }, "executable": true } } }</c>。
    /// 未知 <c>type</c> 一律忽略（宁可少装一个文件，也不要把清单里的新类型当文件下载）。
    /// </summary>
    public static IReadOnlyList<MojangRuntimeFile> ParseManifest(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return Array.Empty<MojangRuntimeFile>();
        }

        var result = new List<MojangRuntimeFile>(256);

        try
        {
            using var document = JsonDocument.Parse(manifestJson);

            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<MojangRuntimeFile>();
            }

            foreach (var property in files.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = property.Value.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;

                var isDirectory = string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase);
                var isLink = string.Equals(type, "link", StringComparison.OrdinalIgnoreCase);

                if (!isDirectory && !isLink && !string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? url = null;
                string? sha1 = null;
                long size = 0;

                if (!isDirectory && !isLink &&
                    property.Value.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("raw", out var raw))
                {
                    if (raw.TryGetProperty("url", out var urlElement))
                    {
                        url = urlElement.GetString();
                    }

                    if (raw.TryGetProperty("sha1", out var sha1Element))
                    {
                        sha1 = sha1Element.GetString();
                    }

                    if (raw.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
                    {
                        size = parsedSize;
                    }
                }

                var executable = property.Value.TryGetProperty("executable", out var exeElement) &&
                                 exeElement.ValueKind == JsonValueKind.True;

                var linkTarget = property.Value.TryGetProperty("target", out var targetElement)
                    ? targetElement.GetString()
                    : null;

                result.Add(new MojangRuntimeFile(
                    property.Name.Replace('\\', '/'),
                    url,
                    sha1,
                    size,
                    isDirectory,
                    executable,
                    isLink ? linkTarget : null));
            }
        }
        catch (JsonException)
        {
            return Array.Empty<MojangRuntimeFile>();
        }

        // 先目录后文件：下载队列不负责建目录，排序让调用方能顺序处理。
        return result
            .OrderBy(file => file.IsDirectory ? 0 : 1)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
    }
}
