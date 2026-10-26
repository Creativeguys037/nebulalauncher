using System;
using System.Collections.Generic;
using System.Linq;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 软件光栅器与默认皮肤。核心是六方向纯色探针：把头的六个面各涂一种纯色，
/// 转到对应视角后画面里该颜色必须占非透明像素的一半以上。
///
/// 探针用的 UV 矩形是硬编码的官方展开图事实，不从 <see cref="SkinMeshBuilder.BuildUv"/> 取，
/// 否则 UV 公式写错时探针会跟着一起错，测不出问题。
/// </summary>
public class AppSkin_RendererTests
{
    private const uint Red = 0xFFFF0000;

    private const uint Blue = 0xFF0000FF;

    private const uint Green = 0xFF00FF00;

    private const uint Yellow = 0xFFFFFF00;

    private const uint Magenta = 0xFFFF00FF;

    private const uint Cyan = 0xFF00FFFF;

    private const int ViewportWidth = 128;

    private const int ViewportHeight = 128;

    [Theory]
    [InlineData(0, 0, Red)]      // 正对正面：红
    [InlineData(180, 0, Blue)]   // 转到背面：蓝
    [InlineData(90, 0, Yellow)]  // 相机在 +X 侧：角色左手面，黄
    [InlineData(-90, 0, Green)]  // 相机在 -X 侧：角色右手面，绿
    [InlineData(0, 85, Magenta)] // 从上方看：顶面，品红
    [InlineData(0, -85, Cyan)]   // 从下方看：底面，青
    public void 六方向纯色探针_主色占比过半(double yaw, double pitch, uint expected)
    {
        var share = RenderHeadShare(expected, yaw, pitch, cullBackFaces: true);

        Assert.True(share > 0.5, $"yaw={yaw}, pitch={pitch} 时期望色 {expected:X8} 只占 {share:P1}");
    }

    [Fact]
    public void 有披风时从背面能看到披风的主色()
    {
        var pixels = new uint[ViewportWidth * ViewportHeight];

        SkinRenderer.Render(
            pixels,
            ViewportWidth,
            ViewportHeight,
            SkinMeshBuilder.Build(hasCape: true),
            new SkinOrbit(180, 0),
            DefaultSkin.Create(),
            background: 0,
            cullBackFaces: true,
            capeTexture: CapeTexture(Red));

        // 披风贴在身体背面，背面视角下它挡在身体前面：红色应当占相当一块画面。
        var share = CountColor(pixels, Red) / (double)CountOpaque(pixels);
        Assert.True(share > 0.2, $"背面视角下披风红色只占 {share:P1}，说明披风展开图没贴上");
    }

    [Fact]
    public void 关闭背面剔除后仍然显示正面主色_说明深度排序挡住了背面()
    {
        var pixels = RenderHead(new SkinOrbit(0, 0), cullBackFaces: false);
        var share = CountColor(pixels, Red) / (double)CountOpaque(pixels);

        Assert.True(share > 0.5, $"关掉剔除后红色占比 {share:P1}，说明画家算法没把背面压到后面");
    }

    [Fact]
    public void 关闭背面剔除时被投影的面数翻倍()
    {
        var mesh = HeadMesh();
        var culled = SkinProjector.Project(mesh, SkinOrbit.Front, ViewportWidth, ViewportHeight, cullBackFaces: true);
        var all = SkinProjector.Project(mesh, SkinOrbit.Front, ViewportWidth, ViewportHeight, cullBackFaces: false);

        Assert.True(all.Quads.Count > culled.Quads.Count);
        Assert.Equal(6, all.Quads.Count);
    }

    [Fact]
    public void 全透明纹理画不出任何像素()
    {
        var texture = new ArgbSkinTexture(64, 64, new uint[64 * 64]);
        var pixels = new uint[ViewportWidth * ViewportHeight];

        var stats = SkinRenderer.Render(pixels, ViewportWidth, ViewportHeight, HeadMesh(), SkinOrbit.Front, texture);

        Assert.Equal(0, stats.DrawnPixels);
    }

    [Fact]
    public void 默认皮肤尺寸正确且外层保持透明()
    {
        var texture = DefaultSkin.Create();

        Assert.Equal(64, texture.Width);
        Assert.Equal(64, texture.Height);

        // 头顶、脸、身体正面都必须有颜色，否则默认皮肤会是一张空图。
        Assert.NotEqual(0u, texture.GetPixel(12, 4));
        Assert.NotEqual(0u, texture.GetPixel(12, 12));
        Assert.NotEqual(0u, texture.GetPixel(24, 26));

        // 第二层区域必须全透明，否则会把内层整个盖住。
        for (var v = 0; v < 16; v++)
        {
            for (var u = 32; u < 64; u++)
            {
                Assert.Equal(0u, texture.GetPixel(u, v));
            }
        }
    }

    [Fact]
    public void 默认皮肤能画出一个完整的人形()
    {
        var mesh = SkinMeshBuilder.Build();
        var texture = DefaultSkin.Create();
        var pixels = new uint[240 * 300];

        var stats = SkinRenderer.Render(pixels, 240, 300, mesh, new SkinOrbit(15, 8), texture);

        Assert.True(stats.QuadCount > 0);
        Assert.True(stats.DrawnPixels > 240 * 300 / 10, $"只画出了 {stats.DrawnPixels} 个像素");
    }

    [Fact]
    public void 侧边栏尺寸下单帧渲染足够快()
    {
        var mesh = SkinMeshBuilder.Build(hasCape: true);
        var texture = DefaultSkin.Create();
        var pixels = new uint[220 * 280];
        var samples = new List<double>(9);

        // 先渲染两帧热身，避免 JIT 与首次纹理访问污染测量。
        SkinRenderer.Render(pixels, 220, 280, mesh, SkinOrbit.Front, texture);
        SkinRenderer.Render(pixels, 220, 280, mesh, SkinOrbit.Front, texture);

        for (var i = 0; i < 9; i++)
        {
            var stats = SkinRenderer.Render(pixels, 220, 280, mesh, new SkinOrbit(i * 10, 5), texture);
            samples.Add(stats.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];

        // 阈值只用来防回归（自转 60fps 的预算是 16.7ms），不是性能承诺。
        Assert.True(median < 16.7, $"中位单帧 {median:0.00}ms 超出 60fps 预算");
    }

    private static double RenderHeadShare(uint expected, double yaw, double pitch, bool cullBackFaces)
    {
        var pixels = RenderHead(new SkinOrbit(yaw, pitch), cullBackFaces);
        var opaque = CountOpaque(pixels);
        Assert.True(opaque > 0, "画面里一个像素都没画出来");
        return (double)CountColor(pixels, expected) / opaque;
    }

    private static uint[] RenderHead(SkinOrbit orbit, bool cullBackFaces)
    {
        var texture = HeadTexture();
        var pixels = new uint[ViewportWidth * ViewportHeight];
        SkinRenderer.Render(pixels, ViewportWidth, ViewportHeight, HeadMesh(), orbit, texture, 0, cullBackFaces);
        return pixels;
    }

    /// <summary>披风图是 64×32，可见内容在左上 22×17：整块涂成同一色，方便断言。</summary>
    private static ArgbSkinTexture CapeTexture(uint color)
    {
        var pixels = new uint[64 * 32];
        for (var y = 0; y < 17; y++)
        {
            for (var x = 0; x < 22; x++)
            {
                pixels[(y * 64) + x] = color;
            }
        }

        return new ArgbSkinTexture(64, 32, pixels);
    }

    /// <summary>只有一个头部盒子的模型：探针只需要六个面，其他部件会挡住视线。</summary>
    private static SkinMesh HeadMesh() => new(
    [
        new SkinPart(
            SkinPartName.Head,
            new SkinVec3(0, 28, 0),
            new SkinVec3(8, 8, 8),
            new Dictionary<SkinFace, SkinUv>
            {
                [SkinFace.Front] = new(8, 8, 16, 16),
                [SkinFace.Back] = new(24, 8, 32, 16),
                [SkinFace.Right] = new(16, 8, 24, 16),
                [SkinFace.Left] = new(0, 8, 8, 16),
                [SkinFace.Top] = new(8, 0, 16, 8),
                [SkinFace.Bottom] = new(16, 0, 24, 8),
            }),
    ]);

    private static ArgbSkinTexture HeadTexture()
    {
        var pixels = new uint[64 * 64];
        Paint(pixels, new SkinUv(8, 8, 16, 16), Red);
        Paint(pixels, new SkinUv(24, 8, 32, 16), Blue);
        Paint(pixels, new SkinUv(16, 8, 24, 16), Yellow);
        Paint(pixels, new SkinUv(0, 8, 8, 16), Green);
        Paint(pixels, new SkinUv(8, 0, 16, 8), Magenta);
        Paint(pixels, new SkinUv(16, 0, 24, 8), Cyan);
        return new ArgbSkinTexture(64, 64, pixels);
    }

    private static void Paint(uint[] pixels, SkinUv uv, uint color)
    {
        for (var v = uv.V0; v < uv.V1; v++)
        {
            for (var u = uv.U0; u < uv.U1; u++)
            {
                pixels[(v * 64) + u] = color;
            }
        }
    }

    private static int CountOpaque(uint[] pixels) => pixels.Count(pixel => ((pixel >> 24) & 0xFF) != 0);

    private static int CountColor(uint[] pixels, uint color) =>
        pixels.Count(pixel => (pixel & 0x00FFFFFF) == (color & 0x00FFFFFF) && ((pixel >> 24) & 0xFF) != 0);
}
