namespace NebulaLauncher.Core.Models;

/// <summary>单个下载任务的快照。</summary>
public sealed class DownloadTaskInfo
{
    public required string Id { get; init; }

    /// <summary>人类可读名称，例如 <c>26.1.json</c> 或 <c>assets/objects/ab/abcdef</c>。</summary>
    public required string Name { get; init; }

    /// <summary>Minecraft 版本名或「Fabric 0.16.9」之类，用于分组展示。</summary>
    public string Group { get; init; } = string.Empty;

    public long TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public DownloadTaskState State { get; set; } = DownloadTaskState.Queued;

    public string? Error { get; set; }

    /// <summary>
    /// 下载源地址。失败项要能「只重试这几个文件」就必须带上它；
    /// 空的 URL 表示这一项无法重建下载请求（例如安装器自造的文件）。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>落盘路径。重试时用来重建 <c>DownloadItem</c>，同时是快照内的稳定键。</summary>
    public string? DestinationPath { get; init; }

    /// <summary>预期 SHA1；重试时保持与首次下载相同的校验强度。</summary>
    public string? Sha1 { get; init; }

    /// <summary>批次内声明顺序。快照按它排序，避免状态变化导致列表跳动。</summary>
    public int Sequence { get; init; }

    public double Ratio => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;

    /// <summary>是否已结束（完成或跳过）。</summary>
    public bool IsFinished => State is DownloadTaskState.Completed or DownloadTaskState.Skipped;

    public bool IsFailed => State == DownloadTaskState.Failed;

    /// <summary>快照内的稳定键：优先落盘路径，退化为 Id，再退化为名称。</summary>
    public string Key => !string.IsNullOrEmpty(DestinationPath)
        ? DestinationPath
        : !string.IsNullOrEmpty(Id) ? Id : Name;
}

/// <summary>
/// 任务状态的展示文案与百分比。
///
/// 放在 Core 而不是 ViewModel：文案要与 <see cref="DownloadTaskState"/> 的语义对齐，
/// 新增状态时在这里补一个分支就能被单测覆盖；具体颜色仍由 XAML 的令牌决定。
/// </summary>
public static class DownloadTaskPresentation
{
    /// <summary>状态徽标文案。</summary>
    public static string DescribeState(DownloadTaskState state) => state switch
    {
        DownloadTaskState.Running => "下载中",
        DownloadTaskState.Completed => "完成",
        DownloadTaskState.Failed => "失败",
        DownloadTaskState.Skipped => "跳过",
        _ => "排队",
    };

    /// <summary>
    /// 行内百分比文字。完成 / 跳过固定 100%，其余按字节比例；
    /// 总量未知（0 字节）且未结束时返回「—」，不要显示 0% 让人以为卡住。
    /// </summary>
    public static string DescribePercent(DownloadTaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsFinished)
        {
            return "100%";
        }

        if (task.TotalBytes <= 0)
        {
            return "—";
        }

        return $"{Math.Round(task.Ratio * 100, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%";
    }
}

public enum DownloadTaskState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>整个下载队列的聚合进度。</summary>
/// <param name="IsPaused">
/// 上报这一刻队列是否处于暂停状态。放在进度里而不是只靠 UI 自己记：
/// 安装链之外的所有下载（安装、修复、重试、在线内容）共用一条进度回调，
/// 回调方据此决定「速度 / 剩余时间」该不该继续显示——暂停时它们恒为 0，
/// 不标出来会让用户以为卡住了。
/// </param>
public sealed record DownloadProgress(
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    long TotalBytes,
    long DownloadedBytes,
    double SpeedBytesPerSecond,
    string? CurrentFile,
    double Ratio,
    string? Stage = null,
    bool IsPaused = false)
{
    public static readonly DownloadProgress Empty = new(0, 0, 0, 0, 0, 0, null, 0);

    public TimeSpan? Eta =>
        SpeedBytesPerSecond > 1024 && TotalBytes > DownloadedBytes
            ? TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / SpeedBytesPerSecond)
            : null;
}

/// <summary>
/// 加载器安装阶段。
///
/// 为什么需要它：Forge / NeoForge 的安装由三段时间尺度完全不同的工作组成——
/// 游戏本体下载（几千个文件）、安装器与 maven 依赖下载（几百 MB）、
/// 安装器处理器执行（拉起 java，几分钟且没有任何字节进度）。
/// 只给一个聚合比例，用户看到的是一条长时间不动的进度条；
/// 拆成阶段后至少能说清「现在卡在哪一段」。
/// </summary>
public enum InstallStage
{
    /// <summary>准备游戏本体（CmlLib 安装原版文件）。</summary>
    Vanilla = 0,

    /// <summary>加载器安装器与 maven 依赖下载。</summary>
    LoaderDownload = 1,

    /// <summary>加载器处理器执行（java 进程，无字节进度）。</summary>
    LoaderProcessors = 2,

    /// <summary>收尾：写版本清单与加载器标记。</summary>
    Finalize = 3,
}

/// <summary>
/// 阶段 → 总进度区间 的映射（纯函数）。
///
/// 区间：原版 0–50%、加载器下载 50–85%、处理器执行 85–100%。
/// 比例一律夹在 0–1；<see cref="Map"/> 保证同一阶段内单调，
/// 阶段之间的单调性由调用方（安装进度适配器）用「只增不减」兜住。
/// </summary>
public static class InstallStagePlan
{
    /// <summary>原版阶段结束点。</summary>
    public const double VanillaEnd = 0.50;

    /// <summary>加载器下载阶段结束点。</summary>
    public const double LoaderDownloadEnd = 0.85;

    /// <summary>全部完成。</summary>
    public const double AllDone = 1.00;

    /// <summary>阶段在总进度里的起点。</summary>
    public static double Start(InstallStage stage) => stage switch
    {
        InstallStage.Vanilla => 0,
        InstallStage.LoaderDownload => VanillaEnd,
        InstallStage.LoaderProcessors => LoaderDownloadEnd,
        _ => LoaderDownloadEnd,
    };

    /// <summary>阶段在总进度里的终点。</summary>
    public static double End(InstallStage stage) => stage switch
    {
        InstallStage.Vanilla => VanillaEnd,
        InstallStage.LoaderDownload => LoaderDownloadEnd,
        _ => AllDone,
    };

    /// <summary>把阶段内的 0–1 比例映射到总进度。NaN 按 0 处理，越界值夹住。</summary>
    public static double Map(InstallStage stage, double stageRatio)
    {
        var start = Start(stage);
        var end = End(stage);
        var ratio = double.IsNaN(stageRatio) ? 0 : Math.Clamp(stageRatio, 0, 1);
        return start + (ratio * (end - start));
    }

    /// <summary>阶段文案。界面顶部那行文字直接用它。</summary>
    public static string Describe(InstallStage stage, LoaderKind loader)
    {
        var name = loader switch
        {
            LoaderKind.Vanilla => "Minecraft",
            _ => loader.ToString(),
        };

        return stage switch
        {
            InstallStage.Vanilla => "正在准备游戏本体…",
            InstallStage.LoaderDownload => $"正在下载 {name} 安装文件…",
            InstallStage.LoaderProcessors => $"正在执行 {name} 安装器…",
            _ => "正在写入版本清单…",
        };
    }
}

/// <summary>
/// 一次下载批次的收尾摘要，用于「批次历史」。
///
/// 只存在内存里（队列最多保留最近 20 条）：落盘会引入新文件、迁移与多实例写冲突，
/// 而用户真正需要的「刚才那次装了什么、哪几个文件失败」看完就可以丢。
/// </summary>
/// <param name="Name">批次名。取文件清单里第一个非空 Group，没有则用「下载任务」。</param>
/// <param name="StartedAt">批次开始时刻（UTC 偏移由调用方决定，界面只显示本地时间）。</param>
/// <param name="FinishedAt">批次结束时刻。</param>
/// <param name="TotalFiles">本批文件总数。</param>
/// <param name="CompletedFiles">实际下载成功的文件数（不含跳过）。</param>
/// <param name="SkippedFiles">已存在 / 跨实例复用而跳过的文件数。</param>
/// <param name="FailedFiles">失败文件数。</param>
/// <param name="TotalBytes">本批总字节（含跳过的文件）。</param>
/// <param name="DownloadedBytes">本批实际落盘字节。</param>
/// <param name="FailedTasks">失败项快照（含 URL 与错误文本），供「重试失败项」使用。</param>
public sealed record DownloadBatchSummary(
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int TotalFiles,
    int CompletedFiles,
    int SkippedFiles,
    int FailedFiles,
    long TotalBytes,
    long DownloadedBytes,
    IReadOnlyList<DownloadTaskInfo> FailedTasks)
{
    public static readonly DownloadBatchSummary Empty = new(
        "—", default, default, 0, 0, 0, 0, 0, 0, Array.Empty<DownloadTaskInfo>());

    /// <summary>耗时。时钟回拨时返回 <see cref="TimeSpan.Zero"/>，不返回负数。</summary>
    public TimeSpan Duration => FinishedAt > StartedAt ? FinishedAt - StartedAt : TimeSpan.Zero;

    public bool Success => FailedFiles == 0;

    /// <summary>
    /// 完成 + 失败之和与总数一致。
    ///
    /// 注意 <see cref="CompletedFiles"/> 已经包含跳过的文件（队列的既有语义：
    /// 跳过也算「不用再下」的完成项），所以这里不再加 <see cref="SkippedFiles"/>。
    /// </summary>
    public bool IsClosed => CompletedFiles + FailedFiles == TotalFiles;

    public double Ratio => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;
}

/// <summary>安装请求。</summary>
public sealed class InstallRequest
{
    public required string MinecraftVersion { get; init; }

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>加载器版本；null 表示取该游戏版本的最新稳定版。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>安装完成后的版本目录名；null 表示自动生成。</summary>
    public string? ResultVersionName { get; init; }

    public DownloadSource Source { get; init; } = DownloadSource.BmclApi;
}

/// <summary>可用的游戏版本与加载器版本组合，供下载页展示。</summary>
public sealed class InstallableVersion
{
    public required string MinecraftVersion { get; init; }

    public VersionChannel Channel { get; init; }

    public IReadOnlyList<LoaderKind> SupportedLoaders { get; init; } = Array.Empty<LoaderKind>();

    public bool IsInstalled { get; init; }
}

/// <summary>
/// 修复档位。
///
/// 单档修复（只补缺失文件）发现不了「jar 存在但被截断 / 被替换」——这类版本能列出、
/// 能点启动，然后在主菜单前崩掉，用户看不出原因。两档把代价与覆盖面分开。
/// </summary>
public enum RepairDepth
{
    /// <summary>只补缺失文件与 0 字节 jar，秒级完成。</summary>
    Quick = 0,

    /// <summary>逐文件校验 libraries 与 assets 的 SHA1，慢但能治「文件在但坏了」。</summary>
    Deep = 1,
}

/// <summary>一次修复的汇总。UI 直接展示这些计数，不做二次统计。</summary>
/// <param name="Missing">判定为缺失的文件数。</param>
/// <param name="Mismatched">存在但校验不通过的文件数（Deep 档才有）。</param>
/// <param name="Redownloaded">实际重新下载成功的文件数。</param>
/// <param name="Fixed">已修复项的可读描述（相对路径为主，避免刷屏）。</param>
/// <param name="Failed">无法修复项及原因。</param>
public sealed record RepairReport(
    int Missing,
    int Mismatched,
    int Redownloaded,
    IReadOnlyList<string> Fixed,
    IReadOnlyList<string> Failed)
{
    public static readonly RepairReport Empty = new(0, 0, 0, Array.Empty<string>(), Array.Empty<string>());

    public bool Success => Failed.Count == 0;

    /// <summary>是否真的改动了磁盘。全为 0 时 UI 应显示「文件完整」而不是「修复成功」。</summary>
    public bool ChangedAnything => Redownloaded > 0 || Fixed.Count > 0;
}
