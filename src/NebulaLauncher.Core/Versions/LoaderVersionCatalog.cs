using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Versions;

/// <summary>
/// 加载器版本候选的合并规则：上游列表 + 本机已安装的同加载器版本。
///
/// 为什么需要它：Forge 的官方只公开「推荐构建」（<c>VersionService.GetLoaderVersionsAsync</c>
/// 因此只返回一条），而用户可能已经装过别的构建。只给推荐版时，实例一旦换到新版就再也
/// 切不回旧构建——这正是 PCL2 组件修改器「还原」要解决的问题；Nebula 用「候选里包含已安装版本」覆盖。
///
/// 纯函数，不碰磁盘：调用方负责提供已安装列表（<see cref="IVersionService.GetInstalledAsync"/>）。
/// </summary>
public static class LoaderVersionCatalog
{
    /// <summary>
    /// 把已安装的同加载器版本并入远程候选。远程顺序保持在前；已安装版本按版本号降序追加在后。
    /// 已有同版本号（忽略大小写与首尾空白）时不重复添加，只把 <see cref="LoaderVersion.IsInstalled"/> 标记上。
    /// </summary>
    public static IReadOnlyList<LoaderVersion> MergeInstalled(
        IReadOnlyList<LoaderVersion> remote,
        IEnumerable<VersionInfo> installed,
        LoaderKind loader,
        string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(installed);

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return remote;
        }

        // 先按版本号建索引：同版本既可能在 remote 里也可能在 installed 里。
        var merged = new List<LoaderVersion>(remote.Count + 4);
        var indexByVersion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in remote)
        {
            if (string.IsNullOrWhiteSpace(item.Version))
            {
                continue;
            }

            indexByVersion[item.Version.Trim()] = merged.Count;
            merged.Add(item);
        }

        var installedVersions = installed
            .Where(info => info.Loader == loader
                           && string.Equals(info.MinecraftVersion?.Trim(), minecraftVersion.Trim(), StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(info.LoaderVersion))
            .Select(info => info.LoaderVersion!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // 必须按段数值排序：字典序会把 47.9.0 排在 47.10.0 前。
            .OrderByDescending(version => version, LoaderVersionComparer.Instance)
            .ToList();

        foreach (var version in installedVersions)
        {
            if (indexByVersion.TryGetValue(version, out var existing))
            {
                // 远程已有同版本：只补「已安装」标记，不动推荐/稳定等上游语义。
                merged[existing] = new LoaderVersion
                {
                    Version = merged[existing].Version,
                    Stable = merged[existing].Stable,
                    GameVersion = merged[existing].GameVersion,
                    IsRecommended = merged[existing].IsRecommended,
                    IsInstalled = true,
                };
                continue;
            }

            indexByVersion[version] = merged.Count;
            merged.Add(new LoaderVersion
            {
                Version = version,
                Stable = true,
                GameVersion = minecraftVersion.Trim(),
                IsInstalled = true,
            });
        }

        return merged;
    }
}
