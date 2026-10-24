using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// CurseForge 在线资源库客户端。
///
/// 为什么不复用 <c>DownloadHttpClientFactory</c>：那个客户端挂着镜像改写 handler，只对
/// launchermeta / piston-* 有意义；CurseForge 的 API 与 CDN 走自己的域名，混进去只会
/// 让镜像测试用例覆盖到不该覆盖的 URL。这里自建 HttpClient。
///
/// 为什么要 <c>x-api-key</c>：官方 API 强制它，缺失直接 403。key 内置于
/// <see cref="EmbeddedApiKey"/>，用户不再填写，因此也没有「未配置」的降级路径——
/// 所有查询方法都直接打官方接口。
///
/// 失败策略与 <see cref="ModrinthService"/> 完全一致：公开方法不向调用方抛异常（取消除外），
/// 失败写 <see cref="LastError"/> 并返回空集合 / 失败结果，原始异常只进日志。
/// </summary>
public sealed class CurseForgeService : ICurseForgeService, IDisposable
{
    /// <summary>
    /// 内置的 CurseForge API key。
    ///
    /// **这是当前唯一来源**：程序不再让用户输入 key，设置里也没有这个字段。
    /// 换 key 只改这一处；它是编译期常量，会原样进入发布产物，更换后必须重新出包。
    /// </summary>
    public const string EmbeddedApiKey = "$2a$10$OCzupZXyXQOt3Zclupqur.5SWumfU3K/C3lxF5titPCNTvz3BHL7i";

    /// <summary>官方 API 基地址。可在设置里换成镜像（<see cref="AppSettings.CurseForgeBaseUrl"/>）。</summary>
    public const string DefaultApiBase = "https://api.curseforge.com";

    /// <summary>与 Modrinth 用同一个身份串：服务端只按字符串识别客户端。</summary>
    public const string UserAgent = "NebulaLauncher/1.0 (github.com/nebula)";

    /// <summary>Minecraft 在 CurseForge 上的 gameId。</summary>
    public const int GameIdMinecraft = 432;

    // classId 就是 CurseForge 的「section id」。官方 REST 参考对它的描述只有一句原文：
    //   "classId | query | integer(int32) | false | Filter by section id (discoverable via Categories)"
    // 文档没有列出 Minecraft 的三个具体值。这里的 6 / 12 / 6552 来自 CurseForge 网站资源路径
    // （mc-mods / texture-packs / shaders）与 API classId 的对应关系，社区实现统一沿用这三个数。
    // 可信度：中高——未经真实 key 实测（手上没有可用 key）。配好 key 后可用
    // GET /v1/categories?gameId=432 里 isClass=true 的条目自证。

    /// <summary>classId：模组（mc-mods）。</summary>
    public const int ClassIdMods = 6;

    /// <summary>classId：资源包（texture-packs）。</summary>
    public const int ClassIdResourcePacks = 12;

    /// <summary>classId：光影（shaders）。</summary>
    public const int ClassIdShaders = 6552;

    /// <summary>
    /// classId：整合包（mc-modpacks）。
    ///
    /// 来源与验证方式：<c>GET /v1/mods/search?gameId=432&amp;classId=4471</c> 返回的
    /// <c>data[].classId</c> 全部是 4471；网页侧对应路径是 <c>/minecraft/modpacks/{slug}</c>。
    /// 与上面三个值一样，官方 REST 参考没有列出具体数字，只能由「搜索结果自己报的 classId」自证。
    /// </summary>
    public const int ClassIdModpacks = 4471;

    /// <summary>search 接口的 pageSize 上限是 50（Modrinth 是 100）。文件列表不走这个值，见 <see cref="FilesPageSize"/>。</summary>
    public const int MaxPageSize = 50;

    /// <summary>
    /// 文件列表每页请求的条数。
    ///
    /// 为什么不直接用 <see cref="MaxPageSize"/>：文档写 pageSize 上限 50，但 PCL2 一直传 10000
    /// 并靠 <c>resultCount == totalCount</c> 判成功（<c>ResourceVersion.vb:236-255</c>），
    /// 说明真实上限并不固定、服务端调整过。传大一点在「服务端真的接受」时能把 JEI 这种
    /// 几百个文件的项目压到 1 次请求。
    ///
    /// 但**绝不能依赖 1000 一定被接受**：翻页循环用服务端实际返回的 <c>resultCount</c> 推进 index，
    /// 服务端按 50 截断也无所谓——那就多翻几页，结果一样是全集。这才是对上限不敏感的写法。
    /// </summary>
    private const int FilesPageSize = 1000;

    /// <summary>
    /// 翻页硬上限（页数）。接口异常时（比如 totalCount 报了一个天文数字，或 index 被忽略）
    /// 没有这个上限就会一直发请求，所以要有个兜底并记 Warn。
    /// </summary>
    private const int MaxFilesPages = 200;

    /// <summary>翻页硬上限（累计条数）。200 页 × 1000 条的量级，正常项目永远碰不到。</summary>
    private const long MaxFilesTotal = 100_000;

    /// <summary>
    /// 免配置的 cfwidget 项目详情接口。用于「粘贴项目链接」这条精确解析路径。
    /// 按 <c>/{section}/{slug}</c> 查单个项目，<b>没有搜索端点</b>（<c>/search?query=</c> 实测 404），
    /// 所以它只承担链接解析，搜索一律走带 key 的官方接口。
    /// </summary>
    public const string WidgetApiBase = "https://api.cfwidget.com";

    /// <summary>未配置之外的统一失败文案不再存在：key 内置，客户端恒可直接打官方接口。</summary>
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>cfwidget 一次返回项目的全部文件（jei 约 2.7 MB），超时给得比搜索宽一点。</summary>
    private static readonly TimeSpan WidgetTimeout = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>失败重试的间隔。只重试一次，避免在被限流时把请求堆起来。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>进度回调的最小间隔，避免小文件下载时把 UI 线程刷爆。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);

    private const int ProgressChunkBytes = 256 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 一页文件列表 + 它自带的分页信息。
    /// <paramref name="ResultCount"/> 取服务端 <c>pagination.resultCount</c>（缺省时退回解析出的条数），
    /// 翻页推进 index 必须用它；<paramref name="TotalCount"/> 是终止判据。
    /// <paramref name="HasPagination"/> 为 false 表示响应里没有 <c>pagination</c>，调用方应退回单页行为。
    /// </summary>
    internal readonly record struct CurseForgeFilePage(
        IReadOnlyList<CurseForgeFile> Files,
        int ResultCount,
        long TotalCount,
        bool HasPagination);

    private readonly IAppLog? _log;
    private readonly ILauncherPaths? _paths;
    private readonly ISettingsService? _settings;
    private readonly HttpClient _http;

    /// <summary>DI 用构造函数。设置缺失时视为「未配置」，日志与路径缺失时降级为不记日志 / 不缓存图标。</summary>
    public CurseForgeService(IAppLog? log = null, ILauncherPaths? paths = null, ISettingsService? settings = null)
        : this(log, paths, settings, handler: null)
    {
    }

    /// <summary>
    /// 测试与诊断用：注入自定义 <see cref="HttpMessageHandler"/> 以便在不联网的情况下覆盖失败分支。
    /// <paramref name="handler"/> 没有默认值，所以 DI 会跳过这个构造函数选上面那个。
    /// </summary>
    public CurseForgeService(
        IAppLog? log,
        ILauncherPaths? paths,
        ISettingsService? settings,
        HttpMessageHandler? handler)
    {
        _log = log;
        _paths = paths;
        _settings = settings;
        _http = CreateClient(handler);
    }

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <summary>当前生效的基地址，末尾斜杠统一剥掉，便于直接拼路径。</summary>
    private string ApiBase
    {
        get
        {
            var configured = _settings?.Current.CurseForgeBaseUrl?.Trim();

            return string.IsNullOrEmpty(configured) ? DefaultApiBase : configured.TrimEnd('/');
        }
    }

    // ---------------- 搜索 ----------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<CurseForgeProject>> SearchAsync(
        ContentKind kind,
        string query,
        string? gameVersion,
        LoaderKind loader,
        int index,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (kind == ContentKind.World)
        {
            LastError = "世界存档不支持在线搜索。";
            return Array.Empty<CurseForgeProject>();
        }

        // 粘贴项目链接时按 slug 精确解析：官方搜索会把整条 URL 当关键词，必然搜不到。
        // 纯关键词（含 slug 形态的 jei）仍走官方搜索——模糊匹配比精确命中更有用。
        if (IsProjectLink(query))
        {
            var project = await GetProjectBySlugAsync(kind, query, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            if (project is not null)
            {
                LastError = null;
                return new[] { project };
            }

            LastError ??= SlugHintMessage(query);
            return Array.Empty<CurseForgeProject>();
        }

        var url = BuildSearchUrl(kind, query, gameVersion, loader, index, pageSize);

        if (url.Length == 0)
        {
            // BuildSearchUrl 只在内容类型不支持（世界存档）时返回空，上面已经拦过，这里是兜底。
            LastError = "这类内容不支持在 CurseForge 上搜索。";
            return Array.Empty<CurseForgeProject>();
        }

        var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return Array.Empty<CurseForgeProject>();
        }

        try
        {
            return ParseSearch(json, kind);
        }
        catch (JsonException ex)
        {
            _log?.Error($"CurseForge 搜索结果解析失败：{url}", ex);
            LastError = "CurseForge 返回的内容无法解析，可能是接口有变化。";
            return Array.Empty<CurseForgeProject>();
        }
    }

    // ---------------- 整合包搜索 ----------------

    /// <inheritdoc />
    /// <remarks>
    /// 与 <see cref="SearchAsync"/> 同构：项目链接走 slug 精确解析，其余走官方搜索。
    ///
    /// 为什么不按游戏版本 / 加载器过滤：玩家的顺序是先找包、再决定包里用哪个构建，
    /// 同一个整包常常同时发布 Forge / Fabric / NeoForge 三套，提前过滤等于替用户做选择。
    /// </remarks>
    public async Task<IReadOnlyList<CurseForgeProject>> SearchModpacksAsync(
        string query,
        int index,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        // 粘贴整合包项目链接时按 slug 精确解析：官方搜索会把整条 URL 当关键词，必然搜不到。
        if (IsProjectLink(query))
        {
            var project = await GetModpackBySlugAsync(query, cancellationToken).ConfigureAwait(false);

            if (project is not null)
            {
                LastError = null;
                return new[] { project };
            }

            LastError ??= SlugHintMessage(query, ModpackExampleUrl);
            return Array.Empty<CurseForgeProject>();
        }

        var url = BuildModpackSearchUrl(query, index, pageSize);
        var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return Array.Empty<CurseForgeProject>();
        }

        try
        {
            // kind 只是展示字段，整合包条目靠 isModpack 表达自己不是普通模组。
            return ParseSearch(json, ContentKind.Mod, isModpack: true);
        }
        catch (JsonException ex)
        {
            _log?.Error($"CurseForge 整合包搜索结果解析失败：{url}", ex);
            LastError = "CurseForge 返回的内容无法解析，可能是接口有变化。";
            return Array.Empty<CurseForgeProject>();
        }
    }

    // ---------------- 项目链接精确解析 ----------------

    /// <inheritdoc />
    public async Task<CurseForgeProject?> GetProjectBySlugAsync(
        ContentKind kind,
        string slugOrUrl,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (kind == ContentKind.World)
        {
            LastError = "世界存档不支持在线查找。";
            return null;
        }

        var slug = CurseForgeSlugParser.Parse(slugOrUrl);

        if (slug is null)
        {
            LastError = SlugHintMessage(slugOrUrl);
            return null;
        }

        var url = BuildWidgetUrl(kind, slug);
        var (body, status) = await FetchAsync(url, WidgetTimeout, includeApiKey: false, cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            if (status == 0)
            {
                // 网络层失败，FetchAsync 已经写好 LastError。
                return null;
            }

            _log?.Warn($"CurseForge 项目详情接口失败：HTTP {status} {url}");

            LastError = status switch
            {
                404 => $"在 CurseForge 上未找到 slug 为「{slug}」的项目（HTTP 404）。请检查拼写，或到网页确认项目存在。",
                >= 500 => $"CurseForge 项目详情接口暂时不可用（HTTP {status}），请稍后再试。",
                _ => $"CurseForge 项目详情接口请求失败（HTTP {status}）。",
            };

            return null;
        }

        try
        {
            return ParseWidgetProject(body, kind, slug, gameVersion, loader);
        }
        catch (JsonException ex)
        {
            _log?.Error($"CurseForge 项目详情解析失败：{url}", ex);
            LastError = "CurseForge 项目详情接口返回的内容无法解析，可能是接口有变化。";
            return null;
        }
    }

    // ---------------- 整合包项目链接精确解析 ----------------

    /// <inheritdoc />
    public async Task<CurseForgeProject?> GetModpackBySlugAsync(
        string slugOrUrl,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var slug = CurseForgeSlugParser.Parse(slugOrUrl);

        if (slug is null)
        {
            LastError = SlugHintMessage(slugOrUrl, ModpackExampleUrl);
            return null;
        }

        // cfwidget 用的是网页路径里的段名（modpacks），不是官方 API 的 classId。
        var url = BuildWidgetUrl(ContentKind.Mod, slug, isModpack: true);
        var (body, status) = await FetchAsync(url, WidgetTimeout, includeApiKey: false, cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            if (status == 0)
            {
                // 网络层失败，FetchAsync 已经写好 LastError。
                return null;
            }

            _log?.Warn($"CurseForge 整合包详情接口失败：HTTP {status} {url}");

            LastError = status switch
            {
                404 => $"在 CurseForge 上未找到 slug 为「{slug}」的整合包（HTTP 404）。请检查拼写，或到网页确认项目存在。",
                >= 500 => $"CurseForge 项目详情接口暂时不可用（HTTP {status}），请稍后再试。",
                _ => $"CurseForge 项目详情接口请求失败（HTTP {status}）。",
            };

            return null;
        }

        try
        {
            // 版本与加载器显式传「不过滤」：整合包文件列表是各个游戏版本 / 加载器组合的整包，
            // 由玩家自己挑，不能在解析层裁掉。
            return ParseWidgetProject(
                body,
                ContentKind.Mod,
                slug,
                gameVersion: null,
                loader: LoaderKind.Vanilla,
                isModpack: true);
        }
        catch (JsonException ex)
        {
            _log?.Error($"CurseForge 整合包详情解析失败：{url}", ex);
            LastError = "CurseForge 项目详情接口返回的内容无法解析，可能是接口有变化。";
            return null;
        }
    }

    // ---------------- 文件列表 ----------------

    /// <inheritdoc />
    public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
        long modId,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default) =>
        GetFilePagesAsync(modId, gameVersion, loader, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// 走同一个翻页实现，只是把 <c>gameVersion</c> 传 null、<c>loader</c> 传 <see cref="LoaderKind.Vanilla"/>，
    /// 于是 URL 里不出现 <c>gameVersion</c> / <c>modLoaderType</c>：整合包要的是全集，
    /// 玩家自己在文件列表里挑游戏版本与加载器组合。
    /// </remarks>
    public Task<IReadOnlyList<CurseForgeFile>> GetModpackFilesAsync(
        long modId,
        CancellationToken cancellationToken = default) =>
        GetFilePagesAsync(modId, null, LoaderKind.Vanilla, cancellationToken);

    /// <summary>
    /// 翻页取全一个项目的文件列表。<see cref="GetFilesAsync"/>（按版本 / 加载器过滤）与
    /// <see cref="GetModpackFilesAsync"/>（不过滤）共用这一份循环，避免两处各写一遍终止判据
    /// 与去重逻辑后走偏。
    /// </summary>
    private async Task<IReadOnlyList<CurseForgeFile>> GetFilePagesAsync(
        long modId,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        LastError = null;

        if (modId <= 0)
        {
            LastError = "缺少项目 ID，无法查询文件。";
            return Array.Empty<CurseForgeFile>();
        }

        // 翻页取全。
        //
        // 旧实现只发一次 index=0&pageSize=50，于是 JEI（238222）这类文件数远超 50 的项目，
        // 历史版本在列表里根本不存在——用户想装「1.20.1 的某个旧版本」时无版本可选。
        //
        // 终止判据用 index + resultCount >= totalCount，而不是「本页拿到几条就停」：
        // 服务端可能按自己的上限截断（文档说 50，实际可能更大），只要它还老实报 pagination，
        // 这个判据在 50 和 1000 两种上限下都收敛到全集。
        var collected = new List<CurseForgeFile>();
        var seen = new HashSet<long>();
        var index = 0;
        var pages = 0;
        var hitLimit = false;

        while (true)
        {
            if (pages >= MaxFilesPages)
            {
                hitLimit = true;
                break;
            }

            var url = BuildFilesUrl(modId, gameVersion, loader, index, FilesPageSize);
            pages++;

            var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

            if (json is null)
            {
                // 第一页就失败：保持旧行为（LastError 已由 GetStringAsync 写好，返回空）。
                if (pages == 1)
                {
                    return Array.Empty<CurseForgeFile>();
                }

                // 后续页失败：已经取到的比什么都没有有用，把残缺结果交出去并记 Warn。
                _log?.Warn($"CurseForge 文件列表第 {pages} 页请求失败，返回已取到的 {collected.Count} 条：{url}");
                break;
            }

            CurseForgeFilePage page;

            try
            {
                page = ParseFilePage(json);
            }
            catch (JsonException ex)
            {
                _log?.Error($"CurseForge 文件列表解析失败：{url}", ex);

                if (pages == 1)
                {
                    LastError = "CurseForge 返回的内容无法解析，可能是接口有变化。";
                    return Array.Empty<CurseForgeFile>();
                }

                _log?.Warn($"CurseForge 文件列表第 {pages} 页解析失败，返回已取到的 {collected.Count} 条");
                break;
            }

            var added = AppendNewFiles(page.Files, collected, seen);

            // 分页信息缺失 / totalCount 无效：不知道还有没有下一页，退回单页行为（不抛异常）。
            if (!page.HasPagination || page.TotalCount <= 0)
            {
                _log?.Warn(
                    $"CurseForge 文件列表缺少有效分页信息（totalCount={page.TotalCount}），按单页结果返回 {collected.Count} 条：{url}");
                break;
            }

            if (page.ResultCount <= 0)
            {
                _log?.Warn($"CurseForge 文件列表第 {pages} 页返回 0 条（totalCount={page.TotalCount}），停止翻页：{url}");
                break;
            }

            // 推进量用服务端给的 resultCount，不用请求的 FilesPageSize：
            // 服务端按 50 截断时实际只前进了 50，按 1000 推进就会漏掉中间的条目。
            index += page.ResultCount;

            if (index >= page.TotalCount)
            {
                break;
            }

            // 整页都是见过的 Id：接口忽略了 index（每次都返回同一批）。继续翻只会把这批重复 200 次。
            if (added == 0)
            {
                _log?.Warn(
                    $"CurseForge 文件列表第 {pages} 页没有任何新文件（index 已到 {index}，totalCount={page.TotalCount}），"
                    + "接口可能忽略了 index 参数，停止翻页");
                break;
            }

            if (collected.Count >= MaxFilesTotal)
            {
                hitLimit = true;
                break;
            }
        }

        if (hitLimit)
        {
            _log?.Warn(
                $"CurseForge 文件列表达到翻页上限（{pages} 页 / {collected.Count} 条），结果可能不完整：modId={modId}");
        }

        // 各页分别有序不代表拼接后有序（分页期间有并发上传时尤其明显），这里统一重排一次。
        return collected
            .OrderByDescending(file => file.FileDate ?? DateTimeOffset.MinValue)
            .ToList();
    }

    // ---------------- 安装 ----------------

    // ---------------- 指纹反查 ----------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
        IReadOnlyList<uint> fingerprints,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (fingerprints is null || fingerprints.Count == 0)
        {
            return Array.Empty<CurseForgeFingerprintMatch>();
        }

        var url = $"{ApiBase}/v1/fingerprints";

        // 指纹按 uint32 原样发给服务端：CurseForge 这个接口只收无符号值，
        // 把高位为 1 的哈希按 int32 补码写成负数会被判 400（实测：
        // 「The JSON value could not be converted to System.UInt32. Path: $.fingerprints[0]」）。
        // 响应里的 fileFingerprint 仍是 int32 形态，解析侧的补码还原不能删（见 ParseFileFingerprint）。
        var payload = JsonSerializer.Serialize(
            new FingerprintQueryRequest(fingerprints.ToArray()),
            CurseForgeRequestJsonContext.Default.FingerprintQueryRequest);

        var (body, status) = await PostJsonAsync(url, payload, SearchTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            if (status != 0)
            {
                _log?.Warn($"CurseForge 指纹反查失败：HTTP {status} {url}");
                LastError = status switch
                {
                    401 or 403 => $"CurseForge 拒绝了请求（HTTP {status}）：内置 API Key 无效、已过期或缺少权限。",
                    404 => "CurseForge 没有提供指纹反查接口（HTTP 404），可能是镜像地址不支持。",
                    429 => "CurseForge 请求过于频繁（HTTP 429），请稍后再试。",
                    >= 500 => $"CurseForge 服务暂时不可用（HTTP {status}），请稍后再试。",
                    _ => $"CurseForge 指纹反查失败（HTTP {status}）。",
                };
            }

            return Array.Empty<CurseForgeFingerprintMatch>();
        }

        try
        {
            var matches = ParseFingerprintMatches(body);
            return AttachRequestFingerprints(matches, fingerprints);
        }
        catch (JsonException ex)
        {
            _log?.Error("CurseForge 指纹反查结果解析失败", ex);
            LastError = "CurseForge 返回的内容无法解析，可能是接口有变化。";
            return Array.Empty<CurseForgeFingerprintMatch>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CurseForgeProject>> GetProjectsAsync(
        IReadOnlyList<long> modIds,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var ids = modIds.Where(id => id > 0).Distinct().ToList();

        if (ids.Count == 0)
        {
            return Array.Empty<CurseForgeProject>();
        }

        var url = $"{ApiBase}/v1/mods";
        var payload = JsonSerializer.Serialize(
            new ModIdsQueryRequest(ids),
            CurseForgeRequestJsonContext.Default.ModIdsQueryRequest);
        var (body, status) = await PostJsonAsync(url, payload, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            if (status != 0)
            {
                _log?.Warn($"CurseForge 批量项目查询失败：HTTP {status} {url}");
                LastError = status switch
                {
                    401 or 403 => $"CurseForge 拒绝了请求（HTTP {status}）：内置 API Key 无效、已过期或缺少权限。",
                    404 => "CurseForge 没有提供批量项目接口（HTTP 404），可能是镜像地址不支持。",
                    429 => "CurseForge 请求过于频繁（HTTP 429），请稍后再试。",
                    >= 500 => $"CurseForge 服务暂时不可用（HTTP {status}），请稍后再试。",
                    _ => $"CurseForge 批量项目查询失败（HTTP {status}）。",
                };
            }

            return Array.Empty<CurseForgeProject>();
        }

        try
        {
            // 批量接口的项目对象与搜索命中同构，复用同一个解析器；
            // kind 只影响 CurseForgeProject.Kind 这个展示字段，补全场景下必为模组。
            return ParseSearch(body, ContentKind.Mod);
        }
        catch (JsonException ex)
        {
            _log?.Error("CurseForge 批量项目查询结果解析失败", ex);
            LastError = "CurseForge 返回的内容无法解析，可能是接口有变化。";
            return Array.Empty<CurseForgeProject>();
        }
    }

    /// <summary>
    /// 响应里的 <c>file.fileFingerprint</c> 缺失时，把请求指纹按顺序补回每条匹配。
    ///
    /// 为什么需要：关联必须按指纹做（文件名会被用户改掉，同名不同文件也会串）。
    /// 官方响应在 <c>exactMatches[].file</c> 里带 <c>fileFingerprint</c>，这是主路径；
    /// 只有整批都缺指纹（旧格式 / 精简镜像）时才退化成「请求顺序 = 命中顺序」。
    /// 数量对不上就无法建立一一对应，此时保持无指纹，由调用方决定是否按文件名兜底。
    /// </summary>
    private static IReadOnlyList<CurseForgeFingerprintMatch> AttachRequestFingerprints(
        IReadOnlyList<CurseForgeFingerprintMatch> matches,
        IReadOnlyList<uint> fingerprints)
    {
        if (matches.Count == 0 || matches.Count != fingerprints.Count)
        {
            return matches;
        }

        foreach (var match in matches)
        {
            if (match.Fingerprint != 0)
            {
                return matches;
            }
        }

        var attached = new List<CurseForgeFingerprintMatch>(matches.Count);

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];

            attached.Add(new CurseForgeFingerprintMatch
            {
                FileId = match.FileId,
                ModId = match.ModId,
                FileName = match.FileName,
                LatestFiles = match.LatestFiles,
                Fingerprint = fingerprints[index],
            });
        }

        return attached;
    }

    /// <summary>解析 <c>POST /v1/fingerprints</c> 的 <c>data.exactMatches[]</c>。</summary>
    public static IReadOnlyList<CurseForgeFingerprintMatch> ParseFingerprintMatches(string json)
    {
        var results = new List<CurseForgeFingerprintMatch>();

        using var document = JsonDocument.Parse(json, JsonOptions);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("exactMatches", out var matches) ||
            matches.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in matches.EnumerateArray())
        {
            if (!item.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var parsed = ParseFileElement(file);

            if (parsed is null)
            {
                continue;
            }

            var latest = new List<CurseForgeFile>();

            if (item.TryGetProperty("latestFiles", out var latestFiles) &&
                latestFiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in latestFiles.EnumerateArray())
                {
                    var latestFile = ParseFileElement(candidate);

                    if (latestFile is not null)
                    {
                        latest.Add(latestFile);
                    }
                }
            }

            results.Add(new CurseForgeFingerprintMatch
            {
                FileId = GetInt64(item, "id") is var fileId and > 0 ? fileId : parsed.Id,
                ModId = parsed.ModId,
                FileName = parsed.FileName,
                LatestFiles = latest,
                Fingerprint = ParseFileFingerprint(file),
            });
        }

        return results;
    }

    /// <summary>
    /// 从 <c>file.fileFingerprint</c> 取 32 位指纹。CurseForge 按 int32 传输：
    /// 无符号值的高位为 1 时响应里是负数（例如 <c>-1506700914</c> 即 <c>2788266382</c>），
    /// 必须按补码还原，否则与本地 <see cref="ModFileHashes.CurseForgeFingerprint"/> 永远对不上。
    /// 缺失 / 类型不符 / 越界时返回 0，表示「这条响应没带指纹」。
    /// </summary>
    private static uint ParseFileFingerprint(JsonElement file)
    {
        if (!file.TryGetProperty("fileFingerprint", out var node) || node.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (!node.TryGetInt64(out var value))
        {
            return 0;
        }

        return value switch
        {
            < int.MinValue => 0,
            < 0 => unchecked((uint)(int)value),
            > uint.MaxValue => 0,
            _ => (uint)value,
        };
    }

    // ---------------- 安装 ----------------

    /// <inheritdoc />
    public async Task<ContentInstallResult> InstallAsync(
        CurseForgeFile file,
        string gameDirectory,
        ContentKind kind,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (file is null)
        {
            return ContentInstallResult.Fail("没有选择要安装的文件。");
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return ContentInstallResult.Fail("游戏目录为空，请先在设置页指定 .minecraft 路径。");
        }

        if (kind == ContentKind.World)
        {
            return ContentInstallResult.Fail("世界存档不支持在线安装。");
        }

        if (!file.IsDownloadable)
        {
            // 作者关闭第三方分发时 downloadUrl 为 null。这不是错误，是 CurseForge 的分发策略。
            LastError = "这个文件没有第三方下载直链（作者关闭了 CurseForge 第三方分发），请到项目网页手动下载。";
            return ContentInstallResult.Fail(LastError);
        }

        // 文件名清理与同名改名复用 Modrinth 的实现：两个数据源落在同一个 mods 目录，
        // 冲突命名规则必须一致，否则用户会看到两套规则。
        var fileName = ModrinthService.SanitizeFileName(file.FileName);

        if (fileName.Length == 0)
        {
            return ContentInstallResult.Fail("服务端给出的文件名不合法，已取消安装。");
        }

        var targetDirectory = Path.Combine(gameDirectory, ContentService.DirectoryNameFor(kind));

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            _log?.Error($"创建内容目录失败：{targetDirectory}", ex);
            return ContentInstallResult.Fail($"无法创建目录：{targetDirectory}");
        }

        var targetPath = ModrinthService.ResolveTargetPath(targetDirectory, fileName);
        var renamed = !string.Equals(Path.GetFileName(targetPath), fileName, StringComparison.Ordinal);
        var partialPath = targetPath + ".part";
        var downloadUrl = file.DownloadUrl!;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallTimeout);
        var token = timeout.Token;

        try
        {
            // cfwidget 路径的直链是按 fileId 推导的，mediafilez 与 edge 两个域名可用性不一致，
            // 主域名 403/404 时自动换备用域名再试一次。
            var (downloadResponse, downloadStatus) = await OpenDownloadAsync(downloadUrl, token).ConfigureAwait(false);

            if (downloadResponse is null)
            {
                var message = DownloadFailureMessage(downloadStatus);
                _log?.Warn($"CurseForge 下载失败：HTTP {downloadStatus} {downloadUrl}");
                LastError = message;
                return ContentInstallResult.Fail(message);
            }

            using var response = downloadResponse;

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _log?.Warn($"CurseForge 下载失败：HTTP {status} {downloadUrl}");
                var message = DownloadFailureMessage(status);
                LastError = message;
                return ContentInstallResult.Fail(message);
            }

            var totalBytes = response.Content.Headers.ContentLength ?? file.FileLength;
            long received = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long lastReportBytes = 0;

            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             partialPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    sha1.AppendData(buffer, 0, read);
                    received += read;

                    var elapsed = stopwatch.Elapsed;

                    // 小文件也会走完整个循环，所以节流同时看字节数与时间。
                    if (received - lastReportBytes >= ProgressChunkBytes || elapsed - lastReport >= ProgressInterval)
                    {
                        Report(progress, totalBytes, received, elapsed, fileName);
                        lastReport = elapsed;
                        lastReportBytes = received;
                    }
                }
            }

            var actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(file.Sha1) &&
                !string.Equals(actualSha1, file.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partialPath);
                _log?.Error($"SHA1 校验失败：{fileName} 期望 {file.Sha1}，实际 {actualSha1}");
                LastError = "下载文件的 SHA1 校验失败，文件已丢弃。可能是网络劫持或代理返回了错误内容。";
                return ContentInstallResult.Fail(LastError);
            }

            // 下载期间目标路径可能被别的安装占用（连点两次），落盘前再确认一次。
            if (File.Exists(targetPath))
            {
                targetPath = ModrinthService.ResolveTargetPath(targetDirectory, fileName);
                renamed = !string.Equals(Path.GetFileName(targetPath), fileName, StringComparison.Ordinal);
            }

            File.Move(partialPath, targetPath, overwrite: false);

            progress?.Report(new DownloadProgress(1, 1, 0, received, received, 0, fileName, 1));
            _log?.Info($"已安装 {fileName} → {targetPath}");

            return ContentInstallResult.Ok(targetPath, received, renamed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partialPath);
            _log?.Info($"已取消安装：{fileName}");
            LastError = "已取消。";
            return ContentInstallResult.Fail(LastError);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            _log?.Warn($"CurseForge 下载超时：{fileName}");
            LastError = $"下载超时（{(int)InstallTimeout.TotalMinutes} 分钟），请重试。";
            return ContentInstallResult.Fail(LastError);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partialPath);
            _log?.Error($"CurseForge 下载连接失败：{fileName}", ex);
            LastError = "无法连接 CurseForge 下载服务器，请检查网络或代理设置。";
            return ContentInstallResult.Fail(LastError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            _log?.Error($"写入模组文件失败：{targetPath}", ex);
            LastError = $"写入失败：{ex.Message}";
            return ContentInstallResult.Fail(LastError);
        }
    }

    // ---------------- 图标缓存 ----------------

    /// <inheritdoc />
    public async Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || _paths is null)
        {
            return null;
        }

        string cacheDirectory;
        string targetPath;

        try
        {
            // 文件名取 URL 的内容哈希：同一个图标只会存一份，且不依赖 URL 里的字符。
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()[..20];
            cacheDirectory = Path.Combine(_paths.CacheDirectory, "curseforge-icons");
            targetPath = Path.Combine(cacheDirectory, hash + ExtensionOf(url));

            if (File.Exists(targetPath))
            {
                return targetPath;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("准备图标缓存路径失败", ex);
            return null;
        }

        var partialPath = targetPath + ".part";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(IconTimeout);

            // 图标来自 forgecdn，不带 x-api-key：把 key 发给 CDN 只会扩大泄漏面。
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn($"图标下载失败：HTTP {(int)response.StatusCode} {url}");
                return null;
            }

            Directory.CreateDirectory(cacheDirectory);

            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await source.CopyToAsync(destination, timeout.Token).ConfigureAwait(false);
            }

            File.Move(partialPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            // 图标只是展示增强，失败一律降级为首字母占位，不设 LastError。
            TryDelete(partialPath);
            _log?.Debug($"图标缓存跳过：{url}（{ex.GetType().Name}）");
            return null;
        }
    }

    // ---------------- 请求 ----------------

    /// <summary>
    /// 发 GET 并读成字符串（官方 API）。失败返回 null 并写好 <see cref="LastError"/>。
    /// 429 与 5xx 重试一次（间隔 <see cref="RetryDelay"/>），其余状态码直接失败。
    /// </summary>
    private async Task<string?> GetStringAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (body, status) = await FetchAsync(url, timeout, includeApiKey: true, cancellationToken).ConfigureAwait(false);

        if (body is not null)
        {
            return body;
        }

        if (status == 0)
        {
            // 网络层失败，FetchAsync 已经写好 LastError。
            return null;
        }

        _log?.Warn($"CurseForge 请求失败：HTTP {status} {url}");
        LastError = status switch
        {
            401 or 403 => $"CurseForge 拒绝了请求（HTTP {status}）：内置 API Key 无效、已过期或缺少权限。",
            404 => "CurseForge 上找不到这个项目（HTTP 404）。",
            429 => "CurseForge 请求过于频繁（HTTP 429），请稍后再试。",
            >= 500 => $"CurseForge 服务暂时不可用（HTTP {status}），请稍后再试。",
            _ => $"CurseForge 请求失败（HTTP {status}）。",
        };
        return null;
    }

    /// <summary>
    /// 发 GET 并读成字符串。429 与 5xx 重试一次（间隔 <see cref="RetryDelay"/>）。
    /// 网络层失败（超时 / 连接失败）由这里写 <see cref="LastError"/> 并返回 Status = 0；
    /// HTTP 状态码的文案交给调用方，因为官方 API 与 cfwidget 的措辞不同。
    /// <paramref name="includeApiKey"/> 为 false 时不带 <c>x-api-key</c>（cfwidget 与 CDN 都不该拿到 key）。
    /// </summary>
    private async Task<(string? Body, int Status)> FetchAsync(
        string url,
        TimeSpan timeout,
        bool includeApiKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout);

            try
            {
                using var request = CreateRequest(url, includeApiKey);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                    return (body, (int)response.StatusCode);
                }

                var status = (int)response.StatusCode;
                var retryable = status == 429 || status >= 500;

                if (retryable && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? RetryDelay;
                    _log?.Warn($"CurseForge 返回 HTTP {status}，稍后重试一次：{url}");
                    await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return (null, status);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 自身超时：不是用户取消，按可重试处理一次。
                _log?.Warn($"CurseForge 请求超时（{(int)timeout.TotalSeconds} 秒）：{url}");

                if (attempt == 0)
                {
                    await DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LastError = $"连接 CurseForge 超时（{(int)timeout.TotalSeconds} 秒），请检查网络后重试。";
                return (null, 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _log?.Warn($"CurseForge 网络请求失败：{url}", ex);

                if (attempt == 0)
                {
                    await DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LastError = "无法连接 CurseForge，请检查网络或代理设置。";
                return (null, 0);
            }
        }
    }

    /// <summary>
    /// 发 POST JSON 并读成字符串。失败语义与 <see cref="FetchAsync"/> 一致：
    /// 429 / 5xx 重试一次，网络层失败返回 Status = 0 并写好 <see cref="LastError"/>。
    /// 只有指纹反查一个端点用 POST，因此不为它做通用化。
    /// </summary>
    private async Task<(string? Body, int Status)> PostJsonAsync(
        string url,
        string json,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout);

            try
            {
                using var request = CreateRequest(url, includeApiKey: true);
                request.Method = HttpMethod.Post;
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                    return (body, (int)response.StatusCode);
                }

                var status = (int)response.StatusCode;
                var retryable = status == 429 || status >= 500;

                if (retryable && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? RetryDelay;
                    _log?.Warn($"CurseForge 返回 HTTP {status}，稍后重试一次：{url}");
                    await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return (null, status);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log?.Warn($"CurseForge 请求超时（{(int)timeout.TotalSeconds} 秒）：{url}");

                if (attempt == 0)
                {
                    await DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LastError = $"连接 CurseForge 超时（{(int)timeout.TotalSeconds} 秒），请检查网络后重试。";
                return (null, 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _log?.Warn($"CurseForge 网络请求失败：{url}", ex);

                if (attempt == 0)
                {
                    await DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LastError = "无法连接 CurseForge，请检查网络或代理设置。";
                return (null, 0);
            }
        }
    }

    /// <summary>构造请求：带上 JSON 的 Accept；只有官方 API 才带 <c>x-api-key</c>。</summary>
    private static HttpRequestMessage CreateRequest(string url, bool includeApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!includeApiKey)
        {
            return request;
        }

        // key 含 `$` / `.` / `/`，严格校验会抛异常；服务端只按字符串匹配。
        _ = request.Headers.TryAddWithoutValidation("x-api-key", EmbeddedApiKey);

        return request;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // 服务端可能给出很大的 Retry-After，夹到 3 秒内，避免页面卡住。
        var clamped = delay > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        await Task.Delay(clamped, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开下载流。cfwidget 路径的直链是按 fileId 推导的，<c>mediafilez</c> 与 <c>edge</c> 两个域名
    /// 的可用性不一致，所以主域名 403 / 404 时换备用域名再试一次。
    /// 返回的 Response 为 null 表示所有候选都没拿到响应；Status 是最后一次的状态码
    /// （0 表示网络层失败）。
    /// </summary>
    private async Task<(HttpResponseMessage? Response, int Status)> OpenDownloadAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var candidates = CurseForgeCdn.Candidates(url);
        var lastStatus = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            var isLast = index == candidates.Count - 1;

            HttpResponseMessage response;

            try
            {
                response = await _http
                    .GetAsync(candidates[index], HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _log?.Warn($"CurseForge 下载请求失败：{candidates[index]}", ex);

                if (isLast)
                {
                    throw;
                }

                continue;
            }

            if (response.IsSuccessStatusCode || isLast)
            {
                return (response, (int)response.StatusCode);
            }

            lastStatus = (int)response.StatusCode;
            response.Dispose();

            // 只有 403 / 404 值得换域名；429 / 5xx 换域名也一样。
            if (lastStatus is not (403 or 404))
            {
                return (null, lastStatus);
            }
        }

        return (null, lastStatus);
    }

    private static string DownloadFailureMessage(int status) => status switch
    {
        404 => "下载地址已失效（HTTP 404），请换一个文件。",
        403 => "下载被拒绝（HTTP 403），可能需要在浏览器里确认。",
        429 => "CurseForge 限流中（HTTP 429），请稍后再试。",
        0 => "无法连接 CurseForge 下载服务器，请检查网络或代理设置。",
        _ => $"下载失败（HTTP {status}）。",
    };

    // ---------------- URL ----------------

    /// <inheritdoc />
    public string BuildSearchUrl(
        ContentKind kind,
        string? query,
        string? gameVersion,
        LoaderKind loader,
        int index,
        int pageSize)
    {
        var classId = ClassIdOf(kind);

        if (classId is null)
        {
            return string.Empty;
        }

        // 参数顺序固定，测试按字符串断言，不要随手重排。
        var parameters = new List<string>(9)
        {
            "gameId=" + GameIdMinecraft.ToString(CultureInfo.InvariantCulture),
            "classId=" + classId.Value.ToString(CultureInfo.InvariantCulture),
        };

        var version = gameVersion?.Trim();

        if (!string.IsNullOrEmpty(version))
        {
            parameters.Add("gameVersion=" + Uri.EscapeDataString(version));
        }

        // API 要求 modLoaderType 与 gameVersion 成对出现（单传会被拒）；资源包 / 光影没有加载器概念，
        // 只在 Mod 上追加，否则会把结果错误清空。
        var loaderType = kind == ContentKind.Mod ? ModLoaderTypeOf(loader) : null;

        if (loaderType is not null && !string.IsNullOrEmpty(version))
        {
            parameters.Add("modLoaderType=" + loaderType.Value.ToString(CultureInfo.InvariantCulture));
        }

        var text = (query ?? string.Empty).Trim();

        if (text.Length > 0)
        {
            parameters.Add("searchFilter=" + Uri.EscapeDataString(text));
        }

        parameters.Add("index=" + Math.Max(0, index).ToString(CultureInfo.InvariantCulture));
        parameters.Add("pageSize=" + Math.Clamp(pageSize, 1, MaxPageSize).ToString(CultureInfo.InvariantCulture));
        parameters.Add("sortField=2");
        parameters.Add("sortOrder=desc");

        return $"{ApiBase}/v1/mods/search?{string.Join("&", parameters)}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// 参数顺序固定 <c>gameId → classId → searchFilter → index → pageSize → sortField → sortOrder</c>，
    /// 测试按字符串断言，不要随手重排。
    ///
    /// 刻意不拼 <c>gameVersion</c> / <c>modLoaderType</c>：整合包是「先找包、再在包里挑构建」，
    /// 按版本 / 加载器筛会把其实能玩的包从结果里删掉。
    /// </remarks>
    public string BuildModpackSearchUrl(string? query, int index, int pageSize)
    {
        var parameters = new List<string>(7)
        {
            "gameId=" + GameIdMinecraft.ToString(CultureInfo.InvariantCulture),
            "classId=" + ClassIdModpacks.ToString(CultureInfo.InvariantCulture),
        };

        var text = (query ?? string.Empty).Trim();

        if (text.Length > 0)
        {
            parameters.Add("searchFilter=" + Uri.EscapeDataString(text));
        }

        parameters.Add("index=" + Math.Max(0, index).ToString(CultureInfo.InvariantCulture));
        parameters.Add("pageSize=" + Math.Clamp(pageSize, 1, MaxPageSize).ToString(CultureInfo.InvariantCulture));
        parameters.Add("sortField=2");
        parameters.Add("sortOrder=desc");

        return $"{ApiBase}/v1/mods/search?{string.Join("&", parameters)}";
    }

    /// <summary>拼文件列表 URL。<paramref name="gameVersion"/> 为空时不传 modLoaderType（API 要求成对）。</summary>
    private string BuildFilesUrl(long modId, string? gameVersion, LoaderKind loader, int index, int pageSize)
    {
        var parameters = new List<string>(4);
        var version = gameVersion?.Trim();

        if (!string.IsNullOrEmpty(version))
        {
            parameters.Add("gameVersion=" + Uri.EscapeDataString(version));
        }

        var loaderType = ModLoaderTypeOf(loader);

        if (loaderType is not null && !string.IsNullOrEmpty(version))
        {
            parameters.Add("modLoaderType=" + loaderType.Value.ToString(CultureInfo.InvariantCulture));
        }

        // 参数顺序固定：现有测试按字符串断言 URL，不要随手重排。
        parameters.Add("index=" + Math.Max(0, index).ToString(CultureInfo.InvariantCulture));
        parameters.Add("pageSize=" + Math.Clamp(pageSize, 1, 1000).ToString(CultureInfo.InvariantCulture));

        return $"{ApiBase}/v1/mods/{modId.ToString(CultureInfo.InvariantCulture)}/files?{string.Join("&", parameters)}";
    }

    /// <summary>
    /// 拼 cfwidget 项目详情 URL。<c>?version=</c> 实测不会让 cfwidget 裁剪响应
    /// （jei 始终返回 2.7 MB），所以版本过滤一律在本地做，不传该参数。
    /// <paramref name="isModpack"/> 为 true 时段名是 <c>modpacks</c>；它覆盖
    /// <paramref name="kind"/> 的映射（整合包没有对应的 <see cref="ContentKind"/> 值），
    /// 其余三种内容类型的映射保持不变。
    /// </summary>
    private static string BuildWidgetUrl(ContentKind kind, string slug, bool isModpack = false)
    {
        var section = isModpack
            ? "modpacks"
            : kind switch
            {
                ContentKind.ResourcePack => "texture-packs",
                ContentKind.ShaderPack => "shaders",
                _ => "mc-mods",
            };

        return $"{WidgetApiBase}/minecraft/{section}/{Uri.EscapeDataString(slug)}";
    }

    /// <summary>输入是否是一个 CurseForge 项目链接（而不是关键词）。只有链接才走 cfwidget 精确解析。</summary>
    private static bool IsProjectLink(string? query)
    {
        var text = query?.Trim();

        if (string.IsNullOrEmpty(text) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return uri.Host.Equals("curseforge.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>整合包示例链接。提示文案里给整合包用户看的例子不能用模组的。</summary>
    private const string ModpackExampleUrl = "https://www.curseforge.com/minecraft/modpacks/better-mc-forge-bmc4";

    /// <summary>模组示例链接（默认的提示文案示例）。</summary>
    private const string ModExampleUrl = "https://www.curseforge.com/minecraft/mc-mods/jei";

    /// <summary>输入是链接但解析不出 slug 时给用户看的提示。</summary>
    /// <param name="input">用户输入。</param>
    /// <param name="exampleUrl">示例项目页地址；整合包入口传整合包示例，缺省给模组示例。</param>
    private static string SlugHintMessage(string? input, string? exampleUrl = null)
    {
        var example = exampleUrl ?? ModExampleUrl;

        return string.IsNullOrWhiteSpace(input)
            ? $"没有输入要查找的项目。可以直接粘贴 CurseForge 项目链接，例如 jei 或 {example} 。"
            : $"「{input.Trim()}」不是有效的 CurseForge 项目链接。请直接粘贴项目页地址，例如 {example} 。";
    }

    /// <summary>
    /// 内容类型 → CurseForge 的 classId。世界存档没有对应的 classId，返回 null 表示不支持。
    /// </summary>
    public static int? ClassIdOf(ContentKind kind) => kind switch
    {
        ContentKind.Mod => ClassIdMods,
        ContentKind.ResourcePack => ClassIdResourcePacks,
        ContentKind.ShaderPack => ClassIdShaders,
        _ => null,
    };

    /// <summary>
    /// 加载器 → CurseForge 的 modLoaderType。Vanilla 表示不过滤（等价于 0 = Any），返回 null。
    /// 2=Cauldron、3=LiteLoader 本项目没有对应 <see cref="LoaderKind"/>，不映射。
    /// </summary>
    public static int? ModLoaderTypeOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.Quilt => 5,
        LoaderKind.NeoForge => 6,
        _ => null,
    };

    // ---------------- JSON 解析 ----------------

    /// <summary>解析 <c>GET /v1/mods/search</c> 的响应。<paramref name="kind"/> 只用于回填内容类型。</summary>
    /// <param name="json">响应正文。</param>
    /// <param name="kind">内容类型（整合包入口传 <see cref="ContentKind.Mod"/>）。</param>
    /// <param name="isModpack">
    /// 整合包入口传 true，回填 <see cref="CurseForgeProject.IsModpack"/>。不能靠响应里的
    /// <c>classId</c> 判断：命中行只带 <c>categories[].classId</c>，项目自身的 classId 并不总在。
    /// </param>
    public static IReadOnlyList<CurseForgeProject> ParseSearch(string json, ContentKind kind, bool isModpack = false)
    {
        var results = new List<CurseForgeProject>();

        using var document = JsonDocument.Parse(json, JsonOptions);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var hit in data.EnumerateArray())
        {
            var id = GetInt64(hit, "id");
            var slug = GetString(hit, "slug");
            var name = GetString(hit, "name");

            if (id <= 0 || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            // 版本 / 加载器集合喂给跨站去重（CrossSourceSearch.IsSameProject），不参与展示。
            var (versions, loaders) = CollectProjectTags(hit);

            results.Add(new CurseForgeProject
            {
                Id = id,
                Slug = slug,
                Name = name,
                Summary = GetString(hit, "summary") ?? string.Empty,
                Authors = JoinAuthors(hit),
                DownloadCount = GetInt64(hit, "downloadCount"),
                IconUrl = GetLogoUrl(hit),
                DateModified = ParseTimestamp(hit, "dateModified"),
                GameVersions = versions,
                Loaders = loaders,
                Kind = kind,
                IsModpack = isModpack,
                AllowModDistribution = GetBoolean(hit, "allowModDistribution", fallback: true),
            });
        }

        return results;
    }

    /// <summary>
    /// 从搜索命中里聚合该项目的 MC 版本与加载器集合（去重，保持出现顺序）。
    ///
    /// 与 PCL2 <c>ResourceProject.vb:179-196</c> 同构：同时遍历 <c>latestFiles[].gameVersions</c>
    /// 与 <c>latestFilesIndexes[]</c>（两个数组包含的文件并不相同，PCL2 #3599），
    /// 把标准 MC 版本收进版本集合、把 Forge / NeoForge / Fabric / Quilt 收进加载器集合。
    ///
    /// 为什么两处都要读：<c>latestFiles</c> 只带最近若干文件，只读它会让 CF 侧版本集合比
    /// Modrinth 的项目全集小；跨站去重按版本集合判断时，同一项目就会被判成不同（双源出现重复条目）。
    /// </summary>
    private static (List<string> Versions, List<string> Loaders) CollectProjectTags(JsonElement hit)
    {
        var versions = new List<string>();
        var loaders = new List<string>();

        if (hit.TryGetProperty("latestFiles", out var latestFiles) && latestFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in latestFiles.EnumerateArray())
            {
                foreach (var tag in GetStringArray(file, "gameVersions"))
                {
                    CollectTag(tag, versions, loaders);
                }
            }
        }

        if (hit.TryGetProperty("latestFilesIndexes", out var indexes) && indexes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in indexes.EnumerateArray())
            {
                var gameVersion = GetString(item, "gameVersion");

                if (CrossSourceSearch.IsStandardGameVersion(gameVersion))
                {
                    AddDistinct(versions, gameVersion!.Trim());
                }

                if (LoaderNameOfTypeNumber(GetInt64(item, "modLoader")) is { } loader)
                {
                    AddDistinct(loaders, loader);
                }
            }
        }

        return (versions, loaders);
    }

    /// <summary>
    /// <c>gameVersions</c> 里的一个标签：是加载器就进 <paramref name="loaders"/>，
    /// 是标准 MC 版本就进 <paramref name="versions"/>，其余（<c>Client</c> / <c>Server</c> / …）丢掉。
    /// </summary>
    private static void CollectTag(string? tag, List<string> versions, List<string> loaders)
    {
        var text = tag?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (LoaderNameOfTag(text) is { } loader)
        {
            AddDistinct(loaders, loader);
            return;
        }

        if (CrossSourceSearch.IsStandardGameVersion(text))
        {
            AddDistinct(versions, text);
        }
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    /// <summary><c>latestFiles[].gameVersions</c> 里的加载器标签 → 统一小写的加载器名。</summary>
    private static string? LoaderNameOfTag(string tag) => tag.ToLowerInvariant() switch
    {
        "forge" => "forge",
        "neoforge" => "neoforge",
        "fabric" => "fabric",
        "quilt" => "quilt",
        _ => null,
    };

    /// <summary>
    /// <c>latestFilesIndexes[].modLoader</c>（CurseForge <c>ModLoaderType</c> 数字）→ 加载器名。
    /// 2=Cauldron、3=LiteLoader 本项目没有对应加载器，与 <see cref="ModLoaderTypeOf"/> 保持同一份映射。
    /// </summary>
    private static string? LoaderNameOfTypeNumber(long modLoader) => modLoader switch
    {
        1 => "forge",
        4 => "fabric",
        5 => "quilt",
        6 => "neoforge",
        _ => null,
    };

    /// <summary>解析 <c>GET /v1/mods/{modId}/files</c> 的响应，按发布时间降序。</summary>
    public static IReadOnlyList<CurseForgeFile> ParseFiles(string json) =>
        ParseFilePage(json).Files
            .OrderByDescending(file => file.FileDate ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>
    /// 解析一页文件列表，连同 <c>pagination</c> 一起交出来。
    ///
    /// 单独暴露分页字段是因为翻页必须知道「服务端这一页到底给了几条」（<see cref="CurseForgeFilePage.ResultCount"/>）
    /// 与「总共几条」（<see cref="CurseForgeFilePage.TotalCount"/>）：
    /// 前者可能小于请求的 pageSize，后者是终止判据，二者都不能从 <c>data[]</c> 反推
    /// （解析失败的单条会被丢掉，反推会把 index 推错、漏页）。
    /// </summary>
    internal static CurseForgeFilePage ParseFilePage(string json)
    {
        var files = new List<CurseForgeFile>();

        using var document = JsonDocument.Parse(json, JsonOptions);
        var root = document.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var file = ParseFileElement(item);

                if (file is not null)
                {
                    files.Add(file);
                }
            }
        }

        var hasPagination = false;
        long totalCount = 0;
        var resultCount = files.Count;

        if (root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
        {
            hasPagination = true;

            if (pagination.TryGetProperty("resultCount", out var resultNode) && resultNode.TryGetInt32(out var parsedResult))
            {
                resultCount = parsedResult;
            }

            if (pagination.TryGetProperty("totalCount", out var totalNode) && totalNode.TryGetInt64(out var parsedTotal))
            {
                totalCount = parsedTotal;
            }
        }

        return new CurseForgeFilePage(files, resultCount, totalCount, hasPagination);
    }

    /// <summary>把一页文件并入结果集，按 <see cref="CurseForgeFile.Id"/> 去重，返回真正新增的条数。</summary>
    /// <remarks>
    /// 按 Id 而不是文件名去重：CF 分页在项目并发上传时会返回重复行（PCL2 issue #1330），
    /// 同一条文件的不同行只按 Id 相同；而「同名不同 Id」是两个真实文件，不能合并。
    /// </remarks>
    private static int AppendNewFiles(
        IReadOnlyList<CurseForgeFile> page,
        List<CurseForgeFile> collected,
        HashSet<long> seen)
    {
        var added = 0;

        for (var i = 0; i < page.Count; i++)
        {
            if (seen.Add(page[i].Id))
            {
                collected.Add(page[i]);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// 解析单个文件对象。列表接口、指纹反查的 <c>file</c> / <c>latestFiles[]</c> 三处形状一致，
    /// 共用一份映射，避免字段增减时漏改其中一处。
    /// </summary>
    public static CurseForgeFile? ParseFileElement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetInt64(item, "id");
        var fileName = GetString(item, "fileName");

        if (id <= 0 || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var releaseType = (int)GetInt64(item, "releaseType");

        return new CurseForgeFile
        {
            Id = id,
            ModId = GetInt64(item, "modId"),
            FileName = fileName,
            DisplayName = GetString(item, "displayName") ?? string.Empty,
            DownloadUrl = GetString(item, "downloadUrl"),
            FileLength = GetInt64(item, "fileLength"),
            FileDate = ParseTimestamp(item, "fileDate"),
            ReleaseType = releaseType is >= 1 and <= 3 ? releaseType : 1,
            GameVersions = GetStringArray(item, "gameVersions"),
            Dependencies = ParseDependencies(item),
            Sha1 = ParseSha1(item),
        };
    }

    /// <summary>
    /// 解析 cfwidget 的项目详情。<c>files[]</c> 默认在本地按游戏版本 / 加载器过滤，并按文件 ID 降序
    /// （cfwidget 的 ID 递增，近似发布时间）；<paramref name="isModpack"/> 为 true 时不过滤、只排序。
    /// 返回 null 表示响应里没有可用的项目。
    /// </summary>
    /// <param name="json">响应正文。</param>
    /// <param name="kind">内容类型（整合包入口传 <see cref="ContentKind.Mod"/>）。</param>
    /// <param name="slug">项目 slug。</param>
    /// <param name="gameVersion">目标游戏版本，null 表示不过滤。</param>
    /// <param name="loader">目标加载器，<c>Vanilla</c> 表示不过滤。</param>
    /// <param name="isModpack">
    /// 整合包入口传 true：回填 <see cref="CurseForgeProject.IsModpack"/>，并且
    /// <see cref="CurseForgeProject.Files"/> 不做版本 / 加载器过滤——整合包是玩家先选包、再在包里
    /// 选构建，提前过滤会把能玩的整包从列表里删掉。
    /// </param>
    public static CurseForgeProject? ParseWidgetProject(
        string json,
        ContentKind kind,
        string slug,
        string? gameVersion,
        LoaderKind loader,
        bool isModpack = false)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetInt64(root, "id");
        var title = GetString(root, "title");

        if (id <= 0 || string.IsNullOrEmpty(title))
        {
            return null;
        }

        var files = ParseWidgetFiles(root, id);

        return new CurseForgeProject
        {
            Id = id,
            Slug = slug,
            Name = title,
            Summary = GetString(root, "summary") ?? string.Empty,
            Authors = JoinMemberNames(root),
            DownloadCount = GetNestedInt64(root, "downloads", "total"),
            IconUrl = GetString(root, "thumbnail"),
            Kind = kind,
            IsModpack = isModpack,
            Source = CurseForgeSource.CfWidget,
            Files = isModpack
                ? SortWidgetFiles(files)
                : FilterWidgetFiles(files, gameVersion, loader, kind),
        };
    }

    /// <summary>整合包文件列表只按文件 ID 降序（近似发布时间），不按版本 / 加载器裁剪。</summary>
    private static List<CurseForgeFile> SortWidgetFiles(IReadOnlyList<CurseForgeFile> files) =>
        files.OrderByDescending(file => file.Id).ToList();

    /// <summary>
    /// 解析 cfwidget 的 <c>files[]</c>。cfwidget 不返回直链与哈希，直链按 fileId 推导，
    /// <see cref="CurseForgeFile.Sha1"/> 保持 null（安装时跳过校验）。
    /// </summary>
    private static IReadOnlyList<CurseForgeFile> ParseWidgetFiles(JsonElement root, long modId)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CurseForgeFile>();
        }

        var results = new List<CurseForgeFile>();

        foreach (var item in files.EnumerateArray())
        {
            var id = GetInt64(item, "id");
            var fileName = GetString(item, "name");

            if (id <= 0 || string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            // versions[] 是完整标签（含 Client / Server / 加载器），version 是主 MC 版本，合并去重。
            var versions = new List<string>(GetStringArray(item, "versions"));
            var primaryVersion = GetString(item, "version");

            if (!string.IsNullOrEmpty(primaryVersion) && !ContainsIgnoreCase(versions, primaryVersion))
            {
                versions.Add(primaryVersion);
            }

            results.Add(new CurseForgeFile
            {
                Id = id,
                ModId = modId,
                FileName = fileName,
                DisplayName = GetString(item, "display") ?? string.Empty,
                DownloadUrl = CurseForgeCdn.BuildUrl(id, fileName),
                FileLength = GetInt64(item, "filesize"),
                FileDate = ParseTimestamp(item, "uploaded_at"),
                ReleaseType = ReleaseTypeOf(GetString(item, "type")),
                GameVersions = versions,
                Source = CurseForgeSource.CfWidget,
            });
        }

        return results;
    }

    /// <summary>
    /// 按游戏版本与加载器过滤文件。口径与官方 API 路径一致：资源包 / 光影不按加载器过滤，
    /// 因为它们没有「加载器」这个概念。
    /// </summary>
    private static List<CurseForgeFile> FilterWidgetFiles(
        IReadOnlyList<CurseForgeFile> files,
        string? gameVersion,
        LoaderKind loader,
        ContentKind kind)
    {
        var version = gameVersion?.Trim();
        var loaderName = kind == ContentKind.Mod ? LoaderNameOf(loader) : null;
        var filtered = new List<CurseForgeFile>(files.Count);

        foreach (var file in files)
        {
            if (!string.IsNullOrEmpty(version) && !ContainsIgnoreCase(file.GameVersions, version))
            {
                continue;
            }

            if (loaderName is not null && !ContainsIgnoreCase(file.GameVersions, loaderName))
            {
                continue;
            }

            filtered.Add(file);
        }

        return filtered
            .OrderByDescending(file => file.Id)
            .ToList();
    }

    /// <summary>cfwidget 的 <c>members[].username</c> 用「、」连接，与官方接口的 authors 输出一致。</summary>
    private static string? JoinMemberNames(JsonElement root)
    {
        if (!root.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();

        foreach (var member in members.EnumerateArray())
        {
            if (GetString(member, "username") is { Length: > 0 } username)
            {
                names.Add(username);
            }
        }

        return names.Count == 0 ? null : string.Join("、", names);
    }

    /// <summary>cfwidget 的 type 是字符串（release / beta / alpha），映射到官方 API 的数字枚举。</summary>
    private static int ReleaseTypeOf(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "beta" => 2,
        "alpha" => 3,
        _ => 1,
    };

    /// <summary>加载器 → cfwidget <c>versions[]</c> 里的名字。Vanilla 表示不过滤。</summary>
    private static string? LoaderNameOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => null,
    };

    private static bool ContainsIgnoreCase(IReadOnlyList<string> values, string expected)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetNestedInt64(JsonElement element, string outer, string inner)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(outer, out var nested) ||
            nested.ValueKind != JsonValueKind.Object ||
            !nested.TryGetProperty(inner, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var number))
        {
            return 0;
        }

        return number;
    }

    /// <summary>作者名拼成「甲、乙」，与 Modrinth 的单作者字段保持可展示的一致形状。</summary>
    private static string? JoinAuthors(JsonElement project)
    {
        if (!project.TryGetProperty("authors", out var authors) || authors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();

        foreach (var author in authors.EnumerateArray())
        {
            if (GetString(author, "name") is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? null : string.Join("、", names);
    }

    /// <summary>取 <c>logo.thumbnailUrl</c>，缺失时退回 <c>logo.url</c>。</summary>
    private static string? GetLogoUrl(JsonElement project)
    {
        if (!project.TryGetProperty("logo", out var logo) || logo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var thumbnail = GetString(logo, "thumbnailUrl");

        return string.IsNullOrWhiteSpace(thumbnail) ? GetString(logo, "url") : thumbnail;
    }

    private static string? ParseSha1(JsonElement file)
    {
        if (!file.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var hash in hashes.EnumerateArray())
        {
            // algo == 1 是 SHA1（CurseForge 的 HashAlgo 枚举）。
            if (GetInt64(hash, "algo") != 1)
            {
                continue;
            }

            var value = GetString(hash, "value");

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<CurseForgeDependency> ParseDependencies(JsonElement file)
    {
        if (!file.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CurseForgeDependency>();
        }

        var results = new List<CurseForgeDependency>();

        foreach (var dependency in dependencies.EnumerateArray())
        {
            var modId = GetInt64(dependency, "modId");

            if (modId <= 0)
            {
                continue;
            }

            results.Add(new CurseForgeDependency(modId, (int)GetInt64(dependency, "relationType")));
        }

        return results;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool GetBoolean(JsonElement element, string property, bool fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(property, out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var items = new List<string>();

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    items.Add(text);
                }
            }

            return items;
        }

        return Array.Empty<string>();
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string property)
    {
        var text = GetString(element, property);

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    // ---------------- 落盘辅助 ----------------

    private static void Report(
        IProgress<DownloadProgress>? progress,
        long totalBytes,
        long receivedBytes,
        TimeSpan elapsed,
        string fileName)
    {
        if (progress is null)
        {
            return;
        }

        var speed = elapsed.TotalSeconds > 0.05 ? receivedBytes / elapsed.TotalSeconds : 0;
        var ratio = totalBytes > 0 ? Math.Clamp((double)receivedBytes / totalBytes, 0, 1) : 0;

        progress.Report(new DownloadProgress(
            TotalFiles: 1,
            CompletedFiles: 0,
            FailedFiles: 0,
            TotalBytes: totalBytes,
            DownloadedBytes: receivedBytes,
            SpeedBytesPerSecond: speed,
            CurrentFile: fileName,
            Ratio: ratio));
    }

    private static string ExtensionOf(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.Absolute).AbsolutePath;
            var extension = Path.GetExtension(path).ToLowerInvariant();

            return extension switch
            {
                ".webp" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" => extension,
                _ => string.Empty,
            };
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 残留的 .part 不影响下一次安装：下一次会用同一个路径 Create 覆盖。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var ownsHandler = handler is not null;

        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 16,
        };

        var client = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            // 兜底值。真实超时由每次请求的 CancelAfter 控制（搜索 15 秒 / 下载 5 分钟）。
            Timeout = TimeSpan.FromMinutes(10),
        };

        // 该值包含括号与斜杠，严格解析会抛异常，而服务端只按字符串匹配。
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 把用户输入解析成 CurseForge 的 slug。「粘贴项目链接」这条精确解析路径要把网页地址
/// 转成 cfwidget 认识的项目名，所以这里同时兼容「裸 slug」与「项目网页链接」两种输入。
/// </summary>
public static class CurseForgeSlugParser
{
    /// <summary>CurseForge 网页里代表项目类型的那一段，slug 紧跟在它后面。</summary>
    private static readonly string[] ProjectSections = ["mc-mods", "texture-packs", "shaders", "modpacks", "worlds"];

    /// <summary>
    /// 解析 slug。无法识别时返回 null：含空格、中文、空串，或者链接里看不出项目类型，
    /// 都由调用方据此提示「请直接粘贴项目页地址」。
    /// </summary>
    public static string? Parse(string? slugOrUrl)
    {
        if (string.IsNullOrWhiteSpace(slugOrUrl))
        {
            return null;
        }

        var text = slugOrUrl.Trim().TrimEnd('/');

        if (text.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // 项目类型段之后的一段就是 slug；再后面的 files/<id> 一律忽略。
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (ProjectSections.Contains(segments[index], StringComparer.OrdinalIgnoreCase))
                {
                    return IsValidSlug(segments[index + 1]) ? segments[index + 1] : null;
                }
            }

            return null;
        }

        return IsValidSlug(text) ? text : null;
    }

    /// <summary>slug 只允许字母、数字与 <c>- _ .</c>；出现其他字符说明用户输入的是搜索词，不是 slug。</summary>
    private static bool IsValidSlug(string value)
    {
        if (value.Length is 0 or > 128)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}

/// <summary>
/// 按 fileId 推导 CurseForge CDN 下载地址。cfwidget 的项目详情不返回直链，
/// 但 CDN 路径由 fileId 决定：
/// <c>/files/{fileId / 1000 补零四位}/{fileId % 1000}/{fileName}</c>。
///
/// 实测（2026-10）：<c>mediafilez.forgecdn.net</c> 对该路径返回 200 且 Content-Length 与
/// cfwidget 的 filesize 一致；<c>edge.forgecdn.net</c> 亦可作为备用；
/// <c>media.forgecdn.net</c> 返回 403，不要用。
///
/// 注意：CurseForge 已宣布 CDN 下载将强制 API Key（官方公告：2026-07 起无 key 返回 401），
/// 届时这条链路需要改用官方接口返回的 downloadUrl。
/// </summary>
public static class CurseForgeCdn
{
    /// <summary>主用域名。</summary>
    public const string PrimaryHost = "https://mediafilez.forgecdn.net";

    /// <summary>备用域名：主域名 403 / 404 时换它再试一次。</summary>
    public const string FallbackHost = "https://edge.forgecdn.net";

    /// <summary>按 fileId 与文件名构造主域名的下载地址。</summary>
    public static string BuildUrl(long fileId, string fileName) => BuildUrl(PrimaryHost, fileId, fileName);

    /// <summary>按指定域名构造下载地址。文件名会做 URL 编码（可能含 <c>+</c>、空格、<c>#</c>）。</summary>
    public static string BuildUrl(string host, long fileId, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var prefix = (fileId / 1000).ToString("D4", CultureInfo.InvariantCulture);
        var suffix = (fileId % 1000).ToString(CultureInfo.InvariantCulture);

        return $"{host.TrimEnd('/')}/files/{prefix}/{suffix}/{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>主备两个候选地址；不是 forgecdn 的地址原样返回一个。</summary>
    public static IReadOnlyList<string> Candidates(string url)
    {
        var candidates = new List<string>(2) { url };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return candidates;
        }

        var alternate = uri.Host switch
        {
            "mediafilez.forgecdn.net" => "edge.forgecdn.net",
            "edge.forgecdn.net" => "mediafilez.forgecdn.net",
            _ => null,
        };

        if (alternate is not null)
        {
            candidates.Add($"{uri.Scheme}://{alternate}{uri.PathAndQuery}");
        }

        return candidates;
    }
}

/// <summary>
/// <c>POST /v1/fingerprints</c> 的请求体。
///
/// 字段名由 <see cref="JsonPropertyNameAttribute"/> 钉死：这个请求体原来是匿名对象，
/// 属性名就是服务端要求的 <c>fingerprints</c>；换成具名记录后默认会输出 <c>Fingerprints</c>，
/// CurseForge 会判 400。
/// </summary>
internal sealed record FingerprintQueryRequest(
    [property: JsonPropertyName("fingerprints")] uint[] Fingerprints);

/// <summary><c>POST /v1/mods</c> 的请求体，字段名同样必须是小写驼峰。</summary>
internal sealed record ModIdsQueryRequest(
    [property: JsonPropertyName("modIds")] List<long> ModIds);

/// <summary>
/// CurseForge 请求体的 Source Generator 上下文。
///
/// 这两个请求原来是匿名对象，靠运行时反射生成元数据；裁剪发布下 POST 会在序列化这一步直接抛异常。
/// </summary>
[JsonSerializable(typeof(FingerprintQueryRequest))]
[JsonSerializable(typeof(ModIdsQueryRequest))]
internal sealed partial class CurseForgeRequestJsonContext : JsonSerializerContext
{
}
