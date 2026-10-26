using System;
using System.Collections.Generic;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 把 Minecraft 皮肤的 64×64 展开图切成六个面，并按官方人形尺寸拼成低模。
///
/// 坐标约定（与 <see cref="SkinVec3"/> 一致）：脚底 y=0、头顶 y=32，+X 是角色左手、+Z 是角色正面。
/// 尺寸全部取自官方模型（头 8×8×8、身 8×12×4、四肢 12 高、Steve 臂宽 4 / Alex 臂宽 3、披风 10×16×1）。
///
/// UV 展开规则直接对应皮肤图的排版：以 (u,v) 为起点，先横排三块侧面、再纵排。
/// 六个面共用同一条公式，因此改一个盒子的尺寸不会漏改某个面。
/// </summary>
public static class SkinMeshBuilder
{
    /// <summary>皮肤纹理的标准边长。64×32 老版皮肤的高度只有一半。</summary>
    public const int TextureWidth = 64;

    /// <summary>新版皮肤纹理的高度。</summary>
    public const int TextureHeight = 64;

    /// <summary>老版皮肤纹理的高度，没有第二层区域。</summary>
    public const int LegacyTextureHeight = 32;

    /// <summary>脚底到头顶的总高度，用来把模型居中到视口。</summary>
    public const double ModelHeight = 32;

    /// <summary>
    /// 组装一份人形低模。
    /// </summary>
    /// <param name="slim">Alex 体型（臂宽 3、臂心 ±5.5）；否则 Steve（臂宽 4、臂心 ±6）。</param>
    /// <param name="hasCape">是否追加披风部件。</param>
    /// <param name="legacyLayout">64×32 老皮肤：没有独立的左侧肢体与第二层，左侧用右侧纹理镜像。</param>
    /// <param name="includeOuterLayer">
    /// 是否生成帽子/外套/袖子/裤管这层。产品渲染要它；几何测试与老皮肤不需要。
    /// <paramref name="legacyLayout"/> 为 true 时恒为 false，因为老皮肤纹理里没有外层区域。
    /// </param>
    public static SkinMesh Build(
        bool slim = false,
        bool hasCape = false,
        bool legacyLayout = false,
        bool includeOuterLayer = true)
    {
        var buildOuterLayer = includeOuterLayer && !legacyLayout;
        var parts = new List<SkinPart>(buildOuterLayer ? 12 : 6);
        var armWidthPx = slim ? 3 : 4;
        var armWidth = (double)armWidthPx;
        var armCenterX = slim ? 5.5 : 6.0;

        parts.Add(new SkinPart(
            SkinPartName.Head,
            new SkinVec3(0, 28, 0),
            new SkinVec3(8, 8, 8),
            BuildUv(0, 0, 8, 8, 8)));

        parts.Add(new SkinPart(
            SkinPartName.Body,
            new SkinVec3(0, 18, 0),
            new SkinVec3(8, 12, 4),
            BuildUv(16, 16, 8, 12, 4)));

        // 观察者的右手边是角色的左手（+X），所以 RightArm 放在 -X 侧。
        parts.Add(new SkinPart(
            SkinPartName.RightArm,
            new SkinVec3(-armCenterX, 18, 0),
            new SkinVec3(armWidth, 12, 4),
            BuildUv(40, 16, armWidthPx, 12, 4)));

        parts.Add(new SkinPart(
            SkinPartName.LeftArm,
            new SkinVec3(armCenterX, 18, 0),
            new SkinVec3(armWidth, 12, 4),
            legacyLayout
                ? (IReadOnlyDictionary<SkinFace, SkinUv>)MirrorUv(BuildUv(40, 16, armWidthPx, 12, 4))
                : BuildUv(32, 48, armWidthPx, 12, 4)));

        parts.Add(new SkinPart(
            SkinPartName.RightLeg,
            new SkinVec3(-2, 6, 0),
            new SkinVec3(4, 12, 4),
            BuildUv(0, 16, 4, 12, 4)));

        parts.Add(new SkinPart(
            SkinPartName.LeftLeg,
            new SkinVec3(2, 6, 0),
            new SkinVec3(4, 12, 4),
            legacyLayout ? MirrorUv(BuildUv(0, 16, 4, 12, 4)) : BuildUv(16, 48, 4, 12, 4)));

        if (buildOuterLayer)
        {
            // 第二层：头部各边 +0.5，身体与四肢各边 +0.25。UV 的宽高仍按内层尺寸算，
            // 因为纹理展开图上的外层区域和内层一一对齐，只是几何被撑大了。
            parts.Add(new SkinPart(
                SkinPartName.Head,
                new SkinVec3(0, 28, 0),
                new SkinVec3(9, 9, 9),
                BuildUv(32, 0, 8, 8, 8),
                outerLayer: true));

            parts.Add(new SkinPart(
                SkinPartName.Body,
                new SkinVec3(0, 18, 0),
                new SkinVec3(8.5, 12.5, 4.5),
                BuildUv(16, 32, 8, 12, 4),
                outerLayer: true));

            parts.Add(new SkinPart(
                SkinPartName.RightArm,
                new SkinVec3(-armCenterX, 18, 0),
                new SkinVec3(armWidth + 0.5, 12.5, 4.5),
                BuildUv(40, 32, armWidthPx, 12, 4),
                outerLayer: true));

            parts.Add(new SkinPart(
                SkinPartName.LeftArm,
                new SkinVec3(armCenterX, 18, 0),
                new SkinVec3(armWidth + 0.5, 12.5, 4.5),
                BuildUv(48, 48, armWidthPx, 12, 4),
                outerLayer: true));

            parts.Add(new SkinPart(
                SkinPartName.RightLeg,
                new SkinVec3(-2, 6, 0),
                new SkinVec3(4.5, 12.5, 4.5),
                BuildUv(0, 32, 4, 12, 4),
                outerLayer: true));

            parts.Add(new SkinPart(
                SkinPartName.LeftLeg,
                new SkinVec3(2, 6, 0),
                new SkinVec3(4.5, 12.5, 4.5),
                BuildUv(0, 48, 4, 12, 4),
                outerLayer: true));
        }

        if (hasCape)
        {
            // 披风贴图只有 64×32，可见内容是左上 22×17 那块，不能用 BuildUv 的等宽公式。
            parts.Add(new SkinPart(
                SkinPartName.Cape,
                new SkinVec3(0, 18, -2.5),
                new SkinVec3(10, 16, 1),
                BuildCapeUv()));
        }

        return new SkinMesh(parts);
    }

    /// <summary>
    /// 按皮肤展开图公式算一个盒子的六面 UV：横排是「左面 | 正面 | 右面 | 背面」，
    /// 纵向依次是顶面、侧面、底面。这是 mojang 布局，不是随便排的。
    /// </summary>
    public static IReadOnlyDictionary<SkinFace, SkinUv> BuildUv(int u, int v, int width, int height, int depth)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "盒子三个轴都必须为正。");
        }

        return new Dictionary<SkinFace, SkinUv>(6)
        {
            [SkinFace.Top] = new(u + depth, v, u + depth + width, v + depth),
            [SkinFace.Bottom] = new(u + depth + width, v, u + depth + (2 * width), v + depth),
            [SkinFace.Left] = new(u, v + depth, u + depth, v + depth + height),
            [SkinFace.Front] = new(u + depth, v + depth, u + depth + width, v + depth + height),
            [SkinFace.Right] = new(u + depth + width, v + depth, u + depth + (2 * width), v + depth + height),
            [SkinFace.Back] = new(u + depth + (2 * width), v + depth, u + depth + (3 * width), v + depth + height),
        };
    }

    /// <summary>
    /// 披风在 64×32 图上的展开。整块只有 22 宽，所以用不了 <see cref="BuildUv"/> 的等宽公式
    /// （那会把背面推到 31 列，超出披风图的内容区，采样只会拿到透明像素）：
    /// 两侧各 1 像素，朝外的 <see cref="SkinFace.Back"/> 是 (12,1) 起那张 10×16 大图。
    /// </summary>
    public static IReadOnlyDictionary<SkinFace, SkinUv> BuildCapeUv() => new Dictionary<SkinFace, SkinUv>(6)
    {
        [SkinFace.Top] = new(1, 0, 11, 1),
        [SkinFace.Bottom] = new(11, 0, 21, 1),
        [SkinFace.Left] = new(0, 1, 1, 17),
        [SkinFace.Front] = new(1, 1, 11, 17),
        [SkinFace.Right] = new(11, 1, 12, 17),
        [SkinFace.Back] = new(12, 1, 22, 17),
    };

    /// <summary>水平镜像一组 UV：交换每一面的 U 区间端点，采样自然就翻转了。</summary>
    private static Dictionary<SkinFace, SkinUv> MirrorUv(IReadOnlyDictionary<SkinFace, SkinUv> source)
    {
        var mirrored = new Dictionary<SkinFace, SkinUv>(source.Count);
        foreach (var pair in source)
        {
            mirrored[pair.Key] = new SkinUv(pair.Value.U1, pair.Value.V0, pair.Value.U0, pair.Value.V1);
        }

        return mirrored;
    }
}
