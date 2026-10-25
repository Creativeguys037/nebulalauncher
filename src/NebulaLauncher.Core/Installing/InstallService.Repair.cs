using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 版本校验与修复。
///
/// 两档的分工来自「代价」与「覆盖面」的取舍：
/// - Quick 只做本地能零成本判定的部分：0 字节客户端 jar，剩下交给 CmlLib 安装流程补缺失文件；
/// - Deep 自己解析版本 JSON（含 <c>inheritsFrom</c> 链），逐文件比对 SHA1，
///   能治「文件在但内容坏了」——CmlLib 的判定粒度是「文件存在即跳过」，发现不了这种损坏。
/// </summary>
public sealed partial class InstallService
{
    private const string LegacySignatureDsa = "META-INF/MOJANG_C.DSA";
    private const string LegacySignatureSf = "META-INF/MOJANG_C.SF";

    private const string LibraryBaseUrl = "https://libraries.minecraft.net/";
    private const string AssetObjectBaseUrl = "https://resources.download.minecraft.net/";

    /// <summary>下载分批大小。Deep 档的 assets 有数千项，一次性建任务会把峰值内存拉高。</summary>
    private const int DownloadBatchSize = 256;

    /// <summary>
    /// 按档位修复一个版本。
    ///
    /// 除取消除外不抛异常：修复面对的是「已经坏掉的版本」，把异常抛给 UI 只会让用户无从下手；
    /// 单文件失败一律进 <see cref="RepairReport.Failed"/>，整体继续。
    /// </summary>
    public async Task<RepairReport> RepairAsync(
        string versionName,
        RepairDepth depth,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            var empty = new RepairState();
            empty.Failed.Add("版本名为空，无法修复。");
            return empty.ToReport();
        }

        var name = versionName.Trim();
        var state = new RepairState();

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInstallEnvironment();

            var versionDirectory = Path.Combine(_paths.VersionsDirectory, name);
            Directory.CreateDirectory(versionDirectory);

            var reporter = new RepairProgress(progress);

            // 版本描述必须在：两档都依赖它，而且它们是同一个事实的两种失败形状——
            // Quick 档会把它交给 CmlLib 的 InstallAsync，库只抛 KeyNotFoundException:
            // Cannot find <name>；Deep 档由 ReadVersionChainAsync 抛 FileNotFoundException，
            // 冒泡成「修复中断：…」。同一件事两套文案，用户都看不出下一步做什么。
            // 所以判定放在分档之前：一个入口、一份说明，两档行为一致。
            if (!VersionDescriptionExists(name))
            {
                _log?.Warn($"修复 {name}：缺少版本描述文件，无法修复");
                state.Failed.Add(DescribeMissingVersionDescription(name));
            }
            else if (depth == RepairDepth.Deep)
            {
                await RepairDeepAsync(name, state, reporter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RepairQuickAsync(name, state, reporter, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log?.Info($"修复已取消：{name}（{depth}）");
            throw;
        }
        catch (Exception ex)
        {
            _log?.Error($"修复 {name} 失败（{depth}）", ex);
            state.Failed.Add($"修复中断：{ex.GetBaseException().Message}");
        }
        finally
        {
            _installGate.Release();
        }

        var report = state.ToReport();
        _log?.Info(
            $"修复结束：{name}（{depth}）缺失 {report.Missing} / 不匹配 {report.Mismatched} / 重下 {report.Redownloaded} / 失败 {report.Failed.Count}");

        return report;
    }

    // ---------------- 两档实现 ----------------

    private async Task RepairQuickAsync(
        string versionName,
        RepairState state,
        RepairProgress reporter,
        CancellationToken cancellationToken)
    {
        // Quick 也要解析版本链：0 字节的客户端 jar 属于父版本（加载器版本自身没有 client 下载），
        // 不沿 inheritsFrom 找就只知道「自家目录里那个 jar」。
        var chain = await TryReadVersionChainAsync(versionName, cancellationToken).ConfigureAwait(false);

        if (chain is not null)
        {
            var targets = new List<RepairTarget>(2);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Quick 只用长度判定：sha1 要读完整个文件，那是 Deep 档的代价。
            CollectClientJarTargets(chain, state, targets, seen, verifySha1: false);
            await DownloadTargetsAsync(versionName, targets, state, reporter, cancellationToken).ConfigureAwait(false);
        }

        // 其余缺失文件交给现有安装流程补齐。这一步会走网络，失败只影响「补齐」，
        // 不该把已经完成的本地修复判成整体失败。
        //
        // 补齐走的是同一个安装器入口（NebulaGameInstaller），所以给它开一个外部批次：
        // 修复时也能在下载页的任务面板看到逐个文件的状态。它不会和上面
        // DownloadTargetsAsync 的普通批次撞车——两者在同一方法内先后执行；
        // 与安装互斥由 RepairAsync/InstallAsync 共用的 _installGate 保证。
        //
        // 版本描述是否存在的判定在 RepairAsync 的入口处（两档共用），到这里一定成立。
        IDownloadTaskScope? repairScope = null;
        try
        {
            repairScope = TryBeginTaskScope($"修复 {versionName}");
            var launcher = CreateLauncher(repairScope);
            var adapter = new InstallProgressAdapter(versionName, reporter.InnerProgress);
            await launcher.InstallAsync(versionName, adapter.FileProgress, adapter.ByteProgress, cancellationToken)
                .ConfigureAwait(false);
            adapter.Complete(versionName);
            repairScope?.Complete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CompleteQuietly(repairScope);
            _log?.Warn($"修复 {versionName}：安装流程未能补齐缺失文件", ex);
            state.Failed.Add($"安装流程未能补齐缺失文件：{ex.GetBaseException().Message}");
        }
        finally
        {
            repairScope?.Dispose();
        }

        if (chain is not null)
        {
            CleanLegacyForgeSignatures(chain, state, cancellationToken);
        }
    }

    private async Task RepairDeepAsync(
        string versionName,
        RepairState state,
        RepairProgress reporter,
        CancellationToken cancellationToken)
    {
        var chain = await ReadVersionChainAsync(versionName, cancellationToken).ConfigureAwait(false);

        // 资源索引要先就位才能校验 objects。索引本身只判「能解析」：
        // Mojang 会回改 assetIndex 的 sha1，按它判定会无限重下（见 EXTENSIONS 条目 6）。
        var assetObjects = await EnsureAssetIndexAsync(chain, state, reporter, cancellationToken).ConfigureAwait(false);

        var targets = new List<RepairTarget>(Math.Max(16, chain.Libraries.Count + (assetObjects?.Count ?? 0)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectClientJarTargets(chain, state, targets, seen, verifySha1: true);

        reporter.BeginScan(chain.Libraries.Count + (assetObjects?.Count ?? 0));

        foreach (var library in chain.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var check = CheckFile(library.RelativePath, library.Sha1, verifySha1: true);
            AddCheckedTarget(
                check,
                state,
                targets,
                seen,
                library.RelativePath,
                library.Url,
                library.Sha1,
                library.Size);

            reporter.ReportScan(library.RelativePath);
        }

        if (assetObjects is not null)
        {
            foreach (var asset in assetObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var check = CheckFile(asset.RelativePath, asset.Hash, verifySha1: true);
                AddCheckedTarget(
                    check,
                    state,
                    targets,
                    seen,
                    asset.RelativePath,
                    AssetObjectBaseUrl + asset.Hash[..2] + "/" + asset.Hash,
                    asset.Hash,
                    asset.Size);

                reporter.ReportScan(asset.RelativePath);
            }
        }

        // 签名清理会重写 jar 的字节流，放下载之后：下载会覆盖 jar，若先删签名再下载，
        // 刚清掉的签名会被写回来。
        await DownloadTargetsAsync(versionName, targets, state, reporter, cancellationToken).ConfigureAwait(false);

        CleanLegacyForgeSignatures(chain, state, cancellationToken);
    }

    private void CollectClientJarTargets(
        VersionChain chain,
        RepairState state,
        List<RepairTarget> targets,
        HashSet<string> seen,
        bool verifySha1)
    {
        var client = chain.Client;
        if (client is null)
        {
            return;
        }

        var providerJar = Path.Combine(_paths.VersionsDirectory, client.ProviderId, client.ProviderId + ".jar");
        var providerRelative = RelativePathOf(providerJar);

        // 版本自身目录里的 <id>.jar：Forge 会把原版 jar 合并进这个文件，
        // 0 字节是「能列出、能点启动、进主菜单前崩掉」的最常见形态。
        var ownJar = Path.Combine(_paths.VersionsDirectory, chain.Id, chain.Id + ".jar");
        var ownRelative = RelativePathOf(ownJar);

        if (!string.Equals(ownRelative, providerRelative, StringComparison.OrdinalIgnoreCase) &&
            SafeLength(ownJar) == 0)
        {
            if (seen.Add(ownRelative))
            {
                state.Missing++;
                targets.Add(new RepairTarget(ownRelative, client.Url, client.Sha1, client.Size));
            }
        }

        if (SafeLength(providerJar) == 0)
        {
            // 缺失与 0 字节都算「没有被有效内容」，靠 SafeLength 一次覆盖：不存在返回 0。
            if (seen.Add(providerRelative))
            {
                state.Missing++;
                targets.Add(new RepairTarget(providerRelative, client.Url, client.Sha1, client.Size));
            }

            return;
        }

        if (verifySha1 && client.Sha1 is { Length: > 0 } expected && !Sha1Matches(providerJar, expected) && seen.Add(providerRelative))
        {
            state.Mismatched++;
            targets.Add(new RepairTarget(providerRelative, client.Url, client.Sha1, client.Size));
        }
    }

    private static void AddCheckedTarget(
        FileCheck check,
        RepairState state,
        List<RepairTarget> targets,
        HashSet<string> seen,
        string relativePath,
        string url,
        string? sha1,
        long size)
    {
        if (check == FileCheck.Present || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!seen.Add(relativePath))
        {
            return;
        }

        if (check == FileCheck.Missing)
        {
            state.Missing++;
        }
        else
        {
            state.Mismatched++;
        }

        targets.Add(new RepairTarget(relativePath, url, sha1, size));
    }

    // ---------------- 下载 ----------------

    private async Task DownloadTargetsAsync(
        string versionName,
        IReadOnlyList<RepairTarget> targets,
        RepairState state,
        RepairProgress reporter,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return;
        }

        // 生产路径用注入的队列：修复的逐文件进度才能出现在下载页的任务面板里。
        // 测试构造注入的是 HttpClient（没有队列），退回自建实例，行为与之前一致。
        // 并发：RepairAsync 与 InstallAsync 共用 _installGate，修复的普通批次不会在
        // 安装的外部批次进行中启动；面板上的「重试失败项」与修复并发时按队列既有语义
        // 后者覆盖前者的快照，不抛异常。
        var queue = _queue ?? new DownloadQueue(_client, _log, maxConcurrency: 8);
        var sink = reporter.DownloadSink;

        for (var offset = 0; offset < targets.Count; offset += DownloadBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(DownloadBatchSize, targets.Count - offset);
            var items = new List<DownloadItem>(count);

            for (var i = 0; i < count; i++)
            {
                var target = targets[offset + i];
                items.Add(new DownloadItem
                {
                    Url = target.Url,
                    DestinationPath = AbsolutePath(target.RelativePath),
                    Id = target.RelativePath,
                    Group = versionName,
                    ExpectedSize = target.Size,
                    ExpectedSha1 = target.Sha1,
                });
            }

            // 每批的 TotalFiles 只是批大小，直接透传会让进度条在批间反复归零，
            // 这里把「本批完成数」累加回整体再上报。
            var batchSink = sink is null ? null : new BatchProgressSink(sink, offset, targets.Count);
            var result = await queue.DownloadAsync(items, batchSink, cancellationToken).ConfigureAwait(false);

            ApplyBatchResult(items, result, state);
        }
    }

    private static void ApplyBatchResult(
        IReadOnlyList<DownloadItem> items,
        DownloadBatchResult result,
        RepairState state)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var failure in result.Failures)
        {
            failures[failure.Item.DestinationPath] = failure.Error;
        }

        foreach (var item in items)
        {
            if (failures.TryGetValue(item.DestinationPath, out var error))
            {
                state.Failed.Add($"{item.Id}：{error}");
                continue;
            }

            // DownloadQueue 会跳过「刚好已经正确」的文件，那些也算这次修复到位了。
            state.Redownloaded++;
            state.AddFixed(item.Id);
        }
    }

    // ---------------- 资源索引 ----------------

    private async Task<List<AssetObject>?> EnsureAssetIndexAsync(
        VersionChain chain,
        RepairState state,
        RepairProgress reporter,
        CancellationToken cancellationToken)
    {
        var index = chain.AssetIndex;
        if (index is null)
        {
            return null;
        }

        var relative = "assets/indexes/" + index.Id + ".json";
        var absolute = AbsolutePath(relative);

        var objects = await TryLoadAssetObjectsAsync(absolute, cancellationToken).ConfigureAwait(false);
        if (objects is not null)
        {
            return objects;
        }

        // 截断 / 非 JSON / 没有 objects 的索引都算坏，先删再下：
        // DownloadQueue 对「文件已存在且大小符合」会跳过，不删就修不动。
        TryDeleteFile(absolute);
        state.Missing++;

        var target = new RepairTarget(relative, index.Url, null, index.Size);
        await DownloadTargetsAsync(chain.Id, [target], state, reporter, cancellationToken).ConfigureAwait(false);

        objects = await TryLoadAssetObjectsAsync(absolute, cancellationToken).ConfigureAwait(false);
        if (objects is null)
        {
            state.Failed.Add($"{relative}：不是有效的资源索引（下载后仍无法解析）");
        }

        return objects;
    }

    private static async Task<List<AssetObject>?> TryLoadAssetObjectsAsync(
        string indexPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("objects", out var objects) ||
                objects.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new List<AssetObject>();

            foreach (var property in objects.EnumerateObject())
            {
                var hash = GetString(property.Value, "hash");
                if (hash is not { Length: >= 2 })
                {
                    continue;
                }

                var relativePath = "assets/objects/" + hash[..2] + "/" + hash;
                result.Add(new AssetObject(relativePath, hash, GetLong(property.Value, "size")));
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warn($"删除损坏文件失败：{path}", ex);
        }
    }

    // ---------------- pre-1.6 Forge 签名 ----------------

    /// <summary>
    /// MC 1.6 之前的 Forge 会给 jar 重新打包，而 Mojang 的类签名（<c>META-INF/MOJANG_C.DSA</c> /
    /// <c>.SF</c>）对不上改过的字节流，JVM 启动时直接 <c>SecurityException</c>。
    /// 删掉这两个签名文件是 HMCL / PCL2 的通行做法；entry 不存在时什么也不做。
    /// </summary>
    private void CleanLegacyForgeSignatures(VersionChain chain, RepairState state, CancellationToken cancellationToken)
    {
        if (!IsLegacyForge(chain))
        {
            return;
        }

        var jarPath = Path.Combine(_paths.VersionsDirectory, chain.Id, chain.Id + ".jar");
        if (SafeLength(jarPath) == 0)
        {
            return;
        }

        try
        {
            var removed = new List<string>(2);

            using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Update))
            {
                foreach (var entryName in new[] { LegacySignatureDsa, LegacySignatureSf })
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = archive.GetEntry(entryName);
                    if (entry is null)
                    {
                        continue;
                    }

                    entry.Delete();
                    removed.Add(entryName);
                }
            }

            if (removed.Count == 0)
            {
                return;
            }

            foreach (var entryName in removed)
            {
                state.AddFixed($"{RelativePathOf(jarPath)}:{entryName}");
            }

            _log?.Info($"已删除 {chain.Id} 的 Mojang 类签名：{string.Join('，', removed)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            state.Failed.Add($"清理 {chain.Id}.jar 的 Mojang 类签名失败：{ex.GetBaseException().Message}");
        }
    }

    private static bool IsLegacyForge(VersionChain chain)
    {
        var lower = chain.Id.ToLowerInvariant();

        // neoforge 名字里也含 forge，但它是 1.20 之后的东西，不该进这条路。
        if (!lower.Contains("forge", StringComparison.Ordinal) || lower.Contains("neoforge", StringComparison.Ordinal))
        {
            return false;
        }

        // 版本目录名里带加载器版本号（1.5.2-forge-7.8.1.738），原版号取继承链最底部那个更可靠。
        var vanilla = chain.Lineage.Count > 0 ? chain.Lineage[^1] : chain.Id;
        return IsOlderThan16(vanilla) || IsOlderThan16(chain.Id);
    }

    private static bool IsOlderThan16(string versionName)
    {
        var numbers = new List<int>(2);

        foreach (var part in versionName.Split('.', '-', '_', '+'))
        {
            if (numbers.Count == 2)
            {
                break;
            }

            if (int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                numbers.Add(value);
            }
        }

        if (numbers.Count < 2)
        {
            return false;
        }

        return numbers[0] < 1 || (numbers[0] == 1 && numbers[1] < 6);
    }

    // ---------------- 版本链 ----------------

    /// <summary>
    /// 版本描述 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 是否存在。
    ///
    /// 这是「这个版本能不能被 CmlLib 解析」的最低前提。整包导入在「不同时安装游戏」
    /// 时只建实例目录、不写版本 json（那是刻意的：写一个 <c>inheritsFrom</c> 为空的
    /// 版本只会让版本页多一条「损坏」），于是它的修复必然走到「没有版本描述」这条路上。
    /// </summary>
    private bool VersionDescriptionExists(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return false;
        }

        var jsonPath = Path.Combine(_paths.VersionsDirectory, versionName, versionName + ".json");
        return File.Exists(jsonPath);
    }

    /// <summary>
    /// 「缺少版本描述」的可读说明。
    ///
    /// 只说「找不到文件」是不够的：用户是从版本页点的修复，他要的是下一步做什么。
    /// 这里给两条互斥的出路——重新导入整包（拿回作者声明的版本），
    /// 或者到版本页把游戏装上（自己造一份父版本）。
    /// </summary>
    private static string DescribeMissingVersionDescription(string versionName) =>
        $"缺少版本描述文件 versions/{versionName}/{versionName}.json：这个版本无法被安装器解析。"
        + "它通常来自「导入整合包时没有同时安装游戏」——请在版本页为它安装游戏，或重新导入该整合包。";

    private async Task<VersionChain?> TryReadVersionChainAsync(string versionName, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadVersionChainAsync(versionName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取版本描述失败：{versionName}", ex);
            return null;
        }
    }

    /// <summary>
    /// 读取 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 及 <c>inheritsFrom</c> 链，合并成一个逻辑版本。
    ///
    /// 加载器版本文件只描述自己的增量，客户端 jar、资源索引、绝大多数库都在父版本里；
    /// 不沿着链合并就会把「库全都在」误判成「库全缺」。
    /// </summary>
    private async Task<VersionChain> ReadVersionChainAsync(string versionName, CancellationToken cancellationToken)
    {
        var documents = new List<(string Id, JsonDocument Document)>();
        var lineage = new List<string>();

        try
        {
            var current = versionName;

            for (var depth = 0; depth < 8; depth++)
            {
                var jsonPath = Path.Combine(_paths.VersionsDirectory, current, current + ".json");
                if (!File.Exists(jsonPath))
                {
                    if (lineage.Count == 0)
                    {
                        throw new FileNotFoundException($"缺少版本描述文件 {jsonPath}", jsonPath);
                    }

                    break;
                }

                lineage.Add(current);

                var json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
                var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                documents.Add((current, document));

                var inheritsFrom = GetString(document.RootElement, "inheritsFrom");
                if (string.IsNullOrWhiteSpace(inheritsFrom) ||
                    lineage.Contains(inheritsFrom.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    break;
                }

                current = inheritsFrom.Trim();
            }

            return MergeChain(versionName, lineage, documents);
        }
        finally
        {
            foreach (var (_, document) in documents)
            {
                document.Dispose();
            }
        }
    }

    private static VersionChain MergeChain(
        string versionName,
        List<string> lineage,
        List<(string Id, JsonDocument Document)> documents)
    {
        var libraries = new List<LibraryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ChainAssetIndex? assetIndex = null;
        ChainClient? client = null;

        foreach (var (documentId, document) in documents)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("libraries", out var librariesElement) && librariesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in librariesElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("downloads", out var downloads) ||
                        !downloads.TryGetProperty("artifact", out var artifact))
                    {
                        continue;
                    }

                    var artifactPath = GetString(artifact, "path");
                    if (string.IsNullOrWhiteSpace(artifactPath))
                    {
                        continue;
                    }

                    // 版本 JSON 的 path 相对 libraries/（Mojang 约定），清单与下载目标都用游戏根目录下的相对路径。
                    artifactPath = artifactPath.Replace('\\', '/').TrimStart('/');
                    var relativePath = "libraries/" + artifactPath;
                    if (!seen.Add(relativePath))
                    {
                        continue;
                    }

                    var url = GetString(artifact, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        url = LibraryBaseUrl + artifactPath;
                    }

                    libraries.Add(new LibraryEntry(
                        relativePath,
                        url.Trim(),
                        GetString(artifact, "sha1"),
                        GetLong(artifact, "size")));
                }
            }

            // 子版本优先：加载器可能带自己的 assetIndex，但通常没有，直接用父版本的。
            assetIndex ??= ParseAssetIndex(root);
            client ??= ParseClient(root, documentId);
        }

        return new VersionChain(versionName, lineage, libraries, assetIndex, client);
    }

    private static ChainAssetIndex? ParseAssetIndex(JsonElement root)
    {
        if (!root.TryGetProperty("assetIndex", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id");
        var url = GetString(element, "url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new ChainAssetIndex(id.Trim(), url.Trim(), GetString(element, "sha1"), GetLong(element, "size"));
    }

    private static ChainClient? ParseClient(JsonElement root, string providerId)
    {
        if (!root.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("client", out var client) ||
            client.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = GetString(client, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new ChainClient(providerId, url.Trim(), GetString(client, "sha1"), GetLong(client, "size"));
    }

    // ---------------- 工具 ----------------

    private FileCheck CheckFile(string relativePath, string? expectedSha1, bool verifySha1)
    {
        var absolute = AbsolutePath(relativePath);
        if (!File.Exists(absolute))
        {
            return FileCheck.Missing;
        }

        if (!verifySha1 || expectedSha1 is not { Length: > 0 })
        {
            return FileCheck.Present;
        }

        return Sha1Matches(absolute, expectedSha1) ? FileCheck.Present : FileCheck.Mismatched;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "Mojang 资源校验本身使用 SHA1，必须与官方一致")]
    private static bool Sha1Matches(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA1.HashData(stream));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了就当作「匹配不上」，让上层重下；重下仍失败会进 Failed。
            return false;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private string AbsolutePath(string relativePath) =>
        Path.Combine(_paths.MinecraftDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string RelativePathOf(string absolutePath) =>
        Path.GetRelativePath(_paths.MinecraftDirectory, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    // ---------------- 安装清单 ----------------

    /// <summary>
    /// 安装成功后写一份清单。记录版本目录里的 json / jar 与版本 JSON 声明的全部库，
    /// 供「组件是否还在」的 mtime 比对使用。
    ///
    /// 库的 sha1 直接取版本 JSON 里的声明值，不重算：1.12.2 这类版本有上千个库文件，
    /// 装完再全量 hash 一遍会把安装时间拉长一个量级，而 json 里的值本来就来自 Mojang。
    /// </summary>
    private async Task WriteInstallManifestAsync(
        string versionName,
        InstallRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var versionDirectory = Path.Combine(_paths.VersionsDirectory, versionName);
            if (!Directory.Exists(versionDirectory))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var manifest = new McInstallManifest { LastModifiedUtc = now };

            manifest.Components.Add(new McInstallComponent
            {
                Name = "minecraft",
                Version = request.MinecraftVersion,
                InstalledUtc = now,
            });

            if (request.Loader != LoaderKind.Vanilla)
            {
                manifest.Components.Add(new McInstallComponent
                {
                    Name = request.Loader.ToString().ToLowerInvariant(),
                    Version = request.LoaderVersion,
                    InstalledUtc = now,
                });
            }

            foreach (var file in Directory.EnumerateFiles(versionDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".jar", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                manifest.ManagedFiles.Add(new McInstallManifestFile
                {
                    RelativePath = RelativePathOf(file),
                    Sha1 = ComputeSha1OrNull(file),
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                    Component = "minecraft",
                });
            }

            var chain = await TryReadVersionChainAsync(versionName, cancellationToken).ConfigureAwait(false);
            if (chain is not null)
            {
                foreach (var library in chain.Libraries)
                {
                    var absolute = AbsolutePath(library.RelativePath);
                    if (!File.Exists(absolute))
                    {
                        continue;
                    }

                    manifest.ManagedFiles.Add(new McInstallManifestFile
                    {
                        RelativePath = library.RelativePath,
                        Sha1 = library.Sha1,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(absolute),
                        Component = "libraries",
                    });
                }
            }

            // 内容没变就不写盘：同一版本重复安装时，清单不该每次都刷新 mtime。
            var existing = await McInstallManifest.LoadAsync(versionDirectory, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.IsEquivalentTo(manifest))
            {
                return;
            }

            await manifest.SaveAsync(versionDirectory, cancellationToken).ConfigureAwait(false);
            _log?.Info($"已写入安装清单：{McInstallManifest.GetPath(versionDirectory)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"写入安装清单失败：{versionName}", ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "与 Mojang 资源校验保持一致，用 SHA1")]
    private static string? ComputeSha1OrNull(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------- 内部数据 ----------------

    private enum FileCheck
    {
        Present = 0,
        Missing = 1,
        Mismatched = 2,
    }

    private sealed record RepairTarget(string RelativePath, string Url, string? Sha1, long Size);

    private sealed record LibraryEntry(string RelativePath, string Url, string? Sha1, long Size);

    private sealed record ChainAssetIndex(string Id, string Url, string? Sha1, long Size);

    private sealed record ChainClient(string ProviderId, string Url, string? Sha1, long Size);

    private sealed record AssetObject(string RelativePath, string Hash, long Size);

    private sealed record VersionChain(
        string Id,
        IReadOnlyList<string> Lineage,
        IReadOnlyList<LibraryEntry> Libraries,
        ChainAssetIndex? AssetIndex,
        ChainClient? Client);

    /// <summary>修复过程中的累计计数。Fixed 有上限，避免上万个资源文件把报告撑爆。</summary>
    private sealed class RepairState
    {
        private const int MaxFixedEntries = 200;

        public int Missing { get; set; }

        public int Mismatched { get; set; }

        public int Redownloaded { get; set; }

        public List<string> Fixed { get; } = new();

        public List<string> Failed { get; } = new();

        public void AddFixed(string description)
        {
            if (Fixed.Count < MaxFixedEntries)
            {
                Fixed.Add(description);
            }
        }

        public RepairReport ToReport() => new(Missing, Mismatched, Redownloaded, Fixed, Failed);
    }

    /// <summary>
    /// 修复的进度曲线：Deep 档先全量 hash 再下载，两段各自从 0 开始会让进度条中途回退。
    /// 这里把校验压到前 30%，下载占后 70%。
    /// </summary>
    private sealed class RepairProgress
    {
        private const double ScanShare = 0.3;

        private readonly IProgress<DownloadProgress>? _target;

        private int _scanTotal;
        private int _scanDone;

        public RepairProgress(IProgress<DownloadProgress>? target)
        {
            _target = target;

            // 给 CmlLib 安装流程用的原样出口（Quick 档的补齐步骤不该被压缩进 30%~100%）。
            InnerProgress = target;
            DownloadSink = target is null ? null : new ScanOffsetProgress(target);
        }

        public IProgress<DownloadProgress>? InnerProgress { get; }

        public IProgress<DownloadProgress>? DownloadSink { get; }

        public void BeginScan(int total)
        {
            _scanTotal = Math.Max(0, total);
            _scanDone = 0;
        }

        public void ReportScan(string currentFile)
        {
            if (_target is null)
            {
                return;
            }

            _scanDone++;

            var ratio = _scanTotal > 0
                ? Math.Clamp((double)_scanDone / _scanTotal, 0, 1) * ScanShare
                : 0;

            _target.Report(new DownloadProgress(_scanTotal, _scanDone, 0, 0, 0, 0, currentFile, ratio));
        }

        private sealed class ScanOffsetProgress(IProgress<DownloadProgress> inner) : IProgress<DownloadProgress>
        {
            public void Report(DownloadProgress value) =>
                inner.Report(value with { Ratio = ScanShare + ((1 - ScanShare) * value.Ratio) });
        }
    }

    /// <summary>把一批下载的计数换算回整体，避免进度条在批间反复归零。</summary>
    private sealed class BatchProgressSink(IProgress<DownloadProgress> inner, int offset, int total)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            var done = offset + value.CompletedFiles;
            var ratio = total > 0 ? Math.Clamp((double)done / total, 0, 1) : value.Ratio;

            inner.Report(value with
            {
                TotalFiles = total,
                CompletedFiles = done,
                Ratio = ratio,
            });
        }
    }
}
