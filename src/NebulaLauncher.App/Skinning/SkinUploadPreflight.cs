using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 把一张 PNG 送去官方之前先在本地过一遍（票 #44 AC9）。
///
/// 为什么不能只靠服务端的 400：官方只认 64×64 与 64×32，而 <see cref="LocalSkinFormat.Validate"/>
/// 允许 128×64 这类「本地能预览、官方不收」的尺寸。先发出去再被打回来，用户看到的是
/// 一句云端错误；在本地拦下则能直接说「你这张是 128×64」。
///
/// 校验顺序是「能不能当皮肤用」→「官方收不收」：前者给出的是尺寸语义，
/// 后者给出的是官方接口的额外限制，两句提示不该混成一句。
/// </summary>
public static class SkinUploadPreflight
{
    /// <summary>官方接受的两种画布尺寸。</summary>
    private const int OfficialWidth = 64;

    private const int OfficialHeight = 64;

    private const int OfficialLegacyHeight = 32;

    /// <summary>通过校验返回 null，否则返回能直接显示给用户的原因。</summary>
    public static string? Check(byte[]? png, LocalSkinModel model)
    {
        if (png is null || png.Length == 0)
        {
            return "没有读到皮肤图的内容，先选中一套皮肤再上传。";
        }

        if (!LocalSkinFormat.TryReadPngSize(png, out var width, out var height))
        {
            return "这个文件不是有效的 PNG，官方只接受 PNG 格式的皮肤。";
        }

        // 本地规则（#42 的 LocalSkinFormat）先跑：它管的是「这张图能不能当皮肤」。
        if (LocalSkinFormat.Validate(width, height, model) is { } local)
        {
            return local;
        }

        // 官方规则更窄：64×64 或 64×32，没有别的尺寸。
        var officialSize = height == OfficialLegacyHeight
            ? (Width: OfficialWidth, Height: OfficialLegacyHeight)
            : (Width: OfficialWidth, Height: OfficialHeight);

        if (width != officialSize.Width || height != officialSize.Height)
        {
            return $"官方只接受 64×64 或 64×32 的皮肤图，这张是 {width}×{height}。";
        }

        return null;
    }
}
