using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 极小的 PNG 解码器，只服务内置默认皮肤：8 位、非隔行、RGB / 调色板 / RGBA 三种格式。
///
/// 为什么不用 Avalonia 的 <c>WriteableBitmap.Decode</c>：默认皮肤会被纯计算的几何/渲染测试
/// 直接读取，那些测试不启动 Avalonia 平台；把图片解码留在这里，测试才能在没有 UI 的进程里跑。
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ArgbSkinTexture Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("不是 PNG 文件。");
        }

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = -1;
        var interlace = -1;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        while (offset + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > png.Length)
            {
                throw new InvalidDataException("PNG 数据块长度越界。");
            }

            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    if (length != 13)
                    {
                        throw new InvalidDataException("PNG IHDR 长度不对。");
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    break;

                case "PLTE":
                    palette = data.ToArray();
                    break;

                case "tRNS":
                    transparency = data.ToArray();
                    break;

                case "IDAT":
                    idat.Write(data);
                    break;

                case "IEND":
                    offset = png.Length;
                    break;
            }

            offset += 12 + length;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG 缺少有效的 IHDR。");
        }

        if (bitDepth != 8 || interlace != 0)
        {
            throw new InvalidDataException($"不支持的 PNG 格式：bitDepth={bitDepth}, interlace={interlace}。");
        }

        var bytesPerPixel = colorType switch
        {
            2 => 3,
            3 => 1,
            6 => 4,
            _ => throw new InvalidDataException($"不支持的 PNG 颜色类型：{colorType}。"),
        };

        if (colorType == 3 && (palette is null || palette.Length < 3))
        {
            throw new InvalidDataException("调色板 PNG 缺少 PLTE。");
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var compressed = raw.ToArray();

        var lineBytes = width * bytesPerPixel;
        if (compressed.Length < (lineBytes + 1) * height)
        {
            throw new InvalidDataException("PNG 像素数据不足。");
        }

        var previous = new byte[lineBytes];
        var current = new byte[lineBytes];
        var pixels = new uint[width * height];
        var source = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = compressed[source++];
            Buffer.BlockCopy(compressed, source, current, 0, lineBytes);
            source += lineBytes;
            Unfilter(filter, current, previous, bytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                uint color;
                if (colorType == 3)
                {
                    var index = current[x];
                    var p = index * 3;
                    if (p + 2 >= palette!.Length)
                    {
                        throw new InvalidDataException("PNG 调色板索引越界。");
                    }

                    var alpha = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                    color = ((uint)alpha << 24) | ((uint)palette[p] << 16) | ((uint)palette[p + 1] << 8) | palette[p + 2];
                }
                else if (colorType == 2)
                {
                    var p = x * 3;
                    color = 0xFF000000u | ((uint)current[p] << 16) | ((uint)current[p + 1] << 8) | current[p + 2];
                }
                else
                {
                    var p = x * 4;
                    color = ((uint)current[p + 3] << 24) | ((uint)current[p] << 16) | ((uint)current[p + 1] << 8) | current[p + 2];
                }

                pixels[(y * width) + x] = color;
            }

            (previous, current) = (current, previous);
        }

        return new ArgbSkinTexture(width, height, pixels);
    }

    private static void Unfilter(int filter, byte[] current, byte[] previous, int bytesPerPixel)
    {
        for (var i = 0; i < current.Length; i++)
        {
            var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
            var up = previous[i];
            var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

            current[i] = filter switch
            {
                0 => current[i],
                1 => (byte)(current[i] + left),
                2 => (byte)(current[i] + up),
                3 => (byte)(current[i] + ((left + up) / 2)),
                4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException($"不支持的 PNG 行过滤类型：{filter}。"),
            };
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var p = left + up - upLeft;
        var pa = Math.Abs(p - left);
        var pb = Math.Abs(p - up);
        var pc = Math.Abs(p - upLeft);
        if (pa <= pb && pa <= pc)
        {
            return left;
        }

        return pb <= pc ? up : upLeft;
    }
}
