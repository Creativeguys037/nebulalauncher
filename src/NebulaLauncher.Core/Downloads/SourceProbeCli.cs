using System.Globalization;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 源测速的命令行输出与退出码。全部是纯函数：只读 <see cref="SourceProbeReport"/>，
/// 不碰时钟、不碰网络、不写控制台——CLI 的格式契约因此能在单测里钉死，不需要真实测量。
///
/// ## 三行格式
/// <code>
/// official ok=true firstByteMs=210 throughputKb=1840 targets=2/2
/// mirror ok=true firstByteMs=95 throughputKb=5200 targets=2/2
/// decision=mirror
/// </code>
/// 一行一个源、行内全是单 token 的 key=value：脚本用 <c>Select-String</c> 或 split 就能取，
/// 人把它贴进 issue 也能直接读。用三行而不是一行 JSON，是因为这里的使用者主要是
/// 「看一眼再贴出来」的排障场景，不是程序间传输。
///
/// ## 为什么额外提供 <see cref="ExitCode"/>
/// 退出码是无人值守场景唯一的可编程信号：0 = 至少一侧能用，3 = 两侧都不可用。
/// 用 3 而不是 1，是为了和「进程异常/未处理异常」区分开——脚本要能判断
/// 「测过了但网络不通」和「程序崩了」。
/// </summary>
public static class SourceProbeCli
{
    /// <summary>至少一侧有成功样本。</summary>
    public const int SuccessExitCode = 0;

    /// <summary>两侧都没有成功样本（含「一个目标都没测」）。</summary>
    public const int BothFailedExitCode = 3;

    /// <summary>decision 行的取值：建议官方。</summary>
    public const string DecisionOfficial = "official";

    /// <summary>decision 行的取值：建议镜像。</summary>
    public const string DecisionMirror = "mirror";

    /// <summary>decision 行的取值：两侧差不多，不给建议。</summary>
    public const string DecisionTie = "tie";

    /// <summary>decision 行的取值：没有可用的源。</summary>
    public const string DecisionBothFailed = "both-failed";

    /// <summary>
    /// 把报告渲染成固定的三行。行数与顺序恒定（official / mirror / decision），
    /// 缺样本的一侧照样输出，字段取 0/false：脚本不必处理「这一行不存在」的分支。
    /// </summary>
    public static IReadOnlyList<string> FormatReport(SourceProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return
        [
            FormatSide("official", Find(report.Samples, DownloadSource.Official)),
            FormatSide("mirror", Find(report.Samples, DownloadSource.Mirror)),
            "decision=" + DecisionText(report),
        ];
    }

    /// <summary>
    /// 本次测速对应的进程退出码：至少一侧成功 → <see cref="SuccessExitCode"/>，
    /// 否则 → <see cref="BothFailedExitCode"/>。
    ///
    /// 判据用「样本里有没有成功项」而不是 <see cref="SourceProbeReport.BothFailed"/>：
    /// 空报告（没测、目标全不可镜像）的 <c>BothFailed</c> 是 false，但它同样没有任何可用源，
    /// 脚本应该拿到 3 而不是 0。
    /// </summary>
    public static int ExitCode(SourceProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return HasAnySuccess(report.Samples) ? SuccessExitCode : BothFailedExitCode;
    }

    /// <summary>
    /// decision 行的取值。<b>与 <see cref="ExitCode"/> 共用「有没有成功样本」这个前置判断</b>：
    /// 两者若用不同判据，脚本会看到 <c>decision=official</c> 却拿到退出码 3。
    /// </summary>
    public static string DecisionText(SourceProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.BothFailed || !HasAnySuccess(report.Samples))
        {
            return DecisionBothFailed;
        }

        if (report.IsTie)
        {
            return DecisionTie;
        }

        return report.SuggestedSource switch
        {
            DownloadSource.Official => DecisionOfficial,
            DownloadSource.Mirror => DecisionMirror,

            // 两侧都成功、又没判持平、却没给建议：只有手工拼出来的报告才会走到这里。
            // 对脚本而言它等价于「没有可执行的切换建议」，所以按持平输出，
            // 而不是把 null / auto 这类非枚举取值漏进 decision 行。
            _ => DecisionTie,
        };
    }

    private static string FormatSide(string key, SourceProbeSample? sample)
    {
        // 失败样本的字段按 0 输出：SourceProbeSample 里首字节/吞吐本来就记 0，
        // 这里再取一次是为了让「样本缺失」与「样本失败」输出完全一致。
        var ok = sample?.Succeeded == true;
        var firstByteMs = ok ? sample!.FirstByteMs : 0d;
        var throughputKbps = ok ? sample!.ThroughputKbps : 0d;
        var targetsOk = sample?.TargetsOk ?? 0;
        var targetsTotal = targetsOk + (sample?.TargetsFailed ?? 0);

        return key
               + " ok=" + (ok ? "true" : "false")
               + " firstByteMs=" + FormatInteger(firstByteMs)
               + " throughputKb=" + FormatInteger(throughputKbps)
               + " targets=" + targetsOk.ToString(CultureInfo.InvariantCulture)
               + "/" + targetsTotal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 毫秒/吞吐取整。用 InvariantCulture：命令行输出要能被脚本按小数点解析，
    /// 而默认区域设置在某些环境里会把小数点写成逗号。
    /// </summary>
    private static string FormatInteger(double value)
    {
        // 非有限值只可能来自被替换的探针实现；CLI 不能把 NaN / Infinity 写进本应全是整数的字段。
        if (!double.IsFinite(value) || value < 0)
        {
            return "0";
        }

        // 先四舍五入再转 long：直接 (long) 会截断，190.9ms 显示成 190ms 会被误读成「比 191 快」。
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);

        // double 的上限远超 long；越界时给 long.MaxValue 而不是抛 OverflowException——
        // 输出函数不该因为一个荒谬的输入把整个进程的退出码变成「崩溃」。
        return rounded >= long.MaxValue
            ? long.MaxValue.ToString(CultureInfo.InvariantCulture)
            : ((long)rounded).ToString(CultureInfo.InvariantCulture);
    }

    private static bool HasAnySuccess(IReadOnlyList<SourceProbeSample> samples)
    {
        foreach (var sample in samples)
        {
            if (sample.Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    private static SourceProbeSample? Find(IReadOnlyList<SourceProbeSample> samples, DownloadSource source)
    {
        foreach (var sample in samples)
        {
            if (sample.Source == source)
            {
                return sample;
            }
        }

        return null;
    }
}