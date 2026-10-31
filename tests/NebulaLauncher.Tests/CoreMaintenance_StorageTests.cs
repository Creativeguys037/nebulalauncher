using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Maintenance;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 存储统计与清理的规则集。
///
/// 这组用例锁的是「删错东西」这一类不可逆后果，而不是数字好不好看：
/// 越界路径必须被拒、数据根里的用户数据必须还在、最近 7 天的日志必须留下、单个坏目录不能让整体抛异常。
/// 统计口径只锁两条容易写错的：cache 不能把 loader-install 重复算一遍、trash 的两个子根要合并。
/// </summary>
public sealed class CoreMaintenance_StorageTests
{
    private const long Kilobyte = 1024;

    [Fact]
    public async Task 统计四个类别且缓存不含安装器临时目录()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        WriteFile(paths.CacheDirectory, "asset.bin", 3 * Kilobyte);
        WriteFile(Path.Combine(paths.CacheDirectory, "icons"), "icon.png", Kilobyte);
        WriteFile(Path.Combine(paths.CacheDirectory, "loader-install"), "installer.jar", 5 * Kilobyte);
        WriteFile(paths.LogsDirectory, "launcher-20260101.log", 2 * Kilobyte);
        WriteFile(Path.Combine(paths.RootDirectory, "trash", "instances", "abc"), "instance.json", Kilobyte);
        WriteFile(Path.Combine(paths.RootDirectory, "trash", "contents", "def"), "mod.jar", 4 * Kilobyte);

        var report = await service.ScanAsync();

        Assert.Equal(StorageCategoryKeys.All.Count, report.Categories.Count);

        var cache = Category(report, StorageCategoryKeys.Cache);
        // 4 KB = asset.bin + icon.png；loader-install 的 5 KB 属于 installer 类别，不能重复计入。
        Assert.Equal(4 * Kilobyte, cache.TotalBytes);
        Assert.Equal(2, cache.FileCount);
        Assert.NotNull(cache.OldestWriteTime);
        Assert.True(cache.CanClean);
        Assert.Equal(paths.CacheDirectory, cache.Path);

        var installer = Category(report, StorageCategoryKeys.Installer);
        Assert.Equal(5 * Kilobyte, installer.TotalBytes);
        Assert.Equal(1, installer.FileCount);

        var logs = Category(report, StorageCategoryKeys.Logs);
        Assert.Equal(2 * Kilobyte, logs.TotalBytes);
        Assert.Equal(1, logs.FileCount);

        var trash = Category(report, StorageCategoryKeys.Trash);
        // 实例回收站与内容回收站同根，合并统计。
        Assert.Equal(5 * Kilobyte, trash.TotalBytes);
        Assert.Equal(2, trash.FileCount);

        Assert.Equal(16 * Kilobyte, report.TotalBytes);
        Assert.Equal(6, report.TotalFiles);
    }

    [Fact]
    public async Task 清理后各类别归零且用户数据不变()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        WriteFile(paths.CacheDirectory, "asset.bin", 2 * Kilobyte);
        WriteFile(Path.Combine(paths.CacheDirectory, "loader-install"), "installer.jar", 3 * Kilobyte);
        WriteFile(paths.LogsDirectory, "launcher-today.log", Kilobyte);
        WriteFile(Path.Combine(paths.RootDirectory, "trash", "contents", "id1"), "mod.jar", 4 * Kilobyte);
        WriteFile(paths.InstancesDirectory, "instance.json", Kilobyte);
        WriteFile(Path.Combine(paths.InstancesDirectory, "inst"), "instance.json", Kilobyte);
        File.WriteAllText(paths.SettingsFile, "{}");
        File.WriteAllText(paths.AccountsFile, "{}");

        var result = await service.CleanAllAsync();

        Assert.Null(result.Rejected);
        Assert.Equal(0, result.FailedItems);

        // 缓存 / 安装器临时文件 / 回收站全清；日志只删 7 天以外的，刚写的这份按策略留下。
        Assert.Equal(3, result.DeletedFiles);
        Assert.Equal(9 * Kilobyte, result.DeletedBytes);

        var report = await service.ScanAsync();
        Assert.Equal(Kilobyte, report.TotalBytes);
        Assert.Equal(1, report.TotalFiles);
        Assert.Equal(0, Category(report, StorageCategoryKeys.Cache).TotalBytes);
        Assert.Equal(0, Category(report, StorageCategoryKeys.Installer).TotalBytes);
        Assert.Equal(0, Category(report, StorageCategoryKeys.Trash).TotalBytes);
        Assert.Equal(1, Category(report, StorageCategoryKeys.Logs).FileCount);

        // 类别根目录本身必须保留：删掉之后下一次写缓存 / 写日志会因为目录缺失失败。
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));

        // 数据根下的用户数据一个都不能少。
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(paths.AccountsFile));
        Assert.True(File.Exists(Path.Combine(paths.InstancesDirectory, "instance.json")));
        Assert.True(File.Exists(Path.Combine(paths.InstancesDirectory, "inst", "instance.json")));
    }

    [Fact]
    public async Task 日志按最后写入时间保留最近七天()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        var recent = Path.Combine(paths.LogsDirectory, "recent.log");
        WriteFile(paths.LogsDirectory, "recent.log", Kilobyte);
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-2));

        var old = Path.Combine(paths.LogsDirectory, "old.log");
        WriteFile(paths.LogsDirectory, "old.log", 3 * Kilobyte);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        var result = await service.CleanAsync([StorageCategoryKeys.Logs]);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(3 * Kilobyte, result.DeletedBytes);
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(old));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public async Task 数据根之外的路径被拒绝()
    {
        using var temp = new TempDirectory();
        var dataRoot = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(dataRoot);

        var outside = Path.Combine(temp.Path, "outside-cache");
        Directory.CreateDirectory(outside);
        var keep = Path.Combine(outside, "keep.bin");
        File.WriteAllBytes(keep, new byte[256]);

        // 类别路径写成 <data>/../outside-cache：字符串上看不出越界，只有 Path.GetFullPath 之后才暴露。
        var paths = new StubPaths(dataRoot, Path.Combine(dataRoot, "..", "outside-cache"));
        var service = new StorageService(paths, NullAppLog.Instance);

        var result = await service.CleanAsync([StorageCategoryKeys.Cache]);

        Assert.True(result.IsRejected);
        Assert.Contains("数据目录之外", result.Rejected!, StringComparison.Ordinal);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.DeletedBytes);
        Assert.True(result.FailedItems > 0);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task 文件被占用时只记失败不抛异常()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        var locked = Path.Combine(paths.CacheDirectory, "locked.bin");
        WriteFile(paths.CacheDirectory, "locked.bin", 2 * Kilobyte);
        WriteFile(paths.CacheDirectory, "free.bin", Kilobyte);

        using var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        // 不该抛：清理是「能删多少删多少」，单个文件被占用只影响它自己。
        var result = await service.CleanAsync([StorageCategoryKeys.Cache]);

        Assert.NotNull(result);
        Assert.False(File.Exists(Path.Combine(paths.CacheDirectory, "free.bin")));

        // Windows 上占用中的文件删不掉 → 计入失败项且文件还在；其它平台删除会成功，不作失败断言。
        if (result.FailedItems > 0)
        {
            Assert.True(File.Exists(locked));
        }
    }

    [Fact]
    public async Task 坏目录不抛异常且统计为零()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 把 logs 路径换成一个**同名文件**：目录串位是真实会发生的（用户清理脚本、同步工具）。
        Directory.Delete(paths.LogsDirectory);
        File.WriteAllText(paths.LogsDirectory, "not a directory");

        var service = new StorageService(paths, NullAppLog.Instance);

        var report = await service.ScanAsync();
        Assert.Equal(0, Category(report, StorageCategoryKeys.Logs).TotalBytes);

        var result = await service.CleanAsync([StorageCategoryKeys.Logs]);
        Assert.Null(result.Rejected);
        Assert.Equal(0, result.DeletedFiles);

        // 同名文件不是日志类别的内容，清理不得把它当残留删掉。
        Assert.True(File.Exists(paths.LogsDirectory));
    }

    [Fact]
    public async Task 空数据目录全为零()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        var report = await service.ScanAsync();

        Assert.Equal(4, report.Categories.Count);
        foreach (var category in report.Categories)
        {
            Assert.Equal(0, category.TotalBytes);
            Assert.Equal(0, category.FileCount);
            Assert.Null(category.OldestWriteTime);
            Assert.True(category.CanClean);
            Assert.False(string.IsNullOrWhiteSpace(category.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(category.CleanHint));
            Assert.False(string.IsNullOrWhiteSpace(category.Path));
        }

        Assert.Equal(0, report.TotalBytes);
        Assert.Equal(0, report.TotalFiles);
    }

    [Fact]
    public async Task 未知类别键被忽略()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);
        WriteFile(paths.CacheDirectory, "asset.bin", Kilobyte);

        var result = await service.CleanAsync(["not-a-category"]);

        Assert.Empty(result.CategoryKeys);
        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(Path.Combine(paths.CacheDirectory, "asset.bin")));
    }

    [Fact]
    public async Task 清理缓存时不连带清安装器临时目录()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        WriteFile(paths.CacheDirectory, "asset.bin", Kilobyte);
        var installerJar = Path.Combine(paths.CacheDirectory, "loader-install", "installer.jar");
        WriteFile(Path.Combine(paths.CacheDirectory, "loader-install"), "installer.jar", 2 * Kilobyte);

        var cacheResult = await service.CleanAsync([StorageCategoryKeys.Cache]);

        // cache 的清理不能越过自己的排除边界：loader-install 属于 installer 类别。
        Assert.Equal(1, cacheResult.DeletedFiles);
        Assert.True(File.Exists(installerJar));

        var installerResult = await service.CleanAsync([StorageCategoryKeys.Installer]);

        Assert.Equal(1, installerResult.DeletedFiles);
        Assert.False(File.Exists(installerJar));
    }

    [Fact]
    public async Task 清理下载缓存时皮肤纹理缓存被清且用户皮肤库完好()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new StorageService(paths, NullAppLog.Instance);

        // 票 54：纹理缓存落在 cache/skins/（uuid 分区 + 站点分区 + 根上的默认皮肤），
        // 用户导入的本地皮肤库在 <data>/skins/。两者只差一层目录，正是最容易删错的一对。
        WriteFile(Path.Combine(paths.CacheDirectory, "skins", UuidScope), "texture.png", Kilobyte);
        WriteFile(Path.Combine(paths.CacheDirectory, "skins", "littleskin.cn"), "917742.png", Kilobyte);
        WriteFile(Path.Combine(paths.CacheDirectory, "skins"), "default-steve.png", Kilobyte);
        WriteFile(Path.Combine(paths.RootDirectory, "skins"), "my-skin.png", 2 * Kilobyte);

        var result = await service.CleanAsync([StorageCategoryKeys.Cache]);

        Assert.Null(result.Rejected);
        Assert.Equal(3, result.DeletedFiles);
        Assert.Equal(3 * Kilobyte, result.DeletedBytes);

        // cache/skins/ 被清空：目录连同内容一起删掉或只剩空目录都算过，下次落盘会自动重建。
        var skinsCache = Path.Combine(paths.CacheDirectory, "skins");
        if (Directory.Exists(skinsCache))
        {
            Assert.Empty(Directory.GetFiles(skinsCache, "*", SearchOption.AllDirectories));
        }

        // 类别根目录本身必须保留。
        Assert.True(Directory.Exists(paths.CacheDirectory));

        // <data>/skins/ 一个字节都不能少。
        Assert.True(File.Exists(Path.Combine(paths.RootDirectory, "skins", "my-skin.png")));
        Assert.Equal(2 * Kilobyte, new FileInfo(Path.Combine(paths.RootDirectory, "skins", "my-skin.png")).Length);
    }

    private const string UuidScope = "493e32e14248466d97fd154e215f2201";

    private static void WriteFile(string directory, string name, long bytes)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), new byte[bytes]);
    }

    private static StorageCategoryUsage Category(StorageReport report, string key) =>
        report.Categories.Single(category => string.Equals(category.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// 只实现存储服务用到的路径成员。<paramref name="cacheDirectory"/> 允许传入带 <c>..</c> 的越界路径，
    /// 用来验证「清理前的规范化 + 根内校验」这道闸真的拦得住。
    /// </summary>
    private sealed class StubPaths : ILauncherPaths
    {
        public StubPaths(string root, string cacheDirectory)
        {
            RootDirectory = root;
            CacheDirectory = cacheDirectory;
        }

        public string RootDirectory { get; }

        public string CacheDirectory { get; }

        public string MinecraftDirectory => Path.Combine(RootDirectory, "minecraft");

        public string VersionsDirectory => Path.Combine(MinecraftDirectory, "versions");

        public string LibrariesDirectory => Path.Combine(MinecraftDirectory, "libraries");

        public string AssetsDirectory => Path.Combine(MinecraftDirectory, "assets");

        public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

        public string LogsDirectory => Path.Combine(RootDirectory, "logs");

        public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

        public string AccountsFile => Path.Combine(RootDirectory, "accounts.json");

        public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

        public string ServersFile => Path.Combine(RootDirectory, "servers.json");

        public void SetMinecraftDirectory(string path)
        {
        }

        public void EnsureCreated()
        {
        }
    }
}