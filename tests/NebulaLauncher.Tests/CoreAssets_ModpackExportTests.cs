using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

// Modrinth 的 files[].hashes.sha1 就是 SHA1，测试必须按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// 整合包导出的往返测试：导出产物必须能被仓库现有的 <see cref="ModpackService.InspectAsync"/> 识别。
///
/// 这里的「网络」全部由假 <see cref="HttpMessageHandler"/> 承担，测试不联网；
/// 每个用例使用独立的 <see cref="CoreAssetsPaths"/>，互不污染 mods / saves。
/// </summary>
public class CoreAssets_ModpackExportTests
{
    [Fact]
    public async Task 导出_可被导入侧识别且区分在线条目与overrides()
    {
        using var environment = new ExportEnvironment();
        await environment.WriteInstanceAsync();

        var result = await environment.ExportAsync(includeSaves: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Files);
        Assert.Equal(1, result.UnresolvedMods);
        Assert.True(result.Bytes > 0);
        Assert.True(File.Exists(result.ArchivePath));

        var service = environment.CreateModpackService();
        var preview = await service.InspectAsync(result.ArchivePath!);
        Assert.Equal(ModpackFormat.ModrinthModpack, preview.Format);
        Assert.Equal("测试整合包", preview.Name);
        Assert.Equal("2.3.4", preview.Version);
        Assert.Equal("1.20.1", preview.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, preview.Loader);
        Assert.Equal("0.16.9", preview.LoaderVersion);
        Assert.Contains("mods/sodium.jar", preview.Files);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();

        Assert.Contains("modrinth.index.json", entries);
        Assert.Contains("overrides/config/example.toml", entries);

        // 在线命中的 jar 只出现在 files[]，不进 overrides。
        Assert.DoesNotContain("overrides/mods/sodium.jar", entries);

        // 未命中的禁用态 jar 按原名写回 overrides/mods/<原名>.jar。
        Assert.Contains("overrides/mods/local-mod.jar", entries);
        Assert.DoesNotContain("overrides/mods/local-mod.jar.disabled", entries);

        Assert.DoesNotContain(entries, entry => entry.StartsWith("logs/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("overrides/saves/", StringComparison.OrdinalIgnoreCase));

        using var indexStream = archive.GetEntry("modrinth.index.json")!.Open();
        using var document = JsonDocument.Parse(indexStream);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("minecraft", root.GetProperty("game").GetString());
        Assert.Equal("2.3.4", root.GetProperty("versionId").GetString());
        Assert.Equal("测试整合包", root.GetProperty("name").GetString());

        var dependencies = root.GetProperty("dependencies");
        Assert.Equal("1.20.1", dependencies.GetProperty("minecraft").GetString());
        Assert.Equal("0.16.9", dependencies.GetProperty("fabric-loader").GetString());

        var files = root.GetProperty("files").EnumerateArray().ToArray();
        var sodium = Assert.Single(files);
        Assert.Equal("mods/sodium.jar", sodium.GetProperty("path").GetString());
        Assert.Equal(environment.HitSha1, sodium.GetProperty("hashes").GetProperty("sha1").GetString());
        Assert.Equal(
            "https://cdn.modrinth.com/data/sodium/versions/ver-1/sodium.jar",
            sodium.GetProperty("downloads")[0].GetString());
        Assert.Equal("required", sodium.GetProperty("env").GetProperty("client").GetString());
        Assert.Equal("required", sodium.GetProperty("env").GetProperty("server").GetString());

        using var overridesStream = archive.GetEntry("overrides/mods/local-mod.jar")!.Open();
        using var overridesBuffer = new MemoryStream();
        await overridesStream.CopyToAsync(overridesBuffer);
        Assert.Equal(environment.MissBytes, overridesBuffer.ToArray());
    }

    [Fact]
    public async Task 导出_包含存档时写入overrides_saves()
    {
        using var environment = new ExportEnvironment();
        await environment.WriteInstanceAsync();

        var result = await environment.ExportAsync(includeSaves: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, result.Files);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();

        Assert.Contains("overrides/saves/world-1/level.dat", entries);
    }

    [Fact]
    public async Task 导出_网络失败时降级为overrides且不中断()
    {
        using var environment = new ExportEnvironment();
        await environment.WriteInstanceAsync();

        var result = await environment.ExportAsync(includeSaves: false, handler: new FailingHandler());

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.UnresolvedMods);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();

        Assert.Contains("overrides/mods/sodium.jar", entries);
        Assert.Contains("overrides/mods/local-mod.jar", entries);

        using var indexStream = archive.GetEntry("modrinth.index.json")!.Open();
        using var document = JsonDocument.Parse(indexStream);
        Assert.Empty(document.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task 导出_关闭在线哈希时不发起请求且全部走overrides()
    {
        using var environment = new ExportEnvironment();
        await environment.WriteInstanceAsync();

        var handler = new LookupHandler(environment.HitSha1);
        var result = await environment.ExportAsync(includeSaves: false, preferHashes: false, handler: handler);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(2, result.UnresolvedMods);

        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName == "overrides/mods/sodium.jar");
    }

    [Fact]
    public async Task 导出_目标已存在时覆盖为有效压缩包()
    {
        using var environment = new ExportEnvironment();
        await environment.WriteInstanceAsync();

        Directory.CreateDirectory(Path.GetDirectoryName(environment.TargetPath)!);
        await File.WriteAllTextAsync(environment.TargetPath, "旧文件");

        var result = await environment.ExportAsync(includeSaves: false);

        Assert.True(result.Success, result.Error);

        using var archive = ZipFile.OpenRead(environment.TargetPath);
        Assert.Contains("modrinth.index.json", archive.Entries.Select(entry => entry.FullName));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(environment.TargetPath)!, "*.tmp"));
    }

    [Fact]
    public async Task 导出_实例目录不存在时返回中文失败()
    {
        using var paths = new CoreAssetsPaths();
        var exporter = new ModpackExporter();
        var target = Path.Combine(paths.RootDirectory, "out", "missing.mrpack");

        var result = await exporter.ExportAsync(
            Path.Combine(paths.RootDirectory, "missing-instance"),
            target,
            new ModpackExportOptions());

        Assert.False(result.Success);
        Assert.Contains("不存在", result.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task 导出_读不到Minecraft版本时返回中文失败()
    {
        using var paths = new CoreAssetsPaths();
        var instanceDirectory = Path.Combine(paths.InstancesDirectory, "no-version");
        Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods"));
        await File.WriteAllTextAsync(Path.Combine(instanceDirectory, "mods", "a.jar"), "x");

        var exporter = new ModpackExporter(paths);
        var result = await exporter.ExportAsync(
            instanceDirectory,
            Path.Combine(paths.RootDirectory, "out", "no-version.mrpack"),
            new ModpackExportOptions { PreferModrinthHashes = false });

        Assert.False(result.Success);
        Assert.Contains("Minecraft 版本", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 导出_进度比例单调递增并以1结束()
    {
        using var environment = new ExportEnvironment();
        await environment.WriteInstanceAsync();

        var progress = new RecordingProgress();
        var result = await environment.ExportAsync(includeSaves: false, progress: progress);

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1, progress.Reports[^1].Ratio);
        Assert.True(progress.Reports[^1].CompletedFiles >= progress.Reports[^1].TotalFiles);

        for (var index = 1; index < progress.Reports.Count; index++)
        {
            Assert.True(
                progress.Reports[index].Ratio >= progress.Reports[index - 1].Ratio,
                $"进度在第 {index} 次上报时回退：{progress.Reports[index - 1].Ratio} -> {progress.Reports[index].Ratio}");
        }
    }

    [Fact]
    public async Task 导出_libraries缺失时按版本id回退加载器版本()
    {
        using var paths = new CoreAssetsPaths();
        var instanceDirectory = Path.Combine(paths.InstancesDirectory, "forge-instance");
        Directory.CreateDirectory(instanceDirectory);
        await WriteInstanceJsonAsync(instanceDirectory, "forge-instance", "1.20.1-forge-47.2.0", LoaderKind.Forge);

        // 版本 JSON 没有 libraries，只能从 id 的 "-forge-" 段取加载器版本。
        var versionDirectory = Path.Combine(paths.VersionsDirectory, "1.20.1-forge-47.2.0");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, "1.20.1-forge-47.2.0.json"),
            """{ "id": "1.20.1-forge-47.2.0", "inheritsFrom": "1.20.1", "libraries": [] }""");

        var target = Path.Combine(paths.RootDirectory, "out", "forge.mrpack");
        var exporter = new ModpackExporter(paths);
        var result = await exporter.ExportAsync(
            instanceDirectory,
            target,
            new ModpackExportOptions { PreferModrinthHashes = false });

        Assert.True(result.Success, result.Error);

        var dependencies = ReadDependencies(target);
        Assert.Equal("1.20.1", dependencies.GetProperty("minecraft").GetString());
        Assert.Equal("47.2.0", dependencies.GetProperty("forge").GetString());
    }

    [Fact]
    public async Task 导出_读不到加载器版本时省略依赖键()
    {
        using var paths = new CoreAssetsPaths();
        var instanceDirectory = Path.Combine(paths.InstancesDirectory, "orphan-instance");
        Directory.CreateDirectory(instanceDirectory);

        // 版本目录整个缺失：minecraft 版本只能来自 instance.json，加载器键必须省略。
        var instance = new InstanceInfo
        {
            Id = "orphan-instance",
            Name = "孤立实例",
            VersionName = "missing-version",
            Loader = LoaderKind.Forge,
            MinecraftVersion = "1.20.1",
        };
        await File.WriteAllTextAsync(
            Path.Combine(instanceDirectory, "instance.json"),
            JsonSerializer.Serialize(instance));

        var target = Path.Combine(paths.RootDirectory, "out", "orphan.mrpack");
        var exporter = new ModpackExporter(paths);
        var result = await exporter.ExportAsync(
            instanceDirectory,
            target,
            new ModpackExportOptions { PreferModrinthHashes = false });

        Assert.True(result.Success, result.Error);

        var dependencies = ReadDependencies(target);
        Assert.Equal("1.20.1", dependencies.GetProperty("minecraft").GetString());
        Assert.False(dependencies.TryGetProperty("forge", out _));
    }

    private static async Task WriteInstanceJsonAsync(
        string instanceDirectory,
        string id,
        string versionName,
        LoaderKind loader)
    {
        var instance = new InstanceInfo
        {
            Id = id,
            Name = id,
            VersionName = versionName,
            Loader = loader,
        };

        await File.WriteAllTextAsync(
            Path.Combine(instanceDirectory, "instance.json"),
            JsonSerializer.Serialize(instance));
    }

    private static JsonElement ReadDependencies(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var stream = archive.GetEntry("modrinth.index.json")!.Open();
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("dependencies").Clone();
    }

    /// <summary>一个隔离的实例环境：临时 launcher 根 + Fabric 版本链 + 命中 / 未命中的 jar。</summary>
    private sealed class ExportEnvironment : IDisposable
    {
        private const string FabricVersionName = "fabric-loader-0.16.9-1.20.1";

        private readonly CoreAssetsLog _log = new();

        public ExportEnvironment()
        {
            Paths = new CoreAssetsPaths();
            InstanceDirectory = Path.Combine(Paths.InstancesDirectory, "demo-instance");
            TargetPath = Path.Combine(Paths.RootDirectory, "out", "测试整合包.mrpack");
            HitBytes = Encoding.UTF8.GetBytes("sodium-jar-content");
            MissBytes = Encoding.UTF8.GetBytes("local-mod-content");
            HitSha1 = Convert.ToHexString(SHA1.HashData(HitBytes)).ToLowerInvariant();
        }

        public CoreAssetsPaths Paths { get; }

        public string InstanceDirectory { get; }

        public string TargetPath { get; }

        public byte[] HitBytes { get; }

        public byte[] MissBytes { get; }

        public string HitSha1 { get; }

        public async Task WriteInstanceAsync()
        {
            Directory.CreateDirectory(Path.Combine(InstanceDirectory, "mods"));
            Directory.CreateDirectory(Path.Combine(InstanceDirectory, "config"));
            Directory.CreateDirectory(Path.Combine(InstanceDirectory, "saves", "world-1"));
            Directory.CreateDirectory(Path.Combine(InstanceDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(InstanceDirectory, "crash-reports"));

            await File.WriteAllBytesAsync(Path.Combine(InstanceDirectory, "mods", "sodium.jar"), HitBytes);
            await File.WriteAllBytesAsync(Path.Combine(InstanceDirectory, "mods", "local-mod.jar.disabled"), MissBytes);
            await File.WriteAllTextAsync(Path.Combine(InstanceDirectory, "config", "example.toml"), "enabled = true");
            await File.WriteAllTextAsync(Path.Combine(InstanceDirectory, "saves", "world-1", "level.dat"), "level");
            await File.WriteAllTextAsync(Path.Combine(InstanceDirectory, "logs", "latest.log"), "log");
            await File.WriteAllTextAsync(Path.Combine(InstanceDirectory, "crash-reports", "crash.txt"), "crash");

            var instance = new InstanceInfo
            {
                Id = "demo-instance",
                Name = "演示实例",
                VersionName = FabricVersionName,
                Loader = LoaderKind.Fabric,
            };

            await File.WriteAllTextAsync(
                Path.Combine(InstanceDirectory, "instance.json"),
                JsonSerializer.Serialize(instance));

            var fabricDirectory = Path.Combine(Paths.VersionsDirectory, FabricVersionName);
            Directory.CreateDirectory(fabricDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(fabricDirectory, FabricVersionName + ".json"),
                """
                {
                  "id": "fabric-loader-0.16.9-1.20.1",
                  "inheritsFrom": "1.20.1",
                  "libraries": [
                    { "name": "net.fabricmc:fabric-loader:0.16.9" }
                  ]
                }
                """);

            var vanillaDirectory = Path.Combine(Paths.VersionsDirectory, "1.20.1");
            Directory.CreateDirectory(vanillaDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(vanillaDirectory, "1.20.1.json"),
                """{ "id": "1.20.1" }""");
        }

        public async Task<ModpackExportResult> ExportAsync(
            bool includeSaves,
            bool preferHashes = true,
            HttpMessageHandler? handler = null,
            IProgress<DownloadProgress>? progress = null)
        {
            using var modrinth = preferHashes
                ? new ModrinthService(_log, Paths, handler ?? new LookupHandler(HitSha1))
                : null;

            var exporter = new ModpackExporter(Paths, modrinth, log: _log);

            return await exporter.ExportAsync(
                InstanceDirectory,
                TargetPath,
                new ModpackExportOptions
                {
                    Name = "测试整合包",
                    Version = "2.3.4",
                    IncludeSaves = includeSaves,
                    IncludeConfig = true,
                    IncludeResourcePacks = false,
                    IncludeShaderPacks = false,
                    PreferModrinthHashes = preferHashes,
                },
                progress);
        }

        /// <summary>只用于 InspectAsync：构造服务需要一个下载队列，但读包不会触发下载。</summary>
        public ModpackService CreateModpackService() =>
            new(Paths, new DownloadQueue(new HttpClient()), _log);

        public void Dispose() => Paths.Dispose();
    }

    /// <summary>按 SHA1 返回一个 Modrinth 版本对象；其余哈希返回 404（用户手工拖入的 jar 是常态）。</summary>
    private sealed class LookupHandler : HttpMessageHandler
    {
        private readonly string _hitSha1;
        private readonly string _versionJson;

        public LookupHandler(string hitSha1)
        {
            _hitSha1 = hitSha1;
            _versionJson =
                "{\"id\":\"ver-1\",\"project_id\":\"sodium\",\"name\":\"Sodium 0.5.8\","
                + "\"version_number\":\"0.5.8\",\"version_type\":\"release\","
                + "\"files\":[{\"hashes\":{\"sha1\":\"" + hitSha1 + "\"},"
                + "\"url\":\"https://cdn.modrinth.com/data/sodium/versions/ver-1/sodium.jar\","
                + "\"filename\":\"sodium.jar\",\"primary\":true,\"size\":17}]}";
        }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;

            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url.Contains(_hitSha1, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_versionJson, Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }

    /// <summary>模拟网络中断：反查必须降级为 overrides，而不是让整次导出失败。</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("模拟网络中断"));
    }

    /// <summary>同步记录进度：<see cref="Progress{T}"/> 在无同步上下文时会投递到线程池，断言会读到空列表。</summary>
    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = new();

        public void Report(DownloadProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }
}
