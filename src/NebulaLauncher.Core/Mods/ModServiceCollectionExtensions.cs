using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 模组更新检查的 DI 注册入口。
///
/// 为什么单独一个文件、而不是塞进 <c>ContentServiceCollectionExtensions.AddCoreContent</c>：
/// 那个文件的注册项已经在 4 个并行任务之间共享，改它会和别人的改动撞在一起。
/// 这里只新增一个独立入口，由组合根显式调用，冲突面为零。
/// </summary>
public static class ModServiceCollectionExtensions
{
    /// <summary>
    /// 注册「本地指纹 + 在线更新检查」。
    ///
    /// 依赖 <see cref="IModrinthService"/>（必需）与 <see cref="ICurseForgeService"/>（可选）、
    /// <see cref="ILauncherPaths"/>（用于哈希缓存）。缺失时服务仍能构造，
    /// 只是 CurseForge 反查与缓存不可用——页面不会因为没填 Key 就白屏。
    /// </summary>
    public static IServiceCollection AddCoreModUpdates(this IServiceCollection services)
    {
        services.AddSingleton<IModUpdateService, ModUpdateService>();

        // 项目信息补全与更新检查共用数据源，但缓存策略不同，各自一份实例（不共享状态）。
        services.AddSingleton<IModProjectInfoService, ModProjectInfoService>();

        return services;
    }
}
