using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace NebulaLauncher.Controls;

/// <summary>
/// 窗口背景层：主题渐变打底 + 三个硬边圆形色块（票 #85）。
///
/// 为什么是「硬边圆」而不是上一版的径向星云光斑：玻璃卡片的背景副本需要明确的色块
/// 才能在模糊后读出层次；柔光斑在缩小窗口时会糊成一片，深浅两套主题也对不上位置。
/// 色块位置按窗口比例定位，所以 1240×800 与 1024×660 下同一块玻璃透出的颜色关系一致。
///
/// 这一层同时是 <see cref="GlassPanel"/> 的背景源：窗口里把它与用户背景图、遮罩一起放进
/// <c>BackdropRoot</c> 面板，玻璃卡片用 VisualBrush 复制整个面板，再按自身位置偏移并模糊。
///
/// 动效：只有色块做极慢的横向漂移（36 秒一循环），关掉「背景特效」后立即静止；
/// 不引入逐帧重绘之外的副作用。
/// </summary>
public class GlassBackdrop : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> Blob1Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Blob1));

    public static readonly StyledProperty<IBrush?> Blob2Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Blob2));

    public static readonly StyledProperty<IBrush?> Blob3Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Blob3));

    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<GlassBackdrop, bool>(nameof(IsAnimated), defaultValue: true);

    private readonly DispatcherTimer _timer;
    private double _phase;

    public GlassBackdrop()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(42), DispatcherPriority.Background, OnTick);

        // 主题切换后色块与渐变都要重画：它们由 DynamicResource 换值，不会自动触发 Render。
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>主题渐变底（XAML 里是 Gradient.Canvas）。</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>左上大色块。</summary>
    public IBrush? Blob1
    {
        get => GetValue(Blob1Property);
        set => SetValue(Blob1Property, value);
    }

    /// <summary>右中色块。</summary>
    public IBrush? Blob2
    {
        get => GetValue(Blob2Property);
        set => SetValue(Blob2Property, value);
    }

    /// <summary>左下色块。</summary>
    public IBrush? Blob3
    {
        get => GetValue(Blob3Property);
        set => SetValue(Blob3Property, value);
    }

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
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

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // 底色走 Background（XAML 里是 Gradient.Canvas）；不调 base.Render，避免重复填一遍。
        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(size));
        }

        // 三块位置与方向卡 glass.css 的 --bg-layers 对齐（1240×800 基准折算成比例）。
        var drift = IsAnimated ? Math.Sin(_phase * Math.PI * 2) * size.Width * 0.006 : 0;
        DrawBlob(context, Blob1, size, 0.44, 0.27, 0.170, drift);
        DrawBlob(context, Blob2, size, 0.88, 0.54, 0.133, -drift);
        DrawBlob(context, Blob3, size, 0.20, 0.80, 0.097, drift * 0.6);
    }

    private static void DrawBlob(
        DrawingContext context,
        IBrush? brush,
        Size size,
        double centerX,
        double centerY,
        double radius,
        double drift)
    {
        if (brush is null)
        {
            return;
        }

        context.DrawEllipse(
            brush,
            null,
            new Point((centerX * size.Width) + drift, centerY * size.Height),
            radius * size.Width,
            radius * size.Width);
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
        // 36 秒一循环；42ms 的刷新只重画三块圆，开销远低于重建布局。
        _phase = (_phase + 0.042 / 36.0) % 1.0;
        InvalidateVisual();
    }
}
