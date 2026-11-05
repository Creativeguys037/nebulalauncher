using System;
using Avalonia.Animation.Easings;

namespace NebulaLauncher.Services;

/// <summary>
/// 全局动效开关与动效词表。关闭后所有转场时长为 0，用于「减少动态效果」偏好与低端机。
/// 由设置页写入，页面与控件只读。
///
/// 时长与缓动是唯一来源：XAML 用 <c>{x:Static services:Motion.Fast}</c> 这类引用，
/// 页面与控件不写 <c>0:0:0.15</c> 字面量。数值与 <c>Themes/Tokens.axaml</c> 的
/// Motion.* 令牌保持同值（90 / 150 / 220 / 320）。
///
/// 注意：<c>{x:Static}</c> 取的是静态词表，不经过 <see cref="Dur(TimeSpan)"/>；
/// 全局「关闭动效」的运行时切换由后续「全局三态、无障碍与动效关闭」票统一处理。
/// </summary>
public static class Motion
{
    /// <summary>按下、勾选。</summary>
    public static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(90);

    /// <summary>hover、图标变色。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(150);

    /// <summary>状态切换、展开。</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(220);

    /// <summary>页面转场。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(320);

    /// <summary>
    /// 全部 UI 动效默认缓动：快出慢收 <c>cubic-bezier(.22, .9, .24, 1)</c>。
    /// Avalonia 11.3 起 <c>CubicBezierEasing</c> 已废弃（不再实现 <see cref="Easing"/>），
    /// 同语义的可用类型是 <see cref="SplineEasing"/>，控制点写法不变。
    /// </summary>
    public static readonly Easing EaseOut = new SplineEasing(0.22, 0.9, 0.24, 1);

    /// <summary>循环与装饰动画的缓动：<c>cubic-bezier(.4, 0, .2, 1)</c>。</summary>
    public static readonly Easing EaseInOut = new SplineEasing(0.4, 0, 0.2, 1);

    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? Changed;

    /// <summary>关闭动效时返回 <see cref="TimeSpan.Zero"/>，调用方无需再判断开关。</summary>
    public static TimeSpan Dur(TimeSpan normal) => _enabled ? normal : TimeSpan.Zero;

    public static TimeSpan Dur(int milliseconds) => _enabled ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;
}
