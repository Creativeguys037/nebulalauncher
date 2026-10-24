using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;

namespace NebulaLauncher.Tests;

/// <summary>
/// 首帧之前的预热（<see cref="StartupState"/>）。
///
/// 这组用例守的是「刚打开程序就是已登录」：账户服务不在构造时读盘，
/// 预热少写一行，首页卡片、版本页启动与启动链拿到的就是空账户——而且不报错，
/// 只有用户自己去账户页转一圈才会好（R59，见 HANDOVER §6.71）。
/// </summary>
public class AppStartup_StartupStateTests
{
    [Fact]
    public async Task 预热_把磁盘上的当前账户读进内存()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var platform = new FakePlatformService { SupportsSecureTokenStorage = false };

        // 造一份「上次登录过」的 accounts.json。
        var seed = new AccountService(paths, NullAppLog.Instance, platform);
        await seed.LoadAsync();
        Assert.True((await seed.AddOfflineAccountAsync("Steve")).Success);

        // 冷启动：另一个实例，内存里还没有账户。
        var accounts = new AccountService(paths, NullAppLog.Instance, platform);
        Assert.Null(accounts.ActiveAccount);
        Assert.Empty(accounts.Accounts);

        StartupState.LoadBeforeFirstFrame(new MapServices(
            (typeof(ILauncherPaths), paths),
            (typeof(IAccountService), accounts)));

        Assert.Equal("Steve", accounts.ActiveAccount?.Username);
        Assert.Single(accounts.Accounts);
    }

    [Fact]
    public void 预热_读盘抛异常不阻断启动()
    {
        // 账户文件坏了 / 磁盘拒绝：启动器的窗口必须照常起来，用户才有机会去修。
        var services = new MapServices((typeof(IAccountService), new ThrowingAccounts()));

        StartupState.LoadBeforeFirstFrame(services);
    }

    [Fact]
    public void 预热_服务都没注册时是空操作()
        => StartupState.LoadBeforeFirstFrame(new MapServices());

    [Fact]
    public void App入口在创建窗口之前调用预热()
    {
        // 这组用例只能证明 helper 做对了事，证明不了它被调用；而漏掉那一行正是本轮的 bug 形状。
        // 测试碰不了 Avalonia 的启动生命周期（HANDOVER §6.25），改为直接读 App.axaml.cs 的文本。
        var path = FindRepoFile("src", "NebulaLauncher.App", "App.axaml.cs");
        Assert.NotNull(path);

        var text = File.ReadAllText(path!);
        var preheat = text.IndexOf("StartupState.LoadBeforeFirstFrame(Services)", StringComparison.Ordinal);
        var window = text.IndexOf("new MainWindow(Services)", StringComparison.Ordinal);

        Assert.True(preheat >= 0, "App.axaml.cs 里没有调用 StartupState.LoadBeforeFirstFrame(Services)");
        Assert.True(window >= 0, "App.axaml.cs 里找不到 new MainWindow(Services)");
        Assert.True(preheat < window, "预热必须在 new MainWindow(Services) 之前：首页是在窗口构造链里建出来的");
    }

    /// <summary>
    /// 从测试输出目录（<c>tests/…/bin/&lt;config&gt;/net8.0</c>）逐级上溯找仓库内的文件。
    /// 找不到返回 null，由调用方断言失败并给出明确原因。
    /// </summary>
    private static string? FindRepoFile(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(relative).ToArray());

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>只按类型查表的 <see cref="IServiceProvider"/>，够 <see cref="StartupState"/> 用。</summary>
    private sealed class MapServices(params (Type Type, object Instance)[] entries) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach (var (type, instance) in entries)
            {
                if (type == serviceType)
                {
                    return instance;
                }
            }

            return null;
        }
    }

    /// <summary>读盘直接失败的账户服务替身。</summary>
    private sealed class ThrowingAccounts : IAccountService
    {
        public IReadOnlyList<Account> Accounts => [];

        public Account? ActiveAccount => null;

        public event EventHandler? AccountsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException(new IOException("账户文件读不出来"));

        public Task<AccountResult> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult(AccountResult.Fail("测试替身"));

        public Task<AccountResult> AddMicrosoftAccountAsync(
            IProgress<MicrosoftAuthProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AccountResult.Fail("测试替身"));

        public Task<AccountResult> RefreshAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(AccountResult.Fail("测试替身"));

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void SetActive(string accountId)
        {
        }

        public Task<Account?> EnsureValidActiveAccountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<Account?>(null);
    }
}
