using System.Globalization;
using System.IO;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 用「文件哈希 → 资源站项目」这条链路补全本地内容的展示信息，并落下 6 小时的项目缓存。
///
/// <para/>
/// 数据流（与 PCL2 <c>LocalResourceLoaders.vb</c> 同形，但把两条批量接口都走到了）：
/// <list type="number">
/// <item>本地算 SHA1 与 CurseForge 指纹（<see cref="ModFingerprint"/> 自带落盘缓存）。</item>
/// <item>Modrinth：<c>POST /v2/version_files</c> 批量换 projectId，再一次 <c>/v2/projects?ids=</c> 批量取项目。</item>
/// <item>CurseForge（已配置 key 时）：<c>POST /v1/fingerprints</c> 批量换 modId，再一次 <c>POST /v1/mods</c> 批量取项目。</item>
/// <item>拿 slug 查 <see cref="ModNameTranslations"/> 得中文名，下载图标到缓存目录。</item>
/// </list>
///
/// 覆盖模组 / 资源包 / 光影包 / 数据包四类本地内容：资源站对四种都有项目，哈希反查接口本身与类型无关。
/// 包内嵌图标（模组的 <c>icon</c> / <c>logoFile</c>、资源包的 <c>pack.png</c>）只是少数，
/// 资源站图标才是绝大多数卡片能显示图标的原因，所以这一步覆盖不到的类型等于「永远没有图标」。
///
/// 为什么整条链路只对「未缓存」的条目发起：进一次内容页就走一遍全量请求的话，
/// 100 个模组的实例每次切页面都要几十次网络往返。缓存按「SHA1 / 指纹」而不是文件名做键，
/// 所以用户重命名 jar 也不会失效。
/// </summary>
public sealed class ModProjectInfoService : IModProjectInfoService
{
    /// <summary>缓存落在 <see cref="ILauncherPaths.CacheDirectory"/> 下，用这个名字。</summary>
    public const string CacheFileName = "mod-projects.json";

    /// <summary>缓存有效期。与 PCL2 的 6 小时一致：项目名与译名是慢变量，更新状态才需要实时。</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    /// <summary>缓存条目上限，防止长期使用后 JSON 无限增长。</summary>
    public const int MaxCacheEntries = 512;

    /// <summary>哈希计算的并发上限。机械硬盘上同时读 16 个大 jar 会互相拖慢。</summary>
    public const int MaxParallelHashes = 4;

    /// <summary>图标下载的并发上限。图标是 CDN 上的小图，2 路足够，也不需要抢带宽。</summary>
    public const int MaxParallelIcons = 2;

    private const int CacheSchemaVersion = 1;

    private static readonly object CacheWriteGate = new();

    private readonly IModrinthService? _modrinth;
    private readonly ICurseForgeService? _curseForge;
    private readonly ILauncherPaths? _paths;
    private readonly IAppLog? _log;
    private readonly ModFingerprint _fingerprint;

    /// <summary>所有依赖都允许缺失：页面在服务没接线时也要能渲染。</summary>
    public ModProjectInfoService(
        IModrinthService? modrinth = null,
        ICurseForgeService? curseForge = null,
        ILauncherPaths? paths = null,
        IAppLog? log = null)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
        _paths = paths;
        _log = log;
        _fingerprint = new ModFingerprint(paths?.CacheDirectory, log);
    }

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <summary>
    /// 能补全在线身份的内容类型。
    ///
    /// 资源站对四种本地内容都有对应项目（Modrinth 的 <c>project_type</c>、CurseForge 的 <c>classId</c>
    /// 各有一套），而哈希反查接口本身与内容类型无关，所以同一条链路可以全部走到。
    /// 之前只放行 <see cref="ContentKind.Mod"/>，代价是资源包 / 光影包 / 数据包的卡片永远拿不到
    /// 资源站图标——它们恰恰是「包里常常没有 <c>pack.png</c>」的那几类。
    /// </summary>
    private static readonly ContentKind[] EnrichableKinds =
    [
        ContentKind.Mod,
        ContentKind.ResourcePack,
        ContentKind.ShaderPack,
        ContentKind.DataPack,
    ];

    /// <inheritdoc />
    public async Task<ModProjectLookupResult> EnrichAsync(
        IReadOnlyList<ContentItem> items,
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        // 只处理磁盘上真实存在的「文件」：数据包还有目录形态（<c>datapacks/&lt;包名&gt;/</c>），
        // 目录算不出哈希，硬塞进去只会得到一条空结果。
        var targets = (items ?? Array.Empty<ContentItem>())
            .Where(item => item is not null && IsEnrichable(item))
            .ToList();

        if (targets.Count == 0)
        {
            return ModProjectLookupResult.Empty;
        }

        if (_modrinth is null && _curseForge is null)
        {
            LastError = "在线数据源不可用：Modrinth 与 CurseForge 都没有接线。";
            return new ModProjectLookupResult
            {
                Total = targets.Count,
                Error = LastError,
            };
        }

        // ---------------- 阶段 1：本地哈希 ----------------
        var hashes = new ModFileHashes?[targets.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelHashes,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                hashes[index] = await _fingerprint
                    .GetHashesAsync(targets[index].FilePath, gameDirectory, token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // ---------------- 阶段 2：缓存命中 ----------------
        var cache = LoadCache();
        var now = DateTimeOffset.UtcNow;
        var drafts = new Draft?[targets.Count];
        var pending = new List<int>();

        for (var index = 0; index < targets.Count; index++)
        {
            var key = CacheKeyOf(hashes[index]);

            if (key is not null
                && cache.TryGetValue(key, out var entry)
                && now - entry.StoredAt <= CacheLifetime)
            {
                drafts[index] = new Draft
                {
                    Key = key,
                    FromCache = true,
                    Source = ParseSource(entry.Source),
                    ProjectId = entry.ProjectId,
                    Slug = entry.Slug,
                    Title = entry.Title,
                    Description = entry.Description,
                    Authors = entry.Authors,
                    IconPath = entry.IconPath,
                    WebUrl = entry.WebUrl,
                };

                continue;
            }

            pending.Add(index);
        }

        // ---------------- 阶段 3：Modrinth 批量反查 ----------------
        if (_modrinth is not null && pending.Count > 0)
        {
            await FillFromModrinthAsync(hashes, drafts, pending, cancellationToken).ConfigureAwait(false);
        }

        // ---------------- 阶段 4：CurseForge 补漏 ----------------
        var curseForgePending = pending.Where(index => drafts[index] is null).ToList();

        if (_curseForge is not null && curseForgePending.Count > 0)
        {
            await FillFromCurseForgeAsync(targets, hashes, drafts, curseForgePending, cancellationToken)
                .ConfigureAwait(false);
        }

        // ---------------- 阶段 5：图标（只补本次新识别、没有缓存图标的） ----------------
        var iconTargets = Enumerable.Range(0, targets.Count)
            .Where(index => drafts[index] is { IconPath: null } draft && !string.IsNullOrWhiteSpace(draft.IconUrl))
            .ToList();

        if (iconTargets.Count > 0)
        {
            await Parallel.ForEachAsync(
                iconTargets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelIcons,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    var draft = drafts[index]!;
                    draft.IconPath = await CacheIconAsync(draft, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        // ---------------- 阶段 6：组装 + 写缓存 ----------------
        var results = new List<ModProjectInfo>(targets.Count);
        var matched = 0;

        for (var index = 0; index < targets.Count; index++)
        {
            var draft = drafts[index];

            if (draft is null)
            {
                results.Add(new ModProjectInfo { Item = targets[index] });
                continue;
            }

            var info = BuildInfo(targets[index], draft);
            results.Add(info);

            if (info.Identified)
            {
                matched++;
            }

            if (draft.Key is not null && !draft.FromCache)
            {
                cache[draft.Key] = new CacheEntry
                {
                    Source = info.Source?.ToString(),
                    ProjectId = info.ProjectId,
                    Slug = info.Slug,
                    Title = info.Title,
                    Description = info.Description,
                    Authors = info.Authors,
                    IconPath = info.IconPath,
                    WebUrl = info.WebUrl,
                    StoredAt = now,
                };
            }
        }

        SaveCache(cache);

        return new ModProjectLookupResult
        {
            Items = results,
            Total = targets.Count,
            Matched = matched,
        };
    }

    /// <summary>
    /// 该条目是否值得走在线补全。
    ///
    /// 模组始终参与：卡片标题、MC 百科译名与后续更新检查都靠它。
    /// 资源包 / 光影包 / 数据包只在「包里没有内嵌图标」时参与——它们的内容常常很大（资源包动辄几十 MB），
    /// 为了拿一张本来就在包里的 <c>pack.png</c> 去算两遍哈希、再发一轮网络请求并不划算。
    /// 目录形态（数据包）没有哈希可算，一律跳过。
    /// </summary>
    private static bool IsEnrichable(ContentItem item)
    {
        if (Array.IndexOf(EnrichableKinds, item.Kind) < 0 || !File.Exists(item.FilePath))
        {
            return false;
        }

        return item.Kind == ContentKind.Mod || string.IsNullOrWhiteSpace(item.IconPath);
    }

    // ---------------- 两个数据源的补全 ----------------

    private async Task FillFromModrinthAsync(
        IReadOnlyList<ModFileHashes?> hashes,
        Draft?[] drafts,
        IReadOnlyList<int> pending,
        CancellationToken cancellationToken)
    {
        var sha1ByIndex = new Dictionary<int, string>();

        foreach (var index in pending)
        {
            // 键统一小写：Modrinth 的哈希与 <see cref="ModFileHashes.Sha1"/> 的大小写约定相反。
            if (hashes[index]?.Sha1 is { Length: > 0 } sha1)
            {
                sha1ByIndex[index] = sha1.ToLowerInvariant();
            }
        }

        if (sha1ByIndex.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, ModrinthVersion> hits;

        try
        {
            hits = await _modrinth!
                .LookupBySha1BatchAsync(sha1ByIndex.Values.Distinct(StringComparer.Ordinal).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("Modrinth 批量哈希反查失败", ex);
            return;
        }

        if (hits.Count == 0)
        {
            return;
        }

        var projectIdByIndex = new Dictionary<int, string>();

        foreach (var (index, sha1) in sha1ByIndex)
        {
            if (hits.TryGetValue(sha1, out var version) && !string.IsNullOrWhiteSpace(version.ProjectId))
            {
                projectIdByIndex[index] = version.ProjectId;
            }
        }

        if (projectIdByIndex.Count == 0)
        {
            return;
        }

        IReadOnlyList<ModrinthProject> projects;

        try
        {
            projects = await _modrinth!
                .GetProjectsAsync(projectIdByIndex.Values.Distinct(StringComparer.Ordinal).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("Modrinth 批量项目信息获取失败", ex);
            return;
        }

        var byId = projects.ToDictionary(project => project.ProjectId, project => project, StringComparer.Ordinal);

        foreach (var (index, projectId) in projectIdByIndex)
        {
            if (!byId.TryGetValue(projectId, out var project))
            {
                continue;
            }

            drafts[index] = new Draft
            {
                Key = CacheKeyOf(hashes[index]),
                Source = ContentSourceKind.Modrinth,
                ProjectId = project.ProjectId,
                Slug = project.Slug,
                Title = project.Title,
                Description = CleanSummary(project.Description),
                Authors = project.Author,
                IconUrl = project.IconUrl,
                WebUrl = project.WebUrl,
            };
        }
    }

    private async Task FillFromCurseForgeAsync(
        IReadOnlyList<ContentItem> targets,
        IReadOnlyList<ModFileHashes?> hashes,
        Draft?[] drafts,
        IReadOnlyList<int> pending,
        CancellationToken cancellationToken)
    {
        var fingerprints = pending
            .Select(index => hashes[index]?.CurseForgeFingerprint ?? 0)
            .Where(fingerprint => fingerprint > 0)
            .Distinct()
            .ToList();

        IReadOnlyList<CurseForgeFingerprintMatch> matches;

        if (fingerprints.Count > 0)
        {
            try
            {
                matches = await _curseForge!
                    .LookupByFingerprintsAsync(fingerprints, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn("CurseForge 批量指纹反查失败", ex);
                return;
            }
        }
        else
        {
            matches = Array.Empty<CurseForgeFingerprintMatch>();
        }

        if (matches.Count == 0)
        {
            return;
        }

        var modIdByFingerprint = new Dictionary<uint, long>();
        var modIdByFileName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            if (match.ModId <= 0)
            {
                continue;
            }

            if (match.Fingerprint != 0)
            {
                modIdByFingerprint[match.Fingerprint] = match.ModId;
            }

            if (!string.IsNullOrWhiteSpace(match.FileName))
            {
                modIdByFileName[match.FileName] = match.ModId;
            }
        }

        var modIdByIndex = new Dictionary<int, long>();

        foreach (var index in pending)
        {
            var fingerprint = hashes[index]?.CurseForgeFingerprint ?? 0;

            if (fingerprint > 0 && modIdByFingerprint.TryGetValue(fingerprint, out var modId))
            {
                modIdByIndex[index] = modId;
            }
            else if (modIdByFileName.TryGetValue(targets[index].FileName, out var fallback))
            {
                // 精简镜像可能整批不带 fileFingerprint；文件名会被用户改掉，只作兜底。
                modIdByIndex[index] = fallback;
            }
        }

        if (modIdByIndex.Count == 0)
        {
            return;
        }

        IReadOnlyList<CurseForgeProject> projects;

        try
        {
            projects = await _curseForge!
                .GetProjectsAsync(modIdByIndex.Values.Distinct().ToList(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("CurseForge 批量项目信息获取失败", ex);
            return;
        }

        var byId = projects.ToDictionary(
            project => project.Id.ToString(CultureInfo.InvariantCulture),
            project => project,
            StringComparer.Ordinal);

        foreach (var (index, modId) in modIdByIndex)
        {
            if (!byId.TryGetValue(modId.ToString(CultureInfo.InvariantCulture), out var project))
            {
                continue;
            }

            drafts[index] = new Draft
            {
                Key = CacheKeyOf(hashes[index]),
                Source = ContentSourceKind.CurseForge,
                ProjectId = modId.ToString(CultureInfo.InvariantCulture),
                Slug = project.Slug,
                Title = project.Name,
                Description = CleanSummary(project.Summary),
                Authors = project.Authors,
                IconUrl = project.IconUrl,
                WebUrl = project.WebUrl,
            };
        }
    }

    // ---------------- 组装与缓存 ----------------

    /// <summary>一张中间态：识别到项目但还没查译名、也没确定图标落点的结果。</summary>
    private sealed class Draft
    {
        /// <summary>缓存键。哈希全失败时为 null，这条结果不进缓存。</summary>
        public string? Key { get; init; }

        /// <summary>
        /// 这条草稿是直接从缓存读出来的。
        ///
        /// 命中缓存的条目**不回写**：写回会把 <c>StoredAt</c> 刷成当前时间，
        /// 于是「6 小时后重新查」永远不成立——只要用户每 5 小时进一次页面，
        /// 这份缓存就永久有效，项目改名的信息再也拉不到。
        /// </summary>
        public bool FromCache { get; init; }

        public ContentSourceKind? Source { get; init; }

        public string? ProjectId { get; init; }

        public string? Slug { get; init; }

        public string? Title { get; init; }

        public string? Description { get; init; }

        public string? Authors { get; init; }

        public string? IconUrl { get; init; }

        public string? IconPath { get; set; }

        public string? WebUrl { get; init; }
    }

    private static ModProjectInfo BuildInfo(ContentItem item, Draft draft)
    {
        var chineseName = draft.Source switch
        {
            ContentSourceKind.Modrinth => ModNameTranslations.FindByModrinthSlug(draft.Slug),
            ContentSourceKind.CurseForge => ModNameTranslations.FindByCurseForgeSlug(draft.Slug),
            _ => null,
        };

        return new ModProjectInfo
        {
            Item = item,
            Source = draft.Source,
            ProjectId = draft.ProjectId,
            Slug = draft.Slug,
            Title = draft.Title,
            ChineseName = chineseName,
            Description = draft.Description,
            Authors = draft.Authors,
            IconPath = File.Exists(draft.IconPath) ? draft.IconPath : null,
            WebUrl = draft.WebUrl,
        };
    }

    private async Task<string?> CacheIconAsync(Draft draft, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(draft.IconUrl))
        {
            return null;
        }

        try
        {
            return draft.Source switch
            {
                ContentSourceKind.Modrinth when _modrinth is not null =>
                    await _modrinth.CacheIconAsync(draft.IconUrl, cancellationToken).ConfigureAwait(false),
                ContentSourceKind.CurseForge when _curseForge is not null =>
                    await _curseForge.CacheIconAsync(draft.IconUrl, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"模组图标缓存失败：{draft.IconUrl}", ex);
            return null;
        }
    }

    /// <summary>缓存键优先用 SHA1，读不出内容时退到 CurseForge 指纹；两者都没有就不缓存。</summary>
    private static string? CacheKeyOf(ModFileHashes? hashes)
    {
        if (hashes is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(hashes.Sha1))
        {
            return "sha1:" + hashes.Sha1.ToUpperInvariant();
        }

        return hashes.CurseForgeFingerprint > 0
            ? "cf:" + hashes.CurseForgeFingerprint.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>资源站摘要里的换行会让卡片描述撑成一段多行的乱码，统一压成单行空格。</summary>
    private static string? CleanSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var single = summary.ReplaceLineEndings(" ").Trim();

        while (single.Contains("  ", StringComparison.Ordinal))
        {
            single = single.Replace("  ", " ", StringComparison.Ordinal);
        }

        return single;
    }

    private static ContentSourceKind? ParseSource(string? source) => source switch
    {
        nameof(ContentSourceKind.Modrinth) => ContentSourceKind.Modrinth,
        nameof(ContentSourceKind.CurseForge) => ContentSourceKind.CurseForge,
        _ => null,
    };

    private Dictionary<string, CacheEntry> LoadCache()
    {
        var path = CachePath();

        if (path is null || !File.Exists(path))
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            }

            var file = JsonSerializer.Deserialize<CacheFile>(json);

            if (file is null || file.Version != CacheSchemaVersion || file.Entries is null)
            {
                _log?.Warn("模组项目缓存版本不匹配，已重置");
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            }

            return new Dictionary<string, CacheEntry>(file.Entries, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 缓存坏了只该让这一次多打几个请求，不该让模组页报错。
            _log?.Warn("读取模组项目缓存失败，已重置", ex);
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    private void SaveCache(Dictionary<string, CacheEntry> entries)
    {
        var path = CachePath();

        if (path is null)
        {
            return;
        }

        try
        {
            var trimmed = entries.Count > MaxCacheEntries
                ? entries
                    .OrderByDescending(pair => pair.Value.StoredAt)
                    .Take(MaxCacheEntries)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : entries;

            var json = JsonSerializer.Serialize(new CacheFile
            {
                Version = CacheSchemaVersion,
                Entries = trimmed,
            });

            lock (CacheWriteGate)
            {
                var directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Warn("写入模组项目缓存失败", ex);
        }
    }

    private string? CachePath() =>
        string.IsNullOrWhiteSpace(_paths?.CacheDirectory)
            ? null
            : Path.Combine(_paths!.CacheDirectory, CacheFileName);

    private sealed class CacheFile
    {
        public int Version { get; set; } = CacheSchemaVersion;

        public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CacheEntry
    {
        public string? Source { get; set; }

        public string? ProjectId { get; set; }

        public string? Slug { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public string? Authors { get; set; }

        public string? IconPath { get; set; }

        public string? WebUrl { get; set; }

        public DateTimeOffset StoredAt { get; set; }
    }
}
