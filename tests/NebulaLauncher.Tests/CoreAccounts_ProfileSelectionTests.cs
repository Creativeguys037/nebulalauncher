using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

public class CoreAccounts_ProfileSelectionTests
{
    private const string ApiRoot = "https://skin.example.invalid/api/yggdrasil";
    private const string FirstProfileId = "069a79f444e94726a5befca90e38aaf5";
    private const string SecondProfileId = "11111111111111111111111111111111";

    [Fact]
    public async Task 多角色尚未选择时_返回候选项且不创建账户文件()
    {
        using var scenario = new ProfileScenario();
        var changed = 0;
        scenario.Service.AccountsChanged += (_, _) => changed++;

        var result = await scenario.LoginAsync();

        Assert.False(result.Success);
        Assert.True(result.RequiresProfileSelection);
        Assert.False(string.IsNullOrWhiteSpace(result.ProfileSelectionId));
        Assert.Null(result.Account);
        Assert.Collection(
            result.AvailableProfiles,
            profile => Assert.Equal(new AuthlibProfileOption(FirstProfileId, "Steve"), profile),
            profile => Assert.Equal(new AuthlibProfileOption(SecondProfileId, "Alex"), profile));
        Assert.Empty(scenario.Service.Accounts);
        Assert.Null(scenario.Service.ActiveAccount);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));
        Assert.Equal(0, changed);
        Assert.Single(scenario.Handler.Calls);
    }

    [Fact]
    public async Task 没有可用角色时_失败且不创建选择会话()
    {
        using var scenario = new ProfileScenario(profileCount: 0);
        var result = await scenario.LoginAsync();

        Assert.False(result.Success);
        Assert.False(result.RequiresProfileSelection);
        Assert.Null(result.ProfileSelectionId);
        Assert.Empty(result.AvailableProfiles);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));
        Assert.Single(scenario.Handler.Calls);
    }

    [Fact]
    public async Task 唯一角色未绑定时_自动通过Refresh绑定后保存()
    {
        using var scenario = new ProfileScenario(profileCount: 1);
        scenario.Handler.OnRefresh = (_, _) => Task.FromResult(JsonResponse(
            RefreshBody(FirstProfileId, "Steve")));

        var result = await scenario.LoginAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(result.RequiresProfileSelection);
        Assert.Equal(FirstProfileId, result.Account!.Uuid);
        Assert.Equal("selected-token", result.Account.AccessToken);
        Assert.Equal("selected-client", result.Account.RefreshToken);
        Assert.Equal(result.Account.Id, scenario.Service.ActiveAccount?.Id);
        Assert.True(File.Exists(scenario.Paths.AccountsFile));
        AssertProfileRequest(Assert.Single(scenario.Handler.RefreshCalls), FirstProfileId, "Steve");
    }

    [Fact]
    public async Task 显式选择角色_绑定返回令牌落盘并禁止重复使用会话()
    {
        using var scenario = new ProfileScenario();
        var pending = await scenario.LoginAsync();

        var result = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.True(result.Success, result.Message);
        Assert.False(result.RequiresProfileSelection);
        var account = Assert.Single(scenario.Service.Accounts);
        Assert.Equal(AccountType.Authlib, account.Type);
        Assert.Equal(SecondProfileId, account.Uuid);
        Assert.Equal("Alex", account.Username);
        Assert.Equal(ApiRoot, account.AuthServerUrl);
        Assert.Equal("selected-token", account.AccessToken);
        Assert.Equal("selected-client", account.RefreshToken);
        Assert.Equal(account.Id, scenario.Service.ActiveAccount?.Id);
        AssertProfileRequest(Assert.Single(scenario.Handler.RefreshCalls), SecondProfileId, "Alex");

        var reloaded = new AccountService(
            scenario.Paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false },
            scenario.Http);
        await reloaded.LoadAsync();
        var saved = Assert.Single(reloaded.Accounts);
        Assert.Equal(account.Id, saved.Id);
        Assert.Equal(SecondProfileId, saved.Uuid);
        Assert.Equal("selected-token", saved.AccessToken);
        Assert.Equal("selected-client", saved.RefreshToken);

        var repeated = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, FirstProfileId);

        Assert.False(repeated.Success);
        Assert.Single(scenario.Handler.RefreshCalls);
        Assert.Equal(SecondProfileId, Assert.Single(scenario.Service.Accounts).Uuid);
    }

    [Fact]
    public async Task 选择候选列表以外角色_不发送请求且允许改选有效角色()
    {
        using var scenario = new ProfileScenario();
        var pending = await scenario.LoginAsync();

        var invalid = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, "22222222222222222222222222222222");

        Assert.False(invalid.Success);
        Assert.Empty(scenario.Handler.RefreshCalls);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));

        var retry = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.True(retry.Success, retry.Message);
        Assert.Equal(SecondProfileId, Assert.Single(scenario.Service.Accounts).Uuid);
    }

    [Fact]
    public async Task 取消选择会话后_不允许继续提交或写入账户()
    {
        using var scenario = new ProfileScenario();
        var pending = await scenario.LoginAsync();

        scenario.Service.CancelAuthlibProfileSelection(pending.ProfileSelectionId!);
        scenario.Service.CancelAuthlibProfileSelection(pending.ProfileSelectionId!);
        var result = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.False(result.Success);
        Assert.Empty(scenario.Handler.RefreshCalls);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));
    }

    [Fact]
    public async Task 未知选择会话_不发送网络请求()
    {
        using var scenario = new ProfileScenario();
        var result = await scenario.Service.SelectAuthlibProfileAsync("unknown-session", SecondProfileId);

        Assert.False(result.Success);
        Assert.Empty(scenario.Handler.Calls);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 绑定响应缺少或返回其他角色_拒绝保存且保留重试能力(bool returnWrongProfile)
    {
        using var scenario = new ProfileScenario();
        scenario.Handler.OnRefresh = (_, _) => Task.FromResult(JsonResponse(
            RefreshBody(returnWrongProfile ? FirstProfileId : null, "Steve")));
        var pending = await scenario.LoginAsync();

        var rejected = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.False(rejected.Success);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));

        scenario.Handler.OnRefresh = (_, _) => Task.FromResult(JsonResponse(RefreshBody()));
        var retry = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.True(retry.Success, retry.Message);
        Assert.Equal(SecondProfileId, Assert.Single(scenario.Service.Accounts).Uuid);
        Assert.Equal(2, scenario.Handler.RefreshCalls.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 绑定网络或服务器失败_不保存且允许同会话重试(bool networkFailure)
    {
        using var scenario = new ProfileScenario();
        scenario.Handler.OnRefresh = (_, _) => networkFailure
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"))
            : Task.FromResult(JsonResponse("{}", HttpStatusCode.ServiceUnavailable));
        var pending = await scenario.LoginAsync();

        var failed = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.False(failed.Success);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));

        scenario.Handler.OnRefresh = (_, _) => Task.FromResult(JsonResponse(RefreshBody()));
        var retry = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        Assert.True(retry.Success, retry.Message);
        Assert.Single(scenario.Service.Accounts);
        Assert.Equal(2, scenario.Handler.RefreshCalls.Length);
    }

    [Fact]
    public async Task 角色已绑定后普通续期_不再发送SelectedProfile()
    {
        using var scenario = new ProfileScenario();
        var pending = await scenario.LoginAsync();
        var login = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);
        Assert.True(login.Success, login.Message);
        scenario.Handler.OnRefresh = (_, _) => Task.FromResult(JsonResponse(
            """{"accessToken":"renewed-token","clientToken":"renewed-client"}"""));

        var refreshed = await scenario.Service.RefreshAsync(login.Account!.Id);

        Assert.True(refreshed.Success, refreshed.Message);
        Assert.Equal("renewed-token", refreshed.Account!.AccessToken);
        Assert.Equal(SecondProfileId, refreshed.Account.Uuid);
        Assert.Equal("Alex", refreshed.Account.Username);
        using var request = JsonDocument.Parse(scenario.Handler.RefreshCalls[^1].Body);
        Assert.False(request.RootElement.TryGetProperty("selectedProfile", out _));
        Assert.Equal("selected-token", request.RootElement.GetProperty("accessToken").GetString());
        Assert.Equal("selected-client", request.RootElement.GetProperty("clientToken").GetString());
    }

    [Fact]
    public async Task 同一会话重复并发提交_立即拒绝第二次且只保存一次()
    {
        using var scenario = new ProfileScenario();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.OnRefresh = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return JsonResponse(RefreshBody());
        };
        var pending = await scenario.LoginAsync();
        var first = scenario.Service.SelectAuthlibProfileAsync(pending.ProfileSelectionId!, SecondProfileId);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = await scenario.Service.SelectAuthlibProfileAsync(
                    pending.ProfileSelectionId!, FirstProfileId)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(duplicate.Success);
            Assert.Single(scenario.Handler.RefreshCalls);
            Assert.Empty(scenario.Service.Accounts);
        }
        finally
        {
            release.TrySetResult();
        }

        var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed.Success, completed.Message);
        Assert.Equal(SecondProfileId, Assert.Single(scenario.Service.Accounts).Uuid);
        Assert.Single(scenario.Handler.RefreshCalls);
    }

    [Fact]
    public async Task 绑定请求进行中取消会话_响应回来也不得保存账户()
    {
        using var scenario = new ProfileScenario();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Handler.OnRefresh = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return JsonResponse(RefreshBody());
        };
        var pending = await scenario.LoginAsync();
        var selection = scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scenario.Service.CancelAuthlibProfileSelection(pending.ProfileSelectionId!);
        }
        finally
        {
            release.TrySetResult();
        }

        var cancelled = await selection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cancelled.Success);
        Assert.Empty(scenario.Service.Accounts);
        Assert.False(File.Exists(scenario.Paths.AccountsFile));

        var repeated = await scenario.Service.SelectAuthlibProfileAsync(
            pending.ProfileSelectionId!, SecondProfileId);
        Assert.False(repeated.Success);
        Assert.Single(scenario.Handler.RefreshCalls);
    }

    private static void AssertProfileRequest(RequestCall call, string profileId, string profileName)
    {
        Assert.Equal("/api/yggdrasil/authserver/refresh", call.Path);
        using var document = JsonDocument.Parse(call.Body);
        var request = document.RootElement;
        Assert.Equal("unbound-token", request.GetProperty("accessToken").GetString());
        Assert.Equal("unbound-client", request.GetProperty("clientToken").GetString());
        Assert.Equal(profileId, request.GetProperty("selectedProfile").GetProperty("id").GetString());
        Assert.Equal(profileName, request.GetProperty("selectedProfile").GetProperty("name").GetString());
        Assert.False(request.TryGetProperty("password", out _));
    }

    private static string AuthenticateBody(int profileCount)
    {
        var profiles = new[]
        {
            new { id = FirstProfileId, name = "Steve" },
            new { id = SecondProfileId, name = "Alex" },
        };
        return JsonSerializer.Serialize(new
        {
            accessToken = "unbound-token",
            clientToken = "unbound-client",
            availableProfiles = profiles.Take(profileCount).ToArray(),
        });
    }

    private static string RefreshBody(string? profileId = SecondProfileId, string profileName = "Alex") =>
        JsonSerializer.Serialize(new
        {
            accessToken = "selected-token",
            clientToken = "selected-client",
            selectedProfile = profileId is null ? null : new { id = profileId, name = profileName },
        });

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ProfileScenario : IDisposable
    {
        public ProfileScenario(int profileCount = 2)
        {
            Handler = new ProfileHandler(AuthenticateBody(profileCount));
            Http = new HttpClient(Handler);
            Service = new AccountService(
                Paths,
                NullAppLog.Instance,
                new FakePlatformService { SupportsSecureTokenStorage = false },
                Http);
        }

        public CoreAssetsPaths Paths { get; } = new();

        public ProfileHandler Handler { get; }

        public HttpClient Http { get; }

        public AccountService Service { get; }

        public Task<AccountResult> LoginAsync() =>
            Service.AddAuthlibAccountAsync(ApiRoot, "player@example.invalid", "password");

        public void Dispose()
        {
            Http.Dispose();
            Paths.Dispose();
        }
    }

    private sealed class ProfileHandler(string authenticateBody) : HttpMessageHandler
    {
        public ConcurrentQueue<RequestCall> Calls { get; } = new();

        public RequestCall[] RefreshCalls => Calls
            .Where(call => call.Path.EndsWith("/authserver/refresh", StringComparison.Ordinal))
            .ToArray();

        public Func<RequestCall, CancellationToken, Task<HttpResponseMessage>> OnRefresh { get; set; } =
            (_, _) => Task.FromResult(JsonResponse(RefreshBody()));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = new RequestCall(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Calls.Enqueue(call);

            if (call.Path.EndsWith("/authserver/authenticate", StringComparison.Ordinal))
            {
                return JsonResponse(authenticateBody);
            }

            if (call.Path.EndsWith("/authserver/refresh", StringComparison.Ordinal))
            {
                return await OnRefresh(call, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed record RequestCall(string Path, string Body);
}
