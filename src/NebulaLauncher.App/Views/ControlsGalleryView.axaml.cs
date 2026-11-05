using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using NebulaLauncher.Services;

namespace NebulaLauncher.Views;

/// <summary>
/// 控件示例页的代码后置：只负责「切换演示状态」。
///
/// 为什么用 code-behind 而不是 14 个 ViewModel 属性：演示态是纯视觉的瞬时状态，
/// 不进业务模型；页面本身也不承载任何可保存状态。真实页面只消费控件的正式样式，
/// 不会用到这里的 demo-* 类。
///
/// 截图模式没有指针，点不了状态按钮，所以 <c>--section hover|pressed|disabled|loading|focus</c>
/// 会在加载后直接摆出对应状态，供双主题逐项目视。
/// </summary>
public partial class ControlsGalleryView : UserControl
{
    private static readonly string[] DemoClasses = ["demo-hover", "demo-pressed", "demo-disabled", "loading"];

    public ControlsGalleryView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (ScreenshotMode.SectionKey is { Length: > 0 } section)
        {
            ApplyState(section);
        }
    }

    private void OnStateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string state })
        {
            ApplyState(state);
        }
    }

    private void ApplyState(string state)
    {
        foreach (var control in DemoControls())
        {
            foreach (var demoClass in DemoClasses)
            {
                control.Classes.Remove(demoClass);
            }

            control.IsEnabled = true;

            switch (state)
            {
                case "hover":
                    control.Classes.Add("demo-hover");
                    break;
                case "pressed":
                    control.Classes.Add("demo-pressed");
                    break;
                case "disabled":
                    control.IsEnabled = false;
                    // Border / GlassPanel 这类非输入控件没有 :disabled 伪类，用演示类表达同一状态。
                    if (control is not InputElement)
                    {
                        control.Classes.Add("demo-disabled");
                    }
                    break;
                case "loading":
                    if (control is Button)
                    {
                        control.Classes.Add("loading");
                    }
                    break;
                case "focus":
                    control.Focus(NavigationMethod.Tab);
                    break;
            }
        }

        var hint = this.FindControl<TextBlock>("StateHint");
        if (hint is not null)
        {
            hint.Text = state switch
            {
                "default" => "default：不加速度、不伪类。",
                "hover" => "hover：上浮 1px、阴影扩大（卡片 3px），150ms。",
                "pressed" => "pressed：底色压深一档、Opacity 0.9，90ms。",
                "disabled" => "disabled：40% 不透明、Cursor=Arrow、无阴影。",
                "loading" => "loading：内容换 14px 进度环、外框尺寸不变（仅 Button 支持）。",
                "focus" => "focus：2px 主色环，键盘可见、鼠标点击不显示（按 Tab 复核）。",
                _ => string.Empty,
            };
        }
    }

    private List<Control> DemoControls() =>
        this.GetLogicalDescendants()
            .OfType<Control>()
            .Where(control => control.Tag as string == "demo")
            .ToList();
}
