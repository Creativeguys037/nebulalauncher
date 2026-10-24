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
        return services;
    }
}
