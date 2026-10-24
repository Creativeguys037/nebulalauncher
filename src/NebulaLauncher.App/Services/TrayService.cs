using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;
using NebulaLauncher.Views;

namespace NebulaLauncher.Services;

/// <summary>
/// 托盘图标与「关闭到托盘」的运行时状态。
///
/// 为什么是窗口级服务而不是 ViewModel：托盘需要 Window、桌面生命周期与 Win32 原生菜单，
/// 这些都不该渗进页面 VM。MainWindow 只负责在 Closing 里问一句
/// <see cref="Core.Platforms.CloseBehavior.ShouldExitOnClose"/>，真正的状态都在这里。
///
/// 生命周期：<see cref="TryCreate"/> 在 <c>App.OnFrameworkInitializationCompleted</c> 建好窗口后调用一次，
/// 由 <see cref="MainWindow.Tray"/> 强引用（<see cref="TrayIcon"/> 被 GC 回收时图标会从通知区消失）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const string IconUri = "avares://NebulaLauncher/Assets/tray.png";

    private const string TrayToolTip = "Nebula Launcher";

    /// <summary>首次隐藏到托盘时的状态栏提示。只提示一次，避免每次关窗都刷一遍。</summary>
    private const string HideHint = "已最小化到托盘，右键托盘图标可退出";

    private readonly MainWindow _window;
    private readonly ISettingsService? _settings;
    private readonly IInstanceContext? _instanceContext;
    private readonly ILauncherPaths? _paths;
    private readonly IPlatformService? _platform;
    private readonly IAppLog? _log;
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop;

    private TrayIcon? _icon;
    private TrayIcons? _icons;
    private bool _hideHintShown;
    private bool _disposed;

    private TrayService(MainWindow window, IServiceProvider services)
    {
        _window = window;
        _settings = services.GetService(typeof(ISettingsService)) as ISettingsService;
        _instanceContext = services.GetService(typeof(IInstanceContext)) as IInstanceContext;
        _paths = services.GetService(typeof(ILauncherPaths)) as ILauncherPaths;
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;
        _log = services.GetService(typeof(IAppLog)) as IAppLog;
        _desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
    }

    /// <summary>托盘图标是否真的建出来了。为 false 时关闭窗口必须退回「直接退出」。</summary>
    public bool TrayAvailable => _icon is not null;

    /// <summary>设置里的「关闭到托盘」。设置服务缺失时按 false 处理，保持旧行为。</summary>
    public bool CloseToTray => _settings?.Current.CloseToTray == true;

    /// <summary>
    /// 托盘菜单已经请求退出。窗口关闭拦截必须先看这个标记，
    /// 否则 <see cref="IControlledApplicationLifetime.Shutdown"/> 触发的 Closing 会把退出自己取消掉。
    /// </summary>
    public bool ExitRequested { get; private set; }

    /// <summary>
    /// 建托盘并挂到主窗口上。截图模式返回 null——无人值守截图靠关窗口结束进程，托盘会把它拦成挂起。
    /// 托盘创建失败同样返回 null：宁可关不掉「关闭到托盘」，也不能留下没有入口的隐藏窗口。
    /// </summary>
    public static TrayService? TryCreate(MainWindow window, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(services);

        if (ScreenshotMode.IsActive)
        {
            return null;
        }

        var tray = new TrayService(window, services);

        if (!tray.CreateTrayIcon())
        {
            tray.Dispose();
            return null;
        }

        window.Tray = tray;
        return tray;
    }

    /// <summary>托盘左键单击 / 双击：把主窗口叫回前台。</summary>
    public void ShowMainWindow()
    {
        if (_disposed)
        {
            return;
        }

        // 点击回调来自原生托盘消息，统一投递到 UI 线程再动窗口。
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            if (!_window.IsVisible)
            {
                _window.Show();
            }

            _window.Activate();
        });
    }

    /// <summary>
    /// 取消关闭并把窗口藏进托盘。返回 false 表示藏不起来（托盘没建出来），调用方必须放行关闭。
    /// </summary>
    public bool HideToTray()
    {
        if (_icon is null)
        {
            return false;
        }

        try
        {
            // 设置开关可能是在窗口已打开后才改的，这里保证真的有一个图标能点回来。
            _icon.IsVisible = true;
            _window.Hide();
        }
        catch (Exception ex)
        {
            _log?.Warn("隐藏主窗口到托盘失败", ex);
            return false;
        }

        if (!_hideHintShown)
        {
            _hideHintShown = true;
            SetStatus(HideHint);
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        if (_desktop is not null)
        {
            _desktop.Exit -= OnLifetimeExit;
        }

        var icon = _icon;
        _icon = null;

        if (icon is null)
        {
            return;
        }

        icon.Clicked -= OnTrayClicked;

        try
        {
            _icons?.Remove(icon);
            icon.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Warn("释放托盘图标失败", ex);
        }
    }

    private bool CreateTrayIcon()
    {
        try
        {
            var tray = new TrayIcon
            {
                Icon = LoadWindowIcon(),
                ToolTipText = TrayToolTip,
                Menu = BuildMenu(),
                IsVisible = CloseToTray,
            };

            tray.Clicked += OnTrayClicked;

            // 托盘图标要登记到 Application 的 TrayIcons 集合：Avalonia 用这个集合管理
            // 原生托盘的挂接与随应用退出时的清理，只 new 一个 TrayIcon 不保证平台实现被接上。
            AttachToApplication(tray);

            _icon = tray;

            if (_settings is not null)
            {
                _settings.Changed += OnSettingsChanged;
            }

            if (_desktop is not null)
            {
                _desktop.Exit += OnLifetimeExit;
            }

            _log?.Info($"托盘图标已创建：{IconUri}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warn("创建托盘图标失败，关闭行为退回「直接退出」", ex);
            return false;
        }
    }

    /// <summary>PNG 先经 Skia 解码成 Bitmap 再交给 WindowIcon，避免 Win32 侧把它当 ICO 解析。</summary>
    private static WindowIcon LoadWindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri(IconUri));

        // Bitmap 由 WindowIcon 持有（图标生命周期与进程相同），这里不能 using 掉。
        var bitmap = new Bitmap(stream);
        return new WindowIcon(bitmap);
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) => ShowMainWindow();
        menu.Add(show);

        var folder = new NativeMenuItem("打开游戏目录");
        folder.Click += OnOpenGameDirectory;
        menu.Add(folder);

        menu.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("退出");
        exit.Click += OnExit;
        menu.Add(exit);

        return menu;
    }

    private void AttachToApplication(TrayIcon tray)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var icons = TrayIcon.GetIcons(app);

        if (icons is null)
        {
            icons = new TrayIcons();
            TrayIcon.SetIcons(app, icons);
        }

        icons.Add(tray);
        _icons = icons;
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnSettingsChanged(object? sender, AppSettings updated)
    {
        // 设置可能在后台线程保存完再通知，切回 UI 线程再碰托盘对象。
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _icon is null)
            {
                return;
            }

            try
            {
                _icon.IsVisible = updated.CloseToTray;
            }
            catch (Exception ex)
            {
                _log?.Warn("更新托盘图标可见性失败", ex);
            }
        });
    }

    private void OnLifetimeExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => Dispose();

    /// <summary>托盘菜单：在资源管理器里打开当前实例的游戏目录（§6.10：一律经 IInstanceContext 取目录）。</summary>
    private async void OnOpenGameDirectory(object? sender, EventArgs e)
    {
        try
        {
            if (_platform is null)
            {
                SetStatus("平台服务未就绪，无法打开游戏目录。");
                return;
            }

            var folder = _instanceContext?.GameDirectory;

            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = _paths?.MinecraftDirectory;
            }

            if (string.IsNullOrWhiteSpace(folder))
            {
                SetStatus("还没有可打开的游戏目录。");
                return;
            }

            await _platform.OpenFolderAsync(folder);
        }
        catch (Exception ex)
        {
            _log?.Warn("从托盘打开游戏目录失败", ex);
            SetStatus($"打开游戏目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 托盘菜单「退出」：走桌面生命周期真正结束进程，而不是只关窗口。
    ///
    /// 退出前先 await 一次设置保存。原因：Shutdown 触发的 Closing 里
    /// <see cref="MainWindow"/> 只能异步尽力写一次（不能为了保存去拦关机 / 应用级 Shutdown），
    /// 而进程往往在写盘完成前就结束了。托盘退出是用户主动结束，窗口尺寸记忆必须落在这里。
    /// </summary>
    private async void OnExit(object? sender, EventArgs e)
    {
        ExitRequested = true;
        Dispose();

        try
        {
            if (_settings is not null)
            {
                // 保存卡住不能变成「退出没反应」：给一个短上限，超时就按失败处理继续退出。
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _settings.SaveAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("托盘退出前保存设置失败，继续退出", ex);
        }

        try
        {
            // await 之后可能不再在 UI 线程（托盘回调来自原生线程，保存用线程池续跑），
            // Shutdown / Close 必须在 UI 线程执行（REVIEW-R34-R36 P3-10）。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_desktop is not null)
                {
                    _desktop.Shutdown();
                }
                else
                {
                    _window.Close();
                }
            });
        }
        catch (Exception ex)
        {
            _log?.Warn("从托盘退出启动器失败", ex);
        }
    }

    private void SetStatus(string message)
    {
        if (_window.DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        // 调用点可能在托盘原生线程或 await 之后（线程池）：跨线程改 VM 属性会破坏绑定。
        if (Dispatcher.UIThread.CheckAccess())
        {
            shell.StatusText = message;
            return;
        }

        Dispatcher.UIThread.Post(() => shell.StatusText = message);
    }
}
