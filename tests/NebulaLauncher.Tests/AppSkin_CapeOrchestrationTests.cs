using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Skinning;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤页的官方披风编排（票 #61）：进入页面拉档案、切换披风 PUT、隐藏披风 DELETE，
/// 成功后广播皮肤变更；失败时保留原来的使用中并显示官方原文。
/// </summary>
public sealed class AppSkin_CapeOrchestrationTests
{
    private const string AccountId = "microsoft-1";
    private const string Token = "minecraft-access-token";
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string CapeUrl = "https://api.minecraftservices.com/minecraft/profile/capes/active";

    [Fact]
    public async Task 进入页面即读取披风列表并标出使用中()
    {
        using var handler = new CapeHandler();
        using var vm = CreateVm(handler);

        await WaitUntilAsync(() => vm.Capes.Count == 2);

        Assert.Equal("Migrator", vm.Capes[0].DisplayName);
        Assert.True(vm.Capes[0].IsActive);
        Assert.Equal("MineCon", vm.Capes[1].DisplayName);
        Assert.False(vm.Capes[1].IsActive);
        Assert.Contains("官方发放", vm.CapeHint);
        Assert.False(vm.HasCapeError);
    }

    [Fact]
    public async Task 切换披风发PUT并切换使用中()
    {
        using var handler = new CapeHandler();
        var changes = new SkinChangeNotifier();
        var notified = 0;
        changes.SkinChanged += (_, _) => notified++;
        using var vm = CreateVm(handler, changes);

        await WaitUntilAsync(() => vm.Capes.Count == 2);
        await vm.UseCapeCommand.ExecuteAsync(vm.Capes[1]);

        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal(CapeUrl, put.Url);
        Assert.Equal("Bearer", put.Scheme);
        Assert.Equal(Token, put.Parameter);
        Assert.Contains("\"capeId\":\"cape-2\"", put.Body);

        Assert.False(vm.Capes[0].IsActive);
        Assert.True(vm.Capes[1].IsActive);
        Assert.Contains("已换上披风", vm.CapeStatusText);
        Assert.Null(vm.CapeErrorText);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task 隐藏披风发DELETE并清空使用中()
    {
        using var handler = new CapeHandler();
        using var vm = CreateVm(handler);

        await WaitUntilAsync(() => vm.Capes.Count == 2);
        Assert.True(vm.CanHideCape);

        await vm.HideCapeCommand.ExecuteAsync(null);

        var delete = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal(CapeUrl, delete.Url);
        Assert.Equal(string.Empty, delete.Body);
        Assert.All(vm.Capes, cape => Assert.False(cape.IsActive));
        Assert.Contains("已隐藏披风", vm.CapeStatusText);
        Assert.False(vm.CanHideCape);
    }

    [Fact]
    public async Task 切换失败时保留原使用中并显示官方兜底文案()
    {
        using var handler = new CapeHandler { RejectCapeChange = true };
        using var vm = CreateVm(handler);

        await WaitUntilAsync(() => vm.Capes.Count == 2);
        await vm.UseCapeCommand.ExecuteAsync(vm.Capes[1]);

        Assert.Contains("不在当前账户的已拥有列表里", vm.CapeErrorText);
        Assert.True(vm.Capes[0].IsActive);
        Assert.False(vm.Capes[1].IsActive);
        Assert.Null(vm.CapeStatusText);
    }

    [Fact]
    public async Task 切换进行中重复点击只发一次PUT()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CapeHandler { CapeChangeGate = gate.Task };
        using var vm = CreateVm(handler);
        await WaitUntilAsync(() => vm.Capes.Count == 2);

        var first = vm.UseCapeCommand.ExecuteAsync(vm.Capes[1]);
        await WaitUntilAsync(() => vm.IsCapeBusy);
        var second = vm.UseCapeCommand.ExecuteAsync(vm.Capes[1]);
        gate.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.True(vm.Capes[1].IsActive);
    }

    [Fact]
    public async Task 切换披风途中换账户会解锁并重拉新账户披风()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CapeHandler
        {
            CapeChangeGate = gate.Task,
            SecondAccountToken = "token-2",
        };
        var accounts = new FakeAccountService(MicrosoftAccount());
        using var vm = CreateVm(handler, accounts: accounts);

        // 页面导航时才订阅 AccountsChanged；这里绕开导航（会触发真实皮肤站请求），只挂事件。
        typeof(SkinsViewModel)
            .GetMethod("AttachAccounts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, null);

        await WaitUntilAsync(() => vm.Capes.Count == 2);
        var switching = vm.UseCapeCommand.ExecuteAsync(vm.Capes[1]);
        await WaitUntilAsync(() => vm.IsCapeBusy);

        accounts.SwitchTo(MicrosoftAccount("microsoft-2", "token-2"));
        await WaitUntilAsync(() => vm.Capes.Any(c => c.DisplayName == "Optifine"));

        gate.SetResult(true);
        await switching;

        Assert.False(vm.IsCapeBusy);
        Assert.True(vm.CanRefreshCapes);
        Assert.Contains(vm.Capes, c => c.DisplayName == "Optifine");
        Assert.DoesNotContain(vm.Capes, c => c.DisplayName == "Migrator");
    }

    [Fact]
    public async Task 读取披风失败后不再显示正在读取()
    {
        using var handler = new CapeHandler { FailProfile = true };
        using var vm = CreateVm(handler);

        await WaitUntilAsync(() => vm.HasCapeError);

        Assert.DoesNotContain("正在读取", vm.CapeHint);
        Assert.True(vm.IsCapeEmpty);
    }

    private static SkinsViewModel CreateVm(
        CapeHandler handler,
        SkinChangeNotifier? changes = null,
        FakeAccountService? accounts = null)
    {
        var http = new HttpClient(handler);
        var services = new ServiceMap()
            .Add<IAccountService>(accounts ?? new FakeAccountService(MicrosoftAccount()))
            .Add(new MojangSkinClient(http));
        if (changes is not null)
        {
            services.Add(changes);
        }

        return new SkinsViewModel(services);
    }

    private static Account MicrosoftAccount(string id = AccountId, string token = Token) => new()
    {
        Id = id,
        Type = AccountType.Microsoft,
        Username = "Steve",
        Uuid = "0123456789abcdef0123456789abcdef",
        AccessToken = token,
    };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(3))
            {
                throw new TimeoutException("等待披风列表加载超时。");
            }

            await Task.Delay(20);
        }
    }

    private sealed record RequestLog(HttpMethod Method, string Url, string Scheme, string Parameter, string Body);

    private sealed class CapeHandler : HttpMessageHandler
    {
        public bool RejectCapeChange { get; init; }

        public bool FailProfile { get; init; }

        public Task? CapeChangeGate { get; init; }

        /// <summary>这个令牌对应的账户返回另一套披风，用来验证切账户后列表真的换了人。</summary>
        public string? SecondAccountToken { get; init; }

        public ConcurrentQueue<RequestLog> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new RequestLog(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme ?? string.Empty,
                request.Headers.Authorization?.Parameter ?? string.Empty,
                body));

            if (request.RequestUri.ToString() == ProfileUrl)
            {
                if (FailProfile)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                if (request.Headers.Authorization?.Parameter == SecondAccountToken)
                {
                    return Json("""
                        {
                          "id": "profile-2",
                          "name": "Alex",
                          "skins": [],
                          "capes": [
                            { "id": "cape-3", "state": "ACTIVE", "alias": "Optifine" }
                          ]
                        }
                        """);
                }

                return Json("""
                    {
                      "id": "profile-1",
                      "name": "Steve",
                      "skins": [ { "id": "skin-1", "state": "ACTIVE", "url": "https://cdn.example/skin.png" } ],
                      "capes": [
                        { "id": "cape-1", "state": "ACTIVE", "alias": "Migrator" },
                        { "id": "cape-2", "state": "INACTIVE", "alias": "MineCon" }
                      ]
                    }
                    """);
            }

            if (request.RequestUri.ToString() == CapeUrl && request.Method == HttpMethod.Put)
            {
                if (CapeChangeGate is { } gate)
                {
                    await gate.ConfigureAwait(false);
                }

                return RejectCapeChange
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : Json("{}");
            }

            if (request.RequestUri.ToString() == CapeUrl && request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ServiceMap : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public ServiceMap Add<T>(T service)
            where T : class
        {
            _services[typeof(T)] = service;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class FakeAccountService(Account? active) : IAccountService
    {
        private Account? _active = active;

        public IReadOnlyList<Account> Accounts => _active is null ? [] : [_active];

        public Account? ActiveAccount => _active;

        public event EventHandler? AccountsChanged;

        public void SwitchTo(Account? account)
        {
            _active = account;
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AccountResult> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccountResult> AddMicrosoftAccountAsync(
            IProgress<MicrosoftAuthProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> RefreshAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void SetActive(string accountId) => throw new NotSupportedException();

        public Task<Account?> EnsureValidActiveAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
