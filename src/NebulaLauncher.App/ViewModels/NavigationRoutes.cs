using System;
using System.Collections.Generic;
using System.Linq;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 导航项在侧栏里的位置分组。样张 `home-glass.html` 的 rail 是「主页/版本/内容/下载 → 分隔 → 账户/服务器 → 撑开 → 设置贴底」，
/// 分组不只是排版：分隔线两边是「玩游戏」与「配置账户与服务器」两类事，设置永远在栏底。
/// </summary>
public enum NavigationPlacement
{
    /// <summary>上半组：启动 / 版本 / 内容 / 下载。</summary>
    Main,

    /// <summary>分隔线之后的账户与服务器。</summary>
    Secondary,

    /// <summary>贴栏底的设置。</summary>
    Footer,
}

/// <summary>一级导航项：键、标题、图标资源键与所在分组。纯数据，外壳只负责逐项摆出来。</summary>
/// <param name="Key">导航键，取自 <see cref="NavigationKeys"/> 的一级常量。</param>
/// <param name="Title">侧栏显示的中文名。</param>
/// <param name="IconKey"><c>Themes/Icons.axaml</c> 里的资源键。</param>
/// <param name="Placement">在侧栏里的分组位置。</param>
public readonly record struct NavigationRoute(string Key, string Title, string IconKey, NavigationPlacement Placement);

/// <summary>
/// 一级导航与旧页面键的唯一映射表（票 #85）。
///
/// 为什么要一张表：
/// 1) 导航从 11 项收敛到 7 项后，旧键还会从深链、快捷键、拖放和页面内跳转进来；
///    如果每处各写一份 if，新增一个入口就会漏掉某条路径。
/// 2) 键、标题、图标与分组写在一处，测试才能把「图标键真的在 Icons.axaml 里」
///    与「7 项顺序与分组」钉死——拼错一个键只会让图标静默空白，编译期与截图都不一定看得出来。
///
/// 这里只描述路由，不碰页面实例；页面注册仍由 MainWindowViewModel 的 partial 文件负责。
/// </summary>
public static class NavigationRoutes
{
    /// <summary>
    /// 7 个一级入口。顺序与分组取自已定版样张 `.oil-ui/nebula-2/explore/home-glass.html` 的 rail：
    /// 启动 / 版本 / 内容 / 下载 → 分隔 → 账户 / 服务器 → 设置贴底。
    /// </summary>
    public static readonly IReadOnlyList<NavigationRoute> Items =
    [
        new(NavigationKeys.Home, "启动", "Icon.Home", NavigationPlacement.Main),
        new(NavigationKeys.Versions, "版本", "Icon.Versions", NavigationPlacement.Main),
        new(NavigationKeys.Content, "内容", "Icon.Content", NavigationPlacement.Main),
        new(NavigationKeys.Download, "下载", "Icon.Download", NavigationPlacement.Main),
        new(NavigationKeys.Accounts, "账户", "Icon.Account", NavigationPlacement.Secondary),
        new(NavigationKeys.Servers, "服务器", "Icon.Server", NavigationPlacement.Secondary),
        new(NavigationKeys.Settings, "设置", "Icon.Settings", NavigationPlacement.Footer),
    ];

    /// <summary>7 个一级键，顺序与 <see cref="Items"/> 一致。</summary>
    public static readonly IReadOnlyList<string> Primary = Items.Select(item => item.Key).ToArray();

    /// <summary>
    /// 旧键到一级键的落点。模组与资源库归内容页，
    /// 世界归版本页的实例详情，日志与帮助归设置页的关于区。
    ///
    /// **皮肤不在这张表里**（#92）：它有独立页面（来源 Tab + 网格 + 固定 300 预览列），
    /// 归一化到内容页会让 `Navigate("skins")` 落到模组页——那正是 D9 记的「不可达缺陷」。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyToPrimary = new(StringComparer.Ordinal)
    {
        [NavigationKeys.Mods] = NavigationKeys.Content,
        [NavigationKeys.Library] = NavigationKeys.Content,
        [NavigationKeys.Worlds] = NavigationKeys.Versions,
        [NavigationKeys.Logs] = NavigationKeys.Settings,
        [NavigationKeys.Help] = NavigationKeys.Settings,
    };

    /// <summary>
    /// 旧键落在一级页里的哪个分区。目前只覆盖日志 / 帮助 → 设置的关于区；
    /// 世界 → 版本实例详情与模组 / 资源 / 皮肤 → 内容页各自分类，分别由 #89、#90、#92 接管。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyToSection = new(StringComparer.Ordinal)
    {
        [NavigationKeys.Logs] = "about",
        [NavigationKeys.Help] = "about",
    };

    /// <summary>旧键返回对应的一级键；一级键与未知键原样返回。</summary>
    public static string Resolve(string key)
        => LegacyToPrimary.TryGetValue(key, out var primary) ? primary : key;

    /// <summary>该键是否是需要归一化的旧页面键。</summary>
    public static bool IsLegacy(string key) => LegacyToPrimary.ContainsKey(key);

    /// <summary>旧键落在一级页里的分区键；没有分区（或本身就是一级键）时返回 null。</summary>
    public static string? SectionFor(string key)
        => LegacyToSection.TryGetValue(key, out var section) ? section : null;

    /// <summary>取某一分组内的入口，保持表内顺序。</summary>
    public static IReadOnlyList<NavigationRoute> InGroup(NavigationPlacement placement)
        => Items.Where(item => item.Placement == placement).ToArray();
}
