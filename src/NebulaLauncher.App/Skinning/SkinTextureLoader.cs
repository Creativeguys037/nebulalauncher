using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 把账户变成一份可渲染的皮肤：查会话服务器的 profile → 解出皮肤与披风地址 → 下载 PNG → 解成像素。
///
/// 任何一步失败都退回默认皮肤，绝不让侧边栏的卡片空着 —— 这张卡是常驻的，
/// 网络抖动不该在界面上表现出来。
/// </summary>
public static class SkinTextureLoader
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>官方会话服务器上的披风图固定是 64×32 布局，披风内容在左上 22×17。</summary>
    private const int CapeCanvasWidth = 64;

    private const int CapeCanvasHeight = 32;

    private const int CapeContentWidth = 22;

    private const int CapeContentHeight = 17;

    public static async Task<SkinVisual> LoadAsync(
        Account? account,
        IAppLog? log,
        CancellationToken cancellationToken = default)
    {
        var profileUrl = SkinProfileParser.BuildProfileUrl(account);
        if (profileUrl is null)
        {
            return SkinVisual.Default;
        }

        try
        {
            var json = await Client.GetStringAsync(profileUrl, cancellationToken).ConfigureAwait(false);
            var textures = SkinProfileParser.ParseProfile(json, account!.SkinModel);
            if (textures is null)
            {
                return SkinVisual.Default;
            }

            var skin = await DownloadTextureAsync(textures.SkinUrl, log, cancellationToken).ConfigureAwait(false);
            if (skin is null)
            {
                return SkinVisual.Default;
            }

            ArgbSkinTexture? cape = null;
            if (!string.IsNullOrWhiteSpace(textures.CapeUrl))
            {
                var downloaded = await DownloadTextureAsync(textures.CapeUrl!, log, cancellationToken).ConfigureAwait(false);
                cape = NormalizeCape(downloaded);
            }

            return new SkinVisual(
                skin,
                cape,
                string.Equals(textures.Model, "slim", StringComparison.Ordinal),
                IsLegacyLayout(skin));
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            WarnUnlessCancelled(log, account, "查询账户皮肤失败", ex, cancellationToken);
            return SkinVisual.Default;
        }
    }

    /// <summary>
    /// 老皮肤是 64×32。有些皮肤站会把上传的老图补齐成 64×64，
    /// 这时高度看不出区别，但新版独占的区域（左腿正面）必然全透明。
    /// </summary>
    private static bool IsLegacyLayout(ArgbSkinTexture skin)
    {
        if (skin.Height == SkinMeshBuilder.LegacyTextureHeight)
        {
            return true;
        }

        if (skin.Width != SkinMeshBuilder.TextureWidth || skin.Height != SkinMeshBuilder.TextureHeight)
        {
            // 尺寸不认识时按新版渲染，至少脸和上半身是对的。
            return false;
        }

        for (var y = 52; y < 64; y++)
        {
            for (var x = 20; x < 24; x++)
            {
                if ((skin.GetPixel(x, y) >> 24) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>取消是正常路径（换账户时会打断上一次请求），不该在日志里留噪音。</summary>
    private static void WarnUnlessCancelled(
        IAppLog? log,
        Account? account,
        string action,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        log?.Warn($"{action}：{account?.Username ?? "未登录"}", exception);
    }

    /// <summary>下载并按 PNG 解码。地址为空、响应不是合法 PNG 时返回 null。</summary>
    public static async Task<ArgbSkinTexture?> DownloadTextureAsync(string url, IAppLog? log, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await Client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            var texture = DecodePng(bytes, log);
            if (texture is null)
            {
                log?.Warn($"皮肤图片无法解码：{url}");
            }

            return texture;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                log?.Warn($"下载皮肤图片失败：{url}", ex);
            }

            return null;
        }
    }

    /// <summary>
    /// 把一张已解好的皮肤图拼成可渲染的 <see cref="SkinVisual"/>（披风可选）。
    ///
    /// 皮肤页从皮肤站拿到的图走这里：体型由站点字段决定，老版布局仍由
    /// <see cref="IsLegacyLayout"/> 判，不必让界面层再抄一遍规则。
    /// </summary>
    public static SkinVisual CreateVisual(ArgbSkinTexture skin, bool slim, ArgbSkinTexture? cape = null) =>
        new(skin, NormalizeCape(cape), slim, IsLegacyLayout(skin));

    /// <summary>把 PNG 字节解成 <c>0xAARRGGBB</c> 像素。不是可识别的 PNG 时返回 null。</summary>
    public static ArgbSkinTexture? DecodePng(byte[] png, IAppLog? log = null)
    {
        if (png is null || png.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var bitmap = WriteableBitmap.Decode(stream);
            using var frame = bitmap.Lock();

            var width = frame.Size.Width;
            var height = frame.Size.Height;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var isBgra = frame.Format == PixelFormat.Bgra8888;
            if (!isBgra && frame.Format != PixelFormat.Rgba8888)
            {
                return null;
            }

            var raw = new byte[frame.RowBytes * height];
            Marshal.Copy(frame.Address, raw, 0, raw.Length);

            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                var rowStart = y * frame.RowBytes;
                for (var x = 0; x < width; x++)
                {
                    var offset = rowStart + (x * 4);
                    var byte0 = raw[offset];
                    var green = raw[offset + 1];
                    var byte2 = raw[offset + 2];
                    var alpha = raw[offset + 3];
                    var red = isBgra ? byte2 : byte0;
                    var blue = isBgra ? byte0 : byte2;
                    pixels[(y * width) + x] = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
                }
            }

            return new ArgbSkinTexture(width, height, pixels);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            log?.Warn("皮肤 PNG 解码失败", ex);
            return null;
        }
    }

    /// <summary>披风 UV 按 64×32 画布展开，所以尺寸不对的图要先摆正；太小就直接放弃。</summary>
    private static ArgbSkinTexture? NormalizeCape(ArgbSkinTexture? cape)
    {
        if (cape is null)
        {
            return null;
        }

        if (cape.Width == CapeCanvasWidth && cape.Height == CapeCanvasHeight)
        {
            return cape;
        }

        if (cape.Width < CapeContentWidth || cape.Height < CapeContentHeight)
        {
            return null;
        }

        var pixels = new uint[CapeCanvasWidth * CapeCanvasHeight];
        for (var y = 0; y < CapeContentHeight; y++)
        {
            for (var x = 0; x < CapeContentWidth; x++)
            {
                pixels[(y * CapeCanvasWidth) + x] = cape.GetPixel(x, y);
            }
        }

        return new ArgbSkinTexture(CapeCanvasWidth, CapeCanvasHeight, pixels);
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or IOException or UriFormatException;
}
