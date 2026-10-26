using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using NebulaLauncher.Services;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Controls;

/// <summary>
/// 侧边栏的常驻皮肤预览：缓慢自转，可以按住拖动改视角。
///
/// 画面由 CPU 软光栅写进 <see cref="WriteableBitmap"/> 再贴上来，而不是用
/// <c>DrawingContext</c> 逐面画几何 —— 皮肤是像素画，逐像素采样既准确又可测，
/// 也绕开了几十次几何 tessellation 的开销与相邻三角形接缝。
/// 背景保持透明，卡片底色与圆角交给外层 <c>Border</c>。
/// </summary>
public sealed class SkinModelView : Control, IDisposable
{
    public static readonly StyledProperty<SkinVisual?> VisualProperty =
        AvaloniaProperty.Register<SkinModelView, SkinVisual?>(nameof(Visual));

    /// <summary>每秒自转的角度。一整圈约 22 秒，慢到不分散注意力。</summary>
    private const double SpinDegreesPerSecond = 16;

    private const double DragDegreesPerPixel = 0.45;

    private const double MinPitch = -35;

    private const double MaxPitch = 55;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer _timer;

    private SkinMesh? _mesh;

    private WriteableBitmap? _bitmap;

    private uint[]? _pixels;

    private byte[]? _bgra;

    private int _pixelWidth;

    private int _pixelHeight;

    private double _renderScale = 1;

    private double _yaw = 25;

    private double _pitch = 8;

    private bool _dragging;

    private Point _lastPoint;

    private long _lastTick;

    private bool _hookedMotion;

    static SkinModelView()
    {
        AffectsRender<SkinModelView>(VisualProperty);
    }

    public SkinModelView()
    {
        // 裁剪与指针光标交给 XAML 设：本地值会盖掉样式 setter（PITFALLS 4.2）。
        _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, OnTick);
        _lastTick = Stopwatch.GetTimestamp();
    }

    /// <summary>要显示的皮肤。为 null 时什么也不画（卡片在没数据时自己显示占位）。</summary>
    public SkinVisual? Visual
    {
        get => GetValue(VisualProperty);
        set => SetValue(VisualProperty, value);
    }

    /// <summary>
    /// 停止自转、拆掉动效订阅并释放离屏位图。与离开视觉树走同一套清理（PITFALLS 2.7）：
    /// 控件离开视觉树时自动调用；之后重新进入视觉树会按新尺寸重建，所以就算外部多调一次也不会坏。
    /// </summary>
    public void Dispose()
    {
        _timer.Stop();
        DetachMotion();
        ReleaseBitmap();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VisualProperty)
        {
            _mesh = Visual?.BuildMesh();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastTick = Stopwatch.GetTimestamp();
        AttachMotion();
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        DetachMotion();
        ReleaseBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var visual = Visual;
        var mesh = _mesh;
        if (visual is null || mesh is null)
        {
            return;
        }

        var size = Bounds.Size;
        if (size.Width < 4 || size.Height < 4)
        {
            return;
        }

        EnsureBuffer(size);
        if (_bitmap is null || _pixels is null)
        {
            return;
        }

        SkinRenderer.Render(
            _pixels,
            _pixelWidth,
            _pixelHeight,
            mesh,
            new SkinOrbit(_yaw, _pitch),
            visual.Texture,
            background: 0,
            cullBackFaces: true,
            capeTexture: visual.Cape);

        UploadPixels();
        context.DrawImage(
            _bitmap,
            new Rect(0, 0, _pixelWidth, _pixelHeight),
            new Rect(size));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Visual is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _lastPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }

        var point = e.GetPosition(this);
        _yaw -= (point.X - _lastPoint.X) * DragDegreesPerPixel;
        _pitch = Math.Clamp(_pitch + ((point.Y - _lastPoint.Y) * DragDegreesPerPixel), MinPitch, MaxPitch);
        _lastPoint = point;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;

        if (!Motion.Enabled || _dragging)
        {
            return;
        }

        _yaw += SpinDegreesPerSecond * Math.Min(elapsed, 0.25);
        InvalidateVisual();
    }

    private void AttachMotion()
    {
        if (_hookedMotion)
        {
            return;
        }

        Motion.Changed += OnMotionChanged;
        _hookedMotion = true;
    }

    private void DetachMotion()
    {
        if (!_hookedMotion)
        {
            return;
        }

        Motion.Changed -= OnMotionChanged;
        _hookedMotion = false;
    }

    private void OnMotionChanged(object? sender, EventArgs e)
    {
        UpdateTimer();

        // 重新打开动效时补一帧，免得停转期间堆着的脏区域拖到下次交互才刷新。
        if (Motion.Enabled)
        {
            _lastTick = Stopwatch.GetTimestamp();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 关掉动效时把定时器也停掉：静止就是真的静止，不留一个每 33 ms 醒一次的空转。
    /// 拖动自己不依赖定时器，指针移动里会直接请求重绘。
    /// </summary>
    private void UpdateTimer()
    {
        if (!Motion.Enabled)
        {
            _timer.Stop();
            return;
        }

        if (!_timer.IsEnabled)
        {
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        }
    }

    private void EnsureBuffer(Size size)
    {
        var scale = (VisualRoot as IRenderRoot)?.RenderScaling ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));

        if (_bitmap is not null && width == _pixelWidth && height == _pixelHeight && Math.Abs(scale - _renderScale) < 0.01)
        {
            return;
        }

        ReleaseBitmap();

        _pixelWidth = width;
        _pixelHeight = height;
        _renderScale = scale;
        _pixels = new uint[width * height];
        _bgra = new byte[width * height * 4];
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96 * scale, 96 * scale),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
    }

    private void UploadPixels()
    {
        var pixels = _pixels;
        var bytes = _bgra;
        var bitmap = _bitmap;
        if (pixels is null || bytes is null || bitmap is null)
        {
            return;
        }

        var count = _pixelWidth * _pixelHeight;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var pixel = pixels[i];
            var alpha = (byte)((pixel >> 24) & 0xFF);
            var red = (byte)((pixel >> 16) & 0xFF);
            var green = (byte)((pixel >> 8) & 0xFF);
            var blue = (byte)(pixel & 0xFF);

            // WriteableBitmap 这里是预乘格式，半透明像素的色度要先按 alpha 缩放。
            if (alpha is not 0 and not 255)
            {
                red = (byte)((red * alpha) / 255);
                green = (byte)((green * alpha) / 255);
                blue = (byte)((blue * alpha) / 255);
            }

            bytes[offset] = blue;
            bytes[offset + 1] = green;
            bytes[offset + 2] = red;
            bytes[offset + 3] = alpha;
            offset += 4;
        }

        using var frame = bitmap.Lock();
        var rowBytes = _pixelWidth * 4;
        for (var y = 0; y < _pixelHeight; y++)
        {
            Marshal.Copy(bytes, y * rowBytes, IntPtr.Add(frame.Address, y * frame.RowBytes), rowBytes);
        }
    }

    private void ReleaseBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _pixels = null;
        _bgra = null;
        _pixelWidth = 0;
        _pixelHeight = 0;
    }
}
