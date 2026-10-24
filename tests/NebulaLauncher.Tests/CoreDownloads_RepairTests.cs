using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本修复的两档行为。
///
/// 这组用例全部用假 <see cref="HttpMessageHandler"/> + 临时目录，判据是「磁盘上发生了什么」，
/// 不看日志也不看内部字段——修复是用户最后的手段，行为必须可由外部观察验证。
/// </summary>
public class CoreDownloads_RepairTests
{
    private const string Version = "1.20.1";
    private const string LibraryPath = "example/lib/lib-1.0.jar";
    private const string LibraryUrl = "https://libraries.minecraft.net/" + LibraryPath;

    [Fact]
    public async Task 缺库文件时Deep修复会补回()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var payload = Encoding.UTF8.GetBytes("library-payload");
        var sha1 = Sha1Hex(payload);

        var handler = new RepairHttpHandler();
        handler.Map(LibraryUrl, payload);
        WriteVersion(paths, Version, VersionJson(Version, sha1, payload.Length, assetIndex: null, client: null));

        using var client = new HttpClient(handler);
        using var service = CreateService(paths, client);

        var report = await service.RepairAsync(Version, RepairDepth.Deep);

        var absolute = Path.Combine(paths.LibrariesDirectory, "example", "lib", "lib-1.0.jar");
        Assert.True(File.Exists(absolute), $"缺失的库文件没有被补回。Failed={Describe(report)}");
        Assert.Equal(payload, await File.ReadAllBytesAsync(absolute));
        Assert.True(report.Redownloaded >= 1, Describe(report));
        Assert.Equal(1, report.Missing);
        Assert.Empty(report.Failed);
    }

    [Fact]
    public async Task 篡改库文件时Quick不动Deep重下()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var payload = Encoding.UTF8.GetBytes("library-payload");
        var sha1 = Sha1Hex(payload);

        // 同大小、内容不同：Quick 之所以「不动」是因为 CmlLib 的判定粒度是「文件存在」（EXTENSIONS 条目 6），
        // 同大小可以排除「它按长度判」这一种实现差异。
        var tampered = (byte[])payload.Clone();
        tampered[^1] ^= 0xFF;

        Assert.Equal(payload.Length, tampered.Length);

        var absolute = Path.Combine(paths.LibrariesDirectory, "example", "lib", "lib-1.0.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, tampered);

        var handler = new RepairHttpHandler();
        handler.Map(LibraryUrl, payload);
        WriteVersion(paths, Version, VersionJson(Version, sha1, payload.Length, assetIndex: null, client: null));

        using var client = new HttpClient(handler);
        using var service = CreateService(paths, client);

        var quick = await service.RepairAsync(Version, RepairDepth.Quick);

        Assert.Equal(0, quick.Redownloaded);
        Assert.Equal(tampered, await File.ReadAllBytesAsync(absolute));

        var deep = await service.RepairAsync(Version, RepairDepth.Deep);

        Assert.True(deep.Redownloaded >= 1);
        Assert.Equal(1, deep.Mismatched);
        Assert.Equal(payload, await File.ReadAllBytesAsync(absolute));
    }

    [Fact]
    public async Task 零字节客户端jar触发重下()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var clientJar = Encoding.UTF8.GetBytes("client-jar-payload");
        var sha1 = Sha1Hex(clientJar);
        const string clientUrl = "https://piston-data.mojang.com/v1/objects/aa/client.jar";

        var handler = new RepairHttpHandler();
        handler.Map(clientUrl, clientJar);
        WriteVersion(paths, Version, VersionJson(Version, librarySha1: null, librarySize: 0, assetIndex: null, client: (clientUrl, sha1, clientJar.Length)));

        var jarPath = Path.Combine(paths.VersionsDirectory, Version, Version + ".jar");
        await File.WriteAllBytesAsync(jarPath, Array.Empty<byte>());

        using var client = new HttpClient(handler);
        using var service = CreateService(paths, client);

        var report = await service.RepairAsync(Version, RepairDepth.Quick);

        Assert.Equal(clientJar, await File.ReadAllBytesAsync(jarPath));
        Assert.True(report.Redownloaded >= 1, Describe(report));

        // 测试环境没有真实网络，CmlLib 的「补齐缺失文件」步骤可能会失败并进 Failed；
        // 这条用例要证明的是「本地判据（0 字节）能独立把 jar 重下」，不因那步失败而中断。
    }

    private static string Describe(RepairReport report) =>
        $"Missing={report.Missing} Mismatched={report.Mismatched} Redownloaded={report.Redownloaded} Fixed=[{string.Join(", ", report.Fixed)}] Failed=[{string.Join(" | ", report.Failed)}]";

    [Fact]
    public async Task 一点五版本Forge的Mojang签名会被删除()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        const string forgeVersion = "1.5.2-forge-7.8.1.738";
        WriteVersion(paths, "1.5.2", """
            {
              "id": "1.5.2",
              "type": "release",
              "mainClass": "net.minecraft.client.Minecraft"
            }
            """);
        WriteVersion(paths, forgeVersion, $$"""
            {
              "id": "{{forgeVersion}}",
              "type": "release",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "inheritsFrom": "1.5.2",
              "libraries": []
            }
            """);

        var jarPath = Path.Combine(paths.VersionsDirectory, forgeVersion, forgeVersion + ".jar");
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        await CreateJarAsync(jarPath, "META-INF/MANIFEST.MF", "META-INF/MOJANG_C.SF", "META-INF/MOJANG_C.DSA");

        using var client = new HttpClient(new RepairHttpHandler());
        using var service = CreateService(paths, client);

        var report = await service.RepairAsync(forgeVersion, RepairDepth.Deep);

        var entries = ReadEntryNames(jarPath);
        Assert.DoesNotContain("META-INF/MOJANG_C.SF", entries);
        Assert.DoesNotContain("META-INF/MOJANG_C.DSA", entries);
        Assert.Contains("META-INF/MANIFEST.MF", entries);
        Assert.Contains(report.Fixed, item => item.EndsWith("MOJANG_C.SF", StringComparison.Ordinal));
        Assert.Empty(report.Failed);
    }

    [Fact]
    public async Task 资源索引只判能否解析不判sha1()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var assetHash = Sha1Hex(Encoding.UTF8.GetBytes("asset-payload"));
        var assetBytes = Encoding.UTF8.GetBytes("asset-payload");
        var assetUrl = "https://resources.download.minecraft.net/" + assetHash[..2] + "/" + assetHash;

        // 索引内容的 sha1 故意与 JSON 声明的不一致：Mojang 会回改 assetIndex 的 sha1，
        // 按它判定就会无限重下，所以只判「能解析成 objects」。
        var indexJson = new StringBuilder()
            .Append("{\"objects\":{\"minecraft/test\":{\"hash\":\"").Append(assetHash)
            .Append("\",\"size\":").Append(assetBytes.Length)
            .Append("}}}")
            .ToString();
        var indexUrl = "https://piston-meta.mojang.com/v1/packages/index.json";

        var handler = new RepairHttpHandler();
        handler.Map(assetUrl, assetBytes);
        handler.Map(indexUrl, Encoding.UTF8.GetBytes(indexJson));

        WriteVersion(paths, Version, VersionJson(Version, librarySha1: null, librarySize: 0, assetIndex: (indexUrl, Sha1Hex(Encoding.UTF8.GetBytes("not-the-file")), indexJson.Length), client: null));

        var indexPath = Path.Combine(paths.AssetsDirectory, "indexes", "5.json");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, indexJson);

        using var client = new HttpClient(handler);
        using var service = CreateService(paths, client);

        var report = await service.RepairAsync(Version, RepairDepth.Deep);

        Assert.DoesNotContain(handler.Requested, url => url.Contains("index.json", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(paths.AssetsDirectory, "objects", assetHash[..2], assetHash)), Describe(report));
        Assert.True(report.Redownloaded >= 1, Describe(report));
        Assert.Empty(report.Failed);
    }

    [Fact]
    public async Task 取消令牌生效()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteVersion(paths, Version, VersionJson(Version, librarySha1: null, librarySize: 0, assetIndex: null, client: null));

        using var client = new HttpClient(new RepairHttpHandler());
        using var service = CreateService(paths, client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RepairAsync(Version, RepairDepth.Deep, cancellationToken: cts.Token));
    }

    // ---------------- 辅助 ----------------

    private static InstallService CreateService(FakeLauncherPaths paths, HttpClient client) =>
        new(paths, new FakePlatformService(), new ExplodingJavaService(), client, NullAppLog.Instance);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "Mojang 的资源校验就是 SHA1，测试必须用同一种算法构造期望值")]
    private static string Sha1Hex(byte[] content) => Convert.ToHexString(SHA1.HashData(content));

    private static void WriteVersion(FakeLauncherPaths paths, string id, string json)
    {
        var directory = Path.Combine(paths.VersionsDirectory, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    private static string VersionJson(
        string id,
        string? librarySha1,
        long librarySize,
        (string Url, string Sha1, long Size)? assetIndex,
        (string Url, string Sha1, long Size)? client)
    {
        var builder = new StringBuilder();
        builder.Append("{\"id\":\"").Append(id).Append("\",\"type\":\"release\",\"mainClass\":\"net.minecraft.client.main.Main\"");

        if (client is { } jar)
        {
            builder.Append(",\"downloads\":{\"client\":{\"sha1\":\"").Append(jar.Sha1)
                .Append("\",\"size\":").Append(jar.Size)
                .Append(",\"url\":\"").Append(jar.Url).Append("\"}}");
        }

        if (assetIndex is { } index)
        {
            builder.Append(",\"assetIndex\":{\"id\":\"5\",\"sha1\":\"").Append(index.Sha1)
                .Append("\",\"size\":").Append(index.Size)
                .Append(",\"url\":\"").Append(index.Url).Append("\"}");
        }

        builder.Append(",\"libraries\":[");
        if (librarySha1 is not null)
        {
            builder.Append("{\"name\":\"example:lib:1.0\",\"downloads\":{\"artifact\":{\"path\":\"")
                .Append(LibraryPath)
                .Append("\",\"sha1\":\"").Append(librarySha1)
                .Append("\",\"size\":").Append(librarySize)
                .Append(",\"url\":\"").Append(LibraryUrl).Append("\"}}}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static async Task CreateJarAsync(string path, params string[] entryNames)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var entryName in entryNames)
        {
            var entry = archive.CreateEntry(entryName);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(Encoding.UTF8.GetBytes(entryName));
        }
    }

    private static string[] ReadEntryNames(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    /// <summary>按 URL 精确匹配的假 handler；未映射的地址一律 404（模拟镜像/官方都没有该文件）。</summary>
    private sealed class RepairHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _requested = new();

        public IReadOnlyList<string> Requested
        {
            get
            {
                lock (_requested)
                {
                    return _requested.ToArray();
                }
            }
        }

        public void Map(string url, byte[] content)
        {
            lock (_responses)
            {
                _responses[url] = content;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            lock (_requested)
            {
                _requested.Add(url);
            }

            byte[]? content;
            lock (_responses)
            {
                _responses.TryGetValue(url, out content);
            }

            if (content is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }
}
