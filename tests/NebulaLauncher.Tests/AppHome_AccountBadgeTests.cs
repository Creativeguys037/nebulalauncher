using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 首页账户大卡片的类型徽标与详情文案（<see cref="HomeViewModel.AccountBadge"/> /
/// <see cref="HomeViewModel.AccountDetail"/>）。
///
/// 这里守的是票 28 报的那条缺陷：账户类型本有三态，首页徽标曾用二元判定把
/// 「第三方验证」压进「离线账户」一档，用户会以为自己挂的是离线身份。
/// 断言值取账户页已有的口径，不复制实现里的 switch。
/// </summary>
public class AppHome_AccountBadgeTests
{
    [Fact]
    public async Task 首页徽标_第三方验证账户显示第三方验证()
    {
        var account = new Account
        {
            Type = AccountType.Authlib,
            Username = "littleskin_player",
            Uuid = "0123456789abcdef0123456789abcdef",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        };

        var viewModel = await CreateLoadedViewModelAsync(account);

        Assert.Equal("第三方验证", viewModel.AccountBadge);
    }

    [Fact]
    public async Task 首页徽标_微软账户与离线账户保持原口径()
    {
        var microsoft = await CreateLoadedViewModelAsync(new Account
        {
            Type = AccountType.Microsoft,
            Username = "Steve",
            Uuid = "11111111111111111111111111111111",
        });

        var offline = await CreateLoadedViewModelAsync(new Account
        {
            Type = AccountType.Offline,
            Username = "Alex",
            Uuid = "22222222222222222222222222222222",
        });

        Assert.Equal("微软账户", microsoft.AccountBadge);
        Assert.Equal("离线账户", offline.AccountBadge);
    }

    [Fact]
    public async Task 首页详情_第三方验证账户显示主机名与UUID()
    {
        var account = new Account
        {
            Type = AccountType.Authlib,
            Username = "littleskin_player",
            Uuid = "0123456789abcdef0123456789abcdef",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        };

        var viewModel = await CreateLoadedViewModelAsync(account);

        Assert.Equal("littleskin.cn · UUID 01234567…", viewModel.AccountDetail);
    }

    [Fact]
    public async Task 首页详情_第三方验证账户API根非法时不显示主机名()
    {
        var account = new Account
        {
            Type = AccountType.Authlib,
            Username = "littleskin_player",
            Uuid = "0123456789abcdef0123456789abcdef",
            AuthServerUrl = null,
        };

        var viewModel = await CreateLoadedViewModelAsync(account);

        Assert.Equal("UUID 01234567…", viewModel.AccountDetail);
    }

    [Fact]
    public async Task 首页详情_微软与离线账户不带主机名()
    {
        var microsoft = await CreateLoadedViewModelAsync(new Account
        {
            Type = AccountType.Microsoft,
            Username = "Steve",
            Uuid = "11111111111111111111111111111111",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        });

        var offline = await CreateLoadedViewModelAsync(new Account
        {
            Type = AccountType.Offline,
            Username = "Alex",
            Uuid = "22222222222222222222222222222222",
        });

        Assert.Equal("UUID 11111111…", microsoft.AccountDetail);
        Assert.Equal("UUID 22222222…", offline.AccountDetail);
    }

    [Fact]
    public async Task 首页账户卡片_没有账户时保持无账户态()
    {
        var viewModel = new HomeViewModel(accountService: new FakeAccountService(null));
        await viewModel.OnNavigatedToAsync();

        Assert.False(viewModel.HasAccount);
        Assert.Equal("无账户", viewModel.AccountBadge);
        Assert.Equal("点击添加账户后即可开始游戏", viewModel.AccountDetail);
    }

    // ==================== 辅助 ====================

    private static async Task<HomeViewModel> CreateLoadedViewModelAsync(Account active)
    {
        var viewModel = new HomeViewModel(accountService: new FakeAccountService(active));
        await viewModel.OnNavigatedToAsync();
        return viewModel;
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
