using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace NebulaLauncher.Controls;

/// <summary>
/// 环形进度指示器。用自绘 arc 实现，避免为了一个进度环引入第三方控件库。
/// 支持不确定态（<see cref="IsIndeterminate"/>）：缺口绕圈旋转。
/// </summary>
public sealed class RingProgress : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(Value), 0.0);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(Thickness), 6.0);

    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<RingProgress, IBrush?>(nameof(ProgressBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingProgress, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<RingProgress, string?>(nameof(Text));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(FontSize), 14.0);

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<RingProgress, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<RingProgress, IBrush?>(nameof(TextBrush));

    static RingProgress()
    {
        AffectsRender<RingProgress>(
            ValueProperty,
            ThicknessProperty,
            ProgressBrushProperty,
            TrackBrushProperty,
            TextProperty,
            FontSizeProperty,
            IsIndeterminateProperty);
        AffectsMeasure<RingProgress>(ThicknessProperty, FontSizeProperty);
    }

    public RingProgress()
    {
        ProgressBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#4C6FFF"), 0),
                new GradientStop(Color.Parse("#A855F7"), 1),
            },
        };

        ApplyThemeDefaults();

        // 轨道色是代码里的本地值，样式 setter 的优先级低于它，只能自己在主题切换时重算。
        ActualThemeVariantChanged += (_, _) => ApplyThemeDefaults();
    }

    /// <summary>
    /// 轨道底色按主题取：深色底用半透明白（原值 #1FFFFFFF），浅色底用墨色 8%（#240F1724）。
    /// 不跟随主题的话，白色轨道在浅色卡片上整圈消失，只剩一段进度弧。
    /// </summary>
    private void ApplyThemeDefaults()
    {
        TrackBrush = new SolidColorBrush(ActualThemeVariant == ThemeVariant.Light
            ? Color.Parse("#240F1724")
            : Color.Parse("#1FFFFFFF"));
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? ProgressBrush
    {
        get => GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>不确定态的旋转角度，由外部动画或定时器写入。</summary>
    public double SpinAngle { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? 64
            : availableSize.Width;
        if (double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height))
        {
            side = Math.Max(side, 64);
        }
        else
        {
            side = Math.Min(side, availableSize.Height);
        }

        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var thickness = Math.Max(1, Thickness);
        var radius = (size - thickness) / 2.0;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is not null)
        {
            context.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);
        }

        var ratio = ComputeRatio();

        if (IsIndeterminate)
        {
            // 240° 的弧在圆周上旋转。
            var start = SpinAngle % 360.0;
            DrawArc(context, center, radius, thickness, start, 240.0);
            return;
        }

        if (ratio <= 0 || ProgressBrush is null)
        {
            DrawText(context, center);
            return;
        }

        // 从 12 点方向顺时针增长：-90° 起点。
        DrawArc(context, center, radius, thickness, -90, ratio * 360.0);
        DrawText(context, center);
    }

    private double ComputeRatio()
    {
        var min = Minimum;
        var max = Maximum;
        if (max <= min)
        {
            return 0;
        }

        return Math.Clamp((Value - min) / (max - min), 0, 1);
    }

    private void DrawArc(DrawingContext context, Point center, double radius, double thickness, double startDeg, double sweepDeg)
    {
        if (sweepDeg <= 0.01 || ProgressBrush is null)
        {
            return;
        }

        var points = Math.Max(6, (int)(sweepDeg / 3));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i <= points; i++)
            {
                var angle = (startDeg + sweepDeg * i / points) * Math.PI / 180.0;
                var p = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
                if (i == 0)
                {
                    ctx.BeginFigure(p, isFilled: false);
                }
                else
                {
                    ctx.LineTo(p);
                }
            }

            ctx.EndFigure(false);
        }

        var pen = new Pen(ProgressBrush, thickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawText(DrawingContext context, Point center)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var formatted = new FormattedText(
            Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            FontSize,
            TextBrush ?? Brushes.White);

        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }
}
