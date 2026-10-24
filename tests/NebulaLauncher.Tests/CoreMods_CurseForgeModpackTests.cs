using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// CurseForge 整合包通道：独立入口（不给 <see cref="ContentKind"/> 加 ModPack 值）。
///
/// 覆盖三件事：URL 里必须出现 <c>classId=4471</c>、必须不出现 <c>gameVersion</c> /
/// <c>modLoaderType</c>；解析出的条目带 <see cref="CurseForgeProject.IsModpack"/>；
/// cfwidget 那条链路走 <c>/minecraft/modpacks/{slug}</c> 且文件不被版本 / 加载器过滤。
///
/// 所有响应都嵌成常量，网络分支用自定义 <see cref="HttpMessageHandler"/> 覆盖，不联网。
/// </summary>
public class CoreMods_CurseForgeModpackTests
{
    private const long ModpackId = 915497;

    /// <summary><c>GET /v1/mods/search?classId=4471</c> 响应片段：一条整合包。</summary>
    private const string ModpackSearchJson = """
    {
      "data": [
        {
          "id": 915497,
          "gameId": 432,
          "name": "Better MC [FORGE] BMC4",
          "slug": "better-mc-forge-bmc4",
          "summary": "The modpack that keeps on giving",
          "downloadCount": 12345678,
          "dateModified": "2026-09-01T10:00:00.000Z",
          "classId": 4471,
          "allowModDistribution": true,
          "authors": [ { "id": 1, "name": "Luna Pixel Studios" } ],
          "logo": {
            "thumbnailUrl": "https://media.forgecdn.net/avatars/thumbnails/915/497/256/256/bmc4.png"
          }
        }
      ],
      "pagination": { "index": 0, "pageSize": 20, "resultCount": 1, "totalCount": 1 }
    }
    """;

    /// <summary>
    /// <c>GET /v1/mods/915497/files</c> 响应片段：data[0] 较旧（1.20.1 Forge），
    /// data[1] 较新（1.21.1 Fabric）。整合包文件列表本来就该同时包含两种构建。
    /// </summary>
    private const string ModpackFilesJson = """
    {
      "data": [
        {
          "id": 7001,
          "gameId": 432,
          "modId": 915497,
          "displayName": "BMC4 v30",
          "fileName": "BMC4-v30.zip",
          "releaseType": 1,
          "fileDate": "2024-01-01T00:00:00.000Z",
          "fileLength": 2048,
          "downloadUrl": "https://edge.forgecdn.net/files/7001/BMC4-v30.zip",
          "gameVersions": ["1.20.1", "Forge"]
        },
        {
          "id": 7002,
          "gameId": 432,
          "modId": 915497,
          "displayName": "BMC4 v32",
          "fileName": "BMC4-v32.zip",
          "releaseType": 1,
          "fileDate": "2025-05-01T00:00:00.000Z",
          "fileLength": 4096,
          "downloadUrl": "https://edge.forgecdn.net/files/7002/BMC4-v32.zip",
          "gameVersions": ["1.21.1", "Fabric"]
        }
      ],
      "pagination": { "index": 0, "pageSize": 50, "resultCount": 2, "totalCount": 2 }
    }
    """;

    /// <summary>
    /// cfwidget 的整合包详情：三个文件分别属于 Forge 1.20.1、Forge 1.19.2、Fabric 1.21.1。
    /// 与模组不同，这里三个文件都必须留在 <c>Files</c> 里——整合包不按版本 / 加载器过滤。
    /// </summary>
    private const string ModpackWidgetJson = """
    {
      "id": 915497,
      "title": "Better MC [FORGE] BMC4",
      "summary": "The modpack that keeps on giving",
      "type": "modpack",
      "thumbnail": "https://media.forgecdn.net/avatars/thumbnails/915/497/256/256/bmc4.png",
      "downloads": { "monthly": 123456, "total": 45678901 },
      "members": [
        { "title": "Owner", "username": "Luna Pixel Studios", "id": 11 },
        { "title": "Author", "username": "shiroroku", "id": 12 }
      ],
      "urls": { "curseforge": "https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4" },
      "files": [
        {
          "id": 6000001,
          "name": "BMC4-v30.zip",
          "display": "BMC4 v30",
          "type": "release",
          "version": "1.20.1",
          "filesize": 1024,
          "versions": ["Client", "1.20.1", "Forge", "Server"],
          "uploaded_at": "2024-01-01T00:00:00Z",
          "url": "https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4/files/6000001"
        },
        {
          "id": 6000003,
          "name": "BMC4-v32.zip",
          "display": "BMC4 v32",
          "type": "beta",
          "version": "1.21.1",
          "filesize": 3072,
          "versions": ["Client", "1.21.1", "Fabric", "Server"],
          "uploaded_at": "2025-05-01T00:00:00Z",
          "url": "https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4/files/6000003"
        },
        {
          "id": 6000002,
          "name": "BMC4-v31.zip",
          "display": "BMC4 v31",
          "type": "release",
          "version": "1.19.2",
          "filesize": 2048,
          "versions": ["Client", "1.19.2", "Forge", "Server"],
          "uploaded_at": "2024-06-01T00:00:00Z",
          "url": "https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4/files/6000002"
        }
      ]
    }
    """;

    /// <summary>
    /// 带 <c>latestFiles[]</c> 的搜索响应：<c>gameVersions</c> 里混着 MC 版本、加载器与
    /// Client / Server 标签，解析后只该留下「标准 MC 版本」与加载器（PCL2 ResourceProject.vb:179-189）。
    /// </summary>
    private const string LatestFilesSearchJson = """
    {
      "data": [
        {
          "id": 238222,
          "slug": "jei",
          "name": "Just Enough Items",
          "latestFiles": [
            { "id": 1, "gameVersions": ["1.20.1", "1.21", "Forge", "Client", "Server"] },
            { "id": 2, "gameVersions": ["1.20.1", "NeoForge", "24w14a"] }
          ]
        }
      ]
    }
    """;

    /// <summary>
    /// 只有 <c>latestFilesIndexes[]</c> 的搜索响应（部分项目与镜像不给 latestFiles）。
    /// modLoader：1=Forge、4=Fabric、0=Any（不产生加载器项）。
    /// </summary>
    private const string LatestFilesIndexesSearchJson = """
    {
      "data": [
        {
          "id": 238222,
          "slug": "jei",
          "name": "Just Enough Items",
          "latestFilesIndexes": [
            { "gameVersion": "1.20.1", "fileId": 11, "modLoader": 1 },
            { "gameVersion": "1.21", "fileId": 12, "modLoader": 4 },
            { "gameVersion": "Client", "fileId": 13, "modLoader": 0 }
          ]
        }
      ]
    }
    """;

    // ---------------- BuildModpackSearchUrl ----------------

    [Fact]
    public void 整合包搜索URL_固定classId4471且不带版本加载器()
    {
        using var service = CreateService(new RecordingHandler(ModpackSearchJson));

        var url = service.BuildModpackSearchUrl(null, 0, 20);

        Assert.Equal(CurseForgeService.ClassIdModpacks, 4471);
        Assert.Contains("gameId=432&classId=4471", url, StringComparison.Ordinal);
        Assert.Contains("&index=0", url, StringComparison.Ordinal);
        Assert.Contains("&pageSize=20", url, StringComparison.Ordinal);
        Assert.Contains("&sortField=2&sortOrder=desc", url, StringComparison.Ordinal);

        // 整合包不按游戏版本 / 加载器筛，这两个参数一个都不能出现。
        Assert.DoesNotContain("gameVersion=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("modLoaderType=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("searchFilter=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 整合包搜索URL_搜索词被URL编码()
    {
        using var service = CreateService(new RecordingHandler(ModpackSearchJson));

        var url = service.BuildModpackSearchUrl("bmc & more", 0, 20);

        Assert.Contains("searchFilter=bmc%20%26%20more", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 整合包搜索URL_pageSize被夹到50且index不为负()
    {
        using var service = CreateService(new RecordingHandler(ModpackSearchJson));

        Assert.Contains(
            "pageSize=50",
            service.BuildModpackSearchUrl("bmc", 0, 500),
            StringComparison.Ordinal);

        Assert.Contains(
            "pageSize=1",
            service.BuildModpackSearchUrl("bmc", 0, 0),
            StringComparison.Ordinal);

        Assert.Contains(
            "index=0",
            service.BuildModpackSearchUrl("bmc", -5, 20),
            StringComparison.Ordinal);

        // 空白搜索词与 null 同义：不该拼出 searchFilter= 空值。
        Assert.DoesNotContain(
            "searchFilter=",
            service.BuildModpackSearchUrl("   ", 0, 20),
            StringComparison.Ordinal);
    }

    // ---------------- SearchModpacksAsync ----------------

    [Fact]
    public async Task 整合包搜索_解析出条目并标记IsModpack()
    {
        var handler = new RecordingHandler(ModpackSearchJson);
        using var service = CreateService(handler);

        var projects = await service.SearchModpacksAsync("better mc", 0, 20);

        var project = Assert.Single(projects);
        Assert.True(project.IsModpack);
        Assert.Equal(ModpackId, project.Id);
        Assert.Equal("better-mc-forge-bmc4", project.Slug);
        Assert.Equal("Better MC [FORGE] BMC4", project.Name);
        Assert.Equal("Luna Pixel Studios", project.Authors);
        Assert.Equal(12345678, project.DownloadCount);
        Assert.Equal(
            "https://media.forgecdn.net/avatars/thumbnails/915/497/256/256/bmc4.png",
            project.IconUrl);

        // 整合包没有对应的 ContentKind：Type 仍是模组，靠 IsModpack 分流。
        Assert.Equal(ContentKind.Mod, project.Kind);
        Assert.Equal("https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4", project.WebUrl);

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("/v1/mods/search?", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("classId=4471", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("searchFilter=better%20mc", handler.Urls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("gameVersion=", handler.Urls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("modLoaderType=", handler.Urls[0], StringComparison.Ordinal);

        // 官方接口必须带内置 key。
        Assert.Equal(CurseForgeService.EmbeddedApiKey, handler.ApiKeys[0]);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 整合包搜索_项目链接走cfwidget的modpacks段()
    {
        var handler = new RecordingHandler(ModpackWidgetJson);
        using var service = CreateService(handler);

        // 官方搜索会把整条 URL 当关键词，所以链接必须走 cfwidget 精确解析。
        var projects = await service.SearchModpacksAsync(
            "https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4",
            0,
            20);

        var project = Assert.Single(projects);
        Assert.True(project.IsModpack);
        Assert.Equal(3, project.Files.Count);
        Assert.Null(service.LastError);

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("/minecraft/modpacks/better-mc-forge-bmc4", handler.Urls[0], StringComparison.Ordinal);

        // cfwidget 不能拿到内置 key。
        Assert.Null(handler.ApiKeys[0]);
    }

    [Fact]
    public async Task 整合包搜索_429时返回空并提示稍后再试()
    {
        // 429 会按既有策略重试一次，所以请求计数是 2。
        var handler = new RecordingHandler("""{"error":"rate limited"}""", HttpStatusCode.TooManyRequests);
        using var service = CreateService(handler);

        var projects = await service.SearchModpacksAsync("better mc", 0, 20);

        Assert.Empty(projects);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("请求过于频繁", service.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("429", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 整合包搜索_503时返回空并提示服务不可用()
    {
        var handler = new RecordingHandler("""{"error":"maintenance"}""", HttpStatusCode.ServiceUnavailable);
        using var service = CreateService(handler);

        var projects = await service.SearchModpacksAsync("better mc", 0, 20);

        Assert.Empty(projects);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("暂时不可用", service.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("503", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    // ---------------- GetModpackFilesAsync ----------------

    [Fact]
    public async Task 整合包文件列表_URL不带版本与加载器并按时间降序()
    {
        var handler = new RecordingHandler(ModpackFilesJson);
        using var service = CreateService(handler);

        var files = await service.GetModpackFilesAsync(ModpackId);

        Assert.Equal(2, files.Count);

        // 较新的文件在前（响应里是旧的在先，解析层要重排）。
        Assert.Equal(7002, files[0].Id);
        Assert.Equal("BMC4-v32.zip", files[0].FileName);
        Assert.Equal(7001, files[1].Id);

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("/v1/mods/915497/files?", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("index=0", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("pageSize=1000", handler.Urls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("gameVersion=", handler.Urls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("modLoaderType=", handler.Urls[0], StringComparison.Ordinal);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 整合包文件列表_分页取全()
    {
        // 137 条、服务端每页只给 50 → 3 次请求、137 条结果；与模组入口共用同一份翻页实现。
        var handler = new ModpackPagingHandler(totalCount: 137, serverCap: 50);
        using var service = CreateService(handler);

        var files = await service.GetModpackFilesAsync(ModpackId);

        Assert.Equal(137, files.Count);
        Assert.Equal(3, handler.Urls.Count);
        Assert.Contains("index=0", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("index=50", handler.Urls[1], StringComparison.Ordinal);
        Assert.Contains("index=100", handler.Urls[2], StringComparison.Ordinal);

        Assert.Equal(137, files.Select(file => file.Id).Distinct().Count());

        // 拼接后仍按发布时间全局降序。
        for (var i = 1; i < files.Count; i++)
        {
            var previous = files[i - 1].FileDate ?? DateTimeOffset.MinValue;
            var current = files[i].FileDate ?? DateTimeOffset.MinValue;

            Assert.True(previous >= current, $"第 {i} 条比前一条新：{previous:O} < {current:O}");
        }
    }

    // ---------------- GetModpackBySlugAsync ----------------

    [Fact]
    public async Task 整合包项目链接_请求cfwidget的modpacks路径并不过滤文件()
    {
        var handler = new RecordingHandler(ModpackWidgetJson);
        using var service = CreateService(handler);

        var project = await service.GetModpackBySlugAsync("better-mc-forge-bmc4");

        Assert.NotNull(project);
        Assert.True(project!.IsModpack);
        Assert.Equal(ModpackId, project.Id);
        Assert.Equal("better-mc-forge-bmc4", project.Slug);
        Assert.Equal("Better MC [FORGE] BMC4", project.Name);
        Assert.Equal("Luna Pixel Studios、shiroroku", project.Authors);
        Assert.Equal(45678901, project.DownloadCount);
        Assert.Equal(CurseForgeSource.CfWidget, project.Source);
        Assert.Equal("https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4", project.WebUrl);

        // 三个文件属于不同的游戏版本 / 加载器，整合包一个都不能裁掉，只按文件 ID 降序。
        Assert.Equal(3, project.Files.Count);
        Assert.Equal(
            new long[] { 6000003, 6000002, 6000001 },
            project.Files.Select(file => file.Id).ToArray());
        Assert.Contains("Fabric", project.Files[0].GameVersions);
        Assert.Contains("Forge", project.Files[1].GameVersions);

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("/minecraft/modpacks/better-mc-forge-bmc4", handler.Urls[0], StringComparison.Ordinal);
        Assert.Null(handler.ApiKeys[0]);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 整合包项目链接_404时返回null并提示未找到()
    {
        var handler = new RecordingHandler("""{"error":"not found"}""", HttpStatusCode.NotFound);
        using var service = CreateService(handler);

        var project = await service.GetModpackBySlugAsync("no-such-modpack");

        Assert.Null(project);
        Assert.Contains("未找到", service.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("no-such-modpack", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 整合包项目链接_无法识别时提示整合包链接示例()
    {
        var handler = new RecordingHandler(ModpackWidgetJson);
        using var service = CreateService(handler);

        // 含空格 / 中文的输入不是 slug，也不该发请求。
        Assert.Null(await service.GetModpackBySlugAsync("中文整合包"));
        Assert.Contains("modpacks/", service.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    // ---------------- WebUrl ----------------

    [Fact]
    public void 整合包网页地址_用modpacks路径()
    {
        var project = new CurseForgeProject
        {
            Id = ModpackId,
            Slug = "better-mc-forge-bmc4",
            Name = "Better MC [FORGE] BMC4",
            Kind = ContentKind.Mod,
            IsModpack = true,
        };

        Assert.Equal("https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4", project.WebUrl);
    }

    [Fact]
    public void 普通项目网页地址_不受整合包标志影响()
    {
        // 默认 IsModpack = false，三个既有分支必须保持原样。
        Assert.Equal(
            "https://www.curseforge.com/minecraft/mc-mods/jei",
            new CurseForgeProject { Id = 1, Slug = "jei", Name = "JEI", Kind = ContentKind.Mod }.WebUrl);

        Assert.Equal(
            "https://www.curseforge.com/minecraft/texture-packs/faithful-32x",
            new CurseForgeProject
            {
                Id = 2,
                Slug = "faithful-32x",
                Name = "Faithful",
                Kind = ContentKind.ResourcePack,
            }.WebUrl);

        Assert.Equal(
            "https://www.curseforge.com/minecraft/shaders/complementary",
            new CurseForgeProject
            {
                Id = 3,
                Slug = "complementary",
                Name = "Complementary",
                Kind = ContentKind.ShaderPack,
            }.WebUrl);
    }

    [Fact]
    public void 解析搜索_默认不是整合包()
    {
        // 既有调用点不传 isModpack，回填结果必须是 false，否则模组条目会被当成整合包。
        var projects = CurseForgeService.ParseSearch(ModpackSearchJson, ContentKind.Mod);

        var project = Assert.Single(projects);
        Assert.False(project.IsModpack);
        Assert.Equal(ContentKind.Mod, project.Kind);

        // 这条响应没有 latestFiles / latestFilesIndexes，版本与加载器集合保持为空（既有行为不变）。
        Assert.Empty(project.GameVersions);
        Assert.Empty(project.Loaders);
    }

    // ---------------- 搜索解析：版本与加载器（跨站去重口径） ----------------

    [Fact]
    public void 搜索解析_latestFiles里只留标准MC版本与加载器()
    {
        var project = Assert.Single(CurseForgeService.ParseSearch(LatestFilesSearchJson, ContentKind.Mod));

        // Client / Server / Forge 这些标签不能进版本集合，否则两站的版本集合永远不相等。
        Assert.Equal("1.20.1,1.21,24w14a", string.Join(",", project.GameVersions));
        Assert.Equal("forge,neoforge", string.Join(",", project.Loaders));
        Assert.DoesNotContain("Forge", project.GameVersions);
        Assert.DoesNotContain("Client", project.GameVersions);
    }

    [Fact]
    public void 搜索解析_只有latestFilesIndexes时仍能得到版本与加载器()
    {
        var project = Assert.Single(CurseForgeService.ParseSearch(LatestFilesIndexesSearchJson, ContentKind.Mod));

        // modLoader：1=Forge、4=Fabric；0=Any 不产生加载器项，"Client" 不是 MC 版本被丢掉。
        Assert.Equal("1.20.1,1.21", string.Join(",", project.GameVersions));
        Assert.Equal("forge,fabric", string.Join(",", project.Loaders));
    }

    [Fact]
    public void 搜索解析_latestFiles为空数组时退回latestFilesIndexes()
    {
        const string json = """
        {
          "data": [
            {
              "id": 1,
              "slug": "x",
              "name": "X",
              "latestFiles": [],
              "latestFilesIndexes": [ { "gameVersion": "1.20.1", "fileId": 1, "modLoader": 4 } ]
            }
          ]
        }
        """;

        var project = Assert.Single(CurseForgeService.ParseSearch(json, ContentKind.Mod));

        Assert.Equal("1.20.1", string.Join(",", project.GameVersions));
        Assert.Equal("fabric", string.Join(",", project.Loaders));
    }

    [Fact]
    public void 搜索解析_同一项目的重复版本与加载器只留一份()
    {
        const string json = """
        {
          "data": [
            {
              "id": 1,
              "slug": "x",
              "name": "X",
              "latestFiles": [
                { "id": 1, "gameVersions": ["1.20.1", "Fabric"] },
                { "id": 2, "gameVersions": ["1.20.1", "Fabric"] }
              ]
            }
          ]
        }
        """;

        var project = Assert.Single(CurseForgeService.ParseSearch(json, ContentKind.Mod));

        Assert.Equal("1.20.1", string.Join(",", project.GameVersions));
        Assert.Equal("fabric", string.Join(",", project.Loaders));
    }

    // ==================== 辅助 ====================

    private static CurseForgeService CreateService(HttpMessageHandler handler) =>
        new(new SilentLog(), paths: null, settings: new ModpackSettings(), handler: handler);

    /// <summary>只内存持有的设置服务：API key 已内置，这里只留一个空快照。</summary>
    private sealed class ModpackSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>不落盘、不输出的日志替身。</summary>
    private sealed class SilentLog : IAppLog
    {
        public string LogFilePath => "memory://test";

        public void Debug(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message, Exception? ex = null)
        {
        }

        public void Error(string message, Exception? ex = null)
        {
        }
    }

    /// <summary>
    /// 记录请求 URL 与是否有 key 并返回固定 JSON。
    ///
    /// 记 <see cref="Uri.AbsoluteUri"/> 而不是 <c>Uri.ToString()</c>：后者返回给人看的形态，
    /// 会把 <c>%20</c> 之类还原成字面字符，于是「参数是否被 URL 编码」这条断言永远看不到转义。
    /// 请求对象在服务里被 using 释放，所以这里把要断言的字段在 SendAsync 内当场抄出来。
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

        public int RequestCount => Urls.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (Urls)
            {
                Urls.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
                ApiKeys.Add(request.Headers.TryGetValues("x-api-key", out var keys) ? string.Join(",", keys) : null);
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    /// <summary>
    /// 假 CF 文件列表接口：服务端每页上限可配，逼着被测实现真的翻页
    /// （复用 <c>CoreMods_CurseForgePagingTests</c> 的判据思路，但只保留整合包需要的部分）。
    /// </summary>
    private sealed class ModpackPagingHandler : HttpMessageHandler
    {
        private readonly long _totalCount;
        private readonly int _serverCap;

        public ModpackPagingHandler(long totalCount, int serverCap)
        {
            _totalCount = totalCount;
            _serverCap = serverCap;
        }

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri?.ToString() ?? string.Empty;

            lock (Urls)
            {
                Urls.Add(url);
            }

            var index = QueryInt(url, "index") ?? 0;
            var pageSize = QueryInt(url, "pageSize") ?? 50;
            var take = Math.Min(pageSize, _serverCap);
            var count = (int)Math.Max(0, Math.Min(take, _totalCount - index));

            var files = new List<object>(count);

            for (var i = 0; i < count; i++)
            {
                // Id 从 1 起：真实 fileId 不会为 0，解析层会把 id <= 0 当坏行丢掉。
                files.Add(FileNode(index + i + 1));
            }

            object body = new
            {
                data = files,
                pagination = new
                {
                    index,
                    pageSize = count,
                    resultCount = count,
                    totalCount = _totalCount,
                },
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>Id 越大越新，便于验证「拼接后仍按时间降序」。</summary>
        private static object FileNode(long id) => new
        {
            id,
            modId = ModpackId,
            fileName = $"BMC4-{id}.zip",
            displayName = $"BMC4 {id}",
            releaseType = 1,
            fileDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMinutes(id)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            fileLength = 2048 + id,
            downloadUrl = $"https://edge.forgecdn.net/files/{id}/BMC4-{id}.zip",
            gameVersions = new[] { "1.20.1", "Forge" },
        };

        private static int? QueryInt(string url, string name)
        {
            var marker = name + "=";
            var start = url.IndexOf(marker, StringComparison.Ordinal);

            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = url.IndexOf('&', start);

            if (end < 0)
            {
                end = url.Length;
            }

            return int.TryParse(url[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }
}
