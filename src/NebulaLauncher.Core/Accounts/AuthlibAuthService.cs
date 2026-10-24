using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Accounts;

/// <summary>authlib-injector 认证结果。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Message">失败时的中文原因，可直接展示给用户。</param>
/// <param name="AccessToken">访问令牌（yggdrasil <c>accessToken</c>）。</param>
/// <param name="ClientToken">客户端令牌；刷新时必须原样带回，否则服务端会拒绝。</param>
/// <param name="Username">角色名。刷新时为 <c>null</c> 表示服务端没返回，调用方保留旧值。</param>
/// <param name="Uuid">无连字符 32 位小写角色 UUID；<c>null</c> 含义同 <paramref name="Username"/>。</param>
/// <param name="AvailableProfiles">该通行证下的全部角色；服务端未返回时为空集合。</param>
public sealed record AuthlibAuthResult(
    bool Success,
    string? Message,
    string? AccessToken = null,
    string? ClientToken = null,
    string? Username = null,
    string? Uuid = null,
    IReadOnlyList<AuthlibProfile>? AvailableProfiles = null)
{
    /// <summary>构造失败结果。</summary>
    public static AuthlibAuthResult Fail(string message) => new(false, message);
}

/// <summary>authlib 角色档案。</summary>
/// <param name="Id">无连字符 32 位小写 UUID。</param>
/// <param name="Name">角色名。</param>
public sealed record AuthlibProfile(string Id, string Name);

/// <summary>
/// 第三方验证（authlib-injector / 统一通行证 / 皮肤站）的认证客户端。
///
/// 只实现 yggdrasil 的 <c>authserver</c> 三个端点：authenticate / refresh / validate。
/// 网络失败与服务端拒绝统一翻译成中文原因并返回结果对象，不向上抛异常——
/// 唯一例外是调用方主动取消（<see cref="OperationCanceledException"/>），由上层决定「已取消」文案。
/// </summary>
public sealed class AuthlibAuthService
{
    /// <summary>
    /// agent 必须是 <c>Minecraft</c>：服务端据此判断这是 Java 版登录，用其它名字会被当成未知客户端。
    /// </summary>
    private const string AgentName = "Minecraft";

    private const int AgentVersion = 1;

    /// <summary>HTTP 429：authlib 服务端普遍用它做登录频率限制。</summary>
    private const int TooManyRequests = 429;

    private const string InvalidRootMessage =
        "验证服务器地址无效，请填写以 http:// 或 https:// 开头的完整 API 根，例如 https://littleskin.cn/api/yggdrasil。";

    private const string NetworkErrorMessage = "无法连接验证服务器，请检查网络或 API 根地址。";

    private readonly HttpClient _http;
    private readonly IAppLog _log;

    /// <summary>创建实例。<paramref name="http"/> 由调用方共享（账户服务已有实例，避免重复建连接池）。</summary>
    public AuthlibAuthService(HttpClient http, IAppLog log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// 规范化用户填写的 API 根：去首尾空白、去尾斜杠、要求 http/https 绝对地址。
    ///
    /// 不做「自动补 <c>/api/yggdrasil</c>」这类猜测：站点根与 API 根是两种地址，
    /// 猜错会把用户引到一个 404 上，还看不出到底填错了哪一段。无法解析时返回 <c>null</c>。
    /// </summary>
    public static string? NormalizeApiRoot(string? apiRoot)
    {
        if (apiRoot is null)
        {
            return null;
        }

        var trimmed = apiRoot.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        // API 根不带查询串/锚点；带了说明用户粘的是某个具体接口地址。
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        // 用 Uri 重建一次，顺带把 scheme/host 的小写形式统一掉，保证「同一服务器」可以被字符串比较。
        var canonical = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return canonical.Length == 0 ? null : canonical;
    }

    /// <summary>
    /// 用账号密码换取访问令牌：<c>POST {apiRoot}/authserver/authenticate</c>。
    /// </summary>
    public async Task<AuthlibAuthResult> AuthenticateAsync(
        string apiRoot,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeApiRoot(apiRoot);
        if (root is null)
        {
            return AuthlibAuthResult.Fail(InvalidRootMessage);
        }

        var account = username?.Trim() ?? string.Empty;
        if (account.Length == 0)
        {
            return AuthlibAuthResult.Fail("账号不能为空。");
        }

        if (string.IsNullOrEmpty(password))
        {
            return AuthlibAuthResult.Fail("密码不能为空。");
        }

        // clientToken 由启动器生成并长期保存：刷新时服务端靠它确认「还是同一个客户端」。
        var clientToken = Guid.NewGuid().ToString();

        var payload = JsonSerializer.Serialize(
            new AuthenticateRequest(
                account,
                password,
                clientToken,
                new AgentDescriptor(AgentName, AgentVersion),
                RequestUser: false),
            AuthlibAuthJsonContext.Default.AuthenticateRequest);

        try
        {
            using var response = await PostJsonAsync(
                root + "/authserver/authenticate",
                payload,
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"第三方验证登录失败：HTTP {(int)response.StatusCode} {body}");
                return AuthlibAuthResult.Fail(TranslateHttpError(
                    response.StatusCode,
                    ExtractServerMessage(body),
                    "账号或密码错误。",
                    "该账号未拥有游戏，或已被验证服务器拒绝。"));
            }

            using var document = JsonDocument.Parse(body);
            var element = document.RootElement;

            if (element.ValueKind != JsonValueKind.Object)
            {
                _log.Error("第三方验证响应不是 JSON 对象");
                return AuthlibAuthResult.Fail("验证服务器返回的数据格式不正确。");
            }

            var accessToken = GetString(element, "accessToken");
            if (string.IsNullOrEmpty(accessToken))
            {
                _log.Error("第三方验证响应缺少 accessToken");
                return AuthlibAuthResult.Fail("验证服务器返回的令牌为空，请稍后重试。");
            }

            var selected = ParseProfile(element, "selectedProfile");
            var profiles = ParseProfiles(element);
            if (selected is null && profiles.Count == 0)
            {
                _log.Warn("第三方验证响应缺少 selectedProfile");
                return AuthlibAuthResult.Fail("该账号在验证服务器上没有可用角色，请先在服务器网站创建角色。");
            }

            return new AuthlibAuthResult(
                Success: true,
                Message: null,
                AccessToken: accessToken,
                // 服务端可能换上自己的 clientToken 并绑定到令牌，之后续期必须用它的；
                // 只有服务端没回时才用本地生成的那个。
                ClientToken: GetString(element, "clientToken") ?? clientToken,
                Username: selected?.Name,
                Uuid: selected?.Id,
                AvailableProfiles: profiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient 超时在 .NET 里也表现为取消；这不是用户主动取消，按网络问题处理。
            return AuthlibAuthResult.Fail("连接验证服务器超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn("无法连接验证服务器", ex);
            return AuthlibAuthResult.Fail(NetworkErrorMessage);
        }
        catch (JsonException ex)
        {
            _log.Error("验证服务器返回的数据无法解析", ex);
            return AuthlibAuthResult.Fail("验证服务器返回的数据格式不正确。");
        }
    }

    /// <summary>
    /// 用访问令牌续期：<c>POST {apiRoot}/authserver/refresh</c>。
    ///
    /// 常规续期不发送 <c>selectedProfile</c>，保持令牌原有的角色绑定。
    /// </summary>
    public Task<AuthlibAuthResult> RefreshAsync(
        string apiRoot,
        string accessToken,
        string clientToken,
        CancellationToken cancellationToken = default)
        => RefreshCoreAsync(apiRoot, accessToken, clientToken, null, cancellationToken);

    /// <summary>把尚未选择角色的登录令牌绑定到服务端提供的角色。</summary>
    public Task<AuthlibAuthResult> SelectProfileAsync(
        string apiRoot,
        string accessToken,
        string clientToken,
        AuthlibProfile profile,
        CancellationToken cancellationToken = default)
        => RefreshCoreAsync(apiRoot, accessToken, clientToken, profile, cancellationToken);

    private async Task<AuthlibAuthResult> RefreshCoreAsync(
        string apiRoot,
        string accessToken,
        string clientToken,
        AuthlibProfile? selectedProfile,
        CancellationToken cancellationToken)
    {
        var root = NormalizeApiRoot(apiRoot);
        if (root is null)
        {
            return AuthlibAuthResult.Fail(InvalidRootMessage);
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            return AuthlibAuthResult.Fail("缺少访问令牌，请重新登录。");
        }

        var payload = BuildTokenPayload(accessToken, clientToken, selectedProfile);

        try
        {
            using var response = await PostJsonAsync(
                root + "/authserver/refresh",
                payload,
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"第三方验证续期失败：HTTP {(int)response.StatusCode} {body}");
                return AuthlibAuthResult.Fail(TranslateHttpError(
                    response.StatusCode,
                    ExtractServerMessage(body),
                    "登录状态已失效，请重新登录第三方验证账户。",
                    "登录状态已失效，请重新登录第三方验证账户。"));
            }

            using var document = JsonDocument.Parse(body);
            var element = document.RootElement;

            if (element.ValueKind != JsonValueKind.Object)
            {
                _log.Error("第三方验证续期响应不是 JSON 对象");
                return AuthlibAuthResult.Fail("验证服务器返回的数据格式不正确。");
            }

            var newAccessToken = GetString(element, "accessToken");
            if (string.IsNullOrEmpty(newAccessToken))
            {
                _log.Error("第三方验证续期响应缺少 accessToken");
                return AuthlibAuthResult.Fail("验证服务器返回的令牌为空，请稍后重试。");
            }

            // 服务端可以不回 selectedProfile（角色没变）；此时保留调用方的旧值。
            var profile = ParseProfile(element, "selectedProfile");
            if (selectedProfile is not null && !string.Equals(profile?.Id, selectedProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return AuthlibAuthResult.Fail("验证服务器未确认所选角色，请重新登录后重试。");
            }

            return new AuthlibAuthResult(
                Success: true,
                Message: null,
                AccessToken: newAccessToken,
                ClientToken: GetString(element, "clientToken") ?? clientToken,
                Username: profile?.Name,
                Uuid: profile?.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthlibAuthResult.Fail("连接验证服务器超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn("无法连接验证服务器", ex);
            return AuthlibAuthResult.Fail(NetworkErrorMessage);
        }
        catch (JsonException ex)
        {
            _log.Error("验证服务器返回的数据无法解析", ex);
            return AuthlibAuthResult.Fail("验证服务器返回的数据格式不正确。");
        }
    }

    /// <summary>
    /// 校验访问令牌是否仍然有效：<c>POST {apiRoot}/authserver/validate</c>。
    /// 成功即「令牌可用」，失败时 <see cref="AuthlibAuthResult.Message"/> 是中文原因。
    /// </summary>
    public async Task<AuthlibAuthResult> ValidateAsync(
        string apiRoot,
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeApiRoot(apiRoot);
        if (root is null)
        {
            return AuthlibAuthResult.Fail(InvalidRootMessage);
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            return AuthlibAuthResult.Fail("缺少访问令牌，请重新登录。");
        }

        var payload = BuildTokenPayload(accessToken, clientToken);

        try
        {
            using var response = await PostJsonAsync(
                root + "/authserver/validate",
                payload,
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // 规范用 204 表示有效；部分服务端实现回 200 空体，同样按有效处理。
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK)
            {
                return new AuthlibAuthResult(Success: true, Message: null);
            }

            _log.Info($"第三方验证令牌校验未通过：HTTP {(int)response.StatusCode} {body}");
            return AuthlibAuthResult.Fail(TranslateHttpError(
                response.StatusCode,
                ExtractServerMessage(body),
                "登录状态已失效，请重新登录。",
                "登录状态已失效，请重新登录。"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthlibAuthResult.Fail("连接验证服务器超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn("无法连接验证服务器", ex);
            return AuthlibAuthResult.Fail(NetworkErrorMessage);
        }
    }

    /// <summary>
    /// refresh 与 validate 共用同一个请求体形状。
    /// 空 clientToken 时不写该字段：写成空串会被部分服务端判成「clientToken 不匹配」，
    /// 而缺省字段是规范允许的。
    /// </summary>
    private static string BuildTokenPayload(string accessToken, string? clientToken, AuthlibProfile? selectedProfile = null)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("accessToken", accessToken);

            if (!string.IsNullOrEmpty(clientToken))
            {
                writer.WriteString("clientToken", clientToken);
            }

            if (selectedProfile is not null)
            {
                writer.WriteStartObject("selectedProfile");
                writer.WriteString("id", selectedProfile.Id);
                writer.WriteString("name", selectedProfile.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
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
    /// 把 HTTP 状态码翻译成用户能照做的中文。
    /// 401/403/429 是 authlib 服务端最常用来区分的三种情况，不能合并成一句「登录失败」。
    /// </summary>
    private static string TranslateHttpError(
        HttpStatusCode status,
        string? serverMessage,
        string unauthorizedMessage,
        string forbiddenMessage)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            return unauthorizedMessage;
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return forbiddenMessage;
        }

        if ((int)status == TooManyRequests)
        {
            return "请求过于频繁，请稍后再试。";
        }

        return string.IsNullOrEmpty(serverMessage)
            ? $"验证服务器返回错误（HTTP {(int)status}），请稍后重试。"
            : $"验证服务器返回错误：{serverMessage}";
    }

    /// <summary>取 authlib 错误体里的 <c>errorMessage</c>，取不到退化成 <c>error</c>。</summary>
    private static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var element = document.RootElement;

            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return GetString(element, "errorMessage") ?? GetString(element, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<AuthlibProfile> ParseProfiles(JsonElement root)
    {
        if (!root.TryGetProperty("availableProfiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AuthlibProfile>();
        }

        var list = new List<AuthlibProfile>();

        foreach (var item in profiles.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = NormalizeUuid(GetString(item, "id"));
            var name = GetString(item, "name");

            if (id is not null && !string.IsNullOrEmpty(name))
            {
                list.Add(new AuthlibProfile(id, name));
            }
        }

        return list;
    }

    private static AuthlibProfile? ParseProfile(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = NormalizeUuid(GetString(element, "id"));
        var name = GetString(element, "name");

        return id is null || string.IsNullOrEmpty(name) ? null : new AuthlibProfile(id, name);
    }

    /// <summary>统一成模型约定的「无连字符 32 位小写」形态，服务端回什么格式都收敛到这一种。</summary>
    private static string? NormalizeUuid(string? id) =>
        string.IsNullOrEmpty(id) ? null : id.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary><c>POST /authserver/authenticate</c> 请求体里的 agent 描述。</summary>
internal sealed record AgentDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] int Version);

/// <summary>
/// <c>POST /authserver/authenticate</c> 的请求体。
///
/// 原来是匿名对象，裁剪发布下会在序列化这一步抛异常，表现为「正版登录永远失败」。
/// </summary>
internal sealed record AuthenticateRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("clientToken")] string ClientToken,
    [property: JsonPropertyName("agent")] AgentDescriptor Agent,
    [property: JsonPropertyName("requestUser")] bool RequestUser);

/// <summary>
/// Yggdrasil 认证请求的 Source Generator 上下文。
/// </summary>
[JsonSerializable(typeof(AuthenticateRequest))]
internal sealed partial class AuthlibAuthJsonContext : JsonSerializerContext
{
}
