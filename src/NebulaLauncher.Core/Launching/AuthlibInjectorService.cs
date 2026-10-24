using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// authlib-injector 的本地缓存与 JVM 参数生成。
///
/// 对齐 PCL2 <c>ModLaunch.vb:1101-1138</c> 的三参数形态：第三方验证服务器不能被原版客户端识别，
/// 必须把 injector 作为 javaagent 注入，并把 API 根的响应 <b>原样</b> Base64 后塞进
/// <c>-Dauthlibinjector.yggdrasil.prefetched</c>——injector 启动时会解码这段数据，
/// 因此它必须是服务器返回的原始字节，不能是重新序列化后的对象。
///
/// 缓存策略：版本清单（<c>latest.json</c>）优先，拉不到时退化为目录里已存在的最高版本 jar。
/// 这样断网时只要之前成功过一次，第三方验证账户仍能启动。
/// </summary>
public sealed class AuthlibInjectorService : IAuthlibInjectorService
{
    /// <summary>官方版本清单地址。authlib-injector 的发布方只提供这一个稳定入口。</summary>
    public const string MetadataUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";

    /// <summary>缓存子目录名，落在 <see cref="ILauncherPaths.CacheDirectory"/> 下。</summary>
    public const string CacheFolderName = "authlib-injector";

    /// <summary>清单缓存文件名。</summary>
    public const string MetadataFileName = "latest.json";

    /// <summary>jar 文件名前缀。</summary>
    public const string JarPrefix = "authlib-injector-";

    /// <summary>jar 文件名后缀。</summary>
    public const string JarSuffix = ".jar";

    /// <summary>
    /// Windows 专有的信任库类型参数：让 JVM 用系统证书存储校验第三方验证服务器的 TLS 证书。
    /// 语义与「只该在 Windows 用」的理由见 <see cref="BuildTrustStoreArguments"/>。
    /// </summary>
    public const string WindowsRootTrustStoreOption = "-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT";

    /// <summary>清单与 API 根的请求预算。超过就按「拿不到」处理，让调用方走缓存或报错，不能卡住启动流程。</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>清单重试次数。清单只有几百字节，重试成本极低，而它失败会直接掉到「无缓存则阻断启动」。</summary>
    private const int MetadataAttempts = 3;

    /// <summary>版本号里只允许这些字符：它会成为文件名的一部分，必须挡住路径分隔符与非法字符。</summary>
    private static readonly Regex SafeVersionPattern = new("^[0-9A-Za-z._-]+$", RegexOptions.CultureInvariant);

    private readonly ILauncherPaths _paths;
    private readonly DownloadQueue? _queue;
    private readonly DownloadHttpClientFactory? _httpFactory;
    private readonly HttpClient? _httpClient;
    private readonly IAppLog? _log;

    public AuthlibInjectorService(
        ILauncherPaths paths,
        DownloadQueue? downloadQueue = null,
        DownloadHttpClientFactory? httpFactory = null,
        IAppLog? log = null,
        HttpClient? httpClient = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _queue = downloadQueue;
        _httpFactory = httpFactory;
        _log = log;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <summary>jar 与清单的缓存目录（绝对路径）。</summary>
    public string CacheDirectory => Path.Combine(_paths.CacheDirectory, CacheFolderName);

    /// <inheritdoc />
    public async Task<string?> EnsureInjectorAsync(
        string apiRoot,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        try
        {
            var directory = CacheDirectory;
            Directory.CreateDirectory(directory);

            var release = await TryFetchReleaseAsync(cancellationToken).ConfigureAwait(false);

            if (release is null)
            {
                // 清单拉不到（离线 / DNS 污染 / 官方站挂掉）：只要本地有 jar 就仍然可用。
                var cached = FindLatestCachedJar(directory);

                if (cached is null)
                {
                    LastError = "无法获取 authlib-injector 版本清单，本地也没有缓存。请检查网络后重试。";
                    _log?.Warn(LastError);
                    return null;
                }

                _log?.Info($"版本清单不可用，退化为本地缓存：{cached}");
                return cached;
            }

            var target = Path.Combine(directory, JarPrefix + release.Version + JarSuffix);

            if (await IsUsableAsync(target, release.Sha256, cancellationToken).ConfigureAwait(false))
            {
                _log?.Debug($"authlib-injector {release.Version} 命中缓存：{target}");
                return target;
            }

            var queue = ResolveQueue();

            if (queue is null)
            {
                LastError = "缺少下载组件，无法下载 authlib-injector。请重启启动器后重试。";
                _log?.Warn(LastError);
                return null;
            }

            var downloaded = await DownloadJarAsync(queue, release, target, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!downloaded)
            {
                return null;
            }

            TryWriteMetadata(directory, release);

            _log?.Info($"authlib-injector {release.Version} 已就绪：{target}");
            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = $"准备 authlib-injector 失败：{ex.Message}";
            _log?.Error(LastError, ex);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AuthlibInjection?> BuildInjectionAsync(
        string apiRoot,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var normalized = NormalizeApiRoot(apiRoot);

        if (normalized is null)
        {
            return null;
        }

        try
        {
            var jarPath = await EnsureInjectorAsync(normalized, progress, cancellationToken).ConfigureAwait(false);

            if (jarPath is null)
            {
                // LastError 已由 EnsureInjectorAsync 写成中文原因，这里不覆盖。
                return null;
            }

            var prefetched = await FetchPrefetchedAsync(normalized, cancellationToken).ConfigureAwait(false);

            if (prefetched is null)
            {
                return null;
            }

            return new AuthlibInjection(jarPath, normalized, ComposeArguments(jarPath, normalized, prefetched));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = $"生成第三方验证注入参数失败：{ex.Message}";
            _log?.Error(LastError, ex);
            return null;
        }
    }

    /// <summary>
    /// 组装注入片段。顺序与 PCL2 一致：javaagent 在前，prefetched 次之，side 再次，Windows 信任库类型最后。
    ///
    /// javaagent 的 jar 路径含空格时必须加引号：JVM 会把 <c>-javaagent:</c> 之后、<c>=</c> 之前的
    /// 整段当路径，Windows 的命令行分词会在空格处断开。
    /// </summary>
    /// <param name="jarPath">jar 绝对路径。</param>
    /// <param name="apiRoot">规范化后的 API 根。</param>
    /// <param name="prefetchedBase64">API 根原始响应的 Base64。</param>
    public static string ComposeArguments(string jarPath, string apiRoot, string prefetchedBase64)
    {
        var parts = new List<string>(4)
        {
            $"-javaagent:{QuoteIfNeeded(jarPath)}={apiRoot}",
            $"-Dauthlibinjector.yggdrasil.prefetched={prefetchedBase64}",
            "-Dauthlibinjector.side=client",
        };

        // 信任库类型排在最后：JVM 对 -D 与 -javaagent 的生效顺序没有要求，
        // 而前三项是与 PCL2 原文（ModLaunch.vb:1282-1284）一一对应的固定形态，
        // 保持它们在前面，比对与排错时不用重新数位置。
        var trustStore = BuildTrustStoreArguments(OperatingSystem.IsWindows());

        if (trustStore.Length > 0)
        {
            parts.Add(trustStore);
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// <c>-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT</c>（PCL2 <c>ModLaunch.vb:1277</c>）。
    ///
    /// 第三方验证服务器普遍只签了系统根证书，而 JVM 默认只认自己的 <c>cacerts</c>：
    /// 不换信任库时表现为「登录成功但游戏里没有角色」，或干脆 TLS 握手失败。
    /// 让 JVM 直接用 Windows 证书存储，与浏览器保持同一套根证书。
    ///
    /// <para>
    /// <b>只在 Windows 加</b>：<c>WINDOWS-ROOT</c> 是 SunJSSE 在 Windows 上的专有 KeyStore 类型，
    /// macOS / Linux 上 JVM 会抛 <c>KeyStoreException: WINDOWS-ROOT not found</c> 并**拒绝启动**，
    /// 比不加严重得多。
    /// </para>
    /// <para>
    /// PCL2 还带一个 <c>Java 主版本 &gt;= 6</c> 的判断，这里省略：Nebula 支持的 Minecraft 版本
    /// 最低要求 Java 8，任何能启动游戏的 JVM 都满足该条件；
    /// 而 <see cref="IAuthlibInjectorService.BuildInjectionAsync"/> 的签名里拿不到 Java 主版本
    /// （接口已冻结）。真需要按版本分支时，先由调用方把主版本传进来。
    /// </para>
    /// </summary>
    /// <param name="isWindows">当前是否运行在 Windows 上。</param>
    /// <returns>参数串；非 Windows 时为空串。</returns>
    public static string BuildTrustStoreArguments(bool isWindows) =>
        isWindows ? WindowsRootTrustStoreOption : string.Empty;

    /// <summary>
    /// 路径含空白时用双引号包裹，否则原样返回。已经是带引号的形式时不再重复包裹
    /// （调用方可能已经把引号写进配置，重复包裹会让 JVM 收到一对多余的引号）。
    /// </summary>
    public static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            return path;
        }

        return path.Contains(' ', StringComparison.Ordinal) || path.Contains('\t', StringComparison.Ordinal)
            ? "\"" + path + "\""
            : path;
    }

    /// <summary>
    /// 规范化 API 根：去首尾空白、去尾部斜杠、校验 scheme。
    ///
    /// 尾部斜杠必须去掉：injector 会用 <c>apiRoot + "/authserver/authenticate"</c> 拼地址，
    /// 留一个斜杠就变成双斜杠，部分服务端（尤其 Nginx 反代）会直接 404。
    /// </summary>
    /// <returns>规范化后的地址；无效时返回 null 并写 <see cref="LastError"/>。</returns>
    internal string? NormalizeApiRoot(string? apiRoot)
    {
        var trimmed = apiRoot?.Trim().TrimEnd('/');

        if (string.IsNullOrEmpty(trimmed))
        {
            LastError = "第三方验证地址为空，无法注入 authlib-injector。请在账户设置里填写 API 根。";
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            LastError = $"第三方验证地址无效：{trimmed}。需要以 http:// 或 https:// 开头的完整地址。";
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// 拉取版本清单。失败（网络、非 2xx、JSON 结构不符、版本号含非法字符）返回 null，
    /// 由调用方退化为本地缓存——清单只是「拿到最新版本号与校验值」的手段，不是必需品。
    /// </summary>
    private async Task<Release?> TryFetchReleaseAsync(CancellationToken cancellationToken)
    {
        var client = ResolveHttpClient();

        if (client is null)
        {
            _log?.Warn("没有可用的 HTTP 客户端，跳过 authlib-injector 版本清单");
            return null;
        }

        for (var attempt = 1; attempt <= MetadataAttempts; attempt++)
        {
            var result = await FetchBytesAsync(client, MetadataUrl, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                var release = ParseRelease(result.Bytes);

                if (release is not null)
                {
                    return release;
                }

                _log?.Warn("authlib-injector 版本清单格式无法识别");
            }
            else
            {
                _log?.Warn($"获取 authlib-injector 版本清单失败（第 {attempt}/{MetadataAttempts} 次）：{result.Error}");
            }

            if (attempt < MetadataAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>解析 <c>latest.json</c>。字段缺失或版本号不安全时返回 null。</summary>
    internal static Release? ParseRelease(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var version = ReadString(root, "version");
            var downloadUrl = ReadString(root, "download_url");

            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                return null;
            }

            version = version!.Trim();
            downloadUrl = downloadUrl!.Trim();

            if (!SafeVersionPattern.IsMatch(version))
            {
                return null;
            }

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            string? sha256 = null;

            if (root.TryGetProperty("checksums", out var checksums) &&
                checksums.ValueKind == JsonValueKind.Object)
            {
                var hash = ReadString(checksums, "sha256");
                sha256 = string.IsNullOrWhiteSpace(hash) ? null : hash!.Trim();
            }

            return new Release(version, downloadUrl, sha256);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>下载到临时文件 → 校验 SHA-256 → 落盘。校验不通过时删掉临时文件，不留半个 jar。</summary>
    private async Task<bool> DownloadJarAsync(
        DownloadQueue queue,
        Release release,
        string target,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var staging = target + ".tmp";
        TryDelete(staging);

        var item = new DownloadItem
        {
            Url = release.DownloadUrl,
            DestinationPath = staging,
            Id = "authlib-injector",
            Group = "authlib-injector",
        };

        var ok = await queue.DownloadFileAsync(item, progress, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            TryDelete(staging);
            LastError = $"下载 authlib-injector {release.Version} 失败，请检查网络或更换下载源。";
            _log?.Warn($"{LastError}（{release.DownloadUrl}）");
            return false;
        }

        if (release.Sha256 is { Length: > 0 } expected)
        {
            var actual = await ComputeSha256Async(staging, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(staging);
                LastError =
                    $"authlib-injector {release.Version} 的 SHA-256 校验不匹配，已删除损坏的下载："
                    + $"期望 {expected}，实际 {actual}。请重试。";
                _log?.Error(LastError);
                return false;
            }
        }
        else
        {
            // 清单没给校验值属于发布侧异常；jar 仍可用，只记录以便排查。
            _log?.Warn("authlib-injector 版本清单缺少 checksums.sha256，跳过完整性校验");
        }

        File.Move(staging, target, overwrite: true);
        return true;
    }

    /// <summary>缓存可用性：文件存在且（有校验值时）SHA-256 对得上。</summary>
    private static async Task<bool> IsUsableAsync(string path, string? sha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (sha256 is not { Length: > 0 })
        {
            // 没有校验值时只能看大小：0 字节的 jar 一定是坏的（下载中断或写盘失败）。
            try
            {
                return new FileInfo(path).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task<string?> FetchPrefetchedAsync(string apiRoot, CancellationToken cancellationToken)
    {
        var client = ResolveHttpClient();

        if (client is null)
        {
            LastError = "缺少 HTTP 组件，无法预取第三方验证元数据。请重启启动器后重试。";
            _log?.Warn(LastError);
            return null;
        }

        var result = await FetchBytesAsync(client, apiRoot, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            LastError = $"无法获取第三方验证元数据（{apiRoot}）：{result.Error}。请检查该地址是否可访问。";
            _log?.Warn(LastError);
            return null;
        }

        if (result.Bytes.Length == 0)
        {
            LastError = $"第三方验证服务器返回了空响应（{apiRoot}），无法生成注入参数。";
            _log?.Warn(LastError);
            return null;
        }

        return Convert.ToBase64String(result.Bytes);
    }

    private static async Task<FetchResult> FetchBytesAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);

        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult(false, [], $"HTTP {(int)response.StatusCode}");
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            return new FetchResult(true, bytes, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new FetchResult(false, [], $"请求超时（{FetchTimeout.TotalSeconds:0} 秒）");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new FetchResult(false, [], ex.Message);
        }
    }

    private HttpClient? ResolveHttpClient() => _httpClient ?? _httpFactory?.Client;

    /// <summary>
    /// 下载队列的取法：生产走 DI 注入的队列（带限速与源切换）；测试只给 <see cref="HttpClient"/> 时
    /// 就地建一个，避免测试为了下载队列再去装配整条下载基础设施。
    /// </summary>
    private DownloadQueue? ResolveQueue()
    {
        if (_queue is not null)
        {
            return _queue;
        }

        var client = ResolveHttpClient();
        return client is null ? null : new DownloadQueue(client, _log);
    }

    /// <summary>目录里最高版本的缓存 jar；按文件名里的版本号排序，同版本取最新写盘的那个。</summary>
    private string? FindLatestCachedJar(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var latest = new DirectoryInfo(directory)
                .GetFiles(JarPrefix + "*" + JarSuffix)
                .Select(file => (File: file, Version: ParseVersion(file.Name)))
                .Where(item => item.Version is not null)
                .OrderByDescending(item => item.Version)
                .ThenByDescending(item => item.File.LastWriteTimeUtc)
                .FirstOrDefault();

            return latest.File?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn("枚举 authlib-injector 缓存目录失败", ex);
            return null;
        }
    }

    private static Version? ParseVersion(string fileName)
    {
        if (!fileName.StartsWith(JarPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = fileName[JarPrefix.Length..^JarSuffix.Length];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>落一份清单副本，便于用户排查「为什么用的是旧版本」；写不进去只记日志。</summary>
    private void TryWriteMetadata(string directory, Release release)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new MetadataDocument(
                    release.Version,
                    release.DownloadUrl,
                    new MetadataChecksums(release.Sha256)),
                AuthlibInjectorJsonContext.Default.MetadataDocument);

            File.WriteAllBytes(Path.Combine(directory, MetadataFileName), payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Debug($"写入 authlib-injector 清单缓存失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉残留文件不影响下次下载：下一次会先覆盖同名临时文件。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：权限问题留给真正的下载步骤去报错。
        }
    }

    /// <summary>版本清单里真正被用到的那几个字段。</summary>
    /// <param name="Version">版本号，例如 <c>1.2.8</c>。</param>
    /// <param name="DownloadUrl">jar 下载地址。</param>
    /// <param name="Sha256">jar 的 SHA-256；清单未提供时为 null。</param>
    internal sealed record Release(string Version, string DownloadUrl, string? Sha256);

    private readonly record struct FetchResult(bool Success, byte[] Bytes, string? Error);
}

/// <summary>清单副本里的 checksums 段。</summary>
internal sealed record MetadataChecksums(string? Sha256);

/// <summary>
/// 落在 jar 旁边的清单副本（<c>authlib-injector.json</c>）。
///
/// 字段名与上游清单一致（snake_case），所以用 <see cref="JsonPropertyNameAttribute"/> 显式钉死，
/// 不依赖任何命名策略。
/// </summary>
internal sealed record MetadataDocument(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("checksums")] MetadataChecksums Checksums);

/// <summary>
/// authlib-injector 清单副本的 Source Generator 上下文。
/// </summary>
[JsonSerializable(typeof(MetadataDocument))]
internal sealed partial class AuthlibInjectorJsonContext : JsonSerializerContext
{
}
