using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Net;
using NebulaLauncher.Core.Settings;
using NebulaLauncher.Core.Transfer;

namespace NebulaLauncher.Tests;

/// <summary>
/// 代理密码的落盘保护（DPAPI）与旧配置迁移。
///
/// 为什么单独测：
/// 1) 「明文从来没写过磁盘」这件事无法靠读代码确认——只有真跑一次保存、再把文件读回来断言语义才成立；
/// 2) 迁移是一次性动作（密文已存在就不再迁移），写错了会表现为「用户重启后代理失效」，
///    而那台机器上往往没有日志；
/// 3) 配置包导出会把代理凭据剥掉，新增的密文字段一旦漏掉，等于把「看起来像密文、换账户就能解」的东西带出去。
/// </summary>
public class CoreNet_ProxyCredentialTests
{
    // Windows 上 DPAPI 可用；其他平台只验证退化编码，不假设加密成功。
    private static bool DpapiUsable => OperatingSystem.IsWindows();

    [Fact]
    public void Protect_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ProxyCredentialStore.Protect(null));
        Assert.Null(ProxyCredentialStore.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ProxyCredentialStore.Unprotect(null));
        Assert.Null(ProxyCredentialStore.Unprotect(string.Empty));
    }

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        const string password = "p@ss word:中文 #1";

        var stored = ProxyCredentialStore.Protect(password);

        Assert.NotNull(stored);
        Assert.Equal(password, ProxyCredentialStore.Unprotect(stored));
    }

    [Fact]
    public void Protect_OnWindows_UsesDpapiPrefix()
    {
        var stored = ProxyCredentialStore.Protect("secret");

        Assert.NotNull(stored);

        if (DpapiUsable)
        {
            Assert.StartsWith(ProxyCredentialStore.DpapiPrefix, stored, StringComparison.Ordinal);
            Assert.True(ProxyCredentialStore.IsProtected(stored));
        }
        else
        {
            Assert.StartsWith(ProxyCredentialStore.PlainPrefix, stored, StringComparison.Ordinal);
            Assert.False(ProxyCredentialStore.IsProtected(stored));
        }
    }

    [Fact]
    public void ProtectedString_DoesNotContainPlaintext()
    {
        const string password = "UnmistakableString123";

        var stored = ProxyCredentialStore.Protect(password);

        Assert.NotNull(stored);
        Assert.DoesNotContain(password, stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unprotect_LegacyPlaintextWithoutPrefix_ReturnsAsIs()
    {
        // 旧版本从没加密过：文件里就是原文，迁移前必须能直接用。
        Assert.Equal("oldpass", ProxyCredentialStore.Unprotect("oldpass"));
    }

    [Fact]
    public void Unprotect_CorruptBase64_ReturnsNullWithoutThrowing()
    {
        Assert.Null(ProxyCredentialStore.Unprotect(ProxyCredentialStore.DpapiPrefix + "not-base64!!"));
        Assert.Null(ProxyCredentialStore.Unprotect(ProxyCredentialStore.PlainPrefix + "%%%"));
    }

    [Fact]
    public void Describe_ReflectsStorageState()
    {
        Assert.Equal("未设置", ProxyCredentialStore.Describe(null));
        Assert.Equal("未设置", ProxyCredentialStore.Describe(string.Empty));

        var stored = ProxyCredentialStore.Protect("x");

        Assert.NotNull(stored);
        Assert.Contains("加密", ProxyCredentialStore.Describe(stored), StringComparison.Ordinal);

        Assert.Contains(
            "未加密",
            ProxyCredentialStore.Describe(ProxyCredentialStore.PlainPrefix + "eA=="),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_CopiesProtectedField()
    {
        var settings = new AppSettings { ProxyPasswordProtected = "dpapi:abc" };

        var clone = settings.Clone();

        Assert.Equal("dpapi:abc", clone.ProxyPasswordProtected);
        Assert.NotSame(settings, clone);
    }

    [Fact]
    public async Task Save_WritesCipherInsteadOfPlaintext()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new SettingsService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        service.Current.ProxyMode = ProxyModes.Custom;
        service.Current.ProxyAddress = "http://127.0.0.1:7890";
        service.Current.ProxyUserName = "user";
        service.Current.ProxyPassword = "plain-secret";
        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(paths.SettingsFile);

        Assert.DoesNotContain("plain-secret", json, StringComparison.Ordinal);
        Assert.Contains("proxyPasswordProtected", json, StringComparison.Ordinal);
        Assert.NotNull(service.Current.ProxyPasswordProtected);
        Assert.True(ProxyCredentialStore.IsStored(service.Current.ProxyPasswordProtected));

        // 内存里仍然留着明文：本次会话的请求还要用它，而磁盘上已经没有。
        Assert.Equal("plain-secret", service.Current.ProxyPassword);
    }

    [Fact]
    public async Task Load_MigratesLegacyPlaintextToCipher()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "proxyMode": "custom",
              "proxyAddress": "http://127.0.0.1:7890",
              "proxyUserName": "user",
              "proxyPassword": "legacy-plain"
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        // 迁移只改内存里的密文字段，磁盘要等下一次保存。
        Assert.Equal("legacy-plain", service.Current.ProxyPassword);
        Assert.True(ProxyCredentialStore.IsStored(service.Current.ProxyPasswordProtected));
        Assert.Equal("legacy-plain", ProxyCredentialStore.Unprotect(service.Current.ProxyPasswordProtected));

        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(paths.SettingsFile);

        Assert.DoesNotContain("legacy-plain", json, StringComparison.Ordinal);
        Assert.Contains("proxyPasswordProtected", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_DecryptsStoredCipherIntoMemory()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var first = new SettingsService(paths, NullAppLog.Instance);

        await first.LoadAsync();
        first.Current.ProxyMode = ProxyModes.Custom;
        first.Current.ProxyAddress = "http://127.0.0.1:7890";
        first.Current.ProxyUserName = "user";
        first.Current.ProxyPassword = "persisted-secret";
        await first.SaveAsync();

        // 模拟重启：新实例读同一个文件，明文必须能解出来给代理用。
        var second = new SettingsService(paths, NullAppLog.Instance);
        await second.LoadAsync();

        Assert.Equal("persisted-secret", second.Current.ProxyPassword);
    }

    [Fact]
    public async Task Load_CipherIsKeptWhenAlreadyProtected()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var first = new SettingsService(paths, NullAppLog.Instance);

        await first.LoadAsync();
        first.Current.ProxyMode = ProxyModes.Custom;
        first.Current.ProxyAddress = "http://127.0.0.1:7890";
        first.Current.ProxyUserName = "user";
        first.Current.ProxyPassword = "keep-me";
        await first.SaveAsync();

        var cipherBefore = first.Current.ProxyPasswordProtected;

        var second = new SettingsService(paths, NullAppLog.Instance);
        await second.LoadAsync();
        await second.SaveAsync();

        // DPAPI 每次加密都带随机盐，密文一定不同；这里要断言的是「没改密码时不需要重新加密」——
        // 也就是保存前后密文相同（同一个内存对象、同一次写入），而明文仍能解出来。
        Assert.Equal("keep-me", ProxyCredentialStore.Unprotect(second.Current.ProxyPasswordProtected));
        Assert.Equal(cipherBefore, second.Current.ProxyPasswordProtected);
    }

    [Fact]
    public async Task Normalize_DropsPasswordWhenUserNameCleared()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "proxyMode": "custom",
              "proxyAddress": "http://127.0.0.1:7890",
              "proxyUserName": "",
              "proxyPassword": "orphan",
              "proxyPasswordProtected": "dpapi:aaaa"
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Null(service.Current.ProxyPassword);
        Assert.Null(service.Current.ProxyPasswordProtected);
    }

    [Fact]
    public async Task Load_Migration_ScrubsPlaintextFromDiskImmediately()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "proxyMode": "custom",
              "proxyAddress": "http://127.0.0.1:7890",
              "proxyUserName": "user",
              "proxyPassword": "scrub-me-now"
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        // 迁移必须当场落盘：用户可能永远不会再点「保存设置」，明文不该留在磁盘上等。
        var json = await File.ReadAllTextAsync(paths.SettingsFile);

        Assert.DoesNotContain("scrub-me-now", json, StringComparison.Ordinal);
        Assert.Contains("proxyPasswordProtected", json, StringComparison.Ordinal);
        Assert.Equal("scrub-me-now", service.Current.ProxyPassword);
    }

    [Fact]
    public void Redaction_StripsPlainAndProtectedPassword()
    {
        var fields = ProfileSettingsRedaction.Fields;

        Assert.Contains("proxyPassword", fields);
        Assert.Contains("proxyPasswordProtected", fields);
        Assert.Contains("proxyUserName", fields);
    }
}