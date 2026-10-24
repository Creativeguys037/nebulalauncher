using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 本地模组文件的两套指纹 + 落盘缓存。
///
/// 为什么需要两套：Modrinth 用 SHA1 反查文件（<c>/v2/version_file/{hash}</c>），
/// CurseForge 只认它自己的 32 位 MurmurHash2 指纹（<c>POST /v1/fingerprints</c>），
/// 两套哈希没有任何换算关系，只能各算一份。
///
/// 为什么缓存按「路径 + 长度 + 最后写入时间」做键：这三个值一起变，才说明文件内容可能变了。
/// 只用路径会在用户替换成新版本后命中旧哈希，反查就会把 A 模组认成 B 模组；
/// 只用时间戳在解开压缩包（时间戳被还原）时会漏掉真正的变化。
///
/// 缓存读写的任何异常都必须吞掉：缓存落在用户磁盘上，可能被杀毒软件临时锁住、
/// 可能被手工改坏。这两种情况都只该让下一次检查慢一点，不该让整个更新检查报错。
/// </summary>
public sealed class ModFingerprint
{
    /// <summary>缓存文件会落在 <see cref="ILauncherPaths.CacheDirectory"/> 下，用这个名字。</summary>
    public const string CacheFileName = "mod-hashes.json";

    /// <summary>缓存条目上限。超了按文件写入时间裁掉最旧的，避免文件无限增长。</summary>
    public const int MaxCacheEntries = 4096;

    /// <summary>缓存结构版本。字段语义变化时递增，读到旧版本直接丢弃重建。</summary>
    private const int CacheSchemaVersion = 1;

    /// <summary>分块读取的块大小。与 <c>ModrinthService</c> 的下载缓冲保持一致。</summary>
    private const int BufferSize = 81920;

    /// <summary>CurseForge 指纹的乘法常数。算法本身来自 MurmurHash2，这个值不能改。</summary>
    private const uint MurmurMultiplier = 1540483477u;

    private readonly string? _cachePath;
    private readonly IAppLog? _log;
    private readonly object _gate = new();

    private Dictionary<string, HashCacheEntry>? _entries;

    private int _computedFileCount;

    /// <summary>0 = 空闲，1 = 正在写缓存。用 Interlocked 做「拿不到就跳过」，避免 4 个并行检查排队等写盘。</summary>
    private int _saving;

    /// <summary>
    /// 构造函数。<paramref name="cacheDirectory"/> 为 null 时退化为「只算不缓存」——
    /// 单测与「launcher 路径尚未初始化」的场景仍需能算出哈希。
    /// </summary>
    public ModFingerprint(string? cacheDirectory, IAppLog? log = null)
    {
        _cachePath = string.IsNullOrWhiteSpace(cacheDirectory)
            ? null
            : Path.Combine(cacheDirectory, CacheFileName);
        _log = log;
    }

    /// <summary>缓存文件的绝对路径；未启用缓存时为 null。测试用来断言缓存真的落盘了。</summary>
    public string? CacheFilePath => _cachePath;

    /// <summary>
    /// 真正读盘计算过哈希的文件次数（缓存命中不计）。
    /// 单独暴露是为了让测试能区分「命中缓存」与「重算得到同样结果」——两者对外返回值完全一样。
    /// </summary>
    public int ComputedFileCount => Volatile.Read(ref _computedFileCount);

    // ---------------- 公开算法（纯函数） ----------------

    /// <summary>
    /// 计算 CurseForge 文件指纹：MurmurHash2 的 CurseForge 变体。
    ///
    /// 与标准 MurmurHash2 的差异（PCL2 <c>LocalResourceFile.CurseForgeHash</c> 同款）：
    /// 1. 先剔除全部空白字节 <c>9 / 10 / 13 / 32</c> 再算，所以 CRLF 与 LF 存档指纹相同；
    /// 2. 种子是 <c>1 ^ 过滤后字节数</c>，而不是固定值；
    /// 3. 收尾是 <c>^&gt;&gt;13 → 乘常数 → ^&gt;&gt;15</c>，标准 MurmurHash2 收尾用的是 13/15 之外的组合。
    ///
    /// 这里的字节序必须是小端：CurseForge 服务端按小端分组，写成大端算出来的指纹一次都匹配不上。
    /// </summary>
    public static uint CurseForgeFingerprint(ReadOnlySpan<byte> data)
    {
        var filteredCount = 0;

        foreach (var value in data)
        {
            if (!IsSkipped(value))
            {
                filteredCount++;
            }
        }

        var state = new FingerprintState(filteredCount);
        state.Append(data);
        return state.Finish();
    }

    /// <summary>计算 SHA1 并转成大写十六进制。<see cref="ModFileHashes.Sha1"/> 的约定就是大写。</summary>
    public static string Sha1Hex(ReadOnlySpan<byte> data)
    {
        // 走 IncrementalHash 而不是 SHA1.HashData：Modrinth 的反查键就是 SHA1，
        // 这不是可替换的加密方案，用同一套 API 也让 CA5350 只在这里说明一次。
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ---------------- 公开入口 ----------------

    /// <summary>
    /// 计算一个文件的哈希；已缓存且文件未变化时直接返回。
    /// 缓存键的相对路径退化为文件名，因为没有基准目录可参考。
    /// </summary>
    public Task<ModFileHashes> GetHashesAsync(string filePath, CancellationToken cancellationToken = default)
        => GetHashesAsync(filePath, baseDirectory: null, cancellationToken);

    /// <summary>
    /// 计算一个文件的哈希。<paramref name="baseDirectory"/> 用来把绝对路径压成相对路径，
    /// 这样同一份整合包换一个安装位置后仍能命中缓存（PCL2 的 ModHash 缓存就是这么做的）。
    /// </summary>
    public async Task<ModFileHashes> GetHashesAsync(
        string filePath,
        string? baseDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return EmptyHashes();
        }

        long size;
        long ticks;
        DateTime lastWriteUtc;

        try
        {
            var info = new FileInfo(filePath);

            if (!info.Exists)
            {
                return EmptyHashes();
            }

            size = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取文件信息失败，跳过哈希计算：{filePath}", ex);
            return EmptyHashes();
        }

        var key = BuildCacheKey(filePath, baseDirectory, size, ticks);
        var cache = EnsureCache();

        lock (_gate)
        {
            if (cache.TryGetValue(key, out var cached) &&
                cached.Size == size &&
                cached.Ticks == ticks)
            {
                return new ModFileHashes(cached.Sha1, cached.CurseForge, size, lastWriteUtc);
            }
        }

        var hashes = await ComputeAsync(filePath, size, lastWriteUtc, cancellationToken).ConfigureAwait(false);

        // 计算失败（文件被独占、权限不足、中途被删）不写缓存：否则下次会直接返回空哈希，
        // 用户即使关掉了占用文件的程序也永远查不出这个模组。
        if (hashes.Sha1 is null)
        {
            return hashes;
        }

        lock (_gate)
        {
            cache[key] = new HashCacheEntry
            {
                Sha1 = hashes.Sha1,
                CurseForge = hashes.CurseForgeFingerprint,
                Size = size,
                Ticks = ticks,
            };

            TrimCache(cache);
        }

        TrySave();
        return hashes;
    }

    // ---------------- 哈希计算 ----------------

    private async Task<ModFileHashes> ComputeAsync(
        string filePath,
        long size,
        DateTime lastWriteUtc,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _computedFileCount);

        try
        {
            // 两遍读盘：第一遍算 SHA1 并统计「过滤空白后」的字节数（指纹的种子依赖它），
            // 第二遍才做 MurmurHash2。内存里只需一个 80 KB 的缓冲，几十 MB 的整合包 jar 也不会爆。
            var (sha1, filteredCount) = await HashSha1AndCountAsync(filePath, cancellationToken).ConfigureAwait(false);
            var fingerprint = await ComputeCurseForgeAsync(filePath, filteredCount, cancellationToken).ConfigureAwait(false);

            return new ModFileHashes(sha1, fingerprint, size, lastWriteUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"计算模组哈希失败：{filePath}", ex);
            return new ModFileHashes(null, 0, size, lastWriteUtc);
        }
    }

    private static async Task<(string Sha1, int FilteredCount)> HashSha1AndCountAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var filteredCount = 0;

        try
        {
            await using var stream = OpenRead(filePath);
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                filteredCount += CountKept(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (Convert.ToHexString(sha1.GetHashAndReset()), filteredCount);
    }

    private static async Task<uint> ComputeCurseForgeAsync(
        string filePath,
        int filteredCount,
        CancellationToken cancellationToken)
    {
        var state = new FingerprintState(filteredCount);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            await using var stream = OpenRead(filePath);
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                state.Append(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return state.Finish();
    }

    private static FileStream OpenRead(string filePath)
        => new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            // 用 FileShare.Read：别的程序正拿着这个 jar 读取时我们照样能算，
            // 只有对方以写方式独占时才失败，那时返回空哈希由调用方标「检查失败」。
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static int CountKept(ReadOnlySpan<byte> data)
    {
        var count = 0;

        foreach (var value in data)
        {
            if (!IsSkipped(value))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>CurseForge 指纹要剔除的四个「空白」字节：制表符、换行、回车、空格。</summary>
    private static bool IsSkipped(byte value) => value is 9 or 10 or 13 or 32;

    /// <summary>
    /// 增量 MurmurHash2 累加器。为什么要增量：文件按 80 KB 分块读，
    /// 一个 4 字节组可能横跨两块，所以不足 4 字节的尾巴要留到下一次 <see cref="Append"/>。
    /// </summary>
    private sealed class FingerprintState
    {
        private uint _hash;
        private uint _tail;
        private int _tailCount;

        public FingerprintState(int filteredCount)
        {
            // 种子是 1 ^ 过滤后的字节数，不是 0。换成 0 会让「内容为空」与「只有空白」的
            // 两个文件算出同一个指纹，而且与 CurseForge 服务端不一致。
            _hash = 1u ^ (uint)filteredCount;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                if (IsSkipped(value))
                {
                    continue;
                }

                // 小端拼装：低地址字节落在最低 8 位，与服务端的 BitConverter.ToUInt32 等价。
                _tail |= (uint)value << (_tailCount * 8);

                if (++_tailCount < 4)
                {
                    continue;
                }

                MixBlock(ref _hash, _tail);
                _tail = 0;
                _tailCount = 0;
            }
        }

        public uint Finish()
        {
            // 尾部剩余 3 / 2 / 1 个字节时，把它们按原位（第 3 / 2 / 1 个字节位）异或进去，
            // 再乘一次常数。注意不能先右移到低位再异或：那样会把 bit 8 的字节塞进 bit 0，
            // 与 PCL2 的 <c>h ^= (data(i) | data(i+1) &lt;&lt; 8)</c> 不再是同一个值。
            switch (_tailCount)
            {
                case 3:
                    _hash ^= _tail & 0x00FF0000u;
                    goto case 2;
                case 2:
                    _hash ^= _tail & 0x0000FF00u;
                    goto case 1;
                case 1:
                    _hash ^= _tail & 0x000000FFu;
                    _hash *= MurmurMultiplier;
                    break;
            }

            _hash ^= _hash >> 13;
            _hash *= MurmurMultiplier;
            _hash ^= _hash >> 15;
            return _hash;
        }

        private static void MixBlock(ref uint hash, uint block)
        {
            block *= MurmurMultiplier;
            block ^= block >> 24;
            block *= MurmurMultiplier;
            hash *= MurmurMultiplier;
            hash ^= block;
        }
    }

    // ---------------- 缓存 ----------------

    private static ModFileHashes EmptyHashes() => new(null, 0, 0, DateTime.MinValue);

    /// <summary>
    /// 缓存键：相对路径 + 长度 + 最后写入时间（tick）。三个值任何一处变化都会落到新键上，
    /// 旧条目自然失效，不需要额外的「失效逻辑」。
    /// </summary>
    private static string BuildCacheKey(string filePath, string? baseDirectory, long size, long ticks)
        => $"{RelativePathOf(filePath, baseDirectory)}|{size}|{ticks}";

    private static string RelativePathOf(string filePath, string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            try
            {
                var root = Path.GetFullPath(baseDirectory);
                var full = Path.GetFullPath(filePath);
                var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;

                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 统一用 '/'：同一个缓存文件可能被不同平台写，反斜杠会让键在不同机器上不一致。
                    return Path.GetRelativePath(root, full).Replace('\\', '/');
                }
            }
            catch (Exception)
            {
                // 路径非法（超长、含非法字符）就退回文件名，缓存本来只是加速手段。
            }
        }

        try
        {
            return Path.GetFileName(filePath);
        }
        catch (Exception)
        {
            return filePath;
        }
    }

    private Dictionary<string, HashCacheEntry> EnsureCache()
    {
        lock (_gate)
        {
            return _entries ??= LoadCache();
        }
    }

    private Dictionary<string, HashCacheEntry> LoadCache()
    {
        var entries = new Dictionary<string, HashCacheEntry>(StringComparer.Ordinal);

        if (_cachePath is null || !File.Exists(_cachePath))
        {
            return entries;
        }

        try
        {
            using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var file = JsonSerializer.Deserialize<HashCacheFile>(stream);

            if (file is null || file.Version != CacheSchemaVersion)
            {
                return entries;
            }

            foreach (var pair in file.Entries)
            {
                if (pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Value.Sha1))
                {
                    entries[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            // JSON 被改坏、文件被独占、磁盘只读——都只让缓存失效，不影响哈希计算本身。
            _log?.Debug($"读取模组哈希缓存失败，本次将重新计算：{ex.GetType().Name}");
        }

        return entries;
    }

    private static void TrimCache(Dictionary<string, HashCacheEntry> cache)
    {
        if (cache.Count <= MaxCacheEntries)
        {
            return;
        }

        var keep = cache
            .OrderByDescending(pair => pair.Value.Ticks)
            .Take(MaxCacheEntries)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        cache.Clear();

        foreach (var pair in keep)
        {
            cache[pair.Key] = pair.Value;
        }
    }

    /// <summary>
    /// 把内存缓存写回磁盘。
    ///
    /// 为什么是同步而不是 async：缓存只有几十 KB，写成异步要多一次状态机与取消传播，
    /// 收益却为零；而「同一时刻只允许一次写入」用 <see cref="Interlocked"/> 表达最简单，
    /// 不需要可释放的 SemaphoreSlim（那会让本类型也得实现 IDisposable）。
    /// </summary>
    private void TrySave()
    {
        if (_cachePath is null)
        {
            return;
        }

        // 4 个文件并行检查时会有多次保存请求。拿不到就跳过：正在写的那次已经包含最新条目，
        // 下一次检查还会再触发保存。
        if (Interlocked.Exchange(ref _saving, 1) == 1)
        {
            return;
        }

        try
        {
            HashCacheFile snapshot;

            lock (_gate)
            {
                var entries = _entries ?? new Dictionary<string, HashCacheEntry>(StringComparer.Ordinal);
                snapshot = new HashCacheFile
                {
                    Version = CacheSchemaVersion,
                    Entries = new Dictionary<string, HashCacheEntry>(entries, StringComparer.Ordinal),
                };
            }

            var directory = Path.GetDirectoryName(_cachePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写 .tmp 再整体替换：写到一半被杀进程时，旧缓存仍然完整可读。
            var temporaryPath = _cachePath + ".tmp";

            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Debug($"写入模组哈希缓存失败，本次检查结果不落盘：{ex.GetType().Name}");
        }
        finally
        {
            Volatile.Write(ref _saving, 0);
        }
    }

    /// <summary>缓存文件的外层结构。带 <see cref="Version"/> 是为了让字段语义变化时能整体丢弃。</summary>
    private sealed class HashCacheFile
    {
        public int Version { get; set; } = CacheSchemaVersion;

        public Dictionary<string, HashCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>一条缓存记录。<see cref="Size"/> 与 <see cref="Ticks"/> 冗余存一份，用于抵御键碰撞。</summary>
    private sealed class HashCacheEntry
    {
        public string? Sha1 { get; set; }

        public uint CurseForge { get; set; }

        public long Size { get; set; }

        public long Ticks { get; set; }
    }
}
