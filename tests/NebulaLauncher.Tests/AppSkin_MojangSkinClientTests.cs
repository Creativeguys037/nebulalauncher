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
/// 官方（Microsoft 账户）换肤客户端：只测请求形状与失败态映射，一律走假 handler。
///
/// 官方端点与皮肤站有三处不能混用的差异（票 #44）：<c>variant</c> 只认
/// <c>classic</c>/<c>slim</c>（皮肤站经典体型是空串）；官方<b>没有</b>披风上传接口；
/// 401 表示 Minecraft access token 过期，必须提示去账户页刷新而不是自动重试。
///
/// 失败一律走 <see cref="SkinUploadResult"/>（CONVENTIONS §3：服务不把异常抛给 ViewModel），
/// 因此断言落在 <c>Succeeded</c>/<c>ErrorText</c>/<c>IsAuthError</c>/<c>IsRetryable</c> 上。
/// </summary>
public class AppSkin_MojangSkinClientTests
{
    private const string SkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";

    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private const string CapeUrl = "https://api.minecraftservices.com/minecraft/profile/capes/active";

    private const string Token = "mc-access-token";

    [Fact]
    public async Task 上传皮肤用POST发multipart且Steve取classic()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(SkinsUrl, request.Url);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Equal("classic", PartValue(request.Body, "variant"));
        Assert.Contains("name=file", request.Body);
        Assert.Contains("filename=skin.png", request.Body);
        Assert.Contains("Content-Type: image/png", request.Body);
    }

    [Fact]
    public async Task Alex体型上传时variant取slim()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Alex, Png(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("slim", PartValue(Assert.Single(handler.Requests).Body, "variant"));
    }

    [Fact]
    public async Task 重置皮肤用DELETE打active端点且不带请求体()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.ResetSkinAsync(Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{SkinsUrl}/active", request.Url);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Equal(string.Empty, request.Body);
    }

    [Fact]
    public async Task 令牌过期时提示去账户页刷新且只发一次请求()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.Unauthorized, """{"path":"/minecraft/profile/skins","errorMessage":"Invalid token."}"""));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("登录已过期", result.ErrorText!);
        Assert.Contains("账户页刷新令牌", result.ErrorText!);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 载荷无效的400写明尺寸要求且附服务端原文()
    {
        var handler = new CapturingHandler(_ => Json(
            HttpStatusCode.BadRequest,
            """{"path":"/minecraft/profile/skins","errorMessage":"Invalid skin file"}"""));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("64×64", result.ErrorText!);
        Assert.Contains("64×32", result.ErrorText!);
        Assert.Contains("Invalid skin file", result.ErrorText!);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task 限流与服务端故障按无网络处理且允许重试(int status)
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("无网络", result.ErrorText!);

        // 重试与限速是上层的事：客户端自己只发这一次。
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 连不上时按无网络处理且允许重试()
    {
        using var client = new MojangSkinClient(new HttpClient(new ThrowingHandler(new HttpRequestException("boom"))));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("无网络", result.ErrorText!);
    }

    [Fact]
    public async Task 请求超时按无网络处理且允许重试()
    {
        var timeout = new TaskCanceledException("timeout", new TimeoutException());
        using var client = new MojangSkinClient(new HttpClient(new ThrowingHandler(timeout)));

        var result = await client.UploadSkinAsync(Token, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("无网络", result.ErrorText!);
    }

    // ==================== 披风（票 #61） ====================

    [Fact]
    public async Task 读取档案解析披风状态与皮肤图地址()
    {
        const string body = """
            {"skins":[{"id":"s1","state":"ACTIVE","url":"https://cdn.example/skin.png"}],
             "capes":[{"id":"c1","state":"ACTIVE","url":"https://cdn.example/c1.png","alias":"Migrator"},
                      {"id":"c2","state":"INACTIVE","url":"https://cdn.example/c2.png","alias":"Vanilla"}]}
            """;
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, body));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.GetProfileAsync(Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://cdn.example/skin.png", result.Value!.SkinUrl);
        Assert.Collection(
            result.Value.Capes,
            cape =>
            {
                Assert.Equal("c1", cape.Id);
                Assert.True(cape.IsActive);
                Assert.Equal("Migrator", cape.Alias);
                Assert.Equal("https://cdn.example/c1.png", cape.Url);
            },
            cape =>
            {
                Assert.Equal("c2", cape.Id);
                Assert.False(cape.IsActive);
            });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(ProfileUrl, request.Url);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
    }

    [Fact]
    public async Task 档案没有披风时返回空列表而不是失败()
    {
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, """{"skins":[],"capes":[]}"""));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.GetProfileAsync(Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Capes);
        Assert.Null(result.Value.SkinUrl);
    }

    [Fact]
    public async Task 档案里缺字段的披风条目被跳过()
    {
        const string body = """{"capes":[{"state":"ACTIVE"},{"id":"c2","state":"INACTIVE"}]}""";
        var handler = new CapturingHandler(_ => Json(HttpStatusCode.OK, body));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.GetProfileAsync(Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        var cape = Assert.Single(result.Value!.Capes);
        Assert.Equal("c2", cape.Id);
        Assert.False(cape.IsActive);
        Assert.Null(cape.Alias);
    }

    [Fact]
    public async Task 切换披风用PUT发capeId的JSON()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.SetActiveCapeAsync(Token, "cape-123", CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(CapeUrl, request.Url);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Contains("\"capeId\":\"cape-123\"", request.Body);
    }

    [Fact]
    public async Task 隐藏披风用DELETE打active端点且不带请求体()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.HideCapeAsync(Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(CapeUrl, request.Url);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Equal(string.Empty, request.Body);
    }

    [Fact]
    public async Task 切换披风被400拒绝且错误体为空时用本地兜底文案()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.SetActiveCapeAsync(Token, "gone", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("不在当前账户的已拥有列表里", result.ErrorText!);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 切换披风遇到404时仍用本地已拥有列表兜底()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.SetActiveCapeAsync(Token, "gone", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("不在当前账户的已拥有列表里", result.ErrorText!);
    }

    [Fact]
    public async Task 切换披风被限流时按无网络处理且允许重试()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.SetActiveCapeAsync(Token, "cape-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("官方服务暂时不可用", result.ErrorText!);
    }

    [Fact]
    public async Task 读取档案时令牌过期仍然提示去账户页刷新()
    {
        var handler = new CapturingHandler(_ => Json(
            HttpStatusCode.Unauthorized,
            """{"path":"/minecraft/profile","errorMessage":"Invalid token."}"""));
        using var client = new MojangSkinClient(new HttpClient(handler));

        var result = await client.GetProfileAsync(Token, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("登录已过期", result.ErrorText!);
    }

    /// <summary>客户端不校验 PNG 尺寸（那是 Core 校验器与 UI 前置拦截的职责），给一小段字节即可。</summary>
    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// 取 multipart 里某个字段的正文。header 顺序由框架决定，不能假设
    /// <c>name=variant</c> 与值相邻，所以定位到该段头之后第一个空行、再截到下一个边界。
    /// </summary>
    private static string? PartValue(string body, string name)
    {
        var marker = $"name={name}";
        var found = body.IndexOf(marker, StringComparison.Ordinal);
        if (found < 0)
        {
            return null;
        }

        var start = body.IndexOf("\r\n\r\n", found, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += 4;
        var end = body.IndexOf("\r\n--", start, StringComparison.Ordinal);
        return end < 0 ? null : body[start..end];
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);

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
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));

            return responder(request);
        }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }
}
