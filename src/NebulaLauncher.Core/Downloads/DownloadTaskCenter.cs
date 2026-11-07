using System.Collections.Concurrent;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 一条「用户可理解的任务」的快照。
///
/// 与 <see cref="Models.DownloadTaskInfo"/> 的区别是粒度：那个是「一个文件」，
/// 这个是「一次用户发起的操作」——装一个版本、导一个整合包、备份一个世界、装一个 Java。
/// 下载页要的是后者：用户关心「Fabric 下载到 91%」，不关心 3000 个 assets 文件各自的进度。
/// </summary>
public sealed class LauncherTaskInfo
{
    public required string Id { get; init; }

    /// <summary>行标题，例如「荒野生存 · 1.21.1 · 模组包」。</summary>
    public required string Title { get; init; }

    /// <summary>副行：阶段 + 体量，例如「下载模组 · 32.4/52.1 MB · 12 项」。</summary>
    public string Detail { get; set; } = string.Empty;

    public LauncherTaskKind Kind { get; init; }

    public LauncherTaskState State { get; set; } = LauncherTaskState.Queued;

    public long TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    /// <summary>当前速度（字节/秒）。暂停或非下载类任务为 0。</summary>
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>失败原因或「可重试」说明；仅 <see cref="LauncherTaskState.Failed"/> 时非空。</summary>
    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? FinishedAt { get; set; }

    public int Sequence { get; init; }

    public double Ratio => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;

    /// <summary>剩余时间。速度未知或已下完时为 null。</summary>
    public TimeSpan? Eta =>
        SpeedBytesPerSecond > 1024 && TotalBytes > DownloadedBytes
            ? TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / SpeedBytesPerSecond)
            : null;

    /// <summary>
    /// 是否「尚未结束」。
    ///
    /// 必须包含 <see cref="LauncherTaskState.Queued"/>：注册后到 <c>Begin</c> 之间的窗口里
    /// 任务已经存在，若把它算成「非活跃非结束」，<see cref="DownloadTaskCenter.Snapshot"/>
    /// 的三路筛选会把它整个丢掉，界面看到的是「注册成功但列表是空的」。
    /// </summary>
    public bool IsActive => State is LauncherTaskState.Queued or LauncherTaskState.Running or LauncherTaskState.Paused;

    public bool IsFinished => State is LauncherTaskState.Completed or LauncherTaskState.Failed
        or LauncherTaskState.Canceled;
}

/// <summary>任务的来源分类。只影响行图标与筛选，不影响调度。</summary>
public enum LauncherTaskKind
{
    /// <summary>安装游戏本体或加载器。</summary>
    Install = 0,

    /// <summary>整合包导入 / 导出。</summary>
    Modpack = 1,

    /// <summary>世界备份 / 还原。</summary>
    Backup = 2,

    /// <summary>Java 运行时安装。</summary>
    Java = 3,

    /// <summary>在线资源下载（模组、资源包、光影、数据包、皮肤）。</summary>
    Resource = 4,

    /// <summary>服务端 jar 下载。</summary>
    Server = 5,

    /// <summary>修复 / 补全文件。</summary>
    Repair = 6,
}

public enum LauncherTaskState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Canceled = 5,
}

/// <summary>
/// 统一任务中心：把「互相独立、各自跑在自己的线程里」的高层操作汇总成一张任务表。
///
/// ## 为什么不能直接复用 <see cref="DownloadQueue"/>
/// 那个类管的是「一批文件的下载」，批次之间互斥（同一时刻只允许一个活动批次），
/// 且快照是文件级的。下载页要的是「同时有几个任务在跑、每个任务整体到了哪一步」，
/// 这与「一个批次里 3000 个文件」是两个层级。
///
/// 这里是上层：每个任务自带一个 <see cref="ILauncherTaskHandle"/>，由发起方自己上报进度
/// （版本安装从 <c>InstallProgress</c> 报、备份报状态不报字节、下载报字节）。
/// 队列仍然是唯一的下载执行者，任务中心只负责「汇总给界面看」。
///
/// ## 线程模型
/// 上报可能来自任意线程（安装器有 8 个下载线程）。内部用 <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// 保存任务，快照在锁内复制；事件在<b>调用方线程</b>触发，订阅方自己切 UI 线程。
/// 事件按 <see cref="EventIntervalMs"/> 节流——200ms 一次足够 UI 跟上，又不会淹没渲染。
///
/// ## 与界面的契约
/// 快照按「活跃 → 失败 → 其余已结束」排序，活跃任务内按 <see cref="LauncherTaskInfo.Sequence"/>；
/// 已结束任务最多保留 <see cref="FinishedRetention"/> 条，超出按结束时间淘汰最旧的。
/// </summary>
public sealed class DownloadTaskCenter
{
    /// <summary>事件节流间隔（毫秒）。与 <see cref="DownloadQueue.TaskEventIntervalMs"/> 对齐。</summary>
    public const int EventIntervalMs = 200;

    /// <summary>已结束任务的保留条数。活跃与失败任务不参与淘汰。</summary>
    public const int FinishedRetention = 24;

    /// <summary>快照条数上限。用户不会同时跑 200 个任务，超出的都是残留。</summary>
    public const int MaxTasks = 200;

    private readonly ConcurrentDictionary<string, LauncherTaskInfo> _tasks = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IAppLog? _log;

    private int _nextId;
    private long _lastRaisedMs = -1;

    public DownloadTaskCenter(IAppLog? log = null)
    {
        _log = log;
    }

    /// <summary>任务表有变化。节流到最多 5 次/秒；收尾与注册时强制触发。</summary>
    public event EventHandler? TasksChanged;

    /// <summary>当前是否有活跃任务（运行中或已暂停）。</summary>
    public bool HasActiveTasks => _tasks.Values.Any(task => task.IsActive);

    /// <summary>运行中的任务数。</summary>
    public int ActiveCount => _tasks.Values.Count(task => task.State == LauncherTaskState.Running);

    /// <summary>
    /// 注册一个任务并返回它的上报句柄。
    ///
    /// 调用方拿到句柄后应当：
    /// <list type="bullet">
    /// <item>开始工作时调用 <see cref="ILauncherTaskHandle.Begin"/>；</item>
    /// <item>进度变化时调用 <see cref="ILauncherTaskHandle.Report"/>；</item>
    /// <item>正常结束时调用 <see cref="ILauncherTaskHandle.Complete"/>；</item>
    /// <item>失败时调用 <see cref="ILauncherTaskHandle.Fail"/>。</item>
    /// </list>
    /// 忘记收尾不会卡住队列（这里没有互斥），只是那条任务会一直显示为进行中，
    /// 直到进程退出——所以用 <c>using</c> 或 try/finally 包住。
    /// </summary>
    public ILauncherTaskHandle Register(string title, LauncherTaskKind kind, string detail = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var id = $"task-{Interlocked.Increment(ref _nextId)}-{Guid.NewGuid():N}";
        var task = new LauncherTaskInfo
        {
            Id = id,
            Title = title.Trim(),
            Detail = detail,
            Kind = kind,
            Sequence = _nextId,
        };

        _tasks[id] = task;
        RaiseTasksChanged(force: true);
        return new TaskHandle(this, id);
    }

    /// <summary>当前任务表的快照，按界面展示顺序排好。</summary>
    public IReadOnlyList<LauncherTaskInfo> Snapshot()
    {
        var all = _tasks.Values.ToArray();
        if (all.Length == 0)
        {
            return Array.Empty<LauncherTaskInfo>();
        }

        var active = all.Where(task => task.IsActive).OrderBy(task => task.Sequence);
        var failed = all.Where(task => task.State == LauncherTaskState.Failed)
            .OrderByDescending(task => task.FinishedAt ?? task.StartedAt);
        var finished = all.Where(task => task.State == LauncherTaskState.Completed
                                         || task.State == LauncherTaskState.Canceled)
            .OrderByDescending(task => task.FinishedAt ?? task.StartedAt)
            .Take(FinishedRetention);

        return active.Concat(failed).Concat(finished).Take(MaxTasks).ToArray();
    }

    /// <summary>总速度（只统计运行中的任务）。概览条用。</summary>
    public double TotalSpeed => _tasks.Values
        .Where(task => task.State == LauncherTaskState.Running)
        .Sum(task => task.SpeedBytesPerSecond);

    /// <summary>
    /// 请求取消一个任务。
    ///
    /// 真正的取消动作由发起方持有（安装有 <c>CancellationTokenSource</c>、下载靠批次取消），
    /// 任务中心只把状态改成 <see cref="LauncherTaskState.Canceled"/> 并记录，
    /// 目的是「界面立刻给出反馈」。发起方随后会调用 <see cref="ILauncherTaskHandle.Fail"/> 或
    /// <see cref="ILauncherTaskHandle.Complete"/>，两次收尾以先到的为准。
    /// </summary>
    public void RequestCancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var task))
        {
            return;
        }

        if (task.IsFinished)
        {
            return;
        }

        task.State = LauncherTaskState.Canceled;
        task.FinishedAt = DateTimeOffset.Now;
        RaiseTasksChanged(force: true);
    }

    /// <summary>清掉所有已结束的任务（用户点「清除已完成」）。活跃与失败项保留。</summary>
    public void ClearFinished()
    {
        foreach (var pair in _tasks)
        {
            // 失败项保留：它们是「还能点重试」的入口，清掉就等于把重试按钮一起删了。
            if (pair.Value.IsFinished && pair.Value.State != LauncherTaskState.Failed)
            {
                _tasks.TryRemove(pair.Key, out _);
            }
        }

        RaiseTasksChanged(force: true);
    }

    /// <summary>只重试失败项：返回待重试任务的可读标题，调用方据此重建请求。</summary>
    public IReadOnlyList<string> FailedTaskTitles => _tasks.Values
        .Where(task => task.State == LauncherTaskState.Failed)
        .OrderByDescending(task => task.FinishedAt ?? task.StartedAt)
        .Select(task => task.Title)
        .ToArray();

    private LauncherTaskInfo? Find(string id) => _tasks.TryGetValue(id, out var task) ? task : null;

    private void RaiseTasksChanged(bool force)
    {
        try
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                var elapsed = now - _lastRaisedMs;
                if (!force && _lastRaisedMs >= 0 && elapsed >= 0 && elapsed < EventIntervalMs)
                {
                    return;
                }

                _lastRaisedMs = now;
            }

            TasksChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 订阅方异常不能把上报线程带崩：那个线程可能正在跑安装。
            _log?.Warn("任务表通知失败（订阅方异常已忽略）", ex);
        }
    }

    /// <summary>
    /// 任务的上报句柄。所有方法幂等，且对已收尾的任务静默忽略——
    /// 取消之后残留在途的回调不该把任务从「已取消」改回「运行中」。
    /// </summary>
    private sealed class TaskHandle(DownloadTaskCenter center, string id) : ILauncherTaskHandle
    {
        public string Id => id;

        public void Begin(string? detail = null)
        {
            if (center.Find(id) is not { } task || task.IsFinished)
            {
                return;
            }

            task.State = LauncherTaskState.Running;
            if (detail is not null)
            {
                task.Detail = detail;
            }

            center.RaiseTasksChanged(force: true);
        }

        public void Report(long downloadedBytes, long totalBytes, double speedBytesPerSecond = 0, string? detail = null)
        {
            if (center.Find(id) is not { } task || task.IsFinished)
            {
                return;
            }

            task.DownloadedBytes = Math.Max(0, downloadedBytes);
            if (totalBytes > 0)
            {
                task.TotalBytes = totalBytes;
            }

            task.SpeedBytesPerSecond = Math.Max(0, speedBytesPerSecond);
            if (detail is not null)
            {
                task.Detail = detail;
            }

            center.RaiseTasksChanged(force: false);
        }

        public void ReportDetail(string detail)
        {
            if (center.Find(id) is not { } task || task.IsFinished)
            {
                return;
            }

            task.Detail = detail;
            center.RaiseTasksChanged(force: false);
        }

        public void Pause()
        {
            if (center.Find(id) is not { } task || task.State != LauncherTaskState.Running)
            {
                return;
            }

            task.State = LauncherTaskState.Paused;
            task.SpeedBytesPerSecond = 0;
            center.RaiseTasksChanged(force: true);
        }

        public void Resume()
        {
            if (center.Find(id) is not { } task || task.State != LauncherTaskState.Paused)
            {
                return;
            }

            task.State = LauncherTaskState.Running;
            center.RaiseTasksChanged(force: true);
        }

        public void Complete(string? detail = null)
        {
            if (center.Find(id) is not { } task || task.IsFinished)
            {
                return;
            }

            task.State = LauncherTaskState.Completed;
            task.FinishedAt = DateTimeOffset.Now;
            task.SpeedBytesPerSecond = 0;
            if (task.TotalBytes > 0)
            {
                task.DownloadedBytes = task.TotalBytes;
            }

            if (detail is not null)
            {
                task.Detail = detail;
            }

            center.RaiseTasksChanged(force: true);
        }

        public void Fail(string error)
        {
            if (center.Find(id) is not { } task || task.IsFinished)
            {
                return;
            }

            task.State = LauncherTaskState.Failed;
            task.FinishedAt = DateTimeOffset.Now;
            task.Error = string.IsNullOrWhiteSpace(error) ? "未知错误" : error.Trim();
            task.SpeedBytesPerSecond = 0;
            center.RaiseTasksChanged(force: true);
        }

        public void Dispose()
        {
            // 忘记收尾时兜底：正在跑的任务标成取消，已经结束的不动。
            if (center.Find(id) is { } task && !task.IsFinished)
            {
                task.State = LauncherTaskState.Canceled;
                task.FinishedAt = DateTimeOffset.Now;
                center.RaiseTasksChanged(force: true);
            }
        }
    }
}

/// <summary>
/// 一个已注册任务的上报入口。由 <see cref="DownloadTaskCenter.Register"/> 返回。
///
/// 单独成接口而不是直接暴露内部类：调用方（ViewModel、安装编排）只该看见「上报」这一面，
/// 不该拿到改别人任务的能力。
/// </summary>
public interface ILauncherTaskHandle : IDisposable
{
    /// <summary>任务标识。</summary>
    string Id { get; }

    /// <summary>标记开始。可重复调用。</summary>
    void Begin(string? detail = null);

    /// <summary>上报进度。字节数来自下载层，速度由调用方用 EWMA 算好。</summary>
    void Report(long downloadedBytes, long totalBytes, double speedBytesPerSecond = 0, string? detail = null);

    /// <summary>只更新副行文案（无字节进度的阶段，例如「正在执行安装器…」）。</summary>
    void ReportDetail(string detail);

    /// <summary>暂停（可逆）。</summary>
    void Pause();

    /// <summary>恢复。</summary>
    void Resume();

    /// <summary>正常结束。</summary>
    void Complete(string? detail = null);

    /// <summary>失败。error 会原样显示在结果条上。</summary>
    void Fail(string error);
}
/// <summary>
/// 任务行的展示文案与数字格式（纯函数）。
///
/// 放在 Core 而不是 ViewModel：格式约定是「等宽数字列 48/96/72 不缩窄」这条布局契约的输入，
/// 写错一个单位会让三列数字对不齐；纯函数才能被单测钉住。
/// 颜色与字号仍由 XAML 令牌决定，这里不碰任何 UI 类型。
/// </summary>
public static class LauncherTaskPresentation
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>把字节数格式化成 <c>32.4 MB</c>。1 KB 以下保留整数，其余一位小数。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {ByteUnits[unit]}";
    }

    /// <summary>速度文案。0 或未知显示「—」，不显示 <c>0 B/s</c>（那看起来像卡死）。</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "—";
        }

        return $"{FormatBytes((long)bytesPerSecond)}/s";
    }

    /// <summary>剩余时间文案 <c>02:10</c>；超过一小时显示 <c>1:02:10</c>。未知为「—」。</summary>
    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is not { } value || value <= TimeSpan.Zero)
        {
            return "—";
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    /// <summary>状态徽标文案。</summary>
    public static string DescribeState(LauncherTaskState state) => state switch
    {
        LauncherTaskState.Running => "进行中",
        LauncherTaskState.Paused => "已暂停",
        LauncherTaskState.Completed => "已完成",
        LauncherTaskState.Failed => "失败",
        LauncherTaskState.Canceled => "已取消",
        _ => "排队",
    };

    /// <summary>
    /// 副行文案：「下载模组 · 32.4/52.1 MB · 12 项」。
    ///
    /// 总字节未知（服务端没给 Content-Length）时只说文件数，不编一个 0/
    /// 那个「0 B」会让用户以为一个字节都没动。
    /// </summary>
    /// <param name="stage">阶段词，例如「下载模组」「准备游戏文件」。</param>
    /// <param name="downloaded">已下载字节。</param>
    /// <param name="total">总字节，0 表示未知。</param>
    /// <param name="items">条目数，0 表示未知（非文件类任务）。</param>
    public static string DescribeDetail(string? stage, long downloaded, long total, int items = 0)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(stage))
        {
            parts.Add(stage.Trim());
        }

        if (total > 0)
        {
            parts.Add($"{FormatBytes(downloaded)}/{FormatBytes(total)}");
        }
        else if (downloaded > 0)
        {
            parts.Add(FormatBytes(downloaded));
        }

        if (items > 0)
        {
            parts.Add($"{items} 项");
        }

        return parts.Count == 0 ? "准备中…" : string.Join(" · ", parts);
    }
}
