using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>测试用的临时目录，Dispose 时递归删除。</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(GetRoot(), "nebula-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>
    /// 优先用系统临时目录；某些受限环境（只允许写工作区）会拒绝 %TEMP% 上的创建，
    /// 此时退回到测试程序集目录下，避免测试因为环境策略而整体失败。
    /// </summary>
    private static string GetRoot()
    {
        var temp = System.IO.Path.GetTempPath();
        var probe = System.IO.Path.Combine(temp, "nebula-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return temp;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var fallback = System.IO.Path.Combine(AppContext.BaseDirectory, "__test-temp");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 测试清理失败不该影响断言结果。
        }
    }
}

/// <summary>内存里的 <see cref="ILauncherPaths"/>，所有目录落在临时根目录下。</summary>
public sealed class FakeLauncherPaths : ILauncherPaths
{
    public FakeLauncherPaths(string root)
    {
        RootDirectory = root;
        MinecraftDirectory = Path.Combine(root, ".minecraft");
        EnsureCreated();
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

    public void SetMinecraftDirectory(string path) => MinecraftDirectory = path;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(MinecraftDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(LibrariesDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}

/// <summary>不做任何事的日志，用于隔离 IO。</summary>
public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    public string LogFilePath => string.Empty;

    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
    }
}

/// <summary>只实现账户与设置模块需要的平台能力。</summary>
public sealed class FakePlatformService : IPlatformService
{
    public string PlatformName => "Windows";

    public bool SupportsWindowMaterial => false;

    public bool SupportsSecureTokenStorage { get; set; }

    public string DefaultMinecraftDirectory => Path.GetTempPath();

    public IReadOnlyList<string> JavaSearchRoots => Array.Empty<string>();

    public string JavaExecutableName => "java.exe";

    public Task OpenFolderAsync(string path) => Task.CompletedTask;

    public Task OpenUrlAsync(string url) => Task.CompletedTask;

    public Task RevealInFileManagerAsync(string path) => Task.CompletedTask;

    public Task CopyTextAsync(string text) => Task.CompletedTask;

    public void ApplyWindowMaterial(IntPtr windowHandle, string material)
    {
    }

    public void SetTaskbarProgress(IntPtr windowHandle, double progress)
    {
    }

    public IDisposable? AcquireSingleInstanceLock(string name) => null;
}

/// <summary>写一个最小的版本描述文件。</summary>
internal static class FakeVersionDirectory
{
    public static void Write(string versionsRoot, string name, string? inheritsFrom, string type = "release")
    {
        var directory = Path.Combine(versionsRoot, name);
        Directory.CreateDirectory(directory);

        var inherits = inheritsFrom is null ? string.Empty : $",\"inheritsFrom\":\"{inheritsFrom}\"";

        File.WriteAllText(
            Path.Combine(directory, name + ".json"),
            $$"""
              {
                "id": "{{name}}",
                "type": "{{type}}",
                "mainClass": "net.minecraft.client.main.Main"{{inherits}}
              }
              """);
    }
}
