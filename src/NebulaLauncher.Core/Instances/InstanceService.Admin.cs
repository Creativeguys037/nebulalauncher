using System.Text.Json.Nodes;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例管理的编辑类操作：克隆、重命名、图标、目录清理。
///
/// 与 <c>InstanceService.cs</c> 拆开是为了让「读实例列表 / 落盘」与「批量文件操作」各自可读；
/// 两者共享同一份 <c>_instances</c> 缓存与私有 <c>Clone</c> 映射。
/// </summary>
public sealed partial class InstanceService
{
    /// <inheritdoc />
    /// <remarks>
    /// 实例目录就是版本目录，所以克隆不能「复制到同一目录」：它必须生成一个新版本目录
    /// （<c>&lt;原版本名&gt;-副本</c>），连版本 JSON/JAR 一起复制并改写 JSON 里的 id。
    /// 这与 PCL2 的「复制版本」是同一件事，好处是克隆出的实例在版本列表里也是一等公民。
    /// </remarks>
    public async Task<InstanceInfo?> CloneAsync(
        string instanceId,
        InstanceCloneOptions options,
        CancellationToken cancellationToken = default)
    {
        options ??= new InstanceCloneOptions();

        var source = FindInstanceCopy(instanceId);

        if (source is null)
        {
            _log.Warn($"克隆实例失败：找不到实例 {instanceId}");
            return null;
        }

        string sourceDirectory;

        try
        {
            sourceDirectory = GetGameDirectoryByVersion(source.VersionName);
        }
        catch (ArgumentException ex)
        {
            _log.Warn($"克隆实例失败：实例没有合法的版本目录 {instanceId}", ex);
            return null;
        }

        if (!Directory.Exists(sourceDirectory))
        {
            _log.Warn($"克隆实例失败：实例目录不存在 {sourceDirectory}");
            return null;
        }

        var newVersionName = MakeUniqueVersionName($"{source.VersionName}-副本");
        var newId = CreateUniqueId();
        var targetDirectory = GetGameDirectoryByVersion(newVersionName);

        // 默认名用「原名-副本」；同一实例克隆多份时靠 InstanceLocator 的序号后缀区分。
        var newName = string.IsNullOrWhiteSpace(options.NewName)
            ? InstanceLocator.MakeUniqueName($"{source.Name}-副本", Instances.Select(i => i.Name))
            : options.NewName.Trim();

        try
        {
            Directory.CreateDirectory(targetDirectory);

            await CopyVersionDirectoryAsync(
                    sourceDirectory,
                    targetDirectory,
                    source.VersionName,
                    newVersionName,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);

            var cloned = Clone(source);
            cloned.Id = newId;
            cloned.Name = newName;
            cloned.VersionName = newVersionName;
            cloned.CreatedAt = DateTimeOffset.UtcNow;

            // 游玩统计属于「这一次运行」，副本从零开始。
            cloned.LastPlayedAt = null;
            cloned.TotalPlayTimeSeconds = 0;

            await SaveAsync(cloned, cancellationToken).ConfigureAwait(false);

            _log.Info($"已克隆实例：{source.Name} → {newName} ({newId})，版本目录 {newVersionName}");

            return Clone(cloned);
        }
        catch (OperationCanceledException)
        {
            InstanceFileSystem.DeleteDirectoryQuietly(targetDirectory, _log);
            throw;
        }
        catch (Exception ex)
        {
            // 半成品目录必须清掉：留着会被下次 LoadAsync 当成「没有 instance.json 的垃圾目录」。
            _log.Error($"克隆实例失败：{sourceDirectory} → {targetDirectory}", ex);
            InstanceFileSystem.DeleteDirectoryQuietly(targetDirectory, _log);
            return null;
        }
    }

    /// <summary>
    /// 复制版本目录。玩家内容按 <see cref="InstanceCloneOptions"/> 过滤，
    /// 版本本体（JSON / JAR / natives / PCL / 任意未知目录）一律复制——少了它们新版本起不来。
    /// </summary>
    private static async Task CopyVersionDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        string sourceVersionName,
        string newVersionName,
        InstanceCloneOptions options,
        CancellationToken cancellationToken)
    {
        var switchable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["mods"] = options.IncludeMods,
            ["config"] = options.IncludeConfig,
            ["resourcepacks"] = options.IncludeResourcePacks,
            ["shaderpacks"] = options.IncludeShaderPacks,
            ["saves"] = options.IncludeSaves,
            // logs / crash-reports 是上一次的故障现场：复制到新实例会让用户把旧日志当成新问题。
            ["logs"] = options.IncludeLogs,
            ["crash-reports"] = options.IncludeLogs,
        };

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);

            if (switchable.TryGetValue(name, out var include) && !include)
            {
                continue;
            }

            await InstanceFileSystem
                .CopyDirectoryAsync(directory, Path.Combine(targetDirectory, name), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);

            // 实例描述与运行锁属于「这个实例」，不能随克隆复制；
            // 半截临时文件复制过去只会让下次原子写读成损坏。
            if (name.Equals("instance.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".nebula-run.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("meta.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 版本 JSON 与客户端 JAR 的文件名必须跟目录名一致，否则版本扫描认不出新版本。
            if (name.Equals(sourceVersionName + ".json", StringComparison.OrdinalIgnoreCase))
            {
                await CopyVersionJsonAsync(
                        file,
                        Path.Combine(targetDirectory, newVersionName + ".json"),
                        newVersionName,
                        cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            if (name.Equals(sourceVersionName + ".jar", StringComparison.OrdinalIgnoreCase))
            {
                await InstanceFileSystem
                    .CopyFileAsync(file, Path.Combine(targetDirectory, newVersionName + ".jar"), cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            await InstanceFileSystem
                .CopyFileAsync(file, Path.Combine(targetDirectory, name), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>复制版本 JSON 并把 <c>id</c> 改成新版本名；改不动时退回原样复制（启动仍可用）。</summary>
    private static async Task CopyVersionJsonAsync(
        string sourcePath,
        string targetPath,
        string newVersionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);

            if (JsonNode.Parse(text) is JsonObject json)
            {
                json["id"] = newVersionName;
                text = json.ToJsonString();
            }

            await File.WriteAllTextAsync(targetPath, text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // 复制失败只剩「JSON 写不进去」这一类原因：让调用方按原逻辑抛错回滚，
            // 这里的 catch 只负责把「改 id」这一步降级成原样复制。
            await InstanceFileSystem.CopyFileAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>生成不撞车的版本目录名：<c>&lt;名字&gt;</c>、<c>&lt;名字&gt; (2)</c>、…</summary>
    private string MakeUniqueVersionName(string baseName)
    {
        var safe = PathSafety.SanitizeDirectoryName(baseName, "instance");

        if (!Directory.Exists(Path.Combine(_paths.VersionsDirectory, safe)))
        {
            return safe;
        }

        for (var suffix = 2; suffix < 10000; suffix++)
        {
            var candidate = $"{safe} ({suffix})";

            if (!Directory.Exists(Path.Combine(_paths.VersionsDirectory, candidate)))
            {
                return candidate;
            }
        }

        return $"{safe}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <inheritdoc />
    public async Task RenameAsync(string instanceId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // 空白名会让卡片显示成「未命名实例」，多个这样的实例在列表里无法区分，直接拒绝。
            throw new ArgumentException("实例名称不能为空。", nameof(name));
        }

        var instance = FindInstanceCopy(instanceId);

        if (instance is null)
        {
            _log.Warn($"重命名失败：找不到实例 {instanceId}");
            return;
        }

        var previousName = instance.Name;
        var renamed = Clone(instance);

        // 目录名就是 Id，改显示名不碰路径——否则所有记录该实例 Id 的地方（settings.json 的
        // lastInstanceId、模组页的 target key）都会指向不存在的目录。
        renamed.Name = name.Trim();

        try
        {
            await SaveAsync(renamed, cancellationToken).ConfigureAwait(false);
            _log.Info($"实例已重命名：{previousName} → {renamed.Name} ({instanceId})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // SaveAsync 已写过日志；这里再记一条带「重命名」语义的，方便排查。
            _log.Error($"重命名实例失败：{instanceId}", ex);
        }
    }

    /// <inheritdoc />
    public async Task SetIconAsync(
        string instanceId,
        InstanceIconKind kind,
        string? iconPath,
        CancellationToken cancellationToken = default)
    {
        var instance = FindInstanceCopy(instanceId);

        if (instance is null)
        {
            _log.Warn($"设置图标失败：找不到实例 {instanceId}");
            return;
        }

        string? resolvedIconPath = null;

        if (kind == InstanceIconKind.Custom && !TryResolveIconFile(iconPath, out resolvedIconPath))
        {
            // 落盘一个指向不存在文件的 IconPath，下次进页面会显示成「有图标但画不出来」，
            // 比直接拒绝更难排查。
            _log.Warn($"设置图标被拒绝：自定义图标必须是已存在的 png/jpg 文件，实际为「{iconPath}」");
            return;
        }

        var updated = Clone(instance);
        updated.IconKind = kind;

        if (kind == InstanceIconKind.Custom)
        {
            updated.IconPath = resolvedIconPath;
        }

        try
        {
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _log.Info($"实例图标已更新：{instanceId} → {kind}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"保存实例图标失败：{instanceId}", ex);
        }
    }

    /// <inheritdoc />
    public Task<InstanceCleanupReport> CleanupAsync(
        string instanceId,
        InstanceCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        options ??= new InstanceCleanupOptions();

        var instance = FindInstanceCopy(instanceId);

        if (instance is null)
        {
            _log.Warn($"清理失败：找不到实例 {instanceId}");
            return Task.FromResult(InstanceCleanupReport.Empty);
        }

        string directory;

        try
        {
            directory = GetGameDirectory(instanceId);
        }
        catch (ArgumentException ex)
        {
            _log.Warn($"清理失败：实例 Id 非法 {instanceId}", ex);
            return Task.FromResult(InstanceCleanupReport.Empty);
        }

        if (!Directory.Exists(directory))
        {
            return Task.FromResult(InstanceCleanupReport.Empty);
        }

        // 目录名对应游戏自己的约定：logs / crash-reports / debug（F3 性能采样输出）/ screenshots。
        var targets = new List<string>(4);

        if (options.IncludeLogs)
        {
            targets.Add("logs");
        }

        if (options.IncludeCrashReports)
        {
            targets.Add("crash-reports");
        }

        if (options.IncludeDebugProfile)
        {
            targets.Add("debug");
        }

        if (options.IncludeScreenshots)
        {
            targets.Add("screenshots");
        }

        var cutoff = options.OlderThanDays > 0
            ? DateTime.UtcNow.AddDays(-options.OlderThanDays)
            : (DateTime?)null;

        var deleted = 0;
        long bytesFreed = 0;
        var failed = new List<string>();

        foreach (var relative in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.Combine(directory, relative);

            if (!Directory.Exists(target))
            {
                continue;
            }

            foreach (var file in InstanceFileSystem.EnumerateFiles(target))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(file);

                    if (!info.Exists)
                    {
                        continue;
                    }

                    // 按最后写入时间过滤：最近还在写的日志不该被当垃圾清掉。
                    if (cutoff is not null && info.LastWriteTimeUtc >= cutoff.Value)
                    {
                        continue;
                    }

                    var length = info.Length;
                    info.Delete();

                    deleted++;
                    bytesFreed += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 游戏正在写这个文件时删不掉；列进 Failed 让用户看到，而不是静默跳过。
                    failed.Add($"{relative}/{Path.GetRelativePath(target, file)}");
                    _log.Warn($"清理文件失败：{file}", ex);
                }
            }
        }

        var report = new InstanceCleanupReport(deleted, bytesFreed, failed);

        _log.Info($"已清理实例 {instance.Name}：删除 {deleted} 个文件，释放 {bytesFreed} 字节，失败 {failed.Count} 项。");

        return Task.FromResult(report);
    }

    /// <summary>自定义图标只能是本地 png/jpg：Core 不引图片解码库，路径交给 UI 去加载。</summary>
    private static bool TryResolveIconFile(string? iconPath, out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return false;
        }

        string full;

        try
        {
            full = Path.GetFullPath(iconPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!File.Exists(full))
        {
            return false;
        }

        var extension = Path.GetExtension(full);

        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolved = full;
        return true;
    }
}
