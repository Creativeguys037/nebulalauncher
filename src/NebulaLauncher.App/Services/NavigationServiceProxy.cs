using System;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Services;

/// <summary>
/// <see cref="INavigationService"/> 的间接层。
///
/// 页面 ViewModel 需要在构造时拿到导航服务，但导航的真正实现在 MainWindowViewModel 里，
/// 而它的构造又需要 DI 提供的各个服务——直接互相依赖会形成循环。
/// 这里用「先注册、后挂接」打破循环：容器里放的永远是代理，窗口构造完成后把真实实现挂上来。
/// </summary>
public sealed class NavigationServiceProxy : INavigationService
{
    private INavigationService? _inner;

    public PageViewModelBase? Current => _inner?.Current;

    public event EventHandler<PageViewModelBase>? Navigated
    {
        add
        {
            if (_inner is not null)
            {
                _inner.Navigated += value;
            }
        }
        remove
        {
            if (_inner is not null)
            {
                _inner.Navigated -= value;
            }
        }
    }

    public void Attach(INavigationService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Navigate(string key) => _inner?.Navigate(key);

    public void Navigate(PageViewModelBase page) => _inner?.Navigate(page);
}
