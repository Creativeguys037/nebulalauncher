using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NebulaLauncher.Skinning;

/// <summary>设备代码流的一次授权挑战：给用户看的代码 + 轮询要用的 device_code。</summary>
public sealed record CloudDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

/// <summary>OAuth 令牌。刷新时整组替换，旧 refresh token 立即作废。</summary>
public sealed record CloudOAuthToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>OAuth 账号的展示名。站点只给昵称，没有用户名。</summary>
public sealed record CloudOAuthUser(int Uid, string Nickname);

/// <summary>
/// <see cref="CloudClosetClient"/> 的 OAuth 设备代码流部分：免密登录、Bearer 调用、refresh。
///
/// 与网页会话并存：<see cref="IsOAuthSession"/> 为 true 时，角色/衣柜/应用/清除走 <c>/api</c> + Bearer；
/// 上传 PNG 与删除材质仍然只有网页会话能做，OAuth 模式下明确拒绝，不假装成功。
/// 站点契约见 docs/research/littleskin-oauth-device-flow-20261102.md。
/// </summary>
public sealed partial class CloudClosetClient
{
    /// <summary>LittleSkin 上「Crimson」应用的客户端 ID。</summary>
    public const string OAuthClientId = "1674";

    /// <summary>
    /// 设备码要的权限：读账号（拿昵称存凭据）、读写角色材质、读衣柜、离线刷新。
    /// 多要 <c>Closet.Read</c> 是因为免密登录后仍要列出衣柜让用户挑已有材质。
    /// </summary>
    public const string OAuthScope = "openid User.Read Player.ReadWrite Closet.Read offline_access";

    private const string LittleSkinOAuthIssuer = "https://open.littleskin.cn";

    private string? _oauthAccessToken;

    private string? _oauthRefreshToken;

    private DateTimeOffset _oauthExpiresAt;

    /// <summary>刷新成功后触发，调用方据此把新令牌写回本机凭据库。</summary>
    public event EventHandler<CloudOAuthToken>? OAuthTokenRefreshed;

    /// <summary>当前会话是不是 OAuth 免密登录。false 表示密码网页会话或未登录。</summary>
    public bool IsOAuthSession => _oauthAccessToken is not null;

    /// <summary>
    /// 设备码流的站点根地址。littleskin.cn 的 OAuth 在 open.littleskin.cn；
    /// 其它 Blessing Skin 站点按同源处理（是否支持设备码由站点自己决定）。
    /// </summary>
    public string OAuthIssuer => _baseAddress.Contains("littleskin.cn", StringComparison.OrdinalIgnoreCase)
        ? LittleSkinOAuthIssuer
        : _baseAddress;

    /// <summary>把已保存的令牌装回客户端，用于启动时自动恢复免密登录。</summary>
    public void UseOAuthToken(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            ClearOAuthSession();
            return;
        }

        _oauthAccessToken = accessToken.Trim();
        _oauthRefreshToken = refreshToken?.Trim() ?? string.Empty;
        _oauthExpiresAt = expiresAt;
        _authenticated = true;
    }

    /// <summary>丢掉内存里的 OAuth 会话。本机保存的令牌由 ViewModel 决定留不留。</summary>
    public void ClearOAuthSession()
    {
        _oauthAccessToken = null;
        _oauthRefreshToken = null;
        _oauthExpiresAt = default;
    }

    /// <summary>第一步：向站点要一对 user_code / device_code。</summary>
    public async Task<CloudClosetResult<CloudDeviceCode>> StartDeviceLoginAsync(CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = OAuthClientId,
            ["scope"] = OAuthScope,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthIssuer + "/oauth/device_code")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return CloudClosetResult<CloudDeviceCode>.Fail(DescribeOAuthError(body, response.StatusCode));
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var deviceCode = ReadString(root, "device_code");
            var userCode = ReadString(root, "user_code");
            if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode))
            {
                return CloudClosetResult<CloudDeviceCode>.Fail("授权服务器没有返回 device_code / user_code。");
            }

            var verification = ReadString(root, "verification_uri") ?? OAuthIssuer + "/oauth/link";
            var complete = ReadString(root, "verification_uri_complete") ?? verification;
            return CloudClosetResult<CloudDeviceCode>.Ok(new CloudDeviceCode(
                deviceCode,
                userCode,
                verification,
                complete,
                ReadInt(root, "expires_in") ?? 300,
                Math.Max(1, ReadInt(root, "interval") ?? 5)));
        }
        catch (JsonException)
        {
            return CloudClosetResult<CloudDeviceCode>.Fail("授权服务器返回的不是有效 JSON。");
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"请求设备代码失败：{OAuthIssuer}", ex);
            return CloudClosetResult<CloudDeviceCode>.Fail("连不上 LittleSkin 授权服务器，检查网络后重试。", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CloudClosetResult<CloudDeviceCode>.Fail("LittleSkin 授权服务器响应超时。", true);
        }
    }

    /// <summary>
    /// 第二步：按 interval 轮询 token 端点，直到用户授权、拒绝或代码过期。
    /// 调用方负责展示 <paramref name="code"/> 里的 user_code 并打开浏览器。
    /// </summary>
    public async Task<CloudClosetResult<CloudOAuthToken>> WaitForDeviceLoginAsync(
        CloudDeviceCode code,
        CancellationToken cancellationToken)
    {
        var interval = Math.Max(1, code.Interval);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, code.ExpiresIn));

        while (true)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return CloudClosetResult<CloudOAuthToken>.Fail("授权码已过期，请重新发起免密登录。");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            var response = await RequestTokenAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = OAuthClientId,
                    ["device_code"] = code.DeviceCode,
                },
                cancellationToken).ConfigureAwait(false);

            if (response.Token is { } token)
            {
                ApplyToken(token);
                return CloudClosetResult<CloudOAuthToken>.Ok(token);
            }

            if (response.IsNetworkError)
            {
                return CloudClosetResult<CloudOAuthToken>.Fail(response.ErrorText ?? "连不上 LittleSkin。", true);
            }

            switch (response.ErrorCode)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    return CloudClosetResult<CloudOAuthToken>.Fail("授权码已过期，请重新发起免密登录。");
                case "access_denied":
                    return CloudClosetResult<CloudOAuthToken>.Fail("你在授权页拒绝了这次登录。");
                default:
                    return CloudClosetResult<CloudOAuthToken>.Fail(
                        response.ErrorText ?? "授权没有完成，请重试。");
            }
        }
    }

    /// <summary>用 Bearer 取当前账号信息。设备码登录后靠它拿到昵称，作为本机凭据的账号名。</summary>
    public async Task<CloudClosetResult<CloudOAuthUser>> GetOAuthUserAsync(CancellationToken cancellationToken)
    {
        var result = await SendWithBearerAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "/api/user"),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CloudClosetResult<CloudOAuthUser>.Fail(
                result.ErrorText!, result.IsNetworkError, result.IsAuthError);
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return CloudClosetResult<CloudOAuthUser>.Fail(response.ErrorText);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (IsApiError(root, out var error))
            {
                return CloudClosetResult<CloudOAuthUser>.Fail(error ?? "皮肤站拒绝了这次请求。");
            }

            var uid = ReadInt(root, "uid") ?? 0;
            var name = ReadString(root, "nickname") ?? ReadString(root, "name") ?? string.Empty;
            return CloudClosetResult<CloudOAuthUser>.Ok(
                new CloudOAuthUser(uid, string.IsNullOrWhiteSpace(name) ? "LittleSkin 账号" : name.Trim()));
        }
        catch (JsonException)
        {
            return CloudClosetResult<CloudOAuthUser>.Fail("账号信息不是有效的 JSON。");
        }
    }

    private async Task<CloudClosetResult<IReadOnlyList<CloudPlayer>>> GetPlayersOAuthAsync(
        CancellationToken cancellationToken)
    {
        var result = await SendWithBearerAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "/api/players"),
            cancellationToken).ConfigureAwait(false);
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
                if (IsApiError(root, out var error))
                {
                    return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail(error ?? "皮肤站拒绝了这次请求。");
                }

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
                    PositiveOrNull(ReadInt(entry, "tid_skin")),
                    PositiveOrNull(ReadInt(entry, "tid_cape"))));
            }

            return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Ok(players);
        }
        catch (JsonException)
        {
            return CloudClosetResult<IReadOnlyList<CloudPlayer>>.Fail("角色列表不是有效的 JSON。");
        }
    }

    private async Task<CloudClosetResult<CloudClosetPage>> GetClosetOAuthAsync(
        int page,
        string category,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = string.Equals(category, "cape", StringComparison.OrdinalIgnoreCase) ? "cape" : "skin";
        var url = $"/api/closet?page={Math.Max(1, page)}&category={normalizedCategory}";
        var result = await SendWithBearerAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
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

        var parsed = ParseCloset(response.Body);
        if (!parsed.Succeeded)
        {
            return parsed;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (IsApiError(document.RootElement, out var error))
            {
                return CloudClosetResult<CloudClosetPage>.Fail(error ?? "皮肤站拒绝了这次请求。");
            }
        }
        catch (JsonException)
        {
            return CloudClosetResult<CloudClosetPage>.Fail("衣柜返回的 JSON 无法解析。");
        }

        return parsed;
    }

    private async Task<CloudClosetResult<CloudOperationResult>> ApplyOAuthAsync(
        int pid,
        string kind,
        int tid,
        CancellationToken cancellationToken)
    {
        var normalizedKind = string.Equals(kind, "cape", StringComparison.OrdinalIgnoreCase) ? "cape" : "skin";
        var body = JsonSerializer.Serialize(
            new Dictionary<string, int> { [normalizedKind] = tid },
            CloudClosetRequestJsonContext.Default.DictionaryStringInt32);
        var result = await SendWithBearerAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"/api/players/{pid}/textures")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            },
            cancellationToken).ConfigureAwait(false);
        return ToBooleanResult(result, "材质应用");
    }

    private async Task<CloudClosetResult<CloudOperationResult>> ClearOAuthAsync(
        int pid,
        string kind,
        CancellationToken cancellationToken)
    {
        var normalizedKind = string.Equals(kind, "cape", StringComparison.OrdinalIgnoreCase) ? "cape" : "skin";
        var body = JsonSerializer.Serialize(
            new ClearTextureRequest([normalizedKind]),
            CloudClosetRequestJsonContext.Default.ClearTextureRequest);
        var result = await SendWithBearerAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"/api/players/{pid}/textures")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            },
            cancellationToken).ConfigureAwait(false);
        return ToBooleanResult(result, "清除材质");
    }

    /// <summary>发一个带 Bearer 的请求。401 时强制刷新一次再重试，其余状态交给统一解析。</summary>
    private async Task<CloudClosetResult<JsonResponse>> SendWithBearerAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await EnsureOAuthTokenAsync(cancellationToken).ConfigureAwait(false))
            {
                return CloudClosetResult<JsonResponse>.Fail(
                    "免密登录已过期，请重新授权。",
                    isAuthError: true);
            }

            using var request = requestFactory();
            if (Uri.TryCreate(request.RequestUri?.OriginalString, UriKind.Relative, out var relative)
                && !relative.IsAbsoluteUri)
            {
                request.RequestUri = new Uri(_baseAddress + request.RequestUri!.OriginalString);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _oauthAccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _oauthExpiresAt = DateTimeOffset.MinValue;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ReadResponse(response, body);
            }
            catch (HttpRequestException ex)
            {
                _log?.Warn($"OAuth 请求失败：{request.RequestUri}", ex);
                return CloudClosetResult<JsonResponse>.Fail("连不上皮肤站，检查网络后重试。", true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CloudClosetResult<JsonResponse>.Fail("皮肤站响应超时。", true);
            }
        }

        return CloudClosetResult<JsonResponse>.Fail("免密登录已过期，请重新授权。", isAuthError: true);
    }

    private async Task<bool> EnsureOAuthTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_oauthAccessToken))
        {
            return false;
        }

        if (_oauthExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_oauthRefreshToken))
        {
            return false;
        }

        var response = await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _oauthRefreshToken!,
                ["client_id"] = OAuthClientId,
            },
            cancellationToken).ConfigureAwait(false);
        if (response.Token is not { } token)
        {
            _log?.Warn($"刷新 OAuth 令牌失败：{response.ErrorCode ?? "unknown"}");
            return false;
        }

        ApplyToken(token);
        OAuthTokenRefreshed?.Invoke(this, token);
        return true;
    }

    private async Task<OAuthTokenResponse> RequestTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthIssuer + "/oauth/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accessToken = ReadString(root, "access_token");
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var expiresIn = ReadInt(root, "expires_in") ?? 3600;
                return new OAuthTokenResponse(
                    new CloudOAuthToken(
                        accessToken,
                        ReadString(root, "refresh_token") ?? string.Empty,
                        DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn))),
                    null,
                    null,
                    false);
            }

            var errorCode = ReadString(root, "error");
            var description = ReadString(root, "error_description");
            return new OAuthTokenResponse(
                null,
                errorCode,
                description is null ? DescribeOAuthError(body, response.StatusCode) : $"{description}（{errorCode}）",
                false);
        }
        catch (JsonException)
        {
            return new OAuthTokenResponse(null, null, "授权服务器返回的不是有效 JSON。", false);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn("请求 OAuth 令牌失败", ex);
            return new OAuthTokenResponse(null, null, "连不上 LittleSkin 授权服务器，检查网络后重试。", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OAuthTokenResponse(null, null, "LittleSkin 授权服务器响应超时。", true);
        }
    }

    private void ApplyToken(CloudOAuthToken token)
    {
        _oauthAccessToken = token.AccessToken;
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            _oauthRefreshToken = token.RefreshToken;
        }

        _oauthExpiresAt = token.ExpiresAt;
        _authenticated = true;
    }

    private bool IsApiError(JsonElement root, out string? errorText)
    {
        errorText = null;
        if (root.ValueKind != JsonValueKind.Object || ReadCode(root) is not { } code || code == 0)
        {
            return false;
        }

        errorText = ReadMessage(root)
                    ?? (code is 401 or 403
                        ? "站点拒绝了这次请求：应用可能没有获得对应权限（Player.ReadWrite）。"
                        : $"站点返回错误码 {code.ToString(CultureInfo.InvariantCulture)}。");
        return true;
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static string DescribeOAuthError(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var error = ReadString(root, "error");
            var description = ReadString(root, "error_description");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return error switch
                {
                    "invalid_client" => "LittleSkin 没有把这个应用加入设备代码流白名单（invalid_client）。",
                    "invalid_scope" => "站点拒绝了请求的授权范围（invalid_scope），可能是权限互斥。",
                    "authorization_pending" => "还在等你完成授权。",
                    "slow_down" => "轮询太快了，稍后继续。",
                    _ => description is null ? $"授权服务器返回 {error}。" : $"{description}（{error}）",
                };
            }
        }
        catch (JsonException)
        {
            // 不是 JSON：退回 HTTP 状态。
        }

        return $"授权服务器返回 HTTP {(int)status}。";
    }

    private sealed record OAuthTokenResponse(
        CloudOAuthToken? Token,
        string? ErrorCode,
        string? ErrorText,
        bool IsNetworkError);
}
