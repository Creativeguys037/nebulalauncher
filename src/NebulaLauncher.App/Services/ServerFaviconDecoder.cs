using System.Diagnostics.CodeAnalysis;
using Avalonia.Media.Imaging;

namespace NebulaLauncher.Services;

/// <summary>
/// 服务器图标的 data URI 预处理：字符串与字节层面的校验，不引用任何 Avalonia 类型。
///
/// 与 <see cref="ServerFaviconDecoder"/> 分成两个类型是为了可测：测试进程没有 Avalonia
/// 渲染后端（CONVENTIONS §7.1），能验证的边界必须留在这层。图标来自远程服务器，
/// 缺字段、坏 base64、非 PNG 都是常态，这里只返回 false，不抛异常。
/// </summary>
internal static class ServerFaviconDataUri
{
    /// <summary>Minecraft 客户端写进 SLP 响应（<c>favicon</c>）与 <c>servers.dat</c> 的固定前缀。</summary>
    internal const string PngDataUriPrefix = "data:image/png;base64,";

    /// <summary>
    /// 图标体积上限（base64 解码后的字节数）。
    /// 正常图标固定 64×64，压缩后普遍 1–8 KB；512 KB 之外只可能是损坏数据或被塞了大图，
    /// 直接拒绝，避免把任意大的载荷交给解码器。
    /// </summary>
    internal const int MaxPngBytes = 512 * 1024;

    /// <summary>PNG 文件头。<see cref="Bitmap"/> 只认真正的 PNG，提前拦掉可以省一次异常。</summary>
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 图标宽高上限。Minecraft 的服务器图标固定 64×64；放宽到 256 兼容个别放大过的图标，
    /// 同时挡住「体积合规但 IHDR 声明 10000×10000」的包——Skia 会按声明尺寸分配内存。
    /// </summary>
    internal const int MaxIconDimension = 256;

    /// <summary>
    /// 把 <c>data:image/png;base64,...</c> 解成 PNG 字节。
    /// null / 空白、前缀不匹配、base64 非法、解码结果不是 PNG 或超过上限，一律返回 false。
    /// </summary>
    /// <param name="dataUri">服务器返回的 favicon 原文。</param>
    /// <param name="pngBytes">成功时是 PNG 字节，失败时为 null。</param>
    internal static bool TryGetPngBytes(string? dataUri, [NotNullWhen(true)] out byte[]? pngBytes)
    {
        pngBytes = null;

        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return false;
        }

        var text = dataUri.Trim();
        if (!text.StartsWith(PngDataUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = text[PngDataUriPrefix.Length..];
        if (payload.Length == 0)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length < PngMagic.Length
            || decoded.Length > MaxPngBytes
            || !decoded.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
        {
            return false;
        }

        // 尺寸校验在字节层完成：解码器只拿到「结构合规且尺寸有限」的 PNG。
        if (!TryReadPngSize(decoded, out var width, out var height)
            || width <= 0
            || height <= 0
            || width > MaxIconDimension
            || height > MaxIconDimension)
        {
            return false;
        }

        pngBytes = decoded;
        return true;
    }

    /// <summary>
    /// 从 PNG 的 IHDR 读宽高（大端 4 字节，偏移 16 / 20）。结构不对返回 false。
    /// </summary>
    private static bool TryReadPngSize(byte[] pngBytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 8 字节签名 + 4 字节长度 + 4 字节 "IHDR" + 4 宽 + 4 高 = 至少 24 字节。
        if (pngBytes.Length < 24
            || pngBytes[12] != (byte)'I'
            || pngBytes[13] != (byte)'H'
            || pngBytes[14] != (byte)'D'
            || pngBytes[15] != (byte)'R')
        {
            return false;
        }

        // 用 long 组装，避免 0xFFFFFFFF 这种恶意尺寸被读成负数后绕过上限判断。
        long parsedWidth = ((long)pngBytes[16] << 24) | ((long)pngBytes[17] << 16)
                           | ((long)pngBytes[18] << 8) | pngBytes[19];
        long parsedHeight = ((long)pngBytes[20] << 24) | ((long)pngBytes[21] << 16)
                            | ((long)pngBytes[22] << 8) | pngBytes[23];

        if (parsedWidth is < 1 or > int.MaxValue || parsedHeight is < 1 or > int.MaxValue)
        {
            return false;
        }

        width = (int)parsedWidth;
        height = (int)parsedHeight;
        return true;
    }
}

/// <summary>
/// 服务器图标解码：<see cref="ServerFaviconDataUri"/> 校验通过后交给 Avalonia 的 Skia 后端。
///
/// 失败返回 null 而不是抛：一张坏图标不该让整行服务器卡片炸掉，调用方退回首字母占位即可。
/// </summary>
internal static class ServerFaviconDecoder
{
    /// <summary>
    /// 解码成可直接绑定到 <c>Image.Source</c> 的位图；任何失败都返回 null。
    /// PNG 结构、体积与宽高已在 <see cref="ServerFaviconDataUri.TryGetPngBytes"/> 里校验。
    /// </summary>
    /// <param name="dataUri">服务器返回的 favicon 原文。</param>
    internal static Bitmap? TryDecode(string? dataUri)
    {
        if (!ServerFaviconDataUri.TryGetPngBytes(dataUri, out var pngBytes))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // Skia 对 PNG 结构、位深、尺寸的校验比魔数严得多，这里只兜「解不出来」。
            return null;
        }
    }
}
