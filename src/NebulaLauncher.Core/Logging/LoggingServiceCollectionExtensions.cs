using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Core.Logging;

/// <summary>日志模块的 DI 注册。</summary>
public static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IAppLog"/> 的文件实现（进程内单例）、日志中心用的只读读取器，
    /// 以及复用它读启动器日志的崩溃导出服务。
    ///
    /// 读取器与写入器分开注册：写日志是每条都跑的热路径，读日志只在用户点开日志页 / 导出崩溃报告时跑，
    /// 两者的异常语义相反（写必须吞异常，读必须把失败原因交给界面）。
    /// </summary>
    public static IServiceCollection AddCoreLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAppLog, FileLogger>();
        services.TryAddSingleton<ILauncherLogReader, LauncherLogReader>();

        // 崩溃导出依赖上面两个 + ILauncherPaths；一并在这里注册，避免让 AddGameLaunching 记住这条依赖。
        services.TryAddSingleton<ICrashExportService, CrashExportService>();
        return services;
    }
}
