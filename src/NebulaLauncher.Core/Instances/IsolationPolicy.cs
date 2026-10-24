using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 版本隔离策略的纯函数规则。
///
/// 抽成静态类是为了让「什么情况下该隔离」这件事只有一个实现，且可以脱离 IO 单测：
/// 版本页、首页、资源页都要回答这个问题，各写一套必然会漂移。
/// </summary>
public static class IsolationPolicyResolver
{
    /// <summary>把 <c>settings.json</c> 里的字符串解析成枚举。非法值退回 <see cref="IsolationPolicyKind.Modded"/>。</summary>
    public static IsolationPolicyKind Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "never" => IsolationPolicyKind.Never,
        "always" => IsolationPolicyKind.Always,
        "modded" => IsolationPolicyKind.Modded,
        _ => IsolationPolicyKind.Modded,
    };

    /// <summary>枚举 → 持久化字符串。与 <see cref="Parse"/> 互为逆运算。</summary>
    public static string ToSettingValue(IsolationPolicyKind policy) => policy switch
    {
        IsolationPolicyKind.Never => "never",
        IsolationPolicyKind.Always => "always",
        _ => "modded",
    };

    /// <summary>
    /// 该加载器在该策略下是否需要独立目录。
    /// </summary>
    /// <param name="policy">设置页选择的策略。</param>
    /// <param name="loader">目标版本的加载器。</param>
    /// <param name="versionDirectoryHasContent">
    /// 版本目录下是否已经有 <c>mods/</c> 或 <c>saves/</c>。为 true 时恒隔离——
    /// 这是 PCL2 <c>ModMinecraft.vb:252-256</c> 的判据：目录里已经有玩家内容就说明它本来就在隔离状态，
    /// 设置页改成「不隔离」也不该让这些内容突然读不到。
    /// </param>
    public static bool ShouldIsolate(
        IsolationPolicyKind policy,
        LoaderKind loader,
        bool versionDirectoryHasContent = false)
    {
        if (versionDirectoryHasContent)
        {
            return true;
        }

        return policy switch
        {
            IsolationPolicyKind.Always => true,
            IsolationPolicyKind.Modded => loader != LoaderKind.Vanilla,
            _ => false,
        };
    }

    /// <summary>策略的中文说明，供设置页展示。</summary>
    public static string Describe(IsolationPolicyKind policy) => policy switch
    {
        IsolationPolicyKind.Never => "不隔离：所有版本共用 .minecraft 的 mods / saves",
        IsolationPolicyKind.Always => "始终隔离：每个版本首次启动都建独立目录",
        _ => "仅加载器版本隔离：Fabric / Quilt / Forge / NeoForge 首次启动建独立目录",
    };
}
