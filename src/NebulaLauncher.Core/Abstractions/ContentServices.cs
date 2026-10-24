namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 回收站里的一个内容条目。
///
/// 为什么不复用 <see cref="Models.InstanceTrashEntry"/>：实例回收站保存的是整个游戏目录 + 重新注册的
/// 实例 Id，内容回收站保存的是单个文件（也可能是数据包那样的目录）及其原始绝对路径。
/// 两者只有「都在 <c>&lt;data&gt;/trash/</c> 下」这一点相同，合成一个类型会让两边都长出用不到的字段。
/// </summary>
public sealed class ContentTrashEntry
{
    /// <summary>回收站条目 Id，用于 <see cref="IContentService.RestoreAsync"/> / <see cref="IContentService.PurgeTrashAsync"/>。</summary>
    public required string Id { get; init; }

    /// <summary>删除前的绝对路径。恢复时按它回去。</summary>
    public required string OriginalPath { get; init; }

    /// <summary>删除前的文件名（或目录名）。界面显示用。</summary>
    public required string FileName { get; init; }

    /// <summary>回收站里的实际落点。</summary>
    public required string TrashPath { get; init; }

    /// <summary>删除时记下的内容类型。恢复时不依赖它，但列表要按类型显示图标与分类名。</summary>
    public Models.ContentKind Kind { get; init; }

    public DateTimeOffset DeletedAt { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>数据包可以是目录，删除与恢复都要走 <c>Directory.Move</c>。</summary>
    public bool IsDirectory { get; init; }
}

/// <summary>
/// 一次「批量更新本地模组」的结果。
///
/// 逐项独立成败，与 <see cref="Models.ModUpdateReport"/> 的检查侧口径对齐：
/// 一个 jar 下载失败不该让另外九个已经替换成功的文件回滚（回滚本身也会失败）。
/// </summary>
public sealed class ModUpdateBatchResult
{
    /// <summary>至少成功更新一项。</summary>
    public bool Success => Updated > 0;

    public int Updated { get; init; }

    public int Failed { get; init; }

    /// <summary>没有可下载地址 / 不是「有新版本」状态而被跳过的条目数。</summary>
    public int Skipped { get; init; }

    /// <summary>失败项的文件名。</summary>
    public IReadOnlyList<string> FailedFiles { get; init; } = [];

    /// <summary>逐条失败原因（中文），已包含文件名前缀。界面按前几条展示。</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>整体失败原因（例如所有条目都缺下载地址）；逐条原因在 <see cref="Failures"/>。</summary>
    public string? Error { get; init; }

    /// <summary>「更新 N 个 / 失败 M 个」。</summary>
    public string Summary => Skipped > 0
        ? $"更新 {Updated} 个 / 失败 {Failed} 个 / 跳过 {Skipped} 个"
        : $"更新 {Updated} 个 / 失败 {Failed} 个";

    public static ModUpdateBatchResult Fail(string error) => new() { Error = error };
}

/// <summary>模组 / 资源包 / 光影包的扫描与管理。</summary>
public interface IContentService
{
    Task<IReadOnlyList<Models.ContentItem>> ScanAsync(
        string gameDirectory,
        Models.ContentKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>启用或禁用。模组通过改扩展名实现，资源包同理。</summary>
    Task SetEnabledAsync(Models.ContentItem item, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除一项内容。默认语义是**移入启动器回收站**（<c>&lt;data&gt;/trash/contents/</c>），
    /// 之后可以用 <see cref="ListTrashAsync"/> / <see cref="RestoreAsync"/> 找回。
    ///
    /// 抄 PCL2 的 <c>PageInstanceMod.xaml.vb:666-683 DeleteMods</c>：
    /// <c>FileUtils.Delete(path, Not IsShiftPressed)</c>——只有按住 Shift 才是永久删除。
    /// </summary>
    Task DeleteAsync(Models.ContentItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除一项内容，可选择永久删除。
    ///
    /// <paramref name="permanent"/> 为 <c>false</c> 时等价于 <see cref="DeleteAsync(Models.ContentItem, CancellationToken)"/>。
    /// 默认实现直接转调既有单参重载：老实现不写这一条也仍然可用。
    /// </summary>
    Task DeleteAsync(Models.ContentItem item, bool permanent, CancellationToken cancellationToken = default)
        => DeleteAsync(item, cancellationToken);

    /// <summary>
    /// 列出回收站里的内容条目，按删除时间降序。
    /// 缺省实现返回空集合：契约先落地、实现后跟上的中间态必须可编译。
    /// </summary>
    Task<IReadOnlyList<ContentTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ContentTrashEntry>>([]);

    /// <summary>
    /// 把回收站条目放回删除前的位置。目标已存在时改名落盘（<c>xxx (1).jar</c>），不覆盖。
    /// 缺省实现返回 <c>false</c>。
    /// </summary>
    Task<bool> RestoreAsync(string trashId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>彻底删除一个回收站条目。缺省实现不做事。</summary>
    Task PurgeTrashAsync(string trashId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>把外部文件复制进对应内容目录，自动处理同名冲突。</summary>
    Task<IReadOnlyList<string>> ImportAsync(
        string gameDirectory,
        Models.ContentKind kind,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default);

    /// <summary>检测同一 modid 的重复版本等常见冲突。</summary>
    IReadOnlyList<Models.ModConflict> DetectConflicts(IReadOnlyList<Models.ContentItem> mods);

    /// <summary>
    /// 检测「缺前置」：用已启用模组的 <see cref="Models.ContentItem.Id"/> 建索引，
    /// 再逐个检查其余模组声明的 <see cref="Models.ContentItem.Depends"/> 是否都能找到提供者。
    /// 可选依赖（<see cref="Models.ModDependency.Optional"/>）缺失同样返回，由 UI 决定降级成提示；
    /// 不抛异常、不阻断启动。
    /// </summary>
    IReadOnlyList<Models.ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<Models.ContentItem> mods);

    /// <summary>找出与目标 Minecraft 版本不兼容的模组。</summary>
    IReadOnlyList<Models.ContentItem> FindVersionMismatch(IReadOnlyList<Models.ContentItem> mods, string minecraftVersion);
}

/// <summary>整合包导入。</summary>
public interface IModpackService
{
    /// <summary>读取压缩包，解析出元信息，不落盘。</summary>
    Task<Models.ModpackPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>把整合包安装为一个独立实例目录。</summary>
    Task<string> ImportAsync(
        string archivePath,
        string instanceName,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个已配置好的实例导出成 <c>.mrpack</c>。
    ///
    /// 规则抄 PCL2 的实例导出：先按哈希反查把 jar 写成在线 <c>files[]</c> 条目（体积小），
    /// 查不到的才原样塞进 <c>overrides/</c>；<c>logs/</c> / <c>crash-reports/</c> 一律排除。
    /// 默认实现返回失败结果，不抛异常——调用方拿到的是可展示的中文原因。
    /// </summary>
    Task<Models.ModpackExportResult> ExportAsync(
        string instanceGameDirectory,
        string targetArchivePath,
        Models.ModpackExportOptions options,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.ModpackExportResult.Fail("当前整合包服务未实现导出。"));

    /// <summary>
    /// 导入时是否顺带安装整合包声明的游戏与加载器。默认关闭（实现方自己决定）。
    ///
    /// 关闭时导入只解压文件——得到一个指向不存在版本的实例，玩家得自己去版本页再装一次；
    /// 这正是本轮之前的形态。开与关都由 UI 上的开关控制，不在这里做默认值猜测。
    /// </summary>
    bool InstallGameOnImport
    {
        get => false;
        set { }
    }

    /// <summary>
    /// 最近一次导入的完整结果（含「游戏是否真的装上」）。
    ///
    /// <see cref="ImportAsync"/> 的返回值只有目录字符串，装没装成看不出来；默认实现返回 null，
    /// 调用方必须容忍拿不到结果。
    /// </summary>
    Models.ModpackImportResult? LastImport => null;
}

/// <summary>世界备份与还原。</summary>
public interface IBackupService
{
    Task<IReadOnlyList<Models.WorldInfo>> ListWorldsAsync(string gameDirectory, CancellationToken cancellationToken = default);

    Task<Models.BackupEntry> CreateAsync(
        string worldDirectory,
        string? note = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Models.BackupEntry> CreateAutomaticAsync(
        string worldDirectory,
        string? note = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => CreateAsync(worldDirectory, note, progress, cancellationToken);

    Task<IReadOnlyList<Models.BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default);

    /// <summary>把备份解压回 <paramref name="targetGameDirectory"/>/saves。</summary>
    Task RestoreAsync(
        Models.BackupEntry entry,
        string targetGameDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Models.BackupEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 轮转清理：每个世界只保留最近 <paramref name="keepPerWorld"/> 份自动备份。
    ///
    /// 自动备份必须配轮转，否则每退出一次就多一份几百 MB 的存档，
    /// 磁盘会在用户发现之前就被吃满。返回删除的份数；默认实现不删除。
    /// </summary>
    Task<int> PruneAsync(int keepPerWorld, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <summary>
    /// 改写存档的 <c>level.dat</c>（种子 / 游戏模式 / 难度 / 作弊 / 保留物品栏 / 世界名）。
    ///
    /// 实现必须遵守三条：
    /// 1. 写入前先把原文件复制成同目录的 <c>level.dat.nebula.bak</c>；
    /// 2. 不认识的 NBT 标签原样保留，只改请求里点名的字段；
    /// 3. 世界正被游戏占用（<c>session.lock</c> 拿不到独占访问）时拒绝写入。
    ///
    /// 默认实现返回失败，不抛异常——旧实现不需要为新增能力改一行代码。
    /// </summary>
    Task<Models.WorldEditResult> UpdateWorldAsync(
        string worldDirectory,
        Models.WorldEditRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.WorldEditResult.Fail("当前备份服务未实现存档编辑。"));
}

/// <summary>
/// 存档缩略图：把 <c>level.dat</c> 的 <c>Data.icon</c>（base64 PNG）提取到启动器缓存目录。
///
/// 所有失败都退化为 null 而不是异常：图标只是列表页的装饰，坏图标、超大图标、非 PNG
/// 只该让那张卡显示占位块，不该让整个世界列表读不出来。
/// 默认实现直接返回 null，旧实现不需要为新增能力改一行代码。
/// </summary>
public interface IWorldIconService
{
    /// <summary>
    /// 取该存档缩略图的缓存文件绝对路径；没有图标或图标不可用时返回 null。
    ///
    /// 返回值是文件路径而不是字节：位图解码属于 UI 层，Core 不引 Avalonia 类型。
    /// </summary>
    Task<string?> GetIconPathAsync(string worldDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// Modrinth 在线资源库：搜索项目、列出可下载版本、把选定版本装进本地内容目录。
///
/// 所有网络失败都退化为「空集合 + <see cref="LastError"/>」，不把异常抛给 UI：
/// 页面在离线时仍要能正常渲染，只是列表为空并给出一条中文提示。
/// </summary>
public interface IModrinthService
{
    /// <summary>最近一次失败的用户可读原因（中文）。成功或尚未请求时为 null。</summary>
    string? LastError { get; }

    /// <summary>
    /// 搜索项目。<paramref name="query"/> 为空表示「按下载量浏览热门」；
    /// <paramref name="gameVersion"/> 为 null 表示不按游戏版本过滤；
    /// <paramref name="loader"/> 为 <c>Vanilla</c> 表示不按加载器过滤。
    /// </summary>
    Task<IReadOnlyList<Models.ModrinthProject>> SearchAsync(
        Models.ContentKind kind,
        string query,
        string? gameVersion,
        Models.LoaderKind loader,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 带排序键的搜索。<paramref name="sort"/> 取 Modrinth 的 <c>index</c> 值
    /// （<c>relevance</c> / <c>downloads</c> / <c>follows</c> / <c>newest</c> / <c>updated</c>），
    /// null 或无法识别时按「有查询用相关度、没查询用下载量」的默认规则。
    /// 默认实现忽略排序转调 <see cref="SearchAsync"/>：任何实现都能编译，不强制重写。
    /// </summary>
    Task<IReadOnlyList<Models.ModrinthProject>> SearchSortedAsync(
        Models.ContentKind kind,
        string query,
        string? gameVersion,
        Models.LoaderKind loader,
        int offset,
        int limit,
        string? sort,
        CancellationToken cancellationToken = default) =>
        SearchAsync(kind, query, gameVersion, loader, offset, limit, cancellationToken);

    /// <summary>列出某个项目在指定游戏版本 / 加载器下可安装的版本，按发布时间降序。</summary>
    Task<IReadOnlyList<Models.ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string? gameVersion,
        Models.LoaderKind loader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 拉项目详情（正文、图库、源码/问题链接、许可）。
    ///
    /// 搜索结果只带摘要；详情页与「这个模组到底是什么」只能靠 <c>/v2/project/{id}</c>。
    /// 失败返回 null 并写 <see cref="LastError"/>，不抛异常。默认实现返回 null。
    /// </summary>
    Task<Models.ModrinthProject?> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<Models.ModrinthProject?>(null);

    /// <summary>
    /// 批量拉项目信息（<c>GET /v2/projects?ids=[...]</c>）。
    ///
    /// 模组页补全要一次问几十个项目的名字 / slug / 描述，逐个 <see cref="GetProjectAsync"/>
    /// 会把 100 个模组变成 100 次请求并撞上 Modrinth 的限速。默认实现退化成逐个查询，
    /// 只为不破坏既有实现方；真实实现必须走批量端点。
    /// </summary>
    async Task<IReadOnlyList<Models.ModrinthProject>> GetProjectsAsync(
        IReadOnlyList<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        var results = new List<Models.ModrinthProject>();

        foreach (var projectId in projectIds)
        {
            var project = await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

            if (project is not null)
            {
                results.Add(project);
            }
        }

        return results;
    }

    /// <summary>
    /// 用一批 SHA1 批量反查版本（<c>POST /v2/version_files</c>）。
    /// 返回的字典键是小写 SHA1；未收录的哈希不会出现在结果里。
    /// 默认实现退化成逐个 <see cref="LookupBySha1Async"/>。
    /// </summary>
    async Task<IReadOnlyDictionary<string, Models.ModrinthVersion>> LookupBySha1BatchAsync(
        IReadOnlyList<string> sha1s,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, Models.ModrinthVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var sha1 in sha1s)
        {
            var hit = await LookupBySha1Async(sha1, cancellationToken).ConfigureAwait(false);

            if (hit is not null)
            {
                results[sha1] = hit;
            }
        }

        return results;
    }

    /// <summary>
    /// 下载并安装到 <paramref name="gameDirectory"/> 下对应的内容子目录。
    /// 同名文件已存在时以 <c>xxx (1).jar</c> 落盘，不覆盖已有文件；SHA1 不匹配则丢弃。
    /// </summary>
    Task<Models.ModrinthInstallResult> InstallAsync(
        Models.ModrinthVersion version,
        string gameDirectory,
        Models.ContentKind kind,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>把项目图标（异地 webp / png）缓存到本地，返回本地路径；失败返回 null。</summary>
    Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 用 SHA1 反查一个文件属于哪个项目、哪个版本（Modrinth <c>/v2/version_file/{hash}</c>）。
    /// 未收录时返回 null 并写 <see cref="LastError"/>；不抛异常。
    /// </summary>
    Task<Models.ModrinthVersion?> LookupBySha1Async(string sha1, CancellationToken cancellationToken = default);

    /// <summary>
    /// 算出「装这个版本要连带装哪些前置」。
    ///
    /// 规则按 Modrinth 的 <c>dependencies[]</c>：<c>required</c> 递归展开（深度上限 8、按 projectId 防环），
    /// <c>optional</c> 只收集，<c>incompatible</c> 记入 <see cref="Models.ModrinthInstallPlan.Blocked"/>，
    /// <c>embedded</c> 跳过。只给 <c>version_id</c> 的依赖降级为 Warning，不阻断。
    /// 默认实现返回「未实现」，不抛异常。
    /// </summary>
    Task<Models.ModrinthInstallPlan> PlanInstallAsync(
        Models.ModrinthVersion root,
        string? gameVersion,
        Models.LoaderKind loader,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new Models.ModrinthInstallPlan
        {
            Root = root,
            Blocked = PlanMessages.DependencyNotImplemented,
        });

    /// <summary>
    /// 按计划逐个下载安装。逐文件独立成败，根版本失败但前置成功时也照实返回。
    /// 默认实现返回失败结果，不抛异常。
    /// </summary>
    Task<Models.ModrinthPlanInstallResult> InstallPlanAsync(
        Models.ModrinthInstallPlan plan,
        string gameDirectory,
        Models.ContentKind kind,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new Models.ModrinthPlanInstallResult
        {
            Success = false,
            Error = PlanMessages.InstallNotImplemented,
        });
}

/// <summary>接口默认实现使用的固定文案。集中放置以避免每次调用新建数组（CA1861）。</summary>
internal static class PlanMessages
{
    internal static readonly string[] DependencyNotImplemented = ["当前数据源未实现依赖解析。"];

    internal const string InstallNotImplemented = "当前数据源未实现计划安装。";
}

/// <summary>
/// 本地模组的在线身份识别与更新检查。
///
/// 抄 PCL2 的 <c>LocalResourceFile</c> + <c>LocalResourceOnlineLoad</c>：
/// 先算文件哈希（Modrinth SHA1 / CurseForge MurmurHash2 指纹），再用哈希反查项目，
/// 最后比较远程最新版本与本地版本。哈希结果落盘缓存，第二次检查不再重算。
/// </summary>
public interface IModUpdateService
{
    /// <summary>最近一次检查的整体失败原因（中文）；成功或未检查时为 null。</summary>
    string? LastError { get; }

    /// <summary>
    /// 批量检查。实现必须遵守：不抛异常（取消除外）、逐个条目独立失败、
    /// 网络不可用时整体返回带 <see cref="Models.ModUpdateReport.Error"/> 的空报告。
    /// </summary>
    /// <param name="items">待检查的本地内容项（通常只传模组）。</param>
    /// <param name="gameDirectory">游戏目录，用于定位哈希缓存与判断是否被占用。</param>
    /// <param name="minecraftVersion">目标 Minecraft 版本，用于筛选兼容的远程版本；null 表示不过滤。</param>
    /// <param name="loader">加载器类型，用于筛选远程版本。</param>
    Task<Models.ModUpdateReport> CheckAsync(
        IReadOnlyList<Models.ContentItem> items,
        string gameDirectory,
        string? minecraftVersion,
        Models.LoaderKind loader,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>计算并缓存一个文件的模组哈希集合。已缓存且文件未变化时直接返回。</summary>
    Task<Models.ModFileHashes> GetHashesAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 对「有新版本」的条目执行下载 → 校验 → 替换，旧文件进回收站。
    ///
    /// 抄 PCL2 的 <c>PageInstanceMod.xaml.vb:528-560</c>：
    /// 只处理调用方传进来的条目（页面传「已勾选且 <see cref="Models.ModUpdateInfo.HasUpdate"/>」的那些），
    /// 文件名按「段对齐」规则从旧名推出新名，避免在 <c>mods/</c> 里留下新旧两份导致重复 modid。
    ///
    /// 逐项独立成败、不抛异常（取消除外）。缺省实现返回「未实现」，不抛异常。
    /// </summary>
    /// <param name="items">要更新的条目，通常来自 <see cref="CheckAsync"/> 的结果。</param>
    /// <param name="gameDirectory">游戏目录，用于定位旧文件与回收站。</param>
    Task<ModUpdateBatchResult> UpdateAsync(
        IReadOnlyList<Models.ModUpdateInfo> items,
        string gameDirectory,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ModUpdateBatchResult.Fail("当前数据源未实现模组更新。"));
}

/// <summary>收藏的服务器列表与状态查询。</summary>
public interface IServerService
{
    IReadOnlyList<Models.ServerEntry> Servers { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增或覆盖一条服务器并落盘。
    ///
    /// 保存失败时抛 <see cref="System.IO.IOException"/>，且内存列表已回滚到调用前状态——
    /// 调用方不能把「没有异常」以外的信号当作成功（界面显示成功、重启后消失就是这种失败形态）。
    /// </summary>
    Task AddAsync(Models.ServerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>删除并落盘；保存失败时抛 <see cref="System.IO.IOException"/> 并把条目放回原位置。</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    Task<Models.ServerStatus> PingAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 <c>&lt;gameDirectory&gt;/servers.dat</c> 导入 Minecraft 自带的服务器列表。
    ///
    /// 合并规则：按 <see cref="Models.ServerEntry.Address"/> 去重，已存在的地址整条跳过
    /// （不覆盖用户改过的名字），返回值只含本次**新增**的条目。
    /// 文件缺失、损坏、非 NBT 都返回空集合，不抛异常；新增条目落盘失败则撤掉本次新增并抛
    /// <see cref="System.IO.IOException"/>。
    ///
    /// 缺省实现返回空集合：为了让「契约先落地、实现后跟上」的中间态始终可编译，
    /// 也为了让没有文件系统访问权的测试替身不必实现它。
    /// </summary>
    /// <param name="gameDirectory">游戏目录（实例目录或全局 <c>.minecraft</c>）。</param>
    Task<IReadOnlyList<Models.ServerEntry>> ImportFromServersDatAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Models.ServerEntry>>([]);

    /// <summary>
    /// 最近一次 <see cref="ImportFromServersDatAsync"/> 的统计（读到几条 / 新增几条 / 跳过几条），
    /// 供界面显示「新增 N 个，跳过 M 个重复」。没导入过或走缺省实现时为 <c>null</c>。
    /// </summary>
    ServerImportSummary? LastServersDatImport => null;
}
