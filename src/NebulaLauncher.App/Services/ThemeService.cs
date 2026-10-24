using Avalonia;
using Avalonia.Styling;

namespace NebulaLauncher.Services;

/// <summary>
/// 主题应用：把设置里的 <c>dark</c> / <c>light</c> / <c>system</c> 映射成 Avalonia 的
/// <see cref="ThemeVariant"/>，再写到 <see cref="Application.RequestedThemeVariant"/>。
///
/// 为什么单独成类：
/// 1) 主题的三处入口（首帧默认、设置加载完成、设置页即时切换）需要同一套字符串到变体的映射，
///    散落在三个文件里迟早有一处写成 <c>"Dark"</c> 而另一处写成 <c>"dark"</c>；
/// 2) 控件取值走 <c>{DynamicResource ...}</c>，主题字典由 Avalonia 按 <c>RequestedThemeVariant</c>
///    自动挑选，所以这里唯一要做的就是切换变体，不需要手工替换字典；
/// 3) <c>system</c> 用 <see cref="ThemeVariant.Default"/>：跟随操作系统深浅色偏好，
///    这一层由 Avalonia 平台后端负责，启动器不自己读注册表（禁编译期 Win32）。
/// </summary>
public static class ThemeService
{
    public const string Dark = "dark";

    public const string Light = "light";

    public const string System = "system";

    /// <summary>
    /// 把任意输入收敛成三个合法值之一。非法值（拼错、旧配置、空串）一律回落 <c>dark</c>——
    /// 深色是主视觉，回落到浅色会让「设置文件坏掉」表现为大面积发白的界面。
    /// </summary>
    public static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        Light => Light,
        System => System,
        _ => Dark,
    };

    /// <summary>字符串 → 主题变体。<c>system</c> 对应 <see cref="ThemeVariant.Default"/>。</summary>
    public static ThemeVariant Resolve(string? theme) => Normalize(theme) switch
    {
        Light => ThemeVariant.Light,
        System => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    /// <summary>
    /// 当前实际生效的主题值：截图命令行 <c>--theme</c> 优先于设置文件。
    ///
    /// 截图要能对同一份演示数据反复出两套主题的图；同时设置页显示的选中项必须和实际渲染一致，
    /// 否则「浅色截图里选中的却是深色」，验收时无法判断到底是没切换还是选错了。
    /// </summary>
    public static string Effective(string? settingTheme) =>
        string.IsNullOrWhiteSpace(ScreenshotMode.Theme)
            ? Normalize(settingTheme)
            : Normalize(ScreenshotMode.Theme);

    /// <summary>
    /// 应用主题。<paramref name="application"/> 为 null 时静默返回：
    /// 设计器预览与单测里没有 <see cref="Application.Current"/>，不能因此抛异常。
    /// </summary>
    public static void Apply(string? theme, Application? application)
    {
        if (application is null)
        {
            return;
        }

        application.RequestedThemeVariant = Resolve(theme);
    }
}
