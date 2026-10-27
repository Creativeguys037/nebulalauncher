using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤站（authlib/Yggdrasil）换肤客户端。
///
/// 与官方换肤（<see cref="AppSkin_MojangSkinClientTests"/>）的关键差异：
/// 皮肤站的 <c>model</c> 只认 <c>slim</c> 与空串（空串＝Steve），传官方那个
/// <c>classic</c> 会被服务端当成未知体型；这条差异必须被断言钉住。
///
/// 地址形状以《Yggdrasil 服务端技术规范》与参考实现 yggdrasil-mock 为准：
/// 规范里的 <c>/api/user/profile/{uuid}/{textureType}</c> 是<b>相对 API 根</b>的，
/// 与 <c>/api/profiles/minecraft</c>、<c>/sessionserver/…</c> 同一套拼法
/// （实测 <c>POST {apiRoot}/api/profiles/minecraft</c> 才返回 200）。
/// </summary>
public class AppSkin_AuthlibSkinClientTests
{
    private const string ApiRoot = "https://littleskin.cn/api/yggdrasil";

    private const string Uuid = "0123456789abcdef0123456789abcdef";

    private const string Token = "yggdrasil-access-token";

    [Theory]
    [InlineData("skin")]
    [InlineData("cape")]
    public void 材质地址拼在API根之下而不是站点根(string textureType)
    {
        Assert.Equal(
            $"https://littleskin.cn/api/yggdrasil/api/user/profile/{Uuid}/{textureType}",
            AuthlibSkinClient.BuildTextureUrl(ApiRoot, Uuid, textureType));
    }

    [Fact]
    public void API根尾部斜杠不影响材质地址()
    {
        Assert.Equal(
            $"https://littleskin.cn/api/yggdrasil/api/user/profile/{Uuid}/skin",
            AuthlibSkinClient.BuildTextureUrl("https://littleskin.cn/api/yggdrasil/", Uuid, "skin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void API根为空白时拿不到材质地址(string apiRoot)
    {
        Assert.Null(AuthlibSkinClient.BuildTextureUrl(apiRoot, Uuid, "skin"));
    }

    [Fact]
    public void 没有API根时拿不到材质地址()
    {
        // 单独一条 Fact：InlineData 传 null 在 TestRunner 的反射调用里参数个数对不上。
        Assert.Null(AuthlibSkinClient.BuildTextureUrl(null, Uuid, "skin"));
    }

    [Fact]
    public void 没有uuid时拿不到材质地址()
    {
        Assert.Null(AuthlibSkinClient.BuildTextureUrl(ApiRoot, "  ", "skin"));
    }

    [Fact]
    public async Task 上传皮肤用PUT打multipart且带Bearer令牌()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new AuthlibSkinClient(new HttpClient(handler));
        var account = Account();

        var result = await client.UploadSkinAsync(account, SkinModelKind.Alex, Png(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{ApiRoot}/api/user/profile/{Uuid}/skin", request.Url);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Contains("name=file", request.Body);
        Assert.Contains("Content-Type: image/png", request.Body);
    }

    /// <summary>
    /// 皮肤站与官方最要命的一处差异：<c>model</c> 只认 <c>slim</c> 与空串（空串＝Steve）。
    /// 两条都断言「不是 classic」，防的是照抄官方客户端把 <c>classic</c> 发出去。
    /// </summary>
    [Fact]
    public async Task Alex体型上传时model取slim而不是classic()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Account(), SkinModelKind.Alex, Png(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var model = PartValue(Assert.Single(handler.Requests).Body, "model");
        Assert.Equal("slim", model);
        Assert.NotEqual("classic", model);
    }

    [Fact]
    public async Task Steve体型上传时model取空串而不是classic()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Account(), SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var model = PartValue(Assert.Single(handler.Requests).Body, "model");
        Assert.Equal(string.Empty, model);
        Assert.NotEqual("classic", model);
    }

    /// <summary>披风没有体型，规范里 <c>model</c> 标的是「仅用于皮肤」，所以披风请求不该带这个字段。</summary>
    [Fact]
    public async Task 上传披风用PUT且不带model字段()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadCapeAsync(Account(), Png(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{ApiRoot}/api/user/profile/{Uuid}/cape", request.Url);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.DoesNotContain("name=model", request.Body);
        Assert.Contains("name=file", request.Body);
    }

    [Theory]
    [InlineData("skin")]
    [InlineData("cape")]
    public async Task 删除材质用DELETE且不带请求体(string textureType)
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new AuthlibSkinClient(new HttpClient(handler));
        var account = Account();

        var result = textureType == "skin"
            ? await client.DeleteSkinAsync(account, CancellationToken.None)
            : await client.DeleteCapeAsync(account, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{ApiRoot}/api/user/profile/{Uuid}/{textureType}", request.Url);
        Assert.Equal(Token, request.AuthorizationParameter);
        Assert.Equal(string.Empty, request.Body);
    }

    [Fact]
    public async Task 令牌过期时提示去账户页刷新且只发一次请求()
    {
        var handler = new CapturingHandler(_ => Json(
            HttpStatusCode.Unauthorized,
            """{"error":"ForbiddenOperationException","errorMessage":"Invalid token."}"""));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Account(), SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("登录已过期", result.ErrorText!);
        Assert.Contains("账户页刷新令牌", result.ErrorText!);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 尺寸或格式不对的400写成要求且附服务端原文()
    {
        var handler = new CapturingHandler(_ => Json(
            HttpStatusCode.BadRequest,
            """{"error":"IllegalArgumentException","errorMessage":"bad image"}"""));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Account(), SkinModelKind.Alex, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("64×64", result.ErrorText!);
        Assert.Contains("64×32", result.ErrorText!);
        Assert.Contains("bad image", result.ErrorText!);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task 限流与服务端故障按无网络处理且允许重试(int status)
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadSkinAsync(Account(), SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("无网络", result.ErrorText!);

        // 重试与限速是上层的事：客户端自己只发这一次。
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 角色不允许上传该材质时403给出明确提示且不打可重试()
    {
        var handler = new CapturingHandler(_ => Json(
            HttpStatusCode.Forbidden,
            """{"error":"ForbiddenOperationException","errorMessage":"Access denied."}"""));
        using var client = new AuthlibSkinClient(new HttpClient(handler));

        var result = await client.UploadCapeAsync(Account(), Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsRetryable);
        Assert.Contains("不允许上传", result.ErrorText!);
    }

    [Fact]
    public async Task 连不上时按无网络处理且允许重试()
    {
        using var client = new AuthlibSkinClient(new HttpClient(new ThrowingHandler(new HttpRequestException("boom"))));

        var result = await client.UploadSkinAsync(Account(), SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("无网络", result.ErrorText!);
    }

    [Fact]
    public async Task 请求超时按无网络处理且允许重试()
    {
        var timeout = new TaskCanceledException("timeout", new TimeoutException());
        using var client = new AuthlibSkinClient(new HttpClient(new ThrowingHandler(timeout)));

        var result = await client.DeleteSkinAsync(Account(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsAuthError);
        Assert.True(result.IsRetryable);
        Assert.Contains("无网络", result.ErrorText!);
    }

    [Fact]
    public async Task 用户主动取消时按取消上抛而不是当成网络错误()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var client = new AuthlibSkinClient(new HttpClient(new ThrowingHandler(new TaskCanceledException())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DeleteCapeAsync(Account(), cancellation.Token));
    }

    [Fact]
    public async Task 离线账户上传不发请求而是直接给出账户类型提示()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new AuthlibSkinClient(new HttpClient(handler));
        var account = new Account { Type = AccountType.Offline, Uuid = Uuid };

        var result = await client.UploadSkinAsync(account, SkinModelKind.Steve, Png(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("皮肤站账户", result.ErrorText!);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void 属性缺失时完全不可上传()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: null);

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.False(capabilities.CanUploadSkin);
        Assert.False(capabilities.CanUploadCape);
    }

    [Fact]
    public void 属性为skin时只允许上传皮肤()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: "skin");

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.True(capabilities.CanUploadSkin);
        Assert.False(capabilities.CanUploadCape);
    }

    [Fact]
    public void 属性为skincape时两种材质都能上传()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: "skin,cape");

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.True(capabilities.CanUploadSkin);
        Assert.True(capabilities.CanUploadCape);
    }

    [Fact]
    public void 属性为cape时只允许上传披风()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: "cape");

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.False(capabilities.CanUploadSkin);
        Assert.True(capabilities.CanUploadCape);
    }

    [Fact]
    public void 属性为空串时完全不可上传()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: string.Empty);

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.False(capabilities.CanUploadSkin);
        Assert.False(capabilities.CanUploadCape);
    }

    [Fact]
    public void 属性含空白与重复项时按逗号归一()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: " skin , cape , skin ");

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.True(capabilities.CanUploadSkin);
        Assert.True(capabilities.CanUploadCape);
    }

    [Fact]
    public void 属性里出现未知材质类型时忽略它()
    {
        var profile = ProfileJson(PayloadJson(), uploadableTextures: "skin,elytra");

        var capabilities = SkinProfileParser.ParseCapabilities(profile);

        Assert.True(capabilities.CanUploadSkin);
        Assert.False(capabilities.CanUploadCape);
    }

    [Fact]
    public void 完全没有角色属性时按不可上传处理()
    {
        Assert.False(SkinProfileParser.ParseCapabilities(null).CanUploadSkin);
        Assert.False(SkinProfileParser.ParseCapabilities("{}").CanUploadSkin);
        Assert.False(SkinProfileParser.ParseCapabilities("{\"properties\":\"不是数组\"}").CanUploadSkin);
    }

    /// <summary>客户端不校验 PNG 尺寸（那是 Core 校验器与 UI 前置拦截的职责），给一小段字节即可。</summary>
    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string PayloadJson() =>
        "{\"timestamp\":0,\"profileId\":\"0123\",\"profileName\":\"Steve\",\"textures\":"
        + "{\"SKIN\":{\"url\":\"https://example.com/textures/skin.png\"}}}";

    /// <summary>
    /// 拼一份 profile 响应。<paramref name="uploadableTextures"/> 为 <c>null</c> 表示
    /// 该属性<b>不存在</b>（Mojang 官方角色就是这样），空串则是属性存在但没有内容。
    /// </summary>
    private static string ProfileJson(string payload, string? uploadableTextures)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var properties = "{\"name\":\"textures\",\"value\":\"" + encoded + "\"}";
        if (uploadableTextures is not null)
        {
            properties += ",{\"name\":\"uploadableTextures\",\"value\":\"" + uploadableTextures + "\"}";
        }

        return "{\"id\":\"0123\",\"name\":\"Steve\",\"properties\":[" + properties + "]}";
    }

    private static Account Account() => new()
    {
        Type = AccountType.Authlib,
        Uuid = Uuid,
        AccessToken = Token,
        AuthServerUrl = ApiRoot,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// 取 multipart 里某个字段的正文。header 顺序由框架决定，不能假设
    /// <c>name=model</c> 与值相邻，所以定位到该段头之后第一个空行、再截到下一个边界。
    /// </summary>
    private static string? PartValue(string body, string name)
    {
        var found = body.IndexOf($"name={name}\r\n", StringComparison.Ordinal);
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
