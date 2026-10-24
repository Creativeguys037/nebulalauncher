using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Tests;

/// <summary>
/// 跨实例 / 跨根目录的文件复用。
///
/// 判据全部落在磁盘上：命中时目标必须逐字节等于源文件且不残留 <c>.tmp</c>；
/// 未命中时目标必须不存在；目标已存在且无法覆盖时必须原样保留。
/// 索引是进程内缓存，所以每个用例都新建一个 <see cref="FileReuseService"/>。
/// </summary>
public class CoreDownloads_FileReuseTests
{
    [Fact]
    public async Task 同sha1同尺寸时复用并逐字节一致()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var payload = Encoding.UTF8.GetBytes("reusable-mod-payload-" + new string('x', 4096));
        var source = WriteInstanceFile(paths, "instance-a", "mods", "reusable.jar", payload);
        var destination = Path.Combine(paths.MinecraftDirectory, "mods", "reusable.jar");

        var service = new FileReuseService(paths, NullAppLog.Instance);

        var reused = await service.TryReuseAsync(Item(destination, payload, Sha1Hex(payload)), CancellationToken.None);

        Assert.True(reused, "同 size + 同 SHA1 的源文件存在时应命中复用");
        Assert.True(File.Exists(destination), "复用后目标文件必须存在");
        Assert.False(File.Exists(destination + ".tmp"), "复用完成后不应残留 .tmp");
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(payload.Length, new FileInfo(destination).Length);
        Assert.True(File.Exists(source), "复用是复制，源文件必须保留");
    }

    [Fact]
    public async Task sha1不同时不复制()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var sourcePayload = Encoding.UTF8.GetBytes("same-size-different-content");
        var expectedPayload = Encoding.UTF8.GetBytes("SAME-SIZE-DIFFERENT-CONTENT");

        // 两者长度必须相同，否则会退化成「size 不同」这条路径，测不到 sha1 判定。
        Assert.Equal(sourcePayload.Length, expectedPayload.Length);

        WriteInstanceFile(paths, "instance-a", "mods", "same-size.jar", sourcePayload);
        var destination = Path.Combine(paths.MinecraftDirectory, "mods", "same-size.jar");

        var service = new FileReuseService(paths, NullAppLog.Instance);

        var reused = await service.TryReuseAsync(Item(destination, expectedPayload, Sha1Hex(expectedPayload)), CancellationToken.None);

        Assert.False(reused, "size 相同但 SHA1 不同时不能复用");
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".tmp"));
    }

    [Fact]
    public async Task 尺寸不同时直接跳过()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var sourcePayload = Encoding.UTF8.GetBytes(new string('a', 2048));
        var expectedPayload = Encoding.UTF8.GetBytes("expected-content");

        Assert.NotEqual(sourcePayload.Length, expectedPayload.Length);

        WriteInstanceFile(paths, "instance-b", "resourcepacks", "pack.zip", sourcePayload);
        var destination = Path.Combine(paths.MinecraftDirectory, "resourcepacks", "pack.zip");

        var service = new FileReuseService(paths, NullAppLog.Instance);

        var reused = await service.TryReuseAsync(Item(destination, expectedPayload, Sha1Hex(expectedPayload)), CancellationToken.None);

        Assert.False(reused, "size 不同时不应进入候选集");
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task 目标已存在时复用不损坏原文件()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var payload = Encoding.UTF8.GetBytes("fresh-content-from-another-instance");
        WriteInstanceFile(paths, "instance-a", "shaderpacks", "shader.zip", payload);

        var destination = Path.Combine(paths.MinecraftDirectory, "shaderpacks", "shader.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var original = Encoding.UTF8.GetBytes("stale-content");
        await File.WriteAllBytesAsync(destination, original);

        var service = new FileReuseService(paths, NullAppLog.Instance);
        var item = Item(destination, payload, Sha1Hex(payload));

        // 目标被独占打开时覆盖必然失败：这条路径要保证「失败不损坏」，而不是「失败也无所谓」。
        using (var hold = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var reused = await service.TryReuseAsync(item, CancellationToken.None);

            Assert.False(reused, "目标不可写时复用必须放弃并回落到下载");
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".tmp"));

        // 句柄释放后同一个服务应当能把这个目标覆盖成正确内容。
        var retried = await service.TryReuseAsync(item, CancellationToken.None);

        Assert.True(retried, "目标可写时应覆盖成功");
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Theory]
    [InlineData("libraries")]
    [InlineData("versions")]
    [InlineData("assets")]
    [InlineData("logs")]
    public async Task 四类子目录之外的路径一律不复用(string folder)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var payload = Encoding.UTF8.GetBytes("must-not-be-reused");
        WriteInstanceFile(paths, "instance-a", "mods", "candidate.jar", payload);

        var destination = Path.Combine(paths.MinecraftDirectory, folder, "candidate.jar");

        var service = new FileReuseService(paths, NullAppLog.Instance);

        var reused = await service.TryReuseAsync(Item(destination, payload, Sha1Hex(payload)), CancellationToken.None);

        Assert.False(reused, $"{folder} 不在复用范围内");
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task 源文件在复用目录之外时不进索引()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var payload = Encoding.UTF8.GetBytes("library-payload");
        WriteFile(paths.MinecraftDirectory, string.Empty, "libraries", "lib.jar", payload);

        var destination = Path.Combine(paths.MinecraftDirectory, "mods", "lib.jar");

        var service = new FileReuseService(paths, NullAppLog.Instance);

        var reused = await service.TryReuseAsync(Item(destination, payload, Sha1Hex(payload)), CancellationToken.None);

        Assert.False(reused, "libraries 下的文件不参与索引，哪怕 size 与 sha1 都一致");
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task 下载队列命中复用时不再发请求()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var payload = Encoding.UTF8.GetBytes("queued-mod-payload");
        WriteInstanceFile(paths, "instance-a", "mods", "queued.jar", payload);
        var destination = Path.Combine(paths.MinecraftDirectory, "mods", "queued.jar");

        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var queue = new DownloadQueue(
            client,
            NullAppLog.Instance,
            reuse: new FileReuseService(paths, NullAppLog.Instance));

        var result = await queue.DownloadAsync([Item(destination, payload, Sha1Hex(payload))]);

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(1, result.CompletedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(payload.LongLength, result.DownloadedBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    // ---------------- 辅助 ----------------

    /// <summary>
    /// 在版本目录下建一个实例并写入文件。实例目录 = <c>versions/&lt;版本名&gt;</c>，
    /// 复用服务只把带 <c>instance.json</c> 的版本目录当实例根。
    /// </summary>
    private static string WriteInstanceFile(
        FakeLauncherPaths paths,
        string version,
        string folder,
        string name,
        byte[] content)
    {
        var instanceDirectory = Path.Combine(paths.VersionsDirectory, version);
        Directory.CreateDirectory(instanceDirectory);
        File.WriteAllText(
            Path.Combine(instanceDirectory, "instance.json"),
            $$"""{"Id":"{{version}}","Name":"{{version}}","VersionName":"{{version}}"}""");

        return WriteFile(instanceDirectory, string.Empty, folder, name, content);
    }

    private static DownloadItem Item(string destination, byte[] expected, string sha1) => new()
    {
        Url = "https://example.invalid/" + Path.GetFileName(destination),
        DestinationPath = destination,
        ExpectedSize = expected.Length,
        ExpectedSha1 = sha1,
    };

    /// <summary>写一个源文件并返回绝对路径；<paramref name="instance"/> 为空时直接落在 <paramref name="root"/> 下。</summary>
    private static string WriteFile(string root, string instance, string folder, string name, byte[] content)
    {
        var path = string.IsNullOrEmpty(instance)
            ? Path.Combine(root, folder, name)
            : Path.Combine(root, instance, folder, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "Mojang 的资源校验就是 SHA1，测试必须用同一种算法构造期望值")]
    private static string Sha1Hex(byte[] content) => Convert.ToHexString(SHA1.HashData(content));

    /// <summary>只计数的假 handler：复用命中时这个计数必须保持 0。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
