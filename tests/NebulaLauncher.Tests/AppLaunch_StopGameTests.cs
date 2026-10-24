using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 缺口 G1：「停止游戏」入口，以及 <see cref="LaunchOptions.RelaxForgeChecks"/> 到启动参数的接线。
///
/// 覆盖三层：
/// 1. App 层两个 ViewModel 的停止命令（是否真的调了 <c>KillCurrentGame</c>、杀完是否复核状态、
///    「本会话没有进程」时是否变成空操作）；
/// 2. <see cref="GameLauncher.ComposeJvmArguments"/> 的开关透传（关掉之后三条放宽项不能出现）；
/// 3. <see cref="ProcessWait"/> 的等待语义（超时判断必须以最后一次复核为准）。
///
/// 真实 <see cref="GameLauncher"/> 里「杀进程 → 等退出 → 释放实例锁」的顺序由
/// <c>LaunchAsync</c> 的 finally 保证，无法在单测里跑通完整启动，所以用替身只验证接线。
/// </summary>
public class AppLaunch_StopGameTests
{
    // ==================== RelaxForgeChecks 数据流 ====================

    [Fact]
    public void 启动参数_放宽Forge校验默认开启()
    {
        // 与 AppSettings.RelaxForgeChecks 的默认值必须一致：两边默认值不同时，
        // 用户不碰设置页也会得到「构建器以为关了、设置以为开了」的错位。
        var options = new LaunchOptions
        {
            Account = new Account { Username = "测试" },
            VersionName = "1.21.4",
        };

        Assert.True(options.RelaxForgeChecks);
        Assert.Equal(new AppSettings().RelaxForgeChecks, options.RelaxForgeChecks);
    }

    [Fact]
    public void 组装JVM参数_开关关闭时不再注入放宽项()
    {
        var arguments = GameLauncher.ComposeJvmArguments(
            authlibArguments: null,
            extraJvmArguments: null,
            gcKind: GcKind.Auto,
            javaMajorVersion: 21,
            relaxForgeChecks: false);

        Assert.DoesNotContain(
            arguments,
            a => a.Contains("allowAmbiguousCommands", StringComparison.Ordinal));
        Assert.DoesNotContain(
            arguments,
            a => a.Contains("fml.ignoreInvalidMinecraftCertificates", StringComparison.Ordinal));
        Assert.DoesNotContain(
            arguments,
            a => a.Contains("fml.ignorePatchDiscrepancies", StringComparison.Ordinal));

        // 堆栈诊断项与开关无关，关掉放宽项也必须留着——否则崩溃时只剩一行没有现场的错误。
        Assert.Contains(
            arguments,
            a => a.Contains("OmitStackTraceInFastThrow", StringComparison.Ordinal));
    }

    [Fact]
    public void 组装JVM参数_开关开启时注入三条放宽项()
    {
        var arguments = GameLauncher.ComposeJvmArguments(
            authlibArguments: null,
            extraJvmArguments: null,
            gcKind: GcKind.Auto,
            javaMajorVersion: 21,
            relaxForgeChecks: true);

        Assert.Contains(arguments, a => a.Contains("allowAmbiguousCommands", StringComparison.Ordinal));
        Assert.Contains(arguments, a => a.Contains("fml.ignoreInvalidMinecraftCertificates", StringComparison.Ordinal));
        Assert.Contains(arguments, a => a.Contains("fml.ignorePatchDiscrepancies", StringComparison.Ordinal));
    }

    [Fact]
    public void 组装JVM参数_用户显式设置同名项时不覆盖()
    {
        // 用户写了自己的值就说明他要严格校验，启动器不能悄悄再补一条 =True 把它顶掉。
        var arguments = GameLauncher.ComposeJvmArguments(
            authlibArguments: null,
            extraJvmArguments: "-Dfml.ignorePatchDiscrepancies=False",
            gcKind: GcKind.Auto,
            javaMajorVersion: 21,
            relaxForgeChecks: true);

        Assert.DoesNotContain(
            arguments,
            a => a.Contains("fml.ignorePatchDiscrepancies=True", StringComparison.Ordinal));
        Assert.Contains(arguments, a => a.Contains("fml.ignorePatchDiscrepancies=False", StringComparison.Ordinal));
    }

    // ==================== 首页：停止游戏 ====================

    [Fact]
    public async Task 首页停止游戏_停掉本次会话并复核为已退出()
    {
        var launcher = new FakeGameLauncher().WithGame(@"D:\instances\alpha", "sess-a");
        var viewModel = new HomeViewModel(gameLauncher: launcher);
        viewModel.TrackLaunchInstance(@"D:\instances\alpha");

        await viewModel.StopGameCommand.ExecuteAsync(null);

        Assert.Equal("sess-a", Assert.Single(launcher.KilledSessions));
        Assert.False(viewModel.IsLaunchSessionRunning);
        Assert.Equal("已停止游戏进程。", viewModel.LaunchDetail);
        Assert.Equal("游戏已停止", viewModel.LaunchPhaseText);
        Assert.False(viewModel.StopGameCommand.CanExecute(null));
    }

    [Fact]
    public async Task 首页停止游戏_进程已经退出时不重复杀()
    {
        // 界面上的「运行中」可能来自上一帧：进程早就自己退了，这里不该再调 Kill。
        var launcher = new FakeGameLauncher();
        var viewModel = new HomeViewModel(gameLauncher: launcher) { IsLaunchSessionRunning = true };
        viewModel.TrackLaunchInstance(@"D:\instances\alpha");

        await viewModel.StopGameCommand.ExecuteAsync(null);

        Assert.Empty(launcher.KilledSessions);
        Assert.False(viewModel.IsLaunchSessionRunning);
    }

    [Fact]
    public async Task 首页停止游戏_杀进程抛错时给出失败文案并保留运行状态()
    {
        var launcher = new FakeGameLauncher { KillThrows = true }.WithGame(@"D:\instances\alpha", "sess-a");
        var viewModel = new HomeViewModel(gameLauncher: launcher);
        viewModel.TrackLaunchInstance(@"D:\instances\alpha");

        await viewModel.StopGameCommand.ExecuteAsync(null);

        Assert.Equal(1, launcher.KillCount);

        // 杀失败就不能把徽标刷成「已停止」——那是比不刷新更坏的一种误导。
        Assert.True(viewModel.IsLaunchSessionRunning);
        Assert.Contains("停止游戏失败", viewModel.LaunchDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 首页停止游戏_启动服务缺失时不抛异常()
    {
        var viewModel = new HomeViewModel { IsLaunchSessionRunning = true };

        await viewModel.StopGameCommand.ExecuteAsync(null);

        // 没有启动器就什么都不做：命令存在是为了让按钮有个落点，不是用来报错的。
        Assert.True(viewModel.IsLaunchSessionRunning);
    }

    [Fact]
    public async Task 首页停止游戏_多开时只停本次会话并提示另有实例在跑()
    {
        // 两个实例都在跑：首页只负责「我这次启动」那一个，另一个交给版本页。
        var launcher = new FakeGameLauncher()
            .WithGame(@"D:\instances\alpha", "sess-a", DateTimeOffset.Now.AddMinutes(-5))
            .WithGame(@"D:\instances\beta", "sess-b", DateTimeOffset.Now);

        var viewModel = new HomeViewModel(gameLauncher: launcher);
        viewModel.TrackLaunchInstance(@"D:\instances\beta");

        await viewModel.StopGameCommand.ExecuteAsync(null);

        // 只停自己那个，不能顺手把 alpha 一起停掉。
        Assert.Equal("sess-b", Assert.Single(launcher.KilledSessions));
        Assert.True(launcher.IsInstanceRunning(@"D:\instances\alpha"));

        // 本次会话已经结束，但首页必须说出来：还有别的东西在跑。
        Assert.False(viewModel.IsLaunchSessionRunning);
        Assert.True(viewModel.HasOtherRunningGames);
        Assert.Equal("另有 1 个实例在运行", viewModel.OtherRunningText);
    }

    [Fact]
    public async Task 首页停止游戏_没有记录过目录时不认领任何会话()
    {
        // 本页从未启动过东西（例如刚打开就在版本页起了游戏）：停止命令不能去停别人的实例。
        var launcher = new FakeGameLauncher().WithGame(@"D:\instances\alpha", "sess-a");
        var viewModel = new HomeViewModel(gameLauncher: launcher) { IsLaunchSessionRunning = true };

        await viewModel.StopGameCommand.ExecuteAsync(null);

        Assert.Empty(launcher.KilledSessions);
        Assert.False(viewModel.IsLaunchSessionRunning);
    }

    [Fact]
    public void 首页取消按钮_只在进程未起来时可见()
    {
        var viewModel = new HomeViewModel { IsLaunching = true, IsLaunchSessionRunning = false };
        Assert.True(viewModel.ShowCancelLaunch);

        // 进程起来之后「取消」会释放实例锁却留下游戏进程，必须让位给「停止游戏」。
        viewModel.IsLaunchSessionRunning = true;
        Assert.False(viewModel.ShowCancelLaunch);

        viewModel.IsLaunchSessionRunning = false;
        viewModel.IsLaunching = false;
        Assert.False(viewModel.ShowCancelLaunch);
    }

    // ==================== 版本页：多开准入 ====================

    [Fact]
    public void 版本页准入_两个不同实例都放行()
    {
        // 多开的核心：准入按目录判断，不按「页面有没有启动在跑」判断。
        var viewModel = new VersionsViewModel(gameLauncher: new FakeGameLauncher());

        Assert.True(viewModel.CanStartInstance(@"D:\instances\alpha"));
        Assert.True(viewModel.CanStartInstance(@"D:\instances\beta"));
    }

    [Fact]
    public void 版本页准入_同一个实例不能同时启动两次()
    {
        var launcher = new FakeGameLauncher().WithGame(@"D:\instances\alpha", "sess-a");
        var viewModel = new VersionsViewModel(gameLauncher: launcher);

        // 已在跑的那一个不放行，另一个照旧放行。
        Assert.False(viewModel.CanStartInstance(@"D:\instances\alpha"));
        Assert.True(viewModel.CanStartInstance(@"D:\instances\beta"));

        // 大小写与尾部分隔符不能绕过判定：它们是同一个目录。
        Assert.False(viewModel.CanStartInstance(@"D:\instances\alpha\"));
        Assert.False(viewModel.CanStartInstance(@"D:\INSTANCES\ALPHA"));
    }

    [Fact]
    public void 版本页准入_没有启动器或目录时空操作()
    {
        Assert.False(new VersionsViewModel().CanStartInstance(@"D:\instances\alpha"));
        Assert.False(new VersionsViewModel(gameLauncher: new FakeGameLauncher()).CanStartInstance(null));
        Assert.False(new VersionsViewModel(gameLauncher: new FakeGameLauncher()).CanStartInstance("  "));
    }

    // ==================== 版本页：按实例停止 ====================

    /// <summary>
    /// 造一行实例卡片。只给停止命令需要的字段：行的其余部分（目录芯片、备注）由
    /// <c>VersionsViewModel</c> 在真实加载时填充，这里不需要，也不该为了一条用例造服务替身。
    /// </summary>
    private static InstanceRow NewInstanceRow(string instanceId, string? sessionId)
    {
        var row = new InstanceRow(new InstanceInfo
        {
            Id = instanceId,
            Name = instanceId,
            VersionName = "1.21.4",
        });

        row.RunningSessionId = sessionId;
        row.IsSessionRunning = sessionId is not null;

        return row;
    }

    [Fact]
    public async Task 版本页停止游戏_按会话停掉目标实例并刷新状态文案()
    {
        var launcher = new FakeGameLauncher()
            .WithGame(@"D:\instances\alpha", "sess-a")
            .WithGame(@"D:\instances\beta", "sess-b");

        var viewModel = new VersionsViewModel(gameLauncher: launcher);
        var row = NewInstanceRow("alpha", "sess-a");

        await viewModel.StopGameCommand.ExecuteAsync(row);

        // 只停 alpha：多开时按下 beta 那个按钮不能把 alpha 一起带走。
        Assert.Equal("sess-a", Assert.Single(launcher.KilledSessions));
        Assert.True(launcher.IsInstanceRunning(@"D:\instances\beta"));
        Assert.Equal("已停止游戏进程。", viewModel.StatusText);
        Assert.False(row.IsSessionRunning);
    }

    [Fact]
    public async Task 版本页停止游戏_会话已经不在时提示而不是抛错()
    {
        var launcher = new FakeGameLauncher();
        var viewModel = new VersionsViewModel(gameLauncher: launcher);
        var row = NewInstanceRow("alpha", sessionId: null);

        await viewModel.StopGameCommand.ExecuteAsync(row);

        Assert.Empty(launcher.KilledSessions);
        Assert.Equal("游戏进程已经不在了。", viewModel.StatusText);
        Assert.False(row.IsSessionRunning);
    }

    [Fact]
    public void 版本页停止按钮_只对本会话在跑的那一行可用()
    {
        var launcher = new FakeGameLauncher().WithGame(@"D:\instances\alpha", "sess-a");
        var viewModel = new VersionsViewModel(gameLauncher: launcher);

        var running = NewInstanceRow("alpha", "sess-a");
        var idle = NewInstanceRow("beta", sessionId: null);

        Assert.True(viewModel.StopGameCommand.CanExecute(running));

        // 卡片上的「运行中」可能来自磁盘锁（别的启动器实例持的），那种进程本会话停不掉，
        // 命令不能对它可用——否则点了也只会得到「进程已经不在了」。
        Assert.False(viewModel.StopGameCommand.CanExecute(idle));
        Assert.False(viewModel.StopGameCommand.CanExecute(null));
    }

    [Fact]
    public void 版本页停止按钮_没有启动器时不可用()
    {
        var viewModel = new VersionsViewModel();
        var row = NewInstanceRow("alpha", "sess-a");

        Assert.False(viewModel.StopGameCommand.CanExecute(row));
    }

    // ==================== 等待工具的语义 ====================

    [Fact]
    public async Task 等待进程退出_条件立即成立时直接返回()
    {
        var result = await ProcessWait.UntilAsync(() => true, TimeSpan.FromSeconds(1));

        Assert.True(result);
    }

    [Fact]
    public async Task 等待进程退出_超时返回最后一次复核结果()
    {
        var probes = 0;

        var result = await ProcessWait.UntilAsync(
            () =>
            {
                probes++;
                // 条件在最后一次复核时才成立：不能因为循环里读过旧值就判超时。
                return probes >= 4;
            },
            TimeSpan.FromMilliseconds(120),
            pollIntervalMs: 10);

        Assert.True(result);
        Assert.True(probes >= 4);
    }

    [Fact]
    public async Task 等待进程退出_始终不成立时返回否()
    {
        var result = await ProcessWait.UntilAsync(
            () => false,
            TimeSpan.FromMilliseconds(80),
            pollIntervalMs: 10);

        Assert.False(result);
    }

    /// <summary>
    /// <see cref="IGameLauncher"/> 的替身：管住「在跑会话表」与「哪一个被杀了」。
    ///
    /// 多开之后停止是按会话的，所以替身必须能同时挂多个会话；只保留一个布尔
    /// 会让「停 A 不连带停 B」这条断言无处可写。
    ///
    /// 不实现按进程名扫描的能力是刻意的：真实启动器只能杀自己持有句柄的进程，
    /// 替身如果多了这种能力，测试就会替一个不存在的行为背书。
    /// </summary>
    private sealed class FakeGameLauncher : IGameLauncher
    {
        private readonly List<RunningGameInfo> _running = new();

        /// <summary>被要求终止的会话标识，按发生顺序。</summary>
        public List<string> KilledSessions { get; } = new();

        public int KillCount { get; private set; }

        /// <summary>杀进程时抛异常（模拟拒绝访问）。</summary>
        public bool KillThrows { get; init; }

        /// <summary>「任一在跑」的语义由会话表推导，不再单独维护一个布尔。</summary>
        public bool IsGameRunning => _running.Count > 0;

        public IReadOnlyList<RunningGameInfo> RunningGames => _running;

        /// <summary>挂一个在跑会话。</summary>
        public FakeGameLauncher WithGame(string instanceKey, string? sessionId = null, DateTimeOffset? startedAt = null)
        {
            _running.Add(new RunningGameInfo(
                sessionId ?? $"sess-{_running.Count}",
                RunningGameInfo.KeyFor(instanceKey),
                Path.GetFileName(instanceKey.TrimEnd('\\', '/')),
                "1.21.4",
                startedAt ?? DateTimeOffset.Now.AddSeconds(_running.Count)));

            return this;
        }

        public bool IsInstanceRunning(string instanceKey)
        {
            var key = RunningGameInfo.KeyFor(instanceKey);

            return _running.Any(game => string.Equals(game.InstanceKey, key, StringComparison.OrdinalIgnoreCase));
        }

        public bool KillGame(string sessionId)
        {
            var target = _running.FirstOrDefault(game =>
                string.Equals(game.SessionId, sessionId, StringComparison.Ordinal));

            if (target is null)
            {
                return false;
            }

            KillCount++;
            KilledSessions.Add(sessionId);

            if (KillThrows)
            {
                // 杀失败：会话必须留在表里，否则界面会把「没杀掉」显示成「已停止」。
                throw new InvalidOperationException("拒绝访问");
            }

            _running.Remove(target);
            return true;
        }

        public void KillCurrentGame()
        {
            KillCount += _running.Count;

            if (KillThrows)
            {
                throw new InvalidOperationException("拒绝访问");
            }

            _running.Clear();
        }

        public event EventHandler<GameOutputLine>? OutputReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionExitedEventArgs>? SessionExited
        {
            add { }
            remove { }
        }

        public Task<string> BuildCommandLineAsync(
            LaunchOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task<LaunchResult> LaunchAsync(
            LaunchOptions options,
            IProgress<LaunchProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LaunchResult(false, -1, TimeSpan.Zero, "测试替身不启动进程"));

        public Task<string?> ExportLaunchScriptAsync(
            LaunchOptions options,
            string targetPath,
            bool maskAccessToken = true,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}