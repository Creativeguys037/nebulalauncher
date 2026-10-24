using System.Net;
using System.Text;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Logging;

namespace NebulaLauncher.Tests;

/// <summary>
/// 微软设备码登录的 client id 契约。
///
/// 背景（R46 → R55）：R46 曾内置 <c>00000000402b5328</c>，那是 MSA 旧应用模型，
/// 在 <c>login.microsoftonline.com/consumers</c> 上会被拒（AADSTS700016），于是当时
/// 改成「必须由部署方提供 client id」——结果是用户开箱登录直接失败。
/// R55 改为内置一个在 consumers 租户登记过的公共应用 id（设备码是 public client 流程，
/// 无 secret），凭据链仍允许环境变量覆盖。本文件覆盖：默认回落、覆盖优先级、
/// 显式禁用、以及 AAD 错误码的文案翻译。
/// </summary>
public class CoreAccounts_MicrosoftAuthTests
{
    [Fact]
    public async Task 显式空clientId_禁用登录并返回可读错误()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: string.Empty);

        var result = await auth.SignInAsync();

        Assert.False(result.Success);
        Assert.Contains(MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task 配置clientId_请求打到设备码端点且带client_id()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, "{\"error\":\"unauthorized_client\"}");
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "test-client-id");

        var result = await auth.SignInAsync();

        Assert.False(result.Success);
        Assert.Equal(1, handler.Count);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains(
            "login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            handler.LastUri!.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Contains("client_id=test-client-id", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("scope=XboxLive.signin", handler.LastBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空白clientId_按禁用处理(string configured)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: configured);

        var result = await auth.SignInAsync();

        Assert.False(result.Success);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task 续期未配置clientId_也给出可读错误()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: string.Empty);

        var result = await auth.RefreshAsync("some-refresh-token");

        Assert.False(result.Success);
        Assert.Contains(MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public void 内置默认clientId_是合法GUID()
    {
        // 端点只接受 GUID 形态的应用 id；写成别的形式会在运行期变成 AADSTS700016。
        Assert.True(Guid.TryParse(MicrosoftDeviceCodeAuth.DefaultClientId, out _));
    }

    [Fact]
    public async Task 未设置环境变量_回落到内置默认clientId()
    {
        var saved = ClientIdEnvironment.Snapshot();

        try
        {
            ClientIdEnvironment.Clear();

            var handler = new RecordingHandler(
                HttpStatusCode.BadRequest,
                "{\"error\":\"unauthorized_client\"}");
            using var http = new HttpClient(handler);
            var auth = new MicrosoftDeviceCodeAuth(http, NullAppLog.Instance, new FakePlatformService());

            Assert.Equal(MicrosoftDeviceCodeAuth.DefaultClientId, auth.ClientId);

            await auth.SignInAsync();

            // 关键契约：不配置任何环境变量也会真的向设备码端点发请求，而不是 0 秒早退。
            Assert.Equal(1, handler.Count);
            Assert.Contains(
                $"client_id={MicrosoftDeviceCodeAuth.DefaultClientId}",
                handler.LastBody,
                StringComparison.Ordinal);
        }
        finally
        {
            saved.Restore();
        }
    }

    [Fact]
    public async Task 环境变量优先于内置默认clientId()
    {
        var saved = ClientIdEnvironment.Snapshot();

        try
        {
            ClientIdEnvironment.Clear();
            Environment.SetEnvironmentVariable(
                MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable,
                "env-client-id");

            var handler = new RecordingHandler(
                HttpStatusCode.BadRequest,
                "{\"error\":\"unauthorized_client\"}");
            using var http = new HttpClient(handler);
            var auth = new MicrosoftDeviceCodeAuth(http, NullAppLog.Instance, new FakePlatformService());

            Assert.Equal("env-client-id", auth.ClientId);

            await auth.SignInAsync();

            Assert.Contains("client_id=env-client-id", handler.LastBody, StringComparison.Ordinal);
        }
        finally
        {
            saved.Restore();
        }
    }

    [Fact]
    public async Task PCL兼容变量_在NEBULA变量为空时生效()
    {
        var saved = ClientIdEnvironment.Snapshot();

        try
        {
            ClientIdEnvironment.Clear();
            Environment.SetEnvironmentVariable(
                MicrosoftDeviceCodeAuth.LegacyClientIdEnvironmentVariable,
                "pcl-client-id");

            var handler = new RecordingHandler(
                HttpStatusCode.BadRequest,
                "{\"error\":\"unauthorized_client\"}");
            using var http = new HttpClient(handler);
            var auth = new MicrosoftDeviceCodeAuth(http, NullAppLog.Instance, new FakePlatformService());

            Assert.Equal("pcl-client-id", auth.ClientId);

            await auth.SignInAsync();

            Assert.Contains("client_id=pcl-client-id", handler.LastBody, StringComparison.Ordinal);
        }
        finally
        {
            saved.Restore();
        }
    }

    [Fact]
    public async Task 设备码被AADSTS700016拒绝_文案指出clientId而不是网络()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":\"unauthorized_client\","
            + "\"error_description\":\"AADSTS700016: Application with identifier 'x' "
            + "was not found in the directory 'consumers'.\"}");
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "bad-client");

        var result = await auth.SignInAsync();

        Assert.False(result.Success);
        Assert.Contains("AADSTS700016", result.Message, StringComparison.Ordinal);
        Assert.Contains("client id", result.Message, StringComparison.Ordinal);
        Assert.Contains(MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable, result.Message, StringComparison.Ordinal);

        // R46 的症状就是被误报成网络问题：这条不能再回来。
        Assert.DoesNotContain("检查网络", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 设备码申请遇503_文案为稍后重试()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.ServiceUnavailable,
            "<html>gateway</html>");
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "test-client");

        var result = await auth.SignInAsync();

        Assert.False(result.Success);
        Assert.Contains("稍后重试", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 设备码 → token → Xbox user → XSTS → Minecraft login → profile 全链路成功。
    /// 全程假服务端，验证的是 Nebula 自己的接线与字段解析（真实服务需要人工账号授权）。
    /// </summary>
    [Fact]
    public async Task 全链路成功_返回账户与令牌()
    {
        var handler = new FullChainHandler();
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "test-client");

        var result = await auth.SignInAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal("Steve", result.Username);
        Assert.Equal("0123456789abcdef0123456789abcdef", result.Uuid);
        Assert.Equal("mc-access-token", result.AccessToken);
        Assert.Equal("ms-refresh-token", result.RefreshToken);

        // 六个端点各调用一次，顺序与链路一致。
        Assert.Equal(
            ["devicecode", "token", "xbox-user", "xsts", "minecraft-login", "profile"],
            handler.Calls.ToArray());
    }

    /// <summary>按 host + path 分发六个端点的假服务端。</summary>
    private sealed class FullChainHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;

            var (tag, body) = uri.Host switch
            {
                "login.microsoftonline.com" when path.EndsWith("/devicecode", StringComparison.Ordinal) =>
                    ("devicecode", """{"device_code":"dc-1","user_code":"USER-CODE","verification_uri":"https://www.microsoft.com/link","expires_in":900,"interval":1,"message":"用浏览器打开并输入代码 USER-CODE"}"""),
                "login.microsoftonline.com" when path.EndsWith("/token", StringComparison.Ordinal) =>
                    ("token", """{"access_token":"msa-token","refresh_token":"ms-refresh-token"}"""),
                "user.auth.xboxlive.com" =>
                    ("xbox-user", """{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"user-hash"}]}}"""),
                "xsts.auth.xboxlive.com" =>
                    ("xsts", """{"Token":"xsts-token"}"""),
                "api.minecraftservices.com" when path.EndsWith("/login_with_xbox", StringComparison.Ordinal) =>
                    ("minecraft-login", """{"access_token":"mc-access-token","expires_in":86400}"""),
                "api.minecraftservices.com" when path.EndsWith("/minecraft/profile", StringComparison.Ordinal) =>
                    ("profile", """{"id":"0123456789ABCDEF0123456789abcdef","name":"Steve"}"""),
                _ => (uri.Host, "{}"),
            };

            Calls.Add(tag);

            // 顺带把请求体读掉：链路上每步都依赖上一步输出的 token，读不到 body 就说明接线错了。
            if (request.Content is not null)
            {
                _ = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task 续期遇到5xx_标记为可重试()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"temporarily_unavailable\"}");
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "test-client");

        var result = await auth.RefreshAsync("some-refresh-token");

        // 临时故障不能报成「令牌已失效」：首页会据此拒绝启动（REVIEW-R43-R51 P2-4）。
        Assert.False(result.Success);
        Assert.True(result.Retryable);
        Assert.Contains("稍后重试", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 续期遇到invalid_grant_标记为不可重试()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "test-client");

        var result = await auth.RefreshAsync("some-refresh-token");

        Assert.False(result.Success);
        Assert.False(result.Retryable);
        Assert.Contains("登录状态已失效", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task clientId带引号_自动剥离()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceCodeAuth(
            http,
            NullAppLog.Instance,
            new FakePlatformService(),
            clientId: "\"quoted-client\"");

        Assert.Equal("quoted-client", auth.ClientId);

        await auth.SignInAsync();

        Assert.Contains("client_id=quoted-client", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%22", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两个 client id 环境变量的快照/还原。
    ///
    /// 解析链在构造时读进程级环境变量，所以「回落到内置 id」这类用例必须把变量清空；
    /// 改动进程级状态后无论断言是否通过都要还原，否则同进程后续用例会读到测试值。
    /// </summary>
    private sealed record ClientIdEnvironment(string? Nebula, string? Legacy)
    {
        public static ClientIdEnvironment Snapshot() => new(
            Environment.GetEnvironmentVariable(MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable),
            Environment.GetEnvironmentVariable(MicrosoftDeviceCodeAuth.LegacyClientIdEnvironmentVariable));

        public static void Clear()
        {
            Environment.SetEnvironmentVariable(MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(MicrosoftDeviceCodeAuth.LegacyClientIdEnvironmentVariable, null);
        }

        public void Restore()
        {
            Environment.SetEnvironmentVariable(MicrosoftDeviceCodeAuth.ClientIdEnvironmentVariable, Nebula);
            Environment.SetEnvironmentVariable(MicrosoftDeviceCodeAuth.LegacyClientIdEnvironmentVariable, Legacy);
        }
    }

    /// <summary>记录请求方法/URL/body 的假 handler。</summary>
    private sealed class RecordingHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}") : HttpMessageHandler
    {
        public int Count { get; private set; }

        public string? LastMethod { get; private set; }

        public Uri? LastUri { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            LastMethod = request.Method.Method;
            LastUri = request.RequestUri;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
