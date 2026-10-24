using System.Text.Json;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例回收站：<c>&lt;launcherDataRoot&gt;/trash/instances/&lt;trashId&gt;/</c>。
///
/// 为什么不用系统的「回收站」：Windows 的 Shell 回收站需要 Shell API，macOS 的废纸篓要靠
/// AppleScript/NSFileManager，要么违反单 TFM 约束，要么在两个平台上行为不一致。
/// 启动器自己维护一个目录，跨平台语义完全相同，且能保证「恢复」一定回到原来的实例 Id。
/// </summary>
public sealed partial class InstanceService
{
    private string TrashRoot => Path.Combine(_paths.RootDirectory, "trash", "instances");

    /// <inheritdoc />
    public async Task<InstanceTrashEntry?> MoveToTrashAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = FindInstanceCopy(instanceId);

        if (instance is null)
        {
            _log.Warn($"移入回收站失败：找不到实例 {instanceId}");
            return null;
        }

        string directory;

        try
        {
            directory = GetGameDirectoryByVersion(instance.VersionName);
        }
        catch (ArgumentException ex)
        {
            _log.Warn($"移入回收站失败：实例没有合法的版本目录 {instanceId}", ex);
            return null;
        }

        if (!Directory.Exists(directory))
        {
            _log.Warn($"移入回收站失败：实例目录不存在 {directory}");
            return null;
        }

        var deletedAt = DateTimeOffset.UtcNow;
        var trashId = MakeTrashId(instanceId, deletedAt);
        var trashPath = Path.Combine(TrashRoot, trashId);
        var sizeBytes = InstanceFileSystem.GetDirectorySize(directory);

        try
        {
            Directory.CreateDirectory(TrashRoot);

            // 先移动、后写 meta：反过来的话，移动失败会留下一个指向不存在目录的条目。
            var moved = await InstanceFileSystem
                .MoveDirectoryAsync(directory, trashPath, _log, cancellationToken)
                .ConfigureAwait(false);

            if (!moved)
            {
                return null;
            }

            await WriteTrashMetadataAsync(
                    trashPath,
                    new TrashMetadata
                    {
                        OriginalInstanceId = instanceId,
                        VersionName = instance.VersionName,
                        DisplayName = instance.Name,
                        DeletedAt = deletedAt,
                        SizeBytes = sizeBytes,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_sync)
            {
                _instances.RemoveAll(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));
            }

            _log.Info($"实例已移入回收站：{instance.Name} ({instanceId}) → {trashPath}");

            return new InstanceTrashEntry
            {
                Id = trashId,
                DisplayName = instance.Name,
                OriginalInstanceId = instanceId,
                TrashPath = trashPath,
                DeletedAt = deletedAt,
                SizeBytes = sizeBytes,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"移入回收站失败：{directory} → {trashPath}", ex);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InstanceTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<InstanceTrashEntry>();

        try
        {
            if (!Directory.Exists(TrashRoot))
            {
                return Task.FromResult<IReadOnlyList<InstanceTrashEntry>>(entries);
            }

            foreach (var directory in Directory.EnumerateDirectories(TrashRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trashId = Path.GetFileName(directory);
                var metadata = ReadTrashMetadata(directory);

                var displayName = metadata?.DisplayName;

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = ReadInstanceName(directory);
                }

                var originalId = metadata?.OriginalInstanceId;

                if (!InstanceFileSystem.IsSafeDirectoryName(originalId))
                {
                    originalId = ExtractOriginalInstanceId(trashId);
                }

                entries.Add(new InstanceTrashEntry
                {
                    Id = trashId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? trashId : displayName,
                    OriginalInstanceId = string.IsNullOrWhiteSpace(originalId) ? trashId : originalId,
                    TrashPath = directory,
                    DeletedAt = metadata?.DeletedAt ?? SafeLastWriteTime(directory),
                    // meta.json 缺失时才去递归统计：目录可能很大，能省一次遍历。
                    SizeBytes = metadata?.SizeBytes ?? InstanceFileSystem.GetDirectorySize(directory),
                });
            }

            entries.Sort((a, b) => b.DeletedAt.CompareTo(a.DeletedAt));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"读取回收站失败：{TrashRoot}", ex);
        }

        return Task.FromResult<IReadOnlyList<InstanceTrashEntry>>(entries);
    }

    /// <inheritdoc />
    public async Task<bool> RestoreFromTrashAsync(string trashId, CancellationToken cancellationToken = default)
    {
        if (!InstanceFileSystem.IsSafeDirectoryName(trashId))
        {
            _log.Warn($"恢复实例失败：非法的回收站条目 Id {trashId}");
            return false;
        }

        var trashPath = Path.Combine(TrashRoot, trashId);

        if (!Directory.Exists(trashPath))
        {
            _log.Warn($"恢复实例失败：回收站条目不存在 {trashPath}");
            return false;
        }

        var metadata = ReadTrashMetadata(trashPath);
        var originalId = metadata?.OriginalInstanceId;

        if (!InstanceFileSystem.IsSafeDirectoryName(originalId))
        {
            originalId = ExtractOriginalInstanceId(trashId);
        }

        if (!InstanceFileSystem.IsSafeDirectoryName(originalId))
        {
            _log.Warn($"恢复实例失败：无法确定原实例 Id {trashId}");
            return false;
        }

        // 恢复目标必须是版本目录。meta.json 里没有版本名（旧回收站条目）时，
        // 退到回收站目录里 instance.json 的 versionName，再退不到就用原 Id（旧布局仍可恢复）。
        var versionName = metadata?.VersionName;

        if (string.IsNullOrWhiteSpace(versionName))
        {
            versionName = ReadInstanceVersionName(trashPath);
        }

        if (string.IsNullOrWhiteSpace(versionName))
        {
            versionName = originalId;
        }

        string target;

        try
        {
            target = GetGameDirectoryByVersion(versionName);
        }
        catch (ArgumentException ex)
        {
            _log.Warn($"恢复实例失败：版本目录名非法 {versionName}", ex);
            return false;
        }

        // 目标目录还在 = 这个版本已经被新实例占用。覆盖会直接毁掉那个实例，只能拒绝。
        if (Directory.Exists(target))
        {
            _log.Warn($"恢复实例失败：目标目录已存在 {target}");
            return false;
        }

        try
        {
            var moved = await InstanceFileSystem
                .MoveDirectoryAsync(trashPath, target, _log, cancellationToken)
                .ConfigureAwait(false);

            if (!moved)
            {
                return false;
            }

            // meta.json 是回收站自己的元数据，恢复后留在实例目录里只会被当成未知文件。
            InstanceFileSystem.DeleteFileQuietly(Path.Combine(target, "meta.json"), _log);

            await LoadAsync(cancellationToken).ConfigureAwait(false);

            _log.Info($"已从回收站恢复实例：{originalId} ← {trashPath}");

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"恢复实例失败：{trashPath} → {target}", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public Task PurgeTrashAsync(string trashId, CancellationToken cancellationToken = default)
    {
        if (!InstanceFileSystem.IsSafeDirectoryName(trashId))
        {
            _log.Warn($"彻底删除失败：非法的回收站条目 Id {trashId}");
            return Task.CompletedTask;
        }

        var trashPath = Path.Combine(TrashRoot, trashId);

        try
        {
            if (Directory.Exists(trashPath))
            {
                Directory.Delete(trashPath, recursive: true);
                _log.Info($"回收站条目已彻底删除：{trashPath}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"彻底删除回收站条目失败：{trashPath}", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>回收站条目 Id：时间戳 + 原实例 Id。同一秒删两次时再加序号。</summary>
    private string MakeTrashId(string instanceId, DateTimeOffset deletedAt)
    {
        var baseId = $"{deletedAt:yyyyMMdd-HHmmss}-{instanceId}";

        if (!Directory.Exists(Path.Combine(TrashRoot, baseId)))
        {
            return baseId;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";

            if (!Directory.Exists(Path.Combine(TrashRoot, candidate)))
            {
                return candidate;
            }
        }

        // 序号被占满（现实中不会发生）时退回随机后缀，保证删除一定能进行。
        return $"{baseId}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>从 <c>yyyyMMdd-HHmmss-&lt;instanceId&gt;[-n]</c> 里取回原实例 Id。</summary>
    private static string? ExtractOriginalInstanceId(string trashId)
    {
        var parts = trashId.Split('-', StringSplitOptions.RemoveEmptyEntries);

        // 日期 8 位、时间 6 位、实例 Id 12 位十六进制；后两段是可选的冲突序号。
        return parts.Length >= 3 ? parts[2] : null;
    }

    /// <summary>meta.json 缺失时退到实例自己的 <c>instance.json</c> 读显示名。</summary>
    private string? ReadInstanceName(string trashPath)
    {
        return ReadInstanceJson(trashPath)?.Name is { Length: > 0 } name ? name : null;
    }

    /// <summary>回收站里的 <c>instance.json</c> 记录的原版本目录名。读不到返回 null。</summary>
    private string? ReadInstanceVersionName(string trashPath)
    {
        var versionName = ReadInstanceJson(trashPath)?.VersionName;
        return string.IsNullOrWhiteSpace(versionName) ? null : versionName;
    }

    private InstanceInfo? ReadInstanceJson(string trashPath)
    {
        var file = Path.Combine(trashPath, "instance.json");

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(file),
                JsonOptions.Of<InstanceInfo>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"读取回收站里的 instance.json 失败：{file}", ex);
            return null;
        }
    }

    private static DateTimeOffset SafeLastWriteTime(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private TrashMetadata? ReadTrashMetadata(string trashPath)
    {
        var file = Path.Combine(trashPath, "meta.json");

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(file),
                JsonOptions.Of<TrashMetadata>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 元数据坏了不该让整条记录从回收站消失：退回目录名与 instance.json 推断。
            _log.Warn($"读取回收站元数据失败：{file}", ex);
            return null;
        }
    }

    private static async Task WriteTrashMetadataAsync(
        string trashPath,
        TrashMetadata metadata,
        CancellationToken cancellationToken)
    {
        var file = Path.Combine(trashPath, "meta.json");
        var temp = file + ".tmp";

        var json = JsonSerializer.Serialize(metadata, JsonOptions.Of<TrashMetadata>());

        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
    }

    // TrashMetadata 提到文件末尾：Source Generator 需要能在同程序集内引用它。
}

/// <summary>回收站条目的元数据。启动器内部格式，UI 只看 <see cref="InstanceTrashEntry"/>。</summary>
internal sealed class TrashMetadata
{
    public string OriginalInstanceId { get; set; } = string.Empty;

    /// <summary>删除前的版本目录名。旧条目没有这个字段，恢复时退回读 instance.json。</summary>
    public string? VersionName { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset DeletedAt { get; set; }

    public long SizeBytes { get; set; }
}
