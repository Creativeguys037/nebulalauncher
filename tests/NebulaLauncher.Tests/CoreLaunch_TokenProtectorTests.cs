using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>账户落盘时的令牌保护链路（DPAPI / 退化为 Base64）。</summary>
public class CoreLaunch_TokenProtectorTests
{
    [Fact]
    public void Protect_UsesDpapiOnWindowsWhenPlatformSupportsIt()
    {
        var platform = new FakePlatformService { SupportsSecureTokenStorage = true };

        var stored = TokenProtector.Protect("token-value", platform);
        Assert.NotNull(stored);

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(TokenProtector.DpapiPrefix, stored, StringComparison.Ordinal);
            Assert.Equal("token-value", TokenProtector.Unprotect(stored, platform));
        }
        else
        {
            // 非 Windows 没有等价密钥保护，只能退化为编码。
            Assert.StartsWith(TokenProtector.PlainPrefix, stored, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("token-value", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Unprotect_OfForeignDpapiBlob_ReturnsNullInsteadOfThrowing()
    {
        var platform = new FakePlatformService { SupportsSecureTokenStorage = true };
        var foreign = TokenProtector.DpapiPrefix + Convert.ToBase64String("not a dpapi payload"u8.ToArray());

        // 解不开就是解不开：返回 null 让上层重新登录，绝不抛给 UI。
        Assert.Null(TokenProtector.Unprotect(foreign, platform));
    }

    [Fact]
    public async Task AccountService_RoundTripsTokensThroughStorage()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var platform = new FakePlatformService { SupportsSecureTokenStorage = true };

        var service = new AccountService(paths, NullAppLog.Instance, platform);
        await service.LoadAsync();
        await service.AddOfflineAccountAsync("TokenHost");

        var json = await File.ReadAllTextAsync(paths.AccountsFile);
        Assert.Contains("\"activeAccountId\"", json, StringComparison.Ordinal);
        Assert.Contains("TokenHost", json, StringComparison.Ordinal);

        var reloaded = new AccountService(paths, NullAppLog.Instance, platform);
        await reloaded.LoadAsync();

        Assert.Equal(AccountType.Offline, Assert.Single(reloaded.Accounts).Type);
    }
}
