using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 下载源 URL 重写。BMCLAPI 的路径规则与官方 CDN 不完全一致，
/// 所以这里是逐条映射而不是简单换 host。
/// </summary>
public static class DownloadMirror
{
    /// <summary>官方前缀 → BMCLAPI 前缀。顺序敏感，长前缀在前。</summary>
    private static readonly (string From, string To)[] BmclApiMap =
    [
        ("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
        ("https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://launcher.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("http://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
        ("http://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("http://launcher.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("http://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("http://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
        // NeoForge / Forge 的 installer jar 与 maven 构件。前缀必须带 `/releases/`：
        // NeoForge 的版本目录接口在 `maven.neoforged.net/api/maven/versions/releases/...`，
        // 若把 `https://maven.neoforged.net/` 整段映射过去，这条接口会被改写成
        // `bmclapi2.bangbang93.com/maven/api/maven/...`（404），而它恰恰是唯一能查目录的路径。
        // PCL2 用的是同一个替换（ModDownloadLib.vb:903）。
        ("https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/"),
        ("http://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("libraries.minecraft.net/", "bmclapi2.bangbang93.com/maven/"),
    ];

    /// <summary>
    /// Modrinth 官方域名 → 第三方镜像前缀。
    ///
    /// 来源是 HMCL <c>HMCLDownloadProvider.java:146-148</c> 的四条 <c>fallback=true</c> 规则，
    /// 其中 CurseForge 的两条已由 <c>AppSettings.CurseForgeBaseUrl</c> 覆盖，这里只保留 Modrinth 的两条。
    ///
    /// **不并进 <see cref="BmclApiMap"/>**：那张表是「把官方前缀整段换成镜像前缀」的下载源改写表，
    /// 参与 <see cref="Candidates"/> 的首选/兜底排序；Modrinth 的两条是 fallback 语义（官方先打、
    /// 连不上才用镜像），而且两个域名的镜像前缀不同——
    /// <c>api.modrinth.com/v2/</c> 要换成 <c>mod.mcimirror.top/modrinth/v2/</c>，
    /// <c>cdn.modrinth.com/data/</c> 要换成 <c>mod.mcimirror.top/data/</c>。
    /// 把两者压成同一个前缀会让 API 路径少拼一段（NeoForge 的 <see cref="BmclApiMap"/> 踩过同一个坑，
    /// <c>docs/HANDOVER.md</c> §6.33）。
    /// </summary>
    private static readonly (string From, string To)[] ModrinthFallbackMap =
    [
        ("https://api.modrinth.com/", "https://mod.mcimirror.top/modrinth/"),
        ("https://cdn.modrinth.com/", "https://mod.mcimirror.top/"),
    ];

    /// <summary>
    /// 该策略是否倾向镜像。<see cref="DownloadSource.Auto"/> 在未降级时的首选就是镜像，
    /// 所以也返回 true——没有会话信息的地方（例如只写 URL 的调用点）按自动模式的初始方向处理。
    /// </summary>
    public static bool PrefersMirror(DownloadSource source) =>
        source is DownloadSource.Mirror or DownloadSource.Auto;

    /// <summary>把官方 URL 重写成当前下载源对应的 URL。已是镜像地址时原样返回。</summary>
    public static string Rewrite(string url, DownloadSource source)
    {
        if (string.IsNullOrEmpty(url) || !PrefersMirror(source))
        {
            return url;
        }

        foreach (var (from, to) in BmclApiMap)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                return to + url[from.Length..];
            }
        }

        return url;
    }

    /// <summary>
    /// 一个 URL 的下载候选，按当前下载源排序：首选在前，另一侧兜底。
    /// 不可重写的地址只返回自身。
    ///
    /// 之所以要「候选」而不是「重写一次」：BMCLAPI 的部分 maven 构件会 302 到第三方 CDN
    /// （实测 lwjgl natives 被重定向到 2.749333.xyz，该域名在部分网络不可达），
    /// 还有部分构件直接 404；这些情况必须能退回官方源。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string url, DownloadSource source) =>
        Candidates(url, source, source == DownloadSource.Auto ? DownloadSource.Mirror : source);

    /// <summary>
    /// 带「当前首选源」的候选排序。
    ///
    /// <paramref name="source"/> 是策略（可能是 <see cref="DownloadSource.Auto"/>），
    /// <paramref name="preferred"/> 是本次会话解析后的首选方向（<see cref="DownloadSource.Mirror"/>
    /// 或 <see cref="DownloadSource.Official"/>）。自动模式降级后由调用方把 preferred 换成官方，
    /// 候选顺序随之翻转；手动档的 preferred 与 source 相同，行为与旧版一致。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string url, DownloadSource source, DownloadSource preferred)
    {
        if (string.IsNullOrEmpty(url))
        {
            return Array.Empty<string>();
        }

        var mirror = Rewrite(url, DownloadSource.Mirror);

        if (string.Equals(mirror, url, StringComparison.Ordinal))
        {
            return [url];
        }

        var mirrorFirst = source == DownloadSource.Auto
            ? PrefersMirror(preferred)
            : PrefersMirror(source);

        return mirrorFirst
            ? [mirror, url]
            : [url, mirror];
    }

    /// <summary>该 URL 是否指向 BMCLAPI 镜像。</summary>
    public static bool IsMirrorUrl(string url) =>
        !string.IsNullOrEmpty(url) && url.Contains("bangbang93.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>该 URL 是否指向 Modrinth 的第三方镜像（<c>mod.mcimirror.top</c>）。</summary>
    public static bool IsModrinthMirrorUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.Contains("mcimirror.top", StringComparison.OrdinalIgnoreCase);

    /// <summary>该 URL 是否是 Modrinth 官方域名（可换出镜像候选）。</summary>
    public static bool IsModrinthOfficialUrl(string? url) => TryRewriteModrinth(url, out _);

    /// <summary>
    /// 把 Modrinth 官方 URL 换成镜像 URL。不可换时返回 false 且 <paramref name="mirrorUrl"/> 为原串。
    /// </summary>
    public static bool TryRewriteModrinth(string? url, out string mirrorUrl)
    {
        mirrorUrl = url ?? string.Empty;

        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        foreach (var (from, to) in ModrinthFallbackMap)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                mirrorUrl = to + url[from.Length..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Modrinth 请求的候选地址：<b>官方永远排第一</b>，镜像只有在
    /// <paramref name="useModrinthMirror"/> 打开时才作为第二个候选追加。
    ///
    /// 与 <see cref="Candidates(string, DownloadSource)"/> 的区别是这里**没有**「镜像优先」这一态：
    /// Modrinth 镜像只有 fallback 语义（HMCL 的 <c>fallback=true</c>），
    /// 默认设置下（开关关）返回的候选表里只有官方一项，任何请求都不会发到镜像主机。
    /// </summary>
    public static IReadOnlyList<string> ModrinthCandidates(string? url, bool useModrinthMirror)
    {
        if (string.IsNullOrEmpty(url))
        {
            return Array.Empty<string>();
        }

        if (!useModrinthMirror || !TryRewriteModrinth(url, out var mirror))
        {
            return [url];
        }

        return [url, mirror];
    }

    /// <summary>
    /// 是否是已知的下载主机（官方 CDN、镜像、加载器 maven、Modrinth CDN）。
    ///
    /// 下载器需要用它判断「镜像把我重定向到了哪里」：如果落在未知主机上，
    /// 说明镜像侧没有该文件（常见于被转卖到第三方 CDN），应放弃镜像候选改走官方。
    ///
    /// 教育网镜像（<c>cernet.edu.cn</c> / <c>ustc.edu.cn</c>）与 BMCLAPI 的下载 CDN
    /// （<c>bmclapi&lt;N&gt;.sakura.sld.tw</c> / <c>.sakura.exp.dog</c> / <c>&lt;N&gt;.749333.xyz</c>）也在名单里：
    /// BMCLAPI 已经不再直接回源，而是**随机**签发这两族 CDN 的带签名 302 ——
    /// <c>bmclapi2.bangbang93.com/...</c> → <c>bmclapi&lt;N&gt;.sakura.*</c> 或 <c>&lt;N&gt;.749333.xyz</c>
    /// →（再一跳）<c>minio.749333.xyz/bmclapi/...</c>（S3 预签名）→ 200。
    /// 实测 8 次采样里 3 次首跳直接落在 <c>&lt;N&gt;.749333.xyz</c>（2026-10-22）。
    ///
    /// **只放行第一跳**就够：<see cref="IsMirrorUrl"/> 只认 <c>bangbang93.com</c>，跟到 CDN 之后
    /// 就不再算「镜像候选」，后续到 minio 的跳转自然不会过这道检查。反过来，第一跳若被判成未知主机，
    /// 下载器会立刻放弃镜像候选；而 <c>maven.neoforged.net</c> 在国内不可达，maven 类构件等于必然失败
    /// （NeoForge 装不上的直接原因，见 <c>docs/HANDOVER.md</c> §6.33）。
    /// </summary>
    public static bool IsKnownDownloadHost(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return url.Contains("bangbang93.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("mojang.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("minecraft.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("fabricmc.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("quiltmc.org", StringComparison.OrdinalIgnoreCase)
               || url.Contains("minecraftforge.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("neoforged.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("modrinth.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("forgecdn.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("adoptium.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("mcimirror.top", StringComparison.OrdinalIgnoreCase)
               || url.Contains("cernet.edu.cn", StringComparison.OrdinalIgnoreCase)
               || url.Contains("ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
               || url.Contains("sakura.sld.tw", StringComparison.OrdinalIgnoreCase)
               || url.Contains("sakura.exp.dog", StringComparison.OrdinalIgnoreCase)
               || url.Contains("749333.xyz", StringComparison.OrdinalIgnoreCase)

               // 兜底：BMCLAPI 的分发 CDN 是**轮换**的（一天内实测到 sakura.sld.tw /
               // sakura.exp.dog / <N>.749333.xyz / open-bmcl-api.lunar.cc 四族），
               // 按域名枚举永远会漏。目标 URL 的形状却是稳定的：
               //   /download/<sha1>?name=...&s=...&e=...      （签名直链）
               //   /bmclapi/<xx>/<sha1>?X-Amz-...             （S3 预签名）
               // 所以按「路径里带 40 位十六进制摘要 + download/bmclapi 段」认，比按域名认更稳。
               || ContainsMirrorCdnSignature(url);
    }

    /// <summary>
    /// BMCLAPI 分发 CDN 的签名直链形状。域名轮换，形状不换：
    /// <c>/download/&lt;sha1&gt;?name=...&amp;s=...&amp;e=...</c> 或
    /// <c>/bmclapi/&lt;xx&gt;/&lt;sha1&gt;?X-Amz-...</c>。
    /// </summary>
    private static bool ContainsMirrorCdnSignature(string url)
    {
        if (!url.Contains("/download/", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("/bmclapi/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart >= 0 ? url[..queryStart] : url;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length != 40)
            {
                continue;
            }

            var isHex = true;
            foreach (var ch in segment)
            {
                if (!char.IsAsciiHexDigit(ch))
                {
                    isHex = false;
                    break;
                }
            }

            if (isHex)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>判断该 URL 是否落在已知的官方 CDN 域上（测试与诊断用）。</summary>
    public static bool IsOfficialCdn(string url)
    {
        foreach (var (from, _) in BmclApiMap)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 在 HTTP 请求发出前重写 URL，并在「镜像 → 官方」之间做候选回退。
/// CmlLib 只接受 <see cref="HttpClient"/>，所以下载源切换必须在这一层完成。
///
/// 下载源**每次请求都重新取**（<see cref="IDownloadSourceProvider"/>）：
/// 自动模式在会话中途降级后，下一个文件就必须换方向，构造期缓存会让降级永远不生效。
/// 没有注入 provider 的旧路径（单测直接 <c>new DownloadHttpClientFactory(DownloadSource.X)</c>）
/// 退回固定源，行为与改造前一致。
///
/// 重定向由本类手动跟随（底层 handler 关闭了自动重定向）：
/// BMCLAPI 会把部分 maven 构件 302 到第三方 CDN，该 CDN 在部分网络不可达；
/// 自动跟随会卡死在一个我们无法回退的主机上。
/// </summary>
public sealed class MirrorRewritingHandler : DelegatingHandler
{
    private const int MaxRedirects = 6;

    private readonly IDownloadSourceProvider? _provider;
    private readonly IDownloadSourceTelemetry? _telemetry;
    private volatile DownloadSource _source;

    /// <summary>
    /// 镜像候选的元数据请求「等响应头」上限（PCL2 <c>DlClientListMain</c> 的 5 秒档）。
    ///
    /// 为什么是 5 秒：版本清单 / 加载器元数据都是百 KB 以内的小 JSON，国内镜像正常在 1 秒内回响应头；
    /// 5 秒还不回，说明这条链路已经坏了（TCP 连上但不吐数据），继续等只会让用户对着进度条发呆。
    /// PCL2 对「应当最快」的那一档用的就是 5 秒。
    /// </summary>
    public TimeSpan MetadataHeaderTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 其余请求的「等响应头」上限（PCL2 <c>DlClientListMain</c> 的 30 秒档）。
    ///
    /// 适用范围比「资源文件」更宽，原因见 <see cref="ResolveHeaderTimeout"/>：
    /// 官方站点的元数据也可能慢到 30 秒（跨洋链路 + 大版本清单），而响应体大小在拿到响应头之前
    /// 是未知的。30 秒既排得掉「连上但不返响应头」的死链路，也不会误伤慢速但活着的下载。
    ///
    /// 两个值都可写：测试要把它调到几十毫秒才能在不拖慢整套用例的前提下验证换源。
    /// </summary>
    public TimeSpan PayloadHeaderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 响应体两次读到数据之间的最长空闲（默认 60 秒）。
    ///
    /// 头超时与首块探测都管不到「传到一半停住」：R42 实测镜像对 client jar 回了头、给了首块，
    /// 之后不再产出字节，整体只能等 <c>HttpClient.Timeout</c>（10 分钟）。
    /// 空闲超时到点会抛 <see cref="IOException"/>，同时把这次请求作为「镜像失败」上报给
    /// <see cref="IDownloadSourceTelemetry"/>——自动模式下连续 3 次即降级到官方，后续文件不再走镜像。
    ///
    /// 可写：测试调到几十毫秒。
    /// </summary>
    public TimeSpan BodyIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public MirrorRewritingHandler(DownloadSource source = DownloadSource.Mirror)
        : this(null, null, source)
    {
    }

    public MirrorRewritingHandler(IDownloadSourceProvider provider, IDownloadSourceTelemetry? telemetry = null)
        : this(provider ?? throw new ArgumentNullException(nameof(provider)), telemetry, DownloadSource.Mirror)
    {
    }

    private MirrorRewritingHandler(
        IDownloadSourceProvider? provider,
        IDownloadSourceTelemetry? telemetry,
        DownloadSource source)
        : base(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // 手动跟随，才能在中途判断「这个重定向值不值得跟」。
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 64,
        })
    {
        _provider = provider;
        _telemetry = telemetry;
        _source = source;
    }

    /// <summary>固定源（无 provider 时的兜底），或最后一次 <c>Source</c> 写入的值。</summary>
    public DownloadSource Source
    {
        get => _source;
        set => _source = value;
    }

    /// <summary>本次请求实际使用的首选源。有 provider 时以它为准，永远是具体方向而不是 Auto。</summary>
    public DownloadSource CurrentSource => _provider?.Effective ?? _source;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var current = request.RequestUri;
        if (current is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var original = current.ToString();
        var candidates = DownloadMirror.Candidates(original, CurrentSource);

        if (candidates.Count == 0)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // 采样状态：一个请求最多两个候选，第一候选就是「首选源」。
        var primary = SourceOf(candidates[0]);
        var primaryElapsed = TimeSpan.Zero;
        var primarySucceeded = false;
        var fallbackUsed = false;
        var fallbackElapsed = TimeSpan.Zero;
        var succeeded = false;

        // 每个候选都要能等到响应头：候选只有一个时也必须有自己的超时，
        // 否则「镜像侧连上却不返响应头」会一直挂到 HttpClient 的兜底 Timeout（10 分钟）。
        HttpRequestException? lastError = null;

        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var isMirror = DownloadMirror.IsMirrorUrl(candidate);
                var isLast = index == candidates.Count - 1;
                var started = Stopwatch.GetTimestamp();

                try
                {
                    var response = await FollowAsync(request, candidate, isMirror, cancellationToken).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started);

                    if (index == 0)
                    {
                        primaryElapsed = elapsed;
                        primarySucceeded = response.IsSuccessStatusCode;
                    }
                    else
                    {
                        fallbackUsed = true;
                        fallbackElapsed = elapsed;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        succeeded = true;
                        return response;
                    }

                    // 镜像缺文件（404）、被拒绝（403）、或上游 5xx：换下一个候选。
                    if (!isLast && IsRetriable(response.StatusCode))
                    {
                        response.Dispose();
                        continue;
                    }

                    return response;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    // 连接失败 / 超时 / 镜像重定向到未知主机：换下一个候选。
                    var elapsed = Stopwatch.GetElapsedTime(started);

                    if (index == 0)
                    {
                        primaryElapsed = elapsed;
                    }
                    else
                    {
                        fallbackUsed = true;
                        fallbackElapsed = elapsed;
                    }

                    lastError = ex as HttpRequestException ?? new HttpRequestException(ex.Message, ex);

                    if (isLast)
                    {
                        break;
                    }
                }
            }

            throw lastError ?? new HttpRequestException($"下载失败（已尝试 {candidates.Count} 个地址）：{original}");
        }
        finally
        {
            // 用户取消不算镜像劣化：那不是网络质量的证据。
            if (!cancellationToken.IsCancellationRequested)
            {
                _telemetry?.Report(new DownloadAttemptResult
                {
                    PrimarySource = primary,
                    PrimarySucceeded = primarySucceeded,
                    PrimaryElapsed = primaryElapsed,
                    FallbackUsed = fallbackUsed,
                    FallbackElapsed = fallbackElapsed,
                    Succeeded = succeeded,
                });
            }
        }
    }

    private static DownloadSource SourceOf(string url) =>
        DownloadMirror.IsMirrorUrl(url) ? DownloadSource.Mirror : DownloadSource.Official;

    private async Task<HttpResponseMessage> FollowAsync(
        HttpRequestMessage request,
        string candidate,
        bool isMirror,
        CancellationToken cancellationToken)
    {
        var current = candidate;

        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            var message = CloneRequest(request, current);

            // 档位按「这一跳的主机类别」重算：镜像元数据是 5 秒档，但 302 到官方 CDN/回源之后
            // 官方清单本身可能要十几秒——沿用镜像档会把慢速但合法的响应判死
            // （REVIEW-R43-R51 P2-3）。
            var headerTimeout = ResolveHeaderTimeout(request, isMirror);

            // 每一跳重新开窗口：沿用上一跳剩下的预算会把「第二跳本来就慢」错算成「第一跳挂死」。
            var response = await SendOnceAsync(
                    message,
                    headerTimeout,
                    isMirror ? DownloadSource.Mirror : DownloadSource.Official,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is not (HttpStatusCode.MovedPermanently
                                            or HttpStatusCode.Found
                                            or HttpStatusCode.SeeOther
                                            or HttpStatusCode.TemporaryRedirect
                                            or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                return response;
            }

            var next = new Uri(new Uri(current, UriKind.Absolute), location).ToString();
            response.Dispose();

            // 镜像把文件转卖到第三方 CDN（实测 BMCLAPI 的 maven 会 302 到 *.749333.xyz）。
            // 这类主机在本机网络不可达，跟过去只会超时，直接放弃镜像候选。
            if (isMirror && !DownloadMirror.IsKnownDownloadHost(next))
            {
                throw new HttpRequestException($"镜像重定向到未知主机：{next}");
            }

            current = next;

            // 重定向到官方 CDN 后不再是「镜像候选」，未知主机检查对后续跳转仍然生效。
            isMirror = DownloadMirror.IsMirrorUrl(current);
        }

        throw new HttpRequestException($"重定向次数过多：{candidate}");
    }

    /// <summary>
    /// 发一次请求并给它一个等待窗口，窗口到点就放弃这次尝试。
    ///
    /// 为什么必须在这个粒度上做，而不是调 <see cref="DownloadHttpClientFactory"/> 里那个
    /// <c>HttpClient.Timeout</c>：那是整个客户端共享的全局预算，一旦调小就会同时掐掉
    /// 几十 MB 文件的合法慢速传输（10 MB @ 100 KB/s 要 100 秒，远超任何合理的响应头阈值）。
    /// 这里给每个候选的每一跳单独开一个窗口，超出就换下一个候选，不影响已经拿到的响应。
    ///
    /// 窗口覆盖的是「<see cref="DelegatingHandler.SendAsync"/> 这一次调用的整个等待」：
    /// 调用方用 <see cref="HttpCompletionOption.ResponseHeadersRead"/> 时（<see cref="DownloadQueue"/>
    /// 与全部大文件下载路径都是），它严格等于「等响应头」；调用方用默认的 ResponseContentRead 时
    /// （<c>ResourceFetcher.GetStringAsync</c> 这类小文档），它连 body 一起覆盖。这个差别在 handler
    /// 层无法观测——<see cref="HttpClient"/> 不会把完成语义写进 <see cref="HttpRequestMessage.Options"/>，
    /// 实测也改不动内层 handler 的行为，所以阈值只能按「最坏情况」选，见 <see cref="ResolveHeaderTimeout"/>。
    /// </summary>
    /// <param name="timeout">本次等待窗口，由 <see cref="ResolveHeaderTimeout"/> 按候选与请求类型选定。</param>
    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage message,
        TimeSpan timeout,
        DownloadSource source,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.SendAsync(message, timeoutSource.Token).ConfigureAwait(false);

            // 「等响应头」超时挡不住「返回了头但不传 body」。R42 实测：镜像对 client jar
            // 回了 200 之后 8 分钟 0 字节，整体卡到 HttpClient.Timeout（10 分钟）。
            // 这里预读首块，超时就换下一个候选——覆盖常见的「头到了、body 不动」。
            //
            // 预算共用：首块窗口 = 总窗口 - 已经花在响应头上的时间。两段各给一份完整窗口
            // 会让单候选真实上限变成 2×档位（镜像 jar 最坏 60 秒才换源，REVIEW-R43-R51 P3-1）。
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining < TimeSpan.FromSeconds(1))
            {
                remaining = TimeSpan.FromSeconds(1);
            }

            return await ProbeFirstChunkAsync(response, remaining, source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 用户没主动取消 → 这是本候选的响应头超时。包成 HttpRequestException 而不是让
            // OperationCanceledException 直接冒出去：调用方（DownloadQueue 的 MaxAttempts、
            // 本类的候选循环、上层各 service 的 catch）都是按 HttpRequestException 处理网络失败的，
            // 抛出别的类型会让「换源」变成「整个文件失败」。
            throw new HttpRequestException(
                $"等待响应头超时（{timeout.TotalSeconds:0.##} 秒）：{message.RequestUri?.Host}",
                ex);
        }
        finally
        {
            // 停表：响应已经拿到，body 可能还要传几分钟，不该再受这个窗口约束。
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 预读响应体的第一块，确认「头到了之后 body 真的在动」。
    ///
    /// 只读一次（最多 8 KB），并把读到的字节用 <see cref="PrefixedStream"/> 放回流首，
    /// 对调用方透明：Content-Length、内容、可读次数都不变。
    /// 超时/首块报错 → 抛 <see cref="HttpRequestException"/>，交给候选循环换源。
    ///
    /// 返回的 body 还包一层 <see cref="IdleTimeoutStream"/>：首块之后的停滞（R42 实测形态）
    /// 由它按 <see cref="BodyIdleTimeout"/> 判超时，并把这次请求作为镜像失败上报。
    /// </summary>
    private async Task<HttpResponseMessage> ProbeFirstChunkAsync(
        HttpResponseMessage response,
        TimeSpan timeout,
        DownloadSource source,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode || response.Content is null)
        {
            return response;
        }

        // 明确没有 body 的响应（204 / HEAD / Content-Length: 0）不需要探测。
        if (response.Content.Headers.ContentLength == 0)
        {
            return response;
        }

        // 取得流本身也可能挂住（非流式 HttpContent 会先序列化），所以它也在同一个窗口里。
        using var probeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeSource.CancelAfter(timeout);

        Stream stream;
        int read;
        var buffer = new byte[8192];

        try
        {
            stream = await response.Content.ReadAsStreamAsync(probeSource.Token).ConfigureAwait(false);
            read = await stream.ReadAsync(buffer.AsMemory(), probeSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw new HttpRequestException(
                $"响应体首块超时（{timeout.TotalSeconds:0.##} 秒）：{response.RequestMessage?.RequestUri?.Host}");
        }
        catch
        {
            response.Dispose();
            throw;
        }

        if (read <= 0)
        {
            // 空 body：流已经读完，保持原响应即可。
            return response;
        }

        var body = new IdleTimeoutStream(
            new PrefixedStream(buffer.AsSpan(0, read).ToArray(), stream),
            BodyIdleTimeout,
            () => ReportBodyStall(source, response.RequestMessage?.RequestUri));

        var rebuilt = new HttpResponseMessage(response.StatusCode)
        {
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version,
            RequestMessage = response.RequestMessage,
            Content = new StreamContent(body),
        };

        foreach (var header in response.Headers)
        {
            rebuilt.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            rebuilt.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // 不能 Dispose 原响应：rebuilt 的 StreamContent 正持着从它取出的流，
        // Dispose 原 Content 会把这条流关掉（下游立刻 "Error while copying content to a stream"）。
        // 这里把「流的所有权」整体移交给 rebuilt，原 response 对象交给 GC。
        return rebuilt;
    }

    /// <summary>
    /// 把「body 传到一半停住」上报成一次镜像失败：自动模式下连续 3 次就降级到官方，
    /// 后续文件不再走这条链路（<see cref="DownloadSourceGovernor"/>）。
    /// </summary>
    private void ReportBodyStall(DownloadSource source, Uri? uri)
    {
        if (_telemetry is null)
        {
            return;
        }

        try
        {
            _telemetry.Report(new DownloadAttemptResult
            {
                PrimarySource = source,
                PrimarySucceeded = false,
                PrimaryElapsed = TimeSpan.Zero,
                FallbackUsed = false,
                FallbackElapsed = TimeSpan.Zero,
                Succeeded = false,
            });
        }
        catch (Exception)
        {
            // 上报失败不能影响下载本身（handler 层没有注入 IAppLog）。
        }
    }

    /// <summary>
    /// 按候选与请求类型选等待上限。
    ///
    /// 判据分两维：
    /// <list type="bullet">
    /// <item>**候选来源**：镜像应当秒回，5 秒不回就是链路坏了；官方要跨洋，慢速但合法，给 30 秒。
    ///   这一维是本改动的核心——「镜像 TCP 连上却不返响应头」正是用户等 10 分钟的那个场景。</item>
    /// <item>**请求类型**：只有 JSON / XML 文档算「应当秒回」的元数据。jar / assets / 整合包
    ///   一律 30 秒（首包可能因为回源或 S3 预签名跳转而慢）。判据用 URL 后缀而不是新增参数：
    ///   调用方是 CmlLib 与十几个 service，要求每个调用点都填枚举不现实，也改不了 CmlLib。</item>
    /// </list>
    /// 官方候选不给 5 秒，是因为 handler 层看不到上层要 headers 还是完整 body（见
    /// <see cref="SendOnceAsync"/>）：官方站点的版本清单实测常常要十几秒才能下完，
    /// 用 5 秒会把「慢速但合法」直接判死，而用户手动选「官方」时没有别的候选可退。
    /// </summary>
    private TimeSpan ResolveHeaderTimeout(HttpRequestMessage request, bool isMirror) =>
        isMirror && IsMetadataRequest(request) ? MetadataHeaderTimeout : PayloadHeaderTimeout;

    /// <summary>
    /// 该请求是不是「元数据」：版本清单、加载器版本列表、maven-metadata 这类文档。
    /// 只认后缀，不认域名——镜像会改写域名，改完还是同一个资源，后缀不变。
    /// </summary>
    private static bool IsMetadataRequest(HttpRequestMessage request)
    {
        // Range 请求一定是断点续传的资源文件，不是文档。
        if (request.Headers.Range is not null)
        {
            return false;
        }

        var path = request.RequestUri?.AbsolutePath;

        return path is not null
               && (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, string url)
    {
        // 下载请求都是 GET；这里对其它方法显式失败，避免「body 静默丢失」变成服务端的 400/422
        // 而不是「客户端没带请求体」（REVIEW-R43-R51 P3-3）。
        if (source.Method != HttpMethod.Get && source.Method != HttpMethod.Head)
        {
            throw new NotSupportedException(
                $"下载池只支持 GET/HEAD，不支持 {source.Method}：{source.RequestUri}");
        }

        var clone = new HttpRequestMessage(source.Method, url);

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static bool IsRetriable(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound
            or HttpStatusCode.Forbidden
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}

/// <summary>
/// 把「已经预读出来的字节」放回流首，再接上原始流。
///
/// 用途只有 <see cref="MirrorRewritingHandler"/> 的首块探测：探测必须读一次，
/// 但调用方拿到的必须是完整的 body（Content-Length 不变、字节不丢）。
/// 只支持顺序读：HTTP 响应流本来也不可 seek。
/// </summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.CanSeek
        ? inner.Length + prefix.Length - _offset
        : throw new NotSupportedException("底层流不可 seek，无法给出长度。");

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var remaining = prefix.Length - _offset;
        if (remaining > 0)
        {
            var take = Math.Min(remaining, buffer.Length);
            prefix.AsSpan(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        return inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return ValueTask.FromResult(0);
        }

        var remaining = prefix.Length - _offset;
        if (remaining > 0)
        {
            var take = Math.Min(remaining, buffer.Length);
            prefix.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            return ValueTask.FromResult(take);
        }

        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override int ReadByte()
    {
        if (_offset < prefix.Length)
        {
            return prefix[_offset++];
        }

        return inner.ReadByte();
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 给响应体加「空闲超时」：两次读到数据之间超过阈值就抛 <see cref="IOException"/> 并通知调用方。
///
/// 解决的是「头到了、首块也到了、后续停住」：R42 实测镜像对 client jar 8 分钟 0 字节，
/// 头超时与首块探测都管不到传输中段。只包异步读——CmlLib / 安装器 / 下载队列全部用异步读，
/// 同步 <see cref="Read(byte[], int, int)"/> 保持直通（无空闲控制），避免为了兼容同步路径引入线程。
/// </summary>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idleTimeout, Action onIdleTimeout) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);

        try
        {
            return await inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                onIdleTimeout();
            }
            catch (Exception)
            {
                // 通知失败不影响超时语义。
            }

            throw new IOException($"读取响应体空闲超时（{idleTimeout.TotalSeconds:0.##} 秒没有新数据）");
        }
    }

    public override int ReadByte() => inner.ReadByte();

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>全启动器共用的 HttpClient。下载源由 <see cref="MirrorRewritingHandler"/> 每次请求重取。</summary>
public sealed class DownloadHttpClientFactory : IDisposable
{
    private readonly MirrorRewritingHandler _handler;

    /// <summary>
    /// 固定源构造（单测与无 DI 宿主用）。不接状态机，自动模式的降级不会生效。
    /// </summary>
    public DownloadHttpClientFactory(DownloadSource source = DownloadSource.Mirror)
    {
        _handler = new MirrorRewritingHandler(source);
        Client = CreateClient(_handler);
    }

    /// <summary>接下载源提供者与状态机（组合根用）。</summary>
    public DownloadHttpClientFactory(IDownloadSourceProvider provider, IDownloadSourceTelemetry? telemetry = null)
    {
        _handler = new MirrorRewritingHandler(provider, telemetry);
        Client = CreateClient(_handler);
    }

    public HttpClient Client { get; }

    public DownloadSource Source
    {
        get => _handler.Source;
        set => _handler.Source = value;
    }

    /// <summary>本次请求实际使用的首选源（有 provider 时是会话解析后的方向）。</summary>
    public DownloadSource CurrentSource => _handler.CurrentSource;

    /// <summary>把 URL 按当前下载源重写，供不走 HttpClient 的场景（例如写进 JSON）使用。</summary>
    public string Rewrite(string url) => DownloadMirror.Rewrite(url, _handler.CurrentSource);

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
    }

    private static HttpClient CreateClient(MirrorRewritingHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            // 这只是**最终兜底**：单次请求的「等响应头」由 MirrorRewritingHandler 按候选逐个限时
            // （元数据 5 秒 / 资源 30 秒）。这里的 10 分钟要留得比任何合法的大文件传输都宽，
            // body 读取不受它限制也不该受它限制——把它调小会掐掉慢速网络上的正常下载。
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NebulaLauncher", "1.0"));
        return client;
    }
}
