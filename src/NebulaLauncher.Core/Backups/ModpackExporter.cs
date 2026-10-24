using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Core.Backups;

/// <summary>
/// 把一个已配置好的实例导出成 Modrinth 格式的 <c>.mrpack</c>。
///
/// 为什么与 <see cref="ModpackService"/> 放在同一目录：导出是导入的逆操作，两边对
/// <c>modrinth.index.json</c> / <c>overrides/</c> 的理解必须一致；分成两个文件后，
/// 任何一边改格式都能在同一个目录里看到另一边的读取代码。
///
/// 为什么未识别的 jar 进 <c>overrides/</c> 而不是让整次导出失败：Modrinth 只收录了部分模组，
/// 用户手工拖入的 jar 是常态。导出的目标是「能完整装回去」，而不是「全部在线化」，
/// 失败只应该发生在真正无法落盘的时候。
///
/// 为什么用 <see cref="ModFingerprint"/> 具体类而不是新增接口：仓库里只有这一个哈希实现，
/// 再抽一层接口只会让调用方多一个必须注册的服务；构造参数保持可注入是为了测试能复用缓存目录。
/// </summary>
public sealed class ModpackExporter
{
    /// <summary>Modrinth 整合包的索引文件名。导入侧的 <c>InspectAsync</c> 靠它判定格式。</summary>
    public const string IndexFileName = "modrinth.index.json";

    /// <summary>覆盖文件在包内的固定前缀，与导入侧 <c>ExtractOverrides</c> 的读取前缀对应。</summary>
    public const string OverridesPrefix = "overrides/";

    /// <summary>版本 JSON 的 <c>inheritsFrom</c> 链最多跟这么多层，防止损坏的 JSON 互相引用成环。</summary>
    private const int MaxVersionChainDepth = 8;

    private const int CopyBufferSize = 81920;

    private static readonly JsonSerializerOptions InstanceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILauncherPaths? _paths;
    private readonly IModrinthService? _modrinth;
    private readonly ModFingerprint _fingerprint;
    private readonly IAppLog? _log;

    /// <summary>
    /// 构造函数。所有依赖都可为空：路径缺失时读不到加载器版本（索引里省略该键），
    /// Modrinth 缺失时全部 jar 走 <c>overrides/</c>——离线环境仍要能导出。
    /// </summary>
    public ModpackExporter(
        ILauncherPaths? paths = null,
        IModrinthService? modrinth = null,
        ModFingerprint? fingerprint = null,
        IAppLog? log = null)
    {
        _paths = paths;
        _modrinth = modrinth;
        _fingerprint = fingerprint ?? new ModFingerprint(paths?.CacheDirectory, log);
        _log = log;
    }

    /// <summary>
    /// 导出实例。<paramref name="instanceGameDirectory"/> 是实例的游戏目录（含 mods / config / saves），
    /// <paramref name="targetArchivePath"/> 是最终 <c>.mrpack</c> 路径，已存在时覆盖。
    /// </summary>
    /// <remarks>
    /// 不抛异常：失败返回 <see cref="ModpackExportResult.Fail(string)"/> 与中文原因；
    /// 取消也走失败结果，因为 UI 需要一句可展示的话而不是 <see cref="OperationCanceledException"/>。
    /// </remarks>
    public async Task<ModpackExportResult> ExportAsync(
        string instanceGameDirectory,
        string targetArchivePath,
        ModpackExportOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            return ModpackExportResult.Fail("缺少导出选项。");
        }

        if (string.IsNullOrWhiteSpace(instanceGameDirectory) || !Directory.Exists(instanceGameDirectory))
        {
            return ModpackExportResult.Fail($"实例目录不存在：{instanceGameDirectory}");
        }

        if (string.IsNullOrWhiteSpace(targetArchivePath))
        {
            return ModpackExportResult.Fail("请选择整合包的保存位置。");
        }

        var target = Path.GetFullPath(targetArchivePath);

        // 先写临时文件再 Move：直接写目标路径时用户中途关掉启动器会留下半个 zip，
        // 而 .mrpack 被覆盖后没有任何提示。同一目录下的 Move 在 Windows 与 Unix 上都接近原子替换。
        var temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            var binding = await ResolveVersionBindingAsync(instanceGameDirectory, options, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(binding.MinecraftVersion))
            {
                return ModpackExportResult.Fail("无法确定实例绑定的 Minecraft 版本，请在导出选项里填写游戏版本。");
            }

            progress?.Report(new DownloadProgress(0, 0, 0, 0, 0, 0, "正在扫描实例文件", 0));

            // 枚举 saves / config 可能是几万个文件，放线程池避免卡住调用方（UI）线程。
            var (mods, overrides) = await Task.Run(
                () => (
                    CollectModFiles(instanceGameDirectory),
                    CollectOverrideFiles(instanceGameDirectory, options)),
                cancellationToken).ConfigureAwait(false);

            var total = mods.Count + overrides.Count;

            var onlineFiles = new List<ExportFileEntry>();
            var overridesToWrite = new List<ExportFile>(overrides);
            var unresolved = 0;
            var completed = 0;

            foreach (var mod in mods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = await TryResolveOnlineAsync(mod, instanceGameDirectory, options, cancellationToken)
                    .ConfigureAwait(false);

                if (entry is not null)
                {
                    onlineFiles.Add(entry);
                }
                else
                {
                    overridesToWrite.Add(mod);
                    unresolved++;
                }

                completed++;
                progress?.Report(ReportProgress(total, completed, mod.EntryPath, "正在识别模组"));
            }

            var indexJson = BuildIndexJson(options, binding, onlineFiles);
            await WriteArchiveAsync(
                target,
                temporary,
                indexJson,
                overridesToWrite,
                total,
                mods.Count,
                progress,
                cancellationToken).ConfigureAwait(false);

            File.Move(temporary, target, overwrite: true);

            var bytes = new FileInfo(target).Length;
            _log?.Info(
                $"整合包已导出：{target}（在线条目 {onlineFiles.Count}，覆盖文件 {overridesToWrite.Count}，未识别模组 {unresolved}）");

            progress?.Report(new DownloadProgress(total, total, 0, 0, 0, 0, "导出完成", 1));

            return new ModpackExportResult
            {
                Success = true,
                ArchivePath = target,
                Files = onlineFiles.Count + overridesToWrite.Count,
                UnresolvedMods = unresolved,
                Bytes = bytes,
            };
        }
        catch (OperationCanceledException)
        {
            return ModpackExportResult.Fail("导出已取消。");
        }
        catch (Exception ex)
        {
            _log?.Error($"导出整合包失败：{target}", ex);
            return ModpackExportResult.Fail($"导出整合包失败：{ex.Message}");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    // ---------------- 文件收集 ----------------

    private static List<ExportFile> CollectModFiles(string gameDirectory)
    {
        var result = new List<ExportFile>();
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        if (!Directory.Exists(modsDirectory))
        {
            return result;
        }

        foreach (var file in EnumerateFilesSafe(modsDirectory))
        {
            var fileName = Path.GetFileName(file);

            if (!IsModFile(fileName))
            {
                continue;
            }

            // 禁用态实现为改扩展名，包里必须写回原文件名，否则导入方会把整个模组当成陌生文件。
            result.Add(new ExportFile(file, "mods/" + RestoreDisabledName(fileName)));
        }

        return result;
    }

    private static List<ExportFile> CollectOverrideFiles(string gameDirectory, ModpackExportOptions options)
    {
        var result = new List<ExportFile>();
        var directories = new List<string>();

        if (options.IncludeConfig)
        {
            directories.Add("config");
        }

        if (options.IncludeResourcePacks)
        {
            directories.Add("resourcepacks");
        }

        if (options.IncludeShaderPacks)
        {
            directories.Add("shaderpacks");
        }

        if (options.IncludeSaves)
        {
            directories.Add("saves");
        }

        if (options.IncludeDefaultConfigs)
        {
            directories.Add("defaultconfigs");
        }

        foreach (var name in directories)
        {
            var root = Path.Combine(gameDirectory, name);

            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateFilesSafe(root))
            {
                var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');

                // logs / crash-reports 是故障现场而不是实例内容；即使调用方传了同名选项也不打包。
                if (IsExcluded(relative))
                {
                    continue;
                }

                result.Add(new ExportFile(file, relative));
            }
        }

        // 顶层单文件：options.txt（游戏设置）与 servers.dat（服务器列表）。
        // 它们不在任何子目录里，必须单独收集，否则「导出选项勾了却没打包」。
        AddTopLevelFile(gameDirectory, "options.txt", options.IncludeOptions, result);
        AddTopLevelFile(gameDirectory, "servers.dat", options.IncludeServerList, result);

        return result;
    }

    private static void AddTopLevelFile(
        string gameDirectory,
        string fileName,
        bool include,
        List<ExportFile> result)
    {
        if (!include)
        {
            return;
        }

        var path = Path.Combine(gameDirectory, fileName);

        if (File.Exists(path))
        {
            result.Add(new ExportFile(path, fileName));
        }
    }

    private static string[] EnumerateFilesSafe(string root)
    {
        try
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            // 单个目录不可读（权限 / 被占用）时跳过它，其余文件照常导出。
            return Array.Empty<string>();
        }
    }

    private static bool IsModFile(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static string RestoreDisabledName(string fileName) =>
        fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

    private static bool IsExcluded(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- 在线反查 ----------------

    private async Task<ExportFileEntry?> TryResolveOnlineAsync(
        ExportFile mod,
        string gameDirectory,
        ModpackExportOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.PreferModrinthHashes || _modrinth is null)
        {
            return null;
        }

        ModFileHashes hashes;

        try
        {
            // 传 gameDirectory 当基准目录：同一份实例整体挪位置后仍能命中哈希缓存。
            hashes = await _fingerprint.GetHashesAsync(mod.SourcePath, gameDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"计算模组哈希失败，按未识别处理：{mod.SourcePath}", ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(hashes.Sha1))
        {
            return null;
        }

        ModrinthVersion? version;

        try
        {
            version = await _modrinth.LookupBySha1Async(hashes.Sha1.ToLowerInvariant(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 网络失败必须降级为 overrides：离线导出仍然是有效产物，只是包体积大。
            _log?.Warn($"在线哈希反查失败，按未识别处理：{mod.SourcePath}", ex);
            return null;
        }

        if (version is null || string.IsNullOrWhiteSpace(version.DownloadUrl))
        {
            return null;
        }

        var sha512 = await TryComputeSha512Async(mod.SourcePath, cancellationToken).ConfigureAwait(false);

        return new ExportFileEntry(
            mod.EntryPath,
            hashes.Sha1.ToLowerInvariant(),
            sha512,
            version.DownloadUrl,
            hashes.SizeBytes);
    }

    private static async Task<string?> TryComputeSha512Async(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                useAsync: true);

            var hash = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // sha512 只是给第三方工具的第二重校验；算不出来时省略字段，sha1 仍是必填项。
            return null;
        }
    }

    // ---------------- 索引与压缩包 ----------------

    private static byte[] BuildIndexJson(
        ModpackExportOptions options,
        VersionBinding binding,
        IReadOnlyList<ExportFileEntry> files)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString("game", "minecraft");
            writer.WriteString(
                "versionId",
                string.IsNullOrWhiteSpace(options.Version) ? "1.0.0" : options.Version.Trim());
            writer.WriteString(
                "name",
                string.IsNullOrWhiteSpace(options.Name) ? "未命名整合包" : options.Name.Trim());
            writer.WriteString("summary", BuildSummary(options));

            writer.WriteStartArray("files");

            foreach (var file in files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);

                writer.WriteStartObject("hashes");
                writer.WriteString("sha1", file.Sha1);

                if (!string.IsNullOrWhiteSpace(file.Sha512))
                {
                    writer.WriteString("sha512", file.Sha512);
                }

                writer.WriteEndObject();

                writer.WriteStartArray("downloads");
                writer.WriteStringValue(file.DownloadUrl);
                writer.WriteEndArray();

                writer.WriteNumber("fileSize", file.SizeBytes);

                // 在线条目只来自 mods/；env 用于告诉安装器「客户端与服务端都保留」，
                // 这里不区分服务端专用模组（本地无法可靠判断）。
                writer.WriteStartObject("env");
                writer.WriteString("client", "required");
                writer.WriteString("server", "required");
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("dependencies");
            writer.WriteString("minecraft", binding.MinecraftVersion);

            if (!string.IsNullOrWhiteSpace(binding.LoaderKey) && !string.IsNullOrWhiteSpace(binding.LoaderVersion))
            {
                writer.WriteString(binding.LoaderKey, binding.LoaderVersion);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string BuildSummary(ModpackExportOptions options) =>
        string.IsNullOrWhiteSpace(options.Author)
            ? "由 NebulaLauncher 导出的整合包。"
            : $"由 {options.Author.Trim()} 打包，NebulaLauncher 导出。";

    private async Task WriteArchiveAsync(
        string target,
        string temporary,
        byte[] indexJson,
        IReadOnlyList<ExportFile> overrides,
        int total,
        int completedBefore,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(target);

        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await using var fileStream = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            useAsync: true);

        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        var indexEntry = archive.CreateEntry(IndexFileName, CompressionLevel.Optimal);

        await using (var indexStream = indexEntry.Open())
        {
            await indexStream.WriteAsync(indexJson, cancellationToken).ConfigureAwait(false);
        }

        var completed = completedBefore;

        foreach (var file in overrides)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileStream input;

            try
            {
                input = new FileStream(
                    file.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    useAsync: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 文件在导出过程中被删 / 被独占：跳过它比让整包失败合理，结果里的文件数会少一个。
                _log?.Warn($"跳过无法读取的整合包条目：{file.SourcePath}", ex);
                continue;
            }

            await using (input)
            {
                var entryPath = OverridesPrefix + file.EntryPath;
                var zipEntry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);

                await using var output = zipEntry.Open();
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            completed++;
            progress?.Report(
                ReportProgress(total, completed, OverridesPrefix + file.EntryPath, "正在写入整合包"));
        }
    }

    private static DownloadProgress ReportProgress(int total, int completed, string entryPath, string label)
    {
        var ratio = total <= 0 ? 0 : Math.Clamp(completed / (double)total, 0, 1);
        var fileName = Path.GetFileName(entryPath);

        return new DownloadProgress(
            total,
            completed,
            0,
            0,
            0,
            0,
            fileName.Length == 0 ? label : $"{label}：{fileName}",
            ratio);
    }

    // ---------------- 实例版本与加载器 ----------------

    private async Task<VersionBinding> ResolveVersionBindingAsync(
        string gameDirectory,
        ModpackExportOptions options,
        CancellationToken cancellationToken)
    {
        var instance = await TryReadInstanceAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        var chain = new List<VersionNode>();

        if (_paths is not null && !string.IsNullOrWhiteSpace(instance?.VersionName))
        {
            chain = await ReadVersionChainAsync(_paths.VersionsDirectory, instance.VersionName!.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        // 优先级：调用方显式指定的版本 > 磁盘上的 inheritsFrom 链 > instance.json 的解析快照。
        // 实例 JSON 只存解析快照，版本被重装后可能过期；链在父版本处断开（父 JSON 被删）时，
        // 最后一层的 inheritsFrom 就是父版本名，仍可用。
        string? chainVersion = null;

        if (chain.Count > 0)
        {
            var tail = chain[^1];
            chainVersion = string.IsNullOrWhiteSpace(tail.InheritsFrom) ? tail.Id : tail.InheritsFrom;
        }

        var minecraft = FirstNonEmpty(options.MinecraftVersion, chainVersion, instance?.MinecraftVersion);

        var loader = instance?.Loader ?? LoaderKind.Vanilla;
        var (loaderKey, loaderVersion) = InferLoader(chain, minecraft, loader);

        return new VersionBinding(minecraft ?? string.Empty, loaderKey, loaderVersion);
    }

    private static async Task<InstanceInfo?> TryReadInstanceAsync(string gameDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var file = Path.Combine(gameDirectory, "instance.json");

            if (!File.Exists(file))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<InstanceInfo>(json, InstanceJsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // instance.json 损坏时仍可导出：Modrinth 索引的 dependencies 允许只写能确定的部分。
            return null;
        }
    }

    private static async Task<List<VersionNode>> ReadVersionChainAsync(
        string versionsDirectory,
        string versionName,
        CancellationToken cancellationToken)
    {
        var chain = new List<VersionNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = versionName;

        for (var depth = 0; depth < MaxVersionChainDepth && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (!visited.Add(current))
            {
                break;
            }

            var node = await TryReadVersionNodeAsync(versionsDirectory, current, cancellationToken)
                .ConfigureAwait(false);

            if (node is null)
            {
                break;
            }

            chain.Add(node);
            current = node.InheritsFrom;
        }

        return chain;
    }

    private static async Task<VersionNode?> TryReadVersionNodeAsync(
        string versionsDirectory,
        string versionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(versionsDirectory, versionName, versionName + ".json");

            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = document.RootElement;
            var id = GetString(root, "id") ?? versionName;
            var inheritsFrom = GetString(root, "inheritsFrom");

            var libraries = new List<string>();

            if (root.TryGetProperty("libraries", out var librariesNode)
                && librariesNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in librariesNode.EnumerateArray())
                {
                    if (GetString(library, "name") is { Length: > 0 } coordinate)
                    {
                        libraries.Add(coordinate);
                    }
                }
            }

            return new VersionNode(id, inheritsFrom, libraries);
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

    private static (string? Key, string? Version) InferLoader(
        IReadOnlyList<VersionNode> chain,
        string? minecraft,
        LoaderKind instanceLoader)
    {
        foreach (var node in chain)
        {
            var detected = DetectLoaderFromLibraries(node.Libraries, minecraft);

            if (!string.IsNullOrWhiteSpace(detected.Key))
            {
                return detected;
            }
        }

        if (chain.Count > 0)
        {
            var fromId = DetectLoaderFromId(chain[0].Id, minecraft, instanceLoader);

            if (!string.IsNullOrWhiteSpace(fromId.Key))
            {
                return fromId;
            }
        }

        return (LoaderKeyOf(instanceLoader), null);
    }

    private static (string? Key, string? Version) DetectLoaderFromLibraries(
        IReadOnlyList<string> libraries,
        string? minecraft)
    {
        foreach (var coordinate in libraries)
        {
            var parts = coordinate.Split(':');

            if (parts.Length < 3)
            {
                continue;
            }

            var artifact = parts[1];
            var version = parts[2];

            if (artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
            {
                return ("fabric-loader", version);
            }

            if (artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
            {
                return ("quilt-loader", version);
            }

            // 先判 NeoForge：它的旧版 group 里带 forge，顺序反了会被 Forge 分支截走。
            if (artifact.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                return ("neoforge", version);
            }

            if (artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
            {
                return ("forge", StripMinecraftPrefix(version, minecraft));
            }
        }

        return (null, null);
    }

    private static (string? Key, string? Version) DetectLoaderFromId(
        string id,
        string? minecraft,
        LoaderKind instanceLoader)
    {
        const string fabricPrefix = "fabric-loader-";
        const string quiltPrefix = "quilt-loader-";
        const string neoforgePrefix = "neoforge-";
        const string neoforgeMarker = "-neoforge-";
        const string forgeMarker = "-forge-";

        var text = id.Trim();

        if (text.StartsWith(fabricPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("fabric-loader", StripMinecraftSuffix(text[fabricPrefix.Length..], minecraft));
        }

        if (text.StartsWith(quiltPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("quilt-loader", StripMinecraftSuffix(text[quiltPrefix.Length..], minecraft));
        }

        if (text.StartsWith(neoforgePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("neoforge", text[neoforgePrefix.Length..]);
        }

        // CmlLib 的 NeoForge 安装目录名是 "<mc>-neoforge-<版本>"；先于 Forge 判断，避免被同一版本的父级名字截走。
        var neoforgeIndex = text.IndexOf(neoforgeMarker, StringComparison.OrdinalIgnoreCase);

        if (neoforgeIndex >= 0)
        {
            return ("neoforge", text[(neoforgeIndex + neoforgeMarker.Length)..]);
        }

        var forgeIndex = text.IndexOf(forgeMarker, StringComparison.OrdinalIgnoreCase);

        if (forgeIndex >= 0)
        {
            return ("forge", text[(forgeIndex + forgeMarker.Length)..]);
        }

        return (LoaderKeyOf(instanceLoader), null);
    }

    private static string? StripMinecraftPrefix(string version, string? minecraft)
    {
        if (!string.IsNullOrWhiteSpace(minecraft)
            && version.StartsWith(minecraft + "-", StringComparison.OrdinalIgnoreCase))
        {
            return version[(minecraft.Length + 1)..];
        }

        return version;
    }

    private static string? StripMinecraftSuffix(string version, string? minecraft)
    {
        if (!string.IsNullOrWhiteSpace(minecraft)
            && version.EndsWith("-" + minecraft, StringComparison.OrdinalIgnoreCase))
        {
            return version[..^(minecraft.Length + 1)];
        }

        return version;
    }

    private static string? LoaderKeyOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric-loader",
        LoaderKind.Quilt => "quilt-loader",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    // ---------------- 通用辅助 ----------------

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

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
            // 临时文件删不掉只影响磁盘，不影响已经返回给调用方的结果。
        }
    }

    /// <summary>待打包的一个本地文件：源绝对路径 + 包内路径。</summary>
    private sealed record ExportFile(string SourcePath, string EntryPath);

    /// <summary>写入 <c>files[]</c> 的一个在线条目。</summary>
    private sealed record ExportFileEntry(string Path, string Sha1, string? Sha512, string DownloadUrl, long SizeBytes);

    /// <summary>从实例绑定的版本链解出的依赖信息。</summary>
    private sealed record VersionBinding(string MinecraftVersion, string? LoaderKey, string? LoaderVersion);

    /// <summary>版本 JSON 里导出真正需要的字段。</summary>
    private sealed record VersionNode(string Id, string? InheritsFrom, IReadOnlyList<string> Libraries);
}
