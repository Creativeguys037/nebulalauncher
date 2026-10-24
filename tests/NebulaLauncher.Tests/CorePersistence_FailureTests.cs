using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Tests;

/// <summary>
/// 持久化失败不能报成功。
///
/// 两个模块以前都吞掉写盘异常后继续返回成功：用户看到「已添加」，重启后数据消失。
/// 这里的用例用「目标文件位置被同名目录占据」制造真实的写入失败（temp 写得进、Move 覆盖不过去），
/// 不依赖 ACL、也不 mock 文件系统。
/// </summary>
public class CorePersistence_FailureTests
{
    private static AccountService CreateAccountService(FakeLauncherPaths paths) =>
        new(paths, NullAppLog.Instance, new FakePlatformService { SupportsSecureTokenStorage = false });

    // ==================== 账户 ====================

    [Fact]
    public async Task 添加离线账户_写盘失败_返回失败并回滚内存()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = CreateAccountService(paths);

        // accounts.json 是同名目录：File.Move(temp, file, overwrite) 必然失败。
        Directory.CreateDirectory(paths.AccountsFile);

        var changed = 0;
        service.AccountsChanged += (_, _) => changed++;

        var result = await service.AddOfflineAccountAsync("Steve");

        Assert.False(result.Success);
        Assert.Contains("写入磁盘失败", result.Message);
        Assert.Empty(service.Accounts);
        Assert.Null(service.ActiveAccount);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task 添加离线账户_写盘成功_返回成功并落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = CreateAccountService(paths);

        var result = await service.AddOfflineAccountAsync("Alex");

        Assert.True(result.Success, result.Message);
        Assert.Single(service.Accounts);
        Assert.True(File.Exists(paths.AccountsFile));
        Assert.Equal(result.Account!.Id, service.ActiveAccount?.Id);
    }

    [Fact]
    public async Task 移除账户_写盘失败_抛异常并把账户放回原位置()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = CreateAccountService(paths);

        var added = await service.AddOfflineAccountAsync("Herobrine");
        Assert.True(added.Success, added.Message);

        // 让下一次落盘失败：删掉文件后在同一路径建目录。
        File.Delete(paths.AccountsFile);
        Directory.CreateDirectory(paths.AccountsFile);

        // 失败必须让调用方看得见（UI 才能显示「移除失败」而不是「已移除」）。
        await Assert.ThrowsAsync<IOException>(() => service.RemoveAsync(added.Account!.Id));

        Assert.Single(service.Accounts);
        Assert.Equal(added.Account!.Id, service.ActiveAccount?.Id);
    }

    [Fact]
    public async Task 添加离线账户_写入前取消_回滚内存()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = CreateAccountService(paths);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AddOfflineAccountAsync("Steve", cancellation.Token));

        // 取消也是「没落盘」：内存必须回到磁盘状态。
        Assert.Empty(service.Accounts);
        Assert.Null(service.ActiveAccount);
    }

    [Fact]
    public async Task 移除账户_写入前取消_账户仍在且保持活动()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = CreateAccountService(paths);

        var added = await service.AddOfflineAccountAsync("Alex");
        Assert.True(added.Success, added.Message);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RemoveAsync(added.Account!.Id, cancellation.Token));

        Assert.Single(service.Accounts);
        Assert.Equal(added.Account!.Id, service.ActiveAccount?.Id);
    }

    // ==================== 服务器 ====================

    [Fact]
    public async Task 添加服务器_写盘失败_抛IOException且不进入列表()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var service = new ServerService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        Directory.CreateDirectory(paths.ServersFile);

        var exception = await Assert.ThrowsAsync<IOException>(() => service.AddAsync(new ServerEntry
        {
            Name = "测试服",
            Host = "play.example.invalid",
            Port = 25565,
        }));

        Assert.Contains("写入磁盘失败", exception.Message);
        Assert.Empty(service.Servers);
    }

    [Fact]
    public async Task 添加服务器_写盘成功_进入列表并落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var service = new ServerService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        await service.AddAsync(new ServerEntry
        {
            Name = "测试服",
            Host = "play.example.invalid",
            Port = 25565,
        });

        Assert.Single(service.Servers);
        Assert.True(File.Exists(paths.ServersFile));
    }

    [Fact]
    public async Task 移除服务器_写盘失败_把条目放回列表()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var service = new ServerService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var entry = new ServerEntry
        {
            Name = "测试服",
            Host = "play.example.invalid",
            Port = 25565,
        };
        await service.AddAsync(entry);

        File.Delete(paths.ServersFile);
        Directory.CreateDirectory(paths.ServersFile);

        await Assert.ThrowsAsync<IOException>(() => service.RemoveAsync(entry.Id));

        var survivor = Assert.Single(service.Servers);
        Assert.Equal(entry.Id, survivor.Id);
    }
}
