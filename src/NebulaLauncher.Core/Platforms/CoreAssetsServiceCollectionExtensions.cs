using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Core.Platforms;

/// <summary>
/// 资源链（平台 / 下载 / Java / 安装 / 内容 / 备份 / 服务器）的一站式注册。
/// 依赖 <c>IAppLog</c> 与 <c>ILauncherPaths</c>，调用前必须先由启动侧模块注册它们。
/// </summary>
public static class CoreAssetsServiceCollectionExtensions
{
    public static IServiceCollection AddCoreAssets(this IServiceCollection services)
    {
        services.AddCorePlatforms();
        services.AddCoreDownloads();
        services.AddCoreJava();
        services.AddCoreInstalling();
        services.AddCoreContent();
        services.AddCoreBackups();
        services.AddCoreServers();
        return services;
    }
}
