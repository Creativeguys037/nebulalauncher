using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Mods;

/// <summary>内容管理的 DI 注册入口。</summary>
public static class ContentServiceCollectionExtensions
{
    public static IServiceCollection AddCoreContent(this IServiceCollection services)
    {
        services.AddSingleton<IContentService, ContentService>();
        services.AddSingleton<IModrinthService, ModrinthService>();
        services.AddSingleton<ICurseForgeService, CurseForgeService>();

        // 整合包在线目录：只有「版本页的整合包视图」与「内容页的整合包分区」用得到。
        // 注册成单例是为了让它持有的 HttpClient 跟进程同寿——按页面重建会反复开连接，
        // 而 Modrinth 对短时间大量新建连接是限流的。
        services.AddSingleton<ModpackCatalogClient>();

        return services;
    }
}
