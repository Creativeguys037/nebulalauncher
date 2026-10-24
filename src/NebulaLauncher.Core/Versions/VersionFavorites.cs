using System.Text.Json;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Versions;

/// <summary>一条收藏记录。时间用于「收藏组内最新收藏在最前」的排序。</summary>
public sealed class FavoriteEntry
{
    /// <summary>版本目录名（<c>versions/</c> 下的目录名）。</summary>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>收藏时间。取消后再收藏会刷新成新时间，因此会排到最前。</summary>
    public DateTimeOffset PinnedAtUtc { get; set; }
}

/// <summary>一条隐藏记录。</summary>
public sealed class HiddenEntry
{
    /// <summary>版本目录名。</summary>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>隐藏时间。仅用于排查，不参与 UI 排序。</summary>
    public DateTimeOffset HiddenAtUtc { get; set; }
}

/// <summary>favorites.json 的文件结构。</summary>
internal sealed class FavoritesDocument
{
    public List<FavoriteEntry> Favorites { get; set; } = [];

    public List<HiddenEntry> Hidden { get; set; } = [];
}

/// <summary>
/// 版本列表的展示顺序：收藏的版本置顶，收藏组内按 <paramref name="favorites"/> 的顺序
/// （即收藏时间倒序），其余版本保持传入顺序。
///
/// 抽成 Core 里的纯函数是为了能在单测里钉住排序：分组本身是 UI 关注点，但「收藏优先」的
/// 顺序规则一旦写错，用户会觉得置顶功能时灵时不灵，而截图又验证不了顺序的稳定性。
/// </summary>
public static class VersionFavoriteOrdering
{
    /// <summary>
    /// 重排 <paramref name="items"/>。没有收藏、或只有一项时原样返回。
    /// 用 LINQ 的稳定排序而不是 <see cref="List{T}.Sort"/>：后者不稳定，同组内的相对顺序会乱跳。
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(
        IReadOnlyList<T> items,
        Func<T, string> nameOf,
        IReadOnlyList<string> favorites)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(favorites);

        if (items.Count < 2)
        {
            return items;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < favorites.Count; i++)
        {
            rank[favorites[i]] = i;
        }

        return items
            .OrderBy(item => rank.ContainsKey(nameOf(item)) ? 0 : 1)
            .ThenBy(item => rank.TryGetValue(nameOf(item), out var index) ? index : int.MaxValue)
            .ToList();
    }
}

/// <summary>
/// 版本收藏 / 隐藏的本地持久化。
///
/// 设计要点：
/// 1. 独立文件 <c>&lt;data&gt;/favorites.json</c>，与 servers.json 同级；写法同样是「先写 .tmp 再 move 覆盖」，
///    避免进程在写一半时被杀导致文件变成半截 JSON。
/// 2. 文件不存在、损坏都按空列表处理并重建，不让「收藏数据坏了」阻断版本页。
/// 3. 删除版本不会清理这里的条目：重新安装同名版本后收藏自动恢复。UI 只对存在的版本显示星标。
/// </summary>
public sealed class VersionFavorites : IVersionFavorites, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<FavoriteEntry> _favorites = [];
    private readonly List<HiddenEntry> _hidden = [];

    private volatile bool _loaded;

    public VersionFavorites(ILauncherPaths paths, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
    }

    /// <summary>收藏文件的完整路径。数据根取 <see cref="ILauncherPaths.RootDirectory"/>。</summary>
    public string FilePath => Path.Combine(_paths.RootDirectory, "favorites.json");

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<string> Favorites
    {
        get
        {
            lock (_sync)
            {
                return _favorites
                    .OrderByDescending(entry => entry.PinnedAtUtc)
                    .Select(entry => entry.VersionName)
                    .ToList();
            }
        }
    }

    /// <inheritdoc />
    public bool IsFavorite(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return false;
        }

        lock (_sync)
        {
            return _favorites.Any(entry => SameName(entry.VersionName, versionName));
        }
    }

    /// <inheritdoc />
    public bool IsHidden(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return false;
        }

        lock (_sync)
        {
            return _hidden.Any(entry => SameName(entry.VersionName, versionName));
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loaded = true;
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ToggleFavoriteAsync(string versionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return;
        }

        var name = versionName.Trim();

        if (!_loaded)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                var existing = _favorites.FirstOrDefault(entry => SameName(entry.VersionName, name));

                if (existing is null)
                {
                    _favorites.Add(new FavoriteEntry
                    {
                        VersionName = name,
                        PinnedAtUtc = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    _favorites.Remove(existing);
                }
            }

            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task SetHiddenAsync(string versionName, bool hidden, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return;
        }

        var name = versionName.Trim();

        if (!_loaded)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var changed = false;

        try
        {
            lock (_sync)
            {
                var existing = _hidden.FirstOrDefault(entry => SameName(entry.VersionName, name));

                if (hidden && existing is null)
                {
                    _hidden.Add(new HiddenEntry
                    {
                        VersionName = name,
                        HiddenAtUtc = DateTimeOffset.UtcNow,
                    });
                    changed = true;
                }
                else if (!hidden && existing is not null)
                {
                    _hidden.Remove(existing);
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        // 已经是目标状态时不算「变化」：重复点隐藏不该产生多余的通知与写盘。
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // ---------------- 内部实现 ----------------

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;

        if (!File.Exists(path))
        {
            Reset();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<FavoritesDocument>(json, JsonOptions);

            if (document is null)
            {
                Reset();
                return;
            }

            int favoriteCount;
            int hiddenCount;

            lock (_sync)
            {
                _favorites.Clear();
                _hidden.Clear();

                foreach (var entry in document.Favorites ?? [])
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.VersionName))
                    {
                        continue;
                    }

                    entry.VersionName = entry.VersionName.Trim();

                    // 手工改坏文件时可能出现重复条目：保留第一条，避免 UI 里同一个版本出现两次。
                    if (_favorites.Any(existing => SameName(existing.VersionName, entry.VersionName)))
                    {
                        continue;
                    }

                    _favorites.Add(entry);
                }

                foreach (var entry in document.Hidden ?? [])
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.VersionName))
                    {
                        continue;
                    }

                    entry.VersionName = entry.VersionName.Trim();

                    if (_hidden.Any(existing => SameName(existing.VersionName, entry.VersionName)))
                    {
                        continue;
                    }

                    _hidden.Add(entry);
                }

                favoriteCount = _favorites.Count;
                hiddenCount = _hidden.Count;
            }

            _log?.Info($"已加载版本收藏：{path}（{favoriteCount} 个收藏 / {hiddenCount} 个隐藏）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 坏文件按空列表重建，下次写盘会覆盖它；绝不让收藏数据阻断版本页。
            _log?.Warn($"版本收藏文件损坏，已按空列表重建：{path}", ex);
            Reset();
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            FavoritesDocument snapshot;
            lock (_sync)
            {
                snapshot = new FavoritesDocument
                {
                    Favorites = _favorites.ToList(),
                    Hidden = _hidden.ToList(),
                };
            }

            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 内存状态已经变了：UI 先反映用户操作，落盘失败只记日志，不把异常抛给页面。
            _log?.Warn($"保存版本收藏失败：{FilePath}", ex);
        }
    }

    private void Reset()
    {
        lock (_sync)
        {
            _favorites.Clear();
            _hidden.Clear();
        }
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
