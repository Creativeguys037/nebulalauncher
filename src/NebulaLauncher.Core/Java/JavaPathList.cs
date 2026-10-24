using System.Text.Json;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Java;

/// <summary><c>java-excluded.json</c> / <c>java-manual.json</c> 的文件结构。字段名固定，不随实现改名。</summary>
internal sealed class JavaPathDocument
{
    public List<string> Paths { get; set; } = [];
}

/// <summary>
/// 一串 Java 路径的用户数据文件：<c>&lt;data&gt;/java-excluded.json</c>（用户移除过的 Java）
/// 与 <c>&lt;data&gt;/java-manual.json</c>（用户手动添加的 Java）共用这一份读写实现。
///
/// 设计要点照 <c>Versions/VersionFavorites.cs</c>：
/// <list type="number">
/// <item>独立文件、不进 <c>settings.json</c>：路径是用户数据，不是程序配置，「恢复默认设置」不该清掉它；</item>
/// <item>原子写：先写 <c>.tmp</c> 再 <c>File.Move(overwrite)</c>，进程被杀不会留下半截 JSON；</item>
/// <item>文件缺失 / 损坏一律按空列表处理并重建，绝不让「一个坏 JSON」阻断 Java 探测；</item>
/// <item>每次真正改变内容后才发 <see cref="Changed"/>：<c>JavaService</c> 靠它丢弃内存里过期的探测结果。</item>
/// </list>
///
/// 路径比较规则（<see cref="Normalize"/>）：<see cref="Path.GetFullPath(string)"/> 归一 + 去尾部分隔符 +
/// <see cref="StringComparison.OrdinalIgnoreCase"/>。Windows 上 <c>C:\jdk</c> 与 <c>c:\JDK\</c> 是同一个目录，
/// 用序数比较会让「移除」时灵时不灵。
/// </summary>
public sealed class JavaPathList : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _paths = [];
    private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _filePath;
    private readonly IAppLog? _log;

    private volatile bool _loaded;

    /// <param name="dataDirectory">启动器数据根目录（<see cref="ILauncherPaths.RootDirectory"/>）。</param>
    /// <param name="fileName">文件名，例如 <c>java-excluded.json</c>。</param>
    /// <param name="log">可选日志出口。坏文件与写盘失败只记日志，不抛给调用方。</param>
    public JavaPathList(string dataDirectory, string fileName, IAppLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _filePath = Path.Combine(dataDirectory, fileName);
        _log = log;
    }

    /// <summary>数据文件的绝对路径。</summary>
    public string FilePath => _filePath;

    /// <summary>内容变化（新增 / 移除 / 清空）后触发。</summary>
    public event EventHandler? Changed;

    /// <summary>已加载的路径，按写入顺序（最新添加的在最前）。首次访问时同步读盘一次。</summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            EnsureLoaded();

            lock (_sync)
            {
                return _paths.ToList();
            }
        }
    }

    /// <summary>是否包含该路径。入参不必预先归一：这里会做完整归一后比较。</summary>
    public bool Contains(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            return false;
        }

        EnsureLoaded();

        lock (_sync)
        {
            return _set.Contains(normalized);
        }
    }

    /// <summary>
    /// 幂等的加载。同步实现是刻意的：<see cref="JavaService.CollectCandidates"/> 是同步方法，
    /// 而这两个文件只有几条路径，读一次的开销远小于「为异步加载再把候选收集改成 async」的复杂度。
    /// </summary>
    public void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            LoadCore();
            _loaded = true;
        }
    }

    /// <summary>加载（已加载则立即返回）。保留异步签名是为了让调用方能放在 await 链上。</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        return Task.CompletedTask;
    }

    /// <summary>追加一条。已存在（按归一化比较）时不写盘、不发通知。</summary>
    public async Task AddAsync(string? path, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            return;
        }

        EnsureLoaded();

        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_set.Add(normalized))
                {
                    // 新项插到最前：手动添加的 Java 在探测结果里应当置顶，而 positional 顺序就是它的优先级。
                    _paths.Insert(0, normalized);
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>移除一条。返回是否真的移除了（「移除」按钮点在已不存在的记录上不该算变化）。</summary>
    public async Task<bool> RemoveAsync(string? path, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            return false;
        }

        EnsureLoaded();

        var removed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var index = _paths.FindIndex(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _paths.RemoveAt(index);
                    _set.Remove(normalized);
                    removed = true;
                }
            }

            if (removed)
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>清空。本来就空时不写盘、不发通知。</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();

        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_paths.Count > 0)
                {
                    _paths.Clear();
                    _set.Clear();
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 归一化一个路径：去首尾空白与包裹引号 → <see cref="Path.GetFullPath(string)"/> → 去尾部分隔符。
    /// 空 / 全空白返回 null。非法路径按原文返回（只是比较不准，不该让整个列表作废）。
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return Path.TrimEndingDirectorySeparator(trimmed);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // ---------------- 内部实现 ----------------

    /// <summary>调用方需持有 <see cref="_sync"/>（或经 <see cref="EnsureLoaded"/> 进入）。</summary>
    private void LoadCore()
    {
        _paths.Clear();
        _set.Clear();

        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var document = JsonSerializer.Deserialize<JavaPathDocument>(json, JsonOptions);

            var loaded = 0;
            foreach (var raw in document?.Paths ?? [])
            {
                var normalized = Normalize(raw);

                // 手工改坏文件时可能出现重复项或空串：跳过即可，不让一条烂记录作废整张表。
                if (normalized is null || !_set.Add(normalized))
                {
                    continue;
                }

                _paths.Add(normalized);
                loaded++;
            }

            _log?.Info($"已加载 Java 路径列表：{_filePath}（{loaded} 条）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            // 坏文件立即按空列表重建：用户下次打开设置页不该再看到同一条错误。
            _log?.Warn($"Java 路径列表损坏，已按空列表重建：{_filePath}", ex);
            RebuildEmpty();
        }
    }

    private void RebuildEmpty()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(new JavaPathDocument(), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Debug($"重建 Java 路径列表失败（不影响本次运行）：{_filePath}（{ex.Message}）");
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<string> snapshot;
            lock (_sync)
            {
                snapshot = _paths.ToList();
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new JavaPathDocument { Paths = snapshot }, JsonOptions);
            var temporary = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 内存状态已经变了：界面先反映用户操作，落盘失败只记日志，不把异常抛给页面。
            _log?.Warn($"保存 Java 路径列表失败：{_filePath}", ex);
        }
    }
}