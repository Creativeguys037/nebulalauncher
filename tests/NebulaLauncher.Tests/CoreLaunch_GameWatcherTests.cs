using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 游戏进程监视（PCL2 <c>ModWatcher</c> 的 8.3 / 8.4 两节）。
///
/// 用例分三层：
/// <list type="bullet">
/// <item><see cref="GameWatcher.Consume"/>：5 级日志里程碑，纯函数；</item>
/// <item><see cref="GameWatcher.Classify"/>：崩溃 / 关闭关键词，纯函数；</item>
/// <item><see cref="GameWatcher.Feed"/> 与事件：线程安全与粘性标志。</item>
/// </list>
/// 样本行照抄真实 Minecraft 日志的格式（含 log4j 的 <c>[线程名/级别]:</c> 前缀），
/// 这样里程碑匹配一旦被写成「必须精确相等」就会立刻暴露。
/// </summary>
public class CoreLaunch_GameWatcherTests
{
    /// <summary>PCL2 的 5 行里程碑样本：逐行喂进去应当一路推进到 TexturesLoaded。</summary>
    private static readonly (string Line, GameStartupStage Expected)[] MilestoneSample =
    [
        ("[12:00:00] [main/INFO]: Setting user: Steve", GameStartupStage.UserSet),
        ("[12:00:01] [main/INFO]: LWJGL Version: 3.3.2", GameStartupStage.LwjglReady),
        ("[12:00:02] [Sound engine/INFO]: OpenAL initialized on device OpenAL Soft", GameStartupStage.AudioReady),
        ("[12:00:03] [Sound engine/INFO]: Starting up SoundSystem...", GameStartupStage.AudioReady),
        ("[12:00:04] [Render thread/INFO]: Created: 1024x1024x4 minecraft:textures/atlas/blocks.png-atlas",
            GameStartupStage.TexturesLoaded),
    ];

    // ==================== 里程碑 ====================

    [Fact]
    public void Feed_FiveMilestoneLines_ReachesTexturesLoaded()
    {
        var watcher = new GameWatcher();

        foreach (var (line, expected) in MilestoneSample)
        {
            watcher.Feed(line);
            Assert.Equal(expected, watcher.Phase);
        }

        Assert.Equal(GameStartupStage.TexturesLoaded, watcher.Phase);
    }

    [Fact]
    public void Consume_UnrelatedLine_AtLeastLaunched()
    {
        Assert.Equal(GameStartupStage.Launched, GameWatcher.Consume("some random output", GameStartupStage.None));
    }

    [Fact]
    public void Consume_LwjglVersionIsCaseInsensitive()
    {
        // 不同版本打的大小写不一样（"LWJGL Version" / "lwjgl version"），必须忽略大小写。
        Assert.Equal(
            GameStartupStage.LwjglReady,
            GameWatcher.Consume("[main/INFO]: lwjgl version 3.3.1", GameStartupStage.UserSet));
    }

    [Fact]
    public void Consume_AnimationInfo_CountsAsTexturesLoaded()
    {
        Assert.Equal(
            GameStartupStage.TexturesLoaded,
            GameWatcher.Consume("[Render thread/INFO]: Found animation info for: textures/block/water", GameStartupStage.AudioReady));
    }

    [Fact]
    public void Consume_NullOrBlankLine_KeepsStage()
    {
        Assert.Equal(GameStartupStage.AudioReady, GameWatcher.Consume(null, GameStartupStage.AudioReady));
        Assert.Equal(GameStartupStage.AudioReady, GameWatcher.Consume("   ", GameStartupStage.AudioReady));
    }

    [Fact]
    public void Consume_NeverRegresses()
    {
        // stderr 与 stdout 交错到达时，不能因为后到的是早期阶段的行就把进度条拉回去。
        Assert.Equal(
            GameStartupStage.TexturesLoaded,
            GameWatcher.Consume("[main/INFO]: Setting user: Steve", GameStartupStage.TexturesLoaded));
    }

    // ==================== 崩溃 / 关闭关键词 ====================

    [Fact]
    public void Classify_CrashReportSaved_IsCrashed()
    {
        Assert.Equal(
            GameLogSignal.Crashed,
            GameWatcher.Classify("#@!@# Game crashed! Crash report saved to: #@!@# D:\\mc\\crash-reports\\crash-2024.txt"));
    }

    [Fact]
    public void Classify_CrashReportSavedAlternateWording_IsCrashed()
    {
        Assert.Equal(
            GameLogSignal.Crashed,
            GameWatcher.Classify("[main/ERROR]: This crash report has been saved to: D:\\mc\\crash-reports\\a.txt"));
    }

    [Fact]
    public void Classify_CouldNotSaveCrashReport_IsCrashed()
    {
        Assert.Equal(
            GameLogSignal.Crashed,
            GameWatcher.Classify("[main/ERROR]: Could not save crash report to D:\\mc\\crash-reports\\a.txt"));
    }

    [Fact]
    public void Classify_UnableToLaunch_IsCrashed()
    {
        Assert.Equal(
            GameLogSignal.Crashed,
            GameWatcher.Classify("[12:00:00] [Render thread/ERROR]: Unable to launch"));
    }

    [Fact]
    public void Classify_ExceptionThrown_IsCrashed()
    {
        Assert.Equal(
            GameLogSignal.Crashed,
            GameWatcher.Classify("[main/FATAL]: An exception was thrown, the game will display an error screen and halt."));
    }

    [Fact]
    public void Classify_SomeoneIsClosingMe_IsClosing()
    {
        Assert.Equal(GameLogSignal.Closing, GameWatcher.Classify("[main/INFO]: Someone is closing me!"));
    }

    [Fact]
    public void Classify_RestartingMinecraft_IsClosing()
    {
        Assert.Equal(
            GameLogSignal.Closing,
            GameWatcher.Classify("[main/INFO]: Restarting Minecraft with command: java -Xmx2G"));
    }

    [Fact]
    public void Classify_ChatLineWithCrashKeyword_IsIgnored()
    {
        // 玩家可以在聊天栏里打出任何字符串，聊天行会被原样回显到 stdout：
        // 不排除 [CHAT] 就会导致启动器对着一条聊天消息做崩溃分析。
        Assert.Equal(
            GameLogSignal.None,
            GameWatcher.Classify("[Render thread/INFO]: [CHAT] <Steve> Crash report saved to D:\\x"));
    }

    [Fact]
    public void Feed_ChatLineClosingKeyword_DoesNotMarkClosing()
    {
        var watcher = new GameWatcher();

        var signal = watcher.Feed("[Render thread/INFO]: [CHAT] <Steve> Someone is closing me!");

        Assert.Equal(GameLogSignal.None, signal);
        Assert.False(watcher.CloseRequested);
        Assert.False(watcher.CrashDetected);
    }

    [Fact]
    public void Classify_OrdinaryLine_IsNone()
    {
        Assert.Equal(GameLogSignal.None, GameWatcher.Classify("[main/INFO]: Loading properties"));
        Assert.Equal(GameLogSignal.None, GameWatcher.Classify(null));
    }

    // ==================== 状态与事件 ====================

    [Fact]
    public void AdvanceTo_HigherStage_AdvancesAndRaisesEvent()
    {
        var watcher = new GameWatcher(GameStartupStage.Launched);
        var raised = new List<GameStartupStage>();
        watcher.StageAdvanced += (_, stage) => raised.Add(stage);

        Assert.True(watcher.AdvanceTo(GameStartupStage.MainMenu));
        Assert.Equal(GameStartupStage.MainMenu, watcher.Phase);
        Assert.Equal([GameStartupStage.MainMenu], raised);
    }

    [Fact]
    public void AdvanceTo_LowerOrEqualStage_DoesNothing()
    {
        var watcher = new GameWatcher(GameStartupStage.AudioReady);
        var count = 0;
        watcher.StageAdvanced += (_, _) => count++;

        Assert.False(watcher.AdvanceTo(GameStartupStage.LwjglReady));
        Assert.False(watcher.AdvanceTo(GameStartupStage.AudioReady));
        Assert.Equal(GameStartupStage.AudioReady, watcher.Phase);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Feed_RaisesStageAdvancedOnlyWhenStageChanges()
    {
        var watcher = new GameWatcher();
        var raised = new List<GameStartupStage>();
        watcher.StageAdvanced += (_, stage) => raised.Add(stage);

        watcher.Feed("[main/INFO]: Loading properties");
        watcher.Feed("[main/INFO]: another line in the same stage");
        watcher.Feed("[main/INFO]: Setting user: Steve");

        Assert.Equal([GameStartupStage.Launched, GameStartupStage.UserSet], raised);
    }

    [Fact]
    public void Feed_CrashSignal_RaisedOnce()
    {
        var watcher = new GameWatcher();
        var lines = new List<string>();
        watcher.CrashSignal += (_, line) => lines.Add(line);

        watcher.Feed("[main/ERROR]: Crash report saved to D:\\a.txt");
        watcher.Feed("[main/ERROR]: Crash report saved to D:\\b.txt");

        var line = Assert.Single(lines);
        Assert.Contains("Crash report saved to", line, StringComparison.Ordinal);
        Assert.True(watcher.CrashDetected);
    }

    [Fact]
    public void Feed_NullLine_IsNoOp()
    {
        var watcher = new GameWatcher(GameStartupStage.UserSet);

        Assert.Equal(GameLogSignal.None, watcher.Feed(null));
        Assert.Equal(GameStartupStage.UserSet, watcher.Phase);
    }

    [Fact]
    public async Task Feed_ConcurrentLines_KeepsStageMonotonic()
    {
        var watcher = new GameWatcher();
        var advanced = 0;
        watcher.StageAdvanced += (_, _) => Interlocked.Increment(ref advanced);

        var workers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                // 四个线程同时喂不同阶段的行，最终阶段必须是最高那一级。
                watcher.Feed(index switch
                {
                    0 => "[main/INFO]: Setting user: Steve",
                    1 => "[main/INFO]: LWJGL Version: 3.3.2",
                    2 => "[main/INFO]: OpenAL initialized",
                    _ => "[Render thread/INFO]: Created: 1024x1024x4 minecraft:textures/atlas/blocks.png-atlas",
                });
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(GameStartupStage.TexturesLoaded, watcher.Phase);

        // 阶段只会前进，事件次数不超过阶段总数（Launched → TexturesLoaded 共 5 次）。
        Assert.InRange(advanced, 1, 5);
        Assert.False(watcher.CrashDetected);
    }

    // ==================== 退出判定 ====================

    [Theory]
    // 没走到可玩阶段就退出：即使 exitCode 为 0 也判崩溃。
    [InlineData(0, GameStartupStage.LwjglReady, 2024, GameLogSignal.None, GameExitVerdict.Crashed)]
    [InlineData(0, GameStartupStage.None, 2024, GameLogSignal.None, GameExitVerdict.Crashed)]
    // 进过游戏、exitCode 为 0：正常退出。
    [InlineData(0, GameStartupStage.TexturesLoaded, 2024, GameLogSignal.None, GameExitVerdict.Ended)]
    [InlineData(0, GameStartupStage.MainMenu, 2024, GameLogSignal.None, GameExitVerdict.Ended)]
    // 进过游戏、exitCode 非 0、现代版本：崩溃。
    [InlineData(1, GameStartupStage.MainMenu, 2024, GameLogSignal.None, GameExitVerdict.Crashed)]
    // 2012 年前的老版本非零退出码是它本来的行为。
    [InlineData(1, GameStartupStage.MainMenu, 2011, GameLogSignal.None, GameExitVerdict.Ended)]
    // 发行年未知时不据此判崩溃。
    [InlineData(1, GameStartupStage.TexturesLoaded, 0, GameLogSignal.None, GameExitVerdict.Ended)]
    // 日志关键词优先于退出码与阶段：崩溃关键词压过一切，关闭关键词压过退出码。
    [InlineData(0, GameStartupStage.MainMenu, 2024, GameLogSignal.Crashed, GameExitVerdict.Crashed)]
    [InlineData(1, GameStartupStage.LwjglReady, 2024, GameLogSignal.Closing, GameExitVerdict.Ended)]
    [InlineData(0, GameStartupStage.None, 2024, GameLogSignal.Closing, GameExitVerdict.Ended)]
    public void JudgeExit_Cases(
        int exitCode,
        GameStartupStage stage,
        int releaseYear,
        GameLogSignal signal,
        GameExitVerdict expected)
    {
        Assert.Equal(expected, GameWatcher.JudgeExit(exitCode, stage, releaseYear, signal));
    }

    [Fact]
    public void JudgeExit_PlayableStageIsTexturesLoaded()
    {
        // 门槛常量与 PCL2 的 LogProgress == 5 对齐；MainMenu 只在枚举到窗口时才到达。
        Assert.Equal(GameStartupStage.TexturesLoaded, GameWatcher.PlayableStage);
        Assert.Equal(2012, GameWatcher.CrashReleaseYear);
    }

    // ==================== 文案 ====================

    [Fact]
    public void DescribeStage_AllStages_ReturnNonEmptyText()
    {
        foreach (var stage in Enum.GetValues<GameStartupStage>())
        {
            var text = GameWatcher.DescribeStage(stage);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{stage} 没有文案");
        }
    }
}
