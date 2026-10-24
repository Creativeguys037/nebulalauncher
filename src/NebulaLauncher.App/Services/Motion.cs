using System;

namespace NebulaLauncher.Services;

/// <summary>
/// 全局动效开关。关闭后所有转场时长为 0，用于「减少动态效果」偏好与低端机。
/// 由设置页写入，页面与控件只读。
/// </summary>
public static class Motion
{
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
