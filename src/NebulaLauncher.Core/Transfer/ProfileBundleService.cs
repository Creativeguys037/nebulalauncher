using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Transfer;

/// <summary>配置包内的固定文件名与版本号。导入端只按这些名字取条目，不做任何解压到目录的操作。</summary>
public static class ProfileBundleFiles
{
    public const string Manifest = "manifest.json";

    public const string Favorites = "favorites.json";

    public const string Servers = "servers.json";

    public const string Settings = "settings.json";

    /// <summary>导出文件名前缀，后面拼 <c>yyyyMMdd-HHmmss</c>。</summary>
    public const string FilePrefix = "nebula-profile-";

    /// <summary>当前写出的 manifest 版本。读到更高的版本必须拒绝，而不是猜着读。</summary>
    public const int ManifestVersion = 1;

    /// <summary>manifest 里的包类型标识，用来挡住「随便挑一个 zip 来导入」。</summary>
    public const string Kind = "nebula-profile";
}

/// <summary>
/// 导出时从 <c>settings.json</c> 里抹掉的字段。
///
/// 两类：
/// 1. **凭据**：<see cref="AppSettings.LastAccountId"/>，
///    以及代理的 <see cref="AppSettings.ProxyPassword"/> / <see cref="AppSettings.ProxyUserName"/>——
///    前者能把包与某个账户关联起来，代理凭据则是明文口令。
///    （CurseForge 的 API key 已内置在程序里、不再是用户配置，这条脱敏项随字段一起删除。）
/// 2. **机器相关**：<see cref="AppSettings.MinecraftDirectory"/>、<see cref="AppSettings.JavaPath"/>、
///    <see cref="AppSettings.LastInstanceId"/>、<see cref="AppSettings.ProxyAddress"/>——在新机器上不是路径
///    不存在就是把游戏指到别人盘里；代理地址同理（另一台机器往往没有同一个代理）。
///
/// 键名是 camelCase，与 <c>SettingsService</c> 的 <c>JsonSerializerDefaults.Web</c> 一致。
/// </summary>
public static class ProfileSettingsRedaction
{
    public static IReadOnlyList<string> Fields { get; } =
    [
        "lastAccountId",
        "lastInstanceId",
        "minecraftDirectory",
        "javaPath",
        "proxyAddress",
        "proxyUserName",
        "proxyPassword",
        // 密文必须一起剥：DPAPI 密文换机器解不开（等于把无用的东西带过去），
        // 而退化的 b64 串换个账户就能直接解出明文——比明文更危险，因为它看起来像密文。
        "proxyPasswordProtected",
        // 背景图路径是绝对路径，会把本机用户名与目录结构带进用户会分享出去的 zip
        // （REVIEW-R43-R51 P3-8）；遮罩强度是跨机成立的偏好，不剥。
        "backgroundImagePath",
    ];
}

/// <summary>
/// 导入设置时**唯一**允许覆盖的键。
///
/// 原则：只放「换个机器也成立」的偏好。游戏目录、Java 路径、内存上限、窗口尺寸、
/// 账户相关一律不放——这些在目标机器上要么无效，要么会顶掉用户已经调好的值。
///
/// 注意这里是按**包里是否出现该键**逐个判定，而不是「整份 settings.json 覆盖」：
/// 否则包里缺的字段会被反序列化默认值（例如 <c>enableAnimations = true</c>）悄悄写进本地。
/// </summary>
public static class ProfileSettingsWhitelist
{
    /// <summary>白名单键（camelCase）。顺序即 manifest 里的展示顺序。</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "theme",
        "windowMaterial",
        "enableAnimations",
        "enableBackgroundEffects",
        "downloadSource",
        "downloadThreads",
        "downloadSpeedLimitKb",
        "jvmPresets",
        "activeJvmPresetId",
    ];

    /// <summary>按包内实际存在的键把值覆盖到 <paramref name="target"/>，返回覆盖了几个键。</summary>
    public static int Apply(AppSettings target, JsonObject source, AppSettings incoming)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(incoming);

        var applied = 0;

        if (source.ContainsKey("theme"))
        {
            target.Theme = incoming.Theme;
            applied++;
        }

        if (source.ContainsKey("windowMaterial"))
        {
            target.WindowMaterial = incoming.WindowMaterial;
            applied++;
        }

        if (source.ContainsKey("enableAnimations"))
        {
            target.EnableAnimations = incoming.EnableAnimations;
            applied++;
        }

        if (source.ContainsKey("enableBackgroundEffects"))
        {
            target.EnableBackgroundEffects = incoming.EnableBackgroundEffects;
            applied++;
        }

        if (source.ContainsKey("downloadSource"))
        {
            target.DownloadSource = incoming.DownloadSource;
            applied++;
        }

        if (source.ContainsKey("downloadThreads"))
        {
            target.DownloadThreads = incoming.DownloadThreads;
            applied++;
        }

        if (source.ContainsKey("downloadSpeedLimitKb"))
        {
            target.DownloadSpeedLimitKb = incoming.DownloadSpeedLimitKb;
            applied++;
        }

        // JVM 预设是引用类型：必须深拷贝，否则导入后的设置页会与包里的对象共用同一个 List。
        if (source.ContainsKey("jvmPresets"))
        {
            target.JvmPresets = incoming.JvmPresets is null
                ? new List<JvmPreset>()
                : incoming.JvmPresets.Select(preset => preset.Clone()).ToList();
            applied++;
        }

        if (source.ContainsKey("activeJvmPresetId"))
        {
            target.ActiveJvmPresetId = incoming.ActiveJvmPresetId;
            applied++;
        }

        return applied;
    }

    /// <summary>包内命中的白名单键（用于导出计数与摘要展示）。</summary>
    public static IReadOnlyList<string> Present(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Keys.Where(source.ContainsKey).ToList();
    }
}

/// <summary>manifest.json 的结构。字段名以 camelCase 落盘，导入端按同一套选项读回。</summary>
internal sealed class ProfileManifest
{
    public int BundleVersion { get; set; }

    public string Kind { get; set; } = ProfileBundleFiles.Kind;

    public DateTimeOffset ExportedAt { get; set; }

    public string LauncherVersion { get; set; } = "unknown";

    public List<string> Files { get; set; } = [];

    public List<string> RedactedFields { get; set; } = [];

    public List<string> SettingsWhitelist { get; set; } = [];

    public int FavoriteCount { get; set; }

    public int HiddenCount { get; set; }

    public int ServerCount { get; set; }

    /// <summary>恒为 false。写进包是给导入方一个显式承诺，而不是靠「读代码确认没带账户」。</summary>
    public bool ContainsAccounts { get; set; }

    public string Note { get; set; } = "只包含收藏、服务器与白名单设置；不含账户、令牌、日志、崩溃报告。";
}

/// <summary>
/// 配置包的导出与导入。
///
/// 设计取舍：
/// 1. **导出白名单，而不是黑名单**：包内只可能出现 manifest 里列出的四个文件。
///    <c>accounts.json</c>、日志、崩溃报告根本没有被读的代码路径，所以「忘了排除」不会变成泄露。
/// 2. **导出原样复制 favorites/servers**：这两份是用户数据的原样快照，重新序列化会丢掉
///    未来版本加的字段；只有 settings.json 必须过一遍（脱敏）。
/// 3. **导入走服务接口而不是直接写 json**：服务器与收藏在内存里有缓存，
///    直接改文件会让「导入成功但页面还是旧的」。走 <see cref="IServerService"/> /
///    <see cref="IVersionFavorites"/> 才能同时更新两边。
/// 4. 任何一步失败都返回带中文原因的结果对象，不把异常抛给页面。
/// </summary>
public sealed class ProfileBundleService : IProfileBundleService
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter<DownloadSource>() },
        TypeInfoResolver = new ProfileBundleJsonContext(),
    };

    /// <summary>favorites.json / servers.json 的格式由各自的持久化实现决定，这里只做计数与读取。</summary>
    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new ProfileBundleJsonContext(),
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new ProfileBundleJsonContext(),
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;
    private readonly ISettingsService? _settings;
    private readonly IServerService? _servers;
    private readonly IVersionFavorites? _favorites;

    public ProfileBundleService(
        ILauncherPaths paths,
        IAppLog? log = null,
        ISettingsService? settings = null,
        IServerService? servers = null,
        IVersionFavorites? favorites = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
        _settings = settings;
        _servers = servers;
        _favorites = favorites;
    }

    /// <summary>收藏文件位置。与 <c>VersionFavorites.FilePath</c> 保持一致（两处都以数据根为准）。</summary>
    public string FavoritesFilePath => Path.Combine(_paths.RootDirectory, ProfileBundleFiles.Favorites);

    /// <inheritdoc />
    public async Task<ProfileBundleExportResult> ExportAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("导出目录不能为空。", nameof(targetDirectory));
        }

        var directory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(directory);

        // 先算好全部内容再写盘：中途失败不该留下一个看起来完整、实际缺东西的包。
        var favoritesBytes = await ReadOrCreateFavoritesAsync(cancellationToken).ConfigureAwait(false);
        var serversBytes = await ReadOrCreateServersAsync(cancellationToken).ConfigureAwait(false);

        var (favoriteCount, hiddenCount) = CountFavorites(favoritesBytes);
        var serverCount = CountServers(serversBytes);

        byte[]? settingsBytes = null;
        var settingsKeys = Array.Empty<string>();

        if (_settings is not null)
        {
            var (bytes, keys) = BuildRedactedSettings(_settings.Current);
            settingsBytes = bytes;
            settingsKeys = keys;
        }

        var files = new List<string>(4)
        {
            ProfileBundleFiles.Favorites,
            ProfileBundleFiles.Servers,
        };

        if (settingsBytes is not null)
        {
            files.Add(ProfileBundleFiles.Settings);
        }

        files.Add(ProfileBundleFiles.Manifest);

        var manifest = new ProfileManifest
        {
            BundleVersion = ProfileBundleFiles.ManifestVersion,
            Kind = ProfileBundleFiles.Kind,
            ExportedAt = DateTimeOffset.UtcNow,
            LauncherVersion = ResolveLauncherVersion(),
            Files = files,
            RedactedFields = ProfileSettingsRedaction.Fields.ToList(),
            SettingsWhitelist = ProfileSettingsWhitelist.Keys.ToList(),
            FavoriteCount = favoriteCount,
            HiddenCount = hiddenCount,
            ServerCount = serverCount,
            ContainsAccounts = false,
        };

        var path = ResolveFreePath(directory);

        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

            await WriteEntryAsync(archive, ProfileBundleFiles.Favorites, favoritesBytes, cancellationToken).ConfigureAwait(false);
            await WriteEntryAsync(archive, ProfileBundleFiles.Servers, serversBytes, cancellationToken).ConfigureAwait(false);

            if (settingsBytes is not null)
            {
                await WriteEntryAsync(archive, ProfileBundleFiles.Settings, settingsBytes, cancellationToken).ConfigureAwait(false);
            }

            var manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions.Of<ProfileManifest>());
            await WriteEntryAsync(archive, ProfileBundleFiles.Manifest, manifestJson, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Cleanup(path);
            throw;
        }
        catch (Exception ex)
        {
            Cleanup(path);
            _log?.Error($"导出配置包失败：{path}", ex);
            throw;
        }

        _log?.Info($"已导出配置包：{path}（收藏 {favoriteCount} / 隐藏 {hiddenCount} / 服务器 {serverCount} / 设置 {settingsKeys.Length}）");

        return new ProfileBundleExportResult(
            path,
            favoriteCount,
            hiddenCount,
            serverCount,
            settingsKeys.Length,
            files,
            ProfileSettingsRedaction.Fields.ToList());
    }

    /// <inheritdoc />
    public async Task<ProfileBundleInspection> InspectAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
        {
            return ProfileBundleInspection.Rejected("找不到配置包文件。");
        }

        try
        {
            using var archive = ZipFile.OpenRead(bundlePath);

            var manifest = await ReadManifestAsync(archive, cancellationToken).ConfigureAwait(false);
            var manifestError = ValidateManifest(manifest);

            if (manifestError is not null)
            {
                return ProfileBundleInspection.Rejected(manifestError);
            }

            var favorites = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Favorites, cancellationToken).ConfigureAwait(false);
            var servers = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Servers, cancellationToken).ConfigureAwait(false);
            var settings = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Settings, cancellationToken).ConfigureAwait(false);

            var (favoriteCount, hiddenCount) = favorites is null ? (0, 0) : CountFavorites(favorites);
            var serverCount = servers is null ? 0 : CountServers(servers);
            var settingKeys = settings is null ? Array.Empty<string>() : ReadSettingKeys(settings);

            return new ProfileBundleInspection(
                true,
                null,
                manifest!.ExportedAt,
                manifest.LauncherVersion,
                manifest.Files,
                manifest.RedactedFields,
                favoriteCount,
                hiddenCount,
                serverCount,
                settingKeys.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return ProfileBundleInspection.Rejected("配置包已损坏：不是有效的 zip 文件。");
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取配置包失败：{bundlePath}", ex);
            return ProfileBundleInspection.Rejected($"读取配置包失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<ProfileBundleImportResult> ImportAsync(
        string bundlePath,
        bool includeSettings = false,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(bundlePath, cancellationToken).ConfigureAwait(false);

        if (!inspection.IsValid)
        {
            return ProfileBundleImportResult.Rejected(inspection.Error ?? "配置包无效。");
        }

        try
        {
            using var archive = ZipFile.OpenRead(bundlePath);

            var (servers, serversAdded) = await ImportServersAsync(archive, cancellationToken).ConfigureAwait(false);
            var (favorites, hidden) = await ImportFavoritesAsync(archive, cancellationToken).ConfigureAwait(false);

            var settings = 0;
            var settingKeys = Array.Empty<string>();

            if (includeSettings)
            {
                (settings, settingKeys) = await ImportSettingsAsync(archive, cancellationToken).ConfigureAwait(false);
            }

            _log?.Info(
                $"已导入配置包：{bundlePath}（服务器 {servers}（新增 {serversAdded}）/ 收藏 {favorites} / 隐藏 {hidden} / 设置 {settings}）");

            return new ProfileBundleImportResult(
                true,
                null,
                servers,
                serversAdded,
                favorites,
                hidden,
                settings,
                settingKeys);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return ProfileBundleImportResult.Rejected("配置包已损坏：不是有效的 zip 文件。");
        }
        catch (Exception ex)
        {
            _log?.Error($"导入配置包失败：{bundlePath}", ex);
            return ProfileBundleImportResult.Rejected($"导入失败：{ex.Message}");
        }
    }

    // ==================== 导出实现 ====================

    /// <summary>读 favorites.json 原样字节；文件不存在时给一份空结构，保证包内文件齐全。</summary>
    private async Task<byte[]> ReadOrCreateFavoritesAsync(CancellationToken cancellationToken)
    {
        var path = FavoritesFilePath;

        if (File.Exists(path))
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 读不出来就当作没有收藏：导出包缺一块内容，比整个导出失败更可接受。
                _log?.Warn($"读取收藏文件失败，按空收藏导出：{path}", ex);
            }
        }

        return Utf8NoBom.GetBytes("{\n  \"favorites\": [],\n  \"hidden\": []\n}");
    }

    private async Task<byte[]> ReadOrCreateServersAsync(CancellationToken cancellationToken)
    {
        var path = _paths.ServersFile;

        if (File.Exists(path))
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn($"读取服务器列表失败，按空列表导出：{path}", ex);
            }
        }

        return Utf8NoBom.GetBytes("[]");
    }

    /// <summary>把当前设置序列化后删掉敏感字段。返回的键清单一并给 manifest 用。</summary>
    private static (byte[] Bytes, string[] Keys) BuildRedactedSettings(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsJsonOptions.Of<AppSettings>()) as JsonObject
                   ?? new JsonObject();

        foreach (var field in ProfileSettingsRedaction.Fields)
        {
            node.Remove(field);
        }

        var keys = ProfileSettingsWhitelist.Present(node);
        var bytes = Utf8NoBom.GetBytes(node.ToJsonString(ManifestJsonOptions));
        return (bytes, keys.ToArray());
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同目录下选一个没被占用的包名：同一秒连续导出两次不会互相覆盖。</summary>
    private static string ResolveFreePath(string directory)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{ProfileBundleFiles.FilePrefix}{timestamp}.zip");

        for (var index = 2; File.Exists(candidate) && index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{ProfileBundleFiles.FilePrefix}{timestamp}-{index}.zip");
        }

        return candidate;
    }

    private void Cleanup(string path)
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
            _log?.Warn($"清理未完成的配置包失败：{path}", ex);
        }
    }

    // ==================== 导入实现 ====================

    private async Task<(int Count, int Added)> ImportServersAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Servers, cancellationToken).ConfigureAwait(false);

        if (bytes is null || _servers is null)
        {
            return (0, 0);
        }

        List<ServerEntry> incoming;

        try
        {
            incoming = JsonSerializer.Deserialize(bytes, BundleJsonOptions.Of<List<ServerEntry>>()) ?? [];
        }
        catch (JsonException ex)
        {
            _log?.Warn("配置包里的 servers.json 无法解析，跳过服务器导入", ex);
            return (0, 0);
        }

        incoming = incoming.Where(entry => !string.IsNullOrWhiteSpace(entry.Host)).ToList();

        if (incoming.Count == 0)
        {
            return (0, 0);
        }

        // 合并基准必须是磁盘上的现状：服务没 Load 过时内存是空的，用它算「新增」会全算成新增。
        await _servers.LoadAsync(cancellationToken).ConfigureAwait(false);

        var existing = new HashSet<string>(_servers.Servers.Select(ServerKey), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var applied = 0;

        foreach (var entry in incoming)
        {
            if (existing.Add(ServerKey(entry)))
            {
                added++;
            }

            await _servers.AddAsync(entry, cancellationToken).ConfigureAwait(false);
            applied++;
        }

        return (applied, added);
    }

    private async Task<(int Favorites, int Hidden)> ImportFavoritesAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Favorites, cancellationToken).ConfigureAwait(false);

        if (bytes is null || _favorites is null)
        {
            return (0, 0);
        }

        var (favorites, hidden) = ReadFavoriteNames(bytes);

        if (favorites.Count == 0 && hidden.Count == 0)
        {
            return (0, 0);
        }

        await _favorites.LoadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var name in favorites)
        {
            // 接口只有 toggle：已经是收藏的不能再 toggle，否则会把它取消掉。
            if (!_favorites.IsFavorite(name))
            {
                await _favorites.ToggleFavoriteAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var name in hidden)
        {
            await _favorites.SetHiddenAsync(name, true, cancellationToken).ConfigureAwait(false);
        }

        return (favorites.Count, hidden.Count);
    }

    private async Task<(int Applied, string[] Keys)> ImportSettingsAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Settings, cancellationToken).ConfigureAwait(false);

        if (bytes is null || _settings is null)
        {
            return (0, Array.Empty<string>());
        }

        JsonObject? node;
        AppSettings? incoming;

        try
        {
            node = JsonNode.Parse(bytes) as JsonObject;
            incoming = JsonSerializer.Deserialize(bytes, SettingsJsonOptions.Of<AppSettings>());
        }
        catch (JsonException ex)
        {
            _log?.Warn("配置包里的 settings.json 无法解析，跳过设置导入", ex);
            return (0, Array.Empty<string>());
        }

        if (node is null || incoming is null)
        {
            return (0, Array.Empty<string>());
        }

        var keys = ProfileSettingsWhitelist.Present(node);
        var applied = ProfileSettingsWhitelist.Apply(_settings.Current, node, incoming);

        if (applied == 0)
        {
            return (0, Array.Empty<string>());
        }

        // 白名单字段改完立刻落盘，否则界面显示新主题、下次启动又变回去。
        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);

        // 再从磁盘读一遍：SettingsService 的 Normalize 只在这条路径上跑（把越界值拉回可用范围），
        // 同时它会触发 Changed，主窗口据此立刻应用新主题/材质，不用重启。
        // 必须用 ReloadAsync：LoadAsync 是幂等的（第一次之后直接返回），这里要的正是「强制重读」。
        await _settings.ReloadAsync(cancellationToken).ConfigureAwait(false);

        return (applied, keys.ToArray());
    }

    // ==================== 读取与校验 ====================

    private static async Task<ProfileManifest?> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadOptionalEntryAsync(archive, ProfileBundleFiles.Manifest, cancellationToken).ConfigureAwait(false);

        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, ManifestJsonOptions.Of<ProfileManifest>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ValidateManifest(ProfileManifest? manifest)
    {
        if (manifest is null)
        {
            // 这是最常见的一类误操作：把游戏存档、整合包 zip 拖到这里。
            return "配置包里没有 manifest.json：请选择 Nebula Launcher 导出的 nebula-profile-*.zip。";
        }

        if (!string.Equals(manifest.Kind, ProfileBundleFiles.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return $"配置包类型不匹配（manifest.kind = {manifest.Kind}）。";
        }

        if (manifest.BundleVersion <= 0)
        {
            return "配置包的 manifest.json 缺少有效的 bundleVersion。";
        }

        if (manifest.BundleVersion > ProfileBundleFiles.ManifestVersion)
        {
            return $"配置包版本 {manifest.BundleVersion} 高于当前启动器支持的 {ProfileBundleFiles.ManifestVersion}，请升级启动器后再导入。";
        }

        return null;
    }

    /// <summary>
    /// 只按固定名字取条目：不遍历、不落盘、不解压。
    /// 这样包内就算塞了 <c>../../evil.txt</c> 也没有被写出去的机会。
    /// </summary>
    private static async Task<byte[]?> ReadOptionalEntryAsync(
        ZipArchive archive,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName);

        if (entry is null)
        {
            return null;
        }

        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static (int Favorites, int Hidden) CountFavorites(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (0, 0);
            }

            var favorites = document.RootElement.TryGetProperty("favorites", out var favoriteArray)
                            && favoriteArray.ValueKind == JsonValueKind.Array
                ? favoriteArray.GetArrayLength()
                : 0;

            var hidden = document.RootElement.TryGetProperty("hidden", out var hiddenArray)
                         && hiddenArray.ValueKind == JsonValueKind.Array
                ? hiddenArray.GetArrayLength()
                : 0;

            return (favorites, hidden);
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

    private static int CountServers(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static (List<string> Favorites, List<string> Hidden) ReadFavoriteNames(byte[] bytes)
    {
        var favorites = new List<string>();
        var hidden = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (favorites, hidden);
            }

            CollectNames(document.RootElement, "favorites", favorites);
            CollectNames(document.RootElement, "hidden", hidden);
        }
        catch (JsonException)
        {
            return (favorites, hidden);
        }

        return (favorites, hidden);
    }

    private static void CollectNames(JsonElement root, string property, List<string> target)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("versionName", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = name.GetString();

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            value = value.Trim();

            // 手改的包里可能有重复名字：重复导入没有意义，还会让计数虚高。
            if (!target.Contains(value, StringComparer.Ordinal))
            {
                target.Add(value);
            }
        }
    }

    private static string[] ReadSettingKeys(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes) is JsonObject node
                ? ProfileSettingsWhitelist.Present(node).ToArray()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>服务器条目的合并键。与 <c>ServerService.AddAsync</c> 的去重口径一致：地址 + 端口。</summary>
    private static string ServerKey(ServerEntry entry) => $"{entry.Host.Trim()}:{entry.Port}";

    /// <summary>
    /// 启动器版本号，写进 manifest 的 <c>launcherVersion</c>。
    ///
    /// 值来自仓库级构建属性（<c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>），
    /// 由 <see cref="DiagnosticsSnapshot.LauncherVersionOf"/> 解析成**裸语义版本**（<c>1.0.0</c>）：
    /// 不带 <c>v</c> 前缀（这个字段是机器读的数据，不是展示串），也不再是
    /// <c>GetName().Version</c> 那种四段形态（<c>1.0.0.0</c>）。
    /// 这样 manifest 与诊断文本、关于页报的是同一个版本号。
    ///
    /// 截图/测试里入口程序集可能不是 NebulaLauncher，取不到就退回 Core 的程序集；
    /// 两者都读不出来才给 <c>unknown</c>——manifest 里这个字段只用于诊断，缺一个精确值不影响导入。
    /// </summary>
    private static string ResolveLauncherVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(ProfileBundleService).Assembly;
            return DiagnosticsSnapshot.LauncherVersionOf(assembly);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}

/// <summary>
/// 配置包导入 / 导出的 Source Generator 上下文。
///
/// 这里一次登记三种口径（settings / servers / manifest）：它们分别对应上面三个
/// <c>JsonSerializerOptions</c>，但生成的元数据本身与 options 无关，
/// 各项开关只在传参时生效，所以共用一份 context 即可。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ServerEntry>))]
[JsonSerializable(typeof(ProfileManifest))]
internal sealed partial class ProfileBundleJsonContext : JsonSerializerContext
{
}
