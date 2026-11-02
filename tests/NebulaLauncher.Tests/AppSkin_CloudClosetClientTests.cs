using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤站网页会话客户端。这里锁的是“页面契约”而不是网络本身：
/// 分页字段、CSRF 头、上传字段名、重复纹理与服务端拒绝的区分。
/// 真实站点探测记录见 docs/research/littleskin-web-session-texture-write-contract.md。
/// </summary>
public sealed class AppSkin_CloudClosetClientTests
{
    private const string Site = "https://example.com";

    [Fact]
    public void 衣柜分页按总页数判断末页而不是把last_page当布尔()
    {
        var result = CloudClosetClient.ParseCloset(
            """
            {
              "current_page": 2,
              "last_page": 4,
              "next_page_url": "https://example.com/user/closet/list?page=3",
              "data": []
            }
            """);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.Page);
        Assert.False(result.Value.IsLastPage);
    }

    [Fact]
    public void 衣柜最后一页标记为末页()
    {
        var result = CloudClosetClient.ParseCloset(
            """
            {
              "current_page": 4,
              "last_page": 4,
              "next_page_url": null,
              "data": []
            }
            """);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.IsLastPage);
    }

    [Fact]
    public void 没有last_page时退回看next_page_url()
    {
        var result = CloudClosetClient.ParseCloset(
            """
            {
              "current_page": 1,
              "next_page_url": null,
              "data": []
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.IsLastPage);
    }

    [Fact]
    public void 衣柜行解析名字类型哈希点赞公开状态与pivot名()
    {
        var result = CloudClosetClient.ParseCloset(
            """
            {
              "current_page": 1,
              "last_page": 1,
              "data": [
                {
                  "tid": 11,
                  "name": "Blue Skin",
                  "type": "Alex",
                  "hash": "abc123",
                  "likes": "7",
                  "public": 1,
                  "pivot": { "item_name": "我的皮肤" }
                },
                {
                  "tid": "12",
                  "type": "CAPE",
                  "hash": "",
                  "public": "0",
                  "pivot": { "item_name": "没有名字的披风" }
                }
              ]
            }
            """);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.Items.Count);

        var skin = result.Value.Items[0];
        Assert.Equal(11, skin.Tid);
        Assert.Equal("Blue Skin", skin.Name);
        Assert.Equal("alex", skin.Type);
        Assert.Equal("abc123", skin.Hash);
        Assert.Equal(7, skin.Likes);
        Assert.True(skin.IsPublic);
        Assert.Equal("我的皮肤", skin.ItemName);

        var cape = result.Value.Items[1];
        Assert.Equal(12, cape.Tid);
        Assert.Equal("没有名字的披风", cape.Name);
        Assert.Equal("cape", cape.Type);
        Assert.Equal(string.Empty, cape.Hash);
        Assert.False(cape.IsPublic);
    }

    [Fact]
    public void 衣柜响应不是分页对象时给出错误()
    {
        var result = CloudClosetClient.ParseCloset("[1,2,3]");

        Assert.False(result.Succeeded);
        Assert.Contains("不是分页对象", result.ErrorText!);
    }

    [Fact]
    public void 衣柜响应不是JSON时给出错误()
    {
        var result = CloudClosetClient.ParseCloset("not-json");

        Assert.False(result.Succeeded);
        Assert.Contains("无法解析", result.ErrorText!);
    }

    [Fact]
    public void 衣柜编号列表支持数组对象与字符串数字()
    {
        var result = CloudClosetClient.ParseClosetIds("""[1, 2, "3", {"tid": 4}]""");

        Assert.True(result.Succeeded);
        Assert.Equal([1, 2, 3, 4], result.Value);
    }

    [Fact]
    public void 衣柜编号列表支持data包装()
    {
        var result = CloudClosetClient.ParseClosetIds("""{"data": [{"tid": 5}, 6]}""");

        Assert.True(result.Succeeded);
        Assert.Equal([5, 6], result.Value);
    }

    [Fact]
    public void 衣柜编号列表不是数组时给出错误()
    {
        var result = CloudClosetClient.ParseClosetIds("""{"foo": 1}""");

        Assert.False(result.Succeeded);
        Assert.Contains("格式不对", result.ErrorText!);
    }

    [Fact]
    public void 衣柜编号列表不是JSON时给出错误()
    {
        var result = CloudClosetClient.ParseClosetIds("not-json");

        Assert.False(result.Succeeded);
        Assert.Contains("无法解析", result.ErrorText!);
    }

    [Fact]
    public void 衣柜编号列表为空时返回空列表()
    {
        var result = CloudClosetClient.ParseClosetIds("[]");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public void 衣柜编号列表全不可识别时给出错误()
    {
        var result = CloudClosetClient.ParseClosetIds("""[{"foo": 1}, "bar"]""");

        Assert.False(result.Succeeded);
        Assert.Contains("无法识别的项目", result.ErrorText!);
    }

    [Fact]
    public void 衣柜编号列表部分不可识别时也给出错误()
    {
        var result = CloudClosetClient.ParseClosetIds("""[77, {"foo": 1}]""");

        Assert.False(result.Succeeded);
        Assert.Contains("无法识别的项目", result.ErrorText!);
    }

    [Fact]
    public async Task 查询衣柜编号遇到业务错误时透传原因与错误码()
    {
        var handler = new CapturingHandler(
            _ => Json(HttpStatusCode.OK, """{"code":1,"message":"积分不足"}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.GetClosetIdsAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("积分不足", result.ErrorText);
        Assert.Equal(1, result.Code);
        Assert.Equal("/user/closet/ids", Assert.Single(handler.Requests).PathAndQuery);
    }

    [Fact]
    public async Task 衣柜请求带页码分类每页条数与转义后的关键词()
    {
        var handler = new CapturingHandler(
            _ => Json(HttpStatusCode.OK, """{"current_page":1,"last_page":1,"data":[]}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.GetClosetAsync(3, "cape", "blue skin&x", CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/user/closet/list?page=3&category=cape&perPage=20&q=blue%20skin%26x", request.PathAndQuery);
    }

    [Fact]
    public async Task 登录先取CSRF再提交JSON并记住会话()
    {
        var handler = new CapturingHandler(request =>
            request.Method == HttpMethod.Get
                ? Html("<meta name=\"csrf-token\" content=\"tok-123\">")
                : Json(HttpStatusCode.OK, """{"code":0,"message":"ok"}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.LoginAsync("me", "pw", remember: true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(client.IsAuthenticated);
        Assert.Equal(2, handler.Requests.Count);

        var post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("/auth/login", post.PathAndQuery);
        Assert.Equal("tok-123", post.Header("X-CSRF-TOKEN"));
        Assert.Contains("\"identification\":\"me\"", post.Body);
        Assert.Contains("\"password\":\"pw\"", post.Body);
        Assert.Contains("\"remember\":true", post.Body);
    }

    [Fact]
    public async Task CSRF过期时刷新一次再重试()
    {
        var csrfPage = 0;
        var posts = 0;
        var handler = new CapturingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                csrfPage++;
                return Html($"<meta name=\"csrf-token\" content=\"tok-{csrfPage}\">");
            }

            posts++;
            return posts == 1
                ? new HttpResponseMessage((HttpStatusCode)419)
                : Json(HttpStatusCode.OK, """{"code":0}""");
        });
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.LoginAsync("me", "pw", remember: false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("tok-1", handler.Requests[1].Header("X-CSRF-TOKEN"));
        Assert.Equal("tok-2", handler.Requests[3].Header("X-CSRF-TOKEN"));
    }

    [Fact]
    public async Task 登录被服务端拒绝时保留服务端原因()
    {
        var handler = new CapturingHandler(request =>
            request.Method == HttpMethod.Get
                ? Html("<meta name=\"csrf-token\" content=\"tok\">")
                : Json(HttpStatusCode.OK, """{"code":1,"message":"用户名或密码错误"}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.LoginAsync("me", "wrong", remember: false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(client.IsAuthenticated);
        Assert.Equal("用户名或密码错误", result.ErrorText);
    }

    [Fact]
    public async Task 上传披风使用站点字段并返回重复纹理编号()
    {
        var handler = new CapturingHandler(request =>
            request.Method == HttpMethod.Get
                ? Html("<meta name=\"csrf-token\" content=\"tok\">")
                : Json(HttpStatusCode.OK, """{"code":2,"message":"已经存在","data":{"tid":42}}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.UploadTextureAsync("我的披风", [1, 2, 3], "cape", isPublic: true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.IsDuplicate);
        Assert.Equal(42, result.Value.Tid);

        var post = handler.Requests[1];
        Assert.Equal("/texture", post.PathAndQuery);
        Assert.Contains("name=file", post.Body);
        Assert.Contains("name=type", post.Body);
        Assert.Contains("cape", post.Body);
        Assert.Contains("name=public", post.Body);
    }

    [Fact]
    public async Task 上传被拒绝时透传业务错误码()
    {
        var handler = new CapturingHandler(request =>
            request.Method == HttpMethod.Get
                ? Html("""<meta name="csrf-token" content="tok">""")
                : Json(HttpStatusCode.OK, """{"code":1,"message":"图片太大了"}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.UploadTextureAsync("skin", [1, 2, 3], "steve", isPublic: false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("图片太大了", result.ErrorText);
        Assert.Equal(1, result.Code);
    }

    [Fact]
    public async Task 上传成功时读取嵌套的纹理编号()
    {
        var handler = new CapturingHandler(request =>
            request.Method == HttpMethod.Get
                ? Html("<meta name=\"csrf-token\" content=\"tok\">")
                : Json(HttpStatusCode.OK, """{"code":0,"data":{"data":{"tid":7}}}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.UploadTextureAsync("skin", [1, 2, 3], "steve", isPublic: false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.False(result.Value!.IsDuplicate);
        Assert.Equal(7, result.Value.Tid);
    }

    [Fact]
    public async Task 删除遇到401时标记登录失效()
    {
        var handler = new CapturingHandler(request =>
            request.Method == HttpMethod.Get
                ? Html("<meta name=\"csrf-token\" content=\"tok\">")
                : Json(HttpStatusCode.Unauthorized, """{"message":"Unauthenticated."}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.DeleteTextureAsync(9, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsAuthError);
        Assert.Equal("Unauthenticated.", result.ErrorText);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("/texture/9", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task 下载纹理时转义哈希里的斜杠与加号()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50]),
        });
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.DownloadTextureAsync("a/b+c", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([0x89, 0x50], result.Value);
        Assert.Contains("%2F", handler.Requests[0].PathAndQuery);
        Assert.Contains("%2B", handler.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task 角色列表用站址拼绝对地址()
    {
        // 回归：GetJsonAsync 收到 "/user/player/list" 这种站内路径时，没设 BaseAddress 的 HttpClient
        // 会抛 InvalidOperationException，登录后拉角色列表直接炸。
        var handler = new CapturingHandler(_ =>
            Json(HttpStatusCode.OK, """[{"name":"Steve","uuid":"u-1"}]"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.GetPlayersAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/user/player/list", Assert.Single(handler.Requests).PathAndQuery);
    }

    [Fact]
    public async Task 材质行缺hash时先查详情再下载()
    {
        // 回归：/skinlib/list 的行没有 hash，空哈希会拼出 /textures/ 拿到 404。
        var handler = new CapturingHandler(request =>
            request.RequestUri!.AbsolutePath == "/texture/9"
                ? Json(HttpStatusCode.OK, """{"hash":"abc123"}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.DownloadTextureAsync(9, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Value!.Length);
        Assert.Collection(
            handler.Requests,
            first => Assert.Equal("/texture/9", first.PathAndQuery),
            second => Assert.Equal("/textures/abc123", second.PathAndQuery));
    }

    [Fact]
    public async Task 材质行带hash时直接下载不再查详情()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) });
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.DownloadTextureAsync(9, "deadbeef", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/textures/deadbeef", Assert.Single(handler.Requests).PathAndQuery);
    }

    [Theory]
    [InlineData("", "https://littleskin.cn")]
    [InlineData("littleskin.cn", "https://littleskin.cn")]
    [InlineData("http://example.com/", "http://example.com")]
    [InlineData(" https://example.com/// ", "https://example.com")]
    public void 站址规范化(string input, string expected)
    {
        Assert.Equal(expected, CloudClosetClient.NormalizeBaseAddress(input));
    }

    [Fact]
    public void 凭据键按站点与用户名归一并忽略大小写()
    {
        Assert.Equal(
            "https://example.com|me",
            CloudClosetClient.CredentialKey("HTTPS://Example.com/", " Me "));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers
                    .Where(pair => pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value!), StringComparer.OrdinalIgnoreCase),
                body));
            return responder(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        public string? Header(string name)
            => Headers.TryGetValue(name, out var value) ? value : null;
    }
}
