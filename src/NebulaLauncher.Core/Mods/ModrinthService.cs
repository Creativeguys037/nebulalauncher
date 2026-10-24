using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// Modrinth 在线资源库客户端。
///
/// 为什么不复用 <c>DownloadHttpClientFactory</c>：那个客户端挂着 BMCLAPI 改写 handler，
/// 会把 https://launchermeta / piston-* 的地址换成镜像；而 Modrinth 的镜像规则是另一组域名前缀
/// （见 <see cref="DownloadMirror.ModrinthCandidates"/>），混进去只会让两类改写互相踩。
/// 这里自建一个 HttpClient，只带 Modrinth 要求的 User-Agent。
///
/// 镜像兜底：默认关闭。设置 <c>AppSettings.UseModrinthMirror</c> 打开后，
/// 每个请求仍然是**先打官方**，只有官方抛网络异常 / 超时 / 429 / 5xx 时才用同一个逻辑请求打镜像一次；
/// 官方 404 / 403 表示「资源本来就不存在」，不换镜像（否则每查一个不存在的项目都要多等一轮超时）。
///
/// 失败策略：所有公开方法都不向调用方抛异常（取消除外），失败写入 <see cref="LastError"/>
/// 并返回空集合 / 失败结果，日志里留原始异常。UI 因此永远拿得到可展示的中文文案。
/// </summary>
public sealed class ModrinthService : IModrinthService, IDisposable
{
    /// <summary>Modrinth API 版本固定为 v2。</summary>
    public const string ApiBase = "https://api.modrinth.com/v2";

    /// <summary>Modrinth 要求每个客户端带能识别自己的 User-Agent，否则可能被限流。</summary>
    public const string UserAgent = "NebulaLauncher/1.0 (github.com/nebula)";

    /// <summary>search 接口的 limit 上限。</summary>
    public const int MaxPageSize = 100;

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>失败重试的间隔。只重试一次，避免在被限流时把请求堆起来。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>进度回调的最小间隔，避免小文件下载时把 UI 线程刷爆。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);

    private const int ProgressChunkBytes = 256 * 1024;

    /// <summary>详情缓存的有效期。正文、图库、链接这一组元信息变化很慢，5 分钟内重复选中没必要再请求。</summary>
    private static readonly TimeSpan DetailCacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>详情缓存的条目上限。热门列表里来回点几十个项目也不该让字典无限增长。</summary>
    private const int MaxDetailCacheEntries = 32;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // StripMarkdown 用的规则。Modrinth 正文是完整 Markdown，页面只按纯文本渲染，
    // 这里一次性把图片 / 链接 / 行级前缀这些「只有渲染器才懂」的记号摘掉。
    private static readonly Regex MarkdownImagePattern = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkPattern = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]{1,200}>", RegexOptions.Compiled);
    private static readonly Regex MarkdownRulePattern = new(@"^[ \t]{0,3}(?:-{3,}|\*{3,}|_{3,})[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MarkdownLinePrefixPattern = new(@"^[ \t]{0,3}(?:#{1,6}[ \t]*|>[ \t]?|[-*+][ \t]+|\d{1,3}[.)][ \t]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex InlineWhitespacePattern = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private readonly IAppLog? _log;
    private readonly ILauncherPaths? _paths;
    private readonly ISettingsService? _settings;
    private readonly HttpClient _http;

    /// <summary>
    /// 详情缓存（projectId → 解析结果 + 写入时刻）。详情只在用户点中某个项目时才需要，
    /// 来回切项目会反复打到同一个 id，缓存省掉的是这一串重复请求。
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset StoredAt, ModrinthProject Project)> _detailCache = new(StringComparer.Ordinal);

    private readonly object _detailCacheGate = new();

    /// <summary>DI 用构造函数。日志与路径缺失时退化为「不记日志、不缓存图标」。</summary>
    public ModrinthService(IAppLog? log = null, ILauncherPaths? paths = null)
        : this(log, paths, settings: null, handler: null)
    {
    }

    /// <summary>
    /// DI 用构造函数（带设置）。镜像开关从 <see cref="ISettingsService.Current"/> 实时读取，
    /// 用户在设置页改完保存后，下一次请求即按新值走，不必重启启动器。
    ///
    /// 与下面带 <see cref="HttpMessageHandler"/> 的重载并存：DI 挑「参数能被容器解析」的那一个，
    /// <see cref="HttpMessageHandler"/> 没有注册，所以组合根选中本重载。
    /// </summary>
    public ModrinthService(IAppLog? log, ILauncherPaths? paths, ISettingsService? settings)
        : this(log, paths, settings, handler: null)
    {
    }

    /// <summary>
    /// 测试与诊断用：注入自定义 <see cref="HttpMessageHandler"/> 以便在不联网的情况下
    /// 覆盖失败分支。<paramref name="handler"/> 没有默认值，所以 DI 会跳过这个构造函数。
    /// </summary>
    public ModrinthService(IAppLog? log, ILauncherPaths? paths, HttpMessageHandler? handler)
        : this(log, paths, settings: null, handler)
    {
    }

    /// <summary>测试与诊断用（同时注入设置）：镜像开关由 <paramref name="settings"/> 控制。</summary>
    public ModrinthService(IAppLog? log, ILauncherPaths? paths, ISettingsService? settings, HttpMessageHandler? handler)
    {
        _log = log;
        _paths = paths;
        _settings = settings;
        _http = CreateClient(handler);
    }

    /// <summary>
    /// 本次请求是否允许使用 Modrinth 镜像。默认关：没注入设置（单测、无 DI 宿主）时恒为 false，
    /// 一个请求都不会发到第三方主机。
    /// </summary>
    internal bool UseModrinthMirror => _settings?.Current.UseModrinthMirror == true;

    /// <inheritdoc />
    public string? LastError { get; private set; }

    // ---------------- 搜索 ----------------

    /// <inheritdoc />
    public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        ContentKind kind,
        string query,
        string? gameVersion,
        LoaderKind loader,
        int offset,
        int limit,
        CancellationToken cancellationToken = default) =>
        SearchSortedAsync(kind, query, gameVersion, loader, offset, limit, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModrinthProject>> SearchSortedAsync(
        ContentKind kind,
        string query,
        string? gameVersion,
        LoaderKind loader,
        int offset,
        int limit,
        string? sort,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (kind == ContentKind.World)
        {
            LastError = "世界存档不支持在线搜索。";
            return Array.Empty<ModrinthProject>();
        }

        var url = BuildSearchUrl(kind, query, gameVersion, loader, offset, limit, sort);
        var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return Array.Empty<ModrinthProject>();
        }

        try
        {
            return ParseSearch(json, kind);
        }
        catch (JsonException ex)
        {
            _log?.Error($"Modrinth 搜索结果解析失败：{url}", ex);
            LastError = "Modrinth 返回的内容无法解析，可能是接口有变化。";
            return Array.Empty<ModrinthProject>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            LastError = "缺少项目 ID，无法查询版本。";
            return Array.Empty<ModrinthVersion>();
        }

        var url = BuildVersionUrl(projectId, gameVersion, loader);
        var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return Array.Empty<ModrinthVersion>();
        }

        try
        {
            var versions = ParseVersions(json);

            // 过滤后为空不是错误：说明这个项目没有适配当前游戏版本 / 加载器的构建。
            return versions;
        }
        catch (JsonException ex)
        {
            _log?.Error($"Modrinth 版本列表解析失败：{url}", ex);
            LastError = "Modrinth 返回的内容无法解析，可能是接口有变化。";
            return Array.Empty<ModrinthVersion>();
        }
    }

    // ---------------- 项目详情 ----------------

    /// <inheritdoc />
    public async Task<ModrinthProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            LastError = "缺少项目 ID，无法查询详情。";
            return null;
        }

        var key = projectId.Trim();

        if (TryGetCachedProject(key) is { } cached)
        {
            return cached;
        }

        var url = $"{ApiBase}/project/{Uri.EscapeDataString(key)}";
        var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            return null;
        }

        try
        {
            var project = ParseProject(json);

            // 详情缺 id / slug / title 时没法和搜索结果里的卡片对上号，当作解析失败处理更诚实。
            if (project is null)
            {
                _log?.Warn($"Modrinth 项目详情缺少必需字段：{url}");
                LastError = "Modrinth 返回的项目详情不完整，可能是接口有变化。";
                return null;
            }

            RememberProject(key, project);
            return project;
        }
        catch (JsonException ex)
        {
            _log?.Error($"Modrinth 项目详情解析失败：{url}", ex);
            LastError = "Modrinth 返回的内容无法解析，可能是接口有变化。";
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(
        IReadOnlyList<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var ids = projectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
        {
            return Array.Empty<ModrinthProject>();
        }

        var results = new List<ModrinthProject>(ids.Count);

        // Modrinth 对 ids 的数量没有明确上限，但 URL 长度有；100 个一包与搜索分页保持一致。
        foreach (var chunk in ids.Chunk(MaxPageSize))
        {
            var url = $"{ApiBase}/projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(chunk))}";
            var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

            if (json is null)
            {
                // 逐个查有镜像兜底，而批量只有官方地址。继续走下一包没有意义，交给调用方回退。
                continue;
            }

            try
            {
                results.AddRange(ParseProjects(json));
            }
            catch (JsonException ex)
            {
                _log?.Error($"Modrinth 批量项目信息解析失败：{url}", ex);
                LastError = "Modrinth 返回的内容无法解析，可能是接口有变化。";
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> LookupBySha1BatchAsync(
        IReadOnlyList<string> sha1s,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var hashes = sha1s
            .Where(sha1 => !string.IsNullOrWhiteSpace(sha1))
            .Select(sha1 => sha1.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var results = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);

        if (hashes.Count == 0)
        {
            return results;
        }

        for (var offset = 0; offset < hashes.Count; offset += MaxPageSize)
        {
            var chunk = hashes.Skip(offset).Take(MaxPageSize).ToList();
            var payload = JsonSerializer.Serialize(new { hashes = chunk, algorithm = "sha1" });
            var json = await PostJsonAsync($"{ApiBase}/version_files", payload, cancellationToken).ConfigureAwait(false);

            if (json is not null)
            {
                try
                {
                    ParseSha1Batch(json, results);
                    continue;
                }
                catch (JsonException ex)
                {
                    _log?.Error("Modrinth 批量哈希反查解析失败", ex);
                    LastError = "Modrinth 返回的内容无法解析，可能是接口有变化。";
                }
            }

            // 批量端点只有官方地址；失败时退回逐个查询，那条路径带镜像兜底。
            // 慢是慢一点，但「整页模组都没有名字」比「慢 3 秒」难解释得多。
            foreach (var sha1 in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hit = await LookupBySha1Async(sha1, cancellationToken).ConfigureAwait(false);

                if (hit is not null)
                {
                    results[sha1] = hit;
                }
            }
        }

        return results;
    }

    /// <summary><c>POST /v2/version_files</c> 的响应是一个「小写 SHA1 → 版本」的对象。</summary>
    private static void ParseSha1Batch(string json, Dictionary<string, ModrinthVersion> results)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var version = ParseVersionElement(entry.Value);

            if (version is not null)
            {
                results[entry.Name.ToLowerInvariant()] = version;
            }
        }
    }

    /// <summary>
    /// 只打官方的 JSON POST。批量接口没有镜像兜底：BMCLAPI 的 Modrinth 镜像不保证
    /// 实现 <c>/version_files</c>，把它写进候选会让失败路径变成「镜像 404 也算失败」，
    /// 反而掩盖真实的网络问题。
    /// </summary>
    private async Task<string?> PostJsonAsync(string url, string payload, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(SearchTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            _log?.Warn($"Modrinth 批量请求失败：HTTP {status} {url}");
            LastError = status switch
            {
                429 => "Modrinth 请求过于频繁（HTTP 429），请稍后再试。",
                >= 500 => $"Modrinth 服务暂时不可用（HTTP {status}），请稍后再试。",
                _ => $"Modrinth 批量请求失败（HTTP {status}）。",
            };
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Warn($"Modrinth 批量请求超时：{url}");
            LastError = "连接 Modrinth 超时，请检查网络后重试。";
            return null;
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"Modrinth 批量请求网络失败：{url}", ex);
            LastError = "无法连接 Modrinth，请检查网络或代理设置。";
            return null;
        }
    }

    private ModrinthProject? TryGetCachedProject(string key)
    {        lock (_detailCacheGate)
        {
            if (!_detailCache.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - entry.StoredAt > DetailCacheLifetime)
            {
                _detailCache.Remove(key);
                return null;
            }

            return entry.Project;
        }
    }

    private void RememberProject(string key, ModrinthProject project)
    {
        lock (_detailCacheGate)
        {
            if (_detailCache.Count >= MaxDetailCacheEntries)
            {
                var now = DateTimeOffset.UtcNow;

                foreach (var stale in _detailCache
                             .Where(entry => now - entry.Value.StoredAt > DetailCacheLifetime)
                             .Select(entry => entry.Key)
                             .ToList())
                {
                    _detailCache.Remove(stale);
                }
            }

            // 仍然满（32 个都是 5 分钟内写进来的）时整体丢弃：再挑一次就会重新拉，不会给出过期数据。
            if (_detailCache.Count >= MaxDetailCacheEntries)
            {
                _detailCache.Clear();
            }

            _detailCache[key] = (DateTimeOffset.UtcNow, project);
        }
    }

    // ---------------- 哈希反查 ----------------

    /// <inheritdoc />
    public async Task<ModrinthVersion?> LookupBySha1Async(string sha1, CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(sha1))
        {
            LastError = "缺少文件哈希，无法反查。";
            return null;
        }

        var url = $"{ApiBase}/version_file/{Uri.EscapeDataString(sha1.Trim())}?algorithm=sha1";
        var json = await GetStringAsync(url, SearchTimeout, cancellationToken).ConfigureAwait(false);

        if (json is null)
        {
            // 404 表示这个文件没有被 Modrinth 收录（用户手工拖进来的 jar 是常态），
            // 不是网络错误。清掉 LastError，让调用方把状态渲染成「未识别」而不是「失败」。
            if (LastError is not null && LastError.Contains("404", StringComparison.Ordinal))
            {
                LastError = null;
            }

            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            return ParseVersionElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            _log?.Error($"Modrinth 哈希反查解析失败：{sha1}", ex);
            LastError = "Modrinth 返回的内容无法解析，可能是接口有变化。";
            return null;
        }
    }

    // ---------------- 安装 ----------------

    /// <inheritdoc />
    public async Task<ModrinthInstallResult> InstallAsync(
        ModrinthVersion version,
        string gameDirectory,
        ContentKind kind,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (version is null)
        {
            return ModrinthInstallResult.Fail("没有选择要安装的版本。");
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return ModrinthInstallResult.Fail("游戏目录为空，请先在设置页指定 .minecraft 路径。");
        }

        if (kind == ContentKind.World)
        {
            return ModrinthInstallResult.Fail("世界存档不支持在线安装。");
        }

        // 服务端插件在这里就停住，绝不落盘：它的 jar 与普通模组同名同扩展，下游任何一步
        // （改名、校验、写入 mods/）都没有能力再把它区分出来，客户端只会在启动时崩。
        if (ServerPluginPolicy.IsServerPlugin(kind))
        {
            _log?.Warn($"拒绝安装服务端插件：{version.FileName}");
            return ModrinthInstallResult.Fail(ServerPluginPolicy.InstallRejectedMessage);
        }

        if (string.IsNullOrWhiteSpace(version.DownloadUrl))
        {
            return ModrinthInstallResult.Fail("这个版本没有可下载的文件。");
        }

        var fileName = SanitizeFileName(version.FileName);
        if (fileName.Length == 0)
        {
            return ModrinthInstallResult.Fail("服务端给出的文件名不合法，已取消安装。");
        }

        var targetDirectory = Path.Combine(gameDirectory, ContentService.DirectoryNameFor(kind));

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            _log?.Error($"创建内容目录失败：{targetDirectory}", ex);
            return ModrinthInstallResult.Fail($"无法创建目录：{targetDirectory}");
        }

        var targetPath = ResolveTargetPath(targetDirectory, fileName);
        var renamed = !string.Equals(Path.GetFileName(targetPath), fileName, StringComparison.Ordinal);
        var partialPath = targetPath + ".part";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallTimeout);
        var token = timeout.Token;

        try
        {
            using var response = await OpenDownloadAsync(version.DownloadUrl, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _log?.Warn($"Modrinth 下载失败：HTTP {status} {version.DownloadUrl}");
                var message = status switch
                {
                    404 => "下载地址已失效（HTTP 404），请换一个版本。",
                    403 => "下载被拒绝（HTTP 403），可能需要在浏览器里确认。",
                    429 => "Modrinth 限流中（HTTP 429），请稍后再试。",
                    _ => $"下载失败（HTTP {status}）。",
                };
                LastError = message;
                return ModrinthInstallResult.Fail(message);
            }

            var totalBytes = response.Content.Headers.ContentLength ?? version.FileSize;
            long received = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long lastReportBytes = 0;

            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             partialPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    sha1.AppendData(buffer, 0, read);
                    received += read;

                    var elapsed = stopwatch.Elapsed;

                    // 小文件也会走完整个循环，所以节流同时看字节数与时间。
                    if (received - lastReportBytes >= ProgressChunkBytes || elapsed - lastReport >= ProgressInterval)
                    {
                        Report(progress, totalBytes, received, elapsed, fileName);
                        lastReport = elapsed;
                        lastReportBytes = received;
                    }
                }
            }

            var actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(version.Sha1) &&
                !string.Equals(actualSha1, version.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partialPath);
                _log?.Error($"SHA1 校验失败：{fileName} 期望 {version.Sha1}，实际 {actualSha1}");
                LastError = "下载文件的 SHA1 校验失败，文件已丢弃。可能是网络劫持或代理返回了错误内容。";
                return ModrinthInstallResult.Fail(LastError);
            }

            // 下载期间目标路径可能被别的安装占用（同一版本连点两次），落盘前再确认一次。
            if (File.Exists(targetPath))
            {
                targetPath = ResolveTargetPath(targetDirectory, fileName);
                renamed = !string.Equals(Path.GetFileName(targetPath), fileName, StringComparison.Ordinal);
            }

            File.Move(partialPath, targetPath, overwrite: false);

            progress?.Report(new DownloadProgress(1, 1, 0, received, received, 0, fileName, 1));
            _log?.Info($"已安装 {fileName} → {targetPath}");

            return ModrinthInstallResult.Ok(targetPath, received, renamed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partialPath);
            _log?.Info($"已取消安装：{fileName}");
            LastError = "已取消。";
            return ModrinthInstallResult.Fail(LastError);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            _log?.Warn($"Modrinth 下载超时：{fileName}");
            LastError = $"下载超时（{(int)InstallTimeout.TotalMinutes} 分钟），请重试。";
            return ModrinthInstallResult.Fail(LastError);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partialPath);
            _log?.Error($"Modrinth 下载连接失败：{fileName}", ex);
            LastError = "无法连接 Modrinth 下载服务器，请检查网络或代理设置。";
            return ModrinthInstallResult.Fail(LastError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            _log?.Error($"写入模组文件失败：{targetPath}", ex);
            LastError = $"写入失败：{ex.Message}";
            return ModrinthInstallResult.Fail(LastError);
        }
    }

    // ---------------- 依赖计划 ----------------

    /// <inheritdoc />
    public async Task<ModrinthInstallPlan> PlanInstallAsync(
        ModrinthVersion root,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        LastError = null;

        var planner = new ModrinthInstallPlanner(GetVersionsAsync, _log);

        try
        {
            var plan = await planner.PlanAsync(root, gameVersion, loader, cancellationToken).ConfigureAwait(false);

            // 依赖查询期间的失败已经写进 plan.Warnings（用户能在确认框里逐条看到），
            // 这里清掉 LastError，否则资源页会把「某个前置没查到」渲染成整页错误并清空列表。
            LastError = null;
            return plan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastError = "已取消。";
            throw;
        }
        catch (Exception ex)
        {
            _log?.Error($"Modrinth 依赖解析失败：{root.ProjectId}", ex);
            LastError = "解析前置失败，只安装本体。";

            // 不阻断：依赖解析是增强能力，失败时退回「只装本体」的旧行为，用户仍可手动补前置。
            return new ModrinthInstallPlan
            {
                Root = root,
                Warnings = new[] { LastError },
            };
        }
    }

    /// <inheritdoc />
    public async Task<ModrinthPlanInstallResult> InstallPlanAsync(
        ModrinthInstallPlan plan,
        string gameDirectory,
        ContentKind kind,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanInstall)
        {
            return new ModrinthPlanInstallResult
            {
                Success = false,
                Error = "存在阻断原因，未执行安装：" + string.Join("；", plan.Blocked),
            };
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new ModrinthPlanInstallResult
            {
                Success = false,
                Error = "游戏目录为空，请先在设置页指定 .minecraft 路径。",
            };
        }

        // 在这里一次性早退，而不是让下面的循环逐个失败：一批插件会刷出几十条同样的错误行，
        // 用户以为「有一部分装上了」而实际一个都没有。
        if (ServerPluginPolicy.IsServerPlugin(kind))
        {
            _log?.Warn($"拒绝按计划安装服务端插件：{plan.Root.FileName}");
            return new ModrinthPlanInstallResult
            {
                Success = false,
                Error = ServerPluginPolicy.InstallRejectedMessage,
            };
        }

        // 前置先装、本体最后：本体启动时立刻会找前置，反过来的中间态是跑不起来的。
        var queue = new List<(ModrinthVersion Version, bool IsDependency)>(plan.TotalFiles);
        queue.AddRange(plan.Required.Select(version => (version, true)));
        queue.Add((plan.Root, false));

        var files = new List<ModrinthPlanFile>(queue.Count);
        long completedBytes = 0;

        for (var index = 0; index < queue.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (version, isDependency) = queue[index];

            // 逐文件独立成败：一个前置失败不该把其余文件一起废掉，用户至少能拿到能装的那部分。
            var batchProgress = progress is null
                ? null
                : new PlanBatchProgress(
                    progress,
                    queue.Count,
                    index,
                    plan.TotalBytes,
                    completedBytes,
                    files.Count(file => file.Error is not null));

            var result = await InstallAsync(version, gameDirectory, kind, batchProgress, cancellationToken).ConfigureAwait(false);

            files.Add(new ModrinthPlanFile
            {
                Version = version,
                IsDependency = isDependency,
                FilePath = result.Success ? result.FilePath : null,
                Bytes = result.Success ? result.Bytes : 0,
                Error = result.Success ? null : (string.IsNullOrWhiteSpace(result.Error) ? "安装失败。" : result.Error),
            });

            if (result.Success)
            {
                completedBytes += result.Bytes;
            }
        }

        progress?.Report(new DownloadProgress(
            TotalFiles: queue.Count,
            CompletedFiles: files.Count(file => file.Error is null),
            FailedFiles: files.Count(file => file.Error is not null),
            TotalBytes: plan.TotalBytes,
            DownloadedBytes: completedBytes,
            SpeedBytesPerSecond: 0,
            CurrentFile: null,
            Ratio: 1));

        // 逐文件失败不写整体 Error（模型约定：Error 只表示准备阶段失败），UI 按行展示。
        return new ModrinthPlanInstallResult
        {
            Success = files.All(file => file.Error is null),
            Files = files,
        };
    }

    // ---------------- 图标缓存 ----------------

    /// <inheritdoc />
    public async Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || _paths is null)
        {
            return null;
        }

        string cacheDirectory;
        string targetPath;

        try
        {
            // 文件名取 URL 的内容哈希：同一个图标只会存一份，且不依赖 URL 里的字符。
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()[..20];
            cacheDirectory = Path.Combine(_paths.CacheDirectory, "modrinth-icons");
            targetPath = Path.Combine(cacheDirectory, hash + ExtensionOf(url));

            if (File.Exists(targetPath))
            {
                return targetPath;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("准备图标缓存路径失败", ex);
            return null;
        }

        var partialPath = targetPath + ".part";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(IconTimeout);

            using var response = await OpenDownloadAsync(url, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn($"图标下载失败：HTTP {(int)response.StatusCode} {url}");
                return null;
            }

            Directory.CreateDirectory(cacheDirectory);

            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await source.CopyToAsync(destination, timeout.Token).ConfigureAwait(false);
            }

            File.Move(partialPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            // 图标只是展示增强，失败一律降级为首字母占位，不设 LastError。
            TryDelete(partialPath);
            _log?.Debug($"图标缓存跳过：{url}（{ex.GetType().Name}）");
            return null;
        }
    }

    // ---------------- 请求 ----------------

    /// <summary>
    /// 打开文件下载流，按候选表走（官方在前，镜像开关打开时镜像在后）。
    ///
    /// 与 <see cref="GetStringAsync"/> 相同的判据：官方网络失败 / 429 / 5xx 才换镜像；
    /// 404 / 403 直接返回给调用方判死（文件本来就不存在，换镜像只是多等一轮）。
    /// </summary>
    private async Task<HttpResponseMessage> OpenDownloadAsync(string url, CancellationToken cancellationToken)
    {
        var candidates = DownloadMirror.ModrinthCandidates(url, UseModrinthMirror);

        if (candidates.Count == 0)
        {
            throw new HttpRequestException($"下载地址为空：{url}");
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            HttpResponseMessage response;

            try
            {
                response = await _http
                    .GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                if (index + 1 < candidates.Count)
                {
                    _log?.Warn($"Modrinth 下载失败，改用镜像重试：{candidate}", ex);
                    continue;
                }

                throw;
            }

            if (index + 1 < candidates.Count && IsWorthMirror(response.StatusCode))
            {
                _log?.Warn($"Modrinth 下载返回 HTTP {(int)response.StatusCode}，改用镜像重试：{candidate}");
                response.Dispose();
                continue;
            }

            return response;
        }

        throw new HttpRequestException($"下载失败（已尝试 {candidates.Count} 个地址）：{url}");
    }

    /// <summary>这个状态码是否属于「换镜像可能有用」：服务端故障或限流，而不是「文件不存在」。</summary>
    private static bool IsWorthMirror(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// 发 GET 并读成字符串，按候选表走：官方在前，镜像（开关打开时）在后。
    ///
    /// 只有官方**连不上**（网络异常 / 超时 / 429 / 5xx）才换镜像；官方 404 / 403 表示资源本来就不存在，
    /// 立刻失败，不白等一轮镜像超时——否则每查一个不存在的项目都要多等一个超时周期。
    /// </summary>
    private async Task<string?> GetStringAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var candidates = DownloadMirror.ModrinthCandidates(url, UseModrinthMirror);

        if (candidates.Count == 0)
        {
            LastError = "请求地址为空。";
            return null;
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var isMirror = index > 0;

            // 镜像只试一轮：官方已经等过「超时 + 重试」，镜像再重试会让一次搜索拖到一分钟以上。
            var (outcome, body) = await FetchAsync(candidate, timeout, allowRetry: !isMirror, cancellationToken)
                .ConfigureAwait(false);

            if (outcome == FetchOutcome.Success)
            {
                LastError = null;

                if (isMirror)
                {
                    _log?.Info($"Modrinth 官方不可达，镜像返回成功：{candidate}");
                }

                return body;
            }

            // 404 / 403：资源不存在，换镜像也问不到。
            if (outcome == FetchOutcome.Missing)
            {
                return null;
            }

            if (!isMirror && index + 1 < candidates.Count)
            {
                _log?.Warn($"Modrinth 官方请求失败，改用镜像重试：{url}");
                continue;
            }

            if (isMirror)
            {
                LastError = "Modrinth 官方与镜像都无法连接，请检查网络或代理设置。";
            }

            break;
        }

        return null;
    }

    /// <summary>一次候选地址的请求结果。</summary>
    private enum FetchOutcome
    {
        /// <summary>拿到 2xx 响应。</summary>
        Success,

        /// <summary>该地址上确实没有这个资源（404 / 403 等）。换镜像也问不到，不该重试。</summary>
        Missing,

        /// <summary>网络层失败、超时、429 或 5xx：换下一个候选才有意义。</summary>
        Unavailable,
    }

    /// <summary>
    /// 请求单个地址。2xx 返回正文；4xx 判死（<see cref="FetchOutcome.Missing"/>）；
    /// 429 / 5xx / 超时 / 连接失败在 <paramref name="allowRetry"/> 为真时重试一次，
    /// 仍失败返回 <see cref="FetchOutcome.Unavailable"/> 并写好 <see cref="LastError"/>。
    /// </summary>
    private async Task<(FetchOutcome Outcome, string? Body)> FetchAsync(
        string url,
        TimeSpan timeout,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return (FetchOutcome.Success,
                        await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false));
                }

                var status = (int)response.StatusCode;

                // 429 与 5xx 是「这次服务端不行」，还值得换镜像；其余 4xx 是「这个资源不存在」。
                var retryable = status == 429 || status >= 500;

                if (retryable && allowRetry && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? RetryDelay;
                    _log?.Warn($"Modrinth 返回 HTTP {status}，稍后重试一次：{url}");
                    await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _log?.Warn($"Modrinth 请求失败：HTTP {status} {url}");
                LastError = status switch
                {
                    404 => "Modrinth 上找不到这个项目（HTTP 404）。",
                    429 => "Modrinth 请求过于频繁（HTTP 429），请稍后再试。",
                    >= 500 => $"Modrinth 服务暂时不可用（HTTP {status}），请稍后再试。",
                    _ => $"Modrinth 请求失败（HTTP {status}）。",
                };
                return (retryable ? FetchOutcome.Unavailable : FetchOutcome.Missing, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 自身超时：不是用户取消，按可重试处理一次。
                _log?.Warn($"Modrinth 请求超时（{(int)timeout.TotalSeconds} 秒）：{url}");

                if (allowRetry && attempt == 0)
                {
                    await DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LastError = $"连接 Modrinth 超时（{(int)timeout.TotalSeconds} 秒），请检查网络后重试。";
                return (FetchOutcome.Unavailable, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _log?.Warn($"Modrinth 网络请求失败：{url}", ex);

                if (allowRetry && attempt == 0)
                {
                    await DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LastError = "无法连接 Modrinth，请检查网络或代理设置。";
                return (FetchOutcome.Unavailable, null);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // 服务端可能给出很大的 Retry-After，夹到 3 秒内，避免页面卡住。
        var clamped = delay > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        await Task.Delay(clamped, cancellationToken).ConfigureAwait(false);
    }

    // ---------------- URL 与 facets ----------------

    /// <summary>拼搜索 URL。未指定 <paramref name="sort"/> 时：<paramref name="query"/> 为空按下载量（浏览热门），
    /// 有查询按相关度。</summary>
    /// <param name="sort">
    /// 排序键，取 Modrinth 的 <c>index</c> 值：<c>relevance</c> / <c>downloads</c> / <c>follows</c> /
    /// <c>newest</c> / <c>updated</c>。null、空串或无法识别的值都回落到默认规则。
    /// </param>
    public static string BuildSearchUrl(
        ContentKind kind,
        string? query,
        string? gameVersion,
        LoaderKind loader,
        int offset,
        int limit,
        string? sort = null)
    {
        var text = (query ?? string.Empty).Trim();
        var index = NormalizeSortIndex(sort, text.Length > 0);
        var facets = BuildSearchFacets(kind, gameVersion, loader);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ApiBase}/search?query={Uri.EscapeDataString(text)}&facets={Uri.EscapeDataString(facets)}&index={index}&offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, MaxPageSize)}");
    }

    /// <summary>
    /// 把界面上的排序选择翻译成 Modrinth 的 <c>index</c> 值。
    /// 白名单而不是直接拼：<c>index</c> 是查询串的一部分，塞进未校验的字符串等于给自己开一个注入口。
    /// </summary>
    public static string NormalizeSortIndex(string? sort, bool hasQuery)
    {
        var fallback = hasQuery ? "relevance" : "downloads";

        return (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "relevance" => hasQuery ? "relevance" : fallback,
            "downloads" => "downloads",
            "follows" => "follows",
            "newest" => "newest",
            "updated" => "updated",
            _ => fallback,
        };
    }

    /// <summary>
    /// 拼 facets。格式是「数组的数组」，外层每个元素是一组 OR 条件，组之间是 AND。
    /// 实测：<c>[[\"project_type:mod\"],[\"versions:1.20.1\"],[\"categories:fabric\"]]</c>。
    /// </summary>
    public static string BuildSearchFacets(ContentKind kind, string? gameVersion, LoaderKind loader)
    {
        var groups = new List<string>(3)
        {
            "[\"project_type:" + ModrinthProject.ProjectTypeOf(kind) + "\"]",
        };

        var version = gameVersion?.Trim();
        if (!string.IsNullOrEmpty(version))
        {
            groups.Add("[\"versions:" + EscapeFacetValue(version) + "\"]");
        }

        // 资源包 / 光影包没有「加载器」概念，它们的 loaders 统一是 minecraft（光影是 iris/optifine），
        // 按实际游戏加载器过滤会把结果错误清空，所以只在 Mod 上过滤。
        var category = LoaderCategoryOf(loader);
        if (kind == ContentKind.Mod && category is not null)
        {
            groups.Add("[\"categories:" + category + "\"]");
        }

        return "[" + string.Join(",", groups) + "]";
    }

    /// <summary>拼版本列表 URL。资源包 / 光影包不传 loaders。</summary>
    public static string BuildVersionUrl(string projectId, string? gameVersion, LoaderKind loader)
    {
        var query = new List<string>(2);
        var version = gameVersion?.Trim();

        if (!string.IsNullOrEmpty(version))
        {
            query.Add("game_versions=" + Uri.EscapeDataString("[\"" + EscapeFacetValue(version) + "\"]"));
        }

        var loaderName = LoaderSlugOf(loader);
        if (loaderName is not null)
        {
            query.Add("loaders=" + Uri.EscapeDataString("[\"" + loaderName + "\"]"));
        }

        var suffix = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
        return $"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version{suffix}";
    }

    /// <summary>游戏加载器 → Modrinth 的 categories / loaders 取值。Vanilla 表示不过滤。</summary>
    public static string? LoaderCategoryOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    private static string? LoaderSlugOf(LoaderKind loader) => LoaderCategoryOf(loader);

    /// <summary>facets 值里不允许出现引号与反斜杠，出现就丢弃，避免把 JSON 结构弄坏。</summary>
    private static string EscapeFacetValue(string value) =>
        new string(value.Where(ch => ch is not ('"' or '\\')).ToArray());

    // ---------------- JSON 解析 ----------------

    /// <summary>解析 <c>GET /v2/search</c> 的响应。<paramref name="kind"/> 只用于回填项目类型。</summary>
    public static IReadOnlyList<ModrinthProject> ParseSearch(string json, ContentKind kind)
    {
        var results = new List<ModrinthProject>();

        using var document = JsonDocument.Parse(json, JsonOptions);

        if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var hit in hits.EnumerateArray())
        {
            var projectId = GetString(hit, "project_id");
            var slug = GetString(hit, "slug");
            var title = GetString(hit, "title");

            if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title))
            {
                continue;
            }

            results.Add(new ModrinthProject
            {
                ProjectId = projectId,
                Slug = slug,
                Title = title,
                Description = GetString(hit, "description") ?? string.Empty,
                Author = GetString(hit, "author"),
                Downloads = GetInt64(hit, "downloads"),
                Follows = GetInt64(hit, "follows"),
                IconUrl = GetString(hit, "icon_url"),
                Categories = GetStringArray(hit, "display_categories", "categories"),
                GameVersions = GetStringArray(hit, "versions"),
                UpdatedAt = ParseTimestamp(hit, "date_modified"),

                // 判定读原始 categories 而不是 display_categories：Modrinth 会把 loaders 从展示分类里
                // 过滤掉，只看 display_categories 永远判不出插件，jar 最后就进了客户端 mods/。
                Kind = ResolveProjectKind(kind, GetStringArray(hit, "categories", "display_categories")),
            });
        }

        return results;
    }

    /// <summary>
    /// 解析 <c>GET /v2/project/{id}</c> 的响应。
    ///
    /// 注意详情接口与搜索响应的字段名不同：分类是 <c>categories</c>（搜索是 <c>display_categories</c>），
    /// 支持的游戏版本是 <c>game_versions</c>（搜索是 <c>versions</c>），关注数是 <c>followers</c>（搜索是 <c>follows</c>），
    /// 修改时间是 <c>updated</c>（搜索是 <c>date_modified</c>）。按搜索那套字段名读详情会静默拿到空集合。
    /// </summary>
    public static ModrinthProject? ParseProject(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);

        return ParseProjectElement(document.RootElement);
    }

    /// <summary>
    /// 解析 <c>GET /v2/projects?ids=[...]</c> 的数组响应（元素结构与详情接口相同）。
    /// 单条不合格就跳过，不影响其余条目——一个坏项目不该让整页模组都没有名字。
    /// </summary>
    public static IReadOnlyList<ModrinthProject> ParseProjects(string json)
    {
        var results = new List<ModrinthProject>();

        using var document = JsonDocument.Parse(json, JsonOptions);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var project = ParseProjectElement(item);

            if (project is not null)
            {
                results.Add(project);
            }
        }

        return results;
    }

    private static ModrinthProject? ParseProjectElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var projectId = GetString(root, "id");
        var slug = GetString(root, "slug");
        var title = GetString(root, "title");

        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title))
        {
            return null;
        }

        return new ModrinthProject
        {
            ProjectId = projectId,
            Slug = slug,
            Title = title,
            Description = GetString(root, "description") ?? string.Empty,
            Downloads = GetInt64(root, "downloads"),
            Follows = GetInt64(root, "followers"),
            IconUrl = GetString(root, "icon_url"),
            Categories = GetStringArray(root, "categories"),
            GameVersions = GetStringArray(root, "game_versions"),
            UpdatedAt = ParseTimestamp(root, "updated"),

            // 详情接口同时给 project_type 与 loaders：插件项目的 project_type 是 mod，
            // 只有 loaders 里才写着 paper/spigot，所以两处都要喂给三段判定。
            Kind = ResolveProjectKind(
                ParseProjectKind(GetString(root, "project_type")),
                [.. GetStringArray(root, "loaders"), .. GetStringArray(root, "categories")]),
            Body = GetString(root, "body") ?? string.Empty,
            GalleryUrls = ParseGalleryUrls(root),
            IssuesUrl = NormalizeUrl(GetString(root, "issues_url")),
            SourceUrl = NormalizeUrl(GetString(root, "source_url")),
            LicenseId = ParseLicenseId(root),
        };
    }

    /// <summary><c>project_type</c> → 枚举。未知 / 缺失按模组处理，与搜索默认值一致。</summary>
    public static ContentKind ParseProjectKind(string? projectType) => projectType?.Trim().ToLowerInvariant() switch
    {
        "resourcepack" => ContentKind.ResourcePack,
        "shader" => ContentKind.ShaderPack,
        "datapack" => ContentKind.DataPack,

        // 官方 API 目前对插件项目报 mod，但网站与部分镜像会直接给 plugin，两条都要认。
        "plugin" => ContentKind.Plugin,
        _ => ContentKind.Mod,
    };

    /// <summary>
    /// 服务端插件的加载器标志。命中任意一个就说明这个 jar 只能在服务端跑。
    ///
    /// 取值来自 Modrinth 的 loader 清单（PCL2 <c>ResourceVersion.vb:189-195</c> 用的是同一组）。
    /// </summary>
    private static readonly string[] ServerPluginLoaders = ["bukkit", "folia", "paper", "purpur", "spigot"];

    /// <summary>
    /// PCL2 <c>ResourceVersion.vb:189-195</c> 的三段判定，落成纯函数便于单测：
    /// <list type="number">
    /// <item>加载器里有 <c>bukkit</c> / <c>folia</c> / <c>paper</c> / <c>purpur</c> / <c>spigot</c> → 插件。</item>
    /// <item>资源包 / 光影包不改判（它们的 categories 不含加载器，硬套规则会把它们全变成模组）。</item>
    /// <item>categories 里有 <c>datapack</c> → 数据包。</item>
    /// <item>其余保持调用方请求的类型（通常是模组）。</item>
    /// </list>
    ///
    /// 顺序不能换：插件的 categories 里同样可能出现 <c>datapack</c> 之外的杂项分类，
    /// 而「先判插件」是唯一能保证不把 jar 塞进客户端 <c>mods/</c> 的顺序。
    /// </summary>
    /// <param name="requested">调用方请求的类型（搜索标签 / 详情接口的 project_type）。</param>
    /// <param name="hints">项目的 <c>loaders</c> 与 <c>categories</c> 合并后的原始字符串。</param>
    public static ContentKind ResolveProjectKind(ContentKind requested, IReadOnlyList<string>? hints)
    {
        if (hints is null || hints.Count == 0)
        {
            return requested;
        }

        foreach (var hint in hints)
        {
            if (Array.Exists(ServerPluginLoaders, loader => string.Equals(loader, hint?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return ContentKind.Plugin;
            }
        }

        if (requested is ContentKind.ResourcePack or ContentKind.ShaderPack or ContentKind.Plugin)
        {
            return requested;
        }

        foreach (var hint in hints)
        {
            if (string.Equals(hint?.Trim(), "datapack", StringComparison.OrdinalIgnoreCase))
            {
                return ContentKind.DataPack;
            }
        }

        return requested;
    }

    /// <summary><c>gallery[]</c> 里只取 <c>url</c>；顺序是作者排的，原样保留。</summary>
    private static IReadOnlyList<string> ParseGalleryUrls(JsonElement root)
    {
        if (!root.TryGetProperty("gallery", out var gallery) || gallery.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var urls = new List<string>(gallery.GetArrayLength());

        foreach (var item in gallery.EnumerateArray())
        {
            var url = NormalizeUrl(GetString(item, "url"));

            if (url is not null)
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    private static string? ParseLicenseId(JsonElement root)
    {
        if (!root.TryGetProperty("license", out var license) || license.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return NormalizeId(GetString(license, "id"));
    }

    /// <summary>
    /// 解析 <c>GET /v2/project/{id}/version</c> 的响应，按发布时间降序。
    ///
    /// 每个版本同时带出 <c>dependencies[]</c>：依赖解析（<see cref="ModrinthInstallPlanner"/>）
    /// 直接吃这里的解析结果，不再单独发请求。
    /// </summary>
    public static IReadOnlyList<ModrinthVersion> ParseVersions(string json)
    {
        var results = new List<ModrinthVersion>();

        using var document = JsonDocument.Parse(json, JsonOptions);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var version = ParseVersionElement(item);

            if (version is not null)
            {
                results.Add(version);
            }
        }

        return results
            .OrderByDescending(version => version.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>
    /// 解析单个版本对象。列表接口与「按哈希反查」接口返回的形状一致，
    /// 所以两个入口共用这一份映射，避免字段增减时只改一处。
    /// </summary>
    public static ModrinthVersion? ParseVersionElement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(item, "id");
        var projectId = GetString(item, "project_id");

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(projectId))
        {
            return null;
        }

        // 一个版本可能挂多个文件（sources / dev jar），只装 primary 那个。
        if (!TrySelectPrimaryFile(item, out var file))
        {
            return null;
        }

        var versionNumber = GetString(item, "version_number") ?? id;

        return new ModrinthVersion
        {
            Id = id,
            ProjectId = projectId,
            Name = GetString(item, "name") ?? versionNumber,
            VersionNumber = versionNumber,
            VersionType = GetString(item, "version_type") ?? "release",
            PublishedAt = ParseTimestamp(item, "date_published"),
            Downloads = GetInt64(item, "downloads"),
            GameVersions = GetStringArray(item, "game_versions"),
            Loaders = GetStringArray(item, "loaders"),
            FileName = GetString(file, "filename") ?? string.Empty,
            DownloadUrl = GetString(file, "url") ?? string.Empty,
            FileSize = GetInt64(file, "size"),
            Sha1 = file.TryGetProperty("hashes", out var hashes) ? GetString(hashes, "sha1") : null,
            Changelog = GetString(item, "changelog"),
            Dependencies = ParseDependencies(item),
        };
    }

    /// <summary>
    /// 解析 <c>dependencies[]</c>。
    ///
    /// Modrinth 允许一条依赖只给 <c>version_id</c>（精确到版本），这时拿不到项目 Id，
    /// 递归解析的请求量会成倍放大；<see cref="ModrinthInstallPlanner"/> 据此把它降级成提示。
    /// </summary>
    private static IReadOnlyList<ModrinthDependency> ParseDependencies(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ModrinthDependency>();
        }

        var results = new List<ModrinthDependency>(dependencies.GetArrayLength());

        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (dependency.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var versionId = NormalizeId(GetString(dependency, "version_id"));
            var projectId = NormalizeId(GetString(dependency, "project_id"));

            // 两个 Id 都没有的条目定位不到任何东西，留着只会污染依赖图。
            if (versionId is null && projectId is null)
            {
                continue;
            }

            results.Add(new ModrinthDependency(
                versionId,
                projectId,
                NormalizeId(GetString(dependency, "file_name")),
                ParseDependencyType(GetString(dependency, "dependency_type"))));
        }

        return results;
    }

    /// <summary>
    /// <c>dependency_type</c> → 枚举。未知取值按 <see cref="ModrinthDependencyType.Required"/> 处理：
    /// 漏装前置会崩在启动期，多装一个最多多占几 MB，保守方向只能是必装。
    /// </summary>
    public static ModrinthDependencyType ParseDependencyType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "optional" => ModrinthDependencyType.Optional,
        "incompatible" => ModrinthDependencyType.Incompatible,
        "embedded" => ModrinthDependencyType.Embedded,
        _ => ModrinthDependencyType.Required,
    };

    private static string? NormalizeId(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUrl(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TrySelectPrimaryFile(JsonElement version, out JsonElement file)
    {
        file = default;

        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var found = false;

        foreach (var candidate in files.EnumerateArray())
        {
            var url = GetString(candidate, "url");
            var fileName = GetString(candidate, "filename");

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            if (!found)
            {
                file = candidate;
                found = true;
            }

            if (candidate.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
            {
                file = candidate;
                return true;
            }
        }

        return found;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(property, out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var items = new List<string>();

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                {
                    items.Add(text);
                }
            }

            return items;
        }

        return Array.Empty<string>();
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string property)
    {
        var text = GetString(element, property);

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    // ---------------- Markdown ----------------

    /// <summary>
    /// 把 Modrinth 的 Markdown 正文压成可以直接塞进 TextBlock 的纯文本。
    ///
    /// 只做「去掉没有渲染器就不该出现的记号」这一件事：图片整段丢弃（不留下 CDN 地址），
    /// 链接只留可见文字，标题 / 列表 / 引用 / 分隔线的行级前缀去掉，HTML 标签去掉，
    /// 成对的强调记号去掉。代码块内容保留——排除掉它只会让正文缺一块，而代码本身是可读文字。
    ///
    /// 纯函数，不依赖实例状态，方便单测直接比对输出。
    /// </summary>
    public static string StripMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        text = MarkdownImagePattern.Replace(text, string.Empty);
        text = MarkdownLinkPattern.Replace(text, "$1");
        text = HtmlTagPattern.Replace(text, string.Empty);
        text = MarkdownRulePattern.Replace(text, string.Empty);
        text = MarkdownLinePrefixPattern.Replace(text, string.Empty);

        // 强调与行内代码的成对记号在纯文本里只剩噪音。
        text = text
            .Replace("```", " ", StringComparison.Ordinal)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        var output = new List<string>();
        var pendingBlankLine = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = InlineWhitespacePattern.Replace(rawLine, " ").Trim();

            if (line.Length == 0)
            {
                // 连续空行只保留一个，且不留在开头 / 结尾。
                pendingBlankLine = output.Count > 0;
                continue;
            }

            if (pendingBlankLine)
            {
                output.Add(string.Empty);
                pendingBlankLine = false;
            }

            output.Add(line);
        }

        return string.Join('\n', output);
    }

    // ---------------- 落盘辅助 ----------------

    /// <summary>
    /// 去掉文件名里不能落盘的部分。Modrinth 的文件名一般合法，但它是服务端可控数据，
    /// 直接拼路径会有目录穿越风险（<c>../../x.jar</c>），所以强制只取最后一段。
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(fileName.Trim());

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.', ' ');

        if (name.Length <= 180)
        {
            return name;
        }

        // 超长名保留扩展名，截断中间部分。
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];

        if (stem.Length <= 0 || extension.Length > 16)
        {
            return name[..180];
        }

        return stem[..Math.Min(stem.Length, 180 - extension.Length)] + extension;
    }

    /// <summary>
    /// 目标目录里已有同名文件时追加 <c> (1)</c>、<c> (2)</c>…… 与 <c>ContentService.ImportAsync</c>
    /// 的命名保持一致：用户在同一目录里会看到同一套冲突规则。
    /// </summary>
    public static string ResolveTargetPath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    // ---------------- 数据包安装目标 ----------------

    /// <summary>
    /// 解析数据包能装到哪些地方：全局 <c>datapacks/</c> 一个，加上每个存档的
    /// <c>saves/&lt;world&gt;/datapacks/</c> 各一个（按存档名排序）。
    ///
    /// 存档目录不存在 / 枚举失败时只返回全局目标——「没有存档就只给全局」是产品约定，
    /// 不是错误。数据包与加载器无关，这里也不需要 <c>LoaderKind</c>。
    /// </summary>
    public static IReadOnlyList<DataPackInstallTarget> ResolveDataPackTargets(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Array.Empty<DataPackInstallTarget>();
        }

        var targets = new List<DataPackInstallTarget>
        {
            new("全局（datapacks/，对所有存档生效）", gameDirectory, null),
        };

        var savesDirectory = Path.Combine(gameDirectory, "saves");

        if (!Directory.Exists(savesDirectory))
        {
            return targets;
        }

        try
        {
            var worlds = Directory.EnumerateDirectories(savesDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            foreach (var world in worlds)
            {
                targets.Add(new DataPackInstallTarget(
                    $"存档：{world}",
                    Path.Combine(savesDirectory, world!),
                    world));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存档目录读不到时只给全局目标：安装功能不该因为一个读不了的目录整体失效。
        }

        return targets;
    }

    /// <summary>
    /// 校验用户选的数据包落点是否仍然有效。
    /// 存档可能在选完到点确认之间被删掉；这时直接拒绝比往一个新建的空目录里塞文件更安全。
    /// </summary>
    /// <returns>无效原因（中文）；有效时返回 null。</returns>
    public static string? ValidateDataPackTarget(DataPackInstallTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.IsGlobal)
        {
            return null;
        }

        return Directory.Exists(target.GameDirectory)
            ? null
            : $"存档「{target.WorldName}」的目录已不存在（可能被删除或改名），请刷新安装位置后重试。";
    }

    private static void Report(
        IProgress<DownloadProgress>? progress,
        long totalBytes,
        long receivedBytes,
        TimeSpan elapsed,
        string fileName)
    {
        if (progress is null)
        {
            return;
        }

        var speed = elapsed.TotalSeconds > 0.05 ? receivedBytes / elapsed.TotalSeconds : 0;
        var ratio = totalBytes > 0 ? Math.Clamp((double)receivedBytes / totalBytes, 0, 1) : 0;

        progress.Report(new DownloadProgress(
            TotalFiles: 1,
            CompletedFiles: 0,
            FailedFiles: 0,
            TotalBytes: totalBytes,
            DownloadedBytes: receivedBytes,
            SpeedBytesPerSecond: speed,
            CurrentFile: fileName,
            Ratio: ratio));
    }

    /// <summary>
    /// 把「单个文件的进度」折算成整批进度。
    ///
    /// <see cref="InstallAsync"/> 只知道自己在传第几个字节；不折算的话进度条会在每个文件开头跳回 0，
    /// 用户看到的是「装完一个又从头开始」。
    /// </summary>
    private sealed class PlanBatchProgress : IProgress<DownloadProgress>
    {
        private readonly IProgress<DownloadProgress> _inner;
        private readonly int _totalFiles;
        private readonly int _completedFiles;
        private readonly long _totalBytes;
        private readonly long _completedBytes;
        private readonly int _failedFiles;

        public PlanBatchProgress(
            IProgress<DownloadProgress> inner,
            int totalFiles,
            int completedFiles,
            long totalBytes,
            long completedBytes,
            int failedFiles)
        {
            _inner = inner;
            _totalFiles = totalFiles;
            _completedFiles = completedFiles;
            _totalBytes = totalBytes;
            _completedBytes = completedBytes;
            _failedFiles = failedFiles;
        }

        public void Report(DownloadProgress value)
        {
            var downloaded = _completedBytes + value.DownloadedBytes;
            var ratio = _totalBytes > 0 ? Math.Clamp(downloaded / (double)_totalBytes, 0, 1) : 0;

            _inner.Report(new DownloadProgress(
                TotalFiles: _totalFiles,
                CompletedFiles: _completedFiles,
                FailedFiles: _failedFiles,
                TotalBytes: _totalBytes,
                DownloadedBytes: downloaded,
                SpeedBytesPerSecond: value.SpeedBytesPerSecond,
                CurrentFile: value.CurrentFile,
                Ratio: ratio));
        }
    }

    private static string ExtensionOf(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.Absolute).AbsolutePath;
            var extension = Path.GetExtension(path).ToLowerInvariant();

            return extension switch
            {
                ".webp" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" => extension,
                _ => string.Empty,
            };
        }
        catch (UriFormatException)
        {
            return string.Empty;
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
            // 残留的 .part 不影响下一次安装：下一次会用同一个路径 Create 覆盖。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var ownsHandler = handler is not null;

        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 16,
        };

        var client = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            // 兜底值。真实超时由每次请求的 CancelAfter 控制（搜索 15 秒 / 下载 5 分钟）。
            Timeout = TimeSpan.FromMinutes(10),
        };

        // Modrinth 要求客户端自报身份；用 TryAdd 是因为该值包含括号与斜杠，
        // 严格解析会抛异常，而服务端只按字符串匹配。
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    public void Dispose() => _http.Dispose();
}
