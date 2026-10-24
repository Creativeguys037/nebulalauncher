using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Platforms;

/// <summary>一条「从 stdin 读文本写进系统剪贴板」的外部命令（程序名 + 固定参数）。</summary>
internal readonly record struct ClipboardCommand(string FileName, string Arguments);

/// <summary>
/// 非 Windows 平台的剪贴板命令链。
///
/// 单独抽出来的原因：这些命令在本机（Windows）并不存在，测试里不能真启动它们；
/// 而「多个候选取哪个」「退出码多少算写成功」正是「失败被当成成功」那条审计问题的核心判据，
/// 抽成纯数据 + 纯函数之后才能逐条钉住。
/// </summary>
internal static class ClipboardCommandChain
{
    /// <summary>macOS 唯一候选。</summary>
    private static readonly ClipboardCommand[] MacOsCommands = [new("pbcopy", string.Empty)];

    /// <summary>
    /// Linux 候选顺序：先试 Wayland 的 wl-copy，再退到 X11 的 xclip。
    /// 反过来的话，Wayland 会话里 xclip 也能返回 0，但它写的是 XWayland 的 selection，
    /// 原生 Wayland 应用读不到——「命令成功」和「用户真能粘贴」不是一回事。
    /// </summary>
    private static readonly ClipboardCommand[] LinuxCommands =
    [
        new("wl-copy", string.Empty),
        new("xclip", "-selection clipboard"),
    ];

    /// <summary>按当前系统给出候选顺序；两个集合都非空，调用方至少会尝试一次。</summary>
    internal static IReadOnlyList<ClipboardCommand> For(bool isMacOs) => isMacOs ? MacOsCommands : LinuxCommands;

    /// <summary>退出码 0 才算真的写进剪贴板；非 0（含被信号杀死时的 128+N）一律视为失败。</summary>
    internal static bool Succeeded(int exitCode) => exitCode == 0;
}

/// <summary>
/// <see cref="IPlatformService"/> 的唯一实现。所有平台差异都走运行时分支，
/// 保证单 TFM（net8.0）下 Windows / macOS / Linux 共用同一份二进制。
/// </summary>
public sealed class PlatformService : IPlatformService
{
    private const int WindowMaterialMinBuild = 22000;

    /// <summary>外部剪贴板命令的等待上限。挂住的命令比失败更糟：调用方会一直等它退出。</summary>
    private const int ClipboardCommandTimeoutSeconds = 10;

    /// <summary>
    /// Win32 剪贴板不可用时的回退命令（只在 Windows 分支使用）。
    /// 参数保持原样：PowerShell 侧从 stdin 读全文再交给 Set-Clipboard，避免命令行长度与转义问题。
    /// </summary>
    private static readonly ClipboardCommand PowerShellClipboardCommand = new(
        "powershell.exe",
        "-NoProfile -NonInteractive -Command \"$s=[Console]::In.ReadToEnd(); Set-Clipboard -Value $s\"");

    private readonly IAppLog? _log;
    private readonly Lazy<IReadOnlyList<string>> _javaSearchRoots;
    private readonly Lazy<bool> _supportsWindowMaterial;

    private PlatformNative.ITaskbarList3? _taskbar;
    private IntPtr _taskbarWindow;

    public PlatformService(IAppLog? log = null)
    {
        _log = log;
        _javaSearchRoots = new Lazy<IReadOnlyList<string>>(DiscoverJavaSearchRoots);
        _supportsWindowMaterial = new Lazy<bool>(() => IsWindows && Environment.OSVersion.Version.Build >= WindowMaterialMinBuild);
    }

    /// <summary>
    /// 供 UI 层接管剪贴板的钩子。Core 不依赖 UI 框架，默认走平台原生命令；
    /// App 层可用 Avalonia 的剪贴板替换。
    /// </summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    public static bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsMacOs { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public string PlatformName => IsWindows ? "Windows" : IsMacOs ? "macOS" : IsLinux ? "Linux" : "Unknown";

    public bool SupportsWindowMaterial => _supportsWindowMaterial.Value;

    public bool SupportsSecureTokenStorage => IsWindows;

    public string DefaultMinecraftDirectory
    {
        get
        {
            if (IsWindows)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, ".minecraft");
            }

            var home = HomeDirectory;
            return IsMacOs
                ? Path.Combine(home, "Library", "Application Support", "minecraft")
                : Path.Combine(home, ".minecraft");
        }
    }

    public IReadOnlyList<string> JavaSearchRoots => _javaSearchRoots.Value;

    public string JavaExecutableName => IsWindows ? "javaw.exe" : "java";

    private static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public Task OpenFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _log?.Warn($"创建目录失败：{path}", ex);
        }

        return StartShellAsync(IsMacOs ? "open" : IsLinux ? "xdg-open" : "explorer.exe", Quote(path));
    }

    public Task OpenUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.CompletedTask;
        }

        if (IsWindows)
        {
            return StartProcessAsync(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        return StartShellAsync(IsMacOs ? "open" : "xdg-open", Quote(url));
    }

    public Task RevealInFileManagerAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        var full = SafeGetFullPath(path);
        if (IsWindows)
        {
            // explorer 的 /select 参数要求「,」紧贴路径且整体被引号包裹。
            return StartShellAsync("explorer.exe", $"/select,\"{full}\"");
        }

        if (IsMacOs)
        {
            return StartShellAsync("open", $"-R {Quote(full)}");
        }

        return OpenFolderAsync(Path.GetDirectoryName(full) ?? full);
    }

    public Task CopyTextAsync(string text) => TryCopyTextAsync(text);

    /// <inheritdoc />
    public async Task<bool> TryCopyTextAsync(string text)
    {
        if (text is null)
        {
            return true;
        }

        var hook = ClipboardWriter;
        if (hook is not null)
        {
            try
            {
                await hook(text).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warn("自定义剪贴板写入失败，回退平台实现", ex);
            }
        }

        try
        {
            if (IsWindows)
            {
                // 原生 API 命中时行为不变：既不启动进程，也没有退出码要检查。
                if (PlatformNative.TrySetClipboardText(text))
                {
                    return true;
                }

                // Win32 剪贴板被别的进程占住时的回退。这里同样要看退出码：
                // PowerShell 起不来、Set-Clipboard 抛错时进程照样「跑完了」，只看有没有异常会漏报。
                if (await WriteThroughStdinAsync(PowerShellClipboardCommand, text).ConfigureAwait(false))
                {
                    return true;
                }

                _log?.Warn("写入剪贴板失败：原生 API 与 Set-Clipboard 回退都没有成功");
                return false;
            }

            foreach (var command in ClipboardCommandChain.For(IsMacOs))
            {
                if (await WriteThroughStdinAsync(command, text).ConfigureAwait(false))
                {
                    return true;
                }

                // 非 0 退出码说明这条候选没写成（例如 xclip 没装、没有 DISPLAY、Wayland 合成器拒绝），
                // 继续试下一条；日志里带上命令名，排查时不用猜是哪一条挂了。
                _log?.Warn($"剪贴板命令 {command.FileName} 写入失败，尝试下一个候选");
            }

            _log?.Warn($"写入剪贴板失败：{PlatformName} 上没有可用的剪贴板命令");
            return false;
        }
        catch (Exception ex)
        {
            _log?.Warn("写入剪贴板失败", ex);
            return false;
        }
    }

    public void ApplyWindowMaterial(IntPtr windowHandle, string material)
    {
        if (!IsWindows || windowHandle == IntPtr.Zero)
        {
            return;
        }

        var kind = NormalizeMaterial(material);
        try
        {
            // 深色标题栏与材质配套；Win10 1809+ 支持该属性，失败无害。
            var dark = 1;
            _ = PlatformNative.DwmSetWindowAttribute(
                windowHandle, PlatformNative.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            if (kind == "solid")
            {
                var none = (int)PlatformNative.BackdropType.None;
                _ = PlatformNative.DwmSetWindowAttribute(
                    windowHandle, PlatformNative.DwmwaSystemBackdropType, ref none, sizeof(int));
                return;
            }

            if (SupportsWindowMaterial)
            {
                var backdrop = kind == "acrylic"
                    ? (int)PlatformNative.BackdropType.Acrylic
                    : (int)PlatformNative.BackdropType.Mica;
                if (PlatformNative.DwmSetWindowAttribute(
                        windowHandle, PlatformNative.DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
                {
                    return;
                }
            }

            if (kind != "acrylic")
            {
                return;
            }

            // Windows 10 回退：ACCENT_ENABLE_ACRYLICBLURBEHIND。
            const int gradient = unchecked((int)0x99202020);
            PlatformNative.TrySetAccentState(windowHandle, PlatformNative.AccentState.EnableAcrylicBlurBehind, gradient);
        }
        catch (Exception ex)
        {
            _log?.Warn("应用窗口材质失败", ex);
        }
    }

    public void SetTaskbarProgress(IntPtr windowHandle, double progress)
    {
        if (!IsWindows || windowHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var taskbar = EnsureTaskbar(windowHandle);
            if (taskbar is null)
            {
                return;
            }

            if (progress < 0)
            {
                taskbar.SetProgressState(windowHandle, PlatformNative.TaskbarProgressState.NoProgress);
                return;
            }

            var clamped = Math.Clamp(progress, 0, 1);
            taskbar.SetProgressState(windowHandle, PlatformNative.TaskbarProgressState.Normal);
            taskbar.SetProgressValue(windowHandle, (ulong)(clamped * 1000), 1000);
        }
        catch (Exception ex)
        {
            // 任务栏指示属于锦上添花，任何失败都不影响启动器主流程。
            _log?.Debug($"设置任务栏进度失败：{ex.Message}");
            _taskbar = null;
        }
    }

    public IDisposable? AcquireSingleInstanceLock(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            return new MutexLock(mutex);
        }
        catch (Exception ex)
        {
            _log?.Warn($"创建单实例锁失败：{name}", ex);
            return null;
        }
    }

    // ---------------- 游戏窗口接管 ----------------

    /// <summary>Minecraft 的窗口类名（GLFW / SDL / LWJGL / AWT）。引擎变了这个列表也要跟着变。</summary>
    private static readonly string[] GameWindowClasses = ["GLFW30", "SDL_app", "LWJGL", "SunAwtFrame"];

    public IntPtr? FindGameWindow(int processId)
    {
        if (!IsWindows || processId <= 0)
        {
            return null;
        }

        try
        {
            var gameStartedAt = TryGetProcessStartTime(processId);
            IntPtr? candidate = null;

            foreach (var window in PlatformNative.EnumerateTopLevelWindows())
            {
                if (!GameWindowClasses.Contains(window.ClassName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(window.Title))
                {
                    continue;
                }

                // 引擎自带的辅助窗口：标题是占位符，最大化/改标题只会让人以为启动器坏了。
                if (IsEnginePlaceholderTitle(window.Title))
                {
                    continue;
                }

                // 加载器的启动画面（FML / Quilt Loader）：一闪而过，不当游戏主窗口。
                if (IsLoaderSplashTitle(window.Title))
                {
                    continue;
                }

                // SDL 的后台窗口不可见，改它的标题会「成功但没人看得到」。
                if (window.ClassName.Equals("SDL_app", StringComparison.OrdinalIgnoreCase) &&
                    !PlatformNative.IsWindowVisible(window.Handle))
                {
                    continue;
                }

                // 同进程的窗口一定属于这次启动，直接命中。
                if (window.ProcessId == processId)
                {
                    return window.Handle;
                }

                // 跨进程：只有启动时间不早于游戏进程的窗口才可能是这次运行产生的。
                if (gameStartedAt is null)
                {
                    continue;
                }

                var windowStartedAt = TryGetProcessStartTime(window.ProcessId);
                if (windowStartedAt is null || windowStartedAt < gameStartedAt)
                {
                    continue;
                }

                candidate ??= window.Handle;
            }

            return candidate;
        }
        catch (Exception ex)
        {
            // 句柄泄漏或权限不足都可能走到这里；窗口接管失败不影响游戏本身。
            _log?.Warn($"枚举游戏窗口失败（进程 {processId}）", ex);
            return null;
        }
    }

    public bool TrySetWindowTitle(IntPtr windowHandle, string title)
    {
        if (!IsWindows || windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        try
        {
            return PlatformNative.SetWindowText(windowHandle, title);
        }
        catch (Exception ex)
        {
            _log?.Warn("设置游戏窗口标题失败", ex);
            return false;
        }
    }

    public bool TryMaximizeWindow(IntPtr windowHandle)
    {
        if (!IsWindows || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return PlatformNative.ShowWindow(windowHandle, PlatformNative.SwMaximize);
        }
        catch (Exception ex)
        {
            _log?.Warn("最大化游戏窗口失败", ex);
            return false;
        }
    }

    // ---------------- 显卡首选项 ----------------

    /// <summary>Windows 存放「每个可执行文件用哪块显卡」的键（HKCU，无需提权）。</summary>
    internal const string GpuPreferenceSubKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>
    /// <c>GpuPreference=2;</c> 即「高性能」。PCL2 的写法（<c>ModMain.vb:802-813 SetGPUPreference</c>）
    /// 逐字节相同：值名是可执行文件全路径、值就是这个字符串，末尾分号不能省（系统按分号切分多段键值）。
    /// </summary>
    internal const string GpuPreferenceHighPerformance = "GpuPreference=2;";

    /// <inheritdoc />
    public bool TrySetGpuPreference(string exePath)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var fullPath = NormalizeGpuPreferenceValueName(exePath);

        if (fullPath.Length == 0)
        {
            return false;
        }

        try
        {
            // 键在用户从没设过图形首选项时并不存在。用 CreateSubKey 而不是 OpenSubKey：
            // 与系统「图形设置」自己建的是同一个键，只写 HKCU，不涉及提权。
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferenceSubKey, writable: true);
            if (key is null)
            {
                _log?.Debug("无法打开 UserGpuPreferences 键，跳过显卡设置");
                return false;
            }

            if (IsGpuPreferenceSatisfied(key.GetValue(fullPath) as string))
            {
                // 幂等：已经是高性能时不再写盘，避免每次启动都动一次注册表。
                _log?.Debug($"无需调整显卡设置：{fullPath}");
                return true;
            }

            key.SetValue(fullPath, GpuPreferenceHighPerformance, RegistryValueKind.String);
            _log?.Info($"已把 {fullPath} 注册为高性能 GPU");
            return true;
        }
        catch (Exception ex)
        {
            // 权限不足（组策略锁住 HKCU 的 DirectX 子键）、键被禁用等都可能走到这里。
            // 显卡设置失败不影响游戏能否启动，只记日志。
            _log?.Warn($"注册显卡首选项失败：{fullPath}", ex);
            return false;
        }
    }

    /// <summary>
    /// 注册表里「值名」的规范化：必须是可执行文件的绝对路径。
    ///
    /// Windows 的「图形设置」写的就是绝对路径，系统按这个字符串匹配进程；
    /// 相对路径写进去不会报错，只是永远不生效——所以这里统一转绝对路径，
    /// 转不了（非法字符、超长）就原样返回，让写入那一步自然失败并记日志。
    /// </summary>
    internal static string NormalizeGpuPreferenceValueName(string exePath)
    {
        var trimmed = (exePath ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return SafeGetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    /// <summary>
    /// 现有值是否已经要求「高性能」。单独抽出来是为了不起注册表就能验证幂等判据。
    ///
    /// 值是多段 <c>Key=Value;</c> 拼起来的，Windows 自己也会往同一个值里追加
    /// <c>SwapEffectUpgradeEnable=1;</c> 之类的段。所以判据必须是「其中有一段是
    /// <c>GpuPreference=2</c>」，而不是「整个字符串等于 <c>GpuPreference=2;</c>」——
    /// 后者会让每次启动都判定为未满足，进而把用户其它的段整片覆盖掉。
    /// </summary>
    internal static bool IsGpuPreferenceSatisfied(string? existingValue)
    {
        if (string.IsNullOrWhiteSpace(existingValue))
        {
            return false;
        }

        foreach (var segment in existingValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Equals("GpuPreference=2", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------- 内部实现 ----------------

    private PlatformNative.ITaskbarList3? EnsureTaskbar(IntPtr windowHandle)
    {
        if (_taskbar is not null && _taskbarWindow == windowHandle)
        {
            return _taskbar;
        }

        var created = PlatformNative.CreateTaskbarList();
        if (created is null)
        {
            return null;
        }

        _taskbar = created;
        _taskbarWindow = windowHandle;
        return _taskbar;
    }

    /// <summary>引擎自己创建的辅助窗口，标题是类名或占位串，不是游戏主窗口。</summary>
    private static bool IsEnginePlaceholderTitle(string title) =>
        title.Equals("SDL_app", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("PopupMessageWindow", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("GLFW", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Hidden Utility Window", StringComparison.OrdinalIgnoreCase);

    /// <summary>加载器启动画面（Forge 的 FML、Quilt Loader）的标题前缀。</summary>
    private static bool IsLoaderSplashTitle(string title) =>
        title.StartsWith("FML", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("Quilt Loader", StringComparison.OrdinalIgnoreCase);

    /// <summary>进程启动时间；进程已退出或权限不足时返回 null（32 位进程读 64 位进程会失败）。</summary>
    private static DateTime? TryGetProcessStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NormalizeMaterial(string? material)
    {
        var value = (material ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "mica" or "mica-alt" or "tabbed" => "mica",
            "acrylic" or "blur" => "acrylic",
            _ => "solid",
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "对外暴露为 IReadOnlyList，内部保持只读语义")]
    private IReadOnlyList<string> DiscoverJavaSearchRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(path);
            }
        }

        if (IsWindows)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var home = HomeDirectory;

            Add(Path.Combine(programFiles, "Java"));
            Add(Path.Combine(programFiles, "Eclipse Adoptium"));
            Add(Path.Combine(programFiles, "Eclipse Foundation"));
            Add(Path.Combine(programFiles, "Zulu"));
            Add(Path.Combine(programFiles, "Microsoft"));
            Add(Path.Combine(programFiles, "BellSoft"));
            Add(Path.Combine(programFiles, "Amazon Corretto"));
            Add(Path.Combine(programFiles, "AdoptOpenJDK"));
            Add(Path.Combine(programFiles, "Semeru"));
            Add(Path.Combine(programFilesX86, "Java"));
            Add(Path.Combine(localAppData, "Programs"));
            Add(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));
            Add(Path.Combine(home, ".jdks"));
            Add(Path.Combine(home, "scoop", "apps"));

            // Mojang 官方启动器自带的 runtime（JavaManager.java:431-443 的同一组目录）。
            // 「只装过官方启动器」的机器上这是唯一存在的 Java，扫不到就只能让用户重下上百 MB。
            Add(Path.Combine(appData, ".minecraft", "runtime"));

            // 微软商店版启动器把 runtime 放在包容器里。
            Add(Path.Combine(
                localAppData,
                "Packages",
                "Microsoft.4297127D64EC6_8wekyb3d8bbwe",
                "LocalCache",
                "Local",
                "runtime"));

            // 旧版官方启动器的安装位置；32 位系统上 ProgramFilesX86 可能为空，空串会拼出相对路径，先挡掉。
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                Add(Path.Combine(programFilesX86, "Minecraft Launcher", "runtime"));
            }

            // 其他启动器自己管理的 Java（HMCL / PrismLauncher / Modrinth App）。
            Add(Path.Combine(appData, ".hmcl", "java"));
            Add(Path.Combine(appData, "PrismLauncher", "java"));
            Add(Path.Combine(appData, "ModrinthApp", "meta", "java_versions"));

            foreach (var home1 in ReadJavaHomesFromRegistry())
            {
                Add(home1);
            }
        }
        else if (IsMacOs)
        {
            var home = HomeDirectory;
            Add("/Library/Java/JavaVirtualMachines");
            Add(Path.Combine(home, "Library", "Java", "JavaVirtualMachines"));
            Add("/opt/homebrew/opt");
            Add("/usr/local/opt");
            Add(Path.Combine(home, ".sdkman", "candidates", "java"));

            foreach (var home2 in ReadMacJavaHomes())
            {
                Add(home2);
            }
        }
        else if (IsLinux)
        {
            var home = HomeDirectory;
            Add("/usr/lib/jvm");
            Add("/usr/java");
            Add("/opt/java");
            Add("/opt/jdk");
            Add(Path.Combine(home, ".sdkman", "candidates", "java"));
            Add(Path.Combine(home, ".jdks"));
        }

        return roots;
    }

    /// <summary>
    /// 读 HKLM\SOFTWARE\JavaSoft\{JDK,JRE}\&lt;版本&gt;\JavaHome。
    /// 属于可选增强：系统没有 Java 或权限不足时静默返回空集合。
    /// </summary>
    private IReadOnlyList<string> ReadJavaHomesFromRegistry()
    {
        if (!IsWindows)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var vendor in new[] { "JavaSoft", @"JavaSoft\JDK", @"JavaSoft\JRE" })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var vendorKey = baseKey.OpenSubKey($@"SOFTWARE\{vendor}");
                    if (vendorKey is null)
                    {
                        continue;
                    }

                    foreach (var versionName in vendorKey.GetSubKeyNames())
                    {
                        using var versionKey = vendorKey.OpenSubKey(versionName);
                        if (versionKey?.GetValue("JavaHome") is string home && !string.IsNullOrWhiteSpace(home))
                        {
                            result.Add(home);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Debug($"读取注册表 JavaSoft 失败（{view}/{vendor}）：{ex.Message}");
                }
            }
        }

        return result;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "对外暴露为 IReadOnlyList，内部保持只读语义")]
    private IReadOnlyList<string> ReadMacJavaHomes()
    {
        var result = new List<string>();
        try
        {
            // /usr/libexec/java_home -V 把候选列表写到 stderr。
            var stdout = RunCapture("/usr/libexec/java_home", string.Empty, 2000);
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                result.Add(stdout.Trim());
            }

            var verbose = RunCapture("/usr/libexec/java_home", "-V", 3000);
            foreach (var line in verbose.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var marker = line.IndexOf("/Library/Java/", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    result.Add(line[marker..].Trim());
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Debug($"探测 macOS java_home 失败：{ex.Message}");
        }

        return result;
    }

    private static string RunCapture(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            return string.Empty;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程已经退出。
            }

            return string.Empty;
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private async Task StartShellAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"执行 {fileName} 失败", ex);
        }
    }

    private async Task StartProcessAsync(ProcessStartInfo info)
    {
        try
        {
            using var process = new Process { StartInfo = info };
            process.Start();
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"打开 {info.FileName} 失败", ex);
        }
    }

    /// <summary>
    /// 把文本从 stdin 交给一条剪贴板命令，并等它退出。
    ///
    /// 返回是否真的写成功。进程起不来、stdin 写不进去、等待超时、退出码非 0 都算失败——
    /// 这些命令失败时通常只在 stderr 里说一句，不看退出码就会把「跑完了」当成「写进去了」。
    /// 异常在这里收口：上层只需要换下一条候选或记日志，不必按异常类型分流。
    /// </summary>
    private async Task<bool> WriteThroughStdinAsync(ClipboardCommand command, string text)
    {
        var info = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };

        using var process = new Process { StartInfo = info };

        int exitCode;
        try
        {
            if (!process.Start())
            {
                _log?.Warn($"启动剪贴板命令失败：{command.FileName}");
                return false;
            }

            // 超时同时覆盖「写 stdin」与「等退出」：命令在读取 stdin 之前就挂住时
            // （等锁、等显示、被安全软件拦住），只包住 WaitForExit 会让调用方无限等待。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ClipboardCommandTimeoutSeconds));

            try
            {
                await process.StandardInput.WriteAsync(text.AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log?.Warn($"剪贴板命令写入超时（{ClipboardCommandTimeoutSeconds}s）：{command.FileName}");
                TryKillProcess(process);
                return false;
            }
            catch (Exception ex)
            {
                // 命令读完就退出时管道会断（例如参数不被接受，立刻打了 usage 就退出）。
                _log?.Warn($"剪贴板命令读取 stdin 失败：{command.FileName}", ex);
                return false;
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // 进程已经退出，管道已经关闭。
                }
            }

            timeout.CancelAfter(TimeSpan.FromSeconds(ClipboardCommandTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 剪贴板工具挂住（等 selection 所有者之类）时不能把调用方一起拖死。
                _log?.Warn($"剪贴板命令超时（{ClipboardCommandTimeoutSeconds}s）：{command.FileName}");
                TryKillProcess(process);
                return false;
            }

            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            // 命令不存在（Win32Exception）、没有权限、桌面环境不完整：都算这条候选失败。
            _log?.Warn($"执行剪贴板命令失败：{command.FileName}", ex);
            return false;
        }

        if (!ClipboardCommandChain.Succeeded(exitCode))
        {
            _log?.Warn($"剪贴板命令返回非 0 退出码：{command.FileName} → {exitCode}");
            return false;
        }

        return true;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已经自己退出了。
        }
    }

    private static string SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private sealed class MutexLock(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // 未持有或被其他线程释放。
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
