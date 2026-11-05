using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;
using NebulaLauncher.Services;

namespace NebulaLauncher.Controls;

/// <summary>
/// 页面宿主：Content 变化时播放 220ms 淡入，不做整页位移（票 #85）。
///
/// 用 Transitions 而不是 Animation.RunAsync，因为 Transitions 由渲染层驱动，
/// 不会被 UI 线程上的同步调用打断；页面加载卡顿时动画仍会补间到终值而不是卡在中途。
/// </summary>
public sealed class PageHost : ContentControl
{
    public PageHost()
    {
        ClipToBounds = true;
        RebuildTransitions();
        Motion.Changed += (_, _) => RebuildTransitions();
    }

    private void RebuildTransitions()
    {
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = Motion.Dur(Motion.Normal),
                Easing = Motion.EaseOut,
            },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ContentProperty)
        {
            return;
        }

        if (Content is null || !Motion.Enabled)
        {
            Opacity = 1;
            return;
        }

        // 先瞬移到起始态（此时 Transitions 会尝试补间，所以先摘掉再设）。
        var transitions = Transitions;
        Transitions = null;
        Opacity = 0;
        Transitions = transitions;

        // 等一次渲染后再设终值，Transitions 才会真正产生插值。
        Dispatcher.UIThread.Post(
            () => Opacity = 1,
            DispatcherPriority.Render);
    }
}
