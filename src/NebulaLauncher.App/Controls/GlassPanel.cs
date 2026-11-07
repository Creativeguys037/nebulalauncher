using System;
using System.Linq;
using System.Runtime.InteropServices;
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
/// 传进来，把它渲染到 <see cref="RenderTargetBitmap"/>，在卡片坐标系里裁出同一块背景，
/// 做完色彩增强再模糊。这样玻璃里透出的就是「卡片所在位置的那块背景」，不是凭空的一层半透明色。
///
/// 玻璃泛光（票 #105）：样张 .glass::after 是
/// <c>filter: blur(26px) saturate(190%) brightness(1.04)</c>——背景色块在玻璃里变亮变艳。
/// Avalonia 的 <see cref="BlurEffect"/> 没有饱和度参数，所以在位图上用 CSS 标准饱和度矩阵
/// 做等价的 <c>saturate(190%) brightness(1.04)</c>，blur 仍由 <see cref="BlurEffect"/> 承担。
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

    /// <summary>
    /// 副本层模糊半径。Avalonia 的 Radius 与 CSS blur 不是同一标度：样张 blur(26px) 在浏览器里
    /// 色块几乎晕成色晕，Avalonia 同值还能看出圆边，所以按视觉放大到 36。
    /// </summary>
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<GlassPanel, double>(nameof(BlurRadius), 120d);

    /// <summary>
    /// 背景副本的外扩边距：模糊会把副本边缘抹淡，向外扩 48px 让淡出发生在卡片之外、
    /// 被 PART_Clip 裁掉——等价样张 .glass::after 的 <c>inset:-48px</c>。
    /// </summary>
    private const double CopyPad = 48d;

    /// <summary>样张 .glass::after 的 <c>saturate(190%)</c>。</summary>
    private const float SaturateAmount = 1.9f;

    /// <summary>样张 .glass::after 的 <c>brightness(1.04)</c>。</summary>
    private const float BrightnessAmount = 1.04f;

    /// <summary>
    /// 背景原始快照（未增强）：整棵背景树只渲染一次，所有玻璃卡片共用。
    /// 背景动画 36 秒才漂移 0.6% 宽度，快照 300ms 的新鲜度足够跟上肉眼。
    /// </summary>
    private static RenderTargetBitmap? _snapshot;
    private static Visual? _snapshotSource;
    private static PixelSize _snapshotSize;
    private static DateTime _snapshotAtUtc;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMilliseconds(300);

    private Rectangle? _copy;
    private Control? _resolvedBackdrop;
    private Visual? _copySource;
    private Rect _copyRegion;
    private bool _copyValid;

    static GlassPanel()
    {
        AffectsRender<GlassPanel>(BackdropProperty, TintProperty, BlurRadiusProperty);
        BlurRadiusProperty.Changed.AddClassHandler<GlassPanel>((panel, _) => panel.UpdateBlur());
        BackdropProperty.Changed.AddClassHandler<GlassPanel>((panel, _) =>
        {
            // 显式 Backdrop 换了以后，之前自动解析的缓存不能留着。
            panel._resolvedBackdrop = null;
            panel._copySource = null;
            panel._copyValid = false;
        });
    }

    public GlassPanel()
    {
        LayoutUpdated += (_, _) => UpdateCopy();
        // 滚动只改变有效视口、不触发布局；背景副本必须跟着滚动重取裁剪区域，否则会错位。
        EffectiveViewportChanged += (_, _) => UpdateCopy();
        // 主题切换换掉的是 Brush 值，不触发布局；主动让快照失效，下次更新重建副本。
        ActualThemeVariantChanged += (_, _) => _copyValid = false;
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
        _copySource = null;
        _copyValid = false;
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
    /// 用它决定从背景快照里裁哪一块。否则窗口缩放或滚动后玻璃会错位（票 #84 的边界条款）。
    ///
    /// 副本区域向外扩 <see cref="CopyPad"/>：模糊把边缘抹淡的部分落在卡片之外，被圆角裁剪吃掉。
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
            _copySource = null;
            _copyValid = false;
            return;
        }

        var origin = this.TranslatePoint(new Point(0, 0), backdrop);
        if (origin is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // 副本区域（背景坐标系）：卡片向外扩 CopyPad，再与背景范围求交——
        // 靠窗口边缘的卡片裁出的是有效交集，不足的部分本来就在窗口外。
        var want = new Rect(
            origin.Value.X - CopyPad,
            origin.Value.Y - CopyPad,
            Bounds.Width + (CopyPad * 2),
            Bounds.Height + (CopyPad * 2));
        var region = want.Intersect(new Rect(backdrop.Bounds.Size));
        if (region.Width <= 1 || region.Height <= 1)
        {
            return;
        }

        // 卡片没动、快照还新鲜就不用重做；滚动、缩放、主题切换都会走到重建。
        if (_copyValid
            && ReferenceEquals(_copySource, backdrop)
            && _copyRegion == region
            && DateTime.UtcNow - _snapshotAtUtc < SnapshotTtl)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var bitmap = RenderSaturatedCopy(backdrop, region, scaling);
        if (bitmap is null)
        {
            return;
        }

        // 副本矩形对齐到交集区域在卡片坐标系里的位置（Canvas 定位，不参与父测量）。
        Canvas.SetLeft(_copy, region.X - origin.Value.X);
        Canvas.SetTop(_copy, region.Y - origin.Value.Y);
        _copy.Width = region.Width;
        _copy.Height = region.Height;
        _copy.Fill = new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };

        _copySource = backdrop;
        _copyRegion = region;
        _copyValid = true;
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
    /// 玻璃泛光核心：把背景快照裁出本卡片的区域，做 CSS 标准饱和度矩阵
    /// <c>saturate(190%) brightness(1.04)</c>——让背景色块在玻璃里变亮变艳。
    /// 这就是样张 .glass::after 的 <c>filter: blur(26px) saturate(190%) brightness(1.04)</c>
    /// 中 saturate + brightness 的等价物（blur 由 BlurEffect 单独处理）。
    /// </summary>
    private static WriteableBitmap? RenderSaturatedCopy(Visual backdrop, Rect regionDip, double scaling)
    {
        var snapshot = GetSnapshot(backdrop, scaling);
        if (snapshot is null)
        {
            return null;
        }

        // DIP → 像素，再与快照范围求交（round 误差兜底）。
        var rect = new PixelRect(
            (int)Math.Round(regionDip.X * scaling),
            (int)Math.Round(regionDip.Y * scaling),
            Math.Max(1, (int)Math.Round(regionDip.Width * scaling)),
            Math.Max(1, (int)Math.Round(regionDip.Height * scaling)));
        rect = rect.Intersect(new PixelRect(0, 0, snapshot.PixelSize.Width, snapshot.PixelSize.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var buffer = new byte[rect.Width * rect.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            // CopyPixels(源矩形, 缓冲区, 缓冲区字节数, 行距)：行距是每行 Width*4 字节。
            snapshot.CopyPixels(rect, handle.AddrOfPinnedObject(), buffer.Length, rect.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        ApplySaturateBrightness(buffer);

        var bitmap = new WriteableBitmap(
            new PixelSize(rect.Width, rect.Height), new Vector(96 * scaling, 96 * scaling));
        using var fb = bitmap.Lock();
        Marshal.Copy(buffer, 0, fb.Address, buffer.Length);
        return bitmap;
    }

    /// <summary>
    /// 背景原始快照：整棵背景树渲染一次，300ms 内所有卡片共用。
    /// 背景动画（36 秒漂移 0.6% 宽度）在这个新鲜度内位移不到 1px，看不出副本滞后。
    /// </summary>
    private static RenderTargetBitmap? GetSnapshot(Visual backdrop, double scaling)
    {
        var dip = backdrop.Bounds.Size;
        if (dip.Width <= 0 || dip.Height <= 0)
        {
            return null;
        }

        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(dip.Width * scaling)),
            Math.Max(1, (int)Math.Round(dip.Height * scaling)));

        if (_snapshot is not null
            && ReferenceEquals(_snapshotSource, backdrop)
            && _snapshotSize == size
            && DateTime.UtcNow - _snapshotAtUtc < SnapshotTtl)
        {
            return _snapshot;
        }

        var rtb = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        rtb.Render(backdrop);
        _snapshot = rtb;
        _snapshotSource = backdrop;
        _snapshotSize = size;
        _snapshotAtUtc = DateTime.UtcNow;
        return rtb;
    }

    /// <summary>
    /// CSS filter 的 saturate 与 brightness 就是线性矩阵：
    /// <c>saturate(s)</c> 按 Rec.709 亮度混合通道，<c>brightness(b)</c> 整体乘 b。
    /// 两个矩阵相乘后逐像素套用，通道序是 BGRA。
    /// </summary>
    private static void ApplySaturateBrightness(byte[] bgra)
    {
        const float s = SaturateAmount;
        const float b = BrightnessAmount;

        var m00 = (0.213f + (0.787f * s)) * b;
        var m01 = (0.715f - (0.715f * s)) * b;
        var m02 = (0.072f - (0.072f * s)) * b;
        var m10 = (0.213f - (0.213f * s)) * b;
        var m11 = (0.715f + (0.285f * s)) * b;
        var m12 = (0.072f - (0.072f * s)) * b;
        var m20 = (0.213f - (0.213f * s)) * b;
        var m21 = (0.715f - (0.715f * s)) * b;
        var m22 = (0.072f + (0.928f * s)) * b;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            var bb = bgra[i] / 255f;
            var gg = bgra[i + 1] / 255f;
            var rr = bgra[i + 2] / 255f;

            var nr = (m00 * rr) + (m01 * gg) + (m02 * bb);
            var ng = (m10 * rr) + (m11 * gg) + (m12 * bb);
            var nb = (m20 * rr) + (m21 * gg) + (m22 * bb);

            bgra[i] = (byte)Math.Clamp((int)((nb * 255f) + 0.5f), 0, 255);
            bgra[i + 1] = (byte)Math.Clamp((int)((ng * 255f) + 0.5f), 0, 255);
            bgra[i + 2] = (byte)Math.Clamp((int)((nr * 255f) + 0.5f), 0, 255);
        }
    }
}

