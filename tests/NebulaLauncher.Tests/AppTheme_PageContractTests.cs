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

    private const string ViewsDir = "src/NebulaLauncher.App/Views";

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

    private static IEnumerable<(string Name, string Text)> PageTexts()
    {
        var dir = Path.Combine(DocsCheckTestPaths.RepoRoot(), ViewsDir);
        Assert.True(Directory.Exists(dir), $"找不到页面目录：{ViewsDir}");

        return Directory.GetFiles(dir, "*.axaml")
            .Where(path => !string.Equals(Path.GetFileName(path), MainWindowFile, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));
    }

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;
}