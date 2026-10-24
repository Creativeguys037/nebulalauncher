using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaLauncher.ViewModels;

/// <summary>侧边栏一个导航项。图标用 SVG path data（24×24 视口），跨平台无需图标字体。</summary>
public sealed partial class NavigationItem : ObservableObject
{
    public NavigationItem(string key, string title, string iconData, bool isFooter = false)
    {
        Key = key;
        Title = title;
        IconData = iconData;
        IsFooter = isFooter;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>24×24 坐标系下的 path 数据。</summary>
    public string IconData { get; }

    /// <summary>是否放在侧边栏底部（设置、关于）。</summary>
    public bool IsFooter { get; }

    /// <summary>当前是否被选中，由 MainWindowViewModel 维护。</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>内置导航项的键与图标常量，UI 与 ViewModel 共用，避免字符串拼写漂移。</summary>
public static class NavigationKeys
{
    public const string Home = "home";
    public const string Versions = "versions";
    public const string Download = "download";
    public const string Mods = "mods";

    /// <summary>在线资源库（Modrinth 搜索与安装）。</summary>
    public const string Library = "library";

    public const string Accounts = "accounts";
    public const string Servers = "servers";
    public const string Worlds = "worlds";

    /// <summary>日志中心：启动器日志与游戏日志的查看入口。</summary>
    public const string Logs = "logs";

    /// <summary>内置离线帮助（不联网）。</summary>
    public const string Help = "help";

    public const string Settings = "settings";

    /// <summary>Material Symbols 风格的 24×24 描边图标。</summary>
    public const string IconHome =
        "M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z";

    public const string IconVersions =
        "M12 2.5 21 7v10l-9 4.5L3 17V7zM12 12l9-5M12 12v9.5M12 12 3 7";

    public const string IconDownload =
        "M12 3v11m0 0 4.5-4.5M12 14l-4.5-4.5M4 17v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3";

    public const string IconMods =
        "M4 8.5 12 4l8 4.5v7L12 20l-8-4.5zM4 8.5 12 13l8-4.5M12 13v7";

    /// <summary>书架：三层书脊，用于「资源」。</summary>
    public const string IconLibrary =
        "M4 4v16M8.5 4v16M13 4l3.6 15.4M18.5 5.4 21 4.4";

    public const string IconAccounts =
        "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM4.5 21a7.5 7.5 0 0 1 15 0";

    public const string IconServers =
        "M4 6h16v5H4zM4 13h16v5H4zM7.5 8.5h.01M7.5 15.5h.01";

    /// <summary>地球 + 经纬线，用于「世界与备份」。</summary>
    public const string IconWorlds =
        "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM3.2 9h17.6M3.2 15h17.6M12 3a14 14 0 0 0 0 18M12 3a14 14 0 0 1 0 18";

    /// <summary>文档 + 折角 + 三行文字，用于「日志」。</summary>
    public const string IconLogs =
        "M6 3h9l4 4v14a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zM15 3v4h4M8.5 8.5h2.5M8.5 12h7M8.5 15.5h7";

    /// <summary>圆圈 + 问号，用于「帮助」。</summary>
    public const string IconHelp =
        "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM9.6 9.6a2.5 2.5 0 1 1 3.6 2.3c-.8.4-1.2 1-1.2 1.9M12 17h.01";

    public const string IconSettings =
        "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-1.8-.3 1.6 1.6 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.6 1.6 0 0 0-1-1.5 1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0 .3-1.8 1.6 1.6 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.6 1.6 0 0 0 1.5-1 1.6 1.6 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 1.8.3H9a1.6 1.6 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.6 1.6 0 0 0 1 1.5 1.6 1.6 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8V9a1.6 1.6 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.6 1.6 0 0 0-1.5 1Z";
}
