using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace NebulaLauncher.Controls;

/// <summary>
/// 星云背景：底色叠加若干缓慢漂移的径向光斑。
///
/// 用 <see cref="DispatcherTimer"/> 驱动相位而不是 XAML 动画，原因：
/// 1) 光斑位置依赖控件实际尺寸，纯 XAML 难以表达；
/// 2) 需要能被「减少动态效果」设置一次性关停，避免低端机持续占用；
/// 3) 24fps 足够——光斑单周期 27–43 秒，肉眼无法分辨帧率差异。
///
/// 主题：本控件整块盖住窗口背景，所以必须跟随 <see cref="StyledElement.ActualThemeVariant"/>。
/// 深色用近黑的蓝底，浅色用冷白到淡雾蓝的底；光斑 alpha 在浅色下要压到十分之一，
/// 否则高饱和青紫铺在白底上会变成刺眼的色块（见 docs/DESIGN.md §1「微光」）。
/// </summary>
public sealed class AuroraBackground : Control
{
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<AuroraBackground, bool>(nameof(IsAnimated), defaultValue: true);

    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<AuroraBackground, double>(nameof(Intensity), defaultValue: 1.0);

    /// <summary>深色底：顶部比底部更亮的底色，模拟大气散射。</summary>
    private static readonly Color DarkBaseTop = Color.Parse("#0A0E17");
    private static readonly Color DarkBaseBottom = Color.Parse("#05070C");

    /// <summary>浅色底：冷白 → 淡雾蓝，两端都避开纯白，卡片才能靠白底浮起来。</summary>
    private static readonly Color LightBaseTop = Color.Parse("#FAFBFF");
    private static readonly Color LightBaseBottom = Color.Parse("#E8EDF7");

    /// <summary>深色主题的顶部环境光 alpha（0–255）。</summary>
    private const double DarkTopGlowAlpha = 34;

    /// <summary>浅色主题的环境光很淡：白底上再叠加蓝光只需要一丝氛围。</summary>
    private const double LightTopGlowAlpha = 10;

    private static readonly Blob[] DarkBlobs =
    [
        // 左上主光：品牌蓝，负责主色调
        new Blob("#4C6FFF", 0.26, 0.28, 0.56, 0.34, 0.13, 0f),
        // 右下副光：紫，制造纵深
        new Blob("#A855F7", 0.80, 0.74, 0.50, 0.30, 0.11, 0.55f),
        // 顶部中偏右：青，把上部从纯黑里拉出来
        new Blob("#22D3EE", 0.55, 0.00, 0.46, 0.26, 0.17, 0.28f),
        // 左下补光：深蓝，避免左下角死黑
        new Blob("#3B82F6", 0.10, 0.92, 0.40, 0.20, 0.09, 0.76f),
    ];

    // 位置与深色版一致，只把 alpha 压到深色的 ~1/3：白底上 0.09 已是明显的色雾。
    private static readonly Blob[] LightBlobs =
    [
        new Blob("#3B5AE0", 0.26, 0.28, 0.56, 0.11, 0.13, 0f),
        new Blob("#7C3AED", 0.80, 0.74, 0.50, 0.09, 0.11, 0.55f),
        new Blob("#0891B2", 0.55, 0.00, 0.46, 0.08, 0.17, 0.28f),
        new Blob("#3B82F6", 0.10, 0.92, 0.40, 0.07, 0.09, 0.76f),
    ];

    private readonly DispatcherTimer _timer;
    private double _phase;

    public AuroraBackground()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(42), DispatcherPriority.Background, OnTick);

        // 主题切换后必须立刻重画：底色与光斑都写死在绘制里，不走资源系统。
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    /// <summary>0–1，整体光斑透明度系数。</summary>
    public double Intensity
    {
        get => GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimatedProperty)
        {
            SyncTimer();
        }
    }

    private void SyncTimer()
    {
        if (IsAnimated && VisualRoot is not null)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 27 秒一循环，分辨率足够小就不会跳帧。
        _phase = (_phase + 0.042 / 27.0) % 1.0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var rect = new Rect(size);
        var isLight = ActualThemeVariant == ThemeVariant.Light;

        var baseBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(isLight ? LightBaseTop : DarkBaseTop, 0),
                new GradientStop(isLight ? LightBaseBottom : DarkBaseBottom, 1),
            },
        };
        context.FillRectangle(baseBrush, rect);

        var intensity = Math.Clamp(Intensity, 0, 1);
        if (intensity <= 0.001)
        {
            return;
        }

        // 高度/宽度比：让径向渐变在非正方形窗口里仍接近圆形。
        var aspect = size.Height / size.Width;

        foreach (var blob in isLight ? LightBlobs : DarkBlobs)
        {
            var (cx, cy) = blob.Sample(_phase);
            var alpha = (byte)Math.Clamp(blob.Alpha * intensity * 255.0, 0, 255);
            var core = Color.FromArgb(alpha, blob.Color.R, blob.Color.G, blob.Color.B);
            var edge = Color.FromArgb(0, blob.Color.R, blob.Color.G, blob.Color.B);

            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(cx, cy, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(cx, cy, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(blob.Radius, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(blob.Radius * aspect, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(core, 0),
                    new GradientStop(edge, 1),
                },
            };

            context.FillRectangle(brush, rect);
        }

        // 顶部环境光：模拟来自上方的漫射光，避免标题栏区域纯黑 / 纯白，
        // 同时给下方留出暗部（浅色主题下是留白）以维持对比度。
        var glowColor = isLight ? Color.Parse("#3B5AE0") : Color.Parse("#4C6FFF");
        var glowAlpha = isLight ? LightTopGlowAlpha : DarkTopGlowAlpha;
        var topGlow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0.42, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(glowAlpha * intensity), glowColor.R, glowColor.G, glowColor.B), 0),
                new GradientStop(Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B), 1),
            },
        };
        context.FillRectangle(topGlow, rect);
    }

    /// <summary>一个光斑的静态参数与运动轨迹。</summary>
    private readonly record struct Blob(
        Color Color,
        double BaseX,
        double BaseY,
        double Radius,
        double Alpha,
        double DriftX,
        double PhaseOffset)
    {
        public Blob(string hex, double baseX, double baseY, double radius, double alpha, double driftX, double phaseOffset)
            : this(Color.Parse(hex), baseX, baseY, radius, alpha, driftX, phaseOffset)
        {
        }

        public (double X, double Y) Sample(double phase)
        {
            var t = (phase + PhaseOffset) * Math.PI * 2;
            return (
                BaseX + Math.Sin(t) * DriftX,
                BaseY + Math.Cos(t * 0.7) * DriftX * 0.6);
        }
    }
}
