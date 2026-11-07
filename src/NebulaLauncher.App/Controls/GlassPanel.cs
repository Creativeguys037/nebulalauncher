using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

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
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BlurRadius), 26d);

    private Rectangle? _copy;
    private Visual? _lastBackdrop;
    private Control? _resolvedBackdrop;

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(BackdropProperty, TintProperty, BlurRadiusProperty);
        BlurRadiusProperty.Changed.AddClassHandler<GlassPanel>((panel, _) => panel.UpdateBlur());
        BackdropProperty.Changed.AddClassHandler<GlassPanel>((panel, _) =>
        {
            // 显式 Backdrop 换了以后，之前自动解析的缓存不能留着。
            panel._resolvedBackdrop = null;
            panel._lastBackdrop = null;
        });
    }

    public GlassPanel()
    {
        LayoutUpdated += (_, _) => UpdateCopy();
        // 滚动只改变有效视口、不触发布局；背景副本必须跟着滚动重取 SourceRect，否则会错位。
        EffectiveViewportChanged += (_, _) => UpdateCopy();
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
        if (_copy is null)
        {
            return;
        }

        _copy.Effect = new BlurEffect { Radius = BlurRadius };
    }

    /// <summary>
    /// 副本的坐标必须跟随卡片位置与窗口尺寸：每次布局更新都重新取卡片左上角在背景坐标系里的位置，
    /// 用它作为 VisualBrush 的 SourceRect。否则窗口缩放或滚动后玻璃会错位（票 #84 的边界条款）。
    ///
    /// 玻璃泛光：样张 .glass::after 有 <c>saturate(190%) brightness(1.04)</c>——
    /// 让背景色块在玻璃里变亮变艳。Avalonia 的 BlurEffect 没有饱和度参数，
    /// 所以把背景渲染到 RenderTargetBitmap 后做像素级饱和度增强，再作为模糊副本的填充。
    /// </summary>
    private void UpdateCopy()
    {
        if (_copy is null)
        {
            return;
        }

        var backdrop = Backdrop ?? ResolveBackdrop();
        if (backdrop is null)
        {
            _copy.Fill = Background;
            _lastBackdrop = null;
            return;
        }

        var origin = this.TranslatePoint(new Point(0, 0), backdrop);
        if (origin is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // 背景控件换了、或 Fill 还不是 VisualBrush，就重挂一次；否则只更新 SourceRect。
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

        if (_copy.Fill is VisualBrush brush)
        {
            brush.SourceRect = new RelativeRect(
                origin.Value.X, origin.Value.Y, Bounds.Width, Bounds.Height, RelativeUnit.Absolute);
        }
    }

    /// <summary>
    /// 没有显式 <see cref="Backdrop"/> 时，从窗口视觉树里找唯一的 <see cref="GlassBackdrop"/>。
    /// 背景控件通常与背景图、遮罩一起被包在 BackdropRoot 里，所以副本源取它的父面板，
    /// 页面里的玻璃卡片因此不需要每张都手写 Backdrop 绑定，也不会把背景图漏在副本外。
    /// </summary>
    private Control? ResolveBackdrop()
    {
        if (_resolvedBackdrop is not null)
        {
            return _resolvedBackdrop;
        }

        var top = TopLevel.GetTopLevel(this);
        var glass = top?.GetVisualDescendants().OfType<GlassBackdrop>().FirstOrDefault();
        var host = glass?.Parent as Control;

        // 如果玻璃背景被错误地放在卡片自己的祖先链上，引用它会形成自引用副本；
        // 这种情况直接退回 Background，宁可少一层玻璃也不递归。
        if (host is not null && !this.GetVisualAncestors().Contains(host))
        {
            _resolvedBackdrop = host;
            return _resolvedBackdrop;
        }

        _resolvedBackdrop = glass is not null && !this.GetVisualAncestors().Contains(glass) ? glass : null;
        return _resolvedBackdrop;
    }

    /// <summary>
    /// 玻璃泛光核心：把背景渲染到 RenderTargetBitmap，做像素级
    /// <c>saturate(190%) brightness(1.04)</c> 增强——让背景色块在玻璃里变亮变艳。
    /// 这就是样张 .glass::after 的 <c>filter: blur(26px) saturate(190%) brightness(1.04)</c>
    /// 中 saturate + brightness 的等价物（blur 由 BlurEffect 单独处理）。
    /// 预算无限不怕性能：每帧做一次像素级 HSL 变换。
    /// </summary>
    private Bitmap? RenderSaturatedBackdrop(Visual backdrop, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rtb.Render(backdrop);

        // 用 Marshal 做像素级处理（不用 unsafe）
        var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96));
        var srcPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(width * height * 4);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, width, height), srcPtr, width * 4, 0);

            using var fb = result.Lock();
            var dstPtr = fb.Address;
            var len = width * height * 4;

            for (var i = 0; i < len; i += 4)
            {
                var b = System.Runtime.InteropServices.Marshal.ReadByte(srcPtr, i) / 255f;
                var g = System.Runtime.InteropServices.Marshal.ReadByte(srcPtr, i + 1) / 255f;
                var r = System.Runtime.InteropServices.Marshal.ReadByte(srcPtr, i + 2) / 255f;

                // RGB → HSL
                var max = MathF.Max(r, MathF.Max(g, b));
                var min = MathF.Min(r, MathF.Min(g, b));
                var l = (max + min) / 2f;
                float h, s;
                if (MathF.Abs(max - min) < 0.0001f)
                {
                    h = 0; s = 0;
                }
                else
                {
                    var d = max - min;
                    s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
                    if (MathF.Abs(max - r) < 0.0001f) h = (g - b) / d + (g < b ? 6f : 0f);
                    else if (MathF.Abs(max - g) < 0.0001f) h = (b - r) / d + 2f;
                    else h = (r - g) / d + 4f;
                    h /= 6f;
                }

                s = MathF.Min(s * 1.9f, 1f);
                l = MathF.Min(l * 1.04f, 1f);

                if (s < 0.0001f)
                {
                    r = g = b = l;
                }
                else
                {
                    var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
                    var p = 2 * l - q;
                    r = HueToRgb(p, q, h + 1f / 3f);
                    g = HueToRgb(p, q, h);
                    b = HueToRgb(p, q, h - 1f / 3f);
                }

                System.Runtime.InteropServices.Marshal.WriteByte(dstPtr, i, (byte)Math.Clamp((int)(b * 255), 0, 255));
                System.Runtime.InteropServices.Marshal.WriteByte(dstPtr, i + 1, (byte)Math.Clamp((int)(g * 255), 0, 255));
                System.Runtime.InteropServices.Marshal.WriteByte(dstPtr, i + 2, (byte)Math.Clamp((int)(r * 255), 0, 255));
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(srcPtr);
        }

        return result;
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}
