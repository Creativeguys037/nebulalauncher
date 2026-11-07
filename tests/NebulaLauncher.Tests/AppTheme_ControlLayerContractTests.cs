using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 控件层（票 #84）的 XAML 契约测试。
///
/// 为什么要有这组测试：ControlTheme 的键写错、漏把一个 C 档控件从聚合字典里挂进来，
/// 编译期都不会报错——运行期只会悄悄退回 Fluent 默认皮肤，而默认皮肤只在截图上才看得出来。
/// 这里把「C 档控件主题齐全」「主题键是全局隐式主题」「页面不写颜色字面量」
/// 「示例页覆盖六态与 14 项控件」做成静态契约。测试只读文本与 XML，不触碰 Avalonia 类型。
/// </summary>
public class AppTheme_ControlLayerContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string ThemesDir = "src/NebulaLauncher.App/Themes";
    private const string ControlThemesDir = "src/NebulaLauncher.App/Themes/ControlThemes";
    private const string GalleryPath = "src/NebulaLauncher.App/Views/ControlsGalleryView.axaml";
    private const string GalleryCodePath = "src/NebulaLauncher.App/Views/ControlsGalleryView.axaml.cs";

    /// <summary>C 档控件：模板结构必须整体替换，不能只靠 Style。</summary>
    private static readonly string[] CLevelControls =
    [
        "Button", "CheckBox", "DialogOverlay", "GlassPanel", "ProgressBar", "TabControl", "TabItem", "ToolTip",
    ];

    [Fact]
    public void ControlThemes_DefineEveryCLevelControl()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), ControlThemesDir), "*.axaml"))
        {
            var doc = XDocument.Load(file);
            foreach (var theme in doc.Descendants().Where(e => e.Name.LocalName == "ControlTheme"))
            {
                var key = (string?)theme.Attribute(X + "Key");
                if (key is not null)
                {
                    declared.Add(key);
                }
            }
        }

        // 自定义控件的键会带命名空间前缀（{x:Type controls:GlassPanel}），按类型名后缀匹配。
        var missing = CLevelControls
            .Where(control => !declared.Any(key => key.EndsWith(control + "}", StringComparison.Ordinal)))
            .ToList();

        Assert.True(missing.Count == 0,
            "ControlThemes 缺少以下 C 档控件的全局隐式主题（x:Key=\"{x:Type X}\"）：" + string.Join(", ", missing));
    }

    [Fact]
    public void ControlThemes_AggregatorIncludesEveryThemeFile()
    {
        var aggregator = Path.Combine(RepoRoot(), ControlThemesDir, "ControlThemes.axaml");
        var text = File.ReadAllText(aggregator);

        var files = Directory.GetFiles(Path.GetDirectoryName(aggregator)!, "*.axaml")
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "ControlThemes.axaml", StringComparison.Ordinal))
            .ToList();

        var missing = files.Where(name => !text.Contains(name!, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "聚合字典没有挂进这些主题文件：" + string.Join(", ", missing));
    }

    [Fact]
    public void ControlsAndControlThemes_HaveNoColorLiterals()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), ControlThemesDir), "*.axaml")
            .Concat([Path.Combine(RepoRoot(), ThemesDir, "Controls.axaml")]);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            // #00000000 是「无阴影」的透明色，不是设计色值，放行。
            foreach (Match match in Regex.Matches(text, "#[0-9A-Fa-f]{6,8}"))
            {
                if (!string.Equals(match.Value, "#00000000", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "控件层出现颜色字面量（应改用令牌）：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ControlsAndControlThemes_UseTheUnifiedFocusRing()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), ControlThemesDir), "*.axaml")
            .Concat([Path.Combine(RepoRoot(), ThemesDir, "Controls.axaml")]);

        var ringCount = files.Sum(file => Regex.Matches(File.ReadAllText(file), @"Stroke\.Focus").Count);

        // Button / CheckBox / TextBox / ComboBox / ListBoxItem / ToggleSwitch 六类可聚焦控件，
        // 每类至少一处 2px 主色环；少于这个数是漏了某个控件的 FocusAdorner。
        Assert.True(ringCount >= 6, $"统一焦点环的引用只有 {ringCount} 处，少于六类可聚焦控件的下限");
    }

    [Fact]
    public void Controls_IncludesTheAggregatorDictionary()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ThemesDir, "Controls.axaml"));
        Assert.Contains("ControlThemes/ControlThemes.axaml", text);
    }

    [Fact]
    public void ControlThemes_UseMotionTokensForEveryDuration()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), ControlThemesDir), "*.axaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "Duration=\"([^\"]+)\""))
            {
                var value = match.Groups[1].Value;
                if (!value.StartsWith("{x:Static services:Motion.", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "ControlThemes 里有非 Motion 来源的时长：" + string.Join(", ", offenders));
    }

    [Fact]
    public void ScrollBar_HasDedicatedStyleOverrides()
    {
        // 票 #84 的 ScrollBar 是 C 档，但自定义 Track 模板在本机不渲染（见交付说明的已知偏差）。
        // 这里保证它至少不再吃 Fluent 默认皮肤：宽度、滑块色、箭头隐藏都必须有明确覆盖。
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ThemesDir, "Controls.axaml"));
        Assert.Contains("ScrollBar:vertical", text);
        Assert.Contains("Scrollbar.Thumb", text);
        Assert.Contains("PART_LineUpButton", text);
        Assert.Contains("PART_LineDownButton", text);
    }

    [Fact]
    public void Gallery_EverySectionHasDemoTargets()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), GalleryPath));
        var sections = Regex.Split(text, "<!-- [0-9]+ ").Skip(1).ToList();
        var missing = new List<string>();

        // 纯展示区段不参与状态切换，因此在「每段都要有 demo 目标」上豁免：
        // 「字号样张」是 #103 的字体实测页（九档字号 × 中西文混排），里面没有控件状态可言。
        var exemptTitles = new[] { "字号样张" };

        foreach (var section in sections)
        {
            var titleEnd = section.IndexOf(" -->", StringComparison.Ordinal);
            var title = titleEnd > 0 ? section[..titleEnd] : section[..Math.Min(20, section.Length)];
            if (exemptTitles.Any(e => title.Contains(e, StringComparison.Ordinal)))
            {
                continue;
            }

            if (!section.Contains("Tag=\"demo\"", StringComparison.Ordinal))
            {
                missing.Add(title);
            }
        }

        Assert.True(missing.Count == 0,
            "示例页这些控件区段没有可切换状态的演示实例：" + string.Join(", ", missing));
    }

    [Fact]
    public void Gallery_HasNoColorLiterals()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), GalleryPath));
        var offenders = Regex.Matches(text, "#[0-9A-Fa-f]{6,8}").Select(m => m.Value).ToList();
        Assert.True(offenders.Count == 0, "示例页出现颜色字面量：" + string.Join(", ", offenders));
    }

    [Fact]
    public void Gallery_CoversEveryControlAndAllSixStates()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), GalleryPath));

        var controls = new[]
        {
            "Button", "TextBox", "ComboBox", "CheckBox", "ToggleSwitch", "ListBox", "ScrollBar",
            "TabControl", "ToolTip", "Dialog", "ProgressBar", "Card", "Badge", "window-btn",
        };
        var missingControls = controls.Where(name => !text.Contains(name, StringComparison.Ordinal)).ToList();
        Assert.True(missingControls.Count == 0,
            "示例页没有覆盖这些控件：" + string.Join(", ", missingControls));

        var states = new[] { "default", "hover", "focus", "pressed", "disabled", "loading" };
        var missingStates = states.Where(state => !text.Contains($"Tag=\"{state}\"", StringComparison.Ordinal)).ToList();
        Assert.True(missingStates.Count == 0,
            "示例页的状态切换缺少这些状态：" + string.Join(", ", missingStates));

        // 每项演示控件都要带 Tag="demo"，状态切换器按这个标记批量应用状态。
        var demoCount = Regex.Matches(text, "Tag=\"demo\"").Count;
        Assert.True(demoCount >= 30, $"Tag=\"demo\" 的演示控件只有 {demoCount} 个，14 项控件都应有可切换的演示实例");
    }

    [Fact]
    public void GalleryCodeBehind_HandlesEveryStateAndLoadingClass()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), GalleryCodePath));

        foreach (var state in new[] { "hover", "pressed", "disabled", "loading", "focus" })
        {
            Assert.Contains($"case \"{state}\":", text);
        }

        Assert.Contains("Classes.Add(\"loading\")", text);
        Assert.Contains("Classes.Add(\"demo-hover\")", text);
        Assert.Contains("Classes.Add(\"demo-pressed\")", text);
    }

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
