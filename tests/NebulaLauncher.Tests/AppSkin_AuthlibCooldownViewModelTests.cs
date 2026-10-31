using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Skinning;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤站通道冷却文案在 ViewModel 层的端到端断言（票 #55）。
///
/// <para>
/// <see cref="SkinUploadThrottle"/> 层的按通道文案已有断言（票 #52），但
/// 「429 → <see cref="SkinsViewModel"/> 把皮肤站实例的 <c>BlockedText</c> 赋给
/// <c>AuthlibCooldownText</c>」这段接线此前零覆盖。这里通过假 <see cref="IServiceProvider"/>
/// 构造 VM（容器只注册假账户服务与包着假 handler 的 <see cref="AuthlibSkinClient"/>），
/// 用 <c>DeleteAuthlibSkinCommand</c> 触发一次真实的皮肤站请求被 429 拒绝，
/// 然后断言界面将看到的文案：说「皮肤站」，不说「官方服务」，且官方通道不受连累
/// （PITFALLS D14：两条链路的节流、重试、冷却各自独立）。
/// </para>
///
/// <para>
/// 全程不触碰 Avalonia 类型（CONVENTIONS §8.2）：不调 <c>OnNavigatedToAsync</c>
/// （那是 <c>Dispatcher.UIThread</c> 唯一入口的订阅点）、不加载缩略图
/// （<c>Bitmap</c> 只在缩略图链路实例化）。删除链路不需要文件字节、选中行或
/// PNG 校验：构造期能力为未知态时 <c>CanDeleteSkin = true</c>，是面最小的触发。
/// </para>
/// </summary>
public class AppSkin_AuthlibCooldownViewModelTests
{
    private const string ApiRoot = "https://littleskin.cn/api/yggdrasil";

    private const string Uuid = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task 皮肤站通道被429拒绝后冷却文案说皮肤站而不是官方服务()
    {
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        using var vm = new SkinsViewModel(new ServiceMap()
            .Add<IAccountService>(new FakeAccountService(AuthlibAccount()))
            .Add(new AuthlibSkinClient(http)));

        // 触发前的基线：还没操作过时两条通道都没有冷却文案，也没有反馈条。
        Assert.Null(vm.AuthlibCooldownText);
        Assert.False(vm.HasAuthlibCooldown);
        Assert.Null(vm.OfficialCooldownText);
        Assert.Null(vm.AuthlibFeedback);

        // 触发前提：能力未知（profile 还没读到）时删除可点——
        // 这就是删除命令是「面最小的触发」的原因（票 #55 拍板）。
        Assert.True(vm.Authlib.CanDeleteSkin);

        await vm.DeleteAuthlibSkinCommand.ExecuteAsync(null);

        // 端到端的证据：429 是服务端（假 handler）对真实删除请求的应答，不是本地摆的文案。
        var delete = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal($"{ApiRoot}/api/user/profile/{Uuid}/skin", delete.Url);

        // 票 #52 修的正是这句话：皮肤站被限流时不能说成「官方服务」。
        Assert.True(vm.HasAuthlibCooldown);
        Assert.Contains("皮肤站", vm.AuthlibCooldownText!);
        Assert.DoesNotContain("官方服务", vm.AuthlibCooldownText!);

        // 反馈条呈可重试失败态：429 是「等一会儿再发可能成功」，不是「改了才对」。
        Assert.NotNull(vm.AuthlibFeedback);
        Assert.True(vm.AuthlibFeedback!.IsError);
        Assert.True(vm.AuthlibFeedback.CanRetry);
        Assert.False(vm.AuthlibFeedback.GoToAccountPage);

        // 冷却没到期时重试按钮保持隐藏：CanRetryAuthlib 同时看反馈与节流。
        Assert.False(vm.CanRetryAuthlib);

        // 通道隔离（票 #55 拍板 Q4）：皮肤站被限流不该把官方通道也拖进冷却。
        Assert.Null(vm.OfficialCooldownText);
    }

    private static Account AuthlibAccount() => new()
    {
        Type = AccountType.Authlib,
        Username = "littleskin_player",
        Uuid = Uuid,
        AccessToken = "yggdrasil-access-token",
        AuthServerUrl = ApiRoot,
    };

    /// <summary>
    /// 皮肤站服务端替身：删除请求回 429（<see cref="AuthlibSkinClient"/> 标为可重试），
    /// 能力查询（GET profile）回 404——读不到能力时 VM 保持未知态，异常被就地消化。
    /// 两种请求都<b>立即应答</b>：构造期的能力查询是 fire-and-forget，挂住不答会留在途任务。
    /// 记录用 <see cref="ConcurrentQueue{T}"/>：能力查询那条线在后台跑，
    /// 普通 List 与测试线程的读取之间存在数据竞争的可能。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.Method, request.RequestUri!.ToString()));

            var response = request.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }

    /// <summary>只按类型索引的服务容器替身；未注册的类型返回 null，与真实 DI 的解析结果一致。</summary>
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

    /// <summary>只提供当前账户的 <see cref="IAccountService"/>；其余成员不会被这些用例触达。</summary>
    private sealed class FakeAccountService(Account? active) : IAccountService
    {
        public IReadOnlyList<Account> Accounts => active is null ? [] : [active];

        public Account? ActiveAccount => active;

        public event EventHandler? AccountsChanged
        {
            add { }
            remove { }
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
