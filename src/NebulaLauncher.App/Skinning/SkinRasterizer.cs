using System;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 软件光栅器：把屏幕四边形画进一块 <c>0xAARRGGBB</c> 像素缓冲，采样皮肤纹理。
///
/// 为什么不用 Avalonia 的几何绘制：这条链要能被单测按像素断言（六方向纯色探针），
/// 而测试禁令不允许测试项目碰 Avalonia 类型。同一份光栅代码产品与测试共用，探针才证明得了产品。
/// 画家算法已经在 <see cref="SkinProjector"/> 排好序，这里按顺序覆盖即可，不做深度缓冲。
///
/// 内循环用 float 与增量式边缘函数：重心坐标对屏幕坐标是线性的，
/// 每像素只做加法就能推进三个权重，避免了每像素十几次浮点乘减。
/// </summary>
public static class SkinRasterizer
{
    /// <summary>
    /// 把四边形拆成两个三角形，按三角形逐个填充。
    /// <paramref name="depthBuffer"/> 可选；传入时用 1/w 做深度测试，避免画家算法在
    /// 形体互相穿插时把面画错，也避免两个三角形在公共对角线上各混一次半透明像素。
    /// </summary>
    public static void FillQuad(
        uint[] pixels,
        int width,
        int height,
        SkinScreenQuad quad,
        ISkinTexture texture,
        float[]? depthBuffer = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(quad);
        ArgumentNullException.ThrowIfNull(texture);
        if (depthBuffer is not null && depthBuffer.Length < width * height)
        {
            throw new ArgumentException("深度缓冲小于视口尺寸。", nameof(depthBuffer));
        }

        var bounds = quad.UvBounds;
        var v0 = quad.Vertices[0];
        var v1 = quad.Vertices[1];
        var v2 = quad.Vertices[2];
        var v3 = quad.Vertices[3];
        FillTriangle(pixels, width, height, v0, v1, v2, bounds, texture, depthBuffer);
        FillTriangle(pixels, width, height, v0, v2, v3, bounds, texture, depthBuffer);
    }

    private static void FillTriangle(
        uint[] pixels,
        int width,
        int height,
        SkinScreenVertex a,
        SkinScreenVertex b,
        SkinScreenVertex c,
        (double MinU, double MaxU, double MinV, double MaxV) uvBounds,
        ISkinTexture texture,
        float[]? depthBuffer)
    {
        var ax = (float)a.X;
        var ay = (float)a.Y;
        var bx = (float)b.X;
        var by = (float)b.Y;
        var cx = (float)c.X;
        var cy = (float)c.Y;

        var area = ((bx - ax) * (cy - ay)) - ((cx - ax) * (by - ay));
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

        // 关键：保留 area 的符号。取绝对值会让三个权重整体翻号，
        // 于是三角形内部被判成外部——屏幕上什么都没有，还不报错。
        var invArea = 1f / area;

        var minX = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)));
        var maxX = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
        var minY = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)));
        var maxY = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
        if (minX < 0)
        {
            minX = 0;
        }

        if (minY < 0)
        {
            minY = 0;
        }

        if (maxX > width - 1)
        {
            maxX = width - 1;
        }

        if (maxY > height - 1)
        {
            maxY = height - 1;
        }

        if (minX > maxX || minY > maxY)
        {
            return;
        }

        // 三个权重的梯度：屏幕 x 或 y 走一格，权重变化量恒定。
        // 对 E0=(b-x)(c-y)-(c-x)(b-y) 求偏导得 (b.y-c.y) 与 (c.x-b.x)；E1 同理。
        var stepW0X = (by - cy) * invArea;
        var stepW0Y = (cx - bx) * invArea;
        var stepW1X = (cy - ay) * invArea;
        var stepW1Y = (ax - cx) * invArea;

        var rowW0 = (((bx - (minX + 0.5f)) * (cy - (minY + 0.5f))) - ((cx - (minX + 0.5f)) * (by - (minY + 0.5f)))) * invArea;
        var rowW1 = (((cx - (minX + 0.5f)) * (ay - (minY + 0.5f))) - ((ax - (minX + 0.5f)) * (cy - (minY + 0.5f)))) * invArea;

        // 取每个纹素的中心：UV 矩形的边界落在纹素边界上，直接用边界值会因为插值误差
        // 采到隔壁的透明/黑色像素，在 HD 皮肤上表现为棱角黑线。矩形至少保留 1 个纹素。
        var minU = (float)(uvBounds.MinU + 0.5);
        var maxU = (float)(uvBounds.MaxU - 0.5);
        if (maxU < minU)
        {
            minU = (float)uvBounds.MinU;
            maxU = (float)Math.Max(uvBounds.MinU, uvBounds.MaxU - 1e-3);
        }

        var minV = (float)(uvBounds.MinV + 0.5);
        var maxV = (float)(uvBounds.MaxV - 0.5);
        if (maxV < minV)
        {
            minV = (float)uvBounds.MinV;
            maxV = (float)Math.Max(uvBounds.MinV, uvBounds.MaxV - 1e-3);
        }

        var aInvW = (float)a.InvW;
        var bInvW = (float)b.InvW;
        var cInvW = (float)c.InvW;
        var aU = (float)a.U;
        var bU = (float)b.U;
        var cU = (float)c.U;
        var aV = (float)a.V;
        var bV = (float)b.V;
        var cV = (float)c.V;

        for (var y = minY; y <= maxY; y++)
        {
            var w0 = rowW0;
            var w1 = rowW1;
            var index = (y * width) + minX;

            for (var x = minX; x <= maxX; x++)
            {
                // 允许极小的负权重：两个三角形/相邻面共享边时，浮点误差会让
                // 两侧都判成外部，留下一条透出背景的缝。深度缓冲会挡住重复写入。
                if (w0 >= -1e-6f && w1 >= -1e-6f)
                {
                    var w2 = 1.0f - w0 - w1;
                    if (w2 >= -1e-6f)
                    {
                        // 透视校正：屏幕空间线性插值的是 1/w 与 uv/w，再统一除回去。
                        var invW = (w0 * aInvW) + (w1 * bInvW) + (w2 * cInvW);
                        if (invW > 1e-12f)
                        {
                            var u = (((w0 * aU * aInvW) + (w1 * bU * bInvW) + (w2 * cU * cInvW)) / invW);
                            var v = (((w0 * aV * aInvW) + (w1 * bV * bInvW) + (w2 * cV * cInvW)) / invW);

                            // 钳到本面的矩形内再取整：插值误差会让边缘像素落进隔壁格子，
                            // 表现出来是脸旁边多一条别的颜色。
                            if (u < minU)
                            {
                                u = minU;
                            }
                            else if (u > maxU)
                            {
                                u = maxU;
                            }

                            if (v < minV)
                            {
                                v = minV;
                            }
                            else if (v > maxV)
                            {
                                v = maxV;
                            }

                            var texel = texture.GetPixel((int)u, (int)v);
                            var alpha = (byte)(texel >> 24);

                            // 深度测试：1/w 越大越靠近相机。严格大于同时挡住两个三角形
                            // 在公共对角线上的第二次写入，半透明像素不会在那里被混两遍。
                            if (alpha != 0 && (depthBuffer is null || invW > depthBuffer[index]))
                            {
                                if (depthBuffer is not null)
                                {
                                    depthBuffer[index] = invW;
                                }

                                if (alpha == 255)
                                {
                                    pixels[index] = texel;
                                }
                                else
                                {
                                    Blend(pixels, index, texel, alpha);
                                }
                            }
                        }
                    }
                }

                index++;
                w0 += stepW0X;
                w1 += stepW1X;
            }

            rowW0 += stepW0Y;
            rowW1 += stepW1Y;
        }
    }

    /// <summary>标准的 source-over 混合。外层纹理大面积透明，直接覆盖会把内层抠出洞。</summary>
    private static void Blend(uint[] pixels, int index, uint texel, byte alpha)
    {
        var destination = pixels[index];
        var destinationAlpha = (byte)(destination >> 24);
        if (destinationAlpha == 0)
        {
            pixels[index] = texel;
            return;
        }

        var sourceAlpha = alpha / 255.0;
        var destinationAlphaFactor = destinationAlpha / 255.0;
        var inverse = 1.0 - sourceAlpha;
        var outAlpha = sourceAlpha + (destinationAlphaFactor * inverse);
        if (outAlpha <= 0)
        {
            pixels[index] = 0;
            return;
        }

        // 直通 alpha 的 source-over：目标色要先乘自己的 alpha，再按输出 alpha 归一。
        // 少了 destinationAlphaFactor 会让半透明边缘发黑，HD 皮肤棱角处尤其明显。
        var red = ((((texel >> 16) & 0xFF) * sourceAlpha)
            + (((destination >> 16) & 0xFF) * destinationAlphaFactor * inverse)) / outAlpha;
        var green = ((((texel >> 8) & 0xFF) * sourceAlpha)
            + (((destination >> 8) & 0xFF) * destinationAlphaFactor * inverse)) / outAlpha;
        var blue = (((texel & 0xFF) * sourceAlpha)
            + ((destination & 0xFF) * destinationAlphaFactor * inverse)) / outAlpha;

        var resultAlpha = (byte)Math.Clamp((int)Math.Round(outAlpha * 255), 0, 255);
        var resultRed = (byte)Math.Clamp((int)Math.Round(red), 0, 255);
        var resultGreen = (byte)Math.Clamp((int)Math.Round(green), 0, 255);
        var resultBlue = (byte)Math.Clamp((int)Math.Round(blue), 0, 255);
        pixels[index] = ((uint)resultAlpha << 24) | ((uint)resultRed << 16) | ((uint)resultGreen << 8) | resultBlue;
    }
}
