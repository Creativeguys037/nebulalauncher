using System.Globalization;
using System.Text;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 安装器 stdout 的日志通道。
///
/// 为什么需要：Forge / NeoForge 的处理器（installertools、binarypatcher、ForgeAutoRenamingTool）
/// 是外部 java 进程，失败原因只出现在它们的 stdout 里。上一轮为了判断「进入处理器阶段」
/// 接了 <c>InstallerOutput</c> 却把内容丢掉，结果是处理器失败时日志里只有「进入阶段」、
/// 没有原始报错。这里把每一行落进 <see cref="IAppLog"/>，同时做三件事，避免把日志刷爆：
///
/// 1. **截断**：单行最长 <see cref="MaxLineLength"/> 字符（超长补省略号）；
/// 2. **限量**：每次安装最多写 <see cref="MaxLines"/> 行，超出后只记一条「已省略」；
/// 3. **折叠**：连续重复的行只写一次并标注「（重复 N 次）」。折叠通过「把上一行挂起到下一行
///    到来时再写」实现——<see cref="IAppLog"/> 只能追加，没法事后给已写的行补计数。
///
/// 级别：含 <c>error</c> / <c>failed</c> / <c>exception</c>（不区分大小写）的行走 Warn，其余 Debug。
/// 安装失败时调用 <see cref="FlushFailure"/>，把最后 <see cref="TailLines"/> 行以 Info 再写一遍——
/// 生产环境的日志级别通常不低于 Info，Debug 行看不到，失败现场必须能直接读。
/// </summary>
internal sealed class InstallerOutputLog
{
    /// <summary>单行最大字符数。CmlLib 会把 java 进程的整段输出按行转发，偶尔出现超长行。</summary>
    public const int MaxLineLength = 500;

    /// <summary>每次安装最多写多少行常规日志（失败提升的尾部不计入）。</summary>
    public const int MaxLines = 200;

    /// <summary>失败时提升为 Info 的尾部行数。</summary>
    public const int TailLines = 20;

    /// <summary>连续重复的标注格式。N 是「除第一行以外的重复次数」。</summary>
    public const string RepeatFormat = "（重复 {0} 次）";

    /// <summary>
    /// <see cref="RepeatFormat"/> 的预解析形态。用 <see cref="CompositeFormat"/> 而不是每次
    /// <c>string.Format(格式串, 值)</c>：后者每次都要重新解析占位符，且默认走当前区域设置（CA1863 / CA1305）。
    /// </summary>
    private static readonly CompositeFormat RepeatComposite = CompositeFormat.Parse(RepeatFormat);

    private readonly string _prefix;
    private readonly IAppLog? _log;
    private readonly object _gate = new();
    private readonly Queue<string> _tail = new();

    private string? _pending;
    private int _pendingRepeat;
    private int _written;
    private bool _closed;
    private bool _limitNoted;

    /// <param name="source">来源标记，例如 <c>Forge</c> / <c>NeoForge</c>；写出时加方括号前缀。</param>
    /// <param name="log">目标日志。为 null 时整个通道退化为空操作（测试/无日志宿主）。</param>
    public InstallerOutputLog(string source, IAppLog? log = null)
    {
        _prefix = $"[{source}]";
        _log = log;
    }

    /// <summary>已经写出的常规行数（测试与诊断用）。</summary>
    public int WrittenCount
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>接收一行安装器输出。空白行忽略；已收尾的通道不再接受新行。</summary>
    public void Append(string? line)
    {
        if (_log is null)
        {
            return;
        }

        var text = Normalize(line);
        if (text.Length == 0)
        {
            return;
        }

        (string? Text, bool Warn) previous;

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            PushTailLocked(text);

            if (string.Equals(text, _pending, StringComparison.Ordinal))
            {
                _pendingRepeat++;
                return;
            }

            previous = TakePendingLocked();
            _pending = text;
            _pendingRepeat = 0;
        }

        WriteLocked(previous);
    }

    /// <summary>安装成功收尾：把挂起的一行写掉，不再接受新行。</summary>
    public void Complete()
    {
        if (_log is null)
        {
            return;
        }

        (string? Text, bool Warn) pending;

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = TakePendingLocked();
        }

        WriteLocked(pending);
    }

    /// <summary>
    /// 安装失败：先写掉挂起行，再把最后 <see cref="TailLines"/> 行以 Info 提升一次。
    /// 尾部不受 <see cref="MaxLines"/> 限制——正常日志已经写满时，失败现场更该留下。
    /// </summary>
    public void FlushFailure()
    {
        if (_log is null)
        {
            return;
        }

        (string? Text, bool Warn) pending;
        string[] tail;

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = TakePendingLocked();
            tail = _tail.ToArray();
        }

        WriteLocked(pending);

        foreach (var line in tail)
        {
            _log.Info($"{_prefix} [最后输出] {line}");
        }
    }

    /// <summary>判断一行是否算错误级。只认任务约定的三个关键词。</summary>
    internal static bool LooksLikeError(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("exception", StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉首尾空白并截断到 <see cref="MaxLineLength"/> 字符（含中英文混合）。</summary>
    internal static string Normalize(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var trimmed = line.Trim();
        if (trimmed.Length <= MaxLineLength)
        {
            return trimmed;
        }

        // 留一个省略号的位置，保证结果长度不超过上限，同时能看出被截断。
        return string.Concat(trimmed.AsSpan(0, MaxLineLength - 1), "…");
    }

    /// <summary>挂起行 + 重复标注；调用方必须持有 <see cref="_gate"/>。</summary>
    private (string? Text, bool Warn) TakePendingLocked()
    {
        if (_pending is null)
        {
            return (null, false);
        }

        // 重复次数是纯数字，跟着系统区域走会在阿拉伯语等区域把数字写成非 ASCII 形，日志就没法 grep 了。
        var text = _pendingRepeat > 0
            ? $"{_pending} {string.Format(CultureInfo.InvariantCulture, RepeatComposite, _pendingRepeat)}"
            : _pending;

        var warn = LooksLikeError(_pending);
        _pending = null;
        _pendingRepeat = 0;

        return (text, warn);
    }

    private void PushTailLocked(string text)
    {
        _tail.Enqueue(text);
        while (_tail.Count > TailLines)
        {
            _tail.Dequeue();
        }
    }

    private void WriteLocked((string? Text, bool Warn) line)
    {
        if (line.Text is null || _log is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_written >= MaxLines)
            {
                if (!_limitNoted)
                {
                    _limitNoted = true;
                    _log.Debug($"{_prefix} 安装器输出已达 {MaxLines} 行上限，后续内容不再记录");
                }

                return;
            }

            _written++;
        }

        var message = $"{_prefix} {line.Text}";
        if (line.Warn)
        {
            _log.Warn(message);
        }
        else
        {
            _log.Debug(message);
        }
    }
}
