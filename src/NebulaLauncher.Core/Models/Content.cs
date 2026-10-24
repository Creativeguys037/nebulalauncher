namespace NebulaLauncher.Core.Models;

/// <summary>可管理的内容类型。</summary>
public enum ContentKind
{
    Mod = 0,
    ResourcePack = 1,
    ShaderPack = 2,
    World = 3,

    /// <summary>
    /// 数据包。与其它三类不同：它有两种落点（<c>datapacks/</c> 与 <c>saves/&lt;world&gt;/datapacks/</c>），
    /// 并且**允许是目录**而不是单个压缩包。追加在末尾，已有数值不变（已落盘的缓存按数值比较）。
    /// </summary>
    DataPack = 4,

    /// <summary>
    /// 服务端插件（Bukkit / Spigot / Paper / Purpur / Folia 系）。
    ///
    /// 它不是一种「新的可安装内容」，而是一个**识别结果**：Modrinth 的 <c>project_type</c> 对插件
    /// 同样返回 <c>mod</c>，真正区分客户端模组与服务端插件的是 <c>loaders</c> / <c>categories</c>
    /// 里的 <c>bukkit</c> / <c>folia</c> / <c>paper</c> / <c>purpur</c> / <c>spigot</c>
    /// （PCL2 <c>ResourceVersion.vb:189-195</c> 的判定顺序：先插件、再数据包、最后看加载器）。
    ///
    /// 为什么要专门标出来：这类 jar 装进客户端 <c>mods/</c> 会让客户端在启动期直接崩
    /// （Bukkit 插件调用的是服务端才有的类），而它的下载、文件名、图标与普通模组完全一样，
    /// 唯一能拦住它的地方就是这里多出来的一个枚举值。
    ///
    /// **本启动器只识别、不安装**：Nebula 是客户端启动器，服务端实例与 <c>plugins/</c> 目录
    /// 属于另一个产品概念。所有安装入口遇到它会明确拒绝并说明去向，绝不落盘。
    /// </summary>
    Plugin = 5,
}

/// <summary>
/// 服务端插件的处置口径。集中在一处，是因为「识别」「拒绝安装」「界面提示」三处必须用同一句话——
/// 分散写必然漂移成三种说法，用户在搜索页看到的提示和实际安装时的报错对不上。
/// </summary>
public static class ServerPluginPolicy
{
    /// <summary>
    /// 安装被拒绝时给用户看的完整原因。
    ///
    /// 必须同时说清三件事：它是什么、该放哪、为什么这里装不了。只说「不支持此类型」的话，
    /// 用户会去猜是不是启动器缺了功能，然后反复重试；不知道「该放服务端」的人根本不会想到
    /// 这个 jar 从来就不属于客户端。
    /// </summary>
    public const string InstallRejectedMessage =
        "这是服务端插件（Paper / Spigot / Bukkit 系），要放进服务端的 plugins/ 目录才能用，"
        + "装进客户端实例的 mods/ 会让游戏在启动时直接崩溃。Nebula 是客户端启动器，不提供服务端安装。";

    /// <summary>卡片 / 徽标上的类型文字。</summary>
    public const string KindLabel = "服务端插件";

    /// <summary>是否需要拦截安装。</summary>
    public static bool IsServerPlugin(ContentKind kind) => kind == ContentKind.Plugin;
}

/// <summary>一个已安装的模组 / 资源包 / 光影包 / 数据包。</summary>
public sealed class ContentItem
{
    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public ContentKind Kind { get; init; }

    /// <summary>MOD 的 modid（fabric.mod.json / mods.toml / MANIFEST）；资源包为 <c>pack_format</c> 描述。</summary>
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// <c>pack.mcmeta</c> 里声明的 <c>min_format</c>（1.21.9+，可带小数，例如 88.0）。
    /// 旧包没有这个字段时为 null；此时按 <see cref="Version"/> 里的整数 pack_format 判断。
    /// </summary>
    public double? MinPackFormat { get; init; }

    /// <summary>
    /// <c>pack.mcmeta</c> 里声明的 <c>max_format</c>。null 表示没声明：
    /// 与 <see cref="MinPackFormat"/> 一起看时，只有 min 表示上界开放，只有 max 表示下界开放。
    /// </summary>
    public double? MaxPackFormat { get; init; }

    public string? Description { get; init; }

    public string? Authors { get; init; }

    /// <summary>从 jar 内提取的图标写入缓存后的绝对路径；无图标为 null。</summary>
    public string? IconPath { get; init; }

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>声明的兼容 Minecraft 版本，例如 <c>&gt;=1.20 &lt;1.21</c>。</summary>
    public string? McVersionRange { get; init; }

    /// <summary>
    /// 模组自带的项目主页（fabric <c>contact</c>、quilt <c>metadata.contact</c>、mcmod.info <c>url</c>）。
    /// 只用于卡片右键跳转；没有就是没有，不拿资源站页面顶替。
    /// </summary>
    public string? HomePage { get; init; }

    /// <summary>
    /// MOD 声明的前置依赖（已排除 <c>minecraft</c>，它归 <see cref="McVersionRange"/>）。
    /// 资源包 / 世界为空集合；解析失败时同样为空，不抛异常。
    /// </summary>
    public IReadOnlyList<ModDependency> Depends { get; init; } = Array.Empty<ModDependency>();

    /// <summary>MOD 声明的不兼容项（Forge 的 <c>type="incompatible"</c>）；目前只展示不拦截。</summary>
    public IReadOnlyList<ModDependency> Breaks { get; init; } = Array.Empty<ModDependency>();

    public long FileSize { get; init; }

    public DateTimeOffset LastModified { get; init; }

    /// <summary>禁用状态实现为改扩展名 <c>.jar.disabled</c>，不移动目录。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 条目的来源提示。目前只给存档级数据包用：值是世界目录名，
    /// 界面据此显示「存档：xxx」——不看这个字段的话，两份同名的存档级数据包
    /// 在列表里长得一模一样，用户分不清改的是哪个世界的。
    /// 全局目录下的条目为 <c>null</c>。
    /// </summary>
    public string? SourceHint { get; init; }

    /// <summary>解析失败时的原因。</summary>
    public string? ParseError { get; init; }
}

/// <summary>两个模组之间的潜在冲突。</summary>
public sealed record ModConflict(string ModId, string Detail, IReadOnlyList<string> Files);

/// <summary>
/// 一个模组声明的前置依赖。<see cref="Optional"/> 为 true 表示 Forge 的
/// <c>mandatory="false"</c> / <c>type="optional"</c>：缺失只提示，不阻断加载。
/// </summary>
public sealed record ModDependency(string ModId, string? VersionRange, bool Optional);

/// <summary>
/// 一条「缺前置」的检查结果。<see cref="ModId"/> 是声明依赖的模组，
/// <see cref="MissingId"/> 是本地找不到的前置；<see cref="Files"/> 收集声明了这条依赖的文件名。
/// </summary>
public sealed record ModDependencyIssue(
    string ModId,
    string MissingId,
    string? RequiredRange,
    bool Optional,
    IReadOnlyList<string> Files);

/// <summary>在线资源的数据源。UI 用来切换搜索后端。</summary>
public enum ContentSourceKind
{
    Modrinth = 0,
    CurseForge = 1,
}

/// <summary>
/// 一条「本地文件 ↔ 在线项目」的匹配结果，也是检查更新的输出单元。
///
/// 匹配靠哈希反查：Modrinth 认 SHA1，CurseForge 认它自己的 MurmurHash2 指纹。
/// 两者都可能查不到（作者没上传 / 文件被改过），这时 <see cref="Matched"/> 为 false，
/// 状态文案必须是「未识别」而不是「无更新」——把两种情形混在一起会让用户以为已经是最新版。
/// </summary>
public sealed class ModUpdateInfo
{
    public required ContentItem Item { get; init; }

    /// <summary>是否在在线数据源里找到了对应项目。</summary>
    public bool Matched { get; init; }

    public ContentSourceKind Source { get; init; } = ContentSourceKind.Modrinth;

    /// <summary>在线项目 Id（Modrinth 用 base62 project_id，CurseForge 用数字 modId 的字符串形式）。</summary>
    public string? ProjectId { get; init; }

    public string? ProjectTitle { get; init; }

    /// <summary>本地文件对应的版本号；匹配不到时为 null。</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>远程最新版本号；查不到新版时为 null。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>远程最新版本的文件直链。</summary>
    public string? DownloadUrl { get; init; }

    public string? FileName { get; init; }

    public long FileSize { get; init; }

    public string? Changelog { get; init; }

    /// <summary>远程有新版本。</summary>
    public bool HasUpdate { get; init; }

    /// <summary>用户可读的状态文案（中文）。</summary>
    public string Status { get; init; } = "未检查";

    /// <summary>该条目的失败原因；成功时为 null。</summary>
    public string? Error { get; init; }
}

/// <summary>一次批量检查更新的汇总。</summary>
public sealed class ModUpdateReport
{
    public IReadOnlyList<ModUpdateInfo> Items { get; init; } = Array.Empty<ModUpdateInfo>();

    public int Total { get; init; }

    public int Matched { get; init; }

    public int Updatable { get; init; }

    /// <summary>整体失败原因（例如网络不可用）；逐条失败在 <see cref="ModUpdateInfo.Error"/>。</summary>
    public string? Error { get; init; }

    public static ModUpdateReport Empty { get; } = new();
}

/// <summary>
/// 一个本地模组文件的双哈希。两个数据源各认一套：
/// Modrinth 认 SHA1，CurseForge 认 MurmurHash2 变体（PCL2 <c>LocalResourceFile.CurseForgeHash</c>）。
/// </summary>
/// <param name="Sha1">大写十六进制 SHA1；计算失败时为 null。</param>
/// <param name="CurseForgeFingerprint">CurseForge 的 32 位指纹；计算失败时为 0。</param>
/// <param name="SizeBytes">文件字节数。</param>
/// <param name="LastWriteUtc">文件最后写入时间，用于缓存失效判断。</param>
public sealed record ModFileHashes(string? Sha1, uint CurseForgeFingerprint, long SizeBytes, DateTime LastWriteUtc);

/// <summary>
/// 一次内容安装的结果，供 Modrinth 以外的数据源（CurseForge）复用。
///
/// 与 <see cref="ModrinthInstallResult"/> 字段一一对应，但刻意分成两个类型：
/// <see cref="ModrinthInstallResult"/> 已在 UI 与测试里被引用（契约冻结，不改签名与类型），
/// 新数据源用一个中性类型，避免「CurseForge 安装返回 Modrinth 结果」这种自相矛盾的签名。
/// </summary>
public sealed class ContentInstallResult
{
    public bool Success { get; init; }

    /// <summary>最终落盘的绝对路径；失败为 null。</summary>
    public string? FilePath { get; init; }

    public string? FileName { get; init; }

    public long Bytes { get; init; }

    /// <summary>目标目录里已有同名文件，本次以 <c>xxx (1).jar</c> 落盘。</summary>
    public bool Renamed { get; init; }

    /// <summary>失败原因（中文可展示）。成功为 null。</summary>
    public string? Error { get; init; }

    public static ContentInstallResult Ok(string filePath, long bytes, bool renamed) => new()
    {
        Success = true,
        FilePath = filePath,
        FileName = Path.GetFileName(filePath),
        Bytes = bytes,
        Renamed = renamed,
    };

    public static ContentInstallResult Fail(string error) => new()
    {
        Success = false,
        Error = error,
    };
}

/// <summary>世界存档（备份源）。</summary>
public sealed class WorldInfo
{
    public required string Name { get; init; }

    public required string Directory { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }

    /// <summary>从 level.dat 解出的版本名，解析失败为 null。</summary>
    public string? VersionName { get; init; }

    /// <summary>是否启用了作弊 / 生存模式等附加信息，可为 null。</summary>
    public string? GameMode { get; init; }

    /// <summary><c>Data.GameType</c> 原始值（0 生存 / 1 创造 / 2 冒险 / 3 旁观），编辑面板用。</summary>
    public int? GameType { get; init; }

    /// <summary><c>Data.RandomSeed</c>。level.dat 缺失该字段时为 null。</summary>
    public long? Seed { get; init; }

    /// <summary><c>Data.Difficulty</c>（0 和平 / 1 简单 / 2 普通 / 3 困难）。</summary>
    public int? Difficulty { get; init; }

    /// <summary><c>Data.allowCommands</c>，即「作弊」开关。</summary>
    public bool? AllowCommands { get; init; }

    /// <summary><c>Data.GameRules.keepInventory</c>，即「保留物品栏」。</summary>
    public bool? KeepInventory { get; init; }

    /// <summary><c>Data.hardcore</c>，仅读取展示，编辑面板不改它。</summary>
    public bool? Hardcore { get; init; }

    /// <summary>
    /// <c>level.dat</c> 里有非空的 <c>Data.icon</c>（base64 PNG）。
    ///
    /// 只表示标签存在：扫描路径不解码图标，真正的提取与缓存走
    /// <see cref="Abstractions.IWorldIconService"/>，按需异步进行。
    /// </summary>
    public bool HasIcon { get; init; }

    /// <summary>
    /// 缩略图缓存文件的绝对路径；扫描阶段为 null，由 <see cref="Abstractions.IWorldIconService"/> 按需填充。
    ///
    /// 扫描不填它是刻意的：缓存到期要重写，扫描路径是同步遍历全部存档，塞进写盘只会拖慢页面。
    /// </summary>
    public string? IconCachePath { get; init; }
}

/// <summary>
/// 存档编辑请求：字段为 null 表示「不改这一项」。
///
/// 用可空字段而不是「整份新值」是有意的：只改种子时不该顺带把难度、作弊、世界名重写一遍——
/// 每一项都是一次写入风险。
/// </summary>
public sealed class WorldEditRequest
{
    /// <summary>新种子。可以负数，也可以超过 <see cref="int"/> 范围（NBT 里是 <c>TAG_Long</c>）。</summary>
    public long? Seed { get; init; }

    /// <summary>0 生存 / 1 创造 / 2 冒险 / 3 旁观。</summary>
    public int? GameMode { get; init; }

    /// <summary>0 和平 / 1 简单 / 2 普通 / 3 困难。</summary>
    public int? Difficulty { get; init; }

    /// <summary>作弊开关（<c>Data.allowCommands</c>）。</summary>
    public bool? AllowCommands { get; init; }

    /// <summary>保留物品栏（<c>Data.GameRules.keepInventory</c>）。</summary>
    public bool? KeepInventory { get; init; }

    /// <summary>硬核标记。界面暂不暴露，留给调用方显式指定。</summary>
    public bool? Hardcore { get; init; }

    /// <summary>世界名（<c>Data.LevelName</c>）。空字符串按「不改」处理。</summary>
    public string? LevelName { get; init; }
}

/// <summary>存档编辑结果。失败时 <see cref="Error"/> 是可展示的中文原因。</summary>
public sealed class WorldEditResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>写入前生成的 <c>level.dat.nebula.bak</c> 绝对路径。</summary>
    public string? BackupPath { get; init; }

    public static WorldEditResult Ok(string? backupPath = null) => new()
    {
        Success = true,
        BackupPath = backupPath,
    };

    public static WorldEditResult Fail(string error) => new()
    {
        Success = false,
        Error = error,
    };
}

/// <summary>一条备份记录。</summary>
public sealed class BackupEntry
{
    public required string Id { get; init; }

    public required string FilePath { get; init; }

    public required string WorldName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public long SizeBytes { get; init; }

    public string Note { get; init; } = string.Empty;

    public bool IsAutomatic { get; init; }

    public string? SourceWorldDirectory { get; init; }
}

/// <summary>整合包导入来源。</summary>
public enum ModpackFormat
{
    Unknown = 0,
    ModrinthModpack = 1,
    CurseForgeZip = 2,
    MultiMcZip = 3,
    PlainZip = 4,

    /// <summary>
    /// MCBBS 整合包（<c>mcbbs.packmeta</c>，或 <c>manifest.json</c> 里带 <c>addons</c>）。
    ///
    /// 它的 <c>manifest.json</c> 与 CurseForge 同构（<c>minecraft.version</c> + <c>modLoaders[]</c>），
    /// 差别只在 <c>files[]</c> 是 addons 引用而不是 projectID/fileID 直链——所以版本与加载器照常装得上，
    /// 反查不到的条目进「待手动安装」清单（PCL2 ModModpack.vb:44/64 是同一判据）。
    /// </summary>
    McbbsZip = 5,

    /// <summary>
    /// HMCL 整合包（<c>modpack.json</c>）。覆盖层目录是 <c>minecraft/</c> 而不是 <c>overrides/</c>。
    /// PCL2 的判据见 <c>ModModpack.vb:75</c>，装载见 <c>InstallPackHMCL</c>（:494）。
    /// </summary>
    HmclZip = 6,

    /// <summary>
    /// 带启动器的分发包：外层 zip 里有 <c>modpack.zip</c> / <c>modpack.mrpack</c>
    /// （PCL2 <c>ModModpack.vb:56</c> 的 PackType=9）。
    ///
    /// 这种包必须先解压到目录、再导入里面那个真正的整合包文件；直接当普通 zip 解压，
    /// 会把启动器 exe 与配置当成客户端内容塞进实例，用户看到的是一堆无法识别的文件。
    /// Nebula 不引入「选空文件夹 + 多游戏目录」交互，所以在导入入口明确拒绝并说明下一步。
    /// </summary>
    LauncherPack = 7,
}

/// <summary>整合包导入预览。</summary>
public sealed class ModpackPreview
{
    public required string Name { get; init; }

    public string Version { get; init; } = string.Empty;

    public string? Author { get; init; }

    public string? Description { get; init; }

    public string MinecraftVersion { get; init; } = string.Empty;

    public LoaderKind Loader { get; init; }

    public string? LoaderVersion { get; init; }

    public ModpackFormat Format { get; init; }

    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 该整合包是否声明了可安装的 Minecraft 版本。
    ///
    /// 解析出「有 MC 版本」就意味着导入时能顺带把游戏与加载器装上；解析不出（普通压缩包、
    /// 缺 <c>gameVersion</c> 的 HMCL 包）就只能解压文件。导入页据此决定是否显示「同时安装游戏」开关。
    /// </summary>
    public bool RequiresGameInstall => !string.IsNullOrWhiteSpace(MinecraftVersion);
}

/// <summary>
/// 一次整合包导入的结果。
///
/// 导入是多阶段的（装游戏 → 解压覆盖层 → 下 index 文件 → 写实例），任何一段失败都可能留下
/// 「文件在但启动不了」的实例。这个对象把每段的真实结果摊开，让 UI 能说清楚是「装完了」
/// 还是「文件解压了但加载器没装上」（PCL2 的 <c>McInstallFailedClearFolder</c> 只做清理，
/// 提示靠调用方自己拼）。
/// </summary>
public sealed class ModpackImportResult
{
    public bool Success { get; init; }

    /// <summary>实例目录绝对路径；失败为 null。</summary>
    public string? InstanceDirectory { get; init; }

    public ModpackFormat Format { get; init; }

    public string? MinecraftVersion { get; init; }

    public LoaderKind Loader { get; init; }

    public string? LoaderVersion { get; init; }

    /// <summary>装出来的 <c>versions/</c> 目录名（实例的 <c>VersionName</c> 就是它）。</summary>
    public string? InstalledVersionName { get; init; }

    /// <summary>本次是否真的装了游戏与加载器（false = 只解压了文件）。</summary>
    public bool GameInstalled { get; init; }

    public int FilesDownloaded { get; init; }

    public int FilesFailed { get; init; }

    /// <summary>非致命问题：加载器装不上、index 文件部分失败、加载器版本无法确认等。</summary>
    public string? Warning { get; init; }

    /// <summary>致命失败原因；<see cref="Success"/> 为 true 时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// addons 里可下载内容的条目总数（已排除 <c>game</c> / <c>forge</c> 这类加载器声明）。
    /// 非 MCBBS 包恒为 0。
    /// </summary>
    public int AddonsTotal { get; init; }

    /// <summary>addons 里成功落到实例目录的条目数（含被 <c>overrides/</c> 覆盖层直接带进来的那些）。</summary>
    public int AddonsDownloaded { get; init; }

    /// <summary>
    /// 需要用户手动装回来的条目（反查失败或下载失败）。列表本身也是给「查看清单」面板用的数据源。
    /// </summary>
    public IReadOnlyList<ModpackPendingAddon> PendingAddons { get; init; } = Array.Empty<ModpackPendingAddon>();

    /// <summary>待手动安装的条目数。</summary>
    public int AddonsPending => PendingAddons.Count;

    public bool HasPendingAddons => PendingAddons.Count > 0;

    /// <summary>前几条待装条目的名字，用「、」连接；给一行文案用，不做全量拼接。</summary>
    public string PendingAddonsPreview
    {
        get
        {
            if (PendingAddons.Count == 0)
            {
                return string.Empty;
            }

            var names = string.Join("、", PendingAddons.Take(3).Select(item => item.Name));
            return PendingAddons.Count > 3 ? $"{names} 等 {PendingAddons.Count} 个" : names;
        }
    }

    public static ModpackImportResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>addons 条目声明它来自哪个平台。<see cref="ModpackAddonSource.Auto"/> 表示按 id 的形态猜。</summary>
public enum ModpackAddonSource
{
    /// <summary>没声明来源：数字 id 先试 CurseForge，非数字 id 试 Modrinth。</summary>
    Auto = 0,

    CurseForge = 1,

    Modrinth = 2,
}

/// <summary>
/// MCBBS 整合包 <c>addons</c> / <c>files</c> 里的一条引用：只有「项目 + 文件」标识，<b>没有直链</b>。
///
/// 与 CurseForge <c>manifest.json</c> 的 <c>files[]</c> 同构（<c>projectID</c> + <c>fileID</c>），
/// 差别只在 MCBBS 的导出工具把字段名写成了 <c>id</c> / <c>version</c>。解析时两者都认，
/// 因为真实样本两种都见过（PCL2 <c>ModModpack.vb:714</c> 只读 <c>id</c>/<c>version</c>，
/// 而它的 <c>InstallPackCurseForge</c>:162-360 只读 <c>projectID</c>/<c>fileID</c>）。
///
/// <see cref="ProjectId"/> 允许是 CurseForge 的数字 id、Modrinth 的 base62 id 或 slug——
/// 反查链路按形态决定先问谁，问不到再换另一家。
/// </summary>
public sealed class ModpackAddonReference
{
    public required string ProjectId { get; init; }

    /// <summary>文件 / 版本 id。CurseForge 是数字 fileID，Modrinth 是 version id。缺失时为 null。</summary>
    public string? FileId { get; init; }

    /// <summary>addons 声明的落盘文件名。缺失时用远程文件名（这是本任务的「文件名回退」）。</summary>
    public string? FileName { get; init; }

    /// <summary>addons 声明的目标路径（相对实例根）。缺失时按 <see cref="Kind"/> 推默认目录。</summary>
    public string? TargetPath { get; init; }

    public ContentKind Kind { get; init; } = ContentKind.Mod;

    public ModpackAddonSource Source { get; init; } = ModpackAddonSource.Auto;

    /// <summary>项目 id 是不是纯数字（决定先问 CurseForge 还是 Modrinth）。</summary>
    public bool HasNumericProjectId => long.TryParse(ProjectId, out _);

    /// <summary>界面与日志里的短名：优先文件名，其次 <c>项目:文件</c>。</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(FileName)
        ? FileName
        : string.IsNullOrWhiteSpace(FileId) ? ProjectId : $"{ProjectId}:{FileId}";
}

/// <summary>
/// 一条「待手动安装」的 addon：反查链路没解析出直链，或下载失败。
///
/// 它不是错误——MCBBS 的包引用的是当年的时效性文件，项目被作者删掉、作者关了第三方分发、
/// 或者当年的 CurseForge 条目早已不可见，都是常态。整包导入仍算成功，
/// 玩家拿到的是一个能启动但少几个 mod 的实例，外加这张清单。
/// </summary>
public sealed class ModpackPendingAddon
{
    /// <summary>展示名：文件名优先，退化为「项目 id 或 项目:文件」。</summary>
    public required string Name { get; init; }

    public string? ProjectId { get; init; }

    public string? FileId { get; init; }

    /// <summary>为什么没装上（中文，可直接展示）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>手动下载入口：项目 / 文件网页地址，可能为 null（无可靠推断时宁可不给）。</summary>
    public string? WebUrl { get; init; }
}

/// <summary>
/// 整合包导出选项。
///
/// 导入侧早就有了，导出侧一直缺——用户配好一套上百个模组的实例后无法分享或迁移，
/// 换机器只能整目录拷贝，把 libraries / assets / logs 一起带走。
/// </summary>
public sealed class ModpackExportOptions
{
    public string Name { get; init; } = "未命名整合包";

    public string Version { get; init; } = "1.0.0";

    public string? Author { get; init; }

    /// <summary>是否把 <c>saves/</c> 打进去。默认否：存档通常有几十 GB，且含个人信息。</summary>
    public bool IncludeSaves { get; init; }

    public bool IncludeConfig { get; init; } = true;

    public bool IncludeResourcePacks { get; init; }

    public bool IncludeShaderPacks { get; init; }

    /// <summary>
    /// 是否带上 <c>options.txt</c>（画面 / 按键 / 语言等游戏设置）。
    /// 默认带：整合包作者配置好的按键与画面设置是「开箱即用」的一部分（PCL2 导出默认包含游戏设置）。
    /// </summary>
    public bool IncludeOptions { get; init; } = true;

    /// <summary>
    /// 是否带上 <c>defaultconfigs/</c>（1.13+ 的新建世界默认配置）。
    /// 默认带：它是模组作者写给「新存档」的配置，漏掉会让整合包在每个新世界都回到默认值。
    /// </summary>
    public bool IncludeDefaultConfigs { get; init; } = true;

    /// <summary>
    /// 是否带上 <c>servers.dat</c>（服务器列表）。默认不带：里面是服务器地址，
    /// 属于用户自己的收藏，分享整合包时通常不希望一并公开（PCL2 导出选项里同样是可选项）。
    /// </summary>
    public bool IncludeServerList { get; init; }

    /// <summary>
    /// 是否用哈希反查把 jar 写成 <c>files[]</c> 的在线下载条目。
    /// 关闭时所有文件都进 <c>overrides/</c>，包体积大但离线也能装。
    /// </summary>
    public bool PreferModrinthHashes { get; init; } = true;

    /// <summary>目标 Minecraft 版本。空表示从实例绑定的版本读。</summary>
    public string? MinecraftVersion { get; init; }
}

/// <summary>一次导出的结果。</summary>
public sealed class ModpackExportResult
{
    public bool Success { get; init; }

    /// <summary>产出的 <c>.mrpack</c> 绝对路径；失败为 null。</summary>
    public string? ArchivePath { get; init; }

    /// <summary>写入的条目总数（在线条目 + overrides 文件）。</summary>
    public int Files { get; init; }

    /// <summary>无法联网识别、只能原样打包进 overrides 的 jar 数。导出后 UI 应提示这一点。</summary>
    public int UnresolvedMods { get; init; }

    public long Bytes { get; init; }

    public string? Error { get; init; }

    public static ModpackExportResult Fail(string error) => new() { Success = false, Error = error };
}
