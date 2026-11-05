using System;
using System.Collections.Generic;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 一级导航与旧页面键的唯一映射表（票 #85）。
///
/// 为什么要一张表：导航从 11 项收敛到 7 项后，旧键还会从深链、快捷键、拖放和页面内跳转进来。
/// 如果每处各写一份 if，新增一个入口就会漏掉某条路径。这里只做「旧键 → 一级键」，
/// 不碰页面实例；页面注册仍由 MainWindowViewModel 的 partial 文件负责。
/// </summary>
public static class NavigationRoutes
{
    /// <summary>7 个一级入口，顺序固定：启动 / 版本 / 内容 / 下载 / 账户 / 服务器 / 设置。</summary>
    public static readonly IReadOnlyList<string> Primary =
    [
        NavigationKeys.Home,
        NavigationKeys.Versions,
        NavigationKeys.Content,
        NavigationKeys.Download,
        NavigationKeys.Accounts,
        NavigationKeys.Servers,
        NavigationKeys.Settings,
    ];

    /// <summary>
    /// 旧键到一级键的落点。内容三键（模组 / 资源 / 皮肤）归内容页，
    /// 世界归版本页的实例详情，日志与帮助归设置页的关于区。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyToPrimary = new(StringComparer.Ordinal)
    {
        [NavigationKeys.Mods] = NavigationKeys.Content,
        [NavigationKeys.Library] = NavigationKeys.Content,
        [NavigationKeys.Skins] = NavigationKeys.Content,
        [NavigationKeys.Worlds] = NavigationKeys.Versions,
        [NavigationKeys.Logs] = NavigationKeys.Settings,
        [NavigationKeys.Help] = NavigationKeys.Settings,
    };

    /// <summary>旧键返回对应的一级键；一级键与未知键原样返回。</summary>
    public static string Resolve(string key)
        => LegacyToPrimary.TryGetValue(key, out var primary) ? primary : key;

    /// <summary>该键是否是需要归一化的旧页面键。</summary>
    public static bool IsLegacy(string key) => LegacyToPrimary.ContainsKey(key);
}
