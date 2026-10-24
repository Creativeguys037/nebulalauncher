using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace NebulaLauncher.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();
}

/// <summary>
/// 渲染带 § 颜色码的 MOTD。
///
/// 用 Inlines 而不是多个 TextBlock：MOTD 是连续文本，拆块会按块换行，中文段落会被切得很碎。
/// 调色板取自 Minecraft 协议默认的 16 色，属于协议数据而非设计令牌，因此不走资源字典。
///
/// 双主题：协议色是为深色游戏画面设计的（§a 的 #55FF55 在白色卡片上只有 1.3:1），
/// 浅色主题下整块 MOTD 会变成几乎看不见的浅色涂料。所以浅色主题用一张「压深一档」的等价色表，
/// 每色相对纯白 ≥ 4.5:1，色相保持与原版一一对应（绿仍是绿、品红仍是品红）。
/// 主题切换时 Runs 里的 Brush 不会自动变，必须重建 Inlines。
/// </summary>
public sealed class MotdBlock : TextBlock
{
    public static readonly StyledProperty<string?> MotdProperty =
        AvaloniaProperty.Register<MotdBlock, string?>(nameof(Motd));

    private static readonly Dictionary<char, IBrush> Palette = new()
    {
        ['0'] = Brush("#000000"),
        ['1'] = Brush("#0000AA"),
        ['2'] = Brush("#00AA00"),
        ['3'] = Brush("#00AAAA"),
        ['4'] = Brush("#AA0000"),
        ['5'] = Brush("#AA00AA"),
        ['6'] = Brush("#FFAA00"),
        ['7'] = Brush("#AAAAAA"),
        ['8'] = Brush("#555555"),
        ['9'] = Brush("#5555FF"),
        ['a'] = Brush("#55FF55"),
        ['b'] = Brush("#55FFFF"),
        ['c'] = Brush("#FF5555"),
        ['d'] = Brush("#FF55FF"),
        ['e'] = Brush("#FFFF55"),
        ['f'] = Brush("#FFFFFF"),
    };

    /// <summary>浅色主题色表：与 <see cref="Palette"/> 一一对应，压深到白底 ≥ 4.5:1。</summary>
    private static readonly Dictionary<char, IBrush> LightPalette = new()
    {
        ['0'] = Brush("#0F1724"),
        ['1'] = Brush("#1A1AB8"),
        ['2'] = Brush("#0B6B2E"),
        ['3'] = Brush("#00706E"),
        ['4'] = Brush("#A31212"),
        ['5'] = Brush("#8E1A8E"),
        ['6'] = Brush("#8A5A00"),
        ['7'] = Brush("#6B7787"),
        ['8'] = Brush("#4A5568"),
        ['9'] = Brush("#3B4BD8"),
        ['a'] = Brush("#15803D"),
        ['b'] = Brush("#006A78"),
        ['c'] = Brush("#B4231F"),
        ['d'] = Brush("#9A1F9A"),
        ['e'] = Brush("#7A5A00"),
        ['f'] = Brush("#0F1724"),
    };

    public MotdBlock()
    {
        // Runs 持有具体 Brush 实例，主题变化后必须重建，否则会一直显示旧主题的颜色。
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    static MotdBlock()
    {
        // MOTD 天然多行且不可横向溢出，默认值比每个使用点都写一遍更可靠。
        TextWrappingProperty.OverrideDefaultValue<MotdBlock>(TextWrapping.Wrap);
        MotdProperty.Changed.AddClassHandler<MotdBlock>((block, _) => block.Rebuild());
    }

    public string? Motd
    {
        get => GetValue(MotdProperty);
        set => SetValue(MotdProperty, value);
    }

    private void Rebuild()
    {
        var inlines = Inlines;
        if (inlines is null)
        {
            inlines = new InlineCollection();
            Inlines = inlines;
        }

        inlines.Clear();

        var raw = Motd;
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        Append(inlines, raw);
    }

    private void Append(InlineCollection target, string raw)
    {
        var palette = ActualThemeVariant == ThemeVariant.Light ? LightPalette : Palette;

        IBrush? brush = null;
        var bold = false;
        var italic = false;
        var underline = false;
        var strike = false;

        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            var run = new Run(buffer.ToString());

            if (brush is not null)
            {
                run.Foreground = brush;
            }

            if (bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (strike)
            {
                run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
            }
            else if (underline)
            {
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            }

            target.Add(run);
            buffer.Clear();
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (ch != '§' || i + 1 >= raw.Length)
            {
                buffer.Append(ch);
                continue;
            }

            var code = char.ToLowerInvariant(raw[i + 1]);
            i++;

            if (palette.TryGetValue(code, out var color))
            {
                Flush();
                brush = color;
                // 协议规定颜色码会顺带重置格式。
                bold = italic = underline = strike = false;
                continue;
            }

            switch (code)
            {
                case 'l':
                    Flush();
                    bold = true;
                    break;
                case 'o':
                    Flush();
                    italic = true;
                    break;
                case 'n':
                    Flush();
                    underline = true;
                    break;
                case 'm':
                    Flush();
                    strike = true;
                    break;
                case 'r':
                    Flush();
                    brush = null;
                    bold = italic = underline = strike = false;
                    break;
                case 'k':
                    // 乱码（obfuscated）效果不做视觉模拟，直接丢弃控制符。
                    Flush();
                    break;
                default:
                    // 未知控制符按协议忽略。
                    Flush();
                    break;
            }
        }

        Flush();
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
