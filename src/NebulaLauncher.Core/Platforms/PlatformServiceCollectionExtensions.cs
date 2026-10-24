using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Platforms;

/// <summary>平台服务的 DI 注册入口。</summary>
public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddCorePlatforms(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformService, PlatformService>();
        return services;
    }
}
