using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Downloads;

/// <summary>下载基础设施的 DI 注册入口。</summary>
public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddCoreDownloads(this IServiceCollection services)
    {
        // 会话级下载源状态机：既是「当前该用哪个源」的提供者，也是下载结果的回灌口。
        // 单例：降级状态必须跨页面、跨批次保持；进程退出即丢弃（不落盘）。
        services.AddSingleton(sp =>
        {
            var settings = sp.GetService<ISettingsService>();
            var governor = new DownloadSourceGovernor();

            if (settings is not null)
            {
                governor.SetStrategy(settings.Current.DownloadSource);
                settings.Changed += (_, updated) => governor.SetStrategy(updated.DownloadSource);
            }

            return governor;
        });
        services.AddSingleton<IDownloadSourceProvider>(sp => sp.GetRequiredService<DownloadSourceGovernor>());
        services.AddSingleton<IDownloadSourceTelemetry>(sp => sp.GetRequiredService<DownloadSourceGovernor>());

        // HttpClient 由状态机驱动：MirrorRewritingHandler 每次请求都重新问当前首选源。
        services.AddSingleton(sp => new DownloadHttpClientFactory(
            sp.GetRequiredService<IDownloadSourceProvider>(),
            sp.GetService<IDownloadSourceTelemetry>()));

        // 路径与日志都是可选依赖：tools/InstallProbe 这类宿主不注册 ILauncherPaths，
        // 复用服务退化成「永不命中」，下载行为与之前一致。
        services.AddSingleton(sp => new FileReuseService(
            sp.GetService<ILauncherPaths>(),
            sp.GetService<IAppLog>()));

        // 用工厂注册而不是裸类型注册：限速值要从设置注入，而设置服务在
        // tools/InstallProbe 这类宿主里并不存在，必须按「可选依赖」处理。
        services.AddSingleton(sp =>
        {
            var reuse = sp.GetRequiredService<FileReuseService>();
            var queue = new DownloadQueue(
                sp.GetRequiredService<DownloadHttpClientFactory>(),
                sp.GetService<IAppLog>(),
                reuse: reuse);

            var settings = sp.GetService<ISettingsService>();
            if (settings is not null)
            {
                // DownloadQueue 按「批级快照」读取限速值，所以这里把变更同步到属性上，
                // 下一次 DownloadAsync 即可生效，不必重建队列。
                // 并发数漏接是票 27 缺陷 B：设置页那根滑块看着能拖，实际从没进过下载队列。
                queue.MaxConcurrency = settings.Current.DownloadThreads;
                queue.SpeedLimitKb = settings.Current.DownloadSpeedLimitKb;
                settings.Changed += (_, updated) =>
                {
                    queue.MaxConcurrency = updated.DownloadThreads;
                    queue.SpeedLimitKb = updated.DownloadSpeedLimitKb;
                    // 设置变更包括切换 .minecraft 目录：旧索引里的路径可能已经不在新根下。
                    reuse.InvalidateCache();
                };
            }

            return queue;
        });

        return services;
    }
}
