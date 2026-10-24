using System.Net;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 第三方验证（authlib-injector）登录：认证服务本身 + 与 <see cref="AccountService"/> 的集成。
/// 全程由假 <see cref="HttpMessageHandler"/> 应答，不联网；判据是「返回的中文原因」与「accounts.json 里发生了什么」。
/// </summary>
public class CoreAssets_AuthlibTests
{
    private const string ApiRoot = "https://skin.example.invalid/api/yggdrasil";

    private const string ProfileId = "069a79f444e94726a5befca90e38aaf5";

    private static readonly string AuthenticateOkBody = $$"""
        {
          "accessToken": "tok-1",
          "clientToken": "client-1",
          "selectedProfile": { "id": "{{ProfileId}}", "name": "Steve" },
          "availableProfiles": [
            { "id": "{{ProfileId}}", "name": "Steve" },
            { "id": "11111111111111111111111111111111", "name": "Alex" }
          ]
        }
        """;

    [Fact]
    public async Task 登录成功_解析令牌与角色并落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
        };

        using var http = new HttpClient(handler);
        var service = new AccountService(
            paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false },
            http);

        await service.LoadAsync();

        // 用户粘进来的地址带尾斜杠，必须被规范化后再发请求、再落盘。
        var result = await service.AddAuthlibAccountAsync(ApiRoot + "/", "steve@example.invalid", "pw");

        Assert.True(result.Success, result.Message);
        var account = result.Account!;
        Assert.Equal(AccountType.Authlib, account.Type);
        Assert.Equal("Steve", account.Username);
        Assert.Equal(ProfileId, account.Uuid);
        Assert.Equal("tok-1", account.AccessToken);
        Assert.Equal("client-1", account.RefreshToken);
        Assert.Null(account.ExpiresAt);
        Assert.Equal(ApiRoot, account.AuthServerUrl);
        Assert.Equal(account.Id, service.ActiveAccount?.Id);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post.Method, call.Method);
        Assert.Equal("/api/yggdrasil/authserver/authenticate", call.Path);

        using var request = JsonDocument.Parse(call.Body);
        var root = request.RootElement;
        Assert.Equal("steve@example.invalid", root.GetProperty("username").GetString());
        Assert.Equal("pw", root.GetProperty("password").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("clientToken").GetString()));
        Assert.Equal("Minecraft", root.GetProperty("agent").GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("agent").GetProperty("version").GetInt32());
        Assert.False(root.GetProperty("requestUser").GetBoolean());

        // 落盘 → 重新加载：AuthServerUrl 是新增字段，必须能往返；令牌经 TokenProtector 解密回明文。
        var reloaded = new AccountService(
            paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false },
            new HttpClient(new AuthlibHandler()));

        await reloaded.LoadAsync();

        var persisted = Assert.Single(reloaded.Accounts);
        Assert.Equal(ApiRoot, persisted.AuthServerUrl);
        Assert.Equal("tok-1", persisted.AccessToken);
        Assert.Equal("client-1", persisted.RefreshToken);
    }

    [Theory]
    [InlineData(401, "账号或密码错误")]
    [InlineData(403, "未拥有游戏")]
    [InlineData(429, "请求过于频繁")]
    public async Task 登录失败_按状态码给出中文原因(int statusCode, string expected)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(
                (HttpStatusCode)statusCode,
                """{"error":"ForbiddenOperationException","errorMessage":"Invalid credentials."}"""),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var result = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "bad-password");

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message);
        Assert.Empty(service.Accounts);
        Assert.False(File.Exists(paths.AccountsFile));
    }

    [Fact]
    public async Task 登录失败_网络异常给出无法连接文案()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler { Failure = new HttpRequestException("connection refused") };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var result = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");

        Assert.False(result.Success);
        Assert.Contains("无法连接验证服务器", result.Message);
        Assert.Empty(service.Accounts);
    }

    [Fact]
    public async Task 登录失败_服务器地址非法时不发请求()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler();

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var result = await service.AddAuthlibAccountAsync("littleskin.cn/api/yggdrasil", "steve", "pw");

        Assert.False(result.Success);
        Assert.Contains("验证服务器", result.Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task 刷新成功_更新访问令牌与客户端令牌()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
            OnRefresh = _ => JsonResponse(
                HttpStatusCode.OK,
                $$"""
                  {
                    "accessToken": "tok-2",
                    "clientToken": "client-2",
                    "selectedProfile": { "id": "{{ProfileId}}", "name": "Steve" }
                  }
                  """),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var login = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");
        Assert.True(login.Success, login.Message);

        var result = await service.RefreshAsync(login.Account!.Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal("tok-2", result.Account!.AccessToken);
        Assert.Equal("client-2", result.Account.RefreshToken);
        Assert.Equal("Steve", result.Account.Username);

        var refreshCall = handler.Calls[^1];
        Assert.Equal("/api/yggdrasil/authserver/refresh", refreshCall.Path);

        using var request = JsonDocument.Parse(refreshCall.Body);
        Assert.Equal("tok-1", request.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal("client-1", request.RootElement.GetProperty("clientToken").GetString());
        // 不给用户切换角色，续期时不发 selectedProfile，保留令牌原有的角色绑定。
        Assert.False(request.RootElement.TryGetProperty("selectedProfile", out _));

        // 刷新结果必须落盘，否则重启后回到旧令牌。
        var reloaded = new AccountService(
            paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false },
            new HttpClient(new AuthlibHandler()));

        await reloaded.LoadAsync();
        Assert.Equal("tok-2", Assert.Single(reloaded.Accounts).AccessToken);
    }

    [Fact]
    public async Task 刷新失败_令牌失效时提示重新登录()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
            OnRefresh = _ => JsonResponse(
                HttpStatusCode.Forbidden,
                """{"error":"ForbiddenOperationException","errorMessage":"Invalid token."}"""),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var login = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");
        var result = await service.RefreshAsync(login.Account!.Id);

        Assert.False(result.Success);
        Assert.Contains("重新登录", result.Message);
        // 刷新失败不能把旧令牌改坏，用户还能看到账户并手动重登。
        Assert.Equal("tok-1", service.Accounts[0].AccessToken);
    }

    [Fact]
    public async Task 同Uuid重复登录_更新令牌而不是新增账户()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var first = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");
        Assert.True(first.Success, first.Message);

        // 第二次登录：同一角色、同一 API 根，令牌换成新的。
        handler.OnAuthenticate = _ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
              {
                "accessToken": "tok-2",
                "clientToken": "client-2",
                "selectedProfile": { "id": "{{ProfileId}}", "name": "Steve" }
              }
              """);

        var second = await service.AddAuthlibAccountAsync(ApiRoot + "/", "steve", "pw");

        Assert.True(second.Success, second.Message);
        Assert.Single(service.Accounts);
        Assert.Equal(first.Account!.Id, second.Account!.Id);
        Assert.Equal(first.Account.Id, service.ActiveAccount?.Id);
        Assert.Equal("tok-2", service.ActiveAccount!.AccessToken);
        Assert.Equal("client-2", service.ActiveAccount.RefreshToken);
        Assert.Equal(2, handler.Calls.Count);

        var reloaded = new AccountService(
            paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false },
            new HttpClient(new AuthlibHandler()));

        await reloaded.LoadAsync();
        Assert.Equal("tok-2", Assert.Single(reloaded.Accounts).AccessToken);
    }

    [Theory]
    [InlineData("https://littleskin.cn", "https://littleskin.cn")]
    [InlineData("https://littleskin.cn/", "https://littleskin.cn")]
    [InlineData("  https://littleskin.cn/api/yggdrasil/  ", "https://littleskin.cn/api/yggdrasil")]
    [InlineData("https://littleskin.cn/api/yggdrasil///", "https://littleskin.cn/api/yggdrasil")]
    [InlineData("HTTP://Example.COM/Api/", "http://example.com/Api")]
    public void Api根规范化_保留合法地址(string? input, string? expected)
    {
        Assert.Equal(expected, AuthlibAuthService.NormalizeApiRoot(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("littleskin.cn/api/yggdrasil")]
    [InlineData("ftp://littleskin.cn/api/yggdrasil")]
    [InlineData("https://")]
    [InlineData("https://littleskin.cn/api/yggdrasil?x=1")]
    public void Api根规范化_拒绝非法地址(string? input)
    {
        Assert.Null(AuthlibAuthService.NormalizeApiRoot(input));
    }

    [Fact]
    public void Api根规范化_拒绝null()
    {
        // 单独一条：xunit 的 [InlineData(null)] 会被当成「没有参数」，无法和上面的 Theory 共用。
        Assert.Null(AuthlibAuthService.NormalizeApiRoot(null));
    }

    [Fact]
    public async Task 校验接口_204视为有效()
    {
        var handler = new AuthlibHandler
        {
            OnValidate = _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        using var http = new HttpClient(handler);
        var authlib = new AuthlibAuthService(http, NullAppLog.Instance);

        var result = await authlib.ValidateAsync("  " + ApiRoot + "/  ", "tok-1", "client-1");

        Assert.True(result.Success, result.Message);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("/api/yggdrasil/authserver/validate", call.Path);

        using var request = JsonDocument.Parse(call.Body);
        Assert.Equal("tok-1", request.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal("client-1", request.RootElement.GetProperty("clientToken").GetString());
    }

    [Fact]
    public async Task 校验接口_401提示重新登录()
    {
        var handler = new AuthlibHandler
        {
            OnValidate = _ => JsonResponse(
                HttpStatusCode.Unauthorized,
                """{"error":"ForbiddenOperationException","errorMessage":"Invalid token."}"""),
        };

        using var http = new HttpClient(handler);
        var authlib = new AuthlibAuthService(http, NullAppLog.Instance);

        var result = await authlib.ValidateAsync(ApiRoot, "tok-1", "client-1");

        Assert.False(result.Success);
        Assert.Contains("重新登录", result.Message);
    }

    [Fact]
    public async Task 启动校验_令牌有效时直接返回活跃账户()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
            OnValidate = _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var login = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");
        Assert.True(login.Success, login.Message);

        var active = await service.EnsureValidActiveAccountAsync();

        Assert.NotNull(active);
        Assert.Equal("tok-1", active!.AccessToken);
        Assert.Equal("/api/yggdrasil/authserver/validate", handler.Calls[^1].Path);
        // 令牌有效就不续期：只有 authenticate + validate 两次请求。
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task 启动校验_令牌失效时回退刷新()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
            OnValidate = _ => JsonResponse(
                HttpStatusCode.Forbidden,
                """{"error":"ForbiddenOperationException"}"""),
            OnRefresh = _ => JsonResponse(HttpStatusCode.OK, """{"accessToken":"tok-2"}"""),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var login = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");
        Assert.True(login.Success, login.Message);

        var active = await service.EnsureValidActiveAccountAsync();

        Assert.NotNull(active);
        Assert.Equal("tok-2", active!.AccessToken);
        // 服务端没回 clientToken 时沿用旧值。
        Assert.Equal("client-1", active.RefreshToken);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task 启动校验_校验与刷新都失败时返回null()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var handler = new AuthlibHandler
        {
            OnAuthenticate = _ => JsonResponse(HttpStatusCode.OK, AuthenticateOkBody),
        };

        using var http = new HttpClient(handler);
        var service = CreateService(paths, http);
        await service.LoadAsync();

        var login = await service.AddAuthlibAccountAsync(ApiRoot, "steve", "pw");
        Assert.True(login.Success, login.Message);

        // 登录之后服务器就不可达了：校验与续期都会拿到网络错误。
        handler.Failure = new HttpRequestException("host unreachable");

        Assert.Null(await service.EnsureValidActiveAccountAsync());
    }

    [Fact]
    public async Task 旧账户文件缺少authServerUrl时按null加载()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.AccountsFile,
            $$"""
              {
                "activeAccountId": "legacy-1",
                "accounts": [
                  {
                    "id": "legacy-1",
                    "type": "Authlib",
                    "username": "Steve",
                    "uuid": "{{ProfileId}}"
                  }
                ]
              }
              """);

        var service = new AccountService(
            paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false },
            new HttpClient(new AuthlibHandler()));

        await service.LoadAsync();

        var account = Assert.Single(service.Accounts);
        Assert.Equal(AccountType.Authlib, account.Type);
        Assert.Null(account.AuthServerUrl);
        Assert.Equal(ProfileId, account.Uuid);

        // 缺服务器地址的账户无法校验/续期，启动前直接判为不可用，而不是发出一个半截请求。
        Assert.Null(await service.EnsureValidActiveAccountAsync());
    }

    private static AccountService CreateService(FakeLauncherPaths paths, HttpClient http) =>
        new(paths, NullAppLog.Instance, new FakePlatformService { SupportsSecureTokenStorage = false }, http);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>按 authserver 端点分派的假 handler，同时记录请求体供断言。</summary>
    private sealed class AuthlibHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OnAuthenticate { get; set; }

        public Func<HttpRequestMessage, HttpResponseMessage>? OnRefresh { get; set; }

        public Func<HttpRequestMessage, HttpResponseMessage>? OnValidate { get; set; }

        /// <summary>非 null 时每次都抛这个异常，模拟网络层失败。</summary>
        public Exception? Failure { get; set; }

        public List<RequestCall> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Calls.Add(new RequestCall(request.Method.Method, path, body));

            if (Failure is not null)
            {
                throw Failure;
            }

            var responder = path switch
            {
                _ when path.EndsWith("/authserver/authenticate", StringComparison.Ordinal) => OnAuthenticate,
                _ when path.EndsWith("/authserver/refresh", StringComparison.Ordinal) => OnRefresh,
                _ when path.EndsWith("/authserver/validate", StringComparison.Ordinal) => OnValidate,
                _ => null,
            };

            return responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private readonly record struct RequestCall(string Method, string Path, string Body);
}
