using System.IO.Compression;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Backups;

/// <summary>
/// 世界备份与还原。备份文件同时落一个 <c>.meta.json</c>，
/// 这样列表页不需要打开 zip 就能拿到世界名、备注与创建时间。
/// </summary>
public sealed class BackupService : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public BackupService(ILauncherPaths paths, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
    }

    public Task<IReadOnlyList<WorldInfo>> ListWorldsAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Task.FromResult<IReadOnlyList<WorldInfo>>(Array.Empty<WorldInfo>());
        }

        var savesDirectory = Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(savesDirectory))
        {
            return Task.FromResult<IReadOnlyList<WorldInfo>>(Array.Empty<WorldInfo>());
        }

        return Task.Run<IReadOnlyList<WorldInfo>>(() =>
        {
            var worlds = new List<WorldInfo>();
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(savesDirectory);
            }
            catch (Exception ex)
            {
                _log?.Warn($"枚举世界目录失败：{savesDirectory}", ex);
                return worlds;
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new DirectoryInfo(directory);
                    var levelDatPath = Path.Combine(directory, "level.dat");
                    var summary = NbtReader.TryReadLevelDat(levelDatPath);
                    worlds.Add(new WorldInfo
                    {
                        Name = FirstNonEmpty(summary?.LevelName, info.Name),
                        Directory = info.FullName,
                        SizeBytes = CalculateDirectorySize(directory),
                        LastWriteTime = info.LastWriteTimeUtc,
                        VersionName = summary?.VersionName,
                        GameMode = summary?.GameMode,
                        GameType = summary?.GameType,
                        Seed = summary?.Seed,
                        Difficulty = summary?.Difficulty,
                        AllowCommands = summary?.AllowCommands,
                        KeepInventory = summary?.KeepInventory,
                        Hardcore = summary?.Hardcore,

                        // 只探「标签在不在」：这里解码 base64 与 PNG 会把世界页从毫秒级拖到秒级。
                        // 因此 level.dat 在这条路径上被解析两次——几 KB~100KB 的解析远便宜于解码，
                        // 而 NbtReader 的 LevelSummary 是写回路径也在用的摘要，不往它上面加字段换一次解析。
                        HasIcon = summary is not null && WorldIconService.HasIconTag(levelDatPath),
                    });
                }
                catch (Exception ex)
                {
                    _log?.Warn($"读取世界信息失败：{directory}", ex);
                }
            }

            return worlds
                .OrderByDescending(world => world.LastWriteTime)
                .ToList();
        }, cancellationToken);
    }

    public Task<BackupEntry> CreateAsync(
        string worldDirectory,
        string? note = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(worldDirectory, false, note, progress, cancellationToken);

    public Task<BackupEntry> CreateAutomaticAsync(
        string worldDirectory,
        string? note = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(worldDirectory, true, note, progress, cancellationToken);

    private async Task<BackupEntry> CreateCoreAsync(
        string worldDirectory,
        bool isAutomatic,
        string? note,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worldDirectory) || !Directory.Exists(worldDirectory))
        {
            throw new DirectoryNotFoundException($"世界目录不存在：{worldDirectory}");
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldDirectory));
        var worldName = Path.GetFileName(normalized);
        var timestamp = DateTimeOffset.Now;
        var fileName = $"{SanitizeFileName(worldName)}-{timestamp:yyyyMMdd-HHmmss}.zip";

        Directory.CreateDirectory(_paths.BackupsDirectory);

        // 临时文件不预先占用最终名：最终名在 Move 那一刻才算数。
        // 以前「先查 File.Exists 再写」在并发下会让两个实例选中同一个名字，
        // 后完成的那个 Move 失败（AUDIT-2026-10-23 第四节 P2）。
        var stagingName = $".{SanitizeFileName(worldName)}-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.partial";
        var temporaryPath = Path.Combine(_paths.BackupsDirectory, stagingName);

        var files = Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories).ToList();
        var totalBytes = files.Sum(SafeLength);
        long written = 0;

        string archivePath;
        try
        {
            await Task.Run(async () =>
            {
                using var archiveStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);
                var buffer = new byte[81920];

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var relative = Path.GetRelativePath(normalized, file).Replace('\\', '/');
                    var entry = archive.CreateEntry($"{worldName}/{relative}", CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTimeUtc(file);
                    using var target = entry.Open();
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        if (totalBytes > 0)
                        {
                            progress?.Report(Math.Clamp((double)written / totalBytes, 0, 1));
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // 不用 overwrite 的 Move 是原子占名：并发实例里只有一个能拿到这个名字，
            // 其余抛 IOException 后换下一个序号，不会覆盖彼此的备份。
            archivePath = MoveIntoFreePath(temporaryPath, _paths.BackupsDirectory, fileName);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }

        var entry = new BackupEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            FilePath = archivePath,
            WorldName = worldName,
            CreatedAt = timestamp,
            SizeBytes = SafeLength(archivePath),
            Note = note ?? string.Empty,
            IsAutomatic = isAutomatic,
            SourceWorldDirectory = normalized,
        };

        try
        {
            if (!await WriteMetaAsync(entry, cancellationToken).ConfigureAwait(false))
            {
                // 归档与 meta 必须同时成立：没有 meta 的压缩包会被当成「手工备份」，
                // 自动轮转就永远清不掉它（AUDIT-2026-10-23 第四节 P2）。
                // 回滚这次备份，让调用方看到失败，而不是留一个假装成功的孤儿 zip。
                TryDeleteFile(archivePath);
                throw new IOException($"备份已生成但元信息写入失败，已回滚：{archivePath}");
            }
        }
        catch (OperationCanceledException)
        {
            // 取消恰好落在「归档已改名、meta 未写成」之间：同样要删归档再重抛，
            // 否则会留下没有 meta 的 zip（REVIEW-R34-R36 P3-5）。
            TryDeleteFile(archivePath);
            throw;
        }

        progress?.Report(1);
        _log?.Info($"已备份世界 {worldName} → {archivePath}");
        return entry;
    }

    public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var directory = _paths.BackupsDirectory;
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<BackupEntry>>(Array.Empty<BackupEntry>());
        }

        return Task.Run<IReadOnlyList<BackupEntry>>(() =>
        {
            var result = new List<BackupEntry>();
            foreach (var archive in Directory.EnumerateFiles(directory, "*.zip"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var meta = ReadMeta(archive);
                if (meta is not null)
                {
                    result.Add(meta);
                    continue;
                }

                // 没有 meta（手工放进来的备份）也要能列出来。
                var info = new FileInfo(archive);
                result.Add(new BackupEntry
                {
                    Id = Path.GetFileNameWithoutExtension(archive),
                    FilePath = archive,
                    WorldName = GuessWorldName(archive),
                    CreatedAt = info.LastWriteTimeUtc,
                    SizeBytes = info.Length,
                });
            }

            return result.OrderByDescending(entry => entry.CreatedAt).ToList();
        }, cancellationToken);
    }

    public async Task RestoreAsync(
        BackupEntry entry,
        string targetGameDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!File.Exists(entry.FilePath))
        {
            throw new FileNotFoundException($"备份文件不存在：{entry.FilePath}", entry.FilePath);
        }

        var savesDirectory = Path.Combine(targetGameDirectory, "saves");
        Directory.CreateDirectory(savesDirectory);

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(entry.FilePath);
            var total = archive.Entries.Count;
            var done = 0;

            foreach (var zipEntry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0);

                if (string.IsNullOrEmpty(zipEntry.Name))
                {
                    // 目录条目
                    continue;
                }

                var destination = ResolveSafeDestination(savesDirectory, zipEntry.FullName);
                if (destination is null)
                {
                    _log?.Warn($"跳过非法路径的压缩包条目：{zipEntry.FullName}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                zipEntry.ExtractToFile(destination, overwrite: true);
            }
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report(1);
        _log?.Info($"已还原备份 {entry.WorldName} → {savesDirectory}");
    }

    public Task DeleteAsync(BackupEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(entry.FilePath))
            {
                File.Delete(entry.FilePath);
            }

            var metaPath = entry.FilePath + ".meta.json";
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"删除备份失败：{entry.FilePath}", ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(int keepPerWorld, CancellationToken cancellationToken = default)
    {
        if (keepPerWorld <= 0)
        {
            // 0 或负数当「不轮转」处理：删光用户的备份是不可逆伤害，
            // 而「保留 0 份」不是一个有意义的配置。
            return 0;
        }

        var all = await ListBackupsAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var group in all
                     .Where(entry => entry.IsAutomatic && !string.IsNullOrWhiteSpace(entry.SourceWorldDirectory))
                     .GroupBy(entry => entry.SourceWorldDirectory!, pathComparer))
        {
            foreach (var entry in group.OrderByDescending(item => item.CreatedAt).Skip(keepPerWorld))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(entry.FilePath))
                {
                    deleted++;
                }
            }
        }

        if (deleted > 0)
        {
            _log?.Info($"备份轮转：每个世界保留 {keepPerWorld} 份，已删除 {deleted} 份旧备份");
        }

        return deleted;
    }

    /// <inheritdoc />
    public Task<WorldEditResult> UpdateWorldAsync(
        string worldDirectory,
        WorldEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(worldDirectory))
        {
            return Task.FromResult(WorldEditResult.Fail("世界目录为空，无法编辑。"));
        }

        return Task.Run(() => UpdateWorldCore(worldDirectory, request, cancellationToken), cancellationToken);
    }

    // ---------------- 内部实现 ----------------

    /// <summary>写入前备份的文件名，固定在存档目录里，方便玩家手动回滚。</summary>
    private const string LevelDatBackupSuffix = ".nebula.bak";

    /// <summary>
    /// 存档编辑的实际流程（顺序不能改）：
    /// 占用检测 → 解析整棵树 → 只改点名字段 → <c>.bak</c> 备份 → 写临时文件 → 替换。
    ///
    /// 任何一步失败都返回失败结果而不是抛异常：UI 需要的是可展示的中文原因。
    /// 尤其「备份失败」必须直接中止——没有备份的写入等于把玩家的存档押在一次 IO 上。
    /// </summary>
    private WorldEditResult UpdateWorldCore(
        string worldDirectory,
        WorldEditRequest request,
        CancellationToken cancellationToken)
    {
        string directory;
        try
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldDirectory));
        }
        catch (Exception ex)
        {
            return WorldEditResult.Fail($"世界目录不合法：{ex.Message}");
        }

        if (!Directory.Exists(directory))
        {
            return WorldEditResult.Fail($"世界目录不存在：{directory}");
        }

        var levelDatPath = Path.Combine(directory, "level.dat");
        if (!File.Exists(levelDatPath))
        {
            return WorldEditResult.Fail("这个存档没有 level.dat，无法编辑。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 正在运行的世界：游戏持有 session.lock。此刻写 level.dat 会被游戏退出时回写覆盖，
        // 或者与游戏的元数据写入交错，属于不可恢复的数据损失。
        if (IsWorldInUse(directory))
        {
            return WorldEditResult.Fail("世界正在被游戏占用（session.lock 被锁定）。请先退出游戏再修改存档。");
        }

        NbtDocument document;
        try
        {
            document = NbtReader.ReadDocument(levelDatPath);
        }
        catch (Exception ex)
        {
            _log?.Warn($"解析 level.dat 失败，已放弃写入：{levelDatPath}", ex);
            return WorldEditResult.Fail($"level.dat 解析失败，已放弃写入：{ex.Message}");
        }

        var data = document.Data;
        if (data is null)
        {
            return WorldEditResult.Fail("level.dat 缺少 Data 段，不是可编辑的存档。");
        }

        ApplyWorldEdits(data, request);

        var backupPath = levelDatPath + LevelDatBackupSuffix;
        try
        {
            // 每次都覆盖：一份「最近一次修改前」的备份比一堆按时间堆积的副本更好找。
            File.Copy(levelDatPath, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Error($"写入前备份 level.dat 失败，已放弃修改：{levelDatPath}", ex);
            return WorldEditResult.Fail($"写入前备份失败，已放弃修改：{ex.Message}");
        }

        // 先写临时文件再替换：中途断电最多留下一个 .tmp，原文件不受影响。
        var tempPath = levelDatPath + ".nebula.tmp";
        try
        {
            NbtWriter.WriteFile(tempPath, document);
            File.Move(tempPath, levelDatPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);
            _log?.Error($"写回 level.dat 失败：{levelDatPath}", ex);
            return WorldEditResult.Fail($"写回 level.dat 失败（原文件已备份为 level.dat.nebula.bak）：{ex.Message}");
        }

        _log?.Info($"已更新存档 {Path.GetFileName(directory)} 的 level.dat（备份：{backupPath}）");
        return WorldEditResult.Ok(backupPath);
    }

    /// <summary>
    /// 只改请求里点名的字段。其余标签（含模组写进去的未知标签）留在树里不动，
    /// 写回时按原类型与原顺序重放。
    /// </summary>
    private static void ApplyWorldEdits(NbtCompound data, WorldEditRequest request)
    {
        if (request.LevelName is { } levelName && levelName.Trim().Length > 0)
        {
            data.Set("LevelName", NbtTag.String(levelName));
        }

        if (request.Seed is { } seed)
        {
            // NBT 规范里 RandomSeed 是 TAG_Long，可以负数，也可以超过 int 范围。
            data.Set("RandomSeed", NbtTag.Long(seed));
        }

        if (request.GameMode is { } gameMode)
        {
            data.Set("GameType", NbtTag.Int(Math.Clamp(gameMode, 0, 3)));
        }

        if (request.Difficulty is { } difficulty)
        {
            data.Set("Difficulty", NbtTag.Byte((byte)Math.Clamp(difficulty, 0, 3)));
        }

        if (request.AllowCommands is { } allowCommands)
        {
            data.Set("allowCommands", NbtTag.Byte(allowCommands ? (byte)1 : (byte)0));
        }

        if (request.Hardcore is { } hardcore)
        {
            data.Set("hardcore", NbtTag.Byte(hardcore ? (byte)1 : (byte)0));
        }

        if (request.KeepInventory is { } keepInventory)
        {
            // GameRules 里每条规则都是 TAG_String（"true"/"false"），不是 TAG_Byte。
            var rules = data.GetCompound("GameRules");
            if (rules is null)
            {
                rules = new NbtCompound();
                data.Set("GameRules", NbtTag.Compound(rules));
            }

            rules.Set("keepInventory", NbtTag.String(keepInventory ? "true" : "false"));
        }
    }

    /// <summary>
    /// 世界是否正被占用。先试 <see cref="FileShare.None"/> 独占打开，再对首字节加锁：
    /// 前者能查到「别人持有共享写句柄」，后者能查到 Java 侧 <c>FileChannel.lock()</c> 的整文件区块锁。
    /// </summary>
    private static bool IsWorldInUse(string worldDirectory)
    {
        var lockPath = Path.Combine(worldDirectory, "session.lock");
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                stream.Lock(0, 1);
                stream.Unlock(0, 1);
            }
            catch (PlatformNotSupportedException)
            {
                // 非 Windows 分支：独占打开已经足够判定。
            }

            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件删不掉不影响结论：原文件完好，备份也在。
        }
    }

    private async Task<bool> WriteMetaAsync(BackupEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(entry.FilePath + ".meta.json", json, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"写入备份元信息失败：{entry.FilePath}", ex);
            return false;
        }
    }

    private BackupEntry? ReadMeta(string archivePath)
    {
        var metaPath = archivePath + ".meta.json";
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(metaPath);
            var entry = JsonSerializer.Deserialize<BackupEntry>(json, JsonOptions);
            if (entry is null)
            {
                return null;
            }

            // meta 里的路径可能是移动过的旧路径，以实际文件为准。
            return new BackupEntry
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? Path.GetFileNameWithoutExtension(archivePath) : entry.Id,
                FilePath = archivePath,
                WorldName = entry.WorldName,
                CreatedAt = entry.CreatedAt,
                SizeBytes = SafeLength(archivePath),
                Note = entry.Note,
                IsAutomatic = entry.IsAutomatic,
                SourceWorldDirectory = entry.SourceWorldDirectory,
            };
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取备份元信息失败：{metaPath}", ex);
            return null;
        }
    }

    private static string GuessWorldName(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        var separator = name.LastIndexOf('-');
        if (separator > 0 && name.Length - separator >= 16)
        {
            return name[..separator];
        }

        return name;
    }

    /// <summary>防止 zip-slip：解析出的路径必须仍在目标目录内。</summary>
    private static string? ResolveSafeDestination(string root, string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? destination : null;
    }

    /// <summary>
    /// 把临时归档原子地搬到一个空闲的最终名上。并发安全的关键是 `File.Move` 不带 overwrite：
    /// 目标已存在时系统调用失败（只有一个实例能成功），失败者换下一个序号。
    ///
    /// 旧实现是「先 File.Exists 查再写」，两个实例同一秒备份同名世界时会选中同一个路径，
    /// 后完成者的 Move 直接抛异常（AUDIT-2026-10-23 第四节 P2）。
    /// </summary>
    private static string MoveIntoFreePath(string temporaryPath, string directory, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var index = 1; index <= 1000; index++)
        {
            var candidate = index == 1
                ? Path.Combine(directory, fileName)
                : Path.Combine(directory, $"{name}-{index}{extension}");

            try
            {
                File.Move(temporaryPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // 这个名字被另一个实例抢占了：换下一个序号。
            }
        }

        var fallback = Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
        File.Move(temporaryPath, fallback, overwrite: false);
        return fallback;
    }

    private static long CalculateDirectorySize(string directory)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                total += SafeLength(file);
            }
        }
        catch (Exception)
        {
            return total;
        }

        return total;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string SanitizeFileName(string name) =>
        NebulaLauncher.Core.Paths.PathSafety.SanitizeFileName(name, "world");

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
