using System;
using Avalonia.Controls;

namespace NebulaLauncher.Services;

/// <summary>
/// <see cref="IWindowActions"/> 的间接层。与 <see cref="NavigationServiceProxy"/> 同一套路：
/// 容器里先放代理，<c>App.OnFrameworkInitializationCompleted</c> 建好窗口后再挂接真实窗口。
/// </summary>
public sealed class WindowActionsProxy : IWindowActions
{
    private Window? _window;

    public bool IsAvailable => _window is not null;

    public void Attach(Window window) => _window = window ?? throw new ArgumentNullException(nameof(window));

    public void Minimize()
    {
        if (_window is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    public void Close() => _window?.Close();
}
