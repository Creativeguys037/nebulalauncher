using System.Net;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// <see cref="ModUpdateService"/> 的端到端测试：真实的 <see cref="ModrinthService"/> /
/// <see cref="CurseForgeService"/> + 假 <see cref="HttpMessageHandler"/>。
///
/// 为什么不直接 mock 服务接口：更新检查的价值全在「哈希怎么变请求参数、响应怎么变结论」这条链上。
/// mock 掉服务就等于把最需要验证的那段逻辑换成手写答案，测完仍然不知道真实客户端会不会拼错 URL。
/// </summary>
public class CoreAssets_ModUpdateTests
{
    /// <summary>GET /v2/version_file/{sha1} 的响应：本地文件就是这个版本。</summary>
    private const string VersionFileJson = """
    {
      "id": "OihdIimA",
      "project_id": "AANobbMI",
      "name": "Sodium 0.5.13",
      "version_number": "mc1.20.1-0.5.13-fabric",
      "version_type": "release",
      "game_versions": ["1.20.1"],
      "loaders": ["fabric"],
      "date_published": "2025-03-03T17:45:49Z",
      "files": [
        {
          "hashes": { "sha1": "bcdbf37d9494e405cba210d7a80ed58e756297b5" },
          "url": "https://cdn.modrinth.com/data/AANobbMI/versions/OihdIimA/sodium-fabric-0.5.13.jar",
          "filename": "sodium-fabric-0.5.13.jar",
          "primary": true,
          "size": 971552
        }
      ]
    }
    """;

    /// <summary>GET /v2/project/{id}/version 的响应：远端最新版本是 0.6.0。</summary>
    private const string NewerVersionsJson = """
    [
      {
        "id": "AAAAAAAA",
        "project_id": "AANobbMI",
        "name": "Sodium 0.6.0",
        "version_number": "mc1.20.1-0.6.0-fabric",
        "version_type": "release",
        "game_versions": ["1.20.1"],
        "loaders": ["fabric"],
        "date_published": "2025-06-01T00:00:00Z",
        "changelog": "修复若干渲染问题",
        "files": [
          {
            "hashes": { "sha1": "1111111111111111111111111111111111111111" },
            "url": "https://cdn.modrinth.com/data/AANobbMI/versions/AAAAAAAA/sodium-0.6.0.jar",
            "filename": "sodium-0.6.0.jar",
            "primary": true,
            "size": 981552
          }
        ]
      }
    ]
    """;

    /// <summary>与本地版本同号的远程列表，用来验证「已是最新」。</summary>
    private const string SameVersionsJson = """
    [
      {
        "id": "OihdIimA",
        "project_id": "AANobbMI",
        "name": "Sodium 0.5.13",
        "version_number": "mc1.20.1-0.5.13-fabric",
        "version_type": "release",
        "game_versions": ["1.20.1"],
        "loaders": ["fabric"],
        "date_published": "2025-03-03T17:45:49Z",
        "files": [
          {
            "hashes": { "sha1": "bcdbf37d9494e405cba210d7a80ed58e756297b5" },
            "url": "https://cdn.modrinth.com/data/AANobbMI/versions/OihdIimA/sodium-fabric-0.5.13.jar",
            "filename": "sodium-fabric-0.5.13.jar",
            "primary": true,
            "size": 971552
          }
        ]
      }
    ]
    """;

    /// <summary>POST /v1/fingerprints 的响应：命中的是本地那份 jei，同时带回该项目的最新文件。</summary>
    private const string FingerprintJson = """
    {
      "data": {
        "exactMatches": [
          {
            "id": 4001,
            "file": {
              "id": 4001,
              "modId": 238222,
              "fileName": "jei-1.20.1-15.2.0.27.jar",
              "displayName": "jei-1.20.1-15.2.0.27.jar",
              "downloadUrl": "https://edge.forgecdn.net/files/4001/jei.jar",
              "fileLength": 1234567,
              "fileDate": "2024-05-01T12:00:00Z",
              "releaseType": 1,
              "gameVersions": ["1.20.1", "Forge"]
            },
            "latestFiles": [
              {
                "id": 5001,
                "modId": 238222,
                "fileName": "jei-1.20.1-15.3.0.30.jar",
                "displayName": "jei-1.20.1-15.3.0.30.jar",
                "downloadUrl": "https://edge.forgecdn.net/files/5001/jei.jar",
                "fileLength": 1334567,
                "fileDate": "2024-06-01T12:00:00Z",
                "releaseType": 1,
                "gameVersions": ["1.20.1", "Forge"]
              }
            ]
          }
        ]
      }
    }
    """;

    [Fact]
    public async Task 检查更新_Modrinth命中且远程有新版本()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-update");
        var file = await CreateModAsync(gameDirectory, "sodium-fabric-0.5.13.jar", "sodium-local");

        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/version_file/", StringComparison.Ordinal))
            {
                return Json(VersionFileJson);
            }

            return path.Contains("/version", StringComparison.Ordinal) ? Json(NewerVersionsJson) : NotFound();
        });

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, handler);
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var report = await service.CheckAsync(
            [Item(file, "0.5.13")],
            gameDirectory,
            "1.20.1",
            LoaderKind.Fabric);

        var info = Assert.Single(report.Items);
        Assert.True(info.Matched);
        Assert.Equal(ContentSourceKind.Modrinth, info.Source);
        Assert.Equal("AANobbMI", info.ProjectId);
        Assert.Equal("0.5.13", info.CurrentVersion);
        Assert.Equal("mc1.20.1-0.6.0-fabric", info.LatestVersion);
        Assert.True(info.HasUpdate);
        Assert.Equal(ModUpdateService.StatusUpdateAvailable, info.Status);
        Assert.Null(info.Error);
        Assert.Null(report.Error);
        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Matched);
        Assert.Equal(1, report.Updatable);

        // 指纹必须按小写 SHA1 反查：Modrinth 服务端按小写存储，大写会查不到。
        var lookupUrl = handler.Urls.FirstOrDefault(url => url.Contains("/version_file/", StringComparison.Ordinal));
        Assert.NotNull(lookupUrl);

        var hashInUrl = Uri.UnescapeDataString(lookupUrl).Split('/')[^1].Split('?')[0];
        Assert.Equal(hashInUrl.ToLowerInvariant(), hashInUrl);
    }

    [Fact]
    public async Task 检查更新_发布号与本地版本一致时已是最新()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-latest");
        var file = await CreateModAsync(gameDirectory, "sodium-fabric-0.5.13.jar", "sodium-local");

        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/version_file/", StringComparison.Ordinal))
            {
                return Json(VersionFileJson);
            }

            return path.Contains("/version", StringComparison.Ordinal) ? Json(SameVersionsJson) : NotFound();
        });

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, handler);
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var report = await service.CheckAsync(
            [Item(file, "0.5.13")],
            gameDirectory,
            "1.20.1",
            LoaderKind.Fabric);

        var info = Assert.Single(report.Items);
        Assert.True(info.Matched);
        Assert.False(info.HasUpdate);
        Assert.Equal(ModUpdateService.StatusLatest, info.Status);
        Assert.Equal(0, report.Updatable);
    }

    /// <summary>本地 jar 没声明版本号时只能确认「属于这个项目」，不能替用户判「已是最新」。</summary>
    [Fact]
    public async Task 检查更新_本地无版本号时只确认归属()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-noversion");
        var file = await CreateModAsync(gameDirectory, "sodium-fabric.jar", "sodium-local");

        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/version_file/", StringComparison.Ordinal))
            {
                return Json(VersionFileJson);
            }

            return path.Contains("/version", StringComparison.Ordinal) ? Json(NewerVersionsJson) : NotFound();
        });

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, handler);
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var report = await service.CheckAsync(
            [Item(file, version: string.Empty)],
            gameDirectory,
            "1.20.1",
            LoaderKind.Fabric);

        var info = Assert.Single(report.Items);
        Assert.True(info.Matched);
        Assert.False(info.HasUpdate);
        Assert.Equal(ModUpdateService.StatusIdentifiedOnly, info.Status);
        Assert.Equal("mc1.20.1-0.6.0-fabric", info.LatestVersion);
    }

    [Fact]
    public async Task 检查更新_两个数据源都没收录时标记未识别()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-unknown");
        var file = await CreateModAsync(gameDirectory, "custom-mod.jar", "hand-built");

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, new RoutingHandler(_ => NotFound()));
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var report = await service.CheckAsync([Item(file, "1.0.0")], gameDirectory, "1.20.1", LoaderKind.Fabric);

        var info = Assert.Single(report.Items);
        Assert.False(info.Matched);
        Assert.Equal(ModUpdateService.StatusUnidentified, info.Status);
        Assert.Contains("未识别", info.Status, StringComparison.Ordinal);

        // 未收录不是错误：网络是通的，只是没人上传过这个文件。
        Assert.Null(info.Error);
        Assert.Null(report.Error);
        Assert.Equal(0, report.Matched);
    }

    [Fact]
    public async Task 检查更新_网络异常时报告整体错误且不抛异常()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-offline");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", "sodium-local");

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, new FailingHandler());
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var report = await service.CheckAsync([Item(file, "0.5.13")], gameDirectory, "1.20.1", LoaderKind.Fabric);

        Assert.NotNull(report.Error);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));

        var info = Assert.Single(report.Items);
        Assert.Equal(ModUpdateService.StatusFailed, info.Status);
        Assert.False(string.IsNullOrWhiteSpace(info.Error));
        Assert.False(info.Matched);
    }

    /// <summary>本地读盘失败只影响那一条，其余条目照常检查。</summary>
    [Fact]
    public async Task 检查更新_单个文件读不出来时只影响该条()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-partial");
        var good = await CreateModAsync(gameDirectory, "good.jar", "good-payload");
        var missing = Path.Combine(gameDirectory, "mods", "missing.jar");

        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/version_file/", StringComparison.Ordinal))
            {
                return Json(VersionFileJson);
            }

            return path.Contains("/version", StringComparison.Ordinal) ? Json(SameVersionsJson) : NotFound();
        });

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, handler);
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var report = await service.CheckAsync(
            [Item(good, "0.5.13"), Item(missing, "1.0.0")],
            gameDirectory,
            "1.20.1",
            LoaderKind.Fabric);

        Assert.Equal(2, report.Items.Count);

        var failed = report.Items.Single(info => info.Item.FilePath == missing);
        Assert.Equal(ModUpdateService.StatusFailed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));

        var matched = report.Items.Single(info => info.Item.FilePath == good);
        Assert.True(matched.Matched);
        Assert.Equal(ModUpdateService.StatusLatest, matched.Status);
    }

    /// <summary>
    /// Modrinth 没收录时要退到 CurseForge 指纹反查。
    /// 指纹接口只回「命中了什么」，所以这条同时验证「按文件名关联」这一步。
    /// </summary>
    [Fact]
    public async Task 检查更新_Modrinth未收录时退回CurseForge指纹反查()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-curseforge");
        var file = await CreateModAsync(gameDirectory, "jei-1.20.1-15.2.0.27.jar", "jei-payload");

        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/v1/fingerprint", StringComparison.Ordinal))
            {
                return Json(FingerprintJson);
            }

            // Modrinth 对两个端点都回 404：这个文件只有 CurseForge 收录。
            return NotFound();
        });

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, handler);
        using var curseForge = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new UpdateTestSettings(),
            handler);

        var service = new ModUpdateService(modrinth, curseForge, paths, new CoreAssetsLog());

        var report = await service.CheckAsync(
            [Item(file, "15.2.0.27", LoaderKind.Forge)],
            gameDirectory,
            "1.20.1",
            LoaderKind.Forge);

        var info = Assert.Single(report.Items);
        Assert.True(info.Matched);
        Assert.Equal(ContentSourceKind.CurseForge, info.Source);
        Assert.Equal("238222", info.ProjectId);
        Assert.True(info.HasUpdate);
        Assert.Equal(ModUpdateService.StatusUpdateAvailable, info.Status);
        Assert.Equal("jei-1.20.1-15.3.0.30.jar", info.LatestVersion);
        Assert.Null(info.Error);
    }

    /// <summary>两个数据源都不可用时，检查结果必须是「失败」而不是「未识别」。</summary>
    [Fact]
    public async Task 检查更新_没有任何数据源时直接给出整体错误()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-nosource");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", "sodium-local");

        var service = new ModUpdateService(modrinth: null, curseForge: null, paths, new CoreAssetsLog());
        var report = await service.CheckAsync([Item(file, "0.5.13")], gameDirectory, "1.20.1", LoaderKind.Fabric);

        Assert.NotNull(report.Error);
        Assert.NotNull(service.LastError);

        var info = Assert.Single(report.Items);
        Assert.Equal(ModUpdateService.StatusFailed, info.Status);
        Assert.False(info.Matched);
    }

    [Fact]
    public async Task 检查更新_空列表直接返回空报告()
    {
        var service = new ModUpdateService(modrinth: null, curseForge: null, paths: null, log: null);

        var report = await service.CheckAsync(
            Array.Empty<ContentItem>(),
            Path.GetTempPath(),
            null,
            LoaderKind.Vanilla);

        Assert.Empty(report.Items);
        Assert.Equal(0, report.Total);
        Assert.Null(report.Error);
    }

    /// <summary>
    /// 版本等价判定的边界：远程发布号带 <c>mc1.20.1-</c> 前缀与 <c>-fabric</c> 后缀，
    /// 本地 jar 声明的却是纯版本号。直接比较会把每个模组都判成「有新版本」。
    /// </summary>
    [Fact]
    public void 版本比较_远程发布号带前后缀时视为同一版本()
    {
        Assert.True(ModUpdateService.VersionsEquivalent("mc1.20.1-0.5.13-fabric", "0.5.13"));
        Assert.True(ModUpdateService.VersionsEquivalent("jei-1.20.1-15.2.0.27.jar", "15.2.0.27"));
        Assert.True(ModUpdateService.VersionsEquivalent("0.5.13+mc1.20.1", "0.5.13"));
        Assert.True(ModUpdateService.VersionsEquivalent("1.20.1", "1.20.1"));

        Assert.False(ModUpdateService.VersionsEquivalent("mc1.20.1-0.6.0-fabric", "0.5.13"));
        Assert.False(ModUpdateService.VersionsEquivalent("1.0.1", "1.0.0"));
        Assert.False(ModUpdateService.VersionsEquivalent(null, "1.0.0"));
        Assert.False(ModUpdateService.VersionsEquivalent("1.0.0", string.Empty));
    }

    /// <summary>哈希缓存要真的被 CheckAsync 用到：第二次检查同一个目录不该重算。</summary>
    [Fact]
    public async Task 检查更新_重复检查命中哈希缓存()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-cache-reuse");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", "sodium-local");

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, new RoutingHandler(_ => NotFound()));
        var service = new ModUpdateService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        await service.CheckAsync([Item(file, "0.5.13")], gameDirectory, "1.20.1", LoaderKind.Fabric);
        await service.CheckAsync([Item(file, "0.5.13")], gameDirectory, "1.20.1", LoaderKind.Fabric);

        Assert.Equal(1, service.ComputedFileCount);
    }

    private static ContentItem Item(string filePath, string version, LoaderKind loader = LoaderKind.Fabric) => new()
    {
        FilePath = filePath,
        FileName = Path.GetFileName(filePath),
        Kind = ContentKind.Mod,
        Id = Path.GetFileNameWithoutExtension(filePath),
        DisplayName = Path.GetFileNameWithoutExtension(filePath),
        Version = version,
        Loader = loader,
    };

    private static async Task<string> CreateModAsync(string gameDirectory, string fileName, string content)
    {
        var directory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    /// <summary>按 URL 路由的假 handler；记录请求过的 URL 供断言。</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (Urls)
            {
                Urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            }

            var response = _responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>模拟网络中断。</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("模拟网络中断"));
    }

    /// <summary>测试用设置服务：只暴露一份内存快照（API key 已内置，不需要再配）。</summary>
    private sealed class UpdateTestSettings : ISettingsService
    {
        public UpdateTestSettings() => Current = new AppSettings();

        public AppSettings Current { get; }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }
}
