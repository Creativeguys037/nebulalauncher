using System;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 渲染入口：清屏 → 投影排序 → 按画家算法逐面光栅。
///
/// 缓冲是 <c>0xAARRGGBB</c>，控件层按 BGRA 小端直接拷进 <c>WriteableBitmap</c>，
/// 所以这里的颜色字面量不需要任何通道交换。
/// </summary>
public static class SkinRenderer
{
    /// <summary>一帧的渲染结果统计，用于性能基准与「画面是不是空的」这类自检。</summary>
    public readonly record struct FrameStats(
        int QuadCount,
        int CulledQuadCount,
        int DrawnPixels,
        double ProjectMilliseconds,
        double RasterMilliseconds)
    {
        public double TotalMilliseconds => ProjectMilliseconds + RasterMilliseconds;
    }

    /// <summary>渲染一帧到 <paramref name="pixels"/>。缓冲区长度必须 ≥ width×height。</summary>
    /// <param name="capeTexture">
    /// 可选的披风纹理。披风在皮肤图上没有对应区域，只能单独取一张图；
    /// 不传时披风部件会退回用皮肤纹理采样（正常流程里根本不建披风部件）。
    /// </param>
    public static FrameStats Render(
        uint[] pixels,
        int width,
        int height,
        SkinMesh mesh,
        SkinOrbit orbit,
        ISkinTexture texture,
        uint background = 0,
        bool cullBackFaces = true,
        ISkinTexture? capeTexture = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(texture);
        if (pixels.Length < width * height)
        {
            throw new ArgumentException("像素缓冲小于视口尺寸。", nameof(pixels));
        }

        Array.Fill(pixels, background, 0, width * height);

        var projectStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var projection = SkinProjector.Project(mesh, orbit, width, height, cullBackFaces);
        var projectEnd = System.Diagnostics.Stopwatch.GetTimestamp();

        foreach (var quad in projection.Quads)
        {
            var source = quad.Part == SkinPartName.Cape && capeTexture is not null ? capeTexture : texture;
            SkinRasterizer.FillQuad(pixels, width, height, quad, source);
        }

        var rasterEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;

        var visible = projection.Quads.Count;
        var culled = (mesh.Parts.Count * 6) - visible;
        var drawn = 0;
        for (var i = 0; i < width * height; i++)
        {
            if (((pixels[i] >> 24) & 0xFF) != 0)
            {
                drawn++;
            }
        }

        return new FrameStats(
            visible,
            Math.Max(0, culled),
            drawn,
            (projectEnd - projectStart) / frequency * 1000.0,
            (rasterEnd - projectEnd) / frequency * 1000.0);
    }
}
