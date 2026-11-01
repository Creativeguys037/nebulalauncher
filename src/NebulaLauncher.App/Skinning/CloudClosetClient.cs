using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Skinning;

/// <summary>皮肤站网页会话里的一个角色。</summary>
public sealed record CloudPlayer(int Pid, string Name, string Uuid, int? TidSkin, int? TidCape);

/// <summary>云端衣柜里的一件材质。</summary>
public sealed record CloudClosetItem(
    int Tid,
    string Name,
    string Type,
    string Hash,
    int Likes,
    bool IsPublic,
    string? ItemName);

/// <summary>衣柜的一页。站点是 Laravel 分页对象，末页看 next_page_url。</summary>
public sealed record CloudClosetPage(int Page, bool IsLastPage, IReadOnlyList<CloudClosetItem> Items);

/// <summary>上传结果：重复 PNG 也会给出已有 tid，不算失败。</summary>
public sealed record CloudUploadResult(int Tid, bool IsDuplicate, string Message);

/// <summary>没有返回值的写操作（应用、清除、删除、登录）用一个显式结果对象，避免泛型结果类容纳值类型。</summary>
public sealed record CloudOperationResult(bool Ok);

/// <summary>
/// 一次皮肤站网页会话调用的结果。沿用 <see cref="SkinLibraryResult{T}"/> 的形状：
/// 服务层不把异常穿过 ViewModel 边界，界面拿到的是一句能直接展示的中文。
/// </summary>
public sealed class CloudClosetResult<T>
    where T : class
{
    private CloudClosetResult(T? value, string? errorText, bool isNetworkError, bool isAuthError)
    {
        Value = value;
        ErrorText = errorText;
        IsNetworkError = isNetworkError;
        IsAuthError = isAuthError;
    }

    public T? Value { get; }

    public string? ErrorText { get; }

    public bool IsNetworkError { get; }

    public bool IsAuthError { get; }

    public bool Succeeded => ErrorText is null;

    // CA1000：与 SkinLibraryResult 保持一致，调用点才读得懂。
    [SuppressMessage("Design", "CA1000:不要在泛型类型中声明静态成员", Justification = "结果对象的工厂带 T，调用点才读得懂。")]
    public static CloudClosetResult<T> Ok(T value) => new(value, null, false, false);

    [SuppressMessage("Design", "CA1000:不要在泛型类型中声明静态成员", Justification = "结果对象的工厂带 T，调用点才读得懂。")]
    public static CloudClosetResult<T> Fail(
        string errorText,
        bool isNetworkError = false,
        bool isAuthError = false) => new(null, errorText, isNetworkError, isAuthError);
}

/// <summary>
/// Blessing Skin 网页会话客户端：密码登录、角色列表、衣柜分页、上传、应用、清除、删除。
///
/// 为什么走网页路由而不是 OAuth：OAuth 的 <c>/api</c> 只能应用衣柜里已有的 tid，没有上传 PNG 的端点；
/// 网页会话还要 CSRF（<c>GET /auth/login</c> 的 meta csrf-token）与 <c>BS_SESSION</c> cookie。
/// 站点契约见 docs/research/littleskin-web-session-texture-write-contract.md。
///
/// 所有改状态请求都经过 <see cref="SendWithCsrfAsync{T}"/>：419 时刷新 token 重试一次，
/// 不静默重试到触发风控。失败响应可能有副作用（尤其 POST /texture 500 仍可能已建 texture 行），
/// 所以上传失败由 ViewModel 按名称/衣柜核对，删除只删启动器记录过的 tid。
/// </summary>
public sealed class CloudClosetClient : IDisposable
{
    public const string DefaultBaseAddress = "https://littleskin.cn";

    private static readonly Regex CsrfMetaNameFirst = new(
        "<meta[^>]*name=[\"']csrf-token[\"'][^>]*content=[\"']([^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CsrfMetaContentFirst = new(
        "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*name=[\"']csrf-token[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly CookieContainer? _cookies;

    private readonly IAppLog? _log;

    private readonly string _baseAddress;

    private string? _csrfToken;

    private bool _authenticated;

    public CloudClosetClient(HttpClient? http = null, string baseAddress = DefaultBaseAddress, IAppLog? log = null)
    {
        _baseAddress = NormalizeBaseAddress(baseAddress);
        _log = log;
        _ownsHttp = http is null;

        if (http is not null)
        {
            _http = http;
            return;
        }

        _cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string BaseAddress => _baseAddress;

    public bool IsAuthenticated => _authenticated;

    /// <summary>
    /// 丢掉当前网页会话。退出登录或切换账号时调用；保存的凭据不在这里处理。
    /// 注入 HttpClient 的测试实例没有自己的 CookieContainer，此时只清登录标记。
    /// </summary>
    public void ResetSession()
    {
        if (_cookies is not null)
        {
            foreach (Cookie cookie in _cookies.GetAllCookies())
            {
                cookie.Expired = true;
            }
        }

        _csrfToken = null;
        _authenticated = false;
    }

    /// <summary>把用户填的站址补成合法的 https 根地址；不合法时原样返回，登录前会再校验。</summary>
    public static string NormalizeBaseAddress(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return DefaultBaseAddress;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return text.TrimEnd('/');
    }

    public static bool IsValidBaseAddress(string value)
        => Uri.TryCreate(NormalizeBaseAddress(value), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>登录。成功后会话 cookie 留在当前客户端，后续请求自动携带。</summary>
    public async Task<CloudClosetResult<CloudOperationResult>> LoginAsync(
        string username,
        string password,
        bool remember,
        CancellationToken cancellationToken)
    {
        if (!IsValidBaseAddress(_baseAddress))
        {
            return CloudClosetResult<CloudOperationResult>.Fail("皮肤站地址不是有效的 http/https 地址。");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return CloudClosetResult<CloudOperationResult>.Fail("请填写皮肤站用户名和密码。");
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["identification"] = username.Trim(),
            ["password"] = password,
            ["remember"] = remember,
        });

        var result = await SendWithCsrfAsync(
            () => new HttpRequestMessage(HttpMethod.Post, _baseAddress + "/auth/login")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            ReadJsonAsync,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return CloudClosetResult<CloudOperationResult>.Fail(result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<CloudOperationResult>.Fail(response.ErrorText);
        }

        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        if (ReadCode(root) is { } code && code != 0)
        {
            return CloudClosetResult<CloudOperationResult>.Fail(ReadMessage(root) ?? "皮肤站拒绝了登录请求。");
        }

        _authenticated = true;
        _log?.Info($"皮肤站登录成功：{_baseAddress} / {username.Trim()}");
        return CloudClosetResult<CloudOperationResult>.Ok(new CloudOperationResult(true));
    }

    /// <summary>角色列表。响应既可能是顶层数组，也可能是包在 data 里的数组。</summary>
    public async Task<CloudClosetResult<IReadOnlyList<CloudPlayer>>> GetPlayersAsync(
        CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("/user/player/list", cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail(
                result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail(response.ErrorText);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!root.TryGetProperty("data", out root) || root.ValueKind != JsonValueKind.Array)
                {
                    return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail("角色列表响应不是数组。");
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail("角色列表响应不是数组。");
            }

            var players = new List<CloudPlayer>();
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || ReadInt(entry, "pid") is not { } pid)
                {
                    continue;
                }

                players.Add(new CloudPlayer(
                    pid,
                    ReadString(entry, "name") ?? "(未命名角色)",
                    ReadString(entry, "uuid") ?? string.Empty,
                    ReadInt(entry, "tid_skin"),
                    ReadInt(entry, "tid_cape")));
            }

            _authenticated = true;
            return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Ok(players);
        }
        catch (JsonException)
        {
            return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail("角色列表不是有效的 JSON。");
        }
    }

    /// <summary>衣柜一页。<paramref name="category"/> 只接受 skin 或 cape。</summary>
    public async Task<CloudClosetResult<CloudClosetPage>> GetClosetAsync(
        int page,
        string category,
        string? keyword,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = string.Equals(category, "cape", StringComparison.OrdinalIgnoreCase)
            ? "cape"
            : "skin";
        var url = new StringBuilder(_baseAddress)
            .Append("/user/closet/list?page=")
            .Append(Math.Max(1, page))
            .Append("&category=")
            .Append(normalizedCategory)
            .Append("&perPage=20");

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            url.Append("&q=").Append(Uri.EscapeDataString(keyword.Trim()));
        }

        var result = await GetJsonAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CloudClosetResult<CloudClosetPage>.Fail(
                result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<CloudClosetPage>.Fail(response.ErrorText);
        }

        return ParseCloset(response.Body);
    }

    /// <summary>解析 <c>/user/closet/list</c>。公开静态便于测试，不依赖网络。</summary>
    public static CloudClosetResult<CloudClosetPage> ParseCloset(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CloudClosetResult<CloudClosetPage>.Fail("衣柜返回了空响应。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CloudClosetResult<CloudClosetPage>.Fail("衣柜返回的 JSON 不是分页对象。");
            }

            var page = Math.Max(1, ReadInt(root, "current_page") ?? 1);

            // Laravel 的 last_page 是总页数（数字），不是布尔；ReadBool 会把 2 当成 true，
            // 于是从第 2 页起就误判成末页。这里优先按总页数比较，字段缺失时才看 next_page_url。
            var lastPage = ReadInt(root, "last_page") is { } totalPages
                ? page >= totalPages
                : IsLastPage(root);
            var items = new List<CloudClosetItem>();

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || ReadInt(entry, "tid") is not { } tid)
                    {
                        continue;
                    }

                    var type = (ReadString(entry, "type") ?? "steve").Trim().ToLowerInvariant();
                    var itemName = entry.TryGetProperty("pivot", out var pivot) && pivot.ValueKind == JsonValueKind.Object
                        ? ReadString(pivot, "item_name")
                        : null;

                    items.Add(new CloudClosetItem(
                        tid,
                        ReadString(entry, "name") ?? itemName ?? "(未命名材质)",
                        type,
                        ReadString(entry, "hash") ?? string.Empty,
                        ReadInt(entry, "likes") ?? 0,
                        ReadBool(entry, "public") ?? false,
                        itemName));
                }
            }

            return CloudClosetResult<CloudClosetPage>.Ok(new CloudClosetPage(page, lastPage, items));
        }
        catch (JsonException)
        {
            return CloudClosetResult<CloudClosetPage>.Fail("衣柜返回的 JSON 无法解析。");
        }
    }

    /// <summary>上传 PNG。type 用 steve / alex / cape，站点不接受 classic / slim。</summary>
    public async Task<CloudClosetResult<CloudUploadResult>> UploadTextureAsync(
        string name,
        byte[] png,
        string type,
        bool isPublic,
        CancellationToken cancellationToken)
    {
        if (png.Length == 0)
        {
            return CloudClosetResult<CloudUploadResult>.Fail("选的 PNG 是空文件。");
        }

        var normalizedType = type is "alex" or "cape" ? type : "steve";
        var uploadName = string.IsNullOrWhiteSpace(name) ? "NebulaLauncher" : name.Trim();

        var result = await SendWithCsrfAsync(
            () =>
            {
                var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(png);
                file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(file, "file", uploadName + ".png");
                form.Add(new StringContent(uploadName, Encoding.UTF8), "name");
                form.Add(new StringContent(normalizedType, Encoding.UTF8), "type");
                form.Add(new StringContent("0", Encoding.UTF8), "source");
                form.Add(new StringContent(string.Empty, Encoding.UTF8), "link");
                form.Add(new StringContent(isPublic ? "1" : "0", Encoding.UTF8), "public");
                form.Add(new StringContent("0", Encoding.UTF8), "ignoreSimCheck");
                return new HttpRequestMessage(HttpMethod.Post, _baseAddress + "/texture") { Content = form };
            },
            ReadJsonAsync,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return CloudClosetResult<CloudUploadResult>.Fail(
                result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<CloudUploadResult>.Fail(response.ErrorText);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var code = ReadCode(root);
            var message = ReadMessage(root) ?? string.Empty;

            if (code == 2)
            {
                var duplicateTid = ReadNestedTid(root);
                return duplicateTid is { } tid
                    ? CloudClosetResult<CloudUploadResult>.Ok(new CloudUploadResult(tid, true, message))
                    : CloudClosetResult<CloudUploadResult>.Fail("这张图已经存在，但站点没有返回已有材质编号。");
            }

            if (code is not null && code != 0)
            {
                return CloudClosetResult<CloudUploadResult>.Fail(
                    message.Length > 0 ? message : "皮肤站拒绝了上传请求。");
            }

            var uploadedTid = ReadNestedTid(root);
            return uploadedTid is { } uploaded
                ? CloudClosetResult<CloudUploadResult>.Ok(new CloudUploadResult(uploaded, false, message))
                : CloudClosetResult<CloudUploadResult>.Fail("上传响应里没有材质编号，无法确认是否成功。");
        }
        catch (JsonException)
        {
            return CloudClosetResult<CloudUploadResult>.Fail("上传响应不是有效的 JSON。");
        }
    }

    /// <summary>下载原始纹理。<paramref name="hash"/> 来自衣柜行。</summary>
    public Task<CloudClosetResult<byte[]>> DownloadTextureAsync(string hash, CancellationToken cancellationToken)
        => DownloadUrlAsync($"{_baseAddress}/textures/{Uri.EscapeDataString(hash)}", cancellationToken);

    /// <summary>
    /// 按材质编号下载原始纹理。<paramref name="hash"/> 为空时先查 <c>/texture/{tid}</c> 取哈希。
    /// 实测 <c>/skinlib/list</c> 的行里没有 hash 字段，衣柜行也可能缺，空哈希会拼出 <c>/textures/</c> 拿到 404。
    /// </summary>
    public async Task<CloudClosetResult<byte[]>> DownloadTextureAsync(
        int tid,
        string? hash,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(hash))
        {
            return await DownloadTextureAsync(hash, cancellationToken).ConfigureAwait(false);
        }

        var resolved = await ResolveTextureHashAsync(tid, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded)
        {
            return CloudClosetResult<byte[]>.Fail(
                resolved.ErrorText!, resolved.IsNetworkError, resolved.IsAuthError);
        }

        return await DownloadTextureAsync(resolved.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查材质详情取哈希。<c>/texture/{tid}</c> 返回材质 JSON，里面的 hash 是 sha256。</summary>
    public async Task<CloudClosetResult<string>> ResolveTextureHashAsync(int tid, CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync($"/texture/{tid}", cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CloudClosetResult<string>.Fail(result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<string>.Fail(response.ErrorText ?? "材质详情读取失败。");
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var hash = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                    ? ReadString(data, "hash") ?? ReadString(root, "hash")
                    : ReadString(root, "hash");
            return string.IsNullOrWhiteSpace(hash)
                ? CloudClosetResult<string>.Fail("站点没有给出材质哈希，无法下载。")
                : CloudClosetResult<string>.Ok(hash);
        }
        catch (JsonException)
        {
            return CloudClosetResult<string>.Fail("材质详情不是有效的 JSON。");
        }
    }

    /// <summary>下载站点生成的预览图，列表缩略图与 3D 预览都用它。</summary>
    public Task<CloudClosetResult<byte[]>> DownloadPreviewAsync(int tid, CancellationToken cancellationToken)
        => DownloadUrlAsync($"{_baseAddress}/preview/{tid}", cancellationToken);

    /// <summary>把材质应用到角色。<paramref name="kind"/> 是 skin 或 cape。</summary>
    public async Task<CloudClosetResult<CloudOperationResult>> ApplyAsync(
        int pid,
        string kind,
        int tid,
        CancellationToken cancellationToken)
    {
        var normalizedKind = string.Equals(kind, "cape", StringComparison.OrdinalIgnoreCase) ? "cape" : "skin";
        var body = JsonSerializer.Serialize(new Dictionary<string, int> { [normalizedKind] = tid });
        var result = await SendWithCsrfAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{_baseAddress}/user/player/{pid}/textures")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            ReadJsonAsync,
            cancellationToken).ConfigureAwait(false);
        return ToBooleanResult(result, "材质应用");
    }

    /// <summary>清除角色身上的皮肤或披风。</summary>
    public async Task<CloudClosetResult<CloudOperationResult>> ClearAsync(
        int pid,
        string kind,
        CancellationToken cancellationToken)
    {
        var normalizedKind = string.Equals(kind, "cape", StringComparison.OrdinalIgnoreCase) ? "cape" : "skin";
        var body = JsonSerializer.Serialize(new { type = new[] { normalizedKind } });
        var result = await SendWithCsrfAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{_baseAddress}/user/player/{pid}/textures")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            ReadJsonAsync,
            cancellationToken).ConfigureAwait(false);
        return ToBooleanResult(result, "清除材质");
    }

    /// <summary>删除衣柜材质。调用方必须先确认这个 tid 是启动器自己上传并记录过的。</summary>
    public async Task<CloudClosetResult<CloudOperationResult>> DeleteTextureAsync(int tid, CancellationToken cancellationToken)
    {
        var result = await SendWithCsrfAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{_baseAddress}/texture/{tid}"),
            ReadJsonAsync,
            cancellationToken).ConfigureAwait(false);
        return ToBooleanResult(result, "删除材质");
    }

    /// <summary>把站点地址与账号组合成凭据键，避免不同站点/账号互相覆盖。</summary>
    public static string CredentialKey(string baseAddress, string username)
        => NormalizeBaseAddress(baseAddress).ToLowerInvariant() + "|" + username.Trim().ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static CloudClosetResult<CloudOperationResult> ToBooleanResult(
        CloudClosetResult<JsonResponse> result,
        string action)
    {
        if (!result.Succeeded)
        {
            return CloudClosetResult<CloudOperationResult>.Fail(result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<CloudOperationResult>.Fail(response.ErrorText);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (ReadCode(root) is { } code && code != 0)
            {
                return CloudClosetResult<CloudOperationResult>.Fail(
                    ReadMessage(root) ?? $"{action}被皮肤站拒绝。");
            }

            return CloudClosetResult<CloudOperationResult>.Ok(new CloudOperationResult(true));
        }
        catch (JsonException)
        {
            return CloudClosetResult<CloudOperationResult>.Fail($"{action}的响应不是有效的 JSON。");
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

    private static int? ReadNestedTid(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        if (ReadInt(data, "tid") is { } direct)
        {
            return direct;
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("data", out var nested)
            && ReadInt(nested, "tid") is { } wrapped)
        {
            return wrapped;
        }

        return null;
    }

    private static int? ReadCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code))
        {
            return null;
        }

        return code.ValueKind switch
        {
            JsonValueKind.Number when code.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(code.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in errors.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                        {
                            return text;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when value.GetString() == "1" => true,
            JsonValueKind.String when value.GetString() == "0" => false,
            _ => null,
        };
    }

    private async Task<CloudClosetResult<JsonResponse>> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        // 调用点既有绝对地址也有 "/user/player/list" 这种站内路径，而 _http 不设 BaseAddress：
        // HttpClient 收到相对 URI 会直接抛 InvalidOperationException，且不是 HttpRequestException，
        // 下面的 catch 兜不住。所以统一在这里补齐站址。
        var absoluteUrl = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.ToString() : _baseAddress + url;
        try
        {
            using var response = await _http
                .GetAsync(absoluteUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadResponse(response, body);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"皮肤站请求失败：{url}", ex);
            return CloudClosetResult<JsonResponse>.Fail("连不上皮肤站，检查网络或站点地址后重试。", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"皮肤站请求超时：{url}");
            return CloudClosetResult<JsonResponse>.Fail("皮肤站响应超时。", true);
        }
    }

    private async Task<CloudClosetResult<JsonResponse>> SendWithCsrfAsync(
        Func<HttpRequestMessage> requestFactory,
        Func<HttpResponseMessage, CancellationToken, Task<CloudClosetResult<JsonResponse>>> reader,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await EnsureCsrfAsync(force: attempt > 0, cancellationToken).ConfigureAwait(false);
            if (token.ErrorText is { } tokenError)
            {
                return CloudClosetResult<JsonResponse>.Fail(tokenError, token.IsNetworkError, token.IsAuthError);
            }

            using var request = requestFactory();
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(_csrfToken))
            {
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", _csrfToken);
            }

            try
            {
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode == 419 && attempt == 0)
                {
                    _csrfToken = null;
                    continue;
                }

                return await reader(response, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _log?.Warn($"皮肤站请求失败：{request.RequestUri}", ex);
                return CloudClosetResult<JsonResponse>.Fail("连不上皮肤站，检查网络或站点地址后重试。", true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log?.Warn($"皮肤站请求超时：{request.RequestUri}");
                return CloudClosetResult<JsonResponse>.Fail("皮肤站响应超时。", true);
            }
        }

        return CloudClosetResult<JsonResponse>.Fail("皮肤站登录状态已过期，请重新登录。", isAuthError: true);
    }

    private async Task<CloudClosetResult<JsonResponse>> EnsureCsrfAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force && !string.IsNullOrWhiteSpace(_csrfToken))
        {
            return CloudClosetResult<JsonResponse>.Ok(JsonResponse.Empty);
        }

        try
        {
            using var response = await _http
                .GetAsync(_baseAddress + "/auth/login", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CloudClosetResult<JsonResponse>.Fail(
                    response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                        ? "皮肤站登录页发生了重定向，可能已经登录或站点地址不对。"
                        : $"打开皮肤站登录页返回 HTTP {(int)response.StatusCode}。");
            }

            var match = CsrfMetaNameFirst.Match(html);
            if (!match.Success)
            {
                match = CsrfMetaContentFirst.Match(html);
            }

            if (match.Success)
            {
                _csrfToken = WebUtility.HtmlDecode(match.Groups[1].Value);
            }
            else if (_cookies?.GetCookies(new Uri(_baseAddress))["XSRF-TOKEN"] is { } cookie)
            {
                _csrfToken = Uri.UnescapeDataString(cookie.Value);
            }
            else
            {
                return CloudClosetResult<JsonResponse>.Fail(
                    "登录页没有 CSRF token，站点可能不是 Blessing Skin，或登录页被拦截。");
            }

            return CloudClosetResult<JsonResponse>.Ok(JsonResponse.Empty);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"读取皮肤站登录页失败：{_baseAddress}", ex);
            return CloudClosetResult<JsonResponse>.Fail("连不上皮肤站，检查网络或站点地址后重试。", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CloudClosetResult<JsonResponse>.Fail("皮肤站响应超时。", true);
        }
    }

    private async Task<CloudClosetResult<byte[]>> DownloadUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CloudClosetResult<byte[]>.Fail(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found
                        ? "登录状态已过期，请重新登录。"
                        : $"素材下载返回 HTTP {(int)response.StatusCode}。");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0
                ? CloudClosetResult<byte[]>.Fail("素材下载返回了空文件。")
                : CloudClosetResult<byte[]>.Ok(bytes);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"素材下载失败：{url}", ex);
            return CloudClosetResult<byte[]>.Fail("连不上皮肤站，检查网络后重试。", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CloudClosetResult<byte[]>.Fail("素材下载超时。", true);
        }
    }

    private static async Task<CloudClosetResult<JsonResponse>> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ReadResponse(response, body);
    }

    private static CloudClosetResult<JsonResponse> ReadResponse(HttpResponseMessage response, string body)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return CloudClosetResult<JsonResponse>.Ok(new JsonResponse(body, true, string.Empty));
        }

        var message = ExtractMessage(body);
        if ((int)response.StatusCode == 419
            || response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found)
        {
            return CloudClosetResult<JsonResponse>.Fail(
                message ?? ((int)response.StatusCode == 419 || response.StatusCode == HttpStatusCode.Found
                    ? "登录状态已过期，请重新登录。"
                    : "没有权限执行这个操作，请确认登录状态。"),
                isAuthError: true);
        }

        return CloudClosetResult<JsonResponse>.Fail(message ?? response.StatusCode switch
        {
            HttpStatusCode.RequestTimeout => "皮肤站响应超时。",
            HttpStatusCode.NotFound => "站点上找不到这个资源。",
            HttpStatusCode.TooManyRequests => "操作太频繁，稍后再试。",
            >= HttpStatusCode.InternalServerError => $"皮肤站暂时不可用（HTTP {(int)response.StatusCode}）。",
            _ => $"皮肤站返回 HTTP {(int)response.StatusCode}。",
        });
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadMessage(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record JsonResponse(string Body, bool IsSuccess, string ErrorText)
    {
        public static JsonResponse Empty { get; } = new(string.Empty, true, string.Empty);
    }
}
