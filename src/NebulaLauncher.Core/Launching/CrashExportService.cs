using System.Globalization;
using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Logging;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 崩溃报告导出：把归因结论、游戏输出尾部、<c>hs_err</c>、启动器日志尾部、系统快照与模组列表
/// 打成一个**脱敏** zip，让用户能直接发给他人求助。
///
/// 三条硬约束：
/// <list type="number">
/// <item>所有文本在写进 zip 之前都要过 <see cref="LogRedactor"/>：令牌、账户名、本机用户目录。</item>
/// <item>打开别人的 zip 就能看懂——<c>crash-analysis.txt</c> 头部写明启动器版本、导出时间与脱敏说明。</item>
/// <item>不覆盖已有文件，同名自动加序号；导出失败不留半成品 zip。</item>
/// </list>
/// </summary>
public sealed class CrashExportService : ICrashExportService
{
    /// <summary>启动器日志最多带的尾部行数。与日志中心的上限一致。</summary>
    public const int LauncherLogTailLines = 2000;

    /// <summary>每秒最多允许多少次同名探测，纯粹防御异常调用方。</summary>
    private const int MaxSequenceProbes = 1000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILauncherPaths _paths;
    private readonly ILauncherLogReader? _logReader;
    private readonly IAppLog? _log;

    public CrashExportService(ILauncherPaths paths, ILauncherLogReader? logReader = null, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logReader = logReader;
        _log = log;
    }

    /// <inheritdoc />
    public bool CanWriteTo(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(directory, $".nebula-write-{Guid.NewGuid():N}.tmp");

            // DeleteOnClose：探针不会留下来，也不需要 try/finally 清理。
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            stream.WriteByte(0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log?.Warn($"导出目录不可写：{directory}", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<CrashExportResult> ExportAsync(
        CrashExportRequest request,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Report);

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return CrashExportResult.Fail("没有选择导出目录。");
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CrashExportResult.Fail($"无法创建导出目录：{ex.Message}");
        }

        if (!CanWriteTo(directory))
        {
            return CrashExportResult.Fail($"导出目录不可写：{directory}");
        }

        string? archivePath = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var baseName = $"{ICrashExportService.FileNamePrefix}-{stamp}";
            archivePath = ResolveFreePath(directory, baseName);

            var entries = new List<string>(7);
            var gameOutput = BuildGameOutputText(request.GameOutputLines);
            var launcherLog = await BuildLauncherLogTextAsync(cancellationToken).ConfigureAwait(false);
            var system = BuildSystemText(request.System);
            var analysis = BuildAnalysisText(request, gameOutput);
            var mods = BuildModsListText(request);

            using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(archive, "crash-analysis.txt", analysis);
                entries.Add("crash-analysis.txt");

                WriteEntry(archive, "game-output.txt", gameOutput);
                entries.Add("game-output.txt");

                if (!string.IsNullOrWhiteSpace(request.HsErrText))
                {
                    WriteEntry(archive, "hs_err.txt", LogRedactor.Redact(request.HsErrText) ?? string.Empty);
                    entries.Add("hs_err.txt");
                }

                WriteEntry(archive, "launcher-log.txt", launcherLog);
                entries.Add("launcher-log.txt");

                WriteEntry(archive, "mods-list.txt", mods);
                entries.Add("mods-list.txt");

                WriteEntry(archive, "system.txt", system);
                entries.Add("system.txt");
            }

            var bytes = new FileInfo(archivePath).Length;
            _log?.Info($"崩溃报告已导出：{archivePath}（{bytes} 字节，{entries.Count} 个条目）");

            return CrashExportResult.Ok(archivePath, bytes, entries);
        }
        catch (OperationCanceledException)
        {
            // 取消也把半成品删掉：留一个残缺 zip 比没有更糟。
            TryDelete(archivePath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(archivePath);
            _log?.Error("导出崩溃报告失败", ex);
            return CrashExportResult.Fail($"导出崩溃报告失败：{ex.Message}");
        }
    }

    // ==================== 各类文本 ====================

    /// <summary>
    /// 结论文件。头部三行（版本 / 时间 / 脱敏说明）是给「拿到别人机器上」准备的：
    /// 没有它们，一份只有堆栈的 txt 无法判断是哪个启动器、什么时候、有没有被改过。
    /// </summary>
    private static string BuildAnalysisText(CrashExportRequest request, string gameOutput)
    {
        var report = request.Report;
        var sb = new StringBuilder(4096);

        // 时间与版本先格式化成不变文化字符串：导出文件会跨机器、跨区域设置被打开发。
        var exportedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var launcherVersion = string.IsNullOrWhiteSpace(request.System?.LauncherVersion)
            ? "未知"
            : request.System!.LauncherVersion!;

        sb.AppendLine("Nebula Launcher 崩溃报告");
        sb.AppendLine("导出时间: " + exportedAt);
        sb.AppendLine("启动器版本: " + launcherVersion);
        sb.AppendLine("生成方式: 首页「导出崩溃报告」。本文件可离线阅读，不需要启动器。");
        sb.AppendLine("脱敏说明: 访问令牌 / 会话标识 / 账户名 / 本机用户目录已替换为 *** 或 <user>，其余内容原样保留。");

        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        sb.AppendLine("[结论]");
        sb.AppendLine(string.IsNullOrWhiteSpace(report.Summary) ? "未能从本次输出中给出结论。" : report.Summary);
        sb.AppendLine();

        sb.AppendLine("[建议]");
        if (report.Suggestions.Count == 0)
        {
            sb.AppendLine("（没有可执行的建议）");
        }
        else
        {
            var index = 1;
            foreach (var suggestion in report.Suggestions)
            {
                sb.Append(index++).Append(". ").AppendLine(suggestion.Title);
                sb.Append("   ").AppendLine(suggestion.Detail);

                if (!string.IsNullOrWhiteSpace(suggestion.ActionId))
                {
                    sb.Append("   动作: ").AppendLine(suggestion.ActionId);
                }
            }
        }

        sb.AppendLine();

        sb.AppendLine("[疑似模组]");
        var suspects = FindSuspectMods(gameOutput);
        if (suspects.Count == 0)
        {
            sb.AppendLine("（没有从输出里识别出具体模组文件名）");
        }
        else
        {
            foreach (var file in suspects)
            {
                sb.Append("- ").AppendLine(file);
            }
        }

        sb.AppendLine();

        sb.AppendLine("[崩溃报告文件]");
        sb.AppendLine(string.IsNullOrWhiteSpace(report.CrashReportPath)
            ? "（本次没有定位到 crash-report 文件）"
            : report.CrashReportPath!);

        sb.AppendLine();
        sb.AppendLine("[原始输出尾部]");
        sb.AppendLine("（完整尾部见 game-output.txt）");
        sb.AppendLine();

        var tail = CrashAnalyzer.JoinTail(gameOutput.Split('\n'), CrashAnalyzer.DefaultTailLines);
        sb.AppendLine(string.IsNullOrWhiteSpace(tail) ? "（没有抓到游戏输出）" : tail);

        return LogRedactor.Redact(sb.ToString()) ?? string.Empty;
    }

    /// <summary>游戏输出尾部。只留 <see cref="CrashAnalyzer.DefaultTailLines"/> 行：崩溃前的陈年日志对排查没用。</summary>
    private static string BuildGameOutputText(IReadOnlyList<string>? lines)
    {
        var joined = lines is null || lines.Count == 0
            ? string.Empty
            : CrashAnalyzer.JoinTail(lines, CrashAnalyzer.DefaultTailLines);

        if (string.IsNullOrWhiteSpace(joined))
        {
            joined = "（没有抓到游戏标准输出）";
        }

        return LogRedactor.Redact(joined) ?? string.Empty;
    }

    /// <summary>启动器日志尾部。读不到时留下说明而不是少一个条目——条目数恒定，接收方不会以为漏打包。</summary>
    private async Task<string> BuildLauncherLogTextAsync(CancellationToken cancellationToken)
    {
        if (_logReader is null)
        {
            // 没有读取器时直接按路径读：导出是低频操作，不值得为它再引一层依赖。
            return await ReadLauncherLogDirectAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var files = _logReader.ListLauncherLogs();
            if (files.Count == 0)
            {
                return await ReadLauncherLogDirectAsync(cancellationToken).ConfigureAwait(false);
            }

            // ListLauncherLogs 已按修改时间倒序，第一条就是当前正在写的那个。
            var result = await _logReader
                .ReadTailAsync(files[0].Path, LauncherLogTailLines, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Status)
            {
                case LogReadStatus.FileMissing:
                    return $"（日志文件已不存在：{files[0].Path}）";

                case LogReadStatus.ReadFailed:
                    return $"（读取日志失败：{result.Error}）";
            }

            if (result.Entries.Count == 0)
            {
                return "（启动器日志是空的）";
            }

            var sb = new StringBuilder(result.Entries.Count * 96);
            sb.Append("来源: ").AppendLine(files[0].Path);

            if (result.Truncated)
            {
                sb.Append("只包含最后 ").Append(result.Entries.Count).AppendLine(" 行。");
            }

            sb.AppendLine();

            foreach (var entry in result.Entries)
            {
                sb.AppendLine(FormatLogEntry(entry));
            }

            return LogRedactor.Redact(sb.ToString()) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（读取启动器日志失败：{ex.Message}）";
        }
    }

    /// <summary>
    /// 不经过 <see cref="ILauncherLogReader"/> 的兜底读取：流式扫一遍，只留最后 N 行（内存 O(N)）。
    /// 启动器日志按天滚动，单文件通常几 MB，扫一遍的成本远低于「导出缺了日志」的代价。
    /// </summary>
    private async Task<string> ReadLauncherLogDirectAsync(CancellationToken cancellationToken)
    {
        string? path;

        try
        {
            var directory = _paths.LogsDirectory;
            path = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                    .Select(file => new FileInfo(file))
                    .Where(info => info.Exists)
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Select(info => info.FullName)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（枚举启动器日志失败：{ex.Message}）";
        }

        if (path is null)
        {
            return "（没有找到启动器日志文件）";
        }

        try
        {
            var tail = new Queue<string>(LauncherLogTailLines);

            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                tail.Enqueue(line);
                if (tail.Count > LauncherLogTailLines)
                {
                    tail.Dequeue();
                }
            }

            if (tail.Count == 0)
            {
                return "（启动器日志是空的）";
            }

            var sb = new StringBuilder(tail.Count * 96);
            sb.Append("来源: ").AppendLine(path);
            sb.AppendLine();

            foreach (var line in tail)
            {
                sb.AppendLine(line);
            }

            return LogRedactor.Redact(sb.ToString()) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（读取启动器日志失败：{ex.Message}）";
        }
    }

    /// <summary>
    /// 系统快照。与设置页的「复制诊断信息」调同一个 <see cref="DiagnosticsSnapshot.Create"/>，
    /// 这里只额外补 Core 自己就能取到的两项。
    /// </summary>
    private static string BuildSystemText(DiagnosticsInput? input)
    {
        var effective = input ?? new DiagnosticsInput();

        if (string.IsNullOrWhiteSpace(effective.PlatformName))
        {
            effective = effective with { PlatformName = Environment.OSVersion.ToString() };
        }

        if (string.IsNullOrWhiteSpace(effective.DotNetVersion))
        {
            effective = effective with { DotNetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription };
        }

        return LogRedactor.Redact(DiagnosticsSnapshot.Create(effective)) ?? string.Empty;
    }

    /// <summary>
    /// 模组列表。条目恒定存在：没有 mods、读目录失败都写明确文案，
    /// 接收方不需要猜「这份报告是不是漏打包了 mods-list」。
    ///
    /// 一行一个模组：<c>[启用] 文件名（版本 x）  大小</c>；按文件名不区分大小写排序，
    /// 保证两次导出同一目录得到同样的顺序，便于对比。
    /// </summary>
    private static string BuildModsListText(CrashExportRequest request)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("Nebula Launcher 模组列表");

        if (!string.IsNullOrWhiteSpace(request.ModsError))
        {
            sb.AppendLine(request.ModsError);
            return LogRedactor.Redact(sb.ToString()) ?? string.Empty;
        }

        var mods = request.Mods ?? Array.Empty<ModSnapshot>();

        if (mods.Count == 0)
        {
            sb.AppendLine("总数: 0（启用: 0，禁用: 0）");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("未安装模组");
            return LogRedactor.Redact(sb.ToString()) ?? string.Empty;
        }

        var enabled = mods.Count(mod => mod.Enabled);
        sb.Append("总数: ").Append(mods.Count)
          .Append("（启用: ").Append(enabled)
          .Append("，禁用: ").Append(mods.Count - enabled)
          .AppendLine("）");
        sb.AppendLine(new string('=', 60));

        foreach (var mod in mods.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(mod.Enabled ? "[启用] " : "[禁用] ").Append(mod.FileName);

            if (!string.IsNullOrWhiteSpace(mod.VersionText))
            {
                sb.Append("（版本 ").Append(mod.VersionText).Append('）');
            }

            sb.Append("  ").AppendLine(FormatFileSize(mod.SizeBytes));
        }

        return LogRedactor.Redact(sb.ToString()) ?? string.Empty;
    }

    /// <summary>模组列表里的文件大小。小于 1 KB 也至少保留 B；负数表示读不到，写「大小未知」。</summary>
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 0)
        {
            return "大小未知";
        }

        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= 1024)
        {
            return (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }

    /// <summary>
    /// 从游戏输出里找肇事 jar。两步与 <see cref="CrashAnalyzer"/> 一致：
    /// 先按堆栈提包名，再在「Mod List」「valid mod file」这类行里找撞上包名的 jar。
    /// </summary>
    private static IReadOnlyList<string> FindSuspectMods(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        try
        {
            var keywords = CrashAnalyzer.FindSuspectPackageNames(text);
            return keywords.Count == 0
                ? Array.Empty<string>()
                : CrashAnalyzer.FindSuspectModFiles(text, keywords);
        }
        catch (Exception)
        {
            // 启发式失败不该让整份报告导不出去。
            return Array.Empty<string>();
        }
    }

    /// <summary>把解析后的日志行还原成与 <c>FileLogger</c> 输出一致的一行，保留时间戳与级别。</summary>
    private static string FormatLogEntry(LogEntry entry)
    {
        if (entry.Timestamp is not { } timestamp || entry.Level is not { } level)
        {
            // 没有时间戳的行（堆栈、崩溃报告正文）原样输出，保留缩进。
            return entry.Text;
        }

        var label = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            _ => "ERROR",
        };

        return $"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{label}] {entry.Text}";
    }

    // ==================== 文件与 zip ====================

    private static string ResolveFreePath(string directory, string baseName)
    {
        var path = Path.Combine(directory, baseName + ".zip");
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        for (var index = 2; index < MaxSequenceProbes; index++)
        {
            var candidate = Path.Combine(directory, $"{baseName}-{index}.zip");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // 极端情况下（同秒内 1000 次）退到 GUID，绝不覆盖已有文件。
        return Path.Combine(directory, $"{baseName}-{Guid.NewGuid():N}.zip");
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
        writer.Write(content);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响已经返回的失败结果。
        }
    }
}
