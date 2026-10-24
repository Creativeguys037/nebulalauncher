using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

// 安装流程按 SHA1 校验，测试必须按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// CurseForge 客户端的纯逻辑测试：URL 拼装、JSON 解析、
/// SHA1 校验、同名改名与「没有直链」分支。
///
/// 响应结构取自官方文档 <c>GET /v1/mods/search</c> 与 <c>GET /v1/mods/{modId}/files</c> 的示例形状，
/// 嵌成常量让测试不依赖网络；网络分支用自定义 <see cref="HttpMessageHandler"/> 覆盖。
///
/// API key 已内置在 <see cref="CurseForgeService.EmbeddedApiKey"/>，所以测试不再构造 key 配置，
/// 只断言请求头里带的就是那一个常量。
/// </summary>
public class CoreAssets_CurseForgeTests
{
    /// <summary>GET /v1/mods/search 响应片段（jei + fabric-api）。</summary>
    private const string SearchJson = """
    {
      "data": [
        {
          "id": 238222,
          "gameId": 432,
          "name": "Just Enough Items",
          "slug": "jei",
          "summary": "View items and recipes",
          "downloadCount": 123456789,
          "dateModified": "2024-05-01T12:34:56.789Z",
          "allowModDistribution": false,
          "isAvailable": true,
          "authors": [
            { "id": 1, "name": "mezz", "url": "https://www.curseforge.com/members/mezz" },
            { "id": 2, "name": "shedaniel", "url": "https://www.curseforge.com/members/shedaniel" }
          ],
          "logo": {
            "thumbnailUrl": "https://media.forgecdn.net/avatars/thumbnails/238/222/256/256/jei.png",
            "url": "https://media.forgecdn.net/avatars/238/222/256/256/jei.png"
          },
          "categories": [ { "id": 423, "name": "Map and Information", "slug": "map-information", "classId": 6 } ],
          "links": { "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/jei" }
        },
        {
          "id": 306612,
          "gameId": 432,
          "name": "Fabric API",
          "slug": "fabric-api",
          "summary": "Core API for Fabric mods",
          "downloadCount": 100,
          "dateModified": "2023-01-01T00:00:00Z",
          "allowModDistribution": true,
          "isAvailable": true,
          "authors": [ { "id": 3, "name": "modmuss50" } ],
          "logo": {
            "thumbnailUrl": null,
            "url": "https://media.forgecdn.net/avatars/306/612/256/256/fabric.png"
          }
        }
      ],
      "pagination": { "index": 0, "pageSize": 20, "resultCount": 2, "totalCount": 2 }
    }
    """;

    /// <summary>
    /// GET /v1/mods/238222/files 响应片段。data[0] 较旧且有完整直链；
    /// data[1] 较新但 downloadUrl 为 null（作者关闭第三方分发）。
    /// hashes 里先放一条 MD5（algo=2），验证只取 algo=1。
    /// </summary>
    private const string FilesJson = """
    {
      "data": [
        {
          "id": 4001,
          "gameId": 432,
          "modId": 238222,
          "displayName": "jei-1.20.1-forge-15.2.0.27.jar",
          "fileName": "jei-1.20.1-forge-15.2.0.27.jar",
          "releaseType": 1,
          "fileStatus": 4,
          "fileDate": "2023-06-01T10:00:00.000Z",
          "fileLength": 1234,
          "downloadCount": 100,
          "downloadUrl": "https://edge.forgecdn.net/files/4001/jei.jar",
          "gameVersions": ["1.20.1", "1.20.2", "Forge"],
          "hashes": [
            { "value": "5d41402abc4b2a76b9719d911017c592", "algo": 2 },
            { "value": "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", "algo": 1 }
          ],
          "dependencies": [
            { "modId": 306612, "relationType": 3 },
            { "modId": 999, "relationType": 2 }
          ],
          "isAvailable": true
        },
        {
          "id": 4002,
          "gameId": 432,
          "modId": 238222,
          "displayName": "jei-1.20.1-forge-15.3.0.jar",
          "fileName": "jei-1.20.1-forge-15.3.0.jar",
          "releaseType": 2,
          "fileStatus": 4,
          "fileDate": "2024-02-01T10:00:00.000Z",
          "fileLength": 2345,
          "downloadCount": 50,
          "downloadUrl": null,
          "gameVersions": ["1.20.1", "Forge"],
          "hashes": [],
          "dependencies": [],
          "isAvailable": true
        }
      ],
      "pagination": { "index": 0, "pageSize": 50, "resultCount": 2, "totalCount": 2 }
    }
    """;

    /// <summary>
    /// cfwidget 项目详情响应片段（项目链接精确解析链路）。形状按实测：id / title / summary / thumbnail /
    /// downloads.total / members[].username / files[]。三个文件分别是 fabric 1.20.1、
    /// forge 1.20.1、fabric 1.21，用来验证版本与加载器过滤。
    /// </summary>
    private const string WidgetJson = """
    {
      "id": 238222,
      "title": "Just Enough Items (JEI)",
      "summary": "View Items and Recipes",
      "type": "mod",
      "thumbnail": "https://media.forgecdn.net/avatars/thumbnails/238/222/256/256/jei.png",
      "downloads": { "monthly": 1234567, "total": 638217190 },
      "members": [
        { "title": "Owner", "username": "mezz", "id": 17072262 },
        { "title": "Author", "username": "shedaniel", "id": 17072263 }
      ],
      "categories": ["Map and Information", "API and Library"],
      "urls": { "curseforge": "https://www.curseforge.com/minecraft/mc-mods/jei" },
      "files": [
        {
          "id": 9268986,
          "name": "jei-1.20.1-fabric-15.74.0.227.jar",
          "display": "15.74.0.227 for Fabric 1.20.1",
          "type": "beta",
          "version": "1.20.1",
          "filesize": 6928859,
          "versions": ["Client", "1.20.1", "Fabric", "Server"],
          "uploaded_at": "2026-10-20T17:05:00.15Z",
          "url": "https://www.curseforge.com/minecraft/mc-mods/jei/files/9268986"
        },
        {
          "id": 9268985,
          "name": "jei-1.20.1-forge-15.74.0.227.jar",
          "display": "15.74.0.227 for Forge 1.20.1",
          "type": "release",
          "version": "1.20.1",
          "filesize": 6930001,
          "versions": ["Client", "1.20.1", "Forge", "Server"],
          "uploaded_at": "2026-10-20T17:04:56.42Z",
          "url": "https://www.curseforge.com/minecraft/mc-mods/jei/files/9268985"
        },
        {
          "id": 9100000,
          "name": "jei-1.21-fabric-16.0.0.jar",
          "display": "16.0.0 for Fabric 1.21",
          "type": "alpha",
          "version": "1.21",
          "filesize": 5000000,
          "versions": ["Client", "1.21", "Fabric"],
          "uploaded_at": "2025-06-01T00:00:00Z",
          "url": "https://www.curseforge.com/minecraft/mc-mods/jei/files/9100000"
        }
      ]
    }
    """;

    // ---------------- BuildSearchUrl ----------------

    [Fact]
    public void 搜索URL_世界存档不拼地址()
    {
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings());

        // 世界存档没有 classId，URL 拼不出来；key 恒定存在不再影响这里。
        Assert.Equal(string.Empty, service.BuildSearchUrl(ContentKind.World, "jei", "1.20.1", LoaderKind.Forge, 0, 20));
    }

    [Fact]
    public void 搜索URL_含gameId与classId()
    {
        using var service = CreateService();

        var url = service.BuildSearchUrl(ContentKind.Mod, null, null, LoaderKind.Vanilla, 0, 20);

        Assert.Contains("gameId=432&classId=6", url, StringComparison.Ordinal);
        Assert.Contains("&index=0", url, StringComparison.Ordinal);
        Assert.Contains("&pageSize=20", url, StringComparison.Ordinal);
        Assert.Contains("&sortField=2&sortOrder=desc", url, StringComparison.Ordinal);
        Assert.DoesNotContain("searchFilter=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("gameVersion=", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContentKind.ResourcePack, "classId=12")]
    [InlineData(ContentKind.ShaderPack, "classId=6552")]
    public void 搜索URL_资源包与光影用各自classId(ContentKind kind, string expected)
    {
        using var service = CreateService();

        var url = service.BuildSearchUrl(kind, null, null, LoaderKind.Vanilla, 0, 20);

        Assert.Contains(expected, url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, "modLoaderType=1")]
    [InlineData(LoaderKind.Fabric, "modLoaderType=4")]
    [InlineData(LoaderKind.Quilt, "modLoaderType=5")]
    [InlineData(LoaderKind.NeoForge, "modLoaderType=6")]
    public void 搜索URL_模组按加载器映射modLoaderType(LoaderKind loader, string expected)
    {
        using var service = CreateService();

        var url = service.BuildSearchUrl(ContentKind.Mod, null, "1.20.1", loader, 0, 20);

        Assert.Contains(expected, url, StringComparison.Ordinal);
    }

    [Fact]
    public void 搜索URL_资源包不追加modLoaderType()
    {
        using var service = CreateService();

        var url = service.BuildSearchUrl(ContentKind.ResourcePack, null, "1.20.1", LoaderKind.Fabric, 0, 20);

        Assert.Contains("gameVersion=1.20.1", url, StringComparison.Ordinal);
        Assert.DoesNotContain("modLoaderType", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 搜索URL_没有游戏版本时不追加modLoaderType()
    {
        using var service = CreateService();

        var url = service.BuildSearchUrl(ContentKind.Mod, null, null, LoaderKind.Fabric, 0, 20);

        Assert.DoesNotContain("modLoaderType", url, StringComparison.Ordinal);

        // 空白版本与 null 同义：API 要求 modLoaderType 必须和 gameVersion 成对出现。
        var blank = service.BuildSearchUrl(ContentKind.Mod, null, "   ", LoaderKind.Fabric, 0, 20);
        Assert.DoesNotContain("modLoaderType", blank, StringComparison.Ordinal);
    }

    [Fact]
    public void 搜索URL_关键词被URL编码()
    {
        using var service = CreateService();

        var url = service.BuildSearchUrl(ContentKind.Mod, "jei & more", null, LoaderKind.Vanilla, 0, 20);

        Assert.Contains("searchFilter=jei%20%26%20more", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 搜索URL_pageSize被夹到50()
    {
        using var service = CreateService();

        Assert.Contains(
            "pageSize=50",
            service.BuildSearchUrl(ContentKind.Mod, null, null, LoaderKind.Vanilla, 0, 500),
            StringComparison.Ordinal);

        Assert.Contains(
            "pageSize=1",
            service.BuildSearchUrl(ContentKind.Mod, null, null, LoaderKind.Vanilla, 0, 0),
            StringComparison.Ordinal);
    }

    // ---------------- SearchAsync ----------------

    [Fact]
    public async Task 搜索_普通关键词走官方接口并带内置key()
    {
        var handler = new RecordingHandler(SearchJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        // 含空格的普通关键词只可能是搜索意图，走官方 /v1/mods/search。
        await service.SearchAsync(ContentKind.Mod, "just enough items", "1.20.1", LoaderKind.Forge, 0, 20);

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("/v1/mods/search?", handler.Urls[0], StringComparison.Ordinal);
        Assert.Equal(CurseForgeService.EmbeddedApiKey, handler.ApiKeys[0]);
    }

    [Fact]
    public async Task 搜索_解析项目字段并带认证头()
    {
        var handler = new RecordingHandler(SearchJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        var projects = await service.SearchAsync(ContentKind.Mod, "jei", "1.20.1", LoaderKind.Forge, 0, 20);

        Assert.Equal(2, projects.Count);

        var jei = projects[0];
        Assert.Equal(238222, jei.Id);
        Assert.Equal("jei", jei.Slug);
        Assert.Equal("Just Enough Items", jei.Name);
        Assert.Equal("View items and recipes", jei.Summary);
        Assert.Equal("mezz、shedaniel", jei.Authors);
        Assert.Equal(123456789, jei.DownloadCount);
        Assert.Equal(ContentKind.Mod, jei.Kind);
        Assert.False(jei.AllowModDistribution);
        Assert.Equal("https://media.forgecdn.net/avatars/thumbnails/238/222/256/256/jei.png", jei.IconUrl);
        Assert.NotNull(jei.DateModified);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/jei", jei.WebUrl);

        // 第二个项目 thumbnailUrl 为 null，回退到 logo.url。
        Assert.Equal("https://media.forgecdn.net/avatars/306/612/256/256/fabric.png", projects[1].IconUrl);
        Assert.True(projects[1].AllowModDistribution);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(CurseForgeService.EmbeddedApiKey, handler.ApiKeys[0]);
        Assert.Contains("application/json", handler.Accepts[0], StringComparison.Ordinal);
        Assert.Contains("classId=6", handler.Urls[0], StringComparison.Ordinal);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 搜索_403提示APIKey()
    {
        var handler = new RecordingHandler("""{"error":{"code":403}}""", HttpStatusCode.Forbidden);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        var projects = await service.SearchAsync(ContentKind.Mod, "jei", null, LoaderKind.Vanilla, 0, 20);

        Assert.Empty(projects);
        Assert.Contains("API Key", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 搜索_世界类型不发请求()
    {
        var handler = new RecordingHandler(SearchJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        Assert.Empty(await service.SearchAsync(ContentKind.World, "x", null, LoaderKind.Vanilla, 0, 20));
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
        Assert.Equal(0, handler.RequestCount);
    }

    // ---------------- GetFilesAsync ----------------

    [Fact]
    public async Task 文件列表_解析哈希依赖版本并按时间降序()
    {
        var handler = new RecordingHandler(FilesJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        var files = await service.GetFilesAsync(238222, "1.20.1", LoaderKind.Forge);

        Assert.Equal(2, files.Count);

        // 时间更新的在前。
        Assert.Equal(4002, files[0].Id);
        Assert.False(files[0].IsDownloadable);
        Assert.True(files[0].IsBeta);

        var stable = files[1];
        Assert.Equal(4001, stable.Id);
        Assert.Equal(238222, stable.ModId);
        Assert.Equal("jei-1.20.1-forge-15.2.0.27.jar", stable.FileName);
        Assert.True(stable.IsRelease);
        Assert.True(stable.IsDownloadable);
        Assert.Equal(1234, stable.FileLength);

        // hashes 里只有 algo == 1 的那条是 SHA1。
        Assert.Equal("aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", stable.Sha1);

        Assert.Contains("Forge", stable.GameVersions);
        Assert.Contains("1.20.2", stable.GameVersions);

        var required = Assert.Single(stable.Dependencies, dependency => dependency.IsRequired);
        Assert.Equal(306612, required.ModId);
        Assert.Equal(2, stable.Dependencies.Count);

        Assert.Contains("/v1/mods/238222/files?", handler.Urls[0], StringComparison.Ordinal);

        // 文件列表改成翻页取全后，单页请求的上限不再固定成 50（见 CurseForgeService.FilesPageSize）：
        // 这里只钉「请求带了 pageSize 参数且属大页」，页码推进与终止见 CoreMods_CurseForgePagingTests。
        Assert.Contains("pageSize=1000", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("modLoaderType=1", handler.Urls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 文件列表_缺少项目ID时不发请求并写原因()
    {
        var handler = new RecordingHandler(FilesJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        // modId <= 0 时无从拼 URL，必须直接失败而不是发一个 /v1/mods/0/files。
        Assert.Empty(await service.GetFilesAsync(0, null, LoaderKind.Vanilla));
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
        Assert.Equal(0, handler.RequestCount);
    }

    // ---------------- InstallAsync ----------------

    [Fact]
    public async Task 安装_没有直链时失败并引导去网页()
    {
        using var paths = new CoreAssetsPaths();
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new CoreAssetsSettings(),
            new StaticContentHandler([]));

        var result = await service.InstallAsync(
            TestFile("jei.jar", url: null, size: 0, sha1: null),
            Path.Combine(paths.RootDirectory, "game-no-url"),
            ContentKind.Mod);

        Assert.False(result.Success);
        Assert.Contains("网页", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 安装_sha1不匹配时丢弃文件()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-bad-sha1");
        var payload = Encoding.UTF8.GetBytes("corrupted-content");

        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new CoreAssetsSettings(),
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            TestFile("jei.jar", "https://edge.forgecdn.net/files/4001/jei.jar", payload.Length, "0000000000000000000000000000000000000000"),
            gameDirectory,
            ContentKind.Mod);

        Assert.False(result.Success);
        Assert.Contains("SHA1", result.Error ?? string.Empty, StringComparison.Ordinal);

        // .part 必须已经删掉，不能残留半截文件。
        var mods = Path.Combine(gameDirectory, "mods");
        Assert.Empty(Directory.GetFiles(mods));
    }

    [Fact]
    public async Task 安装_成功后落在mods目录()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-install");
        var payload = Encoding.UTF8.GetBytes("hello-jei");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new CoreAssetsSettings(),
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            TestFile("jei.jar", "https://edge.forgecdn.net/files/4001/jei.jar", payload.Length, sha1),
            gameDirectory,
            ContentKind.Mod);

        Assert.True(result.Success, result.Error);
        Assert.Equal("jei.jar", result.FileName);
        Assert.False(result.Renamed);
        Assert.Equal(payload.Length, result.Bytes);

        var installed = Path.Combine(gameDirectory, "mods", "jei.jar");
        Assert.True(File.Exists(installed));
        Assert.Equal(payload, await File.ReadAllBytesAsync(installed));
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 安装_同名已存在时改名落盘()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-rename");
        var mods = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "jei.jar"), "old");

        var payload = Encoding.UTF8.GetBytes("new-content");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new CoreAssetsSettings(),
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            TestFile("jei.jar", "https://edge.forgecdn.net/files/4001/jei.jar", payload.Length, sha1),
            gameDirectory,
            ContentKind.Mod);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Renamed);
        Assert.Equal("jei (1).jar", result.FileName);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(mods, "jei.jar")));
    }

    [Fact]
    public async Task 安装_资源包落在resourcepacks目录()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-rp");
        var payload = Encoding.UTF8.GetBytes("pack-bytes");

        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new CoreAssetsSettings(),
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            TestFile("pack.zip", "https://edge.forgecdn.net/files/9/pack.zip", payload.Length, sha1: null),
            gameDirectory,
            ContentKind.ResourcePack);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(gameDirectory, "resourcepacks", "pack.zip")));
    }

    // ---------------- 其它 ----------------

    [Fact]
    public async Task 图标缓存_没有路径时返回null()
    {
        using var service = CreateService();

        // 没有 ILauncherPaths 就没地方缓存，按约定返回 null 让 UI 用占位图。
        Assert.Null(await service.CacheIconAsync("https://media.forgecdn.net/avatars/238/222/256/256/jei.png"));
    }

    [Fact]
    public void 镜像地址覆盖后仍拼官方路径()
    {
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(baseUrl: "https://mirror.example.com/curseforge/"));

        // 末尾斜杠必须剥掉，否则会拼出 //v1/mods/search。
        var url = service.BuildSearchUrl(ContentKind.Mod, null, null, LoaderKind.Vanilla, 0, 20);
        Assert.StartsWith("https://mirror.example.com/curseforge/v1/mods/search?", url, StringComparison.Ordinal);
        Assert.DoesNotContain("//v1", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 解析搜索_缺少data时返回空集合()
    {
        Assert.Empty(CurseForgeService.ParseSearch("""{"pagination":{"index":0}}""", ContentKind.Mod));
        Assert.Empty(CurseForgeService.ParseFiles("""{"pagination":{"index":0}}"""));
    }

    // ---------------- 项目链接精确解析（cfwidget + forgecdn） ----------------

    [Fact]
    public void Slug解析_裸slug直接返回()
    {
        Assert.Equal("jei", CurseForgeSlugParser.Parse("jei"));
        Assert.Equal("jei", CurseForgeSlugParser.Parse("  jei  "));
        Assert.Equal("sodium-extra", CurseForgeSlugParser.Parse("sodium-extra"));
    }

    [Fact]
    public void Slug解析_项目链接取项目段后的slug()
    {
        Assert.Equal("jei", CurseForgeSlugParser.Parse("https://www.curseforge.com/minecraft/mc-mods/jei"));
        Assert.Equal("jei", CurseForgeSlugParser.Parse("https://www.curseforge.com/minecraft/mc-mods/jei/"));
    }

    [Fact]
    public void Slug解析_带文件路径的链接忽略files尾部()
    {
        Assert.Equal(
            "jei",
            CurseForgeSlugParser.Parse("https://www.curseforge.com/minecraft/mc-mods/jei/files/9268986"));
    }

    [Fact]
    public void Slug解析_资源包与光影用各自项目段()
    {
        Assert.Equal(
            "faithful-32x",
            CurseForgeSlugParser.Parse("https://www.curseforge.com/minecraft/texture-packs/faithful-32x"));

        Assert.Equal(
            "complementary",
            CurseForgeSlugParser.Parse("https://www.curseforge.com/minecraft/shaders/complementary"));
    }

    [Theory]
    [InlineData("jei extra")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("中文项目")]
    [InlineData("https://www.curseforge.com/minecraft/mc-mods/")]
    public void Slug解析_无法识别时返回null(string input)
    {
        Assert.Null(CurseForgeSlugParser.Parse(input));
    }

    [Fact]
    public void Slug解析_null返回null()
    {
        Assert.Null(CurseForgeSlugParser.Parse(null));
    }

    [Fact]
    public void 免key下载地址_按fileId推导()
    {
        Assert.Equal(
            "https://mediafilez.forgecdn.net/files/9268/986/jei-1.20.1-fabric-15.74.0.227.jar",
            CurseForgeCdn.BuildUrl(9268986, "jei-1.20.1-fabric-15.74.0.227.jar"));
    }

    [Fact]
    public void 免key下载地址_前缀补零到四位且文件名编码()
    {
        Assert.Equal(
            "https://mediafilez.forgecdn.net/files/0000/123/a.jar",
            CurseForgeCdn.BuildUrl(123, "a.jar"));

        // 文件名里的 + 必须编码，否则 CDN 会当成空格或直接 404。
        Assert.Equal(
            "https://mediaforge.cdn/files/0000/123/a%2Bb.jar",
            CurseForgeCdn.BuildUrl("https://mediaforge.cdn/", 123, "a+b.jar"));

        // 备用域名只在主域名解析不出来时才追加，非 forgecdn 地址原样返回一个。
        Assert.Single(CurseForgeCdn.Candidates("https://example.com/a.jar"));
        Assert.Equal(2, CurseForgeCdn.Candidates(
            "https://mediafilez.forgecdn.net/files/9268/986/jei.jar").Count);
    }

    [Fact]
    public async Task 免key项目_解析字段并按版本加载器过滤()
    {
        var handler = new RecordingHandler(WidgetJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        var project = await service.GetProjectBySlugAsync(ContentKind.Mod, "jei", "1.20.1", LoaderKind.Fabric);

        Assert.NotNull(project);
        Assert.Null(service.LastError);

        Assert.Equal(238222, project!.Id);
        Assert.Equal("jei", project.Slug);
        Assert.Equal("Just Enough Items (JEI)", project.Name);
        Assert.Equal("View Items and Recipes", project.Summary);
        Assert.Equal("mezz、shedaniel", project.Authors);
        Assert.Equal(638217190, project.DownloadCount);
        Assert.Equal("https://media.forgecdn.net/avatars/thumbnails/238/222/256/256/jei.png", project.IconUrl);
        Assert.Equal(CurseForgeSource.CfWidget, project.Source);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/jei", project.WebUrl);

        // 1.20.1 + Fabric 过滤后只剩 fabric 那个文件。
        var file = Assert.Single(project.Files);
        Assert.Equal(9268986, file.Id);
        Assert.Equal("jei-1.20.1-fabric-15.74.0.227.jar", file.FileName);
        Assert.Equal("15.74.0.227 for Fabric 1.20.1", file.DisplayName);
        Assert.Equal(6928859, file.FileLength);
        Assert.Equal(2, file.ReleaseType);
        Assert.True(file.IsDownloadable);
        Assert.False(file.Sha1Available);
        Assert.Equal(CurseForgeSource.CfWidget, file.Source);
        Assert.Contains("Fabric", file.GameVersions);
        Assert.Contains("1.20.1", file.GameVersions);
        Assert.Equal(
            "https://mediafilez.forgecdn.net/files/9268/986/jei-1.20.1-fabric-15.74.0.227.jar",
            file.DownloadUrl);

        // 免 key 接口不能带 x-api-key。
        Assert.Equal(1, handler.RequestCount);
        Assert.Null(handler.ApiKeys[0]);
        Assert.Contains("/minecraft/mc-mods/jei", handler.Urls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 免key项目_链接形式输入也能查()
    {
        var handler = new RecordingHandler(WidgetJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        var project = await service.GetProjectBySlugAsync(
            ContentKind.Mod,
            "https://www.curseforge.com/minecraft/mc-mods/jei/files/9268986",
            null,
            LoaderKind.Vanilla);

        Assert.NotNull(project);
        Assert.Equal(3, project!.Files.Count);
        Assert.Equal(9268986, project.Files[0].Id);
    }

    [Fact]
    public async Task 搜索_项目链接走cfwidget精确解析且不带key()
    {
        var handler = new RecordingHandler(WidgetJson);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        // 官方搜索会把整条 URL 当关键词，所以链接必须走 cfwidget 的 slug 精确解析。
        var projects = await service.SearchAsync(
            ContentKind.Mod,
            "https://www.curseforge.com/minecraft/mc-mods/jei",
            "1.20.1",
            LoaderKind.Forge,
            0,
            20);

        Assert.Single(projects);
        Assert.Equal("jei", projects[0].Slug);
        Assert.Null(service.LastError);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("/minecraft/mc-mods/jei", handler.Urls[0], StringComparison.Ordinal);

        // cfwidget 不能拿到内置 key。
        Assert.Null(handler.ApiKeys[0]);

        // Forge 过滤后只剩 forge 文件。
        var file = Assert.Single(projects[0].Files);
        Assert.Equal(9268985, file.Id);
    }

    [Fact]
    public async Task 免key项目_404时返回null并提示未找到()
    {
        var handler = new RecordingHandler("""{"error":"not found"}""", HttpStatusCode.NotFound);
        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths: null,
            settings: new CoreAssetsSettings(),
            handler: handler);

        var project = await service.GetProjectBySlugAsync(ContentKind.Mod, "no-such-mod", null, LoaderKind.Vanilla);

        Assert.Null(project);
        Assert.Contains("未找到", service.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("no-such-mod", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 免key项目_安装时跳过sha1校验()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-no-key");
        var payload = Encoding.UTF8.GetBytes("widget-payload");

        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new CoreAssetsSettings(),
            new StaticContentHandler(payload));

        var fileName = "jei-1.20.1-fabric-15.74.0.227.jar";

        var file = new CurseForgeFile
        {
            Id = 9268986,
            ModId = 238222,
            FileName = fileName,
            DownloadUrl = CurseForgeCdn.BuildUrl(9268986, fileName),
            FileLength = payload.Length,
            Sha1 = null,
            Source = CurseForgeSource.CfWidget,
        };

        var result = await service.InstallAsync(file, gameDirectory, ContentKind.Mod);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(gameDirectory, "mods", fileName)));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(gameDirectory, "mods", fileName)));
    }

    private static CurseForgeService CreateService() => new(
        new CoreAssetsLog(),
        paths: null,
        settings: new CoreAssetsSettings(),
        handler: new RecordingHandler(SearchJson));

    private static CurseForgeFile TestFile(string fileName, string? url, long size, string? sha1) => new()
    {
        Id = 4001,
        ModId = 238222,
        FileName = fileName,
        DisplayName = fileName,
        DownloadUrl = url,
        FileLength = size,
        Sha1 = sha1,
        ReleaseType = 1,
        FileDate = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero),
        GameVersions = ["1.20.1", "Forge"],
    };

    /// <summary>测试用设置服务：只暴露一份可写的内存快照。</summary>
    private sealed class CoreAssetsSettings : ISettingsService
    {
        public CoreAssetsSettings(string? baseUrl = null)
        {
            Current = new AppSettings { CurseForgeBaseUrl = baseUrl };
        }

        public AppSettings Current { get; }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// 记录请求头与 URL 并返回固定 JSON。请求对象在服务里被 using 释放，
    /// 所以这里把要断言的字段在 SendAsync 内当场抄出来。
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public List<string> Urls { get; } = [];

        public List<string?> ApiKeys { get; } = [];

        public List<string> Accepts { get; } = [];

        public int RequestCount => Urls.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (Urls)
            {
                Urls.Add(request.RequestUri?.ToString() ?? string.Empty);
                ApiKeys.Add(request.Headers.TryGetValues("x-api-key", out var keys) ? string.Join(",", keys) : null);
                Accepts.Add(request.Headers.TryGetValues("Accept", out var accepts) ? string.Join(",", accepts) : string.Empty);
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

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
}
