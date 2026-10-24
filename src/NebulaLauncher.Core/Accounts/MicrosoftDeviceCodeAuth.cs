using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Accounts;

/// <summary>微软账户登录结果。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Message">失败时的中文原因。</param>
/// <param name="AccessToken">Minecraft 访问令牌。</param>
/// <param name="RefreshToken">微软 OAuth 刷新令牌。</param>
/// <param name="ExpiresAt">Minecraft 令牌过期时间（UTC）。</param>
/// <param name="Username">游戏档案名。</param>
/// <param name="Uuid">无连字符 32 位小写 UUID。</param>
public sealed record MicrosoftAuthResult(
    bool Success,
    string? Message,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTimeOffset ExpiresAt = default,
    string? Username = null,
    string? Uuid = null)
{
    /// <summary>
    /// 失败是否属于「稍后重试可能成功」（429 / 5xx）。
    /// 调用方用它区分「令牌真失效」与「微软服务暂时不可用」（REVIEW-R43-R51 P2-4）。
    /// </summary>
    public bool Retryable { get; init; }

    public static MicrosoftAuthResult Fail(string message, bool retryable = false) =>
        new(false, message) { Retryable = retryable };
}

/// <summary>
/// 微软账户设备码登录。
///
/// 为什么不用 <c>CmlLib.Core.Auth.Microsoft</c> 的内置登录：3.4.0 只提供
/// 「授权码 + WebView2/浏览器回调」这一条交互路径（<c>CodeFlowAuthenticator</c>），
/// 没有任何设备码 API，而 Avalonia 端没有 WebView2 也不允许内嵌浏览器。
/// 设备码流程只需普通 HttpClient，因此这里直接对微软公开端点实现，
/// 令牌产物与 CmlLib 的 <c>MSession</c> 完全兼容。
/// </summary>
public sealed class MicrosoftDeviceCodeAuth
{
    /// <summary>
    /// 覆盖内置 client id 用的环境变量（与 PCL2 的 <c>PCL_MS_CLIENT_ID</c> 等价）。
    ///
    /// 解析优先级：构造参数（含空串，用于禁用）→ <c>NEBULA_MS_CLIENT_ID</c> →
    /// <c>PCL_MS_CLIENT_ID</c> → <see cref="DefaultClientId"/>。
    /// </summary>
    public const string ClientIdEnvironmentVariable = "NEBULA_MS_CLIENT_ID";

    /// <summary>PCL2 使用的环境变量名，兼容同一个部署环境。</summary>
    public const string LegacyClientIdEnvironmentVariable = "PCL_MS_CLIENT_ID";

    /// <summary>
    /// 内置的默认 client id，让微软登录开箱可用。
    ///
    /// 为什么可以内置：设备码是 **public client** 流程，没有 client secret，也不需要重定向
    /// URL，id 本身就是公开信息。R46 当初把这个口子改成「必须由用户提供」是因为当时用的
    /// <c>00000000402b5328</c> 属于 MSA 旧应用模型，在 AAD v2 端点会报 AADSTS700016；
    /// 换成在个人账户（consumers）租户登记过的公共应用即可直接使用。
    ///
    /// 取值的依据：这是 PCL2 正式版二进制里内置的同一个公共应用 id（开源版在
    /// <c>ModSecret.vb</c> 里把它替换成环境变量）。实测该 id 在
    /// <c>login.microsoftonline.com/consumers/oauth2/v2.0/devicecode</c> 返回 200 + <c>user_code</c>，
    /// 轮询返回 <c>authorization_pending</c>，刷新端点返回 <c>invalid_grant</c>（而不是
    /// <c>invalid_client</c>），说明端点接受该应用。
    ///
    /// 失效时不需要改代码重新发版：设 <c>NEBULA_MS_CLIENT_ID</c> 覆盖即可。
    /// </summary>
    public const string DefaultClientId = "fe72edc2-3a6f-4280-90e8-e2beb64ce7e1";

    private const string Scope = "XboxLive.signin offline_access";
    private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxUserAuthEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XboxXstsEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftAuthEndpoint = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileEndpoint = "https://api.minecraftservices.com/minecraft/profile";

    private readonly HttpClient _http;
    private readonly IAppLog _log;
    private readonly IPlatformService _platform;

    public MicrosoftDeviceCodeAuth(
        HttpClient http,
        IAppLog log,
        IPlatformService platform,
        string? clientId = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ClientId = ResolveClientId(clientId);
    }

    /// <summary>
    /// 本次会话使用的 client id；为空只可能来自显式传入的空串（测试与嵌入式宿主用来禁用登录）。
    /// </summary>
    public string? ClientId { get; }

    private static string? ResolveClientId(string? explicitValue)
    {
        // 显式传入（哪怕空串）就按显式值处理：测试与嵌入式宿主需要屏蔽环境变量与内置值。
        if (explicitValue is not null)
        {
            return TrimQuotes(explicitValue);
        }

        var value = TrimQuotes(Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable))
            ?? TrimQuotes(Environment.GetEnvironmentVariable(LegacyClientIdEnvironmentVariable));

        return value ?? DefaultClientId;
    }

    /// <summary>
    /// 去掉首尾空白与成对引号。
    /// Windows 上 <c>set NEBULA_MS_CLIENT_ID="guid"</c> 会把引号写进值里，
    /// 不剥的话仍是 AADSTS700016，而用户想不到是引号的问题（REVIEW-R43-R51 P3-6）。
    /// </summary>
    private static string? TrimQuotes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>发起一次完整的设备码登录。取消时抛 <see cref="OperationCanceledException"/>。</summary>
    public async Task<MicrosoftAuthResult> SignInAsync(
        IProgress<MicrosoftAuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new MicrosoftAuthProgress("device_code", "正在向微软申请设备码…"));

        if (ClientId is null)
        {
            // 只有显式传空串才会走到这里（正常部署使用内置 id 或环境变量）。
            _log.Warn("微软登录的 client id 被显式清空，已阻止请求");
            return MicrosoftAuthResult.Fail(
                $"微软登录已被显式禁用（client id 为空）。删除该配置或设置环境变量 {ClientIdEnvironmentVariable}"
                + $"（或 {LegacyClientIdEnvironmentVariable}）后重试。");
        }

        using var deviceCodeResponse = await PostFormAsync(
            DeviceCodeEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
            },
            cancellationToken).ConfigureAwait(false);

        if (!deviceCodeResponse.IsSuccessStatusCode)
        {
            var body = await deviceCodeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _log.Error($"设备码申请失败：HTTP {(int)deviceCodeResponse.StatusCode} {body}");
            return MicrosoftAuthResult.Fail(
                TranslateDeviceCodeError((int)deviceCodeResponse.StatusCode, body));
        }

        using var deviceCodeDoc = JsonDocument.Parse(
            await deviceCodeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var root = deviceCodeDoc.RootElement;
        var deviceCode = GetString(root, "device_code");
        var userCode = GetString(root, "user_code");
        var verificationUri = GetString(root, "verification_uri");
        var expiresIn = GetInt(root, "expires_in", 900);
        var interval = GetInt(root, "interval", 5);
        var message = GetString(root, "message");

        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
        {
            _log.Error("设备码响应缺少必需字段");
            return MicrosoftAuthResult.Fail("微软返回的设备码数据不完整，请重试。");
        }

        progress?.Report(new MicrosoftAuthProgress(
            "device_code",
            string.IsNullOrEmpty(message) ? $"请在浏览器中输入代码 {userCode} 完成登录。" : message,
            userCode,
            verificationUri));

        if (!string.IsNullOrEmpty(verificationUri))
        {
            try
            {
                await _platform.OpenUrlAsync(verificationUri).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 打不开浏览器不影响流程：用户还可以手动访问 verificationUri。
                _log.Warn("自动打开设备码页面失败", ex);
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        var pollInterval = Math.Max(interval, 2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken).ConfigureAwait(false);

            using var tokenResponse = await PostFormAsync(
                TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode,
                },
                cancellationToken).ConfigureAwait(false);

            if (tokenResponse.IsSuccessStatusCode)
            {
                // 成功路径才解析 body：失败时可能拿到的是 HTML 登录页（REVIEW-R43-R51 P3-5）。
                using var tokenDoc = JsonDocument.Parse(
                    await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

                var accessToken = GetString(tokenDoc.RootElement, "access_token");
                var refreshToken = GetString(tokenDoc.RootElement, "refresh_token");

                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                {
                    return MicrosoftAuthResult.Fail("微软返回的令牌数据不完整，请重试。");
                }

                progress?.Report(new MicrosoftAuthProgress("xbox", "微软账号已授权，正在登录 Xbox Live…"));
                return await CompleteXboxChainAsync(accessToken, refreshToken, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var error = await TryReadJsonStringAsync(tokenResponse, "error", cancellationToken)
                .ConfigureAwait(false);

            switch (error)
            {
                case "authorization_pending":
                    progress?.Report(new MicrosoftAuthProgress(
                        "waiting",
                        "等待在浏览器中完成登录…",
                        userCode,
                        verificationUri));
                    continue;

                case "slow_down":
                    pollInterval += 5;
                    continue;

                case "authorization_declined":
                    return MicrosoftAuthResult.Fail("登录请求已被拒绝。");

                case "expired_token":
                    return MicrosoftAuthResult.Fail("设备码已过期，请重新发起登录。");

                case "bad_verification_code":
                    return MicrosoftAuthResult.Fail("设备码无效，请重新发起登录。");

                case "invalid_client":
                    // client id 被端点拒绝（内置 id 失效或用户覆盖成了错误值）。
                    // 说清楚是配置问题，不要落到「未知错误」。
                    _log.Error($"设备码轮询被拒绝：client id 不被接受 {error}");
                    return MicrosoftAuthResult.Fail(
                        "微软不接受当前的 client id。若覆盖过 "
                        + $"{ClientIdEnvironmentVariable}，请检查取值后重试。");

                default:
                    var description = await TryReadJsonStringAsync(tokenResponse, "error_description", cancellationToken)
                        .ConfigureAwait(false);
                    _log.Error($"设备码轮询失败：{error} {description}");
                    return MicrosoftAuthResult.Fail($"微软登录失败：{error ?? "未知错误"}");
            }
        }

        return MicrosoftAuthResult.Fail("设备码已过期，请重新发起登录。");
    }

    /// <summary>用刷新令牌续期。返回的 RefreshToken 可能被微软轮换，调用方必须整体覆盖保存。</summary>
    public async Task<MicrosoftAuthResult> RefreshAsync(
        string refreshToken,
        IProgress<MicrosoftAuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return MicrosoftAuthResult.Fail("刷新令牌为空，需要重新登录。");
        }

        if (ClientId is null)
        {
            _log.Warn("微软令牌续期时 client id 被显式清空，已阻止请求");
            return MicrosoftAuthResult.Fail(
                $"微软登录已被显式禁用（client id 为空）。删除该配置或设置环境变量 {ClientIdEnvironmentVariable}"
                + $"（或 {LegacyClientIdEnvironmentVariable}）后重试。");
        }

        progress?.Report(new MicrosoftAuthProgress("refresh", "正在续期微软登录状态…"));

        using var tokenResponse = await PostFormAsync(
            TokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope,
            },
            cancellationToken).ConfigureAwait(false);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            // 必须先判状态码再解析 body：代理/网关返回 HTML 时 Parse 会抛 JsonException
            // （REVIEW-R43-R51 P3-5）。
            var status = (int)tokenResponse.StatusCode;
            var error = await TryReadJsonStringAsync(tokenResponse, "error", cancellationToken)
                .ConfigureAwait(false);
            _log.Warn($"刷新令牌失败：HTTP {status} {error}");

            if (status is 429 or >= 500)
            {
                // 临时故障：令牌很可能仍然有效，文案不能说「已失效」。
                return MicrosoftAuthResult.Fail("微软服务暂时不可用，请稍后重试。", retryable: true);
            }

            return error is "invalid_grant" or "invalid_client"
                ? MicrosoftAuthResult.Fail("登录状态已失效，请重新登录微软账户。")
                : MicrosoftAuthResult.Fail($"微软登录状态续期失败（HTTP {status}）。");
        }

        using var tokenDoc = JsonDocument.Parse(
            await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var accessToken = GetString(tokenDoc.RootElement, "access_token");
        var newRefresh = GetString(tokenDoc.RootElement, "refresh_token") ?? refreshToken;

        if (string.IsNullOrEmpty(accessToken))
        {
            return MicrosoftAuthResult.Fail("微软返回的令牌数据不完整，请重新登录。");
        }

        return await CompleteXboxChainAsync(accessToken, newRefresh, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>MSA 令牌 → Xbox Live → XSTS → Minecraft 服务 → 游戏档案。</summary>
    private async Task<MicrosoftAuthResult> CompleteXboxChainAsync(
        string msaAccessToken,
        string refreshToken,
        IProgress<MicrosoftAuthProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 1) Xbox Live 用户令牌。RpsTicket 前缀 d= 表示这是一个 MSA 令牌。
        using var xblResponse = await PostJsonAsync(
            XboxUserAuthEndpoint,
            $$"""
              {
                "Properties": {
                  "AuthMethod": "RPS",
                  "SiteName": "user.auth.xboxlive.com",
                  "RpsTicket": "d={{msaAccessToken}}"
                },
                "RelyingParty": "http://auth.xboxlive.com",
                "TokenType": "JWT"
              }
              """,
            cancellationToken).ConfigureAwait(false);

        if (!xblResponse.IsSuccessStatusCode)
        {
            _log.Error($"Xbox Live 认证失败：HTTP {(int)xblResponse.StatusCode}");
            return MicrosoftAuthResult.Fail("Xbox Live 认证失败，请确认该微软账户已注册 Xbox 档案。");
        }

        using var xblDoc = JsonDocument.Parse(
            await xblResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var xblToken = GetString(xblDoc.RootElement, "Token");
        var userHash = GetFirstXuiClaim(xblDoc.RootElement, "uhs");

        if (string.IsNullOrEmpty(xblToken) || string.IsNullOrEmpty(userHash))
        {
            _log.Error("Xbox Live 响应缺少 Token 或 uhs");
            return MicrosoftAuthResult.Fail("Xbox Live 返回的数据不完整，请重试。");
        }

        progress?.Report(new MicrosoftAuthProgress("xsts", "正在获取 Xbox 安全令牌…"));

        // 2) XSTS 授权。RelyingParty 必须是 Minecraft 服务，否则拿到的令牌无法换游戏令牌。
        using var xstsResponse = await PostJsonAsync(
            XboxXstsEndpoint,
            $$"""
              {
                "Properties": {
                  "SandboxId": "RETAIL",
                  "UserTokens": ["{{xblToken}}"]
                },
                "RelyingParty": "rp://api.minecraftservices.com/",
                "TokenType": "JWT"
              }
              """,
            cancellationToken).ConfigureAwait(false);

        if (!xstsResponse.IsSuccessStatusCode)
        {
            var xstsBody = await xstsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _log.Error($"XSTS 授权失败：HTTP {(int)xstsResponse.StatusCode} {xstsBody}");
            return MicrosoftAuthResult.Fail(TranslateXstsError(xstsBody));
        }

        using var xstsDoc = JsonDocument.Parse(
            await xstsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var xstsToken = GetString(xstsDoc.RootElement, "Token");
        if (string.IsNullOrEmpty(xstsToken))
        {
            return MicrosoftAuthResult.Fail("XSTS 返回的数据不完整，请重试。");
        }

        progress?.Report(new MicrosoftAuthProgress("minecraft", "正在登录 Minecraft 服务…"));

        // 3) 用 XSTS 换 Minecraft 访问令牌。
        using var mcResponse = await PostJsonAsync(
            MinecraftAuthEndpoint,
            $$"""
              {
                "identityToken": "XBL3.0 x={{userHash}};{{xstsToken}}"
              }
              """,
            cancellationToken).ConfigureAwait(false);

        if (!mcResponse.IsSuccessStatusCode)
        {
            var mcBody = await mcResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _log.Error($"Minecraft 登录失败：HTTP {(int)mcResponse.StatusCode} {mcBody}");
            return MicrosoftAuthResult.Fail("Minecraft 服务拒绝了本次登录，请确认账户已购买 Minecraft: Java 版。");
        }

        using var mcDoc = JsonDocument.Parse(
            await mcResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var mcAccessToken = GetString(mcDoc.RootElement, "access_token");
        var expiresInSeconds = GetInt(mcDoc.RootElement, "expires_in", 86400);

        if (string.IsNullOrEmpty(mcAccessToken))
        {
            return MicrosoftAuthResult.Fail("Minecraft 服务返回的令牌为空，请重试。");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);

        progress?.Report(new MicrosoftAuthProgress("profile", "正在读取游戏档案…"));

        // 4) 读档案：既是为了显示正确的用户名，也是校验「确实拥有游戏」。
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileEndpoint);
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);

        using var profileResponse = await _http.SendAsync(profileRequest, cancellationToken).ConfigureAwait(false);

        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return MicrosoftAuthResult.Fail("该微软账户没有 Minecraft: Java 版的所有权。");
        }

        if (!profileResponse.IsSuccessStatusCode)
        {
            _log.Error($"读取 Minecraft 档案失败：HTTP {(int)profileResponse.StatusCode}");
            return MicrosoftAuthResult.Fail("读取 Minecraft 档案失败，请稍后重试。");
        }

        using var profileDoc = JsonDocument.Parse(
            await profileResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var username = GetString(profileDoc.RootElement, "name");
        var uuid = GetString(profileDoc.RootElement, "id");

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(uuid))
        {
            return MicrosoftAuthResult.Fail("Minecraft 档案数据不完整，请重试。");
        }

        progress?.Report(new MicrosoftAuthProgress("done", $"已登录为 {username}"));

        _log.Info($"微软账户登录成功：{username}");

        return new MicrosoftAuthResult(
            Success: true,
            Message: null,
            AccessToken: mcAccessToken,
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt,
            Username: username,
            Uuid: uuid.ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把设备码申请阶段的失败翻译成可操作的中文。
    ///
    /// 为什么要解析 AAD 错误码：<c>consumers</c> 租户只接受登记过的应用，client id 无效时
    /// 返回 AADSTS700016 / AADSTS9002332。统一说成「请检查网络」会让用户永远查不到真正原因
    /// ——R46 的原症状正是这样被误报的。
    /// </summary>
    private static string TranslateDeviceCodeError(int status, string body)
    {
        if (body.Contains("AADSTS700016", StringComparison.Ordinal)
            || body.Contains("AADSTS9002332", StringComparison.Ordinal)
            || body.Contains("unauthorized_client", StringComparison.Ordinal)
            || body.Contains("invalid_client", StringComparison.Ordinal))
        {
            return $"微软登录的 client id 被拒绝（{FirstAadstsCode(body)}）。"
                + $"可设置环境变量 {ClientIdEnvironmentVariable} 覆盖内置值后重试。";
        }

        if (status is 429 or >= 500)
        {
            return "微软登录服务暂时不可用，请稍后重试。";
        }

        if (body.Contains("AADSTS", StringComparison.Ordinal))
        {
            return $"微软拒绝了设备码申请（{FirstAadstsCode(body)}），请稍后重试。";
        }

        return "无法向微软申请设备码，请检查网络后重试。";
    }

    /// <summary>从响应体里取出第一个 AADSTS 错误码，取不到时返回 HTTP 层面的占位名。</summary>
    private static string FirstAadstsCode(string body)
    {
        var index = body.IndexOf("AADSTS", StringComparison.Ordinal);

        if (index < 0)
        {
            return "unauthorized_client";
        }

        var end = index;

        while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == '.'))
        {
            end++;
        }

        var code = body[index..end];
        return code.Length > "AADSTS".Length ? code : "unauthorized_client";
    }

    /// <summary>XSTS 用 XErr 数字码表达「没买游戏 / 没建档 / 儿童账号」这类具体原因。</summary>
    private static string TranslateXstsError(string body)
    {
        const string noAccount = "2148916233";
        const string noXboxProfile = "2148916235";
        const string childAccount = "2148916238";

        if (body.Contains(noAccount, StringComparison.Ordinal) || body.Contains(noXboxProfile, StringComparison.Ordinal))
        {
            return "该微软账户还没有 Xbox 档案，请先用该账号登录一次 xbox.com 创建档案。";
        }

        if (body.Contains(childAccount, StringComparison.Ordinal))
        {
            return "该账户是儿童账号，需要先由家庭组织者批准后才能登录。";
        }

        return "Xbox 授权失败，请确认账户状态正常后重试。";
    }

    /// <summary>
    /// 从响应体里安全地取一个字符串字段：不是 JSON（代理登录页、HTML 错误页）时返回 null，不抛。
    /// </summary>
    private static async Task<string?> TryReadJsonStringAsync(
        HttpResponseMessage response,
        string property,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return GetString(document.RootElement, property);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static string? GetFirstXuiClaim(JsonElement root, string claim)
    {
        if (!root.TryGetProperty("DisplayClaims", out var claims) ||
            !claims.TryGetProperty("xui", out var xui) ||
            xui.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in xui.EnumerateArray())
        {
            if (item.TryGetProperty(claim, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
