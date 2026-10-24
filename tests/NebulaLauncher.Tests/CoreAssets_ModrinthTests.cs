using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

// Modrinth 的 files[].hashes.sha1 就是 SHA1，测试必须按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// Modrinth 客户端的纯逻辑测试：JSON 解析、facets 拼装、文件名落盘策略、SHA1 校验与失败降级。
///
/// 真实 API 的响应结构取自 2026-10 的一次实测（见任务报告），这里嵌成常量，
/// 让测试不依赖网络；网络相关的失败分支用自定义 HttpMessageHandler 覆盖。
/// </summary>
public class CoreAssets_ModrinthTests
{
    /// <summary>GET /v2/search 响应片段（sodium + sodium-extra），字段按实测结构裁剪。</summary>
    private const string SearchJson = """
    {
      "hits": [
        {
          "project_id": "AANobbMI",
          "project_type": "mod",
          "slug": "sodium",
          "author": "jellysquid3",
          "title": "Sodium",
          "description": "A high-performance rendering engine replacement for Minecraft.",
          "categories": ["fabric", "neoforge", "optimization", "quilt"],
          "display_categories": ["fabric", "neoforge", "optimization", "quilt"],
          "versions": ["1.20.1", "1.21.4"],
          "downloads": 250585660,
          "follows": 41999,
          "icon_url": "https://cdn.modrinth.com/data/AANobbMI/295862f4724dc3f78df3447ad6072b2dcd3ef0c9_96.webp",
          "date_modified": "2026-10-16T01:51:36.06314+08:00"
        },
        {
          "project_id": "PtjYWJkn",
          "project_type": "mod",
          "slug": "sodium-extra",
          "author": "FlashyReese",
          "title": "Sodium Extra",
          "description": "A Sodium addon.",
          "categories": ["fabric", "quilt"],
          "versions": ["1.20.1"],
          "downloads": 105327434,
          "follows": 13746,
          "icon_url": null,
          "date_modified": "2026-09-16T10:48:16.702643+08:00"
        }
      ],
      "offset": 0,
      "limit": 2,
      "total_hits": 38
    }
    """;

    /// <summary>GET /v2/project/{id}/version 响应片段；第一个版本挂了 sources 文件（primary=false）。</summary>
    private const string VersionsJson = """
    [
      {
        "id": "OihdIimA",
        "project_id": "AANobbMI",
        "name": "Sodium 0.5.13 for Fabric",
        "version_number": "mc1.20.1-0.5.13-fabric",
        "version_type": "release",
        "game_versions": ["1.20.1"],
        "loaders": ["fabric", "quilt"],
        "date_published": "2025-03-03T17:45:49.132919Z",
        "downloads": 12470100,
        "changelog": "稳定版",
        "files": [
          {
            "hashes": { "sha1": "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d" },
            "url": "https://cdn.modrinth.com/data/AANobbMI/versions/OihdIimA/sodium-sources.jar",
            "filename": "sodium-sources.jar",
            "primary": false,
            "size": 1024
          },
          {
            "hashes": { "sha1": "bcdbf37d9494e405cba210d7a80ed58e756297b5" },
            "url": "https://cdn.modrinth.com/data/AANobbMI/versions/OihdIimA/sodium-fabric-0.5.13%2Bmc1.20.1.jar",
            "filename": "sodium-fabric-0.5.13+mc1.20.1.jar",
            "primary": true,
            "size": 971552
          }
        ]
      },
      {
        "id": "AAAAAAAA",
        "project_id": "AANobbMI",
        "name": "Sodium 0.6.0 for Fabric",
        "version_number": "mc1.21.4-0.6.0-fabric",
        "version_type": "beta",
        "game_versions": ["1.21.4"],
        "loaders": ["fabric"],
        "date_published": "2025-06-01T00:00:00Z",
        "downloads": 100,
        "files": [
          {
            "hashes": { "sha1": "0000000000000000000000000000000000000000" },
            "url": "https://cdn.modrinth.com/data/AANobbMI/versions/AAAAAAAA/sodium-0.6.0.jar",
            "filename": "sodium-0.6.0.jar",
            "primary": true,
            "size": 2048
          }
        ]
      }
    ]
    """;

    [Fact]
    public void 解析搜索_读取项目字段()
    {
        var projects = ModrinthService.ParseSearch(SearchJson, ContentKind.Mod);

        Assert.Equal(2, projects.Count);

        var sodium = projects[0];
        Assert.Equal("AANobbMI", sodium.ProjectId);
        Assert.Equal("sodium", sodium.Slug);
        Assert.Equal("Sodium", sodium.Title);
        Assert.Equal("jellysquid3", sodium.Author);
        Assert.Equal(250585660, sodium.Downloads);
        Assert.Equal(41999, sodium.Follows);
        Assert.Equal(ContentKind.Mod, sodium.Kind);
        Assert.Equal("https://modrinth.com/mod/sodium", sodium.WebUrl);
        Assert.Contains("fabric", sodium.Categories);
        Assert.Contains("1.21.4", sodium.GameVersions);
        Assert.NotNull(sodium.UpdatedAt);
        Assert.Equal("https://cdn.modrinth.com/data/AANobbMI/295862f4724dc3f78df3447ad6072b2dcd3ef0c9_96.webp", sodium.IconUrl);

        // 第二个项目没有图标，落到 null 由界面用首字母占位。
        Assert.Null(projects[1].IconUrl);
    }

    [Fact]
    public void 解析搜索_资源包的网页地址用resourcepack段()
    {
        var projects = ModrinthService.ParseSearch(SearchJson, ContentKind.ResourcePack);
        Assert.Equal("https://modrinth.com/resourcepack/sodium", projects[0].WebUrl);
    }

    [Fact]
    public void 解析搜索_缺少hits时返回空集合而不是抛出()
    {
        Assert.Empty(ModrinthService.ParseSearch("""{"offset":0,"limit":20}""", ContentKind.Mod));
    }

    [Fact]
    public void 解析版本_只取primary文件并解析sha1()
    {
        var versions = ModrinthService.ParseVersions(VersionsJson);

        Assert.Equal(2, versions.Count);

        var stable = versions.Single(version => version.Id == "OihdIimA");
        Assert.Equal("sodium-fabric-0.5.13+mc1.20.1.jar", stable.FileName);
        Assert.Equal("bcdbf37d9494e405cba210d7a80ed58e756297b5", stable.Sha1);
        Assert.Equal(971552, stable.FileSize);
        Assert.Equal("mc1.20.1-0.5.13-fabric", stable.VersionNumber);
        Assert.Equal("Sodium 0.5.13 for Fabric", stable.Name);
        Assert.True(stable.IsRelease);
        Assert.Contains("fabric", stable.Loaders);
    }

    [Fact]
    public void 解析版本_按发布时间降序()
    {
        var versions = ModrinthService.ParseVersions(VersionsJson);

        Assert.Equal("AAAAAAAA", versions[0].Id);
        Assert.True(versions[0].IsBeta);
        Assert.Equal("OihdIimA", versions[1].Id);
    }

    [Fact]
    public void 解析版本_没有files的版本被跳过()
    {
        const string json = """
        [
          { "id": "x", "project_id": "p", "version_number": "1", "files": [] },
          { "id": "y", "project_id": "p", "version_number": "2" }
        ]
        """;

        Assert.Empty(ModrinthService.ParseVersions(json));
    }

    [Fact]
    public void facets_模组同时带类型版本与加载器()
    {
        var facets = ModrinthService.BuildSearchFacets(ContentKind.Mod, "1.20.1", LoaderKind.Fabric);

        Assert.Equal("[[\"project_type:mod\"],[\"versions:1.20.1\"],[\"categories:fabric\"]]", facets);
    }

    [Theory]
    [InlineData(ContentKind.ResourcePack, "1.20.1", LoaderKind.Fabric)]
    [InlineData(ContentKind.ShaderPack, "1.20.1", LoaderKind.NeoForge)]
    public void facets_资源包与光影不按游戏加载器过滤(ContentKind kind, string version, LoaderKind loader)
    {
        var facets = ModrinthService.BuildSearchFacets(kind, version, loader);

        Assert.Contains("project_type:", facets, StringComparison.Ordinal);
        Assert.Contains("versions:1.20.1", facets, StringComparison.Ordinal);
        Assert.DoesNotContain("categories:", facets, StringComparison.Ordinal);
    }

    [Fact]
    public void facets_无版本无加载器时只有类型()
    {
        Assert.Equal("[[\"project_type:mod\"]]", ModrinthService.BuildSearchFacets(ContentKind.Mod, null, LoaderKind.Vanilla));
        Assert.Equal("[[\"project_type:mod\"]]", ModrinthService.BuildSearchFacets(ContentKind.Mod, "   ", LoaderKind.Vanilla));
    }

    [Fact]
    public void facets_光影的类型段是shader()
    {
        Assert.Contains("project_type:shader", ModrinthService.BuildSearchFacets(ContentKind.ShaderPack, null, LoaderKind.Vanilla), StringComparison.Ordinal);
    }

    [Fact]
    public void 搜索URL_空关键词按下载量排序()
    {
        var url = ModrinthService.BuildSearchUrl(ContentKind.Mod, "  ", null, LoaderKind.Vanilla, 0, 20);

        Assert.Contains("&index=downloads", url, StringComparison.Ordinal);
        Assert.Contains("&offset=0", url, StringComparison.Ordinal);
        Assert.Contains("&limit=20", url, StringComparison.Ordinal);

        var relevant = ModrinthService.BuildSearchUrl(ContentKind.Mod, "sodium", null, LoaderKind.Vanilla, 40, 20);
        Assert.Contains("&index=relevance", relevant, StringComparison.Ordinal);
        Assert.Contains("&offset=40", relevant, StringComparison.Ordinal);
    }

    [Fact]
    public void 搜索URL_limit被夹到上限()
    {
        var url = ModrinthService.BuildSearchUrl(ContentKind.Mod, "a", null, LoaderKind.Vanilla, 0, 500);
        Assert.Contains($"&limit={ModrinthService.MaxPageSize}", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 版本URL_模组按加载器过滤_不过滤时省略loaders()
    {
        var mod = ModrinthService.BuildVersionUrl("AANobbMI", "1.20.1", LoaderKind.Fabric);
        Assert.Contains("game_versions=", mod, StringComparison.Ordinal);
        Assert.Contains("loaders=", mod, StringComparison.Ordinal);
        Assert.Contains("fabric", Uri.UnescapeDataString(mod), StringComparison.Ordinal);

        // 资源包 / 光影包的 loaders 是 minecraft（或 iris/optifine），页面侧统一传 Vanilla 表示不过滤。
        var noFilter = ModrinthService.BuildVersionUrl("50dA9Sha", "1.20.1", LoaderKind.Vanilla);
        Assert.DoesNotContain("loaders=", noFilter, StringComparison.Ordinal);

        var noVersion = ModrinthService.BuildVersionUrl("50dA9Sha", null, LoaderKind.Vanilla);
        Assert.DoesNotContain("?", noVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void 文件名清理_去掉目录穿越与非法字符()
    {
        Assert.Equal("sodium.jar", ModrinthService.SanitizeFileName("sodium.jar"));
        Assert.Equal("sodium-fabric-0.5.13+mc1.20.1.jar", ModrinthService.SanitizeFileName("sodium-fabric-0.5.13+mc1.20.1.jar"));
        Assert.Equal("evil.jar", ModrinthService.SanitizeFileName("../../evil.jar"));
        Assert.Equal("x.jar", ModrinthService.SanitizeFileName("C:\\tmp\\x.jar"));
        Assert.Equal(string.Empty, ModrinthService.SanitizeFileName("   "));
        Assert.Equal(string.Empty, ModrinthService.SanitizeFileName(null));
    }

    [Fact]
    public void 目标路径_同名文件追加序号而不是覆盖()
    {
        using var paths = new CoreAssetsPaths();
        var directory = Path.Combine(paths.RootDirectory, "mods");
        Directory.CreateDirectory(directory);

        var first = ModrinthService.ResolveTargetPath(directory, "sodium.jar");
        Assert.Equal(Path.Combine(directory, "sodium.jar"), first);

        File.WriteAllText(first, "existing");
        var second = ModrinthService.ResolveTargetPath(directory, "sodium.jar");
        Assert.Equal(Path.Combine(directory, "sodium (1).jar"), second);

        File.WriteAllText(second, "existing");
        var third = ModrinthService.ResolveTargetPath(directory, "sodium.jar");
        Assert.Equal(Path.Combine(directory, "sodium (2).jar"), third);
    }

    [Fact]
    public async Task 安装_校验sha1后落盘()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-install");
        var payload = Encoding.UTF8.GetBytes("hello-sodium");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var service = new ModrinthService(new CoreAssetsLog(), paths, new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            Version("sodium.jar", "https://cdn.modrinth.com/fake/sodium.jar", payload.Length, sha1),
            gameDirectory,
            ContentKind.Mod);

        Assert.True(result.Success, result.Error);
        Assert.Equal("sodium.jar", result.FileName);
        Assert.False(result.Renamed);
        Assert.Equal(payload.Length, result.Bytes);

        var installed = Path.Combine(gameDirectory, "mods", "sodium.jar");
        Assert.True(File.Exists(installed));
        Assert.Equal(payload, await File.ReadAllBytesAsync(installed));
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 安装_sha1不匹配时丢弃文件并返回中文错误()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-bad-sha1");
        var payload = Encoding.UTF8.GetBytes("corrupted-content");

        using var service = new ModrinthService(new CoreAssetsLog(), paths, new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            Version("sodium.jar", "https://cdn.modrinth.com/fake/sodium.jar", payload.Length, "0000000000000000000000000000000000000000"),
            gameDirectory,
            ContentKind.Mod);

        Assert.False(result.Success);
        Assert.Contains("SHA1", result.Error ?? string.Empty, StringComparison.Ordinal);

        var mods = Path.Combine(gameDirectory, "mods");
        Assert.Empty(Directory.GetFiles(mods));
    }

    [Fact]
    public async Task 安装_同名已存在时改名落盘()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-rename");
        var mods = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "sodium.jar"), "old");

        var payload = Encoding.UTF8.GetBytes("new-content");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var service = new ModrinthService(new CoreAssetsLog(), paths, new StaticContentHandler(payload));
        var result = await service.InstallAsync(
            Version("sodium.jar", "https://cdn.modrinth.com/fake/sodium.jar", payload.Length, sha1),
            gameDirectory,
            ContentKind.Mod);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Renamed);
        Assert.Equal("sodium (1).jar", result.FileName);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(mods, "sodium.jar")));
    }

    [Fact]
    public async Task 安装_没有下载地址时直接失败()
    {
        using var service = new ModrinthService(new CoreAssetsLog(), null, new StaticContentHandler([]));

        var result = await service.InstallAsync(
            Version("sodium.jar", string.Empty, 0, null),
            Path.GetTempPath(),
            ContentKind.Mod);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task 搜索_网络失败返回空集合并写中文错误()
    {
        using var service = new ModrinthService(new CoreAssetsLog(), null, new FailingHandler());

        var projects = await service.SearchAsync(ContentKind.Mod, "sodium", "1.20.1", LoaderKind.Fabric, 0, 10);

        Assert.Empty(projects);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
        Assert.Contains("Modrinth", service.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 搜索_世界类型不发请求直接返回空()
    {
        using var service = new ModrinthService(new CoreAssetsLog(), null, new FailingHandler());

        Assert.Empty(await service.SearchAsync(ContentKind.World, "x", null, LoaderKind.Vanilla, 0, 10));
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
    }

    [Fact]
    public async Task 搜索_400错误返回空集合并给出状态码()
    {
        using var service = new ModrinthService(new CoreAssetsLog(), null, new StatusHandler(HttpStatusCode.BadRequest));

        Assert.Empty(await service.SearchAsync(ContentKind.Mod, "sodium", null, LoaderKind.Vanilla, 0, 10));
        Assert.Contains("400", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 图标缓存_路径不可用时返回null()
    {
        using var service = new ModrinthService(new CoreAssetsLog(), null, new StaticContentHandler([1, 2, 3]));

        // 没有 ILauncherPaths 就没地方缓存，按约定返回 null 让 UI 用首字母占位。
        Assert.Null(await service.CacheIconAsync("https://cdn.modrinth.com/a.webp"));
    }

    private static ModrinthVersion Version(string fileName, string url, long size, string? sha1) => new()
    {
        Id = "OihdIimA",
        ProjectId = "AANobbMI",
        Name = "Test Version",
        VersionNumber = "1.0.0",
        FileName = fileName,
        DownloadUrl = url,
        FileSize = size,
        Sha1 = sha1,
    };

    /// <summary>返回固定内容的 handler，用于覆盖下载 / 校验 / 落盘路径。</summary>
    private sealed class StaticContentHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StaticContentHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
                RequestMessage = request,
            });
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

    /// <summary>固定返回某个错误状态码。</summary>
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { RequestMessage = request });
    }
}
