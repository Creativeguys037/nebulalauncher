using System;
using System.IO;

namespace NebulaLauncher.Core.Skinning;

/// <summary>
/// 内置默认皮肤的原始 PNG 字节：Mojang 官方客户端的 Steve（classic）与 Alex（slim）。
///
/// 资源只放在 Core 一处：账户头像（<c>SkinAvatarService</c>）与 App 的 3D 默认皮肤都读这里，
/// 避免两套「默认皮肤」再次分叉。图片是官方 64×64 展开图，不是按像素手绘的近似图。
/// </summary>
public static class DefaultSkinAssets
{
    /// <summary>读取默认皮肤 PNG。<paramref name="slim"/> 为 true 时返回 Alex，否则返回 Steve。</summary>
    public static byte[] Read(bool slim)
    {
        var name = slim ? "alex" : "steve";
        var resource = "NebulaLauncher.Core.Resources.Skins." + name + ".png";
        using var stream = typeof(DefaultSkinAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"找不到内置默认皮肤资源：{resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
