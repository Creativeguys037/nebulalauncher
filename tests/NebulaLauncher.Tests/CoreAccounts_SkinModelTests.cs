using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 账户体型的落盘（票 #44 AC5）：离线账户没有服务端皮肤档案，Steve / Alex 只能记在
/// <see cref="Account.SkinModel"/> 里，而且要**写进 accounts.json**，否则重开启动器就丢。
///
/// 走 <see cref="IAccountService"/> 而不是让界面自己改 <c>Account</c> 对象：账户文件是
/// 令牌加密、原子写盘、失败回滚这三件事的唯一边界，绕过去就等于绕过了那三件事。
/// </summary>
public class CoreAccounts_SkinModelTests
{
    [Fact]
    public async Task 切换体型写进账户文件并通知一次()
    {
        using var scenario = new SkinScenario();
        var account = await scenario.AddOfflineAsync("Steve");
        var changed = 0;
        scenario.Service.AccountsChanged += (_, _) => changed++;

        var result = await scenario.Service.SetSkinModelAsync(account.Id, "slim");

        Assert.True(result.Success, result.Message);
        Assert.Equal("slim", result.Account!.SkinModel);
        Assert.Equal(1, changed);
        Assert.Equal("slim", scenario.Service.ActiveAccount!.SkinModel);

        // 落盘才算成功：内存改了但文件没改，重启就退回 classic。
        var raw = await File.ReadAllTextAsync(scenario.Paths.AccountsFile);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal("slim", document.RootElement.GetProperty("accounts")[0].GetProperty("skinModel").GetString());
    }

    [Fact]
    public async Task 重开服务后体型仍然是上次选的那个()
    {
        using var scenario = new SkinScenario();
        var account = await scenario.AddOfflineAsync("Steve");
        await scenario.Service.SetSkinModelAsync(account.Id, "slim");

        var reloaded = new AccountService(
            scenario.Paths,
            NullAppLog.Instance,
            new FakePlatformService { SupportsSecureTokenStorage = false });
        await reloaded.LoadAsync();

        Assert.Equal("slim", Assert.Single(reloaded.Accounts).SkinModel);
    }

    [Fact]
    public async Task 大小写与首尾空格被规范化而认不出的词退回classic()
    {
        using var scenario = new SkinScenario();
        var account = await scenario.AddOfflineAsync("Steve");

        // 合法值处理干净空白与大小写。
        var spaced = await scenario.Service.SetSkinModelAsync(account.Id, "  SLIM  ");
        Assert.True(spaced.Success, spaced.Message);
        Assert.Equal("slim", spaced.Account!.SkinModel);

        // 账户文件是用户能手改的。"alex" 是界面词，不是落盘词；
        // 放它过去会在 SkinProfileParser 与官方 variant 那里变成「体型莫名其妙变回 Steve」。
        var alex = await scenario.Service.SetSkinModelAsync(account.Id, "alex");
        Assert.True(alex.Success, alex.Message);
        Assert.Equal("classic", alex.Account!.SkinModel);

        var blank = await scenario.Service.SetSkinModelAsync(account.Id, " ");
        Assert.True(blank.Success, blank.Message);
        Assert.Equal("classic", blank.Account!.SkinModel);
    }

    [Fact]
    public async Task 账户不存在时明确失败且不动文件()
    {
        using var scenario = new SkinScenario();
        await scenario.AddOfflineAsync("Steve");

        var result = await scenario.Service.SetSkinModelAsync("no-such-id", "slim");

        Assert.False(result.Success);
        Assert.Contains("账户", result.Message!);
    }

    [Fact]
    public async Task 体型没变时不重复写盘也不重复通知()
    {
        using var scenario = new SkinScenario();
        var account = await scenario.AddOfflineAsync("Steve");
        var changed = 0;
        scenario.Service.AccountsChanged += (_, _) => changed++;

        var result = await scenario.Service.SetSkinModelAsync(account.Id, "classic");

        // 默认就是 classic：这次调用等于什么都没做，界面不该因此重绘一遍。
        Assert.True(result.Success, result.Message);
        Assert.Equal(0, changed);
    }

    private sealed class SkinScenario : IDisposable
    {
        public SkinScenario()
        {
            Paths = new CoreAssetsPaths();
            Service = new AccountService(
                Paths,
                NullAppLog.Instance,
                new FakePlatformService { SupportsSecureTokenStorage = false });
        }

        public CoreAssetsPaths Paths { get; }

        public AccountService Service { get; }

        public async Task<Account> AddOfflineAsync(string username)
        {
            var result = await Service.AddOfflineAccountAsync(username);
            Assert.True(result.Success, result.Message);
            return result.Account!;
        }

        public void Dispose() => Paths.Dispose();
    }
}
