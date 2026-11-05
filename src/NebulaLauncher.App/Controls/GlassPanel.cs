using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NebulaLauncher.Controls;

/// <summary>
/// 玻璃面板：三层结构——背景副本（按卡片位置做同源偏移 + 圆角裁剪 + <see cref="BlurEffect"/>）、
/// 半透明色调层、内容层。
///
/// 为什么不是 CSS 的 backdrop-filter：Avalonia 11.3 的 <see cref="BlurEffect"/> 只作用于
/// 元素自身的绘制结果，读不到背后兄弟控件的像素。所以这里把页面背景当作 <see cref="Backdrop"/>
/// 传进来，用 <see cref="VisualBrush"/> 在卡片坐标系里重画同一块背景，再模糊副本。
/// 这样玻璃里透出的就是「卡片所在位置的那块背景」，不是凭空的一层半透明色。
///
/// 命中测试：副本层与色调层都 <c>IsHitTestVisible=false</c>，文字与描边全部在内容层，
/// 所以点击永远落在内容上，不会被副本吃掉。
/// </summary>
public class GlassPanel : ContentControl
{
    /// <summary>背景副本的源。通常是页面底层的背景控件；为空时退回 <see cref="TemplatedControl.Background"/>。</summary>
    public static readonly StyledProperty<Control?> BackdropProperty =
        AvaloniaProperty.Register<GlassPanel, Control?>(nameof(Backdrop));

    /// <summary>玻璃色调层。深色是红 6%，浅色是白 34%。</summary>
    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<GlassPanel, IBrush?>(nameof(Tint));

    /// <summary>副本层模糊半径。Avalonia 的 Radius 与 CSS blur 不是同一标度，按视觉调。</summary>
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BlurRadius), 18d);

    private Rectangle? _copy;
    private Panel? _copyLayer;
    private Visual? _lastBackdrop;

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(BackdropProperty, TintProperty, BlurRadiusProperty);
        BlurRadiusProperty.Changed.AddClassHandler<GlassPanel>((panel, _) => panel.UpdateBlur());
    }

    public GlassPanel()
    {
        LayoutUpdated += (_, _) => UpdateCopy();
    }

    public Control? Backdrop
    {
        get => GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _copy = e.NameScope.Find<Rectangle>("PART_Copy");
        _copyLayer = e.NameScope.Find<Panel>("PART_CopyLayer");
        _lastBackdrop = null;
        UpdateBlur();
        UpdateCopy();
    }

    /// <summary>
    /// 模糊必须用代码设：<see cref="BlurEffect"/> 不是 <see cref="StyledElement"/>，
    /// 模板里的 <c>{TemplateBinding BlurRadius}</c> 解析不到 TemplatedParent，Radius 会停在 0，
    /// 玻璃就退化成「清晰背景副本 + 半透明色调」。
    /// </summary>
    private void UpdateBlur()
    {
        if (_copyLayer is null)
        {
            return;
        }

        _copyLayer.Effect = new BlurEffect { Radius = BlurRadius };
    }

    /// <summary>
    /// 副本的坐标必须跟随卡片位置与窗口尺寸：每次布局更新都重新取卡片左上角在背景坐标系里的位置，
    /// 用它作为 VisualBrush 的 SourceRect。否则窗口缩放或滚动后玻璃会错位（票 #84 的边界条款）。
    /// </summary>
    private void UpdateCopy()
    {
        if (_copy is null)
        {
            return;
        }

        var backdrop = Backdrop;
        if (backdrop is null)
        {
            _copy.Fill = Background;
            _lastBackdrop = null;
            return;
        }

        if (!ReferenceEquals(_lastBackdrop, backdrop) || _copy.Fill is not VisualBrush)
        {
            _copy.Fill = new VisualBrush(backdrop)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None,
            };
            _lastBackdrop = backdrop;
        }

        if (_copy.Fill is not VisualBrush brush)
        {
            return;
        }

        var origin = this.TranslatePoint(new Point(0, 0), backdrop);
        if (origin is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        brush.SourceRect = new RelativeRect(origin.Value.X, origin.Value.Y, Bounds.Width, Bounds.Height, RelativeUnit.Absolute);
    }
}
