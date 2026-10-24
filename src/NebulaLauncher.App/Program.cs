using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using NebulaLauncher.Services;

namespace NebulaLauncher;

internal static class Program
{
    // Avalonia 初始化必须在任何 UI 代码之前完成；此处不做业务初始化。
    [STAThread]
    public static void Main(string[] args)
    {
        // 目录诊断：用启动器自身进程（未签名）复现写入失败，
        // 用于区分「目录 ACL 问题」与「安全软件按进程拦截」。
        if (TryProbeDirectory(args))
        {
            return;
        }

        // 源测速：同样是「跑完就退出」的无界面入口，放在 Avalonia 初始化之前。
        if (TryProbeSources(args))
        {
            return;
        }

        // JSON 落盘通路自检：裁剪发布包里唯一能证明「System.Text.Json 元数据还在」的入口（#26）。
        if (TrySelfTestJson(args))
        {
            return;
        }

        // 数据目录写不进去时请求提权重启。实测：同一份 exe，Medium 进程写不了
        // %APPDATA%\NebulaLauncher，High（管理员）进程可以。放在这里而不是 UI 里，
        // 是因为目录回退只能让程序「能跑」，解决不了用户想用自己目录的需求。
        if (TryRelaunchElevated(args))
        {
            return;
        }

        // 单实例放在提权之后：提权会用同一个 exe 重启自己，此时父进程还没退出，
        // 放在前面会把「自己的子进程」当成重复实例挡掉。
        if (TryRedirectToRunningInstance(args))
        {
            return;
        }

        ScreenshotMode.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// 已有一个启动器在跑时，把它的窗口唤到前台并让新进程退出（PCL2 的 <c>PCL_SingletonMutex</c>）。
    ///
    /// 截图 / 目录探测 / 源测速 / 显式 <c>--no-single-instance</c> 豁免：截图脚本会并行起多个实例，
    /// 单实例会直接把后面的进程挡掉；测速是脚本调用，不应被用户正开着的启动器拦下。
    /// </summary>
    /// <returns>true 表示已有实例在运行，调用方应立即退出。</returns>
    private static bool TryRedirectToRunningInstance(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg is "--screenshot" or "--probe-dir" or "--probe-sources" or "--selftest-json" or "--no-single-instance")
            {
                return false;
            }
        }

        try
        {
            // Local\ 前缀：只在当前登录会话内唯一，不跨用户抢占同一台机器上的其他账户。
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                @"Local\NebulaLauncher.Singleton",
                out var createdNew);

            if (createdNew)
            {
                return false;
            }

            ActivateExistingWindow();
            return true;
        }
        catch (Exception)
        {
            // 命名互斥量建不出来（权限受限、平台不支持）时宁可允许多开，也不要启动不了。
            return false;
        }
    }

    /// <summary>找到另一个同名进程的可见主窗口并前置。失败静默——用户已经知道启动器在运行。</summary>
    private static void ActivateExistingWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var current = Process.GetCurrentProcess();

            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                var handle = process.MainWindowHandle;

                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SwRestore);
                    SetForegroundWindow(handle);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // 唤醒只是顺手的体验优化，失败不影响「新进程退出」这个决定。
        }
    }

    /// <summary>进程级单实例锁。必须被静态字段持有，回收后锁就释放了。</summary>
    private static Mutex? _singleInstanceMutex;

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 默认数据目录不可写且当前不是管理员时，用 runas 重启自身。
    /// 用户取消 UAC 就继续以当前权限运行（走 LauncherPaths 的目录回退）。
    /// </summary>
    private static bool TryRelaunchElevated(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // --elevated 是提权后追加的标记，防止 UAC 取消/失败时反复重启。
        // --screenshot / --probe-sources 是无人值守入口：不能弹 UAC，否则脚本会卡在确认框上。
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--elevated", StringComparison.Ordinal)
                || string.Equals(arg, "--no-elevate", StringComparison.Ordinal)
                || string.Equals(arg, "--screenshot", StringComparison.Ordinal)
                || string.Equals(arg, "--probe-sources", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (IsElevated())
        {
            return false;
        }

        var root = NebulaLauncher.Core.Paths.LauncherPaths.ResolveDefaultRoot();
        if (NebulaLauncher.Core.Paths.PathProbe.TestWritable(root) is null)
        {
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.ArgumentList.Add("--elevated");

            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 上点了「否」。
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>当前进程是否以管理员（High 完整性）运行。</summary>
    private static bool IsElevated()
    {
        if (!Tok.IsWindows)
        {
            return false;
        }

        if (!Tok.OpenProcessToken(Tok.GetCurrentProcess(), Tok.TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                return Tok.GetTokenInformation(token, Tok.TokenElevation, buffer, sizeof(int), out _)
                       && Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Tok.CloseHandle(token);
        }
    }

    /// <summary>最小的令牌查询 P/Invoke，避免为一个 bool 引入 System.Security.Principal.Windows。</summary>
    private static class Tok
    {
        public const uint TokenQuery = 0x0008;
        public const int TokenElevation = 20;

        public static bool IsWindows { get; } = OperatingSystem.IsWindows();

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>NebulaLauncher.exe --probe-dir &lt;目录&gt;：打印可写性探测结果后退出。</summary>
    private static bool TryProbeDirectory(string[] args)
    {
        var index = Array.IndexOf(args, "--probe-dir");
        if (index < 0 || index + 1 >= args.Length)
        {
            return false;
        }

        var path = args[index + 1];
        var lines = new List<string>
        {
            $"path={path}",
            $"exists={Directory.Exists(path)}",
        };

        try
        {
            lines.Add($"attributes={new DirectoryInfo(path).Attributes}");
        }
        catch (Exception ex)
        {
            lines.Add($"attributes-error={ex.GetType().Name}: {ex.Message}");
        }

        var reason = NebulaLauncher.Core.Paths.PathProbe.TestWritable(path);
        lines.Add(reason is null ? "writable=YES" : $"writable=NO;{reason}");

        var output = string.Join(Environment.NewLine, lines);

        try
        {
            Console.WriteLine(output);
        }
        catch (Exception)
        {
            // 无控制台时忽略；下面还有文件输出。
        }

        // 诊断文件写在 exe 同级目录：临时目录可能被安全软件重定向或拦写。
        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "nebula-probe-dir.txt"),
                output,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            // 诊断输出失败不影响退出码。
        }

        return true;
    }

    /// <summary>
    /// NebulaLauncher.exe --selftest-json：在产物自己的进程里跑一遍会落盘的 JSON 往返，打印结果后退出。
    ///
    /// 存在的理由（#26）：<c>PublishTrimmed=true</c> 关掉 System.Text.Json 的反射元数据后，
    /// 漏改的调用点不会在编译期报错，只在用户点「保存设置」时炸成一句中文提示。
    /// 单元测试跑的是未裁剪的 Debug 程序集，够不到这个面；只有真实产物能证明它是好的。
    ///
    /// 输出可编程：每行 <c>json-selfcheck.OK.&lt;名称&gt;</c> / <c>json-selfcheck.FAIL.&lt;名称&gt;</c>，
    /// 末尾 <c>json-selfcheck.summary=passed/total</c>。退出码 0 = 全通过，4 = 有失败项。
    /// </summary>
    private static bool TrySelfTestJson(string[] args)
    {
        if (Array.IndexOf(args, "--selftest-json") < 0)
        {
            return false;
        }

        IReadOnlyList<string> lines;

        try
        {
            // Main 不是 async：Avalonia 的 [STAThread] 要求它在原线程启动，这里单独等一次。
            // 工作目录传产物目录而不是 %TEMP%：本机安全软件会拦这个 exe 写临时目录。
            lines = NebulaLauncher.Core.Diagnostics.JsonSelfCheck
                .RunAsync(AppContext.BaseDirectory)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            lines = new[] { $"json-selfcheck.FAIL.unexpected;{ex.GetType().Name}: {ex.Message}" };
        }

        var failed = lines.Any(line => line.StartsWith("json-selfcheck.FAIL", StringComparison.Ordinal));
        var output = string.Join(Environment.NewLine, lines);

        try
        {
            Console.WriteLine(output);
        }
        catch (Exception)
        {
            // 无控制台时忽略；下面是文件输出。
        }

        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "nebula-selftest-json.txt"),
                output,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            // 诊断输出失败不改退出码。
        }

        Environment.ExitCode = failed ? 4 : 0;
        return true;
    }

    /// <summary>
    /// NebulaLauncher.exe --probe-sources：真实测一次官方 / 镜像的首字节与吞吐，打印三行后退出。
    ///
    /// 和 <see cref="TryProbeDirectory"/> 同级放在 Avalonia 初始化之前：这是给脚本与排障用的，
    /// 不能起窗口、不能等用户关窗。可编程信号只有 stdout 三行与退出码
    /// （0 = 至少一侧可用，3 = 两侧都不可用，见 <see cref="NebulaLauncher.Core.Downloads.SourceProbeCli"/>）。
    /// </summary>
    private static bool TryProbeSources(string[] args)
    {
        if (Array.IndexOf(args, "--probe-sources") < 0)
        {
            return false;
        }

        // 与 DI 的 AddDownloadSourceProbe 保持同一套设置：不带 MirrorRewritingHandler
        // （否则两侧会被改写成同一个地址，测出来必然相等），超时交给 probe 自己的令牌。
        // 这里没有跑 ProxyConfigurator，走的是进程默认代理（HTTP_PROXY / 系统设置）——
        // 对命令行场景正是期望行为：用户在环境变量里配的代理应当生效。
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NebulaLauncher/1.0");

        using var probe = new NebulaLauncher.Core.Downloads.DownloadSourceProbe(client);

        NebulaLauncher.Core.Abstractions.SourceProbeReport report;

        try
        {
            // 同步等待：Main 不是 async（[STAThread] 下 Avalonia 必须回到原线程启动），
            // 而退出码要在 return 之前写进 Environment.ExitCode。此处 Avalonia 尚未初始化、
            // 没有 SynchronizationContext，probe 内部全部 ConfigureAwait(false)，
            // 不存在 CONVENTIONS §3 想防的 UI 线程死锁路径。
            report = probe.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // ProbeAsync 的契约是「除用户取消外不抛」，这里兜底只为让退出码永远是 0 或 3：
            // 真出现意外异常时把原因写进 stderr，脚本仍能按 both-failed 处理，不会当成崩溃。
            try
            {
                Console.Error.WriteLine("probe-sources failed: " + ex.Message);
            }
            catch (Exception)
            {
                // 无控制台时连 stderr 都写不出去，退出码仍然有效。
            }

            Environment.ExitCode = NebulaLauncher.Core.Downloads.SourceProbeCli.BothFailedExitCode;
            return true;
        }

        try
        {
            foreach (var line in NebulaLauncher.Core.Downloads.SourceProbeCli.FormatReport(report))
            {
                Console.WriteLine(line);
            }
        }
        catch (Exception)
        {
            // 没有可用控制台（例如从资源管理器双击）时忽略；退出码不受影响。
        }

        Environment.ExitCode = NebulaLauncher.Core.Downloads.SourceProbeCli.ExitCode(report);
        return true;
    }

    // Avalonia 设计器与单元测试会反射调用该方法，签名不可改。
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            // 只用 WGL（系统 OpenGL）与软件渲染，不用 ANGLE。
            //
            // ANGLE 会额外带一个 av_libglesv2.dll（约 5.2 MB），而它只是把 OpenGL ES
            // 翻译到 D3D 的中间层。Windows 上 WGL 直接走显卡驱动，覆盖绝大多数机器；
            // 驱动异常时 Avalonia 会自动回落到 Software 渲染。省下的这 5 MB 直接体现在下发体积上。
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software,
                },
            });
        }

        return builder;
    }

    /// <summary>
    /// 按平台设置默认字体。
    ///
    /// 之前用 <c>Avalonia.Fonts.Inter</c> 嵌入了整套 Inter（发布后 1.8 MB），但 Inter 不含 CJK，
    /// 界面实际一直在用系统字体回退，这份资源是纯体积负担。现在直接取系统字体，
    /// 缺字由系统字体链回落。
    /// </summary>
    public static FontFamily DefaultFontFamily
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return new FontFamily("Microsoft YaHei UI");
            }

            if (OperatingSystem.IsMacOS())
            {
                return new FontFamily("PingFang SC");
            }

            return new FontFamily("Noto Sans CJK SC");
        }
    }
}
