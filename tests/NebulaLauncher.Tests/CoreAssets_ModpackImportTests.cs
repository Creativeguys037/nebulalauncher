using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 整合包导入后写出的 <c>instance.json</c> 必须是「完整实例」：
/// 缺加载器与版本号会让导入的实例在模组页里退化成一堆原版条目，
/// 缺 managedPack 则后续无法回答「这个实例是从哪个整合包来的」。
/// </summary>
public sealed class CoreAssets_ModpackImportTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Import_WritesLoaderVersionAndManagedPack()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new ModpackService(paths, new DownloadQueue(new HttpClient(new NoOpHandler())), new CoreAssetsLog());

        var archive = Path.Combine(temp.Path, "pack.mrpack");
        WriteModpack(archive, """
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.0.0",
          "name": "测试整合包",
          "files": [],
          "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.0" }
        }
        """);

        var directory = await service.ImportAsync(archive, "导入实例");

        var json = await File.ReadAllTextAsync(Path.Combine(directory, "instance.json"));
        var instance = JsonSerializer.Deserialize<InstanceInfo>(json, WebJson);

        Assert.NotNull(instance);
        Assert.Equal("导入实例", instance!.Name);
        Assert.Equal(LoaderKind.Fabric, instance.Loader);
        Assert.Equal("1.20.1", instance.MinecraftVersion);
        Assert.True(instance.IsolationEnabled);

        Assert.NotNull(instance.ManagedPack);
        Assert.Equal("modrinth", instance.ManagedPack!.Type);
        Assert.Equal("测试整合包", instance.ManagedPack.Name);
        Assert.Equal("1.0.0", instance.ManagedPack.VersionId);
        Assert.Equal(Path.GetFullPath(archive), instance.ManagedPack.SourceArchive);
    }

    [Fact]
    public async Task Import_PlainZipRecordsZipType()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new ModpackService(paths, new DownloadQueue(new HttpClient(new NoOpHandler())), new CoreAssetsLog());

        var archive = Path.Combine(temp.Path, "plain.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("普通压缩包");
        }

        var directory = await service.ImportAsync(archive, "普通包");
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "instance.json"));
        var instance = JsonSerializer.Deserialize<InstanceInfo>(json, WebJson);

        Assert.NotNull(instance?.ManagedPack);
        Assert.Equal("zip", instance!.ManagedPack!.Type);
    }

    [Fact]
    public async Task Import_ModrinthCopiesClientOverridesAndSkipsUnsupportedClientFiles()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new NoOpHandler();
        var service = new ModpackService(paths, new DownloadQueue(new HttpClient(handler)), new CoreAssetsLog());

        var archive = Path.Combine(temp.Path, "client-overrides.mrpack");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var index = zip.CreateEntry("modrinth.index.json");
            await using (var stream = index.Open())
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false))
            {
                await writer.WriteAsync("""
                {
                  "formatVersion": 1,
                  "game": "minecraft",
                  "name": "客户端覆盖层",
                  "dependencies": { "minecraft": "1.20.1" },
                  "files": [
                    {
                      "path": "mods/server-only.jar",
                      "downloads": ["https://example.invalid/server-only.jar"],
                      "env": { "client": "unsupported", "server": "required" }
                    }
                  ]
                }
                """);
            }

            var clientOverride = zip.CreateEntry("client-overrides/config/options.txt");
            await using (var overrideWriter = new StreamWriter(clientOverride.Open(), new UTF8Encoding(false)))
            {
                await overrideWriter.WriteAsync("lang:zh_cn");
            }

            var commonOverride = zip.CreateEntry("overrides/config/options.txt");
            await using var commonWriter = new StreamWriter(commonOverride.Open(), new UTF8Encoding(false));
            await commonWriter.WriteAsync("lang:en_us");
        }

        var directory = await service.ImportAsync(archive, "客户端覆盖层");

        Assert.Equal("lang:zh_cn", await File.ReadAllTextAsync(Path.Combine(directory, "config", "options.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "mods", "server-only.jar")));
        Assert.Equal(0, handler.RequestCount);
    }

    private static void WriteModpack(string path, string indexJson)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var index = zip.CreateEntry("modrinth.index.json");
        using (var writer = new StreamWriter(index.Open(), new UTF8Encoding(false)))
        {
            writer.Write(indexJson);
        }

        var overrideFile = zip.CreateEntry("overrides/mods/readme.txt");
        using var overrideWriter = new StreamWriter(overrideFile.Open(), new UTF8Encoding(false));
        overrideWriter.Write("override");
    }

    /// <summary>导入流程不该发任何请求（files 为空；即便有，也走不到网络）。</summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
