using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 主窗口壳的 ViewModel，同时承担 <see cref="INavigationService"/> 的默认实现。
///
/// 页面实例按导航键缓存：来回切换不重建，避免重复拉取版本列表等网络数据。
/// 需要每次进页都刷新的数据由页面自身在 <see cref="PageViewModelBase.OnNavigatedToAsync"/> 里处理。
/// </summary>
public partial class MainWindowViewModel : ObservableObject, INavigationService
{
    private readonly Dictionary<string, Func<PageViewModelBase>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageViewModelBase> _pageCache = new(StringComparer.Ordinal);

    /// <summary>实例上下文的订阅只建立一次；设置可能被重复加载。</summary>
    private bool _instanceContextAttached;

    /// <summary>所有导航键。未注册真实页面的键由 <see cref="FillPlaceholders"/> 兜底。</summary>
    private static readonly string[] AllKeys =
    [
        NavigationKeys.Home, NavigationKeys.Versions, NavigationKeys.Download,
        NavigationKeys.Mods, NavigationKeys.Library, NavigationKeys.Accounts, NavigationKeys.Servers,
        NavigationKeys.Worlds, NavigationKeys.Logs, NavigationKeys.Help, NavigationKeys.Settings,
    ];

    private static readonly Dictionary<string, (string Title, string Description)> PlaceholderMeta = new(StringComparer.Ordinal)
    {
        [NavigationKeys.Home] = ("首页", "仪表盘：当前账户、最近版本、一键启动、磁盘用量、服务器状态。"),
        [NavigationKeys.Versions] = ("版本", "已安装版本列表：启动、修复、删除、打开目录、安装模组加载器。"),
        [NavigationKeys.Download] = ("下载", "版本下载：原版 + Fabric/Quilt/Forge/NeoForge，实时速度与文件级进度。"),
        [NavigationKeys.Mods] = ("模组", "模组/资源包/光影包管理：启用禁用、冲突检测、拖入安装。"),
        [NavigationKeys.Library] = ("资源", "在线资源库：按版本与加载器搜索 Modrinth，一键安装到当前游戏目录。"),
        [NavigationKeys.Accounts] = ("账户", "微软账户登录与离线账户管理。"),
        [NavigationKeys.Servers] = ("服务器", "服务器收藏与实时状态、MOTD、在线人数、延迟。"),
        [NavigationKeys.Worlds] = ("世界", "世界存档、备份与还原、整合包导入与导出。"),
        [NavigationKeys.Logs] = ("日志", "日志中心：启动器日志与游戏日志的查看、按级别与关键字过滤。"),
        [NavigationKeys.Help] = ("帮助", "内置离线帮助：功能说明、常见问题与故障排查。"),
        [NavigationKeys.Settings] = ("设置", "游戏目录、Java 路径、内存、下载源、主题与动效、窗口材质。"),
    };

    /// <summary>DI 容器，供 partial 注册文件解析页面 ViewModel。</summary>
    protected IServiceProvider Services { get; }

    public MainWindowViewModel()
        : this(new EmptyServiceProvider())
    {
    }

    public MainWindowViewModel(IServiceProvider services)
    {
        Services = services;
        SkinCard = new SkinCardViewModel(services);
        ApplyLaunchUiPreferences();
        RegisterPages();
        BuildNavigation();
        Navigate(NavigationKeys.Home);
        _ = InitializeSkinCardAsync();
    }

    /// <summary>
    /// 皮肤卡的后台初始化。异常在这里就地落日志：调用方丢弃了这个任务（PITFALLS 2.8），
    /// 让它静默失败在界面上只表现为「卡片一直停在默认皮肤」。
    /// </summary>
    private async Task InitializeSkinCardAsync()
    {
        try
        {
            await SkinCard.InitializeAsync();
        }
        catch (Exception ex)
        {
            (Services.GetService(typeof(IAppLog)) as IAppLog)?.Warn("初始化侧边栏皮肤卡失败", ex);
        }
    }

    /// <summary>窗口关闭时拆掉皮肤卡的账户订阅与在途请求。</summary>
    public void ReleaseSkinCard() => SkinCard.Dispose();

    /// <summary>
    /// 把「动效开关 / 背景特效」两项设置接进壳层。
    ///
    /// 这两项影响的是窗口级行为（星云背景的定时器、页面转场时长），
    /// 不属于任何单个页面，所以放在壳的构造函数里一次接好，并订阅后续变更。
    /// 设置服务缺失（设计器预览、单测）时保持默认开启。
    /// </summary>
    private void ApplyLaunchUiPreferences()
    {
        if (Services.GetService(typeof(ISettingsService)) is not ISettingsService settings)
        {
            return;
        }

        var paths = Services.GetService(typeof(ILauncherPaths)) as ILauncherPaths;

        ApplyUi(settings.Current);

        settings.Changed += (_, updated) => Dispatcher.UIThread.Post(() =>
        {
            ApplyUi(updated);
            ApplyGameDirectory(updated, paths);
        });

        // 启动即加载设置。之前只有进入设置页才调用 LoadAsync，
        // 导致「内存 / Java 路径 / 下载源」在设置页之外一律按默认值生效，
        // 并且 ApplyGameDirectory 在构造期拿到的是空设置，会把 --minecraft-dir 覆盖成默认目录。
        _ = LoadSettingsAsync(settings);
    }

    private async Task LoadSettingsAsync(ISettingsService settings)
    {
        try
        {
            await settings.LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"读取设置失败，使用默认值：{ex.Message}";
        }

        // 必须在设置读完之后再恢复实例选择：LastInstanceId 存在 settings.json 里，
        // 提前调用只会拿到默认值，然后被 SelectAsync(null) 覆盖成「不隔离」。
        ApplyUi(settings.Current);

        await AttachInstanceContextAsync();
    }

    /// <summary>
    /// 接上版本隔离的运行时状态。
    ///
    /// 只让「非当前页」的缓存失效：当前页正在显示的列表不应该因为一次实例切换被打断重建，
    /// 否则在模组页里点选目标会触发「Changed → 重建页面 → 重新加载」的抖动。
    /// </summary>
    private async Task AttachInstanceContextAsync()
    {
        if (_instanceContextAttached)
        {
            return;
        }

        if (Services.GetService(typeof(IInstanceContext)) is not IInstanceContext context)
        {
            return;
        }

        _instanceContextAttached = true;

        context.Changed += (_, _) => Dispatcher.UIThread.Post(InvalidateStalePages);

        try
        {
            await context.LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"恢复上次的实例选择失败，已回到全局目录：{ex.Message}";
        }
    }

    /// <summary>丢弃除当前页以外的页面实例，使它们下次进入时按新的实例目录重新加载。</summary>
    private void InvalidateStalePages()
    {
        var current = CurrentPage;

        if (current is null)
        {
            _pageCache.Clear();
            return;
        }

        _pageCache.Clear();
        _pageCache[current.Key] = current;
    }

    private void ApplyUi(AppSettings current)
    {
        Motion.Enabled = current.EnableAnimations;
        EnableBackgroundAnimations = current.EnableBackgroundEffects && current.EnableAnimations;

        // 主题：截图参数优先，保证同一份数据目录在任何时刻截出的图一致；
        // 正常运行取设置值。Application.Current 在设计器/单测里可能为 null，ThemeService 会忽略。
        ThemeService.Apply(ThemeService.Effective(current.Theme), Application.Current);

        ApplyBackgroundImage(current);
    }

    /// <summary>
    /// 按设置加载/卸载启动器背景图片。
    ///
    /// 只在路径变化时重新解码：设置保存会触发多次 Changed，重复 new Bitmap 会持续泄漏句柄。
    /// 解码失败（文件被删、格式不支持）回退到底色并把原因写到状态栏——不静默，也不写回设置。
    /// </summary>
    private void ApplyBackgroundImage(AppSettings current)
    {
        BackgroundImageOverlay = Math.Clamp(current.BackgroundImageOverlay, 0, 0.95);

        var path = string.IsNullOrWhiteSpace(current.BackgroundImagePath)
            ? null
            : current.BackgroundImagePath.Trim();

        if (path is null)
        {
            SwapBackgroundImage(null, null);
            return;
        }

        if (string.Equals(path, _backgroundImagePath, StringComparison.OrdinalIgnoreCase)
            && BackgroundImage is not null)
        {
            return;
        }

        try
        {
            var bitmap = LoadBackgroundBitmap(path);
            SwapBackgroundImage(bitmap, path);
        }
        catch (Exception ex)
        {
            SwapBackgroundImage(null, null);
            StatusText = $"背景图片加载失败：{Path.GetFileName(path)}（{ex.Message}）";
        }
    }

    /// <summary>
    /// 解码背景图；超过 3840 像素宽时按比例缩到 3840。
    ///
    /// 一张 8000×6000 的照片原样解码要占约 180 MB 原生内存，且解码发生在 UI 线程——
    /// 缩到 4K 宽既够 2K/4K 窗口使用，也把内存压到十几 MB。小图不放大。
    /// </summary>
    private static Bitmap LoadBackgroundBitmap(string path)
    {
        const int MaxWidth = 3840;

        using var stream = File.OpenRead(path);
        var original = new Bitmap(stream);

        if (original.PixelSize.Width <= MaxWidth)
        {
            return original;
        }

        try
        {
            stream.Position = 0;
            return Bitmap.DecodeToWidth(stream, MaxWidth);
        }
        finally
        {
            original.Dispose();
        }
    }

    private void SwapBackgroundImage(Bitmap? bitmap, string? path)
    {
        var previous = BackgroundImage;

        _backgroundImagePath = path;
        BackgroundImage = bitmap;
        HasBackgroundImage = bitmap is not null;

        // 不能同步 Dispose：Image.Source 刚变更，合成器可能仍在消费上一帧的位图。
        // 仓库在 ServersViewModel / TrayService 里对同类场景已有结论（交给 GC / 延迟释放），
        // 这里排到下一个渲染周期之后（REVIEW-R43-R51 P2-2）。
        if (!ReferenceEquals(previous, bitmap) && previous is not null)
        {
            Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 把设置里的游戏目录同步到 <see cref="ILauncherPaths"/>。
    ///
    /// 目录是进程级状态：各页面在构造时就缓存了 <c>MinecraftDirectory</c>，
    /// 所以切换后必须让页面缓存整体失效，否则版本/模组/世界页仍指向旧目录。
    /// 冷启动的目录由 <c>LauncherPaths</c> 自己读 settings.json；
    /// 命令行 <c>--minecraft-dir</c> 是测试/并行运行用的强制覆盖，这里不碰。
    /// </summary>
    private void ApplyGameDirectory(AppSettings settings, ILauncherPaths? paths)
    {
        // --minecraft-dir 显式指定，或 --data-dir 推导出 <data>/minecraft 时，
        // 命令行意图优先于 settings.json，任何自动同步都不应覆盖它。
        if (paths is null || !string.IsNullOrWhiteSpace(ScreenshotMode.EffectiveMinecraftDirectory))
        {
            return;
        }

        var desired = string.IsNullOrWhiteSpace(settings.MinecraftDirectory)
            ? null
            : settings.MinecraftDirectory.Trim();

        var current = paths.MinecraftDirectory;

        // 空值表示「使用平台默认目录」，与显式路径分开比较，避免把默认目录误判成已切换。
        if (desired is null)
        {
            if (string.Equals(current, NebulaLauncher.Core.Paths.LauncherPaths.ResolveDefaultMinecraft(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        else
        {
            try
            {
                desired = Path.GetFullPath(desired);
            }
            catch (Exception)
            {
                return;
            }

            if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        try
        {
            paths.SetMinecraftDirectory(desired ?? string.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"切换游戏目录失败：{ex.Message}";
            return;
        }

        InvalidatePageCache();
        StatusText = $"游戏目录已切换：{paths.MinecraftDirectory}";
    }

    /// <summary>丢弃所有页面实例并重建当前页，使页面用新目录重新加载数据。</summary>
    private void InvalidatePageCache()
    {
        var key = CurrentPage?.Key;
        if (string.IsNullOrEmpty(key))
        {
            _pageCache.Clear();
            return;
        }

        _pageCache.Clear();
        CurrentPage = null;
        Navigate(key);
    }

    /// <summary>主导航项。</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    /// <summary>侧边栏底部导航项（日志 / 帮助 / 设置）。保留完整集合供高亮逻辑统一遍历。</summary>
    public ObservableCollection<NavigationItem> FooterItems { get; } = new();

    /// <summary>皮肤卡上方的页脚项：日志、帮助。</summary>
    public ObservableCollection<NavigationItem> FooterUpperItems { get; } = new();

    /// <summary>皮肤卡下方的页脚项：设置。</summary>
    public ObservableCollection<NavigationItem> FooterLowerItems { get; } = new();

    /// <summary>
    /// 侧边栏常驻的 3D 皮肤卡。它与上面的页脚项共用同一批
    /// <see cref="NavigationItem"/> 实例，高亮只在一处维护。
    /// </summary>
    public SkinCardViewModel SkinCard { get; }

    [ObservableProperty]
    private PageViewModelBase? _currentPage;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>背景星云是否运动。跟随「减少动态效果」设置。</summary>
    [ObservableProperty]
    private bool _enableBackgroundAnimations = true;

    /// <summary>用户设置的启动器背景图；null 表示用主题底色。</summary>
    [ObservableProperty]
    private Bitmap? _backgroundImage;

    /// <summary>是否显示了背景图（控制遮罩层可见性）。</summary>
    [ObservableProperty]
    private bool _hasBackgroundImage;

    /// <summary>背景图上的主题色遮罩强度（0–1），来自设置。</summary>
    [ObservableProperty]
    private double _backgroundImageOverlay = 0.55;

    private string? _backgroundImagePath;

    /// <summary>背景星云是否运动。跟随「减少动态效果」设置，且背景图铺满时停掉（看不见还烧 CPU）。</summary>
    public bool BackgroundAnimationsActive => EnableBackgroundAnimations && !HasBackgroundImage;

    partial void OnEnableBackgroundAnimationsChanged(bool value) =>
        OnPropertyChanged(nameof(BackgroundAnimationsActive));

    partial void OnHasBackgroundImageChanged(bool value) =>
        OnPropertyChanged(nameof(BackgroundAnimationsActive));

    PageViewModelBase? INavigationService.Current => CurrentPage;

    public event EventHandler<PageViewModelBase>? Navigated;

    public void Navigate(string key)
    {
        if (!_factories.TryGetValue(key, out var factory))
        {
            StatusText = $"导航目标不存在: {key}";
            return;
        }

        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = factory();
            _pageCache[key] = page;
        }

        if (ReferenceEquals(page, CurrentPage))
        {
            return;
        }

        var previous = CurrentPage;
        CurrentPage = page;

        foreach (var item in NavigationItems)
        {
            item.IsActive = string.Equals(item.Key, page.Key, StringComparison.Ordinal);
        }

        foreach (var item in FooterItems)
        {
            item.IsActive = string.Equals(item.Key, page.Key, StringComparison.Ordinal);
        }

        Navigated?.Invoke(this, page);
        _ = RunPageLifecycleAsync(previous, page);
    }

    public void Navigate(PageViewModelBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _pageCache[page.Key] = page;
        Navigate(page.Key);
    }

    /// <summary>XAML 绑定入口。绑定到 <see cref="Navigate"/> 本身会因为方法不可绑定而失败。</summary>
    [RelayCommand]
    private void GoTo(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            Navigate(key);
        }
    }

    // ---------------- 窗口内快捷键 ----------------
    //
    // 五个键都由 MainWindow 的 KeyBindings 挂接（只在窗口内生效，不注册系统级全局热键）。
    // 这里只放命令本体：窗口负责「按键 → 命令」与 Esc 的焦点判断，VM 不持有窗口引用。

    /// <summary>F5：重新加载当前页。不重建 MainWindow，窗口尺寸与导航高亮原样保留。</summary>
    [RelayCommand]
    private async Task RefreshCurrentPageAsync()
    {
        var page = CurrentPage;

        if (page is null)
        {
            return;
        }

        try
        {
            await page.ReloadCurrentAsync();
            StatusText = $"{page.Title} 已刷新";
        }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
    }

    /// <summary>Ctrl+L：跳到日志页。</summary>
    [RelayCommand]
    private void NavigateLogs() => Navigate(NavigationKeys.Logs);

    /// <summary>
    /// Ctrl+O：打开当前游戏目录。
    ///
    /// 目录来源与各页面一致：<see cref="ILauncherPaths.MinecraftDirectory"/> 已经包含实例隔离的解析结果，
    /// 这里不自己读 settings.json，否则选了实例时打开的会是全局目录。
    /// </summary>
    [RelayCommand]
    private async Task OpenGameFolderAsync()
    {
        if (Services.GetService(typeof(ILauncherPaths)) is not ILauncherPaths paths)
        {
            StatusText = "打开游戏目录需要路径服务，当前未就绪。";
            return;
        }

        if (Services.GetService(typeof(IPlatformService)) is not IPlatformService platform)
        {
            StatusText = "打开游戏目录需要平台服务，当前未就绪。";
            return;
        }

        try
        {
            Directory.CreateDirectory(paths.MinecraftDirectory);
            await platform.OpenFolderAsync(paths.MinecraftDirectory);
            StatusText = $"已打开游戏目录：{paths.MinecraftDirectory}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开游戏目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Esc：关闭当前页最上层的确认 / 表单 / 计划浮层。
    ///
    /// 为什么分发放在壳层：各页已经有真实的 <c>CancelXxx</c> / <c>CloseXxx</c> 命令，
    /// 但页面之间没有统一的浮层栈，公共基类也拿不到页面私有的面板状态。
    /// 这里只在「面板确实打开着」时挑一个已存在的命令执行，不新造空命令；
    /// 没有任何浮层时什么都不做（不弹提示）。
    /// </summary>
    [RelayCommand]
    private void Escape()
    {
        switch (CurrentPage)
        {
            case VersionsViewModel versions:
                CancelVersionsOverlay(versions);
                return;
            case WorldsViewModel worlds:
                CancelWorldsOverlay(worlds);
                return;
            case ModsViewModel mods when mods.HasPendingDelete:
                mods.CancelDeleteCommand.Execute(null);
                return;
            case ServersViewModel servers when servers.IsFormOpen:
                servers.CancelFormCommand.Execute(null);
                return;
            case LibraryViewModel library when library.IsPlanOpen:
                library.CancelPlanCommand.Execute(null);
                return;
            case AccountsViewModel accounts:
                CancelAccountsOverlay(accounts);
                return;
            case SettingsViewModel settings when settings.IsStorageCleanConfirmOpen:
                // 清理确认浮层盖在设置页上，Esc 关它；其余分区没有浮层，落到下面的 no-op。
                settings.CancelCleanStorageCommand.Execute(null);
                return;
            case DownloadViewModel download:
                // 命令内部自己判断有没有进行中的安装，没有就什么都不做。
                download.CancelInstallCommand.Execute(null);
                return;
            case HomeViewModel home when home.HasLaunchPanel && home.ShowCancelLaunch:
                // 只有界面上确实有「取消」按钮时才让 Esc 取消：进程起来后取消=移交后台，
                // 那是 API 能力，不能用隐藏快捷键触发。
                home.CancelLaunchCommand.Execute(null);
                return;
        }
    }

    /// <summary>版本页的浮层按「越靠上层越先关」的顺序判断，与用户看到的叠放顺序一致。</summary>
    private static void CancelVersionsOverlay(VersionsViewModel versions)
    {
        if (versions.IsPurgeConfirmOpen)
        {
            versions.CancelPurgeCommand.Execute(null);
            return;
        }

        if (versions.IsRepairConfirmOpen)
        {
            versions.CancelRepairConfirmCommand.Execute(null);
            return;
        }

        if (versions.IsRenameOpen)
        {
            versions.CancelRenameCommand.Execute(null);
            return;
        }

        if (versions.IsIconPickerOpen)
        {
            versions.CancelIconPickerCommand.Execute(null);
            return;
        }

        if (versions.IsCleanupOpen)
        {
            versions.CancelCleanupCommand.Execute(null);
            return;
        }

        if (versions.IsTrashOpen)
        {
            versions.ToggleTrashCommand.Execute(null);
            return;
        }

        if (versions.IsInstanceEditorOpen)
        {
            versions.CloseInstanceEditorCommand.Execute(null);
            return;
        }

        if (versions.IsInstanceConfirmOpen)
        {
            versions.CancelDeleteInstanceCommand.Execute(null);
            return;
        }

        if (versions.IsConfirmOpen)
        {
            versions.CancelDeleteCommand.Execute(null);
        }
    }

    /// <summary>世界页：导入预览优先，其次世界 / 备份的删除确认（确认态挂在卡片上，命令要卡片做参数）。</summary>
    private static void CancelWorldsOverlay(WorldsViewModel worlds)
    {
        if (worlds.HasImportPreview)
        {
            worlds.CancelImportCommand.Execute(null);
            return;
        }

        var world = worlds.Worlds.FirstOrDefault(item => item.IsConfirmingDelete);
        if (world is not null)
        {
            worlds.CancelDeleteWorldCommand.Execute(world);
            return;
        }

        var backup = worlds.Backups.FirstOrDefault(item => item.IsConfirmingDelete);
        if (backup is not null)
        {
            worlds.CancelDeleteBackupCommand.Execute(backup);
        }
    }

    /// <summary>账户页：移除确认 → 第三方登录表单 → 微软设备码登录。</summary>
    private static void CancelAccountsOverlay(AccountsViewModel accounts)
    {
        if (accounts.HasPendingRemoval)
        {
            accounts.CancelRemoveCommand.Execute(null);
            return;
        }

        if (accounts.IsAuthlibFormOpen)
        {
            accounts.CancelAuthlibFormCommand.Execute(null);
            return;
        }

        if (accounts.HasDeviceCode)
        {
            accounts.CancelSignInCommand.Execute(null);
        }
    }

    private async Task RunPageLifecycleAsync(PageViewModelBase? previous, PageViewModelBase page)
    {
        try
        {
            if (previous is not null)
            {
                await previous.OnNavigatedFromAsync();
            }

            await page.OnNavigatedToAsync();

            // 页面的 Subtitle 通常是「数据加载后才算得出来」的计算属性，本身不做变更通知。
            // 生命周期跑完后强制重求一次抬头绑定，否则首帧取到的空值会一直留在界面上。
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(CurrentPage)));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"页面加载失败：{ex.Message}");
        }
    }

    private void RegisterPages()
    {
        // 三个 partial 钩子分别由不同协作者实现，互不覆盖同一个文件。
        // 没人实现时编译器会把调用整个移除，随后由 FillPlaceholders 兜底。
        RegisterHomePages();
        RegisterContentPages();
        RegisterSystemPages();

        FillPlaceholders();
    }

    /// <summary>首页 / 版本 / 下载 三页的注册。</summary>
    partial void RegisterHomePages();

    /// <summary>模组 / 账户 / 服务器 三页的注册。</summary>
    partial void RegisterContentPages();

    /// <summary>设置 / 备份 / 整合包 等系统页的注册。</summary>
    partial void RegisterSystemPages();

    /// <summary>对仍未注册的导航键填占位页，保证导航骨架永远可用。</summary>
    private void FillPlaceholders()
    {
        foreach (var key in AllKeys)
        {
            if (_factories.ContainsKey(key))
            {
                continue;
            }

            var (title, description) = PlaceholderMeta.TryGetValue(key, out var meta)
                ? meta
                : (key, "尚未实现。");
            _factories[key] = () => new PlaceholderPageViewModel(key, title, description);
        }
    }

    /// <summary>页面注册入口。协作者在 partial 实现里调用，Key 取自 <see cref="NavigationKeys"/>。</summary>
    private void RegisterPage(string key, Func<PageViewModelBase> factory) => _factories[key] = factory;

    /// <summary>
    /// 从 DI 解析页面。页面 ViewModel 无需显式注册 —— 依赖已注册时由
    /// <see cref="ActivatorUtilities"/> 直接构造，这样多个协作者各写各的页面时
    /// 不必争抢同一个组合根文件。
    /// </summary>
    protected T Resolve<T>() where T : PageViewModelBase
    {
        if (Services.GetService(typeof(T)) is T registered)
        {
            return registered;
        }

        return ActivatorUtilities.CreateInstance<T>(Services);
    }

    /// <summary>本窗口独立运行（设计器预览）时的空容器。</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private void BuildNavigation()
    {
        NavigationItems.Add(new NavigationItem(NavigationKeys.Home, "首页", NavigationKeys.IconHome));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Versions, "版本", NavigationKeys.IconVersions));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Download, "下载", NavigationKeys.IconDownload));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Mods, "模组", NavigationKeys.IconMods));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Library, "资源", NavigationKeys.IconLibrary));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Accounts, "账户", NavigationKeys.IconAccounts));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Servers, "服务器", NavigationKeys.IconServers));
        NavigationItems.Add(new NavigationItem(NavigationKeys.Worlds, "世界", NavigationKeys.IconWorlds));

        var logs = new NavigationItem(NavigationKeys.Logs, "日志", NavigationKeys.IconLogs, isFooter: true);
        var help = new NavigationItem(NavigationKeys.Help, "帮助", NavigationKeys.IconHelp, isFooter: true);
        var settings = new NavigationItem(NavigationKeys.Settings, "设置", NavigationKeys.IconSettings, isFooter: true);

        // 三个集合共享同一批实例：FooterItems 负责让高亮逻辑只遍历一处，
        // 上下两段只决定它们和皮肤卡的先后位置。
        FooterItems.Add(logs);
        FooterItems.Add(help);
        FooterItems.Add(settings);

        FooterUpperItems.Add(logs);
        FooterUpperItems.Add(help);
        FooterLowerItems.Add(settings);
    }
}
