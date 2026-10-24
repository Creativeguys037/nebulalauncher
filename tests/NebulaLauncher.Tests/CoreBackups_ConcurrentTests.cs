using NebulaLauncher.Core.Backups;

namespace NebulaLauncher.Tests;

/// <summary>
/// 备份的并发安全与「归档 + 元信息」提交语义（AUDIT-2026-10-23 第四节两条 P2）。
/// </summary>
public sealed class CoreBackups_ConcurrentTests
{
    [Fact]
    public async Task ConcurrentBackupsOfSameWorld_AllSucceedWithDistinctPaths()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var world = Path.Combine(paths.RootDirectory, "saves", "World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "test world");

        // 同一秒内 8 个并发请求：时间戳相同，最终名只能靠 Move 的原子占名区分。
        var entries = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => service.CreateAsync(world)));

        Assert.Equal(8, entries.Select(entry => entry.FilePath).Distinct().Count());
        Assert.All(entries, entry => Assert.True(File.Exists(entry.FilePath)));
        Assert.All(entries, entry => Assert.True(File.Exists(entry.FilePath + ".meta.json")));

        // 临时文件不能留在备份目录里。
        Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "*.partial"));
        Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "*.partial*"));
    }

    [Fact]
    public async Task ConcurrentAutomaticBackups_AllCarryMetaAndSurviveListing()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var world = Path.Combine(paths.RootDirectory, "saves", "World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "test world");

        var created = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => service.CreateAutomaticAsync(world, "退出后自动备份")));

        var listed = await service.ListBackupsAsync();

        Assert.Equal(created.Length, listed.Count);
        Assert.All(listed, entry => Assert.True(entry.IsAutomatic));
        Assert.All(listed, entry => Assert.Equal("退出后自动备份", entry.Note));
    }
}
