using System;
using System.Collections.Generic;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 盒子的六个面。命名按「观察者看角色的左右」，与 Minecraft 皮肤纹理的展开图一致：
/// <list type="bullet">
///   <item><see cref="Front"/> 是角色的脸/胸，+Z 面。</item>
///   <item><see cref="Right"/> 是观察者的右手边，也就是角色的**左手**，+X 面。</item>
///   <item><see cref="Left"/> 是观察者的左手边，也就是角色的**右手**，-X 面。</item>
/// </list>
/// 这条约定写反过一次就会让左右两侧的纹理互换，而纯色皮肤下肉眼几乎看不出来。
/// </summary>
public enum SkinFace
{
    Front,
    Back,
    Right,
    Left,
    Top,
    Bottom,
}

/// <summary>人形模型的部件。顺序同时也是纹理展开图从左到右的习惯顺序。</summary>
public enum SkinPartName
{
    Head,
    Body,
    RightArm,
    LeftArm,
    RightLeg,
    LeftLeg,
    Cape,
}

/// <summary>
/// 纹理上某个面的矩形区域，单位是皮肤像素。<c>U0</c> 可以大于 <c>U1</c>：
/// 64×32 老皮肤的左侧肢体是右臂/右腿纹理的水平镜像，靠反向区间表达，不需要额外标志位。
/// </summary>
public readonly struct SkinUv
{
    public SkinUv(int u0, int v0, int u1, int v1)
    {
        U0 = u0;
        V0 = v0;
        U1 = u1;
        V1 = v1;
    }

    public int U0 { get; }

    public int V0 { get; }

    public int U1 { get; }

    public int V1 { get; }

    public bool IsMirrored => U0 > U1;

    public override string ToString() => $"({U0},{V0})-({U1},{V1})";
}

/// <summary>
/// 一个盒子部件。<see cref="Center"/> 是几何中心的世界坐标（脚底 y=0、头顶 y=32），
/// <see cref="Size"/> 是它在三个轴上的像素尺寸。
/// </summary>
public sealed class SkinPart
{
    public SkinPart(
        SkinPartName name,
        SkinVec3 center,
        SkinVec3 size,
        IReadOnlyDictionary<SkinFace, SkinUv> uv,
        bool outerLayer = false)
    {
        Name = name;
        Center = center;
        Size = size;
        Uv = uv;
        OuterLayer = outerLayer;
    }

    public SkinPartName Name { get; }

    public SkinVec3 Center { get; }

    public SkinVec3 Size { get; }

    public IReadOnlyDictionary<SkinFace, SkinUv> Uv { get; }

    /// <summary>
    /// 是否是第二层（帽子、外套、袖子、裤管）。外层必须后画、且纹理带 alpha，
    /// 排序用它的几何中心即可，因为它总是包在内层外面。
    /// </summary>
    public bool OuterLayer { get; }

    public override string ToString() => $"{Name} {Size} @ {Center}";
}

/// <summary>一个人的完整低模：6 个本体部件 + 可选的披风 + 每个部件可选的外层。</summary>
public sealed class SkinMesh
{
    public SkinMesh(IReadOnlyList<SkinPart> parts)
    {
        Parts = parts ?? throw new ArgumentNullException(nameof(parts));
    }

    public IReadOnlyList<SkinPart> Parts { get; }

    /// <summary>是否含外层。老版 64×32 皮肤没有外层区域，这个值为 false。</summary>
    public bool HasOuterLayer
    {
        get
        {
            foreach (var part in Parts)
            {
                if (part.OuterLayer)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// 观察角度。绕目标的球面轨道：<see cref="Yaw"/> 绕 Y 轴（0 度正对角色正面），
/// <see cref="Pitch"/> 是俯仰（正值从上方看），<see cref="Distance"/> 是相机到模型中心的距离。
/// </summary>
public readonly struct SkinOrbit
{
    public SkinOrbit(double yawDegrees, double pitchDegrees, double distance = 72, double fovDegrees = 30)
    {
        Yaw = yawDegrees;
        Pitch = pitchDegrees;
        Distance = distance;
        Fov = fovDegrees;
    }

    /// <summary>正对角色正面的角度。</summary>
    public static SkinOrbit Front => new(0, 0);

    public double Yaw { get; }

    public double Pitch { get; }

    public double Distance { get; }

    public double Fov { get; }
}
