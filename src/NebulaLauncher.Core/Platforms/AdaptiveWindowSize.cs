using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Platforms;

/// <summary>
/// 侧边栏实测出来的自然尺寸（设备无关像素，与 Avalonia 的 Bounds / DesiredSize 同一单位）。
///
/// 四个值都必须来自真实布局：调用方在布局完成后量一次即可。测不出来时传 0，
/// <see cref="AdaptiveWindowSize.Compute"/> 会退回兜底下限，而不是把窗口算成 0。
/// </summary>
/// <param name="TitleBarHeight">标题栏（Logo + 皮肤入口 + 标题）的自然高度。</param>
/// <param name="NavigationHeight">主导航列表的自然高度。</param>
/// <param name="FooterHeight">底部工具区的自然高度。</param>
/// <param name="SidebarWidth">侧边栏的自然宽度（取导航与底部工具区的较大者）。</param>
public readonly record struct SidebarMeasurements(
    double TitleBarHeight,
    double NavigationHeight,
    double FooterHeight,
    double SidebarWidth);

/// <summary>算出来的窗口最小尺寸（客户区，单位与 Avalonia 的 MinWidth / MinHeight 一致）。</summary>
public readonly record struct LauncherMinimumWindowSize(int Width, int Height);

/// <summary>
/// 由「侧边栏实际内容有多高多宽」算出启动器窗口的最小尺寸。
///
/// 为什么需要它：主窗口是 <c>Grid RowDefinitions="46,*"</c>——标题栏固定 46，侧边栏内部再分
/// <c>*,Auto</c>（导航可滚、底部工具区不压）。窗口最小尺寸一旦写死成 1024×660，任何「往侧边栏里
/// 加内容」的改动都会在窄屏上把导航压掉而不是把窗口撑高：票 #40 在底部加了 3D 皮肤卡，
/// 侧边栏需要的高度涨了约 145px，660 的窗口就给不出那一段，于是主导航最后的「世界」被卡片上边缘切掉，
/// 而写死的 660 还能骗过 1024×660 的验收检查。
///
/// 这里的规则：最小高度 = 标题栏 + 导航 + 底部工具区；最小宽度 = 侧边栏宽 + 内容区最小宽。
/// 常量 <see cref="AppSettings.MinimumLauncherWindowWidth"/> / <see cref="AppSettings.MinimumLauncherWindowHeight"/>
/// 只当地板，不当地板之上的天花板——内容更长时以算出值为准。
///
/// 放在 Core 而不是 App：窗口与控件是 Avalonia 类型，测试项目不许碰（见 CONVENTIONS.md §8.2），
/// 而这套算法正是最需要回归保护的地方，所以算尺寸这一步必须是纯函数。
/// </summary>
public static class AdaptiveWindowSize
{
    /// <summary>侧边栏宽度的地板：再窄也留 228，否则导航文字会挤成折行。</summary>
    public const int SidebarMinWidth = 228;

    /// <summary>主内容区（英雄横幅 + 三个卡片 + 底部状态条）的最小宽度。</summary>
    public const int ContentMinWidth = 796;

    /// <summary>按实测的自然尺寸算出最小尺寸；任何一项不可用时退回兜底下限。</summary>
    public static LauncherMinimumWindowSize Compute(SidebarMeasurements measurements)
    {
        var height = IsUsable(measurements.TitleBarHeight)
            && IsUsable(measurements.NavigationHeight)
            && IsUsable(measurements.FooterHeight)
                ? measurements.TitleBarHeight + measurements.NavigationHeight + measurements.FooterHeight
                : double.NaN;

        var sidebarWidth = IsUsable(measurements.SidebarWidth)
            ? Math.Max(measurements.SidebarWidth, SidebarMinWidth)
            : SidebarMinWidth;

        return new LauncherMinimumWindowSize(
            Resolve(sidebarWidth + ContentMinWidth, AppSettings.MinimumLauncherWindowWidth),
            Resolve(height, AppSettings.MinimumLauncherWindowHeight));
    }

    private static bool IsUsable(double value) => double.IsFinite(value) && value > 0;

    /// <summary>
    /// 取整是向上取整：测量值常带小数，向下取整会算出比内容矮一点点的最小尺寸，
    /// 而那一点点正好就是被裁掉的那一行。
    /// </summary>
    private static int Resolve(double computed, int floor)
        => IsUsable(computed)
            ? Math.Max(floor, (int)Math.Ceiling(computed))
            : floor;
}
