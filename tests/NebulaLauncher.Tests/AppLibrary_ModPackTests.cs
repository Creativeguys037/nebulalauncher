using System.Net;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 资源页「整合包」分区的纯逻辑：搜索 URL 的 facets、结果解析、临时归档命名、导入结果文案。
///
/// 网络全部由假 <see cref="HttpMessageHandler"/> 承担，测试不联网；判据是「拼出来的 facets 对不对」
/// 与「失败时给出的是中文原因而不是异常」。界面行为（点 chip、导入按钮）靠编译期绑定校验与截图。
/// </summary>
public class AppLibrary_ModPackTests
{
    [Fact]
    public void 搜索URL_只按整合包类型拼分面且不带游戏版本()
    {
        var url = ModpackCatalogClient.BuildSearchUrl("fabulous", offset: 20, limit: 20);
        var decoded = Uri.UnescapeDataString(url);

        // project_type 必须是 modpack：拼成 mod 会搜出模组列表。
        Assert.Contains("project_type:modpack", decoded, StringComparison.Ordinal);

        // 整合包搜索不按游戏版本过滤：玩家的顺序是先找包、再在包里挑构建。
        Assert.DoesNotContain("versions:", decoded, StringComparison.Ordinal);

        // 有查询词按相关度，空查询按下载量（浏览热门）。
        Assert.Contains("index=relevance", url, StringComparison.Ordinal);
        Assert.Contains("index=downloads", ModpackCatalogClient.BuildSearchUrl("  ", 0, 20), StringComparison.Ordinal);

        // 分页参数照传，负偏移夹成 0。
        Assert.Contains("offset=20", url, StringComparison.Ordinal);
        Assert.Contains("offset=0", ModpackCatalogClient.BuildSearchUrl(null, -5, 20), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 搜索_用假响应解析出项目并带上请求分面()
    {
        var handler = new RecordingHandler(SearchJson);
        using var client = new ModpackCatalogClient(handler);

        var projects = await client.SearchAsync("optimized", 0, 20);

        Assert.Null(client.LastError);
        var project = Assert.Single(projects);
        Assert.Equal("fabulously-optimized", project.Slug);
        Assert.Equal("Fabulously Optimized", project.Title);
        Assert.Equal(32_118_402, project.Downloads);

        var requested = Uri.UnescapeDataString(handler.LastUrl);
        Assert.Contains("/search?", requested, StringComparison.Ordinal);
        Assert.Contains("project_type:modpack", requested, StringComparison.Ordinal);
        Assert.DoesNotContain("versions:", requested, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 搜索_限流时不抛异常且给中文原因()
    {
        using var client = new ModpackCatalogClient(new StatusHandler(HttpStatusCode.TooManyRequests));

        var projects = await client.SearchAsync(null, 0, 20);

        Assert.Empty(projects);
        Assert.False(string.IsNullOrWhiteSpace(client.LastError));
        Assert.Contains("限流", client.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void 临时归档路径_清洗非法字符并补扩展名()
    {
        var path = LibraryViewModel.ResolveModPackArchivePath(@"C:\data\cache", "Fab Opt v2.0.mrpack", "Fabulously Optimized");

        Assert.Equal(Path.Combine(@"C:\data\cache", "modpacks", "Fab Opt v2.0.mrpack"), path);

        // Windows 非法字符与路径分隔符换成下划线，否则落盘会直接失败。
        var sanitized = LibraryViewModel.ResolveModPackArchivePath(
            @"C:\data\cache",
            "a/b\\c:d*e?.mrpack",
            "ignored");
        Assert.Equal(Path.Combine(@"C:\data\cache", "modpacks", "a_b_c_d_e_.mrpack"), sanitized);

        // 没有文件名时退回实例名；没有扩展名时补 .mrpack。
        Assert.Equal(
            Path.Combine(@"C:\data\cache", "modpacks", "My Pack.mrpack"),
            LibraryViewModel.ResolveModPackArchivePath(@"C:\data\cache", "  ", "My Pack"));
    }

    [Fact]
    public void 导入结果文案_三种情形与实例名拼接()
    {
        var withWarning = LibraryViewModel.DescribeModPackImport(
            new ModpackImportResult
            {
                Success = true,
                GameInstalled = true,
                InstalledVersionName = "fabric-loader-0.16.9-1.20.1",
                Warning = "加载器版本无法确认，已按最新版安装。",
            },
            "Fab Opt");

        Assert.Contains("已在版本页可见", withWarning, StringComparison.Ordinal);
        Assert.Contains("加载器版本无法确认", withWarning, StringComparison.Ordinal);

        var installed = LibraryViewModel.DescribeModPackImport(
            new ModpackImportResult { Success = true, GameInstalled = true },
            "Fab Opt");
        Assert.Contains("游戏与加载器一并安装", installed, StringComparison.Ordinal);

        // 只解压了文件：必须明说「未安装游戏」，否则玩家会以为装完就能启动。
        var unpacked = LibraryViewModel.DescribeModPackImport(
            new ModpackImportResult { Success = true, GameInstalled = false },
            "Fab Opt");
        Assert.Contains("未安装游戏", unpacked, StringComparison.Ordinal);

        // 实例名：项目名已含版本号时不重复拼接。
        Assert.Equal("Fab Opt 5.9.0", LibraryViewModel.ModPackInstanceName("Fab Opt", "5.9.0"));
        Assert.Equal("Fab Opt 5.9.0", LibraryViewModel.ModPackInstanceName("Fab Opt 5.9.0", "5.9.0"));
        Assert.True(LibraryViewModel.ModPackInstanceName(new string('x', 200), null).Length <= 60);
    }

    private const string SearchJson = """
    {
      "hits": [
        {
          "project_id": "1KVo5zza",
          "slug": "fabulously-optimized",
          "title": "Fabulously Optimized",
          "description": "以帧率为第一目标的 Fabric 整合包。",
          "author": "robotkoer",
          "downloads": 32118402,
          "follows": 1200,
          "icon_url": "https://cdn.modrinth.com/data/1KVo5zza/icon.png",
          "display_categories": ["fabric", "optimization"],
          "versions": ["1.20.1", "1.20.2"],
          "date_modified": "2025-03-01T00:00:00Z"
        }
      ],
      "offset": 0,
      "limit": 20,
      "total_hits": 1
    }
    """;

    /// <summary>固定返回一段 JSON，并记下被请求的 URL。</summary>
    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUrl = request.RequestUri?.ToString() ?? string.Empty;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    /// <summary>固定返回某个错误状态码。</summary>
    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
}