using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 跨实例 / 跨根目录的文件复用：本机别处已有同 size、同 SHA1 的文件时直接复制，不再走网络。
///
/// 思路来自 PCL2 <c>ModNet.vb:1474-1564</c>：一个文件真正发起请求之前，先在所有已注册的
/// .minecraft 里按 size/sha1 找同内容文件。PCL2 是每次下载都遍历，这里换成「进程内 size 索引 +
/// 首次访问时后台枚举」——一次安装动辄几百个文件，逐个全盘遍历会把磁盘 IO 打满。
///
/// 只索引 mods / resourcepacks / shaderpacks / saves 四类目录：libraries / assets / versions
/// 在本启动器里本来就是全局共享的（docs/PCL2-PORTING.md §4.4），对它们做复用没有收益，
/// 反而会让索引规模不可控。
/// </summary>
public sealed class FileReuseService
{
    /// <summary>单个目录的文件数超过它时退化为只索引 <c>*.jar</c>，避免整合包解压后的巨树拖垮首次枚举。</summary>
    private const int FullScanFileLimit = 5000;

    private const int CopyBufferSize = 64 * 1024;

    /// <summary>只在这四类子目录里找源文件；也只有落在这四类子目录里的目标才会尝试复用。</summary>
    private static readonly string[] ReusableSubdirectories = ["mods", "resourcepacks", "shaderpacks", "saves"];

    /// <summary>Windows 文件系统大小写不敏感，其余平台敏感。</summary>
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>目录不存在或尚未建索引时共用的空表，避免每次访问都分配。</summary>
    private static readonly Dictionary<long, List<string>> EmptyIndex = new();

    private readonly ILauncherPaths? _paths;
    private readonly IAppLog? _log;

    /// <summary>四类子目录 → (文件大小 → 同大小文件列表)。key 就是被扫描的那个子目录的绝对路径。</summary>
    private readonly ConcurrentDictionary<string, Dictionary<long, List<string>>> _indexes = new(PathComparer);

    public FileReuseService(ILauncherPaths? paths = null, IAppLog? log = null)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// 丢弃全部索引。切换 .minecraft 目录或设置变更后必须调用：
    /// 索引里的文件可能已经被移到别处，而路径本身不会再出现在新的根下。
    /// </summary>
    public void InvalidateCache() => _indexes.Clear();

    /// <summary>
    /// 尝试用本机已有文件满足 <paramref name="item"/>。命中时目标文件已经就位，返回 true。
    ///
    /// 前置条件（任一不满足都直接返回 false，交给正常下载）：
    /// <see cref="DownloadItem.ExpectedSha1"/> 非空、<see cref="DownloadItem.ExpectedSize"/> 已知、
    /// 目标路径落在某个复用根的四类子目录内。复用是纯优化，任何失败都必须回落到网络下载。
    /// </summary>
    public async Task<bool> TryReuseAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            var expectedSha1 = item.ExpectedSha1;
            var expectedSize = item.ExpectedSize;
            if (string.IsNullOrEmpty(expectedSha1) || expectedSize <= 0)
            {
                return false;
            }

            var paths = _paths;
            if (paths is null || string.IsNullOrWhiteSpace(item.DestinationPath))
            {
                return false;
            }

            var destination = item.DestinationPath;
            var roots = EnumerateRoots(paths);
            if (FindContainer(destination, roots) is null)
            {
                return false;
            }

            var scanDirectories = CollectScanDirectories(roots);

            // 首次访问的目录在后台线程枚举；已经建过索引的目录只是字典查找。
            var indexes = await Task
                .WhenAll(scanDirectories.Select(directory => GetIndexAsync(directory, cancellationToken)))
                .ConfigureAwait(false);

            foreach (var index in indexes)
            {
                if (!index.TryGetValue(expectedSize, out var candidates))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (PathComparer.Equals(candidate, destination))
                    {
                        continue;
                    }

                    var actualSha1 = await ComputeSha1Async(candidate, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return await CopyIntoAsync(candidate, destination, cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 复用失败只意味着「这次省不掉」，不是下载失败，所以不向上抛。
            _log?.Debug($"文件复用失败，回落到网络下载：{item.DestinationPath}（{ex.GetType().Name}: {ex.Message}）");
            return false;
        }
    }

    /// <summary>复用根：全局 .minecraft 目录 + 每个版本目录（实例目录）。</summary>
    private static List<string> EnumerateRoots(ILauncherPaths paths)
    {
        var roots = new List<string>(8);

        var minecraft = paths.MinecraftDirectory;
        if (!string.IsNullOrEmpty(minecraft))
        {
            roots.Add(minecraft);
        }

        var versions = paths.VersionsDirectory;
        if (!string.IsNullOrEmpty(versions))
        {
            try
            {
                if (Directory.Exists(versions))
                {
                    foreach (var directory in Directory.EnumerateDirectories(versions))
                    {
                        // 只有带 instance.json 的版本目录才是实例。普通版本目录里没有 mods/saves，
                        // 把它当复用根只会白白多扫几个空目录。
                        if (File.Exists(Path.Combine(directory, "instance.json")))
                        {
                            roots.Add(directory);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 版本目录读不到时只复用全局目录，不影响下载。
            }
        }

        return roots;
    }

    /// <summary>目标是否落在某个复用根的 mods / resourcepacks / shaderpacks / saves 下；不是则返回 null。</summary>
    private static string? FindContainer(string destination, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            foreach (var name in ReusableSubdirectories)
            {
                var directory = Path.Combine(root, name);
                if (IsUnder(destination, directory))
                {
                    return directory;
                }
            }
        }

        return null;
    }

    /// <summary>所有待扫描的四类子目录，跨根去重。</summary>
    private static List<string> CollectScanDirectories(IReadOnlyList<string> roots)
    {
        var directories = new List<string>(roots.Count * ReusableSubdirectories.Length);
        var seen = new HashSet<string>(PathComparer);

        foreach (var root in roots)
        {
            foreach (var name in ReusableSubdirectories)
            {
                var directory = Path.Combine(root, name);
                if (seen.Add(directory))
                {
                    directories.Add(directory);
                }
            }
        }

        return directories;
    }

    /// <summary><paramref name="path"/> 是否位于 <paramref name="directory"/> 之下（不含目录自身）。</summary>
    private static bool IsUnder(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var container = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

            return fullPath.Length > container.Length
                   && fullPath.StartsWith(container, PathComparison)
                   && fullPath[container.Length] == Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private Task<Dictionary<long, List<string>>> GetIndexAsync(string directory, CancellationToken cancellationToken)
    {
        if (_indexes.TryGetValue(directory, out var cached))
        {
            return Task.FromResult(cached);
        }

        // 不存在或已被删掉的目录不建索引：下次调用还能重新扫到它（首次下载时 mods/ 常常还不存在）。
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(EmptyIndex);
        }

        return Task.Run(() => BuildIndex(directory, cancellationToken), cancellationToken);
    }

    private Dictionary<long, List<string>> BuildIndex(string directory, CancellationToken cancellationToken)
    {
        return _indexes.GetOrAdd(directory, target =>
        {
            try
            {
                return Scan(target, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Debug($"建立复用索引失败：{target}（{ex.GetType().Name}: {ex.Message}）");
                return new Dictionary<long, List<string>>();
            }
        });
    }

    private static Dictionary<long, List<string>> Scan(string directory, CancellationToken cancellationToken)
    {
        var files = ListFiles(directory, "*", FullScanFileLimit + 1, cancellationToken, out var tooMany);
        if (tooMany)
        {
            // 超过阈值就只认 jar：整合包实例的 mods 树可能有几万个文件，全量索引的收益抵不上代价。
            files = ListFiles(directory, "*.jar", 0, cancellationToken, out _);
        }

        var index = new Dictionary<long, List<string>>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var length = new FileInfo(file).Length;
                if (length <= 0)
                {
                    continue;
                }

                if (!index.TryGetValue(length, out var bucket))
                {
                    bucket = new List<string>();
                    index[length] = bucket;
                }

                bucket.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件取不到大小（正被写入、权限不足）：跳过它，其余索引照常可用。
            }
        }

        return index;
    }

    /// <summary>递归列出文件。<paramref name="limit"/> 为 0 表示不限；触顶时 <paramref name="truncated"/> 为 true。</summary>
    private static List<string> ListFiles(
        string directory,
        string pattern,
        int limit,
        CancellationToken cancellationToken,
        out bool truncated)
    {
        truncated = false;
        var files = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);

                if (limit > 0 && files.Count >= limit)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 递归枚举中途撞到被删除 / 无权限的子目录时，保留已经枚举到的部分。
        }

        return files;
    }

    private static Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken) =>
        Task.Run(() => ComputeSha1(path, cancellationToken), cancellationToken);

    [SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "Mojang 的资源校验本身使用 SHA1，复用判定必须与下载校验用同一种算法")]
    private static string ComputeSha1(string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            CopyBufferSize,
            FileOptions.SequentialScan);

        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// 先写 <c>.tmp</c> 再原子覆盖：复制中途失败时目标要么还是旧内容，要么不存在，
    /// 不会被截成半个文件——下载队列随后会照常重下，不能让它读到损坏的目标。
    /// </summary>
    private Task<bool> CopyIntoAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var scratch = destination + ".tmp";

        return Task.Run(
            () =>
            {
                try
                {
                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Copy(source, scratch, overwrite: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(scratch, destination, overwrite: true);
                    _log?.Debug($"复用已有文件：{destination} ← {source}");
                    return true;
                }
                catch
                {
                    TryDelete(scratch);
                    throw;
                }
            },
            cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留的 .tmp 下次复制时会被 overwrite，清理失败不影响结论。
        }
    }
}
