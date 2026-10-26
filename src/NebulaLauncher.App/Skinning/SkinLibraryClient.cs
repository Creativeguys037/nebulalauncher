using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Skinning;

/// <summary>皮肤体型。站点里只有 Steve（经典 4 格臂）与 Alex（3 格臂）两种。</summary>
public enum SkinModelKind
{
    Steve,
    Alex,
}

/// <summary>皮肤库的筛选维度，对应 <c>/skinlib/list?filter=…</c>。</summary>
public enum SkinLibraryFilter
{
    /// <summary>全部皮肤，站点值是 <c>skin</c>。</summary>
    All,
    Steve,
    Alex,
    Cape,
}

/// <summary>列表里的一行：够画平面小图 + 名字 + 体型 + 人气，不展开整套皮肤。</summary>
public sealed record SkinLibraryItem(int Tid, string Name, SkinModelKind Model, int Likes);

/// <summary>一页结果。站点不返回总数，末页只能靠 <c>next_page_url</c> 缺失判断。</summary>
public sealed record SkinLibraryPage(int Page, IReadOnlyList<SkinLibraryItem> Items, bool IsLastPage);

/// <summary>详情。原始纹理要另取一次 <c>/textures/{hash}</c>（PNG，路径不带扩展名）。</summary>
public sealed record SkinLibraryInfo(int Tid, string Name, SkinModelKind Model, int Likes, string Hash);

/// <summary>
/// 一次皮肤站调用的结果：要么有值，要么有一句能直接给界面看的错。
///
/// 用结果对象而不是抛异常，是为了不让网络与协议失败穿过层边界
/// （CONVENTIONS §3：异常只在上边界吞，服务不把原始异常抛给 ViewModel）。
/// </summary>
public sealed class SkinLibraryResult<T>
    where T : class
{
    private SkinLibraryResult(T? value, string? errorText, bool isNetworkError)
    {
        Value = value;
        ErrorText = errorText;
        IsNetworkError = isNetworkError;
    }

    /// <summary>成功时的值；失败为 <c>null</c>。</summary>
    public T? Value { get; }

    /// <summary>失败时给用户看的句子；成功为 <c>null</c>。</summary>
    public string? ErrorText { get; }

    /// <summary>
    /// 失败原因是「根本连不上」还是「连上了但站点不对」。界面据此决定错误条写「无网络」
    /// 还是「皮肤站异常」——把 500 说成断网会让人白折腾网络设置。
    /// </summary>
    public bool IsNetworkError { get; }

    public bool Succeeded => ErrorText is null;

    // CA1000：工厂必须带 T 才能让调用点写成 SkinLibraryResult<SkinLibraryPage>.Fail("…")，
    // 挪到非泛型静态类反而要求每处调用手写类型参数，可读性更差。
    [SuppressMessage("Design", "CA1000:不要在泛型类型中声明静态成员", Justification = "结果对象的工厂带 T，调用点才读得懂。")]
    public static SkinLibraryResult<T> Ok(T value) => new(value, null, false);

    [SuppressMessage("Design", "CA1000:不要在泛型类型中声明静态成员", Justification = "结果对象的工厂带 T，调用点才读得懂。")]
    public static SkinLibraryResult<T> Fail(string errorText, bool isNetworkError = false) =>
        new(null, errorText, isNetworkError);
}

/// <summary>
/// 皮肤站（BlessingSkin）浏览端点：list / info / textures / preview。
///
/// 三条站点实测事实决定了这里的形状（2026-10-26，littleskin.cn）：
/// <list type="bullet">
/// <item><c>/skinlib/list</c> 是 Laravel 分页对象，**没有** <c>total</c> 和 <c>last_page</c>，
/// 末页只能看 <c>next_page_url == null</c>；把缺失字段当必填会直接抛异常。</item>
/// <item><c>sort</c> 只认 <c>time</c>/<c>likes</c>/<c>tid</c>，其它值站点 500，
/// 所以本地先用白名单拦，非法值一个请求都不发。</item>
/// <item><c>per_page</c> 无效，站点硬编码 20 条/页，不要传。</item>
/// </list>
///
/// <see cref="HttpClient"/> 走注入，测试用假 handler 就能覆盖全部容错分支。
/// </summary>
public sealed class SkinLibraryClient : IDisposable
{
    /// <summary>默认皮肤站。</summary>
    public const string DefaultBaseAddress = "https://littleskin.cn";

    /// <summary>站点每页硬编码 20 条，界面页码与「下一页」判定都按它算。</summary>
    public const int PageSize = 20;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly string _baseAddress;

    private readonly IAppLog? _log;

    public SkinLibraryClient(HttpClient? http = null, string baseAddress = DefaultBaseAddress, IAppLog? log = null)
    {
        _ownsHttp = http is null;
        _http = http ?? Shared;
        _baseAddress = string.IsNullOrWhiteSpace(baseAddress) ? DefaultBaseAddress : baseAddress.TrimEnd('/');
        _log = log;
    }

    /// <summary>站点接受的排序值。顺序就是界面上的顺序。</summary>
    public static IReadOnlyList<string> SortKeys { get; } = ["time", "likes", "tid"];

    public string BaseAddress => _baseAddress;

    public string TextureUrl(string hash) => $"{_baseAddress}/textures/{hash}";

    public string PreviewUrl(int tid) => $"{_baseAddress}/preview/{tid}";

    /// <summary>
    /// 列一页皮肤。<paramref name="sort"/> 不在 <see cref="SortKeys"/> 里直接回
    /// <see cref="SkinLibraryResult{T}.Fail"/>：站点会 500，这个值一个请求都不该发出去。
    /// </summary>
    public Task<SkinLibraryResult<SkinLibraryPage>> ListAsync(
        int page,
        SkinLibraryFilter filter,
        string? keyword,
        string sort,
        CancellationToken cancellationToken)
    {
        if (!SortKeys.Contains(sort))
        {
            return Task.FromResult(SkinLibraryResult<SkinLibraryPage>.Fail(
                $"sort 只接受 {string.Join('/', SortKeys)}，收到「{sort}」。"));
        }

        var url = new StringBuilder($"{_baseAddress}/skinlib/list?page={Math.Max(1, page)}");

        if (filter != SkinLibraryFilter.All)
        {
            url.Append("&filter=").Append(FilterValue(filter));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            url.Append("&keyword=").Append(Uri.EscapeDataString(keyword.Trim()));
        }

        url.Append("&sort=").Append(sort);
        return FetchPageAsync(url.ToString(), cancellationToken);
    }

    /// <summary>取一套皮肤的详情，主要是为了拿 <c>hash</c> 去拼原始纹理地址。非 200 视为拿不到。</summary>
    public async Task<SkinLibraryResult<SkinLibraryInfo>> InfoAsync(int tid, CancellationToken cancellationToken)
    {
        var url = $"{_baseAddress}/skinlib/info/{tid}";
        var fetched = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (fetched.ErrorText is { } error)
        {
            return SkinLibraryResult<SkinLibraryInfo>.Fail(error, fetched.IsNetworkError);
        }

        if (fetched.Status != HttpStatusCode.OK)
        {
            _log?.Warn($"皮肤库详情 {tid} 返回 {(int)fetched.Status} {fetched.Status}。");
            return SkinLibraryResult<SkinLibraryInfo>.Fail($"拿不到这套皮肤的详情（HTTP {(int)fetched.Status}）。");
        }

        if (JsonError(fetched.MediaType, $"皮肤库详情 {tid}") is { } mediaError)
        {
            return SkinLibraryResult<SkinLibraryInfo>.Fail(mediaError);
        }

        return ParseInfo(fetched.Body);
    }

    /// <summary>下载任意皮肤库资源（纹理 PNG 或预览图）。非 200/空内容/网络失败返回 <c>null</c>。</summary>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log?.Warn($"皮肤库资源 {url} 返回 {(int)response.StatusCode} {response.StatusCode}。");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"皮肤库资源下载失败：{url}", ex);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"皮肤库资源下载超时：{url}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp && !ReferenceEquals(_http, Shared))
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// 解析 <c>/skinlib/list</c>。缺 <c>data</c> 当空页；缺 <c>next_page_url</c> 当末页；
    /// 单条缺 <c>tid</c> 跳过而不是整页失败。
    /// </summary>
    public static SkinLibraryResult<SkinLibraryPage> ParseList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return SkinLibraryResult<SkinLibraryPage>.Fail("皮肤库返回了空响应。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return SkinLibraryResult<SkinLibraryPage>.Fail("皮肤库返回的 JSON 不是分页对象。");
            }

            var page = ReadInt(root, "current_page") ?? 1;
            var items = new List<SkinLibraryItem>();

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (ReadInt(entry, "tid") is not { } tid)
                    {
                        continue;
                    }

                    items.Add(new SkinLibraryItem(
                        tid,
                        ReadString(entry, "name") ?? "(未命名)",
                        ModelOf(ReadString(entry, "type")),
                        ReadInt(entry, "likes") ?? 0));
                }
            }

            return SkinLibraryResult<SkinLibraryPage>.Ok(new SkinLibraryPage(page, items, IsLastPage(root)));
        }
        catch (JsonException)
        {
            return SkinLibraryResult<SkinLibraryPage>.Fail("皮肤库返回的 JSON 无法解析。");
        }
    }

    /// <summary>解析 <c>/skinlib/info/{tid}</c>。缺 <c>tid</c> 或缺 <c>hash</c> 就等于拿不到皮肤。</summary>
    public static SkinLibraryResult<SkinLibraryInfo> ParseInfo(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return SkinLibraryResult<SkinLibraryInfo>.Fail("皮肤库返回了空响应。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return SkinLibraryResult<SkinLibraryInfo>.Fail("皮肤库详情不是 JSON 对象。");
            }

            if (ReadInt(root, "tid") is not { } tid || ReadString(root, "hash") is not { Length: > 0 } hash)
            {
                return SkinLibraryResult<SkinLibraryInfo>.Fail("皮肤库详情里没有皮肤文件地址。");
            }

            return SkinLibraryResult<SkinLibraryInfo>.Ok(new SkinLibraryInfo(
                tid,
                ReadString(root, "name") ?? "(未命名)",
                ModelOf(ReadString(root, "type")),
                ReadInt(root, "likes") ?? 0,
                hash));
        }
        catch (JsonException)
        {
            return SkinLibraryResult<SkinLibraryInfo>.Fail("皮肤库详情无法解析。");
        }
    }

    private static bool IsLastPage(JsonElement root)
    {
        if (!root.TryGetProperty("next_page_url", out var next))
        {
            return true;
        }

        return next.ValueKind == JsonValueKind.Null
               || (next.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(next.GetString()));
    }

    private static string FilterValue(SkinLibraryFilter filter) => filter switch
    {
        SkinLibraryFilter.Steve => "steve",
        SkinLibraryFilter.Alex => "alex",
        SkinLibraryFilter.Cape => "cape",
        _ => "skin",
    };

    private static SkinModelKind ModelOf(string? type) =>
        string.Equals(type, "alex", StringComparison.OrdinalIgnoreCase) ? SkinModelKind.Alex : SkinModelKind.Steve;

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private async Task<SkinLibraryResult<SkinLibraryPage>> FetchPageAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var fetched = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (fetched.ErrorText is { } error)
        {
            return SkinLibraryResult<SkinLibraryPage>.Fail(error, fetched.IsNetworkError);
        }

        if (fetched.Status != HttpStatusCode.OK)
        {
            return SkinLibraryResult<SkinLibraryPage>.Fail($"皮肤库列表返回 {(int)fetched.Status} {fetched.Status}。");
        }

        if (JsonError(fetched.MediaType, "皮肤库列表") is { } mediaError)
        {
            return SkinLibraryResult<SkinLibraryPage>.Fail(mediaError);
        }

        return ParseList(fetched.Body);
    }

    private static string? JsonError(string? mediaType, string what) =>
        mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? $"{what}返回的不是 application/json（实际 {mediaType ?? "无 Content-Type"}），"
              + "多半是站点没开皮肤库或地址填错了。"
            : null;

    private async Task<(HttpStatusCode Status, string? MediaType, string Body, string? ErrorText, bool IsNetworkError)>
        GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, null, false);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"皮肤库请求失败：{url}", ex);
            return (HttpStatusCode.ServiceUnavailable, null, string.Empty, "连不上皮肤站，检查网络后重试。", true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"皮肤库请求超时：{url}");
            return (HttpStatusCode.GatewayTimeout, null, string.Empty, "皮肤站响应超时。", true);
        }
    }

    /// <summary>
    /// 探测站点能力：BlessingSkin 在 <c>/api</c> 报自己的版本。接入自建站前先看一眼，
    /// 免得把「装了个别的站」当成「皮肤库为空」。没有任何版本字段时视为不是 BlessingSkin。
    /// </summary>
    public async Task<string?> ProbeVersionAsync(CancellationToken cancellationToken)
    {
        var url = $"{_baseAddress}/api";
        var (status, _, body, errorText, _) = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (errorText is not null || status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var version = ReadString(document.RootElement, "blessing_skin");
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
