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
/// 皮肤站 OAuth 设备代码流。锁的是设备码/轮询/refresh 与 Bearer 调用的外部契约：
/// 请求参数、轮询状态机、令牌替换、以及「OAuth 模式下哪些动作明确不做」。
/// 站点实测见 docs/research/littleskin-oauth-device-flow-20261102.md。
/// </summary>
public sealed class AppSkin_CloudClosetOAuthTests
{
    private const string Site = "https://example.com";

    [Fact]
    public async Task 设备码请求带上客户端与读写权限()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, DeviceCodeJson()));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.StartDeviceLoginAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/oauth/device_code", request.PathAndQuery);
        var body = WebUtility.UrlDecode(request.Body);
        Assert.Contains("client_id=1674", body, StringComparison.Ordinal);
        Assert.Contains("Player.ReadWrite", body, StringComparison.Ordinal);
        Assert.Contains("Closet.Read", body, StringComparison.Ordinal);
        Assert.Contains("offline_access", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 设备码响应解析出用户码与直达链接()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, DeviceCodeJson()));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var result = await client.StartDeviceLoginAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var code = result.Value!;
        Assert.Equal("dev-1", code.DeviceCode);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("https://example.com/oauth/link", code.VerificationUri);
        Assert.Equal("https://example.com/oauth/link?user_code=ABCD-1234", code.VerificationUriComplete);
        Assert.Equal(1, code.Interval);
    }

    [Fact]
    public async Task 轮询先待授权再拿到令牌()
    {
        var polls = 0;
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/device_code")
            {
                return Json(HttpStatusCode.OK, DeviceCodeJson());
            }

            polls++;
            return polls == 1
                ? Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""")
                : Json(HttpStatusCode.OK, TokenJson("access-1", "refresh-1"));
        });
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var code = (await client.StartDeviceLoginAsync(CancellationToken.None)).Value!;
        var result = await client.WaitForDeviceLoginAsync(code, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("access-1", result.Value!.AccessToken);
        Assert.Equal("refresh-1", result.Value.RefreshToken);
        Assert.True(client.IsOAuthSession);
        Assert.Equal(2, polls);
        Assert.All(
            handler.Requests.Where(item => item.PathAndQuery == "/oauth/token"),
            item => Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", item.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 轮询遇到过期码停止并提示重新发起()
    {
        var handler = new CapturingHandler(request =>
            request.RequestUri!.AbsolutePath == "/oauth/device_code"
                ? Json(HttpStatusCode.OK, DeviceCodeJson())
                : Json(HttpStatusCode.BadRequest, """{"error":"expired_token"}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);

        var code = (await client.StartDeviceLoginAsync(CancellationToken.None)).Value!;
        var result = await client.WaitForDeviceLoginAsync(code, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("过期", result.ErrorText, StringComparison.Ordinal);
        Assert.False(client.IsOAuthSession);
    }

    [Fact]
    public async Task 免密会话下角色列表走api并带Bearer()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, PlayerJson()));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);
        client.UseOAuthToken("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var result = await client.GetPlayersAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Steve", Assert.Single(result.Value!).Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/players", request.PathAndQuery);
        Assert.Equal("Bearer access-1", request.Header("Authorization"));
    }

    [Fact]
    public async Task 免密会话下应用材质PUT到api并保留皮肤字段()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, """{"code":0,"message":"ok","data":{}}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);
        client.UseOAuthToken("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var result = await client.ApplyAsync(7, "skin", 42, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/players/7/textures", request.PathAndQuery);
        Assert.Equal("""{"skin":42}""", request.Body);
    }

    [Fact]
    public async Task 免密会话下上传材质被拒绝且不发请求()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);
        client.UseOAuthToken("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var result = await client.UploadTextureAsync("x", [1, 2, 3], "steve", true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("网页会话", result.ErrorText, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 免密会话下删除材质被拒绝且不发请求()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);
        client.UseOAuthToken("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var result = await client.DeleteTextureAsync(42, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("网页会话", result.ErrorText, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Bearer收到401时用refresh换新令牌再重试()
    {
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
            {
                return Json(HttpStatusCode.OK, TokenJson("access-2", "refresh-2"));
            }

            return request.Headers.Authorization?.Parameter == "access-2"
                ? Json(HttpStatusCode.OK, PlayerJson())
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var client = new CloudClosetClient(new HttpClient(handler), Site);
        client.UseOAuthToken("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var result = await client.GetPlayersAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(
            handler.Requests,
            first => Assert.Equal("/api/players", first.PathAndQuery),
            second => Assert.Equal("/oauth/token", second.PathAndQuery),
            third => Assert.Equal("Bearer access-2", third.Header("Authorization")));
        Assert.Contains("grant_type=refresh_token", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task api返回200但code非零时按失败处理并透出站点消息()
    {
        var handler = new CapturingHandler(_ =>
            Json(HttpStatusCode.OK, """{"code":403,"message":"无权访问"}"""));
        using var client = new CloudClosetClient(new HttpClient(handler), Site);
        client.UseOAuthToken("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

        var result = await client.GetPlayersAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("无权访问", result.ErrorText, StringComparison.Ordinal);
    }

    private static string DeviceCodeJson() =>
        """
        {
          "device_code": "dev-1",
          "user_code": "ABCD-1234",
          "verification_uri": "https://example.com/oauth/link",
          "verification_uri_complete": "https://example.com/oauth/link?user_code=ABCD-1234",
          "expires_in": 300,
          "interval": 1
        }
        """;

    private static string TokenJson(string access, string refresh) =>
        $$"""
        {
          "token_type": "Bearer",
          "expires_in": 259200,
          "access_token": "{{access}}",
          "refresh_token": "{{refresh}}"
        }
        """;

    private static string PlayerJson() =>
        """
        [
          {"pid":7,"uid":1,"name":"Steve","uuid":"00000000-0000-0000-0000-000000000000","tid_skin":42,"tid_cape":0}
        ]
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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
