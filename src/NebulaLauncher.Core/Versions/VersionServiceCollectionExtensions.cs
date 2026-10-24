using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Core.Versions;

/// <summary>版本模块的 DI 注册。</summary>
public static class VersionServiceCollectionExtensions
{
    /// <summary>注册 <see cref="IVersionService"/>。</summary>
    public static IServiceCollection AddCoreVersions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVersionService>(sp => new VersionService(
            sp.GetRequiredService<ILauncherPaths>(),
            sp.GetRequiredService<IAppLog>(),
            sp.GetService<DownloadHttpClientFactory>()?.Client,
            sp.GetService<ISettingsService>(),
            sp.GetService<IDownloadSourceProvider>()));

        // 版本收藏 / 隐藏：独立落盘 favorites.json，不经过 settings.json。
        services.AddSingleton<IVersionFavorites>(sp => new VersionFavorites(
            sp.GetRequiredService<ILauncherPaths>(),
            sp.GetRequiredService<IAppLog>()));

        // 服务端（server.jar）下载：清单复用 IVersionService 的 manifest 缓存，
        // 下载复用 AddCoreDownloads 注册的 DownloadHttpClientFactory（镜像重写 + 候选回退）。
        // ILauncherPaths 只用于落盘缓存 <cache>/server-versions.json。
        services.AddSingleton<IServerDownloadService>(sp => new ServerDownloadService(
            sp.GetRequiredService<IVersionService>(),
            sp.GetRequiredService<DownloadHttpClientFactory>(),
            sp.GetService<IAppLog>(),
            sp.GetService<ILauncherPaths>()));

        return services;
    }
}
