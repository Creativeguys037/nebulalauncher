using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Maintenance;

/// <summary>存储维护模块的 DI 注册入口。</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// 注册存储统计与清理服务。
    ///
    /// 只依赖 <see cref="ILauncherPaths"/> 与可选的 <see cref="IAppLog"/>（容器里有就注入），
    /// 因此在 <c>AddLauncherPaths()</c> / <c>AddCoreLogging()</c> 之后调用即可，没有其他前置模块。
    /// </summary>
    public static IServiceCollection AddCoreStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<StorageService>();
        services.AddSingleton<IStorageService>(sp => sp.GetRequiredService<StorageService>());
        return services;
    }
}