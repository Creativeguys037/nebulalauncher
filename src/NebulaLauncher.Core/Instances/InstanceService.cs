using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例管理。一个实例 = <c>&lt;游戏目录&gt;/versions/&lt;版本名&gt;/</c> 目录 + 同目录下的 <c>instance.json</c>。
/// 目录本身就是版本目录，也是游戏运行目录，因此 mods / saves / config 天然隔离，
/// 且与 PCL2（<c>PathIndie = PathVersion</c>）和 HMCL（<c>getRunDirectory</c>）的版本隔离布局一致。
///
/// 三条由此推出的规则：
/// <list type="number">
/// <item><b>一个版本目录只能有一个实例</b>。同版本再次创建返回已有实例（幂等），不复制目录。</item>
/// <item><b>克隆必须生成新版本目录</b>（复制版本 JSON/JAR 并改写 id），否则会复制到自身。</item>
/// <item><b>删除 / 移入回收站作用于整个版本目录</b>，版本本体一起走；恢复后原样回来。</item>
/// </list>
///
/// 克隆 / 重命名 / 图标 / 清理在 <c>InstanceService.Admin.cs</c>，
/// 回收站四件套在 <c>InstanceService.Trash.cs</c>，共享这里的缓存与落盘逻辑。
/// </summary>
public sealed partial class InstanceService : IInstanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new JsonStringEnumConverter<LoaderKind>(),
            new JsonStringEnumConverter<InstanceIconKind>(),
            new JsonStringEnumConverter<GcKind>(),
        },
        TypeInfoResolver = new InstanceJsonContext(),
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog _log;
    private readonly IVersionService? _versions;
    private readonly IInstallService? _install;
    private readonly object _sync = new();
    private readonly List<InstanceInfo> _instances = new();

    /// <summary>
    /// 构造。<paramref name="versions"/> 与 <paramref name="install"/> 是「换加载器」才需要的：
    /// 前者用来从磁盘解析实例真实的 MC 版本与加载器（<see cref="InstanceInfo"/> 上的那两个字段
    /// 只是缓存，可能过期），后者负责真装。两者都可以缺省——其余实例操作不需要它们，
    /// 而 DI 容器里它们本来就已经注册（见 <c>AddCoreVersions</c> / <c>AddCoreInstalling</c>）。
    /// </summary>
    public InstanceService(
        ILauncherPaths paths,
        IAppLog log,
        IVersionService? versions = null,
        IInstallService? install = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _versions = versions;
        _install = install;
    }

    /// <inheritdoc />
    public IReadOnlyList<InstanceInfo> Instances
    {
        get
        {
            lock (_sync)
            {
                return _instances.Select(Clone).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 旧布局（<data>/instances/<id>）一次性迁移到版本目录。留在数据目录的话，
        // 用户会看到「游戏装在 D 盘，mods 却在 C 盘」——正是这次要修的那条链。
        await MigrateLegacyInstancesAsync(cancellationToken).ConfigureAwait(false);

        var root = _paths.VersionsDirectory;
        var loaded = new List<InstanceInfo>();

        try
        {
            if (!Directory.Exists(root))
            {
                ReplaceAll(loaded);
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = Path.Combine(directory, "instance.json");

                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    var instance = JsonSerializer.Deserialize(json, JsonOptions.Of<InstanceInfo>());

                    if (instance is null)
                    {
                        _log.Warn($"跳过无效的实例描述：{file}");
                        continue;
                    }

                    // 目录名是权威版本名：用户手改过 instance.json 的 versionName 时仍以磁盘目录为准，
                    // 否则实例会指向另一个版本目录，启动时 mods 与存档全部对不上。
                    instance.VersionName = Path.GetFileName(directory);

                    if (string.IsNullOrWhiteSpace(instance.Id))
                    {
                        instance.Id = instance.VersionName;
                    }

                    loaded.Add(instance);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"读取实例失败：{file}", ex);
                }
            }

            loaded.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            ReplaceAll(loaded);

            _log.Info($"已加载 {loaded.Count} 个实例：{root}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"扫描实例目录失败：{root}", ex);
            ReplaceAll(loaded);
        }
    }

    /// <inheritdoc />
    public async Task<InstanceInfo> CreateAsync(
        string name,
        string versionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            throw new ArgumentException("实例必须绑定一个版本目录。", nameof(versionName));
        }

        // 实例 = 版本目录，所以同一版本只能有一个实例。已有则直接返回它：
        // 「启动时按隔离策略自动建实例」每次启动都会走到这里，重复建目录会覆盖玩家的 mods 视图。
        var existing = _instances.FirstOrDefault(i =>
            string.Equals(i.VersionName, versionName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return Clone(existing);
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? versionName : name.Trim();
        var id = CreateUniqueId();

        var instance = new InstanceInfo
        {
            Id = id,
            Name = displayName,
            VersionName = versionName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var directory = GetGameDirectoryByVersion(versionName);
        Directory.CreateDirectory(directory);

        lock (_sync)
        {
            _instances.Insert(0, instance);
        }

        await SaveAsync(instance, cancellationToken).ConfigureAwait(false);

        _log.Info($"已创建实例：{displayName} ({id}) → {directory}");

        return Clone(instance);
    }

    /// <inheritdoc />
    public async Task SaveAsync(InstanceInfo instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(instance.Id))
        {
            throw new ArgumentException("实例 Id 不能为空。", nameof(instance));
        }

        // 用实例自己的 VersionName 拼目录，不依赖缓存：导入整合包等路径会先写文件后登记缓存。
        var directory = GetGameDirectoryByVersion(instance.VersionName);
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, "instance.json");
        var temp = file + ".tmp";

        var json = JsonSerializer.Serialize(instance, JsonOptions.Of<InstanceInfo>());

        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"保存实例失败：{file}", ex);
            throw;
        }

        lock (_sync)
        {
            var index = _instances.FindIndex(i => i.Id == instance.Id);

            if (index >= 0)
            {
                _instances[index] = Clone(instance);
            }
            else
            {
                _instances.Insert(0, Clone(instance));
            }
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return Task.CompletedTask;
        }

        var instance = FindInstanceCopy(instanceId);

        if (instance is null)
        {
            return Task.CompletedTask;
        }

        var directory = GetGameDirectoryByVersion(instance.VersionName);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _log.Info($"已删除实例版本目录：{directory}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"删除实例版本目录失败：{directory}", ex);
            return Task.CompletedTask;
        }

        lock (_sync)
        {
            _instances.RemoveAll(i => i.Id == instanceId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string GetGameDirectory(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("实例 Id 不能为空。", nameof(instanceId));
        }

        var instance = FindInstanceCopy(instanceId)
                       ?? throw new ArgumentException($"找不到实例：{instanceId}", nameof(instanceId));

        return GetGameDirectoryByVersion(instance.VersionName);
    }

    /// <summary>
    /// 版本名 → 实例目录（版本目录）的绝对路径。
    ///
    /// 版本名就是 <c>versions/</c> 下的目录名，必须挡住路径穿越：它来自 <c>instance.json</c>，
    /// 而那个文件可能被用户手改或从别的机器拷进来。
    /// </summary>
    public string GetGameDirectoryByVersion(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            throw new ArgumentException("实例没有绑定版本目录。", nameof(versionName));
        }

        if (versionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            versionName.Contains("..", StringComparison.Ordinal) ||
            versionName.Contains(Path.DirectorySeparatorChar) ||
            versionName.Contains(Path.AltDirectorySeparatorChar) ||
            string.Equals(versionName, ".", StringComparison.Ordinal))
        {
            throw new ArgumentException($"非法的版本目录名：{versionName}", nameof(versionName));
        }

        return Path.GetFullPath(Path.Combine(_paths.VersionsDirectory, versionName));
    }

    private string CreateUniqueId()
    {
        lock (_sync)
        {
            string id;

            do
            {
                id = Guid.NewGuid().ToString("N")[..12];
            }
            while (_instances.Any(i => string.Equals(i.Id, id, StringComparison.Ordinal)));

            return id;
        }
    }

    // ---------------- 旧布局迁移 ----------------

    /// <summary>
    /// 把 <c>&lt;data&gt;/instances/&lt;id&gt;/</c> 里的旧实例搬到 <c>versions/&lt;版本名&gt;/</c>。
    ///
    /// 三种情况都必须有出口（少一种就是「升级后实例消失」）：
    /// <list type="number">
    /// <item>目标版本目录不存在：整目录移动（同盘原子，跨盘复制后删除）。</item>
    /// <item>目标是已有版本目录但没有实例：把玩家内容合并进去（不覆盖已有文件），再写 instance.json。</item>
    /// <item>目标已被另一个实例占用：另建 <c>&lt;版本名&gt;-旧实例</c> 版本目录，复制版本本体 + 合并玩家内容。</item>
    /// </list>
    /// 迁移失败一律保留原目录并记 Warn，绝不删用户数据；下次启动会再试一次。
    /// </summary>
    private async Task MigrateLegacyInstancesAsync(CancellationToken cancellationToken)
    {
        var legacyRoot = _paths.InstancesDirectory;

        if (string.IsNullOrWhiteSpace(legacyRoot) || !Directory.Exists(legacyRoot))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(legacyRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = Path.Combine(directory, "instance.json");

                if (!File.Exists(file))
                {
                    continue;
                }

                InstanceInfo? legacy;

                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    legacy = JsonSerializer.Deserialize(json, JsonOptions.Of<InstanceInfo>());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _log.Warn($"读取旧布局实例失败，跳过迁移：{file}", ex);
                    continue;
                }

                if (legacy is null || !InstanceFileSystem.IsSafeDirectoryName(legacy.VersionName))
                {
                    _log.Warn($"旧布局实例缺少可用的版本名，跳过迁移：{file}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(legacy.Id))
                {
                    legacy.Id = Path.GetFileName(directory);
                }

                try
                {
                    await MigrateLegacyInstanceAsync(directory, legacy, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"迁移旧布局实例失败，保留原目录：{directory}", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"扫描旧布局实例目录失败：{legacyRoot}", ex);
        }
    }

    private async Task MigrateLegacyInstanceAsync(
        string legacyDirectory,
        InstanceInfo legacy,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(_paths.VersionsDirectory, legacy.VersionName);

        if (!Directory.Exists(target))
        {
            await InstanceFileSystem
                .MoveDirectoryAsync(legacyDirectory, target, _log, cancellationToken)
                .ConfigureAwait(false);

            _log.Info($"旧实例已迁移到版本目录：{legacyDirectory} → {target}");
            return;
        }

        if (!File.Exists(Path.Combine(target, "instance.json")))
        {
            // 目标只是一个普通版本目录（版本本体已装好）：把旧实例的玩家内容合并进去。
            await MergeLegacyContentAsync(legacyDirectory, target, cancellationToken).ConfigureAwait(false);

            legacy.VersionName = Path.GetFileName(target);
            await SaveAsync(legacy, cancellationToken).ConfigureAwait(false);
            InstanceFileSystem.DeleteDirectoryQuietly(legacyDirectory, _log);

            _log.Info($"旧实例内容已合并到版本目录：{legacyDirectory} → {target}");
            return;
        }

        // 目标版本已被另一个实例占用：给旧实例一个新版本目录，先复制版本本体再合并内容。
        var newVersionName = MakeUniqueVersionName($"{legacy.VersionName}-旧实例");
        var newDirectory = Path.Combine(_paths.VersionsDirectory, newVersionName);

        Directory.CreateDirectory(newDirectory);
        await CopyVersionPayloadAsync(target, legacy.VersionName, newDirectory, newVersionName, cancellationToken)
            .ConfigureAwait(false);
        await MergeLegacyContentAsync(legacyDirectory, newDirectory, cancellationToken).ConfigureAwait(false);

        legacy.VersionName = newVersionName;
        await SaveAsync(legacy, cancellationToken).ConfigureAwait(false);
        InstanceFileSystem.DeleteDirectoryQuietly(legacyDirectory, _log);

        _log.Info($"旧实例已迁移到新版本目录（原版本已有实例）：{legacyDirectory} → {newDirectory}");
    }

    /// <summary>
    /// 复制版本本体（JSON / JAR / natives / 安装器元数据）到新目录，不复制玩家内容。
    /// 改名规则与克隆一致：<c>&lt;旧版本&gt;.json/.jar</c> → <c>&lt;新版本&gt;.json/.jar</c>，JSON 的 id 同步改写。
    /// </summary>
    private static async Task CopyVersionPayloadAsync(
        string sourceDirectory,
        string sourceVersionName,
        string targetDirectory,
        string newVersionName,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var name = Path.GetFileName(directory);

            // 只带版本本体相关的目录；mods / saves / config 属于目标目录里那个实例，不能复制。
            if (!name.Equals("natives", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("PCL", StringComparison.OrdinalIgnoreCase))
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

            if (name.Equals("instance.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".nebula-run.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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

    /// <summary>把旧实例目录的玩家内容合并到目标目录。目标已有同名文件时保留目标那份。</summary>
    private static async Task MergeLegacyContentAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await MergeDirectoryAsync(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);

            if (name.Equals("instance.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("meta.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".nebula-run.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(targetDirectory, name);

            if (File.Exists(destination))
            {
                continue;
            }

            await InstanceFileSystem.CopyFileAsync(file, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task MergeDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            await MergeDirectoryAsync(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(targetDirectory, Path.GetFileName(file));

            if (File.Exists(destination))
            {
                continue;
            }

            await InstanceFileSystem.CopyFileAsync(file, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按 Id 取实例的**副本**。返回副本而不是缓存里的对象：调用方改完字段要走
    /// <see cref="SaveAsync"/> 回写，改动失败时缓存不该被污染。
    /// </summary>
    private InstanceInfo? FindInstanceCopy(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        lock (_sync)
        {
            var found = _instances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.Ordinal));
            return found is null ? null : Clone(found);
        }
    }

    private void ReplaceAll(List<InstanceInfo> instances)
    {
        lock (_sync)
        {
            _instances.Clear();
            _instances.AddRange(instances);
        }
    }

    private static InstanceInfo Clone(InstanceInfo source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        VersionName = source.VersionName,
        IconPath = source.IconPath,
        CreatedAt = source.CreatedAt,
        LastPlayedAt = source.LastPlayedAt,
        TotalPlayTimeSeconds = source.TotalPlayTimeSeconds,
        MaxMemoryMb = source.MaxMemoryMb,
        JavaPath = source.JavaPath,
        // 版本隔离的分组依据。漏掉这三个字段会让「按加载器分组」在保存一次之后全部退化成原版。
        Loader = source.Loader,
        MinecraftVersion = source.MinecraftVersion,

        // 加载器版本缓存。漏掉的现场是「instance.json 里写着 47.1.106，保存一次之后
        // Java 上限判定退回『无加载器级上限』」——对外暴露的实例对象都来自 Clone，
        // 缺这一行等于该字段从未被读过（VersionType 与 AuthServer* 都踩过同一个坑）。
        LoaderVersion = source.LoaderVersion,
        IsolationEnabled = source.IsolationEnabled,
        // 实例级启动覆盖。Clone 是手写映射，新字段漏一个就会被「保存一次」悄悄清空，
        // 与 Loader 那次的坑同源。
        JvmArguments = source.JvmArguments,
        GameArguments = source.GameArguments,
        LaunchBeforeCommand = source.LaunchBeforeCommand,
        LaunchAfterCommand = source.LaunchAfterCommand,
        WaitForLaunchCommands = source.WaitForLaunchCommands,
        WindowTitle = source.WindowTitle,
        Notes = source.Notes,

        // 版本信息：漏掉的现场是「实例里填了版本名，保存一次之后面板里的勾选又变回『跟随全局』」，
        // 因为对外暴露的实例对象都来自 Clone，缺这一行等于该字段从未被读过。
        VersionType = source.VersionType,
        IconKind = source.IconKind,
        GcKind = source.GcKind,
        ServerAddress = source.ServerAddress,

        // 实例级登录服务器：漏这三行等于「instance.json 里写着服主的验证服地址，面板永远显示空、
        // 启动也永远走账户的全局地址」——正是 VersionType 上次踩过的坑（见上面那段注释）。
        // AuthServerUrl 的三态（null / "" / 值）必须原样搬运，不能做任何归一化。
        AuthServerUrl = source.AuthServerUrl,
        AuthRegisterUrl = source.AuthRegisterUrl,
        AuthServerName = source.AuthServerName,
        ManagedPack = CloneManagedPack(source.ManagedPack),
    };

    private static ManagedPackInfo? CloneManagedPack(ManagedPackInfo? source) => source is null
        ? null
        : new ManagedPackInfo
        {
            Type = source.Type,
            ProjectId = source.ProjectId,
            Name = source.Name,
            VersionId = source.VersionId,
            SourceArchive = source.SourceArchive,
            ImportedAt = source.ImportedAt,
        };
}

/// <summary>
/// 实例文件的 Source Generator 上下文。裁剪发布下 instance.json 不能靠运行时反射读写，
/// 否则整仓所有 JSON 持久化在 portable 包里全线失效（见 #26）。
/// </summary>
[JsonSerializable(typeof(InstanceInfo))]
[JsonSerializable(typeof(TrashMetadata))]
internal sealed partial class InstanceJsonContext : JsonSerializerContext
{
}
