using System.Text.Json;
using System.Text.Json.Serialization;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例的编辑类操作：克隆、重命名、图标、目录清理、回收站。
///
/// 这些动作都直接动磁盘，失败方式很具体（漏字段、漏排除、按错误的时间基准过滤、路径穿越），
/// 所以断言的是「磁盘上真实发生了什么」，而不是服务返回了什么值。
/// </summary>
public class CoreLaunch_InstanceAdminTests
{
    /// <summary>与 <c>InstanceService</c> 落盘时用的选项一致，否则枚举字段读不回来。</summary>
    private static readonly JsonSerializerOptions InstanceJsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Clone_CopiesContentAndExcludesLogs()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var source = await service.CreateAsync("原版生存", "1.20.1");
        var sourceDirectory = service.GetGameDirectory(source.Id);

        WriteFile(sourceDirectory, "mods/fabric-api.jar", "mod");
        WriteFile(sourceDirectory, "config/sodium.toml", "config");
        WriteFile(sourceDirectory, "resourcepacks/VanillaPlus.zip", "pack");
        WriteFile(sourceDirectory, "shaderpacks/BSL.zip", "shader");
        WriteFile(sourceDirectory, "saves/远古遗迹/level.dat", "world");
        WriteFile(sourceDirectory, "logs/latest.log", "log");
        WriteFile(sourceDirectory, "crash-reports/crash-1.txt", "crash");
        WriteFile(sourceDirectory, "options.txt", "lang:zh_cn");

        var clone = await service.CloneAsync(source.Id, new InstanceCloneOptions());

        Assert.NotNull(clone);
        var cloneDirectory = service.GetGameDirectory(clone!.Id);

        Assert.True(File.Exists(Path.Combine(cloneDirectory, "mods", "fabric-api.jar")));
        Assert.True(File.Exists(Path.Combine(cloneDirectory, "config", "sodium.toml")));
        Assert.True(File.Exists(Path.Combine(cloneDirectory, "resourcepacks", "VanillaPlus.zip")));
        Assert.True(File.Exists(Path.Combine(cloneDirectory, "shaderpacks", "BSL.zip")));
        Assert.True(File.Exists(Path.Combine(cloneDirectory, "saves", "远古遗迹", "level.dat")));

        // 根目录散文件是「副本进游戏后还是原设置」的前提。
        Assert.True(File.Exists(Path.Combine(cloneDirectory, "options.txt")));

        // logs / crash-reports 默认不复制：它们是上一份实例的故障现场。
        Assert.False(Directory.Exists(Path.Combine(cloneDirectory, "logs")));
        Assert.False(Directory.Exists(Path.Combine(cloneDirectory, "crash-reports")));
    }

    [Fact]
    public async Task Clone_CopiesLogsWhenRequested()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var source = await service.CreateAsync("调试实例", "1.20.1");
        var sourceDirectory = service.GetGameDirectory(source.Id);
        WriteFile(sourceDirectory, "logs/latest.log", "log");
        WriteFile(sourceDirectory, "crash-reports/crash-1.txt", "crash");

        var clone = await service.CloneAsync(source.Id, new InstanceCloneOptions { IncludeLogs = true });

        Assert.NotNull(clone);
        var cloneDirectory = service.GetGameDirectory(clone!.Id);

        Assert.True(File.Exists(Path.Combine(cloneDirectory, "logs", "latest.log")));
        Assert.True(File.Exists(Path.Combine(cloneDirectory, "crash-reports", "crash-1.txt")));
    }

    [Fact]
    public async Task Clone_UsesNewIdAndResetsRuntimeStats()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var source = await service.CreateAsync("1.21.4 Fabric", "1.21.4");
        source.Loader = LoaderKind.Fabric;
        source.MinecraftVersion = "1.21.4";
        source.ServerAddress = "play.example.com";
        source.LastPlayedAt = DateTimeOffset.UtcNow.AddHours(-2);
        source.TotalPlayTimeSeconds = 7200;
        source.IconKind = InstanceIconKind.Fabric;
        await service.SaveAsync(source);

        var clone = await service.CloneAsync(source.Id, new InstanceCloneOptions());

        Assert.NotNull(clone);
        Assert.NotEqual(source.Id, clone!.Id);
        Assert.False(string.IsNullOrWhiteSpace(clone.Id));

        var sourceDirectory = service.GetGameDirectory(source.Id);
        var cloneDirectory = service.GetGameDirectory(clone.Id);
        Assert.NotEqual(sourceDirectory, cloneDirectory);
        Assert.True(Directory.Exists(cloneDirectory));

        // 默认名 = 原名 + 后缀。
        Assert.Equal("1.21.4 Fabric-副本", clone.Name);

        // 游玩统计属于「这一次运行」，副本从零开始；其余实例级配置必须跟着走（HANDOVER 5.1 的坑）。
        Assert.Null(clone.LastPlayedAt);
        Assert.Equal(0L, clone.TotalPlayTimeSeconds);
        Assert.Equal(LoaderKind.Fabric, clone.Loader);
        Assert.Equal("1.21.4", clone.MinecraftVersion);
        Assert.Equal("play.example.com", clone.ServerAddress);
        Assert.Equal(InstanceIconKind.Fabric, clone.IconKind);

        // 同实例再克隆一次自动加序号，不能撞名。
        var second = await service.CloneAsync(source.Id, new InstanceCloneOptions());
        Assert.NotNull(second);
        Assert.Equal("1.21.4 Fabric-副本 (2)", second!.Name);
        Assert.NotEqual(clone.Id, second.Id);

        // 两个副本都能从磁盘重新读出来，说明 instance.json 的 Id 与目录名一致。
        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();
        Assert.Equal(3, reloaded.Instances.Count);
    }

    [Fact]
    public async Task Clone_WithExplicitName_TrimsAndUsesIt()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var source = await service.CreateAsync("生存", "1.20.1");

        var clone = await service.CloneAsync(source.Id, new InstanceCloneOptions { NewName = "  红石实验  " });

        Assert.NotNull(clone);
        Assert.Equal("红石实验", clone!.Name);
    }

    [Fact]
    public async Task Clone_UnknownInstance_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();

        Assert.Null(await service.CloneAsync("does-not-exist", new InstanceCloneOptions()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_RejectsBlankName(string name)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("原名", "1.20.1");

        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync(instance.Id, name));

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        Assert.Equal("原名", Assert.Single(reloaded.Instances).Name);
    }

    [Fact]
    public async Task Rename_OnlyChangesDisplayName()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("旧名", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);

        await service.RenameAsync(instance.Id, "  新名  ");

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        var loaded = Assert.Single(reloaded.Instances);
        Assert.Equal("新名", loaded.Name);

        // 目录名就是 Id，重命名不能动路径，否则所有记录 Id 的地方都会指向空目录。
        Assert.Equal(instance.Id, loaded.Id);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task SetIcon_CustomRequiresExistingPngOrJpg()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("图标测试", "1.20.1");

        var png = Path.Combine(temp.Path, "icon.png");
        await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47]);

        var textFile = Path.Combine(temp.Path, "icon.txt");
        await File.WriteAllTextAsync(textFile, "not an image");

        // 1) 不存在的文件：拒绝，且不能落盘。
        await service.SetIconAsync(instance.Id, InstanceIconKind.Custom, Path.Combine(temp.Path, "missing.png"));
        Assert.Equal(InstanceIconKind.Default, ReadInstance(paths, instance.VersionName).IconKind);

        // 2) 扩展名不对：拒绝。
        await service.SetIconAsync(instance.Id, InstanceIconKind.Custom, textFile);
        Assert.Equal(InstanceIconKind.Default, ReadInstance(paths, instance.VersionName).IconKind);
        Assert.Null(ReadInstance(paths, instance.VersionName).IconPath);

        // 3) 合法 png：落盘 IconKind 与绝对路径。
        await service.SetIconAsync(instance.Id, InstanceIconKind.Custom, png);
        var custom = ReadInstance(paths, instance.VersionName);
        Assert.Equal(InstanceIconKind.Custom, custom.IconKind);
        Assert.Equal(Path.GetFullPath(png), custom.IconPath);

        // 4) 换回内置图标：IconKind 跟着变。
        await service.SetIconAsync(instance.Id, InstanceIconKind.Chest, null);
        Assert.Equal(InstanceIconKind.Chest, ReadInstance(paths, instance.VersionName).IconKind);
    }

    [Fact]
    public async Task Cleanup_FiltersByAgeAndKeepsRecentFiles()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("清理测试", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);

        var oldLog = WriteFile(directory, "logs/old.log", "old");
        var newLog = WriteFile(directory, "logs/new.log", "new");
        var oldCrash = WriteFile(directory, "crash-reports/crash-old.txt", "crash");
        var screenshot = WriteFile(directory, "screenshots/2026-01-01.png", "shot");

        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(oldCrash, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(screenshot, DateTime.UtcNow.AddDays(-30));

        var report = await service.CleanupAsync(
            instance.Id,
            new InstanceCleanupOptions { IncludeLogs = true, IncludeCrashReports = true, OlderThanDays = 7 });

        Assert.Equal(2, report.FilesDeleted);
        Assert.Empty(report.Failed);

        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldCrash));

        // 最近还在写的日志不能清：它是正在排查的那一份。
        Assert.True(File.Exists(newLog));

        // 截图是用户资产，默认不动，即使它已经很旧。
        Assert.True(File.Exists(screenshot));
    }

    [Fact]
    public async Task Cleanup_WithoutDayLimit_RemovesEverythingSelected()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("全量清理", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);

        var log = WriteFile(directory, "logs/latest.log", "0123456789");
        WriteFile(directory, "debug/profile.txt", "debug");

        var report = await service.CleanupAsync(instance.Id, new InstanceCleanupOptions());

        Assert.Equal(2, report.FilesDeleted);
        Assert.Equal(15L, report.BytesFreed);
        Assert.False(File.Exists(log));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "logs")));
    }

    [Fact]
    public async Task MoveToTrash_Lists_Restores_RoundTrip()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("要删的实例", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);
        WriteFile(directory, "saves/世界/level.dat", "world");

        var entry = await service.MoveToTrashAsync(instance.Id);

        Assert.NotNull(entry);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(service.Instances);

        // 回收站条目必须自带 instance.json 与 meta.json，否则恢复时拿不回原 Id 与显示名。
        Assert.True(File.Exists(Path.Combine(entry!.TrashPath, "instance.json")));
        Assert.True(File.Exists(Path.Combine(entry.TrashPath, "meta.json")));
        Assert.True(File.Exists(Path.Combine(entry.TrashPath, "saves", "世界", "level.dat")));
        Assert.Equal(instance.Id, entry.OriginalInstanceId);
        Assert.Equal("要删的实例", entry.DisplayName);

        var listed = await service.ListTrashAsync();
        var only = Assert.Single(listed);
        Assert.Equal(entry.Id, only.Id);
        Assert.Equal(instance.Id, only.OriginalInstanceId);

        Assert.True(await service.RestoreFromTrashAsync(entry.Id));

        Assert.True(Directory.Exists(directory));
        Assert.False(Directory.Exists(entry.TrashPath));
        Assert.False(File.Exists(Path.Combine(directory, "meta.json")));

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        var restored = Assert.Single(reloaded.Instances);
        Assert.Equal(instance.Id, restored.Id);
        Assert.Equal("要删的实例", restored.Name);
        Assert.Empty(await reloaded.ListTrashAsync());
    }

    [Fact]
    public async Task Restore_WhenTargetDirectoryExists_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("冲突实例", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);

        var entry = await service.MoveToTrashAsync(instance.Id);
        Assert.NotNull(entry);

        // 原 Id 被重新占用：覆盖会毁掉现有目录，只能拒绝恢复。
        Directory.CreateDirectory(directory);

        Assert.False(await service.RestoreFromTrashAsync(entry!.Id));
        Assert.True(Directory.Exists(entry.TrashPath));
    }

    [Fact]
    public async Task Purge_DeletesTrashEntryRecursively()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("待彻底删除", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);
        WriteFile(directory, "mods/a.jar", "mod");

        var entry = await service.MoveToTrashAsync(instance.Id);
        Assert.NotNull(entry);

        await service.PurgeTrashAsync(entry!.Id);

        Assert.False(Directory.Exists(entry.TrashPath));
        Assert.Empty(await service.ListTrashAsync());
    }

    [Fact]
    public async Task Trash_RejectsPathTraversalIds()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();

        Assert.False(await service.RestoreFromTrashAsync("../instances"));
        Assert.False(await service.RestoreFromTrashAsync("a/b"));

        // 彻底删除同样不能接受穿越 Id，否则会删到启动器目录外面去。
        await service.PurgeTrashAsync("../instances");
        Assert.True(Directory.Exists(paths.InstancesDirectory));
    }

    [Fact]
    public async Task Clone_Cancelled_ThrowsAndCleansUp()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("取消测试", "1.20.1");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CloneAsync(instance.Id, new InstanceCloneOptions(), cancellation.Token));

        // 取消后不能留下半成品目录，否则下次 LoadAsync 会看到没有 instance.json 的垃圾目录。
        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();
        Assert.Single(reloaded.Instances);
    }

    [Fact]
    public async Task Clone_WithoutMods_OnlyCopiesVersionPayload()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var source = await service.CreateAsync("源实例", "1.20.1");
        var sourceDirectory = service.GetGameDirectory(source.Id);
        WriteFile(sourceDirectory, "mods/a.jar", "mod");
        WriteFile(sourceDirectory, "saves/世界/level.dat", "world");

        var clone = await service.CloneAsync(source.Id, new InstanceCloneOptions
        {
            IncludeMods = false,
            IncludeSaves = false,
        });

        Assert.NotNull(clone);
        var cloneDirectory = service.GetGameDirectory(clone!.Id);

        // 版本页「同版本再建一个干净实例」就靠这两个 false：版本本体过去，内容从零开始。
        Assert.False(Directory.Exists(Path.Combine(cloneDirectory, "mods")));
        Assert.False(Directory.Exists(Path.Combine(cloneDirectory, "saves")));
        Assert.NotEqual(sourceDirectory, cloneDirectory);
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyInstanceIntoVersionDirectory()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var legacyDirectory = Path.Combine(paths.InstancesDirectory, "legacy1");
        WriteFile(legacyDirectory, "mods/a.jar", "mod");
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "instance.json"),
            """
            {
              "Id": "legacy1",
              "Name": "老实例",
              "VersionName": "1.20.1",
              "Loader": "Fabric",
              "MinecraftVersion": "1.20.1"
            }
            """);

        var service = new InstanceService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        var instance = Assert.Single(service.Instances);
        Assert.Equal("1.20.1", instance.VersionName);

        var expected = Path.Combine(paths.VersionsDirectory, "1.20.1");
        Assert.Equal(expected, service.GetGameDirectory(instance.Id), ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(expected, "mods", "a.jar")));

        // 迁移完成后旧目录必须消失，否则下次启动会再复制一遍。
        Assert.False(Directory.Exists(legacyDirectory));
    }

    [Fact]
    public async Task LoadAsync_MergesLegacyInstanceIntoExistingVersionDirectory()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 目标版本已装好（有版本 JSON），只是没有实例。
        var versionDirectory = Path.Combine(paths.VersionsDirectory, "1.20.1");
        WriteFile(versionDirectory, "1.20.1.json", """{"id":"1.20.1"}""");
        WriteFile(versionDirectory, "mods/existing.jar", "existing");

        var legacyDirectory = Path.Combine(paths.InstancesDirectory, "legacy2");
        WriteFile(legacyDirectory, "mods/legacy.jar", "legacy");
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "instance.json"),
            """
            {
              "Id": "legacy2",
              "Name": "老实例",
              "VersionName": "1.20.1"
            }
            """);

        var service = new InstanceService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        var instance = Assert.Single(service.Instances);
        Assert.Equal("1.20.1", instance.VersionName);

        // 合并不能覆盖目标已有文件；旧目录的独有文件要带过来。
        Assert.True(File.Exists(Path.Combine(versionDirectory, "mods", "existing.jar")));
        Assert.True(File.Exists(Path.Combine(versionDirectory, "mods", "legacy.jar")));
        Assert.False(Directory.Exists(legacyDirectory));
    }

    private static InstanceInfo ReadInstance(FakeLauncherPaths paths, string versionName)
    {
        var json = File.ReadAllText(Path.Combine(paths.VersionsDirectory, versionName, "instance.json"));

        return JsonSerializer.Deserialize<InstanceInfo>(json, InstanceJsonOptions)!;
    }

    private static string WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
