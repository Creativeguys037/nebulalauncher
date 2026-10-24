using NebulaLauncher.Core.Logging;

namespace NebulaLauncher.Tests;

/// <summary>日志必须落盘，且自身出错时绝不冒泡。</summary>
public class CoreLaunch_FileLoggerTests
{
    [Fact]
    public async Task WritesAllLevelsToDatedFile()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var logger = new FileLogger(paths);

        logger.Debug("调试信息");
        logger.Info("普通信息");
        logger.Warn("告警信息", new InvalidOperationException("样本异常"));
        logger.Error("错误信息", new InvalidOperationException("错误异常"));

        Assert.True(File.Exists(logger.LogFilePath));
        Assert.StartsWith(paths.LogsDirectory, logger.LogFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("launcher-", Path.GetFileName(logger.LogFilePath), StringComparison.Ordinal);

        var content = await File.ReadAllTextAsync(logger.LogFilePath);

        Assert.Contains("调试信息", content, StringComparison.Ordinal);
        Assert.Contains("[INFO ]", content, StringComparison.Ordinal);
        Assert.Contains("[WARN ]", content, StringComparison.Ordinal);
        Assert.Contains("[ERROR]", content, StringComparison.Ordinal);
        Assert.Contains("样本异常", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotThrowAndKeepEveryLine()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var logger = new FileLogger(paths);

        const int writers = 8;
        const int perWriter = 40;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                logger.Info($"w{writer}-{i}");
            }
        })));

        var content = await File.ReadAllTextAsync(logger.LogFilePath);

        for (var writer = 0; writer < writers; writer++)
        {
            for (var i = 0; i < perWriter; i++)
            {
                Assert.Contains($"w{writer}-{i}", content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Write_WhenDirectoryIsInvalid_DoesNotThrow()
    {
        // 目录里带 NUL 字符，任何写盘操作都会失败；日志实现必须吞掉。
        var logger = new FileLogger(new UnwritableLogPaths());

        var exception = Record.Exception(() =>
        {
            logger.Info("仍然不能抛");
            logger.Error("仍然不能抛", new InvalidOperationException());
        });

        Assert.Null(exception);
    }

    /// <summary>只有日志目录不可写，其余路径随便给个合法值。</summary>
    private sealed class UnwritableLogPaths : NebulaLauncher.Core.Abstractions.ILauncherPaths
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "nebula-" + Guid.NewGuid().ToString("N"));

        public string RootDirectory => _root;

        public string MinecraftDirectory => _root;

        public string VersionsDirectory => _root;

        public string LibrariesDirectory => _root;

        public string AssetsDirectory => _root;

        public string InstancesDirectory => _root;

        public string LogsDirectory => _root + "\0invalid";

        public string CacheDirectory => _root;

        public string BackupsDirectory => _root;

        public string AccountsFile => Path.Combine(_root, "accounts.json");

        public string SettingsFile => Path.Combine(_root, "settings.json");

        public string ServersFile => Path.Combine(_root, "servers.json");

        public void SetMinecraftDirectory(string path)
        {
        }

        public void EnsureCreated()
        {
        }
    }
}
