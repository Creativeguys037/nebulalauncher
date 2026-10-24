using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Backups;

/// <summary>备份与整合包的 DI 注册入口。</summary>
public static class BackupsServiceCollectionExtensions
{
    public static IServiceCollection AddCoreBackups(this IServiceCollection services)
    {
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IModpackService, ModpackService>();
        return services;
    }
}
