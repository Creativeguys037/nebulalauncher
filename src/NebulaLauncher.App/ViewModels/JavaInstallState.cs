using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 一次「缺 Java / 装 Java」的可见阶段。
///
/// 判定该不该装、装哪个版本仍然是 Core 的 <see cref="JavaProvisionPlanner"/> 的职责；
/// 这里只管「用户现在能看到什么」：按钮能不能点、进度条在哪、失败后还能不能重试。
/// </summary>
public enum JavaInstallPhase
{
    /// <summary>没有正在处理的 Java 安装：页面初态，或上下文已被清掉。</summary>
    Idle = 0,

    /// <summary>处在一次「缺 Java」的上下文里，planner 说可以一键装。</summary>
    Ready = 1,

    /// <summary>处在上下文里，但启动器装不了（上界冲突 / 无下载包 / 判不出需求），只能手动指定。</summary>
    Manual = 2,

    /// <summary>下载中。此时按钮必须禁用，否则会并发下载同一个运行时。</summary>
    Installing = 3,

    /// <summary>装好了：路径已写回并强制重扫过，可以重新启动。</summary>
    Installed = 4,

    /// <summary>上次下载失败。状态回到可重试，而不是卡在失败态。</summary>
    Failed = 5,

    /// <summary>上次下载被取消。同样可以重试。</summary>
    Canceled = 6,
}

/// <summary>一次安装尝试的结局。界面据此补只有自己才知道的文案（例如设置页的红色错误行）。</summary>
public enum JavaInstallOutcome
{
    /// <summary>下载、写回、保存、强制重扫全部完成。</summary>
    Succeeded = 0,

    /// <summary>下载返回空路径：网络或代理问题，重试有意义。</summary>
    DownloadFailed = 1,

    /// <summary>中途抛异常：解压失败、磁盘满等。</summary>
    Failed = 2,

    /// <summary>被取消。</summary>
    Canceled = 3,

    /// <summary>已有一个安装在进行，本次直接返回（防并发）。</summary>
    Busy = 4,

    /// <summary>Java 服务未接入。</summary>
    ServiceUnavailable = 5,
}

/// <summary>一次安装尝试的结果。</summary>
/// <param name="Outcome">结局。</param>
/// <param name="MajorVersion">这次尝试的主版本。</param>
/// <param name="Path">成功时的新 java 可执行文件路径。</param>
/// <param name="Error">异常消息（仅 <see cref="JavaInstallOutcome.Failed"/>）。</param>
public readonly record struct JavaInstallResult(
    JavaInstallOutcome Outcome,
    int MajorVersion,
    string? Path,
    string? Error)
{
    /// <summary>是否装成功。调用方不必再比枚举值。</summary>
    public bool Succeeded => Outcome == JavaInstallOutcome.Succeeded;
}

/// <summary>
/// 三处 Java 安装的文案口径。
///
/// 为什么是参数而不是常量：三处的措辞本来就是逐字不同的（版本页的取消句比首页短、
/// 设置页把长句放在红色错误行而状态行只写「下载失败」）。合并状态机不等于统一口径——
/// 统一措辞属于改变用户可见行为，不在这次改动的范围内。
/// </summary>
public sealed class JavaInstallWording
{
    /// <summary>开始下载时状态行的那一句。</summary>
    public required Func<int, string> Downloading { get; init; }

    /// <summary>下载返回空路径时的状态行文案。</summary>
    public required Func<int, string> DownloadFailed { get; init; }

    /// <summary>取消后的状态行文案（整句定死，三处标点不同）。</summary>
    public required string Canceled { get; init; }

    /// <summary>抛异常时的状态行文案；第二个参数是异常消息。</summary>
    public required Func<int, string, string> Failed { get; init; }

    /// <summary>装成功后的状态行文案；第二个参数是新路径。</summary>
    public required Func<int, string, string> Succeeded { get; init; }

    /// <summary>首页：失败句带「Java N」前缀，取消句带句号。</summary>
    public static JavaInstallWording Home { get; } = new()
    {
        Downloading = major => $"正在下载 Java {major}…",
        DownloadFailed = major => $"Java {major} 下载失败，请检查网络或代理后重试。",
        Canceled = "下载已取消。",
        Failed = (major, error) => $"Java {major} 安装失败：{error}",
        Succeeded = (major, path) => $"已安装 Java {major}：{path}",
    };

    /// <summary>版本页：失败句不带前缀（页面更窄，状态条已经说明了上下文）。</summary>
    public static JavaInstallWording Versions { get; } = new()
    {
        Downloading = major => $"正在下载 Java {major}…",
        DownloadFailed = _ => "下载失败，请检查网络或代理后重试。",
        Canceled = "已取消下载。",
        Failed = (_, error) => $"安装失败：{error}",
        Succeeded = (major, path) => $"Java {major} 已就绪：{path}",
    };

    /// <summary>设置页：状态行只写短句，长句走独立的错误行（由页面自己拼）。</summary>
    public static JavaInstallWording Settings { get; } = new()
    {
        Downloading = major => $"正在下载 Java {major}…",
        DownloadFailed = _ => "下载失败",
        Canceled = "下载已取消",
        Failed = (_, _) => "安装失败",
        Succeeded = (major, path) => $"Java {major} 已安装：{path}",
    };
}

/// <summary>
/// 装完之后把新路径接到哪里。
///
/// 为什么必须做成参数：首页与版本页装完要立刻生效（写 <c>settings.json</c> 并落盘），
/// 设置页只是把路径填进输入框草稿（落盘由用户点「保存设置」决定，否则会破坏「取消修改」）。
/// 这是三处唯一的实质差异，写进状态机就是三份分支。
/// </summary>
public sealed class JavaInstallSink
{
    /// <summary>
    /// internal 而不是 public：生产代码只该走下面两个工厂（落点只有「设置文件」与「页面草稿」两种），
    /// 但测试需要造一个「按顺序记录调用」的落点，才能钉住写回 → 保存 → 强制重扫的顺序。
    /// </summary>
    internal JavaInstallSink(Action<string> applyPath, Func<CancellationToken, Task>? saveAsync)
    {
        ApplyPath = applyPath;
        SaveAsync = saveAsync;
    }

    /// <summary>把路径写回目标（可以是设置对象，也可以是页面属性）。</summary>
    public Action<string> ApplyPath { get; }

    /// <summary>落盘；null 表示落盘不由安装流程负责。</summary>
    public Func<CancellationToken, Task>? SaveAsync { get; }

    /// <summary>
    /// 首页 / 版本页：写 <see cref="ISettingsService.Current"/> 的 <c>JavaPath</c> 并立即保存。
    /// 设置服务缺失时退化为「只回报路径」——页面本来就在降级态，不应该再抛。
    /// </summary>
    public static JavaInstallSink SettingsFile(ISettingsService? settings) => new(
        path =>
        {
            if (settings is not null)
            {
                settings.Current.JavaPath = path;
            }
        },
        settings is null ? null : token => settings.SaveAsync(token));

    /// <summary>设置页：只写回调用方给的属性（页面草稿），不落盘。</summary>
    public static JavaInstallSink Draft(Action<string> applyPath) => new(applyPath, saveAsync: null);
}

/// <summary>
/// 「缺 Java → 装 → 装好」的唯一状态机，首页 / 版本页 / 设置页 Java 分区共用。
///
/// 为什么放在 App 层而不是 Core：它驱动的是 Visible、按钮文案、进度比例这些 UI 状态，
/// 还要持有一个下载用的取消源并与页面生命周期绑定；Core 那边只该有判定用的纯函数
/// （<see cref="JavaProvisionPlanner"/>）——把 UI 状态塞进 Core 会让 Core 反过来依赖界面语义。
///
/// 为什么首页与版本页各持一个实例而不是共享单例：两者各自对应一次独立的启动失败上下文
/// （首页可能停在「缺 Java 25」，版本页停在另一个版本的「缺 Java 17」），共享一份状态会让
/// 一边的失败把另一边正在下载的进度条清掉。
///
/// 合并守住的行为（三处逐字保留）：按钮文案、进度百分比、失败/取消文案、装完后能否重试。
/// </summary>
public sealed partial class JavaInstallState : ObservableObject, IDisposable
{
    private readonly IJavaService? _java;
    private readonly JavaInstallWording _wording;

    /// <summary>
    /// 下载自己的取消源。与启动流程的 CTS 分开：取消下载不该连带打断启动，
    /// 反过来页面离开时也只该停下载。每次安装重建一个，避免复用已取消的源。
    /// </summary>
    private CancellationTokenSource? _cts;

    public JavaInstallState(IJavaService? java, JavaInstallWording wording)
    {
        _java = java;
        _wording = wording;
    }

    // ---------------- 上下文（缺 Java 的判定结论） ----------------

    /// <summary>
    /// planner 的结论；null 表示判不出来（服务未接入 / 探测失败）。
    /// 界面要区分这两种 null：判不出来时仍需显示「打开 Java 设置」的出口。
    /// </summary>
    [ObservableProperty]
    private JavaProvisionPlan? _plan;

    /// <summary>这次启动需要的主版本。null 表示没有缺 Java 的上下文。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContext))]
    private int? _requiredMajor;

    /// <summary>planner 建议安装的主版本；不可一键安装时为 null。</summary>
    [ObservableProperty]
    private int? _installMajor;

    /// <summary>提示文案。取 planner 的说明，判不出来时由调用方给一句降级说明。</summary>
    [ObservableProperty]
    private string? _summary;

    // ---------------- 运行状态 ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private JavaInstallPhase _phase;

    /// <summary>装成功的主版本。非空时按钮要换成「已安装 Java N，重新启动」。</summary>
    [ObservableProperty]
    private int? _installedMajor;

    /// <summary>0..1 的下载比例。XAML 一律用 <c>Minimum=0 Maximum=1</c> 绑它，不套转换器。</summary>
    [ObservableProperty]
    private double _ratio;

    [ObservableProperty]
    private string _percentText = "0%";

    /// <summary>下载 / 失败 / 成功的状态行。空表示还没开始。</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>是否处在一次「缺 Java」的上下文里（决定整块入口的可见性）。</summary>
    public bool HasContext => RequiredMajor is > 0;

    /// <summary>是否正在下载。绑定进度条与按钮禁用都要它。</summary>
    public bool IsInstalling => Phase == JavaInstallPhase.Installing;

    /// <summary>是否已经装好（本次装完，或本机已有满足要求的运行时）。</summary>
    public bool IsInstalled => Phase == JavaInstallPhase.Installed;

    /// <summary>
    /// 是否允许发起一次安装（不含「已经装好」的判断）。
    ///
    /// 设置页装完一个版本后还要能继续装别的版本，所以这里只看「此刻是不是正在下载」；
    /// 首页禁用「已装好之后的安装按钮」是页面自己的语义，由页面在上层叠加。
    /// </summary>
    public bool CanStart => Phase != JavaInstallPhase.Installing;

    partial void OnRatioChanged(double value) =>
        PercentText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(int)Math.Round(Math.Clamp(value, 0, 1) * 100)}%");

    /// <summary>
    /// 进入一次「缺 Java」的上下文。
    ///
    /// <paramref name="plan"/> 为 null 表示判定失败（探测抛异常 / 服务未接入）：这时只保留
    /// 需求主版本与调用方给的降级说明，安装一律不可用——给一个装不成的按钮等于让用户白下一次。
    /// </summary>
    public void Begin(int requiredMajor, JavaProvisionPlan? plan, string? summary = null)
    {
        Plan = plan;
        RequiredMajor = requiredMajor > 0 ? requiredMajor : null;
        InstallMajor = plan?.InstallMajor;
        Summary = summary ?? plan?.Summary;
        InstalledMajor = null;
        Phase = plan is { CanAutoInstall: true } ? JavaInstallPhase.Ready : JavaInstallPhase.Manual;
        Ratio = 0;
        StatusText = null;
    }

    /// <summary>
    /// 清掉上一轮的上下文（新一次启动、或页面重新进入）。
    ///
    /// 不在这里取消在途下载：取消是页面生命周期的决定（版本页离开页面才取消），
    /// 状态清空与「停掉下载」是两件事，混在一起会让「点启动时顺手取消安装」这种意外行为冒出来。
    /// </summary>
    public void Reset()
    {
        Plan = null;
        RequiredMajor = null;
        InstallMajor = null;
        Summary = null;
        InstalledMajor = null;
        Phase = JavaInstallPhase.Idle;
        Ratio = 0;
        StatusText = null;
    }

    /// <summary>把下载进度折进 0..1 的比例。调度（UI 线程）由调用方的 <see cref="Progress{T}"/> 负责。</summary>
    public void ApplyProgress(DownloadProgress progress) =>
        Ratio = double.IsFinite(progress.Ratio) ? Math.Clamp(progress.Ratio, 0, 1) : 0;

    /// <summary>停掉在途下载。页面离开时调用；已装好的状态不受影响。</summary>
    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 安装已结束，取消是空操作。
        }
    }

    /// <summary>
    /// 页面释放时收尾。取消源是这个类唯一持有的可释放资源，
    /// 不在这里释放就等于把它留给 GC 的终结器，CA1001 抓的正是这类问题。
    /// </summary>
    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 下载 → 写回 → 保存 → 强制重扫。
    ///
    /// 这个顺序不能颠倒，也不能省最后一步：少了强制重扫，界面会继续用旧的探测缓存说
    /// 「本机没有 Java」，用户会认为安装没生效。写回与保存都用 <see cref="CancellationToken.None"/>：
    /// 下载既然已经成功，页面切走导致的取消不该让「装好了但没记住」这种状态出现。
    /// </summary>
    public async Task<JavaInstallResult> InstallAsync(
        int majorVersion,
        JavaInstallSink sink,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (Phase == JavaInstallPhase.Installing)
        {
            return new JavaInstallResult(JavaInstallOutcome.Busy, majorVersion, null, null);
        }

        if (_java is null)
        {
            return new JavaInstallResult(JavaInstallOutcome.ServiceUnavailable, majorVersion, null, null);
        }

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        Phase = JavaInstallPhase.Installing;
        InstalledMajor = null;
        Ratio = 0;
        StatusText = _wording.Downloading(majorVersion);

        try
        {
            var path = await _java.DownloadRuntimeAsync(majorVersion, progress, token).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = _wording.DownloadFailed(majorVersion);
                Phase = JavaInstallPhase.Failed;
                return new JavaInstallResult(JavaInstallOutcome.DownloadFailed, majorVersion, null, null);
            }

            sink.ApplyPath(path);

            if (sink.SaveAsync is not null)
            {
                await sink.SaveAsync(CancellationToken.None).ConfigureAwait(true);
            }

            // 强制重扫：探测缓存里没有刚装的运行时，这一步省掉就等于「安装没生效」。
            await _java.DetectAsync(forceRefresh: true, CancellationToken.None).ConfigureAwait(true);

            InstalledMajor = majorVersion;
            Ratio = 1;
            StatusText = _wording.Succeeded(majorVersion, path);
            Phase = JavaInstallPhase.Installed;
            return new JavaInstallResult(JavaInstallOutcome.Succeeded, majorVersion, path, null);
        }
        catch (OperationCanceledException)
        {
            StatusText = _wording.Canceled;
            Phase = JavaInstallPhase.Canceled;
            return new JavaInstallResult(JavaInstallOutcome.Canceled, majorVersion, null, null);
        }
        catch (Exception ex)
        {
            StatusText = _wording.Failed(majorVersion, ex.Message);
            Phase = JavaInstallPhase.Failed;
            return new JavaInstallResult(JavaInstallOutcome.Failed, majorVersion, null, ex.Message);
        }
    }
}