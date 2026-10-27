using System.Runtime.InteropServices;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Maintenance;

/// <summary>
/// 存储占用统计与安全清理的实现。
///
/// 统计口径（四个类别，键稳定，见 <see cref="StorageCategoryKeys"/>）：
/// <list type="bullet">
/// <item><c>cache</c>：<c>&lt;data&gt;/cache</c>，但**排除** <c>loader-install</c> 子树——
/// 它是 cache 的子目录，不排除就会被统计两次，用户会看到「缓存 500 MB + 安装器 500 MB」而实际只有 500 MB。</item>
/// <item><c>logs</c>：<c>&lt;data&gt;/logs</c>。</item>
/// <item><c>installer</c>：<c>&lt;data&gt;/cache/loader-install</c>（安装期间 TMP/TEMP 指向这里）。</item>
/// <item><c>trash</c>：<c>&lt;data&gt;/trash</c>，实例回收站（<c>trash/instances</c>）与内容回收站
/// （<c>trash/contents</c>）同根，合并成一个类别。</item>
/// </list>
///
/// 三条实现底线：
/// 1. 扫描**不跟随**符号链接与目录联接。跟随会把链接目标的容量算进本类别，清理时还会顺着链接删到数据根之外。
/// 2. 扫描逐项枚举累加，只用文件元数据（长度 / 写入时间），从不打开文件读内容——日志目录里可能有几百 MB 的文件。
/// 3. 清理先把目标路径 <see cref="Path.GetFullPath(string)"/> 规范化，再校验它在数据根之下；
/// 规范化是必须的，<c>&lt;data&gt;/cache/../../..</c> 这种写法在字符串上完全看不出越界。
/// </summary>
public sealed class StorageService : IStorageService
{
    /// <summary>
    /// 日志保留天数。按**最后写入时间**判断，不看文件名里的日期：
    /// 文件名只有 <c>launcher-yyyyMMdd.log</c> 一种形态带日期，崩溃导出、安装器输出、用户手改/复制过的
    /// 日志都没有日期或带着过时日期；而最后写入时间就是「最后一次被使用」的时刻，与保留语义一致。
    /// </summary>
    public const int LogRetentionDays = 7;

    /// <summary>安装器临时目录名。与 <c>InstallService</c> 里指向 TMP/TEMP 的目录名必须一致。</summary>
    private const string LoaderInstallDirectoryName = "loader-install";

    /// <summary>
    /// 路径比较口径。Windows 区分大小写会让 <c>C:\data</c> 与 <c>c:\DATA</c> 判成两条路径，
    /// 越界校验因此可以绕过；Linux 上大小写敏感，用 IgnoreCase 反而会放过一条大小写不同的越界路径。
    /// </summary>
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 绝不允许清理碰到的用户数据文件。清理范围只在四个类别根目录之内，这里再挡一道是防「类别定义写错」
    /// ——把 <c>cache</c> 误写成 <c>&lt;data&gt;</c> 时，用户数据文件仍然删不掉。
    /// </summary>
    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings.json",
        "accounts.json",
        "servers.json",
        "favorites.json",
        "java-excluded.json",
        "java-cache.json",
    };

    /// <summary>受保护的顶层目录（相对数据根）。同上，属于防御性检查。</summary>
    private static readonly string[] ProtectedTopLevelNames =
    [
        "instances",
        "versions",
        "libraries",
        "assets",
        "backups",
        "saves",
        "minecraft",
        // 本地皮肤库：用户自己导入的图，删掉就没有第二份，清缓存绝不能碰（票据 #42）。
        "skins",
    ];

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public StorageService(ILauncherPaths paths, IAppLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _log = log;
    }

    /// <inheritdoc />
    public Task<StorageReport> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanReport(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<StorageCleanResult> CleanAsync(
        IReadOnlyCollection<string> categoryKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryKeys);
        return Task.Run(() => CleanCore(categoryKeys, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 清理全部类别。契约里是默认实现（接口调用即可用），这里再显式转发一次，
    /// 让持有具体类型的调用方（单测、维护脚本）不必为了一个默认实现去转接口。
    /// </summary>
    public Task<StorageCleanResult> CleanAllAsync(CancellationToken cancellationToken = default) =>
        CleanAsync(StorageCategoryKeys.All, cancellationToken);

    // ==================== 统计 ====================

    private StorageReport ScanReport(CancellationToken cancellationToken)
    {
        var usages = new List<StorageCategoryUsage>(4);
        foreach (var definition in BuildDefinitions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            usages.Add(Measure(definition, cancellationToken));
        }

        return new StorageReport(usages, DateTimeOffset.Now);
    }

    private StorageCategoryUsage Measure(CategoryDefinition definition, CancellationToken cancellationToken)
    {
        var excluded = NormalizeAll(definition.ExcludedDirectories);

        long totalBytes = 0;
        var fileCount = 0;
        DateTimeOffset? oldest = null;

        // 显式栈而不是 EnumerateFiles(RecurseSubdirectories)：后者没法在遍历中按绝对路径跳过子树，
        // 而 loader-install 必须被跳过；也不能在中途对「某个子目录读失败」单独 catch。
        var pending = new Stack<string>();
        var reportedPath = definition.RootPath;
        if (TryNormalize(definition.RootPath, out var root))
        {
            reportedPath = root;
            if (Directory.Exists(root))
            {
                pending.Push(root);
            }
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                // 单个目录无权访问 / 被占用：跳过它，其余目录照常统计。
                _log?.Warn($"统计存储占用时跳过目录：{directory}", ex);
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (IsIoFailure(ex))
                {
                    // 遍历期间被删除或无权访问：跳过。
                    _log?.Warn($"统计存储占用时跳过条目：{entry}", ex);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsExcluded(entry, excluded))
                    {
                        continue;
                    }

                    pending.Push(entry);
                    continue;
                }

                try
                {
                    var info = new FileInfo(entry);
                    totalBytes += info.Length;
                    fileCount++;

                    var writeTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    if (oldest is null || writeTime < oldest.Value)
                    {
                        oldest = writeTime;
                    }
                }
                catch (Exception ex) when (IsIoFailure(ex))
                {
                    _log?.Warn($"统计存储占用时跳过文件：{entry}", ex);
                }
            }
        }

        return new StorageCategoryUsage(
            definition.Key,
            definition.DisplayName,
            reportedPath,
            totalBytes,
            fileCount,
            oldest,
            definition.CanClean,
            definition.CleanHint);
    }

    // ==================== 清理 ====================

    private StorageCleanResult CleanCore(IReadOnlyCollection<string> categoryKeys, CancellationToken cancellationToken)
    {
        var requested = new List<string>(categoryKeys.Count);
        foreach (var key in categoryKeys)
        {
            if (!string.IsNullOrWhiteSpace(key) && !requested.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                requested.Add(key);
            }
        }

        if (requested.Count == 0)
        {
            return new StorageCleanResult(0, 0, 0, Array.Empty<string>(), null);
        }

        var counter = new CleanCounter();
        var cleanedKeys = new List<string>(requested.Count);
        string? rejected = null;
        var context = CreateContext();

        foreach (var definition in BuildDefinitions())
        {
            if (!requested.Contains(definition.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!definition.CanClean)
            {
                counter.Failed++;
                rejected ??= $"「{definition.DisplayName}」不允许清理。";
                continue;
            }

            if (!TryNormalize(definition.RootPath, out var target))
            {
                counter.Failed++;
                rejected ??= $"路径非法，已跳过：{definition.RootPath}";
                _log?.Warn($"存储清理跳过非法路径：{definition.RootPath}");
                continue;
            }

            // 第一道安全闸：规范化之后必须严格位于数据根之下。数据根本身也不允许作为目标。
            if (!IsInsideDataRoot(context.DataRoot, target))
            {
                counter.Failed++;
                rejected ??= $"拒绝清理数据目录之外的路径：{target}";
                _log?.Warn($"存储清理被拒绝（数据目录之外）：{target}");
                continue;
            }

            if (!Directory.Exists(target))
            {
                // 目录不存在等于「已经是干净的」，不算失败。
                cleanedKeys.Add(definition.Key);
                continue;
            }

            CleanDirectory(target, definition, NormalizeAll(definition.ExcludedDirectories), context, counter, cancellationToken);
            cleanedKeys.Add(definition.Key);
        }

        var result = new StorageCleanResult(counter.Bytes, counter.Files, counter.Failed, cleanedKeys, rejected);
        _log?.Info(
            $"存储清理完成：请求 [{string.Join(", ", requested)}]，删除 {result.DeletedFiles} 个文件 / {result.DeletedBytes} 字节，失败 {result.FailedItems} 项。");
        return result;
    }

    private void CleanDirectory(
        string directory,
        CategoryDefinition definition,
        List<string> excluded,
        CleanContext context,
        CleanCounter counter,
        CancellationToken cancellationToken)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            counter.Failed++;
            _log?.Warn($"存储清理跳过目录：{directory}", ex);
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                counter.Failed++;
                _log?.Warn($"存储清理跳过条目：{entry}", ex);
                continue;
            }

            // 链接一条都不删：删掉链接本身无害，但「跟随链接进去」会把数据根之外的目标一起删掉。
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                _log?.Warn($"存储清理跳过符号链接 / 目录联接：{entry}");
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                // 类别边界：cache 的清理不碰 loader-install，它由独立的 installer 类别负责。
                // 否则用户只想清下载缓存时，安装失败现场也被一起清掉了。
                if (IsExcluded(entry, excluded))
                {
                    continue;
                }

                // 先清子目录，回来后才有机会把它删空；目录本身只在空了之后才删。
                CleanDirectory(entry, definition, excluded, context, counter, cancellationToken);
                TryDeleteEmptyDirectory(entry, counter);
                continue;
            }

            if (IsProtected(entry, context.ProtectedRoots))
            {
                _log?.Warn($"存储清理跳过受保护文件：{entry}");
                continue;
            }

            if (IsRetained(entry, definition, context))
            {
                continue;
            }

            try
            {
                // 先取长度再删：删掉之后 FileInfo.Length 就没了，而且这个长度决定了「释放了多少」。
                var length = new FileInfo(entry).Length;
                File.Delete(entry);
                counter.Bytes += length;
                counter.Files++;
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                counter.Failed++;
                _log?.Warn($"存储清理删除文件失败：{entry}", ex);
            }
        }
    }

    private void TryDeleteEmptyDirectory(string directory, CleanCounter counter)
    {
        try
        {
            // 只删空目录：里面还有被保留的最近日志或被占用的文件时保持原样，留给用户看现场。
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            counter.Failed++;
            _log?.Warn($"存储清理删除空目录失败：{directory}", ex);
        }
    }

    /// <summary>日志类别按保留窗口判定；其余类别一律删。</summary>
    private static bool IsRetained(string file, CategoryDefinition definition, CleanContext context)
    {
        if (definition.RetainWindow is null)
        {
            return false;
        }

        try
        {
            // 读不到写入时间时按「保留」处理：宁可为用户多留一个日志文件，也不要因为时间戳异常误删现场。
            return File.GetLastWriteTimeUtc(file) >= context.LogCutoff.UtcDateTime;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return true;
        }
    }

    private static bool IsProtected(string path, IReadOnlyList<string> protectedRoots)
    {
        foreach (var root in protectedRoots)
        {
            if (IsSameOrInside(root, path))
            {
                return true;
            }
        }

        return ProtectedFileNames.Contains(Path.GetFileName(path));
    }

    // ==================== 类别定义与安全校验 ====================

    private List<CategoryDefinition> BuildDefinitions()
    {
        var cache = _paths.CacheDirectory;
        var installer = Path.Combine(cache, LoaderInstallDirectoryName);
        var trash = Path.Combine(_paths.RootDirectory, "trash");

        return
        [
            new CategoryDefinition(
                StorageCategoryKeys.Cache,
                "下载缓存",
                cache,
                CanClean: true,
                "下载缓存与图标、头像缓存。清理后再次下载或打开对应页面时会自动重建。",
                [installer],
                RetainWindow: null),

            new CategoryDefinition(
                StorageCategoryKeys.Logs,
                "日志",
                _paths.LogsDirectory,
                CanClean: true,
                $"保留最近 {LogRetentionDays} 天的日志，更早的删除。正在写入的当日日志不受影响。",
                [],
                TimeSpan.FromDays(LogRetentionDays)),

            new CategoryDefinition(
                StorageCategoryKeys.Installer,
                "安装器临时文件",
                installer,
                CanClean: true,
                "加载器安装器的解包与下载临时目录。安装失败留下的残留可以整块清掉。",
                [],
                RetainWindow: null),

            new CategoryDefinition(
                StorageCategoryKeys.Trash,
                "回收站",
                trash,
                CanClean: true,
                "实例回收站与内容回收站。清理后已删除的实例与文件无法再恢复。",
                [],
                RetainWindow: null),
        ];
    }

    private CleanContext CreateContext()
    {
        var dataRoot = TryNormalize(_paths.RootDirectory, out var normalized) ? normalized : _paths.RootDirectory;

        var protectedRoots = new List<string>(ProtectedTopLevelNames.Length);
        foreach (var name in ProtectedTopLevelNames)
        {
            if (TryNormalize(Path.Combine(_paths.RootDirectory, name), out var protectedRoot))
            {
                protectedRoots.Add(protectedRoot);
            }
        }

        return new CleanContext
        {
            DataRoot = dataRoot,
            LogCutoff = DateTimeOffset.Now - TimeSpan.FromDays(LogRetentionDays),
            ProtectedRoots = protectedRoots,
        };
    }

    /// <summary>
    /// 目标是否严格位于数据根之下（不含数据根本身）。
    /// 用「根 + 分隔符」做前缀比较：只比 <c>StartsWith(root)</c> 会把 <c>&lt;data&gt;-backup</c>
    /// 这类同名前缀的兄弟目录误判成根内。
    /// </summary>
    private static bool IsInsideDataRoot(string dataRoot, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(dataRoot);
        var normalized = Path.TrimEndingDirectorySeparator(candidate);

        if (string.Equals(root, normalized, PathComparison))
        {
            return false;
        }

        return normalized.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsSameOrInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);

        if (string.Equals(normalizedRoot, normalizedCandidate, PathComparison))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsExcluded(string directory, List<string> excluded) =>
        excluded.Exists(path => string.Equals(path, directory, PathComparison));

    /// <summary>把类别定义里的排除目录规范化。统计与清理共用同一份，口径才不会漂。</summary>
    private static List<string> NormalizeAll(IReadOnlyList<string> paths)
    {
        var result = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (TryNormalize(path, out var normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>文件系统类失败：一律「跳过并记日志」，不让单点异常中断整批扫描 / 清理。</summary>
    private static bool IsIoFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;

    /// <summary>一个统计 / 清理类别。</summary>
    /// <param name="RootPath">类别根目录的绝对路径。</param>
    /// <param name="ExcludedDirectories">统计时要跳过的子树（绝对路径）。</param>
    /// <param name="RetainWindow">保留窗口；<c>null</c> 表示全清。</param>
    private sealed record CategoryDefinition(
        string Key,
        string DisplayName,
        string RootPath,
        bool CanClean,
        string CleanHint,
        IReadOnlyList<string> ExcludedDirectories,
        TimeSpan? RetainWindow);

    /// <summary>一次清理的累计量。用可变对象在递归里累加，避免每层都返回元组。</summary>
    private sealed class CleanCounter
    {
        public long Bytes { get; set; }

        public int Files { get; set; }

        public int Failed { get; set; }
    }

    /// <summary>一次清理的不可变上下文：数据根、日志保留截止时刻、受保护顶层目录。</summary>
    private sealed class CleanContext
    {
        public required string DataRoot { get; init; }

        public required DateTimeOffset LogCutoff { get; init; }

        public required IReadOnlyList<string> ProtectedRoots { get; init; }
    }
}