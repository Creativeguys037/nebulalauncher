using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 皮肤体型的三个说法之间的翻译：界面（<see cref="SkinModelKind"/>：Steve / Alex）、
/// 落盘与 Core（<see cref="LocalSkinModel"/>：Classic / Slim）、账户文件里的字符串（<c>classic</c> / <c>slim</c>）。
///
/// 为什么要单独一个类：同一件事换了三个名字，而票 #44 与 #43 都要在这些名字之间来回走。
/// 散着写迟早会出现「一处认 slim、一处认 alex」，而账户文件是用户能手改的，
/// 脏值必须有唯一确定的退路（<see cref="FromAccountValue"/> 一律回 Classic）。
/// </summary>
public static class SkinModelMapping
{
    /// <summary>界面枚举 → Core 枚举。</summary>
    public static LocalSkinModel ToLocal(SkinModelKind kind) =>
        kind == SkinModelKind.Alex ? LocalSkinModel.Slim : LocalSkinModel.Classic;

    /// <summary>Core 枚举 → 界面枚举。</summary>
    public static SkinModelKind ToKind(LocalSkinModel model) =>
        model == LocalSkinModel.Slim ? SkinModelKind.Alex : SkinModelKind.Steve;

    /// <summary>要写进 <c>Account.SkinModel</c> 的取值。只产出这两个词，别处不要再拼字符串。</summary>
    public static string ToAccountValue(LocalSkinModel model) =>
        model == LocalSkinModel.Slim ? "slim" : "classic";

    /// <summary>读 <c>Account.SkinModel</c>。大小写不敏感，任何不认识的值（含 null）都当经典体型。</summary>
    public static LocalSkinModel FromAccountValue(string? value) =>
        string.Equals(value?.Trim(), "slim", StringComparison.OrdinalIgnoreCase)
            ? LocalSkinModel.Slim
            : LocalSkinModel.Classic;
}
