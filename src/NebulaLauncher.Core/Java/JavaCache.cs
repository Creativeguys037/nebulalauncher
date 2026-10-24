using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Java;

/// <summary>java-cache.json 里的一条探测记录。</summary>
public sealed class JavaCacheEntry
{
    /// <summary>java / javaw 可执行文件绝对路径。缓存 key 就是它。</summary>
    public string Path { get; set; } = string.Empty;

    public int MajorVersion { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool Is64Bit { get; set; }

    public string Vendor { get; set; } = string.Empty;

    /// <summary>探测当时可执行文件的字节数，失效判据之一。</summary>
    public long PathSize { get; set; }

    /// <summary>探测当时可执行文件的 <see cref="FileInfo.LastWriteTimeUtc"/> 刻度，失效判据之二。</summary>
    public long PathLastWriteTicks { get; set; }

    /// <summary>上次成功探测的时间。只用于诊断，不参与失效判断（见 <see cref="JavaCache"/>）。</summary>
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>
/// Java 列表的持久化缓存：<c>&lt;CacheDirectory&gt;/java-cache.json</c>。
///
/// 为什么缓存「探测结果」而不是「候选目录」：一次 <see cref="JavaService.DetectAsync"/>
/// 的成本几乎全在每个候选跑一次 <c>java -XshowSettings:properties -version</c> 子进程，
/// 目录扫描本身只是 <c>File.Exists</c>。所以 key 直接用可执行文件路径，命中即可跳过子进程。
///
/// 失效判据用「size + LastWriteTimeUtc」，不用 mtime 字符串、也不做 TTL：
/// - size 变化必然意味着换了文件（升级、替换安装）；
/// - LastWriteTimeUtc 是操作系统的元数据，重装、搬运、就地更新都会更新；
/// - Java 装上之后常年不动是常态，TTL 到点就重探等于没有缓存；
/// - 两者都与 Java 输出格式无关，任何发行版、任何版本号写法都适用。
///
/// 所有方法都不抛异常：缓存是纯加速层，文件损坏 / 被占用就按空缓存处理并重建，
/// 失败绝不影响探测结果。
///
/// 与 PCL2 的差异（<c>PCL\PCLCS\Java.cs:34-77</c>）：PCL2 的 <c>Configs.JavaList</c> 只持久化
/// 路径列表（<c>{ Folder, Version }</c>），每次使用前仍然跑一次
/// <c>java.exe -XshowSettings:properties -version</c> 复核，<c>isChecked</c> 只保证同一进程内
/// 不重复检查。本实现额外用「可执行文件 size + LastWriteTimeUtc」做探测结果的缓存，
/// 文件元数据没变就跳过子进程：一次 DetectAsync 从「N 次进程启动」降到「一次文件元数据比对」。
/// 代价是用户把同路径、同大小的 java.exe 换成另一个文件（且修改时间恰好也被伪造）时会沿用旧结果，
/// 这种情况概率可忽略；换了 Java 的正常安装 / 升级 / 搬运都会改这两个字段之一。
/// </summary>
public sealed class JavaCache
{
    /// <summary>缓存文件名。放在 <see cref="ILauncherPaths.CacheDirectory"/> 下。</summary>
    public const string FileName = "java-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly IAppLog? _log;

    /// <param name="cacheDirectory">启动器缓存根目录；为空表示禁用落盘（Load 恒空、Save 空操作）。</param>
    /// <param name="log">可选的日志出口。</param>
    public JavaCache(string cacheDirectory, IAppLog? log = null)
    {
        _filePath = string.IsNullOrWhiteSpace(cacheDirectory)
            ? string.Empty
            : System.IO.Path.Combine(cacheDirectory, FileName);
        _log = log;
    }

    /// <summary>缓存文件绝对路径；未指定缓存目录时为空串。</summary>
    public string FilePath => _filePath;

    /// <summary>读取缓存。文件缺失、损坏、被占用或解析失败一律返回空字典。</summary>
    public IReadOnlyDictionary<string, JavaCacheEntry> Load()
    {
        var empty = new Dictionary<string, JavaCacheEntry>(StringComparer.OrdinalIgnoreCase);
        if (_filePath.Length == 0)
        {
            return empty;
        }

        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return empty;
                }

                var json = File.ReadAllText(_filePath);
                var entries = JsonSerializer.Deserialize<List<JavaCacheEntry>>(json, JsonOptions) ?? [];

                var result = new Dictionary<string, JavaCacheEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    // 手工改坏的单条记录不该让整张表作废，跳过它即可。
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                    {
                        continue;
                    }

                    result[entry.Path] = entry;
                }

                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _log?.Warn($"读取 Java 缓存失败，按空缓存处理：{_filePath}", ex);
                return empty;
            }
        }
    }

    /// <summary>整表回写。先写临时文件再替换，避免进程被杀时留下半截 JSON。</summary>
    public void Save(IEnumerable<JavaCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (_filePath.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(entries.ToList(), JsonOptions);
                var temporary = _filePath + ".tmp";

                File.WriteAllText(temporary, json);
                File.Move(temporary, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _log?.Debug($"写入 Java 缓存失败（不影响本次探测）：{_filePath}（{ex.Message}）");
            }
        }
    }

    /// <summary>按可执行文件的当前元数据构造一条记录。</summary>
    public static JavaCacheEntry Capture(JavaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var (size, ticks) = ReadMetadata(info.Path);

        return new JavaCacheEntry
        {
            Path = info.Path,
            MajorVersion = info.MajorVersion,
            Version = info.Version,
            Is64Bit = info.Is64Bit,
            Vendor = info.Vendor,
            PathSize = size,
            PathLastWriteTicks = ticks,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 记录是否仍然可信：可执行文件存在，且 size 与 LastWriteTimeUtc 都和记录一致。
    /// 任何一项读不到都按「不可信」处理，宁可多跑一次探测。
    /// </summary>
    public static bool IsStillValid(JavaCacheEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(entry.Path);
            return file.Exists
                   && file.Length == entry.PathSize
                   && file.LastWriteTimeUtc.Ticks == entry.PathLastWriteTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static (long Size, long Ticks) ReadMetadata(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (0, 0);
        }
    }
}
