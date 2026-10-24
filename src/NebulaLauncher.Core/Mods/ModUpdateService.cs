using System.Security.Cryptography;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// 本地模组的在线身份识别与更新检查。
///
/// 策略照抄 PCL2 的 <c>LocalResourceOnlineLoad</c>，但按「一个文件只查一次」的原则重排了顺序：
///
/// 1. 先算本地哈希（<see cref="ModFingerprint"/>，带磁盘缓存）；
/// 2. 逐个用 SHA1 反查 Modrinth——它的命中率更高，而且一次响应就带回完整版本对象；
/// 3. 只有 Modrinth 没收录、且 CurseForge 已配置时，才把剩余的指纹攒成一批做
///    <c>POST /v1/fingerprints</c>。这个接口按请求计费，逐文件调用会白白多花配额。
///
/// 为什么单个文件失败不整体失败：模组目录里混着用户手改过的 jar、旧版本、甚至不是 jar 的
/// 文件是常态，一条查不到不该让另外 80 条也没有结果。
///
/// 为什么整体网络失败要写 <see cref="ModUpdateReport.Error"/>：逐条都标「检查失败」时，
/// 用户会以为是自己装的模组有问题；给一条整体原因才能说明「是网络断了」。
/// </summary>
public sealed class ModUpdateService : IModUpdateService, IDisposable
{
    /// <summary>同时在飞的查询数。4 是「够快但不会把家庭宽带和机械硬盘一起打满」的经验值。</summary>
    public const int MaxParallelLookups = 4;

    /// <summary>已是最新版本。</summary>
    public const string StatusLatest = "已是最新";

    /// <summary>远程存在更高的发布号。</summary>
    public const string StatusUpdateAvailable = "有新版本";

    /// <summary>两个数据源都没收录这个文件。与「检查失败」严格区分，否则用户会以为已经是最新版。</summary>
    public const string StatusUnidentified = "未识别（未收录于在线数据源）";

    /// <summary>该条目的查询或读盘失败。</summary>
    public const string StatusFailed = "检查失败";

    /// <summary>认出了项目，但本地 jar 没声明版本号，无法判断新旧。</summary>
    public const string StatusIdentifiedOnly = "已识别（本地无版本号，未判更新）";

    private readonly IModrinthService? _modrinth;
    private readonly ICurseForgeService? _curseForge;
    private readonly IContentService? _content;
    private readonly ILauncherPaths? _paths;
    private readonly ModFingerprint _fingerprint;
    private readonly IAppLog? _log;
    private readonly HttpClient _http;

    /// <summary>
    /// 构造函数。所有依赖都允许缺失：页面在服务没接线时也要能渲染，
    /// 真正检查时再用 <see cref="CheckAsync"/> 返回的 <see cref="ModUpdateReport.Error"/> 说明原因。
    /// </summary>
    /// <param name="content">删除 / 回收站服务。缺失时 <see cref="UpdateAsync"/> 退回直接删旧文件。</param>
    /// <param name="handler">测试用的 HTTP 管道替身；为 null 时自建一个。</param>
    public ModUpdateService(
        IModrinthService? modrinth = null,
        ICurseForgeService? curseForge = null,
        ILauncherPaths? paths = null,
        IAppLog? log = null,
        IContentService? content = null,
        HttpMessageHandler? handler = null)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
        _content = content;
        _paths = paths;
        _log = log;
        _fingerprint = new ModFingerprint(paths?.CacheDirectory, log);
        _http = CreateClient(handler);
    }

    /// <summary>
    /// 不带 BMCLAPI 改写 handler 的裸客户端。模组直链由数据源给出（Modrinth CDN / forgecdn），
    /// 走镜像重写反而会把签名 URL 改坏——与 <see cref="ModrinthService"/> 的取舍一致。
    /// </summary>
    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        // 自建的 handler 由客户端负责释放；外部传入的（测试替身）不归我们管。
        var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("NebulaLauncher/1.0 (+https://github.com/nebula-launcher)");
        return client;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public Task<ModFileHashes> GetHashesAsync(string filePath, CancellationToken cancellationToken = default)
        => _fingerprint.GetHashesAsync(filePath, cancellationToken);

    /// <summary>真正读盘计算过哈希的文件数。测试用来验证「第二次检查命中缓存」。</summary>
    public int ComputedFileCount => _fingerprint.ComputedFileCount;

    /// <inheritdoc />
    public async Task<ModUpdateReport> CheckAsync(
        IReadOnlyList<ContentItem> items,
        string gameDirectory,
        string? minecraftVersion,
        LoaderKind loader,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (items is null || items.Count == 0)
        {
            return ModUpdateReport.Empty;
        }

        var resources = items.Where(item => item is not null).ToList();

        if (resources.Count == 0)
        {
            return ModUpdateReport.Empty;
        }

        var curseForgeReady = _curseForge is not null;

        if (_modrinth is null && !curseForgeReady)
        {
            // 服务没接线。这不是「模组未收录」，不能让界面显示「已是最新」。
            LastError = "在线数据源尚未就绪：Modrinth 与 CurseForge 服务都不可用。";
            var failed = resources.Select(item => Failure(item, LastError)).ToList();

            return new ModUpdateReport
            {
                Items = failed,
                Total = failed.Count,
                Error = LastError,
            };
        }

        var state = new RunState { Total = resources.Count };
        var results = new ModUpdateInfo?[resources.Count];
        var hashes = new ModFileHashes?[resources.Count];

        // ---------------- 阶段 1：本地哈希 ----------------
        // 纯本地 IO 也限并发：机械硬盘上同时读 16 个大 jar 会互相拖慢，反而更慢。
        await Parallel.ForEachAsync(
            Enumerable.Range(0, resources.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelLookups,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                var item = resources[index];
                var hash = await _fingerprint.GetHashesAsync(item.FilePath, gameDirectory, token).ConfigureAwait(false);
                hashes[index] = hash;

                if (!string.IsNullOrWhiteSpace(hash.Sha1))
                {
                    return;
                }

                // 读不出内容（被独占、权限不足、刚被删除）时，后面的网络查询也就无从谈起。
                results[index] = Failure(item, "无法读取文件内容：文件可能被占用、没有读取权限，或已经被删除。");
                state.AddCompleted(failed: true);
                ReportProgress(progress, state, item.FileName);
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // ---------------- 阶段 2：Modrinth SHA1 反查（逐文件） ----------------
        if (_modrinth is not null)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, resources.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelLookups,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    if (results[index] is not null || hashes[index]?.Sha1 is not { } sha1)
                    {
                        return;
                    }

                    var item = resources[index];
                    ModUpdateInfo? info = null;
                    string? error = null;

                    try
                    {
                        // Modrinth 官方约定哈希小写；<see cref="ModFileHashes.Sha1"/> 按模型约定存大写，
                        // 这里转换一次，避免大小写差异导致查不到。
                        var hit = await _modrinth
                            .LookupBySha1Async(sha1.ToLowerInvariant(), token)
                            .ConfigureAwait(false);

                        if (hit is null)
                        {
                            // 服务把网络异常吞进 LastError 后返回 null：为空才是「真的没收录」。
                            // 非空说明这次查询本身失败，必须显示「检查失败」而不是「未识别」，
                            // 否则断网时用户会以为自己的模组都没被收录。
                            //
                            // 已知局限：LastError 是服务实例上的共享字段，4 个并发查询会互相覆盖，
                            // 极端情况下可能把一条网络失败记到相邻条目上。错误信息本身仍然准确，
                            // 只是归属条目可能差一位；要彻底修需要给服务加返回错误码的重载。
                            var lookupError = _modrinth.LastError;

                            if (!string.IsNullOrWhiteSpace(lookupError))
                            {
                                error = lookupError;
                            }
                        }
                        else
                        {
                            // 命中的是「本地这个文件属于哪个版本」，还要再问一次「这个项目最新的版本是什么」。
                            // 按当前游戏版本 / 加载器过滤，避免把 1.21 的构建推给 1.20.1 的实例。
                            var versions = await _modrinth
                                .GetVersionsAsync(hit.ProjectId, minecraftVersion, loader, token)
                                .ConfigureAwait(false);

                            info = MatchedModrinth(item, hit, versions.Count > 0 ? versions[0] : null);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"Modrinth 反查失败：{item.FileName}", ex);
                        error = $"Modrinth 查询失败：{ex.Message}";
                    }

                    if (info is not null)
                    {
                        results[index] = info;
                        state.AddCompleted(failed: false);
                        ReportProgress(progress, state, item.FileName);
                    }
                    else if (error is not null)
                    {
                        results[index] = Failure(item, error);
                        state.AddCompleted(failed: true);
                        ReportProgress(progress, state, item.FileName);
                    }

                    // 命中为 null 且没报错 = 数据源没收录，留给阶段 3 或最终的「未识别」。
                }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
        }

        // ---------------- 阶段 3：CurseForge 指纹批量反查 ----------------
        var pending = Enumerable.Range(0, resources.Count)
            .Where(index => results[index] is null && hashes[index]?.CurseForgeFingerprint is > 0)
            .ToList();

        if (pending.Count > 0)
        {
            string? curseForgeError = null;

            if (curseForgeReady)
            {
                var fingerprints = pending
                    .Select(index => hashes[index]!.CurseForgeFingerprint)
                    .Distinct()
                    .ToList();

                IReadOnlyList<CurseForgeFingerprintMatch> matches = Array.Empty<CurseForgeFingerprintMatch>();

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
                    _log?.Warn("CurseForge 指纹反查失败", ex);
                    curseForgeError = $"CurseForge 查询失败：{ex.Message}";
                }

                // 按「请求指纹 → 匹配结果」关联。CF 返回的 FileName 是它自己的文件名，
                // 本地 jar 被用户改过名就对不上；两个不同文件同名时又会串成同一份结果。
                var requested = pending
                    .Select(index => (Fingerprint: hashes[index]!.CurseForgeFingerprint, FileName: resources[index].FileName))
                    .ToList();

                var associated = AssociateFingerprintMatches(requested, matches);

                for (var slot = 0; slot < pending.Count; slot++)
                {
                    var index = pending[slot];
                    var item = resources[index];
                    var match = associated[slot];

                    if (match is not null)
                    {
                        results[index] = MatchedCurseForge(item, match, minecraftVersion, loader);
                        state.AddCompleted(failed: false);
                    }
                    else if (curseForgeError is not null)
                    {
                        results[index] = Failure(item, curseForgeError);
                        state.AddCompleted(failed: true);
                    }

                    ReportProgress(progress, state, item.FileName);
                }
            }
        }

        // ---------------- 收尾 ----------------
        for (var index = 0; index < results.Length; index++)
        {
            if (results[index] is null)
            {
                results[index] = Unidentified(resources[index]);
                state.AddCompleted(failed: false);
            }
        }

        var list = results.Select(info => info!).ToList();
        var matched = list.Count(info => info.Matched);
        var updatable = list.Count(info => info.HasUpdate);
        var failedCount = list.Count(info => !string.IsNullOrWhiteSpace(info.Error));

        // 每一条都失败 = 在线数据源整体不可达。逐条错误对用户没有可操作性，
        // 给一条整体原因（含首次失败详情）才能让他去查网络或代理。
        string? reportError = null;

        if (failedCount == list.Count)
        {
            var first = list.FirstOrDefault(info => !string.IsNullOrWhiteSpace(info.Error))?.Error;
            reportError = string.IsNullOrWhiteSpace(first)
                ? "所有模组都没能完成在线查询，请检查网络或代理设置。"
                : $"所有模组都没能完成在线查询：{first}";
            _log?.Warn(reportError);
        }

        LastError = reportError;

        ReportProgress(progress, state, null);

        return new ModUpdateReport
        {
            Items = list,
            Total = list.Count,
            Matched = matched,
            Updatable = updatable,
            Error = reportError,
        };
    }

    // ---------------- 批量更新（下载 → 校验 → 替换） ----------------

    /// <inheritdoc />
    public async Task<ModUpdateBatchResult> UpdateAsync(
        IReadOnlyList<ModUpdateInfo> items,
        string gameDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (items is null || items.Count == 0)
        {
            return new ModUpdateBatchResult { Error = "没有需要更新的条目。" };
        }

        var asked = items.Where(info => info is not null).ToList();

        // 只有「有新版本 + 有下载地址」才能动。其余整条跳过并计数：
        // 静默忽略会让「点了更新却没反应」看起来像卡死。
        var candidates = asked
            .Where(info => info.HasUpdate)
            .Where(info => !string.IsNullOrWhiteSpace(info.DownloadUrl) && !string.IsNullOrWhiteSpace(info.FileName))
            .ToList();

        var skipped = asked.Count - candidates.Count;

        if (candidates.Count == 0)
        {
            var error = asked.All(info => !info.HasUpdate)
                ? "所选条目都没有新版本。"
                : "待更新条目缺少下载地址，请重新检查更新后再试。";

            LastError = error;
            return new ModUpdateBatchResult { Skipped = skipped, Error = error };
        }

        var meter = new UpdateMeter
        {
            Total = candidates.Count,
            TotalBytes = candidates.Sum(info => info.FileSize),
        };

        var failedFiles = new List<string>();
        var failures = new List<string>();
        var updated = 0;

        foreach (var info in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var displayName = DisplayNameOf(info);
            meter.CurrentBytes = 0;

            try
            {
                await UpdateOneAsync(info, gameDirectory, meter, progress, cancellationToken).ConfigureAwait(false);
                updated++;
                meter.CompletedBytes += info.FileSize > 0 ? info.FileSize : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Error($"更新模组失败：{displayName}", ex);
                failedFiles.Add(displayName);
                failures.Add($"{displayName}：{ex.Message}");
                meter.Failed++;
            }
            finally
            {
                meter.Completed++;
                meter.CurrentBytes = 0;
                ReportUpdateProgress(progress, meter, displayName);
            }
        }

        LastError = failures.Count == 0 ? null : $"有 {failures.Count} 个模组未能更新。";

        return new ModUpdateBatchResult
        {
            Updated = updated,
            Failed = failedFiles.Count,
            Skipped = skipped,
            FailedFiles = failedFiles,
            Failures = failures,
            Error = failures.Count == 0 ? null : LastError,
        };
    }

    /// <summary>
    /// 单个条目的更新：下载到缓存 → 校验 → 旧文件进回收站 → 新文件落到旧位置。
    ///
    /// 顺序不能颠倒：「先放新文件再删旧文件」会在两份同名 jar 上撞车，
    /// 「先删旧文件再确认下载成功」则可能两头空。这里先下完再动旧文件，且旧文件进回收站可找回。
    /// </summary>
    private async Task UpdateOneAsync(
        ModUpdateInfo info,
        string gameDirectory,
        UpdateMeter meter,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var currentEnabledPath = ContentService.EnabledPathOf(info.Item.FilePath);
        var currentName = Path.GetFileName(currentEnabledPath);

        if (string.IsNullOrWhiteSpace(currentName))
        {
            currentName = info.Item.FileName;
        }

        var targetName = AlignFileName(currentName, info.FileName!);
        var directory = Path.GetDirectoryName(currentEnabledPath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(gameDirectory, ContentService.DirectoryNameFor(info.Item.Kind));
        }

        Directory.CreateDirectory(directory);

        var targetEnabledPath = Path.Combine(directory, targetName);
        var targetPath = info.Item.IsEnabled ? targetEnabledPath : DisabledPathFor(targetEnabledPath, info.Item);

        var tempDirectory = Path.Combine(_paths?.CacheDirectory ?? Path.GetTempPath(), "mod-update");
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".part");

        try
        {
            await DownloadAsync(info.DownloadUrl!, tempPath, meter, progress, cancellationToken).ConfigureAwait(false);
            await VerifyAsync(info, tempPath, cancellationToken).ConfigureAwait(false);

            // 旧文件连同它的禁用变体一起进回收站：留下半份会变成「重复 modid」。
            await RemoveOldAsync(info, cancellationToken).ConfigureAwait(false);

            // 目标名可能刚好等于旧名（段完全重合），此时原地已空；被别人占用时改名落盘。
            var finalPath = File.Exists(targetPath) || Directory.Exists(targetPath)
                ? ResolveFreePath(targetPath)
                : targetPath;

            File.Move(tempPath, finalPath, overwrite: false);
            _log?.Info($"模组已更新：{currentName} → {Path.GetFileName(finalPath)}");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>删除旧文件。优先走 <see cref="IContentService.DeleteAsync"/>（进回收站）。</summary>
    private async Task RemoveOldAsync(ModUpdateInfo info, CancellationToken cancellationToken)
    {
        if (_content is not null)
        {
            await _content.DeleteAsync(info.Item, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 没有内容服务时只能直删：更新完至少不会留下新旧两份。
        foreach (var path in new[] { info.Item.FilePath, ContentService.EnabledPathOf(info.Item.FilePath) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>禁用态目标路径：沿用原条目的后缀风格，没有历史时用 <c>.disabled</c>。</summary>
    private static string DisabledPathFor(string enabledPath, ContentItem item)
    {
        var original = item.FilePath;

        if (original.EndsWith(ContentBatch.LegacyDisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return enabledPath + ContentBatch.LegacyDisabledSuffix;
        }

        return enabledPath + ContentBatch.DisabledSuffix;
    }

    private async Task DownloadAsync(
        string url,
        string path,
        UpdateMeter meter,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        var buffer = new byte[81920];
        var currentName = Path.GetFileName(path);

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            meter.CurrentBytes += read;

            // 每 256 KiB 报一次：逐字节通知会把 UI 线程淹掉。
            if (meter.CurrentBytes % (256 * 1024) < read)
            {
                ReportUpdateProgress(progress, meter, currentName);
            }
        }
    }

    /// <summary>
    /// 校验下载到的文件。
    ///
    /// 两道判据：
    /// 1. 字节数必须与数据源声明的一致（两者都有值时）——它能挡住「下到 404 页面」这类错误；
    /// 2. Modrinth 来源再用下载文件的 SHA1 反查一次，确认它确实属于同一个项目。
    ///    反查失败（断网 / 该文件未收录）不阻断：此时第 1 条已经成立，而且旧文件在回收站里可找回。
    /// </summary>
    private async Task VerifyAsync(ModUpdateInfo info, string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;

        if (info.FileSize > 0 && length != info.FileSize)
        {
            throw new InvalidOperationException($"下载文件大小不符（{length} / 期望 {info.FileSize} 字节）");
        }

        if (_modrinth is null
            || info.Source != ContentSourceKind.Modrinth
            || string.IsNullOrWhiteSpace(info.ProjectId))
        {
            return;
        }

        string sha1;

        // 走 IncrementalHash 而不是 SHA1.HashData：Modrinth 的反查键就是 SHA1，
        // 这不是可替换的加密方案（与 ModFingerprint 同一套取舍，CA5350 只在那里说明一次）。
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            var buffer = new byte[81920];

            await using (var stream = File.OpenRead(path))
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    hasher.AppendData(buffer, 0, read);
                }
            }

            sha1 = Convert.ToHexString(hasher.GetHashAndReset());
        }

        var hit = await _modrinth.LookupBySha1Async(sha1.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);

        if (hit is null)
        {
            _log?.Warn($"更新文件的 SHA1 未能反查（接受大小校验结果）：{Path.GetFileName(path)}");
            return;
        }

        if (!string.Equals(hit.ProjectId, info.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("下载文件属于另一个项目，已丢弃");
        }
    }

    private static void ReportUpdateProgress(IProgress<DownloadProgress>? progress, UpdateMeter meter, string? currentFile)
    {
        if (progress is null)
        {
            return;
        }

        var downloaded = meter.CompletedBytes + meter.CurrentBytes;
        var totalBytes = meter.TotalBytes > 0 ? meter.TotalBytes : meter.Total;

        progress.Report(new DownloadProgress(
            TotalFiles: meter.Total,
            CompletedFiles: meter.Completed,
            FailedFiles: meter.Failed,
            TotalBytes: totalBytes,
            DownloadedBytes: downloaded,
            SpeedBytesPerSecond: 0,
            CurrentFile: currentFile,
            Ratio: totalBytes > 0 ? Math.Clamp((double)downloaded / totalBytes, 0, 1) : 0));
    }

    private static string DisplayNameOf(ModUpdateInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Item.FileName))
        {
            return info.Item.FileName;
        }

        var name = Path.GetFileName(info.Item.FilePath);
        return string.IsNullOrWhiteSpace(name) ? info.FileName ?? "未知文件" : name;
    }

    private static string ResolveFreePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"清理更新临时文件失败：{path}", ex);
        }
    }

    /// <summary>
    /// 由「本地文件名」与「数据源给的新文件名」推出替换后的文件名。
    ///
    /// 抄 PCL2 的 <c>PageInstanceMod.xaml.vb:556-581</c>：两边都按 <c>-</c> 切段，
    /// 先去掉相同的**前缀**段、再去掉相同的**后缀**段，只把中间变化的那一段换掉。
    /// 这样 <c>jei-1.20.1-15.2.0.27.jar</c> 更新成 <c>jei-1.20.1-15.2.0.30.jar</c> 时
    /// 不会变成 <c>jei-15.2.0.30.jar</c> 或 <c>jei-1.20.1-15.2.0.30-1.20.1.jar</c>。
    ///
    /// 为什么必须做这一步：新文件名若直接采用远程名，<c>mods/</c> 里会同时存在新旧两份，
    /// 加载器报「duplicate mod」直接崩游戏——这是更新功能最容易踩的坑。
    /// 扩展名沿用本地的（除非远程换了扩展名，例如 .litemod）。
    /// </summary>
    public static string AlignFileName(string currentFileName, string remoteFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFileName);

        var currentExtension = Path.GetExtension(currentFileName);
        var remoteExtension = Path.GetExtension(remoteFileName);

        var extension = string.IsNullOrEmpty(currentExtension)
            || !currentExtension.Equals(remoteExtension, StringComparison.OrdinalIgnoreCase)
            ? remoteExtension
            : currentExtension;

        var currentStem = Path.GetFileNameWithoutExtension(currentFileName);
        var remoteStem = Path.GetFileNameWithoutExtension(remoteFileName);

        if (string.Equals(currentStem, remoteStem, StringComparison.Ordinal)
            && string.Equals(currentExtension, remoteExtension, StringComparison.OrdinalIgnoreCase))
        {
            return currentFileName;
        }

        var currentSegments = currentStem.Split('-');
        var remoteSegments = remoteStem.Split('-');

        var head = 0;
        while (head < currentSegments.Length
               && head < remoteSegments.Length
               && string.Equals(currentSegments[head], remoteSegments[head], StringComparison.Ordinal))
        {
            head++;
        }

        var tail = 0;
        while (tail < currentSegments.Length - head
               && tail < remoteSegments.Length - head
               && string.Equals(
                   currentSegments[currentSegments.Length - 1 - tail],
                   remoteSegments[remoteSegments.Length - 1 - tail],
                   StringComparison.Ordinal))
        {
            tail++;
        }

        if (head == 0 && tail == 0)
        {
            // 完全没有可对齐的段：只能整体采用远程名（保留本地扩展名）。
            return remoteStem + extension;
        }

        var keptTailStart = currentSegments.Length - tail;
        var currentMiddle = currentSegments[head..keptTailStart];
        var remoteMiddle = remoteSegments[head..(remoteSegments.Length - tail)];

        if (currentMiddle.Length == 0 || remoteMiddle.Length == 0)
        {
            // 段完全相同（版本号没变）：文件名保持不变，避免生成一个只差扩展名的副本。
            return currentFileName;
        }

        var merged = currentSegments[..head]
            .Concat(remoteMiddle)
            .Concat(currentSegments[keptTailStart..]);

        return string.Join("-", merged) + extension;
    }

    /// <summary>一次批量更新的共享计数器（进度与 <see cref="Interlocked"/> 无关，串行执行）。</summary>
    private sealed class UpdateMeter
    {
        public int Total;

        public int Completed;

        public int Failed;

        public long TotalBytes;

        public long CompletedBytes;

        public long CurrentBytes;
    }

    // ---------------- 结果组装 ----------------

    /// <summary>
    /// 把 CurseForge 的指纹反查结果关联回本地请求。
    ///
    /// 主键是被命中的指纹：CF 的 <see cref="CurseForgeFingerprintMatch.FileName"/> 是它自己的文件名，
    /// 本地文件被改名就再也对不上，两个不同文件同名时又会串成同一份结果。
    /// 只有整批响应都不带指纹（旧格式 / 精简镜像 / 调用方替身）时才退回按文件名关联——
    /// 那份兼容逻辑会重现同名串号问题，所以必须整体缺指纹才启用。
    /// </summary>
    /// <param name="requested">本地待查文件：本地指纹 + 本地文件名（文件名只用于兼容兜底）。</param>
    /// <param name="matches">反查结果，顺序不限。</param>
    /// <returns>与 <paramref name="requested"/> 等长的数组；同一位置是对应的命中，未命中为 null。</returns>
    public static IReadOnlyList<CurseForgeFingerprintMatch?> AssociateFingerprintMatches(
        IReadOnlyList<(uint Fingerprint, string FileName)> requested,
        IReadOnlyList<CurseForgeFingerprintMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(matches);

        var associated = new CurseForgeFingerprintMatch?[requested.Count];

        if (requested.Count == 0 || matches.Count == 0)
        {
            return associated;
        }

        var byFingerprint = new Dictionary<uint, CurseForgeFingerprintMatch>();

        foreach (var match in matches)
        {
            // 0 既是「本地没算出指纹」也是「响应没带指纹」，不能当键：否则所有无指纹的请求互相串。
            if (match is null || match.Fingerprint == 0)
            {
                continue;
            }

            byFingerprint.TryAdd(match.Fingerprint, match);
        }

        Dictionary<string, CurseForgeFingerprintMatch>? byFileName = null;

        if (byFingerprint.Count == 0)
        {
            byFileName = new Dictionary<string, CurseForgeFingerprintMatch>(StringComparer.OrdinalIgnoreCase);

            foreach (var match in matches)
            {
                if (match is not null && !string.IsNullOrWhiteSpace(match.FileName))
                {
                    byFileName.TryAdd(match.FileName, match);
                }
            }
        }

        for (var index = 0; index < requested.Count; index++)
        {
            var (fingerprint, fileName) = requested[index];

            if (fingerprint > 0 && byFingerprint.TryGetValue(fingerprint, out var match))
            {
                associated[index] = match;
            }
            else if (byFileName is not null
                     && !string.IsNullOrWhiteSpace(fileName)
                     && byFileName.TryGetValue(fileName, out var fallback))
            {
                associated[index] = fallback;
            }
        }

        return associated;
    }

    /// <summary>
    /// 组装一条 Modrinth 命中的结果。
    private static ModUpdateInfo MatchedModrinth(ContentItem item, ModrinthVersion hit, ModrinthVersion? latest)
    {
        var latestVersion = latest ?? hit;
        var hasLocalVersion = !string.IsNullOrWhiteSpace(item.Version);
        var hasUpdate = hasLocalVersion && !VersionsEquivalent(latestVersion.VersionNumber, item.Version);

        var status = !hasLocalVersion
            ? StatusIdentifiedOnly
            : hasUpdate ? StatusUpdateAvailable : StatusLatest;

        return new ModUpdateInfo
        {
            Item = item,
            Matched = true,
            Source = ContentSourceKind.Modrinth,
            ProjectId = hit.ProjectId,
            ProjectTitle = null,
            CurrentVersion = hasLocalVersion ? item.Version : hit.VersionNumber,
            LatestVersion = latestVersion.VersionNumber,
            DownloadUrl = latestVersion.DownloadUrl,
            FileName = latestVersion.FileName,
            FileSize = latestVersion.FileSize,
            Changelog = latestVersion.Changelog,
            HasUpdate = hasUpdate,
            Status = status,
        };
    }

    /// <summary>
    /// 组装一条 CurseForge 命中的结果。
    ///
    /// CurseForge 的文件对象没有独立的版本号字段，版本只存在于文件名 / 显示名里
    /// （例如 <c>jei-1.20.1-15.2.0.27.jar</c>），所以「发布号」用 <see cref="CurseForgeFile.Label"/>，
    /// 比较时由 <see cref="VersionsEquivalent"/> 提取数字段，避免把游戏版本号当成模组版本号。
    /// </summary>
    private static ModUpdateInfo MatchedCurseForge(
        ContentItem item,
        CurseForgeFingerprintMatch match,
        string? minecraftVersion,
        LoaderKind loader)
    {
        var latest = PickLatestFile(match.LatestFiles, minecraftVersion, loader);
        var latestLabel = latest?.Label;
        var hasLocalVersion = !string.IsNullOrWhiteSpace(item.Version);

        var hasUpdate = hasLocalVersion
                        && latest is not null
                        && !VersionsEquivalent(latest.FileName, item.Version)
                        && !VersionsEquivalent(latest.DisplayName, item.Version);

        var status = !hasLocalVersion
            ? StatusIdentifiedOnly
            : hasUpdate ? StatusUpdateAvailable : StatusLatest;

        return new ModUpdateInfo
        {
            Item = item,
            Matched = true,
            Source = ContentSourceKind.CurseForge,
            ProjectId = match.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProjectTitle = null,
            CurrentVersion = hasLocalVersion ? item.Version : match.FileName,
            LatestVersion = latestLabel,
            DownloadUrl = latest?.DownloadUrl,
            FileName = latest?.FileName,
            FileSize = latest?.FileLength ?? 0,
            Changelog = null,
            HasUpdate = hasUpdate,
            Status = status,
        };
    }

    /// <summary>
    /// 从 <c>latestFiles[]</c> 里挑目标游戏版本 + 加载器可用的最新文件。
    /// 过滤后为空时退回未过滤的列表——宁可给用户一个「可能不适用」的版本提示，
    /// 也不要因为 CurseForge 的标签命名（例如 <c>Forge</c> 与 <c>forge</c>）把结果吞掉。
    /// </summary>
    private static CurseForgeFile? PickLatestFile(
        IReadOnlyList<CurseForgeFile> files,
        string? minecraftVersion,
        LoaderKind loader)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var candidates = files;

        if (!string.IsNullOrWhiteSpace(minecraftVersion))
        {
            var byVersion = candidates
                .Where(file => file.GameVersions.Contains(minecraftVersion.Trim(), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (byVersion.Count > 0)
            {
                candidates = byVersion;
            }
        }

        if (loader != LoaderKind.Vanilla)
        {
            var byLoader = candidates
                .Where(file => file.GameVersions.Any(version => LoaderMatches(version, loader)))
                .ToList();

            if (byLoader.Count > 0)
            {
                candidates = byLoader;
            }
        }

        return candidates
            .OrderByDescending(file => file.FileDate ?? DateTimeOffset.MinValue)
            .ThenByDescending(file => file.Id)
            .FirstOrDefault();
    }

    private static bool LoaderMatches(string tag, LoaderKind loader)
    {
        // CurseForge 用游戏版本数组同时承载 Minecraft 版本与加载器标签（"Fabric" / "Forge" / "NeoForge"）。
        // 与 Modrinth 的 loaders 取值对齐（小写 slug），大小写不敏感比较。
        var slug = ModrinthService.LoaderCategoryOf(loader);

        return slug is not null && string.Equals(tag, slug, StringComparison.OrdinalIgnoreCase);
    }

    private static ModUpdateInfo Unidentified(ContentItem item) => new()
    {
        Item = item,
        Matched = false,
        Status = StatusUnidentified,
    };

    private static ModUpdateInfo Failure(ContentItem item, string error) => new()
    {
        Item = item,
        Matched = false,
        Status = StatusFailed,
        Error = error,
    };

    private static void ReportProgress(IProgress<DownloadProgress>? progress, RunState state, string? currentFile)
    {
        if (progress is null)
        {
            return;
        }

        var completed = Volatile.Read(ref state.Completed);
        var failed = Volatile.Read(ref state.Failed);
        var total = state.Total;

        progress.Report(new DownloadProgress(
            TotalFiles: total,
            CompletedFiles: completed,
            FailedFiles: failed,
            TotalBytes: total,
            DownloadedBytes: completed,
            SpeedBytesPerSecond: 0,
            CurrentFile: currentFile,
            Ratio: total > 0 ? Math.Clamp((double)completed / total, 0, 1) : 0));
    }

    // ---------------- 版本比较 ----------------

    /// <summary>
    /// 判断「远程发布号」与「本地版本」是否指向同一个版本。
    ///
    /// 不能直接字符串比较：远程发布号常带前后缀（<c>mc1.20.1-0.5.13-fabric</c>、
    /// <c>jei-1.20.1-15.2.0.27.jar</c>），本地 jar 里的 <c>version</c> 字段却是
    /// <c>0.5.13</c> / <c>0.5.13+mc1.20.1</c>。直接 <c>!=</c> 会把每个模组都判成「有新版本」。
    ///
    /// 做法：抽出数字段，任一方的段序列完整出现在另一方里就算同一版本。
    /// 为什么要求段数 ≥ 2：<c>"2"</c> 这种单段版本会命中任何带 2 的发布号；
    /// 两段以上（<c>0.5.13</c> → <c>[0,5,13]</c>）才足以区分版本。
    /// 接受的代价：本地 <c>1.0</c> 与远程 <c>1.0.1</c> 会被判成同一个版本（少报一次更新），
    /// 这在模组版本号里几乎不出现，因为两段式版本号本身就不是发布号的常见形态。
    /// </summary>
    public static bool VersionsEquivalent(string? remote, string? local)
    {
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local))
        {
            return false;
        }

        var remoteText = remote.Trim();
        var localText = local.Trim();

        if (string.Equals(remoteText, localText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var remoteParts = NumberParts(remoteText);
        var localParts = NumberParts(localText);

        if (remoteParts.Count == 0 || localParts.Count == 0)
        {
            return false;
        }

        if (remoteParts.Count == localParts.Count)
        {
            return remoteParts.SequenceEqual(localParts, StringComparer.Ordinal);
        }

        return remoteParts.Count > localParts.Count
            ? localParts.Count >= 2 && ContainsSequence(remoteParts, localParts)
            : remoteParts.Count >= 2 && ContainsSequence(localParts, remoteParts);
    }

    /// <summary>提取版本号里的数字段，去掉前导零（<c>01</c> 与 <c>1</c> 是同一段）。</summary>
    private static List<string> NumberParts(string value)
    {
        var parts = new List<string>();
        var start = -1;

        for (var index = 0; index <= value.Length; index++)
        {
            var isDigit = index < value.Length && value[index] is >= '0' and <= '9';

            if (isDigit)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start < 0)
            {
                continue;
            }

            var part = value[start..index].TrimStart('0');
            parts.Add(part.Length > 0 ? part : "0");
            start = -1;
        }

        return parts;
    }

    private static bool ContainsSequence(List<string> longer, List<string> shorter)
    {
        for (var start = 0; start + shorter.Count <= longer.Count; start++)
        {
            var matched = true;

            for (var offset = 0; offset < shorter.Count; offset++)
            {
                if (string.Equals(longer[start + offset], shorter[offset], StringComparison.Ordinal))
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 一次检查的共享计数器。用类字段而不是闭包局部变量，是因为
    /// <see cref="Interlocked.Increment(ref int)"/> 不能对闭包捕获的局部变量取 <c>ref</c>。
    /// </summary>
    private sealed class RunState
    {
        public int Total { get; init; }

        public int Completed;

        public int Failed;

        public void AddCompleted(bool failed)
        {
            Interlocked.Increment(ref Completed);

            if (failed)
            {
                Interlocked.Increment(ref Failed);
            }
        }
    }
}
