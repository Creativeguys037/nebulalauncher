using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 页面层（票 #82）的 XAML 契约测试。
///
/// 为什么要有这组测试：票面 Testing Decisions 把「页面零设计字面量」定成硬契约，但 Avalonia 对
/// <c>FontSize="13"</c> 这类值完全接受——编译期不报、运行期不炸，只会在改令牌或换档位时
/// 变成一堆散落在各个页面里的孤儿值。本机没有 UI 自动化，人眼截图也拦不住「某个页多了一个 18px」，
/// 所以把票面那四条断言里跟页面有关的部分做成静态契约：
///   1. 页面没有颜色字面量（十六进制与命名颜色都拦）；
///   2. 页面没有字号 / 圆角 / 时长字面量；
///   3. 每个 <c>DataTemplate</c> 都写了 <c>x:DataType</c>。
///
/// 属性式（<c>FontSize="13"</c>）与 Setter 式（<c>&lt;Setter Property="CornerRadius" Value="5" /&gt;</c>）
/// 两种写法都会抓到：后者正是本次清理时最容易漏掉的一处（ServersView 的状态灯）。
///
/// 关于「三个属性合成一个 [Theory]」：它们共用同一段扫描与断言，拆成三个 [Fact] 只是三份逐字重复的壳；
/// [Theory] 的每个 InlineData 在报告里仍带参数（<c>Pages_HaveNoDesignLiteral(property: "FontSize")</c>），
/// 一眼看得出是哪类契约破了。颜色单独留一个 [Fact] 是因为它还多查命名颜色。
///
/// 测试只读文本与 XML，不触碰任何 Avalonia 类型。
/// </summary>
public class AppTheme_PageContractTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>把元素从「默认拉伸铺满」收紧的属性；出现任一条就不是遮罩层。</summary>
    private static readonly string[] SizingAttributes =
    {
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin", "Padding",
        "HorizontalAlignment", "VerticalAlignment",
    };

    private const string ViewsDir = "src/NebulaLauncher.App/Views";

    private const string ControlThemesDir = "src/NebulaLauncher.App/Themes/ControlThemes";

    private const string TokensFile = "src/NebulaLauncher.App/Themes/Tokens.axaml";

    /// <summary>
    /// MainWindow 已有专属的 AppShell_NavigationContractTests.MainWindow_HasNoPageLevelDesignLiterals，
    /// 这里排除它，避免同一处问题报两遍。
    /// </summary>
    private const string MainWindowFile = "MainWindow.axaml";

    [Fact]
    public void Pages_HaveNoColorLiterals()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in PageTexts())
        {
            foreach (Match match in Regex.Matches(text, "#[0-9A-Fa-f]{6,8}"))
            {
                offenders.Add($"{name}:{LineOf(text, match.Index)}: {match.Value}");
            }

            // 票面只写 #RRGGBB，AGENTS.md 的禁令是「页面里不写颜色字面量」——命名颜色同样绕过令牌，
            // 而且它比十六进制更难 grep（改名没有色值可搜）。Transparent 不是设计色值而是「无填充」，放行。
            foreach (Match match in Regex.Matches(text,
                         @"(?:Background|Foreground|BorderBrush|Fill|Stroke|TextBrush)=""(?<v>[A-Za-z]+)"""))
            {
                var value = match.Groups["v"].Value;
                if (!string.Equals(value, "Transparent", StringComparison.Ordinal))
                {
                    offenders.Add($"{name}:{LineOf(text, match.Index)}: 命名颜色 {value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "页面出现颜色字面量（应改用 Themes/Tokens.axaml 的令牌）：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("FontSize", "字号字面量（应改用 FontSize.* 令牌，或用 .caption/.muted/.h2 等样式类）")]
    [InlineData("CornerRadius", "圆角字面量（应改用 Radius.* 令牌：Xs/Sm/Md/Lg/Xl/X2l/X3l/Pill）")]
    [InlineData("Duration", "时长字面量（应改用 {x:Static services:Motion.*}）")]
    public void Pages_HaveNoDesignLiteral(string property, string hint)
    {
        var offenders = CollectDesignLiterals(property);

        Assert.True(offenders.Count == 0,
            $"页面出现{hint}：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryDataTemplate_DeclaresDataType()
    {
        var missing = new List<string>();
        foreach (var (name, text) in PageTexts())
        {
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            foreach (var template in doc.Descendants().Where(e => e.Name.LocalName == "DataTemplate"))
            {
                var dataType = template.Attribute(XamlNamespace + "DataType");
                if (dataType is null || string.IsNullOrWhiteSpace(dataType.Value))
                {
                    var line = (template as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;
                    missing.Add($"{name}:{line}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "这些 DataTemplate 没有写 x:DataType（编译绑定会退回反射，绑错路径只运行期报）：" + Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// 浮层遮罩契约（docs/UI-STRUCTURE.md §6 规则 7，偏差 D5）：全页浮层的遮罩必须用
    /// <c>Surface.Scrim</c>（黑 40%，两套主题共用），不得用任何不透明底冒充——那会把下层
    /// 整片盖住，而不是压暗。
    ///
    /// 「哪些令牌不透明」不维护名单：名单一定会漏（本票审查实测漏掉 Raised 与 Tooltip，
    /// 它们与 Sunken 是同一类错误）。改为读 Themes/Tokens.axaml 的令牌定义，凡在任一主题里
    /// 给出不透明色值的 Surface.* 都算；Scrim 自己的 #66000000 带 alpha，天然排除。
    ///
    /// 识别形状两种，都是本仓库实际出现过的写法：
    ///   1. 背景挂在浮层容器自身（<c>Border Background="…" IsVisible="…"</c>）；
    ///      只有直接子级里有「居中浮层卡」时才算遮罩——实例设置 / 换加载器 / 回收站这类
    ///      内嵌面板同样是状态绑定 + 不透明底，但它们不是浮层，不该被拦；
    ///   2. 背景挂在无内容的填充子级（AccountsView 原写法：Panel + 独立遮罩层），
    ///      它没有居中卡可认，按「纯填充层引用了不透明底」判定。
    ///
    /// 可见性绑定不限定命名：ModsView 用 HasPendingDelete / HasUpdates 驱动浮层，
    /// 按 Is*Open 白名单匹配会漏掉它们。范围除页面外还含 Themes/ControlThemes
    /// （DialogOverlay 模板里那层遮罩是同一条规则的另一处实现）。
    ///
    /// 与页面字面量契约一样只读 XML 文本，不触碰 Avalonia 类型。
    /// </summary>
    [Fact]
    public void FullScreenOverlays_UseScrimBackground()
    {
        const string scrim = "Surface.Scrim";
        var opaqueSurfaces = OpaqueSurfaceTokens();
        Assert.NotEmpty(opaqueSurfaces);

        var offenders = new List<string>();
        foreach (var (name, text) in OverlayTexts())
        {
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants())
            {
                if (!IsVisibilityBound(element))
                {
                    continue;
                }

                if (SurfaceTokenOf(element) is { } own && opaqueSurfaces.Contains(own) && HasCenteredCard(element))
                {
                    offenders.Add($"{name}:{LineNumberOf(element)}: 浮层容器用 {own}，应为 {scrim}");
                }

                foreach (var child in element.Elements())
                {
                    if (!IsPureFill(child) ||
                        SurfaceTokenOf(child) is not { } childToken ||
                        !opaqueSurfaces.Contains(childToken))
                    {
                        continue;
                    }

                    offenders.Add($"{name}:{LineNumberOf(child)}: 遮罩层用 {childToken}，应为 {scrim}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "浮层遮罩必须用 Surface.Scrim（docs/UI-STRUCTURE.md §6 规则 7）：" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 从 Tokens.axaml 算出所有不透明的 Surface.* 令牌。同一个键可能在深浅两套主题各定义一次，
    /// 只要有一处不透明就收紧：遮罩的语义与主题无关，任何一套里退化成实心底都算坏。
    /// </summary>
    private static HashSet<string> OpaqueSurfaceTokens()
    {
        var path = Path.Combine(DocsCheckTestPaths.RepoRoot(), TokensFile);
        Assert.True(File.Exists(path), $"找不到令牌文件：{TokensFile}");

        var doc = XDocument.Parse(File.ReadAllText(path));
        var opaque = new HashSet<string>(StringComparer.Ordinal);
        foreach (var brush in doc.Descendants())
        {
            var key = brush.Attribute(XamlNamespace + "Key")?.Value;
            if (key is null || !key.StartsWith("Surface.", StringComparison.Ordinal))
            {
                continue;
            }

            // Color 属性与元素文本两种写法都有（SolidColorBrush 的简写形式）。
            if (IsOpaqueColor(brush.Attribute("Color")?.Value ?? brush.Value))
            {
                opaque.Add(key);
            }
        }

        return opaque;
    }

    /// <summary>#RRGGBB 不透明；#AARRGGBB 只有 FF 才算不透明。</summary>
    private static bool IsOpaqueColor(string value)
    {
        var hex = value.Trim().TrimStart('#');
        return hex.Length switch
        {
            6 => true,
            8 => hex.StartsWith("FF", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>元素 Background 若恰好是单个 Surface.* 令牌，返回令牌名。</summary>
    private static string? SurfaceTokenOf(XElement element)
    {
        var match = Regex.Match(
            element.Attribute("Background")?.Value ?? "",
            @"^\{DynamicResource (Surface\.\w+)\}$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 可见性由状态驱动的容器。不限定命名：本仓库的浮层有 Is*Open、Is*Confirm，也有
    /// HasPendingDelete / HasUpdates；控件模板里则是 TemplateBinding。
    /// </summary>
    private static bool IsVisibilityBound(XElement element)
    {
        var value = element.Attribute("IsVisible")?.Value ?? "";
        return value.EndsWith('}') &&
               (value.StartsWith("{Binding ", StringComparison.Ordinal) ||
                value.StartsWith("{TemplateBinding ", StringComparison.Ordinal));
    }

    private static int LineNumberOf(XElement element) =>
        (element as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;

    /// <summary>
    /// 居中浮层卡：本仓库的全页浮层统一是「容器 + Border Classes="card" 居中、带 Width/MaxWidth」
    /// 的形状。只认 Center/Center 会把图标占位块里居中的文字也当成浮层卡。
    /// </summary>
    private static bool HasCenteredCard(XElement element) =>
        element.Elements().Any(e =>
            string.Equals(e.Attribute("HorizontalAlignment")?.Value, "Center", StringComparison.Ordinal) &&
            string.Equals(e.Attribute("VerticalAlignment")?.Value, "Center", StringComparison.Ordinal) &&
            (e.Attribute("Width") is not null || e.Attribute("MaxWidth") is not null) &&
            (e.Attribute("Classes")?.Value ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("card", StringComparer.Ordinal));

    /// <summary>
    /// 纯填充层：没有元素子级，也没有非空文本，而且没有被尺寸 / 外边距 / 对齐收紧——遮罩默认
    /// Stretch 铺满父容器。骨架屏占位条有 Height、卡片里的图标底有子级文字，都不是遮罩。
    /// </summary>
    private static bool IsPureFill(XElement element) =>
        !element.Nodes().OfType<XElement>().Any() &&
        !element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)) &&
        !SizingAttributes.Any(name => element.Attribute(name) is not null);

    /// <summary>
    /// 遮罩契约的扫描范围：全部页面（含 MainWindow——D5 那处浮层就挂在 MainWindow.axaml）
    /// 加控件主题目录（DialogOverlay 的遮罩写在模板里）。
    /// </summary>
    private static IEnumerable<(string Name, string Text)> OverlayTexts() =>
        ViewTexts(includeMainWindow: true).Concat(ControlThemeTexts());

    private static IEnumerable<(string Name, string Text)> ControlThemeTexts()
    {
        var dir = Path.Combine(DocsCheckTestPaths.RepoRoot(), ControlThemesDir);
        Assert.True(Directory.Exists(dir), $"找不到控件主题目录：{ControlThemesDir}");

        return Directory.GetFiles(dir, "*.axaml")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));
    }

    /// <summary>
    /// 收集某个属性的字面量。值以数字开头才算字面量：<c>{DynamicResource ...}</c> 与
    /// <c>{x:Static ...}</c> 都以 <c>{</c> 起头，天然被排除，不需要维护白名单。
    /// </summary>
    private static List<string> CollectDesignLiterals(string property)
    {
        // 属性式：FontSize="13"
        var attributePattern = $@"\b{property}=""([0-9][^""]*)""";
        // Setter 式：<Setter Property="FontSize" Value="13" />（Value 可以与 Property 跨行）
        var setterPattern = $@"Property=""{property}""[^>]*?Value=""([0-9][^""]*)""";

        var offenders = new List<string>();
        foreach (var (name, text) in PageTexts())
        {
            foreach (Match match in Regex.Matches(text, attributePattern))
            {
                offenders.Add($"{name}:{LineOf(text, match.Index)}: {property}=\"{match.Groups[1].Value}\"");
            }

            foreach (Match match in Regex.Matches(text, setterPattern))
            {
                offenders.Add($"{name}:{LineOf(text, match.Index)}: Setter {property}=\"{match.Groups[1].Value}\"");
            }
        }

        return offenders;
    }

    /// <summary>
    /// 页面文本。<paramref name="includeMainWindow"/> 为 false 时排除外壳，理由见
    /// <see cref="MainWindowFile"/> 的注释；遮罩契约反过来需要它。
    /// </summary>
    private static IEnumerable<(string Name, string Text)> ViewTexts(bool includeMainWindow)
    {
        var dir = Path.Combine(DocsCheckTestPaths.RepoRoot(), ViewsDir);
        Assert.True(Directory.Exists(dir), $"找不到页面目录：{ViewsDir}");

        return Directory.GetFiles(dir, "*.axaml")
            .Where(path => includeMainWindow ||
                           !string.Equals(Path.GetFileName(path), MainWindowFile, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));
    }

    private static IEnumerable<(string Name, string Text)> PageTexts() => ViewTexts(includeMainWindow: false);

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;
}