using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 官方（Microsoft 账户）换肤端点：只有「上传皮肤」与「重置为默认皮肤」两件事。
///
/// <para>
/// 三条来自票 #44 的事实决定了这里的形状：
/// <c>variant</c> 只认 <c>classic</c>/<c>slim</c>（皮肤站经典体型传空串，照抄会 400）；
/// 官方<b>没有</b>披风上传接口，所以这里也不该长出一个；
/// 401 是 Minecraft access token 过期，界面要引导去账户页刷新，不能自动重试。
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

    private async Task<SkinUploadResult> SendAsync(
        HttpRequestMessage request,
        string action,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return SkinUploadResult.Ok();
            }

            // 401 是 Minecraft access token 自己过期：重发同一个 token 还是 401，
            // 所以这里既不打重试标记，也不再发第二次请求，只把人引到账户页。
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log?.Warn($"官方{action}被拒绝：access token 过期或无效（401）。");
                return SkinUploadResult.Fail("登录已过期，请到账户页刷新令牌。", isAuthError: true);
            }

            // 400 几乎都是尺寸/格式不对（官方只收 64×64 与 64×32 的 PNG）。
            // 服务端的 errorMessage 太空泛时，本地这句才是用户真正能照做的信息。
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                const string sizeHint = "皮肤尺寸必须是 64×64 或 64×32，且必须是 PNG 格式。";
                var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                return SkinUploadResult.Fail(
                    string.IsNullOrWhiteSpace(detail) ? sizeHint : $"{sizeHint}（服务端：{detail}）");
            }

            // 429 / 5xx：服务端侧的问题，同一个请求等一会儿再发就可能成功。
            // 这里只打「可重试」标记，重发与限速留给上层（本客户端一次调用只发一次）。
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                _log?.Warn($"官方{action}暂时不可用：{(int)response.StatusCode} {response.StatusCode}。");
                return SkinUploadResult.Fail("无网络：官方服务暂时不可用，请稍后重试。", isRetryable: true);
            }

            return SkinUploadResult.Fail($"{action}返回 {(int)response.StatusCode} {response.StatusCode}。");
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"官方{action}请求失败（连不上）。", ex);
            return SkinUploadResult.Fail("无网络，请检查网络后重试。", isRetryable: true);
        }

        // 超时对用户来说与断网同一种体验。用户主动取消（token 已触发）不在此列——
        // 那种情况要按取消向上抛，否则界面会把「我取消了」画成「网络错误」。
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"官方{action}请求超时。");
            return SkinUploadResult.Fail("无网络：请求超时，请重试。", isRetryable: true);
        }
    }

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
