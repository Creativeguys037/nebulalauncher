using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 下载源提供者：每次 HTTP 请求都问一次「现在该优先哪个源」。
///
/// 为什么是「每次问」而不是构造期缓存一个 <see cref="DownloadSource"/>：
/// 自动模式要在会话中途降级，而 <c>MirrorRewritingHandler</c> 是单例、被 CmlLib 与
/// 下载队列共用；把源缓存到构造期，运行时就再也切不动了。
/// </summary>
public interface IDownloadSourceProvider
{
    /// <summary>用户选择的策略：<see cref="DownloadSource.Auto"/> / <see cref="DownloadSource.Official"/> / <see cref="DownloadSource.Mirror"/>。</summary>
    DownloadSource Strategy { get; }

    /// <summary>
    /// 本次请求的首选源。自动模式会按本次会话的劣化状态解析成
    /// <see cref="DownloadSource.Mirror"/> 或 <see cref="DownloadSource.Official"/>，
    /// 因此**永远不返回 <see cref="DownloadSource.Auto"/>**——候选排序需要一个具体方向。
    /// </summary>
    DownloadSource Effective { get; }

    /// <summary>降级原因（可直接显示给用户的中文）。未降级、或手动档时为 null。</summary>
    string? DegradeReason { get; }

    /// <summary>本次会话发生降级的时刻；未降级为 null。</summary>
    DateTimeOffset? DegradedAtUtc { get; }

    /// <summary>降级、恢复、或策略变化时触发，供界面刷新状态行。</summary>
    event EventHandler? Changed;
}

/// <summary>
/// 下载结果回灌。传输层（<c>MirrorRewritingHandler</c>）在每次请求结束后调用一次，
/// 用来驱动 <see cref="IDownloadSourceProvider"/> 的会话级状态机。
/// </summary>
public interface IDownloadSourceTelemetry
{
    void Report(DownloadAttemptResult result);
}

/// <summary>
/// 一次文件请求的结果。
///
/// 只记录「到响应头为止」的耗时：真正的文件内容由调用方读取，传输层无法中断也没法重放，
/// 所以这里的耗时是连接与首字节的时间，不是完整下载时长。判据见 <c>DownloadSourceGovernor</c>。
/// </summary>
public readonly record struct DownloadAttemptResult
{
    /// <summary>首选源的实际来源（镜像或官方）。</summary>
    public DownloadSource PrimarySource { get; init; }

    /// <summary>首选源是否直接拿到成功响应（没有回退到另一个源）。</summary>
    public bool PrimarySucceeded { get; init; }

    /// <summary>首选源从发起到放弃/成功拿到响应头的时间。</summary>
    public TimeSpan PrimaryElapsed { get; init; }

    /// <summary>是否回退到了第二个候选源。</summary>
    public bool FallbackUsed { get; init; }

    /// <summary>回退源拿到响应头的时间。</summary>
    public TimeSpan FallbackElapsed { get; init; }

    /// <summary>整个请求最终是否成功。</summary>
    public bool Succeeded { get; init; }

    /// <summary>首选源是「镜像失败后回退」的样本：判定镜像劣化的直接依据。</summary>
    public bool MirrorFailed =>
        PrimarySource == DownloadSource.Mirror && !PrimarySucceeded;
}

// ==================== 下载源测速（只提示，不决策） ====================

/// <summary>
/// 一个测速目标：同路径可镜像的官方地址。
///
/// 只存官方地址，镜像侧由 <c>DownloadMirror.Rewrite(url, DownloadSource.Mirror)</c> 现算：
/// 把镜像 URL 写死等于把 <c>BmclApiMap</c> 抄成第二份，映射规则一改两边立刻不一致。
/// </summary>
public sealed record SourceProbeTarget(string Name, string OfficialUrl);

/// <summary>
/// 一个下载源的测速结果（同一源的所有目标聚合为一条）。
///
/// 首字节与吞吐都是「成功目标的聚合值」：首字节取中位数（避免一次抖动定调），
/// 吞吐按总字节 / 总传输时间加权（不是各目标算术平均，否则小文件会把大文件的速度拉偏）。
/// </summary>
public sealed record SourceProbeSample
{
    /// <summary>该样本属于哪一侧。</summary>
    public required DownloadSource Source { get; init; }

    /// <summary>可直接显示的源名：<c>官方</c> / <c>BMCLAPI 镜像</c>。</summary>
    public required string SourceName { get; init; }

    /// <summary>是否至少有一个目标测成功。全失败时首字节与吞吐都为 0。</summary>
    public bool Succeeded { get; init; }

    /// <summary>成功目标的首字节时间中位数（毫秒）。失败或未测为 0。</summary>
    public double FirstByteMs { get; init; }

    /// <summary>成功目标的聚合吞吐（KB/s）。失败或未读到字节为 0。</summary>
    public double ThroughputKbps { get; init; }

    /// <summary>实际读到的字节数（受单目标上限封顶）。</summary>
    public long BytesRead { get; init; }

    /// <summary>成功的目标数。</summary>
    public int TargetsOk { get; init; }

    /// <summary>失败的目标数。</summary>
    public int TargetsFailed { get; init; }

    /// <summary>失败原因（可直接显示）。成功时为 null；失败时由聚合保证非空。</summary>
    public string? FailureReason { get; init; }
}

/// <summary>排序结论。纯函数产出，界面只渲染。</summary>
/// <param name="SuggestedSource">建议源；<c>null</c> 表示不给建议（持平、两边都失败、或没有样本）。</param>
/// <param name="IsTie">两侧成功但综合分差距小于容差，判「差不多」。</param>
/// <param name="BothFailed">两侧都没有成功样本（或只有失败的样本）。</param>
public sealed record SourceProbeDecision(DownloadSource? SuggestedSource, bool IsTie, bool BothFailed)
{
    /// <summary>没有样本时的结论。</summary>
    public static readonly SourceProbeDecision None = new(null, false, false);
}

/// <summary>
/// 一次完整测速的报告。<b>不可变</b>：<see cref="Samples"/> 是只读集合、全部属性 init-only，
/// 拿 <c>with</c> 改任何一项都会得到新对象，不影响已有报告与已显示的结果。
/// </summary>
public sealed record SourceProbeReport
{
    /// <summary>没有测过任何东西。</summary>
    public static readonly SourceProbeReport Empty = new();

    /// <summary>各个源的样本（当前实现是官方与镜像各一条）。</summary>
    public IReadOnlyList<SourceProbeSample> Samples { get; init; } = Array.Empty<SourceProbeSample>();

    /// <summary>建议源；null 表示不给建议。</summary>
    public DownloadSource? SuggestedSource { get; init; }

    /// <summary>两侧综合分接近，判「差不多」。</summary>
    public bool IsTie { get; init; }

    /// <summary>两侧都失败。</summary>
    public bool BothFailed { get; init; }

    /// <summary>整次测速的墙钟耗时（毫秒）。</summary>
    public double ElapsedMs { get; init; }

    /// <summary>一行可直接显示的结论，例如 <c>官方 320ms · BMCLAPI 镜像 190ms · 建议用 BMCLAPI 镜像</c>。</summary>
    public string Summary { get; init; } = "尚未测速";
}

/// <summary>
/// 下载源测速。纯测量：只发一组小请求量一次时间，<b>不参与任何下载决策</b>，
/// 不改设置、不动 <c>DownloadSourceGovernor</c> 的当前源。
///
/// 默认实现返回 <see cref="SourceProbeReport.Empty"/>：界面据此把按钮置灰，
/// 而不是在组合根没注册时点出一个空白结果。
/// </summary>
public interface IDownloadSourceProbe
{
    /// <summary>测一次两侧（官方 / 镜像）的首字节与吞吐。失败记 0 分并保留原因，不抛异常。</summary>
    Task<SourceProbeReport> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SourceProbeReport.Empty);
}
