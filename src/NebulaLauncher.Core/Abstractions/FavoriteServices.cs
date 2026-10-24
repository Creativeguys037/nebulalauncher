namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 版本的收藏与隐藏。
///
/// 为什么独立落盘（<c>&lt;data&gt;/favorites.json</c>）而不是塞进 <c>settings.json</c>：
/// 这里的数据是「按版本名」的高频开关，而且删掉版本后条目要保留（重装回来还是收藏），
/// 与设置文件「用户偏好」的语义、写入频率都不同；混在一起还会让设置保存互相覆盖。
/// </summary>
public interface IVersionFavorites
{
    /// <summary>
    /// 收藏的版本名，按收藏时间倒序（最新收藏的在前）。
    /// 已卸载版本的条目仍在这里——删除再装回来应该还是收藏。
    /// </summary>
    IReadOnlyList<string> Favorites { get; }

    /// <summary>版本是否被收藏。未加载时返回 false。</summary>
    bool IsFavorite(string versionName);

    /// <summary>切换收藏状态。收藏时记录当前时间用于排序，取消收藏时移除条目。</summary>
    Task ToggleFavoriteAsync(string versionName, CancellationToken cancellationToken = default);

    /// <summary>版本是否被隐藏。未加载时返回 false。</summary>
    bool IsHidden(string versionName);

    /// <summary>设置隐藏状态。隐藏不删文件，只是默认不在版本列表里显示。</summary>
    Task SetHiddenAsync(string versionName, bool hidden, CancellationToken cancellationToken = default);

    /// <summary>读取 favorites.json。文件不存在或损坏时静默重建为空列表，不抛。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 收藏或隐藏状态变化后触发。
    /// 可能从后台线程触发（写盘在 <c>ConfigureAwait(false)</c> 的续体里），订阅方需自行切回 UI 线程。
    /// </summary>
    event EventHandler? Changed;
}
