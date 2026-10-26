using System;
using System.Collections.Generic;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 内置的默认皮肤（Steve 风格），在没有账户或账户皮肤拉不到时兜底。
///
/// 用代码画而不是放一个 PNG 资产：不引入第三方素材、不受仓库 ACL 限制，
/// 也让「默认皮肤不是空白」这件事可以被单测直接断言（头顶/身体区域 alpha 必须非零）。
/// </summary>
public static class DefaultSkin
{
    private const uint SkinTone = 0xFFC68E6B;

    private const uint SkinShade = 0xFFA9714F;

    private const uint Hair = 0xFF3A2416;

    private const uint EyeWhite = 0xFFEFEFEF;

    private const uint EyeIris = 0xFF3A3A9E;

    private const uint Mouth = 0xFF6E4230;

    private const uint Shirt = 0xFF00A0A0;

    private const uint ShirtShade = 0xFF00807F;

    private const uint Pants = 0xFF3A3A8E;

    private const uint Shoe = 0xFF4A4A4A;

    /// <summary>每次调用都新建一份纹理，调用方可以安全地长期持有。</summary>
    public static ArgbSkinTexture Create() => new(
        SkinMeshBuilder.TextureWidth,
        SkinMeshBuilder.TextureHeight,
        BuildPixels());

    /// <summary>生成 64×64 像素数组，外层区域保持全透明。</summary>
    public static uint[] BuildPixels()
    {
        var pixels = new uint[SkinMeshBuilder.TextureWidth * SkinMeshBuilder.TextureHeight];

        DrawHead(pixels);
        DrawBody(pixels);
        DrawArm(pixels, SkinMeshBuilder.BuildUv(40, 16, 4, 12, 4));
        DrawArm(pixels, SkinMeshBuilder.BuildUv(32, 48, 4, 12, 4));
        DrawLeg(pixels, SkinMeshBuilder.BuildUv(0, 16, 4, 12, 4));
        DrawLeg(pixels, SkinMeshBuilder.BuildUv(16, 48, 4, 12, 4));

        return pixels;
    }

    private static void DrawHead(uint[] pixels)
    {
        var uv = SkinMeshBuilder.BuildUv(0, 0, 8, 8, 8);
        FillRect(pixels, uv[SkinFace.Front], 0, 0, 8, 8, SkinTone);
        FillRect(pixels, uv[SkinFace.Left], 0, 0, 8, 8, SkinTone);
        FillRect(pixels, uv[SkinFace.Right], 0, 0, 8, 8, SkinTone);
        FillRect(pixels, uv[SkinFace.Back], 0, 0, 8, 8, Hair);
        FillRect(pixels, uv[SkinFace.Top], 0, 0, 8, 8, Hair);
        FillRect(pixels, uv[SkinFace.Bottom], 0, 0, 8, 8, SkinShade);

        // 刘海：正面与两侧的顶上两行。
        FillRect(pixels, uv[SkinFace.Front], 0, 0, 8, 2, Hair);
        FillRect(pixels, uv[SkinFace.Left], 0, 0, 8, 2, Hair);
        FillRect(pixels, uv[SkinFace.Right], 0, 0, 8, 2, Hair);

        // 眼睛：2×2 一块，内侧是瞳孔。
        FillRect(pixels, uv[SkinFace.Front], 1, 3, 3, 5, EyeWhite);
        FillRect(pixels, uv[SkinFace.Front], 2, 3, 3, 5, EyeIris);
        FillRect(pixels, uv[SkinFace.Front], 5, 3, 7, 5, EyeWhite);
        FillRect(pixels, uv[SkinFace.Front], 5, 3, 6, 5, EyeIris);

        // 鼻子与嘴。
        FillRect(pixels, uv[SkinFace.Front], 3, 5, 5, 6, SkinShade);
        FillRect(pixels, uv[SkinFace.Front], 3, 6, 5, 7, Mouth);
    }

    private static void DrawBody(uint[] pixels)
    {
        var uv = SkinMeshBuilder.BuildUv(16, 16, 8, 12, 4);
        foreach (var face in AllFaces)
        {
            FillRect(pixels, uv[face], 0, 0, 8, 12, Shirt);
        }

        // 领口与下摆用深一点的青，避免整个人是一块平板色。
        FillRect(pixels, uv[SkinFace.Front], 0, 0, 8, 1, SkinShade);
        FillRect(pixels, uv[SkinFace.Back], 0, 0, 8, 1, SkinShade);
        FillRect(pixels, uv[SkinFace.Front], 0, 10, 8, 12, ShirtShade);
        FillRect(pixels, uv[SkinFace.Back], 0, 10, 8, 12, ShirtShade);
        FillRect(pixels, uv[SkinFace.Top], 0, 0, 8, 4, Shirt);
        FillRect(pixels, uv[SkinFace.Bottom], 0, 0, 8, 4, ShirtShade);
    }

    /// <summary>短袖：肩膀附近三行是衬衫，其余是皮肤。</summary>
    private static void DrawArm(uint[] pixels, IReadOnlyDictionary<SkinFace, SkinUv> uv)
    {
        foreach (var face in AllFaces)
        {
            FillRect(pixels, uv[face], 0, 0, 4, 12, SkinTone);
        }

        FillRect(pixels, uv[SkinFace.Top], 0, 0, 4, 4, Shirt);
        FillRect(pixels, uv[SkinFace.Bottom], 0, 0, 4, 4, SkinTone);
        FillRect(pixels, uv[SkinFace.Front], 0, 0, 4, 3, Shirt);
        FillRect(pixels, uv[SkinFace.Back], 0, 0, 4, 3, Shirt);
        FillRect(pixels, uv[SkinFace.Left], 0, 0, 4, 3, Shirt);
        FillRect(pixels, uv[SkinFace.Right], 0, 0, 4, 3, Shirt);
    }

    /// <summary>长裤 + 鞋：上面八行裤子，最下面四行鞋。</summary>
    private static void DrawLeg(uint[] pixels, IReadOnlyDictionary<SkinFace, SkinUv> uv)
    {
        foreach (var face in AllFaces)
        {
            FillRect(pixels, uv[face], 0, 0, 4, 12, Pants);
        }

        FillRect(pixels, uv[SkinFace.Top], 0, 0, 4, 4, Pants);
        FillRect(pixels, uv[SkinFace.Bottom], 0, 0, 4, 4, Shoe);
        FillRect(pixels, uv[SkinFace.Front], 0, 8, 4, 12, Shoe);
        FillRect(pixels, uv[SkinFace.Back], 0, 8, 4, 12, Shoe);
        FillRect(pixels, uv[SkinFace.Left], 0, 8, 4, 12, Shoe);
        FillRect(pixels, uv[SkinFace.Right], 0, 8, 4, 12, Shoe);
    }

    /// <summary>
    /// 在一个面内填一块矩形。<paramref name="left"/>、<paramref name="top"/>、
    /// <paramref name="right"/>、<paramref name="bottom"/> 都是**面内相对坐标**，
    /// 右开区间（<paramref name="right"/> 与 <paramref name="bottom"/> 不含），
    /// 并且会被钳在本面之内——越界写入会污染相邻面，那是最难查的一类错。
    /// </summary>
    private static void FillRect(
        uint[] pixels,
        SkinUv uv,
        int left,
        int top,
        int right,
        int bottom,
        uint color)
    {
        var faceU = Math.Min(uv.U0, uv.U1);
        var faceV = Math.Min(uv.V0, uv.V1);
        var faceWidth = Math.Abs(uv.U1 - uv.U0);
        var faceHeight = Math.Abs(uv.V1 - uv.V0);

        var startU = faceU + Math.Max(0, left);
        var startV = faceV + Math.Max(0, top);
        var endU = Math.Min(faceU + Math.Min(right, faceWidth), SkinMeshBuilder.TextureWidth);
        var endV = Math.Min(faceV + Math.Min(bottom, faceHeight), SkinMeshBuilder.TextureHeight);

        for (var v = startV; v < endV; v++)
        {
            for (var u = startU; u < endU; u++)
            {
                pixels[(v * SkinMeshBuilder.TextureWidth) + u] = color;
            }
        }
    }

    private static readonly SkinFace[] AllFaces =
    [
        SkinFace.Front,
        SkinFace.Back,
        SkinFace.Right,
        SkinFace.Left,
        SkinFace.Top,
        SkinFace.Bottom,
    ];
}
