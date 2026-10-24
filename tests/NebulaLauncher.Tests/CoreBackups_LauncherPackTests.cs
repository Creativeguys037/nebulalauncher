using System.IO.Compression;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 带启动器的分发包（外层含 <c>modpack.zip</c> / <c>modpack.mrpack</c>）必须在导入入口被识别并明确拒绝。
/// 以前它落到 <c>PlainZip</c>：外层被原样解进实例根，用户拿到的是一堆启动器 exe 与配置。
/// </summary>
public sealed class CoreBackups_LauncherPackTests
{
    [Theory]
    [InlineData("modpack.zip")]
    [InlineData("modpack.mrpack")]
    public async Task OuterPackWithNestedModpack_IsDetectedAndRejected(string nestedName)
    {
        using var paths = new CoreAssetsPaths();
        var archive = CreateArchive(paths, nestedName, atRoot: true);
        var service = CreateService(paths);

        var preview = await service.InspectAsync(archive);
        Assert.Equal(ModpackFormat.LauncherPack, preview.Format);

        var result = await service.ImportWithResultAsync(archive, "测试实例");

        Assert.False(result.Success);
        Assert.Contains("带启动器的整合包", result.Error);
        Assert.Equal(ModpackFormat.LauncherPack, result.Format);
        Assert.False(Directory.Exists(Path.Combine(paths.InstancesDirectory, "测试实例")));
    }

    [Fact]
    public async Task NestedModpackInsideSingleTopFolder_IsAlsoDetected()
    {
        using var paths = new CoreAssetsPaths();
        var archive = CreateArchive(paths, "modpack.zip", atRoot: false);
        var service = CreateService(paths);

        var preview = await service.InspectAsync(archive);

        Assert.Equal(ModpackFormat.LauncherPack, preview.Format);
    }

    [Fact]
    public async Task PlainZipWithoutNestedModpack_StaysPlainZip()
    {
        using var paths = new CoreAssetsPaths();
        var archive = Path.Combine(paths.RootDirectory, "plain.zip");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            WriteText(zip, "mods/example.jar", "not a real jar");
            WriteText(zip, "options.txt", "lang:zh_cn");
        }

        var service = CreateService(paths);
        var preview = await service.InspectAsync(archive);

        Assert.Equal(ModpackFormat.PlainZip, preview.Format);
    }

    private static ModpackService CreateService(CoreAssetsPaths paths) =>
        new(paths, new DownloadQueue(new HttpClient()), new CoreAssetsLog());

    private static string CreateArchive(CoreAssetsPaths paths, string nestedName, bool atRoot)
    {
        var archive = Path.Combine(paths.RootDirectory, "launcher-pack.zip");
        var prefix = atRoot ? string.Empty : "LauncherPack/";

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            WriteText(zip, prefix + "PCL.exe", "fake launcher");
            WriteText(zip, prefix + nestedName, "fake nested modpack");
            WriteText(zip, prefix + ".minecraft/options.txt", "lang:zh_cn");
        }

        return archive;
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
