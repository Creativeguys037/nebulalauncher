using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤库客户端：只测纯解析与本地校验，网络请求一律走假 handler。
///
/// 三条关键容错来自站点实测（2026-10-26，littleskin.cn）：
/// <c>/skinlib/list</c> 的 Laravel 分页对象既没有 <c>total</c> 也没有 <c>last_page</c>，
/// 判末页只能看 <c>next_page_url == null</c>；<c>sort</c> 传白名单以外的值站点直接 500，
/// 所以必须在本地先拦下来，连请求都不发。
///
/// 失败一律走 <see cref="SkinLibraryResult{T}"/>（CONVENTIONS §3：服务不把异常抛给 ViewModel），
/// 因此这里的断言都落在 <c>Succeeded</c> / <c>ErrorText</c> 上，而不是 <c>Throws</c>。
/// </summary>
public class AppSkin_LibraryClientTests
{
    // 真实响应裁剪而来：无 total / 无 last_page，data 项里也没有 hash。
    private const string ListJson = """
    {"current_page":1,"data":[{"tid":917742,"name":"真红shinku","type":"alex","uploader":3336207,"public":true,"hd":false,"likes":1,"nickname":"Anon_216"},{"tid":917743,"name":"Blue Steve","type":"steve","likes":7}],"first_page_url":"https://littleskin.cn/skinlib/list?page=1","from":1,"next_page_url":"https://littleskin.cn/skinlib/list?page=2","path":"https://littleskin.cn/skinlib/list","per_page":20,"prev_page_url":null,"to":2}
    """;

    private const string InfoJson = """
    {"tid":917742,"name":"真红shinku","type":"alex","hash":"4200a3d16f481b45daddbb1c9305911ed7aea3890c673607e3488e647cdb9b30","size":3,"likes":1,"hd":false}
    """;

    [Fact]
    public void 列表响应缺少总数与末页字段也能解析()
    {
        var result = SkinLibraryClient.ParseList(ListJson);

        Assert.True(result.Succeeded);
        var page = result.Value!;
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.Items.Count);
        Assert.False(page.IsLastPage);
        Assert.Equal(917742, page.Items[0].Tid);
        Assert.Equal("真红shinku", page.Items[0].Name);
        Assert.Equal(1, page.Items[0].Likes);
        Assert.Equal(SkinModelKind.Alex, page.Items[0].Model);
        Assert.Equal(SkinModelKind.Steve, page.Items[1].Model);
    }

    [Fact]
    public void 下一页地址为空即末页()
    {
        var json = ListJson.Replace("\"next_page_url\":\"https://littleskin.cn/skinlib/list?page=2\"", "\"next_page_url\":null");

        var page = SkinLibraryClient.ParseList(json).Value!;

        Assert.True(page.IsLastPage);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public void 空列表既不抛异常也算末页()
    {
        var page = SkinLibraryClient.ParseList("""{"current_page":3,"data":[],"next_page_url":null}""").Value!;

        Assert.Empty(page.Items);
        Assert.True(page.IsLastPage);
        Assert.Equal(3, page.Page);
    }

    [Fact]
    public async Task 非法排序值在本地被白名单拦截且不发请求()
    {
        var handler = new FakeHandler(Json(ListJson));
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn");

        var result = await client.ListAsync(1, SkinLibraryFilter.All, null, "likes desc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("sort", result.ErrorText!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void 排序白名单就是站点认的那三个值()
    {
        Assert.Equal(3, SkinLibraryClient.SortKeys.Count);
        Assert.Equal("time", SkinLibraryClient.SortKeys[0]);
        Assert.Equal("likes", SkinLibraryClient.SortKeys[1]);
        Assert.Equal("tid", SkinLibraryClient.SortKeys[2]);
    }

    [Fact]
    public async Task 查询串带上页码筛选与关键字且不传每页条数()
    {
        var handler = new FakeHandler(Json(ListJson));
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn".TrimEnd('/'));

        await client.ListAsync(2, SkinLibraryFilter.Alex, " 女仆 ", "likes", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var query = request.Query;
        Assert.Equal("/skinlib/list", request.AbsolutePath);
        Assert.Contains("page=2", query);
        Assert.Contains("filter=alex", query);
        Assert.Contains("sort=likes", query);
        Assert.Contains("keyword=%E5%A5%B3%E4%BB%86", query);
        Assert.DoesNotContain("per_page", query);
    }

    [Fact]
    public async Task 列表接口返回网页而不是json时报可读错误()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html"),
        });
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn");

        var result = await client.ListAsync(1, SkinLibraryFilter.All, null, "time", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("application/json", result.ErrorText!);
    }

    [Fact]
    public async Task 皮肤详情解析出hash与体型()
    {
        var handler = new FakeHandler(Json(InfoJson));
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn");

        var result = await client.InfoAsync(917742, CancellationToken.None);

        Assert.True(result.Succeeded);
        var info = result.Value!;
        Assert.Equal("4200a3d16f481b45daddbb1c9305911ed7aea3890c673607e3488e647cdb9b30", info.Hash);
        Assert.Equal("真红shinku", info.Name);
        Assert.Equal(SkinModelKind.Alex, info.Model);
        Assert.Equal("https://littleskin.cn/textures/4200a3d16f481b45daddbb1c9305911ed7aea3890c673607e3488e647cdb9b30", client.TextureUrl(info.Hash));
        Assert.Equal("https://littleskin.cn/preview/917742", client.PreviewUrl(917742));
    }

    [Fact]
    public void 详情缺hash时视为拿不到皮肤()
    {
        Assert.False(SkinLibraryClient.ParseInfo("""{"tid":5,"name":"x","type":"steve"}""").Succeeded);
        Assert.False(SkinLibraryClient.ParseInfo("").Succeeded);
    }

    [Fact]
    public void 未知体型按Steve处理()
    {
        var page = SkinLibraryClient.ParseList("""{"current_page":1,"data":[{"tid":1,"name":"x","type":"cape"}],"next_page_url":null}""").Value!;

        Assert.Equal(SkinModelKind.Steve, Assert.Single(page.Items).Model);
    }

    [Fact]
    public async Task 连不上时标记为网络错误()
    {
        var handler = new ThrowingHandler(new HttpRequestException("boom"));
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn");

        var result = await client.ListAsync(1, SkinLibraryFilter.All, null, "time", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsNetworkError);
        Assert.Contains("网络", result.ErrorText!);
    }

    [Fact]
    public async Task 站点返回500时不算网络错误()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("oops", Encoding.UTF8, "text/plain"),
        });
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn");

        var result = await client.ListAsync(1, SkinLibraryFilter.All, null, "time", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsNetworkError);
        Assert.Contains("500", result.ErrorText!);
    }

    [Fact]
    public async Task 探测站点API版本读的是blessing_skin字段()
    {
        var handler = new FakeHandler(Json("""{"blessing_skin":"4.0.0","php_version":"8.2"}"""));
        using var client = new SkinLibraryClient(new HttpClient(handler), "https://littleskin.cn");

        Assert.Equal("4.0.0", await client.ProbeVersionAsync(CancellationToken.None));
        Assert.Equal("/api", Assert.Single(handler.Requests).AbsolutePath);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Json(string body) => _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }
}
