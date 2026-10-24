using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Core.Java;

/// <summary>Java 服务的 DI 注册入口。</summary>
public static class JavaServiceCollectionExtensions
{
    public static IServiceCollection AddCoreJava(this IServiceCollection services)
    {
        // 工厂注册：IAppLog / ISettingsService 是可选依赖，部分宿主（tools/InstallProbe）
        // 只注册路径与平台服务；裸类型注册会让 DI 在构造解析阶段直接失败。
        services.AddSingleton<IJavaService>(sp => new JavaService(
            sp.GetRequiredService<IPlatformService>(),
            sp.GetRequiredService<ILauncherPaths>(),
            sp.GetRequiredService<DownloadQueue>(),
            sp.GetService<IAppLog>(),
            sp.GetService<ISettingsService>(),
            // 下载工厂用于拉 Mojang 的 java-runtime 清单；缺失时 DownloadRuntimeAsync 退回 Adoptium。
            sp.GetService<DownloadHttpClientFactory>()));

        return services;
    }
}
