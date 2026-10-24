using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Transfer;

namespace NebulaLauncher.Core.Settings;

/// <summary>设置模块的 DI 注册。</summary>
public static class CoreSettingsServiceCollectionExtensions
{
    /// <summary>注册 <see cref="ISettingsService"/> 的 JSON 实现。</summary>
    public static IServiceCollection AddCoreSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISettingsService, SettingsService>();
        return services;
    }

    /// <summary>
    /// 注册配置包导出 / 导入（<see cref="IProfileBundleService"/>）。
    ///
    /// 依赖里只有路径是必须的：设置、服务器列表、收藏三个服务用 <c>GetService</c> 取，
    /// 缺失时对应部分跳过——这样「设置模块先落地、其他模块后接」的中间态仍能启动。
    /// 注意注册顺序：要拿到服务器与收藏，本注册必须在 <c>AddCoreAssets</c> / <c>AddCoreVersions</c> 之后。
    /// </summary>
    public static IServiceCollection AddCoreProfileTransfer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProfileBundleService>(provider => new ProfileBundleService(
            provider.GetRequiredService<ILauncherPaths>(),
            provider.GetService<IAppLog>(),
            provider.GetService<ISettingsService>(),
            provider.GetService<IServerService>(),
            provider.GetService<IVersionFavorites>()));

        return services;
    }
}

