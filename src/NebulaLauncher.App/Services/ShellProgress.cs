using System;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Services;

/// <summary>
/// 任务栏 / Dock 进度的接线点。
///
/// 为什么要这一层：<c>IPlatformService.SetTaskbarProgress</c> 背后是 COM 调用（Windows 的
/// <c>ITaskbarList3</c>），而启动与下载的进度回调都是高频的（下载每读一个 80 KiB 块就回调一次）。
/// 节流、去重、非法值过滤集中在这里，页面只写一行 <see cref="Report"/>。
///
/// 平台差异不在这里判断：非 Windows 平台的 <c>SetTaskbarProgress</c> 已是空实现，
/// 本类只负责「这个值该不该交给平台」。
///
/// 已知限制：窗口隐藏到托盘后任务栏按钮消失，<c>ITaskbarList3</c> 画在按钮上的进度也随之不可见。
/// 这是平台行为（进度是任务栏按钮的附属显示），不是本类的缺陷；不要为此改成常驻任务栏按钮。
///
/// 多条进度（启动与安装同时进行）不叠加：任务栏只有一个进度条，后上报的值直接覆盖前者，
/// 也就是「最后上报者优先」。用时间戳排队会让进度停在旧任务的值上，反而更糟。
/// </summary>
public static class ShellProgress
{
    /// <summary>最小上报间隔（毫秒）：10 次/秒。窗口内看不出一帧差别，但 COM 调用少一个数量级。</summary>
    public const int MinIntervalMs = 100;

    /// <summary>值变化小于该阈值视为没变，跳过上报。</summary>
    public const double MinDelta = 0.01;

    /// <summary>
    /// 约定 <c>-1</c> 表示清除进度条，与 <see cref="IPlatformService.SetTaskbarProgress"/> 一致。
    /// </summary>
    public const double ClearRatio = -1;

    private static readonly object Gate = new();

    private static IPlatformService? _platform;
    private static IAppLog? _log;
    private static IntPtr _handle = IntPtr.Zero;
    private static Func<long> _clock = static () => Environment.TickCount64;

    private static long _lastReportMs = -1;
    private static double _lastRatio = double.NaN;
    private static bool _firstReportLogged;

    /// <summary>平台服务与窗口句柄都已就绪。为 false 时 <see cref="Report"/> 是空操作。</summary>
    public static bool IsSupported
    {
        get
        {
            lock (Gate)
            {
                return _platform is not null && _handle != IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 挂接主窗口句柄。句柄在 <c>Window.Opened</c> 之后才有效，所以调用点必须在那之后。
    /// 平台服务与日志从 <see cref="App.Services"/> 取。
    /// </summary>
    public static void Attach(IntPtr handle) => Attach(ResolvePlatform(), handle, clock: null);

    /// <summary>
    /// 显式挂接（组合根与测试用）。
    /// </summary>
    /// <param name="platform">平台服务；null 表示降级为「只记录不上报」。</param>
    /// <param name="handle">顶层窗口句柄；<see cref="IntPtr.Zero"/> 表示未就绪。</param>
    /// <param name="clock">时间源（毫秒）。测试注入假时钟后可稳定验证节流。</param>
    public static void Attach(IPlatformService? platform, IntPtr handle, Func<long>? clock = null)
    {
        IAppLog? log;

        lock (Gate)
        {
            _platform = platform;
            _log ??= ResolveLog();
            log = _log;
            _handle = handle;
            _clock = clock ?? (static () => Environment.TickCount64);

            // 重新挂接（窗口重建、测试切换替身）时清零节流状态，否则新窗口会继承旧窗口的
            // 「上次上报的值」，第一个进度可能被误判成「没变化」而丢掉。
            _lastReportMs = -1;
            _lastRatio = double.NaN;
            _firstReportLogged = false;
        }

        log?.Debug($"任务栏进度已挂接：handle=0x{handle.ToInt64():X}, supported={platform is not null && handle != IntPtr.Zero}");
    }

    /// <summary>解除挂接并丢弃节流状态。进程退出与测试清理用；之后 <see cref="Report"/> 是空操作。</summary>
    public static void Detach()
    {
        lock (Gate)
        {
            _platform = null;
            _log = null;
            _handle = IntPtr.Zero;
            _lastReportMs = -1;
            _lastRatio = double.NaN;
            _firstReportLogged = false;
            _clock = static () => Environment.TickCount64;
        }
    }

    /// <summary>
    /// 上报 0..1 的进度。返回值表示这次是否真的调到了平台 API（便于测试与诊断）。
    ///
    /// 节流规则：与上次上报相差小于 <see cref="MinDelta"/> 丢弃；距上次不足
    /// <see cref="MinIntervalMs"/> 也丢弃。清除（<see cref="Clear"/>）不受节流限制。
    /// </summary>
    public static bool Report(double ratio)
    {
        var normalized = Normalize(ratio);
        if (normalized is null)
        {
            // 非法值不传给平台：SetProgressValue 收到 NaN 会写出无意义的值。
            WriteLog($"任务栏进度值非法，已忽略：{ratio}");
            return false;
        }

        var value = normalized.Value;
        var isClear = value == ClearRatio;

        IPlatformService? platform;
        IAppLog? log;
        IntPtr handle;
        var announce = false;

        lock (Gate)
        {
            platform = _platform;
            log = _log;
            handle = _handle;

            if (platform is null || handle == IntPtr.Zero)
            {
                return false;
            }

            var now = _clock();

            // 清除永远放行：宁可多调一次，也不要让任务栏留着一条假进度。
            if (value >= 0 && !ShouldReport(_lastRatio, _lastReportMs, value, now))
            {
                return false;
            }

            if (!_firstReportLogged)
            {
                _firstReportLogged = true;
                announce = true;
            }

            _lastReportMs = now;
            _lastRatio = value;
        }

        if (announce)
        {
            // 每次上报都记会刷屏（10 条/秒 × 几分钟）；只记第一条，作为「接线真的生效」的日志证据。
            log?.Debug($"任务栏进度开始上报：{value:0.###}");
        }

        if (isClear)
        {
            // 清除是低频事件（每次启动/安装结束一次），记下来便于确认「进度条没有留在任务栏上」。
            log?.Debug("任务栏进度已清除（-1）");
        }

        try
        {
            platform.SetTaskbarProgress(handle, value);
            return true;
        }
        catch (Exception ex)
        {
            // 任务栏进度是锦上添花，绝不能让它把启动/安装流程带崩。
            log?.Warn("设置任务栏进度失败（已忽略）", ex);
            return false;
        }
    }

    /// <summary>清除任务栏进度（平台 API 收到 -1）。</summary>
    public static void Clear() => Report(ClearRatio);

    /// <summary>
    /// 把外部进度值映射成平台可接受的值：
    /// <list type="bullet">
    /// <item><c>NaN</c> / <c>±Infinity</c> / 越界（除 -1）→ null（不上报）；</item>
    /// <item><c>-1</c> → -1（清除）；</item>
    /// <item><c>0..1</c> → 原样。</item>
    /// </list>
    ///
    /// 越界值刻意不 clamp：传 1.5 说明上游算式有问题，静默夹到 1 会把问题藏起来，
    /// 而任务栏进度本身没有「必须显示」的义务。
    /// </summary>
    public static double? Normalize(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            return null;
        }

        if (ratio == ClearRatio)
        {
            return ClearRatio;
        }

        return ratio is >= 0 and <= 1 ? ratio : null;
    }

    /// <summary>
    /// 节流判定。抽成纯函数（时间源由参数传入）才能在不 sleep 的前提下单测：
    /// 真实 <see cref="Report"/> 里时间是 <see cref="Environment.TickCount64"/>。
    /// </summary>
    internal static bool ShouldReport(
        double lastRatio,
        long lastReportMs,
        double ratio,
        long nowMs,
        int minIntervalMs = MinIntervalMs,
        double minDelta = MinDelta)
    {
        // 首次上报（lastReportMs < 0）或从「无值」状态恢复。
        if (double.IsNaN(lastRatio) || lastReportMs < 0)
        {
            return true;
        }

        if (Math.Abs(ratio - lastRatio) < minDelta)
        {
            return false;
        }

        var elapsed = nowMs - lastReportMs;

        // 时钟回退（或 TickCount 回绕）按到期处理：宁可多推一次，也不要永远卡在旧值。
        return elapsed >= minIntervalMs || elapsed < 0;
    }

    private static void WriteLog(string message)
    {
        IAppLog? log;

        lock (Gate)
        {
            log = _log;
        }

        log?.Debug(message);
    }

    private static IPlatformService? ResolvePlatform() =>
        App.Services.GetService(typeof(IPlatformService)) as IPlatformService;

    private static IAppLog? ResolveLog() =>
        App.Services.GetService(typeof(IAppLog)) as IAppLog;
}
