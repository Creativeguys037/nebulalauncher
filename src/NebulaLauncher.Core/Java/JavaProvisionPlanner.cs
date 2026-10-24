using System.Globalization;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Java;

/// <summary>「本机缺 Java」时界面能走的三条路。</summary>
public enum JavaProvisionAction
{
    /// <summary>已装的运行时满足要求，不需要动作。</summary>
    NothingToDo = 0,

    /// <summary>可以一键下载安装（主版本在 <see cref="JavaProvisionPlanner.DownloadableMajors"/> 里）。</summary>
    AutoInstall = 1,

    /// <summary>启动器装不了（没有该版本的下载包，或允许区间本身冲突），只能让用户手动指定路径。</summary>
    ManualConfigure = 2,
}

/// <summary>
/// 一次「缺 Java」处置建议。纯数据，界面照它决定显示哪个按钮、按钮上写什么。
/// </summary>
/// <param name="RequiredMajor">这次启动需要的主版本上限；拿不到需求时为 null。</param>
/// <param name="InstallMajor">建议安装的主版本；不需要装或装不了时为 null。</param>
/// <param name="Action">建议走哪条路。</param>
/// <param name="EstimatedDownloadBytes">建议安装项的下载体积估算（0 表示不适用）。</param>
/// <param name="Summary">一句中文说明，直接作为界面提示文案。</param>
public sealed record JavaProvisionPlan(
    int? RequiredMajor,
    int? InstallMajor,
    JavaProvisionAction Action,
    long EstimatedDownloadBytes,
    string Summary)
{
    /// <summary>是否可以用「一键安装」解决问题。</summary>
    public bool CanAutoInstall => Action == JavaProvisionAction.AutoInstall && InstallMajor is > 0;

    /// <summary>是否需要界面给出出口（false 时连提示都不用显示）。</summary>
    public bool NeedsAttention => Action != JavaProvisionAction.NothingToDo;
}

/// <summary>
/// 「本机没有满足要求的 Java」→「建议装哪个、能不能一键装」。纯函数，零 IO、零网络。
///
/// 为什么需要它：<c>GameLauncher</c> 只能给出「需要 Java 25」这句文案，用户还得自己找设置页、
/// 挑版本、点下载。抄 PCL2 <c>ModJava.vb</c> 的做法，把「装哪个版本」的判断集中到这里一份，
/// 界面只负责渲染——否则首页 / 版本页 / 设置页三处各写一遍判断，早晚互相不一致。
///
/// 判断顺序：拿到需求 → 已装的是否真的能用（含上界）→ 区间是否冲突 → 该主版本是否有下载包。
/// 每一条都按「宁可退回手动配置，也不给一个装了也用不了的按钮」处理。
/// </summary>
public static class JavaProvisionPlanner
{
    /// <summary>
    /// 启动器能一键下载的主版本，与设置页的 <c>SettingsViewModel.JavaDownloadOptions</c> 同源：
    /// 那些就是 <c>MojangRuntimeCatalog</c> 覆盖的组件（java-runtime legacy/alpha/gamma/delta/epsilon）。
    ///
    /// 顺序即界面下拉顺序（新版本在前）。改这里必须同步改设置页那张列表，
    /// 否则「启动失败处能装 25」而「设置页选不到 25」这种分裂会立刻出现。
    /// </summary>
    public static readonly IReadOnlyList<int> DownloadableMajors = [25, 21, 17, 16, 8];

    /// <summary>区间无上界。与 <c>JavaRequirementTable</c> 的语义一致。</summary>
    private const int NoUpperBound = int.MaxValue;

    /// <summary>
    /// 体积估算兜底：Mojang / Adoptium 的 JRE 压缩包都在 40–60 MB 量级。
    /// 只用于按钮文案里的「约 xx MB」，不参与任何判据——所以不必精确，也不去联网查。
    /// </summary>
    private const long DefaultEstimateBytes = 50L * 1024 * 1024;

    private const long Megabyte = 1024L * 1024;

    /// <summary>该主版本是否有一键安装的下载包。</summary>
    public static bool IsDownloadable(int majorVersion) => DownloadableMajors.Contains(majorVersion);

    /// <summary>按主版本给一个下载体积估算。未知主版本用兜底值。</summary>
    public static long EstimateDownloadBytes(int majorVersion) => majorVersion switch
    {
        8 => 40 * Megabyte,
        16 => 45 * Megabyte,
        17 => 47 * Megabyte,
        21 => 48 * Megabyte,
        25 => 55 * Megabyte,
        _ => DefaultEstimateBytes,
    };

    /// <summary>
    /// 给「缺少 Java」的启动失败产出处置建议。
    /// </summary>
    /// <param name="requiredMajor">该游戏版本（含加载器）需要的主版本；≤0 表示拿不到需求。</param>
    /// <param name="installed">本机已探测到的运行时；空集合表示一个都没有。</param>
    /// <param name="allowedRange">
    /// 允许的 Java 主版本闭区间（<c>IJavaService.GetAllowedRange</c>）。
    /// null 表示没有上界——此时下界取 <paramref name="requiredMajor"/>。
    /// </param>
    public static JavaProvisionPlan Plan(
        int requiredMajor,
        IReadOnlyList<JavaInfo> installed,
        (int Min, int Max)? allowedRange = null)
    {
        ArgumentNullException.ThrowIfNull(installed);

        // 需求缺失（未知版本 / 快照）：给不出该装哪个，只能让用户自己指路径。
        if (requiredMajor <= 0)
        {
            return new JavaProvisionPlan(
                null,
                null,
                JavaProvisionAction.ManualConfigure,
                0,
                "无法确定该游戏版本需要哪个 Java 主版本，请在「设置 → Java」手动指定 Java 路径。");
        }

        var (min, max) = allowedRange ?? (requiredMajor, NoUpperBound);

        // 「已装」不等于「能用」：上界是硬约束（1.12.2 Forge 只能 Java ≤8），
        // 装了 Java 17 在那个组合下照样起不来，不能据此说「已满足」。
        var usable = installed.FirstOrDefault(info =>
            info.MajorVersion >= requiredMajor
            && info.MajorVersion >= min
            && info.MajorVersion <= max);

        if (usable is not null)
        {
            return new JavaProvisionPlan(
                requiredMajor,
                null,
                JavaProvisionAction.NothingToDo,
                0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"本机已有 Java {usable.MajorVersion}，满足 Java {requiredMajor} 的要求。"));
        }

        // 需求与上界可能互相矛盾（加载器规则把区间压空）：这时候装哪个版本都起不来，
        // 给出「安装」按钮只会让用户白下一次 50 MB。
        var installMajor = Math.Max(requiredMajor, min);

        if (installMajor > max)
        {
            return new JavaProvisionPlan(
                requiredMajor,
                null,
                JavaProvisionAction.ManualConfigure,
                0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"该版本要求 Java {requiredMajor}，但加载器允许的上限是 Java {max}，两者冲突："
                    + $"装任何版本都起不来，请在「设置 → Java」手动指定 Java 路径。"));
        }

        if (!IsDownloadable(installMajor))
        {
            return new JavaProvisionPlan(
                requiredMajor,
                null,
                JavaProvisionAction.ManualConfigure,
                0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"需要 Java {installMajor}，启动器没有该版本的下载包，请在「设置 → Java」手动指定 Java 路径。"));
        }

        var estimate = EstimateDownloadBytes(installMajor);

        return new JavaProvisionPlan(
            requiredMajor,
            installMajor,
            JavaProvisionAction.AutoInstall,
            estimate,
            string.Create(
                CultureInfo.InvariantCulture,
                $"本机没有满足要求的 Java，可一键安装 Java {installMajor}（约 {estimate / Megabyte} MB）。"));
    }
}