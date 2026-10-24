using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>离线账户落盘往返、令牌保护、活动账户与实例目录。</summary>
public class CoreLaunch_AccountServiceTests
{
    [Fact]
    public async Task AddOfflineAccount_WritesUuidAndPersists()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var platform = new FakePlatformService { SupportsSecureTokenStorage = false };

        var service = new AccountService(paths, NullAppLog.Instance, platform);
        await service.LoadAsync();

        var result = await service.AddOfflineAccountAsync("Steve");

        Assert.True(result.Success);
        Assert.Equal("Steve", result.Account!.Username);
        Assert.Equal("5627dd98e6bebc21f8a8e92344183641", result.Account.Uuid);
        Assert.Equal(AccountType.Offline, result.Account.Type);
        Assert.Equal(result.Account.Id, service.ActiveAccount?.Id);
        Assert.True(File.Exists(paths.AccountsFile));

        var reloaded = new AccountService(paths, NullAppLog.Instance, platform);
        await reloaded.LoadAsync();

        var persisted = Assert.Single(reloaded.Accounts);
        Assert.Equal("Steve", persisted.Username);
        Assert.Equal("5627dd98e6bebc21f8a8e92344183641", persisted.Uuid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ThisNameIsWayTooLongForMinecraft")]
    public async Task AddOfflineAccount_RejectsInvalidNames(string name)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new AccountService(paths, NullAppLog.Instance, new FakePlatformService());

        var result = await service.AddOfflineAccountAsync(name);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(service.Accounts);
    }

    [Fact]
    public async Task Load_WhenAccountsFileCorrupt_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        await File.WriteAllTextAsync(paths.AccountsFile, "}{");

        var service = new AccountService(paths, NullAppLog.Instance, new FakePlatformService());
        await service.LoadAsync();

        Assert.Empty(service.Accounts);
        Assert.True(File.Exists(paths.AccountsFile + ".bak"));
    }

    [Fact]
    public async Task Remove_ClearsActiveAccount()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new AccountService(paths, NullAppLog.Instance, new FakePlatformService());

        await service.LoadAsync();
        var added = await service.AddOfflineAccountAsync("Alex");

        await service.RemoveAsync(added.Account!.Id);

        Assert.Empty(service.Accounts);
        Assert.Null(service.ActiveAccount);
    }

    [Fact]
    public async Task EnsureValidActiveAccount_ReturnsOfflineAccount()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new AccountService(paths, NullAppLog.Instance, new FakePlatformService());

        await service.LoadAsync();
        await service.AddOfflineAccountAsync("Alex");

        var active = await service.EnsureValidActiveAccountAsync();

        Assert.NotNull(active);
        Assert.Equal("Alex", active!.Username);
    }

    [Fact]
    public async Task LoadAsync_IsIdempotent_AndReloadPicksUpExternalWrites()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var platform = new FakePlatformService { SupportsSecureTokenStorage = false };

        var seed = new AccountService(paths, NullAppLog.Instance, platform);
        await seed.LoadAsync();
        Assert.True((await seed.AddOfflineAccountAsync("Steve")).Success);

        // 冷启动：组合根预热过一次之后，页面再进来不该重读盘。
        var service = new AccountService(paths, NullAppLog.Instance, platform);
        await service.LoadAsync();
        Assert.Equal("Steve", service.ActiveAccount?.Username);

        var changes = 0;
        service.AccountsChanged += (_, _) => changes++;

        // 外部把文件换成另一个账户（另一个进程改了 accounts.json）。
        File.Delete(paths.AccountsFile);
        var external = new AccountService(paths, NullAppLog.Instance, platform);
        await external.LoadAsync();
        Assert.True((await external.AddOfflineAccountAsync("Alex")).Success);

        // 幂等：第二次 LoadAsync 直接返回，既不重读盘也不发事件。
        // 否则「启动预热 → 进账户页」会用磁盘上的旧状态盖掉内存里刚改完的当前账户。
        await service.LoadAsync();

        Assert.Equal("Steve", service.ActiveAccount?.Username);
        Assert.Equal(0, changes);

        // 真要读回外部改动必须显式 ReloadAsync。
        // 走接口引用调用：接口里那份是 DIM 默认实现，类上的同名方法必须隐式实现它，
        // 否则接口分发会退化成「转调 LoadAsync」，这里的断言就是「还是重读了」。
        await ((IAccountService)service).ReloadAsync();

        Assert.Equal("Alex", service.ActiveAccount?.Username);
        Assert.Single(service.Accounts);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task LoadAsync_WhenFileUnreadable_DoesNotMarkLoaded_SoLaterRetryWorks()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var platform = new FakePlatformService { SupportsSecureTokenStorage = false };

        var seed = new AccountService(paths, NullAppLog.Instance, platform);
        await seed.LoadAsync();
        Assert.True((await seed.AddOfflineAccountAsync("Steve")).Success);

        // 坏 JSON：这一次读不出来。
        await File.WriteAllTextAsync(paths.AccountsFile, "}{");

        var service = new AccountService(paths, NullAppLog.Instance, platform);
        await service.LoadAsync();
        Assert.Empty(service.Accounts);

        // 文件被修好（用户手工改 / 另一个进程写回）。上一次失败不能算「已加载」，
        // 否则本次会话永久停在「无账户」，而生产代码里没有任何 ReloadAsync 调用方会来救。
        File.Delete(paths.AccountsFile);
        var external = new AccountService(paths, NullAppLog.Instance, platform);
        await external.LoadAsync();
        Assert.True((await external.AddOfflineAccountAsync("Alex")).Success);

        await service.LoadAsync();

        Assert.Equal("Alex", service.ActiveAccount?.Username);
        Assert.Single(service.Accounts);
    }

    [Fact]
    public async Task EnsureValidActiveAccount_WithoutAccounts_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new AccountService(paths, NullAppLog.Instance, new FakePlatformService());

        await service.LoadAsync();

        Assert.Null(await service.EnsureValidActiveAccountAsync());
    }

    [Fact]
    public void TokenProtector_RoundTripsWithoutSecureStorage()
    {
        var platform = new FakePlatformService { SupportsSecureTokenStorage = false };

        var stored = TokenProtector.Protect("secret-token", platform);

        Assert.NotNull(stored);
        Assert.StartsWith(TokenProtector.PlainPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", stored, StringComparison.Ordinal);
        Assert.Equal("secret-token", TokenProtector.Unprotect(stored, platform));
    }

    [Fact]
    public void TokenProtector_HandlesEmptyAndGarbage()
    {
        var platform = new FakePlatformService();

        Assert.Null(TokenProtector.Protect(null, platform));
        Assert.Null(TokenProtector.Protect(string.Empty, platform));
        Assert.Null(TokenProtector.Unprotect(null, platform));
        Assert.Null(TokenProtector.Unprotect("dpapi:not-base64!!", platform));
        Assert.Null(TokenProtector.Unprotect("b64:%%%", platform));
    }
}

/// <summary>实例目录创建、落盘往返、致命路径穿越防护。</summary>
public class CoreLaunch_InstanceServiceTests
{
    [Fact]
    public async Task Create_MakesDirectoryAndPersistsJson()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("原版生存", "1.20.1");

        var directory = service.GetGameDirectory(instance.Id);
        Assert.True(Directory.Exists(directory));
        Assert.True(Path.IsPathRooted(directory));
        Assert.True(File.Exists(Path.Combine(directory, "instance.json")));

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        var loaded = Assert.Single(reloaded.Instances);
        Assert.Equal("原版生存", loaded.Name);
        Assert.Equal("1.20.1", loaded.VersionName);
    }

    [Fact]
    public async Task Save_UpdatesExistingInstance()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("测试", "1.20.1");

        instance.MaxMemoryMb = 8192;
        instance.TotalPlayTimeSeconds = 120;
        await service.SaveAsync(instance);

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        var loaded = Assert.Single(reloaded.Instances);
        Assert.Equal(8192, loaded.MaxMemoryMb);
        Assert.Equal(120, loaded.TotalPlayTimeSeconds);
    }

    [Fact]
    public async Task Delete_RemovesDirectory()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("临时", "1.20.1");
        var directory = service.GetGameDirectory(instance.Id);

        await service.DeleteAsync(instance.Id);

        Assert.False(Directory.Exists(directory));
        Assert.Empty(service.Instances);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public void GetGameDirectory_RejectsTraversal(string id)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        Assert.Throws<ArgumentException>(() => service.GetGameDirectory(id));
    }
}


