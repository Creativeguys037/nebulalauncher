using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>游戏输出行给出的退出/崩溃信号。</summary>
public enum GameLogSignal
{
    /// <summary>与退出无关的普通日志行。</summary>
    None = 0,

    /// <summary>游戏主动关闭或重启，属于正常结束（PCL2 <c>MinecraftState.Ended</c>）。</summary>
    Closing = 1,

    /// <summary>日志里出现了崩溃特征行。</summary>
    Crashed = 2,
}

/// <summary>进程退出后的判定结论。</summary>
public enum GameExitVerdict
{
    /// <summary>正常退出（含用户主动关闭、重启）。</summary>
    Ended = 0,

    /// <summary>崩溃：没加载完就退出，或非零退出码 + 现代版本。</summary>
    Crashed = 1,
}

/// <summary>
/// 游戏进程监视：把 stdout/stderr 的每一行翻译成「启动阶段」与「崩溃/关闭信号」。
///
/// 为什么需要它：只看进程是否存活判断不了「进没进主菜单」。用户在加载中就关掉、显卡驱动导致
/// 窗口创建失败、模组让客户端崩在早期，这三种情况进程都会正常退出（甚至 exitCode = 0），
/// 只按退出码判断会把它们全当成「游戏已正常退出」，崩溃分析因此永远不会启动。
///
/// 判定分两层：
/// <list type="number">
/// <item><see cref="Consume"/>：纯函数，按 PCL2 <c>ModWatcher.GameLog</c> 的 5 级里程碑推进阶段；</item>
/// <item><see cref="Classify"/>：纯函数，按关键词判崩溃/正常关闭，先排除 <c>[CHAT]</c> 行——
///       聊天内容会原样复读玩家打出的字，一句「Crash report saved to」就能让启动器误报崩溃。</item>
/// </list>
/// 实例部分（<see cref="Feed"/> / <see cref="Phase"/> / 事件）负责线程安全：日志行来自两条
/// 独立的读取线程（stdout 与 stderr），阶段只能单调前进，事件在锁外触发。
/// </summary>
public sealed class GameWatcher
{
    /// <summary>PCL2 的「版本发行年」分界：2012 年之前的版本非零退出码不判崩溃。</summary>
    public const int CrashReleaseYear = 2012;

    /// <summary>聊天行的前缀。命中它的行不参与崩溃判定。</summary>
    public const string ChatMarker = "[CHAT]";

    /// <summary>进入「可玩」阶段的门槛：材质加载完成，与 PCL2 的 <c>LogProgress == 5</c> 对齐。</summary>
    public const GameStartupStage PlayableStage = GameStartupStage.TexturesLoaded;

    private readonly object _sync = new();

    private GameStartupStage _phase;
    private bool _crashDetected;
    private bool _closeRequested;

    /// <summary>创建一个监视器。</summary>
    /// <param name="initialPhase">初始阶段；进程刚拉起时传 <see cref="GameStartupStage.Launched"/>。</param>
    public GameWatcher(GameStartupStage initialPhase = GameStartupStage.None)
    {
        _phase = initialPhase;
    }

    /// <summary>当前阶段。只增不减，读取线程安全。</summary>
    public GameStartupStage Phase
    {
        get
        {
            lock (_sync)
            {
                return _phase;
            }
        }
    }

    /// <summary>是否已经看到过崩溃特征行。粘性标志，一旦为 true 不再回落。</summary>
    public bool CrashDetected
    {
        get
        {
            lock (_sync)
            {
                return _crashDetected;
            }
        }
    }

    /// <summary>是否已经看到过「主动关闭 / 重启」行。粘性标志。</summary>
    public bool CloseRequested
    {
        get
        {
            lock (_sync)
            {
                return _closeRequested;
            }
        }
    }

    /// <summary>阶段前进时触发。参数是新阶段；只在真正前进时触发，不会重复推送同一阶段。</summary>
    public event EventHandler<GameStartupStage>? StageAdvanced;

    /// <summary>首次看到崩溃特征行时触发。参数是触发判定的那一行原文，供日志留证。</summary>
    public event EventHandler<string>? CrashSignal;

    /// <summary>
    /// 线程安全地消化一行输出：推进阶段、记录崩溃/关闭信号、触发事件。
    /// 内部吞掉所有异常（订阅方抛异常不能拖垮进程的输出读取线程）。
    /// </summary>
    /// <param name="line">一行游戏输出；null / 空白行忽略。</param>
    /// <returns>这一行给出的信号。</returns>
    public GameLogSignal Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return GameLogSignal.None;
        }

        var signal = Classify(line);
        GameStartupStage? advancedTo = null;
        var raiseCrash = false;

        lock (_sync)
        {
            var next = Consume(line, _phase);
            if (next > _phase)
            {
                _phase = next;
                advancedTo = next;
            }

            if (signal == GameLogSignal.Crashed && !_crashDetected)
            {
                _crashDetected = true;
                raiseCrash = true;
            }
            else if (signal == GameLogSignal.Closing)
            {
                _closeRequested = true;
            }
        }

        if (advancedTo is not null)
        {
            RaiseStageAdvanced(advancedTo.Value);
        }

        if (raiseCrash)
        {
            RaiseCrashSignal(line!);
        }

        return signal;
    }

    /// <summary>
    /// 外部里程碑（例如枚举到游戏窗口）把阶段直接推到某一级。
    /// </summary>
    /// <param name="stage">目标阶段。</param>
    /// <returns>是否真的前进。</returns>
    public bool AdvanceTo(GameStartupStage stage)
    {
        lock (_sync)
        {
            if (stage <= _phase)
            {
                return false;
            }

            _phase = stage;
        }

        RaiseStageAdvanced(stage);
        return true;
    }

    /// <summary>
    /// 纯函数：按一行日志推进启动阶段（PCL2 <c>ModWatcher.GameLog</c> 的 5 级里程碑）。
    ///
    /// 阶段只增不减：同一行可能同时命中多级特征（少见），取命中的最高一级；
    /// 已经越过的级别不会被拖回去，否则 stderr 与 stdout 交错到达时进度条会来回跳。
    /// </summary>
    /// <param name="line">一行游戏输出。</param>
    /// <param name="current">当前阶段。</param>
    /// <returns>推进后的阶段；无变化时返回 <paramref name="current"/>。</returns>
    public static GameStartupStage Consume(string? line, GameStartupStage current)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return current;
        }

        var next = current;

        // 任意一行都说明进程真的跑起来了（PCL2 的 LogProgress 1）。
        if (next < GameStartupStage.Launched)
        {
            next = GameStartupStage.Launched;
        }

        if (next < GameStartupStage.UserSet && line.Contains("Setting user:", StringComparison.Ordinal))
        {
            next = GameStartupStage.UserSet;
        }

        if (next < GameStartupStage.LwjglReady && ContainsIgnoreCase(line, "lwjgl version"))
        {
            next = GameStartupStage.LwjglReady;
        }

        if (next < GameStartupStage.AudioReady &&
            (line.Contains("OpenAL initialized", StringComparison.Ordinal) ||
             line.Contains("Starting up SoundSystem", StringComparison.Ordinal)))
        {
            next = GameStartupStage.AudioReady;
        }

        if (next < GameStartupStage.TexturesLoaded && IsTexturesLoaded(line))
        {
            next = GameStartupStage.TexturesLoaded;
        }

        return next;
    }

    /// <summary>
    /// 纯函数：按关键词判定这一行给出的信号（PCL2 <c>ModWatcher.GameLog</c> 的崩溃/关闭表）。
    ///
    /// 含 <see cref="ChatMarker"/> 的行一律不判：玩家可以在聊天栏里打出任何字符串，
    /// 包括「Crash report saved to」，而聊天行会被原样回显到 stdout。
    /// </summary>
    /// <param name="line">一行游戏输出。</param>
    /// <returns>信号；普通行返回 <see cref="GameLogSignal.None"/>。</returns>
    public static GameLogSignal Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Contains(ChatMarker, StringComparison.Ordinal))
        {
            return GameLogSignal.None;
        }

        // 崩溃优先于关闭：一行同时命中两类关键词时按崩溃处理，宁可多分析一次。
        if (line.Contains("Crash report saved to", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("This crash report has been saved to:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Could not save crash report to", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("An exception was thrown, the game will display an error screen and halt.", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("/ERROR]: Unable to launch", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ERROR]: Unable to launch", StringComparison.OrdinalIgnoreCase))
        {
            return GameLogSignal.Crashed;
        }

        if (line.Contains("Someone is closing me!", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Restarting Minecraft with command", StringComparison.OrdinalIgnoreCase))
        {
            return GameLogSignal.Closing;
        }

        return GameLogSignal.None;
    }

    /// <summary>
    /// 纯函数：进程退出后的崩溃判定（PCL2 <c>ModWatcher</c> 的双判据）。
    ///
    /// 规则与理由：
    /// <list type="number">
    /// <item>日志已经明确给出崩溃特征 → 崩溃；明确给出「主动关闭/重启」→ 正常结束（关键词优先于退出码）。</item>
    /// <item>阶段没到「可玩」（材质加载完成）就退出 → 崩溃，即使 exitCode = 0。
    ///       门槛用 <see cref="PlayableStage"/> 而不是 <see cref="GameStartupStage.MainMenu"/>：
    ///       主菜单阶段只有「枚举到游戏窗口」才能进入，非 Windows 平台或平台服务缺失时永远到不了，
    ///       用它会把所有正常退出都判成崩溃。窗口一出现就会把阶段推到 MainMenu，两者在 Windows 上等价。</item>
    /// <item>已经进过游戏、退出码非 0、且版本发行年不早于 2012 → 崩溃。2012 年前的老版本
    ///       在关闭时常返回非零码，那是它本来的行为，不是崩溃。</item>
    /// </list>
    /// 取消启动的路径不经过这里：取消由调用方直接返回，不该报崩溃。
    /// </summary>
    /// <param name="exitCode">进程退出码。</param>
    /// <param name="stage">退出时观察到的启动阶段。</param>
    /// <param name="releaseYear">版本发行年；0 或负数表示未知（按 PCL2 语义不据此判崩溃）。</param>
    /// <param name="signal">退出前从日志里累积到的最强信号。</param>
    public static GameExitVerdict JudgeExit(
        int exitCode,
        GameStartupStage stage,
        int releaseYear,
        GameLogSignal signal)
    {
        if (signal == GameLogSignal.Crashed)
        {
            return GameExitVerdict.Crashed;
        }

        if (signal == GameLogSignal.Closing)
        {
            return GameExitVerdict.Ended;
        }

        if (stage < PlayableStage)
        {
            return GameExitVerdict.Crashed;
        }

        if (exitCode != 0 && releaseYear >= CrashReleaseYear)
        {
            return GameExitVerdict.Crashed;
        }

        return GameExitVerdict.Ended;
    }

    /// <summary>阶段的中文描述，直接用于进度条文案。</summary>
    /// <param name="stage">启动阶段。</param>
    public static string DescribeStage(GameStartupStage stage) => stage switch
    {
        GameStartupStage.None => "游戏进程已启动，等待输出…",
        GameStartupStage.Launched => "游戏进程已启动，正在加载…",
        GameStartupStage.UserSet => "正在注入账号信息…",
        GameStartupStage.LwjglReady => "正在初始化图形…",
        GameStartupStage.AudioReady => "正在初始化声音…",
        GameStartupStage.TexturesLoaded => "正在加载材质（5/5）…",
        GameStartupStage.MainMenu => "游戏窗口已出现，正在进入主菜单…",
        _ => "游戏运行中…",
    };

    /// <summary>材质里程碑：Minecraft 的 atlas 建好之后打的那行，或动画信息加载完成。</summary>
    private static bool IsTexturesLoaded(string line) =>
        (line.Contains("Created", StringComparison.Ordinal) &&
         line.Contains("textures", StringComparison.Ordinal) &&
         line.Contains("-atlas", StringComparison.Ordinal)) ||
        line.Contains("Found animation info", StringComparison.Ordinal);

    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void RaiseStageAdvanced(GameStartupStage stage)
    {
        try
        {
            StageAdvanced?.Invoke(this, stage);
        }
        catch (Exception)
        {
            // 订阅方（进度条）抛异常不能影响游戏进程的输出读取线程。
        }
    }

    private void RaiseCrashSignal(string line)
    {
        try
        {
            CrashSignal?.Invoke(this, line);
        }
        catch (Exception)
        {
            // 同上：崩溃留证失败不该改变判定结果。
        }
    }
}
