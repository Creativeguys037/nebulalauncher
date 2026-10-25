using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>一个待下载文件。</summary>
public sealed class DownloadItem
{
    public required string Url { get; init; }

    public required string DestinationPath { get; init; }

    public string Id { get; init; } = string.Empty;

    public string Group { get; init; } = string.Empty;

    /// <summary>预期大小，0 表示未知（会用响应头补齐）。</summary>
    public long ExpectedSize { get; init; }

    /// <summary>预期 SHA1，提供时才校验。</summary>
    public string? ExpectedSha1 { get; init; }

    public string Name => string.IsNullOrEmpty(Id) ? Path.GetFileName(DestinationPath) : Id;
}

public sealed record DownloadFailure(DownloadItem Item, string Error);

/// <summary>
/// 任务快照变化的通知内容。
///
/// 事件本身被节流到最多 5 次/秒（见 <see cref="DownloadQueue.TaskEventIntervalMs"/>）：
/// 3000 个文件如果每个字节块都推一次，UI 线程会被淹没，而用户 200ms 也看不出一帧差别。
/// 处理方应在 UI 线程重建列表（Core 不关心线程亲和性）。
/// </summary>
/// <param name="Tasks">当前批次的逐文件快照（已分组、已截断）。</param>
/// <param name="Summary">最近一次结束的批次摘要；尚未有批次结束时为 null。</param>
public sealed class DownloadTasksChangedEventArgs(IReadOnlyList<DownloadTaskInfo> tasks, DownloadBatchSummary? summary)
    : EventArgs
{
    public IReadOnlyList<DownloadTaskInfo> Tasks { get; } = tasks;

    public DownloadBatchSummary? Summary { get; } = summary;
}

/// <summary>一批下载的整体结果。</summary>
public sealed class DownloadBatchResult
{
    public int TotalFiles { get; init; }

    public int CompletedFiles { get; init; }

    public int SkippedFiles { get; init; }

    public int FailedFiles { get; init; }

    public long TotalBytes { get; init; }

    public long DownloadedBytes { get; init; }

    public IReadOnlyList<DownloadFailure> Failures { get; init; } = Array.Empty<DownloadFailure>();

    public bool Success => FailedFiles == 0;

    public static readonly DownloadBatchResult Empty = new();
}

/// <summary>
/// 外部批次的上报作用域，给「不经过本队列下载、但要出现在任务面板」的链路用
/// （目前是安装游戏本体：<c>NebulaGameInstaller</c> 按自己的并发调度下载，只把逐文件状态回传）。
///
/// 生命周期：
/// <list type="bullet">
/// <item>由 <see cref="DownloadQueue.BeginExternalBatch"/> 创建，同一时刻本队列只允许一个活动批次。</item>
/// <item><see cref="Complete"/> 把这一批写进 <see cref="DownloadQueue.History"/> 并更新
/// <see cref="DownloadQueue.LatestSummary"/>；此后再 <see cref="Report"/> 会被忽略。</item>
/// <item>未 <see cref="Complete"/> 就 <see cref="IDisposable.Dispose"/> 视为取消：不写历史，
/// 快照清空（取消的批次混进历史会让用户以为那批已经结束）。</item>
/// </list>
///
/// 实现方不需要自己加锁：同一个作用域会被安装器的多个下载线程并发调用。
/// </summary>
public interface IDownloadTaskScope : IDisposable
{
    /// <summary>
    /// 上报（或更新）一个文件的状态。同一个 <paramref name="displayName"/> /
    /// <paramref name="destinationPath"/> 重复上报是更新，不是新增。
    /// </summary>
    /// <param name="displayName">面板上显示的短名，例如 <c>1.20.1.json</c>。</param>
    /// <param name="group">分组名，例如 <c>libraries</c> / <c>assets</c>。</param>
    /// <param name="totalBytes">预期字节数；0 表示未知，完成时用已下载字节补齐。</param>
    /// <param name="downloadedBytes">已下载字节数。</param>
    /// <param name="state">当前状态。</param>
    /// <param name="error">失败原因；仅 <see cref="DownloadTaskState.Failed"/> 时有意义。</param>
    /// <param name="url">下载源地址。给了才能「只重试失败项」，安装器自造的文件可为 null。</param>
    /// <param name="destinationPath">落盘路径。给了才能重建重试请求，同时是快照内的稳定键。</param>
    /// <param name="sha1">预期 SHA1，重试时保持与首次下载相同的校验强度。</param>
    void Report(
        string displayName,
        string group,
        long totalBytes,
        long downloadedBytes,
        DownloadTaskState state,
        string? error = null,
        string? url = null,
        string? destinationPath = null,
        string? sha1 = null);

    /// <summary>结束本批次并写入历史。重复调用是空操作。</summary>
    void Complete();
}

/// <summary>
/// 限流下载队列：SemaphoreSlim 控制并发，单个失败最多重试 3 次（指数退避），
/// 进度以聚合形式上报并带 EWMA 速度估计。任何文件的失败都不会让整批抛异常。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "任务约定的公开类型名")]
public sealed class DownloadQueue
{
    private const int MaxAttempts = 3;
    private const int BufferSize = 81920;

    /// <summary>快照里保留的已结束（完成 / 跳过）条目上限。进行中与失败的条目不参与淘汰。</summary>
    public const int FinishedTaskRetention = 24;

    /// <summary>单次快照返回的最大条目数。UI 侧另有一层 200 条的渲染上限。</summary>
    public const int MaxSnapshotTasks = 200;

    /// <summary>内存中保留的批次摘要条数。</summary>
    public const int HistoryCapacity = 20;

    /// <summary>任务快照事件的最小间隔（毫秒）。5 次/秒足够跟上进度，又不会打爆 UI 线程。</summary>
    public const int TaskEventIntervalMs = 200;

    private readonly HttpClient _client;
    private readonly IAppLog? _log;
    private readonly FileReuseService? _reuse;

    /// <summary>保护「当前批次跟踪器」的引用替换；下载线程只读它，替换发生在批次开始处。</summary>
    private readonly object _stateGate = new();

    /// <summary>保护节流时间戳。锁粒度极小，只在真正上报时持有。</summary>
    private readonly object _eventGate = new();

    /// <summary>保护批次历史列表。</summary>
    private readonly object _historyGate = new();

    private readonly List<DownloadBatchSummary> _history = [];
    private BatchTaskTracker? _tracker;
    private DownloadBatchSummary? _latestSummary;

    private long _lastTasksRaisedMs = -1;

    private int _speedLimitKb;

    private int _maxConcurrency = 64;

    /// <summary>
    /// 暂停闸门。关闭时每个下载任务在真正发请求之前挂起。
    ///
    /// 为什么是「闸门 + 可等待」而不是取消：暂停是用户的临时决定，恢复后必须继续下完，
    /// 用取消令牌表达暂停会让在途文件的半成品与重试次数语义一起乱掉
    /// （取消后队列会按「取消的批次」清空快照、不写历史）。
    /// </summary>
    private readonly PauseGate _pauseGate = new();

    /// <summary>0 = 运行中，1 = 已暂停。与闸门同步更新，用 Int 是为了能原子读写。</summary>
    private int _isPaused;

    public DownloadQueue(
        DownloadHttpClientFactory httpClientFactory,
        IAppLog? log = null,
        int maxConcurrency = 64,
        FileReuseService? reuse = null)
        : this((httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory))).Client, log, maxConcurrency, reuse)
    {
    }

    /// <summary>
    /// 直接用现成 <see cref="HttpClient"/> 的构造函数。
    ///
    /// 生产路径走 <see cref="DownloadHttpClientFactory"/>（统一镜像重写与源切换）；
    /// 这个重载是给测试注入假 <c>HttpMessageHandler</c> 用的：限速是「读了多少字节」的
    /// 行为约束，只有把网络层换成可控的流才能稳定复现。
    /// </summary>
    /// <param name="reuse">跨实例文件复用；不传时每个文件都走网络。</param>
    public DownloadQueue(
        HttpClient httpClient,
        IAppLog? log = null,
        int maxConcurrency = 64,
        FileReuseService? reuse = null)
    {
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
        _reuse = reuse;
        MaxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// 单批次的逐文件并发上限。
    ///
    /// 为什么是可写的：它是设置页「并发下载数」那根滑块的下游。改成只读的构造参数时，
    /// 用户在设置里把 64 调到 8（或反过来）对已经建好的单例队列毫无影响——
    /// 设置项看着能改，实际不起作用（票 27 缺陷 B）。
    ///
    /// 与 <see cref="SpeedLimitKb"/> 同样是「批级快照」：只在
    /// <see cref="DownloadAsync"/> 开头读一次去建信号量，批次进行中改只影响下一批。
    /// </summary>
    public int MaxConcurrency
    {
        get => Volatile.Read(ref _maxConcurrency);
        set => Volatile.Write(ref _maxConcurrency, Math.Clamp(value, 1, 256));
    }

    /// <summary>
    /// 速度上限（KB/s），0 表示不限速。每次 <see cref="DownloadAsync"/> 开始时读取一次，
    /// 批次进行中修改只影响下一批——把设置变更做成「批级快照」，
    /// 避免读循环每块都去比对设置对象（读循环是热路径）。
    /// </summary>
    public int SpeedLimitKb
    {
        get => Volatile.Read(ref _speedLimitKb);
        set => Volatile.Write(ref _speedLimitKb, Math.Max(0, value));
    }

    /// <summary>
    /// 是否处于暂停状态。暂停只拦「启动新的文件」：已在途的请求会正常下完，
    /// 不中断、也不抛取消异常。
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _isPaused) != 0;

    /// <summary>当前是否有活动批次（普通批次未收尾，或外部批次未 Complete / Dispose）。</summary>
    public bool HasActiveBatch => CurrentTracker() is { IsClosed: false };

    /// <summary>
    /// 当前活动批次是否是外部批次（安装链：<see cref="BeginExternalBatch"/>）。
    /// 外部批次不接受暂停，界面要据此把「暂停 / 继续」置灰并说明原因。
    /// </summary>
    public bool IsExternalBatchActive => CurrentTracker() is { IsClosed: false, IsExternal: true };

    /// <summary>
    /// 暂停下载：之后不再启动新文件，已在途的文件下完即停。
    ///
    /// 幂等；没有活动批次时调用不抛——下一次批次开始时闸门会被强制放行
    /// （暂停是「这一批」的状态，跨批次保留会让下一次下载永久卡住）。
    ///
    /// 外部批次（安装链）不接受暂停：安装器按自己的调度跑，闸门对它没有意义，
    /// 这里静默忽略而不是抛异常——界面已经把按钮禁用了，命令仍可能被快捷键或宿主调到。
    /// </summary>
    public void Pause()
    {
        if (IsExternalBatchActive)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isPaused, 1) == 1)
        {
            return;
        }

        _pauseGate.Close();
    }

    /// <summary>继续：立刻放行所有停在闸门前的文件。幂等；无批次时调用不抛。</summary>
    public void Resume()
    {
        if (Interlocked.Exchange(ref _isPaused, 0) == 0)
        {
            return;
        }

        _pauseGate.Open();
    }

    /// <summary>
    /// 新批次开始时强制放行。三条收尾路径（完成 / 取消 / 异常）也走这里，
    /// 否则上一次暂停会把闸门永久留在关闭态，下一次下载一个文件都启动不了。
    /// </summary>
    private void ResetPause()
    {
        Interlocked.Exchange(ref _isPaused, 0);
        _pauseGate.Open();
    }

    /// <summary>
    /// 逐文件任务快照发生变化。节流到最多 5 次/秒，批次收尾时强制再发一次。
    ///
    /// 只暴露「最近一个批次」：并发跑两个批次时后开始的覆盖前一个的快照。
    /// 这是刻意的——任务面板的价值是「当前这批在下载什么」，把多个并发批次的
    /// 3000 个条目合在一起既难读，也会让 UI 的 200 条上限变成随机抽样。
    /// </summary>
    public event EventHandler<DownloadTasksChangedEventArgs>? TasksChanged;

    /// <summary>一个批次正常结束时触发（取消与异常不写历史）。以摘要为参数，方便只订阅历史的地方少读一次快照。</summary>
    public event EventHandler<DownloadBatchSummary>? BatchCompleted;

    /// <summary>最近一个批次的逐文件快照，按 Group 分组、按声明顺序排列。</summary>
    /// <param name="maxTasks">返回条数上限；0 或负数表示用 <see cref="MaxSnapshotTasks"/>。</param>
    public IReadOnlyList<DownloadTaskInfo> Snapshot(int maxTasks = MaxSnapshotTasks)
    {
        var tracker = CurrentTracker();
        return tracker?.BuildSnapshot(maxTasks > 0 ? maxTasks : MaxSnapshotTasks)
               ?? Array.Empty<DownloadTaskInfo>();
    }

    /// <summary>内存中的批次历史，最新的在前。最多 <see cref="HistoryCapacity"/> 条，不落盘。</summary>
    public IReadOnlyList<DownloadBatchSummary> History
    {
        get
        {
            lock (_historyGate)
            {
                if (_history.Count == 0)
                {
                    return Array.Empty<DownloadBatchSummary>();
                }

                var snapshot = new DownloadBatchSummary[_history.Count];
                for (var i = 0; i < _history.Count; i++)
                {
                    snapshot[i] = _history[_history.Count - 1 - i];
                }

                return snapshot;
            }
        }
    }

    /// <summary>最近一次正常结束的批次摘要；还没有批次结束时为 null。</summary>
    public DownloadBatchSummary? LatestSummary => Volatile.Read(ref _latestSummary);

    /// <summary>
    /// 开始一个「外部批次」：调用方自己不经过 <see cref="DownloadAsync"/>，
    /// 而是通过返回的作用域逐文件上报状态（安装游戏本体走的就是这条路）。
    ///
    /// 作用域生效期间：<see cref="Snapshot"/> / <see cref="TasksChanged"/> / <see cref="FailedTasks"/>
    /// 都按本批次的文件工作，与普通批次完全一致。
    ///
    /// 互斥：同一时刻本队列只允许一个**活动**批次。当前已有批次在跑（普通批次未收尾，
    /// 或上一个外部批次未 <see cref="IDownloadTaskScope.Complete"/> / Dispose）时抛
    /// <see cref="InvalidOperationException"/>；反过来，外部批次活动期间调用
    /// <see cref="DownloadAsync"/> 也抛同样的异常。
    ///
    /// 这不是新限制，而是把「并发批次互相覆盖快照」这条既有假设显式化：
    /// 3000 个文件的两个批次合进一个面板既难读，也会让 200 条上限变成随机抽样。
    /// </summary>
    /// <param name="name">批次名，显示在批次历史里，例如 <c>安装 1.20.1</c>。空白时用「外部批次」。</param>
    /// <exception cref="InvalidOperationException">已有活动批次（普通或外部）。</exception>
    public IDownloadTaskScope BeginExternalBatch(string name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "外部批次" : name.Trim();
        var tracker = new BatchTaskTracker(label, external: true);
        tracker.Changed += RaiseTasksChanged;

        lock (_stateGate)
        {
            if (_tracker is { IsClosed: false })
            {
                throw new InvalidOperationException(
                    "同一时刻只允许一个下载批次：当前已有批次在进行，等它结束后再开始新的批次。");
            }

            _tracker = tracker;
        }

        // 外部批次虽然不接受暂停，闸门仍要复位：上一个普通批次留下的暂停态不能被带进来，
        // 否则安装结束后紧接着的普通下载会带着「已暂停」的错觉启动。
        ResetPause();

        // 空批次也要推一次：面板从上一个批次的残影切成空态。
        RaiseTasksChanged(force: true);
        return new ExternalTaskScope(this, tracker);
    }

    /// <summary>
    /// 可重试的失败项：优先取当前批次里已失败的条目，否则取最近一次批次的失败项。
    /// 只返回 URL 与落盘路径都齐备的条目——缺任何一项都无法重建下载请求。
    /// </summary>
    public IReadOnlyList<DownloadTaskInfo> FailedTasks
    {
        get
        {
            var failed = CurrentTracker()?.BuildFailedTasks() ?? Array.Empty<DownloadTaskInfo>();
            if (failed.Length > 0)
            {
                return failed;
            }

            return LatestSummary?.FailedTasks ?? Array.Empty<DownloadTaskInfo>();
        }
    }

    /// <summary>进度曲线的展示下限：开头 5% 留给「连接 / 首字节」，不让进度条从 0 卡住。</summary>
    private const double ProgressFloor = 0.05;

    /// <summary>进度曲线的展示上限：剩下的 2% 留给收尾（校验 / 落盘 / 改名）。</summary>
    private const double ProgressCeiling = 0.98;

    /// <summary>曲线的幂指数。取值来自 PCL2 的 <c>1-(1-raw)^0.9</c>。</summary>
    private const double ProgressExponent = 0.9;

    /// <summary>
    /// 把「实际字节比例」映射成「展示比例」，值域 <see cref="ProgressFloor"/>～<see cref="ProgressCeiling"/>。
    ///
    /// 直接采用 PCL2 的 <c>NetFile.Progress</c>（B §1.6）：<c>1-(1-raw)^0.9</c> 再乘 0.93 加 0.05。
    /// 形状是「先慢后快」：raw=0.5 只显示约 48%，最后 10% 的实际进度会迅速填满进度条。
    /// 这是刻意的——下载卡在 99% 比卡在 95% 更让人焦虑，把收敛放在末端观感更好。
    ///
    /// 早期版本写过 <c>raw^0.9</c>（先快后慢），是为了凑一条「raw=0.5 必须大于 0.5」的验收判据；
    /// 那条判据本身是错的，已按 PCL2 原式改正。
    ///
    /// 只影响上报给 <see cref="IProgress{T}"/> 的 <c>Ratio</c>，不改任何字节计数。
    /// </summary>
    public static double ApplyProgressCurve(double raw)
    {
        if (double.IsNaN(raw) || raw <= 0)
        {
            return ProgressFloor;
        }

        if (raw >= 1)
        {
            return ProgressCeiling;
        }

        return ProgressFloor + ((ProgressCeiling - ProgressFloor) * (1 - Math.Pow(1 - raw, ProgressExponent)));
    }

    public async Task<DownloadBatchResult> DownloadAsync(
        IReadOnlyList<DownloadItem> items,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 新批次开始时闸门必须是放行态：暂停属于「上一批」，留着会让这一批卡死。
        // 代价是「暂停中再发起一次下载」会自动恢复——这比无声卡住更符合预期。
        ResetPause();

        if (items.Count == 0)
        {
            progress?.Report(DownloadProgress.Empty);
            return DownloadBatchResult.Empty;
        }

        var tracker = new BatchTaskTracker(ResolveBatchName(items), items);
        tracker.Changed += RaiseTasksChanged;
        lock (_stateGate)
        {
            // 外部批次（安装链）活动时不抢它的快照：安装中途弹一次「重试失败项」
            // 或跑一次在线内容下载，都会让安装面板的 3000 行被替换成别的批次。
            if (_tracker is { IsExternal: true, IsClosed: false })
            {
                throw new InvalidOperationException(
                    "安装批次正在进行，不能同时启动普通下载批次。等安装结束后再试。");
            }

            _tracker = tracker;
        }

        var counters = new BatchCounters(items);
        var reporter = new ProgressReporter(counters, progress, () => IsPaused);
        reporter.Report(force: true);
        RaiseTasksChanged(force: true);

        DownloadBatchResult result;
        try
        {
            result = await DispatchAsync(items, counters, reporter, tracker, respectPause: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // 取消或意外异常：已经跑出来的状态仍然推给 UI，但不写历史——
            // 「取消的批次」混进历史列表会让用户以为那批已经结束。
            // 标记收尾：否则这个批次的残影会一直占着「活动批次」的位置，
            // 让下一次安装（外部批次）被互斥检查拒掉。
            tracker.MarkClosed();
            ResetPause();
            RaiseTasksChanged(force: true);
            throw;
        }

        reporter.Report(force: true);

        CompleteBatch(tracker, result);
        ResetPause();
        return result;
    }

    /// <summary>下载单个文件，成功返回 true。</summary>
    public async Task<bool> DownloadFileAsync(
        DownloadItem item,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DownloadAsync([item], progress, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>
    /// 旁路下载：照常下载与计数，但**不占用任务面板、不写批次历史**，
    /// 因此可以与安装的外部批次并发进行（票 27 缺陷 D）。
    ///
    /// ## 为什么需要它
    /// 安装期间队列被 <see cref="BeginExternalBatch"/> 独占，普通 <see cref="DownloadAsync"/>
    /// 直接抛「安装批次正在进行」。而被安装过程自己需要的下载恰好走的是普通批次：
    /// Java 运行时（26.1.2 需要 Java 25+）、authlib-injector。于是形成死锁式体验——
    /// 不先退出安装就装不了 Java，不装 Java 就装不完游戏。日志里同一条
    /// 「安装批次正在进行」连撞四次，就是这个形状。
    ///
    /// ## 为什么是「不抢快照」而不是「允许并发覆盖快照」
    /// 安装面板那 3000 行是用户唯一能看懂的进度。让 Java 的 30 个文件把面板换掉，
    /// 用户会以为安装进度丢了（见 <see cref="BeginExternalBatch"/> 的说明与
    /// <see cref="DownloadAsync"/> 的互斥检查）。旁路批次干脆不进面板：
    /// 它只有几十个文件、几秒钟，进度由调用方自己用 <paramref name="progress"/> 消化。
    ///
    /// ## 边界
    /// 仍然受并发上限与限速约束（同一个设置、同一份网络）；仍然会被取消令牌中断；
    /// 但**不改变**暂停状态——暂停是「这一批普通下载」的语义，旁路不该替用户按暂停或恢复。
    /// </summary>
    /// <param name="items">待下载文件。</param>
    /// <param name="progress">聚合进度；旁路不产生逐文件快照，调用方只能从这里看进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<DownloadBatchResult> DownloadBypassAsync(
        IReadOnlyList<DownloadItem> items,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            progress?.Report(DownloadProgress.Empty);
            return DownloadBatchResult.Empty;
        }

        // 局部 tracker：只为让 RunOneAsync 的既有实现原样复用（它按条目记状态）。
        // 不赋给 _tracker、不订阅 Changed，所以面板与历史都看不到这一批。
        var tracker = new BatchTaskTracker(ResolveBatchName(items), items);

        var counters = new BatchCounters(items);
        var reporter = new ProgressReporter(counters, progress, () => IsPaused);

        // 正常结束不写历史、不发快照：这两件事都由 _tracker 触发，而旁路从不接管它。
        var result = await DispatchAsync(items, counters, reporter, tracker, respectPause: false, cancellationToken)
            .ConfigureAwait(false);

        reporter.Report(force: true);
        return result;
    }

    /// <summary>
    /// 两批共用的调度内核：建并发闸门与限速器，把每个条目派给 <see cref="RunOneAsync"/>，等全部收尾。
    ///
    /// 抽出来是因为普通批次与旁路批次在这件事上只有「要不要受暂停约束」一个差别；
    /// 各写一份的话，将来给下载循环加约束（比如按主机限流）只改一处就会漏掉另一处。
    /// </summary>
    private async Task<DownloadBatchResult> DispatchAsync(
        IReadOnlyList<DownloadItem> items,
        BatchCounters counters,
        ProgressReporter reporter,
        BatchTaskTracker tracker,
        bool respectPause,
        CancellationToken cancellationToken)
    {
        // 限速=0 时 limiter 为 null，读循环里只剩一次 null 判断，不进入令牌计算。
        var speedLimitKb = SpeedLimitKb;
        var limiter = speedLimitKb > 0 ? new SpeedLimiter(speedLimitKb) : null;

        using var throttle = new SemaphoreSlim(MaxConcurrency);

        var tasks = new Task[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            tasks[i] = RunOneAsync(
                items[i], counters, reporter, tracker, throttle, limiter, cancellationToken, respectPause);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return counters.ToResult();
    }

    /// <summary>
    /// 按需等待暂停闸门。
    ///
    /// 旁路批次（<see cref="DownloadBypassAsync"/>）不参与暂停语义：它的典型调用者
    /// 是「安装过程中要装 Java」，而安装牌的外部批次本来就拒绝暂停
    /// （见 <see cref="Pause"/>），替用户把旁路也挂住只会让安装卡在缺 Java 上。
    /// </summary>
    private Task WaitPauseAsync(bool respectPause, CancellationToken cancellationToken) =>
        respectPause ? _pauseGate.WaitAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// 把一批任务快照排成「可直接绑定到列表」的顺序：
    /// Group 为主键（同一批文件按来源分组，界面上一眼能看出是 assets 还是 libraries），
    /// Sequence 为次键（声明顺序稳定，状态变化不会让行上下跳）。
    ///
    /// 已结束（完成 / 跳过）的条目只保留最近 <paramref name="finishedRetention"/> 条：
    /// 3000 个文件如果全留着，用户永远看不到正在下载的那几条。
    /// 进行中与失败项不参与淘汰——失败项正是用户要看的。
    /// </summary>
    /// <param name="maxTasks">总条数上限；0 或负数表示不限。超出时按「失败 → 进行中 → 其余」的优先级保留。</param>
    public static IReadOnlyList<DownloadTaskInfo> OrderTasks(
        IEnumerable<DownloadTaskInfo> tasks,
        int finishedRetention = FinishedTaskRetention,
        int maxTasks = MaxSnapshotTasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var active = new List<DownloadTaskInfo>();
        var finished = new List<DownloadTaskInfo>();

        foreach (var task in tasks)
        {
            if (task is null)
            {
                continue;
            }

            if (task.IsFinished)
            {
                finished.Add(task);
            }
            else
            {
                active.Add(task);
            }
        }

        // 「最近完成」= Sequence 最大的若干条；取完再按原顺序放回，避免组内倒序。
        var retainedFinished = finished
            .OrderByDescending(task => task.Sequence)
            .Take(Math.Max(0, finishedRetention))
            .OrderBy(task => task.Sequence);

        active.AddRange(retainedFinished);

        var ordered = active
            .OrderBy(task => task.Group, StringComparer.Ordinal)
            .ThenBy(task => task.Sequence)
            .ToList();

        if (maxTasks <= 0 || ordered.Count <= maxTasks)
        {
            return ordered;
        }

        var capped = new List<DownloadTaskInfo>(maxTasks);
        capped.AddRange(ordered.Where(task => task.IsFailed).Take(maxTasks));
        if (capped.Count < maxTasks)
        {
            capped.AddRange(ordered
                .Where(task => task.State == DownloadTaskState.Running)
                .Take(maxTasks - capped.Count));
        }

        if (capped.Count < maxTasks)
        {
            capped.AddRange(ordered
                .Where(task => !task.IsFailed && task.State != DownloadTaskState.Running)
                .Take(maxTasks - capped.Count));
        }

        return capped
            .OrderBy(task => task.Group, StringComparer.Ordinal)
            .ThenBy(task => task.Sequence)
            .ToList();
    }

    /// <summary>
    /// 把失败项重建成本次可重试的下载条目（「只重试失败的文件」，不重跑整个安装）。
    ///
    /// 过滤规则：
    /// - 状态必须是 <see cref="DownloadTaskState.Failed"/>；
    /// - URL 与落盘路径都必须存在，否则无法重建请求；
    /// - 同一路径只保留一条（批次里可能有重复条目）。
    /// 预期大小沿用首次快照：它可能来自响应头，比原始清单更准。
    /// </summary>
    public static IReadOnlyList<DownloadItem> BuildRetryItems(IEnumerable<DownloadTaskInfo> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var items = new List<DownloadItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            if (task is null || !task.IsFailed)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(task.Url) || string.IsNullOrWhiteSpace(task.DestinationPath))
            {
                continue;
            }

            if (!seen.Add(task.DestinationPath))
            {
                continue;
            }

            items.Add(new DownloadItem
            {
                Url = task.Url,
                DestinationPath = task.DestinationPath,
                Id = task.Id,
                Group = task.Group,
                ExpectedSize = task.TotalBytes > 0 ? task.TotalBytes : 0,
                ExpectedSha1 = string.IsNullOrWhiteSpace(task.Sha1) ? null : task.Sha1,
            });
        }

        return items;
    }

    /// <summary>
    /// 节流判定：距上次上报不足 <see cref="TaskEventIntervalMs"/> 时不发；<paramref name="force"/> 时无条件发。
    ///
    /// 抽成纯函数是为了能在不 sleep 的前提下单测节流本身——时间源由调用方传入。
    /// </summary>
    internal static bool ShouldRaiseTasksChanged(
        long lastRaisedMs,
        long nowMs,
        bool force,
        int intervalMs = TaskEventIntervalMs)
    {
        if (force)
        {
            return true;
        }

        if (lastRaisedMs < 0)
        {
            return true;
        }

        var elapsed = nowMs - lastRaisedMs;

        // 时钟回退（或 TickCount 回绕）时按「到期」处理：宁可多推一次，也不要永远不推。
        return elapsed >= intervalMs || elapsed < 0;
    }

    /// <summary>批次名：取第一个非空 Group。安装器、修复、整合包都把人类可读名放在 Group 上。</summary>
    private static string ResolveBatchName(IReadOnlyList<DownloadItem> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Group))
            {
                return item.Group;
            }
        }

        return "下载任务";
    }

    private BatchTaskTracker? CurrentTracker()
    {
        lock (_stateGate)
        {
            return _tracker;
        }
    }

    /// <summary>按节流间隔把快照发给订阅方。异常只写日志，不允许订阅方把下载循环带崩。</summary>
    private void RaiseTasksChanged(bool force)
    {
        try
        {
            lock (_eventGate)
            {
                var now = Environment.TickCount64;
                if (!ShouldRaiseTasksChanged(_lastTasksRaisedMs, now, force))
                {
                    return;
                }

                _lastTasksRaisedMs = now;
            }

            TasksChanged?.Invoke(this, new DownloadTasksChangedEventArgs(Snapshot(), LatestSummary));
        }
        catch (Exception ex)
        {
            _log?.Warn("下载任务快照通知失败（订阅方异常已忽略）", ex);
        }
    }

    private void CompleteBatch(BatchTaskTracker tracker, DownloadBatchResult result)
    {
        tracker.MarkClosed();
        PublishBatch(tracker.Complete(result));
    }

    /// <summary>外部批次正常收尾：计数由 tracker 自己按条目状态算，不需要调用方提供结果对象。</summary>
    private void CompleteExternalBatch(BatchTaskTracker tracker)
    {
        CompleteBatch(tracker, tracker.BuildResultFromTasks());
    }

    /// <summary>
    /// 外部批次被取消（未 <see cref="IDownloadTaskScope.Complete"/> 就 Dispose）：
    /// 不写历史，清掉快照。清空的理由是面板不该留下一个「看起来还在跑」的批次；
    /// 用户取消安装时想看已完成文件没有意义，重试也不是从面板发起的。
    /// </summary>
    private void CancelExternalBatch(BatchTaskTracker tracker)
    {
        tracker.MarkClosed();

        lock (_stateGate)
        {
            if (ReferenceEquals(_tracker, tracker))
            {
                _tracker = null;
            }
        }

        RaiseTasksChanged(force: true);
    }

    /// <summary>写历史、更新最近摘要、推事件。普通批次与外部批次共用这一段收尾。</summary>
    private void PublishBatch(DownloadBatchSummary summary)
    {
        lock (_historyGate)
        {
            _history.Add(summary);
            while (_history.Count > HistoryCapacity)
            {
                _history.RemoveAt(0);
            }
        }

        Volatile.Write(ref _latestSummary, summary);
        RaiseTasksChanged(force: true);

        try
        {
            BatchCompleted?.Invoke(this, summary);
        }
        catch (Exception ex)
        {
            _log?.Warn("批次完成通知失败（订阅方异常已忽略）", ex);
        }
    }

    private async Task RunOneAsync(
        DownloadItem item,
        BatchCounters counters,
        ProgressReporter reporter,
        BatchTaskTracker tracker,
        SemaphoreSlim throttle,
        SpeedLimiter? limiter,
        CancellationToken cancellationToken,
        bool respectPause = true)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 暂停闸门放在并发槽之后：这条路径覆盖「跳过 / 复用 / 下载」全部出口，
            // 而闸门本身不占用任何资源，暂停期间只是让这些任务停在原地。
            // 取消令牌照常生效——暂停中取消要能立刻收尾（见 DownloadAsync 的 catch）。
            await WaitPauseAsync(respectPause, cancellationToken).ConfigureAwait(false);

            if (IsUpToDate(item))
            {
                var skippedBytes = item.ExpectedSize > 0 ? item.ExpectedSize : SafeLength(item.DestinationPath);
                counters.MarkSkipped(skippedBytes);
                tracker.MarkSkipped(item, skippedBytes);
                reporter.Report(item.Name);
                return;
            }

            // 发请求之前先问本机别处有没有同内容文件：命中的话复制比下载便宜得多。
            // 必须放在 IsUpToDate 之后——目标已经正确时连复制都不需要。
            if (_reuse is not null
                && await _reuse.TryReuseAsync(item, cancellationToken).ConfigureAwait(false))
            {
                var reusedBytes = item.ExpectedSize > 0 ? item.ExpectedSize : SafeLength(item.DestinationPath);
                counters.MarkSkipped(reusedBytes);
                tracker.MarkSkipped(item, reusedBytes);
                reporter.Report(item.Name);
                return;
            }

            string? lastError = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 重试之间也要过闸门：暂停恰好发生在退避等待里时，
                // 下一次尝试不能绕过暂停直接发请求（那样「暂停后不再启动新文件」就有例外）。
                await WaitPauseAsync(respectPause, cancellationToken).ConfigureAwait(false);

                try
                {
                    // 每个 attempt 重新从 0 计字节：上一次尝试写了一半的 .part 会被覆盖，
                    // 让单个文件的进度条跟着重来，而不是停在 100% 再跳回去。
                    tracker.MarkRunning(item);
                    await DownloadAttemptAsync(item, counters, reporter, tracker, limiter, cancellationToken).ConfigureAwait(false);
                    counters.MarkCompleted();
                    tracker.MarkCompleted(item);
                    reporter.Report(item.Name);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    _log?.Warn($"下载失败（第 {attempt}/{MaxAttempts} 次）：{item.Url}", ex);
                    if (attempt < MaxAttempts)
                    {
                        var backoff = TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1)));
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            counters.MarkFailed(new DownloadFailure(item, lastError ?? "未知错误"));
            tracker.MarkFailed(item, lastError ?? "未知错误");
            reporter.Report(item.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task DownloadAttemptAsync(
        DownloadItem item,
        BatchCounters counters,
        ProgressReporter reporter,
        BatchTaskTracker tracker,
        SpeedLimiter? limiter,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(item.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partPath = item.DestinationPath + ".part";
        using var response = await _client
            .GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? item.ExpectedSize;
        if (contentLength > 0)
        {
            counters.ClaimSize(item, contentLength);
            tracker.SetTotalBytes(item, contentLength);
        }

        var alreadyCounted = 0L;
        using var sha1 = item.ExpectedSha1 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                // 先等额度再读：反过来「先读后等」会让 HTTP 管道提前把数据灌进来，
                // 限速只在读循环内部生效，网络侧的突发仍然存在。
                if (limiter is not null)
                {
                    await limiter.AcquireAsync(BufferSize, cancellationToken).ConfigureAwait(false);
                }

                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (limiter is not null)
                {
                    // 按整块预扣、按实际字节结算：小块读取不该按 80 KiB 计费。
                    limiter.Refund(BufferSize - read);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sha1?.AppendData(buffer, 0, read);
                alreadyCounted += read;
                counters.AddBytes(read);
                tracker.AddBytes(item, read);
                reporter.Report(item.Name);
            }
        }

        if (sha1 is not null && item.ExpectedSha1 is { Length: > 0 } expected)
        {
            var actual = Convert.ToHexString(sha1.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partPath);
                counters.SubtractBytes(alreadyCounted);
                tracker.SubtractBytes(item, alreadyCounted);
                throw new InvalidDataException($"校验失败：期望 {expected}，实际 {actual}");
            }
        }

        File.Move(partPath, item.DestinationPath, overwrite: true);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "Mojang 资源校验本身使用 SHA1，必须与官方一致")]
    private static bool IsUpToDate(DownloadItem item)
    {
        if (!File.Exists(item.DestinationPath))
        {
            return false;
        }

        var length = SafeLength(item.DestinationPath);
        if (item.ExpectedSize > 0 && length != item.ExpectedSize)
        {
            return false;
        }

        if (item.ExpectedSha1 is { Length: > 0 } expected)
        {
            try
            {
                using var stream = File.OpenRead(item.DestinationPath);
                var hash = Convert.ToHexString(SHA1.HashData(stream));
                return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }

        return item.ExpectedSize > 0;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 残留 .part 不影响下次下载（会被覆盖）。
        }
    }

    /// <summary>
    /// 单个批次的逐文件状态表。
    ///
    /// 单独成类的原因：<see cref="BatchCounters"/> 管聚合计数、<see cref="ProgressReporter"/> 管速度曲线，
    /// 逐文件状态是第三件事。混在一起后「哪条失败、失败原因、URL」很快会被聚合逻辑吃掉——
    /// 这正是之前 3000 个文件只能看到一句错误的原因。
    ///
    /// 并发：每个 <see cref="DownloadItem"/> 只被一个下载任务处理，所以条目是「单写者」；
    /// 读快照时在锁内复制数组，排序在锁外做（排序是热路径里最贵的一步）。
    ///
    /// 两种模式共用同一个类：
    /// <list type="bullet">
    /// <item>普通批次：构造时给定完整文件清单，条目由 <c>Mark*</c> 驱动（单写者）。</item>
    /// <item>外部批次：清单一开始为空，条目由 <see cref="Report"/> 按需创建（安装器逐文件回传，
    /// 8 个下载线程并发上报，全部在锁内完成「查找或创建 + 更新」）。</item>
    /// </list>
    /// </summary>
    private sealed class BatchTaskTracker
    {
        private readonly object _gate = new();
        private readonly List<DownloadTaskInfo> _tasks;
        private readonly Dictionary<string, DownloadTaskInfo> _byKey;
        private readonly string _name;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

        private int _nextSequence;
        private volatile bool _closed;

        /// <summary>外部批次：条目按需创建，不预先占用清单。</summary>
        public BatchTaskTracker(string name, bool external)
        {
            _name = name;
            IsExternal = external;
            _tasks = new List<DownloadTaskInfo>(external ? 16 : 0);
            _byKey = new Dictionary<string, DownloadTaskInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public BatchTaskTracker(string name, IReadOnlyList<DownloadItem> items)
            : this(name, external: false)
        {
            _tasks.Capacity = items.Count;
            _byKey.EnsureCapacity(items.Count);

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var task = new DownloadTaskInfo
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? item.Name : item.Id,
                    Name = item.Name,
                    Group = item.Group,
                    TotalBytes = Math.Max(0L, item.ExpectedSize),
                    Url = item.Url,
                    DestinationPath = item.DestinationPath,
                    Sha1 = item.ExpectedSha1,
                    Sequence = i,
                };

                _tasks.Add(task);

                // 同一落盘路径只留第一条：清单里重复条目会导致两个任务写同一文件，
                // 快照里出现两条同名行对用户没有意义。
                _byKey.TryAdd(task.Key, task);
            }

            _nextSequence = items.Count;
        }

        /// <summary>是否由 <see cref="DownloadQueue.BeginExternalBatch"/> 创建。</summary>
        public bool IsExternal { get; }

        /// <summary>批次是否已收尾（完成或取消）。收尾后的批次不再算「活动批次」。</summary>
        public bool IsClosed => _closed;

        public void MarkClosed() => _closed = true;

        /// <summary>任一条目变化时回调。参数 true 表示强制上报，忽略节流。</summary>
        public event Action<bool>? Changed;

        public void MarkRunning(DownloadItem item)
        {
            lock (_gate)
            {
                var task = Find(item);
                if (task is null)
                {
                    return;
                }

                task.State = DownloadTaskState.Running;
                task.Error = null;

                // 每次尝试从 0 计：上一次写了半个 .part 会被覆盖，进度条跟着重来。
                task.DownloadedBytes = 0;
            }

            Changed?.Invoke(false);
        }

        public void MarkSkipped(DownloadItem item, long bytes)
        {
            lock (_gate)
            {
                var task = Find(item);
                if (task is null)
                {
                    return;
                }

                task.State = DownloadTaskState.Skipped;
                task.Error = null;
                task.DownloadedBytes = Math.Max(0L, bytes);
            }

            Changed?.Invoke(false);
        }

        public void MarkCompleted(DownloadItem item)
        {
            lock (_gate)
            {
                var task = Find(item);
                if (task is null)
                {
                    return;
                }

                task.State = DownloadTaskState.Completed;
                task.Error = null;

                // 服务端没给 Content-Length 时，TotalBytes 可能是 0；用实际字节补齐，
                // 这样 3000 条列表里不会出现「完成但 0%」。
                if (task.TotalBytes <= 0)
                {
                    task.TotalBytes = task.DownloadedBytes;
                }
            }

            Changed?.Invoke(false);
        }

        public void MarkFailed(DownloadItem item, string error)
        {
            lock (_gate)
            {
                var task = Find(item);
                if (task is null)
                {
                    return;
                }

                task.State = DownloadTaskState.Failed;
                task.Error = error;
            }

            Changed?.Invoke(false);
        }

        public void SetTotalBytes(DownloadItem item, long total)
        {
            if (total <= 0)
            {
                return;
            }

            lock (_gate)
            {
                var task = Find(item);
                if (task is null || task.TotalBytes >= total)
                {
                    return;
                }

                task.TotalBytes = total;
            }
        }

        public void AddBytes(DownloadItem item, long count)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_gate)
            {
                var task = Find(item);
                if (task is null)
                {
                    return;
                }

                task.DownloadedBytes += count;
            }

            Changed?.Invoke(false);
        }

        public void SubtractBytes(DownloadItem item, long count)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_gate)
            {
                var task = Find(item);
                if (task is null)
                {
                    return;
                }

                task.DownloadedBytes = Math.Max(0L, task.DownloadedBytes - count);
            }
        }

        /// <summary>
        /// 外部批次的唯一写入口：按 <paramref name="destinationPath"/>（没有则按显示名）
        /// 查找条目，不存在就创建，然后应用状态。安装器的 8 个下载线程会并发调用。
        ///
        /// 状态语义与普通批次的 <c>Mark*</c> 保持一致：
        /// - <see cref="DownloadTaskState.Running"/> 清掉上一次尝试的错误，字节数从调用方给的 0 计；
        /// - 完成 / 跳过时若总量仍未知，用已下载字节补齐，避免 3000 条里出现「完成但 0%」；
        /// - 失败保留错误文本，供面板展开与「只重试失败项」使用。
        /// </summary>
        public void Report(
            string displayName,
            string group,
            long totalBytes,
            long downloadedBytes,
            DownloadTaskState state,
            string? error,
            string? url,
            string? destinationPath,
            string? sha1)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            var key = string.IsNullOrWhiteSpace(destinationPath) ? displayName : destinationPath;

            lock (_gate)
            {
                if (!_byKey.TryGetValue(key, out var task))
                {
                    task = new DownloadTaskInfo
                    {
                        Id = displayName,
                        Name = displayName,
                        Group = string.IsNullOrWhiteSpace(group) ? "游戏文件" : group,
                        Url = string.IsNullOrWhiteSpace(url) ? null : url,
                        DestinationPath = string.IsNullOrWhiteSpace(destinationPath) ? null : destinationPath,
                        Sha1 = string.IsNullOrWhiteSpace(sha1) ? null : sha1,
                        Sequence = _nextSequence++,
                    };

                    _tasks.Add(task);
                    _byKey[key] = task;
                }

                if (totalBytes > 0)
                {
                    task.TotalBytes = totalBytes;
                }

                task.DownloadedBytes = Math.Max(0L, downloadedBytes);
                task.State = state;
                task.Error = state == DownloadTaskState.Failed
                    ? (string.IsNullOrWhiteSpace(error) ? "未知错误" : error)
                    : null;

                if (state is DownloadTaskState.Completed or DownloadTaskState.Skipped
                    && task.TotalBytes <= 0)
                {
                    task.TotalBytes = task.DownloadedBytes;
                }
            }

            Changed?.Invoke(false);
        }

        public IReadOnlyList<DownloadTaskInfo> BuildSnapshot(int maxTasks)
        {
            DownloadTaskInfo[] copy;
            lock (_gate)
            {
                copy = _tasks.ToArray();
            }

            return DownloadQueue.OrderTasks(copy, DownloadQueue.FinishedTaskRetention, maxTasks);
        }

        public DownloadTaskInfo[] BuildFailedTasks()
        {
            lock (_gate)
            {
                return _tasks.Where(task => task.IsFailed).ToArray();
            }
        }

        /// <summary>
        /// 按条目状态算出外部批次的结果对象（普通批次的计数来自 <see cref="BatchCounters"/>，
        /// 外部批次没有那个东西）。
        ///
        /// <see cref="DownloadBatchResult.CompletedFiles"/> 沿用队列既有语义「完成项含跳过」，
        /// 否则摘要的 <see cref="DownloadBatchSummary.IsClosed"/> 永远不成立。
        /// </summary>
        public DownloadBatchResult BuildResultFromTasks()
        {
            lock (_gate)
            {
                var completed = 0;
                var skipped = 0;
                var failed = 0;
                var totalBytes = 0L;
                var downloadedBytes = 0L;
                var failures = new List<DownloadFailure>();

                foreach (var task in _tasks)
                {
                    switch (task.State)
                    {
                        case DownloadTaskState.Completed:
                            completed++;
                            break;
                        case DownloadTaskState.Skipped:
                            skipped++;
                            break;
                        case DownloadTaskState.Failed:
                            failed++;
                            failures.Add(new DownloadFailure(
                                new DownloadItem
                                {
                                    Url = task.Url ?? string.Empty,
                                    DestinationPath = task.DestinationPath ?? task.Name,
                                    Id = task.Id,
                                    Group = task.Group,
                                    ExpectedSize = Math.Max(0L, task.TotalBytes),
                                    ExpectedSha1 = task.Sha1,
                                },
                                task.Error ?? "未知错误"));
                            break;
                    }

                    totalBytes += Math.Max(0L, task.TotalBytes);
                    downloadedBytes += Math.Max(0L, task.DownloadedBytes);
                }

                return new DownloadBatchResult
                {
                    TotalFiles = _tasks.Count,
                    CompletedFiles = completed + skipped,
                    SkippedFiles = skipped,
                    FailedFiles = failed,
                    TotalBytes = totalBytes,
                    DownloadedBytes = downloadedBytes,
                    Failures = failures,
                };
            }
        }

        public DownloadBatchSummary Complete(DownloadBatchResult result)
        {
            DownloadTaskInfo[] copy;
            var finishedAt = DateTimeOffset.Now;
            lock (_gate)
            {
                copy = _tasks.ToArray();
            }

            return new DownloadBatchSummary(
                _name,
                _startedAt,
                finishedAt,
                result.TotalFiles,
                result.CompletedFiles,
                result.SkippedFiles,
                result.FailedFiles,
                result.TotalBytes,
                result.DownloadedBytes,
                copy.Where(task => task.IsFailed).ToArray());
        }

        private DownloadTaskInfo? Find(DownloadItem item) =>
            _byKey.TryGetValue(item.DestinationPath, out var task) ? task : null;
    }

    /// <summary>
    /// <see cref="BeginExternalBatch"/> 返回的作用域。
    ///
    /// 状态机：0 = 活动中，1 = 已 Complete，2 = 已 Dispose（取消）。
    /// 三个入口都不加锁：用 <see cref="Interlocked.Exchange(ref int, int)"/> 抢占，
    /// 后到的调用直接变成空操作。这样安装器最后的异步回调即便晚到，也不会往已收尾的批次里写。
    /// </summary>
    private sealed class ExternalTaskScope(DownloadQueue owner, BatchTaskTracker tracker) : IDownloadTaskScope
    {
        private int _state;

        public void Report(
            string displayName,
            string group,
            long totalBytes,
            long downloadedBytes,
            DownloadTaskState state,
            string? error = null,
            string? url = null,
            string? destinationPath = null,
            string? sha1 = null)
        {
            // 收尾后的迟到上报直接丢弃：历史已经写死，再改会让面板与摘要不一致。
            if (Volatile.Read(ref _state) != 0)
            {
                return;
            }

            tracker.Report(displayName, group, totalBytes, downloadedBytes, state, error, url, destinationPath, sha1);
            owner.RaiseTasksChanged(force: false);
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _state, 1) != 0)
            {
                return;
            }

            owner.CompleteExternalBatch(tracker);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 2) != 0)
            {
                return;
            }

            owner.CancelExternalBatch(tracker);
        }
    }

    private sealed class BatchCounters
    {
        private readonly int _totalFiles;
        private readonly ConcurrentQueue<DownloadFailure> _failures = new();
        private readonly ConcurrentDictionary<string, long> _claimed = new(StringComparer.Ordinal);

        private long _totalBytes;
        private long _downloadedBytes;
        private int _completed;
        private int _skipped;
        private int _failed;

        public BatchCounters(IReadOnlyList<DownloadItem> items)
        {
            _totalFiles = items.Count;
            foreach (var item in items)
            {
                if (item.ExpectedSize > 0)
                {
                    Interlocked.Add(ref _totalBytes, item.ExpectedSize);
                }
            }
        }

        public int TotalFiles => _totalFiles;

        public int Completed => Volatile.Read(ref _completed);

        public int Skipped => Volatile.Read(ref _skipped);

        public int Failed => Volatile.Read(ref _failed);

        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

        public void ClaimSize(DownloadItem item, long size)
        {
            if (item.ExpectedSize > 0)
            {
                return;
            }

            if (_claimed.TryAdd(item.DestinationPath, size))
            {
                Interlocked.Add(ref _totalBytes, size);
            }
        }

        public void AddBytes(long count) => Interlocked.Add(ref _downloadedBytes, count);

        public void SubtractBytes(long count) => Interlocked.Add(ref _downloadedBytes, -count);

        public void MarkSkipped(long bytes)
        {
            Interlocked.Increment(ref _skipped);
            Interlocked.Increment(ref _completed);
            Interlocked.Add(ref _downloadedBytes, bytes);
        }

        public void MarkCompleted() => Interlocked.Increment(ref _completed);

        public void MarkFailed(DownloadFailure failure)
        {
            Interlocked.Increment(ref _failed);
            _failures.Enqueue(failure);
        }

        public DownloadBatchResult ToResult() => new()
        {
            TotalFiles = _totalFiles,
            CompletedFiles = Completed,
            SkippedFiles = Skipped,
            FailedFiles = Failed,
            TotalBytes = TotalBytes,
            DownloadedBytes = DownloadedBytes,
            Failures = _failures.ToArray(),
        };
    }

    private sealed class ProgressReporter(BatchCounters counters, IProgress<DownloadProgress>? progress, Func<bool> isPaused)
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private double _lastSampleAt;
        private double _lastReportAt = -1;
        private long _lastSampleBytes;
        private double _speed;

        public void Report(string? currentFile = null, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                var now = _clock.Elapsed.TotalSeconds;
                var bytes = counters.DownloadedBytes;

                if (now - _lastSampleAt >= 0.25)
                {
                    var delta = bytes - _lastSampleBytes;
                    var dt = now - _lastSampleAt;
                    if (dt > 0 && delta >= 0)
                    {
                        var instant = delta / dt;
                        _speed = _speed <= 0 ? instant : (_speed * 0.7) + (instant * 0.3);
                    }

                    _lastSampleBytes = bytes;
                    _lastSampleAt = now;
                }

                if (!force && now - _lastReportAt < 0.1)
                {
                    return;
                }

                _lastReportAt = now;

                var totalBytes = counters.TotalBytes;
                var raw = totalBytes > 0
                    ? (double)bytes / totalBytes
                    : counters.TotalFiles > 0
                        ? (double)counters.Completed / counters.TotalFiles
                        : 0;

                // 最后 2% 由曲线留给收尾，只有「确实没有失败且全部完成」时才补满 1.0，
                // 否则进度条会永远停在 98%。
                var ratio = counters.Failed == 0 && counters.Completed >= counters.TotalFiles
                    ? 1.0
                    : ApplyProgressCurve(raw);

                progress.Report(new DownloadProgress(
                    counters.TotalFiles,
                    counters.Completed,
                    counters.Failed,
                    totalBytes,
                    bytes,
                    _speed,
                    currentFile,
                    ratio,
                    IsPaused: isPaused()));
            }
        }
    }

    /// <summary>
    /// 可取消的「布尔闸门」：关闭后所有 <see cref="WaitAsync"/> 挂起，打开时一起放行。
    ///
    /// 为什么不用现成的同步原语：
    /// <list type="bullet">
    /// <item><see cref="ManualResetEventSlim"/> 只有同步等待，64 个下载任务各占一个线程池线程
    /// 干等，会把线程池拖到饥饿；</item>
    /// <item><see cref="SemaphoreSlim"/> 表达「关闭」必须由某一方持有令牌，而那次等待可能发生在
    /// 调用方线程（UI 线程）上，等于让界面去阻塞；</item>
    /// <item>用取消令牌表达暂停则更糟：取消会清空批次快照、不写历史，恢复后还得重来。</item>
    /// </list>
    /// 用 <see cref="TaskCompletionSource"/> 后，等待方是真正的异步挂起，并且照样能被
    /// 批次的取消令牌打断（暂停中取消要能立刻收尾）。
    /// </summary>
    private sealed class PauseGate
    {
        private readonly object _gate = new();

        /// <summary>放行信号。关闭时替换成一个未完成的实例，等待方持有的是各自那一份。</summary>
        private TaskCompletionSource _released = Completed();

        private bool _closed;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return _closed ? _released.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Open()
        {
            TaskCompletionSource? released;
            lock (_gate)
            {
                if (!_closed)
                {
                    return;
                }

                _closed = false;
                released = _released;
                _released = Completed();
            }

            // 在锁外放行：延续会立刻回到下载循环，别让它持有闸门内部锁。
            released.TrySetResult();
        }

        private static TaskCompletionSource Completed()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
