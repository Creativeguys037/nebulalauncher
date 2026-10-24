using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// 服务端（server.jar）下载：清单解析、SHA1 校验、跳过已存在、目录不可写、进度单调。
///
/// 全部用假 handler：真实网络下载（1.20.1，约 45 MB）单独做过冒烟，见交付报告；
/// 这里钉住的是「哪些版本该出现、校验失败必须删文件、已存在不能重下」这些行为判据。
/// </summary>
public class CoreVersions_ServerDownloadTests
{
    private const string VersionJsonUrl = "https://piston-meta.mojang.com/v1/packages/aaaa/1.20.1.json";
    private const string ServerUrl = "https://piston-data.mojang.com/v1/objects/bbbb/server.jar";

    [Fact]
    public void 清单解析读出服务端条目()
    {
        var version = NewRemote("1.20.1", "release", 2023, 6, 12);
        var json = VersionJson(ServerUrl, "ABCDEF1234", 45_000_000);

        var info = ServerDownloadService.ParseServerInfo(version, json);

        Assert.NotNull(info);
        Assert.Equal("1.20.1", info.Id);
        Assert.Equal(ServerUrl, info.Url);
        Assert.Equal("ABCDEF1234", info.Sha1);
        Assert.Equal(45_000_000, info.Size);
        Assert.Equal(VersionChannel.Release, info.Channel);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"downloads":{}}""")]
    [InlineData("""{"downloads":{"client":{"url":"https://x/client.jar"}}}""")]
    [InlineData("""{"downloads":{"server":{"sha1":"aa"}}}""")]
    [InlineData("""{"downloads":{"server":{"url":"","sha1":"aa"}}}""")]
    [InlineData("not json")]
    public void 清单解析对缺失服务端的版本返回空(string json)
    {
        Assert.Null(ServerDownloadService.ParseServerInfo(NewRemote("x", "release", 2020, 1, 1), json));
    }

    [Fact]
    public async Task 列表只包含带服务端的版本()
    {
        using var handler = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100)))
            .Add(Url("1.19.4"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl + "2", "BB", 200)))
            .Add(Url("1.18.2"), Encoding.UTF8.GetBytes("""{"downloads":{"client":{"url":"https://x"}}}"""));

        using var client = new HttpClient(handler);
        var versions = new FakeVersionService(
            NewRemote("1.18.2", "release", 2022, 2, 28),
            NewRemote("1.20.1", "release", 2023, 6, 12),
            NewRemote("1.19.4", "release", 2023, 3, 14));

        using var service = new ServerDownloadService(versions, client, NullAppLog.Instance);
        var list = await service.ListAsync();

        Assert.Equal(2, list.Count);

        // 按发行时间降序：1.20.1 (2023-06) 在前。
        Assert.Equal("1.20.1", list[0].Id);
        Assert.Equal("1.19.4", list[1].Id);
    }

    [Fact]
    public async Task 下载成功写入文件并返回实际SHA1()
    {
        var payload = Payload(64 * 1024);
        using var handler = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, Sha1Of(payload), payload.Length)))
            .Add(ServerUrl, payload);

        using var client = new HttpClient(handler);
        using var service = NewService(client, "1.20.1");
        using var temp = new TempDirectory();

        var target = temp.Combine("servers", "1.20.1");
        var result = await service.DownloadAsync("1.20.1", target);

        Assert.True(result.Success, result.Error);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(Path.Combine(target, "server-1.20.1.jar"), result.FilePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(payload.Length, result.Size);
        Assert.Equal(Sha1Of(payload), result.Sha1, ignoreCase: true);
        Assert.Equal(1, handler.Hits(ServerUrl));
    }

    [Fact]
    public async Task 已存在且校验通过时跳过下载()
    {
        var payload = Payload(32 * 1024);
        using var handler = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, Sha1Of(payload), payload.Length)))
            .Add(ServerUrl, payload);

        using var client = new HttpClient(handler);
        using var service = NewService(client, "1.20.1");
        using var temp = new TempDirectory();

        var target = temp.Combine("servers", "1.20.1");
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target, "server-1.20.1.jar"), payload);

        var result = await service.DownloadAsync("1.20.1", target);

        Assert.True(result.Success, result.Error);
        Assert.True(result.AlreadyExisted);
        Assert.Equal(0, handler.Hits(ServerUrl));
    }

    [Fact]
    public async Task SHA1校验失败会删掉文件()
    {
        var payload = Payload(16 * 1024);
        using var handler = new RouteHandler()
            // 清单给的 SHA1 与真实内容不符，模拟镜像回源拿到错误内容 / 传输损坏。
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "0000000000000000000000000000000000000000", payload.Length)))
            .Add(ServerUrl, payload);

        using var client = new HttpClient(handler);
        using var service = NewService(client, "1.20.1");
        using var temp = new TempDirectory();

        var target = temp.Combine("servers", "1.20.1");
        var result = await service.DownloadAsync("1.20.1", target);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(File.Exists(Path.Combine(target, "server-1.20.1.jar")));

        // .part 也不能留下。
        Assert.Empty(Directory.GetFiles(target, "*.part"));
    }

    [Fact]
    public async Task 目标目录不可写时报错()
    {
        var payload = Payload(1024);
        using var handler = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, Sha1Of(payload), payload.Length)))
            .Add(ServerUrl, payload);

        using var client = new HttpClient(handler);
        using var service = NewService(client, "1.20.1");
        using var temp = new TempDirectory();

        // 把「目录」位置先占成一个文件：CreateDirectory 在其下必然失败。
        var blocked = temp.Combine("blocked");
        await File.WriteAllTextAsync(blocked, "not a directory");

        var result = await service.DownloadAsync("1.20.1", Path.Combine(blocked, "sub"));

        Assert.False(result.Success);
        Assert.Contains("无法创建目录", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, handler.Hits(ServerUrl));
    }

    [Fact]
    public async Task 版本没有服务端时报错()
    {
        using var handler = new RouteHandler()
            .Add(Url("1.12"), Encoding.UTF8.GetBytes("""{"downloads":{"client":{"url":"https://x"}}}"""));

        using var client = new HttpClient(handler);
        using var service = NewService(client, "1.12");
        using var temp = new TempDirectory();

        var result = await service.DownloadAsync("1.12", temp.Combine("servers", "1.12"));

        Assert.False(result.Success);
        Assert.Contains("没有提供官方服务端", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 进度回调字节递增且单调不回退()
    {
        var payload = Payload(3 * 1024 * 1024);
        using var handler = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, Sha1Of(payload), payload.Length)))
            .Add(ServerUrl, payload);

        using var client = new HttpClient(handler);
        using var service = NewService(client, "1.20.1");
        using var temp = new TempDirectory();

        var progress = new RecordingProgress();
        var result = await service.DownloadAsync("1.20.1", temp.Combine("servers", "1.20.1"), progress);

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(progress.Items);

        var previous = 0L;

        foreach (var item in progress.Items)
        {
            Assert.True(
                item.DownloadedBytes >= previous,
                $"进度回退：{previous} → {item.DownloadedBytes}");

            Assert.InRange(item.Ratio, 0, 1);
            previous = item.DownloadedBytes;
        }

        // 结束时的聚合字节等于文件大小。
        Assert.Equal(payload.Length, progress.Items[^1].DownloadedBytes);
    }

    // ==================== 清单缓存与扫描策略 ====================

    [Fact]
    public async Task 缓存命中时不发网络请求()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var versions = SingleVersionList("1.20.1");

        // 第一次：扫描并落盘。
        var scanning = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100)));

        using (var client = new HttpClient(scanning))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            var first = await service.ListSnapshotAsync();

            Assert.Equal(ServerListSource.Network, first.Source);
            Assert.True(File.Exists(service.CacheFilePath!));
            Assert.NotEqual(0, scanning.TotalHits);
        }

        // 第二次：全新实例 + 全新 handler（不注册任何路由）→ 命中缓存，0 请求。
        var silent = new RouteHandler();

        using (var client = new HttpClient(silent))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            var second = await service.ListSnapshotAsync();

            Assert.Equal(ServerListSource.Cache, second.Source);
            Assert.False(second.IsRefreshing);
            Assert.Equal(0, silent.TotalHits);
            Assert.Equal("1.20.1", Assert.Single(second.Items).Id);
            Assert.NotNull(second.CachedAt);
        }
    }

    [Fact]
    public async Task 缓存过期先返回旧值再后台刷新()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var versions = SingleVersionList("1.20.1");

        // 先写入一份新鲜缓存。
        using (var client = new HttpClient(new RouteHandler()
                   .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100)))))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            await service.ListSnapshotAsync();
        }

        // 推进到过期。
        clock.Advance(TimeSpan.FromHours(7));

        using var refreshing = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "BB", 200)));

        using var refreshClient = new HttpClient(refreshing);
        using var reader = NewServiceWithCache(refreshClient, versions, paths, clock);

        var stale = await reader.ListSnapshotAsync();

        // 先拿到旧值：不阻塞、不发网络（后台刷新是稍后才发生的）。
        Assert.Equal(ServerListSource.Cache, stale.Source);
        Assert.True(stale.IsRefreshing);
        Assert.NotEmpty(stale.Items);

        var fresh = await reader.EnsureRefreshedAsync();

        Assert.Equal(ServerListSource.Network, fresh.Source);
        Assert.False(fresh.IsRefreshing);
        Assert.NotEqual(0, refreshing.TotalHits);
        Assert.Equal("BB", Assert.Single(fresh.Items).Sha1);
    }

    [Fact]
    public async Task 损坏缓存静默重建()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var versions = SingleVersionList("1.20.1");

        var cachePath = Path.Combine(paths.CacheDirectory, ServerDownloadService.CacheFileName);
        Directory.CreateDirectory(paths.CacheDirectory);
        await File.WriteAllTextAsync(cachePath, "{ 这不是合法 JSON");

        using var client = new HttpClient(new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100))));
        using var service = NewServiceWithCache(client, versions, paths, clock);

        var snapshot = await service.ListSnapshotAsync();

        Assert.Equal(ServerListSource.Network, snapshot.Source);
        Assert.Equal("1.20.1", Assert.Single(snapshot.Items).Id);
    }

    [Fact]
    public async Task TTL边界_不足六小时命中_超过六小时视为过期()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var versions = SingleVersionList("1.20.1");

        using (var client = new HttpClient(new RouteHandler()
                   .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100)))))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            await service.ListSnapshotAsync();
        }

        // 5h59m：仍算新鲜，不发网络。
        clock.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59));

        var near = new RouteHandler();

        using (var client = new HttpClient(near))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            var snapshot = await service.ListSnapshotAsync();

            Assert.Equal(ServerListSource.Cache, snapshot.Source);
            Assert.False(snapshot.IsRefreshing);
            Assert.Equal(0, near.TotalHits);
        }

        // 6h01m：过期，返回旧值 + 后台刷新。
        clock.Advance(TimeSpan.FromMinutes(2));

        var expired = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "CC", 300)));

        using (var client = new HttpClient(expired))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            var snapshot = await service.ListSnapshotAsync();

            Assert.Equal(ServerListSource.Cache, snapshot.Source);
            Assert.True(snapshot.IsRefreshing);
            Assert.Equal(TimeSpan.FromHours(6), ServerDownloadService.CacheTtl);
        }
    }

    [Fact]
    public async Task ForceRefresh绕过缓存()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var versions = SingleVersionList("1.20.1");

        using (var client = new HttpClient(new RouteHandler()
                   .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100)))))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            await service.ListSnapshotAsync();
        }

        var network = new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "DD", 400)));

        using (var client = new HttpClient(network))
        using (var service = NewServiceWithCache(client, versions, paths, clock))
        {
            // 缓存还很新，但 forceRefresh 必须绕过它。
            var snapshot = await service.ListSnapshotAsync(forceRefresh: true);

            Assert.Equal(ServerListSource.Network, snapshot.Source);
            Assert.NotEqual(0, network.TotalHits);
            Assert.Equal("DD", Assert.Single(snapshot.Items).Sha1);
        }
    }

    [Fact]
    public async Task 快照带latest_release()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);

        // 真实 manifest 的 versions 数组按发行时间降序（最新在前），假清单按同一顺序构造。
        var versions = new FakeVersionService(
            NewRemote("1.21", "release", 2024, 6, 13),
            NewRemote("1.20.1", "release", 2023, 6, 12));

        using var client = new HttpClient(new RouteHandler()
            .Add(Url("1.20.1"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "AA", 100)))
            .Add(Url("1.21"), Encoding.UTF8.GetBytes(VersionJson(ServerUrl, "BB", 100))));

        using var service = NewServiceWithCache(client, versions, paths, clock);
        var snapshot = await service.ListSnapshotAsync();

        Assert.Equal("1.21", snapshot.LatestRelease);
        Assert.Equal(2, snapshot.Items.Count);
    }

    [Fact]
    public void 默认扫描窗口包含latest_release即使发行时间排序异常()
    {
        // 清单顺序里第一个 release 就是 latest；但它的 releaseTime 被写成 2020（数据异常），
        // 按时间排序会掉到窗口外。实现必须显式把它补回扫描列表。
        var remote = new List<RemoteVersion>
        {
            NewRemote("latest", "release", 2020, 1, 1),
        };

        for (var i = 0; i < 40; i++)
        {
            remote.Add(NewRemote($"r{i:00}", "release", 2025, 1, 1));
        }

        var targets = ServerDownloadService.SelectScanTargets(remote, fullScan: false);

        Assert.Contains(targets, version => version.Id == "latest");
        Assert.Equal(41, targets.Count);
        Assert.Equal("latest", ServerDownloadService.ResolveLatestRelease(remote));
    }

    [Fact]
    public void 扫描全部包含窗口外的旧版本()
    {
        var remote = new List<RemoteVersion>();

        for (var i = 0; i < 60; i++)
        {
            remote.Add(NewRemote($"r{i:00}", "release", 2024, 1, 1));
        }

        remote.Add(NewRemote("old", "release", 2012, 1, 1));
        remote.Add(NewRemote("snap", "snapshot", 2025, 1, 1));

        var windowed = ServerDownloadService.SelectScanTargets(remote, fullScan: false);
        var all = ServerDownloadService.SelectScanTargets(remote, fullScan: true);

        Assert.DoesNotContain(windowed, version => version.Id == "old");
        Assert.Contains(all, version => version.Id == "old");
        Assert.Contains(all, version => version.Id == "snap");
        Assert.Equal(62, all.Count);
    }

    /// <summary>同步收集进度，避免 Progress&lt;T&gt; 的线程池回调把断言变成竞态。</summary>
    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = [];

        public void Report(DownloadProgress value)
        {
            lock (Items)
            {
                Items.Add(value);
            }
        }
    }

    private static ServerDownloadService NewService(HttpClient client, string versionId) =>
        new(
            new FakeVersionService(NewRemote(versionId, "release", 2023, 6, 12)),
            client,
            NullAppLog.Instance);

    private static FakeVersionService SingleVersionList(string versionId) =>
        new(NewRemote(versionId, "release", 2023, 6, 12));

    private static ServerDownloadService NewServiceWithCache(
        HttpClient client,
        IVersionService versions,
        ILauncherPaths paths,
        FakeClock clock) =>
        new(versions, client, NullAppLog.Instance, paths, () => clock.Now);

    /// <summary>可推进的假时钟，用来稳定测 TTL 边界而不 sleep。</summary>
    private sealed class FakeClock
    {
        public FakeClock(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; private set; }

        public void Advance(TimeSpan delta) => Now += delta;
    }

    private static RemoteVersion NewRemote(string id, string type, int year, int month, int day) => new()
    {
        Id = id,
        Type = type,
        Url = Url(id),
        ReleaseTime = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero),
    };

    private static string Url(string id) => $"https://piston-meta.mojang.com/v1/packages/{id}/{id}.json";

    // 用拼接而不是原始插值字符串：JSON 里连续的大括号在 $$$""" 里数量极易算错，
    // 这行是测试夹具，可读性优先。
    private static string VersionJson(string url, string sha1, long size) =>
        "{\"downloads\":{\"server\":{\"url\":\""
        + url
        + "\",\"sha1\":\""
        + sha1
        + "\",\"size\":"
        + size.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "}}}";

    private static byte[] Payload(int size)
    {
        var payload = new byte[size];

        for (var i = 0; i < size; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "与服务端 jar 的官方校验算法一致")]
    private static string Sha1Of(byte[] payload) => Convert.ToHexString(SHA1.HashData(payload));

    /// <summary>按 URL 路由的内存响应；未注册的 URL 返回 404。</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

        public RouteHandler Add(string url, byte[] payload)
        {
            _routes[url] = payload;
            return this;
        }

        public int Hits(string url)
        {
            lock (_hits)
            {
                return _hits.TryGetValue(url, out var count) ? count : 0;
            }
        }

        /// <summary>所有 URL 的请求总数。</summary>
        public int TotalHits
        {
            get
            {
                lock (_hits)
                {
                    return _hits.Values.Sum();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            lock (_hits)
            {
                _hits[url] = Hits(url) + 1;
            }

            if (!_routes.TryGetValue(url, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new StreamContent(new MemoryStream(payload, writable: false));
            content.Headers.ContentLength = payload.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>只提供清单的最小版本服务。</summary>
    private sealed class FakeVersionService : IVersionService
    {
        private readonly IReadOnlyList<RemoteVersion> _versions;

        public FakeVersionService(params RemoteVersion[] versions) => _versions = versions;

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult(_versions);

        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionInfo>>(Array.Empty<VersionInfo>());

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionInfo?>(null);

        public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordLaunchAsync(string versionName, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
            LoaderKind loader,
            string minecraftVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LoaderVersion>>(Array.Empty<LoaderVersion>());
    }
}
