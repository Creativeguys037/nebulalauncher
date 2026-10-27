using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 侧边栏皮肤卡的 ViewModel：跟随当前账户，把它的皮肤准备成 <see cref="SkinVisual"/>。
///
/// 卡片常驻在窗口里，所以这里对失败的态度是「退到默认皮肤」而不是「显示错误」：
/// 没登录、没上传皮肤、会话服务器超时，看到的都是同一只 Steve。
/// </summary>
public sealed partial class SkinCardViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;

    private IAccountService? _accounts;

    private SkinChangeNotifier? _skinChanges;

    private IAppLog? _log;

    private CancellationTokenSource? _loadCancellation;

    private bool _initialized;

    private bool _subscribed;

    [ObservableProperty]
    private SkinVisual? _visual;

    [ObservableProperty]
    private string _playerName = "未登录";

    [ObservableProperty]
    private string _detail = "默认皮肤";

    [ObservableProperty]
    private bool _hasAccount;

    public SkinCardViewModel(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>接上账户服务并加载第一份皮肤。重复调用只有第一次生效。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _log = _services.GetService<IAppLog>();
        _accounts = _services.GetService<IAccountService>();

        // 皮肤变更通知：换肤改的是服务端材质，AccountsChanged 不会发（票 #44 AC2）。
        // 容器里没注册时落到进程级默认实例，保证与发通知的一方是同一个对象。
        _skinChanges = _services.GetService<SkinChangeNotifier>() ?? SkinChangeBus.Current;
        _skinChanges.SkinChanged += OnSkinChanged;

        if (_accounts is null)
        {
            await ApplyAsync(null).ConfigureAwait(true);
            return;
        }

        _accounts.AccountsChanged += OnAccountsChanged;
        _subscribed = true;
        await ApplyAsync(_accounts.ActiveAccount).ConfigureAwait(true);
    }

    /// <summary>
    /// 强制按当前账户重新取一次皮肤（票 #44 AC2）。
    ///
    /// 给两个来源用：一是本类的「皮肤已变更」订阅，二是需要确定性刷新的调用方
    /// （例如票 #43 换完皮肤站材质之后）。返回的任务可以直接 await，失败已就地落日志。
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            await ApplyAsync(_accounts?.ActiveAccount).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn("刷新侧边栏皮肤卡失败", ex);
        }
    }

    /// <summary>
    /// 拆掉账户订阅并打断还没回来的请求。卡片与主窗口同寿命，正常关闭时进程随即退出；
    /// 这里显式退出是为了让「订阅不留在服务上」这件事有确定的一条路径（见 PITFALLS 2.7 / 2.11）。
    /// </summary>
    public void Dispose()
    {
        if (_subscribed && _accounts is not null)
        {
            _accounts.AccountsChanged -= OnAccountsChanged;
            _subscribed = false;
        }

        if (_skinChanges is not null)
        {
            _skinChanges.SkinChanged -= OnSkinChanged;
            _skinChanges = null;
        }

        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    /// <summary>
    /// 换肤成功之后由 <see cref="SkinChangeNotifier"/> 触发。通知的发布方可能在后台线程，
    /// 所以这里与 <see cref="OnAccountsChanged"/> 一样先切回 UI 线程再动 <see cref="Visual"/>。
    /// </summary>
    private void OnSkinChanged(object? sender, SkinChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyInBackground(_accounts?.ActiveAccount));
            return;
        }

        ApplyInBackground(_accounts?.ActiveAccount);
    }

    /// <summary>按给定账户刷新卡片。取消上一次还没回来的请求。</summary>
    private async Task ApplyAsync(Account? account)
    {
        PlayerName = string.IsNullOrWhiteSpace(account?.Username) ? "未登录" : account!.Username;
        HasAccount = account is not null;

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        SkinVisual visual;
        try
        {
            visual = await SkinTextureLoader.LoadAsync(account, _log, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        Visual = visual;
        Detail = DescribeVisual(visual, HasAccount);
    }

    /// <summary>
    /// 后台刷新的兜底入口：调用方拿不到这个任务，所以异常必须在这里就地消化并落日志
    /// （PITFALLS 2.8），否则账户切换失败在界面上只表现为「皮肤没变」。
    /// </summary>
    private void ApplyInBackground(Account? account) => _ = ApplyInBackgroundAsync(account);

    private async Task ApplyInBackgroundAsync(Account? account)
    {
        try
        {
            await ApplyAsync(account).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warn("刷新侧边栏皮肤卡失败", ex);
        }
    }

    private static string DescribeVisual(SkinVisual visual, bool hasAccount)
    {
        if (!hasAccount)
        {
            return "默认皮肤";
        }

        if (visual.HasCape)
        {
            return visual.Slim ? "Alex · 有披风" : "Steve · 有披风";
        }

        return visual.Slim ? "Alex 体型" : "Steve 体型";
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyInBackground(_accounts?.ActiveAccount));
            return;
        }

        ApplyInBackground(_accounts?.ActiveAccount);
    }
}
