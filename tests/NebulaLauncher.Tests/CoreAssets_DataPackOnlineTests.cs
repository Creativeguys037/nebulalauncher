using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

// 被测代码用 SHA1 校验数据包，测试必须按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// Modrinth 数据包的在线搜索与安装。
///
/// 三条硬约束在这里钉死：
/// 1. 数据包没有加载器：facets 只能有 <c>project_type:datapack</c>（外加游戏版本），
///    带 <c>categories:fabric</c> 之类的 loader facet 会让 Modrinth 返回空列表；
/// 2. 安装落点必须显式：全局 <c>datapacks/</c> 与存档级 <c>saves/&lt;world&gt;/datapacks/</c> 是两处不同目录，
///    存档被删后必须报错而不是默默建一个空目录；
/// 3. 落盘沿用既有安装链：SHA1 不匹配不落地、同名自动改名。
///
/// 网络一律用假 handler，不声称真实 Modrinth 搜索已验证。
/// </summary>
public sealed class CoreAssets_DataPackOnlineTests
{
    [Fact]
    public void 搜索facets_数据包只带project_type与游戏版本_不带加载器()
    {
        var facets = ModrinthService.BuildSearchFacets(ContentKind.DataPack, "1.21.4", LoaderKind.Fabric);

        Assert.Contains("project_type:datapack", facets, StringComparison.Ordinal);
        Assert.Contains("versions:1.21.4", facets, StringComparison.Ordinal);

        // 数据包与加载器无关：带上 fabric facet 会让结果直接变空。
        Assert.DoesNotContain("categories:", facets, StringComparison.Ordinal);
        Assert.DoesNotContain("fabric", facets, StringComparison.Ordinal);
    }

    [Fact]
    public void 版本URL_数据包不传loaders查询参数()
    {
        var url = ModrinthService.BuildVersionUrl("terralith-id", "1.21.4", LoaderKind.Vanilla);

        Assert.Contains("game_versions=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("loaders=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 项目类型_识别datapack且网页地址用datapack路径()
    {
        Assert.Equal(ContentKind.DataPack, ModrinthService.ParseProjectKind("datapack"));
        Assert.Equal(ContentKind.DataPack, ModrinthService.ParseProjectKind("DataPack"));

        var project = new ModrinthProject
        {
            ProjectId = "id",
            Slug = "terralith",
            Title = "Terralith",
            Kind = ContentKind.DataPack,
        };

        Assert.Equal("https://modrinth.com/datapack/terralith", project.WebUrl);
    }

    [Fact]
    public void 搜索结果_按传入的数据包类型回填()
    {
        const string json = """
        {
          "hits": [
            {
              "project_id": "1a2b3c",
              "slug": "terralith",
              "title": "Terralith",
              "description": "地形生成重写",
              "author": "Starmute",
              "downloads": 41337120,
              "follows": 1200,
              "icon_url": "https://cdn.modrinth.com/terralith.webp",
              "display_categories": ["worldgen"],
              "versions": ["1.21.4", "1.20.1"]
            }
          ]
        }
        """;

        var projects = ModrinthService.ParseSearch(json, ContentKind.DataPack);

        var project = Assert.Single(projects);
        Assert.Equal(ContentKind.DataPack, project.Kind);
        Assert.Equal("terralith", project.Slug);
        Assert.Equal("https://modrinth.com/datapack/terralith", project.WebUrl);
    }

    [Fact]
    public void 安装目标_没有存档时只有全局一项()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-no-worlds");
        Directory.CreateDirectory(gameDirectory);

        var targets = ModrinthService.ResolveDataPackTargets(gameDirectory);

        var global = Assert.Single(targets);
        Assert.True(global.IsGlobal);
        Assert.Null(global.WorldName);
        Assert.Equal(Path.Combine(gameDirectory, "datapacks"), global.DataPackDirectory);
    }

    [Fact]
    public void 安装目标_列出全局与每个存档()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-with-worlds");
        var saves = Path.Combine(gameDirectory, "saves");
        Directory.CreateDirectory(Path.Combine(saves, "alpha"));
        Directory.CreateDirectory(Path.Combine(saves, "Beta"));

        var targets = ModrinthService.ResolveDataPackTargets(gameDirectory);

        Assert.Equal(3, targets.Count);
        Assert.True(targets[0].IsGlobal);

        // 存档按名字排序（忽略大小写），路径落在各自的 saves/<world>/datapacks。
        Assert.Equal("alpha", targets[1].WorldName);
        Assert.Equal(Path.Combine(saves, "alpha", "datapacks"), targets[1].DataPackDirectory);
        Assert.Equal("Beta", targets[2].WorldName);
        Assert.Equal(Path.Combine(saves, "Beta", "datapacks"), targets[2].DataPackDirectory);
    }

    [Fact]
    public void 安装目标_空游戏目录返回空列表()
    {
        Assert.Empty(ModrinthService.ResolveDataPackTargets(string.Empty));
        Assert.Empty(ModrinthService.ResolveDataPackTargets("   "));
    }

    [Fact]
    public void 安装目标校验_存档被删除时给出中文原因()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-validate");
        var worldDirectory = Path.Combine(gameDirectory, "saves", "gone");
        Directory.CreateDirectory(worldDirectory);

        var target = new DataPackInstallTarget("存档：gone", worldDirectory, "gone");

        // 存在时通过。
        Assert.Null(ModrinthService.ValidateDataPackTarget(target));

        Directory.Delete(worldDirectory);

        var error = ModrinthService.ValidateDataPackTarget(target);
        Assert.NotNull(error);
        Assert.Contains("不存在", error, StringComparison.Ordinal);

        // 全局目标不依赖任何已有目录，创建即可，永远有效。
        Assert.Null(ModrinthService.ValidateDataPackTarget(
            new DataPackInstallTarget("全局", gameDirectory, null)));
    }

    [Fact]
    public async Task 安装_数据包落进全局datapacks()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-install-global");
        var payload = Encoding.UTF8.GetBytes("terralith-data");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var service = new ModrinthService(
            new CoreAssetsLog(),
            paths,
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            Version("Terralith-1.21.4.zip", payload.Length, sha1),
            gameDirectory,
            ContentKind.DataPack);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Terralith-1.21.4.zip", result.FileName);

        var installed = Path.Combine(gameDirectory, "datapacks", "Terralith-1.21.4.zip");
        Assert.True(File.Exists(installed));
        Assert.Equal(payload, await File.ReadAllBytesAsync(installed));
    }

    [Fact]
    public async Task 安装_数据包落进存档datapacks()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-install-world");
        var worldDirectory = Path.Combine(gameDirectory, "saves", "world-1");
        Directory.CreateDirectory(worldDirectory);

        var payload = Encoding.UTF8.GetBytes("world-only-pack");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        var target = Assert.Single(
            ModrinthService.ResolveDataPackTargets(gameDirectory),
            item => item.WorldName == "world-1");

        using var service = new ModrinthService(
            new CoreAssetsLog(),
            paths,
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            Version("pack.zip", payload.Length, sha1),
            target.GameDirectory,
            ContentKind.DataPack);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(worldDirectory, "datapacks", "pack.zip")));

        // 存档级安装不能污染全局 datapacks/。
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "datapacks")));
    }

    [Fact]
    public async Task 安装_sha1不匹配时数据包不落地()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-install-bad-sha1");
        var payload = Encoding.UTF8.GetBytes("corrupted-datapack");

        using var service = new ModrinthService(
            new CoreAssetsLog(),
            paths,
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            Version("pack.zip", payload.Length, "0000000000000000000000000000000000000000"),
            gameDirectory,
            ContentKind.DataPack);

        Assert.False(result.Success);
        Assert.Contains("SHA1", result.Error ?? string.Empty, StringComparison.Ordinal);

        var datapacks = Path.Combine(gameDirectory, "datapacks");
        Assert.True(Directory.Exists(datapacks));
        Assert.Empty(Directory.GetFiles(datapacks));
    }

    [Fact]
    public async Task 安装_同名数据包自动改名()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-install-rename");
        var datapacks = Path.Combine(gameDirectory, "datapacks");
        Directory.CreateDirectory(datapacks);
        await File.WriteAllTextAsync(Path.Combine(datapacks, "pack.zip"), "old-pack");

        var payload = Encoding.UTF8.GetBytes("new-pack");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        using var service = new ModrinthService(
            new CoreAssetsLog(),
            paths,
            new StaticContentHandler(payload));

        var result = await service.InstallAsync(
            Version("pack.zip", payload.Length, sha1),
            gameDirectory,
            ContentKind.DataPack);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Renamed);
        Assert.Equal("pack (1).zip", result.FileName);
        Assert.Equal("old-pack", await File.ReadAllTextAsync(Path.Combine(datapacks, "pack.zip")));
    }

    private static ModrinthVersion Version(string fileName, long size, string? sha1) => new()
    {
        Id = "dppack01",
        ProjectId = "terralith",
        Name = "Terralith 1.21.4",
        VersionNumber = "1.21.4",
        FileName = fileName,
        DownloadUrl = "https://cdn.modrinth.com/fake/" + fileName,
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
}
