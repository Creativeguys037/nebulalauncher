using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NebulaLauncher.ViewModels;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 外壳（票 #85）的静态契约：7 个一级入口的顺序、旧键落点、外壳不写设计字面量、
/// 以及侧栏不再出现皮肤卡 / 星云背景。测试只读文本与纯 VM，不触碰 Avalonia 类型。
/// </summary>
public class AppShell_NavigationContractTests
{
    private const string MainWindowPath = "src/NebulaLauncher.App/Views/MainWindow.axaml";
    private const string MainWindowCodePath = "src/NebulaLauncher.App/Views/MainWindow.axaml.cs";
    private const string PageHostPath = "src/NebulaLauncher.App/Controls/PageHost.cs";
    private const string IconsPath = "src/NebulaLauncher.App/Themes/Icons.axaml";
    private const string ControlsPath = "src/NebulaLauncher.App/Themes/Controls.axaml";

    private static readonly string[] ExpectedPrimary =
    [
        NavigationKeys.Home,
        NavigationKeys.Versions,
        NavigationKeys.Content,
        NavigationKeys.Download,
        NavigationKeys.Accounts,
        NavigationKeys.Servers,
        NavigationKeys.Settings,
    ];

    [Fact]
    public void Primary_IsSevenEntriesInFixedOrder()
    {
        Assert.Equal(ExpectedPrimary, NavigationRoutes.Primary);
        Assert.Equal(7, NavigationRoutes.Primary.Count);
    }

    [Fact]
    public void LegacyKeys_ResolveToTheirMergedEntry()
    {
        Assert.Equal(NavigationKeys.Content, NavigationRoutes.Resolve(NavigationKeys.Mods));

        // Library / Worlds 已独立注册为页面（#100），不再归一化到父页。
        Assert.Equal(NavigationKeys.Library, NavigationRoutes.Resolve(NavigationKeys.Library));
        Assert.Equal(NavigationKeys.Skins, NavigationRoutes.Resolve(NavigationKeys.Skins));
        Assert.Equal(NavigationKeys.Worlds, NavigationRoutes.Resolve(NavigationKeys.Worlds));

        Assert.Equal(NavigationKeys.Settings, NavigationRoutes.Resolve(NavigationKeys.Logs));
        Assert.Equal(NavigationKeys.Settings, NavigationRoutes.Resolve(NavigationKeys.Help));
    }

    [Fact]
    public void EveryLegacyKey_HasAUniquePrimaryTarget()
    {
        var legacy = new[]
        {
            NavigationKeys.Mods, NavigationKeys.Logs, NavigationKeys.Help,
        };

        Assert.All(legacy, key =>
        {
            Assert.True(NavigationRoutes.IsLegacy(key), $"{key} 没有登记为旧键");
            Assert.Contains(NavigationRoutes.Resolve(key), ExpectedPrimary);
        });

        // Library / Worlds / Skins 已独立注册，不再是旧键。
        Assert.All(
            new[] { NavigationKeys.Library, NavigationKeys.Worlds, NavigationKeys.Skins },
            key => Assert.False(NavigationRoutes.IsLegacy(key), $"{key} 不应是旧键"));

        // 一级键本身不再被当成旧键，否则 Navigate 会多绕一跳。
        Assert.All(ExpectedPrimary, key => Assert.False(NavigationRoutes.IsLegacy(key)));
    }

    [Fact]
    public void Navigation_IsGroupedLikeTheApprovedSample()
    {
        // 形态取自已定版样张 home-glass.html 的 rail：
        // 启动/版本/内容/下载 → 分隔 → 账户/服务器 → 撑开 → 设置贴栏底。
        var main = NavigationRoutes.InGroup(NavigationPlacement.Main).Select(r => r.Key).ToArray();
        var secondary = NavigationRoutes.InGroup(NavigationPlacement.Secondary).Select(r => r.Key).ToArray();
        var footer = NavigationRoutes.InGroup(NavigationPlacement.Footer).Select(r => r.Key).ToArray();

        Assert.Equal(
            [NavigationKeys.Home, NavigationKeys.Versions, NavigationKeys.Content, NavigationKeys.Download],
            main);
        Assert.Equal([NavigationKeys.Accounts, NavigationKeys.Servers], secondary);
        Assert.Equal([NavigationKeys.Settings], footer);
    }

    [Fact]
    public void MainWindow_MirrorsTheSampleRailShape()
    {
        // 样张的竖栏是浮动玻璃底板（Border.glass），不是整高实心侧栏；
        // 导航项图标在上、文字在下；设置由独立的贴底容器承载。
        var window = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));

        Assert.Contains("Classes=\"glass\"", window);
        Assert.Contains("ItemsSource=\"{Binding NavigationItems}\"", window);
        Assert.Contains("ItemsSource=\"{Binding SecondaryNavigationItems}\"", window);
        Assert.Contains("ItemsSource=\"{Binding FooterNavigationItems}\"", window);
        Assert.Contains("Classes=\"nav-label\"", window);

        // 竖栏宽度两档来自样张 glass.css 的 .rail：82 / 68。
        Assert.Contains("ColumnDefinitions=\"82,*\"", window);

        var code = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowCodePath));
        Assert.Contains("compact ? 68 : 82", code);
    }

    [Fact]
    public void EveryNavigationIconKey_ExistsInIconDictionary()
    {
        // 图标键拼错不会编译失败，只会在界面上静默空白；这里把它变成硬失败。
        var doc = XDocument.Load(Path.Combine(RepoRoot(), IconsPath));
        var declared = doc.Descendants()
            .Select(e => (string?)e.Attribute(XamlNamespace + "Key"))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        var missing = NavigationRoutes.Items
            .Where(route => !declared.Contains(route.IconKey))
            .Select(route => $"{route.Key} → {route.IconKey}")
            .ToList();

        Assert.True(missing.Count == 0,
            "这些一级入口的图标键在 Icons.axaml 里没有定义：" + string.Join(", ", missing));
    }

    [Fact]
    public void EveryPrimaryRoute_HasATitleAndUniqueKey()
    {
        var keys = NavigationRoutes.Items.Select(route => route.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(NavigationRoutes.Items, route =>
            Assert.False(string.IsNullOrWhiteSpace(route.Title), $"{route.Key} 没有中文标题"));
    }

    [Fact]
    public void LogsAndHelp_LandOnSettingsAboutSection()
    {
        // 「旧键要有唯一落点」不只是落到一级页：日志与帮助要落在设置的关于区。
        Assert.Equal("about", NavigationRoutes.SectionFor(NavigationKeys.Logs));
        Assert.Equal("about", NavigationRoutes.SectionFor(NavigationKeys.Help));

        // 其它键不指定分区，避免把设置页固定在某个分区里。
        Assert.Null(NavigationRoutes.SectionFor(NavigationKeys.Mods));
        Assert.Null(NavigationRoutes.SectionFor(NavigationKeys.Settings));
    }

    [Fact]
    public void MainWindow_UsesSevenItemNavigationAndNoSidebarSkinCard()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));

        Assert.Contains("ItemsSource=\"{Binding NavigationItems}\"", text);
        Assert.DoesNotContain("SkinModelView", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AuroraBackground", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationKeys.Icon", text, StringComparison.Ordinal);

        // 7 个入口由 VM 的 NavigationItems 提供，XAML 里不硬编码按钮列表。
        Assert.DoesNotContain("NavigationKeys.Mods", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationKeys.Worlds", text, StringComparison.Ordinal);

        Assert.Contains("AutomationProperties.Name=\"主导航\"", text);
        Assert.Contains("AutomationProperties.AutomationId=\"Navigation\"", text);
        Assert.Contains("AutomationProperties.Name=\"主内容\"", text);
        Assert.Contains("AutomationProperties.AutomationId=\"Main\"", text);
    }

    [Fact]
    public void MainWindow_WindowButtonsAreRealButtonsWithReadableNames()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));

        foreach (var name in new[] { "最小化", "最大化", "关闭" })
        {
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", text);
        }

        // 票 #107：三键收进胶囊后带 .solid 修饰类，这里只钉「是 Button 且类名以 window-btn 起头」，
        // 不锁死完整类名——否则每次加修饰类都要改测试。
        Assert.Contains("<Button Classes=\"window-btn", text);
        Assert.Matches("Classes=\"window-btn[^\"]*\"", text);
        Assert.Matches("Classes=\"window-btn[^\"]*close[^\"]*\"", text);
        Assert.Contains("Icon.WindowMinimize", text);
        Assert.Contains("Icon.WindowMaximize", text);
        Assert.Contains("Icon.WindowClose", text);
    }

    [Fact]
    public void WindowButtons_TakeStrokeFromStyleNotInline()
    {
        // PITFALLS 4.2：Path 上的内联 Stroke 是本地值，会压过 :pointerover 的样式 setter，
        // 关闭键 hover 就只变底色、叉号不变白。这里钉住「内联不写 Stroke、样式给默认色」。
        var window = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));
        var controls = File.ReadAllText(Path.Combine(RepoRoot(), ControlsPath));

        foreach (var key in new[] { "Icon.WindowMinimize", "Icon.WindowMaximize", "Icon.WindowClose", "Icon.Search" })
        {
            var block = Regex.Match(window, $"Data=\"\\{{StaticResource {Regex.Escape(key)}\\}}\"(?<body>[^>]*)>");
            Assert.True(block.Success, $"MainWindow.axaml 里找不到 {key} 的 Path");
            Assert.DoesNotContain("Stroke=", block.Groups["body"].Value);
        }

        Assert.Contains("Selector=\"Button.window-btn Path\"", controls);
        Assert.Contains("Selector=\"Button.window-btn.close:pointerover Path\"", controls);
        // 票 #107：主页搜索框里的放大镜也走样式，不写内联描边。
        Assert.Contains("Selector=\"Border.titlesearch Path\"", controls);
    }

    [Fact]
    public void ShellChromeStyles_LiveInThemesNotInTheWindow()
    {
        // §7.5：A/B 档控件样式集中在 Themes/Controls.axaml，窗口只负责摆放。
        var window = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));
        var controls = File.ReadAllText(Path.Combine(RepoRoot(), ControlsPath));

        Assert.DoesNotContain("<Window.Styles>", window);
        Assert.Contains("Selector=\"Button.nav\"", controls);
        Assert.Contains("Selector=\"Button.nav.active\"", controls);
        Assert.Contains("Selector=\"TextBlock.page-title\"", controls);
        // 票 #107：胶囊、搜索框、实块三键也都是主题层样式，不在窗口里写。
        Assert.Contains("Selector=\"Border.winpill\"", controls);
        Assert.Contains("Selector=\"Border.titlesearch\"", controls);
        Assert.Contains("Selector=\"Button.window-btn.solid\"", controls);
    }

    [Fact]
    public void WindowPill_ShiftsBetweenSearchBoxAndRightEdge()
    {
        // 票 #107 §13：胶囊贴右，主页时横移到搜索框右侧；位移只由 UpdatePillOffset 一处算，
        // 时长走 Motion.Dur，动效关掉时归零。
        var window = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowCodePath));

        Assert.Contains("x:Name=\"WindowPill\"", window);
        Assert.Contains("x:Name=\"HomeSearch\"", window);
        Assert.Contains("IsVisible=\"{Binding IsHomePage}\"", window);
        Assert.Matches("DoubleTransition Property=\"X\"", window);
        Assert.Contains("UpdatePillOffset", code);
        Assert.Contains("Motion.Dur(", code);
    }

    [Fact]
    public void MainWindow_HasNoPageLevelDesignLiterals()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), MainWindowPath));

        var colorLiterals = Regex.Matches(text, "#[0-9A-Fa-f]{6,8}").Select(m => m.Value).ToList();
        Assert.True(colorLiterals.Count == 0, "MainWindow.axaml 出现颜色字面量：" + string.Join(", ", colorLiterals));

        var fontLiterals = Regex.Matches(text, "FontSize=\"[0-9]").Select(m => m.Value).ToList();
        Assert.True(fontLiterals.Count == 0, "MainWindow.axaml 出现字号字面量：" + string.Join(", ", fontLiterals));

        var radiusLiterals = Regex.Matches(text, "CornerRadius=\"[0-9]").Select(m => m.Value).ToList();
        Assert.True(radiusLiterals.Count == 0, "MainWindow.axaml 出现圆角字面量：" + string.Join(", ", radiusLiterals));

        var durationLiterals = Regex.Matches(text, "Duration=\"0:0:").Select(m => m.Value).ToList();
        Assert.True(durationLiterals.Count == 0, "MainWindow.axaml 出现时长字面量：" + string.Join(", ", durationLiterals));
    }

    [Fact]
    public void PageHost_UsesFadeOnlyTransition()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), PageHostPath));

        Assert.Contains("Motion.Dur(Motion.Normal)", text);
        Assert.DoesNotContain("TransformOperationsTransition", text);
        Assert.DoesNotContain("translateY", text);
    }

    [Fact]
    public void EveryUserControlInViews_HasDataType()
    {
        var views = Directory.GetFiles(Path.Combine(RepoRoot(), "src/NebulaLauncher.App/Views"), "*.axaml");
        var missing = new List<string>();

        foreach (var file in views)
        {
            var doc = XDocument.Load(file);
            if (doc.Root?.Name.LocalName != "UserControl")
            {
                continue;
            }

            var dataType = doc.Root.Attribute(XamlNamespace + "DataType");
            if (dataType is null || string.IsNullOrWhiteSpace(dataType.Value))
            {
                missing.Add(Path.GetFileName(file));
            }
        }

        Assert.True(missing.Count == 0,
            "这些 UserControl 没有写 x:DataType：" + string.Join(", ", missing));
    }

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NebulaLauncher.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 NebulaLauncher.sln");
    }
}
