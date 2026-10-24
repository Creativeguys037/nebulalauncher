namespace NebulaLauncher.Core.Abstractions;

/// <summary>日志文件的来源分类。左列按它分组。</summary>
public enum LogFileKind
{
    /// <summary>启动器自己写的 <c>&lt;data&gt;/logs/*.log</c>。</summary>
    Launcher,

    /// <summary>当前游戏目录的 <c>logs/latest.log</c>。</summary>
    Game,

    /// <summary>当前游戏目录的 <c>crash-reports/*.txt</c>。</summary>
    Crash,
}

/// <summary>
/// 日志级别。取值与 <c>FileLogger</c> 实际写出的标记一一对应；
/// 游戏侧的 <c>FATAL</c> / <c>TRACE</c> 分别归入 <see cref="Error"/> / <see cref="Debug"/>。
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>一个可读取的日志文件。</summary>
/// <param name="Path">绝对路径，用于读取与「在文件夹中显示」。</param>
/// <param name="DisplayName">列表里显示的文件名。</param>
/// <param name="Kind">来源分类。</param>
/// <param name="SizeBytes">文件字节数。</param>
/// <param name="ModifiedUtc">最后修改时间（UTC）。</param>
public sealed record LogFileInfo(
    string Path,
    string DisplayName,
    LogFileKind Kind,
    long SizeBytes,
    DateTimeOffset ModifiedUtc);

/// <summary>解析后的一行日志。</summary>
/// <param name="LineNumber">
/// 本次读取窗口内的序号（从 1 开始）。尾部读取时会丢弃窗口之外的行，
/// 因此它不等于文件里的绝对行号——这一点在界面上用「最后 N 行」的说法覆盖。
/// </param>
/// <param name="Timestamp">解析出的时间戳；行内没有时间戳时为 <c>null</c>。</param>
/// <param name="Level">解析出的级别；行内没有级别标记时为 <c>null</c>。</param>
/// <param name="Text">去掉时间戳与级别前缀后的正文。解析失败时是整行原文。</param>
public sealed record LogEntry(int LineNumber, DateTimeOffset? Timestamp, LogLevel? Level, string Text);

/// <summary>尾部读取的结果状态。空状态 / 文件不存在 / 读取失败三种界面文案各对应一项。</summary>
public enum LogReadStatus
{
    /// <summary>读取成功。正文为空表示文件是空的。</summary>
    Success,

    /// <summary>文件不存在（日志被清理、实例被删除）。</summary>
    FileMissing,

    /// <summary>文件存在但读不出来（被独占锁定、权限不足、路径非法）。</summary>
    ReadFailed,
}

/// <summary>尾部读取结果。</summary>
/// <param name="Entries">解析后的行，按文件中的先后顺序排列。</param>
/// <param name="Truncated">为真表示文件长于请求的行数，<see cref="Entries"/> 只是末尾一部分。</param>
/// <param name="Status">读取状态。</param>
/// <param name="Error">读取失败时的原因，其余状态为 <c>null</c>。</param>
public sealed record LogTailResult(
    IReadOnlyList<LogEntry> Entries,
    bool Truncated,
    LogReadStatus Status,
    string? Error = null)
{
    /// <summary>文件不存在。</summary>
    public static LogTailResult Missing() => new(Array.Empty<LogEntry>(), false, LogReadStatus.FileMissing);

    /// <summary>读取失败，带上原因。</summary>
    public static LogTailResult Failed(string error) =>
        new(Array.Empty<LogEntry>(), false, LogReadStatus.ReadFailed, error);

    /// <summary>读取成功。</summary>
    public static LogTailResult Ok(IReadOnlyList<LogEntry> entries, bool truncated) =>
        new(entries, truncated, LogReadStatus.Success);
}

/// <summary>
/// 日志读取契约。只读，不写盘，不改文件。
///
/// 为什么单独一个契约而不是塞进 <see cref="IAppLog"/>：写日志是「每条都要落盘」的热路径，
/// 读日志是「用户点开页面才跑」的冷路径，两边的异常语义完全相反
/// （写必须吞掉一切异常；读必须把失败原因告诉用户）。
/// </summary>
public interface ILauncherLogReader
{
    /// <summary>默认只读最后这么多行：几百 MB 的崩溃日志不能整份进内存。</summary>
    public const int DefaultMaxLines = 2000;

    /// <summary>枚举启动器日志，按修改时间倒序。目录不存在或不可读时返回空列表，不抛异常。</summary>
    IReadOnlyList<LogFileInfo> ListLauncherLogs();

    /// <summary>
    /// 枚举游戏侧日志：<c>logs/latest.log</c> 加上 <c>crash-reports/*.txt</c>（倒序）。
    /// 目录不存在时返回空列表，不抛异常。
    /// </summary>
    IReadOnlyList<LogFileInfo> ListGameLogs(string gameDirectory);

    /// <summary>
    /// 从文件尾部倒读最多 <paramref name="maxLines"/> 行并解析。
    /// 文件不存在或读取失败时返回带状态的结果，不抛异常。
    /// </summary>
    Task<LogTailResult> ReadTailAsync(
        string path,
        int maxLines = DefaultMaxLines,
        CancellationToken cancellationToken = default);
}

/// <summary>增量读取（跟随）的结果状态。</summary>
public enum LogFollowStatus
{
    /// <summary>正常：文件在 <c>offset</c> 之后有新内容（也可能暂时没有）。</summary>
    Appended,

    /// <summary>文件变短了（被截断 / 重新写过），已从头重读。</summary>
    Truncated,

    /// <summary>文件被替换成了另一份内容（游戏轮转 <c>latest.log</c>），已从头重读。</summary>
    Rotated,

    /// <summary>文件不存在或读不出来。跟随不停摆，下个周期再试。</summary>
    Missing,
}

/// <summary>
/// 一次增量读取的结果。
/// </summary>
/// <param name="Entries">本次读到的完整行，按文件中的先后顺序排列。</param>
/// <param name="Offset">
/// 下一次读取的起点（字节）。<see cref="LogFollowStatus.Appended"/> 时它一定落在行边界上：
/// 落在行中间的半行不会推进偏移，下个周期会被当作新增内容重新读到。
/// </param>
/// <param name="Status">本次读取的状态判据。</param>
/// <param name="Error">失败原因；只在诊断时用，界面主要依赖 <paramref name="Status"/>。</param>
public sealed record LogFollowResult(
    IReadOnlyList<LogEntry> Entries,
    long Offset,
    LogFollowStatus Status,
    string? Error = null)
{
    /// <summary>没有新内容，偏移保持不变。</summary>
    public static LogFollowResult Empty(long offset) =>
        new(Array.Empty<LogEntry>(), offset, LogFollowStatus.Appended);

    /// <summary>文件不存在 / 读不出来。</summary>
    public static LogFollowResult Missing(long offset) =>
        new(Array.Empty<LogEntry>(), offset, LogFollowStatus.Missing);

    /// <summary>文件不存在 / 读不出来，带上原因。</summary>
    public static LogFollowResult Failed(long offset, string error) =>
        new(Array.Empty<LogEntry>(), offset, LogFollowStatus.Missing, error);
}

/// <summary>
/// 日志跟随契约：从给定偏移之后增量读取新写的行。
///
/// 与 <see cref="ILauncherLogReader.ReadTailAsync"/> 的分工：后者是「打开页面 / 切文件」时的一次性整份读取，
/// 前者是「页面开着、文件还在长」时的轮询。两者都只读，绝不写盘、绝不改日志文件。
/// </summary>
public interface ILogFollowService
{
    /// <summary>
    /// 单次读取的字节上限。游戏日志滚动很快时一次追几十 MB 会卡住界面，
    /// 每次最多读这么多、剩余内容留给下个周期。
    /// </summary>
    public const int DefaultMaxBytes = 256 * 1024;

    /// <summary>
    /// 从 <paramref name="offset"/> 之后读最多 <paramref name="maxBytes"/> 字节，只返回完整的行。
    /// 文件不存在、路径非法、被独占锁定都返回 <see cref="LogFollowStatus.Missing"/>，不抛异常。
    /// </summary>
    Task<LogFollowResult> ReadSinceAsync(
        string path,
        long offset,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        // 契约先落地：没有实现的宿主（设计器预览、测试替身）也必须能编译，并表现为「没有新行」。
        return Task.FromResult(LogFollowResult.Empty(Math.Max(0, offset)));
    }
}
