using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Paths;

/// <summary>
/// <see cref="ILauncherPaths"/> 的默认实现。
///
/// 启动器数据目录与 .minecraft 目录分开：
/// - 启动器数据（设置、账户、备份、缓存）属于启动器自己，放平台惯例位置；
/// - .minecraft 属于游戏，放在用户熟悉的位置，且可被设置覆盖。
///
/// 单 TFM 下全部用运行时分支，不做编译期平台开关，以便未来直接 publish -r osx-arm64。
/// </summary>
public sealed class LauncherPaths : ILauncherPaths
{
    private readonly object _sync = new();
    private readonly List<PathCreationFailure> _failures = new();

    private string _minecraftDirectory;
    private bool _initialized;

    public LauncherPaths()
        : this(null, null)
    {
    }

    /// <param name="rootOverride">仅测试使用；指定后不使用平台默认位置。</param>
    /// <param name="minecraftOverride">仅测试使用。</param>
    /// <param name="enableFallback">
    /// 是否允许在默认目录不可写时回退到启动器目录。测试可关掉，以便断言平台默认值。
    /// </param>
    public LauncherPaths(string? rootOverride, string? minecraftOverride, bool enableFallback = true)
    {
        // 启动器数据目录要真的能写：受限环境（安全软件/受控目录策略）会把进程限制在
        // 「可执行文件所在目录树」内，此时 %APPDATA% 连 settings.json 都写不进去。
        // 这里按 平台默认 → LocalAppData → <exe目录>/data 的顺序选第一个可写的。
        RootDirectory = string.IsNullOrWhiteSpace(rootOverride)
            ? (enableFallback ? ResolveWritableRoot() : ResolveDefaultRoot())
            : Path.GetFullPath(rootOverride);

        // 优先级：命令行/测试覆盖 > 配置文件里的自定义目录 > 平台默认（不可写则退到启动器目录下）。
        // 在构造函数里读取配置，是因为 UI 各页面在构造时就会缓存 MinecraftDirectory，
        // 若等设置页加载后再切换，首个页面已经指向了错误目录。
        if (!string.IsNullOrWhiteSpace(minecraftOverride))
        {
            _minecraftDirectory = Path.GetFullPath(minecraftOverride);
        }
        else
        {
            _minecraftDirectory = ResolveConfiguredMinecraft(RootDirectory)
                                  ?? (enableFallback ? ResolveWritableMinecraft(RootDirectory) : ResolveDefaultMinecraft());
        }
    }

    /// <summary>
    /// 选一个实际能写的启动器数据根目录。全部不可写时返回平台默认值，
    /// 让失败暴露在具体读写操作上，而不是在这里静默改变用户预期。
    /// </summary>
    private static string ResolveWritableRoot()
    {
        var preferred = ResolveDefaultRoot();
        if (PathProbe.TestWritable(preferred) is null)
        {
            return preferred;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var local = Path.Combine(localAppData, "NebulaLauncher");
            if (PathProbe.TestWritable(local) is null)
            {
                return local;
            }
        }

        // 受限环境通常放行「程序自身目录」：把数据放在 exe 旁边的 data/。
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (PathProbe.TestWritable(portable) is null)
        {
            return portable;
        }

        return preferred;
    }

    /// <summary>
    /// 默认 .minecraft 不可写时，退到启动器数据目录下的 minecraft。
    /// 这一步只影响「没有显式配置自定义目录」的情况；用户手动指定的路径仍原样使用。
    /// </summary>
    private static string ResolveWritableMinecraft(string rootDirectory)
    {
        var standard = ResolveDefaultMinecraft();
        if (PathProbe.TestWritable(standard) is null)
        {
            return standard;
        }

        var fallback = Path.Combine(rootDirectory, "minecraft");
        if (PathProbe.TestWritable(fallback) is null)
        {
            return fallback;
        }

        return standard;
    }

    public string RootDirectory { get; }

    public string MinecraftDirectory
    {
        get
        {
            lock (_sync)
            {
                return _minecraftDirectory;
            }
        }
    }

    public string VersionsDirectory => Path.Combine(MinecraftDirectory, "versions");

    public string LibrariesDirectory => Path.Combine(MinecraftDirectory, "libraries");

    public string AssetsDirectory => Path.Combine(MinecraftDirectory, "assets");

    /// <summary>
    /// 旧版实例根（<c>&lt;data&gt;/instances</c>）。**不再是实例目录**：
    /// 实例目录已改成「版本隔离」布局 <see cref="VersionsDirectory"/><c>/&lt;版本名&gt;/</c>，
    /// 与 PCL2 / HMCL 一致。保留这个属性只为兼容旧的布局与配置包，别再往这里写东西。
    /// </summary>
    public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string CacheDirectory => Path.Combine(RootDirectory, "cache");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string AccountsFile => Path.Combine(RootDirectory, "accounts.json");

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string ServersFile => Path.Combine(RootDirectory, "servers.json");

    /// <summary>最近一次 <see cref="EnsureCreated"/> 中创建失败的目录，按发生顺序。</summary>
    public IReadOnlyList<PathCreationFailure> CreationFailures
    {
        get
        {
            lock (_sync)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <summary>切换 .minecraft 目录；传空字符串表示恢复平台默认目录。</summary>
    public void SetMinecraftDirectory(string path)
    {
        var target = string.IsNullOrWhiteSpace(path)
            ? ResolveDefaultMinecraft()
            : Path.GetFullPath(path);

        lock (_sync)
        {
            if (string.Equals(_minecraftDirectory, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _minecraftDirectory = target;
            _initialized = false;
        }

        EnsureCreated();
    }

    /// <summary>
    /// 幂等地创建全部目录。
    ///
    /// 这里刻意不抛异常：这些目录里有相当一部分与「当前操作」无关——
    /// 例如安装游戏只需要 versions/libraries/assets，却曾被 instances 目录（实例隔离用）
    /// 创建失败连坐，表现为「Fabric 安装失败：Access to the path '...\instances' is denied」。
    /// 失败项记录在 <see cref="CreationFailures"/>，由调用方判断自己关心的目录是否可用。
    /// </summary>
    public void EnsureCreated()
    {
        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }
        }

        var failures = new List<PathCreationFailure>();

        foreach (var (kind, directory) in EnumerateManagedDirectories())
        {
            if (TryCreateDirectory(directory, out var error))
            {
                continue;
            }

            failures.Add(new PathCreationFailure(kind, directory, error!));
        }

        lock (_sync)
        {
            _failures.Clear();
            _failures.AddRange(failures);

            // 只有全部成功才固化状态；有失败时下次调用会重试，
            // 用户修好权限或关掉拦截后无需重启启动器。
            _initialized = failures.Count == 0;

            if (failures.Count == 0)
            {
                _failures.Clear();
            }
        }
    }

    private IEnumerable<(string Kind, string Path)> EnumerateManagedDirectories()
    {
        string minecraft;
        lock (_sync)
        {
            minecraft = _minecraftDirectory;
        }

        yield return ("启动器数据", RootDirectory);
        yield return ("日志", LogsDirectory);
        yield return ("缓存", CacheDirectory);
        yield return ("备份", BackupsDirectory);
        // 不再预建 instances/：实例目录已经是 <game>/versions/<版本名>，这里只有一个
        // 向后兼容属性，建空目录只会让用户以为实例还在数据目录里。
        yield return ("游戏目录", minecraft);
        yield return ("版本", Path.Combine(minecraft, "versions"));
        yield return ("库文件", Path.Combine(minecraft, "libraries"));
        yield return ("资源文件", Path.Combine(minecraft, "assets"));
    }

    private static bool TryCreateDirectory(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>启动器数据根目录。</summary>
    public static string ResolveDefaultRoot()
    {
        if (IsWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                appData = Path.Combine(Home, "AppData", "Roaming");
            }

            return Path.Combine(appData, "NebulaLauncher");
        }

        if (IsMacOs)
        {
            return Path.Combine(Home, "Library", "Application Support", "NebulaLauncher");
        }

        // Linux / 其他：遵循 XDG。
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home, ".local", "share") : xdg;
        return Path.Combine(dataHome, "nebula-launcher");
    }

    /// <summary>用户默认的 .minecraft 目录。</summary>
    public static string ResolveDefaultMinecraft()
    {
        if (IsWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                appData = Path.Combine(Home, "AppData", "Roaming");
            }

            return Path.Combine(appData, ".minecraft");
        }

        if (IsMacOs)
        {
            return Path.Combine(Home, "Library", "Application Support", "minecraft");
        }

        return Path.Combine(Home, ".minecraft");
    }

    /// <summary>
    /// 读取 <c>settings.json</c> 里的自定义游戏目录。
    ///
    /// 这里不依赖 <c>SettingsService</c>：路径服务是最早构造的组件，而设置服务依赖它。
    /// 只解析一个字段，解析失败一律回退默认目录，坏配置不能阻断启动。
    /// </summary>
    private static string? ResolveConfiguredMinecraft(string rootDirectory)
    {
        try
        {
            var file = Path.Combine(rootDirectory, "settings.json");
            if (!File.Exists(file))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // System.Text.Json 的 Web 默认写 camelCase，但用户手改时可能写成 PascalCase。
                if (!string.Equals(property.Name, "minecraftDirectory", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value!);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>一个创建失败的目录。用于在界面上指出「哪个目录不可用、原因是什么」。</summary>
public sealed record PathCreationFailure(string Kind, string Path, string Message);

/// <summary>路径服务的 DI 注册。必须最先调用——其余模块都依赖 <see cref="ILauncherPaths"/>。</summary>
public static class PathsServiceCollectionExtensions
{
    public static IServiceCollection AddLauncherPaths(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILauncherPaths>(_ => new LauncherPaths());
        return services;
    }
}
