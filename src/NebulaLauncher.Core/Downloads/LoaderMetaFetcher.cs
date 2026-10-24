using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// Fabric / Quilt 的 meta API 候选地址。
///
/// 国内网络访问 <c>meta.fabricmc.net</c> 时常出现 DNS 解析失败
/// （实测 <c>SocketException 11001 不知道这样的主机</c>），
/// 而 BMCLAPI 提供 <c>/fabric-meta</c> 镜像且结构与官方一致。
/// 这里按下载源给出「首选 + 兜底」两个候选，由 <see cref="ResourceFetcher"/> 顺序尝试。
/// </summary>
public static class LoaderMetaUrls
{
    private const string FabricOfficial = "https://meta.fabricmc.net";
    private const string FabricMirror = "https://bmclapi2.bangbang93.com/fabric-meta";
    private const string QuiltOfficial = "https://meta.quiltmc.org";

    private const string ForgePromotionsOfficial =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    /// <summary>
    /// BMCLAPI 的 Forge promoted 列表。**结构与官方不同**：官方是
    /// <c>{"promos":{"1.20.1-recommended":"47.2.0"}}</c>，这里是
    /// <c>[{"name":"1.20.1-recommended","build":{"version":"47.2.0"}}]</c>（PCL2 读的就是这个）。
    /// 解析器两种形状都认，见 <c>VersionService.ParseForgePromotions</c>。
    /// </summary>
    private const string ForgePromotionsMirror = "https://bmclapi2.bangbang93.com/forge/promos";

    /// <summary>某 Minecraft 版本的加载器版本列表。</summary>
    public static IReadOnlyList<string> Versions(LoaderKind loader, string minecraftVersion, DownloadSource source)
    {
        var mc = Uri.EscapeDataString(minecraftVersion);

        return loader switch
        {
            // BMCLAPI 没有 quilt-meta（实测 404），Quilt 只保留官方地址。
            LoaderKind.Fabric => source == DownloadSource.BmclApi
                ? [$"{FabricMirror}/v2/versions/loader/{mc}", $"{FabricOfficial}/v2/versions/loader/{mc}"]
                : [$"{FabricOfficial}/v2/versions/loader/{mc}", $"{FabricMirror}/v2/versions/loader/{mc}"],
            LoaderKind.Quilt => [$"{QuiltOfficial}/v3/versions/loader/{mc}"],
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>单个加载器版本的 profile JSON（含 inheritsFrom，供 CmlLib 补全原版文件）。</summary>
    public static IReadOnlyList<string> Profile(
        LoaderKind loader,
        string minecraftVersion,
        string loaderVersion,
        DownloadSource source)
    {
        var mc = Uri.EscapeDataString(minecraftVersion);
        var version = Uri.EscapeDataString(loaderVersion);

        return loader switch
        {
            LoaderKind.Fabric => source == DownloadSource.BmclApi
                ? [
                    $"{FabricMirror}/v2/versions/loader/{mc}/{version}/profile/json",
                    $"{FabricOfficial}/v2/versions/loader/{mc}/{version}/profile/json",
                ]
                : [
                    $"{FabricOfficial}/v2/versions/loader/{mc}/{version}/profile/json",
                    $"{FabricMirror}/v2/versions/loader/{mc}/{version}/profile/json",
                ],
            LoaderKind.Quilt => [$"{QuiltOfficial}/v3/versions/loader/{mc}/{version}/profile/json"],
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Forge 的 promoted 版本列表候选地址。返回的是「哪个构建被官方标为推荐」，
    /// 不是可安装版本的全集——全集由安装器自己决定。
    ///
    /// **永远官方优先，不随下载源切换**。这是一条实测出来的例外：
    /// <list type="bullet">
    /// <item>官方 <c>promotions_slim.json</c>：4 KB，<c>1.21.1-recommended</c> = <c>52.1.20</c>；</item>
    /// <item>BMCLAPI <c>/forge/promos</c>：62 KB，同一条是 <c>52.1.0</c>——镜像滞后了二十个小版本。</item>
    /// </list>
    /// 镜像存在的意义是省带宽和加速，而这份 JSON 一旦拿旧值，用户会被引导去装一个官方已经不再推荐的构建。
    /// 4 KB 的代价换正确性，值。PCL2 只读 BMCLAPI 那份（<c>ModDownloadLib.vb:1419</c>），
    /// 这里刻意不照抄。
    /// </summary>
    public static IReadOnlyList<string> ForgePromotions() =>
        [ForgePromotionsOfficial, ForgePromotionsMirror];

    private const string NeoForgeCatalogMirror =
        "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged";

    /// <summary>
    /// NeoForge 官方 maven 的版本目录接口。**本机不可达**（实测 TCP 超时），只作兜底。
    /// 注意路径是 <c>/api/maven/versions/releases/</c>，与下载坐标 <c>/releases/</c> 不是一回事。
    /// </summary>
    private const string NeoForgeCatalogOfficial = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged";

    /// <summary>
    /// NeoForge 的版本目录候选地址（首选组）：返回「新式」与「Legacy」两族。
    ///
    /// 两族是两个不同的 maven 坐标（判据照抄 PCL2 <c>ModDownload.vb:908-927</c>）：
    /// <list type="bullet">
    /// <item><c>net/neoforged/neoforge</c>：1.20.2 及以后，目录名如 <c>21.1.264</c>；</item>
    /// <item><c>net/neoforged/forge</c>：1.20.1 的 NeoForge（当时还叫 forge 坐标），目录名如 <c>1.20.1-47.1.106</c>。</item>
    /// </list>
    /// Legacy 那族的 installer jar 在 <c>net/neoforged/forge</c> 下而非 <c>neoforge</c> 下，
    /// CmlLib 硬编码的是后者——这就是 <see cref="Installing.NeoForgeInstallerBridge"/> 要做坐标改写的原因。
    ///
    /// **只给一组、不给「同一次尝试里的官方兜底」**：官方 <c>maven.neoforged.net</c> 在本机 TCP 超时，
    /// 把它排在镜像后面会让每次调用都白等两个 21 秒的连接超时（截图里表现为加载器版本一直「加载中…」）。
    /// 兜底由 <see cref="NeoForgeCatalogBackup"/> 承担，且只在首选组整族失败时才试。
    /// </summary>
    public static IReadOnlyList<string> NeoForgeCatalog(DownloadSource source) =>
        (source is DownloadSource.BmclApi or DownloadSource.Auto)
            ? [$"{NeoForgeCatalogMirror}/neoforge", $"{NeoForgeCatalogMirror}/forge"]
            : [$"{NeoForgeCatalogOfficial}/neoforge", $"{NeoForgeCatalogOfficial}/forge"];

    /// <summary>首选组整族都失败时的兜底组（同一族的另一半源）。</summary>
    public static IReadOnlyList<string> NeoForgeCatalogBackup(DownloadSource source) =>
        (source is DownloadSource.BmclApi or DownloadSource.Auto)
            ? [$"{NeoForgeCatalogOfficial}/neoforge", $"{NeoForgeCatalogOfficial}/forge"]
            : [$"{NeoForgeCatalogMirror}/neoforge", $"{NeoForgeCatalogMirror}/forge"];
}

/// <summary>
/// 按候选顺序请求文本资源：一个地址失败（DNS / TLS / 超时 / 5xx）自动换下一个。
///
/// 只做「取字符串」这类小请求；大文件下载仍走 CmlLib 的安装器与镜像重写。
/// 全部候选失败时抛出的异常里带每个地址的原因，便于用户判断是网络还是镜像侧问题。
/// </summary>
public static class ResourceFetcher
{
    public static async Task<string> GetStringAsync(
        HttpClient client,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken,
        IAppLog? log = null,
        int attemptsPerUrl = 2)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (candidates is null || candidates.Count == 0)
        {
            throw new InvalidOperationException("没有可用的下载地址");
        }

        var failures = new List<string>();

        foreach (var url in candidates)
        {
            for (var attempt = 1; attempt <= Math.Max(1, attemptsPerUrl); attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await ReadStringAsync(client, url, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient.Timeout 触发的是 TaskCanceledException，不是用户取消。
                    //
                    // 用 ResponseHeadersRead + 自己读 body 之后，超时有两个可能的发生点：等响应头，
                    // 和读 body。两者都走这条分支——用户没取消就是超时，分不出也不必分出是哪一段，
                    // 对用户的含义一样（这个地址没在窗口内给出完整文档）。
                    failures.Add($"{url}（请求超时）");
                    log?.Warn($"请求超时（第 {attempt} 次）：{url}");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    var reason = ex.GetBaseException().Message;
                    failures.Add($"{url}（{reason}）");
                    log?.Warn($"请求失败（第 {attempt} 次）：{url}", ex);
                }
            }
        }

        throw new HttpRequestException(
            "所有下载地址均失败：" + string.Join("；", failures.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// 取一个文档：<c>ResponseHeadersRead</c> + 自己读 body。
    ///
    /// 为什么不用 <c>GetStringAsync</c>：它内部是 <c>ResponseContentRead</c>，整个响应（含 body）
    /// 只有 <c>HttpClient.Timeout</c> 一个兜底（本项目是 10 分钟）。而镜像换源靠的是 handler 层的
    /// **响应头**超时窗口（镜像元数据 5 秒 / 官方 30 秒，见 <c>DownloadInfrastructure</c>）——
    /// 头快、body 慢的镜像会让那个窗口完全失效，用户对着一个卡住的下载界面等十分钟。
    ///
    /// 改成 headers 模式后，handler 的窗口只管「头到没到」，body 由调用方的
    /// <see cref="CancellationToken"/> 与 <c>HttpClient.Timeout</c> 兜底，两段各管一段。
    /// </summary>
    private static async Task<string> ReadStringAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // 与 GetStringAsync 一致：非 2xx 抛 HttpRequestException，消息里带状态码，
        // 上层把它记进 failures（用户看到「404」比看到「请求失败」有用得多）。
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        // 编码必须显式指定：GetStringAsync 会读 Content-Type 的 charset，手写 StreamReader
        // 不指定就退化成 UTF-8。版本 JSON 一般是 UTF-8，但镜像站返回 gb18030 的情况真实存在，
        // 静默乱码会变成「JSON 解析失败」这种驴唇不对马嘴的报错。
        var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

#if NET8_0_OR_GREATER
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
#else
        return await reader.ReadToEndAsync().ConfigureAwait(false);
#endif
    }

    /// <summary>
    /// 按 <c>Content-Type</c> 的 charset 选编码；没写或写了不认识的名字一律 UTF-8
    /// （与 <c>HttpContent.ReadAsStringAsync</c> 的默认一致）。
    /// </summary>
    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            // 未知 charset 名（有些镜像会写 "utf8" 之外的花样）。退回 UTF-8 而不是抛异常——
            // 编码猜错最多是乱码后解析失败，抛异常会把「能拿到内容」变成「一条道都走不通」。
            return Encoding.UTF8;
        }
    }
}
