using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// <see cref="IInstanceContext"/> 的默认实现。
///
/// 三条设计约束：
///
/// 1. <b>绝不抛异常给 UI</b>。实例被删、版本被删、settings.json 损坏都要退化成「全局目录」，
///    页面在异常状态下仍要能渲染出可用的空列表，而不是整页报错。
/// 2. <b>目录永远非空</b>。没有实例时 <see cref="GameDirectory"/> 是全局 <c>.minecraft</c>，
///    调用方无需再写 null 判断——这正是旧代码各页各自退回全局目录时漏掉的那层。
/// 3. <b>版本信息以磁盘为准</b>。实例里记录的 <see cref="InstanceInfo.Loader"/> 只是缓存；
///    用户把版本重装成 Forge 后，分组必须跟着变，否则会把 Forge 模组装进 Fabric 的分组里。
/// </summary>
public sealed class InstanceContext : IInstanceContext
{
    /// <summary>全局目标的固定 Key。与实例 Id 不会冲突：实例 Id 是 12 位十六进制。</summary>
    public const string GlobalTargetKey = "global";

    private readonly IInstanceService _instances;
    private readonly IVersionService? _versions;
    private readonly ILauncherPaths _paths;
    private readonly ISettingsService? _settings;
    private readonly IAppLog? _log;

    private readonly object _sync = new();

    private InstanceInfo? _current;
    private string? _versionName;
    private LoaderKind _loader = LoaderKind.Vanilla;
    private string? _minecraftVersion;

    /// <summary>上一次通知 Change 时的状态快照，用于把 <see cref="SelectAsync"/> 做成幂等。</summary>
    private string? _stateKey;

    /// <summary>settings.json 是否已尝试读过一次（无论成功失败）。</summary>
    private bool _settingsLoaded;

    public InstanceContext(
        IInstanceService instances,
        ILauncherPaths paths,
        IVersionService? versions = null,
        ISettingsService? settings = null,
        IAppLog? log = null)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _versions = versions;
        _settings = settings;
        _log = log;
    }

    /// <inheritdoc />
    public InstanceInfo? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public string GameDirectory
    {
        get
        {
            lock (_sync)
            {
                if (_current is null)
                {
                    return _paths.MinecraftDirectory;
                }

                return ResolveInstanceDirectory(_current);
            }
        }
    }

    /// <inheritdoc />
    public string? VersionName
    {
        get
        {
            lock (_sync)
            {
                return _versionName;
            }
        }
    }

    /// <inheritdoc />
    public LoaderKind Loader
    {
        get
        {
            lock (_sync)
            {
                return _loader;
            }
        }
    }

    /// <inheritdoc />
    public string? MinecraftVersion
    {
        get
        {
            lock (_sync)
            {
                return _minecraftVersion;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 页面在自己 OnNavigatedToAsync 里就会调这里，而主窗口的设置加载是 fire-and-forget 的：
        // 设置读得慢时 LastInstanceId 还是 null，页面会停在「全局」上，用户看着像隔离没生效。
        // 所以首次调用时自己确保设置已从磁盘读过一次；之后再调就是纯内存操作。
        await EnsureSettingsLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _instances.LoadAsync(cancellationToken).ConfigureAwait(false);

        var preferred = _settings?.Current.LastInstanceId;

        if (string.IsNullOrWhiteSpace(preferred))
        {
            await SelectAsync(null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SelectAsync(preferred, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只读一次 settings.json。失败不能让页面挂掉，退化为默认设置继续跑。</summary>
    private async Task EnsureSettingsLoadedAsync(CancellationToken cancellationToken)
    {
        if (_settingsLoaded || _settings is null)
        {
            return;
        }

        _settingsLoaded = true;

        try
        {
            await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _settingsLoaded = false;
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("读取设置失败，实例上下文按默认值工作", ex);
        }
    }

    /// <inheritdoc />
    public async Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default)
    {
        InstanceInfo? picked = null;

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            picked = _instances.Instances.FirstOrDefault(i =>
                string.Equals(i.Id, instanceId, StringComparison.Ordinal));

            if (picked is null)
            {
                _log?.Warn($"实例不存在，回退到全局目录：{instanceId}");
            }
        }

        string? versionName;
        string? minecraftVersion;
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        if (picked is null)
        {
            // 全局目录没有「自己的版本」。这里仍解析 lastSelectedVersion，因为它描述的是
            // 「下一次从全局入口启动会用哪个版本」，Java 上限与兼容性判断要用；
            // 但 ListTargetsAsync 不再把它当作全局目标的身份（见该方法的注释）。
            versionName = _settings?.Current.LastSelectedVersion;
            (minecraftVersion, loader, loaderVersion) = await ResolveVersionAsync(versionName, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            versionName = picked.VersionName;
            (minecraftVersion, loader, loaderVersion) = await ResolveVersionAsync(versionName, cancellationToken)
                .ConfigureAwait(false);

            // 版本目录读不到（被删了 / 还没装完）时退回实例自己记录的元数据，
            // 至少还能把「这个实例属于哪个加载器」显示出来。
            if (loader == LoaderKind.Vanilla && picked.Loader != LoaderKind.Vanilla)
            {
                loader = picked.Loader;
                loaderVersion ??= TryReadLoaderVersion(picked.VersionName);
            }

            minecraftVersion ??= picked.MinecraftVersion;

            // 顺手把磁盘上的真实值写回实例，避免 instance.json 长期落后于实际安装。
            // 加载器版本只在解析到真值时才回写：读不到（目录被删、版本名不规范）返回 null，
            // 拿它盖掉 instance.json 里已有的值等于把「上次解析成功的结果」也一起丢了，
            // 下次启动的 Java 上限判定会退回「无加载器级上限」。
            var loaderVersionChanged = !string.IsNullOrWhiteSpace(loaderVersion)
                                       && !string.Equals(picked.LoaderVersion, loaderVersion, StringComparison.Ordinal);

            if (picked.Loader != loader || picked.MinecraftVersion != minecraftVersion || loaderVersionChanged)
            {
                picked.Loader = loader;
                picked.MinecraftVersion = minecraftVersion;

                if (loaderVersionChanged)
                {
                    picked.LoaderVersion = loaderVersion;
                }

                try
                {
                    await _instances.SaveAsync(picked, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 回写失败只影响下次启动的分组展示，不能阻断本次切换。
                    _log?.Warn($"回写实例加载器信息失败：{picked.Id}", ex);
                }
            }
        }

        lock (_sync)
        {
            _current = picked;
            _versionName = versionName;
            _loader = loader;
            _minecraftVersion = minecraftVersion;
        }

        await PersistSelectionAsync(picked?.Id, cancellationToken).ConfigureAwait(false);

        // 幂等：页面每次进入都会调 LoadAsync → SelectAsync，若状态没变就不通知。
        // 否则「切换目标 → Changed → 重建页面 → LoadAsync」会变成无限重建。
        var key = $"{picked?.Id}|{versionName}|{loader}|{minecraftVersion}";

        lock (_sync)
        {
            if (string.Equals(_stateKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _stateKey = key;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        var targets = new List<ContentTarget>();

        // 全局目录不是「某个版本的原版」：它没有绑定版本，也不参与加载器分组。
        // 旧实现把 lastSelectedVersion 塞进 VersionName / Loader，界面据此把它显示成
        // 「1.21 · 原版」并收起模组入口——用户也就无法管理全局 .minecraft/mods。
        targets.Add(new ContentTarget
        {
            Key = GlobalTargetKey,
            Kind = ContentTargetKind.Global,
            DisplayName = "全局目录 (.minecraft)",
            GameDirectory = _paths.MinecraftDirectory,
            VersionName = null,
            Loader = LoaderKind.Vanilla,
            LoaderVersion = null,
            MinecraftVersion = null,
            IsVersionInstalled = true,
        });

        foreach (var instance in _instances.Instances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (mcVersion, loader, loaderVersion) =
                await ResolveVersionAsync(instance.VersionName, cancellationToken).ConfigureAwait(false);

            if (loader == LoaderKind.Vanilla && instance.Loader != LoaderKind.Vanilla)
            {
                loader = instance.Loader;
            }

            mcVersion ??= instance.MinecraftVersion;

            targets.Add(new ContentTarget
            {
                Key = instance.Id,
                Kind = ContentTargetKind.Instance,
                DisplayName = instance.Name,
                GameDirectory = ResolveInstanceDirectory(instance),
                InstanceId = instance.Id,
                VersionName = string.IsNullOrWhiteSpace(instance.VersionName) ? null : instance.VersionName,
                Loader = loader,
                LoaderVersion = loaderVersion,
                MinecraftVersion = mcVersion,
                IsVersionInstalled = !string.IsNullOrWhiteSpace(instance.VersionName)
                                     && File.Exists(VersionJsonPath(instance.VersionName)),
                IsolationEnabled = instance.IsolationEnabled,
            });
        }

        return targets;
    }

    /// <inheritdoc />
    public async Task<ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var targets = await ListTargetsAsync(cancellationToken).ConfigureAwait(false);

        return targets.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<InstanceResolution> ResolveLaunchTargetAsync(
        string versionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            await SelectAsync(null, cancellationToken).ConfigureAwait(false);

            return new InstanceResolution(
                _paths.MinecraftDirectory,
                Instance: null,
                Isolated: false,
                Created: false);
        }

        // 1) 已经有绑定这个版本的实例：直接用它。用户手动建的实例优先级最高。
        var existing = InstanceLocator.FindForVersion(_instances.Instances, versionName, Current?.Id);

        if (existing is not null)
        {
            await SelectAsync(existing.Id, cancellationToken).ConfigureAwait(false);

            // SelectAsync 刚把磁盘上解析到的加载器版本回写进实例，所以这里读到的是最新的。
            return new InstanceResolution(
                ResolveInstanceDirectory(existing),
                existing,
                Isolated: true,
                Created: false,
                existing.LoaderVersion);
        }

        // 2) 没有实例：看策略要不要自动建一个。
        var (minecraftVersion, loader, resolvedLoaderVersion) = await ResolveVersionAsync(versionName, cancellationToken)
            .ConfigureAwait(false);

        var policy = IsolationPolicyResolver.Parse(_settings?.Current.IsolationPolicy);

        // 版本目录里已经有 mods/saves 时恒隔离（PCL2 ModMinecraft.vb:252-256）：
        // 那些内容本来就在版本目录里，退到全局目录等于让它们突然读不到。
        if (!IsolationPolicyResolver.ShouldIsolate(policy, loader, VersionDirectoryHasContent(versionName)))
        {
            await SelectAsync(null, cancellationToken).ConfigureAwait(false);

            return new InstanceResolution(
                _paths.MinecraftDirectory,
                Instance: null,
                Isolated: false,
                Created: false,
                resolvedLoaderVersion);
        }

        // 3) 自动建实例。失败退回全局目录：因为一次目录创建失败而启动不了游戏，比不隔离更糟。
        try
        {
            // 实例目录就是版本目录（versions/<版本名>），所以显示名直接用版本名；
            // CreateAsync 对「同版本已有实例」是幂等的，不会重建目录。
            var created = await _instances.CreateAsync(versionName, versionName, cancellationToken)
                .ConfigureAwait(false);

            created.Loader = loader;
            created.MinecraftVersion = minecraftVersion;
            created.LoaderVersion = string.IsNullOrWhiteSpace(resolvedLoaderVersion) ? null : resolvedLoaderVersion;
            created.IsolationEnabled = true;
            await _instances.SaveAsync(created, cancellationToken).ConfigureAwait(false);

            _log?.Info($"按隔离策略（{policy}）为版本 {versionName} 自动创建实例：{created.Id}");

            await SelectAsync(created.Id, cancellationToken).ConfigureAwait(false);

            return new InstanceResolution(
                ResolveInstanceDirectory(created),
                created,
                Isolated: true,
                Created: true,
                created.LoaderVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"自动创建实例失败，本次改用全局目录：{versionName}", ex);

            await SelectAsync(null, cancellationToken).ConfigureAwait(false);

            return new InstanceResolution(_paths.MinecraftDirectory, null, Isolated: false, Created: false);
        }
    }

    // ---------------- 内部 ----------------
    private string ResolveInstanceDirectory(InstanceInfo instance)
    {
        try
        {
            return _instances.GetGameDirectory(instance.Id);
        }
        catch (Exception ex)
        {
            // GetGameDirectory 会对非法 Id 抛异常；实例目录名被外部改坏时退化为全局目录。
            _log?.Warn($"解析实例目录失败：{instance.Id}", ex);
            return _paths.MinecraftDirectory;
        }
    }

    /// <summary>版本目录下是否已经有玩家内容（<c>mods/</c> 或 <c>saves/</c>）。读不到按「没有」处理。</summary>
    private bool VersionDirectoryHasContent(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return false;
        }

        try
        {
            var directory = Path.Combine(_paths.VersionsDirectory, versionName);
            return Directory.Exists(Path.Combine(directory, "mods"))
                   || Directory.Exists(Path.Combine(directory, "saves"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<(string? MinecraftVersion, LoaderKind Loader, string? LoaderVersion)> ResolveVersionAsync(
        string? versionName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(versionName) || _versions is null)
        {
            return (null, LoaderKind.Vanilla, null);
        }

        try
        {
            var info = await _versions.GetVersionAsync(versionName, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                return (null, LoaderKind.Vanilla, TryReadLoaderVersion(versionName));
            }

            return (info.MinecraftVersion, info.Loader, info.LoaderVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取版本 {versionName} 的加载器信息失败", ex);
            return (null, LoaderKind.Vanilla, TryReadLoaderVersion(versionName));
        }
    }

    /// <summary>
    /// 版本目录不存在时，仍从名字里认出加载器（<c>fabric-loader-0.16.9-1.20.1</c> 这类命名）。
    /// 只用于分组展示，不作为启动依据。
    /// </summary>
    private static string? TryReadLoaderVersion(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        var parts = versionName.Split('-');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("loader", StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(parts[i + 1], out var parsed))
            {
                return parsed.ToString();
            }
        }

        return null;
    }

    private string VersionJsonPath(string versionName) =>
        Path.Combine(_paths.VersionsDirectory, versionName, versionName + ".json");

    /// <summary>
    /// 把选择写回 settings.json。失败只记日志：选择是「偏好」而不是「数据」，
    /// 写不进去时本次会话仍然生效，下次启动退回全局目录。
    /// </summary>
    private async Task PersistSelectionAsync(string? instanceId, CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return;
        }

        if (string.Equals(_settings.Current.LastInstanceId, instanceId, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Current.LastInstanceId = instanceId;

        try
        {
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("保存当前实例选择失败", ex);
        }
    }
}
