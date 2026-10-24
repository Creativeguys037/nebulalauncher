using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Installing;

/// <summary>版本安装的 DI 注册入口。</summary>
public static class InstallServiceCollectionExtensions
{
    public static IServiceCollection AddCoreInstalling(this IServiceCollection services)
    {
        services.AddSingleton<IInstallService, InstallService>();

        // 组件检查器只依赖路径与日志：不联网、不写盘，UI 打开面板即可调用。
        services.TryAddSingleton<IComponentInspector, ComponentInspector>();
        return services;
    }
}
