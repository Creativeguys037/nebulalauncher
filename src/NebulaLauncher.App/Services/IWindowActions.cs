namespace NebulaLauncher.Services;

/// <summary>
/// 窗口级动作。ViewModel 只依赖这个接口，不直接持有 Window。
///
/// 为什么不让页面 VM 直接操作窗口：本仓库的页面 VM 要能在没有窗口的环境里构造
/// （单测、截图模式、未来的 macOS 分支），直接引用 Window 会把 Avalonia 的桌面生命周期
/// 绑进 ViewModel。由 <see cref="WindowActionsProxy"/> 在窗口创建后挂接。
/// </summary>
public interface IWindowActions
{
    /// <summary>是否已挂接真实窗口。截图模式或无桌面生命周期时为 false，调用方应跳过动作。</summary>
    bool IsAvailable { get; }

    /// <summary>最小化主窗口。</summary>
    void Minimize();

    /// <summary>关闭主窗口。只关启动器，游戏进程不受影响。</summary>
    void Close();
}
