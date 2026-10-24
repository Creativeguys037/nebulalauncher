using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Servers;

/// <summary>服务器列表的 DI 注册入口。</summary>
public static class ServerServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServers(this IServiceCollection services)
    {
        services.AddSingleton<IServerService, ServerService>();

        // GameSpy4 Query 是 SLP 之外的另一条通道（UDP 同端口），彼此独立：
        // 不合并进 ServerService，避免把「TCP 探测」与「UDP 全量查询」绑在同一个生命周期上。
        services.AddSingleton<IServerQueryService, ServerQueryService>();
        return services;
    }
}
