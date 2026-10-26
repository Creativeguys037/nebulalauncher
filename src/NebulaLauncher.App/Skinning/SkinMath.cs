using System;
using System.Linq;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 模型空间向量。单位是 Minecraft 皮肤像素（1 px = 1/16 格），坐标约定见 <see cref="SkinMeshBuilder"/>：
/// +X 是角色左手、+Y 向上、+Z 是角色正面。
///
/// 这个类型刻意不引用 Avalonia：几何核心要能被测试项目直接断言，
/// 而本仓测试禁令针对的是「触碰 Avalonia 类型」，不是 App 项目本身。
/// </summary>
public readonly struct SkinVec3 : IEquatable<SkinVec3>
{
    public SkinVec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    public static SkinVec3 Zero => new(0, 0, 0);

    public static SkinVec3 operator +(SkinVec3 a, SkinVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static SkinVec3 operator -(SkinVec3 a, SkinVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static SkinVec3 operator *(SkinVec3 a, double scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);

    public static SkinVec3 operator -(SkinVec3 a) => new(-a.X, -a.Y, -a.Z);

    public static double Dot(SkinVec3 a, SkinVec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static SkinVec3 Cross(SkinVec3 a, SkinVec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    public double Length => Math.Sqrt(Dot(this, this));

    public SkinVec3 Normalized()
    {
        var length = Length;
        return length < 1e-12 ? Zero : new SkinVec3(X / length, Y / length, Z / length);
    }

    public static bool operator ==(SkinVec3 left, SkinVec3 right) => left.Equals(right);

    public static bool operator !=(SkinVec3 left, SkinVec3 right) => !left.Equals(right);

    public bool Equals(SkinVec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is SkinVec3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####})";
}

/// <summary>齐次坐标点。<see cref="W"/> 是透视除法前的分母，测试要断言它等于视空间深度的相反数。</summary>
public readonly struct SkinVec4
{
    public SkinVec4(double x, double y, double z, double w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    public double W { get; }

    public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####}, {W:0.####})";
}

/// <summary>
/// 4×4 齐次矩阵，行主序存储 + 列向量约定：<c>Transform(v) = M * v</c>。
///
/// 组合顺序是这条链上最容易写错的地方：世界坐标的顶点要依次经过模型、视图、投影，
/// 列向量约定下必须写成 <c>projection * view * model</c>；写反不报错，只会让所有面被近平面剔除、输出空图。
/// </summary>
public readonly struct SkinMat4
{
    private readonly double[] _m;

    private SkinMat4(double[] values)
    {
        _m = values;
    }

    public static SkinMat4 Identity => new(
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ]);

    public double this[int row, int column] => _m[(row * 4) + column];

    public static SkinMat4 Translation(double x, double y, double z) => new(
    [
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1,
    ]);

    public static SkinMat4 RotationX(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new(
        [
            1, 0, 0, 0,
            0, c, -s, 0,
            0, s, c, 0,
            0, 0, 0, 1,
        ]);
    }

    public static SkinMat4 RotationY(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new(
        [
            c, 0, s, 0,
            0, 1, 0, 0,
            -s, 0, c, 0,
            0, 0, 0, 1,
        ]);
    }

    /// <summary>
    /// 右手系透视投影，相机朝 -Z 看。<c>W = -viewZ</c>，所以前方的点 W 恒为正，
    /// 透视除法不会因为符号翻转让整屏翻面。
    /// </summary>
    public static SkinMat4 Perspective(double fovRadians, double aspect, double near, double far)
    {
        if (fovRadians <= 0 || fovRadians >= Math.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(fovRadians), fovRadians, "视角必须在 (0, π) 之间。");
        }

        if (aspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspect), aspect, "宽高比必须为正。");
        }

        if (near <= 0 || far <= near)
        {
            throw new ArgumentOutOfRangeException(nameof(near), near, "近平面必须为正且小于远平面。");
        }

        var f = 1.0 / Math.Tan(fovRadians / 2.0);
        return new(
        [
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), (2 * far * near) / (near - far),
            0, 0, -1, 0,
        ]);
    }

    /// <summary>右手系 LookAt：返回把世界坐标搬到视空间的矩阵（相机朝 -Z 看）。</summary>
    public static SkinMat4 LookAt(SkinVec3 eye, SkinVec3 target, SkinVec3 up)
    {
        var forward = (target - eye).Normalized();
        var side = SkinVec3.Cross(forward, up).Normalized();
        if (side.Length < 1e-9)
        {
            // 视线与 up 平行时叉积退化，换一个参考轴，否则整张图会静默变黑。
            side = SkinVec3.Cross(forward, new SkinVec3(0, 0, 1)).Normalized();
        }

        var trueUp = SkinVec3.Cross(side, forward);
        return new(
        [
            side.X, side.Y, side.Z, -SkinVec3.Dot(side, eye),
            trueUp.X, trueUp.Y, trueUp.Z, -SkinVec3.Dot(trueUp, eye),
            -forward.X, -forward.Y, -forward.Z, SkinVec3.Dot(forward, eye),
            0, 0, 0, 1,
        ]);
    }

    public static SkinMat4 operator *(SkinMat4 a, SkinMat4 b)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var sum = 0.0;
                for (var k = 0; k < 4; k++)
                {
                    sum += a[row, k] * b[k, column];
                }

                result[(row * 4) + column] = sum;
            }
        }

        return new SkinMat4(result);
    }

    public SkinVec4 Transform(SkinVec3 point) => new(
        (this[0, 0] * point.X) + (this[0, 1] * point.Y) + (this[0, 2] * point.Z) + this[0, 3],
        (this[1, 0] * point.X) + (this[1, 1] * point.Y) + (this[1, 2] * point.Z) + this[1, 3],
        (this[2, 0] * point.X) + (this[2, 1] * point.Y) + (this[2, 2] * point.Z) + this[2, 3],
        (this[3, 0] * point.X) + (this[3, 1] * point.Y) + (this[3, 2] * point.Z) + this[3, 3]);

    public SkinVec4 Transform(SkinVec4 point) => new(
        (this[0, 0] * point.X) + (this[0, 1] * point.Y) + (this[0, 2] * point.Z) + (this[0, 3] * point.W),
        (this[1, 0] * point.X) + (this[1, 1] * point.Y) + (this[1, 2] * point.Z) + (this[1, 3] * point.W),
        (this[2, 0] * point.X) + (this[2, 1] * point.Y) + (this[2, 2] * point.Z) + (this[2, 3] * point.W),
        (this[3, 0] * point.X) + (this[3, 1] * point.Y) + (this[3, 2] * point.Z) + (this[3, 3] * point.W));

    public override string ToString()
    {
        var m00 = this[0, 0];
        var m01 = this[0, 1];
        var m02 = this[0, 2];
        var m03 = this[0, 3];
        var m10 = this[1, 0];
        var m11 = this[1, 1];
        var m12 = this[1, 2];
        var m13 = this[1, 3];
        var m20 = this[2, 0];
        var m21 = this[2, 1];
        var m22 = this[2, 2];
        var m23 = this[2, 3];
        var m30 = this[3, 0];
        var m31 = this[3, 1];
        var m32 = this[3, 2];
        var m33 = this[3, 3];
        return string.Join(
            Environment.NewLine,
            $"| {m00:0.####} {m01:0.####} {m02:0.####} {m03:0.####} |",
            $"| {m10:0.####} {m11:0.####} {m12:0.####} {m13:0.####} |",
            $"| {m20:0.####} {m21:0.####} {m22:0.####} {m23:0.####} |",
            $"| {m30:0.####} {m31:0.####} {m32:0.####} {m33:0.####} |");
    }
}
