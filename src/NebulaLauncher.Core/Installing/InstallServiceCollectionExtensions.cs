using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Core.Installing;

/// <summary>版本安装的 DI 注册入口。</summary>
public static class InstallServiceCollectionExtensions
{
    public static IServiceCollection AddCoreInstalling(this IServiceCollection services)
    {
        // 用工厂注册而不是裸类型注册：安装器的逐文件并发要从设置注入，
        // 而设置服务在 tools/InstallProbe 这类宿主里并不存在，必须按「可选依赖」处理。
        //
        // 显式列构造函数参数而不是靠容器构造：工厂里要拿到实例才能写 MaxParallel，
        // 列出参数能让「这个服务需要什么」在这一处看全；容器构造会把依赖藏进
        // InstallService 的签名里，改签名时这段不会报错，只在运行期解析失败。
        services.AddSingleton(sp =>
        {
            var service = new InstallService(
                sp.GetRequiredService<ILauncherPaths>(),
                sp.GetRequiredService<IPlatformService>(),
                sp.GetRequiredService<IJavaService>(),
                sp.GetRequiredService<DownloadHttpClientFactory>(),
                sp.GetService<IAppLog>(),
                sp.GetService<DownloadQueue>(),
                sp.GetService<IModrinthService>());

            var settings = sp.GetService<ISettingsService>();
            if (settings is not null)
            {
                // 与 DownloadQueue 同样的「快照 + 订阅」模式：改完设置不必重建服务。
                // 漏接这一条就是票 27 缺陷 B：设置页的并发数从没进过安装器。
                service.MaxParallel = settings.Current.DownloadThreads;
                settings.Changed += (_, updated) => service.MaxParallel = updated.DownloadThreads;
            }

            return service;
        });
        services.AddSingleton<IInstallService>(sp => sp.GetRequiredService<InstallService>());

        // 组件检查器只依赖路径与日志：不联网、不写盘，UI 打开面板即可调用。
        services.TryAddSingleton<IComponentInspector, ComponentInspector>();
        return services;
    }
}
