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
    private const string PageHostPath = "src/NebulaLauncher.App/Controls/PageHost.cs";

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
        Assert.Equal(NavigationKeys.Content, NavigationRoutes.Resolve(NavigationKeys.Library));
        Assert.Equal(NavigationKeys.Content, NavigationRoutes.Resolve(NavigationKeys.Skins));
        Assert.Equal(NavigationKeys.Versions, NavigationRoutes.Resolve(NavigationKeys.Worlds));
        Assert.Equal(NavigationKeys.Settings, NavigationRoutes.Resolve(NavigationKeys.Logs));
        Assert.Equal(NavigationKeys.Settings, NavigationRoutes.Resolve(NavigationKeys.Help));
    }

    [Fact]
    public void EveryLegacyKey_HasAUniquePrimaryTarget()
    {
        var legacy = new[]
        {
            NavigationKeys.Mods, NavigationKeys.Library, NavigationKeys.Skins,
            NavigationKeys.Worlds, NavigationKeys.Logs, NavigationKeys.Help,
        };

        Assert.All(legacy, key =>
        {
            Assert.True(NavigationRoutes.IsLegacy(key), $"{key} 没有登记为旧键");
            Assert.Contains(NavigationRoutes.Resolve(key), ExpectedPrimary);
        });

        // 一级键本身不再被当成旧键，否则 Navigate 会多绕一跳。
        Assert.All(ExpectedPrimary, key => Assert.False(NavigationRoutes.IsLegacy(key)));
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

        Assert.Contains("Classes=\"window-btn\"", text);
        Assert.Contains("Classes=\"window-btn close\"", text);
        Assert.Contains("Icon.WindowMinimize", text);
        Assert.Contains("Icon.WindowMaximize", text);
        Assert.Contains("Icon.WindowClose", text);
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
