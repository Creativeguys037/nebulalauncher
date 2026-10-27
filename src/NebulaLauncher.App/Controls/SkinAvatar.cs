using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NebulaLauncher.Controls;

/// <summary>
/// Minecraft 皮肤头像：从整张 64×64 皮肤纹理里裁出 8×8 的脸，再叠一层帽子层。
///
/// 头像按 PCL 的口径铺满控件、呈完整方形：不做圆形或椭圆裁剪，圆角若要有也只能来自外层 Border，
/// 而三处调用点的 Border 都是直角，所以这里的 8×8 脸就是整块方形像素格。
///
/// 两层几何照抄 PCL 的 MySkin：底图取头部 UV <c>(8,8)</c>，帽子层取 <c>(40,8)</c>，
/// 帽子层叠在底图之上（它大部分是透明像素，只有头发与鬓角不透明）。
/// </summary>
public sealed class SkinAvatar : Control
{
    /// <summary>头部底图的 UV 原点与边长（皮肤纹理像素坐标）。</summary>
    private static readonly Rect HeadSource = new(8, 8, 8, 8);

    /// <summary>帽子层（第二层皮肤）的 UV 原点与边长。</summary>
    private static readonly Rect HatSource = new(40, 8, 8, 8);

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<SkinAvatar, Bitmap?>(nameof(Source));

    /// <summary>整张皮肤纹理。为空时控件什么都不画，外层占位色会透出来。</summary>
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    static SkinAvatar()
    {
        AffectsRender<SkinAvatar>(SourceProperty);
    }

    public SkinAvatar()
    {
        // 8×8 像素被放大到几十像素：必须最近邻，否则整张脸糊成色块（PCL 同样用 NearestNeighbor）。
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (Source is not { } bitmap)
        {
            return;
        }

        // 铺满整块控件：8×8 的脸与 8×8 的帽子层各自缩放到 Bounds，合起来就是完整的方形头像。
        var destination = new Rect(size);
        context.DrawImage(bitmap, HeadSource, destination);
        context.DrawImage(bitmap, HatSource, destination);
    }
}
