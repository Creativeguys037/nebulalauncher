using System;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 只读皮肤纹理。坐标原点在左上、单位是像素，颜色是 <c>0xAARRGGBB</c>。
///
/// 接口刻意不依赖 Avalonia 的 <c>Bitmap</c>：几何核心与软件光栅器要能在测试里
/// 用合成纹理跑（六方向纯色探针就是这么做的），控件层再拿位图适配成这个接口。
/// </summary>
public interface ISkinTexture
{
    int Width { get; }

    int Height { get; }

    uint GetPixel(int x, int y);
}

/// <summary>用一块托管像素数组实现纹理。控件层把解码后的位图拷进来，测试直接构造。</summary>
public sealed class ArgbSkinTexture : ISkinTexture
{
    private readonly uint[] _pixels;

    public ArgbSkinTexture(int width, int height, uint[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "纹理尺寸必须为正。");
        }

        if (pixels.Length < width * height)
        {
            throw new ArgumentException("像素数量少于宽×高。", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public uint GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return 0;
        }

        return _pixels[(y * Width) + x];
    }
}
