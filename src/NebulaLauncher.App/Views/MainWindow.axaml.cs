using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Platforms;
using NebulaLauncher.Services;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsService? _settings;
    private readonly IAppLog? _log;

    /// <summary>
    /// 是否已经按设置恢复过窗口状态。只做一次：之后再跟着设置变化改尺寸，
    /// 会在用户拖窗口的同时把窗口跳回上次的值。
    /// </summary>
    private bool _windowStateRestored;

    /// <summary>
    /// 真正退出前的那次异步落盘是否已经开始。Closing 第一次进入时置位并取消关闭，
    /// 保存完成后由 <see cref="FlushAndCloseAsync"/> 再调一次 <see cref="Window.Close"/>；
    /// 第二次进入直接放行。用户等不及又点了一次关闭时，第二次会立刻生效。
    /// </summary>
    private bool _closeFlushStarted;

    /// <summary>
    /// 本次关闭流程里窗口尺寸是否变过（写回内存算一次变化）。
    ///
    /// 有下载确认时关闭会被拦一次，第二次 CaptureWindowState 发现「没变化」就会跳过保存；
    /// 用这个字段把第一次的结果带过去，尺寸才不会被静默丢掉。
    /// </summary>
    private bool _pendingWindowStateChanged;

    /// <summary>
    /// 托盘服务。由 <see cref="TrayService.TryCreate"/> 在应用启动时挂上来；
    /// 截图模式或托盘建不出来时为 <c>null</c>，此时关闭窗口保持旧行为（直接退出）。
    /// </summary>
    internal TrayService? Tray { get; set; }

    /// <summary>Avalonia 设计器与热重载使用的无参构造。</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        AttachDragDrop();
        RestoreWindowState();
        RegisterShortcuts();
    }

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        var shell = new MainWindowViewModel(services);
        shell.ExitConfirmed += (_, _) => ExitAfterDownloadConfirm();
        DataContext = shell;
        AttachDragDrop();
        Closing += OnClosing;
        Closed += OnClosed;
        Opened += OnOpened;

        _settings = services.GetService(typeof(ISettingsService)) as ISettingsService;
        _log = services.GetService(typeof(IAppLog)) as IAppLog;

        RestoreWindowState();
        RegisterShortcuts();

        // 截图模式档位：覆盖层只在真实下载 + 点关闭时出现，没有这个入口截不到。
        if (string.Equals(ScreenshotMode.SectionKey, "exit-guard", StringComparison.OrdinalIgnoreCase))
        {
            shell.ApplyExitGuardDemo();
            _log?.Info("截图模式：退出下载确认覆盖层已打开");
        }
    }

    /// <summary>
    /// 按 <c>settings.json</c> 恢复上次的窗口尺寸与最大化状态。
    ///
    /// 优先级：<c>--size</c>（截图模式）&gt; 设置里的尺寸 &gt; XAML 里的 1240×800。
    /// 截图参数必须压过设置：无人值守截图要的是确定尺寸，被用户上次拖出来的大小带偏后，
    /// <c>tools/shot.ps1</c> 就验证不了窄屏布局了。
    ///
    /// 调用时机是构造函数末尾：必须在首帧之前，否则窗口会先按默认尺寸画一帧再跳。
    /// 「设置已经读完」由 <c>App.OnFrameworkInitializationCompleted</c> 保证（那里先同步读一次设置）。
    /// </summary>
    private void RestoreWindowState()
    {
        if (_windowStateRestored)
        {
            return;
        }

        _windowStateRestored = true;

        if (ScreenshotMode.WindowWidth is { } shotWidth && ScreenshotMode.WindowHeight is { } shotHeight)
        {
            Width = shotWidth;
            Height = shotHeight;
            _log?.Info($"窗口尺寸来自截图参数：{shotWidth}x{shotHeight}");
            return;
        }

        var settings = _settings?.Current;

        // 用户关掉「记住窗口大小」时启动端也不读：保持 XAML 的默认尺寸。
        if (settings is null || !settings.RememberWindowSize)
        {
            _log?.Info("未启用窗口尺寸记忆，使用默认尺寸 1240x800");
            return;
        }

        Width = settings.LauncherWindowWidth;
        Height = settings.LauncherWindowHeight;

        // 截图模式不恢复最大化：截图要按 settings.json 的尺寸出图，
        // 恢复最大化会变成整屏尺寸，「1024×660 生效」这类验收就看不出来了。
        if (!ScreenshotMode.IsActive && settings.LauncherWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _log?.Info(
            $"恢复窗口尺寸：{settings.LauncherWindowWidth}x{settings.LauncherWindowHeight}，" +
            $"最大化={!ScreenshotMode.IsActive && settings.LauncherWindowMaximized}");
    }

    /// <summary>首帧后用真实布局尺寸再记一条日志：尺寸是否真的生效，验收时以这条为准。</summary>
    private void OnOpened(object? sender, EventArgs e)
        => _log?.Info($"启动器窗口已打开：{Bounds.Width:0}x{Bounds.Height:0}，状态={WindowState}");

    /// <summary>
    /// 窗口内快捷键。只挂 <see cref="InputElement.KeyBindings"/>，不注册系统级全局热键：
    /// 全局热键会跟游戏、输入法抢按键；帮助页里「不做隐藏组合键」的承诺仍然成立。
    /// </summary>
    private void RegisterShortcuts()
    {
        var shell = DataContext as MainWindowViewModel;

        // Esc 的 CanExecute 先看焦点类型：输入框里要把 Esc 留给控件自己（结束输入 / 关闭补全弹层）。
        // F5 与 Ctrl+O 在输入框里仍然生效，文本框不消费这两个组合。
        AddKeyBinding(Key.Escape, KeyModifiers.None, new RelayCommand(RunEscape, CanRunEscape));
        AddKeyBinding(Key.F5, KeyModifiers.None, shell?.RefreshCurrentPageCommand);
        AddKeyBinding(Key.F11, KeyModifiers.None, new RelayCommand(ToggleMaximize));
        AddKeyBinding(Key.O, KeyModifiers.Control, shell?.OpenGameFolderCommand);
        AddKeyBinding(Key.L, KeyModifiers.Control, shell?.NavigateLogsCommand);
    }

    private void AddKeyBinding(Key key, KeyModifiers modifiers, ICommand? command)
    {
        if (command is null)
        {
            return;
        }

        // 代码里挂接而不是写在 XAML 里：F11 要调到窗口的 ToggleMaximize，
        // 其余四个绑到壳层命令，放在一处才能一眼看全「窗口内有哪些快捷键」。
        // KeyBindings 是只读的 List<KeyBinding>（Avalonia 11.3），直接 Add 即可。
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(key, modifiers),
            Command = command,
        });
    }

    private void RunEscape()
        => (DataContext as MainWindowViewModel)?.EscapeCommand.Execute(null);

    private bool CanRunEscape() => !IsTextInputFocused();

    /// <summary>
    /// 焦点是否在文本输入控件里。Avalonia 的文本框自己处理 Esc，
    /// 窗口级 Esc 抢过来会让「按 Esc 结束输入 / 关闭补全」变成「关掉背后看不见的面板」。
    /// <see cref="AutoCompleteBox"/> 与 <see cref="NumericUpDown"/> 内部各有一个 TextBox，
    /// 所以沿视觉祖先链判断，而不是只比较焦点元素本身。
    /// </summary>
    private static bool IsTextInputFocused()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return false;
        }

        var focused = desktop.MainWindow?.FocusManager?.GetFocusedElement();

        return focused is Visual visual
            && visual.GetSelfAndVisualAncestors().Any(ancestor =>
                ancestor is TextBox or AutoCompleteBox or NumericUpDown);
    }

    /// <summary>
    /// 「关闭到托盘」：开关打开且托盘真的建出来时，关闭只隐藏窗口。
    /// 判据在 Core 的 <see cref="CloseBehavior"/>（有单测），这里只负责执行，
    /// 避免窗口与托盘服务两处各写一套分支。
    ///
    /// 落盘顺序：先把尺寸写回内存（无 IO），再按分支决定要不要等异步保存——
    /// 关机 / 关到托盘不能拦，保存放后台；真正退出则取消一次关闭，等保存跑完再关。
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 写回窗口尺寸必须早于「隐藏 / 退出」判定：关到托盘时窗口只是隐藏、进程继续存活，
        // 这是唯一可靠的写回时机；真正退出时也要在 Closing 里落盘，因为退出后没有第二次机会。
        var stateChanged = CaptureWindowState();
        _pendingWindowStateChanged |= stateChanged;

        // 系统注销 / 关机、或应用级 Shutdown 一律放行：拦下来会挡住关机流程，也不能在这里 await。
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            // 托盘「退出」会先自己把设置写完再 Shutdown（见 TrayService.OnExit），不必重复写；
            // 其余应用级退出与关机没有第二次机会，尽力异步写一次：写不完最坏是保留旧文件。
            if (Tray?.ExitRequested != true)
            {
                FlushSettingsInBackground(stateChanged, "关机或应用级退出");
            }

            return;
        }

        var tray = Tray;

        // 关到托盘：窗口只是隐藏、进程继续存活，落盘同样不拦关闭。
        if (tray is not null
            && !tray.ExitRequested
            && !CloseBehavior.ShouldExitOnClose(tray.CloseToTray, tray.TrayAvailable, ScreenshotMode.IsActive)
            && tray.HideToTray())
        {
            // 合并上一次被下载确认拦下时留下的变化：那次已经改过内存尺寸，只是没落盘。
            FlushSettingsInBackground(stateChanged || _pendingWindowStateChanged, "关闭到托盘");
            e.Cancel = true;
            return;
        }

        // 真正退出之前：有未完成的下载任务先问一次（PCL2 FormMain.EndProgram 同样处理）。
        // 覆盖层由壳 VM 管，这里只决定「这次关闭算不算数」。
        if (DataContext is MainWindowViewModel shell && !shell.ShouldConfirmExit())
        {
            e.Cancel = true;
            return;
        }

        // 无托盘（截图模式 / 创建失败）、托盘菜单已请求退出、或藏不起来：真正退出。
        // 第一次先取消关闭，等异步保存跑完再 Close()；第二次（由那次 Close 触发）直接放行。
        CloseAfterSave(e, stateChanged || _pendingWindowStateChanged);
    }

    /// <summary>
    /// 下载确认覆盖层点「退出」后的续接：第一次 Closing 已经取消过，这次不再走 OnClosing 的判定，
    /// 直接复用那次的尺寸变化结论并保存退出。
    /// </summary>
    private void ExitAfterDownloadConfirm()
    {
        if (_closeFlushStarted)
        {
            return;
        }

        _closeFlushStarted = true;

        if (_pendingWindowStateChanged)
        {
            FlushAndCloseAsync();
            return;
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _log?.Warn("关闭窗口失败", ex);
        }
    }

    /// <summary>
    /// 真正退出前的落盘收尾。
    ///
    /// 没有要写的尺寸（截图模式 / 关掉了「记住窗口大小」）或保存已经在跑时直接放行：
    /// 前者没必要等，后者说明用户在第一次取消关闭后又点了一次关闭，按用户意图立刻退出。
    /// </summary>
    private void CloseAfterSave(WindowClosingEventArgs e, bool stateChanged)
    {
        if (!stateChanged || _closeFlushStarted)
        {
            return;
        }

        _closeFlushStarted = true;
        e.Cancel = true;
        FlushAndCloseAsync();
    }

    /// <summary>
    /// 先 await 异步保存，再真正关闭窗口。
    ///
    /// 这里是 <c>async void</c>（Closing 是同步签名），所以整段必须有 try/catch：
    /// 一次保存异常不能把进程带崩，也不能让窗口停在「取消过一次关闭」的状态里。
    /// </summary>
    private async void FlushAndCloseAsync()
    {
        await FlushSettingsAsync("窗口关闭");

        // 这次已经尝试落盘，标记不能再带到下一次关闭流程（进程随后就退出了）。
        _pendingWindowStateChanged = false;

        try
        {
            // await 回到 UI 线程（Closing 在 UI 线程触发，这里没有 ConfigureAwait(false)），
            // Close 再次进入 Closing；_closeFlushStarted 已置位，那一次直接放行。
            Close();
        }
        catch (Exception ex)
        {
            _log?.Warn("关闭窗口失败", ex);
        }
    }

    /// <summary>不拦关闭的分支（关机 / 隐藏到托盘）用后台写：调用方不能等，失败由内部记日志。</summary>
    private void FlushSettingsInBackground(bool stateChanged, string reason)
    {
        if (!stateChanged)
        {
            return;
        }

        _ = FlushSettingsAsync(reason);
    }

    /// <summary>把设置写盘。成功与失败都只记日志：关闭路径上已经没有可以回退的界面了。</summary>
    private async Task FlushSettingsAsync(string reason)
    {
        var settings = _settings;
        if (settings is null)
        {
            return;
        }

        try
        {
            await settings.SaveAsync().ConfigureAwait(false);
            _log?.Info($"关闭流程已保存设置（{reason}）");
        }
        catch (Exception ex)
        {
            _log?.Warn($"关闭流程保存设置失败（{reason}）", ex);
        }
    }

    /// <summary>
    /// 把当前窗口尺寸写回内存里的设置，返回是否真的变了（变了才值得落盘）。
    ///
    /// 最大化时只记 <c>AppSettings.LauncherWindowMaximized</c>，尺寸仍保留上一次 Normal 的值：
    /// 直接存最大化后的尺寸，用户下次「还原」会得到一个和屏幕一样大的窗口。
    /// 最小化时两样都不更新——此时 <see cref="Window.Bounds"/> 与最大化标记都没有信息量。
    /// </summary>
    private bool CaptureWindowState()
    {
        var settings = _settings;
        if (settings is null)
        {
            return false;
        }

        var current = settings.Current;

        // 关掉「记住窗口大小」→ 不读写；截图模式 → 会把 --size 的验证尺寸污染进真实设置。
        if (!WindowMemory.ShouldPersist(current.RememberWindowSize, ScreenshotMode.IsActive))
        {
            return false;
        }

        if (WindowState == WindowState.Minimized)
        {
            return false;
        }

        var maximized = WindowState == WindowState.Maximized;
        var changed = current.LauncherWindowMaximized != maximized;

        if (!maximized)
        {
            // 只有 Normal 状态才更新尺寸：这时 Bounds 才是用户拖出来的窗口大小。
            var width = (int)Math.Round(Bounds.Width);
            var height = (int)Math.Round(Bounds.Height);

            if (current.LauncherWindowWidth != width || current.LauncherWindowHeight != height)
            {
                current.LauncherWindowWidth = width;
                current.LauncherWindowHeight = height;
                changed = true;
            }
        }

        current.LauncherWindowMaximized = maximized;

        return changed;
    }

    /// <summary>窗口真正关闭时收掉托盘图标，避免通知区留下一个点不动的死图标。</summary>
    private void OnClosed(object? sender, EventArgs e) => Tray?.Dispose();

    /// <summary>
    /// 整个窗口接受文件拖入（PCL2 的 DragHelper 同样是窗口级钩子）。
    /// 用代码挂接而不是 XAML 附加事件：附加事件的绑定路径在编译绑定下容易写错，
    /// 而且这里本来就需要 code-behind 读本地路径。
    /// </summary>
    private void AttachDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CollectLocalPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Drop 是事件处理器，只能用 async void；内部必须整段 try/catch，
    /// 否则一次导入异常会直接把进程带崩。
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        var paths = CollectLocalPaths(e);

        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            await shell.HandleDroppedFilesAsync(paths);
        }
        catch (Exception ex)
        {
            // 这里已经是最外层，UI 不允许再往上抛。
            shell.StatusText = $"拖入处理失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 取出拖入项的本地路径。非本地项（虚拟文件、来自浏览器的拖动）没有路径，
    /// 直接忽略——本启动器只处理磁盘上的文件。
    /// </summary>
    private static List<string> CollectLocalPaths(DragEventArgs e)
    {
        var paths = new List<string>();

        foreach (var item in e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
        {
            var path = item.TryGetLocalPath();

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>无边框窗口需要自己实现拖动：只在标题栏按下左键时进入系统移动循环。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 双击标题栏切换最大化，与系统行为一致。
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
