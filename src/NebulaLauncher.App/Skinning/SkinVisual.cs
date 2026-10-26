using System;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 一份可以直接交给渲染器的皮肤：主纹理、可选披风纹理，以及影响几何构建的体型与版式。
///
/// 皮肤与披风在世界里是两张不同的图，而 <see cref="ISkinTexture"/> 只描述一张，
/// 所以这里分开保存，由 <see cref="SkinRenderer.Render"/> 按部件挑。
/// </summary>
public sealed class SkinVisual
{
    /// <summary>默认皮肤：没登录、拉取失败、或者对方没上传皮肤时都用它。</summary>
    public static SkinVisual Default { get; } = new(DefaultSkin.Create(), null, false, false);

    public SkinVisual(ISkinTexture texture, ISkinTexture? cape, bool slim, bool legacyLayout)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Texture = texture;
        Cape = cape;
        Slim = slim;
        LegacyLayout = legacyLayout;
    }

    public ISkinTexture Texture { get; }

    public ISkinTexture? Cape { get; }

    /// <summary>Alex 体型（手臂 3 像素宽）为 true，Steve 体型为 false。</summary>
    public bool Slim { get; }

    /// <summary>64×32 的老版皮肤为 true：没有双层外层，左半身靠镜像展开。</summary>
    public bool LegacyLayout { get; }

    public bool HasCape => Cape is not null;

    /// <summary>按这份皮肤构建网格。没有披风就不建披风部件。</summary>
    public SkinMesh BuildMesh() => SkinMeshBuilder.Build(Slim, HasCape, LegacyLayout);
}
