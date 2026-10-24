namespace NebulaLauncher.Core.Abstractions;

/// <summary>启动器运行期所有路径的唯一来源。所有目录在首次访问前必须存在。</summary>
public interface ILauncherPaths
{
    /// <summary>启动器自身数据根目录。</summary>
    string RootDirectory { get; }

    /// <summary>当前生效的 .minecraft 目录（可能是用户自定义的）。</summary>
    string MinecraftDirectory { get; }

    string VersionsDirectory { get; }

    string LibrariesDirectory { get; }

    string AssetsDirectory { get; }

    /// <summary>
    /// 旧版实例根（<c>&lt;data&gt;/instances</c>）。实例目录现在是版本目录
    /// <see cref="VersionsDirectory"/> 下的 <c>&lt;版本名&gt;</c>（与 PCL2 / HMCL 一致），
    /// 这个属性只为读取历史布局而保留，新代码不要往这里写。
    /// </summary>
    string InstancesDirectory { get; }

    string LogsDirectory { get; }

    string CacheDirectory { get; }

    string BackupsDirectory { get; }

    string AccountsFile { get; }

    string SettingsFile { get; }

    string ServersFile { get; }

    /// <summary>切换 .minecraft 目录；已存在的目录会被复用。传空字符串表示恢复平台默认目录。</summary>
    void SetMinecraftDirectory(string path);

    /// <summary>幂等地创建全部必需目录。实现必须逐目录容错，单个目录失败不得抛异常。</summary>
    void EnsureCreated();
}

/// <summary>平台差异集中在这里。Core 与 UI 都不得直接写 <c>#if WINDOWS</c> 之外的平台判断。</summary>
public interface IPlatformService
{
    /// <summary>面向用户展示的平台名：<c>Windows</c> / <c>macOS</c> / <c>Linux</c>。</summary>
    string PlatformName { get; }

    /// <summary>是否支持 Mica / Acrylic 窗口材质（仅 Windows 11+）。</summary>
    bool SupportsWindowMaterial { get; }

    /// <summary>是否支持 DPAPI 级别的账户令牌加密。Unix 上退化为文件权限保护。</summary>
    bool SupportsSecureTokenStorage { get; }

    /// <summary>当前用户的默认 .minecraft 绝对路径。</summary>
    string DefaultMinecraftDirectory { get; }

    /// <summary>Java 扫描的候选根目录（Windows 含注册表与 Program Files，macOS 含 /Library/Java）。</summary>
    IReadOnlyList<string> JavaSearchRoots { get; }

    /// <summary>Windows 下为 <c>javaw.exe</c>，其他平台为 <c>java</c>。</summary>
    string JavaExecutableName { get; }

    Task OpenFolderAsync(string path);

    Task OpenUrlAsync(string url);

    /// <summary>在文件管理器中定位并选中目标文件。不支持时退化为打开父目录。</summary>
    Task RevealInFileManagerAsync(string path);

    Task CopyTextAsync(string text);

    /// <summary>
    /// 尝试把文本写入剪贴板，返回是否成功。
    ///
    /// 默认实现走 <see cref="CopyTextAsync"/> 并假定成功（测试替身保持旧语义）；
    /// <c>PlatformService</c> 覆盖它，把「原生 API 失败 + 回退命令全部失败」如实返回。
    /// 调用方应优先用这个重载，才能把失败告诉用户，而不是无条件显示「已复制」
    /// （REVIEW-R34-R36 P2-5）。
    /// </summary>
    async Task<bool> TryCopyTextAsync(string text)
    {
        await CopyTextAsync(text).ConfigureAwait(false);
        return true;
    }

    /// <summary>设置窗口背景材质。非 Windows 平台或无效 handle 时直接返回。</summary>
    void ApplyWindowMaterial(IntPtr windowHandle, string material);

    /// <summary>设置任务栏 / Dock 进度条。0–1，&lt;0 表示清除。</summary>
    void SetTaskbarProgress(IntPtr windowHandle, double progress);

    /// <summary>创建实例级互斥量，实现单实例启动。已存在时返回 null。</summary>
    IDisposable? AcquireSingleInstanceLock(string name);

    // ==================== 游戏窗口接管 ====================

    /// <summary>
    /// 查找某个游戏进程的顶层窗口，找不到时返回 null。
    ///
    /// 为什么要按「类名 + 进程启动时间」筛选而不是只看进程：Minecraft 的窗口类是引擎固定的
    /// （<c>GLFW30</c> / <c>SDL_app</c> / <c>LWJGL</c> / <c>SunAwtFrame</c>），但同名的加载器
    /// 启动画面（FML / Quilt Loader）也可能是同一个进程的窗口；启动时间比较能把上次运行残留的
    /// 同名窗口排除掉，避免把标题改到已经退出的旧窗口上。
    ///
    /// 默认实现返回 null（非 Windows 平台、测试替身都不需要窗口能力）。
    /// </summary>
    /// <param name="processId">游戏进程 Id。</param>
    IntPtr? FindGameWindow(int processId) => null;

    /// <summary>改写窗口标题。失败返回 false，调用方只记日志。</summary>
    /// <param name="windowHandle">窗口句柄，来自 <see cref="FindGameWindow"/>。</param>
    /// <param name="title">新标题。</param>
    bool TrySetWindowTitle(IntPtr windowHandle, string title) => false;

    /// <summary>最大化窗口。失败返回 false，调用方只记日志。</summary>
    /// <param name="windowHandle">窗口句柄，来自 <see cref="FindGameWindow"/>。</param>
    bool TryMaximizeWindow(IntPtr windowHandle) => false;

    // ==================== 显卡首选项 ====================

    /// <summary>
    /// 把某个可执行文件注册为「高性能 GPU」启动（Windows 的
    /// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>，值名是可执行文件全路径、
    /// 值是 <c>GpuPreference=2;</c>）。
    ///
    /// 为什么由启动器代做：这一步在系统「设置 → 显示 → 图形」里要点击五次，而双显卡笔记本上
    /// 不做的后果是游戏跑在核显上——帧数掉一半，且用户不知道有这一步。
    ///
    /// 只写 <c>HKCU</c>，绝不碰 <c>HKLM</c>：后者需要提权，而且改的是全机器的设置。
    /// 已经是目标值时直接返回 true（幂等），不重写注册表。
    /// 非 Windows 平台、路径非法、权限不足一律返回 false 且不抛异常——它属于「锦上添花」，
    /// 失败只记日志，绝不能拦住启动。默认实现直接返回 false。
    /// </summary>
    /// <param name="exePath">要注册的 Java 可执行文件路径（通常是 <c>javaw.exe</c>）。</param>
    /// <returns>true 表示已写入或本来就是目标值；false 表示没有写成。</returns>
    bool TrySetGpuPreference(string exePath) => false;
}

/// <summary>日志出口。实现必须线程安全且不抛异常。</summary>
public interface IAppLog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message, Exception? ex = null);

    void Error(string message, Exception? ex = null);

    /// <summary>当前日志文件绝对路径，用于「打开日志」入口。</summary>
    string LogFilePath { get; }
}
