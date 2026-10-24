using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// Forge「推荐版」：promoted 列表的解析、缓存与安装面板的默认选项。
///
/// 背景：Forge 的「最新构建」经常是坏的，用户按默认装到它之后启动崩、日志看不出原因。
/// 官方在 promotions_slim.json 里标了每个 Minecraft 版本的 <c>&lt;mc&gt;-recommended</c>，
/// 这里验证我们挑的是它，而不是号最大的那个。
/// </summary>
public class CoreVersions_ForgeRecommendTests
{
    [Fact]
    public void 解析官方promotions_只取recommended后缀()
    {
        const string json = """
            {
              "homepage": "https://files.minecraftforge.net/",
              "promos": {
                "1.20.1-latest": "47.3.0",
                "1.20.1-recommended": "47.2.0",
                "1.19.2-recommended": "43.2.0",
                "1.16.5-latest": "36.2.39"
              }
            }
            """;

        var promotions = VersionService.ParseForgePromotions(json);

        Assert.Equal(2, promotions.Count);
        Assert.Equal("47.2.0", promotions["1.20.1"]);
        Assert.Equal("43.2.0", promotions["1.19.2"]);

        // -latest 是「最新构建」，正是要避开的那个，不能混进推荐表。
        Assert.DoesNotContain("47.3.0", promotions.Values);
        Assert.DoesNotContain("36.2.39", promotions.Values);
    }

    [Fact]
    public void 解析BMCLAPI数组形状_取build版本号()
    {
        // BMCLAPI 的 /forge/promos 与官方形状不同（PCL2 读的就是这个）。
        const string json = """
            [
              { "name": "1.20.1-recommended", "build": { "version": "47.2.0" }, "mcversion": "1.20.1" },
              { "name": "1.20.1-latest", "build": { "version": "47.3.0" }, "mcversion": "1.20.1" }
            ]
            """;

        var promotions = VersionService.ParseForgePromotions(json);

        var single = Assert.Single(promotions);
        Assert.Equal("47.2.0", single.Value);
        Assert.Equal("1.20.1", single.Key);
    }

    [Fact]
    public void 裸recommended键被忽略_无法映射到具体版本()
    {
        // 官方文件顶层的 "recommended": "..." 指「当前最新 MC 版本」的推荐构建。
        // 那条键不带版本前缀，猜错会让用户装到别的版本的构建，所以必须跳过。
        const string json = """
            {
              "promos": {
                "recommended": "47.3.0",
                "latest": "47.4.0",
                "1.20.1-recommended": "47.2.0"
              }
            }
            """;

        var promotions = VersionService.ParseForgePromotions(json);

        var single = Assert.Single(promotions);
        Assert.Equal("1.20.1", single.Key);
        Assert.Equal("47.2.0", single.Value);
    }

    [Fact]
    public void 缺少promos字段或空对象时返回空表()
    {
        Assert.Empty(VersionService.ParseForgePromotions("{}"));
        Assert.Empty(VersionService.ParseForgePromotions("""{ "promos": {} }"""));
        Assert.Empty(VersionService.ParseForgePromotions("[]"));
    }

    [Fact]
    public void 候选地址永远官方优先_不随下载源切换()
    {
        // 实测：官方 promotions_slim.json（4 KB）的 1.21.1-recommended = 52.1.20，
        // 而 BMCLAPI /forge/promos（62 KB）同一条是 52.1.0——镜像滞后了二十个小版本。
        // 推荐版本给错会把用户引导去装官方已不再推荐的构建，所以这里不跟下载源走。
        var urls = LoaderMetaUrls.ForgePromotions();

        Assert.Equal(2, urls.Count);
        Assert.StartsWith("https://files.minecraftforge.net/", urls[0]);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/forge/promos", urls[1]);
    }

    [Fact]
    public async Task Forge的加载器列表给出推荐版并标记IsRecommended()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new StubJsonHandler("""
            { "promos": { "1.20.1-recommended": "47.2.0", "1.20.1-latest": "47.3.0" } }
            """);
        using var client = new HttpClient(handler);

        var service = new VersionService(paths, NullAppLog.Instance, client);
        var versions = await service.GetLoaderVersionsAsync(LoaderKind.Forge, "1.20.1");

        var only = Assert.Single(versions);
        Assert.Equal("47.2.0", only.Version);
        Assert.True(only.IsRecommended);

        // 推荐版一定也是「稳定」的：安装面板的默认选中逻辑先看 IsRecommended 再看 Stable，
        // 两条都成立才不会出现「推荐但不算稳定」的自相矛盾状态。
        Assert.True(only.Stable);
        Assert.Equal("1.20.1", only.GameVersion);
    }

    [Fact]
    public async Task 没有推荐的Minecraft版本返回空列表而不是猜一个()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new StubJsonHandler("""{ "promos": { "1.20.1-recommended": "47.2.0" } }""");
        using var client = new HttpClient(handler);

        var service = new VersionService(paths, NullAppLog.Instance, client);

        // 1.7.10 从来没有 recommended 条目：返回空，界面退化成「装最新构建」。
        Assert.Empty(await service.GetLoaderVersionsAsync(LoaderKind.Forge, "1.7.10"));
    }

    [Fact]
    public async Task 网络失败时不抛异常_只返回空列表()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var client = new HttpClient(new StubJsonHandler(null, HttpStatusCode.ServiceUnavailable));

        var service = new VersionService(paths, NullAppLog.Instance, client);

        // 拉不到推荐列表只是「没有推荐标记」，不能变成安装面板打不开。
        Assert.Empty(await service.GetLoaderVersionsAsync(LoaderKind.Forge, "1.20.1"));
    }

    [Fact]
    public async Task NeoForge不编造推荐版()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var client = new HttpClient(new StubJsonHandler("""{ "promos": { "1.21.1-recommended": "21.1.80" } }"""));

        var service = new VersionService(paths, NullAppLog.Instance, client);

        // NeoForge 没有官方推荐列表：它的版本由安装器决定，这里必须是空。
        Assert.Empty(await service.GetLoaderVersionsAsync(LoaderKind.NeoForge, "1.21.1"));
    }

    /// <summary>返回固定 JSON 的桩；<paramref name="body"/> 为 null 时返回指定状态码。</summary>
    private sealed class StubJsonHandler(string? body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = body is null
                ? new HttpResponseMessage(status)
                : new HttpResponseMessage(status) { Content = new StringContent(body) };

            return Task.FromResult(response);
        }
    }
}