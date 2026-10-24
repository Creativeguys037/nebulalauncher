using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 模组 / 资源包 / 光影包 / 数据包的扫描与管理。禁用通过改扩展名实现（<c>.jar.disabled</c>），
/// 这样加载器不会读到它，用户也能一眼看出文件还在。
///
/// 数据包是唯一的**双目录 + 目录形态**内容：它既能放在 <c>datapacks/</c>，
/// 也能放在 <c>saves/&lt;world&gt;/datapacks/</c>（只对那个世界生效），
/// 而且可以直接是一个目录而不是 zip。所以它的扫描与启停都不复用其他三类文件的分支。
/// </summary>
public sealed class ContentService : IContentService
{
    /// <summary>本启动器生成的禁用后缀。</summary>
    private const string DisabledSuffix = ".disabled";

    /// <summary>
    /// PCL2 / 部分第三方工具用的禁用后缀（<c>LocalResourceFile.vb:39/41</c>、
    /// <c>PageInstanceMod.xaml.vb:465/674</c>）。迁移来的实例里大量存在，必须认。
    ///
    /// 与 <see cref="DisabledSuffix"/> 并存时的**优先级**：
    /// 1. 禁用时若同名 <c>.old</c> 已存在，沿用它（PCL2 的做法，避免生成第二份文件）；
    /// 2. 扫描时若两种后缀同时存在，只列 <c>.disabled</c> 那条——它是本启动器自己生成的，
    ///    另一份多半是迁移残留。这条规则保证同一模组在列表里只出现一次。
    /// </summary>
    private const string LegacyDisabledSuffix = ".old";

    /// <summary>全部禁用后缀，顺序即「生成时优先选谁」的顺序。</summary>
    private static readonly string[] DisabledSuffixes = [DisabledSuffix, LegacyDisabledSuffix];

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public ContentService(ILauncherPaths paths, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
    }

    /// <summary>内容回收站根目录。<c>&lt;data&gt;/trash/contents/</c>，与实例回收站并列。</summary>
    private string TrashRoot => Path.Combine(_paths.RootDirectory, "trash", "contents");

    /// <summary>内容类型对应的游戏目录名。数据包的全局落点是 <c>datapacks/</c>（存档级由扫描器自行展开）。</summary>
    public static string DirectoryNameFor(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        ContentKind.World => "saves",
        ContentKind.DataPack => "datapacks",

        // 服务端插件：所有安装入口在走到这里之前已经拒绝（见 ServerPluginPolicy），
        // 这个映射只是兜底——万一哪条新路径漏了拦截，插件会落到 plugins/ 而不是 mods/，
        // 客户端看不到它，于是「装错了」不会变成「启动崩溃」。
        // 这不代表支持插件安装：没有扫描入口，也没有任何界面会列出这个目录。
        ContentKind.Plugin => "plugins",

        _ => "mods",
    };

    /// <summary>
    /// 磁盘上的真实启用态：<c>true</c> 启用、<c>false</c> 禁用、<c>null</c> 两个候选都不存在。
    ///
    /// 与 <see cref="ContentBatch.IsEnabledOnDisk"/> 的区别只有一处：这里同时认目录。
    /// <see cref="ContentBatch"/> 是文件专用的（判据写死 <c>File.Exists</c>），
    /// 数据包的目录形态走它会把「改名成功」全数误判成失败。
    /// </summary>
    public static bool? IsEnabledOnDisk(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var enabledPath = EnabledPathOf(item.FilePath);
        if (File.Exists(enabledPath) || Directory.Exists(enabledPath))
        {
            return true;
        }

        return ExistsAnyDisabled(enabledPath) ? false : null;
    }

    /// <summary>
    /// 去掉禁用后缀（<c>.disabled</c> 或 <c>.old</c>）。
    /// <c>a.zip.disabled</c> / <c>a.zip.old</c> → <c>a.zip</c>；已启用时原样返回。
    /// </summary>
    public static string EnabledPathOf(string path)
    {
        foreach (var suffix in DisabledSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^suffix.Length];
            }
        }

        return path;
    }

    /// <summary>加上本启动器使用的禁用后缀。需要「沿用手写 <c>.old</c>」的语义时用 <see cref="DisabledCandidatesOf"/>。</summary>
    public static string DisabledPathOf(string path) => EnabledPathOf(path) + DisabledSuffix;

    /// <summary>
    /// 一个启用态路径对应的全部禁用态候选，顺序即优先级（<c>.disabled</c> 先）。
    /// 删除与启用态判定都要看全，否则迁移实例里的 <c>.jar.old</c> 会被当成「文件已被删除」。
    /// </summary>
    public static IReadOnlyList<string> DisabledCandidatesOf(string path)
    {
        var enabledPath = EnabledPathOf(path);
        return DisabledSuffixes.Select(suffix => enabledPath + suffix).ToList();
    }

    /// <summary>
    /// 禁用时应落到的路径：同名 <c>.old</c> 已存在就沿用它（PCL2 <c>PageInstanceMod.xaml.vb:465</c>），
    /// 否则用 <c>.disabled</c>。这条规则让「从 PCL2 迁来的 .old 模组」被禁用后不会变成两个文件。
    /// </summary>
    public static string PreferredDisabledPathOf(string path)
    {
        var enabledPath = EnabledPathOf(path);

        foreach (var suffix in DisabledSuffixes)
        {
            var candidate = enabledPath + suffix;
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return enabledPath + DisabledSuffix;
    }

    private static bool ExistsAnyDisabled(string enabledPath)
    {
        foreach (var candidate in DisabledCandidatesOf(enabledPath))
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    public Task<IReadOnlyList<ContentItem>> ScanAsync(
        string gameDirectory,
        ContentKind kind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Task.FromResult<IReadOnlyList<ContentItem>>(Array.Empty<ContentItem>());
        }

        // 数据包有两个来源目录，且全局目录不存在时存档级仍可能有内容，不能提前 Directory.Exists 返回。
        if (kind == ContentKind.DataPack)
        {
            return Task.Run<IReadOnlyList<ContentItem>>(
                () => ScanDataPacks(gameDirectory, cancellationToken),
                cancellationToken);
        }

        var directory = Path.Combine(gameDirectory, DirectoryNameFor(kind));
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<ContentItem>>(Array.Empty<ContentItem>());
        }

        return Task.Run<IReadOnlyList<ContentItem>>(
            () => kind == ContentKind.World
                ? ScanWorlds(directory, cancellationToken)
                : ScanFiles(directory, kind, cancellationToken),
            cancellationToken);
    }

    public Task SetEnabledAsync(ContentItem item, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // FilePath 是 init-only（模型已冻结），所以每次都从当前路径推导出「启用态 / 禁用态」
            // 两个候选，方法因此天然幂等：重复调用不会因为上一次改名而找不到文件。
            var enabledPath = EnabledPathOf(item.FilePath);
            var disabledCandidates = DisabledCandidatesOf(item.FilePath);

            // 目录形态（数据包）必须走 Directory.Move：File.Move 对目录会抛，
            // File.Exists 对目录恒为 false，混用会把「目录改名成功」判成找不到文件。
            var isDirectory = Directory.Exists(enabledPath) || disabledCandidates.Any(Directory.Exists);

            var exists = isDirectory
                ? (Func<string, bool>)Directory.Exists
                : File.Exists;

            var current = exists(enabledPath)
                ? enabledPath
                : disabledCandidates.FirstOrDefault(exists);

            if (current is null)
            {
                _log?.Warn($"切换启用状态失败，文件不存在：{item.FilePath}");
                return Task.CompletedTask;
            }

            // 禁用时沿用手写 .old（PCL2 语义），启用时一律回到无后缀路径。
            var target = enabled ? enabledPath : PreferredDisabledPathOf(enabledPath);
            if (!string.Equals(current, target, StringComparison.Ordinal))
            {
                if (isDirectory)
                {
                    Directory.Move(current, target);
                }
                else
                {
                    File.Move(current, target, overwrite: false);
                }

                _log?.Info($"{(enabled ? "启用" : "禁用")} {Path.GetFileName(target)}");
            }

            item.IsEnabled = enabled;
        }
        catch (IOException ex)
        {
            _log?.Error($"切换启用状态失败：{item.FileName}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Error($"切换启用状态失败（权限）：{item.FileName}", ex);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(ContentItem item, CancellationToken cancellationToken = default)
        => DeleteAsync(item, permanent: false, cancellationToken);

    public Task DeleteAsync(ContentItem item, bool permanent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        if (permanent)
        {
            DeletePermanently(item);
            return Task.CompletedTask;
        }

        // 默认进回收站：跨卷 / 权限不足时退回「复制 + 删除」，两条路都失败才记错误。
        // 一次删除必须把同名的另一份也带走（PCL2 `:674` 的 SelectMany 逻辑）：
        // 只删当前那一份会让「删掉一个模组」在列表里剩下一半。
        foreach (var path in ExistingPathsOf(item))
        {
            try
            {
                MoveToTrash(path, item.Kind, item.FileName);
            }
            catch (Exception ex)
            {
                _log?.Error($"移入回收站失败：{path}", ex);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContentTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ContentTrashEntry>>(() => ListTrash(cancellationToken), cancellationToken);

    public Task<bool> RestoreAsync(string trashId, CancellationToken cancellationToken = default)
        => Task.Run(() => Restore(trashId, cancellationToken), cancellationToken);

    public Task PurgeTrashAsync(string trashId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSafeTrashId(trashId))
        {
            _log?.Warn($"彻底删除失败：非法的回收站条目 Id {trashId}");
            return Task.CompletedTask;
        }

        var directory = Path.Combine(TrashRoot, trashId);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _log?.Info($"回收站条目已彻底删除：{trashId}");
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"彻底删除回收站条目失败：{directory}", ex);
        }

        return Task.CompletedTask;
    }

    // ---------------- 回收站实现 ----------------

    // TrashMetadata 提到文件末尾：Source Generator 需要能在同程序集内引用它。

    /// <summary>
    /// 与删除操作相关的全部现存路径：当前路径 + 同名另一份（<c>.disabled</c> / <c>.old</c>）。
    /// 顺序去重，删的时候一份都不留。
    /// </summary>
    private static List<string> ExistingPathsOf(ContentItem item)
    {
        var paths = new List<string>();
        var enabledPath = EnabledPathOf(item.FilePath);

        foreach (var candidate in new[] { item.FilePath, enabledPath }.Concat(DisabledCandidatesOf(item.FilePath)))
        {
            if (paths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                paths.Add(candidate);
            }
        }

        return paths;
    }

    private void DeletePermanently(ContentItem item)
    {
        foreach (var path in ExistingPathsOf(item))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                _log?.Info($"已永久删除：{path}");
            }
            catch (Exception ex)
            {
                _log?.Error($"永久删除失败：{path}", ex);
            }
        }
    }

    /// <summary>
    /// 把文件 / 目录移进 <c>&lt;data&gt;/trash/contents/&lt;trashId&gt;/</c>。
    ///
    /// 先移动后写 meta：反过来的话，移动失败会留下一个指向不存在文件的条目。
    /// 跨卷时 <c>Move</c> 会抛 <see cref="IOException"/>，退回「复制 + 删除」。
    /// </summary>
    private void MoveToTrash(string path, ContentKind kind, string displayName)
    {
        var deletedAt = DateTimeOffset.UtcNow;
        var trashId = MakeTrashId(deletedAt);
        var trashPath = Path.Combine(TrashRoot, trashId);
        var isDirectory = Directory.Exists(path);

        Directory.CreateDirectory(trashPath);

        var payload = Path.Combine(trashPath, Path.GetFileName(path));

        try
        {
            if (isDirectory)
            {
                Directory.Move(path, payload);
            }
            else
            {
                File.Move(path, payload, overwrite: false);
            }
        }
        catch (IOException)
        {
            MoveAcrossVolumes(path, payload, isDirectory);
        }

        var metadata = new TrashMetadata
        {
            FileName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(path) : displayName,
            OriginalPath = path,
            Kind = (int)kind,
            DeletedAt = deletedAt,
            SizeBytes = isDirectory ? -1 : new FileInfo(payload).Length,
            IsDirectory = isDirectory,
        };

        try
        {
            File.WriteAllText(
                Path.Combine(trashPath, "meta.json"),
                JsonSerializer.Serialize(metadata, ContentJsonContext.Default.TrashMetadata));
        }
        catch (Exception ex)
        {
            // meta 写不进去只影响「列表里的显示名与恢复目标」，文件本身已经在回收站里了。
            _log?.Warn($"写入回收站元数据失败：{trashPath}", ex);
        }

        _log?.Info($"已移入回收站：{path} → {trashPath}");
    }

    private static void MoveAcrossVolumes(string path, string payload, bool isDirectory)
    {
        if (isDirectory)
        {
            CopyDirectory(path, payload);
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Copy(path, payload, overwrite: false);
        File.Delete(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private List<ContentTrashEntry> ListTrash(CancellationToken cancellationToken)
    {
        var entries = new List<ContentTrashEntry>();

        try
        {
            if (!Directory.Exists(TrashRoot))
            {
                return entries;
            }

            foreach (var directory in Directory.EnumerateDirectories(TrashRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trashId = Path.GetFileName(directory);
                var metadata = ReadTrashMetadata(directory);
                var payload = FindPayload(directory, metadata);

                if (payload is null)
                {
                    // 只有 meta 没有内容：跳过而不是当成条目，否则「恢复」会恢复出一个空壳。
                    continue;
                }

                entries.Add(new ContentTrashEntry
                {
                    Id = trashId,
                    OriginalPath = string.IsNullOrWhiteSpace(metadata?.OriginalPath)
                        ? payload
                        : metadata.OriginalPath,
                    FileName = string.IsNullOrWhiteSpace(metadata?.FileName)
                        ? Path.GetFileName(payload)
                        : metadata.FileName,
                    TrashPath = payload,
                    Kind = metadata is null ? ContentKind.Mod : (ContentKind)metadata.Kind,
                    DeletedAt = metadata?.DeletedAt ?? Directory.GetLastWriteTimeUtc(directory),
                    SizeBytes = metadata?.SizeBytes ?? -1,
                    IsDirectory = Directory.Exists(payload),
                });
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"读取内容回收站失败：{TrashRoot}", ex);
        }

        return entries
            .OrderByDescending(entry => entry.DeletedAt)
            .ToList();
    }

    private bool Restore(string trashId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSafeTrashId(trashId))
        {
            _log?.Warn($"恢复失败：非法的回收站条目 Id {trashId}");
            return false;
        }

        var directory = Path.Combine(TrashRoot, trashId);

        if (!Directory.Exists(directory))
        {
            _log?.Warn($"恢复失败：回收站条目不存在 {directory}");
            return false;
        }

        var metadata = ReadTrashMetadata(directory);
        var payload = FindPayload(directory, metadata);

        if (payload is null)
        {
            _log?.Warn($"恢复失败：回收站条目没有内容 {directory}");
            return false;
        }

        var target = string.IsNullOrWhiteSpace(metadata?.OriginalPath)
            ? payload
            : metadata.OriginalPath;

        try
        {
            var targetDirectory = Path.GetDirectoryName(target);

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 原位置已有同名文件时不覆盖：用户在删除后可能又装了同一个模组的新版本，
            // 覆盖会把它静静换掉。
            var free = ResolveFreePathAt(target);

            if (Directory.Exists(payload))
            {
                if (Directory.Exists(free))
                {
                    Directory.Delete(free, recursive: true);
                }

                Directory.Move(payload, free);
            }
            else
            {
                File.Move(payload, free, overwrite: false);
            }

            TryDeleteDirectory(directory);
            _log?.Info($"已从回收站恢复：{trashId} → {free}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error($"恢复失败：{payload} → {target}", ex);
            return false;
        }
    }

    private TrashMetadata? ReadTrashMetadata(string trashDirectory)
    {
        var file = Path.Combine(trashDirectory, "meta.json");

        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize(
                    File.ReadAllText(file),
                    ContentJsonContext.Default.TrashMetadata)
                : null;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取回收站元数据失败：{file}", ex);
            return null;
        }
    }

    /// <summary>回收站目录里除 <c>meta.json</c> 之外的那一份内容。找不到返回 null。</summary>
    private static string? FindPayload(string trashDirectory, TrashMetadata? metadata)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(metadata?.FileName))
            {
                // meta 里的 FileName 是显示名，落盘名取自 OriginalPath 的最后一段。
                var original = string.IsNullOrWhiteSpace(metadata.OriginalPath)
                    ? null
                    : Path.GetFileName(metadata.OriginalPath);

                if (original is not null)
                {
                    var candidate = Path.Combine(trashDirectory, original);
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return Directory.EnumerateFileSystemEntries(trashDirectory)
                .Where(entry => !string.Equals(Path.GetFileName(entry), "meta.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>回收站 Id 只允许本项目生成的字符集：拼进路径前必须挡掉 <c>..</c> 与分隔符。</summary>
    private static bool IsSafeTrashId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? trashId)
        => !string.IsNullOrWhiteSpace(trashId)
           && trashId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && !trashId.Contains("..", StringComparison.Ordinal)
           && !trashId.Contains(Path.DirectorySeparatorChar)
           && !trashId.Contains(Path.AltDirectorySeparatorChar);

    private string MakeTrashId(DateTimeOffset deletedAt)
    {
        var baseId = deletedAt.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        for (var index = 0; index < 1000; index++)
        {
            // 同一秒内连删多个文件很常见，纯时间戳会撞名，所以固定带一段随机尾巴。
            var candidate = index == 0
                ? $"{baseId}-{Guid.NewGuid().ToString("N")[..8]}"
                : $"{baseId}-{index}";

            if (!Directory.Exists(Path.Combine(TrashRoot, candidate)))
            {
                return candidate;
            }
        }

        return $"{baseId}-{Guid.NewGuid():N}";
    }

    /// <summary>同名文件已存在时追加 (1)、(2)……；这里用在恢复路径上，路径可能已是自由态。</summary>
    private static string ResolveFreePathAt(string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        if (!exists)
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"清理回收站条目目录失败：{directory}", ex);
        }
    }

    public async Task<IReadOnlyList<string>> ImportAsync(
        string gameDirectory,
        ContentKind kind,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);

        var targetDirectory = Path.Combine(gameDirectory, DirectoryNameFor(kind));
        Directory.CreateDirectory(targetDirectory);

        var imported = new List<string>(sourceFiles.Count);
        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                _log?.Warn($"跳过不存在的文件：{source}");
                continue;
            }

            try
            {
                var target = ResolveFreePath(targetDirectory, Path.GetFileName(source));
                await CopyFileAsync(source, target, cancellationToken).ConfigureAwait(false);
                imported.Add(target);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Error($"导入失败：{source}", ex);
            }
        }

        return imported;
    }

    public IReadOnlyList<ModConflict> DetectConflicts(IReadOnlyList<ContentItem> mods)
    {
        if (mods is null || mods.Count == 0)
        {
            return Array.Empty<ModConflict>();
        }

        var conflicts = new List<ModConflict>();
        var groups = mods
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && item.Kind == ContentKind.Mod)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var files = group.Select(item => item.FileName).ToList();
            if (files.Count <= 1)
            {
                continue;
            }

            var versions = group
                .Select(item => string.IsNullOrWhiteSpace(item.Version) ? "未知版本" : item.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            conflicts.Add(new ModConflict(
                group.Key,
                $"检测到 {files.Count} 个同 modid 的文件（{string.Join("、", versions)}），加载时会冲突",
                files));
        }

        return conflicts;
    }

    public IReadOnlyList<ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<ContentItem> mods)
    {
        if (mods is null || mods.Count == 0)
        {
            return Array.Empty<ModDependencyIssue>();
        }

        // 只有已启用的模组才会被加载，才可能提供 modid。禁用的 jar 不算「已提供」：
        // 否则「A 依赖 B、B 被禁用」会被漏报。
        var provided = new HashSet<string>(
            mods.Where(item => item.IsEnabled
                               && item.Kind == ContentKind.Mod
                               && !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => item.Id),
            StringComparer.OrdinalIgnoreCase);

        var issues = new List<ModDependencyIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (!mod.IsEnabled || mod.Kind != ContentKind.Mod || mod.Depends.Count == 0)
            {
                continue;
            }

            foreach (var dependency in mod.Depends)
            {
                if (string.IsNullOrWhiteSpace(dependency.ModId) || provided.Contains(dependency.ModId))
                {
                    continue;
                }

                var range = string.IsNullOrWhiteSpace(dependency.VersionRange)
                    ? null
                    : dependency.VersionRange;

                // 同一个 mod 可能在 TOML 里多段声明同一依赖：按内容去重，避免提示刷屏。
                if (!seen.Add($"{mod.Id}\u001F{dependency.ModId}\u001F{range}\u001F{dependency.Optional}"))
                {
                    continue;
                }

                issues.Add(new ModDependencyIssue(
                    mod.Id,
                    dependency.ModId,
                    range,
                    dependency.Optional,
                    [mod.FileName]));
            }
        }

        return issues;
    }

    public IReadOnlyList<ContentItem> FindVersionMismatch(
        IReadOnlyList<ContentItem> mods,
        string minecraftVersion)
    {
        if (mods is null || mods.Count == 0 || string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return Array.Empty<ContentItem>();
        }

        var mismatched = new List<ContentItem>();
        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.McVersionRange))
            {
                continue;
            }

            var range = McVersionRange.Parse(mod.McVersionRange);
            if (!range.Contains(minecraftVersion))
            {
                mismatched.Add(mod);
            }
        }

        return mismatched;
    }

    // ---------------- 扫描实现 ----------------

    /// <summary>
    /// 扫描数据包的两个来源：全局 <c>datapacks/</c> 与每个存档的 <c>saves/&lt;world&gt;/datapacks/</c>。
    /// 任何一个目录缺失都只是少一批条目，不抛异常。
    /// </summary>
    private List<ContentItem> ScanDataPacks(string gameDirectory, CancellationToken cancellationToken)
    {
        var items = new List<ContentItem>();

        CollectDataPacks(Path.Combine(gameDirectory, "datapacks"), worldName: null, items, cancellationToken);

        var savesDirectory = Path.Combine(gameDirectory, "saves");
        foreach (var worldDirectory in SafeEnumerateDirectories(savesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worldName = Path.GetFileName(worldDirectory);
            CollectDataPacks(Path.Combine(worldDirectory, "datapacks"), worldName, items, cancellationToken);
        }

        // 启用优先 → 全局在前（SourceHint 为 null）→ 世界名 → 显示名。
        // 存档级条目必须带世界名，否则「x 世界的数据包」和「全局数据包」在列表里无法区分。
        return DeduplicateVariants(items)
            .OrderByDescending(item => item.IsEnabled)
            .ThenBy(item => item.SourceHint ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void CollectDataPacks(
        string directory,
        string? worldName,
        List<ContentItem> items,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in SafeEnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDataPackFile(file))
            {
                continue;
            }

            try
            {
                items.Add(BuildDataPackItem(file, isDirectory: false, worldName));
            }
            catch (Exception ex)
            {
                // 单个包读不出来（被占用 / 权限）只丢这一条，其余照常列出。
                _log?.Warn($"读取数据包失败：{file}", ex);
            }
        }

        // 目录形态：Minecraft 把 datapacks/ 下的每个子目录都当成一个数据包，
        // 所以这里不按名字过滤（禁用态是两个候选之一，改名后仍在同一层）。
        foreach (var subdirectory in SafeEnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                items.Add(BuildDataPackItem(subdirectory, isDirectory: true, worldName));
            }
            catch (Exception ex)
            {
                _log?.Warn($"读取数据包目录失败：{subdirectory}", ex);
            }
        }
    }

    private ContentItem BuildDataPackItem(string path, bool isDirectory, string? worldName)
    {
        var fileName = Path.GetFileName(path);
        var enabled = !HasDisabledSuffix(fileName);
        var displayFileName = EnabledPathOf(fileName);

        // 目录形态没有扩展名可剥；zip 形态剥掉 .zip。
        var stem = isDirectory
            ? displayFileName
            : Path.GetFileNameWithoutExtension(displayFileName);

        long size;
        DateTime lastWrite;

        if (isDirectory)
        {
            var info = new DirectoryInfo(path);
            size = DirectorySize(info);
            lastWrite = info.LastWriteTimeUtc;
        }
        else
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWrite = info.LastWriteTimeUtc;
        }

        var (description, packFormat, minFormat, maxFormat) = ReadDataPackMeta(path, isDirectory);
        var formatRange = minFormat is null && maxFormat is null
            ? null
            : new PackFormatRange(minFormat, maxFormat);

        byte[]? iconBytes = ReadDataPackIcon(path, isDirectory);

        return new ContentItem
        {
            FilePath = path,
            FileName = fileName,
            Kind = ContentKind.DataPack,
            Id = stem,
            DisplayName = stem,

            // 数据包没有版本号，pack_format / min-max_format 是唯一能说明「这个包适配哪个版本」的数字。
            Version = PackFormatTable.DescribeDeclaration(packFormat, formatRange),
            MinPackFormat = minFormat,
            MaxPackFormat = maxFormat,
            Description = description,
            IconPath = iconBytes is { Length: > 0 } ? TryCacheIcon(iconBytes) : null,
            FileSize = size,
            LastModified = lastWrite,
            IsEnabled = enabled,
            SourceHint = worldName,

            // 数据包与加载器无关：写 Vanilla 而不是猜。
            Loader = LoaderKind.Vanilla,
        };
    }

    /// <summary>
    /// 数据包的图标同样是包根目录的 <c>pack.png</c>：zip 形态走压缩包，目录形态直接读文件。
    ///
    /// 读不到（没有这个文件 / 被占用 / 超过体积上限）返回 null，卡片退回首字母占位；
    /// 图标不是数据包能被扫描出来的前提，任何失败都不写入 <see cref="ContentItem.ParseError"/>。
    /// </summary>
    private static byte[]? ReadDataPackIcon(string path, bool isDirectory)
    {
        const long maxBytes = 4 * 1024 * 1024;

        try
        {
            if (!isDirectory)
            {
                return ModMetadataReader.ReadFirstEntryBytesFromFile(path, ["pack.png"]);
            }

            // 目录形态按名字匹配而不是拼 "pack.png"：非 Windows 文件系统区分大小写，
            // 而玩家手工解压出来的包常写 Pack.png。
            foreach (var candidate in SafeEnumerateFiles(path))
            {
                if (!string.Equals(Path.GetFileName(candidate), "pack.png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(candidate);
                return info.Length is > 0 and <= maxBytes ? File.ReadAllBytes(candidate) : null;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsDataPackFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || DisabledSuffixes.Any(suffix => path.EndsWith(".zip" + suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>文件名是否带任一禁用后缀。</summary>
    public static bool HasDisabledSuffix(string path) =>
        DisabledSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 读 <c>pack.mcmeta</c> 里的 <c>pack_format</c> 与 <c>description</c>。
    ///
    /// 刻意不复用 <see cref="ModMetadataReader.ReadFile"/>：那个方法只认 zip 文件，
    /// 对目录形态会以 <c>UnauthorizedAccessException</c> 收场并把「解析失败」写进卡片，
    /// 而目录形态恰恰是数据包的正常形态之一。这里读不出来一律返回 null，不算错误。
    /// </summary>
    private static (string? Description, int? PackFormat, double? MinFormat, double? MaxFormat) ReadDataPackMeta(
        string path,
        bool isDirectory)
    {
        try
        {
            using Stream? stream = isDirectory
                ? OpenDataPackFile(Path.Combine(path, "pack.mcmeta"))
                : OpenZipEntry(path, "pack.mcmeta");

            if (stream is null)
            {
                return (null, null, null, null);
            }

            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("pack", out var pack)
                || pack.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null);
            }

            // 旧式整数 pack_format：1.21.8 及更早的包只有它。
            int? format = pack.TryGetProperty("pack_format", out var formatElement)
                          && formatElement.ValueKind == JsonValueKind.Number
                          && formatElement.TryGetInt32(out var value)
                ? value
                : null;

            // 1.21.9+ 的区间写法：min_format / max_format 允许小数（88.0 / 94.1），
            // 缺任意一端都合法（缺 min = 下界开放，缺 max = 上界开放）。
            var minFormat = ReadFormatNumber(pack, "min_format");
            var maxFormat = ReadFormatNumber(pack, "max_format");

            var description = pack.TryGetProperty("description", out var descriptionElement)
                ? ReadDataPackDescription(descriptionElement)
                : null;

            return (description, format, minFormat, maxFormat);
        }
        catch (Exception)
        {
            // 非法 JSON、被占用、权限不足都不该影响列表：描述与格式只是锦上添花。
            return (null, null, null, null);
        }
    }

    private static double? ReadFormatNumber(JsonElement pack, string propertyName) =>
        pack.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var value)
        && value >= 0
            ? value
            : null;

    /// <summary>数据包的 description 允许直接写字符串，也允许写 text component 对象。</summary>
    private static string? ReadDataPackDescription(JsonElement element)
    {
        string? text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("text", out var inner) => inner.GetString(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static FileStream? OpenDataPackFile(string path) =>
        File.Exists(path) ? File.OpenRead(path) : null;

    /// <summary>
    /// 取 zip 里某个条目的内容。必须先把字节读进 <see cref="MemoryStream"/>：
    /// <see cref="ZipArchive"/> 与底层文件流在 using 结束时关闭，直接返回条目流会读到已释放的对象。
    /// </summary>
    private static MemoryStream? OpenZipEntry(string archivePath, string entryName)
    {
        using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        var buffer = new MemoryStream();
        using (var entryStream = entry.Open())
        {
            entryStream.CopyTo(buffer);
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>目录形态数据包的大小。算不出来返回 -1（与世界目录的「未知大小」同一约定）。</summary>
    private static long DirectorySize(DirectoryInfo directory)
    {
        try
        {
            long total = 0;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                    // 单个文件读不到就跳过，把能算的加起来。
                }
            }

            return total;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private List<ContentItem> ScanWorlds(string directory, CancellationToken cancellationToken)
    {
        var items = new List<ContentItem>();
        foreach (var worldDirectory in SafeEnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new DirectoryInfo(worldDirectory);
                items.Add(new ContentItem
                {
                    FilePath = info.FullName,
                    FileName = info.Name,
                    Kind = ContentKind.World,
                    Id = info.Name,
                    DisplayName = info.Name,
                    FileSize = -1,
                    LastModified = info.LastWriteTimeUtc,
                    IsEnabled = true,
                });
            }
            catch (Exception ex)
            {
                _log?.Warn($"读取世界目录失败：{worldDirectory}", ex);
            }
        }

        return items;
    }

    private List<ContentItem> ScanFiles(
        string directory,
        ContentKind kind,
        CancellationToken cancellationToken)
    {
        var items = new List<ContentItem>();
        foreach (var file in SafeEnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsContentFile(file, kind))
            {
                continue;
            }

            items.Add(BuildItem(file, kind));
        }

        return DeduplicateVariants(items)
            .OrderByDescending(item => item.IsEnabled)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 同一模组的多个文件变体（<c>a.jar</c> / <c>a.jar.disabled</c> / <c>a.jar.old</c>）只保留一条。
    ///
    /// 不合并的话，迁移实例里同时存在两种禁用后缀时，同一个模组会在列表里出现两次——
    /// 用户删掉其中一条，另一条还在，看起来像「删不掉」。
    /// 优先级：启用态 → <c>.disabled</c> → <c>.old</c>（见 <see cref="DisabledSuffixes"/> 的注释）。
    /// </summary>
    private static IEnumerable<ContentItem> DeduplicateVariants(List<ContentItem> items)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        return items
            .GroupBy(item => EnabledPathOf(item.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.IsEnabled)
                .ThenBy(item => SuffixRank(item.FilePath))
                .First());
    }

    /// <summary>禁用后缀的优先级序号，越小越优先；启用态返回后缀总数（排最后）。</summary>
    private static int SuffixRank(string path)
    {
        for (var index = 0; index < DisabledSuffixes.Length; index++)
        {
            if (path.EndsWith(DisabledSuffixes[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return DisabledSuffixes.Length;
    }

    private static bool IsContentFile(string path, ContentKind kind) => kind switch
    {
        // LiteLoader 的 .litemod 与 .jar 同语义（PCL2 `LocalResourceFile.vb:39` 把三者并列）。
        // 不按 LiteLoader 加载器分组：Nebula 不认这个加载器，这里只负责「看得见、管得了」。
        ContentKind.Mod => HasAnyExtension(path, ".jar", ".litemod"),
        ContentKind.ResourcePack => HasAnyExtension(path, ".zip", ".resourcepack"),
        ContentKind.ShaderPack => HasAnyExtension(path, ".zip"),

        // 插件文件按 .jar 认，但客户端不扫这个类型：这里返回 false 是刻意的，
        // 保证任何「顺手把 Plugin 传给扫描器」的调用都拿不到内容而不是列出一堆 plugins/。
        ContentKind.Plugin => false,

        _ => false,
    };

    /// <summary>启用态或任一禁用态下扩展名是否命中。<c>a.litemod.old</c> 也算模组文件。</summary>
    private static bool HasAnyExtension(string path, params string[] extensions)
    {
        var baseName = EnabledPathOf(path);

        foreach (var extension in extensions)
        {
            if (baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private ContentItem BuildItem(string path, ContentKind kind)
    {
        var fileName = Path.GetFileName(path);
        var enabled = !HasDisabledSuffix(fileName);
        var displayFileName = EnabledPathOf(fileName);

        var metadata = new ModMetadata();
        byte[]? iconBytes = null;

        if (kind != ContentKind.World)
        {
            try
            {
                (metadata, iconBytes) = ModMetadataReader.ReadFile(path, kind);
            }
            catch (Exception ex)
            {
                metadata = new ModMetadata { Error = ex.Message };
            }
        }

        string? iconPath = null;
        if (iconBytes is { Length: > 0 })
        {
            iconPath = TryCacheIcon(iconBytes);
        }

        long size;
        DateTime lastWrite;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWrite = info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            size = 0;
            lastWrite = DateTime.UtcNow;
        }

        return new ContentItem
        {
            FilePath = path,
            FileName = fileName,
            Kind = kind,
            Id = string.IsNullOrWhiteSpace(metadata.Id)
                ? Path.GetFileNameWithoutExtension(displayFileName)
                : metadata.Id,
            DisplayName = FirstNonEmpty(metadata.Name, Path.GetFileNameWithoutExtension(displayFileName)),
            Version = metadata.Version ?? string.Empty,
            MinPackFormat = metadata.MinPackFormat,
            MaxPackFormat = metadata.MaxPackFormat,
            Description = metadata.Description,
            Authors = metadata.Authors,
            IconPath = iconPath,
            Loader = metadata.Loader,
            McVersionRange = metadata.McVersionRange,
            HomePage = metadata.HomePage,
            Depends = metadata.Depends,
            Breaks = metadata.Breaks,
            FileSize = size,
            LastModified = lastWrite,
            IsEnabled = enabled,
            ParseError = metadata.Error,
        };
    }

    /// <summary>图标写入缓存目录，按内容哈希去重；同一个图标只存一份。</summary>
    private string? TryCacheIcon(byte[] bytes)
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32];
            var directory = Path.Combine(_paths.CacheDirectory, "icons");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, hash + ".png");
            if (!File.Exists(target))
            {
                File.WriteAllBytes(target, bytes);
            }

            return target;
        }
        catch (Exception ex)
        {
            _log?.Warn("写入图标缓存失败", ex);
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    /// <summary>同名文件已存在时追加 (1)、(2)…… 而不是覆盖。</summary>
    private static string ResolveFreePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var sourceStream = File.OpenRead(source);
        await using var targetStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>条目里记录的元数据。启动器内部格式，界面只看 <see cref="ContentTrashEntry"/>。</summary>
internal sealed class TrashMetadata
{
    public string FileName { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public int Kind { get; set; }

    public DateTimeOffset DeletedAt { get; set; }

    public long SizeBytes { get; set; }

    public bool IsDirectory { get; set; }
}

/// <summary>
/// 内容回收站元数据的 Source Generator 上下文。
/// </summary>
[JsonSerializable(typeof(TrashMetadata))]
internal sealed partial class ContentJsonContext : JsonSerializerContext
{
}
