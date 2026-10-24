using System.Globalization;
using System.Text;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Logging;

/// <summary>
/// 按天滚动的文件日志。<c>logs/launcher-YYYYMMDD.log</c>。
/// 日志是最后一道防线：本类写盘过程中的任何异常都必须被吞掉，否则会顶掉真正的业务异常。
/// </summary>
public sealed class FileLogger : IAppLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly object _gate = new();

    public FileLogger(ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = paths.LogsDirectory;
    }

    /// <inheritdoc />
    public string LogFilePath => GetCurrentPath();

    /// <inheritdoc />
    public void Debug(string message) => Write("DEBUG", message, null);

    /// <inheritdoc />
    public void Info(string message) => Write("INFO ", message, null);

    /// <inheritdoc />
    public void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    /// <inheritdoc />
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private string GetCurrentPath() =>
        Path.Combine(_directory, $"launcher-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder(128);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" [").Append(level).Append("] ")
              .Append(LogRedactor.Redact(message) ?? string.Empty);

            if (ex is not null)
            {
                // 异常消息里同样可能带 URL 查询串（token 常拼在 URL 上），整段一起脱敏。
                sb.Append(Environment.NewLine).Append(LogRedactor.Redact(ex.ToString()));
            }

            sb.Append(Environment.NewLine);

            // 锁只保护一次追加，避免后台线程与 UI 线程交错写坏行。
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);

                var path = GetCurrentPath();
                EnsureTrailingNewline(path);
                File.AppendAllText(path, sb.ToString(), Utf8NoBom);
            }
        }
        catch
        {
            // 磁盘满、目录被占用、权限不足都只能放弃这条日志。
        }
    }

    /// <summary>本次进程是否已经检查过当日日志的末尾换行。</summary>
    private bool _tailChecked;

    /// <summary>
    /// 上一个进程被杀在半行上时（截图模式用 <c>Environment.Exit</c> 退出，正在写的那一行没有换行符），
    /// 新进程的第一条日志会接在那半行后面，读起来像一条乱码。这里补一个换行再写。
    /// 只检查一次：跨天换文件时新文件必然存在且完整。
    /// </summary>
    private void EnsureTrailingNewline(string path)
    {
        if (_tailChecked)
        {
            return;
        }

        _tailChecked = true;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return;
            }

            stream.Seek(-1, SeekOrigin.End);
            var last = stream.ReadByte();

            // Windows 上是 \n（上一行以 \r\n 结尾）；Unix 上同样以 \n 结尾。
            if (last != '\n')
            {
                File.AppendAllText(path, Environment.NewLine, Utf8NoBom);
            }
        }
        catch (FileNotFoundException)
        {
            // 首次写当天日志，文件还不存在。
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception)
        {
            // 读不出来就照常写：补换行只是排版，不值得让日志失败。
        }
    }
}
