namespace NebulaLauncher.Core.Models;

/// <summary>
/// 下载源策略。
///
/// - <see cref="Auto"/>：先走镜像（BMCLAPI），本次会话内镜像被判劣化时自动降到官方；
///   判据与状态机在 <c>Downloads/DownloadSourceGovernor</c>。
/// - <see cref="Official"/>：官方 CDN 优先，不重写 URL。
/// - <see cref="Mirror"/>：BMCLAPI 镜像优先（拿不到时仍会退回官方，见 <c>DownloadMirror.Candidates</c>）。
///
/// 兼容性：<see cref="BmclApi"/> 是 <see cref="Mirror"/> 的旧名（同值）。改成三态时保留这个名字，
/// 因为 <c>settings.json</c> 里历史写入的是 <c>"BmclApi"</c>，反序列化要能读到同一个值；
/// 新代码一律用 <see cref="Mirror"/>。
/// </summary>
public enum DownloadSource
{
    Auto = 0,
    Official = 1,
    Mirror = 2,

    /// <summary>历史名称，等价于 <see cref="Mirror"/>。只为兼容旧配置与旧调用点保留。</summary>
    BmclApi = Mirror,
}

/// <summary>
/// HTTP 代理模式取值。存字符串而不是枚举，与 <see cref="AppSettings.Theme"/> /
/// <see cref="AppSettings.IsolationPolicy"/> 一致：拼错的旧配置由 <c>SettingsService.Normalize</c> 回落，
/// 不需要额外的 JSON 转换器。
/// </summary>
public static class ProxyModes
{
    /// <summary>不走代理（连环境变量里的 <c>HTTP_PROXY</c> 也忽略）。</summary>
    public const string None = "none";

    /// <summary>跟随系统代理（环境变量 + Windows 的 IE / 系统设置）。历史默认值。</summary>
    public const string System = "system";

    /// <summary>自定义 <c>scheme://host:port</c>，可选用户名密码。</summary>
    public const string Custom = "custom";
}

/// <summary>启动器全局设置。序列化为 <c>%APPDATA%/NebulaLauncher/settings.json</c>。</summary>
public sealed class AppSettings
{
    /// <summary>
    /// 自定义 .minecraft 目录；null 表示使用平台默认位置。
    ///
    /// 该字段由设置页「游戏 → 游戏目录」写入，冷启动时由 <c>LauncherPaths</c> 直接读取，
    /// 运行期保存后由主窗口订阅 <see cref="Abstractions.ISettingsService.Changed"/> 即时生效。
    /// </summary>
    public string? MinecraftDirectory { get; set; }

    /// <summary>手动指定的 Java 路径；null 表示自动检测。</summary>
    public string? JavaPath { get; set; }

    public int MinMemoryMb { get; set; } = 512;

    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>游戏窗口宽度。注意与启动器自身窗口的 <see cref="LauncherWindowWidth"/> 无关。</summary>
    public int WindowWidth { get; set; } = 854;

    public int WindowHeight { get; set; } = 480;

    public bool Fullscreen { get; set; }

    /// <summary>启动器窗口默认宽度（与 <c>MainWindow.axaml</c> 的初始值一致）。</summary>
    public const int DefaultLauncherWindowWidth = 1240;

    /// <summary>启动器窗口默认高度。</summary>
    public const int DefaultLauncherWindowHeight = 800;

    /// <summary>启动器窗口宽度下限，即窄屏验收尺寸（1024×660）。</summary>
    public const int MinimumLauncherWindowWidth = 1024;

    /// <summary>启动器窗口高度下限。</summary>
    public const int MinimumLauncherWindowHeight = 660;

    /// <summary>启动器窗口尺寸上限：挡住「拔掉外接屏后记录的超大尺寸」导致下次开不出窗口。</summary>
    public const int MaximumLauncherWindowSize = 8000;

    /// <summary>
    /// 启动器窗口宽度（像素）。关闭窗口（或隐藏到托盘）时写回，下次启动按这个尺寸打开。
    ///
    /// 以前启动器窗口写死在 <c>MainWindow.axaml</c> 的 1240×800，用户拖过的尺寸下次必丢；
    /// 而 <see cref="WindowWidth"/> 是**游戏窗口**尺寸，复用会把游戏分辨率写进启动器。
    /// </summary>
    public int LauncherWindowWidth { get; set; } = DefaultLauncherWindowWidth;

    /// <summary>启动器窗口高度（像素）。</summary>
    public int LauncherWindowHeight { get; set; } = DefaultLauncherWindowHeight;

    /// <summary>
    /// 启动器背景图片的绝对路径；空表示用主题底色。
    ///
    /// 只在 App 层解码成位图（<c>Bitmap</c> 是 Avalonia 类型，不进 Core）。
    /// 路径失效（文件被删/改名）时 App 层回退到底色并把原因写进状态栏，不写回设置。
    /// </summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>
    /// 背景图上的主题色遮罩强度（0–1）。值越大，图越淡、文字越清楚。
    /// 默认 0.55：不改设置时背景图也能看清前景内容。
    /// </summary>
    public double BackgroundImageOverlay { get; set; } = 0.55;

    /// <summary>
    /// 启动器窗口上次是否最大化。最大化时只记这个标记，尺寸仍保留上一次 Normal 的值，
    /// 否则「最大化 → 关闭 → 下次启动 → 还原」会把窗口还原成整屏大小。
    /// </summary>
    public bool LauncherWindowMaximized { get; set; }

    /// <summary>
    /// 是否记住启动器窗口尺寸与最大化状态。关闭时启动与关闭两条路径都不读写窗口字段。
    /// </summary>
    public bool RememberWindowSize { get; set; } = true;

    public string ExtraJvmArguments { get; set; } = string.Empty;

    /// <summary>
    /// 为兼容 Forge / 模组环境放宽启动校验（PCL2 <c>LaunchAdvanceJvm</c> 四条约默认值里的三条）：
    /// <c>-Djdk.lang.Process.allowAmbiguousCommands=True</c>、
    /// <c>-Dfml.ignoreInvalidMinecraftCertificates=True</c>、
    /// <c>-Dfml.ignorePatchDiscrepancies=True</c>。
    ///
    /// 默认开：离线启动、镜像源、手动替换 jar 都会让签名与补丁校验对不上，
    /// 不放宽时用户看到的是「模组文件损坏」，而实际是启动器拒绝了合法文件。
    /// 代价是本地 jar 被改动不再被拦，因此必须给用户一个能关的开关——关掉即恢复严格校验。
    ///
    /// 由 <c>JvmArgumentBuilder.Build</c> 的 <c>relaxForgeChecks</c> 参数消费；
    /// 同一组的 <c>-XX:-OmitStackTraceInFastThrow</c> 不受此开关控制（它只管异常堆栈，无关校验）。
    /// </summary>
    public bool RelaxForgeChecks { get; set; } = true;

    /// <summary>
    /// 全局游戏参数（PCL2 <c>LaunchArgumentGame</c> 的全局部分）。
    /// 以前只有 <see cref="LaunchOptions.ExtraGameArguments"/> 可传，设置页没有入口，
    /// 结果「给所有实例统一加 <c>--demo</c> / <c>--width 1280</c>」只能每次手填。
    /// </summary>
    public string ExtraGameArguments { get; set; } = string.Empty;

    /// <summary>
    /// 自定义版本信息，即 <c>--versionType</c> 的值（PCL2 <c>LaunchArgumentInfo</c>）。
    ///
    /// 显示在游戏主菜单标题与 F3 调试屏上，是「这个客户端是谁的整合包」的唯一可见标识。
    /// 空表示不传 <c>--versionType</c>：CmlLib 默认会填 <c>release</c>，而 PCL2 的做法是
    /// 把键和值一起从参数里删掉，否则主菜单会显示字面量 <c>${version_type}</c> 或一个空字符串
    /// （见 <c>ModLaunch.vb:1538-1544</c>）。
    ///
    /// 值里可以有空格：CmlLib 拼命令行时会按需加引号，切分不会被破坏。
    /// </summary>
    public string VersionArgumentInfo { get; set; } = string.Empty;

    /// <summary>
    /// 是否按本机可用内存自动推荐 <see cref="MaxMemoryMb"/>（PCL2 <c>LaunchArgumentRam</c>）。
    /// 打开时 <see cref="MaxMemoryMb"/> 只作为「自动关闭后的值」保存。
    /// </summary>
    public bool AutoMaxMemory { get; set; } = true;

    /// <summary>
    /// 下载限速 KB/s（PCL2 <c>ToolDownloadSpeed</c>）。0 表示不限速。
    /// 限速只约束下载队列的聚合带宽，不影响单个文件的超时判定。
    /// </summary>
    public int DownloadSpeedLimitKb { get; set; }

    /// <summary>
    /// HTTP 代理模式，取值见 <see cref="ProxyModes"/>。
    ///
    /// 默认 <c>system</c>：不改变历史行为（以前启动器从未碰过 <c>HttpClient.DefaultProxy</c>，
    /// 等于跟随环境变量）。生效点是 <c>Core.Net.ProxyConfigurator</c>，
    /// 由 <c>SettingsService</c> 在加载与保存后各应用一次。
    /// </summary>
    public string ProxyMode { get; set; } = ProxyModes.System;

    /// <summary>
    /// 自定义代理地址，形如 <c>http://127.0.0.1:7890</c>；只填 <c>host:port</c> 时按 http 补齐。
    /// 由 <c>ProxyAddressParser</c> 规范化成 <c>scheme://host[:port]</c> 后保存。
    /// </summary>
    public string? ProxyAddress { get; set; }

    /// <summary>代理用户名。为空表示代理不需要认证；此时 <see cref="ProxyPassword"/> 会被清掉。</summary>
    public string? ProxyUserName { get; set; }

    /// <summary>
    /// 代理密码。与用户名一起写进 <c>NetworkCredential</c>。
    ///
    /// 落盘时这个字段<b>永远是 null</b>：真正写进 settings.json 的是
    /// <see cref="ProxyPasswordProtected"/>（DPAPI 密文）。这里保留明文字段是为了两件事——
    /// 1) 旧版本配置里的明文能读到并迁移；2) 本次会话里 <c>ProxyConfigurator</c> 需要一个可用的密码。
    /// 也就是说：内存里有明文，磁盘上没有。
    /// </summary>
    public string? ProxyPassword { get; set; }

    /// <summary>
    /// 实际落盘的代理密码（<see cref="Net.ProxyCredentialStore"/> 产出的 <c>dpapi:</c> / <c>b64:</c> 串）。
    /// 非空表示「磁盘上有密码，用它」；解不开时（换机器 / 换 Windows 账户）按未设置处理。
    /// </summary>
    public string? ProxyPasswordProtected { get; set; }

    /// <summary>
    /// 游戏窗口出现后自动最大化（PCL2 <c>LaunchArgumentWindowType == 4</c>）。
    /// </summary>
    public bool MaximizeGameWindow { get; set; }

    /// <summary>
    /// 全局游戏窗口标题模板（PCL2 <c>VersionArgumentTitle</c> 的全局兜底）。
    /// 支持 <c>{version}</c> / <c>{user}</c> / <c>{date}</c> / <c>{time}</c>；空表示不改标题。
    /// </summary>
    public string? GameWindowTitle { get; set; }

    /// <summary>
    /// 是否持久化 Java 探测结果（PCL2 <c>Configs.JavaList</c>）。
    /// 打开后第二次启动启动器不再对每个候选跑一次 <c>java -version</c>。
    /// </summary>
    public bool EnableJavaCache { get; set; } = true;

    /// <summary>下载源策略。默认沿用历史行为：镜像优先（等价于未降级的 <see cref="DownloadSource.Auto"/>）。</summary>
    public DownloadSource DownloadSource { get; set; } = DownloadSource.Mirror;

    /// <summary>并发下载文件数。范围 8–256。</summary>
    public int DownloadThreads { get; set; } = 64;

    /// <summary>主题：<c>dark</c> / <c>light</c> / <c>system</c>。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>关闭后所有页面/控件动效降级为瞬时切换，用于低端机与「减少动态效果」偏好。</summary>
    public bool EnableAnimations { get; set; } = true;

    /// <summary>背景粒子特效开关。独立于 <see cref="EnableAnimations"/>，便于省电。</summary>
    public bool EnableBackgroundEffects { get; set; } = true;

    /// <summary>窗口背景材质：<c>mica</c> / <c>acrylic</c> / <c>solid</c>。非 Windows 平台自动降级为 solid。</summary>
    public string WindowMaterial { get; set; } = "mica";

    // 这里曾经有 Language 字段（默认 "zh-CN"），但界面只有简体中文、也没有 i18n 基础设施，
    // 它从未被任何消费方读取——一个恒等于默认值的死字段只会让接手者以为已经支持多语言。
    // 旧 settings.json 里的 "language" 由 System.Text.Json 按未知字段忽略，删掉不会让配置读失败。
    // 真正做多语言时再按「资源字典 + 语言键」重新设计，不要只加回一个字符串。

    public string? LastSelectedVersion { get; set; }

    /// <summary>
    /// 上次选中的实例 Id。null 表示不隔离，直接用全局 <c>.minecraft</c>。
    /// 由 <see cref="Abstractions.IInstanceContext"/> 读写；实例被删掉后自动失效。
    /// </summary>
    public string? LastInstanceId { get; set; }

    // 这里曾经有 CurseForgeApiKey 字段：CurseForge 的公共 API 强制 x-api-key（无 key 返回 403），
    // 早期版本让用户自己在设置页填。现在 key 内置于 Core 的 CurseForgeService.EmbeddedApiKey，
    // 用户不再输入，因此字段与「未配置」状态一起删除；旧 settings.json 里的 "curseForgeApiKey"
    // 会被 System.Text.Json 按未知字段忽略，不需要迁移代码。

    /// <summary>
    /// CurseForge API 基地址覆盖。为空使用 <c>https://api.curseforge.com</c>；
    /// 填镜像（例如 <c>https://mod.mcimirror.top/curseforge</c>）时镜像必须自己处理 key 或忽略它。
    /// 末尾斜杠会被剥掉。
    /// </summary>
    public string? CurseForgeBaseUrl { get; set; }

    /// <summary>
    /// Modrinth 镜像兜底开关（第三方服务 <c>mod.mcimirror.top</c>）。
    ///
    /// **默认关**：镜像会把「我在启动器里搜了什么」暴露给第三方，默认打开等于替用户做决定。
    /// 打开后也只在 Modrinth 官方**连不上**（网络异常 / 超时 / 429 / 5xx）时才用镜像；
    /// 官方 404 表示项目本来就不存在，不换镜像。规则与判据见 <c>Downloads/DownloadInfrastructure.cs</c>
    /// 的 <c>ModrinthCandidates</c> 与 <c>Mods/ModrinthService.GetStringAsync</c>。
    /// </summary>
    public bool UseModrinthMirror { get; set; }

    /// <summary>在线资源默认数据源：<c>modrinth</c> / <c>curseforge</c>。</summary>
    public string ContentSource { get; set; } = "modrinth";

    /// <summary>
    /// 版本隔离策略：<c>never</c> / <c>modded</c> / <c>always</c>。
    ///
    /// 默认 <c>modded</c>：首次启动装了加载器的版本时自动建实例目录。
    /// 以前没有这个开关，隔离只能靠用户手动「创建实例」，
    /// 结果就是所有版本的 mods 挤在同一个 <c>.minecraft/mods</c> 里互相干扰。
    /// </summary>
    public string IsolationPolicy { get; set; } = "modded";

    public string? LastAccountId { get; set; }

    public bool CloseToTray { get; set; }

    /// <summary>
    /// 历史字段，已无 UI 入口也无消费方：仓库里没有发布端点，PCL2 开源版的自更新也是空桩（PCL2-TODO A3 / B73）。
    /// 保留属性只为让旧 settings.json 往返不丢字段。
    /// </summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>启动后自动关闭启动器窗口（游戏保持运行）。</summary>
    public bool CloseLauncherAfterLaunch { get; set; }

    /// <summary>
    /// 游戏启动后对启动器窗口做什么：<c>keep</c>（不变）/ <c>minimize</c>（最小化）/
    /// <c>close</c>（关闭，游戏继续跑）。对应 PCL2 <c>LaunchArgumentVisible</c> 的五档，
    /// 这里收敛成三种用户真正会用的行为；<see cref="CloseLauncherAfterLaunch"/> 保留向后兼容。
    /// </summary>
    public string LauncherVisibilityOnLaunch { get; set; } = "keep";

    /// <summary>游戏进程优先级：<c>normal</c> / <c>high</c> / <c>low</c>（PCL2 <c>LaunchArgumentPriority</c>）。</summary>
    public string GameProcessPriority { get; set; } = "normal";

    /// <summary>
    /// 启动前把已有的 <c>options.txt</c> 语言设成简体中文（PCL2 <c>ToolHelpChinese</c>）。
    /// 只改「空 / en_us」两种状态，用户已选别的语言时不动。
    /// </summary>
    public bool AutoChineseLanguage { get; set; } = true;

    /// <summary>崩溃后把崩溃报告与 <c>logs</c> 打成一个 zip 放进 <c>crash-reports/archive</c>。</summary>
    public bool ArchiveCrashReports { get; set; } = true;

    /// <summary>
    /// 启动前把 Java 注册为「高性能 GPU」（PCL2 <c>LaunchAdvanceGraphicCard</c>）。
    ///
    /// 打开时启动前写 <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>，
    /// 让 Windows 把 <c>javaw.exe</c> 分到独立显卡；双显卡笔记本上不做这一步游戏会跑在核显上。
    /// 只影响 Windows，其他平台由 <c>IPlatformService.TrySetGpuPreference</c> 静默返回。
    /// </summary>
    public bool RegisterGpuPreference { get; set; } = true;

    /// <summary>
    /// 游戏正常退出后自动备份当前实例的所有世界存档。
    ///
    /// 默认关闭：一次全量 zip 可能几百 MB，不能替用户决定每次都做。
    /// 打开后必须同时受 <see cref="WorldBackupRetention"/> 约束，否则磁盘会被备份吃满。
    /// </summary>
    public bool AutoBackupWorldsOnExit { get; set; }

    /// <summary>每个世界最多保留多少份备份。自动备份每次退出都会新增一份。</summary>
    public int WorldBackupRetention { get; set; } = 20;

    /// <summary>
    /// JVM 参数预设组。用户在设置页维护若干组参数，启动时按 <see cref="ActiveJvmPresetId"/> 取一组。
    /// 为什么需要：不同游戏版本对 GC / 内存参数的要求不同，靠一段全局字符串来回改极易出错。
    /// </summary>
    public List<JvmPreset> JvmPresets { get; set; } = new();

    /// <summary>
    /// 当前生效的预设 Id；null 或找不到时退回 <see cref="ExtraJvmArguments"/> 原文。
    /// </summary>
    public string? ActiveJvmPresetId { get; set; }

    /// <summary>
    /// 上次已经提示过的 Minecraft 正式版号。
    /// 首页的「有新版本」提示只在第一次发现该版本时出现，不重复打扰。
    /// </summary>
    public string? LastNotifiedGameVersion { get; set; }

    /// <summary>
    /// 深拷贝。列表字段必须逐个复制：<see cref="object.MemberwiseClone"/> 只复制引用，
    /// 共用同一个 <see cref="JvmPresets"/> 列表会让「设置页改一处、别处跟着变」。
    /// </summary>
    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.JvmPresets = JvmPresets is null
            ? new List<JvmPreset>()
            : JvmPresets.Select(preset => preset.Clone()).ToList();

        // 字符串字段 MemberwiseClone 已经复制；这里显式写出，避免以后把它换成引用类型时漏拷贝，
        // 也提醒后来的人：加了字段必须在 Clone 里有交代。
        clone.Theme = Theme;

        // 启动器窗口尺寸是后加字段，同样显式列出：这一组全部是值类型，
        // 写在这里的作用是让「加字段必须同步 Clone」这条约定在 diff 里可见。
        clone.LauncherWindowWidth = LauncherWindowWidth;
        clone.LauncherWindowHeight = LauncherWindowHeight;
        clone.LauncherWindowMaximized = LauncherWindowMaximized;
        clone.RememberWindowSize = RememberWindowSize;
        clone.BackgroundImagePath = BackgroundImagePath;
        clone.BackgroundImageOverlay = BackgroundImageOverlay;

        // 显卡首选项开关同样是值类型，显式列出只为让「加字段必须同步 Clone」在 diff 里可见。
        clone.RegisterGpuPreference = RegisterGpuPreference;

        // Forge 放宽开关：漏拷的表现为「设置页关掉 → 保存 → 重启后又打开了」，
        // 而用户关它的动机正是「我要严格校验」，静默回滚等于让开关失效。
        clone.RelaxForgeChecks = RelaxForgeChecks;

        // 版本信息：漏拷的表现为「设置页填了版本名，保存 → 重启后又变回默认的 release」。
        clone.VersionArgumentInfo = VersionArgumentInfo;

        // Modrinth 镜像开关：默认关。漏拷会让「打开后保存 → 重载」回到关闭状态，
        // 表现为「设开镜像，搜索却还是直连」。
        clone.UseModrinthMirror = UseModrinthMirror;

        // 代理设置同理。这几个是字符串，漏拷就会表现成「设置页改完、点保存、重启后地址丢了」，
        // 而 MemberwiseClone 恰好会把它复制过来——所以这条显式赋值主要是防未来换成引用类型时踩坑。
        clone.ProxyMode = ProxyMode;
        clone.ProxyAddress = ProxyAddress;
        clone.ProxyUserName = ProxyUserName;
        clone.ProxyPassword = ProxyPassword;
        clone.ProxyPasswordProtected = ProxyPasswordProtected;
        return clone;
    }
}

/// <summary>一组具名 JVM 参数。Id 用于引用，Name 只用于展示。</summary>
public sealed class JvmPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "新预设";

    /// <summary>参数原文，空格或换行分隔；与 <see cref="AppSettings.ExtraJvmArguments"/> 同格式。</summary>
    public string Arguments { get; set; } = string.Empty;

    public JvmPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Arguments = Arguments,
    };
}
