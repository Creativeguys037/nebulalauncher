using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Logging;

namespace NebulaLauncher.Tests;

/// <summary>
/// 日志中心的数据层：枚举、尾部倒读、行解析、过滤。
/// 读取器必须「读不出来就报状态」，绝不把异常抛给界面——日志页本身挂掉是最难排查的故障。
/// </summary>
public class CoreLogs_LauncherLogReaderTests
{
    private static readonly string[] ExpectedEntryTexts = ["第一条", "第二条", "第三条"];

    private static readonly int[] ExpectedLineNumbers = [1, 2, 3];

    private static readonly int[] ExpectedFirstAndLastLineNumbers = [1, 100];

    // ==================== 枚举 ====================

    [Fact]
    public void ListLauncherLogs_WhenDirectoryMissing_ReturnsEmptyList()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        Directory.Delete(paths.LogsDirectory, recursive: true);

        var reader = new LauncherLogReader(paths);

        Assert.Empty(reader.ListLauncherLogs());
    }

    [Fact]
    public void ListLauncherLogs_ReturnsOnlyLogFilesNewestFirst()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var older = WriteLog(paths.LogsDirectory, "launcher-20261020.log", "older\n");
        var newer = WriteLog(paths.LogsDirectory, "launcher-20261022.log", "newer\n");
        WriteLog(paths.LogsDirectory, "settings.backup.json", "{}");

        File.SetLastWriteTimeUtc(older, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc));

        var reader = new LauncherLogReader(paths);
        var files = reader.ListLauncherLogs();

        Assert.Equal(2, files.Count);
        Assert.Equal("launcher-20261022.log", files[0].DisplayName);
        Assert.Equal("launcher-20261020.log", files[1].DisplayName);
        Assert.All(files, file => Assert.Equal(LogFileKind.Launcher, file.Kind));
        Assert.All(files, file => Assert.True(Path.IsPathRooted(file.Path)));
        Assert.True(files[0].ModifiedUtc > files[1].ModifiedUtc);
        Assert.True(files[0].SizeBytes > 0);
    }

    [Fact]
    public void ListGameLogs_ReturnsLatestLogThenCrashReports()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));
        var game = Path.Combine(temp.Path, "game");

        WriteLog(Path.Combine(game, "logs"), "latest.log", "[09:41:07] [main/INFO]: started\n");
        WriteLog(Path.Combine(game, "logs"), "2026-10-20-1.log.gz", "gz");
        WriteLog(Path.Combine(game, "crash-reports"), "crash-2026-10-22_09.41.07-client.txt", "报告");
        WriteLog(Path.Combine(game, "crash-reports"), "crash-2026-10-20_21.05.44-server.txt", "报告");
        WriteLog(Path.Combine(game, "crash-reports"), "notes.md", "不是崩溃报告");

        var files = reader.ListGameLogs(game);

        Assert.Equal(3, files.Count);
        Assert.Equal(LogFileKind.Game, files[0].Kind);
        Assert.Equal("latest.log", files[0].DisplayName);
        Assert.Equal(2, files.Count(file => file.Kind == LogFileKind.Crash));
        Assert.All(files, file => Assert.False(string.IsNullOrWhiteSpace(file.DisplayName)));
    }

    [Fact]
    public void ListGameLogs_WhenGameDirectoryMissingOrBlank_ReturnsEmptyList()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));

        Assert.Empty(reader.ListGameLogs(Path.Combine(temp.Path, "no-such-game")));
        Assert.Empty(reader.ListGameLogs(string.Empty));
        Assert.Empty(reader.ListGameLogs("   "));
    }

    // ==================== 尾部读取 ====================

    [Fact]
    public async Task ReadTailAsync_WhenFileMissing_ReturnsMissingStatus()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));

        var result = await reader.ReadTailAsync(Path.Combine(temp.Path, "launcher-19700101.log"));

        Assert.Equal(LogReadStatus.FileMissing, result.Status);
        Assert.Empty(result.Entries);
        Assert.False(result.Truncated);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ReadTailAsync_WhenPathBlank_ReturnsMissingStatus()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));

        Assert.Equal(LogReadStatus.FileMissing, (await reader.ReadTailAsync(string.Empty)).Status);
        Assert.Equal(LogReadStatus.FileMissing, (await reader.ReadTailAsync("  ")).Status);
    }

    [Fact]
    public async Task ReadTailAsync_EmptyFile_ReturnsSuccessWithoutEntries()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));
        var path = WriteLog(temp.Path, "empty.log", string.Empty);

        var result = await reader.ReadTailAsync(path);

        Assert.Equal(LogReadStatus.Success, result.Status);
        Assert.Empty(result.Entries);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ReadTailAsync_ReturnsEveryLineInFileOrder()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));
        var path = WriteLog(
            temp.Path,
            "launcher-20261022.log",
            "2026-10-22 09:41:07.218 [INFO ] 第一条\r\n" +
            "2026-10-22 09:41:08.000 [WARN ] 第二条\n" +
            "2026-10-22 09:41:09.500 [ERROR] 第三条\n");

        var result = await reader.ReadTailAsync(path);

        Assert.Equal(LogReadStatus.Success, result.Status);
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Entries.Count);

        Assert.Equal(ExpectedEntryTexts, result.Entries.Select(entry => entry.Text));
        Assert.Equal(
            new LogLevel?[] { LogLevel.Info, LogLevel.Warn, LogLevel.Error },
            result.Entries.Select(entry => entry.Level));
        Assert.Equal(ExpectedLineNumbers, result.Entries.Select(entry => entry.LineNumber));
        Assert.All(result.Entries, entry => Assert.NotNull(entry.Timestamp));
        Assert.Equal(7, result.Entries[0].Timestamp!.Value.Second);
    }

    [Fact]
    public async Task ReadTailAsync_WhenFileSpansManyChunks_KeepsOnlyLastLinesAndTrimsPartialHead()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));

        var builder = new StringBuilder();
        for (var i = 1; i <= 5000; i++)
        {
            builder.Append("2026-10-22 09:41:07.218 [INFO ] 填充内容填充内容填充内容 ").Append(i).Append('\n');
        }

        var path = WriteLog(temp.Path, "big.log", builder.ToString());

        var result = await reader.ReadTailAsync(path, maxLines: 100);

        Assert.Equal(LogReadStatus.Success, result.Status);
        Assert.True(result.Truncated);
        Assert.Equal(100, result.Entries.Count);
        Assert.Equal("填充内容填充内容填充内容 4901", result.Entries[0].Text);
        Assert.Equal("填充内容填充内容填充内容 5000", result.Entries[^1].Text);
        Assert.Equal(ExpectedFirstAndLastLineNumbers, new[] { result.Entries[0].LineNumber, result.Entries[^1].LineNumber });
        Assert.All(result.Entries, entry => Assert.Equal<LogLevel?>(LogLevel.Info, entry.Level));
    }

    [Fact]
    public async Task ReadTailAsync_WhenFileShorterThanLimit_IsNotTruncated()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));

        var builder = new StringBuilder();
        for (var i = 1; i <= 20; i++)
        {
            builder.Append("2026-10-22 09:41:07.218 [INFO ] 行 ").Append(i).Append('\n');
        }

        var path = WriteLog(temp.Path, "small.log", builder.ToString());

        var result = await reader.ReadTailAsync(path, maxLines: 2000);

        Assert.False(result.Truncated);
        Assert.Equal(20, result.Entries.Count);
        Assert.Equal("行 20", result.Entries[^1].Text);
    }

    [Fact]
    public async Task ReadTailAsync_WhenMaxLinesIsZero_UsesDefaultLimit()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));
        var path = WriteLog(temp.Path, "one.log", "2026-10-22 09:41:07.218 [INFO ] 唯一一行\n");

        var result = await reader.ReadTailAsync(path, maxLines: 0);

        Assert.Equal(LogReadStatus.Success, result.Status);
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task ReadTailAsync_WhileFileIsStillBeingAppended_StillReads()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));
        var path = WriteLog(temp.Path, "live.log", "2026-10-22 09:41:07.218 [INFO ] 已经写下的行\n");

        // FileLogger 走 File.AppendAllText，句柄允许共享读；日志页打开时日志还在增长是常态。
        using var appender = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        var result = await reader.ReadTailAsync(path);

        Assert.Equal(LogReadStatus.Success, result.Status);
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task ReadTailAsync_WhenFileIsExclusivelyLocked_ReturnsReadFailed()
    {
        using var temp = new TempDirectory();
        var reader = new LauncherLogReader(new FakeLauncherPaths(temp.Path));
        var path = WriteLog(temp.Path, "locked.log", "2026-10-22 09:41:07.218 [INFO ] 被占用\n");

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await reader.ReadTailAsync(path);

        Assert.Equal(LogReadStatus.ReadFailed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Entries);
    }

    // ==================== 行解析 ====================

    [Fact]
    public void ParseLine_FileLoggerOutput_ParsesTimestampAndPaddedLevel()
    {
        // FileLogger 的级别标记是定宽 5 字符，INFO / WARN 各带一个尾随空格。
        var info = LauncherLogReader.ParseLine(2, "2026-10-22 09:41:07.218 [INFO ] 已加载 12 个版本");
        var warn = LauncherLogReader.ParseLine(3, "2026-10-22 09:41:08.500 [WARN ] 下载源回退");
        var error = LauncherLogReader.ParseLine(4, "2026-10-22 09:41:09.001 [ERROR] 启动失败");

        Assert.Equal<LogLevel?>(LogLevel.Info, info.Level);
        Assert.Equal("已加载 12 个版本", info.Text);
        Assert.Equal(2, info.LineNumber);
        Assert.Equal(2026, info.Timestamp!.Value.Year);

        Assert.Equal<LogLevel?>(LogLevel.Warn, warn.Level);
        Assert.Equal<LogLevel?>(LogLevel.Error, error.Level);
        Assert.Equal("启动失败", error.Text);
    }

    [Fact]
    public void ParseLine_GameLogOutput_ParsesThreadAndLevel()
    {
        var entry = LauncherLogReader.ParseLine(1, "[09:41:07] [Render thread/WARN]: 材质加载失败");

        Assert.Equal<LogLevel?>(LogLevel.Warn, entry.Level);
        Assert.Equal("材质加载失败", entry.Text);
        Assert.NotNull(entry.Timestamp);
        Assert.Equal(9, entry.Timestamp!.Value.Hour);
    }

    [Fact]
    public void ParseLine_GameLogFatal_IsTreatedAsError()
    {
        var entry = LauncherLogReader.ParseLine(1, "[09:41:10] [main/FATAL]: 崩溃");

        Assert.Equal<LogLevel?>(LogLevel.Error, entry.Level);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\tat net.minecraft.client.Main.main(Main.java:42)")]
    [InlineData("Caused by: java.lang.OutOfMemoryError: Java heap space")]
    [InlineData("---- Minecraft Crash Report ----")]
    [InlineData("2026-10-22 不是完整时间戳")]
    public void ParseLine_UnrecognizedLine_KeepsWholeLineAsText(string raw)
    {
        var entry = LauncherLogReader.ParseLine(9, raw);

        Assert.Null(entry.Level);
        Assert.Null(entry.Timestamp);
        Assert.Equal(raw, entry.Text);
        Assert.Equal(9, entry.LineNumber);
    }

    // ==================== 过滤（纯函数） ====================

    [Fact]
    public void Filter_WhenNoConditions_ReturnsSameInstance()
    {
        var entries = new[] { new LogEntry(1, null, LogLevel.Info, "x") };

        Assert.Same(entries, LauncherLogReader.Filter(entries, null, null));
        Assert.Same(entries, LauncherLogReader.Filter(entries, Array.Empty<LogLevel>(), "   "));
    }

    [Fact]
    public void Filter_ByLevel_KeepsUnlabeledLinesAndFoldsDebugIntoInfo()
    {
        var entries = new[]
        {
            new LogEntry(1, null, LogLevel.Debug, "调试"),
            new LogEntry(2, null, LogLevel.Info, "信息"),
            new LogEntry(3, null, LogLevel.Warn, "警告"),
            new LogEntry(4, null, LogLevel.Error, "错误"),
            new LogEntry(5, null, null, "崩溃报告正文"),
        };

        var infoOnly = new[] { 1, 2, 5 };
        var warnOnly = new[] { 3, 5 };
        var errorOnly = new[] { 4, 5 };
        var warnAndError = new[] { 3, 4, 5 };

        Assert.Equal(infoOnly, Numbers(LauncherLogReader.Filter(entries, [LogLevel.Info], null)));
        Assert.Equal(warnOnly, Numbers(LauncherLogReader.Filter(entries, [LogLevel.Warn], null)));
        Assert.Equal(errorOnly, Numbers(LauncherLogReader.Filter(entries, [LogLevel.Error], null)));
        Assert.Equal(warnAndError, Numbers(LauncherLogReader.Filter(entries, [LogLevel.Warn, LogLevel.Error], null)));
    }

    [Fact]
    public void Filter_ByKeyword_IsCaseInsensitiveAndCombinesWithLevel()
    {
        var entries = new[]
        {
            new LogEntry(1, null, LogLevel.Error, "HTTP 403 Forbidden"),
            new LogEntry(2, null, LogLevel.Error, "java.lang.OutOfMemoryError"),
            new LogEntry(3, null, LogLevel.Info, "403 回退到镜像"),
            new LogEntry(4, null, null, "  Forbidden 在崩溃报告里"),
        };

        var forbidden = new[] { 1, 4 };
        var forbidden403 = new[] { 1 };

        Assert.Equal(forbidden, Numbers(LauncherLogReader.Filter(entries, null, "forbidden")));
        Assert.Equal(forbidden403, Numbers(LauncherLogReader.Filter(entries, [LogLevel.Error], " 403 ")));
        Assert.Empty(LauncherLogReader.Filter(entries, null, "不存在的关键字"));
    }

    [Fact]
    public void MatchesLevel_UnlabeledLineAlwaysMatches()
    {
        Assert.True(LauncherLogReader.MatchesLevel(null, [LogLevel.Error]));
        Assert.True(LauncherLogReader.MatchesLevel(LogLevel.Debug, [LogLevel.Info]));
        Assert.False(LauncherLogReader.MatchesLevel(LogLevel.Debug, [LogLevel.Warn]));
        Assert.True(LauncherLogReader.MatchesLevel(LogLevel.Error, [LogLevel.Error, LogLevel.Warn]));
        Assert.False(LauncherLogReader.MatchesLevel(LogLevel.Info, Array.Empty<LogLevel>()));
    }

    private static int[] Numbers(IReadOnlyList<LogEntry> entries) =>
        entries.Select(entry => entry.LineNumber).ToArray();

    private static string WriteLog(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
