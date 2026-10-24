using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Versions;

/// <summary>
/// 本地已安装版本扫描、Mojang 版本清单（带 30 分钟缓存）、Fabric/Quilt 加载器版本查询。
/// 所有网络失败都退化为「用缓存」或「返回空集合」，不把异常抛给 UI。
/// </summary>
/// <remarks>
/// 不实现 <see cref="IDisposable"/>：<c>_forgePromotionsGate</c> 是进程级单例的字段，
/// 随进程退出一起回收。理由与 <c>SettingsService</c> 相同——主动 Dispose 反而可能把
/// 一次正在进行的后台请求打断在半个锁上。
/// </remarks>
#pragma warning disable CA1001
public sealed class VersionService : IVersionService
#pragma warning restore CA1001
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string ManifestMirrorUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    private const string ManifestCacheFileName = "version_manifest_v2.json";
    private const string LaunchRecordFileName = "nebula.json";

    /// <summary>Forge promoted 列表的落盘缓存（位于 <c>ILauncherPaths.CacheDirectory</c>）。</summary>
    public const string ForgePromotionsCacheFileName = "forge-promotions.json";

    /// <summary>promoted 列表的缓存有效期。它一个月才变一次，没必要每次进安装面板都拉。</summary>
    private static readonly TimeSpan ForgePromotionsTtl = TimeSpan.FromDays(1);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly VersionNameComparer NameComparer = new();

    private readonly ILauncherPaths _paths;
    private readonly IAppLog _log;
    private readonly HttpClient _http;

    /// <summary>下载源决定 Fabric meta 的首选地址（BMCLAPI 镜像 / 官方）。</summary>
    private readonly ISettingsService? _settings;
    private readonly IDownloadSourceProvider? _sourceProvider;

    private DownloadSource CurrentSource => _sourceProvider?.Effective ?? _settings?.Current.DownloadSource ?? DownloadSource.BmclApi;

    /// <summary>Forge promoted 列表的内存缓存，键是 Minecraft 版本、值是推荐构建号。</summary>
    private IReadOnlyDictionary<string, string>? _forgePromotions;

    /// <summary>内存缓存写入时刻。</summary>
    private DateTimeOffset _forgePromotionsCachedAt;

    /// <summary>promoted 列表的拉取闸门：切加载器时 UI 可能连点，避免并发打同一份 JSON。</summary>
    private readonly SemaphoreSlim _forgePromotionsGate = new(1, 1);

    public VersionService(
        ILauncherPaths paths,
        IAppLog log,
        HttpClient? httpClient = null,
        ISettingsService? settings = null,
        IDownloadSourceProvider? sourceProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http = httpClient ?? CreateDefaultClient();
        _settings = settings;
        _sourceProvider = sourceProvider;
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Modrinth / 部分 CDN 会拒绝没有 User-Agent 的请求；统一带上标识。
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NebulaLauncher", "1.0"));
        return client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var root = _paths.VersionsDirectory;

        if (!Directory.Exists(root))
        {
            return Array.Empty<VersionInfo>();
        }

        var results = new List<VersionInfo>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            var info = await ScanVersionAsync(name, cancellationToken).ConfigureAwait(false);

            if (info is not null)
            {
                results.Add(info);
            }
        }

        results.Sort((a, b) => NameComparer.Compare(b.Name, a.Name));
        return results;
    }

    /// <inheritdoc />
    public async Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        // 目录不存在 = 这个版本没有被安装；目录存在但 JSON 坏了则返回带 BrokenReason 的条目，
        // 让 UI 能区分「没装」和「装了但坏了」。
        if (!Directory.Exists(Path.Combine(_paths.VersionsDirectory, versionName)))
        {
            return null;
        }

        return await ScanVersionAsync(versionName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var cacheFile = Path.Combine(_paths.CacheDirectory, ManifestCacheFileName);

        try
        {
            if (!forceRefresh && File.Exists(cacheFile) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
            {
                var cachedJson = await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
                var cached = ParseManifest(cachedJson);

                if (cached.Count > 0)
                {
                    return cached;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("读取版本清单缓存失败", ex);
        }

        try
        {
            // 官方清单域名在国内偶尔直接不可达（DNS/连接重置），BMCLAPI 提供同构镜像。
            var candidates = DownloadMirror.PrefersMirror(CurrentSource)
                ? new[] { ManifestMirrorUrl, ManifestUrl }
                : new[] { ManifestUrl, ManifestMirrorUrl };

            var json = await ResourceFetcher.GetStringAsync(_http, candidates, cancellationToken, _log)
                .ConfigureAwait(false);
            var versions = ParseManifest(json);

            if (versions.Count == 0)
            {
                throw new InvalidOperationException("版本清单内容为空");
            }

            await WriteCacheAsync(cacheFile, json, cancellationToken).ConfigureAwait(false);
            return versions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("拉取 Mojang 版本清单失败，尝试回退缓存", ex);
        }

        // 离线/超时：过期缓存也比空列表有用。
        try
        {
            if (File.Exists(cacheFile))
            {
                var cachedJson = await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
                return ParseManifest(cachedJson);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("回退版本清单缓存失败", ex);
        }

        _log.Error("版本清单不可用：既没有网络也没有缓存");
        return Array.Empty<RemoteVersion>();
    }

    /// <inheritdoc />
    public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(versionName))
        {
            return Task.CompletedTask;
        }

        var directory = Path.Combine(_paths.VersionsDirectory, versionName);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _log.Info($"已删除版本目录：{directory}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"删除版本目录失败：{directory}", ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RecordLaunchAsync(
        string versionName,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return;
        }

        var directory = Path.Combine(_paths.VersionsDirectory, versionName);

        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var file = Path.Combine(directory, LaunchRecordFileName);
            var record = new LaunchRecord();

            if (File.Exists(file))
            {
                try
                {
                    var existing = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    record = JsonSerializer.Deserialize(existing, VersionJsonOptions.Of<LaunchRecord>()) ?? new LaunchRecord();
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    _log.Warn($"启动记录损坏，重新生成：{file}", ex);
                    record = new LaunchRecord();
                }
            }

            record.LastPlayedAt = DateTimeOffset.UtcNow;
            record.TotalPlayTimeSeconds += (long)Math.Max(0, duration.TotalSeconds);

            var json = JsonSerializer.Serialize(record, VersionJsonOptions.Of<LaunchRecord>());
            var temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"写入启动记录失败：{versionName}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
        LoaderKind loader,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return Array.Empty<LoaderVersion>();
        }

        // Forge 没有「可选版本全集」可用：构建列表由安装器自己决定，官方只公开
        // 「哪个构建被标为 recommended」。所以这里只回一条推荐版，UI 默认选中它，
        // 其余构建仍由安装器按「最新」装（PCL2 ModDownloadLib.vb:1351-1361 的同一取舍）。
        if (loader == LoaderKind.Forge)
        {
            var recommended = await GetForgeRecommendedAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(recommended)
                ? Array.Empty<LoaderVersion>()
                : [
                    new LoaderVersion
                    {
                        Version = recommended,
                        Stable = true,
                        GameVersion = minecraftVersion,
                        IsRecommended = true,
                    },
                ];
        }

        // NeoForge 的可选版本来自 maven 目录接口，判据与 Forge 的「推荐版」不是一回事：
        // Forge 只有 promoted 列表（一个值），NeoForge 有完整目录（可挑具体构建）。
        if (loader == LoaderKind.NeoForge)
        {
            return await GetNeoForgeVersionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
        }

        var candidates = LoaderMetaUrls.Versions(loader, minecraftVersion, CurrentSource);

        if (candidates.Count == 0)
        {
            // 其余加载器（LiteLoader 等）没有纳入本版本管理的 meta 渠道。
            return Array.Empty<LoaderVersion>();
        }

        try
        {
            var json = await ResourceFetcher.GetStringAsync(_http, candidates, cancellationToken, _log).ConfigureAwait(false);
            return ParseLoaderVersions(json, minecraftVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"拉取 {loader} 加载器版本失败（已尝试 {candidates.Count} 个地址）", ex);
            return Array.Empty<LoaderVersion>();
        }
    }

    // ---------------- 仅下载客户端 jar ----------------

    /// <summary>沿 <c>inheritsFrom</c> 找 <c>downloads.client</c> 的最大层数（加载器 → 原版通常 1 层）。</summary>
    private const int ClientDownloadInheritDepth = 6;

    /// <inheritdoc />
    public async Task<ClientJarSaveResult> SaveClientJarAsync(
        string versionName,
        string targetDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var name = (versionName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return ClientJarSaveResult.Fail(name, targetDirectory, "版本名为空");
        }

        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\\')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return ClientJarSaveResult.Fail(name, targetDirectory, "版本名包含非法路径或文件名字符");
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return ClientJarSaveResult.Fail(name, targetDirectory, "目标目录为空");
        }

        string directory;

        try
        {
            directory = Path.GetFullPath(targetDirectory.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ClientJarSaveResult.Fail(name, targetDirectory, $"目标目录无效：{ex.Message}");
        }

        var filePath = Path.Combine(directory, name + ".jar");

        var download = await ResolveClientDownloadAsync(name, cancellationToken).ConfigureAwait(false);

        if (download.Url is null)
        {
            return ClientJarSaveResult.Fail(
                name,
                filePath,
                $"版本 {name} 的 JSON 里没有 downloads.client。"
                + "该版本可能被第三方工具改写过，或只装了加载器元数据。");
        }

        // 已经下过且 SHA1 对得上：不重下。客户端 jar 动辄十几 MB，重复下载对流量不友好。
        if (File.Exists(filePath) && download.Sha1 is { Length: > 0 } expectedSha1)
        {
            var existingSha1 = await TryComputeSha1Async(filePath, cancellationToken).ConfigureAwait(false);

            if (string.Equals(existingSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                return new ClientJarSaveResult
                {
                    VersionName = name,
                    FilePath = filePath,
                    Size = SafeLength(filePath),
                    Sha1 = existingSha1,
                    AlreadyExisted = true,
                    Success = true,
                    Duration = started.Elapsed,
                };
            }
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ClientJarSaveResult.Fail(name, filePath, $"无法创建目录 {directory}：{ex.Message}");
        }

        var queue = new DownloadQueue(_http, _log, maxConcurrency: 2);
        DownloadBatchResult batch;

        try
        {
            batch = await queue.DownloadAsync(
                [
                    new DownloadItem
                    {
                        Url = download.Url,
                        DestinationPath = filePath,
                        Id = name,
                        Group = name,
                        ExpectedSize = download.Size,
                        ExpectedSha1 = download.Sha1,
                    },
                ],
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消也把目标文件删掉：半截 jar 在磁盘上比没有更糟糕（拷给别人会直接崩在启动）。
            TryDelete(filePath);
            throw;
        }

        if (!batch.Success)
        {
            TryDelete(filePath);
            var error = batch.Failures.Count > 0 ? batch.Failures[0].Error : "下载失败";
            _log.Warn($"保存客户端 jar 失败：{name}（{filePath}）：{error}");

            return new ClientJarSaveResult
            {
                VersionName = name,
                FilePath = filePath,
                Success = false,
                Error = error,
                Duration = started.Elapsed,
            };
        }

        var actualSha1 = await TryComputeSha1Async(filePath, cancellationToken).ConfigureAwait(false);

        return new ClientJarSaveResult
        {
            VersionName = name,
            FilePath = filePath,
            Size = SafeLength(filePath),
            Sha1 = actualSha1,
            Success = true,
            Duration = started.Elapsed,
        };
    }

    /// <summary>
    /// 从版本 JSON 里取 <c>downloads.client</c>，找不到就沿 <c>inheritsFrom</c> 往父版本走。
    ///
    /// 为什么要走继承链：Fabric / Quilt 的 profile JSON 只描述自己的增量，
    /// <c>downloads</c> 往往是空对象；客户端 jar 永远挂在原版那一层的 JSON 上。
    /// </summary>
    private async Task<(string? Url, string? Sha1, long Size)> ResolveClientDownloadAsync(
        string versionName,
        CancellationToken cancellationToken)
    {
        var current = versionName;

        for (var depth = 0; depth < ClientDownloadInheritDepth; depth++)
        {
            var jsonFile = Path.Combine(_paths.VersionsDirectory, current, current + ".json");

            if (!File.Exists(jsonFile))
            {
                return (null, null, 0);
            }

            try
            {
                var json = await File.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

                var root = document.RootElement;

                if (root.TryGetProperty("downloads", out var downloads) &&
                    downloads.ValueKind == JsonValueKind.Object &&
                    downloads.TryGetProperty("client", out var client) &&
                    client.ValueKind == JsonValueKind.Object &&
                    GetString(client, "url") is { Length: > 0 } url)
                {
                    var sha1 = GetString(client, "sha1");
                    var size = client.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                        ? value
                        : 0;

                    return (url, sha1, size);
                }

                var parent = GetString(root, "inheritsFrom");

                if (string.IsNullOrEmpty(parent))
                {
                    return (null, null, 0);
                }

                current = parent;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"解析 downloads.client 失败：{jsonFile}", ex);
                return (null, null, 0);
            }
        }

        return (null, null, 0);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA1 是 Mojang 在版本 JSON 里给出的官方校验值，此处只做完整性比对，不承担安全用途")]
    private static async Task<string?> TryComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
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
        catch (Exception)
        {
            // 删不掉只留下一个坏文件，不值得为它抛。
        }
    }

    // ---------------- NeoForge 版本目录 ----------------

    /// <summary>
    /// NeoForge 目录名形状：<c>[&lt;mc&gt;-]主.次.构建[.修订][-beta|alpha[.N]][+snapshot-N]</c>。
    /// 「新式」目录名不含 MC 前缀（<c>21.1.264</c>），Legacy 含（<c>1.20.1-47.1.106</c>）。
    /// 快照版的 <c>0.25w14craftmine.3-beta</c> 里的「次」不是数字，会被排除——它们不对应任何正式 MC 版本。
    /// </summary>
    private static readonly Regex NeoForgeNamePattern = new(
        @"^(?:(?<mc>\d+\.\d+\.\d+)-)?(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?:\.\d+)?(?:-(?:beta|alpha)(?:\.\d+)?)?(?:\+snapshot-\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// NeoForge 的可选版本列表。
    ///
    /// 判据照抄 PCL2 <c>ModDownload.vb:936-946 GetNeoForgeEntries</c>：拉「新式」
    /// （<c>net/neoforged/neoforge</c>）与「Legacy」（<c>net/neoforged/forge</c>）两份目录，
    /// 抽出全部目录名，排除目录里存在但构件已撤下的 <c>47.1.82</c>，按版本号降序。
    ///
    /// 与 PCL2 的差异：PCL2 不过滤 MC 版本（把一千八百多项全列出来），本实现按目录名反推 MC 版本，
    /// 只保留与当前所选版本一致的项——安装面板的下拉框里塞跨版本构建没有意义。
    /// </summary>
    private async Task<IReadOnlyList<LoaderVersion>> GetNeoForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var wanted = minecraftVersion.Trim();
        var names = new List<string>();

        // 首选组整族优先，只有整族都失败才试兜底组。
        // 不做「同一次尝试里官方紧跟镜像」：官方 maven.neoforged.net 在本机 TCP 超时，
        // 每次都等两个 21 秒的连接超时，UI 上的表现就是加载器版本永远「加载中…」。
        foreach (var group in new[]
                 {
                     LoaderMetaUrls.NeoForgeCatalog(CurrentSource),
                     LoaderMetaUrls.NeoForgeCatalogBackup(CurrentSource),
                 })
        {
            foreach (var url in group)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var json = await ResourceFetcher
                        .GetStringAsync(_http, [url], cancellationToken, _log, attemptsPerUrl: 1)
                        .ConfigureAwait(false);

                    names.AddRange(ParseNeoForgeCatalogNames(json));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"拉取 NeoForge 版本目录失败：{url}", ex);
                }
            }

            if (names.Count > 0)
            {
                break;
            }
        }

        if (names.Count == 0)
        {
            _log.Warn("NeoForge 版本目录全部不可用，只能装最新稳定版");
            return Array.Empty<LoaderVersion>();
        }

        var versions = new List<LoaderVersion>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 目录里有、maven 里取不到（PCL2 ModDownload.vb:941 同一排除项）。
            if (string.Equals(name, "47.1.82", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (NeoForgeGameVersionOf(name) is not { } gameVersion
                || !string.Equals(gameVersion, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            versions.Add(new LoaderVersion
            {
                Version = name,
                GameVersion = gameVersion,
                Stable = !IsNeoForgePrerelease(name),
            });
        }

        versions.Sort(CompareNeoForgeVersions);
        return versions;
    }

    /// <summary>
    /// 从目录名反推对应的 Minecraft 版本。Legacy 直接带前缀；
    /// 新式的「主.次」就是 MC 的 <c>1.主.次</c>（<c>21.1.264</c> → <c>1.21.1</c>、<c>20.4.30</c> → <c>1.20.4</c>）。
    /// 解析不出返回 null（快照、格式外的名字）。
    /// </summary>
    internal static string? NeoForgeGameVersionOf(string name)
    {
        var match = NeoForgeNamePattern.Match(name ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups["mc"].Success)
        {
            return match.Groups["mc"].Value;
        }

        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Value;

        return minor == "0" ? $"1.{major}" : $"1.{major}.{minor}";
    }

    internal static bool IsNeoForgePrerelease(string name) =>
        name.Contains("-beta", StringComparison.OrdinalIgnoreCase)
        || name.Contains("-alpha", StringComparison.OrdinalIgnoreCase)
        || name.Contains("+snapshot-", StringComparison.OrdinalIgnoreCase);

    /// <summary>按「主.次.构建」降序；同段时稳定版排在 beta/alpha 前面。</summary>
    private static int CompareNeoForgeVersions(LoaderVersion left, LoaderVersion right)
    {
        var byVersion = NeoForgeSortKey(right.Version).CompareTo(NeoForgeSortKey(left.Version));
        return byVersion != 0 ? byVersion : right.Stable.CompareTo(left.Stable);
    }

    private static ulong NeoForgeSortKey(string name)
    {
        var match = NeoForgeNamePattern.Match(name ?? string.Empty);
        if (!match.Success)
        {
            return 0;
        }

        static ulong Part(Group group) => ulong.TryParse(group.Value, out var value) ? value : 0;

        return (Part(match.Groups["major"]) << 40)
               | (Part(match.Groups["minor"]) << 20)
               | Part(match.Groups["build"]);
    }

    /// <summary>
    /// 解析版本目录 JSON。两种形状都认：
    /// BMCLAPI（nexus 目录列表）<c>{"files":[{"name":"21.1.264","type":"DIRECTORY"}]}</c>；
    /// 官方 maven API <c>{"versions":["21.1.264", ...]}</c>。
    /// </summary>
    internal static IReadOnlyList<string> ParseNeoForgeCatalogNames(string json)
    {
        var names = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    // 目录列表里除了子目录还有文件（jar / pom），只留 DIRECTORY。
                    if (file.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && !string.Equals(type.GetString(), "DIRECTORY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (file.TryGetProperty("name", out var node)
                        && node.ValueKind == JsonValueKind.String
                        && node.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("versions", out var versions)
                && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versions.EnumerateArray())
                {
                    if (version.ValueKind == JsonValueKind.String
                        && version.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 坏 JSON 当空列表：调用方会退化成「只能装最新版」。
        }

        return names;
    }

    // ---------------- Forge 推荐版 ----------------

    /// <summary>
    /// 取某个 Minecraft 版本对应的 Forge 推荐构建号；没有推荐或网络不可用时返回 null。
    ///
    /// 「推荐版」是 Forge 官方在 <c>promotions_slim.json</c> 里标的 <c>&lt;mc&gt;-recommended</c>。
    /// 为什么不拿「最新构建」当默认：最新的 Forge 构建经常是坏的，新手按默认装到它，
    /// 表现就是「装完启动崩、日志看不出原因」——这正是这条能力要解决的问题。
    /// </summary>
    private async Task<string?> GetForgeRecommendedAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        var promotions = await GetForgePromotionsAsync(cancellationToken).ConfigureAwait(false);

        return promotions.TryGetValue(minecraftVersion, out var build) && !string.IsNullOrWhiteSpace(build)
            ? build
            : null;
    }

    /// <summary>
    /// 内存缓存 → 磁盘缓存 → 网络 三级取 promoted 列表。任何一级失败都退到上一级的旧值，
    /// 全都没有就返回空表：安装面板照常可用，只是没有「推荐版」标记。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetForgePromotionsAsync(CancellationToken cancellationToken)
    {
        if (IsForgePromotionsFresh())
        {
            return _forgePromotions!;
        }

        await _forgePromotionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 闸门外已经判过一次，这里再判是为了「并发进来的第二个调用方不要重复拉」。
            if (IsForgePromotionsFresh())
            {
                return _forgePromotions!;
            }

            var cached = await TryReadForgePromotionsCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return AdoptForgePromotions(cached.Promotions, cached.CachedAt);
            }

            try
            {
                var json = await ResourceFetcher
                    .GetStringAsync(_http, LoaderMetaUrls.ForgePromotions(), cancellationToken, _log)
                    .ConfigureAwait(false);

                var promotions = ParseForgePromotions(json);
                if (promotions.Count == 0)
                {
                    throw new InvalidOperationException("promoted 列表里没有任何 recommended 条目");
                }

                var now = DateTimeOffset.UtcNow;
                await TryWriteForgePromotionsCacheAsync(promotions, now, cancellationToken).ConfigureAwait(false);
                _log?.Info($"已刷新 Forge 推荐版列表（{promotions.Count} 个版本有推荐构建）");
                return AdoptForgePromotions(promotions, now);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn("拉取 Forge 推荐版列表失败，本次不显示推荐版", ex);
                // 空表也要缓存一小段时间的作用：不然每次切加载器都重新发一遍请求。
                return AdoptForgePromotions(new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            _forgePromotionsGate.Release();
        }
    }

    private bool IsForgePromotionsFresh() =>
        _forgePromotions is not null && DateTimeOffset.UtcNow - _forgePromotionsCachedAt < ForgePromotionsTtl;

    private IReadOnlyDictionary<string, string> AdoptForgePromotions(
        IReadOnlyDictionary<string, string> promotions,
        DateTimeOffset cachedAt)
    {
        _forgePromotions = promotions;
        _forgePromotionsCachedAt = cachedAt;
        return promotions;
    }

    private async Task<ForgePromotionsCache?> TryReadForgePromotionsCacheAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(_paths.CacheDirectory, ForgePromotionsCacheFileName);

        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(file) >= ForgePromotionsTtl)
            {
                // 过期的磁盘缓存仍然要能当兜底，所以不删也不跳过，只是标记成旧值继续用。
                _log?.Debug($"Forge 推荐版缓存已过期，仍作为兜底使用：{file}");
            }

            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (!document.RootElement.TryGetProperty("promotions", out var promotions) ||
                promotions.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in promotions.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String &&
                    entry.Value.GetString() is { Length: > 0 } build)
                {
                    map[entry.Name] = build;
                }
            }

            if (map.Count == 0)
            {
                return null;
            }

            var cachedAt = document.RootElement.TryGetProperty("cachedAtUtc", out var stamp) &&
                           stamp.ValueKind == JsonValueKind.String &&
                           DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            return new ForgePromotionsCache(map, cachedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取 Forge 推荐版缓存失败：{file}", ex);
            return null;
        }
    }

    private async Task TryWriteForgePromotionsCacheAsync(
        IReadOnlyDictionary<string, string> promotions,
        DateTimeOffset cachedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = _paths.CacheDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);

            var payload = new ForgePromotionsCacheDocument(cachedAt, promotions);

            var json = JsonSerializer.Serialize(payload, VersionJsonOptions.Of<ForgePromotionsCacheDocument>());
            await WriteCacheAsync(
                    Path.Combine(directory, ForgePromotionsCacheFileName),
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 缓存写不进去只是下次多一次网络请求，不能影响本次结果。
            _log?.Warn("写入 Forge 推荐版缓存失败", ex);
        }
    }

    /// <summary>
    /// 解析 Forge 的 promoted 列表，返回「Minecraft 版本 → 推荐构建号」。
    ///
    /// 两种形状都要认（两个候选地址返回的结构不同，见 <see cref="LoaderMetaUrls.ForgePromotions"/>）：
    /// <list type="bullet">
    /// <item>官方 <c>promotions_slim.json</c>：<c>{"promos":{"1.20.1-recommended":"47.2.0", ...}}</c>；</item>
    /// <item>BMCLAPI <c>/forge/promos</c>：<c>[{"name":"1.20.1-recommended","build":{"version":"47.2.0"}}, ...]</c>。</item>
    /// </list>
    /// 只收 <c>-recommended</c> 后缀：<c>-latest</c> 是「最新构建」，正是要避开的那个。
    /// 不带 Minecraft 前缀的裸 <c>recommended</c> 键（指当前最新版本的缩写）直接跳过——
    /// 它无法映射到具体版本号，猜错会让用户装到别的版本的构建。
    /// </summary>
    internal static Dictionary<string, string> ParseForgePromotions(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("promos", out var promos) &&
            promos.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in promos.EnumerateObject())
            {
                AddPromotion(result, entry.Name, entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null);
            }

            return result;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(item, "name");
                var build = item.TryGetProperty("build", out var buildElement) && buildElement.ValueKind == JsonValueKind.Object
                    ? GetString(buildElement, "version")
                    : null;

                AddPromotion(result, name, build);
            }
        }

        return result;
    }

    private static void AddPromotion(Dictionary<string, string> result, string? promoName, string? build)
    {
        const string suffix = "-recommended";

        if (string.IsNullOrWhiteSpace(promoName) || string.IsNullOrWhiteSpace(build))
        {
            return;
        }

        var trimmed = promoName.Trim();

        // 必须长度大于后缀本身：裸 "recommended" 没有 Minecraft 版本前缀，映射不到具体版本。
        if (trimmed.Length <= suffix.Length ||
            !trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var minecraftVersion = trimmed[..^suffix.Length].Trim();

        if (minecraftVersion.Length > 0)
        {
            result[minecraftVersion] = build.Trim();
        }
    }

    /// <summary>磁盘缓存的形状：<c>{"cachedAtUtc":"…","promotions":{"1.20.1":"47.2.0"}}</c>。</summary>
    private sealed record ForgePromotionsCache(IReadOnlyDictionary<string, string> Promotions, DateTimeOffset CachedAt);

    private async Task<VersionInfo?> ScanVersionAsync(string name, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_paths.VersionsDirectory, name);
        var jsonFile = Path.Combine(directory, name + ".json");

        if (!File.Exists(jsonFile))
        {
            return new VersionInfo
            {
                Name = name,
                BrokenReason = $"缺少版本描述文件 {name}.json",
            };
        }

        string? inheritsFrom = null;
        var channel = VersionChannel.Release;

        try
        {
            var json = await File.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = document.RootElement;
            inheritsFrom = GetString(root, "inheritsFrom");

            // 渠道判定与清单用同一个分类器：本地装的愚人节版要显示成「愚人节」，
            // 只按 type 映射会退回「快照」。releaseTime 缺失时退化成「不是 4/1」，不影响 id 白名单。
            channel = VersionChannelClassifier.Classify(
                GetString(root, "id") ?? name,
                GetString(root, "type"),
                ParseTimestamp(root, "releaseTime"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"解析版本描述失败：{jsonFile}", ex);
            return new VersionInfo
            {
                Name = name,
                BrokenReason = "版本 JSON 解析失败：" + ex.Message,
            };
        }

        var minecraftVersion = await ResolveMinecraftVersionAsync(name, cancellationToken).ConfigureAwait(false);
        var (loader, loaderVersion) = DetectLoader(name, inheritsFrom, minecraftVersion);

        DateTimeOffset? lastPlayedAt = null;
        long totalPlayTime = 0;

        var launchRecord = Path.Combine(directory, LaunchRecordFileName);
        if (File.Exists(launchRecord))
        {
            try
            {
                var json = await File.ReadAllTextAsync(launchRecord, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize(json, VersionJsonOptions.Of<LaunchRecord>());
                lastPlayedAt = record?.LastPlayedAt;
                totalPlayTime = record?.TotalPlayTimeSeconds ?? 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"读取启动记录失败：{launchRecord}", ex);
            }
        }

        return new VersionInfo
        {
            Name = name,
            Channel = channel,
            InheritsFrom = inheritsFrom,
            Loader = loader,
            LoaderVersion = loaderVersion,
            MinecraftVersion = minecraftVersion,
            LastPlayedAt = lastPlayedAt,
            TotalPlayTimeSeconds = totalPlayTime,
        };
    }

    /// <summary>沿 inheritsFrom 链回溯到最底层的原版版本号。</summary>
    private async Task<string?> ResolveMinecraftVersionAsync(string name, CancellationToken cancellationToken)
    {
        var current = name;

        // 深度上限防止环形继承把扫描卡死。
        for (var depth = 0; depth < 10; depth++)
        {
            var jsonFile = Path.Combine(_paths.VersionsDirectory, current, current + ".json");

            if (!File.Exists(jsonFile))
            {
                return current;
            }

            try
            {
                var json = await File.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

                var parent = GetString(document.RootElement, "inheritsFrom");

                if (string.IsNullOrEmpty(parent))
                {
                    return current;
                }

                current = parent;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"回溯继承链失败：{jsonFile}", ex);
                return current;
            }
        }

        return current;
    }

    /// <summary>
    /// 从版本目录名推断加载器种类与加载器版本。
    /// 命名约定来自各加载器安装器的默认产物：<c>fabric-loader-&lt;loader&gt;-&lt;mc&gt;</c>、
    /// <c>&lt;mc&gt;-forge-&lt;loader&gt;</c>、<c>neoforge-&lt;loader&gt;</c>、<c>quilt-loader-&lt;loader&gt;-&lt;mc&gt;</c>。
    ///
    /// **父版本名优先**：整合包实例的目录名是玩家起的（「Better MC [FORGE] BMC4」），
    /// 名字里出现的 forge / fabric 只是标题，不代表实际加载器——真正的加载器在
    /// <c>inheritsFrom</c> 指向的版本名里（PCL2 同样从版本链取，不看目录名）。
    /// 父版本名识别不出时再退回自身名字；父版本名里的原版版本号用沿链解析出的
    /// <paramref name="minecraftVersion"/> 剥离——fabric / quilt 的 <c>&lt;mc&gt;</c> 挂在尾部，
    /// 不用它就会把 <c>0.15.0-1.20.1</c> 整段当成加载器版本。
    /// </summary>
    private static (LoaderKind Kind, string? LoaderVersion) DetectLoader(
        string id,
        string? inheritsFrom,
        string? minecraftVersion)
    {
        if (!string.IsNullOrWhiteSpace(inheritsFrom))
        {
            var parent = DetectLoaderFromName(inheritsFrom, minecraftVersion ?? inheritsFrom);

            if (parent.Kind != LoaderKind.Vanilla)
            {
                return parent;
            }
        }

        return DetectLoaderFromName(id, inheritsFrom);
    }

    private static (LoaderKind Kind, string? LoaderVersion) DetectLoaderFromName(string id, string? inheritsFrom)
    {
        var lower = id.ToLowerInvariant();

        // neoforge 里含 "forge"，必须先判断。
        if (lower.Contains("neoforge", StringComparison.Ordinal))
        {
            return (LoaderKind.NeoForge, ExtractLoaderVersion(id, lower, "neoforge", inheritsFrom));
        }

        if (lower.Contains("quilt", StringComparison.Ordinal))
        {
            var marker = lower.Contains("quilt-loader", StringComparison.Ordinal) ? "quilt-loader" : "quilt";
            return (LoaderKind.Quilt, ExtractLoaderVersion(id, lower, marker, inheritsFrom));
        }

        if (lower.Contains("fabric", StringComparison.Ordinal))
        {
            var marker = lower.Contains("fabric-loader", StringComparison.Ordinal) ? "fabric-loader" : "fabric";
            return (LoaderKind.Fabric, ExtractLoaderVersion(id, lower, marker, inheritsFrom));
        }

        if (lower.Contains("forge", StringComparison.Ordinal))
        {
            return (LoaderKind.Forge, ExtractLoaderVersion(id, lower, "forge", inheritsFrom));
        }

        return (LoaderKind.Vanilla, null);
    }

    private static string? ExtractLoaderVersion(string id, string lower, string marker, string? inheritsFrom)
    {
        var index = lower.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
        {
            return null;
        }

        var rest = id[(index + marker.Length)..].TrimStart('-', '_', '+', ' ');

        if (rest.Length == 0)
        {
            return null;
        }

        // <mc>-forge-<loader>：marker 后面还挂着原版版本号。
        if (!string.IsNullOrEmpty(inheritsFrom) &&
            rest.StartsWith(inheritsFrom + "-", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[(inheritsFrom.Length + 1)..];
        }

        // fabric-loader-<loader>-<mc>：原版版本号挂在尾部。
        if (!string.IsNullOrEmpty(inheritsFrom) &&
            rest.EndsWith("-" + inheritsFrom, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[..^(inheritsFrom.Length + 1)];
        }

        return rest.Length == 0 ? null : rest;
    }

    private static List<RemoteVersion> ParseManifest(string json)
    {
        var versions = new List<RemoteVersion>();

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (!document.RootElement.TryGetProperty("versions", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return versions;
        }

        foreach (var item in array.EnumerateArray())
        {
            var id = GetString(item, "id");
            var url = GetString(item, "url");

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url))
            {
                continue;
            }

            versions.Add(new RemoteVersion
            {
                Id = id,
                Type = GetString(item, "type") ?? "release",
                Url = url,
                ReleaseTime = ParseTimestamp(item, "releaseTime"),
            });
        }

        return versions;
    }

    private static List<LoaderVersion> ParseLoaderVersions(string json, string minecraftVersion)
    {
        var result = new List<LoaderVersion>();

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            // Fabric 与 Quilt 都把加载器信息包在 "loader" 对象里。
            var loaderElement = item.TryGetProperty("loader", out var loader) ? loader : item;
            var version = GetString(loaderElement, "version");

            if (string.IsNullOrEmpty(version))
            {
                continue;
            }

            var stable = loaderElement.TryGetProperty("stable", out var stableElement) &&
                         stableElement.ValueKind == JsonValueKind.True;

            result.Add(new LoaderVersion
            {
                Version = version,
                Stable = stable,
                GameVersion = minecraftVersion,
            });
        }

        return result;
    }

    private static async Task WriteCacheAsync(string cacheFile, string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cacheFile);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = cacheFile + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, cacheFile, overwrite: true);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset ParseTimestamp(JsonElement element, string property)
    {
        var text = GetString(element, property);

        return DateTimeOffset.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }

    // 原来的 MapChannel(type) 已被 VersionChannelClassifier 取代：它只看 type，
    // 无法纠正 Mojang 标成 snapshot 的 1.x 正式版，也认不出愚人节版。
    private static readonly JsonSerializerOptions VersionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = new VersionJsonContext(),
    };

    // LaunchRecord 提到文件末尾（internal）：Source Generator 需要能在同程序集里引用它。

    /// <summary>按版本号语义降序比较：数字段按数值比，其他段按序数比。</summary>
    private sealed class VersionNameComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var xs = x.Split('.', '-', '_');
            var ys = y.Split('.', '-', '_');

            var length = Math.Min(xs.Length, ys.Length);

            for (var i = 0; i < length; i++)
            {
                var leftIsNumber = long.TryParse(xs[i], out var leftNumber);
                var rightIsNumber = long.TryParse(ys[i], out var rightNumber);

                var comparison = leftIsNumber && rightIsNumber
                    ? leftNumber.CompareTo(rightNumber)
                    : string.Compare(xs[i], ys[i], StringComparison.OrdinalIgnoreCase);

                if (comparison != 0)
                {
                    return comparison;
                }
            }

            // 前缀相同，段数多的视为更新（1.20.1 > 1.20）。
            return xs.Length.CompareTo(ys.Length);
        }
    }
}

/// <summary>
/// <c>cache/forge-promotions.json</c> 的形状：拉取时间 + 版本号到推荐版的映射。
/// </summary>
internal sealed record ForgePromotionsCacheDocument(
    DateTimeOffset CachedAtUtc,
    IReadOnlyDictionary<string, string> Promotions);

/// <summary>
/// 版本相关 JSON 的 Source Generator 上下文。
///
/// 注意 <see cref="VersionService.LaunchRecord"/> 仍然留在 <c>VersionService</c> 内部：
/// 它是 private 嵌套类型，这里只通过 <see cref="JsonSerializableAttribute"/> 登记它。
/// </summary>
[JsonSerializable(typeof(LaunchRecord))]
[JsonSerializable(typeof(ForgePromotionsCacheDocument))]
internal sealed partial class VersionJsonContext : JsonSerializerContext
{
}

/// <summary><c>versions/&lt;name&gt;/nebula.json</c> 的形状。</summary>
internal sealed class LaunchRecord
{
    public DateTimeOffset? LastPlayedAt { get; set; }

    public long TotalPlayTimeSeconds { get; set; }
}
