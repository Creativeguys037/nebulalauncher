using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 所有页面 ViewModel 的基类。页面不持有窗口引用，只通过 <see cref="Services.INavigationService"/> 跳转。
///
/// 实现 <see cref="IDisposable"/> 是为了给页面持有的 <see cref="CancellationTokenSource"/> 一个确定的释放点。
/// 这些页面是 DI 单例，正常情况下到进程退出才轮到容器释放；但没有这个契约，就没有任何一处能写清
/// 「谁负责收尾」——CA1001 抓的正是这种状态。切页**不**释放对象（页面还会被再次进入），
/// <see cref="OnNavigatedFromAsync"/> 只取消在飞的请求与退订事件。
/// </summary>
public abstract class PageViewModelBase : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>页面标题，显示在顶部栏。</summary>
    public abstract string Title { get; }

    /// <summary>标题右侧的次要说明，可为空。</summary>
    public virtual string? Subtitle => null;

    /// <summary>导航键，用于 <see cref="Services.INavigationService.Navigate(string)"/>。</summary>
    public abstract string Key { get; }

    /// <summary>进入页面时调用。耗时数据加载放这里，实现方自行负责异常处理。</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>
    /// 重新加载当前页数据（F5）。
    ///
    /// 默认语义与「重新进入页面」一致：各页面的加载逻辑都在 <see cref="OnNavigatedToAsync"/> 里，
    /// 这里只做转调，避免为 11 个页面各写一份刷新实现。页面若有更轻的刷新路径可以在自己的实现里覆写。
    /// 注意与各页面私有的 <c>RefreshAsync</c> 不冲突：那些是页面内部的命令实现，不是生命周期契约。
    /// </summary>
    public virtual Task ReloadCurrentAsync() => OnNavigatedToAsync();

    /// <summary>离开页面时调用，用于取消订阅与停止定时器。</summary>
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>页面是否已被释放。已释放的页面上再发起操作应当直接返回。</summary>
    protected bool IsDisposed => _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放页面持有的资源。子类覆写后必须调用基类实现，并把已释放的字段置 null——
    /// 那些字段在 <see cref="OnNavigatedFromAsync"/> 里还会被 <c>?.</c> 访问，
    /// 置 null 之后就是空短路，不会撞上 <see cref="ObjectDisposedException"/>。
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
        }
    }
}
