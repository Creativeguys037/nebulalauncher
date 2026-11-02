using System;
using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 内置的默认皮肤：Mojang 官方客户端自带的 Steve（classic）与 Alex（slim）64×64 纹理。
///
/// PNG 资源由 <see cref="DefaultSkinAssets"/> 统一提供（账户头像也用它），这里只负责解码。
/// 不再用代码逐像素画脸：手绘版本既不像官方皮肤，也会在默认皮肤上重复一套与贴图布局无关的逻辑。
/// </summary>
public static class DefaultSkin
{
    /// <summary>Steve（classic，4 像素宽手臂）。</summary>
    public static ArgbSkinTexture Create() => Create(slim: false);

    /// <summary>按模型选择官方默认皮肤：<paramref name="slim"/> 为 true 时返回 Alex。</summary>
    public static ArgbSkinTexture Create(bool slim) => PngDecoder.Decode(DefaultSkinAssets.Read(slim));
}
