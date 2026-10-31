using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 皮肤站（authlib-injector / Yggdrasil）的材质读写端点：上传皮肤、上传披风、删除材质。
///
/// <para>
/// <b>地址形状</b>以《Yggdrasil 服务端技术规范》「材质上传」节为准：规范里写的
/// <c>PUT /api/user/profile/{uuid}/{textureType}</c> 是<b>相对 API 根</b>的，与同一份规范里的
/// <c>POST /api/profiles/minecraft</c>、<c>GET /sessionserver/…</c> 同一套拼法。
/// 2026-11-01 复核过三条证据（探测记录见 <c>docs/research/littleskin-texture-write-contract.md</c> §6）：
/// ① LittleSkin 自己的插件 <c>MUAlliance/yggdrasil-connect</c> 把 <c>Route::prefix('api/user/profile')</c>
///（<c>PUT {uuid}/{type}</c>）挂在 <c>Route::prefix('api/yggdrasil')</c> 之下——<c>{apiRoot}</c> 本身就以
/// <c>/api/yggdrasil</c> 结尾，所以真实 URL 是 <c>https://站点/api/yggdrasil/api/user/profile/{uuid}/skin</c>；
/// ② <c>skin.vlssu.com</c> 与 <c>mcskin.cn</c> 上这条路径对假令牌回 401
/// <c>{"error":"UnauthorizedException","errorMessage":"Bearer token expired…"}</c>，<c>OPTIONS</c> 回
/// <c>Allow: PUT, DELETE</c>（路由存在，只是令牌不对）；③ <c>littleskin.cn</c> 自己<b>没装这个插件</b>——
/// 它的 <c>/api/yggdrasil</c> 是另一套 Express 网关，同一路径带不带令牌都是 404
/// <c>{"message":"Cannot PUT /api/user/profile/…","error":"Not Found","statusCode":404}</c>。
/// 所以拿 littleskin 当「能上传」的样板是错读，它的材质写入走 Blessing Skin OAuth；
/// 客户端对这类 404 的话术见 <c>SendAsync</c> 的 404 分支。
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

        // 服务端也会校验，但它回的是笼统的 400；本地先拦一道，
        // 用户拿到的就是「必须恰好 64×64」这种能照着改的话，还省一次往返。
        var localModel = model == SkinModelKind.Alex ? LocalSkinModel.Slim : LocalSkinModel.Classic;
        if (ValidatePng(png, localModel) is { } error)
        {
            return Task.FromResult(SkinUploadResult.Fail(error));
        }

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

        // 披风的尺寸规则与皮肤不同（64×32 或 22×17 的整数倍），LocalSkinFormat 只覆盖皮肤，
        // 所以这里只做「是 PNG」这一道，尺寸交给服务端。
        if (!LocalSkinFormat.TryReadPngSize(png, out _, out _))
        {
            return Task.FromResult(SkinUploadResult.Fail("披风必须是 PNG 图片。"));
        }

        return UploadAsync(account, "cape", model: null, png, "上传披风", cancellationToken);
    }

    /// <summary>
    /// 读当前角色的材质能力（<c>uploadableTextures</c>），决定披风按钮要不要点亮。
    ///
    /// <para>
    /// 返回 <c>null</c> 表示<b>没读到</b>（地址缺失、网络失败、非 2xx、不是 JSON）——
    /// 与「读到了，但什么都不允许」必须分开：前者调用方应保持原有判断，
    /// 后者才是真的没权限。两者混为一谈会让一次断网把用户的披风权限「收走」。
    /// </para>
    ///
    /// <para>
    /// 用 <see cref="SkinProfileParser.BuildProfileUrl"/> 拼地址：它会按账户类型决定
    /// 走官方会话服务器还是第三方站点，这里只关心第三方。
    /// </para>
    /// </summary>
    public async Task<SkinCapabilities?> GetCapabilitiesAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (SkinProfileParser.BuildProfileUrl(account) is not { } url)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return SkinProfileParser.ParseCapabilities(json);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn("读取皮肤站角色材质能力失败（连不上）。", ex);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn("读取皮肤站角色材质能力超时。");
            return null;
        }
    }

    /// <summary>本地前置校验：文件头能读出宽高，且尺寸符合所选体型。返回 null 表示通过。</summary>
    private static string? ValidatePng(byte[] png, LocalSkinModel model)
    {
        if (!LocalSkinFormat.TryReadPngSize(png, out var width, out var height))
        {
            return "这张图不是有效的 PNG，没法当皮肤用。";
        }

        return LocalSkinFormat.Validate(width, height, model);
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

            // 404 有两种来源，要说的话完全不同：一是站点根本没实现这套材质路由
            //（littleskin 现在就是：`/api/yggdrasil` 由 Express 网关接管，直接 404），
            // 二是 Yggdrasil 服务端在说「找不到这个角色」。两种都重试不出结果，
            // 但一个要人去站点网页操作，另一个应当把服务端原话交出去。
            // 判别看错误体形状，不看状态码：网关体带 `statusCode`（Express / Nginx 的惯例），
            // Yggdrasil 体带 `error` / `errorMessage`（`errorMessage` 规范上可以省）。
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var (detail, isYggdrasilError) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                if (isYggdrasilError && !string.IsNullOrWhiteSpace(detail))
                {
                    _log?.Warn($"皮肤站{action}返回 404：{detail}");
                    return SkinUploadResult.Fail($"皮肤站返回 404：{detail}");
                }

                // 同一个 SendAsync 既管上传也管删除：话术里的动作必须跟着请求方法走，
                // 否则删除材质的人会被指去「网页上传」。
                var verb = request.Method == HttpMethod.Delete ? "删除" : "上传";
                _log?.Warn($"皮肤站{action}返回 404：站点没有提供材质{verb}接口。");
                return SkinUploadResult.Fail($"这个皮肤站没有提供材质{verb}接口，请到皮肤站网页{verb}。");
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
    /// </summary>
    private static async Task<string?> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false)).Detail;

    /// <summary>
    /// 读错误体，并判断它是不是 Yggdrasil 的形状（<c>{error, errorMessage, cause}</c>）。
    ///
    /// <para>
    /// 判别只认这两样：<c>errorMessage</c> 字符串（规范里的长文案，可以省略），或者一个不带
    /// <c>statusCode</c> 的 <c>error</c> 字符串（如 <c>ForbiddenOperationException</c>）。
    /// 网关自己的错误体（Express 的 <c>{message, error, statusCode}</c>、Nginx 的 HTML）里
    /// <c>error</c> 只是 HTTP 状态的复述且总带 <c>statusCode</c>，所以不会被当成 Yggdrasil 的话。
    /// </para>
    ///
    /// <para>
    /// 用 <see cref="JsonDocument"/> 而不是反序列化：只想拿一句给用户看的文本，
    /// 不值得为它造一个落盘类型；HTML 与空体解析必然失败，按「不是 Yggdrasil 体」处理。
    /// </para>
    /// </summary>
    private static async Task<(string? Detail, bool IsYggdrasilError)> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, false);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, false);
            }

            if (root.TryGetProperty("errorMessage", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return (message.GetString()?.Trim(), true);
            }

            if (!root.TryGetProperty("statusCode", out _)
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return (error.GetString()?.Trim(), true);
            }

            return (null, false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
        catch (HttpRequestException)
        {
            return (null, false);
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
