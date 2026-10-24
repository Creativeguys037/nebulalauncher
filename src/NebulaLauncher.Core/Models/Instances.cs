using System.Text.Json.Serialization;

namespace NebulaLauncher.Core.Models;

/// <summary>
/// 一个实例 = 一套独立的游戏目录（mods / saves / config 互不干扰）。
/// 实例目录就是版本目录：<c>&lt;游戏目录&gt;/versions/&lt;VersionName&gt;/</c>，通过其中的
/// <c>instance.json</c> 描述。布局与 PCL2 的 <c>PathIndie = PathVersion</c>、
/// HMCL 的 <c>getRunDirectory</c> 一致。
/// </summary>
public sealed class InstanceInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "新实例";

    /// <summary>使用的已安装版本目录名。</summary>
    public string VersionName { get; set; } = string.Empty;

    public string? IconPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; set; }

    public long TotalPlayTimeSeconds { get; set; }

    /// <summary>覆盖全局内存设置；null 表示跟随全局。</summary>
    public int? MaxMemoryMb { get; set; }

    public string? JavaPath { get; set; }

    /// <summary>
    /// 实例绑定的加载器。创建实例时从版本信息写入，之后作为「按加载器分组」的依据。
    ///
    /// 追加字段，旧 <c>instance.json</c> 缺省为 <see cref="LoaderKind.Vanilla"/>；
    /// <see cref="IInstanceContext"/> 在读取版本目录时会用它校准（版本被重装成别的加载器时以磁盘为准）。
    /// </summary>
    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    /// <summary>实例解析出的原版 Minecraft 版本号（例如 <c>1.20.1</c>），用于兼容性判断与分组排序。</summary>
    public string? MinecraftVersion { get; set; }

    /// <summary>
    /// 实例绑定的加载器自身版本号缓存，例如 Forge 的 build <c>36.2.25</c>、
    /// NeoForge 的 <c>20.2.62-beta</c>、Fabric Loader 的 <c>0.16.9</c>。
    ///
    /// <b>语义与 <see cref="MinecraftVersion"/> 同类，是缓存而不是用户设置</b>——
    /// 这与 <see cref="VersionType"/> 那种「null / 空串 / 值」三态字段完全不同，不要混：
    /// <list type="bullet">
    /// <item><c>null</c> = 还不知道（旧 <c>instance.json</c> 没有这个字段，或版本目录读不出来）。
    /// 此时按「无加载器级上限」处理，不猜。</item>
    /// <item>非空 = 上次从版本目录名解析出的值，由 <see cref="IInstanceContext"/> 在读取时校准，
    /// 用户手工换过 <c>versions/</c> 里的文件时以磁盘为准。</item>
    /// </list>
    /// 它没有「空串 = 明确关闭」这一态：加载器版本号要么有、要么没有。
    ///
    /// <b>为什么必须落盘而不是每次现解析</b>：Java 上限与「这个 build 属于哪一代」强相关
    /// （Forge 36.2.25 与 36.2.26 的 Java 上限差一整个大版本），而启动前的 Java 判定发生在
    /// 版本目录可能已被删除 / 正在被修复的时刻——那时再读目录名就来不及了。
    /// 形态一律原样保存，解析交给 <c>JavaRequirementTable.GetLoaderLimit</c>（它认得多种写法）。
    /// </summary>
    public string? LoaderVersion { get; set; }

    /// <summary>是否启用版本隔离：启用时游戏以本实例目录为 <c>--gameDir</c> 运行。</summary>
    public bool IsolationEnabled { get; set; } = true;

    // ==================== 实例级启动覆盖（对应 PCL2 的 VersionAdvance* / VersionArgument* 实例设置） ====================

    /// <summary>
    /// 实例级 JVM 参数，覆盖全局 <see cref="AppSettings.ExtraJvmArguments"/>。
    /// null = 跟随全局；空字符串 = 显式清空（这一区分是 PCL2 <c>VersionAdvanceJvm</c> 的语义）。
    /// </summary>
    public string? JvmArguments { get; set; }

    /// <summary>实例级游戏参数，覆盖全局 <see cref="AppSettings.ExtraGameArguments"/>。null = 跟随全局。</summary>
    public string? GameArguments { get; set; }

    /// <summary>
    /// 启动游戏前执行的命令行（PCL2 <c>VersionAdvanceRun</c>）。
    /// 支持 <c>{gameDir}</c> / <c>{version}</c> / <c>{java}</c> 占位符。
    /// </summary>
    public string? LaunchBeforeCommand { get; set; }

    /// <summary>游戏退出后执行的命令行。占位符同上，另支持 <c>{exitCode}</c>。</summary>
    public string? LaunchAfterCommand { get; set; }

    /// <summary>
    /// 启动前/后命令是否以阻塞方式执行（PCL2 <c>VersionAdvanceRunWait</c>）。
    /// false 时只投递进程，不等结果。
    /// </summary>
    public bool WaitForLaunchCommands { get; set; } = true;

    /// <summary>
    /// 游戏窗口标题模板（PCL2 <c>VersionArgumentTitle</c>）。
    /// 支持 <c>{version}</c> / <c>{user}</c> / <c>{date}</c> / <c>{time}</c>。
    /// null 或空白表示不改标题。
    /// </summary>
    public string? WindowTitle { get; set; }

    /// <summary>实例备注（PCL2 <c>VersionArgumentInfo</c>）。</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// 实例级版本信息（<c>--versionType</c>），显示在 Minecraft 主菜单与 F3 屏上。
    ///
    /// <b>三种取值必须区分，两个「空」不是一回事：</b>
    /// <list type="bullet">
    /// <item><c>null</c> = 本实例没设置，启动时回落到全局 <see cref="AppSettings.VersionArgumentInfo"/>。
    /// 这是旧 <c>instance.json</c> 缺失该字段时的默认值，所以「跟随全局」不会打破既有实例。</item>
    /// <item><c>""</c> = 本实例明确要求「不显示版本名」，连全局值也压掉。
    /// 为什么需要这一层：用户在设置页把全局填成了整合包名，而某个实例想恢复成不显示。
    /// 如果空串也当「跟随全局」，他在实例上清空输入框只会把全局值又拿回来，这个实例就永远关不掉。
    /// PCL2 只有「实例优先、全局兜底」这一层（<c>ModLaunch.vb:1531-1532</c>），
    /// 所以它在实例上清空等于回到全局值——这里刻意多给一层。</item>
    /// <item>非空字符串 = 直接用这个值。</item>
    /// </list>
    ///
    /// 不要复用 <see cref="Notes"/>：那是显示在实例卡片上的备注，复用会把用户写给自己的
    /// 备忘（「整合包 1.20.1 专用，别在这装光影」）直接印到游戏主菜单标题上。
    /// </summary>
    public string? VersionType { get; set; }

    /// <summary>
    /// 实例图标种类（PCL2 <c>GameInstanceIconType</c>）。
    /// <see cref="InstanceIconKind.Custom"/> 时回落到 <see cref="IconPath"/>。
    /// </summary>
    public InstanceIconKind IconKind { get; set; } = InstanceIconKind.Default;

    /// <summary>
    /// 垃圾回收器偏好（PCL2 <c>VersionAdvanceGC</c>）。
    /// <see cref="GcKind.Auto"/> 时不追加任何 GC 参数，由 JVM 决定。
    /// </summary>
    public GcKind GcKind { get; set; } = GcKind.Auto;

    /// <summary>
    /// 启动后直连的服务器地址 <c>host[:port]</c>（PCL2 <c>VersionServerEnter</c>）。
    /// null 表示进主菜单。
    /// </summary>
    public string? ServerAddress { get; set; }

    // ==================== 实例级登录服务器（PCL2 的 VersionServerAuth* 三项） ====================

    /// <summary>
    /// 实例级第三方验证服务器地址（PCL2 <c>VersionServerAuthServer</c>）。
    ///
    /// <b>三态，与 <see cref="VersionType"/> 同一套语义：</b>
    /// <list type="bullet">
    /// <item><c>null</c> = 本实例没设置，启动时用账户自己的 <see cref="Account.AuthServerUrl"/>。</item>
    /// <item><c>""</c> = 本实例明确要求「不注入 authlib-injector」，连账户里的地址也压掉。
    /// 用户想裸连（例如该验证服已经关停、或这个包只想单人玩）时用得上。</item>
    /// <item>非空字符串 = 就用这个地址，覆盖账户值。</item>
    /// </list>
    ///
    /// <b>为什么必须挂在实例上而不是账户上</b>：语义是「这个整合包对这套验证服」。
    /// 服主发的包绑定了自己的验证服，玩家的号还是玩家自己的；导入别人的包时不该要求
    /// 用户先改掉自己账户里的全局地址——那样他从自己的服切到这个包就得改一次再改回去。
    ///
    /// <b>这里只存服务器地址，绝不存用户名或密码</b>：<c>instance.json</c> 会随整合包
    /// 一起被打包分享，里面出现凭据等于把账号送给接收方。角色名与令牌仍然来自
    /// <see cref="Account"/>，落盘由 <c>IAccountService</c> 单独加密。
    /// </summary>
    public string? AuthServerUrl { get; set; }

    /// <summary>
    /// 实例级注册链接（PCL2 <c>VersionServerAuthRegister</c>）。
    ///
    /// 只用于在实例面板上显示一个可点的「注册」按钮，<b>不参与启动参数</b>：
    /// 它是给人看的地址，塞进 JVM 参数只会变成一个无用 token。
    /// 是否可点由 <see cref="AuthServerAddressPolicy.IsHttpUrl"/> 判定（PCL2 的 <c>ValidateHttp</c>）。
    /// </summary>
    public string? AuthRegisterUrl { get; set; }

    /// <summary>
    /// 实例级验证服务器显示名（PCL2 <c>VersionServerAuthName</c>）。
    /// 纯展示：面板上写「登录服务器：XX 服」，不参与启动，也不做任何校验。
    /// </summary>
    public string? AuthServerName { get; set; }

    /// <summary>
    /// 导入该实例的整合包来源（PCL2 的 <c>managedPack</c> 元数据）。
    /// 手工创建的实例为 null；导入的整合包写入后可用于「检查整合包更新」。
    /// </summary>
    public ManagedPackInfo? ManagedPack { get; set; }
}

/// <summary>
/// 登录服务器地址的取值规则（PCL2 <c>ValidateHttp</c>）。
///
/// 单独抽成静态类而不是散在界面里：注册链接是否显示、面板上的灰字提示、
/// 点「注册」时的兜底校验，三处必须用同一个判据，否则会出现
/// 「按钮显示着但点不开」或「地址合法却不显示按钮」。
/// </summary>
public static class AuthServerAddressPolicy
{
    /// <summary>
    /// 是否是可直接展示 / 打开的网络地址。
    ///
    /// 只接受 <c>http://</c> 与 <c>https://</c> 的绝对地址，且必须带主机名：
    /// <list type="bullet">
    /// <item><c>ftp://</c> / <c>file://</c> / <c>javascript:</c> 一律拒绝——前两个点开会弹资源管理器，
    /// 后者是脚本协议，把用户填的内容原样交给系统 shell 等于开了个口子。</item>
    /// <item>裸主机名（<c>example.com/register</c>）也拒绝：<see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>
    /// 会把它当相对地址，用户少打 scheme 时应该看到提示而不是一个点不开的按钮。</item>
    /// </list>
    /// </summary>
    public static bool IsHttpUrl(string? value)
    {
        var trimmed = value?.Trim();

        return !string.IsNullOrEmpty(trimmed)
               && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host);
    }
}

/// <summary>
/// 一次「某个游戏目录正被游戏进程占用」的事实记录，落在该目录的 <c>.nebula-run.json</c>。
///
/// 为什么需要它：同一个实例被启动两次时，两个 JVM 会同时写 <c>saves/</c> 与 <c>mods/</c>，
/// 坏的是存档而不是「打不开游戏」——这种损坏是静默的，用户往往几小时后才发现。
///
/// 字段里同时存 PID 与进程启动时间：只有 PID 挡不住系统复用（上一个游戏退出后
/// 新进程恰好拿到同一个 PID，会被误判成「仍在运行」，用户就再也启动不了这个实例）。
/// </summary>
public sealed class InstanceLockInfo
{
    /// <summary>持有锁的启动器进程 PID。</summary>
    public int Pid { get; set; }

    /// <summary>持有进程的启动时间（UTC）。与 PID 一起构成「还是不是同一个进程」的判据。</summary>
    public DateTimeOffset ProcessStartUtc { get; set; }

    /// <summary>本次启动的版本目录名。</summary>
    public string? VersionName { get; set; }

    /// <summary>本次启动时刻（UTC）。</summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>随机令牌。释放时只删自己写的锁，靠它认领。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>锁所在的游戏目录绝对路径。写进文件只为排错时能一眼确认认领的是哪个目录。</summary>
    public string? GameDirectory { get; set; }

    /// <summary>面向用户的版本文案：缺字段时不能显示成空白。</summary>
    [JsonIgnore]
    public string VersionText => string.IsNullOrWhiteSpace(VersionName) ? "未知版本" : VersionName!;
}

/// <summary>实例图标种类。与 PCL2 <c>GameInstanceIconType</c> 的 15 个内置图标对应（去掉 PCL2 专属的愚人节图标）。</summary>
public enum InstanceIconKind
{
    Default = 0,
    Grass = 1,
    Chest = 2,
    Command = 3,
    CraftTable = 4,
    Furnace = 5,
    Chicken = 6,
    OptiFine = 7,
    Fabric = 8,
    Quilt = 9,
    Forge = 10,
    NeoForge = 11,

    /// <summary>使用 <see cref="InstanceInfo.IconPath"/> 指向的图片。</summary>
    Custom = 12,
}

/// <summary>垃圾回收器偏好。旧版本 Java（&lt; 9）不支持后来的 GC 组合，选择时按 Java 主版本过滤。</summary>
public enum GcKind
{
    /// <summary>不追加 GC 参数。</summary>
    Auto = 0,

    /// <summary><c>-XX:+UseG1GC</c>（Java 8 起可用，现代默认）。</summary>
    G1 = 1,

    /// <summary><c>-XX:+UseZGC</c>（Java 15 起可用，低延迟大堆）。</summary>
    Z = 2,

    /// <summary><c>-XX:+UseParallelGC</c>（吞吐优先，旧机器常用）。</summary>
    Parallel = 3,

    /// <summary><c>-XX:+UseSerialGC</c>（低配 / 单核）。</summary>
    Serial = 4,
}

/// <summary>导入实例的整合包来源信息。字段形状对齐 PCL2 / HMCL 的 managedPack 元数据。</summary>
public sealed class ManagedPackInfo
{
    /// <summary>来源类型：<c>modrinth</c> / <c>curseforge</c> / <c>mmc</c> / <c>zip</c>。</summary>
    public string Type { get; set; } = "zip";

    /// <summary>在线项目 Id；纯本地整合包为 null。</summary>
    public string? ProjectId { get; set; }

    /// <summary>整合包名称。</summary>
    public string? Name { get; set; }

    /// <summary>导入时的版本号。</summary>
    public string? VersionId { get; set; }

    /// <summary>源压缩包的绝对路径。仅作诊断用途，移动后失效。</summary>
    public string? SourceArchive { get; set; }

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>内容目标的来源：全局 <c>.minecraft</c>，或某个实例目录。</summary>
public enum ContentTargetKind
{
    /// <summary>全局 <c>.minecraft</c>：所有未隔离的版本共用这一份 mods / resourcepacks。</summary>
    Global = 0,

    /// <summary>实例目录：独立的 mods / saves / config。</summary>
    Instance = 1,
}

/// <summary>
/// 版本隔离策略：首次启动一个版本时，是否自动为它建立独立游戏目录。
///
/// 默认 <see cref="Modded"/> —— 只隔离装了加载器的版本。
/// 理由：原版版本没有 mods 可隔离，建目录只是白占空间；而带加载器的版本
/// 放同一个 <c>.minecraft/mods</c> 里必定互相干扰（Forge 与 Fabric 的 jar 不能共存）。
/// </summary>
public enum IsolationPolicyKind
{
    /// <summary>不隔离：所有版本共用 <c>.minecraft</c>。与旧行为一致。</summary>
    Never = 0,

    /// <summary>只有装了 Fabric / Quilt / Forge / NeoForge 的版本自动隔离。</summary>
    Modded = 1,

    /// <summary>每个版本首次启动都自动建实例目录。</summary>
    Always = 2,
}

/// <summary>
/// 一次启动目标解析的结果。
/// </summary>
/// <param name="GameDirectory">最终使用的游戏目录绝对路径。</param>
/// <param name="Instance">命中的或自动创建的实例；不隔离时为 null。</param>
/// <param name="Isolated">是否使用了隔离目录。</param>
/// <param name="Created">本次是否新建了实例。</param>
public sealed record InstanceResolution(
    string GameDirectory,
    InstanceInfo? Instance,
    bool Isolated,
    bool Created,
    string? LoaderVersion = null)
{
    /// <summary>
    /// 这次解析拿到的加载器版本号：隔离实例取 <see cref="InstanceInfo.LoaderVersion"/>，
    /// 全局目录取解析结果（全局没有实例对象，<see cref="Instance"/> 为 null 时只能靠这个字段）。
    ///
    /// 为什么单独放一个字段而不是让调用方读 <c>Instance?.LoaderVersion</c>：
    /// 全局目录（不开隔离的多数用户）根本拿不到实例，而 Java 上限判定对两种目录一视同仁。
    /// </summary>
    public string? EffectiveLoaderVersion => LoaderVersion ?? Instance?.LoaderVersion;
}

/// <summary>
/// 一个可以独立管理内容的游戏目录。
///
/// 模组页要回答的问题不是「本机有什么模组」，而是「<b>这个版本用哪个目录</b>里有什么模组」——
/// 同一个 jar 在全局目录里对 Fabric 版本有效，对 Forge 版本就是坏的。
/// 因此内容管理的最小单位是 <see cref="ContentTarget"/>，而不是 <see cref="ContentItem"/>。
/// </summary>
public sealed class ContentTarget
{
    /// <summary>稳定标识：全局目标固定为 <c>global</c>，实例目标为实例 Id。</summary>
    public required string Key { get; init; }

    public ContentTargetKind Kind { get; init; }

    /// <summary>展示名，例如 <c>全局 (.minecraft)</c>、<c>1.20.1 Fabric</c>。</summary>
    public required string DisplayName { get; init; }

    /// <summary>该目标的游戏目录绝对路径。</summary>
    public required string GameDirectory { get; init; }

    /// <summary>实例 Id；全局目标为 null。</summary>
    public string? InstanceId { get; init; }

    /// <summary>目标绑定的版本目录名；全局目标为当前选中版本，可能为 null。</summary>
    public string? VersionName { get; init; }

    /// <summary>加载器。决定该目标下哪些 jar 才可能生效。</summary>
    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>加载器自身版本号（例如 <c>0.16.9</c>），原版为 null。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>解析出的原版 Minecraft 版本号，用于模组兼容性判断。</summary>
    public string? MinecraftVersion { get; init; }

    /// <summary>该目标绑定的版本目录是否真实存在。为 false 时界面应提示「版本未安装」。</summary>
    public bool IsVersionInstalled { get; init; } = true;

    /// <summary>是否启用版本隔离。全局目标恒为 false。</summary>
    public bool IsolationEnabled { get; init; }

    /// <summary>内容子目录（<c>mods</c> / <c>resourcepacks</c> / <c>shaderpacks</c>）的绝对路径。</summary>
    public string ContentDirectory(ContentKind kind) =>
        Path.Combine(GameDirectory, DirectoryNameOf(kind));

    /// <summary>与 <c>ContentService.DirectoryNameFor</c> 保持一致；这里重复一份是为了让 Model 层不依赖实现层。</summary>
    public static string DirectoryNameOf(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        ContentKind.World => "saves",

        // 数据包没有「实例级」目录：全局在 <gameDir>/datapacks，存档级在 saves/<world>/datapacks。
        // 这里给全局路径，调用方要存档级得自己拼（见 ContentService.ScanDataPacks）。
        ContentKind.DataPack => "datapacks",
        _ => "mods",
    };

    /// <summary>加载器展示名，与 UI 的徽标文案保持一致。</summary>
    public string LoaderLabel => Loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版",
    };
}

/// <summary>启动器首页聚合数据，一次性喂给 Dashboard。</summary>
public sealed record LauncherSnapshot(
    Account? ActiveAccount,
    VersionInfo? ActiveVersion,
    IReadOnlyList<VersionInfo> RecentVersions,
    int InstalledVersionCount,
    int ModCount,
    int WorldCount,
    long DiskUsageBytes);

/// <summary>
/// 实例回收站条目。
///
/// 直接 <c>Directory.Delete(recursive: true)</c> 删实例是不可逆的：用户点错一次，
/// 几十小时的存档与配置一起消失。改为先移到启动器自己的回收站目录（跨平台一致），
/// 由用户显式「彻底删除」。
/// </summary>
public sealed class InstanceTrashEntry
{
    /// <summary>回收站条目标识（时间戳 + 原 Id，保证同名实例多次删除也不冲突）。</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>删除前的实例 Id，用于「恢复」时提示可能重名。</summary>
    public required string OriginalInstanceId { get; init; }

    /// <summary>回收站内的绝对路径。</summary>
    public required string TrashPath { get; init; }

    public DateTimeOffset DeletedAt { get; init; }

    /// <summary>占用字节数；统计失败为 0。</summary>
    public long SizeBytes { get; init; }
}

/// <summary>
/// 实例克隆选项。
///
/// 克隆是「换加载器试模组」最常用的动作：复制一份实例，改它的版本，原实例不动。
/// 因此默认复制内容（mods / config / resourcepacks / saves），只排除日志与崩溃报告。
/// </summary>
public sealed class InstanceCloneOptions
{
    /// <summary>新实例显示名；null 表示在原名字后加「-副本」。</summary>
    public string? NewName { get; init; }

    /// <summary>
    /// 是否复制 <c>mods/</c>。默认复制——克隆却不带模组毫无意义。
    /// 版本页「为同一版本新建一个干净实例」把它置 false：只复制版本本体，内容从零开始。
    /// </summary>
    public bool IncludeMods { get; init; } = true;

    public bool IncludeSaves { get; init; } = true;

    public bool IncludeConfig { get; init; } = true;

    public bool IncludeResourcePacks { get; init; } = true;

    public bool IncludeShaderPacks { get; init; } = true;

    /// <summary>是否连 <c>logs/</c> 与 <c>crash-reports/</c> 一起复制。默认否，它们是故障现场。</summary>
    public bool IncludeLogs { get; init; }
}

/// <summary>实例目录清理选项。默认只清日志与崩溃报告，不碰截图与存档。</summary>
public sealed class InstanceCleanupOptions
{
    public bool IncludeLogs { get; init; } = true;

    public bool IncludeCrashReports { get; init; } = true;

    public bool IncludeDebugProfile { get; init; } = true;

    /// <summary>截图是用户资产，默认不动。</summary>
    public bool IncludeScreenshots { get; init; }

    /// <summary>只清这么早以前的文件；0 表示全部。给「每周自动清理」留的入口。</summary>
    public int OlderThanDays { get; init; }
}

/// <summary>清理结果。</summary>
public sealed record InstanceCleanupReport(int FilesDeleted, long BytesFreed, IReadOnlyList<string> Failed)
{
    public static readonly InstanceCleanupReport Empty = new(0, 0, Array.Empty<string>());
}

/// <summary>
/// 一次「原地换加载器」的结果。
///
/// 之所以不复用 <c>bool</c>：这个动作有四种不同的「非成功」语义，界面要给出不同的话——
/// 实例不存在（该刷新列表）、无法确定 MC 版本（该去修复）、跨版本被拒（改不了）、
/// 已经是目标加载器（不需要动）。压成一个 false 会让 UI 全部显示成「更换失败」。
/// </summary>
public sealed class InstanceLoaderSwitchResult
{
    public bool Success { get; init; }

    /// <summary>实例本来就已经是目标加载器（且版本相同），什么都没做。</summary>
    public bool AlreadyMatches { get; init; }

    /// <summary>失败原因（中文，可直接展示）。成功且未跳过时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>切换后实例指向的版本目录名。</summary>
    public string? NewVersionName { get; init; }

    /// <summary>切换前实例指向的版本目录名。回滚提示与日志用。</summary>
    public string? PreviousVersionName { get; init; }

    public LoaderKind Loader { get; init; }

    /// <summary>实际的加载器版本号；原版为 null。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>实例解析出的原版 Minecraft 版本号——换加载器前后它必须相同。</summary>
    public string? MinecraftVersion { get; init; }

    /// <summary>本次切换会改变现有 mods 的可用性，界面必须先让用户确认。</summary>
    public bool RequiresModConfirmation { get; init; }

    /// <summary>与 <see cref="RequiresModConfirmation"/> 配套的中文说明。</summary>
    public string? ConfirmationMessage { get; init; }

    public static InstanceLoaderSwitchResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// 「换加载器」的兼容性建议。
///
/// 单独放成纯静态类而不是藏在服务实现里：界面在**弹确认之前**就要知道该不该提示，
/// 而那时还没有任何安装动作发生。判定是纯函数，可单测。
/// </summary>
public static class InstanceLoaderAdvice
{
    /// <summary>
    /// 从 <paramref name="from"/> 换成 <paramref name="to"/> 是否会改变现有 mods 的可用性。
    ///
    /// 只有四种组合可以静默执行：
    /// 1. 同一个加载器（换版本只是升级）；
    /// 2. 原版 → 任何加载器：原版根本不加载 <c>mods/</c>，那些 jar 本来就是躺着的；
    /// 3. Fabric ↔ Quilt：Quilt 是 Fabric 的上游超集，能加载绝大多数 Fabric 模组；
    /// 4. 同上反向。
    ///
    /// 其余都提示：卸载加载器会让现役模组全部失效，而 Forge ↔ Fabric / Forge ↔ NeoForge
    /// 的 jar 互不通用——它们用的是完全不同的注入方式与包名。
    /// </summary>
    public static bool RequiresModConfirmation(LoaderKind from, LoaderKind to)
    {
        if (from == to)
        {
            return false;
        }

        if (from == LoaderKind.Vanilla)
        {
            return false;
        }

        // 卸掉加载器：mods 目录还在，但不会再被读取。
        if (to == LoaderKind.Vanilla)
        {
            return true;
        }

        return !(IsFabricFamily(from) && IsFabricFamily(to));
    }

    /// <summary>给用户看的一句话理由。不需要确认时返回 null。</summary>
    public static string? DescribeConsequence(LoaderKind from, LoaderKind to)
    {
        if (!RequiresModConfirmation(from, to))
        {
            return null;
        }

        if (to == LoaderKind.Vanilla)
        {
            return $"换成原版后，{Label(from)} 的加载器不再生效，mods 目录里的模组一个都不会被加载。";
        }

        return $"从 {Label(from)} 换成 {Label(to)} 后，mods 目录里现有的 jar 基本都不能用："
               + "两者的模组格式互不兼容，需要换成对应加载器的版本。"
               + "存档与实例配置不受影响，但请先把 mods 目录备份或记下清单。";
    }

    /// <summary>加载器展示名。与界面徽标文案保持一致。</summary>
    public static string Label(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版",
    };

    private static bool IsFabricFamily(LoaderKind kind) => kind is LoaderKind.Fabric or LoaderKind.Quilt;
}
