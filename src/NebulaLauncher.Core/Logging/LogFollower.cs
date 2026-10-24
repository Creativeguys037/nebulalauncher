using System.Text;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Logging;

/// <summary>
/// 日志跟随的增量读取实现：给定上次读到的字节偏移，只返回之后新写下的完整行。
///
/// 三条设计判据：
/// <list type="number">
/// <item><b>只读</b>：用 <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> 打开，
/// 游戏进程或 <see cref="FileLogger"/> 正在追加时也要能读，且绝不写回文件。</item>
/// <item><b>只返回完整的行</b>：偏移推进到最后一个 <c>\n</c> 之后。进程被杀在半行上（HANDOVER §6.30）
/// 时那半行不会被吞掉，也不会有「读到一半的行」闪进界面。</item>
/// <item><b>自愈</b>：文件变短按截断处理、偏移不再落在行边界按轮转处理，两种情况都从头重读。
/// 游戏每启动一次就重建 <c>latest.log</c>，跟随不能因此停摆。</item>
/// </list>
///
/// 为什么轮转判据是「offset-1 处不是换行」而不是文件 ID / 创建时间：
/// 本接口刻意无状态（只吃 <c>offset</c>），跨平台 .NET 没有稳定的文件 ID；
/// 文件被替换成新内容时，上次推进到「某个换行的下一字节」这个前提不再成立，这已经够抓住真实场景。
/// 若新文件恰好更长且该位置仍是换行，则会漏判一次——代价是多显示一段旧内容，而不是丢新内容。
/// </summary>
public sealed class LogFollower : ILogFollowService
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public Task<LogFollowResult> ReadSinceAsync(
        string path,
        long offset,
        int maxBytes = ILogFollowService.DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        // 读盘放线程池：跟随每 700ms 跑一次，网络盘 / 机械盘上的同步读会直接卡住 UI 线程。
        return Task.Run(() => ReadSince(path, offset, maxBytes, cancellationToken), cancellationToken);
    }

    private static LogFollowResult ReadSince(
        string path,
        long offset,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var start = Math.Max(0, offset);

        if (string.IsNullOrWhiteSpace(path))
        {
            return LogFollowResult.Missing(start);
        }

        var limit = maxBytes <= 0 ? ILogFollowService.DefaultMaxBytes : maxBytes;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            var status = LogFollowStatus.Appended;

            if (start > length)
            {
                // 文件比上次记的位置短：被截断（清空重写）或换了一份更短的文件。
                start = 0;
                status = LogFollowStatus.Truncated;
            }
            else if (start > 0 && !IsLineBoundary(stream, start))
            {
                // 上次的偏移落在行中间：内容已经不是我们读过的那个文件了（游戏轮转了 latest.log）。
                start = 0;
                status = LogFollowStatus.Rotated;
            }

            var available = length - start;
            if (available <= 0)
            {
                return new LogFollowResult(Array.Empty<LogEntry>(), length, status);
            }

            var toRead = (int)Math.Min(limit, available);
            var buffer = new byte[toRead];

            stream.Position = start;
            var read = 0;
            while (read < toRead)
            {
                var chunk = stream.Read(buffer, read, toRead - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read == 0)
            {
                return new LogFollowResult(Array.Empty<LogEntry>(), start, status);
            }

            // 只吐完整的行：最后一个换行之上的内容是这一批，之后的半行留给下个周期。
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0)
            {
                // 单次上限内没有出现过换行（一行超长 / 文件刚被创建还没写完）：
                // 偏移不推进，避免把半行切成两段重复显示。
                return new LogFollowResult(Array.Empty<LogEntry>(), start, status);
            }

            var consumed = lastNewline + 1;
            var entries = ParseLines(Utf8.GetString(buffer, 0, consumed));
            return new LogFollowResult(entries, start + consumed, status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return LogFollowResult.Failed(start, ex.Message);
        }
    }

    /// <summary><paramref name="offset"/> 是否落在行边界（前一字节是换行）。</summary>
    private static bool IsLineBoundary(FileStream stream, long offset)
    {
        if (offset <= 0)
        {
            return true;
        }

        if (offset > stream.Length)
        {
            return false;
        }

        stream.Position = offset - 1;
        return stream.ReadByte() == (byte)'\n';
    }

    private static List<LogEntry> ParseLines(string text)
    {
        // 文本以 '\n' 结尾，Split 出来的最后一个元素是空串，跳过它。
        var parts = text.Split('\n');
        var count = parts.Length;
        if (count > 0 && parts[count - 1].Length == 0)
        {
            count--;
        }

        var entries = new List<LogEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = parts[i];

            // Windows 换行：\r 贴在行尾，不剥掉会让正文末尾多一个不可见字符。
            if (raw.Length > 0 && raw[^1] == '\r')
            {
                raw = raw[..^1];
            }

            entries.Add(LauncherLogReader.ParseLine(i + 1, raw));
        }

        return entries;
    }

    /// <summary>「读不出来」的异常集合。全都不该冒泡到界面，由调用方转成「暂时不可读」。</summary>
    private static bool IsIoFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}