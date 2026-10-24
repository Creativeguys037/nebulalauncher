using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using NebulaLauncher.Services;

namespace NebulaLauncher.Controls;

/// <summary>
/// 可收起的宿主：<see cref="IsExpanded"/> 变 false 时把可见宽度平滑收到 0，收起后完全不占布局空间。
///
/// 为什么不用 <c>IsVisible</c>：它是瞬切，胶囊一类的控件消失得太快会显得界面在跳。
/// 为什么不用缩放（<c>ScaleTransform</c>）：会把文字横向压扁，看起来像换了字体。
/// 这里让子元素始终保持自然尺寸，只裁剪可见宽度，视觉上像被「抽走」。
///
/// 为什么继承 <see cref="Decorator"/> 而不是 <c>ContentControl</c>：后者带模板，
/// 模板根（ContentPresenter）也必须参与排列；只排 <c>Content</c> 会让内容整个不显示。
/// Decorator 直接承载单个子元素，测量与排列的语义与这里要做的裁切一致。
///
/// 时长同样取自 <see cref="Motion"/>，关掉动效后瞬间到位。Transitions 由渲染层驱动，
/// 页面卡顿时会补间到终值而不是停在半途（与 PageHost 同一约定）。
/// </summary>
public sealed class CollapseHost : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapseHost, bool>(nameof(IsExpanded), defaultValue: true);

    /// <summary>
    /// 0..1 的可见比例。私有：对外只暴露 <see cref="IsExpanded"/>，
    /// 两套状态同时可写会让「谁覆盖谁」变成隐式约定。
    /// </summary>
    private static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<CollapseHost, double>(nameof(Progress), 1d);

    static CollapseHost()
    {
        // Progress 只用来算尺寸，不参与绘制：两个阶段都要重新走一遍。
        AffectsMeasure<CollapseHost>(ProgressProperty);
        AffectsArrange<CollapseHost>(ProgressProperty);
    }

    /// <summary>子元素上一次测量出的自然尺寸。收起时按它裁剪，而不是把子元素压窄。</summary>
    private Size _natural;

    public CollapseHost()
    {
        ClipToBounds = true;
        RebuildTransition();
        Motion.Changed += (_, _) => RebuildTransition();
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private void RebuildTransition()
    {
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = ProgressProperty,
                Duration = Motion.Dur(180),
                Easing = new CubicEaseOut(),
            },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsExpandedProperty)
        {
            return;
        }

        // 收起后不该还能点中里面的按钮。
        IsHitTestVisible = IsExpanded;
        Progress = IsExpanded ? 1d : 0d;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
        {
            return default;
        }

        // 按无限尺寸量：收起时只裁可见宽度，子元素自己不能被压窄，否则文字会变形。
        Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _natural = Child.DesiredSize;

        return new Size(_natural.Width * Progress, _natural.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 子元素始终按自然宽度排布，多出来的部分由 ClipToBounds 裁掉。
        Child?.Arrange(new Rect(0, 0, _natural.Width, finalSize.Height));

        return finalSize;
    }
}
