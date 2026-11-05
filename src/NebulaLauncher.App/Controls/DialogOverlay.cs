using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace NebulaLauncher.Controls;

/// <summary>
/// 页内对话框浮层：遮罩 + 居中玻璃面板 + 首焦点落在主行动。
///
/// 为什么不用 <c>Window.ShowDialog</c>：票 #84 要求「自建浮层」——
/// 模态窗口会打断导航上下文、无法跟随页面滚动，且会挡掉主窗口的动效与背景。
/// 这里用页面级浮层：遮罩层负责阻断下层点击，面板自身是 <see cref="GlassPanel"/> 的三层结构。
/// </summary>
public class DialogOverlay : ContentControl
{
    /// <summary>是否打开。关闭时整棵子树 <c>IsVisible=false</c>，不参与布局与命中测试。</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<DialogOverlay, bool>(nameof(IsOpen));

    /// <summary>打开时接收键盘焦点的控件。约定传主行动按钮。</summary>
    public static readonly StyledProperty<Control?> PrimaryActionProperty =
        AvaloniaProperty.Register<DialogOverlay, Control?>(nameof(PrimaryAction));

    static DialogOverlay()
    {
        IsOpenProperty.Changed.AddClassHandler<DialogOverlay>((dialog, _) => dialog.OnOpenChanged());
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public Control? PrimaryAction
    {
        get => GetValue(PrimaryActionProperty);
        set => SetValue(PrimaryActionProperty, value);
    }

    private void OnOpenChanged()
    {
        if (!IsOpen)
        {
            return;
        }

        // 等一帧：面板刚由不可见变可见时还没完成布局，直接 Focus 会落在尚未挂进视觉树的内容上。
        Dispatcher.UIThread.Post(() => PrimaryAction?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
    }
}
