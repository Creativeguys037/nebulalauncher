using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Instances;

/// <summary>实例模块的 DI 注册。</summary>
public static class InstanceServiceCollectionExtensions
{
    /// <summary>注册 <see cref="IInstanceService"/> 与 <see cref="IInstanceContext"/>。</summary>
    public static IServiceCollection AddInstances(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IInstanceService, InstanceService>();

        // 版本隔离的运行时状态。页面一律经它取游戏目录，不再直接读 ILauncherPaths.MinecraftDirectory。
        services.TryAddSingleton<IInstanceContext, InstanceContext>();

        return services;
    }

    /// <summary>
    /// 注册 <see cref="IInstanceLockService"/>：同一游戏目录同时只允许一个游戏进程。
    ///
    /// 与 <see cref="AddInstances"/> 分开成一个方法：启动链（<c>GameLauncher</c>）要依赖它，
    /// 而只注册了启动模块的宿主（工具、探针）不该被迫拉起整套实例服务。
    /// </summary>
    public static IServiceCollection AddInstanceLocks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IInstanceLockService, InstanceLockService>();
        return services;
    }

    /// <summary>
    /// 注册 <see cref="IInstanceFolderService"/>：实例卡上的存档 / 模组 / 资源包 / 截图 / 日志芯片。
    ///
    /// 实现无依赖（只读磁盘与纯函数），单独成一个方法是为了让「没注册就退化」显式可见：
    /// 契约的默认实现返回「没有入口」，忘了注册不会抛，只会少一行芯片。
    /// </summary>
    public static IServiceCollection AddInstanceFolders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IInstanceFolderService, InstanceFolderService>();
        return services;
    }
}
