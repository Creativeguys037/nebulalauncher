using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 令牌 / 图标 / 动效的 XAML 契约测试。
///
/// 为什么要有这组测试：Avalonia 的 <c>{DynamicResource}</c> 找不到键不会编译失败，
/// 而是运行期抛 <c>KeyNotFoundException</c>；主题字典只加一边也能编译通过，切主题才炸。
/// 本机没有 UI 自动化，靠人眼截图拦不住所有键，所以把「键集合相等」「所有引用可解析」
/// 「时长只剩一处来源」做成静态契约。测试只读文本与 XML，不触碰任何 Avalonia 类型。
/// </summary>
public class AppTheme_TokenContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string TokensPath = "src/NebulaLauncher.App/Themes/Tokens.axaml";
    private const string IconsPath = "src/NebulaLauncher.App/Themes/Icons.axaml";
    private const string ControlsPath = "src/NebulaLauncher.App/Themes/Controls.axaml";
    private const string AppPath = "src/NebulaLauncher.App/App.axaml";
    private const string MotionPath = "src/NebulaLauncher.App/Services/Motion.cs";

    [Fact]
    public void ThemeDictionaries_HaveIdenticalKeySets()
    {
        var doc = Load(TokensPath);
        var dark = ThemeKeys(doc, "Dark").OrderBy(k => k, StringComparer.Ordinal).ToList();
        var light = ThemeKeys(doc, "Light").OrderBy(k => k, StringComparer.Ordinal).ToList();

        var onlyDark = dark.Except(light).ToList();
        var onlyLight = light.Except(dark).ToList();

        Assert.True(onlyDark.Count == 0 && onlyLight.Count == 0,
            $"Dark / Light 键集合不一致。仅 Dark: [{string.Join(", ", onlyDark)}]；仅 Light: [{string.Join(", ", onlyLight)}]");
        // 45 个旧键 + 10 个 Nebula 2.0 新键；只许往后加，不许减。
        Assert.True(dark.Count >= 55, $"主题键少于基线 55 个，实际 {dark.Count} 个");
    }

    [Fact]
    public void EveryDynamicResourceKeyResolves()
    {
        var tokens = Load(TokensPath);
        var tokenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var theme in new[] { "Dark", "Light" })
        {
            tokenKeys.UnionWith(ThemeKeys(tokens, theme));
        }

        tokenKeys.UnionWith(RootKeys(tokens));
        var declared = new HashSet<string>(tokenKeys, StringComparer.Ordinal);

        // 页面 / 样式 / 图标里自己声明的 x:Key 也算可解析目标（例如 App.axaml 的 Font.UI）。
        var appRoot = RepoRoot();
        var allFiles = Directory.GetFiles(Path.Combine(appRoot, "src/NebulaLauncher.App"), "*.axaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in allFiles)
        {
            var doc = XDocument.Load(file);
            documents[file] = doc;
            declared.UnionWith(AllDeclaredKeys(doc));
        }

        var missing = new List<string>();
        var tokenPrefixes = new[] { "Surface.", "Glass.", "Stroke.", "Text.", "Accent.", "Status.", "Memory.", "Gradient.", "Shadow.", "Scrollbar.", "Window." };

        foreach (var (file, doc) in documents)
        {
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    foreach (Match match in Regex.Matches(attribute.Value, @"\{DynamicResource\s+([^\s},]+)"))
                    {
                        var key = match.Groups[1].Value;

                        // 令牌前缀的键必须真的来自令牌字典：页面本地同名 x:Key 不能掩盖全局缺失。
                        var isTokenKey = tokenPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));
                        if (!declared.Contains(key) || (isTokenKey && !tokenKeys.Contains(key)))
                        {
                            missing.Add($"{Relative(appRoot, file)} => {key}");
                        }
                    }
                }
            }
        }

        Assert.True(missing.Count == 0,
            "以下 DynamicResource 键在两个主题字典与资源根里都找不到：" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Controls_UseMotionTokensForEveryDuration()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ControlsPath));

        Assert.Contains("xmlns:services=\"using:NebulaLauncher.Services\"", text);
        Assert.DoesNotContain("Duration=\"0:0:", text);

        var literals = Regex.Matches(text, "Duration=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(v => !v.StartsWith("{x:Static services:Motion.", StringComparison.Ordinal))
            .ToList();

        Assert.True(literals.Count == 0,
            $"Controls.axaml 里还有非 Motion 来源的时长：[{string.Join(", ", literals)}]");
    }

    [Fact]
    public void Icons_ExposeRequiredGeometry()
    {
        var doc = Load(IconsPath);
        var keys = AllDeclaredKeys(doc).ToHashSet(StringComparer.Ordinal);
        var required = new[]
        {
            "Icon.Home", "Icon.Versions", "Icon.Content", "Icon.Download", "Icon.Account",
            "Icon.Server", "Icon.Settings", "Icon.WindowMinimize", "Icon.WindowMaximize", "Icon.WindowClose",
        };

        var missing = required.Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"Icons.axaml 缺少：[{string.Join(", ", missing)}]");

        foreach (var key in required)
        {
            var geometry = doc.Root!.Elements()
                .Single(e => (string?)e.Attribute(X + "Key") == key);
            Assert.Equal("StreamGeometry", geometry.Name.LocalName);
            Assert.False(string.IsNullOrWhiteSpace(geometry.Value), $"{key} 的 path data 为空");
            Assert.True(geometry.Value.TrimStart().StartsWith("M", StringComparison.OrdinalIgnoreCase),
                $"{key} 的 path data 不是以 M 起笔的合法几何：{geometry.Value}");
        }
    }

    [Fact]
    public void App_MergesIconDictionary()
    {
        var doc = Load(AppPath);
        var merged = doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceInclude")
            .Select(e => (string?)e.Attribute("Source"))
            .Where(s => s is not null)
            .ToList();

        Assert.Contains(merged, s => s!.EndsWith("Themes/Icons.axaml", StringComparison.Ordinal));
        Assert.Contains(merged, s => s!.EndsWith("Themes/Tokens.axaml", StringComparison.Ordinal));
    }

    [Fact]
    public void DarkTheme_MatchesNebula2Values()
    {
        var doc = Load(TokensPath);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Surface.Base"] = "#0A0205",
            ["Surface.Canvas"] = "#0F0407",
            ["Surface.Card"] = "#0FE80018",
            ["Surface.CardSolid"] = "#1A0810",
            ["Surface.Raised"] = "#1A0810",
            ["Surface.Hover"] = "#1FE80018",
            ["Surface.Pressed"] = "#29E80018",
            ["Surface.Sunken"] = "#140305",
            ["Surface.Glass"] = "#14E80018",
            ["Surface.Sidebar"] = "#12050A",
            ["Stroke.Subtle"] = "#14FFFAFA",
            ["Stroke.Default"] = "#24FFFAFA",
            ["Stroke.Strong"] = "#3DFFFAFA",
            ["Stroke.Focus"] = "#E80018",
            ["Text.Primary"] = "#FFFAFA",
            ["Text.Secondary"] = "#F0F0F0",
            ["Text.Tertiary"] = "#DBD6D2",
            ["Text.Muted"] = "#80DBD6D2",
            ["Text.OnBrand"] = "#FFFAFA",
            ["Accent.Primary"] = "#E80018",
            ["Accent.Brand"] = "#B53131",
            ["Accent.Soft"] = "#1FE80018",
            ["Accent.Glow"] = "#40E80018",
            ["Status.Danger"] = "#B53131",
            ["Status.Success"] = "#34D399",
            ["Status.Warning"] = "#FBBF24",
            ["Status.Info"] = "#22D3EE",
            ["Glass.Tint"] = "#0FE80018",
            ["Glass.Edge"] = "#2EB53131",
            ["Glass.TopLight"] = "#14FFFAFA",
            ["Glass.InnerShadow"] = "#66000000",
            ["Glass.Shadow"] = "#99000000",
            ["Window.CloseHover"] = "#B53131",
        };

        AssertValues(doc, "Dark", expected);
    }

    [Fact]
    public void LightTheme_MatchesNebula2Values()
    {
        var doc = Load(TokensPath);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Surface.Base"] = "#E8F0FF",
            ["Surface.Canvas"] = "#F7FAFF",
            ["Surface.Card"] = "#B3FFFFFF",
            ["Surface.CardSolid"] = "#FFFFFF",
            ["Surface.Raised"] = "#CCFFFFFF",
            ["Surface.Hover"] = "#F0F4FF",
            ["Surface.Pressed"] = "#E2EAFC",
            ["Surface.Sunken"] = "#E9EFFC",
            ["Surface.Glass"] = "#57FFFFFF",
            ["Surface.Sidebar"] = "#EDF3FF",
            ["Stroke.Focus"] = "#1668FF",
            ["Text.Primary"] = "#0A0D14",
            ["Text.OnBrand"] = "#FFFAFA",
            ["Accent.Primary"] = "#1668FF",
            ["Accent.Brand"] = "#B53131",
            ["Accent.Secondary"] = "#B53131",
            ["Status.Danger"] = "#C42020",
            ["Glass.Tint"] = "#57FFFFFF",
            ["Glass.Edge"] = "#DBFFFFFF",
            ["Glass.TopLight"] = "#FFFFFF",
            ["Glass.InnerShadow"] = "#47103078",
            ["Glass.Shadow"] = "#38103078",
        };

        AssertValues(doc, "Light", expected);
    }

    [Fact]
    public void GlassShadow_UsesInsetSyntaxWithoutPx()
    {
        var doc = Load(TokensPath);
        foreach (var theme in new[] { "Dark", "Light" })
        {
            var shadow = ThemeDictionary(doc, theme).Elements()
                .Single(e => (string?)e.Attribute(X + "Key") == "Shadow.Glass");
            var text = shadow.Value;

            Assert.Contains("inset 0 2 0", text);
            Assert.Contains("inset 0 -16 30 -16", text);
            Assert.Contains("0 26 60 -12", text);
            Assert.DoesNotContain("px", text);
        }
    }

    [Fact]
    public void ShapeAndTypeScales_MatchNebula2()
    {
        var doc = Load(TokensPath);
        var radii = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Radius.Sm"] = "8",
            ["Radius.Md"] = "12",
            ["Radius.Lg"] = "16",
            ["Radius.Xl"] = "20",
            ["Radius.X2l"] = "24",
            ["Radius.X3l"] = "28",
            ["Radius.Pill"] = "999",
        };
        AssertValues(doc, null, radii);

        var sizes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FontSize.Caption"] = "11",
            ["FontSize.Small"] = "12",
            ["FontSize.Body"] = "13",
            ["FontSize.BodyLg"] = "15",
            ["FontSize.Subtitle"] = "17",
            ["FontSize.Title"] = "20",
            ["FontSize.Headline"] = "26",
            ["FontSize.Display"] = "29",
            ["FontSize.DisplayLg"] = "38",
        };
        AssertValues(doc, null, sizes);

        // 旧键必须保留：页面仍引用 Radius.Xs / Accent.Secondary 等，删键会在运行期炸。
        // 这些键分布在两个主题字典与字典根部，不能只查根部。
        var declaredKeys = AllDeclaredKeys(doc).ToHashSet(StringComparer.Ordinal);
        foreach (var legacy in new[] { "Radius.Xs", "Accent.Secondary", "Accent.Tertiary", "Gradient.Aurora", "Gradient.CardOverlay", "Memory.BarFree" })
        {
            Assert.Contains(legacy, declaredKeys);
        }
    }

    [Fact]
    public void Motion_ExposesTypedDurationsAndEasings()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), MotionPath));

        Assert.Contains("public static readonly TimeSpan Instant", text);
        Assert.Contains("public static readonly TimeSpan Fast", text);
        Assert.Contains("public static readonly TimeSpan Normal", text);
        Assert.Contains("public static readonly TimeSpan Slow", text);
        Assert.Contains("public static readonly Easing EaseOut", text);
        Assert.Contains("public static readonly Easing EaseInOut", text);
        // Avalonia 11.3 的 CubicBezierEasing 已废弃且不再实现 Easing，四参等价物是 SplineEasing。
        Assert.Contains("new SplineEasing(0.22, 0.9, 0.24, 1)", text);
        Assert.Contains("new SplineEasing(0.4, 0, 0.2, 1)", text);
        // 代码里不能再构造已废弃、且不再实现 Easing 的 CubicBezierEasing；注释里的说明不算。
        Assert.DoesNotContain("new CubicBezierEasing(", text);
        Assert.Contains("public static TimeSpan Dur(TimeSpan normal)", text);
    }

    private static void AssertValues(XDocument doc, string? theme, IReadOnlyDictionary<string, string> expected)
    {
        var failures = new List<string>();
        foreach (var (key, value) in expected)
        {
            var element = theme is null
                ? doc.Root!.Elements().SingleOrDefault(e => (string?)e.Attribute(X + "Key") == key)
                : ThemeDictionary(doc, theme).Elements().SingleOrDefault(e => (string?)e.Attribute(X + "Key") == key);

            var actual = element is null
                ? null
                : (string?)element.Attribute("Color") ?? element.Value.Trim();

            if (actual != value)
            {
                failures.Add($"{theme ?? "root"}.{key}: 期望 {value}，实际 {actual ?? "<缺键>"}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static XElement ThemeDictionary(XDocument doc, string theme) =>
        doc.Root!
            .Element(XNamespace.Get("https://github.com/avaloniaui") + "ResourceDictionary.ThemeDictionaries")!
            .Elements(XNamespace.Get("https://github.com/avaloniaui") + "ResourceDictionary")
            .Single(e => (string?)e.Attribute(X + "Key") == theme);

    private static IEnumerable<string> ThemeKeys(XDocument doc, string theme) =>
        ThemeDictionary(doc, theme).Elements()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null)
            .Select(k => k!);

    private static IEnumerable<string> RootKeys(XDocument doc) =>
        doc.Root!.Elements()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null)
            .Select(k => k!);

    private static IEnumerable<string> AllDeclaredKeys(XDocument doc) =>
        doc.Descendants()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null)
            .Select(k => k!);

    private static XDocument Load(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"找不到文件：{relativePath}");
        return XDocument.Load(path);
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

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
