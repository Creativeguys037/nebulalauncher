using System;
using System.Collections.Generic;
using System.Linq;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 3D 皮肤卡的几何核心：部件尺寸、UV 展开、矩阵、投影、背面剔除与深度排序。
///
/// 这些断言全部是纯计算，不碰 Avalonia 类型（测试禁令）。断言用的事实来自
/// Minecraft 官方模型与皮肤展开图，不是从实现里抄下来的数：
/// 头 8×8×8、身 8×12×4、四肢高 12、脚底 y=0 头顶 y=32、Steve 臂宽 4 / Alex 臂宽 3。
/// </summary>
public class AppSkin_GeometryTests
{
    [Fact]
    public void 基础体型_默认不含外层时正好六个部件()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);

        Assert.Equal(6, mesh.Parts.Count);
        Assert.Equal(
            new[]
            {
                SkinPartName.Head,
                SkinPartName.Body,
                SkinPartName.RightArm,
                SkinPartName.LeftArm,
                SkinPartName.RightLeg,
                SkinPartName.LeftLeg,
            },
            mesh.Parts.Select(part => part.Name));
        Assert.False(mesh.HasOuterLayer);
    }

    [Fact]
    public void Steve_臂宽四且臂心在正负六()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);

        var rightArm = Single(mesh, SkinPartName.RightArm);
        var leftArm = Single(mesh, SkinPartName.LeftArm);

        Assert.Equal(4, rightArm.Size.X);
        Assert.Equal(4, leftArm.Size.X);
        Assert.Equal(-6, rightArm.Center.X);
        Assert.Equal(6, leftArm.Center.X);
    }

    [Fact]
    public void Alex_臂宽三且臂心在正负五点五()
    {
        var mesh = SkinMeshBuilder.Build(slim: true, includeOuterLayer: false);

        var rightArm = Single(mesh, SkinPartName.RightArm);
        var leftArm = Single(mesh, SkinPartName.LeftArm);

        Assert.Equal(3, rightArm.Size.X);
        Assert.Equal(3, leftArm.Size.X);
        Assert.Equal(-5.5, rightArm.Center.X);
        Assert.Equal(5.5, leftArm.Center.X);
    }

    [Fact]
    public void 脚底为零头顶为三十二_身体部件上下贴合()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);

        var head = Single(mesh, SkinPartName.Head);
        var body = Single(mesh, SkinPartName.Body);
        var rightLeg = Single(mesh, SkinPartName.RightLeg);
        var rightArm = Single(mesh, SkinPartName.RightArm);

        Assert.Equal(0, rightLeg.Center.Y - (rightLeg.Size.Y / 2), 6);
        Assert.Equal(32, head.Center.Y + (head.Size.Y / 2), 6);
        Assert.Equal(12, rightLeg.Center.Y + (rightLeg.Size.Y / 2), 6);
        Assert.Equal(12, body.Center.Y - (body.Size.Y / 2), 6);
        Assert.Equal(24, body.Center.Y + (body.Size.Y / 2), 6);
        Assert.Equal(24, head.Center.Y - (head.Size.Y / 2), 6);
        Assert.Equal(24, rightArm.Center.Y + (rightArm.Size.Y / 2), 6);
    }

    [Fact]
    public void 头部六个面的纹理区域符合官方展开图()
    {
        var uv = Single(SkinMeshBuilder.Build(includeOuterLayer: false), SkinPartName.Head).Uv;

        AssertRectangle(uv[SkinFace.Front], 8, 8, 16, 16);
        AssertRectangle(uv[SkinFace.Right], 16, 8, 24, 16);
        AssertRectangle(uv[SkinFace.Left], 0, 8, 8, 16);
        AssertRectangle(uv[SkinFace.Back], 24, 8, 32, 16);
        AssertRectangle(uv[SkinFace.Top], 8, 0, 16, 8);
        AssertRectangle(uv[SkinFace.Bottom], 16, 0, 24, 8);
    }

    [Fact]
    public void 身体背面贴的是展开图第三块而不是正面()
    {
        var uv = Single(SkinMeshBuilder.Build(includeOuterLayer: false), SkinPartName.Body).Uv;

        AssertRectangle(uv[SkinFace.Front], 20, 20, 28, 32);
        AssertRectangle(uv[SkinFace.Back], 36, 20, 44, 32);
    }

    [Fact]
    public void 左臂用展开图下半区_右臂用上半区()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);

        AssertRectangle(Single(mesh, SkinPartName.RightArm).Uv[SkinFace.Front], 44, 20, 48, 32);
        AssertRectangle(Single(mesh, SkinPartName.LeftArm).Uv[SkinFace.Front], 36, 52, 40, 64);
        AssertRectangle(Single(mesh, SkinPartName.LeftLeg).Uv[SkinFace.Front], 20, 52, 24, 64);
    }

    [Fact]
    public void 新版皮肤每个面的UV都落在六十四见方的纹理内()
    {
        var mesh = SkinMeshBuilder.Build(hasCape: true);

        foreach (var part in mesh.Parts)
        {
            foreach (var (face, uv) in part.Uv)
            {
                Assert.True(uv.U0 >= 0 && uv.U0 <= 64, $"{part.Name}.{face} U0={uv.U0}");
                Assert.True(uv.U1 >= 0 && uv.U1 <= 64, $"{part.Name}.{face} U1={uv.U1}");
                Assert.True(uv.V0 >= 0 && uv.V0 <= 64, $"{part.Name}.{face} V0={uv.V0}");
                Assert.True(uv.V1 >= 0 && uv.V1 <= 64, $"{part.Name}.{face} V1={uv.V1}");
                Assert.True(uv.U1 > uv.U0, $"{part.Name}.{face} 的 U 区间必须递增");
                Assert.True(uv.V1 > uv.V0, $"{part.Name}.{face} 的 V 区间必须递增");
            }
        }
    }

    [Fact]
    public void 披风只多一个部件且贴在身体背面()
    {
        var withCape = SkinMeshBuilder.Build(hasCape: true, includeOuterLayer: false);
        var cape = Single(withCape, SkinPartName.Cape);

        Assert.Equal(7, withCape.Parts.Count);
        Assert.Equal(10, cape.Size.X);
        Assert.Equal(16, cape.Size.Y);
        Assert.Equal(1, cape.Size.Z);
        Assert.Equal(-2.5, cape.Center.Z);
    }

    [Fact]
    public void 放大层_头部九见方身体与四肢各边多半格()
    {
        var mesh = SkinMeshBuilder.Build();

        var head = mesh.Parts.Single(part => part.Name == SkinPartName.Head && part.OuterLayer);
        var body = mesh.Parts.Single(part => part.Name == SkinPartName.Body && part.OuterLayer);
        var arm = mesh.Parts.Single(part => part.Name == SkinPartName.RightArm && part.OuterLayer);

        Assert.Equal(9, head.Size.X);
        Assert.Equal(9, head.Size.Y);
        Assert.Equal(9, head.Size.Z);
        Assert.Equal(8.5, body.Size.X);
        Assert.Equal(12.5, body.Size.Y);
        Assert.Equal(4.5, body.Size.Z);
        Assert.Equal(4.5, arm.Size.X);
        Assert.Equal(12.5, arm.Size.Y);
        Assert.Equal(4.5, arm.Size.Z);
        Assert.True(mesh.HasOuterLayer);
    }

    [Fact]
    public void 老版六十四乘三十二皮肤_不建外层且左侧肢体用右侧纹理镜像()
    {
        var mesh = SkinMeshBuilder.Build(legacyLayout: true);

        Assert.Equal(6, mesh.Parts.Count);
        Assert.False(mesh.HasOuterLayer);

        var rightArmFront = Single(mesh, SkinPartName.RightArm).Uv[SkinFace.Front];
        var leftArmFront = Single(mesh, SkinPartName.LeftArm).Uv[SkinFace.Front];

        // 镜像靠交换 U 区间端点表达，跨度必须一致、方向必须相反。
        Assert.Equal(rightArmFront.U1 - rightArmFront.U0, leftArmFront.U0 - leftArmFront.U1);
        Assert.Equal(rightArmFront.U1, leftArmFront.U0);
        Assert.Equal(rightArmFront.U0, leftArmFront.U1);
        Assert.True(leftArmFront.IsMirrored);
    }

    [Fact]
    public void UV展开公式_顶面与底面宽度取深度而不是宽度()
    {
        var uv = SkinMeshBuilder.BuildUv(16, 16, 8, 12, 4);

        // 身体：深度 4、宽 8，所以顶面是 8 宽 4 高，底面紧随其后。
        AssertRectangle(uv[SkinFace.Top], 20, 16, 28, 20);
        AssertRectangle(uv[SkinFace.Bottom], 28, 16, 36, 20);
        AssertRectangle(uv[SkinFace.Left], 16, 20, 20, 32);
    }

    [Fact]
    public void 恒等矩阵不改变点()
    {
        var point = new SkinVec3(1.5, -2.25, 7);
        var result = SkinMat4.Identity.Transform(point);

        Assert.Equal(1.5, result.X, 9);
        Assert.Equal(-2.25, result.Y, 9);
        Assert.Equal(7, result.Z, 9);
        Assert.Equal(1, result.W, 9);
    }

    [Fact]
    public void 透视矩阵的W等于视空间深度的相反数_近远平面映射到正负一()
    {
        var perspective = SkinMat4.Perspective(60 * Math.PI / 180, 1.0, 0.5, 1000);

        // 相机朝 -Z 看，位于 z=-10 的点在视空间深度是 10。
        var clip = perspective.Transform(new SkinVec3(0, 0, -10));
        Assert.Equal(10, clip.W, 9);

        // 深度不能直接断言为 0：透视除法是非线性的，近平面映射到 -1、远平面到 +1。
        var nearPoint = perspective.Transform(new SkinVec3(0, 0, -0.5));
        Assert.Equal(-1, nearPoint.Z / nearPoint.W, 5);

        var farPoint = perspective.Transform(new SkinVec3(0, 0, -1000));
        Assert.Equal(1, farPoint.Z / farPoint.W, 5);
    }

    [Fact]
    public void 模型中心投影到视口正中()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);
        var projection = SkinProjector.Project(
            mesh,
            SkinOrbit.Front,
            200,
            260,
            cullBackFaces: true);

        var center = projection.ViewProjection.Transform(new SkinVec3(0, SkinProjector.OrbitCenterY, 0));

        Assert.Equal(0, center.X / center.W, 6);
        Assert.Equal(0, center.Y / center.W, 6);
        Assert.True(center.W > 0);
    }

    [Fact]
    public void 矩阵顺序写反时正面会被近平面剔光_正序时正面可见()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);

        // 相机抬到头顶以上再看（pitch 45 时相机高度 50.9 > 头顶 32），
        // 这样正面与顶面同时可见，背面对应被剔掉。
        var projection = SkinProjector.Project(
            mesh,
            new SkinOrbit(0, 45),
            200,
            260,
            cullBackFaces: true);

        var headFaces = projection.Quads
            .Where(quad => quad.Part == SkinPartName.Head)
            .Select(quad => quad.Face)
            .ToHashSet();

        Assert.Contains(SkinFace.Front, headFaces);
        Assert.Contains(SkinFace.Top, headFaces);
        Assert.DoesNotContain(SkinFace.Back, headFaces);
    }

    [Fact]
    public void 视角下每个面都有投影面积_关闭剔除时面数等于部件数乘六()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);

        // 斜着看，任何面都不会退化成一条线，面数才是干净的 6 面×部件数。
        var all = SkinProjector.Project(mesh, new SkinOrbit(30, 20), 200, 260, cullBackFaces: false);
        var culled = SkinProjector.Project(mesh, new SkinOrbit(30, 20), 200, 260, cullBackFaces: true);

        Assert.Equal(mesh.Parts.Count * 6, all.Quads.Count);
        Assert.True(culled.Quads.Count < all.Quads.Count);
        Assert.True(culled.Quads.Count > 0);
    }

    [Fact]
    public void 背面剔除留下的一定是朝着相机的面()
    {
        var mesh = SkinMeshBuilder.Build(includeOuterLayer: false);
        var projection = SkinProjector.Project(mesh, new SkinOrbit(35, 12), 200, 260, cullBackFaces: true);

        foreach (var quad in projection.Quads)
        {
            var part = mesh.Parts.First(candidate => candidate.Name == quad.Part && candidate.OuterLayer == quad.OuterLayer);
            var corners = SkinProjector.GetWorldCorners(part, quad.Face);
            Assert.True(SkinProjector.IsFacingCamera(corners, projection.Eye), $"{quad.Part}.{quad.Face} 不该保留");
        }
    }

    [Fact]
    public void 画家算法按深度从远到近排列()
    {
        var mesh = SkinMeshBuilder.Build();
        var projection = SkinProjector.Project(mesh, new SkinOrbit(20, 15), 240, 300, cullBackFaces: true);

        for (var i = 1; i < projection.Quads.Count; i++)
        {
            Assert.True(
                projection.Quads[i - 1].Depth >= projection.Quads[i].Depth,
                $"第 {i} 个面的深度比前一个更远，排序反了");
        }
    }

    [Fact]
    public void 屏幕上四边形是顺时针绕向_叉积为负()
    {
        var mesh = SkinMeshBuilder.Build();
        var projection = SkinProjector.Project(mesh, SkinOrbit.Front, 200, 260, cullBackFaces: true);

        Assert.NotEmpty(projection.Quads);
        foreach (var quad in projection.Quads)
        {
            var a = quad.Vertices[0];
            var b = quad.Vertices[1];
            var c = quad.Vertices[2];
            var cross = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
            Assert.True(cross < 0, $"{quad.Part}.{quad.Face} 的屏幕绕向不是顺时针，叉积={cross}");
        }
    }

    [Fact]
    public void 底面纹理要旋转一百八十度()
    {
        var uv = new SkinUv(16, 0, 24, 8);

        var corners = SkinProjector.GetCornerUvs(uv, SkinFace.Bottom);

        // 左下角取纹理右上角，才有 180° 的翻转效果。
        Assert.Equal((24, 0), corners[0]);
        Assert.Equal((16, 0), corners[1]);
        Assert.Equal((16, 8), corners[2]);
        Assert.Equal((24, 8), corners[3]);
    }

    [Fact]
    public void 正面纹理的左下角对应纹理左下角_其余面不旋转()
    {
        var uv = new SkinUv(8, 8, 16, 16);

        var corners = SkinProjector.GetCornerUvs(uv, SkinFace.Front);

        Assert.Equal((8, 16), corners[0]);
        Assert.Equal((16, 16), corners[1]);
        Assert.Equal((16, 8), corners[2]);
        Assert.Equal((8, 8), corners[3]);
    }

    private static SkinPart Single(SkinMesh mesh, SkinPartName name) =>
        mesh.Parts.Single(part => part.Name == name && !part.OuterLayer);

    private static void AssertRectangle(SkinUv uv, int u0, int v0, int u1, int v1)
    {
        Assert.Equal(new SkinUv(u0, v0, u1, v1).ToString(), uv.ToString());
    }
}
