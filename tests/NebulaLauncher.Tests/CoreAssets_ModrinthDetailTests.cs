using System.Net;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「项目详情」的纯逻辑测试：详情 JSON 解析、Markdown → 纯文本、5 分钟内存缓存。
///
/// 详情响应结构按 2026-10 的实测锁定：分类是 <c>categories</c>、支持版本是 <c>game_versions</c>、
/// 关注数是 <c>followers</c>、修改时间是 <c>updated</c>，与 <c>/v2/search</c> 的字段名不同。
/// 网络失败分支用自定义 HttpMessageHandler 覆盖，测试不依赖网络。
/// </summary>
public class CoreAssets_ModrinthDetailTests
{
    /// <summary>GET /v2/project/{id} 响应片段（sodium），带正文、5 张图库、源码/问题链接与许可。</summary>
    private const string ProjectJson = """
    {
      "id": "AANobbMI",
      "slug": "sodium",
      "project_type": "mod",
      "title": "Sodium",
      "description": "A high-performance rendering engine replacement for Minecraft.",
      "icon_url": "https://cdn.modrinth.com/data/AANobbMI/icon.png",
      "downloads": 250585660,
      "followers": 41999,
      "categories": ["fabric", "neoforge", "optimization"],
      "game_versions": ["1.20.1", "1.21.4"],
      "updated": "2026-10-16T01:51:36.06314Z",
      "issues_url": "https://github.com/CaffeineMC/sodium/issues",
      "source_url": "https://github.com/CaffeineMC/sodium",
      "license": {
        "id": "LGPL-3.0-only",
        "name": "GNU Lesser General Public License v3.0 only",
        "url": "https://www.gnu.org/licenses/lgpl-3.0.html"
      },
      "body": "## 简介\n\nSodium 重写了渲染管线。\n\n- 更少的内存分配\n- 更快的区块更新\n\n![截图](https://cdn.modrinth.com/data/AANobbMI/images/1.png)",
      "gallery": [
        { "url": "https://cdn.modrinth.com/data/AANobbMI/images/1.png", "featured": true, "title": "主界面", "description": "帧率对比" },
        { "url": "https://cdn.modrinth.com/data/AANobbMI/images/2.png", "featured": false, "title": null, "description": null },
        { "url": "https://cdn.modrinth.com/data/AANobbMI/images/3.png", "featured": false },
        { "url": "https://cdn.modrinth.com/data/AANobbMI/images/4.png", "featured": false },
        { "url": "https://cdn.modrinth.com/data/AANobbMI/images/5.png", "featured": false }
      ]
    }
    """;

    [Fact]
    public void 解析详情_读取正文图库链接与许可()
    {
        var project = ModrinthService.ParseProject(ProjectJson);

        Assert.NotNull(project);
        Assert.Equal("AANobbMI", project.ProjectId);
        Assert.Equal("sodium", project.Slug);
        Assert.Equal("Sodium", project.Title);
        Assert.Equal(250585660, project.Downloads);
        Assert.Equal(41999, project.Follows);
        Assert.Equal(ContentKind.Mod, project.Kind);
        Assert.Equal("https://modrinth.com/mod/sodium", project.WebUrl);
        Assert.Equal("https://cdn.modrinth.com/data/AANobbMI/icon.png", project.IconUrl);
        Assert.NotNull(project.UpdatedAt);

        // 详情的分类与版本字段名和搜索响应不同（categories / game_versions），读错就是空集合。
        Assert.Contains("fabric", project.Categories);
        Assert.Contains("1.21.4", project.GameVersions);

        Assert.Contains("渲染管线", project.Body, StringComparison.Ordinal);
        Assert.Equal(5, project.GalleryUrls.Count);
        Assert.Equal("https://cdn.modrinth.com/data/AANobbMI/images/1.png", project.GalleryUrls[0]);
        Assert.Equal("https://github.com/CaffeineMC/sodium/issues", project.IssuesUrl);
        Assert.Equal("https://github.com/CaffeineMC/sodium", project.SourceUrl);
        Assert.Equal("LGPL-3.0-only", project.LicenseId);
        Assert.True(project.HasDetails);
    }

    [Fact]
    public void 解析详情_按project_type推断内容类型()
    {
        const string json = """
        {
          "id": "abc12345",
          "slug": "fresh-animations",
          "project_type": "resourcepack",
          "title": "Fresh Animations",
          "categories": ["minecraft"],
          "game_versions": ["1.21.4"]
        }
        """;

        var project = ModrinthService.ParseProject(json);

        Assert.NotNull(project);
        Assert.Equal(ContentKind.ResourcePack, project.Kind);
        Assert.Equal("https://modrinth.com/resourcepack/fresh-animations", project.WebUrl);
        Assert.Empty(project.GalleryUrls);
        Assert.Null(project.IssuesUrl);
        Assert.Null(project.LicenseId);
        Assert.False(project.HasDetails);
    }

    [Theory]
    [InlineData("""{ "slug": "sodium", "title": "Sodium" }""")]
    [InlineData("""{ "id": "AANobbMI", "title": "Sodium" }""")]
    [InlineData("""{ "id": "AANobbMI", "slug": "sodium" }""")]
    [InlineData("""[]""")]
    public void 解析详情_缺少必需字段时返回null(string json)
    {
        Assert.Null(ModrinthService.ParseProject(json));
    }

    [Fact]
    public void 解析详情_空对象不抛异常()
    {
        Assert.Null(ModrinthService.ParseProject("""{}"""));
    }

    [Fact]
    public void StripMarkdown_去掉图片与链接语法()
    {
        Assert.Equal("见 官网 说明", ModrinthService.StripMarkdown("见 [官网](https://example.com) 说明"));
        Assert.Equal("看图 结束", ModrinthService.StripMarkdown("看图 ![截图](https://cdn.example.com/x.png) 结束"));

        // 徽章式嵌套：图片在链接里，整段消失而不是留下空链接。
        Assert.Equal(string.Empty, ModrinthService.StripMarkdown("[![徽章](https://img.example.com/x.svg)](https://example.com)"));
    }

    [Fact]
    public void StripMarkdown_去掉行级前缀并压缩空行()
    {
        Assert.Equal("标题\n\n一\n二", ModrinthService.StripMarkdown("# 标题\n\n- 一\n- 二"));
        Assert.Equal("引用\n\n正文", ModrinthService.StripMarkdown("> 引用\n\n\n\n正文"));
        Assert.Equal("行一\n\n行二", ModrinthService.StripMarkdown("行一\r\n\r\n\r\n行二"));
        Assert.Equal("加粗文字 与 代码", ModrinthService.StripMarkdown("**加粗**文字 与 `代码`"));
    }

    [Fact]
    public void StripMarkdown_去掉HTML标签与分隔线()
    {
        Assert.Equal("第一段\n\n第二段", ModrinthService.StripMarkdown("<p>第一段</p>\n\n---\n\n<br/>第二段"));
    }

    [Fact]
    public void StripMarkdown_null输入返回空字符串()
    {
        // 不用 [InlineData(null)]：C# 会把它解析成 params 空数组，变成零参数用例。
        Assert.Equal(string.Empty, ModrinthService.StripMarkdown(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void StripMarkdown_空白输入返回空字符串(string markdown)
    {
        Assert.Equal(string.Empty, ModrinthService.StripMarkdown(markdown));
    }

    [Fact]
    public async Task 详情_成功解析并写入缓存_同一项目不重复请求()
    {
        var handler = new CountingJsonHandler(ProjectJson);
        using var service = new ModrinthService(new CoreAssetsLog(), null, handler);

        var first = await service.GetProjectAsync("AANobbMI");

        Assert.NotNull(first);
        Assert.Equal(1, handler.RequestCount);
        Assert.Null(service.LastError);

        var second = await service.GetProjectAsync("AANobbMI");

        // 5 分钟内命中缓存：返回同一份解析结果，不再发请求。
        Assert.Same(first, second);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task 详情_404时写中文错误并返回null()
    {
        using var service = new ModrinthService(new CoreAssetsLog(), null, new StatusHandler(HttpStatusCode.NotFound));

        Assert.Null(await service.GetProjectAsync("does-not-exist"));
        Assert.Contains("404", service.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 详情_项目ID为空时不发请求()
    {
        var handler = new CountingJsonHandler(ProjectJson);
        using var service = new ModrinthService(new CoreAssetsLog(), null, handler);

        Assert.Null(await service.GetProjectAsync("   "));
        Assert.Equal(0, handler.RequestCount);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
    }

    /// <summary>固定返回一段 JSON，并记录被调用次数（用来验证缓存确实省掉了请求）。</summary>
    private sealed class CountingJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public CountingJsonHandler(string json) => _json = json;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
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
