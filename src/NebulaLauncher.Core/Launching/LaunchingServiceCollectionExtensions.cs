using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Launching;

/// <summary>启动模块的 DI 注册。</summary>
public static class LaunchingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IGameLauncher"/>、<see cref="IPreflightChecker"/> 与
    /// <see cref="IAuthlibInjectorService"/>。
    /// 依赖 <see cref="IJavaService"/> 与 <see cref="IVersionService"/>；
    /// <see cref="IContentService"/> / <see cref="IPlatformService"/> 可选，缺失时对应检查项跳过。
    /// 注入服务还需要 <c>DownloadQueue</c> / <c>DownloadHttpClientFactory</c>，
    /// 在只注册了启动模块的宿主里拿不到时它退化为「只用本地缓存」。
    /// </summary>
    public static IServiceCollection AddGameLaunching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<IPreflightChecker, PreflightCheckerService>();
        services.AddSingleton<IAuthlibInjectorService, AuthlibInjectorService>();
        return services;
    }
}
