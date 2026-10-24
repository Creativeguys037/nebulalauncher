using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Backups;

/// <summary>
/// 整合包识别与导入。支持 Modrinth（<c>modrinth.index.json</c>）、
/// CurseForge（<c>manifest.json</c>）、MCBBS（<c>mcbbs.packmeta</c> / 带 <c>addons</c> 的 manifest）、
/// HMCL（<c>modpack.json</c>）、MultiMC（<c>mmc-pack.json</c>）与普通压缩包。
///
/// 导入是五段的：装游戏与加载器 → 解压覆盖层 → 按 index 下载文件 → 写实例自己的继承版本 json
/// → 写 <c>instance.json</c>。顺序是刻意的：装游戏排在最前面，失败时实例目录还是空的，
/// 不需要回滚已解压的几百 MB 文件（PCL2 用 <c>ModDownloadLib.vb:McInstallFailedClearFolder</c>
/// 事后清目录，这里靠顺序避免）。
///
/// 实例目录 = 版本目录 = <c>versions/&lt;整合包实例名&gt;</c>（PCL2 <c>ModModpack.vb:37</c> 的模型）：
/// 每个整合包因此是一个独立版本，同一 MC 版本可以装任意多份；游戏本体只装一份，由实例版本
/// 用 <c>inheritsFrom</c> 继承。
/// </summary>
public sealed class ModpackService : IModpackService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new ModpackJsonContext(),
    };

    /// <summary>
    /// 格式判定键，顺序即优先级。照抄 PCL2 <c>ModModpack.vb:41-83</c>：
    /// <c>mcbbs.packmeta</c> 与 <c>mmc-pack.json</c> 都要排在 <c>manifest.json</c> 之前
    /// （MCBBS 的 manifest 与 CurseForge 同构，先判 CurseForge 会把 MCBBS 包认成 CF 包）。
    /// </summary>
    private static readonly string[] FormatKeys =
    [
        "mcbbs.packmeta",
        "mmc-pack.json",
        "modrinth.index.json",
        "manifest.json",
        "modpack.json",
    ];

    private readonly ILauncherPaths _paths;
    private readonly DownloadQueue _queue;
    private readonly IAppLog? _log;
    private readonly ModpackExporter _exporter;
    private readonly IInstallService? _install;
    private readonly IModrinthService? _modrinth;
    private readonly ICurseForgeService? _curseForge;

    /// <summary>
    /// 构造服务。<paramref name="modrinth"/> 用于导出时按 SHA1 反查在线项目、以及导入时
    /// 反查 MCBBS <c>addons</c> 的直链；
    /// 缺失时导出仍可用，只是所有 jar 都进 <c>overrides/</c>。
    /// <paramref name="install"/> 用于导入时安装整合包声明的游戏与加载器；
    /// 缺失时导入退化为「只解压文件」（本轮之前的形态），不抛异常。
    /// <paramref name="curseForge"/> 是 addons 反查的第一顺位；缺失时
    /// 退化为「按 fileId 推 forgecdn 直链 + Modrinth 兜底」。
    /// </summary>
    public ModpackService(
        ILauncherPaths paths,
        DownloadQueue downloadQueue,
        IAppLog? log = null,
        IModrinthService? modrinth = null,
        IInstallService? install = null,
        ICurseForgeService? curseForge = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _queue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _log = log;
        _exporter = new ModpackExporter(paths, modrinth, log: log);
        _install = install;
        _modrinth = modrinth;
        _curseForge = curseForge;
    }

    /// <inheritdoc />
    public bool InstallGameOnImport { get; set; } = true;

    /// <inheritdoc />
    public ModpackImportResult? LastImport { get; private set; }

    public Task<ModpackPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException($"整合包不存在：{archivePath}", archivePath);
        }

        return Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(archivePath);
            cancellationToken.ThrowIfCancellationRequested();

            var entries = archive.Entries;
            var fallbackName = Path.GetFileNameWithoutExtension(archivePath);
            var (format, entry, baseFolder) = DetectFormat(archive);

            return format switch
            {
                ModpackFormat.ModrinthModpack => ReadModrinth(entry!, entries, fallbackName),
                ModpackFormat.MultiMcZip => ReadMultiMc(entry!, entries, fallbackName),
                ModpackFormat.CurseForgeZip => ReadCurseForge(entry!, entries, fallbackName, ModpackFormat.CurseForgeZip),
                ModpackFormat.McbbsZip => ReadMcbbs(entry!, baseFolder, entries, fallbackName),
                ModpackFormat.HmclZip => ReadHmcl(entry!, entries, fallbackName),
                ModpackFormat.LauncherPack => new ModpackPreview
                {
                    Name = fallbackName,
                    Format = ModpackFormat.LauncherPack,
                    FileCount = entries.Count,
                    TotalBytes = entries.Sum(e => e.Length),
                    Files = entries.Take(200).Select(e => e.FullName).ToList(),
                },
                _ => new ModpackPreview
                {
                    Name = fallbackName,
                    Format = ModpackFormat.PlainZip,
                    FileCount = entries.Count,
                    TotalBytes = entries.Sum(e => e.Length),
                    Files = entries.Take(200).Select(e => e.FullName).ToList(),
                },
            };
        }, cancellationToken);
    }

    /// <summary>
    /// 打不开压缩包时给用户看的原因。
    ///
    /// 为什么按扩展名判断而不是按异常类型：<c>ZipFile.OpenRead</c> 对 .rar、.7z 与「损坏的 zip」
    /// 抛的是同一个异常（<c>InvalidDataException</c>，偶尔是 <c>EndOfStreamException</c>），
    /// 异常信息还是英文。只有文件名能表达「用户以为这是能导入的整合包，但它根本不是 zip」这层意图。
    ///
    /// 为什么不顺手支持 rar：那要引入第三方解压库（RAR 的压缩算法有专利与许可问题），
    /// 与本条的成本不符。既然不做，提示就必须给出用户能做的下一步——
    /// 只说「不支持」等于把人卡死在原地。
    /// </summary>
    internal static string DescribeOpenFailure(string archivePath)
    {
        var extension = Path.GetExtension(archivePath ?? string.Empty).TrimStart('.').ToLowerInvariant();

        return extension switch
        {
            "rar" => "无法读取这个整合包：启动器不能直接处理 rar 格式的压缩包。"
                     + "请先用压缩软件把它解压，再把解压出来的文件重新压缩成 zip 格式后重试。",
            "7z" => "无法读取这个整合包：启动器只能读取 zip / mrpack 格式的压缩包（7z 不行）。"
                    + "请先用压缩软件把 .7z 解压，再把解压出来的文件重新压缩成 zip 格式后重试。",
            _ => "无法读取这个整合包：文件可能已损坏，或者不是受支持的压缩包格式。"
                 + "请确认文件完整；如果它其实是 rar / 7z，请解压后重新压缩成 zip 再试。",
        };
    }

    /// <inheritdoc />
    public async Task<string> ImportAsync(
        string archivePath,
        string instanceName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ImportWithResultAsync(archivePath, instanceName, progress, cancellationToken)
            .ConfigureAwait(false);

        LastImport = result;

        if (!result.Success || result.InstanceDirectory is null)
        {
            throw new InvalidOperationException(result.Error ?? "整合包导入失败");
        }

        return result.InstanceDirectory;
    }

    /// <summary>
    /// <see cref="ImportAsync"/> 的结构化版本：不抛异常，把所有阶段的真实结果摊在返回值里。
    ///
    /// 导入失败的类型差别很大（装不上 vs 解压失败 vs 部分文件没下下来），
    /// 旧签名只能把它们压成一句 <c>ex.Message</c>。
    /// </summary>
    public async Task<ModpackImportResult> ImportWithResultAsync(
        string archivePath,
        string instanceName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ModpackPreview preview;
        try
        {
            preview = await InspectAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Error($"读取整合包失败：{archivePath}", ex);
            return ModpackImportResult.Fail(DescribeOpenFailure(archivePath));
        }

        // 带启动器的分发包：外层没有可用的版本声明，内层才是真正的整合包。
        // 明确拒绝并给下一步，比把它当普通 zip 解压出一堆 exe/配置更像「能用」。
        if (preview.Format == ModpackFormat.LauncherPack)
        {
            return new ModpackImportResult
            {
                Success = false,
                Format = ModpackFormat.LauncherPack,
                Error =
                    "这是带启动器的整合包（外层包含 modpack.zip 或 modpack.mrpack）。"
                    + "请先用压缩软件把它解压，再导入解压出来的那个 modpack 文件；"
                    + "直接导入外层会把启动器文件当成客户端内容。",
            };
        }

        string? installedVersionName = null;
        var warning = default(string);

        // 第 1 段：装游戏与加载器。
        var shouldInstall = InstallGameOnImport
                            && _install is not null
                            && preview.RequiresGameInstall;

        if (shouldInstall)
        {
            progress?.Report(new DownloadProgress(0, 0, 0, 0, 0, 0, "正在安装游戏与加载器…", 0.05));

            try
            {
                (installedVersionName, warning) = await EnsureGameInstalledAsync(preview, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 此时实例目录还没建，不需要回滚任何已落盘的文件。
                _log?.Error($"整合包的游戏与加载器安装失败：{ex.Message}", ex);
                return new ModpackImportResult
                {
                    Success = false,
                    Error = $"整合包声明的 {DescribeTarget(preview)} 安装失败：{ex.Message}。"
                            + "可以关掉「同时安装游戏」只解压文件，或在版本页单独安装后重试。",
                    Format = preview.Format,
                    MinecraftVersion = preview.MinecraftVersion,
                    Loader = preview.Loader,
                    LoaderVersion = preview.LoaderVersion,
                };
            }
        }

        // 第 2～4 段：解压、下载、写实例描述。
        //
        // 实例目录就是版本目录，目录名取「整合包实例名」——每个整合包因此得到自己的版本目录
        // （PCL2 的模型，ModModpack.vb:37 <c>TargetFolder = versions\{InstanceName}</c>），
        // 同一 MC 版本可以并存任意多份，游戏本体共享 EnsureGameInstalledAsync 装出来的那一份。
        // 重名由 ResolveUniqueVersionName 加序号解决，不再拒绝导入。
        var versionName = ResolveUniqueVersionName(instanceName);
        var instanceDirectory = ResolveInstanceDirectory(versionName);

        var directoryExistedBefore = Directory.Exists(instanceDirectory);
        Directory.CreateDirectory(instanceDirectory);

        var downloaded = 0;
        var failed = 0;
        var addons = AddonImportOutcome.Empty;

        try
        {
            await Task.Run(
                () => ExtractOverrides(archivePath, preview.Format, instanceDirectory, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new DownloadProgress(0, 0, 0, 0, 0, 0, "覆盖文件解压完成", 0.8));

            var downloadItems = ReadModrinthDownloads(archivePath, instanceDirectory);
            if (downloadItems.Count > 0)
            {
                var queueResult = await _queue.DownloadAsync(downloadItems, progress, cancellationToken)
                    .ConfigureAwait(false);

                downloaded = queueResult.CompletedFiles;
                failed = queueResult.FailedFiles;

                if (!queueResult.Success)
                {
                    _log?.Warn($"整合包有 {failed} 个文件下载失败（共 {queueResult.TotalFiles} 个）");
                }
            }

            // MCBBS / CurseForge 的 addons 只有「项目 id + 文件 id」，必须逐项反查直链。
            // 这一步放在覆盖层解压之后：overrides 里的文件是作者改过的版本，应当优先于在线原件，
            // 已存在的目标直接跳过下载。任何一项反查/下载失败只进清单，不中断导入。
            addons = await ImportMcbbsAddonsAsync(archivePath, preview, instanceDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            downloaded += addons.Downloaded;
        }
        catch (OperationCanceledException)
        {
            // 导入前就存在的目录里可能有版本本体，不能因为一次取消就把它删掉。
            if (!directoryExistedBefore)
            {
                TryDeleteDirectory(instanceDirectory);
            }

            throw;
        }
        catch (Exception ex)
        {
            _log?.Error($"整合包解压/下载失败：{instanceName}", ex);

            if (!directoryExistedBefore)
            {
                TryDeleteDirectory(instanceDirectory);
            }
            return new ModpackImportResult
            {
                Success = false,
                Error = $"整合包解压失败：{ex.Message}",
                Format = preview.Format,
                MinecraftVersion = preview.MinecraftVersion,
                Loader = preview.Loader,
                LoaderVersion = preview.LoaderVersion,
            };
        }

        // 第 5 段：写版本描述与实例描述。
        //
        // 版本 json 只声明「我是谁、我继承谁」，文件交给 CmlLib 沿 inheritsFrom 链补全；
        // 没装游戏时不写（实例目录只有 instance.json，文案里也已说明「未安装游戏」），
        // 装了一部分但拿不到父版本名时同样不写——写一个 inheritsFrom 为空的版本只会让
        // 版本页多一条「损坏」。
        if (shouldInstall && !string.IsNullOrEmpty(installedVersionName))
        {
            await WriteInheritedVersionJsonAsync(instanceDirectory, versionName, installedVersionName, cancellationToken)
                .ConfigureAwait(false);
        }

        // VersionName 必须真实存在于 versions/ 里：实例目录本身就是 versions/<versionName>，
        // 两者由同一个变量拼出来，不会再对不上。
        var instance = new InstanceInfo
        {
            Id = Guid.NewGuid().ToString("N")[..12],

            // 实例名用唯一化后的版本名：重复导入同一个包时目录是「Pack A (2)」，
            // 若这里仍写「Pack A」，版本页会出现两个同名实例，玩家分不清哪个是刚装的。
            Name = versionName,
            VersionName = versionName,
            CreatedAt = DateTimeOffset.UtcNow,

            // 加载器与版本号必须一起写：模组页按「加载器 → 版本」分组，
            // 缺这两个字段会让导入的实例在界面上退化成一堆「原版」条目。
            Loader = preview.Loader,
            MinecraftVersion = string.IsNullOrWhiteSpace(preview.MinecraftVersion) ? null : preview.MinecraftVersion,
            IsolationEnabled = true,

            // 记录来源：导出侧与后续「检查整合包更新」都要知道这份实例从哪来。
            ManagedPack = new ManagedPackInfo
            {
                Type = ManagedPackTypeOf(preview.Format),
                Name = preview.Name,
                VersionId = string.IsNullOrWhiteSpace(preview.Version) ? null : preview.Version,
                SourceArchive = Path.GetFullPath(archivePath),
                ImportedAt = DateTimeOffset.UtcNow,
            },
        };

        var json = JsonSerializer.Serialize(instance, JsonOptions.Of<InstanceInfo>());
        await File.WriteAllTextAsync(
            Path.Combine(instanceDirectory, "instance.json"),
            json,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new DownloadProgress(
            downloaded + failed,
            downloaded + failed,
            0,
            0,
            0,
            0,
            "导入完成",
            1));

        _log?.Info(
            $"整合包已导入：{instanceDirectory}（{preview.Format}，版本 {versionName}"
            + (shouldInstall
                ? installedVersionName is null ? "，未确认安装出的父版本" : $"，继承 {installedVersionName}"
                : "，未安装游戏")
            + (addons.Total > 0 ? $"，附加内容 {addons.Downloaded}/{addons.Total} 个已下载、{addons.Pending.Count} 个待手动安装" : string.Empty)
            + "）");

        return new ModpackImportResult
        {
            Success = true,
            InstanceDirectory = instanceDirectory,
            Format = preview.Format,
            MinecraftVersion = preview.MinecraftVersion,
            Loader = preview.Loader,
            LoaderVersion = preview.LoaderVersion,
            InstalledVersionName = versionName,

            // 「游戏装上了」的判据必须包含「实例版本有父版本可继承」：只装成功但拿不到父版本名时，
            // 实例目录没有版本 json，启动会被前置检查挡下。此时如实报 false，让 UI 的红条
            // 提示「到版本页完成安装」，而不是给一个看似成功、实际启动不了的结果。
            GameInstalled = shouldInstall && !string.IsNullOrEmpty(installedVersionName),
            FilesDownloaded = downloaded,
            FilesFailed = failed,
            Warning = warning,
            AddonsTotal = addons.Total,
            AddonsDownloaded = addons.Downloaded,
            PendingAddons = addons.Pending,
        };
    }

    /// <summary>
    /// 把实例导出成 <c>.mrpack</c>。实现委托给 <see cref="ModpackExporter"/>：
    /// 导入侧与导出侧必须共享同一套 <c>modrinth.index.json</c> / <c>overrides/</c> 约定。
    /// </summary>
    public Task<ModpackExportResult> ExportAsync(
        string instanceGameDirectory,
        string targetArchivePath,
        ModpackExportOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _exporter.ExportAsync(instanceGameDirectory, targetArchivePath, options, progress, cancellationToken);

    // ---------------- 游戏与加载器安装 ----------------

    /// <summary>
    /// 按整合包声明的 MC 版本与加载器装一份可启动的版本，返回它在 <c>versions/</c> 下的目录名。
    ///
    /// 这一份是**所有整合包共享的父版本**（原版或加载器版本），实例自己的版本目录只写一条
    /// <c>inheritsFrom</c> 指向它，所以同一 MC 版本装多个整合包不会重复装游戏本体。
    /// </summary>
    private async Task<(string? VersionName, string? Warning)> EnsureGameInstalledAsync(
        ModpackPreview preview,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var minecraftVersion = preview.MinecraftVersion.Trim();
        var loaderVersion = string.IsNullOrWhiteSpace(preview.LoaderVersion) ? null : preview.LoaderVersion.Trim();

        var request = new InstallRequest
        {
            MinecraftVersion = minecraftVersion,
            Loader = preview.Loader,
            LoaderVersion = loaderVersion,
            Source = DownloadSource.BmclApi,
        };

        await _install!.InstallAsync(request, progress, cancellationToken).ConfigureAwait(false);

        // InstallAsync 不返回版本名，marker 是唯一稳定依据（InstallService 每次安装成功后写）。
        var installed = FindInstalledVersionName(request);

        // 加载器版本没有标注时装的是「最新稳定版」，与整合包作者的原始组合大概率不一致——
        // 这在 PCL2 里是静默的，玩家装完崩了无从判断。这里必须说出来。
        var warning = loaderVersion is null && preview.Loader != LoaderKind.Vanilla
            ? $"整合包没有标注 {preview.Loader} 版本，已安装最新稳定版，可能与整合包的模组组合不匹配。"
            : null;

        if (installed is null)
        {
            _log?.Warn(
                $"未能确认 {DescribeTarget(preview)} 的安装目录名，改按命名规则推断父版本名");
        }

        var parentName = installed ?? FallbackVersionName(request);

        // Forge / NeoForge 的目录名带加载器构建号，没有 marker 就推不出来：父版本名无从确认时
        // 不写 inheritsFrom（写错比不写更难查），并把「实例版本不可启动」这件事说给用户。
        if (parentName is null)
        {
            const string unconfirmed = "未能确认游戏安装产出的版本目录名，整合包实例没有生成版本描述文件；"
                                       + "请到版本页重新安装一次后再启动。";
            warning = string.IsNullOrEmpty(warning) ? unconfirmed : warning + unconfirmed;
        }

        return (parentName, warning);
    }

    /// <summary>
    /// 反查安装产出的版本目录名：扫 <c>versions/*/nebula-loader.json</c>，
    /// 取 MC 版本与加载器都匹配、<c>installedAt</c> 最新的那条。
    /// </summary>
    private string? FindInstalledVersionName(InstallRequest request)
    {
        var root = _paths.VersionsDirectory;
        if (!Directory.Exists(root))
        {
            return null;
        }

        string? best = null;
        var bestAt = DateTimeOffset.MinValue;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var markerPath = Path.Combine(directory, "nebula-loader.json");
            if (!File.Exists(markerPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
                var root2 = document.RootElement;

                if (!string.Equals(GetString(root2, "minecraftVersion"), request.MinecraftVersion.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(GetString(root2, "loader"), request.Loader.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 指定了加载器版本时，marker 里的版本必须一致；marker 没记版本时按匹配处理。
                var markerLoaderVersion = GetString(root2, "loaderVersion");
                if (!string.IsNullOrWhiteSpace(request.LoaderVersion)
                    && !string.IsNullOrWhiteSpace(markerLoaderVersion)
                    && !string.Equals(markerLoaderVersion, request.LoaderVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var at = DateTimeOffset.TryParse(GetString(root2, "installedAt"), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

                if (at >= bestAt)
                {
                    bestAt = at;
                    best = Path.GetFileName(directory);
                }
            }
            catch (Exception ex)
            {
                // 坏 marker 只影响一次反查，不值得让整个导入失败。
                _log?.Warn($"读取加载器标记失败：{markerPath}", ex);
            }
        }

        return best;
    }

    /// <summary>marker 缺失时的兜底：原版与 Fabric/Quilt 的目录名规则是确定的。</summary>
    private static string? FallbackVersionName(InstallRequest request) => request.Loader switch
    {
        LoaderKind.Vanilla => request.MinecraftVersion.Trim(),
        LoaderKind.Fabric or LoaderKind.Quilt when !string.IsNullOrWhiteSpace(request.LoaderVersion) =>
            InstallService.BuildLoaderVersionName(request.Loader, request.MinecraftVersion.Trim(), request.LoaderVersion),
        _ => null,
    };

    /// <summary>
    /// 写实例版本自己的版本描述：<c>{ id, inheritsFrom, type, time, releaseTime, libraries: [], arguments: { game: [], jvm: [] } }</c>。
    ///
    /// 与 <see cref="InstallService"/> 写加载器 profile 是同一机制（<c>InstallService.cs:577-597</c>）：
    /// 版本 json 只描述自身增量，原版与加载器的文件全部交给 CmlLib 沿 <c>inheritsFrom</c> 链补全。
    /// 实测依据（CmlLib.Core 4.1.0）：
    /// <list type="bullet">
    /// <item><c>VersionMetadataCollection.inheritIfRequired</c> 按 <c>inheritsFrom</c> 找父版本的
    /// <c>versions/&lt;父版本&gt;/&lt;父版本&gt;.json</c> 并挂成 <c>ParentVersion</c>；</item>
    /// <item><c>MinecraftProcessBuilder</c> 用 <c>GetInheritedProperty</c> 取 MainClass / AssetIndex /
    /// Logging / JavaVersion，用 <c>ConcatInherited*Arguments</c> 与 <c>EnumerateToParent().Libraries</c>
    /// 拼参数与 classpath——自身留空即等于「全部继承」；</item>
    /// <item><c>MinecraftLauncher.InstallAsync</c> 对链上每一层跑一遍文件提取，所以空的
    /// <c>libraries</c> / <c>arguments</c> 不会挡住补全。</item>
    /// </list>
    ///
    /// 不写 <c>jar</c>：<c>MainJarId</c> 沿 <c>jar</c> 属性回溯，自身没写就落到自己的 id，
    /// CmlLib 会在首次启动前把 <c>downloads.client</c> 补到
    /// <c>versions/&lt;实例名&gt;/&lt;实例名&gt;.jar</c>——与 Forge 版本目录里那份 jar 的位置规则一致。
    ///
    /// 父版本 json 一个字都不复制：复制会让每个整合包多背一份原版 / 加载器描述，
    /// 版本升级后还会留下一份永远追不上的旧快照。
    /// </summary>
    private static async Task WriteInheritedVersionJsonAsync(
        string instanceDirectory,
        string versionName,
        string parentVersionName,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 匿名类型无法被 Source Generator 静态登记（裁剪发布下会被剪掉元数据），
        // 因此这里用一条显式的记录类型描述同一份 JSON 形状。
        var model = new InheritedVersionDocument(
            versionName,
            parentVersionName,
            "release",
            now,
            now,
            Array.Empty<object>(),
            new InheritedVersionArguments(Array.Empty<string>(), Array.Empty<string>()));

        await File.WriteAllTextAsync(
            Path.Combine(instanceDirectory, versionName + ".json"),
            JsonSerializer.Serialize(model, JsonOptions.Of<InheritedVersionDocument>()),
            cancellationToken).ConfigureAwait(false);
    }

    private static string DescribeTarget(ModpackPreview preview)
    {
        var loader = preview.Loader == LoaderKind.Vanilla
            ? "原版"
            : string.IsNullOrWhiteSpace(preview.LoaderVersion)
                ? $"{preview.Loader}（最新版）"
                : $"{preview.Loader} {preview.LoaderVersion}";

        return $"Minecraft {preview.MinecraftVersion} + {loader}";
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 删不掉就留着：至少日志里能看到半成品实例的真实路径，玩家可以手动删。
            _log?.Warn($"清理未完成的实例目录失败：{directory}", ex);
        }
    }

    // ---------------- 格式判定 ----------------

    /// <summary>
    /// 判定整合包格式。先看根目录，再看一级子目录（PCL2 <c>ModModpack.vb:41-83</c> 的同一判据）。
    ///
    /// 只看根与一级是刻意的：任意层级查找会把「普通压缩包里恰好有一个深层 manifest.json」
    /// 误判成整合包，解压时就只挑 <c>overrides/</c> 里的东西，用户得到半个包。
    /// </summary>
    private static (ModpackFormat Format, ZipArchiveEntry? Entry, string BaseFolder) DetectFormat(ZipArchive archive)
    {
        var entries = archive.Entries;

        foreach (var baseFolder in EnumerateBaseFolders(entries))
        {
            // 带启动器的分发包优先判：它的外层没有 manifest，但里面一定有一个 modpack.*。
            // 放在格式键之前，避免「外层恰好也有 manifest.json」时被误判成 CurseForge。
            foreach (var launcherPackName in LauncherPackNames)
            {
                var nested = FindAt(entries, baseFolder, launcherPackName);
                if (nested is not null)
                {
                    return (ModpackFormat.LauncherPack, nested, baseFolder);
                }
            }

            foreach (var key in FormatKeys)
            {
                var entry = FindAt(entries, baseFolder, key);
                if (entry is null)
                {
                    continue;
                }

                var format = key switch
                {
                    "mcbbs.packmeta" => ModpackFormat.McbbsZip,
                    "mmc-pack.json" => ModpackFormat.MultiMcZip,
                    "modrinth.index.json" => ModpackFormat.ModrinthModpack,
                    "modpack.json" => ModpackFormat.HmclZip,
                    _ => HasProperty(entry, "addons") ? ModpackFormat.McbbsZip : ModpackFormat.CurseForgeZip,
                };

                return (format, entry, baseFolder);
            }
        }

        return (ModpackFormat.PlainZip, null, string.Empty);
    }

    /// <summary>带启动器的分发包里那个「真正的整合包文件」（PCL2 <c>ModModpack.vb:56</c>）。</summary>
    private static readonly string[] LauncherPackNames = ["modpack.zip", "modpack.mrpack"];

    /// <summary>根目录在前，其次是一级子目录（去重、保序）。</summary>
    private static IEnumerable<string> EnumerateBaseFolders(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        yield return string.Empty;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = Normalize(entry.FullName);
            var slash = path.IndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            var folder = path[..(slash + 1)];
            if (seen.Add(folder))
            {
                yield return folder;
            }
        }
    }

    private static ZipArchiveEntry? FindAt(
        IReadOnlyCollection<ZipArchiveEntry> entries,
        string baseFolder,
        string fileName)
    {
        var wanted = (baseFolder + fileName).Replace('\\', '/');
        return entries.FirstOrDefault(entry =>
            string.Equals(Normalize(entry.FullName), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasProperty(ZipArchiveEntry entry, string property)
    {
        try
        {
            using var document = ParseJson(entry);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(property, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------------- 各格式解析 ----------------

    private static ModpackPreview ReadModrinth(ZipArchiveEntry indexEntry, IReadOnlyCollection<ZipArchiveEntry> entries, string fallbackName)
    {
        using var document = ParseJson(indexEntry);
        var root = document.RootElement;

        var name = GetString(root, "name") ?? fallbackName;
        var version = GetString(root, "versionId") ?? string.Empty;

        var minecraftVersion = string.Empty;
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        if (root.TryGetProperty("dependencies", out var dependencies)
            && dependencies.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in dependencies.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                switch (property.Name)
                {
                    case "minecraft":
                        minecraftVersion = value ?? string.Empty;
                        break;
                    case "fabric-loader":
                        loader = LoaderKind.Fabric;
                        loaderVersion = value;
                        break;
                    case "quilt-loader":
                        loader = LoaderKind.Quilt;
                        loaderVersion = value;
                        break;
                    case "forge":
                        loader = LoaderKind.Forge;
                        loaderVersion = value;
                        break;
                    case "neoforge":
                        loader = LoaderKind.NeoForge;
                        loaderVersion = value;
                        break;
                }
            }
        }

        var files = new List<string>();
        if (root.TryGetProperty("files", out var filesNode) && filesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesNode.EnumerateArray())
            {
                if (GetString(file, "path") is { Length: > 0 } path)
                {
                    files.Add(path);
                }
            }
        }

        return new ModpackPreview
        {
            Name = name,
            Version = version,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Format = ModpackFormat.ModrinthModpack,
            FileCount = entries.Count,
            TotalBytes = entries.Sum(entry => entry.Length),
            Files = files,
        };
    }

    private static ModpackPreview ReadMultiMc(ZipArchiveEntry packEntry, IReadOnlyCollection<ZipArchiveEntry> entries, string fallbackName)
    {
        using var document = ParseJson(packEntry);
        var root = document.RootElement;

        var minecraftVersion = string.Empty;
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                var uid = GetString(component, "uid") ?? string.Empty;
                var version = GetString(component, "version");
                if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    minecraftVersion = version ?? string.Empty;
                }
                else if (uid.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                {
                    loader = LoaderKind.Fabric;
                    loaderVersion = version;
                }
                else if (uid.Contains("quilt", StringComparison.OrdinalIgnoreCase))
                {
                    loader = LoaderKind.Quilt;
                    loaderVersion = version;
                }
                else if (uid.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
                {
                    loader = LoaderKind.NeoForge;
                    loaderVersion = version;
                }
                else if (uid.Contains("forge", StringComparison.OrdinalIgnoreCase))
                {
                    loader = LoaderKind.Forge;
                    loaderVersion = version;
                }
            }
        }

        return new ModpackPreview
        {
            Name = fallbackName,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Format = ModpackFormat.MultiMcZip,
            FileCount = entries.Count,
            TotalBytes = entries.Sum(entry => entry.Length),
            Files = entries.Take(200).Select(entry => entry.FullName).ToList(),
        };
    }

    private static ModpackPreview ReadCurseForge(
        ZipArchiveEntry manifestEntry,
        IReadOnlyCollection<ZipArchiveEntry> entries,
        string fallbackName,
        ModpackFormat format = ModpackFormat.CurseForgeZip)
    {
        using var document = ParseJson(manifestEntry);
        var root = document.RootElement;

        var minecraftVersion = string.Empty;
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        if (root.TryGetProperty("minecraft", out var minecraft))
        {
            minecraftVersion = GetString(minecraft, "version") ?? string.Empty;
            if (minecraft.TryGetProperty("modLoaders", out var modLoaders)
                && modLoaders.ValueKind == JsonValueKind.Array)
            {
                foreach (var modLoader in modLoaders.EnumerateArray())
                {
                    var id = GetString(modLoader, "id") ?? string.Empty;
                    var primary = modLoader.TryGetProperty("primary", out var primaryNode)
                                  && primaryNode.ValueKind == JsonValueKind.True;
                    var (kind, version) = ParseCurseLoader(id);
                    if (kind != LoaderKind.Vanilla && (primary || loader == LoaderKind.Vanilla))
                    {
                        loader = kind;
                        loaderVersion = version;
                    }
                }
            }
        }

        return new ModpackPreview
        {
            Name = GetString(root, "name") ?? fallbackName,
            Version = GetString(root, "version") ?? string.Empty,
            Author = GetString(root, "author"),
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Format = format,
            FileCount = entries.Count,
            TotalBytes = entries.Sum(entry => entry.Length),
            Files = entries.Take(200).Select(entry => entry.FullName).ToList(),
        };
    }

    /// <summary>
    /// MCBBS 整合包：<c>mcbbs.packmeta</c> 优先，缺失时退回结构同构的 <c>manifest.json</c>
    /// （PCL2 <c>ModModpack.vb:682</c> 同一回退）。
    ///
    /// <c>packmeta</c> 的 schema 没有正式文档，只按 PCL2 的读法取 <c>name</c> / <c>version</c>
    /// 与 <c>gameVersion</c>；解析不出就只保留 name，让 UI 降级提示。
    /// </summary>
    private static ModpackPreview ReadMcbbs(
        ZipArchiveEntry packMetaEntry,
        string baseFolder,
        IReadOnlyCollection<ZipArchiveEntry> entries,
        string fallbackName)
    {
        try
        {
            var fromPrimary = ReadCurseForge(packMetaEntry, entries, fallbackName, ModpackFormat.McbbsZip);
            if (fromPrimary.RequiresGameInstall)
            {
                return fromPrimary;
            }
        }
        catch (JsonException)
        {
        }

        // packmeta 本身给不出完整的版本/加载器组合，优先借用同包里的 manifest.json。
        var manifestEntry = FindAt(entries, baseFolder, "manifest.json");
        if (manifestEntry is not null && !ReferenceEquals(manifestEntry, packMetaEntry))
        {
            var fromManifest = ReadCurseForge(manifestEntry, entries, fallbackName, ModpackFormat.McbbsZip);
            if (fromManifest.RequiresGameInstall)
            {
                return fromManifest;
            }
        }

        var name = fallbackName;
        var version = string.Empty;
        var minecraftVersion = string.Empty;
        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        try
        {
            using var document = ParseJson(packMetaEntry);
            var root = document.RootElement;
            name = GetString(root, "name") ?? fallbackName;
            version = GetString(root, "version") ?? string.Empty;
            minecraftVersion = GetString(root, "gameVersion")
                               ?? GetString(root, "minecraftVersion")
                               ?? string.Empty;

            if (root.TryGetProperty("addons", out var addons))
            {
                foreach (var (id, componentVersion) in ReadMcbbsComponents(addons))
                {
                    if (string.IsNullOrWhiteSpace(componentVersion))
                    {
                        continue;
                    }

                    switch (id.ToLowerInvariant())
                    {
                        case "game":
                        case "minecraft":
                            minecraftVersion = componentVersion;
                            break;
                        case "forge":
                            loader = LoaderKind.Forge;
                            loaderVersion = componentVersion;
                            break;
                        case "neoforge":
                            loader = LoaderKind.NeoForge;
                            loaderVersion = componentVersion;
                            break;
                        case "fabric":
                        case "fabric-loader":
                            loader = LoaderKind.Fabric;
                            loaderVersion = componentVersion;
                            break;
                        case "quilt":
                        case "quilt-loader":
                            loader = LoaderKind.Quilt;
                            loaderVersion = componentVersion;
                            break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // packmeta 坏了不影响「当成普通压缩包解开」这条退路。
        }

        return new ModpackPreview
        {
            Name = name,
            Version = version,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Format = ModpackFormat.McbbsZip,
            FileCount = entries.Count,
            TotalBytes = entries.Sum(entry => entry.Length),
            Files = entries.Take(200).Select(entry => entry.FullName).ToList(),
        };
    }

    private static IEnumerable<(string Id, string? Version)> ReadMcbbsComponents(JsonElement addons)
    {
        if (addons.ValueKind == JsonValueKind.Array)
        {
            foreach (var addon in addons.EnumerateArray())
            {
                if (GetString(addon, "id") is { Length: > 0 } id)
                {
                    yield return (id, GetString(addon, "version"));
                }
            }
        }
        else if (addons.ValueKind == JsonValueKind.Object)
        {
            foreach (var addon in addons.EnumerateObject())
            {
                yield return (
                    addon.Name,
                    addon.Value.ValueKind == JsonValueKind.String
                        ? addon.Value.GetString()
                        : GetString(addon.Value, "version"));
            }
        }
    }

    /// <summary>
    /// HMCL 整合包（PCL2 <c>InstallPackHMCL</c>，<c>ModModpack.vb:494-520</c>）。
    ///
    /// 读 <c>gameVersion</c>——PCL2 只认这个字段，缺了就直接说「未提供游戏版本信息」。
    /// 加载器信息 HMCL 没有标准字段（<c>modLoader</c> / <c>mode</c> 都是派生工具自己的扩展），
    /// 所以按候选名容错读，读不到就只装原版并在导入结果里给出提示。
    /// </summary>
    private static ModpackPreview ReadHmcl(
        ZipArchiveEntry packEntry,
        IReadOnlyCollection<ZipArchiveEntry> entries,
        string fallbackName)
    {
        using var document = ParseJson(packEntry);
        var root = document.RootElement;

        var minecraftVersion = GetString(root, "gameVersion")
                               ?? GetString(root, "minecraftVersion")
                               ?? string.Empty;

        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;

        // launchInfo 里出现的字段名跨 HMCL 版本变过，按候选名逐个试。
        var launchInfo = root.TryGetProperty("launchInfo", out var info) && info.ValueKind == JsonValueKind.Object
            ? info
            : default;

        var loaderName = GetString(root, "modLoader")
                         ?? GetString(root, "mode")
                         ?? (launchInfo.ValueKind == JsonValueKind.Object ? GetString(launchInfo, "modLoader") : null);

        var parsed = ParseCurseLoaderishLoader(loaderName);
        if (parsed != LoaderKind.Vanilla)
        {
            loader = parsed;
            loaderVersion = GetString(root, "modLoaderVersion")
                            ?? GetString(root, "loaderVersion")
                            ?? (launchInfo.ValueKind == JsonValueKind.Object
                                ? GetString(launchInfo, "modLoaderVersion")
                                : null);
        }

        return new ModpackPreview
        {
            Name = GetString(root, "name") ?? fallbackName,
            Version = GetString(root, "version") ?? string.Empty,
            Author = GetString(root, "author"),
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Format = ModpackFormat.HmclZip,
            FileCount = entries.Count,
            TotalBytes = entries.Sum(entry => entry.Length),
            Files = entries.Take(200).Select(entry => entry.FullName).ToList(),
        };
    }

    /// <summary>HMCL 的加载器字段是自由文本（<c>fabric</c> / <c>Fabric</c> / <c>forge-47.1.0</c> 都见过）。</summary>
    private static LoaderKind ParseCurseLoaderishLoader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LoaderKind.Vanilla;
        }

        var lowered = value.Trim().ToLowerInvariant();
        if (lowered.Contains("neoforge"))
        {
            return LoaderKind.NeoForge;
        }

        if (lowered.Contains("quilt"))
        {
            return LoaderKind.Quilt;
        }

        if (lowered.Contains("fabric"))
        {
            return LoaderKind.Fabric;
        }

        if (lowered.Contains("forge"))
        {
            return LoaderKind.Forge;
        }

        return LoaderKind.Vanilla;
    }

    private static (LoaderKind Loader, string? Version) ParseCurseLoader(string id)
    {
        var separator = id.IndexOf('-');
        if (separator <= 0)
        {
            return (LoaderKind.Vanilla, null);
        }

        var prefix = id[..separator];
        var version = id[(separator + 1)..];
        return prefix switch
        {
            "forge" => (LoaderKind.Forge, version),
            "fabric" => (LoaderKind.Fabric, version),
            "quilt" => (LoaderKind.Quilt, version),
            "neoforge" => (LoaderKind.NeoForge, version),
            _ => (LoaderKind.Vanilla, null),
        };
    }

    // ---------------- 解压与下载 ----------------

    private void ExtractOverrides(
        string archivePath,
        ModpackFormat format,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var (_, _, baseFolder) = DetectFormat(archive);

        var prefixes = new List<string>
        {
            (baseFolder + OverrideFolderOf(format)).Replace('\\', '/'),
        };

        if (format == ModpackFormat.ModrinthModpack)
        {
            prefixes.Add((baseFolder + "client-overrides/").Replace('\\', '/'));
        }

        // HMCL 的覆盖层固定叫 minecraft/，但部分导出工具直接放文件；没有该目录时退回整包，
        // 结果是多解出几个元数据文件，比「什么都没解出来」好。
        if (format == ModpackFormat.HmclZip
            && prefixes[0].Length > 0
            && !archive.Entries.Any(entry => Normalize(entry.FullName).StartsWith(prefixes[0], StringComparison.OrdinalIgnoreCase)))
        {
            prefixes[0] = baseFolder.Replace('\\', '/');
        }

        foreach (var entry in archive.Entries.OrderBy(entry =>
                     format == ModpackFormat.ModrinthModpack
                     && Normalize(entry.FullName).StartsWith(baseFolder + "client-overrides/", StringComparison.OrdinalIgnoreCase)
                         ? 1
                         : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var normalized = Normalize(entry.FullName);
            string? relative = null;
            foreach (var prefix in prefixes)
            {
                if (prefix.Length == 0)
                {
                    relative = normalized;
                    break;
                }

                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relative = normalized[prefix.Length..];
                    break;
                }
            }

            if (relative is null)
            {
                continue;
            }

            if (relative.Length == 0)
            {
                continue;
            }

            var destination = ResolveSafeDestination(instanceDirectory, relative);
            if (destination is null)
            {
                _log?.Warn($"跳过非法路径的整合包条目：{entry.FullName}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>各格式的覆盖层目录名（相对整合包根或一级子目录）。</summary>
    private static string OverrideFolderOf(ModpackFormat format) => format switch
    {
        ModpackFormat.ModrinthModpack => "overrides/",
        ModpackFormat.CurseForgeZip => "overrides/",
        ModpackFormat.McbbsZip => "overrides/",
        ModpackFormat.MultiMcZip => ".minecraft/",

        // HMCL 是 minecraft/（没有前导点），PCL2 InstallPackHMCL:508 同一取值。
        ModpackFormat.HmclZip => "minecraft/",
        _ => string.Empty,
    };

    private IReadOnlyList<DownloadItem> ReadModrinthDownloads(string archivePath, string instanceDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var indexEntry = FindEntry(archive.Entries, "modrinth.index.json");
        if (indexEntry is null)
        {
            return Array.Empty<DownloadItem>();
        }

        using var document = ParseJson(indexEntry);
        if (!document.RootElement.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DownloadItem>();
        }

        var items = new List<DownloadItem>();
        foreach (var file in files.EnumerateArray())
        {
            if (IsClientUnsupported(file))
            {
                continue;
            }

            var path = GetString(file, "path");
            if (string.IsNullOrWhiteSpace(path) || !file.TryGetProperty("downloads", out var downloads)
                || downloads.ValueKind != JsonValueKind.Array || downloads.GetArrayLength() == 0)
            {
                continue;
            }

            var url = downloads[0].GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var destination = ResolveSafeDestination(instanceDirectory, path);
            if (destination is null)
            {
                _log?.Warn($"跳过非法下载路径：{path}");
                continue;
            }

            string? sha1 = null;
            if (file.TryGetProperty("hashes", out var hashes))
            {
                sha1 = GetString(hashes, "sha1");
            }

            long size = 0;
            if (file.TryGetProperty("fileSize", out var sizeNode) && sizeNode.TryGetInt64(out var parsedSize))
            {
                size = parsedSize;
            }

            items.Add(new DownloadItem
            {
                Url = url,
                DestinationPath = destination,
                Id = Path.GetFileName(destination),
                Group = "整合包",
                ExpectedSize = size,
                ExpectedSha1 = sha1,
            });
        }

        return items;
    }

    /// <summary>
    /// Modrinth 的 <c>env.client=unsupported</c> 文件只供服务端使用，不能下载进客户端实例。
    /// PCL2 在构造整合包下载清单时直接跳过这类条目；没有这个判断会把服务端专属模组
    /// 放进客户端，导致启动器导入成功但游戏加载阶段报错。
    /// </summary>
    private static bool IsClientUnsupported(JsonElement file)
    {
        if (!file.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return env.TryGetProperty("client", out var client)
               && client.ValueKind == JsonValueKind.String
               && string.Equals(client.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- MCBBS addons 反查 ----------------

    /// <summary>一次 addons 处理的结果。</summary>
    private readonly record struct AddonImportOutcome(
        int Total,
        int Downloaded,
        IReadOnlyList<ModpackPendingAddon> Pending)
    {
        public static AddonImportOutcome Empty { get; } = new(0, 0, Array.Empty<ModpackPendingAddon>());
    }

    /// <summary>反查成功、拿到直链的一项 addon。</summary>
    private sealed record ResolvedAddon(string Url, string FileName, long Size, string? Sha1);

    /// <summary>
    /// 逐项反查 <c>addons</c> 并下载。整个过程是「尽力而为」：任何一项失败只进待手动安装清单，
    /// 不抛给调用方（取消除外），导入结果仍是成功。
    ///
    /// 顺序与 PCL2 的 <c>InstallPackCurseForge</c>（<c>ModModpack.vb:162-360</c>）一致：
    /// 先按 CurseForge 的「项目 id + 文件 id」查元数据拿权威直链，查不到再问 Modrinth。
    /// 中间插了一步 forgecdn 直链推导——MCBBS 包几乎都引用 CurseForge 文件，
    /// 而官方元数据不给第三方分发直链（`downloadUrl` 为 null）时，只有这一步能把文件取下来。
    /// </summary>
    private async Task<AddonImportOutcome> ImportMcbbsAddonsAsync(
        string archivePath,
        ModpackPreview preview,
        string instanceDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (preview.Format is not (ModpackFormat.McbbsZip or ModpackFormat.CurseForgeZip))
        {
            return AddonImportOutcome.Empty;
        }

        IReadOnlyList<ModpackAddonReference> references;
        try
        {
            references = ReadMcbbsAddons(archivePath);
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取整合包 addons 失败：{ex.Message}");
            return AddonImportOutcome.Empty;
        }

        if (references.Count == 0)
        {
            return AddonImportOutcome.Empty;
        }

        var minecraftVersion = string.IsNullOrWhiteSpace(preview.MinecraftVersion) ? null : preview.MinecraftVersion.Trim();
        progress?.Report(new DownloadProgress(
            0,
            references.Count,
            0,
            0,
            0,
            0,
            $"正在解析 {references.Count} 个整合包附加内容…",
            0.85));

        var items = new List<DownloadItem>(references.Count);
        var planned = new Dictionary<string, ModpackAddonReference>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<ModpackPendingAddon>();
        var alreadyPresent = 0;

        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ResolvedAddon? resolved;
            try
            {
                resolved = await ResolveAddonAsync(reference, minecraftVersion, preview.Loader, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn($"反查整合包附加内容失败：{reference.DisplayName}，{ex.Message}");
                resolved = null;
            }

            if (resolved is null)
            {
                pending.Add(DescribePending(reference, "CurseForge 与 Modrinth 都没有查到可下载的文件"));
                continue;
            }

            var relative = ResolveAddonRelativePath(reference, resolved);
            var destination = string.IsNullOrWhiteSpace(relative)
                ? null
                : ResolveSafeDestination(instanceDirectory, relative);
            if (destination is null)
            {
                pending.Add(DescribePending(reference, "声明的目标路径非法，已跳过"));
                continue;
            }

            // 覆盖层已经带了同名文件：作者改过的版本优先，不覆盖。
            if (File.Exists(destination))
            {
                alreadyPresent++;
                continue;
            }

            items.Add(new DownloadItem
            {
                Url = resolved.Url,
                DestinationPath = destination,
                Id = Path.GetFileName(destination),
                Group = "整合包附加内容",
                ExpectedSize = resolved.Size,
                ExpectedSha1 = resolved.Sha1,
            });
            planned[destination] = reference;
        }

        var downloaded = alreadyPresent;

        if (items.Count > 0)
        {
            try
            {
                var batch = await _queue.DownloadAsync(items, progress, cancellationToken).ConfigureAwait(false);
                downloaded += batch.CompletedFiles;

                foreach (var failure in batch.Failures)
                {
                    var reference = planned.TryGetValue(failure.Item.DestinationPath, out var known) ? known : null;
                    pending.Add(reference is null
                        ? new ModpackPendingAddon
                        {
                            Name = failure.Item.Name,
                            Reason = $"下载失败：{failure.Error}",
                        }
                        : DescribePending(reference, $"下载失败：{failure.Error}"));
                }

                if (!batch.Success)
                {
                    _log?.Warn($"整合包附加内容有 {batch.FailedFiles} 个下载失败（共 {batch.TotalFiles} 个）");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 队列层面整体失败（例如安装批次占用）也算「这批没下成」，不是导入失败。
                _log?.Warn($"整合包附加内容下载批次失败：{ex.Message}");
                foreach (var item in items)
                {
                    var reference = planned.TryGetValue(item.DestinationPath, out var known) ? known : null;
                    pending.Add(reference is null
                        ? new ModpackPendingAddon { Name = item.Name, Reason = ex.Message }
                        : DescribePending(reference, ex.Message));
                }
            }
        }

        _log?.Info(
            $"MCBBS 附加内容：{references.Count} 个声明，{downloaded} 个已就位，{pending.Count} 个待手动安装");

        return new AddonImportOutcome(references.Count, downloaded, pending);
    }

    /// <summary>
    /// 把一条 addons 引用解析成可下载地址。三步依次尝试，任一步拿到可直连的地址就返回：
    /// <list type="number">
    /// <item>CurseForge 元数据端点——能拿到权威文件名、体积与 SHA1。</item>
    /// <item>forgecdn 直链推导——<c>fileId</c> + 文件名即可拼出，注释见 <see cref="CurseForgeCdn"/>。</item>
    /// <item>Modrinth——先按 id / slug 查版本，再用搜索兜底。</item>
    /// </list>
    /// </summary>
    private async Task<ResolvedAddon?> ResolveAddonAsync(
        ModpackAddonReference reference,
        string? minecraftVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        var tryCurseForge = reference.Source != ModpackAddonSource.Modrinth;
        var tryModrinth = reference.Source != ModpackAddonSource.CurseForge;

        long? wantedFileId = long.TryParse(reference.FileId, out var parsedFileId) ? parsedFileId : null;
        long? numericProjectId = long.TryParse(reference.ProjectId, out var parsedProjectId) ? parsedProjectId : null;

        // 1) CurseForge 元数据端点。
        if (tryCurseForge && numericProjectId is { } modId && _curseForge is not null)
        {
            var files = await _curseForge.GetFilesAsync(modId, minecraftVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            var match = wantedFileId is { } fileId
                ? files.FirstOrDefault(candidate => candidate.Id == fileId)
                : files.Count > 0 ? files[0] : null;

            if (match is { IsDownloadable: true } && match.DownloadUrl is { Length: > 0 } url)
            {
                return new ResolvedAddon(
                    url,
                    string.IsNullOrWhiteSpace(reference.FileName) ? match.FileName : reference.FileName!,
                    match.FileLength,
                    match.Sha1Available ? match.Sha1 : null);
            }
        }

        // 2) forgecdn 直链推导：只要 fileId 与文件名齐备就能拼。
        if (tryCurseForge
            && wantedFileId is { } cdnFileId
            && !string.IsNullOrWhiteSpace(reference.FileName))
        {
            return new ResolvedAddon(
                CurseForgeCdn.BuildUrl(cdnFileId, reference.FileName!),
                reference.FileName!,
                0,
                null);
        }

        // 3) Modrinth：projectId 可以是 base62 id，也可以是 slug。
        if (tryModrinth && _modrinth is not null)
        {
            var direct = await TryResolveOnModrinthAsync(
                reference,
                new[] { reference.ProjectId },
                minecraftVersion,
                loader,
                cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }

            // 搜索兜底：addons 里的 id 有时是项目名 / 旧 slug，直接查版本会 404。
            var searched = await _modrinth.SearchAsync(
                reference.Kind,
                reference.ProjectId,
                minecraftVersion,
                loader,
                0,
                5,
                cancellationToken).ConfigureAwait(false);

            var candidates = searched
                .Select(project => project.ProjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length > 0)
            {
                return await TryResolveOnModrinthAsync(reference, candidates, minecraftVersion, loader, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>按一组候选 Modrinth 项目 id 依次查版本，取第一个能给出直链的。</summary>
    private async Task<ResolvedAddon?> TryResolveOnModrinthAsync(
        ModpackAddonReference reference,
        IReadOnlyList<string> projectIds,
        string? minecraftVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        if (_modrinth is null)
        {
            return null;
        }

        foreach (var projectId in projectIds)
        {
            var versions = await _modrinth.GetVersionsAsync(projectId, minecraftVersion, loader, cancellationToken)
                .ConfigureAwait(false);

            if (versions.Count == 0)
            {
                continue;
            }

            var match = !string.IsNullOrWhiteSpace(reference.FileId)
                ? versions.FirstOrDefault(version =>
                    string.Equals(version.Id, reference.FileId, StringComparison.OrdinalIgnoreCase))
                : versions[0];

            if (match is null || string.IsNullOrWhiteSpace(match.DownloadUrl))
            {
                continue;
            }

            return new ResolvedAddon(
                match.DownloadUrl,
                string.IsNullOrWhiteSpace(reference.FileName) ? match.FileName : reference.FileName!,
                match.FileSize,
                string.IsNullOrWhiteSpace(match.Sha1) ? null : match.Sha1);
        }

        return null;
    }

    /// <summary>addons 声明的目标路径优先；没有则按内容类型落到惯例子目录。</summary>
    private static string? ResolveAddonRelativePath(ModpackAddonReference reference, ResolvedAddon resolved)
    {
        if (!string.IsNullOrWhiteSpace(reference.TargetPath))
        {
            return reference.TargetPath!.Replace('\\', '/').TrimStart('/');
        }

        var fileName = string.IsNullOrWhiteSpace(reference.FileName) ? resolved.FileName : reference.FileName!;
        var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return null;
        }

        return $"{AddonDirectoryOf(reference.Kind)}/{safeName}";
    }

    /// <summary>
    /// 内容类型 → 实例内的落盘子目录。
    ///
    /// internal 而非 private：整合包这条链路的判据（插件必须落 <c>plugins/</c> 而不是 <c>mods/</c>）
    /// 是测试要钉的硬约束，走 <see cref="Parse"/> 只能验到类型、验不到目录。
    /// </summary>
    internal static string AddonDirectoryOf(ContentKind kind) => kind switch
    {
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        ContentKind.DataPack => "datapacks",
        ContentKind.World => "saves",

        // 服务端插件落到 plugins/ 而不是 mods/。
        //
        // 与「搜索安装」的拒绝策略有意不同：那边是一个个挑，拒绝是对的（用户能改主意去服务端装）；
        // 整合包是批量还原别人的环境，逐个拒绝会让整包残缺，用户还得自己找齐。放 plugins/ 是
        // 「不装进客户端、也不丢内容」的中间态——客户端不会加载它，服务端目录可以直接拷走用。
        ContentKind.Plugin => "plugins",

        _ => "mods",
    };

    private static ModpackPendingAddon DescribePending(ModpackAddonReference reference, string reason) => new()
    {
        Name = reference.DisplayName,
        ProjectId = reference.ProjectId,
        FileId = reference.FileId,
        Reason = reason,
        WebUrl = WebUrlOf(reference),
    };

    /// <summary>手动下载入口。数字项目 id 视为 CurseForge，其余视为 Modrinth 的 slug。</summary>
    internal static string? WebUrlOf(ModpackAddonReference reference)
    {
        var numericProject = reference.HasNumericProjectId;
        if (reference.Source == ModpackAddonSource.CurseForge || (reference.Source == ModpackAddonSource.Auto && numericProject))
        {
            return string.IsNullOrWhiteSpace(reference.FileId)
                ? $"https://www.curseforge.com/projects/{reference.ProjectId}"
                : $"https://www.curseforge.com/projects/{reference.ProjectId}/files/{reference.FileId}";
        }

        if (string.IsNullOrWhiteSpace(reference.ProjectId))
        {
            return null;
        }

        var section = reference.Kind switch
        {
            ContentKind.ResourcePack => "resourcepack",
            ContentKind.ShaderPack => "shader",
            ContentKind.DataPack => "datapack",

            // Modrinth 的插件详情页在 /plugin/ 下，而 API 的 project_type 仍是 mod。
            // 拼成 /mod/ 会落到「找不到」页面，用户在「手动下载」里点进去就是死链。
            ContentKind.Plugin => "plugin",
            _ => "mod",
        };

        return string.IsNullOrWhiteSpace(reference.FileId)
            ? $"https://modrinth.com/{section}/{reference.ProjectId}"
            : $"https://modrinth.com/{section}/{reference.ProjectId}/version/{reference.FileId}";
    }

    /// <summary>
    /// 从整合包里读出 <c>addons</c> 引用。先 <c>mcbbs.packmeta</c>，再同目录的 <c>manifest.json</c>
    /// （PCL2 <c>ModModpack.vb:682</c> 同一回退：<c>If(GetEntry(packmeta), GetEntry(manifest.json))</c>）。
    ///
    /// 解析不出任何可下载项时返回空集合，不抛异常——包里只有 <c>game</c>/<c>forge</c> 两个 addon
    /// 也是合法形态（那说明模组都在 <c>overrides/</c> 里）。
    /// </summary>
    internal IReadOnlyList<ModpackAddonReference> ReadMcbbsAddons(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var (_, detected, baseFolder) = DetectFormat(archive);

        var candidates = new List<ZipArchiveEntry>(2);
        foreach (var name in new[] { "mcbbs.packmeta", "manifest.json" })
        {
            var entry = FindAt(archive.Entries, baseFolder, name) ?? FindEntry(archive.Entries, name);
            if (entry is not null && !candidates.Contains(entry))
            {
                candidates.Add(entry);
            }
        }

        if (detected is not null && !candidates.Contains(detected))
        {
            candidates.Insert(0, detected);
        }

        foreach (var entry in candidates)
        {
            try
            {
                using var document = ParseJson(entry);
                var parsed = McbbsAddonParser.Parse(document.RootElement);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch (JsonException ex)
            {
                _log?.Warn($"解析 {entry.FullName} 失败：{ex.Message}");
            }
        }

        return Array.Empty<ModpackAddonReference>();
    }

    // ---------------- 通用辅助 ----------------

    /// <summary>
    /// 版本名 → 实例目录。实例目录就是版本目录（与 PCL2 / HMCL 的版本隔离一致），
    /// 不另建 <c>&lt;data&gt;/instances</c> 目录。
    /// </summary>
    private string ResolveInstanceDirectory(string versionName)
    {
        var safeName = SanitizeDirectoryName(versionName);
        return Path.Combine(_paths.VersionsDirectory, safeName);
    }

    /// <summary>
    /// 整合包实例名 → <c>versions/</c> 下不撞车的版本目录名。
    ///
    /// 版本目录 = 实例目录（PCL2 <c>ModModpack.vb:37</c> 的 <c>versions\{InstanceName}</c>），
    /// 所以两次导入同名整合包不能共用目录——那会覆盖前一个实例的 <c>instance.json</c> 与文件。
    /// PCL2 让用户重新输入名字，这里自动加序号（与资源管理器、<c>InstanceService.MakeUniqueVersionName</c>
    /// 的重名策略一致）：<c>名字</c> → <c>名字 (2)</c> → <c>名字 (3)</c> …
    ///
    /// 占用判据同时看目录与同名 json：<c>versions/&lt;名字&gt;.json</c>（单文件版本）一写下去，
    /// <c>versions/&lt;名字&gt;/</c> 这个目录名就被 CmlLib 与 <c>VersionService</c> 认成同一个版本。
    /// Windows 大小写不敏感由 <see cref="Directory.Exists"/> / <see cref="File.Exists"/> 兜住。
    /// </summary>
    private string ResolveUniqueVersionName(string instanceName)
    {
        var baseName = SanitizeDirectoryName(instanceName);

        if (!IsVersionNameTaken(baseName))
        {
            return baseName;
        }

        // 上限与 InstanceService.MakeUniqueVersionName 一致：撞到 10000 个同名版本已经不是
        // 「重名」而是磁盘被批量造过，落到 GUID 后缀保证还能导入。
        for (var suffix = 2; suffix < 10000; suffix++)
        {
            var candidate = AppendSuffix(baseName, $" ({suffix})");

            if (!IsVersionNameTaken(candidate))
            {
                return candidate;
            }
        }

        return AppendSuffix(baseName, $"-{Guid.NewGuid().ToString("N")[..8]}");
    }

    /// <summary>
    /// 追加后缀但不越过单段的长度上限（<see cref="NebulaLauncher.Core.Paths.PathSafety.MaxSegmentLength"/>）。
    ///
    /// 名字本身已经顶到 100 字符时，直接拼 <c> (2)</c> 会让
    /// <see cref="ResolveInstanceDirectory"/> 里的第二次清洗把整个后缀截掉，
    /// 于是又落回原名、写进别人的目录——唯一化就白做了。
    /// </summary>
    private static string AppendSuffix(string baseName, string suffix)
    {
        var room = NebulaLauncher.Core.Paths.PathSafety.MaxSegmentLength - suffix.Length;

        if (baseName.Length <= room)
        {
            return baseName + suffix;
        }

        var stem = baseName[..Math.Max(room, 1)].TrimEnd('.', ' ');

        return stem.Length == 0 ? baseName + suffix : stem + suffix;
    }

    private bool IsVersionNameTaken(string versionName)
    {
        var directory = Path.Combine(_paths.VersionsDirectory, versionName);

        return Directory.Exists(directory) || File.Exists(directory + ".json");
    }

    private static string SanitizeDirectoryName(string name) =>
        NebulaLauncher.Core.Paths.PathSafety.SanitizeDirectoryName(name, "modpack");

    /// <summary>
    /// 整合包格式 → <see cref="ManagedPackInfo.Type"/> 的取值。
    /// 与 <c>ManagedPackInfo</c> 注释里约定的 <c>modrinth</c> / <c>curseforge</c> / <c>mmc</c> / <c>zip</c> 对齐。
    /// </summary>
    private static string ManagedPackTypeOf(ModpackFormat format) => format switch
    {
        ModpackFormat.ModrinthModpack => "modrinth",
        ModpackFormat.CurseForgeZip => "curseforge",
        ModpackFormat.MultiMcZip => "mmc",
        ModpackFormat.HmclZip => "hmcl",
        ModpackFormat.McbbsZip => "mcbbs",
        _ => "zip",
    };

    private static ZipArchiveEntry? FindEntry(
        IReadOnlyCollection<ZipArchiveEntry> entries,
        string fileName)
    {
        return entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), fileName, StringComparison.OrdinalIgnoreCase))
               ?? entries.FirstOrDefault(entry =>
                   string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static JsonDocument ParseJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ResolveSafeDestination(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? destination : null;
    }
}

/// <summary>
/// MCBBS 整合包的 <c>addons</c>（以及与之同构的 CurseForge <c>files[]</c>）解析。
///
/// 独立成类型而不是 <see cref="ModpackService"/> 的私有方法：它是纯函数（JSON 进、引用出），
/// 不碰网络也不碰文件系统，单测直接喂 JSON 文本就能覆盖「字段缺失 / 单条 / 多条 / 未知格式」。
///
/// 字段名宽容是刻意的：<c>mcbbs.packmeta</c> 没有正式 schema，PCL2 只读 <c>id</c> / <c>version</c>
/// （<c>ModModpack.vb:714</c>），而 CurseForge 同构的 <c>files[]</c> 写的是
/// <c>projectID</c> / <c>fileID</c> / <c>fileName</c>（PCL2 <c>InstallPackCurseForge</c>:162-360）。
/// 两套键名都认，且数组与字典两种容器形态都接受。
/// </summary>
internal static class McbbsAddonParser
{
    /// <summary><c>addons</c> 里表示游戏本体或加载器的 id：它们不是可下载内容。</summary>
    private static readonly string[] LoaderIds =
    [
        "game", "minecraft", "forge", "neoforge", "fabric", "quilt",
        "optifine", "liteloader", "legacy", "cleanroom",
    ];

    /// <summary>项目标识的候选键，按优先级排列（<c>projectID</c> 比 <c>id</c> 更可信）。</summary>
    private static readonly string[] ProjectKeys =
    [
        "projectID", "projectId", "project_id", "modId", "modID", "mod_id", "addonId", "addon_id", "slug", "id",
    ];

    private static readonly string[] FileKeys =
    [
        "fileID", "fileId", "file_id", "versionId", "versionID", "version_id", "version", "file",
    ];

    private static readonly string[] FileNameKeys =
    [
        "fileName", "filename", "file_name", "targetFileName",
    ];

    private static readonly string[] PathKeys =
    [
        "path", "target", "targetPath", "dest", "destination",
    ];

    private static readonly string[] KindKeys =
    [
        "type", "kind", "category", "contentType",
    ];

    /// <summary>
    /// 分类 / 加载器数组的候选键，用来判「这条是不是服务端插件」。
    ///
    /// CurseForge 与 MCBBS 的导出工具都可能写这几项，里面出现 <c>bukkit</c> / <c>paper</c> 之类的
    /// 加载器就说明它只能跑在服务端。单值（标量）形式由 <see cref="KindKeys"/> 覆盖。
    /// </summary>
    private static readonly string[] HintKeys =
    [
        "loaders", "categories", "tags",
    ];

    /// <summary>
    /// 声明类型 / 路径里表示「服务端插件」的词，与
    /// <see cref="ModrinthService.ResolveProjectKind"/> 的加载器词表同源
    /// （PCL2 <c>ResourceVersion.vb:189-195</c>），搜索侧与整合包侧必须判成同一类。
    /// </summary>
    private static readonly string[] PluginHints =
    [
        "plugin", "bukkit", "folia", "paper", "purpur", "spigot",
    ];

    private static readonly string[] SourceKeys =
    [
        "source", "platform", "provider", "repository",
    ];

    private static readonly string[] SourcePrefixes = ["curseforge", "cf", "modrinth", "mr"];

    private static readonly IReadOnlyList<ModpackAddonReference> Empty = Array.Empty<ModpackAddonReference>();

    /// <summary>从整合包 JSON 的根对象里取出可下载的 addon 引用。解析不出任何一项时返回空集合。</summary>
    public static IReadOnlyList<ModpackAddonReference> Parse(JsonElement manifestRoot)
    {
        if (manifestRoot.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        if (TryGetProperty(manifestRoot, "addons", out var addons)
            && addons.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            return ParseContainer(addons);
        }

        // CurseForge 风格：MCBBS 的导出工具把同一份内容写成 files[] 的情况也见过。
        if (TryGetProperty(manifestRoot, "files", out var files)
            && files.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            return ParseContainer(files);
        }

        return Empty;
    }

    private static List<ModpackAddonReference> ParseContainer(JsonElement container)
    {
        var results = new List<ModpackAddonReference>();

        if (container.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in container.EnumerateArray())
            {
                // 字符串数组之类没有项目标识的形态：跳过，不当作错误。
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var reference = ParseEntry(item, nameHint: null);
                if (reference is not null)
                {
                    results.Add(reference);
                }
            }

            return results;
        }

        // 字典形态：[{...}] 的另一半写法是 {"jei": "15.2.0.27", "forge": "47.1.0"}。
        foreach (var property in container.EnumerateObject())
        {
            var reference = property.Value.ValueKind switch
            {
                JsonValueKind.Object => ParseEntry(property.Value, property.Name),
                JsonValueKind.String or JsonValueKind.Number => ParseNameVersionPair(property.Name, Scalar(property.Value)),
                _ => null,
            };

            if (reference is not null)
            {
                results.Add(reference);
            }
        }

        return results;
    }

    private static ModpackAddonReference? ParseNameVersionPair(string name, string? version) =>
        ParseEntry(default, name, version);

    private static ModpackAddonReference? ParseEntry(JsonElement element, string? nameHint, string? fallbackFileId = null)
    {
        var hasElement = element.ValueKind == JsonValueKind.Object;

        var projectId = hasElement ? ScalarAny(element, ProjectKeys) : null;
        projectId = string.IsNullOrWhiteSpace(projectId) ? nameHint : projectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var (prefixSource, normalizedId) = SplitSourcePrefix(projectId.Trim());
        if (string.IsNullOrWhiteSpace(normalizedId) || IsLoaderId(normalizedId))
        {
            return null;
        }

        var fileId = hasElement ? ScalarAny(element, FileKeys) : null;
        if (string.IsNullOrWhiteSpace(fileId))
        {
            fileId = fallbackFileId;
        }

        var fileName = hasElement ? ScalarAny(element, FileNameKeys) : null;
        var targetPath = hasElement ? ScalarAny(element, PathKeys) : null;
        var kind = KindOf(
            hasElement ? ScalarAny(element, KindKeys) : null,
            targetPath,
            hasElement ? ListAny(element, HintKeys) : null);
        var declaredSource = hasElement ? SourceOf(ScalarAny(element, SourceKeys)) : ModpackAddonSource.Auto;

        return new ModpackAddonReference
        {
            ProjectId = normalizedId,
            FileId = string.IsNullOrWhiteSpace(fileId) ? null : fileId.Trim(),
            FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim(),
            TargetPath = string.IsNullOrWhiteSpace(targetPath) ? null : targetPath.Trim(),
            Kind = kind,
            Source = declaredSource != ModpackAddonSource.Auto ? declaredSource : prefixSource,
        };
    }

    /// <summary>拆 <c>curseforge:238222</c> / <c>modrinth:jei</c> 这类带来源前缀的 id。</summary>
    private static (ModpackAddonSource Source, string Id) SplitSourcePrefix(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return (ModpackAddonSource.Auto, value);
        }

        var prefix = value[..separator].Trim().ToLowerInvariant();
        if (!SourcePrefixes.Contains(prefix, StringComparer.Ordinal))
        {
            return (ModpackAddonSource.Auto, value);
        }

        var source = prefix is "modrinth" or "mr" ? ModpackAddonSource.Modrinth : ModpackAddonSource.CurseForge;
        return (source, value[(separator + 1)..].Trim());
    }

    private static ModpackAddonSource SourceOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ModpackAddonSource.Auto;
        }

        var lowered = value.Trim().ToLowerInvariant();
        if (lowered.Contains("modrinth", StringComparison.Ordinal))
        {
            return ModpackAddonSource.Modrinth;
        }

        return lowered.Contains("curse", StringComparison.Ordinal) ? ModpackAddonSource.CurseForge : ModpackAddonSource.Auto;
    }

    private static bool IsLoaderId(string id) =>
        LoaderIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 推断 addons 条目的内容类型。三个来源依次是：声明的 type/kind/category（标量）、
    /// 声明的落点路径、<c>loaders</c>/<c>categories</c> 数组。
    ///
    /// 插件**必须最先判**：漏判的后果不是「显示不准」而是装进客户端 <c>mods/</c> 后启动直接崩
    /// （Bukkit 插件调用服务端才有的类）。顺序与 <see cref="ModrinthService.ResolveProjectKind"/>
    /// 一致，插件词表也复用它的判定，避免搜索侧与整合包侧判成两种类型。
    /// </summary>
    private static ContentKind KindOf(string? declared, string? targetPath, IReadOnlyList<string>? hints = null)
    {
        var hint = (declared ?? string.Empty).Trim().ToLowerInvariant();

        if (ContainsAnyPluginHint(hint)
            || ModrinthService.ResolveProjectKind(ContentKind.Mod, hints) == ContentKind.Plugin)
        {
            return ContentKind.Plugin;
        }

        if (hint.Length > 0)
        {
            if (hint.Contains("shader", StringComparison.Ordinal))
            {
                return ContentKind.ShaderPack;
            }

            if (hint.Contains("resource", StringComparison.Ordinal) || hint.Contains("texture", StringComparison.Ordinal))
            {
                return ContentKind.ResourcePack;
            }

            if (hint.Contains("datapack", StringComparison.Ordinal))
            {
                return ContentKind.DataPack;
            }

            if (hint.Contains("world", StringComparison.Ordinal) || hint.Contains("save", StringComparison.Ordinal))
            {
                return ContentKind.World;
            }
        }

        // 没声明类型时看落点目录：addons 给了 path 就足够判断。
        var path = (targetPath ?? string.Empty).Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        return path switch
        {
            _ when path.StartsWith("plugins/", StringComparison.Ordinal) => ContentKind.Plugin,
            _ when path.StartsWith("shaderpacks/", StringComparison.Ordinal) => ContentKind.ShaderPack,
            _ when path.StartsWith("resourcepacks/", StringComparison.Ordinal) => ContentKind.ResourcePack,
            _ when path.StartsWith("datapacks/", StringComparison.Ordinal) => ContentKind.DataPack,
            _ when path.StartsWith("saves/", StringComparison.Ordinal) => ContentKind.World,
            _ => ContentKind.Mod,
        };
    }

    private static bool ContainsAnyPluginHint(string hint) =>
        hint.Length > 0
        && Array.Exists(PluginHints, word => hint.Contains(word, StringComparison.Ordinal));

    private static string? ScalarAny(JsonElement element, string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 外层按候选键的优先级走：同一条目里既有 projectID 又有 id 时，前者胜出。
        foreach (var key in keys)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(key, property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = Scalar(property.Value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 把 <c>loaders</c> / <c>categories</c> 这类「字符串数组」读成列表。
    ///
    /// 标量也接受：<c>"loaders": "paper"</c> 与 <c>"loaders": ["paper"]</c> 在导出工具里都见过，
    /// 只认数组会把单值写法漏掉，而漏判插件的后果是装进客户端 <c>mods/</c> 后启动崩。
    /// 对象 / null / 空数组一律当作缺失（返回 null），交给下一个判据。
    /// </summary>
    private static List<string>? ListAny(JsonElement element, string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 与 ScalarAny 同一套优先级：外层按候选键走，同一个键命中就不再往下找。
        foreach (var key in keys)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(key, property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<string>();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        var text = Scalar(item);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            items.Add(text.Trim());
                        }
                    }

                    if (items.Count > 0)
                    {
                        return items;
                    }

                    continue;
                }

                var single = Scalar(property.Value);
                if (!string.IsNullOrWhiteSpace(single))
                {
                    return [single.Trim()];
                }
            }
        }

        return null;
    }

    /// <summary>把字符串或数字读成一个字符串；对象 / 数组 / null 一律当作缺失。</summary>
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// 实例自己那份版本描述（<c>versions/&lt;实例&gt;/&lt;实例&gt;.json</c>）。
/// 只描述增量，原版与加载器全部沿 <c>inheritsFrom</c> 继承。
/// </summary>
/// <remarks>
/// 用记录类型而不是匿名类型，是因为裁剪发布下 Source Generator 必须在编译期看到确切的类型；
/// 属性名与 JSON 字段名一一对应（这里刻意用 PascalCase，由 camelCase 命名策略写出）。
/// </remarks>
internal sealed record InheritedVersionDocument(
    string Id,
    string InheritsFrom,
    string Type,
    DateTimeOffset Time,
    DateTimeOffset ReleaseTime,
    IReadOnlyList<object> Libraries,
    InheritedVersionArguments Arguments);

/// <summary>版本描述里 <c>arguments</c> 的两条空数组：全部继承父版本。</summary>
internal sealed record InheritedVersionArguments(
    IReadOnlyList<string> Game,
    IReadOnlyList<string> Jvm);

/// <summary>
/// 整合包链路用到的 JSON 元数据：<c>instance.json</c> 与实例自写的版本描述。
/// </summary>
[JsonSerializable(typeof(InstanceInfo))]
[JsonSerializable(typeof(InheritedVersionDocument))]
internal sealed partial class ModpackJsonContext : JsonSerializerContext
{
}
