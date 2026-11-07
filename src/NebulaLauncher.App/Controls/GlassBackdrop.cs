using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace NebulaLauncher.Controls;

/// <summary>
/// 窗口背景层：线性渐变打底 + 五层径向雾 + 三个硬边色块 + 40px 网格（票 #85 / #105）。
///
/// 这一层严格照定版样张 <c>.oil-ui/nebula-2/explore/home-glass.html</c> 的
/// <c>--bg-layers</c> / <c>body::before</c> / <c>body::after</c> 复刻：
///
///   1. <c>linear-gradient(152deg,#E8F0FF,#C9DCFF,#A8C6FF,#8FB4FF)</c> —— 底色（Background）；
///   2. <c>body::before</c> 的五层径向雾（Haze1…Haze5）：左上蓝、右上青、右下深蓝、左下白雾、中部深蓝；
///   3. <c>--bg-layers</c> 的三个硬边小圆（Blob1…Blob3）：模糊后是玻璃里能读出的「色块」；
///   4. <c>body::after</c> 的 40px 网格：蓝色 10%，径向遮罩淡出。
///
/// 顺序是关键：玻璃卡片复制这一整棵视觉树做背景副本，所以这里的层级顺序就是玻璃里透出来的顺序。
/// 位置与半径都按 1240×800 的样张坐标折算成比例，1024 档下同一块玻璃透出的颜色关系一致。
///
/// 动效：只让三块小圆做极慢的横向漂移（36 秒一循环）；关掉「背景特效」后立即静止。
/// </summary>
public class GlassBackdrop : Control
{
    /// <summary>基准画布尺寸（样张 1240×800）。所有像素坐标按它折算。</summary>
    private const double RefWidth = 1240d;
    private const double RefHeight = 800d;

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> Blob1Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Blob1));

    public static readonly StyledProperty<IBrush?> Blob2Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Blob2));

    public static readonly StyledProperty<IBrush?> Blob3Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Blob3));

    /// <summary>左上蓝雾（样张 560×400 @ 10%,4%，蓝 78%）。</summary>
    public static readonly StyledProperty<IBrush?> Haze1Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Haze1));

    /// <summary>右上青雾（480×360 @ 92%,10%，青 66%）。</summary>
    public static readonly StyledProperty<IBrush?> Haze2Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Haze2));

    /// <summary>右下深蓝雾（680×440 @ 76%,100%，深蓝 55%）。</summary>
    public static readonly StyledProperty<IBrush?> Haze3Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Haze3));

    /// <summary>左下白雾（360×280 @ 30%,74%，白 95%）：样板里它把「最近实例」压亮。</summary>
    public static readonly StyledProperty<IBrush?> Haze4Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Haze4));

    /// <summary>中部深蓝雾（300×240 @ 52%,46%，深蓝 28%）：给画面一个深色锚点。</summary>
    public static readonly StyledProperty<IBrush?> Haze5Property =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Haze5));

    /// <summary>网格线色（样张 body::after：蓝 10%，1px）。</summary>
    public static readonly StyledProperty<IBrush?> GridProperty =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(Grid));

    /// <summary>网格的径向遮罩：中心可见、边缘淡出（样张 88%×78% @ 50%,42%）。</summary>
    public static readonly StyledProperty<IBrush?> GridMaskProperty =
        AvaloniaProperty.Register<GlassBackdrop, IBrush?>(nameof(GridMask));

    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<GlassBackdrop, bool>(nameof(IsAnimated), defaultValue: true);

    private readonly DispatcherTimer _timer;
    private double _phase;

    static GlassBackdrop()
    {
        // DynamicResource 换主题时只改 Brush 实例，不重画就仍留着旧主题的色块；
        // IsAnimated 参与漂移计算，切换时也要重画到 0 偏移。
        AffectsRender<GlassBackdrop>(
            BackgroundProperty, Blob1Property, Blob2Property, Blob3Property,
            Haze1Property, Haze2Property, Haze3Property, Haze4Property, Haze5Property,
            GridProperty, GridMaskProperty, IsAnimatedProperty);
    }

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

    /// <summary>左上大色块（545,215 r210 蓝 55%）。</summary>
    public IBrush? Blob1
    {
        get => GetValue(Blob1Property);
        set => SetValue(Blob1Property, value);
    }

    /// <summary>右中色块（1090,430 r165 青 62%）。</summary>
    public IBrush? Blob2
    {
        get => GetValue(Blob2Property);
        set => SetValue(Blob2Property, value);
    }

    /// <summary>左下色块（250,640 r120 深蓝 42%）。</summary>
    public IBrush? Blob3
    {
        get => GetValue(Blob3Property);
        set => SetValue(Blob3Property, value);
    }

    /// <summary>左上蓝雾。</summary>
    public IBrush? Haze1
    {
        get => GetValue(Haze1Property);
        set => SetValue(Haze1Property, value);
    }

    /// <summary>右上青雾。</summary>
    public IBrush? Haze2
    {
        get => GetValue(Haze2Property);
        set => SetValue(Haze2Property, value);
    }

    /// <summary>右下深蓝雾。</summary>
    public IBrush? Haze3
    {
        get => GetValue(Haze3Property);
        set => SetValue(Haze3Property, value);
    }

    /// <summary>左下白雾。</summary>
    public IBrush? Haze4
    {
        get => GetValue(Haze4Property);
        set => SetValue(Haze4Property, value);
    }

    /// <summary>中部深蓝雾。</summary>
    public IBrush? Haze5
    {
        get => GetValue(Haze5Property);
        set => SetValue(Haze5Property, value);
    }

    /// <summary>网格线色。</summary>
    public IBrush? Grid
    {
        get => GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    /// <summary>网格径向遮罩。</summary>
    public IBrush? GridMask
    {
        get => GetValue(GridMaskProperty);
        set => SetValue(GridMaskProperty, value);
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

        // 1 · 线性渐变底（XAML 里是 Gradient.Canvas）。
        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(size));
        }

        // 2 · body::before 的五层径向雾，从底到顶（样张里列表靠前的在最上层，所以这里反着画）。
        DrawHaze(context, Haze5, size, 0.52, 0.46, 300d / RefWidth, 240d / RefHeight);
        DrawHaze(context, Haze4, size, 0.30, 0.74, 360d / RefWidth, 280d / RefHeight);
        DrawHaze(context, Haze3, size, 0.76, 1.00, 680d / RefWidth, 440d / RefHeight);
        DrawHaze(context, Haze2, size, 0.92, 0.10, 480d / RefWidth, 360d / RefHeight);
        DrawHaze(context, Haze1, size, 0.10, 0.04, 560d / RefWidth, 400d / RefHeight);

        // 3 · 三个硬边小圆（还是从底到顶：250 → 1090 → 545）。
        var drift = IsAnimated ? Math.Sin(_phase * Math.PI * 2) * size.Width * 0.006 : 0;
        DrawBlob(context, Blob3, size, 0.20, 0.80, 0.097, drift * 0.6);
        DrawBlob(context, Blob2, size, 0.88, 0.54, 0.133, -drift);
        DrawBlob(context, Blob1, size, 0.44, 0.27, 0.170, drift);

        // 4 · 40px 网格（body::after），径向遮罩淡出。
        DrawGrid(context, size);
    }

    /// <summary>画一层径向雾：中心是色，到椭圆边缘透明。</summary>
    private static void DrawHaze(
        DrawingContext context,
        IBrush? brush,
        Size size,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY)
    {
        if (brush is null)
        {
            return;
        }

        context.DrawEllipse(
            brush,
            null,
            new Point(centerX * size.Width, centerY * size.Height),
            radiusX * size.Width,
            radiusY * size.Height);
    }

    /// <summary>画一个硬边色块（样张里 96% 实心、4% 收边）。</summary>
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

    /// <summary>
    /// 40px 网格：竖横各一组 1px 线，40px 间距，颜色由 <see cref="Grid"/> 给（蓝 10%）。
    /// 用 <see cref="DrawingContext.PushOpacityMask"/> 套一层径向遮罩，边缘自然淡出——
    /// 这是样张 body::after 的 mask-image，直接画线做不到「中间有、四周没有」。
    /// </summary>
    private void DrawGrid(DrawingContext context, Size size)
    {
        var penBrush = Grid;
        if (penBrush is null)
        {
            return;
        }

        const double step = 40d;
        var pen = new Pen(penBrush, 1d);

        var mask = GridMask;
        if (mask is null)
        {
            DrawLines(context, size, pen, step);
            return;
        }

        using (context.PushOpacityMask(mask, new Rect(size)))
        {
            DrawLines(context, size, pen, step);
        }
    }

    private static void DrawLines(DrawingContext context, Size size, Pen pen, double step)
    {
        for (var x = step; x < size.Width; x += step)
        {
            context.DrawLine(pen, new Point(x, 0), new Point(x, size.Height));
        }

        for (var y = step; y < size.Height; y += step)
        {
            context.DrawLine(pen, new Point(0, y), new Point(size.Width, y));
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
        // 36 秒一循环；42ms 的刷新只重画背景，开销远低于重建布局。
        _phase = (_phase + 0.042 / 36.0) % 1.0;
        InvalidateVisual();
    }
}