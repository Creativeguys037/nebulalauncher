using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 控件示例页：14 项控件 × 六态 × 双主题的复核入口。
///
/// 这张票交付后保留：后续外壳、页面改造都在这里先看控件长什么样再落页面，
/// 避免每改一个页面就重新拼一遍「按钮 + 输入框 + 页签」的最小样张。
/// 页面不在侧边栏导航里露出（它不属于 7 个一级入口），只通过 <c>--page controls</c> 进入。
/// </summary>
public sealed partial class ControlsGalleryViewModel : PageViewModelBase
{
    /// <summary>页面级对话框的开关。示例页只演示「打开 → 首焦点落在主行动」。</summary>
    [ObservableProperty]
    private bool _isDialogOpen;

    public override string Title => "控件示例";

    public override string Key => NavigationKeys.Controls;

    public override string? Subtitle => "14 项控件 · 六态 · 双主题复核";

    public override Task OnNavigatedToAsync() => Task.CompletedTask;

    [RelayCommand]
    private void OpenDialog() => IsDialogOpen = true;

    [RelayCommand]
    private void CloseDialog() => IsDialogOpen = false;
}
