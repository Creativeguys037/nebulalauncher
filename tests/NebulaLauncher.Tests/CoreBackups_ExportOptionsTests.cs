using System.IO.Compression;
using System.Text.Json;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 导出选项补的三类文件：<c>options.txt</c> / <c>defaultconfigs/</c> / <c>servers.dat</c>。
///
/// 这三样以前一律不导出：用户勾了「包含配置」也拿不到游戏设置与新建世界默认配置，
/// 导入方的整合包在每个新世界上都回到默认值。
/// </summary>
public class CoreBackups_ExportOptionsTests
{
    [Fact]
    public async Task 默认导出包含游戏设置与新建世界默认配置且不含服务器列表()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var instance = CreateInstance(paths, temp.Path);

        var result = await new ModpackExporter(paths).ExportAsync(
            instance,
            Path.Combine(temp.Path, "out.mrpack"),
            new ModpackExportOptions { MinecraftVersion = "1.20.1", PreferModrinthHashes = false });

        Assert.True(result.Success, result.Error);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("overrides/options.txt", entries);
        Assert.Contains("overrides/defaultconfigs/example.json", entries);
        Assert.DoesNotContain("overrides/servers.dat", entries);
        Assert.Contains("overrides/config/example.toml", entries);
    }

    [Fact]
    public async Task 勾选服务器列表后servers_dat进入覆盖层()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var instance = CreateInstance(paths, temp.Path);

        var result = await new ModpackExporter(paths).ExportAsync(
            instance,
            Path.Combine(temp.Path, "out.mrpack"),
            new ModpackExportOptions
            {
                MinecraftVersion = "1.20.1",
                PreferModrinthHashes = false,
                IncludeServerList = true,
            });

        Assert.True(result.Success, result.Error);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        Assert.Contains(archive.Entries, entry => entry.FullName == "overrides/servers.dat");
    }

    [Fact]
    public async Task 关掉游戏设置后options_txt不进入覆盖层()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var instance = CreateInstance(paths, temp.Path);

        var result = await new ModpackExporter(paths).ExportAsync(
            instance,
            Path.Combine(temp.Path, "out.mrpack"),
            new ModpackExportOptions
            {
                MinecraftVersion = "1.20.1",
                PreferModrinthHashes = false,
                IncludeOptions = false,
                IncludeDefaultConfigs = false,
            });

        Assert.True(result.Success, result.Error);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.DoesNotContain("overrides/options.txt", entries);
        Assert.DoesNotContain("overrides/defaultconfigs/example.json", entries);
        Assert.Contains("overrides/config/example.toml", entries);
    }

    private static string CreateInstance(FakeLauncherPaths paths, string root)
    {
        var instance = Path.Combine(root, "instance");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(instance, "config"));
        Directory.CreateDirectory(Path.Combine(instance, "defaultconfigs"));

        File.WriteAllText(Path.Combine(instance, "options.txt"), "lang:zh_cn\nfov:0.5\n");
        File.WriteAllText(Path.Combine(instance, "servers.dat"), "fake-nbt");
        File.WriteAllText(Path.Combine(instance, "config", "example.toml"), "enabled = true");
        File.WriteAllText(Path.Combine(instance, "defaultconfigs", "example.json"), "{}");

        var instanceInfo = new InstanceInfo
        {
            Id = "demo",
            Name = "演示实例",
            VersionName = "1.20.1",
            Loader = LoaderKind.Vanilla,
        };

        File.WriteAllText(
            Path.Combine(instance, "instance.json"),
            JsonSerializer.Serialize(instanceInfo));

        var versionDirectory = Path.Combine(paths.VersionsDirectory, "1.20.1");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.20.1.json"),
            """{ "id": "1.20.1" }""");

        return instance;
    }
}
