using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Logging;

/// <summary>日志跟随的 DI 注册。与 <see cref="LoggingServiceCollectionExtensions.AddCoreLogging"/> 分开一份，
/// 因为跟随服务只被日志页用到，而 <c>AddCoreLogging</c> 的写入器是全局地基。</summary>
public static class LogFollowServiceCollectionExtensions
{
    /// <summary>注册日志跟随读取器（进程内单例，无状态、无定时器）。</summary>
    public static IServiceCollection AddCoreLogFollow(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ILogFollowService, LogFollower>();
        return services;
    }
}