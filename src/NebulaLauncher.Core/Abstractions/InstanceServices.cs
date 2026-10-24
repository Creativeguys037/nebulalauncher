namespace NebulaLauncher.Core.Abstractions;

/// <summary>实例（隔离的游戏目录）管理。没有实例时 UI 退化为直接使用 .minecraft。</summary>
public interface IInstanceService
{
    IReadOnlyList<Models.InstanceInfo> Instances { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 为 <paramref name="versionName"/> 建立实例。实例目录就是版本目录
    /// （<c>&lt;game&gt;/versions/&lt;versionName&gt;/</c>），因此同一版本重复调用是幂等的：
    /// 已有实例时直接返回它，不重建目录、不覆盖 <c>instance.json</c>。
    /// </summary>
    Task<Models.InstanceInfo> CreateAsync(string name, string versionName, CancellationToken cancellationToken = default);

    Task SaveAsync(Models.InstanceInfo instance, CancellationToken cancellationToken = default);

    /// <summary>删除实例目录及其全部内容（即整个版本目录）。</summary>
    Task DeleteAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>实例对应的工作目录绝对路径：<c>&lt;game&gt;/versions/&lt;VersionName&gt;</c>。</summary>
    string GetGameDirectory(string instanceId);

    /// <summary>
    /// 复制一个实例。新实例用新的 Id 与目录，版本名沿用源实例；内容按 <paramref name="options"/> 复制。
    ///
    /// 实现方必须覆盖：默认实现抛 <see cref="NotSupportedException"/>，
    /// 让「UI 接了按钮但服务没实现」在第一次点击时立刻暴露，而不是静默什么也没发生。
    /// </summary>
    Task<Models.InstanceInfo?> CloneAsync(
        string instanceId,
        Models.InstanceCloneOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现克隆。");

    /// <summary>改显示名。只改 <c>instance.json</c>，不动目录名（目录名就是 Id）。</summary>
    Task RenameAsync(string instanceId, string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现重命名。");

    /// <summary>设置图标。<paramref name="kind"/> 为 <c>Custom</c> 时 <paramref name="iconPath"/> 必填。</summary>
    Task SetIconAsync(
        string instanceId,
        Models.InstanceIconKind kind,
        string? iconPath,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现图标设置。");

    /// <summary>
    /// 把实例移入启动器回收站（跨平台一致的 <c>&lt;data&gt;/trash/instances/</c>），而不是直接删除。
    /// 返回回收站条目；实例不存在时返回 null。
    /// </summary>
    Task<Models.InstanceTrashEntry?> MoveToTrashAsync(string instanceId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现删除到回收站。");

    /// <summary>列出回收站里的实例。</summary>
    Task<IReadOnlyList<Models.InstanceTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现回收站列表。");

    /// <summary>把回收站条目恢复成实例。目标 Id 被占用时返回 false。</summary>
    Task<bool> RestoreFromTrashAsync(string trashId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现回收站恢复。");

    /// <summary>彻底删除回收站条目（不可恢复）。</summary>
    Task PurgeTrashAsync(string trashId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现回收站清理。");

    /// <summary>清理实例目录里的日志 / 崩溃报告等可再生成文件。失败项逐条返回，不抛异常。</summary>
    Task<Models.InstanceCleanupReport> CleanupAsync(
        string instanceId,
        Models.InstanceCleanupOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前实例服务未实现目录清理。");

    /// <summary>
    /// 原地更换实例的加载器：把实例绑定的底层版本从「<c>&lt;mc&gt; 原版</c>」换成
    /// 「<c>&lt;mc&gt; Fabric / Quilt / Forge / NeoForge</c>」，或从一个加载器换到另一个。
    ///
    /// **实例目录下的内容一律不动**（mods / config / saves / options.txt / servers.dat）：
    /// 换的只是「启动时用哪个版本目录」，而版本目录在 <c>versions/</c> 下、与实例目录分离。
    ///
    /// 三条约束：
    /// 1. **同 Minecraft 版本才允许换**。<paramref name="targetMinecraftVersion"/> 非空时
    ///    必须与实例当前解析出的版本相同，否则直接拒绝——跨版本会让存档与 mods 全部失效，
    ///    那不是「换加载器」，是「换游戏」。
    /// 2. 装失败时实例的 <c>VersionName</c> 保持原值，不写入半成品状态。
    /// 3. 返回值里的 <see cref="Models.InstanceLoaderSwitchResult.RequiresModConfirmation"/>
    ///    只描述「会不会改变现有 mods 的可用性」，**确认由界面完成**；本方法不弹窗、不阻塞。
    ///
    /// 默认实现返回失败结果，不抛异常：界面拿到的是可展示的中文原因。
    /// </summary>
    /// <param name="instanceId">目标实例 Id。</param>
    /// <param name="kind">目标加载器；<see cref="Models.LoaderKind.Vanilla"/> 表示拆掉加载器。</param>
    /// <param name="loaderVersion">目标加载器版本号；null 表示取该游戏版本的最新版。</param>
    /// <param name="progress">安装进度。</param>
    /// <param name="targetMinecraftVersion">
    /// 可选：调用方希望的 Minecraft 版本。仅用于**校验**（必须与当前实例一致），
    /// 不参与安装——真正装什么版本永远取实例自己的版本。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<Models.InstanceLoaderSwitchResult> SwitchLoaderAsync(
        string instanceId,
        Models.LoaderKind kind,
        string? loaderVersion = null,
        IProgress<Models.DownloadProgress>? progress = null,
        string? targetMinecraftVersion = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.InstanceLoaderSwitchResult.Fail("当前实例服务未实现换加载器。"));
}

/// <summary>
/// 实例运行锁：同一个游戏目录同时只允许一个游戏进程。
///
/// 锁文件是目录下的 <c>.nebula-run.json</c>，内容见 <see cref="Models.InstanceLockInfo"/>。
/// 判据是「PID + 进程启动时间」：只比 PID 会被系统复用骗过，上一次退出后新进程
/// 拿到同一个 PID 时，用户会觉得「游戏明明没开，却一直说有人在跑」。
///
/// 失败的两种形态必须分开：
/// 1. **抢不到**（活着别的进程持有）→ <see cref="TryAcquireAsync"/> 返回 <c>null</c>，
///    调用方据此拒绝启动并给出 PID；
/// 2. **锁文件坏了 / 进程已退出** → 一律当僵死锁接管并写日志，绝不抛异常。
///    用户手改过锁文件、进程被任务管理器杀掉，都不该让实例永久打不开。
/// </summary>
public interface IInstanceLockService
{
    /// <summary>
    /// 查询目录当前是否被活着的游戏进程持有。返回 <c>null</c> 表示没有活锁
    /// （文件不存在 / 进程已退出 / 文件损坏）。
    ///
    /// 默认实现返回 <c>null</c>：没有锁服务不等于有锁在跑，不能把「不支持」
    /// 误报成「被占用」——那会让用户在旧实现上永远启动不了。
    /// </summary>
    Task<Models.InstanceLockInfo?> QueryAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
        => Task.FromResult<Models.InstanceLockInfo?>(null);

    /// <summary>
    /// 尝试获取目录的运行锁。已存在有效锁时返回 <c>null</c>。
    ///
    /// 默认实现返回一个不落盘的空句柄：契约先落地、实现后跟上时（HANDOVER §5.1），
    /// 启动链必须照常工作，中间态不能把游戏拦下来。
    /// </summary>
    /// <param name="gameDirectory">游戏目录（实例目录或全局 <c>.minecraft</c>）。</param>
    /// <param name="versionName">本次启动的版本目录名，仅用于给用户看「跑的是哪个版本」。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IInstanceLockHandle?> TryAcquireAsync(
        string gameDirectory,
        string? versionName = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IInstanceLockHandle?>(NoopInstanceLockHandle.Instance);
}

/// <summary>
/// 一次成功获取的运行锁。
///
/// 释放只删**自己写的那份**（比对令牌）：锁可能已经被判僵死并由另一个进程接管，
/// 这时删文件会把别人正在用的锁抹掉，等于把重复启动保护直接关掉。Dispose 必须幂等。
/// </summary>
public interface IInstanceLockHandle : IDisposable, IAsyncDisposable
{
    /// <summary>本次持锁的完整记录（PID / 启动时间 / 版本名 / 令牌）。</summary>
    Models.InstanceLockInfo Info { get; }

    /// <summary>
    /// 游戏进程启动后，把锁的「持有者」从启动器进程改成游戏进程。返回是否写入成功。
    ///
    /// 为什么必须改：锁最初写在启动器名下，用户关掉启动器（默认关闭即退出）而游戏还在跑时，
    /// 下次启动器打开会看到「持有者 PID 已死」→ 判成僵死锁接管 → 同一实例可以再起一个 JVM，
    /// 两个进程同时写 saves/（AUDIT 复核 `REVIEW-R34-R36-CODE.md` P2-6）。
    ///
    /// 默认实现返回 false：空句柄（不落盘）与旧实现不需要这个能力，调用方按「没改成功」记日志即可。
    /// </summary>
    /// <param name="processId">游戏进程 PID。</param>
    /// <param name="processStartUtc">游戏进程启动时间（UTC），与 PID 一起排除 PID 复用。</param>
    bool UpdateProcessId(int processId, DateTimeOffset processStartUtc) => false;
}

/// <summary>
/// 不支持锁能力时的空句柄：不写盘、Dispose 无操作。
///
/// 放在契约文件里而不是实现文件：接口默认实现要用它，而 <c>Abstractions</c> 是唯一
/// 允许被实现方引用的边界，默认实现不能倒过来依赖 <c>Core.Instances</c> 的实现细节。
/// </summary>
internal sealed class NoopInstanceLockHandle : IInstanceLockHandle
{
    public static readonly NoopInstanceLockHandle Instance = new();

    public Models.InstanceLockInfo Info { get; } = new()
    {
        Pid = Environment.ProcessId,
        ProcessStartUtc = DateTimeOffset.UtcNow,
        StartedAtUtc = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 实例游戏目录里的一个快捷入口：存档 / 模组 / 资源包 / 截图 / 日志。
///
/// <see cref="RelativePath"/> 是相对游戏目录的名字，**不是绝对路径**：同一个实例可能在
/// 实例目录与全局 <c>.minecraft</c> 两种根下解析，把绝对路径写进模型会让「换根」时全部失效。
/// </summary>
/// <param name="Key">稳定标识，界面命令参数用它。</param>
/// <param name="DisplayName">卡片上显示的短名。</param>
/// <param name="RelativePath">相对游戏目录的子目录名。</param>
/// <param name="CreateOnOpen">打开前是否允许按需创建（没截过图时 <c>screenshots/</c> 并不存在）。</param>
public sealed record InstanceFolder(string Key, string DisplayName, string RelativePath, bool CreateOnOpen)
{
    /// <summary>
    /// 目录当前是否存在。由枚举时按磁盘事实填充；静态目录表里恒为 <c>false</c>。
    /// 界面据此把「还没建出来的目录」画成弱化态，而不是显示一个点了没反应的入口。
    /// </summary>
    public bool Exists { get; init; }
}

/// <summary>
/// 实例游戏目录的快捷入口清单。
///
/// 只回答「有哪几项、相对路径是什么、路径拼出来是什么」，**不写盘**：目录在用户点击时
/// 才由界面层创建（<c>screenshots/</c> 在第一次截图之前本来就不存在，扫描期建目录等于
/// 替用户改游戏目录）。
///
/// 默认实现一律返回「没有入口」：目录表是纯数据，放在 <c>Core.Instances.InstanceFolders</c>。
/// 契约文件只依赖 Models，所以默认实现不复制一份目录表——两份目录表迟早会漂。
/// 未注册时退化为「卡片上没有芯片」，其余入口（打开目录 / 启动 / 删除）照常可用。
/// </summary>
public interface IInstanceFolderService
{
    /// <summary>按磁盘事实列出某个游戏目录下的快捷入口；目录参数为空时返回空列表，不抛异常。</summary>
    IReadOnlyList<InstanceFolder> List(string? gameDirectory) => Array.Empty<InstanceFolder>();

    /// <summary>按 Key 取一项定义（不含磁盘状态）；未知 Key 返回 <c>null</c>。默认实现返回 <c>null</c>。</summary>
    InstanceFolder? Find(string? key) => null;

    /// <summary>
    /// 拼出「游戏目录 + 相对名」的绝对路径，并保证结果严格位于游戏目录之下；
    /// 未知 Key、空目录参数、越界结果一律返回 <c>null</c>，不抛异常。
    /// </summary>
    string? ResolvePath(string? gameDirectory, string? key) => null;
}

/// <summary>
/// 「当前生效的实例」——版本隔离的运行时状态。
///
/// 之前的问题：<see cref="IInstanceService"/> 只有实例列表与「按 Id 取目录」，
/// 没有「现在用哪个」的概念，结果模组页 / 资源页 / 世界页各自退回全局 <c>.minecraft</c>，
/// 实例目录里的 mods 永远读不到，下载也永远装到全局目录去。
///
/// 现在的规则：所有与「游戏目录」相关的页面都必须经本接口取目录，不再直接读
/// <see cref="ILauncherPaths.MinecraftDirectory"/>。
/// </summary>
public interface IInstanceContext
{
    /// <summary>当前选中的实例；<c>null</c> 表示不隔离，直接用全局 <c>.minecraft</c>。</summary>
    Models.InstanceInfo? Current { get; }

    /// <summary>当前生效的游戏目录绝对路径（实例目录或全局 <c>.minecraft</c>）。</summary>
    string GameDirectory { get; }

    /// <summary>当前生效的版本目录名；可能为 null（尚未选过版本）。</summary>
    string? VersionName { get; }

    /// <summary>当前生效的加载器；未确定时为 <see cref="Models.LoaderKind.Vanilla"/>。</summary>
    Models.LoaderKind Loader { get; }

    /// <summary>当前生效的原版 Minecraft 版本号；未确定时为 null。</summary>
    string? MinecraftVersion { get; }

    /// <summary>选择项或实例列表变化时触发。UI 订阅后重建列表，不得在回调里做重活。</summary>
    event EventHandler? Changed;

    /// <summary>读取实例列表并恢复上次选择（<c>settings.json</c> 的 <c>lastInstanceId</c>）。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换当前实例。传 <c>null</c> 表示回到全局目录。
    /// 实例不存在时退化为全局目录，不抛异常——删掉实例后页面仍要能渲染。
    /// </summary>
    Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 枚举所有可独立管理内容的目标：全局 <c>.minecraft</c> 一个，加上每个实例一个。
    /// 结果供模组页按「加载器 → 版本」分组展示。
    /// </summary>
    Task<IReadOnlyList<Models.ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>按 Key 取一个目标（<c>global</c> 或实例 Id）。找不到返回 null。</summary>
    Task<Models.ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析「启动 <paramref name="versionName"/> 时该用哪个游戏目录」。
    ///
    /// 顺序：已有实例 → 按 <see cref="Models.AppSettings.IsolationPolicy"/> 自动建实例 → 全局 <c>.minecraft</c>。
    /// 解析完成后会把上下文切到对应实例（或全局），因此调用方拿到目录后不需要再调 <see cref="SelectAsync"/>。
    ///
    /// 该方法永远返回可用目录，不抛异常：建实例失败时退化为全局目录并把原因写进日志。
    /// </summary>
    Task<Models.InstanceResolution> ResolveLaunchTargetAsync(
        string versionName,
        CancellationToken cancellationToken = default);
}
