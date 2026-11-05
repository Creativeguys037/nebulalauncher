using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaLauncher.ViewModels;

/// <summary>侧边栏一个导航项。图标只存资源键，几何本体在 <c>Themes/Icons.axaml</c>。</summary>
public sealed partial class NavigationItem : ObservableObject
{
    public NavigationItem(string key, string title, string iconKey)
    {
        Key = key;
        Title = title;
        IconKey = iconKey;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>图标资源键，例如 <c>Icon.Home</c>。</summary>
    public string IconKey { get; }

    /// <summary>当前是否被选中，由 MainWindowViewModel 维护。</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// 导航键常量。7 个一级键是 <see cref="NavigationRoutes.Primary"/> 的唯一来源；
/// 旧键保留下来供深链、快捷键与页面自身跳转使用，由 <see cref="NavigationRoutes.Resolve"/> 归一化。
/// </summary>
public static class NavigationKeys
{
    public const string Home = "home";
    public const string Versions = "versions";

    /// <summary>内容：模组 / 资源包 / 光影包 / 数据包 / 皮肤的合并入口。</summary>
    public const string Content = "content";

    public const string Download = "download";
    public const string Accounts = "accounts";
    public const string Servers = "servers";
    public const string Settings = "settings";

    // ---- 旧页面键：不再出现在一级导航，但深链与页面内跳转仍在使用 ----

    public const string Mods = "mods";

    /// <summary>在线资源库（Modrinth 搜索与安装）。</summary>
    public const string Library = "library";

    public const string Worlds = "worlds";

    /// <summary>皮肤页：网络皮肤库浏览与 3D 预览。</summary>
    public const string Skins = "skins";

    /// <summary>日志中心：启动器日志与游戏日志的查看入口。</summary>
    public const string Logs = "logs";

    /// <summary>内置离线帮助（不联网）。</summary>
    public const string Help = "help";

    /// <summary>控件示例页：不在侧边栏露出，只用于控件复核与截图（票 #84）。</summary>
    public const string Controls = "controls";
}
