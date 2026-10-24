using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Versions;

// 「仅下载客户端 jar」按 SHA1 校验，测试必须按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// 「仅下载客户端 jar」：只取 <c>downloads.client</c>，沿继承链回溯，不写 <c>versions/</c>。
///
/// 背景：拿同版本的客户端 jar 喂别的工具 / 拷给别人 / 离线留档，以前只能先安装再拷出去，
/// 而安装会顺带补一大堆 libraries 与 assets。
/// </summary>
public class CoreVersions_ClientJarSaveTests
{
    private static readonly byte[] FakeJar = Encoding.ASCII.GetBytes("fake-client-jar-payload");

    [Fact]
    public async Task 有downloads_client时下载到目标目录()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var sha1 = Sha1(FakeJar);
        WriteVersion(paths, "1.20.1", inheritsFrom: null, clientSha1: sha1, clientSize: FakeJar.Length);

        var handler = new StubJarHandler(FakeJar);
        using var client = new HttpClient(handler);
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var target = temp.Combine("out");
        var result = await service.SaveClientJarAsync("1.20.1", target);

        Assert.True(result.Success, result.Error);
        Assert.Equal("1.20.1", result.VersionName);
        Assert.Equal(Path.Combine(target, "1.20.1.jar"), result.FilePath);
        Assert.Equal(FakeJar.Length, result.Size);
        Assert.Equal(sha1, result.Sha1, ignoreCase: true);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(FakeJar, await File.ReadAllBytesAsync(result.FilePath));

        // 关键约束：版本目录不能被改动（不写 versions/<name>/ 里的任何文件）。
        Assert.False(File.Exists(Path.Combine(paths.VersionsDirectory, "1.20.1", "1.20.1.jar")));
    }

    [Fact]
    public async Task 加载器版本沿inheritsFrom回溯到原版()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var sha1 = Sha1(FakeJar);

        // Fabric 的 profile JSON 只描述增量，downloads 是空对象。
        WriteVersion(paths, "1.20.1", inheritsFrom: null, clientSha1: sha1, clientSize: FakeJar.Length);
        WriteVersion(paths, "fabric-loader-0.15.0-1.20.1", inheritsFrom: "1.20.1", clientSha1: null, clientSize: 0);

        using var client = new HttpClient(new StubJarHandler(FakeJar));
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var target = temp.Combine("out");
        var result = await service.SaveClientJarAsync("fabric-loader-0.15.0-1.20.1", target);

        Assert.True(result.Success, result.Error);

        // 落盘名用「你选的那个版本」，内容取自原版——复制出去时文件名才对得上。
        Assert.Equal("fabric-loader-0.15.0-1.20.1.jar", Path.GetFileName(result.FilePath));
    }

    [Fact]
    public async Task 没有downloads_client时返回中文失败原因()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteVersion(paths, "1.20.1", inheritsFrom: null, clientSha1: null, clientSize: 0);

        using var client = new HttpClient(new StubJarHandler(FakeJar));
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var result = await service.SaveClientJarAsync("1.20.1", temp.Combine("out"));

        Assert.False(result.Success);
        Assert.Contains("downloads.client", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task 目标已存在且SHA1一致时不重复下载()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var sha1 = Sha1(FakeJar);
        WriteVersion(paths, "1.20.1", inheritsFrom: null, clientSha1: sha1, clientSize: FakeJar.Length);

        var target = temp.Combine("out");
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "1.20.1.jar");
        await File.WriteAllBytesAsync(existing, FakeJar);

        var handler = new StubJarHandler(FakeJar);
        using var client = new HttpClient(handler);
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var result = await service.SaveClientJarAsync("1.20.1", target);

        Assert.True(result.Success, result.Error);
        Assert.True(result.AlreadyExisted);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task 空版本名与空目录都直接失败不联网()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new StubJarHandler(FakeJar);
        using var client = new HttpClient(handler);
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var noName = await service.SaveClientJarAsync("   ", temp.Combine("out"));
        var noDirectory = await service.SaveClientJarAsync("1.20.1", "  ");

        Assert.False(noName.Success);
        Assert.Contains("版本名", noName.Error, StringComparison.Ordinal);

        Assert.False(noDirectory.Success);
        Assert.Contains("目标目录", noDirectory.Error, StringComparison.Ordinal);

        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    public async Task 非法版本路径直接失败且不写出目标目录(string versionName)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new StubJarHandler(FakeJar);
        using var client = new HttpClient(handler);
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var result = await service.SaveClientJarAsync(versionName, temp.Combine("out"));

        Assert.False(result.Success);
        Assert.Contains("非法", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.jar")));
    }

    [Fact]
    public async Task 下载失败时不留半截文件()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteVersion(paths, "1.20.1", inheritsFrom: null, clientSha1: Sha1(FakeJar), clientSize: FakeJar.Length);

        var handler = new StubJarHandler(FakeJar, HttpStatusCode.Forbidden);
        using var client = new HttpClient(handler);
        var service = new VersionService(paths, NullAppLog.Instance, client);

        var target = temp.Combine("out");
        var result = await service.SaveClientJarAsync("1.20.1", target);

        Assert.False(result.Success);
        // 半截 jar 在磁盘上比没有更糟：拷给别人会直接崩在启动。
        Assert.False(File.Exists(Path.Combine(target, "1.20.1.jar")));
    }

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));

    /// <summary>
    /// 写一份最小版本 JSON。<paramref name="clientSha1"/> 为 null 时 <c>downloads</c> 是空对象
    /// （Fabric / Quilt profile 的真实形状）。
    /// </summary>
    private static void WriteVersion(
        FakeLauncherPaths paths,
        string name,
        string? inheritsFrom,
        string? clientSha1,
        long clientSize)
    {
        var directory = Path.Combine(paths.VersionsDirectory, name);
        Directory.CreateDirectory(directory);

        var inherits = inheritsFrom is null ? string.Empty : $",\n  \"inheritsFrom\": \"{inheritsFrom}\"";

        var downloads = clientSha1 is null
            ? "\"downloads\": {}"
            : $$"""
                "downloads": {
                    "client": {
                      "url": "https://piston-data.mojang.com/v1/objects/{{clientSha1}}/client.jar",
                      "sha1": "{{clientSha1}}",
                      "size": {{clientSize}}
                    }
                  }
                """;

        File.WriteAllText(
            Path.Combine(directory, name + ".json"),
            $$"""
              {
                "id": "{{name}}",
                "type": "release",
                "mainClass": "net.minecraft.client.main.Main",
                {{downloads}}{{inherits}}
              }
              """);
    }

    private sealed class StubJarHandler(byte[] payload, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            if (status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
