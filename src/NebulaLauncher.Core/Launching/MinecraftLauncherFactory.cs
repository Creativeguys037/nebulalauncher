using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 构造 CmlLib <see cref="MinecraftLauncher"/> 的唯一入口。
///
/// 为什么必须只有一份：安装页（<c>InstallService</c>）与启动前的「补齐文件」
/// （<see cref="GameLauncher.LaunchAsync"/>）都会调 CmlLib 的安装器。两处各写一遍参数，
/// 就会出现「安装页走代理 / 镜像与下载队列，启动补全却走默认 HttpClient」——
/// 用户在设置里选了代理，安装成功，启动时那 788 MB 又直连超时。
///
/// 三个固定设定：
/// <list type="number">
/// <item>注入 <see cref="NebulaGameInstaller"/>：库自带的 ParallelGameInstaller 按 CPU 核数并发，
///   在 1.12.2 这类 1500+ 文件的版本上会因重复条目同时写同一目标文件而撞锁。</item>
/// <item>摘掉 <c>JavaFileExtractor</c>：CmlLib 会顺带下载 Mojang 的 Java 运行时，
///   而镜像对 <c>/v1/products/java-runtime/...</c> 返回 403/404，安装会中途失败。
///   启动器的 Java 由 <c>IJavaService</c> 负责。</item>
/// <item>下载用的 <see cref="HttpClient"/> 由调用方给：生产环境传
///   <see cref="DownloadHttpClientFactory.Client"/>（带镜像重写与换源），单测不传则退回库默认。</item>
/// </list>
/// </summary>
internal static class MinecraftLauncherFactory
{
    /// <summary>
    /// 建一个可安装、可启动的 CmlLib 启动器。
    /// </summary>
    /// <param name="path">CmlLib 的目录结构（库 / 版本 / 资源 / 实例）。</param>
    /// <param name="client">
    /// 下载客户端。null 表示调用方没有统一下载链（单测、探针宿主），
    /// 此时保留库默认的 <see cref="HttpClient"/> 与安装器，行为与改造前一致。
    /// </param>
    /// <param name="scope">下载队列的逐文件状态回传口；null 表示不回传（启动补全不需要任务面板）。</param>
    /// <param name="maxParallel">单版本安装的并发上限。</param>
    public static MinecraftLauncher Create(
        MinecraftPath path,
        HttpClient? client = null,
        IDownloadTaskScope? scope = null,
        int maxParallel = 8)
    {
        ArgumentNullException.ThrowIfNull(path);

        MinecraftLauncherParameters parameters;

        if (client is null)
        {
            parameters = MinecraftLauncherParameters.CreateDefault(path);
        }
        else
        {
            parameters = MinecraftLauncherParameters.CreateDefault(path, client);
            parameters.GameInstaller = new NebulaGameInstaller(client, maxParallel: maxParallel, maxRetries: 3, scope: scope);
        }

        RemoveJavaRuntimeExtractors(parameters);

        return new MinecraftLauncher(parameters);
    }

    /// <summary>
    /// 摘掉库自带的 Java 运行时提取器。抽成方法是为了让「为什么摘」只写一遍，
    /// 且将来 CmlLib 换提取器类型时只有一个修改点。
    /// </summary>
    private static void RemoveJavaRuntimeExtractors(MinecraftLauncherParameters parameters)
    {
        var extractors = parameters.FileExtractors;
        if (extractors is null)
        {
            return;
        }

        foreach (var extractor in extractors.OfType<JavaFileExtractor>().ToList())
        {
            extractors.Remove(extractor);
        }
    }
}
