using System.Globalization;
using System.Net;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 整合包在线目录：按 Modrinth 的 <c>project_type:modpack</c> 搜项目。
///
/// 为什么需要它：<see cref="Abstractions.IModrinthService.SearchAsync"/> 的 facets 由
/// <see cref="ContentKind"/> 决定，而 <see cref="ContentKind"/> 是<b>本地内容目录</b>的键
/// （mods / resourcepacks / shaderpacks / datapacks）。整合包不落在内容目录里——它是「装成实例」的，
/// 给那个枚举加一个值要同步改 <c>DirectoryNameFor</c>、<c>ContentTarget.DirectoryNameOf</c>、
/// 扫描与版本不匹配检查等 6 处 switch（HANDOVER §9），代价比收益高一个数量级。
///
/// 所以这里只是<b>薄封装</b>：补一个「搜索 URL + 结果解析」，**不引入新模型、不引入新搜索状态机**。
/// 解析复用 <see cref="ModrinthService.ParseSearch"/>，版本列表仍走
/// <see cref="Abstractions.IModrinthService.GetVersionsAsync"/>，下载走 <c>DownloadQueue</c>，
/// 导入走 <see cref="Abstractions.IModpackService.ImportAsync"/>。
///
/// <b>搜索不按游戏版本过滤</b>：玩家的顺序是「先找主题包 → 再在包里挑构建」。
/// 搜索层按版本筛会把别的版本的包全部藏起来，玩家连「有这个包」都看不到；
/// 版本筛选放在选中包之后的版本列表里（由 UI 侧的「按游戏版本筛选」下拉承担）。
///
/// 所有网络失败都退化成「空集合 + <see cref="LastError"/>（中文）」，不把异常抛给 UI。
/// </summary>
public sealed class ModpackCatalogClient : IDisposable
{
    /// <summary>与 <see cref="ModrinthService.ApiBase"/> 同一个 API 根。</summary>
    public const string ApiBase = ModrinthService.ApiBase;

    /// <summary>Modrinth 的整合包 project_type 取值。</summary>
    public const string ProjectTypeFacet = "project_type:modpack";

    /// <summary>单页上限。与 <see cref="ModrinthService.MaxPageSize"/> 保持一致，超了服务端会截断。</summary>
    public const int MaxPageSize = ModrinthService.MaxPageSize;

    private const string UserAgent = ModrinthService.UserAgent;

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;

    /// <summary>用默认网络栈构造。生产路径走这个。</summary>
    public ModpackCatalogClient()
        : this(null)
    {
    }

    /// <summary>
    /// 注入 <see cref="HttpMessageHandler"/> 的构造：测试用假 handler 顶掉网络，测试不联网。
    /// </summary>
    public ModpackCatalogClient(HttpMessageHandler? handler)
    {
        _http = CreateClient(handler);
    }

    /// <summary>最近一次失败的用户可读原因（中文）。成功或尚未请求时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 搜索整合包项目。<paramref name="query"/> 为空表示「按下载量浏览热门」。
    ///
    /// 不按游戏版本过滤（理由见类型注释），也不按加载器过滤：整合包自带加载器，
    /// 按当前实例的加载器筛会把别的加载器的包藏起来。
    /// </summary>
    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var url = BuildSearchUrl(query, offset, limit);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);

        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = DescribeStatus(response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseSearch(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 只有本次请求的超时（不是调用方取消）：照样退化成可展示的原因。
            LastError = "搜索超时：Modrinth 没有在 15 秒内响应，稍后重试。";
            return [];
        }
        catch (Exception)
        {
            LastError = "连接 Modrinth 失败：检查网络或代理后重试。";
            return [];
        }
    }

    /// <summary>拼搜索 URL。空查询按下载量排序（浏览热门），有查询按相关度。</summary>
    public static string BuildSearchUrl(string? query, int offset, int limit)
    {
        var text = (query ?? string.Empty).Trim();
        var index = text.Length == 0 ? "downloads" : "relevance";
        var facets = BuildSearchFacets();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ApiBase}/search?query={Uri.EscapeDataString(text)}&facets={Uri.EscapeDataString(facets)}&index={index}&offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, MaxPageSize)}");
    }

    /// <summary>
    /// 拼 facets。只有一组 OR 条件：<c>project_type:modpack</c>。
    /// 格式与 <see cref="ModrinthService.BuildSearchFacets"/> 同一套，只是把 project_type 换成 modpack，
    /// 并刻意不再追加 <c>versions:</c> 组。
    /// </summary>
    public static string BuildSearchFacets() =>
        "[[\"" + ProjectTypeFacet + "\"]]";

    /// <summary>
    /// 解析 <c>GET /v2/search</c> 的响应。复用 <see cref="ModrinthService.ParseSearch"/>：
    /// 搜索响应里没有 <c>project_type</c> 字段，所以回填的类型是 <see cref="ContentKind.Mod"/>；
    /// 卡片上的「整合包」徽标与项目页链接由调用方按分区修正（见 LibraryViewModel）。
    /// </summary>
    public static IReadOnlyList<ModrinthProject> ParseSearch(string json) =>
        ModrinthService.ParseSearch(json, ContentKind.Mod);

    /// <summary>状态码 → 用户可读的中文原因。429 单独说明，因为它会自然恢复。</summary>
    public static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "Modrinth 限流了，等几秒再搜。",
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
            "Modrinth 暂时不可用，稍后重试。",
        _ => $"Modrinth 返回了错误（HTTP {(int)status}），稍后重试。",
    };

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var ownsHandler = handler is not null;

        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 4,
        };

        var client = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            // 兜底值。真实超时由每次请求的 CancelAfter 控制（15 秒）。
            Timeout = TimeSpan.FromMinutes(2),
        };

        // Modrinth 要求客户端自报身份；该值含括号与斜杠，严格解析会抛异常，服务端只按字符串匹配。
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        return client;
    }

    public void Dispose() => _http.Dispose();
}