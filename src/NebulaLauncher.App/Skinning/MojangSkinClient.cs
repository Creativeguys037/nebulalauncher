using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 官方（Microsoft 账户）端点：上传皮肤、重置皮肤、读取档案、切换/隐藏已拥有的披风。
///
/// <para>
/// 四条事实决定了这里的形状：
/// <c>variant</c> 只认 <c>classic</c>/<c>slim</c>（皮肤站经典体型传空串，照抄会 400）；
/// 官方<b>没有</b>披风上传接口，但可以切换账户已经拥有的披风（票 #61）；
/// 401 是 Minecraft access token 过期，界面要引导去账户页刷新，不能自动重试；
/// 披风切换对未拥有/已下架的披风可能返回 400 且错误体为空，需要本地兜底文案。
/// </para>
///
/// <para>
/// 纯 C#：不引用任何 Avalonia 类型，<see cref="HttpClient"/> 走注入，
/// 测试用假 handler 就能覆盖全部请求形状与失败分支。
/// </para>
/// </summary>
public sealed class MojangSkinClient : IDisposable
{
    /// <summary>Minecraft 服务 API 根地址。</summary>
    public const string DefaultBaseAddress = "https://api.minecraftservices.com";

    /// <summary>披风切换/隐藏被服务端拒绝（400/404）时的兜底文案：官方错误体可能为空。</summary>
    private const string CapeRejectedText = "这个披风不在当前账户的已拥有列表里，请刷新后重试。";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly string _baseAddress;

    private readonly IAppLog? _log;

    public MojangSkinClient(HttpClient? http = null, string baseAddress = DefaultBaseAddress, IAppLog? log = null)
    {
        _ownsHttp = http is null;
        _http = http ?? Shared;
        _baseAddress = string.IsNullOrWhiteSpace(baseAddress) ? DefaultBaseAddress : baseAddress.TrimEnd('/');
        _log = log;
    }

    public string BaseAddress => _baseAddress;

    /// <summary>官方 <c>variant</c> 取值：Steve → <c>classic</c>，Alex → <c>slim</c>。</summary>
    public static string VariantOf(SkinModelKind model) => model switch
    {
        SkinModelKind.Alex => "slim",
        _ => "classic",
    };

    /// <summary>
    /// 把一张 PNG 传到当前账户的官方档案上。<paramref name="accessToken"/> 是账户模型里的
    /// <c>Account.AccessToken</c>（Minecraft access token，不是微软的 token）。
    /// </summary>
    public async Task<SkinUploadResult> UploadSkinAsync(
        string accessToken,
        SkinModelKind model,
        byte[] png,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseAddress}/minecraft/profile/skins");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(VariantOf(model)), "variant");

        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");

        request.Content = form;

        return await SendAsync(request, "上传皮肤", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把当前账户的皮肤重置成官方默认皮肤（没有请求体，服务端自己决定默认长什么样）。</summary>
    public async Task<SkinUploadResult> ResetSkinAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseAddress}/minecraft/profile/skins/active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await SendAsync(request, "重置皮肤", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 当前账户的官方档案：已拥有的披风（含哪一件正在使用）与当前皮肤图地址。
    /// 披风列表只在内存里，进页面/切账户/手动刷新时重新拉（票 #61）。
    /// </summary>
    public async Task<MojangProfileResult> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseAddress}/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var (result, body) = await SendCoreAsync(request, "读取官方档案", cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return MojangProfileResult.Fail(
                result.ErrorText ?? "读取官方档案失败。",
                result.IsAuthError,
                result.IsRetryable);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return MojangProfileResult.Fail("官方档案返回了空响应。");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return MojangProfileResult.Ok(ParseProfile(document.RootElement));
        }
        catch (JsonException)
        {
            return MojangProfileResult.Fail("官方档案返回的 JSON 无法解析。");
        }
    }

    /// <summary>把账户已拥有的某件披风设为使用中。</summary>
    public async Task<SkinUploadResult> SetActiveCapeAsync(
        string accessToken,
        string capeId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{_baseAddress}/minecraft/profile/capes/active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new SetCapeRequest(capeId)),
            Encoding.UTF8,
            "application/json");

        return await SendAsync(request, "切换披风", cancellationToken, CapeRejectedText).ConfigureAwait(false);
    }

    /// <summary>隐藏当前披风。官方语义是清掉 active cape，账户里的披风仍然保留。</summary>
    public async Task<SkinUploadResult> HideCapeAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseAddress}/minecraft/profile/capes/active");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await SendAsync(request, "隐藏披风", cancellationToken, CapeRejectedText).ConfigureAwait(false);
    }

    private async Task<SkinUploadResult> SendAsync(
        HttpRequestMessage request,
        string action,
        CancellationToken cancellationToken,
        string? rejectedText = null)
    {
        var (result, _) = await SendCoreAsync(request, action, cancellationToken, rejectedText).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 统一的请求收尾：成功时把响应体一并带回（读档案要解析它，上传/切换只看状态码）。
    /// 失败映射集中在这里，几个用例才不会各写一套。
    /// </summary>
    private async Task<(SkinUploadResult Result, string? Body)> SendCoreAsync(
        HttpRequestMessage request,
        string action,
        CancellationToken cancellationToken,
        string? rejectedText = null)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (SkinUploadResult.Ok(), body);
            }

            // 401 是 Minecraft access token 自己过期：重发同一个 token 还是 401，
            // 所以这里既不打重试标记，也不再发第二次请求，只把人引到账户页。
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log?.Warn($"官方{action}被拒绝：access token 过期或无效（401）。");
                return (SkinUploadResult.Fail("登录已过期，请到账户页刷新令牌。", isAuthError: true), null);
            }

            // 400 几乎都是尺寸/格式不对（官方只收 64×64 与 64×32 的 PNG）。
            // 披风用例的 400/404 另有含义（不在已拥有列表里），由调用方传入兜底文案。
            // 服务端的 errorMessage 太空泛时，本地那句才是用户真正能照做的信息。
            if (response.StatusCode == HttpStatusCode.BadRequest
                || (rejectedText is not null && response.StatusCode == HttpStatusCode.NotFound))
            {
                var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                if (rejectedText is not null)
                {
                    return (SkinUploadResult.Fail(
                        string.IsNullOrWhiteSpace(detail) ? rejectedText : $"{rejectedText}（服务端：{detail}）"), null);
                }

                const string sizeHint = "皮肤尺寸必须是 64×64 或 64×32，且必须是 PNG 格式。";
                return (SkinUploadResult.Fail(
                    string.IsNullOrWhiteSpace(detail) ? sizeHint : $"{sizeHint}（服务端：{detail}）"), null);
            }

            // 429 / 5xx：服务端侧的问题，同一个请求等一会儿再发就可能成功。
            // 这里只打「可重试」标记，重发与限速留给上层（本客户端一次调用只发一次）。
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                _log?.Warn($"官方{action}暂时不可用：{(int)response.StatusCode} {response.StatusCode}。");
                return (SkinUploadResult.Fail("无网络：官方服务暂时不可用，请稍后重试。", isRetryable: true), null);
            }

            return (SkinUploadResult.Fail($"{action}返回 {(int)response.StatusCode} {response.StatusCode}。"), null);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"官方{action}请求失败（连不上）。", ex);
            return (SkinUploadResult.Fail("无网络，请检查网络后重试。", isRetryable: true), null);
        }

        // 超时对用户来说与断网同一种体验。用户主动取消（token 已触发）不在此列——
        // 那种情况要按取消向上抛，否则界面会把「我取消了」画成「网络错误」。
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"官方{action}请求超时。");
            return (SkinUploadResult.Fail("无网络：请求超时，请重试。", isRetryable: true), null);
        }
    }

    /// <summary>
    /// 只取这一屏要用的字段：<c>capes[]</c> 的 id/state/url/alias 与第一张 <c>skins[].url</c>。
    /// 缺字段的条目跳过而不是让整份档案失败——官方档案里 <c>skins</c> 可能是空数组。
    /// </summary>
    private static MojangProfile ParseProfile(JsonElement root)
    {
        var capes = new List<MojangCape>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("capes", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var state = ReadString(item, "state");
                capes.Add(new MojangCape(
                    id,
                    string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase),
                    ReadString(item, "url"),
                    ReadString(item, "alias")));
            }
        }

        string? skinUrl = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("skins", out var skins)
            && skins.ValueKind == JsonValueKind.Array)
        {
            foreach (var skin in skins.EnumerateArray())
            {
                if (skin.ValueKind == JsonValueKind.Object && ReadString(skin, "url") is { } url)
                {
                    skinUrl = url;
                    break;
                }
            }
        }

        return new MojangProfile(capes, skinUrl);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    /// <summary>
    /// 读官方错误体的 <c>errorMessage</c>（形状是 <c>{"path":"…","errorMessage":"…"}</c>）。
    /// 这里用 <see cref="JsonDocument"/> 而不是反序列化：只想拿一句给用户看的文本，
    /// 不值得为它造一个落盘类型（App 层读响应的既有写法同此，见 <see cref="SkinLibraryClient"/>）。
    /// </summary>
    private static async Task<string?> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("errorMessage", out var message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
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
}

/// <summary>PUT /minecraft/profile/capes/active 的请求体。具名类型避免匿名类型在裁剪/AOT 下的坑。</summary>
internal sealed record SetCapeRequest([property: JsonPropertyName("capeId")] string CapeId);
