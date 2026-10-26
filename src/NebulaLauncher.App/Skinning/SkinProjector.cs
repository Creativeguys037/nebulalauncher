using System;
using System.Collections.Generic;

namespace NebulaLauncher.Skinning;

/// <summary>投影到屏幕之后的一个顶点。UV 单位是纹理像素，<see cref="Depth"/> 越大越远。</summary>
public readonly struct SkinScreenVertex
{
    public SkinScreenVertex(double x, double y, double u, double v, double depth, double invW)
    {
        X = x;
        Y = y;
        U = u;
        V = v;
        Depth = depth;
        InvW = invW;
    }

    /// <summary>屏幕坐标，原点在左上、y 向下。</summary>
    public double X { get; }

    public double Y { get; }

    public double U { get; }

    public double V { get; }

    /// <summary>视空间深度（正数，越大越远），画家算法按它排序。</summary>
    public double Depth { get; }

    /// <summary>1/w，透视校正插值用。</summary>
    public double InvW { get; }
}

/// <summary>一个屏幕空间四边形：4 个顶点按「从外面看逆时针」的顺序。</summary>
public sealed class SkinScreenQuad
{
    public SkinScreenQuad(SkinPartName part, SkinFace face, bool outerLayer, SkinScreenVertex[] vertices)
    {
        if (vertices.Length != 4)
        {
            throw new ArgumentException("四边形必须有 4 个顶点。", nameof(vertices));
        }

        Part = part;
        Face = face;
        OuterLayer = outerLayer;
        Vertices = vertices;
        Depth = (vertices[0].Depth + vertices[1].Depth + vertices[2].Depth + vertices[3].Depth) / 4.0;
    }

    public SkinPartName Part { get; }

    public SkinFace Face { get; }

    public bool OuterLayer { get; }

    public IReadOnlyList<SkinScreenVertex> Vertices { get; }

    /// <summary>画家算法的排序键：四个顶点深度的平均值。</summary>
    public double Depth { get; }

    /// <summary>四个顶点 UV 的包围矩形，用来把插值出的 UV 钳在本面内部。</summary>
    public (double MinU, double MaxU, double MinV, double MaxV) UvBounds
    {
        get
        {
            var minU = double.MaxValue;
            var maxU = double.MinValue;
            var minV = double.MaxValue;
            var maxV = double.MinValue;
            foreach (var vertex in Vertices)
            {
                minU = Math.Min(minU, vertex.U);
                maxU = Math.Max(maxU, vertex.U);
                minV = Math.Min(minV, vertex.V);
                maxV = Math.Max(maxV, vertex.V);
            }

            return (minU, maxU, minV, maxV);
        }
    }
}

/// <summary>一次投影的结果：把四边形按深度从远到近排好，光栅器按顺序画即可。</summary>
public sealed class SkinProjection
{
    public SkinProjection(IReadOnlyList<SkinScreenQuad> quads, SkinMat4 viewProjection, SkinVec3 eye)
    {
        Quads = quads;
        ViewProjection = viewProjection;
        Eye = eye;
    }

    public IReadOnlyList<SkinScreenQuad> Quads { get; }

    /// <summary>完整的 <c>projection * view * model</c>，给测试断言乘法顺序用。</summary>
    public SkinMat4 ViewProjection { get; }

    public SkinVec3 Eye { get; }
}

/// <summary>
/// 把 <see cref="SkinMesh"/> 投影成屏幕四边形，并完成背面剔除与画家算法排序。
///
/// 这是整条渲染链上最容易「静默出错」的一段：矩阵顺序写反、屏幕绕向判反、深度排序反了，
/// 都不会抛异常，只会输出空图或乱序图。所以每一步刻意写成可断言的小函数。
/// </summary>
public static class SkinProjector
{
    /// <summary>模型中心的高度，相机绕它转、投影也以它为中心。</summary>
    public const double OrbitCenterY = SkinMeshBuilder.ModelHeight / 2.0;

    private const double NearPlane = 0.5;

    private const double FarPlane = 1000;

    public static SkinProjection Project(
        SkinMesh mesh,
        SkinOrbit orbit,
        int viewportWidth,
        int viewportHeight,
        bool cullBackFaces)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "视口尺寸必须为正。");
        }

        var eye = ComputeEye(orbit);
        var model = SkinMat4.Translation(0, -OrbitCenterY, 0);
        var view = SkinMat4.LookAt(eye, SkinVec3.Zero, new SkinVec3(0, 1, 0));
        var projection = SkinMat4.Perspective(
            orbit.Fov * Math.PI / 180.0,
            (double)viewportWidth / viewportHeight,
            NearPlane,
            FarPlane);

        // 列向量约定：顶点先过 model、再过 view、最后过 projection，所以矩阵要反着乘。
        var viewProjection = projection * view * model;

        var quads = new List<SkinScreenQuad>(mesh.Parts.Count * 4);
        foreach (var part in mesh.Parts)
        {
            foreach (var face in AllFaces)
            {
                if (!part.Uv.TryGetValue(face, out var uv))
                {
                    continue;
                }

                var corners = GetWorldCorners(part, face);
                if (cullBackFaces && !IsFacingCamera(corners, eye))
                {
                    continue;
                }

                var quad = ProjectQuad(
                    part,
                    face,
                    corners,
                    uv,
                    viewProjection,
                    view,
                    viewportWidth,
                    viewportHeight,
                    cullBackFaces);

                if (quad is not null)
                {
                    quads.Add(quad);
                }
            }
        }

        // 画家算法：远的先画。同一深度时先画内层，避免外套被自己的内层盖住。
        quads.Sort(static (left, right) =>
        {
            var byDepth = right.Depth.CompareTo(left.Depth);
            return byDepth != 0 ? byDepth : left.OuterLayer.CompareTo(right.OuterLayer);
        });

        return new SkinProjection(quads, viewProjection, eye);
    }

    /// <summary>相机位置：以模型中心为球心，yaw 绕 Y 轴、pitch 抬高。</summary>
    public static SkinVec3 ComputeEye(SkinOrbit orbit)
    {
        var yaw = orbit.Yaw * Math.PI / 180.0;
        var pitch = Math.Clamp(orbit.Pitch, -89.0, 89.0) * Math.PI / 180.0;
        var distance = orbit.Distance;
        var horizontal = Math.Cos(pitch) * distance;
        return new SkinVec3(
            Math.Sin(yaw) * horizontal,
            Math.Sin(pitch) * distance,
            Math.Cos(yaw) * horizontal);
    }

    /// <summary>
    /// 世界空间背面剔除：面朝外时，面的法线与「从面中心指向相机」的方向同侧。
    /// 与屏幕空间绕向判定互为兜底——两条都判，任何一条的符号错都会立刻暴露。
    /// </summary>
    public static bool IsFacingCamera(SkinVec3[] corners, SkinVec3 eye)
    {
        var normal = SkinVec3.Cross(corners[1] - corners[0], corners[2] - corners[0]).Normalized();
        var center = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25;
        return SkinVec3.Dot(normal, eye - center) > 0;
    }

    /// <summary>
    /// 一个盒子的六个面，每个面 4 个世界坐标角点，顺序是「从外面正对着看：左下、右下、右上、左上」。
    /// 这个顺序同时决定了屏幕绕向判定与纹理方向，改它等于把整张皮肤左右/上下翻转。
    /// </summary>
    public static SkinVec3[] GetWorldCorners(SkinPart part, SkinFace face)
    {
        var halfX = part.Size.X / 2.0;
        var halfY = part.Size.Y / 2.0;
        var halfZ = part.Size.Z / 2.0;
        var center = part.Center;
        var minX = center.X - halfX;
        var maxX = center.X + halfX;
        var minY = center.Y - halfY;
        var maxY = center.Y + halfY;
        var minZ = center.Z - halfZ;
        var maxZ = center.Z + halfZ;

        return face switch
        {
            // 正面 (+Z)：观察者站在 +Z 侧，右手边是 +X。
            SkinFace.Front =>
            [
                new(minX, minY, maxZ),
                new(maxX, minY, maxZ),
                new(maxX, maxY, maxZ),
                new(minX, maxY, maxZ),
            ],

            // 背面 (-Z)：观察者右手边变成 -X。
            SkinFace.Back =>
            [
                new(maxX, minY, minZ),
                new(minX, minY, minZ),
                new(minX, maxY, minZ),
                new(maxX, maxY, minZ),
            ],

            // 角色左手侧 (+X)：观察者右手边是 -Z。
            SkinFace.Right =>
            [
                new(maxX, minY, maxZ),
                new(maxX, minY, minZ),
                new(maxX, maxY, minZ),
                new(maxX, maxY, maxZ),
            ],

            // 角色右手侧 (-X)：观察者右手边是 +Z。
            SkinFace.Left =>
            [
                new(minX, minY, minZ),
                new(minX, minY, maxZ),
                new(minX, maxY, maxZ),
                new(minX, maxY, minZ),
            ],

            // 顶面 (+Y)：从上往下看，屏幕上方朝 -Z。
            SkinFace.Top =>
            [
                new(minX, maxY, maxZ),
                new(maxX, maxY, maxZ),
                new(maxX, maxY, minZ),
                new(minX, maxY, minZ),
            ],

            // 底面 (-Y)：从下往上看，屏幕上方朝 +Z。
            SkinFace.Bottom =>
            [
                new(minX, minY, minZ),
                new(maxX, minY, minZ),
                new(maxX, minY, maxZ),
                new(minX, minY, maxZ),
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(face), face, "未知的面。"),
        };
    }

    /// <summary>
    /// 把面的四个角映射到纹理矩形。正常情况下左下角对应纹理右下角（纹理 y 向下）；
    /// 底面要额外旋转 180°，这是 Minecraft 1.0.0 起的官方行为，不转会让鞋底纹路方向错。
    /// </summary>
    public static (int U, int V)[] GetCornerUvs(SkinUv uv, SkinFace face)
    {
        var (u0, v0, u1, v1) = (uv.U0, uv.V0, uv.U1, uv.V1);
        if (face == SkinFace.Bottom)
        {
            return
            [
                (u1, v0),
                (u0, v0),
                (u0, v1),
                (u1, v1),
            ];
        }

        return
        [
            (u0, v1),
            (u1, v1),
            (u1, v0),
            (u0, v0),
        ];
    }

    private static SkinScreenQuad? ProjectQuad(
        SkinPart part,
        SkinFace face,
        SkinVec3[] corners,
        SkinUv uv,
        SkinMat4 viewProjection,
        SkinMat4 view,
        int viewportWidth,
        int viewportHeight,
        bool cullBackFaces)
    {
        var cornerUvs = GetCornerUvs(uv, face);
        var vertices = new SkinScreenVertex[4];
        var screenArea = 0.0;
        var positions = new (double X, double Y)[4];

        for (var i = 0; i < 4; i++)
        {
            var clip = viewProjection.Transform(corners[i]);
            if (clip.W <= 1e-6)
            {
                // 顶点跑到相机后面了。整面丢弃而不是硬裁，小模型上不会出现半个面。
                return null;
            }

            var ndcX = clip.X / clip.W;
            var ndcY = clip.Y / clip.W;
            var screenX = ((ndcX * 0.5) + 0.5) * viewportWidth;

            // NDC 的 y 向上、屏幕 y 向下，所以这里是 0.5 - ndcY/2，不是 0.5 +。
            var screenY = (0.5 - (ndcY * 0.5)) * viewportHeight;
            positions[i] = (screenX, screenY);

            var viewPoint = view.Transform(corners[i]);
            var depth = -viewPoint.Z;
            var cornerUv = cornerUvs[i];
            vertices[i] = new SkinScreenVertex(screenX, screenY, cornerUv.U, cornerUv.V, depth, 1.0 / clip.W);
        }

        if (cullBackFaces)
        {
            // 屏幕坐标 y 向下，从外面看是逆时针的四边形投影后变成顺时针，叉积为负。
            screenArea = ((positions[1].X - positions[0].X) * (positions[2].Y - positions[0].Y))
                - ((positions[2].X - positions[0].X) * (positions[1].Y - positions[0].Y));
            if (screenArea >= 0)
            {
                return null;
            }
        }
        else
        {
            screenArea = ((positions[1].X - positions[0].X) * (positions[2].Y - positions[0].Y))
                - ((positions[2].X - positions[0].X) * (positions[1].Y - positions[0].Y));
            if (Math.Abs(screenArea) < 1e-9)
            {
                return null;
            }
        }

        return new SkinScreenQuad(part.Name, face, part.OuterLayer, vertices);
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
