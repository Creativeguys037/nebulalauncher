namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// CurseForge 在线资源库客户端。
///
/// 与 <see cref="IModrinthService"/> 的差异（都不是可以抹平的细节）：
///
/// - <b>必须带 API key</b>。<c>x-api-key</c> 缺失时官方接口直接 403。key 内置于
///   <c>Mods.CurseForgeService.EmbeddedApiKey</c>，用户不再配置，因此本接口没有「未配置」状态，
///   也没有对应的引导文案。
/// - <b>分页参数叫 <c>index + pageSize</c></b>，pageSize 上限 50（Modrinth 是 100）。
/// - <b>部分文件没有直链</b>。作者关闭第三方分发时 <c>downloadUrl</c> 为 null，
///   <see cref="Models.CurseForgeFile.IsDownloadable"/> 为 false，UI 必须给「去网页下载」出口。
///
/// 失败策略与 Modrinth 一致：公开方法不抛异常（取消除外），原因写进 <see cref="LastError"/>。
/// </summary>
public interface ICurseForgeService
{
    /// <summary>最近一次失败的用户可读原因（中文）。成功或尚未请求时为 null。</summary>
    string? LastError { get; }

    /// <summary>
    /// 搜索项目。<paramref name="query"/> 为空表示浏览热门（按下载量降序）。
    /// <paramref name="gameVersion"/> 为 null 表示不按游戏版本过滤；
    /// <paramref name="loader"/> 为 <c>Vanilla</c> 表示不按加载器过滤。
    /// </summary>
    Task<IReadOnlyList<Models.CurseForgeProject>> SearchAsync(
        Models.ContentKind kind,
        string query,
        string? gameVersion,
        Models.LoaderKind loader,
        int index,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>列出某个项目在指定游戏版本 / 加载器下可用的文件，按发布时间降序。</summary>
    Task<IReadOnlyList<Models.CurseForgeFile>> GetFilesAsync(
        long modId,
        string? gameVersion,
        Models.LoaderKind loader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载并安装到 <paramref name="gameDirectory"/> 下对应的内容子目录。
    /// 同名文件已存在时以 <c>xxx (1).jar</c> 落盘；SHA1 可用且不匹配则丢弃。
    /// 文件没有直链时返回失败结果并给出「去网页下载」文案。
    /// </summary>
    Task<Models.ContentInstallResult> InstallAsync(
        Models.CurseForgeFile file,
        string gameDirectory,
        Models.ContentKind kind,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>把项目图标缓存到本地，返回本地路径；失败返回 null。</summary>
    Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 拼搜索 URL（纯函数，供测试断言参数拼装）。世界存档没有对应 classId，返回空字符串。
    /// </summary>
    string BuildSearchUrl(
        Models.ContentKind kind,
        string? query,
        string? gameVersion,
        Models.LoaderKind loader,
        int index,
        int pageSize);

    /// <summary>
    /// 按 slug 或 CurseForge 项目链接取一个项目及其可安装文件。
    /// 输入可以是 <c>jei</c>、<c>https://www.curseforge.com/minecraft/mc-mods/jei</c>、
    /// 或带文件路径的 <c>.../mc-mods/jei/files/9268986</c>。
    /// 走 cfwidget，返回的项目带 <c>Files</c>（已按版本 / 加载器过滤）。
    /// 解析失败返回 null 并写 <see cref="LastError"/>。
    /// </summary>
    Task<Models.CurseForgeProject?> GetProjectBySlugAsync(
        Models.ContentKind kind,
        string slugOrUrl,
        string? gameVersion,
        Models.LoaderKind loader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 用文件指纹反查项目（<c>POST /v1/fingerprints</c>）。这是 CurseForge 官方提供的
    /// 「本地 jar 属于哪个项目」接口，与 Modrinth 的 SHA1 反查等价。
    ///
    /// 该端点只存在于官方 API（cfwidget 没有），因此也只能走内置 key 这条链路。
    /// 未匹配上的指纹不会出现在结果里，调用方按数量差判断「未收录」。
    /// </summary>
    Task<IReadOnlyList<Models.CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
        IReadOnlyList<uint> fingerprints,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量取项目信息（<c>POST /v1/mods</c>）。模组页补全用：指纹反查只给出 modId 与文件名，
    /// 名字 / slug / 摘要还要再问一次。默认实现返回空集合。
    /// </summary>
    Task<IReadOnlyList<Models.CurseForgeProject>> GetProjectsAsync(
        IReadOnlyList<long> modIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Models.CurseForgeProject>>([]);

    // ---------------- 整合包 ----------------
    //
    // 整合包是独立入口，不给 Models.ContentKind 加 ModPack 值：ContentKind 被安装落点、目录名、
    // 详情页路由等 6 处 switch 依赖，加值等于改所有既有分支。整合包条目的 Kind 仍是 Mod，
    // 靠 Models.CurseForgeProject.IsModpack 分流。
    //
    // 这四个成员一律带默认实现（空集合 / 空串 / null）：已有的测试替身（AppLibrary_DualSourceTests、
    // CoreAssets_McbbsAddonTests、CoreMods_ProjectInfoTests 里的 FakeCurseForge）只实现既有成员，
    // 不该为了一个新增入口全部改一遍。

    /// <summary>
    /// 搜索整合包（classId = 4471）。语义与 <see cref="SearchAsync"/> 一致：<paramref name="query"/>
    /// 是 CurseForge 整合包项目链接时走 slug 精确解析，否则走官方搜索接口。
    ///
    /// 为什么不按游戏版本 / 加载器过滤：玩家的顺序是先找包、再决定包里用哪个构建（同一个整包常常同时
    /// 发布 Forge / Fabric / NeoForge 版本），先按版本筛会把「其实能玩」的包从结果里删掉。
    /// 默认实现返回空集合。
    /// </summary>
    Task<IReadOnlyList<Models.CurseForgeProject>> SearchModpacksAsync(
        string query,
        int index,
        int pageSize,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Models.CurseForgeProject>>([]);

    /// <summary>
    /// 拼整合包搜索 URL（纯函数，供测试断言参数拼装）。固定 <c>gameId=432&amp;classId=4471</c>，
    /// 有搜索词才追加 <c>searchFilter</c>，URL 里不出现 <c>gameVersion</c> / <c>modLoaderType</c>
    /// —— 理由同 <see cref="SearchModpacksAsync"/>。默认实现返回空串。
    /// </summary>
    string BuildModpackSearchUrl(string? query, int index, int pageSize) => string.Empty;

    /// <summary>
    /// 列出某个整合包项目的全部文件，按发布时间降序。与 <see cref="GetFilesAsync"/> 的差别只有
    /// 一个：请求里不带 <c>gameVersion</c> / <c>modLoaderType</c>，因为整合包文件列表就是「各个
    /// 游戏版本 / 加载器组合下的整包」，由玩家自己挑。默认实现返回空集合。
    /// </summary>
    Task<IReadOnlyList<Models.CurseForgeFile>> GetModpackFilesAsync(
        long modId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Models.CurseForgeFile>>([]);

    /// <summary>
    /// 按 slug 或 CurseForge 整合包链接取一个项目及其文件（走 cfwidget 的
    /// <c>/minecraft/modpacks/{slug}</c>）。输入可以是 <c>better-mc-forge-bmc4</c> 或
    /// <c>https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4</c>。
    ///
    /// 返回的项目 <c>IsModpack</c> 为 true，<c>Files</c> 不做版本 / 加载器过滤：整合包是玩家先选包
    /// 再选构建，过滤掉「不是当前加载器」的整包等于把选择权提前收走。默认实现返回 null。
    /// </summary>
    Task<Models.CurseForgeProject?> GetModpackBySlugAsync(
        string slugOrUrl,
        CancellationToken cancellationToken = default)
        => Task.FromResult<Models.CurseForgeProject?>(null);
}
