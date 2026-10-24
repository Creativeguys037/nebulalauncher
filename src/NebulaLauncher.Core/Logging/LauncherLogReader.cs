using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Logging;

/// <summary>
/// 日志文件的读取实现：枚举、尾部倒读、行解析、过滤。
///
/// 为什么不「整份读进来再切」：崩溃现场常见几十到几百 MB 的 <c>latest.log</c>，
/// 一次 <c>File.ReadAllLines</c> 会在用户最需要排查的机器上把内存打满。
/// 这里从文件末尾按 64 KB 块倒着读，攒够行数立刻停，内存占用与文件大小无关。
///
/// 解析格式取自实际输出，不做猜测：
/// <list type="bullet">
/// <item>启动器：<c>yyyy-MM-dd HH:mm:ss.fff [LEVEL] text</c>（见 <see cref="FileLogger"/>，级别标记定宽 5 字符）</item>
/// <item>游戏：<c>[HH:mm:ss] [thread/LEVEL]: text</c>（Minecraft 默认 log4j 控制台格式）</item>
/// <item>其余（崩溃报告正文、堆栈、空行）：整行当 <see cref="LogEntry.Text"/>，时间戳与级别留空</item>
/// </list>
/// </summary>
public sealed class LauncherLogReader : ILauncherLogReader
{
    /// <summary>倒读的块大小。64 KB 是「一次系统调用够快」与「不会一次读太多」的折中。</summary>
    private const int ChunkSize = 64 * 1024;

    /// <summary>启动器日志行：<c>2026-10-22 09:41:07.218 [INFO ] 消息</c>。</summary>
    private static readonly Regex LauncherLinePattern = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+\[(?<level>DEBUG|INFO|WARN|ERROR)\s*\]\s?(?<text>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>游戏日志行：<c>[09:41:07] [Render thread/WARN]: 消息</c>。</summary>
    private static readonly Regex GameLinePattern = new(
        @"^\[(?<ts>\d{2}:\d{2}:\d{2})\]\s+\[(?<thread>[^\]]*?)/(?<level>[A-Za-z]+)\]:?\s?(?<text>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILauncherPaths _paths;

    public LauncherLogReader(ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public IReadOnlyList<LogFileInfo> ListLauncherLogs()
    {
        var directory = SafeDirectory(() => _paths.LogsDirectory);
        return EnumerateFiles(directory, "*.log", LogFileKind.Launcher);
    }

    /// <inheritdoc />
    public IReadOnlyList<LogFileInfo> ListGameLogs(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Array.Empty<LogFileInfo>();
        }

        var result = new List<LogFileInfo>(4);

        // 只探测存在的那几个文件：游戏目录里通常没有任何日志（还没启动过），
        // 这时页面应该显示空状态而不是一串不存在的条目。
        TryDescribe(Combine(gameDirectory, "logs", "latest.log"), LogFileKind.Game, result);
        result.AddRange(EnumerateFiles(Combine(gameDirectory, "crash-reports"), "*.txt", LogFileKind.Crash));

        return result;
    }

    /// <inheritdoc />
    public Task<LogTailResult> ReadTailAsync(
        string path,
        int maxLines = ILauncherLogReader.DefaultMaxLines,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(LogTailResult.Missing());
        }

        // 读盘放在线程池：2000 行的尾部读取通常是个位数毫秒，但文件在机械盘 / 网络盘上时
        // 同步读会把 UI 线程卡住，而这类目录恰恰是「自定义游戏目录」的常见配置。
        return Task.Run(() => ReadTail(path, maxLines, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 级别 + 关键字过滤（纯函数，便于单测）。
    /// </summary>
    /// <param name="entries">待过滤的行。</param>
    /// <param name="levels">要保留的级别；<c>null</c> 或空集合表示不按级别过滤。</param>
    /// <param name="keyword">关键字；空白表示不按关键字过滤。大小写不敏感。</param>
    public static IReadOnlyList<LogEntry> Filter(
        IReadOnlyList<LogEntry> entries,
        IReadOnlyCollection<LogLevel>? levels,
        string? keyword)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var allLevels = levels is null || levels.Count == 0;
        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        if (allLevels && !hasKeyword)
        {
            return entries;
        }

        var result = new List<LogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!allLevels && !MatchesLevel(entry.Level, levels!))
            {
                continue;
            }

            if (hasKeyword
                && entry.Text.IndexOf(keyword!.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// 单行级别是否命中筛选条件。
    ///
    /// 两条刻意的取舍：
    /// <list type="number">
    /// <item><c>Debug</c> 归入「信息」档：界面上不提供「调试」chip，但勾了「信息」却丢掉调试行会更让人困惑。</item>
    /// <item>没有级别标记的行（崩溃报告正文、异常堆栈）**始终保留**：否则勾任何一级都会把最需要看的崩溃详情滤掉。</item>
    /// </list>
    /// </summary>
    public static bool MatchesLevel(LogLevel? level, IReadOnlyCollection<LogLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (level is null)
        {
            return true;
        }

        var effective = Normalize(level.Value);
        foreach (var wanted in levels)
        {
            if (Normalize(wanted) == effective)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把一行原始文本解析成 <see cref="LogEntry"/>。解析不出来时整行进 <see cref="LogEntry.Text"/>。</summary>
    public static LogEntry ParseLine(int lineNumber, string raw)
    {
        raw ??= string.Empty;

        var launcher = LauncherLinePattern.Match(raw);
        if (launcher.Success)
        {
            return new LogEntry(
                lineNumber,
                ParseLauncherTimestamp(launcher.Groups["ts"].Value),
                MapLevel(launcher.Groups["level"].Value),
                launcher.Groups["text"].Value);
        }

        var game = GameLinePattern.Match(raw);
        if (game.Success)
        {
            return new LogEntry(
                lineNumber,
                ParseGameTimestamp(game.Groups["ts"].Value),
                MapLevel(game.Groups["level"].Value),
                game.Groups["text"].Value);
        }

        return new LogEntry(lineNumber, null, null, raw);
    }

    private static LogLevel Normalize(LogLevel level) => level == LogLevel.Debug ? LogLevel.Info : level;

    private static LogLevel? MapLevel(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "DEBUG" or "TRACE" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARN" or "WARNING" => LogLevel.Warn,
        "ERROR" or "FATAL" => LogLevel.Error,
        _ => null,
    };

    /// <summary>
    /// <c>FileLogger</c> 写的是 <c>DateTime.Now</c>，所以按本地时间还原。
    /// 用 <see cref="DateTimeStyles.None"/> 让解析结果保持 Unspecified，再显式补当前偏移，
    /// 避免跨时区读旧日志时被「今天」的偏移篡改。
    /// </summary>
    private static DateTimeOffset? ParseLauncherTimestamp(string value)
    {
        DateTime stamp;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp)
            && !DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
        {
            return null;
        }

        return new DateTimeOffset(stamp, TimeZoneInfo.Local.GetUtcOffset(stamp));
    }

    /// <summary>游戏日志只有时分秒，日期按今天补——它总是「本次启动」写的，用今天的日期不会错。</summary>
    private static DateTimeOffset? ParseGameTimestamp(string value)
    {
        if (!TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, TimeSpanStyles.None, out var time))
        {
            return null;
        }

        var stamp = DateTime.SpecifyKind(DateTime.Today + time, DateTimeKind.Unspecified);
        return new DateTimeOffset(stamp, TimeZoneInfo.Local.GetUtcOffset(stamp));
    }

    private static LogTailResult ReadTail(string path, int maxLines, CancellationToken cancellationToken)
    {
        var limit = maxLines <= 0 ? ILauncherLogReader.DefaultMaxLines : maxLines;

        try
        {
            if (!File.Exists(path))
            {
                return LogTailResult.Missing();
            }

            // FileShare.ReadWrite | Delete：日志正被 <see cref="FileLogger"/> 追加、或被游戏进程占用时也要能读。
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length == 0)
            {
                return LogTailResult.Ok(Array.Empty<LogEntry>(), truncated: false);
            }

            var (lines, truncated) = ReadTailLines(stream, limit, cancellationToken);
            return LogTailResult.Ok(ParseLines(lines), truncated);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return LogTailResult.Failed(ex.Message);
        }
    }

    /// <summary>从末尾按块倒读，攒够行数即停。返回的 <c>Truncated</c> 表示前面还有没读进来的内容。</summary>
    private static (List<string> Lines, bool Truncated) ReadTailLines(
        FileStream stream,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var chunks = new List<byte[]>();
        var newlineCount = 0;
        var position = stream.Length;

        // 多留一行：窗口的第一行可能是从行中间截断的残段，多留一行才能保证至少 maxLines 行完整。
        var needed = maxLines + 1;

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = (int)Math.Min(ChunkSize, position);
            position -= count;

            var buffer = new byte[count];
            stream.Position = position;

            var read = 0;
            while (read < count)
            {
                var chunkRead = stream.Read(buffer, read, count - read);
                if (chunkRead <= 0)
                {
                    break;
                }

                read += chunkRead;
            }

            if (read == 0)
            {
                break;
            }

            if (read < count)
            {
                Array.Resize(ref buffer, read);
            }

            chunks.Add(buffer);
            newlineCount += CountNewlines(buffer, read);

            if (newlineCount >= needed)
            {
                break;
            }
        }

        // position 是已读窗口的起点：为 0 表示窗口覆盖了整个文件。
        var truncated = position > 0;

        var total = 0;
        foreach (var chunk in chunks)
        {
            total += chunk.Length;
        }

        var bytes = new byte[total];
        var offset = 0;
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            Buffer.BlockCopy(chunks[i], 0, bytes, offset, chunks[i].Length);
            offset += chunks[i].Length;
        }

        var all = Utf8.GetString(bytes).Split('\n');

        // 被截断时第一个元素是残段（起点落在行中间），丢掉；最后一个元素在文件以换行结尾时是空的，也丢掉。
        var start = truncated ? 1 : 0;
        var end = all.Length;
        if (end > start && all[end - 1].Length == 0)
        {
            end--;
        }

        if (end - start > maxLines)
        {
            start = end - maxLines;
            truncated = true;
        }

        var lines = new List<string>(Math.Max(0, end - start));
        for (var index = start; index < end; index++)
        {
            lines.Add(all[index]);
        }

        return (lines, truncated);
    }

    private static List<LogEntry> ParseLines(IReadOnlyList<string> lines)
    {
        var entries = new List<LogEntry>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];

            // Windows 换行：\r 会贴在行尾，不剥掉会让每行的正文末尾多一个不可见字符。
            if (raw.Length > 0 && raw[^1] == '\r')
            {
                raw = raw[..^1];
            }

            entries.Add(ParseLine(i + 1, raw));
        }

        return entries;
    }

    private static int CountNewlines(byte[] buffer, int length)
    {
        var count = 0;
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<LogFileInfo> EnumerateFiles(string directory, string pattern, LogFileKind kind)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<LogFileInfo>();
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<LogFileInfo>();
            }

            var result = new List<LogFileInfo>();
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                result.Add(new LogFileInfo(
                    file.FullName,
                    file.Name,
                    kind,
                    file.Length,
                    ToUtc(file.LastWriteTimeUtc)));
            }

            // 最新的日志排最前：排查问题时第一眼要看的就是刚才那次启动。
            result.Sort((left, right) => right.ModifiedUtc.CompareTo(left.ModifiedUtc));
            return result;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return Array.Empty<LogFileInfo>();
        }
    }

    private static void TryDescribe(string path, LogFileKind kind, List<LogFileInfo> sink)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            sink.Add(new LogFileInfo(info.FullName, info.Name, kind, info.Length, ToUtc(info.LastWriteTimeUtc)));
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            // 单个候选探测失败不该让整组列表消失。
        }
    }

    /// <summary>直接用 Ticks 构造，避免 <see cref="DateTime.Kind"/> 为 Unspecified 时被当成本地时间再偏移一次。</summary>
    private static DateTimeOffset ToUtc(DateTime value) => new(value.Ticks, TimeSpan.Zero);

    private static string Combine(string root, params string[] parts)
    {
        try
        {
            var combined = root;
            foreach (var part in parts)
            {
                combined = Path.Combine(combined, part);
            }

            return combined;
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string SafeDirectory(Func<string> accessor)
    {
        try
        {
            return accessor() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>「读不出来」的异常集合。全都不该冒泡到界面，由调用方转成 <see cref="LogReadStatus"/>。</summary>
    private static bool IsIoFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}
