using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 退出判定与「取消等待」的边界：这两条都不需要真实 JVM，但错了会让用户看到相反的结论
/// （崩溃被报成正常 / 正常被报成崩溃），或者更糟——游戏还在跑而实例锁已经放了。
/// </summary>
public class CoreLaunch_ExitVerdictTests
{
    // ==================== 取消等待是否移交后台 ====================

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]   // 还没起进程：取消就是取消，锁要放
    [InlineData(true, true, true, false)]     // 进程已退出：走正常收尾，不需要移交
    [InlineData(true, false, false, false)]   // 没登记会话：没有可移交的对象
    public void ShouldDetachOnCancel_RequiresStartedAliveAndRegistered(
        bool processStarted, bool processExited, bool hasSession, bool expected)
    {
        Assert.Equal(expected, GameLauncher.ShouldDetachOnCancel(processStarted, processExited, hasSession));
    }

    [Fact]
    public void DetachedResult_IsStartedButNotFailed()
    {
        // 移交后台的结果必须同时满足：Started=true（游戏真的起来了）、Detached=true（别再按失败渲染）、
        // Verdict=null（还没退出，谈不上崩溃或正常）。
        var result = new LaunchResult(true, -1, TimeSpan.FromSeconds(3))
        {
            Detached = true,
        };

        Assert.True(result.Started);
        Assert.True(result.Detached);
        Assert.Null(result.Verdict);
        Assert.Null(result.FailureReason);
    }

    // ==================== BuildExitResult 带出最终判定 ====================

    [Theory]
    [InlineData(GameExitVerdict.Crashed, 0)]
    [InlineData(GameExitVerdict.Crashed, 1)]
    [InlineData(GameExitVerdict.Ended, 0)]
    public void BuildExitResult_CarriesVerdict(GameExitVerdict verdict, int exitCode)
    {
        var result = GameLauncher.BuildExitResult(
            exitCode,
            TimeSpan.FromSeconds(12),
            crashAnalysis: null,
            crashReport: null,
            hsErrLogs: Array.Empty<string>(),
            verdict: verdict);

        Assert.True(result.Started);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(verdict, result.Verdict);
        Assert.False(result.Detached);
    }

    [Fact]
    public void BuildExitResult_CrashWithZeroExitCodeStaysCrashed()
    {
        // 加载到一半被外部结束：exit 0，但阶段没到可玩 → Core 判崩溃。
        // UI 只看退出码的话会显示「游戏已正常退出」，这一条钉住 verdict 必须原样带出。
        var verdict = GameWatcher.JudgeExit(
            exitCode: 0,
            stage: GameStartupStage.UserSet,
            releaseYear: 2024,
            signal: GameLogSignal.None);

        Assert.Equal(GameExitVerdict.Crashed, verdict);

        var result = GameLauncher.BuildExitResult(
            0,
            TimeSpan.FromSeconds(5),
            "崩溃结论",
            new CrashReport(true, "崩溃结论", Array.Empty<CrashSuggestion>()),
            ["hs_err"],
            verdict);

        Assert.Equal(GameExitVerdict.Crashed, result.Verdict);
        Assert.Equal("崩溃结论", result.CrashAnalysis);
        Assert.Single(result.HsErrLogs);
    }

    [Fact]
    public void BuildExitResult_OldVersionNonZeroExitEndsNormally()
    {
        // 2012 年前的老版本正常关闭也可能返回非零码：verdict 为 Ended 时 UI 不该报崩溃。
        var verdict = GameWatcher.JudgeExit(
            exitCode: 1,
            stage: GameStartupStage.TexturesLoaded,
            releaseYear: 2011,
            signal: GameLogSignal.None);

        Assert.Equal(GameExitVerdict.Ended, verdict);

        var result = GameLauncher.BuildExitResult(
            1,
            TimeSpan.FromSeconds(30),
            null,
            null,
            Array.Empty<string>(),
            verdict);

        Assert.Equal(GameExitVerdict.Ended, result.Verdict);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void BuildExitResult_WithoutVerdict_LeavesNullForLegacyFallback()
    {
        // 旧调用点（第三方插件 / 早期代码）不传 verdict：必须保持 null，
        // 调用方才知道该退回「看退出码」而不是拿默认值去判。
        var result = GameLauncher.BuildExitResult(
            0,
            TimeSpan.Zero,
            null,
            null,
            Array.Empty<string>());

        Assert.Null(result.Verdict);
    }
}
