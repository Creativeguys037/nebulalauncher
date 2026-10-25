using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 账户类型文案的共享映射（<see cref="AccountTypeDisplay"/>）。
///
/// 票 28 的根因是三处各写一份判定，其中一处写成了二元判定。
/// 这些用例直接锁映射本身：长标签、短标签、详情三条出口对三种账户类型的口径，
/// 以及首页与账户页确实走同一份数据（而不是各自又写一份）。
/// </summary>
public class AppAccounts_TypeDisplayTests
{
    [Theory]
    [InlineData(AccountType.Offline, "离线账户", "离线")]
    [InlineData(AccountType.Microsoft, "微软账户", "微软")]
    [InlineData(AccountType.Authlib, "第三方验证", "第三方验证")]
    public void 映射_长标签与短标签对三种账户类型各自正确(
        AccountType type,
        string expectedBadge,
        string expectedShortBadge)
    {
        Assert.Equal(expectedBadge, AccountTypeDisplay.BadgeOf(type));
        Assert.Equal(expectedShortBadge, AccountTypeDisplay.ShortBadgeOf(type));
    }

    [Fact]
    public void 映射_短标签不因合并而变长()
    {
        // 账户列表行的视觉宽度依赖短标签：它不能长于长标签，
        // 否则合并之后列表会挤掉右侧的主机名。
        // 「第三方验证」本身已是最短表达，长短标签相同是正确的。
        foreach (var type in Enum.GetValues<AccountType>())
        {
            Assert.True(
                AccountTypeDisplay.ShortBadgeOf(type).Length <= AccountTypeDisplay.BadgeOf(type).Length,
                $"{type} 的短标签不应长于长标签");
        }
    }

    [Fact]
    public void 映射_第三方验证才带主机名()
    {
        var authlib = new Account
        {
            Type = AccountType.Authlib,
            Uuid = "abc",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        };
        var microsoft = new Account
        {
            Type = AccountType.Microsoft,
            Uuid = "abc",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        };
        var offline = new Account { Type = AccountType.Offline, Uuid = "abc" };

        Assert.Equal("littleskin.cn · UUID abc", AccountTypeDisplay.DetailOf(authlib, "UUID abc"));

        // 另外两类即使身上带着 API 根（历史数据 / 手工改过文件）也不显示主机名。
        Assert.Equal("UUID abc", AccountTypeDisplay.DetailOf(microsoft, "UUID abc"));
        Assert.Equal("UUID abc", AccountTypeDisplay.DetailOf(offline, "UUID abc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("littleskin.cn/api/yggdrasil")]
    [InlineData("not a url")]
    public void 映射_API根非法或缺失时不显示主机名(string apiRoot)
    {
        var account = new Account { Type = AccountType.Authlib, Uuid = "abc", AuthServerUrl = apiRoot };

        Assert.Null(AccountTypeDisplay.HostOf(apiRoot));
        Assert.Equal("UUID abc", AccountTypeDisplay.DetailOf(account, "UUID abc"));
    }

    [Fact]
    public void 映射_API根为null时不显示主机名()
    {
        var account = new Account { Type = AccountType.Authlib, Uuid = "abc", AuthServerUrl = null };

        Assert.Null(AccountTypeDisplay.HostOf(null));
        Assert.Equal("UUID abc", AccountTypeDisplay.DetailOf(account, "UUID abc"));
    }

    [Fact]
    public void 映射_主机名只取host不带路径与查询串()
    {
        Assert.Equal(
            "littleskin.cn",
            AccountTypeDisplay.HostOf("https://littleskin.cn/api/yggdrasil?token=secret"));
    }

    [Theory]
    [InlineData(AccountType.Offline, "离线", false, "")]
    [InlineData(AccountType.Microsoft, "微软", false, "")]
    [InlineData(AccountType.Authlib, "第三方验证", true, "littleskin.cn")]
    public void 账户列表行_合并后显示与合并前一致(
        AccountType type,
        string expectedTypeLabel,
        bool expectedIsAuthlib,
        string expectedAuthHost)
    {
        var account = new Account
        {
            Type = type,
            Username = "Steve",
            Uuid = "0123456789abcdef0123456789abcdef",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        };

        var row = new AccountRowViewModel(account, activeId: account.Id);

        Assert.Equal(expectedTypeLabel, row.TypeLabel);
        Assert.Equal(expectedIsAuthlib, row.IsAuthlib);
        Assert.Equal(expectedAuthHost, row.AuthHost);
        Assert.True(row.IsActive);
        Assert.Equal(type == AccountType.Microsoft, row.UseAurora);
    }
}
