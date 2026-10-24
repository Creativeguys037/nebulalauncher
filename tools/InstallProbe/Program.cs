using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Logging;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Paths;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.InstallProbe;

/// <summary>
/// 版本安装的命令行探针。用于复现「安装失败」并拿到真实异常，
/// 因为 UI 只显示了 <c>ex.Message</c>，堆栈与内部阶段信息会丢失。
///
/// 用法：
///   dotnet run --project tools/InstallProbe -- &lt;数据目录&gt; &lt;MC版本&gt; &lt;加载器&gt; [加载器版本] [official|bmclapi]
///
/// 例：
///   dotnet run --project tools/InstallProbe -- D:\agent_workspace\.probe\probe-data 1.12.2 Forge
///   dotnet run --project tools/InstallProbe -- D:\agent_workspace\.probe\probe-data 1.20.1 Fabric 0.16.9
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : @"D:\agent_workspace\.probe\probe-data";
        var mcVersion = args.Length > 1 ? args[1] : "1.12.2";
        var loader = args.Length > 2 && Enum.TryParse<LoaderKind>(args[2], ignoreCase: true, out var lk)
            ? lk
            : LoaderKind.Forge;
        var loaderVersion = args.Length > 3 && !string.Equals(args[3], "-", StringComparison.Ordinal) ? args[3] : null;
        var source = args.Length > 4 && Enum.TryParse<DownloadSource>(args[4], ignoreCase: true, out var ds)
            ? ds
            : DownloadSource.BmclApi;

        var mcDir = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(mcDir);

        Console.WriteLine($"数据目录 : {root}");
        Console.WriteLine($"游戏目录 : {mcDir}");
        Console.WriteLine($"目标     : MC {mcVersion} / {loader} {(loaderVersion ?? "(最新)")} / {source}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddSingleton<ILauncherPaths>(_ => new LauncherPaths(root, mcDir));
        services.AddCoreLogging();
        services.AddCorePlatforms();
        services.AddCoreDownloads();
        services.AddCoreJava();
        services.AddCoreInstalling();

        using var provider = services.BuildServiceProvider();

        var java = provider.GetRequiredService<IJavaService>();
        var install = provider.GetRequiredService<IInstallService>();
        install.Source = source;

        Console.WriteLine("===== Java =====");
        try
        {
            var detected = await java.DetectAsync(forceRefresh: true);
            foreach (var j in detected)
            {
                Console.WriteLine($"  {j.MajorVersion,3} | {j.Vendor,-12} | 64bit={j.Is64Bit,-5} | {j.Path}");
            }

            var requirement = java.GetRequirement(mcVersion);
            Console.WriteLine($"  需求: {(requirement is null ? "未知（不做限制）" : $"Java {requirement.RequiredMajor} — {requirement.Reason}")}");

            var resolved = await java.ResolveForVersionAsync(mcVersion);
            Console.WriteLine($"  选用: {resolved?.Path ?? "NULL —— 没有满足要求的 Java，加载器安装必然失败"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Java 探测异常: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("===== 安装 =====");

        var lastReport = DateTime.UtcNow;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds < 120 && p.Ratio < 1)
            {
                return;
            }

            lastReport = now;
            Console.WriteLine($"  [{p.CompletedFiles,4}/{p.TotalFiles,-4}] {p.Ratio * 100,5:0.0}%  {p.CurrentFile}");
        });

        try
        {
            await install.InstallAsync(
                new InstallRequest
                {
                    MinecraftVersion = mcVersion,
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                    Source = source,
                },
                progress);

            Console.WriteLine("结果: 成功");
            Console.WriteLine($"版本目录: {Path.Combine(mcDir, "versions")}");
            foreach (var dir in Directory.GetDirectories(Path.Combine(mcDir, "versions")))
            {
                Console.WriteLine($"  - {Path.GetFileName(dir)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"结果: 失败");
            Console.WriteLine($"类型: {ex.GetType().FullName}");
            Console.WriteLine($"消息: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.WriteLine($"内层: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                if (ex.InnerException.InnerException is not null)
                {
                    Console.WriteLine($"更深: {ex.InnerException.InnerException.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("堆栈:");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
