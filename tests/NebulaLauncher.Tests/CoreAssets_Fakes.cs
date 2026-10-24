using System.Collections.Concurrent;
using NebulaLauncher.Core.Abstractions;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "测试类型/方法与测试文件统一用 CoreAssets_ 前缀标明归属，方法名用「场景_断言」形式表达意图")]

namespace NebulaLauncher.Tests;

/// <summary>测试用 IAppLog：只记录，不落盘。</summary>
internal sealed class CoreAssetsLog : IAppLog
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public string LogFilePath { get; } = Path.Combine(Path.GetTempPath(), "nebula-tests.log");

    public void Debug(string message) => Messages.Enqueue($"DEBUG {message}");

    public void Info(string message) => Messages.Enqueue($"INFO {message}");

    public void Warn(string message, Exception? ex = null) => Messages.Enqueue($"WARN {message} {ex?.Message}");

    public void Error(string message, Exception? ex = null) => Messages.Enqueue($"ERROR {message} {ex?.Message}");

    public IEnumerable<string> Warnings => Messages.Where(m => m.StartsWith("WARN", StringComparison.Ordinal));
}

/// <summary>
/// 测试用 ILauncherPaths：所有路径落在临时目录下，实例释放时整体删除。
/// 沙箱环境里 .NET 子进程可能不允许在 %TEMP% 下建目录，因此带多级回落。
/// </summary>
internal sealed class CoreAssetsPaths : ILauncherPaths, IDisposable
{
    public CoreAssetsPaths()
    {
        RootDirectory = CreateRootDirectory();
        MinecraftDirectory = Path.Combine(RootDirectory, "game");
        EnsureCreated();
    }

    private static string CreateRootDirectory()
    {
        var name = "nebula-tests-" + Guid.NewGuid().ToString("N")[..10];
        foreach (var baseDirectory in new[]
                 {
                     Path.GetTempPath(),
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                 })
        {
            try
            {
                var candidate = Path.Combine(baseDirectory, name);
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception)
            {
                // 当前候选目录不可写，换下一个。
            }
        }

        throw new InvalidOperationException("无法创建测试临时目录");
    }

    public string RootDirectory { get; }

    public string MinecraftDirectory { get; private set; }

    public string VersionsDirectory => Path.Combine(MinecraftDirectory, "versions");

    public string LibrariesDirectory => Path.Combine(MinecraftDirectory, "libraries");

    public string AssetsDirectory => Path.Combine(MinecraftDirectory, "assets");

    public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string CacheDirectory => Path.Combine(RootDirectory, "cache");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string AccountsFile => Path.Combine(RootDirectory, "accounts.json");

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string ServersFile => Path.Combine(RootDirectory, "servers.json");

    public void SetMinecraftDirectory(string path)
    {
        MinecraftDirectory = path;
        EnsureCreated();
    }

    public void EnsureCreated()
    {
        foreach (var directory in new[]
                 {
                     MinecraftDirectory, VersionsDirectory, LibrariesDirectory, AssetsDirectory,
                     InstancesDirectory, LogsDirectory, CacheDirectory, BackupsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试收尾失败不影响结论。
        }
    }
}
