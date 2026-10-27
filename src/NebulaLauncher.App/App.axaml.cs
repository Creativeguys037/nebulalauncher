using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Logging;
using NebulaLauncher.Core.Maintenance;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Paths;
using NebulaLauncher.Core.Platforms;
using NebulaLauncher.Core.Settings;
using NebulaLauncher.Core.Versions;
using NebulaLauncher.Services;
using NebulaLauncher.ViewModels;
using NebulaLauncher.Views;

namespace NebulaLauncher;

public partial class App : Application
{
    /// <summary>全局服务容器。窗口与页面通过构造注入获取依赖。</summary>
    public static IServiceProvider Services { get; private set; } = new ServiceCollection().BuildServiceProvider();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyPlatformFont();

        // 首帧主题：设置是异步加载的（见 MainWindowViewModel.ApplyLaunchUiPreferences），
        // 这里只保证 --theme 覆盖立即生效；用户设置里持久化的主题在读盘完成后由 ApplyUi 再应用一次。
        ThemeService.Apply(ThemeService.Effective(null), this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = BuildServices();

            // 窗口尺寸与首页账户卡片都要在首帧之前就拿到磁盘上的值，而这两个服务都是异步加载的。
            // 统一在 Services/StartupState.cs 里同步读一次：之后页面里的 LoadAsync 幂等，重复调用是空操作。
            StartupState.LoadBeforeFirstFrame(Services);

            var window = new MainWindow(Services);
            desktop.MainWindow = window;

            // 窄屏验证：截图前改窗口尺寸。必须在首帧之前设置，否则会二次布局。
            // 截图参数优先级最高：MainWindow 内部已按「截图 > 设置 > XAML 默认」恢复过一次，
            // 这里再写一次是保证「--size 永远压过 settings.json 里的窗口尺寸」。
            if (ScreenshotMode.WindowWidth is { } shotWidth && ScreenshotMode.WindowHeight is { } shotHeight)
            {
                window.Width = shotWidth;
                window.Height = shotHeight;
            }

            // 导航代理先注册、后挂接，避免「页面 ← 导航 → 主窗口 → 页面」的循环依赖。
            if (Services.GetService(typeof(INavigationService)) is NavigationServiceProxy proxy
                && window.DataContext is MainWindowViewModel shell)
            {
                proxy.Attach(shell);
            }

            // 窗口动作代理同理：页面 VM 拿接口，真实窗口在这里挂上去。
            if (Services.GetService(typeof(IWindowActions)) is WindowActionsProxy windowActions)
            {
                windowActions.Attach(window);
            }

            // 托盘：截图模式内部直接跳过；正常启动时由它接上「关闭到托盘」。
            // 返回值故意丢掉——强引用由 MainWindow.Tray 持有。
            TrayService.TryCreate(window, Services);

            // 任务栏进度：原生句柄要到 Opened 之后才有效（之前 TryGetPlatformHandle 返回 null）。
            // 只挂事件、不主动调 Report——真正的进度由页面 ViewModel 在启动/安装时上报。
            window.Opened += (_, _) =>
            {
                ShellProgress.Attach(window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
            };

            if (ScreenshotMode.IsActive)
            {
                AttachScreenshot(window);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 无人值守截图：等首帧渲染与数据加载完成后，把窗口视觉树渲染到 PNG 并退出。
    /// 只在 <c>--screenshot</c> 模式下挂接，正常启动零开销。
    /// </summary>
    private static void AttachScreenshot(Window window)
    {
        window.Opened += async (_, _) =>
        {
            var path = ScreenshotMode.OutputPath!;
            try
            {
                if (window.DataContext is MainWindowViewModel vm && !string.IsNullOrEmpty(ScreenshotMode.PageKey))
                {
                    vm.Navigate(ScreenshotMode.PageKey);
                }

                // 版本页：打开「显示已隐藏」。
                // 隐藏行、行内「已隐藏」徽标与「取消隐藏」按钮只在开关打开时出现，
                // 而开关本身是 VM 的瞬时状态，favorites.json 只能决定谁被隐藏。
                if (ScreenshotMode.ShowHidden
                    && window.DataContext is MainWindowViewModel hiddenShell
                    && hiddenShell.CurrentPage is VersionsViewModel hiddenVersions)
                {
                    hiddenVersions.ShowHidden = true;
                }

                // 设置页默认停在「常规」，非默认分区（游戏目录、Java 下载）只能靠这个参数截到。
                if (window.DataContext is MainWindowViewModel shell
                    && !string.IsNullOrEmpty(ScreenshotMode.SectionKey)
                    && shell.CurrentPage is SettingsViewModel settingsViewModel)
                {
                    settingsViewModel.SelectedSectionKey = ScreenshotMode.SectionKey;
                }

                await Task.Delay(ScreenshotMode.DelayMs);

                // 首页：把页面推进「缺少 Java 25」的启动失败态。
                // 真实路径要跑一次启动 + 探测本机 Java + 联网下载，截图模式要确定性，
                // 所以这里只摆 ViewModel 状态，按钮文案仍由 Core 的 planner 真实算出。
                // 必须排在下面的 --scroll 之前：它会把失败面板撑高，先撑开才滚得动。
                if (ScreenshotMode.MissingJavaDemo
                    && window.DataContext is MainWindowViewModel shellMissingJava
                    && shellMissingJava.CurrentPage is HomeViewModel homeViewModel)
                {
                    homeViewModel.ApplyMissingJavaDemo();
                    await Task.Delay(300);
                }

                // 版本页：摆出「启动失败 + 缺 Java」的区块。与首页那段同理，但页面更窄——
                // 按钮不换行、进度行不挤掉提示文案，只能靠另一张图验证。
                if (ScreenshotMode.MissingJavaVersionsDemo
                    && window.DataContext is MainWindowViewModel shellMissingJavaVersions
                    && shellMissingJavaVersions.CurrentPage is VersionsViewModel versionsMissingJava)
                {
                    versionsMissingJava.ApplyMissingJavaDemo();
                    await Task.Delay(300);
                }

                // 下载页：注入演示任务并把面板摆到「有活动批次」。
                // 截图模式没有点击能力，任务面板在真实启动时永远是空态；
                // 这段只在 --screenshot 下执行（AttachScreenshot 本身只在截图模式挂接）。
                if (ScreenshotMode.DownloadDemo
                    && window.DataContext is MainWindowViewModel shellDownload
                    && shellDownload.CurrentPage is DownloadViewModel downloadViewModel)
                {
                    downloadViewModel.ApplyDownloadDemo(ScreenshotMode.DownloadDemoPaused);
                    await Task.Delay(300);
                }

                // 下载页：摆出一次「源测速」结论。测速要真发请求且结果取决于当天网络，
                // 截图要可复现；文案仍由 Core 的排序纯函数生成，与线上同源。
                if (ScreenshotMode.SourceProbeDemo
                    && window.DataContext is MainWindowViewModel shellSourceProbe
                    && shellSourceProbe.CurrentPage is DownloadViewModel downloadSourceProbe)
                {
                    downloadSourceProbe.ApplySourceProbeDemo();
                    await Task.Delay(200);
                }

                // 页面内的滚动位置：设置页加长后首屏之外的项只能靠这个参数截到。
                // 视觉树里有多个 ScrollViewer（导航栏也有一个），只对「真的能滚」的那些生效。
                if (ScreenshotMode.ScrollOffset > 0)
                {
                    foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>())
                    {
                        if (scroller.Extent.Height > scroller.Viewport.Height)
                        {
                            scroller.Offset = new Vector(0, ScreenshotMode.ScrollOffset);
                        }
                    }

                    await Task.Delay(300);
                }

                // 版本页：数据加载完再展开实例设置面板。
                // 截图模式没有点击能力，没有这段就永远验证不到面板的布局。
                if (ScreenshotMode.OpenInstanceEditor
                    && window.DataContext is MainWindowViewModel shell2
                    && shell2.CurrentPage is VersionsViewModel versions
                    && versions.Instances.Count > 0)
                {
                    versions.OpenInstanceEditorCommand.Execute(versions.Instances[0]);
                    await Task.Delay(300);
                }

                // 模组页：把当前列表全部勾选。批量操作条在无选中时是禁用态，
                // 不点这一下截图里只能看到灰按钮，验证不到「已选 N 项 → 按钮可用」这条链路。
                if (ScreenshotMode.SelectAllMods
                    && window.DataContext is MainWindowViewModel shell3
                    && shell3.CurrentPage is ModsViewModel modsViewModel
                    && modsViewModel.Items.Count > 0)
                {
                    modsViewModel.SelectAllCommand.Execute(null);
                    await Task.Delay(200);
                }

                // 版本页：打开第一个实例的完整备注浮层。备注按钮只在有备注时出现，所以取第一个有备注的。
                if (ScreenshotMode.OpenInstanceNotes
                    && window.DataContext is MainWindowViewModel shell4
                    && shell4.CurrentPage is VersionsViewModel versionsForNotes)
                {
                    var noted = versionsForNotes.Instances.FirstOrDefault(row => row.HasNote);

                    if (noted is not null)
                    {
                        versionsForNotes.OpenNotesCommand.Execute(noted);
                        await Task.Delay(200);
                    }
                }

                // 版本页：打开第一个版本的组件面板（只能点版本行的「组件…」出现）。
                if (ScreenshotMode.OpenVersionComponents
                    && window.DataContext is MainWindowViewModel shell5
                    && shell5.CurrentPage is VersionsViewModel versionsForComponents
                    && versionsForComponents.Versions.Count > 0)
                {
                    versionsForComponents.OpenComponentsCommand.Execute(versionsForComponents.Versions[0]);
                    await Task.Delay(500);
                }

                // 日志页：写一份演示日志并真实跟随一次，截到「跟随中 + 刚追加的新行」。
                // 跟随只在文件还在增长时才有可见效果，而截图模式没有点击能力、也没有第二个写日志的进程。
                if (ScreenshotMode.LogFollowDemo
                    && window.DataContext is MainWindowViewModel shell6
                    && shell6.CurrentPage is LogsViewModel logsViewModel)
                {
                    await logsViewModel.ApplyLogFollowDemoAsync();
                }

                var size = window.Bounds.Size;
                var pixelSize = new PixelSize(
                    Math.Max(1, (int)Math.Round(size.Width)),
                    Math.Max(1, (int)Math.Round(size.Height)));

                using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(path);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(path + ".error.txt", ex.ToString());
            }

            Environment.Exit(0);
        };
    }

    /// <summary>
    /// 组合根。注册顺序有依赖关系：路径 → 日志 → 平台 → 其余业务模块。
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // ---- 基础设施 ----
        // --data-dir / --minecraft-dir 让并行运行的多个实例各用一份数据目录与游戏目录，
        // 否则它们会并发写同一份 accounts.json / servers.json，并且互相看到对方的版本列表。
        var dataDir = ScreenshotMode.DataDirectory;
        var mcDir = ScreenshotMode.EffectiveMinecraftDirectory;

        if (string.IsNullOrWhiteSpace(dataDir) && string.IsNullOrWhiteSpace(mcDir))
        {
            services.AddLauncherPaths();
        }
        else
        {
            var root = string.IsNullOrWhiteSpace(dataDir)
                ? LauncherPaths.ResolveDefaultRoot()
                : Path.GetFullPath(dataDir!);
            var mc = string.IsNullOrWhiteSpace(mcDir) ? null : Path.GetFullPath(mcDir);

            services.AddSingleton<ILauncherPaths>(_ => new LauncherPaths(root, mc));
        }

        services.AddCoreLogging();

        // 日志跟随：日志页的增量 tail 读取器。与 AddCoreLogging 分开一份，
        // 因为跟随只被日志页用到，而上面的写入器是全局地基。
        services.AddCoreLogFollow();

        // ---- Core 业务模块 ----
        services.AddCorePlatforms();
        services.AddCoreSettings();
        services.AddAccounts();
        services.AddCoreVersions();
        services.AddGameLaunching();
        services.AddInstances();
        services.AddInstanceLocks();
        services.AddInstanceFolders();
        services.AddCoreAssets();

        // 存档缩略图：世界页按需异步提取 level.dat 的 Data.icon，注册点与 BackupService 分开一份。
        services.AddCoreWorldIcons();

        // 模组更新检查：依赖 IModrinthService（必需）与 ICurseForgeService（可选）。
        // 必须在 AddCoreAssets 之后注册，否则它拿不到两个在线数据源。
        services.AddCoreModUpdates();

        // 配置包导出 / 导入：要拿到 IServerService 与 IVersionFavorites，所以排在它们之后。
        services.AddCoreProfileTransfer();

        // 存储统计与清理：只依赖 ILauncherPaths 与 IAppLog，与业务模块无顺序要求。
        services.AddCoreStorage();

        // 下载源测速：只读测量（自带不带镜像重写的 HttpClient），不参与下载决策。
        services.AddDownloadSourceProbe();

        // ---- UI 服务 ----
        services.AddSingleton<NavigationServiceProxy>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationServiceProxy>());

        // 窗口动作（最小化 / 关闭）同样走「先注册代理、窗口建好后挂接」。
        services.AddSingleton<WindowActionsProxy>();
        services.AddSingleton<IWindowActions>(sp => sp.GetRequiredService<WindowActionsProxy>());

        // ---- 皮肤（票 37 / 42 / 43 / 44） ----
        // 三条通道都注册成单例：换肤客户端持有 HttpClient，按页面重建会反复开连接。
        // 「皮肤已变更」广播必须是同一个实例，否则换肤页发的通知侧边栏收不到。
        services.AddSingleton<Skinning.SkinLibraryClient>();
        services.AddSingleton<Skinning.MojangSkinClient>();
        services.AddSingleton<Skinning.AuthlibSkinClient>();
        services.AddSingleton<Skinning.SkinChangeNotifier>(_ => Skinning.SkinChangeBus.Current);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 把默认 UI 字体注入资源字典。字体选择逻辑集中在 <see cref="Program.DefaultFontFamily"/>，
    /// 这里只负责写入，避免两处维护同一张平台表。
    /// </summary>
    private void ApplyPlatformFont() => Resources["Font.UI"] = Program.DefaultFontFamily;
}
