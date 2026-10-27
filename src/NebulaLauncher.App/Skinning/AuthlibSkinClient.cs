using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 皮肤站（authlib-injector / Yggdrasil）的材质读写端点：上传皮肤、上传披风、删除材质。
///
/// <para>
/// <b>地址形状</b>以《Yggdrasil 服务端技术规范》「材质上传」节与参考实现
/// <c>yggdrasil-mock</c>（<c>@PutMapping("/api/user/profile/{uuid}/{textureType}")</c>）为准：
/// 规范里写的 <c>PUT /api/user/profile/{uuid}/{textureType}</c> 是<b>相对 API 根</b>的，
/// 与同一份规范里的 <c>POST /api/profiles/minecraft</c>、<c>GET /sessionserver/…</c> 同一套拼法。
/// 2026-10-27 对 littleskin.cn 实测：<c>POST {apiRoot}/api/profiles/minecraft</c> 返回 200、
/// <c>PUT {apiRoot}/user/profile/{uuid}/skin</c> 返回 Yggdrasil 风格的 JSON 404，
/// 而站点根下的同类路径只返回 HTML 404，故取 <c>{apiRoot}/api/user/profile/…</c>。
/// </para>
///
/// <para>
/// <b>请求体</b>是 <c>multipart/form-data</c>：<c>file</c>（PNG）必填，
/// <c>model</c> 仅皮肤需要且<b>只认 <c>slim</c> 与空串</b>（空串＝Steve）——
/// 官方换肤那个 <c>classic</c> 在这里是未知值。成功返回 <c>204 No Content</c>。
/// </para>
///
/// <para>
/// 纯 C#：不引用任何 Avalonia 类型，<see cref="HttpClient"/> 走注入，
/// 测试用假 handler 就能覆盖全部请求形状与失败分支。
/// </para>
/// </summary>
public sealed class AuthlibSkinClient : IDisposable
{
    /// <summary>皮肤站路径里 <c>/api/user/profile</c> 这一段（相对 API 根）。</summary>
    private const string TexturePath = "api/user/profile";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;

    private readonly bool _ownsHttp;

    private readonly IAppLog? _log;

    public AuthlibSkinClient(HttpClient? http = null, IAppLog? log = null)
    {
        _ownsHttp = http is null;
        _http = http ?? Shared;
        _log = log;
    }

    /// <summary>
    /// 材质端点地址。API 根为空、UUID 为空或材质类型不在 <c>skin</c>/<c>cape</c> 里时返回 <c>null</c>
    /// ——这几个都是「本地就能判定不可能成功」的输入，不该发请求出去。
    /// </summary>
    public static string? BuildTextureUrl(string? apiRoot, string? uuid, string textureType)
    {
        if (string.IsNullOrWhiteSpace(apiRoot)
            || string.IsNullOrWhiteSpace(uuid)
            || !IsTextureType(textureType))
        {
            return null;
        }

        return $"{apiRoot.Trim().TrimEnd('/')}/{TexturePath}/{uuid.Trim()}/{textureType}";
    }

    private static bool IsTextureType(string? textureType) =>
        string.Equals(textureType, "skin", StringComparison.Ordinal)
        || string.Equals(textureType, "cape", StringComparison.Ordinal);

    /// <summary>
    /// 把一张 PNG 传到当前角色上。<paramref name="accessToken"/> 是账户模型里的
    /// <c>Account.AccessToken</c>（Yggdrasil <c>accessToken</c>，不是微软的 token）。
    /// </summary>
    public Task<SkinUploadResult> UploadSkinAsync(
        Account account,
        SkinModelKind model,
        byte[] png,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(png);

        return UploadAsync(account, "skin", ModelFieldOf(model), png, "上传皮肤", cancellationToken);
    }

    /// <summary>
    /// 把一张 PNG 作为披风传上去。规范里 <c>model</c> 标注「仅用于皮肤」，披风不带该字段。
    /// </summary>
    public Task<SkinUploadResult> UploadCapeAsync(
        Account account,
        byte[] png,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(png);

        return UploadAsync(account, "cape", model: null, png, "上传披风", cancellationToken);
    }

    /// <summary>清除皮肤，服务端会把它恢复成默认皮肤。成功返回 <c>204</c>。</summary>
    public Task<SkinUploadResult> DeleteSkinAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        return DeleteAsync(account, "skin", "删除皮肤", cancellationToken);
    }

    /// <summary>清除披风。成功返回 <c>204</c>。</summary>
    public Task<SkinUploadResult> DeleteCapeAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        return DeleteAsync(account, "cape", "删除披风", cancellationToken);
    }

    /// <summary>
    /// 皮肤站 <c>model</c> 的取值：Alex → <c>slim</c>，Steve → <b>空串</b>。
    ///
    /// 这里刻意不写成官方那套 <c>classic</c>：规范只允许 <c>slim</c> 与空字符串，
    /// 参考实现把除 <c>slim</c> 之外的一切都当 Steve，但真站点可能直接判为非法值。
    /// </summary>
    public static string ModelFieldOf(SkinModelKind model) =>
        model == SkinModelKind.Alex ? "slim" : string.Empty;

    private async Task<SkinUploadResult> UploadAsync(
        Account account,
        string textureType,
        string? model,
        byte[] png,
        string action,
        CancellationToken cancellationToken)
    {
        var url = BuildTextureUrl(account.AuthServerUrl, account.Uuid, textureType);
        if (url is null)
        {
            return SkinUploadResult.Fail(account.Type != AccountType.Authlib
                ? "该账户不是皮肤站账户，不能上传材质。"
                : "账户缺少验证服务器地址或角色 UUID，请到账户页重新登录。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken ?? string.Empty);

        using var form = new MultipartFormDataContent();
        if (model is not null)
        {
            form.Add(new StringContent(model), "model");
        }

        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", textureType + ".png");

        request.Content = form;

        return await SendAsync(request, action, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SkinUploadResult> DeleteAsync(
        Account account,
        string textureType,
        string action,
        CancellationToken cancellationToken)
    {
        var url = BuildTextureUrl(account.AuthServerUrl, account.Uuid, textureType);
        if (url is null)
        {
            return SkinUploadResult.Fail(account.Type != AccountType.Authlib
                ? "该账户不是皮肤站账户，不能管理材质。"
                : "账户缺少验证服务器地址或角色 UUID，请到账户页重新登录。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken ?? string.Empty);

        return await SendAsync(request, action, cancellationToken).ConfigureAwait(false);
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

            // 401 是 Yggdrasil accessToken 自己过期：重发同一个令牌还是 401，
            // 所以这里既不打重试标记，也不再发第二次请求，只把人引到账户页。
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log?.Warn($"皮肤站{action}被拒绝：access token 过期或无效（401）。");
                return SkinUploadResult.Fail("登录已过期，请到账户页刷新令牌。", isAuthError: true);
            }

            // 400 通常是尺寸或格式不对。服务端那句往往太笼统，
            // 本地这句才是用户能照着做的那条信息。
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                const string sizeHint = "材质尺寸或格式不对：皮肤须为 PNG，尺寸为 64×64 或 64×32 的整数倍；披风须为 64×32 或 22×17 的整数倍。";
                var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                return SkinUploadResult.Fail(
                    string.IsNullOrWhiteSpace(detail) ? sizeHint : $"{sizeHint}（服务端：{detail}）");
            }

            // 429 / 5xx：服务端侧的问题，同一个请求等一会儿再发就可能成功。
            // 这里只打「可重试」标记，重发与限速留给上层（本客户端一次调用只发一次）。
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                _log?.Warn($"皮肤站{action}暂时不可用：{(int)response.StatusCode} {response.StatusCode}。");
                return SkinUploadResult.Fail("无网络：皮肤站暂时不可用，请稍后重试。", isRetryable: true);
            }

            // 403 在 Yggdrasil 里有两层意思：令牌与角色不匹配，或该角色不允许上传这种材质
            //（`uploadableTextures` 不含它）。后者要人去站点网页改，重试没用。
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _log?.Warn($"皮肤站{action}被禁止：403（令牌与角色不匹配，或角色不允许上传该材质）。");
                return SkinUploadResult.Fail("这个角色不允许上传该材质，或登录状态与角色不匹配，请到皮肤站网页确认。");
            }

            return SkinUploadResult.Fail($"{action}返回 {(int)response.StatusCode} {response.StatusCode}。");
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"皮肤站{action}请求失败（连不上）。", ex);
            return SkinUploadResult.Fail("无网络，请检查网络后重试。", isRetryable: true);
        }

        // 超时对用户来说与断网同一种体验。用户主动取消（token 已触发）不在此列——
        // 那种情况要按取消向上抛，否则界面会把「我取消了」画成「网络错误」。
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"皮肤站{action}请求超时。");
            return SkinUploadResult.Fail("无网络：请求超时，请重试。", isRetryable: true);
        }
    }

    /// <summary>
    /// 读 Yggdrasil 错误体的 <c>errorMessage</c>（形状是 <c>{error, errorMessage, cause}</c>）。
    /// 用 <see cref="JsonDocument"/> 而不是反序列化：只想拿一句给用户看的文本，
    /// 不值得为它造一个落盘类型。
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
