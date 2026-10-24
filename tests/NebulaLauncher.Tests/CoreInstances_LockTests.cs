using System.Diagnostics;
using System.Text.Json;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例运行锁（<c>&lt;游戏目录&gt;/.nebula-run.json</c>）。
///
/// 断言只打在「判错会让用户打不开游戏」的那几处：
/// 1. 判活靠 PID + 进程启动时间，只比 PID 会被系统复用骗过；
/// 2. 僵死锁与损坏锁必须能接管——否则实例会永久显示「已有游戏在运行」；
/// 3. 释放只删自己那份（令牌比对），否则会把别人正在用的锁抹掉，重复启动保护直接失效；
/// 4. 跨目录互不影响：每个实例的锁是它自己目录里的文件。
///
/// 这些都只需要真实文件系统，不碰进程生命周期以外的任何外部状态。
/// </summary>
public class CoreInstances_LockTests
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 获取_写入锁文件且能查询到持锁进程()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-a");
        Directory.CreateDirectory(directory);

        var service = new InstanceLockService(NullAppLog.Instance);

        using var handle = await service.TryAcquireAsync(directory, "1.20.1-Fabric");

        Assert.NotNull(handle);
        Assert.Equal(Environment.ProcessId, handle!.Info.Pid);
        Assert.Equal("1.20.1-Fabric", handle.Info.VersionName);
        Assert.False(string.IsNullOrWhiteSpace(handle.Info.Token));

        // 落盘的令牌必须与句柄里的一致：释放靠它认领自己写的那份。
        var path = Path.Combine(directory, InstanceLockService.LockFileName);
        Assert.True(File.Exists(path));

        var onDisk = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), WriteOptions);
        Assert.NotNull(onDisk);
        Assert.Equal(handle.Info.Token, onDisk!.Token);
        Assert.Equal(Environment.ProcessId, onDisk.Pid);

        // 进程启动时间也要写进文件：它是排除 PID 复用的唯一依据。
        var expected = new DateTimeOffset(
            Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            TimeSpan.Zero);
        Assert.True((onDisk.ProcessStartUtc - expected).Duration() < TimeSpan.FromSeconds(5));

        var running = await service.QueryAsync(directory);
        Assert.NotNull(running);
        Assert.Equal(handle.Info.Token, running!.Token);
    }

    [Fact]
    public async Task 冲突_同一目录第二次获取返回null并报告持锁进程()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-b");
        Directory.CreateDirectory(directory);

        // 两个服务实例模拟两个启动器进程：它们只共享磁盘上的锁文件。
        var first = new InstanceLockService(NullAppLog.Instance);
        var second = new InstanceLockService(NullAppLog.Instance);

        using var held = await first.TryAcquireAsync(directory, "1.20.1");
        Assert.NotNull(held);

        var blocked = await second.TryAcquireAsync(directory, "1.20.1");
        Assert.Null(blocked);

        var running = await second.QueryAsync(directory);
        Assert.NotNull(running);
        Assert.Equal(Environment.ProcessId, running!.Pid);
        Assert.Equal("1.20.1", running.VersionName);

        // 抢不到的一方不能动锁文件：它还得让持锁者正常释放。
        Assert.Equal(held!.Info.Token, running.Token);
    }

    [Fact]
    public async Task 跨目录隔离_不同游戏目录各自可获取互不冲突()
    {
        using var temp = new TempDirectory();
        var left = temp.Combine("instance-left");
        var right = temp.Combine("instance-right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);

        var service = new InstanceLockService(NullAppLog.Instance);

        using var leftHandle = await service.TryAcquireAsync(left, "1.20.1");
        using var rightHandle = await service.TryAcquireAsync(right, "1.21.4");

        Assert.NotNull(leftHandle);
        Assert.NotNull(rightHandle);
        Assert.NotEqual(leftHandle!.Info.Token, rightHandle!.Info.Token);

        // 释放左边不影响右边：锁是按目录存的，不是全局单例。
        leftHandle.Dispose();
        Assert.Null(await service.QueryAsync(left));
        Assert.NotNull(await service.QueryAsync(right));
    }

    [Fact]
    public async Task 僵死接管_持锁进程已退出时接管并视为无锁()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-stale");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, InstanceLockService.LockFileName);

        // 真的起一个进程再让它退出，用它的 PID：这是「PID 还被锁文件记着但进程已经没了」的完整形态。
        var deadPid = StartAndCollectExitedProcessId();

        var stale = new InstanceLockInfo
        {
            Pid = deadPid,
            ProcessStartUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            VersionName = "1.19.2",
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            Token = "stale-token",
            GameDirectory = directory,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(stale, WriteOptions));

        var service = new InstanceLockService(NullAppLog.Instance);

        // 查询层先要判定「不是运行中」：否则版本页会永远显示一个早就不存在的游戏。
        Assert.Null(await service.QueryAsync(directory));

        using var handle = await service.TryAcquireAsync(directory, "1.20.1");

        Assert.NotNull(handle);
        Assert.NotEqual("stale-token", handle!.Info.Token);

        var onDisk = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), WriteOptions);
        Assert.Equal(handle.Info.Token, onDisk!.Token);
    }

    [Fact]
    public async Task 损坏文件接管_锁文件不是JSON时接管而不是抛异常()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-corrupt");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, InstanceLockService.LockFileName);

        // 上次写盘被打断 / 用户手改过：内容读不出结构。
        File.WriteAllText(path, "{ 这不是 JSON");

        var service = new InstanceLockService(NullAppLog.Instance);

        Assert.Null(await service.QueryAsync(directory));

        using var handle = await service.TryAcquireAsync(directory, "1.20.1");

        Assert.NotNull(handle);
        Assert.True(File.Exists(path));

        var onDisk = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), WriteOptions);
        Assert.Equal(handle!.Info.Token, onDisk!.Token);
    }

    [Fact]
    public async Task Dispose幂等_重复释放不抛异常且锁文件只删一次()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-idempotent");
        Directory.CreateDirectory(directory);

        var service = new InstanceLockService(NullAppLog.Instance);
        var path = Path.Combine(directory, InstanceLockService.LockFileName);

        var handle = await service.TryAcquireAsync(directory, "1.20.1");
        Assert.NotNull(handle);

        handle!.Dispose();
        Assert.False(File.Exists(path));

        // 第二次释放：文件已不存在，必须静默返回。
        handle.Dispose();
        handle.Dispose();
        Assert.False(File.Exists(path));

        await handle.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 释放后可重新获取_令牌每次不同()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-reacquire");
        Directory.CreateDirectory(directory);

        var service = new InstanceLockService(NullAppLog.Instance);

        var first = await service.TryAcquireAsync(directory, "1.20.1");
        Assert.NotNull(first);
        var firstToken = first!.Info.Token;
        first.Dispose();

        var second = await service.TryAcquireAsync(directory, "1.20.1");
        Assert.NotNull(second);
        Assert.NotEqual(firstToken, second!.Info.Token);

        second.Dispose();
    }

    [Fact]
    public async Task 令牌不匹配时不删除别人的锁()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-handover");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, InstanceLockService.LockFileName);

        var service = new InstanceLockService(NullAppLog.Instance);

        var handle = await service.TryAcquireAsync(directory, "1.20.1");
        Assert.NotNull(handle);
        var myToken = handle!.Info.Token;

        // 模拟「我们被判成僵死、另一个启动器接管了锁」：文件内容换掉，令牌不同。
        var takeover = new InstanceLockInfo
        {
            Pid = Environment.ProcessId,
            ProcessStartUtc = new DateTimeOffset(
                Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                TimeSpan.Zero),
            VersionName = "1.21.4",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Token = "another-process-token",
            GameDirectory = directory,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(takeover, WriteOptions));

        handle.Dispose();

        // 释放必须只删自己写的那份：删掉别人的锁等于把重复启动保护关掉。
        Assert.True(File.Exists(path));

        var remaining = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), WriteOptions);
        Assert.Equal("another-process-token", remaining!.Token);
        Assert.NotEqual(myToken, remaining.Token);
    }

    /// <summary>
    /// 游戏进程启动后锁的持有者改写为游戏进程：另一个启动器进程（新 service）判活看的是
    /// 游戏 PID，而不是「已经退出的启动器 PID」。这条是 R34–R36 审查的 P2-6 回归。
    /// </summary>
    [Fact]
    public async Task 改持锁进程_以游戏PID判活阻止第二个启动器接管()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("instance-owner");
        Directory.CreateDirectory(directory);

        var launcherProcess = new InstanceLockService(NullAppLog.Instance);
        using var handle = await launcherProcess.TryAcquireAsync(directory, "1.20.1");
        Assert.NotNull(handle);

        // 用当前测试进程模拟「游戏进程」：PID 与启动时间都真实存在。
        var gameStartUtc = new DateTimeOffset(
            Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            TimeSpan.Zero);

        Assert.True(handle!.UpdateProcessId(Environment.ProcessId, gameStartUtc));
        Assert.Equal(Environment.ProcessId, handle.Info.Pid);

        // 落盘内容也必须更新：判活读的是文件，不是内存。
        var path = Path.Combine(directory, InstanceLockService.LockFileName);
        var onDisk = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), WriteOptions);
        Assert.Equal(Environment.ProcessId, onDisk!.Pid);
        Assert.Equal(handle.Info.Token, onDisk.Token);

        // 模拟「启动器关闭后重开」：新 service 看不到旧启动器，只能靠锁文件里的游戏 PID 判活。
        var reopenedLauncher = new InstanceLockService(NullAppLog.Instance);
        var blocked = await reopenedLauncher.TryAcquireAsync(directory, "1.20.1");
        Assert.Null(blocked);

        var running = await reopenedLauncher.QueryAsync(directory);
        Assert.NotNull(running);
        Assert.Equal(Environment.ProcessId, running!.Pid);
    }

    /// <summary>
    /// 起一个立刻退出的进程并拿到它的 PID。沙箱里起不了进程时退化成「几乎不可能存在的 PID」：
    /// 两条路径对被测代码都是「进程已不存在」，断言不变。
    /// </summary>
    private static int StartAndCollectExitedProcessId()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            if (process is not null)
            {
                process.WaitForExit(5000);
                return process.Id;
            }
        }
        catch (Exception)
        {
            // 落到下面的退化分支。
        }

        return int.MaxValue - 1;
    }
}