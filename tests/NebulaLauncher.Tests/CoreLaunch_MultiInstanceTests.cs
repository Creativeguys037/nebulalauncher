using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Tests;

/// <summary>
/// 缺口 G5：多实例并行运行。
///
/// <see cref="GameLauncher.LaunchAsync"/> 的真路径要起 JVM（版本 JSON、Java、下载文件），
/// 单测里跑不起来，所以把多开的判定全部落在两个可独立构造的类型上：
/// <see cref="RunningGameRegistry"/>（谁在跑、能不能再进来、杀哪一个）与
/// <see cref="RunningGame"/>（每个会话自己的日志尾巴与崩溃上下文）。
///
/// 「同一游戏目录不能两个 JVM」的正确性边界本身由 <c>InstanceLockService</c> 负责且已有单测，
/// 这里只验证内存侧的第二道闸门——它管的是同一个启动器进程内的并发点击。
/// </summary>
public class CoreLaunch_MultiInstanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 两个不同目录可以同时注册并都被报告为在跑()
    {
        var registry = new RunningGameRegistry();
        var a = new Session(@"D:\instances\alpha", "sess-a", T0);
        var b = new Session(@"D:\instances\beta", "sess-b", T0.AddSeconds(1));

        Assert.True(registry.TryRegister(a.Game));
        Assert.True(registry.TryRegister(b.Game));

        var snapshot = registry.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("sess-a", snapshot[0].SessionId);
        Assert.Equal("sess-b", snapshot[1].SessionId);

        // 启动时刻升序：界面按这个顺序显示「谁先起的」，也是「最近一次启动」的判据。
        Assert.True(snapshot[0].StartedAt < snapshot[1].StartedAt);
    }

    [Fact]
    public void 同一目录第二次注册被拒()
    {
        var registry = new RunningGameRegistry();
        var first = new Session(@"D:\instances\alpha", "sess-a", T0);
        var second = new Session(@"D:\instances\alpha", "sess-b", T0.AddMinutes(1));

        Assert.True(registry.TryRegister(first.Game));
        Assert.False(registry.TryRegister(second.Game));

        // 被拒的那一次不能顶掉已有会话，否则「第二次启动」就把第一份的句柄丢了。
        var snapshot = registry.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("sess-a", snapshot[0].SessionId);
    }

    [Theory]
    [InlineData(@"D:\instances\alpha\", @"D:\instances\alpha")]
    [InlineData(@"D:\instances\ALPHA", @"D:\instances\alpha")]
    [InlineData(@"D:\instances\alpha", @"d:\INSTANCES\Alpha")]
    public void 同一目录的不同写法算同一个实例(string firstForm, string secondForm)
    {
        var registry = new RunningGameRegistry();
        var keyA = RunningGameInfo.KeyFor(firstForm);
        var keyB = RunningGameInfo.KeyFor(secondForm);

        Assert.True(registry.TryRegister(new Session(keyA, "sess-a", T0).Game));
        Assert.False(registry.TryRegister(new Session(keyB, "sess-b", T0.AddSeconds(1)).Game));
        Assert.True(registry.IsInstanceRunning(keyB));
    }

    [Fact]
    public void 规范化键去掉尾部分隔符()
    {
        Assert.Equal(
            RunningGameInfo.KeyFor(@"D:\instances\alpha"),
            RunningGameInfo.KeyFor(@"D:\instances\alpha\"));
    }

    [Fact]
    public void 按会话停止只影响目标会话()
    {
        var registry = new RunningGameRegistry();
        var a = new Session(@"D:\instances\alpha", "sess-a", T0);
        var b = new Session(@"D:\instances\beta", "sess-b", T0.AddSeconds(1));
        registry.TryRegister(a.Game);
        registry.TryRegister(b.Game);

        Assert.True(registry.KillBySession("sess-a", log: null));

        Assert.Equal(1, a.KillCount);
        Assert.Equal(0, b.KillCount);

        // 杀完必须复核：IsAlive 读的是进程真实状态，不信内存里的标志。
        Assert.False(registry.IsInstanceRunning(@"D:\instances\alpha"));
        Assert.True(registry.IsInstanceRunning(@"D:\instances\beta"));
    }

    [Fact]
    public void 停止不存在的会话返回false而不是抛异常()
    {
        var registry = new RunningGameRegistry();
        registry.TryRegister(new Session(@"D:\instances\alpha", "sess-a", T0).Game);

        Assert.False(registry.KillBySession("不存在的会话", log: null));
        Assert.False(registry.KillBySession(string.Empty, log: null));
    }

    [Fact]
    public void 已经退出的会话再杀一次返回false()
    {
        var registry = new RunningGameRegistry();
        var session = new Session(@"D:\instances\alpha", "sess-a", T0);
        registry.TryRegister(session.Game);

        Assert.True(registry.KillBySession("sess-a", log: null));
        Assert.False(registry.KillBySession("sess-a", log: null));
        Assert.Equal(1, session.KillCount);
    }

    [Fact]
    public void IsGameRunning语义是任一在跑()
    {
        var registry = new RunningGameRegistry();
        Assert.False(registry.AnyRunning);

        var a = new Session(@"D:\instances\alpha", "sess-a", T0);
        registry.TryRegister(a.Game);
        Assert.True(registry.AnyRunning);

        // 再起第二个：A 退出后因为 B 还在跑，整体仍必须为 true。
        var b = new Session(@"D:\instances\beta", "sess-b", T0.AddSeconds(1));
        registry.TryRegister(b.Game);
        Assert.True(registry.AnyRunning);

        a.Exit();
        Assert.True(registry.AnyRunning, "B 还在跑，整体不应该变成「没有游戏在运行」");

        registry.KillBySession("sess-b", log: null);
        Assert.False(registry.AnyRunning);
    }

    [Fact]
    public void KillAll结束全部会话()
    {
        var registry = new RunningGameRegistry();
        var a = new Session(@"D:\instances\alpha", "sess-a", T0);
        var b = new Session(@"D:\instances\beta", "sess-b", T0.AddSeconds(1));
        registry.TryRegister(a.Game);
        registry.TryRegister(b.Game);

        Assert.Equal(2, registry.KillAll(log: null));
        Assert.Equal(1, a.KillCount);
        Assert.Equal(1, b.KillCount);
        Assert.False(registry.AnyRunning);
    }

    [Fact]
    public void 注销之后同一目录可以重新注册()
    {
        var registry = new RunningGameRegistry();
        var first = new Session(@"D:\instances\alpha", "sess-a", T0);

        Assert.True(registry.TryRegister(first.Game));
        Assert.True(registry.Unregister(first.Game));
        Assert.True(registry.TryRegister(new Session(@"D:\instances\alpha", "sess-b", T0.AddMinutes(1)).Game));
    }

    [Fact]
    public void 注销按引用比较_不会误删同一目录的新会话()
    {
        var registry = new RunningGameRegistry();
        var old = new Session(@"D:\instances\alpha", "sess-old", T0);
        registry.TryRegister(old.Game);
        registry.Unregister(old.Game);

        var fresh = new Session(@"D:\instances\alpha", "sess-new", T0.AddSeconds(5));
        registry.TryRegister(fresh.Game);

        // 老会话收尾晚于新会话注册：按 key 删会把新会话抹掉，变成看不见的幽灵进程。
        Assert.False(registry.Unregister(old.Game));
        Assert.True(registry.IsInstanceRunning(@"D:\instances\alpha"));
        Assert.Equal("sess-new", Assert.Single(registry.Snapshot()).SessionId);
    }

    [Fact]
    public void 占位阶段的会话也算在跑_并发启动会被挡住()
    {
        var registry = new RunningGameRegistry();

        // 不调 MarkStarted：等价于 LaunchAsync 里「已注册、进程还没 Start」的窗口。
        var pending = new RunningGame(
            "sess-pending",
            @"D:\instances\alpha",
            "alpha",
            "1.21.4",
            process: null,
            watcher: new GameWatcher(),
            startedAt: T0);

        Assert.True(registry.TryRegister(pending));
        Assert.True(registry.AnyRunning);
        Assert.False(registry.TryRegister(new Session(@"D:\instances\alpha", "sess-b", T0.AddSeconds(1)).Game));
    }

    [Fact]
    public void 每个会话的日志尾巴互相隔离()
    {
        var alpha = new Session(@"D:\instances\alpha", "sess-a", T0);
        var beta = new Session(@"D:\instances\beta", "sess-b", T0.AddSeconds(1));

        alpha.Game.AppendOutput("[alpha] 正在加载 128 个模组");
        beta.Game.AppendOutput("[beta] 正常启动");

        Assert.Equal("[alpha] 正在加载 128 个模组", Assert.Single(alpha.Game.OutputSnapshot()));
        Assert.Equal("[beta] 正常启动", Assert.Single(beta.Game.OutputSnapshot()));
    }

    [Fact]
    public void 崩溃归因只读本会话的尾巴_不会读到另一个会话的崩溃路径()
    {
        // 这条是整个缺口里最要紧的回归：单开时代「最后 200 行」隐含就是本次的现场，
        // 多开之后如果取错尾巴，A 崩溃会被归因到 B，用户拿着 B 的报告去修 A。
        var alpha = new Session(@"D:\instances\alpha", "sess-a", T0);
        var beta = new Session(@"D:\instances\beta", "sess-b", T0.AddSeconds(1));

        alpha.Game.AppendOutput(
            "#@!@# Game crashed! Crash report saved to: #@!@# D:\\instances\\alpha\\crash-reports\\crash-a.txt");
        beta.Game.AppendOutput("[beta] 一切正常");

        Assert.Equal(
            @"D:\instances\alpha\crash-reports\crash-a.txt",
            CrashAnalyzer.ExtractCrashReportPath(Join(alpha.Game.OutputSnapshot())));

        Assert.Null(CrashAnalyzer.ExtractCrashReportPath(Join(beta.Game.OutputSnapshot())));
    }

    [Fact]
    public void 日志尾巴超过上限时丢弃最旧的()
    {
        var game = new Session(@"D:\instances\alpha", "sess-a", T0).Game;

        for (var i = 0; i < CrashAnalyzer.DefaultTailLines + 10; i++)
        {
            game.AppendOutput($"第 {i} 行");
        }

        var tail = game.OutputSnapshot();

        Assert.Equal(CrashAnalyzer.DefaultTailLines, tail.Length);
        Assert.Equal("第 10 行", tail[0]);
        Assert.Equal($"第 {CrashAnalyzer.DefaultTailLines + 9} 行", tail[^1]);
    }

    [Fact]
    public void 会话退出后不再计入快照()
    {
        var registry = new RunningGameRegistry();
        var session = new Session(@"D:\instances\alpha", "sess-a", T0);
        registry.TryRegister(session.Game);

        Assert.Single(registry.Snapshot());

        session.Exit();

        // 用户在任务管理器里结束进程时启动器收不到通知，只能靠复查 HasExited。
        Assert.Empty(registry.Snapshot());
        Assert.False(registry.IsInstanceRunning(@"D:\instances\alpha"));
    }

    [Fact]
    public void 会话投影不含进程对象但带着停止所需的信息()
    {
        var info = new Session(@"D:\instances\alpha", "sess-a", T0).Game.ToInfo();

        Assert.Equal("sess-a", info.SessionId);
        Assert.Equal(@"D:\instances\alpha", info.InstanceKey);
        Assert.Equal("alpha", info.InstanceName);
        Assert.Equal("1.21.4", info.VersionName);
        Assert.Equal(T0, info.StartedAt);
    }

    private static string Join(string[] lines) => string.Join(Environment.NewLine, lines);

    /// <summary>
    /// 测试用会话：判活与终止都走委托，不真起进程。
    ///
    /// <see cref="RunningGame"/> 的生产路径在委托为 null 时才去读 <c>Process.HasExited</c>，
    /// 所以这里注入委托不会掩盖真实语义；构造后立刻 MarkStarted，模拟「进程已经起来了」。
    /// </summary>
    private sealed class Session
    {
        private int _killCount;
        private bool _alive = true;

        public Session(string instanceKey, string sessionId, DateTimeOffset startedAt)
        {
            Game = new RunningGame(
                sessionId,
                instanceKey,
                instanceName: Path.GetFileName(instanceKey.TrimEnd('\\', '/')),
                versionName: "1.21.4",
                process: null,
                watcher: new GameWatcher(),
                startedAt: startedAt,
                hasExited: () => !_alive,
                kill: () =>
                {
                    _killCount++;
                    _alive = false;
                });

            Game.MarkStarted();
        }

        public RunningGame Game { get; }

        public int KillCount => _killCount;

        /// <summary>模拟用户在任务管理器里结束了进程（启动器收不到任何事件）。</summary>
        public void Exit() => _alive = false;
    }
}
