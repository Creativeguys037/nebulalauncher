using System.Text.Json;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

public sealed class CoreBackups_AutomaticTests
{
    [Fact]
    public async Task Retention_PreservesManualLegacyAndOtherInstanceWorlds()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var firstWorld = CreateWorld(paths.RootDirectory, "first", "World");
        var secondWorld = CreateWorld(paths.RootDirectory, "second", "World");
        var oldest = await service.CreateAutomaticAsync(firstWorld);
        var newest = await service.CreateAutomaticAsync(firstWorld + Path.DirectorySeparatorChar);
        var otherInstance = await service.CreateAutomaticAsync(secondWorld);
        var manual = await service.CreateAsync(firstWorld, "退出后自动备份");
        var legacy = await service.CreateAsync(firstWorld);
        await File.WriteAllTextAsync(legacy.FilePath + ".meta.json", JsonSerializer.Serialize(new
        {
            legacy.Id, legacy.FilePath, legacy.WorldName, legacy.CreatedAt, Note = "退出后自动备份",
        }));

        var deleted = await service.PruneAsync(1);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldest.FilePath));
        Assert.True(File.Exists(newest.FilePath));
        Assert.True(File.Exists(otherInstance.FilePath));
        Assert.True(File.Exists(manual.FilePath));
        Assert.True(File.Exists(legacy.FilePath));
        var entries = await service.ListBackupsAsync();
        Assert.Equal(4, entries.Count);
        Assert.Equal(Path.GetFullPath(firstWorld), entries.Single(entry => entry.Id == newest.Id).SourceWorldDirectory);
        Assert.True(entries.Single(entry => entry.Id == newest.Id).IsAutomatic);
        Assert.False(entries.Single(entry => entry.Id == legacy.Id).IsAutomatic);
    }

    [Fact]
    public async Task Cancellation_DoesNotPublishPartialArchive()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var world = CreateWorld(paths.RootDirectory, "first", "World");
        await File.WriteAllBytesAsync(Path.Combine(world, "region.mca"), new byte[200000]);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateAsync(world, progress: progress, cancellationToken: cancellation.Token));

        Assert.Empty(await service.ListBackupsAsync());
        Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory));
    }

    [Fact]
    public async Task UnreadableWorld_DoesNotPublishIncompleteBackup()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var world = CreateWorld(paths.RootDirectory, "first", "World");
        using var locked = new FileStream(Path.Combine(world, "level.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => service.CreateAutomaticAsync(world));

        Assert.Empty(await service.ListBackupsAsync());
        Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory));
    }

    [Fact]
    public async Task ExitBackup_UsesResolvedInstanceAndHonorsDisabledSetting()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var firstWorld = CreateWorld(paths.RootDirectory, "first", "World");
        var secondWorld = CreateWorld(paths.RootDirectory, "second", "World");
        var settings = new AppSettings { AutoBackupWorldsOnExit = false, WorldBackupRetention = 1 };
        var log = new CoreAssetsLog();

        await AutomaticWorldBackup.RunAsync(service, settings, Path.Combine(paths.RootDirectory, "first"), false, log);
        Assert.Empty(await service.ListBackupsAsync());
        settings.AutoBackupWorldsOnExit = true;
        await AutomaticWorldBackup.RunAsync(service, settings, Path.Combine(paths.RootDirectory, "first"), false, log);
        await AutomaticWorldBackup.RunAsync(service, settings, Path.Combine(paths.RootDirectory, "second"), true, log);

        var entries = await service.ListBackupsAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.True(entry.IsAutomatic));
        Assert.Contains(entries, entry => entry.SourceWorldDirectory == firstWorld && entry.Note == "退出后自动备份");
        Assert.Contains(entries, entry => entry.SourceWorldDirectory == secondWorld && entry.Note == "崩溃退出后自动备份");
    }

    [Fact]
    public async Task ExitBackup_ContinuesAfterOneWorldFails()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);
        var brokenWorld = CreateWorld(paths.RootDirectory, "first", "Broken");
        var goodWorld = CreateWorld(paths.RootDirectory, "first", "Good");
        var settings = new AppSettings { AutoBackupWorldsOnExit = true, WorldBackupRetention = 1 };
        var log = new CoreAssetsLog();
        using var locked = new FileStream(Path.Combine(brokenWorld, "level.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await AutomaticWorldBackup.RunAsync(service, settings, Path.Combine(paths.RootDirectory, "first"), false, log);

        Assert.Equal(goodWorld, Assert.Single(await service.ListBackupsAsync()).SourceWorldDirectory);
        Assert.Contains(log.Warnings, message => message.Contains("自动备份失败", StringComparison.Ordinal));
    }

    private static string CreateWorld(string root, string instance, string name)
    {
        var directory = Path.Combine(root, instance, "saves", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "level.dat"), "test world");
        return directory;
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
