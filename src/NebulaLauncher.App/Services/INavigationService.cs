using System;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Services;

/// <summary>
/// 页面导航。ViewModel 只依赖这个接口，不接触窗口。
/// 实现由 <see cref="MainWindowViewModel"/> 提供，页面切换动画由 PageHost 负责。
/// </summary>
public interface INavigationService
{
    /// <summary>当前页面。</summary>
    PageViewModelBase? Current { get; }

    /// <summary>按导航键切换页面。键不存在时抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    void Navigate(string key);

    /// <summary>直接切换到某个页面实例（用于带参数的详情页）。</summary>
    void Navigate(PageViewModelBase page);

    /// <summary>页面切换完成后触发。</summary>
    event EventHandler<PageViewModelBase>? Navigated;
}
