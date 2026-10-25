using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Core.Java;

/// <summary>
/// 本机 Java 探测、版本选择与运行时下载。进程调用全部带超时，
/// 任何一个候选探测失败都不影响整体结果。
/// </summary>
public sealed class JavaService : IJavaService, IDisposable
{
    private const int ProbeTimeoutMs = 10_000;

    /// <summary>
    /// 候选上限。原值 64 是按「只扫 Program Files + 注册表」定的；加入 Mojang 官方 runtime、
    /// 其他启动器的 java 目录之后候选会明显变多（一个 <c>runtime</c> 下常有 5～6 个 component，
    /// 每个 component 又要按 2～3 个平台键展开），提到 96 让常见机器一次扫全。
    /// 探测本身有 4 并发闸门，只有「存在但跑不起来」的坏路径才会吃满 10 秒超时，
    /// 所以数量上限放宽比并发/超时放宽更安全。
    /// </summary>
    private const int MaxCandidates = 96;

    /// <summary>
    /// Mojang 官方 runtime 的 <c>platform</c> 目录名，对应 JavaManager.java:82-105 的
    /// <c>getMojangJavaPlatform</c>。同一台机器上并存多个键是常态（32 位与 64 位 runtime 同时存在）。
    /// </summary>
    private static readonly string[] OfficialRuntimePlatformKeys = PlatformService.IsWindows
        ? ["windows-x64", "windows-x86", "windows-arm64"]
        : PlatformService.IsMacOs
            ? ["mac-os", "mac-os-arm64"]
            : ["linux", "linux-i386", "linux-arm64"];

    /// <summary>Oracle 的 javapath 桩目录标记；其中的 java 会指向自动更新的 JRE，版本与实际安装不符。</summary>
    private const string OracleJavapathMarker = @"\common files\oracle\java\";

    /// <summary>
    /// 排除列表文件名。用户数据，落在数据根目录下（不是 <c>settings.json</c>）：
    /// 「恢复默认设置」不该把用户移除过的 Java 又放回来。
    /// </summary>
    public const string ExcludedFileName = "java-excluded.json";

    /// <summary>
    /// 手动添加的 Java 路径文件名。与排除列表分开两个文件：两者语义相反，
    /// 合成一个文档会让「读一个文件」变成「先判断里面是哪个字段」。
    /// </summary>
    public const string ManualFileName = "java-manual.json";

    private readonly IPlatformService _platform;
    private readonly ILauncherPaths _paths;
    private readonly DownloadQueue _queue;
    private readonly DownloadHttpClientFactory? _httpFactory;
    private readonly IAppLog? _log;
    private readonly ISettingsService? _settings;
    private readonly JavaCache _javaCache;
    private readonly JavaPathList _excludedList;
    private readonly JavaPathList _manualList;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    // volatile：用户列表变化时由事件处理器在任意线程清空，探测线程要能立刻看见。
    private volatile IReadOnlyList<JavaInfo>? _cache;

    public JavaService(
        IPlatformService platform,
        ILauncherPaths paths,
        DownloadQueue downloadQueue,
        IAppLog? log = null,
        ISettingsService? settings = null,
        DownloadHttpClientFactory? httpFactory = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _queue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _log = log;
        _settings = settings;
        _httpFactory = httpFactory;
        _javaCache = new JavaCache(_paths.CacheDirectory, log);

        // 排除 / 手动列表都是用户数据：文件坏了按空列表重建，绝不让它阻断 Java 探测。
        _excludedList = new JavaPathList(_paths.RootDirectory, ExcludedFileName, log);
        _manualList = new JavaPathList(_paths.RootDirectory, ManualFileName, log);
        _excludedList.Changed += OnUserJavaListChanged;
        _manualList.Changed += OnUserJavaListChanged;
    }

    public async Task<IReadOnlyList<JavaInfo>> DetectAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        // 排除 / 手动列表必须先加载：CollectCandidates 要用它们过滤与置顶，而且用户列表一变
        // 缓存的探测结果就过期了（被排除的那条还留在 _cache 里）。
        EnsureUserJavaLists();

        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cache is { Count: > 0 })
            {
                return _cache;
            }

            var candidates = CollectCandidates();

            // 缓存是纯加速层：关闭时连文件都不碰（EnableJavaCache 的语义就是「完全跳过缓存」）。
            var useCache = IsJavaCacheEnabled;

            // forceRefresh 时依然读盘，只是不采信命中项：SaveCache 需要磁盘上的旧条目来保留
            // 「已被用户排除」的那些路径（见 SaveCache）。
            var cached = useCache
                ? _javaCache.Load()
                : new Dictionary<string, JavaCacheEntry>(StringComparer.OrdinalIgnoreCase);
            var trustCache = useCache && !forceRefresh;

            var restored = new List<JavaInfo>(candidates.Count);
            var hitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<string>(candidates.Count);

            // 逐项比对可执行文件元数据：没变的直接采信，跳过子进程。
            foreach (var path in candidates)
            {
                if (trustCache && cached.TryGetValue(path, out var entry) && JavaCache.IsStillValid(entry))
                {
                    restored.Add(ToJavaInfo(entry));
                    hitPaths.Add(path);
                }
                else
                {
                    pending.Add(path);
                }
            }

            using var gate = new SemaphoreSlim(4);
            var probed = await Task.WhenAll(pending.Select(async path =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            var installed = probed.Where(info => info is not null)
                .Select(info => info!)
                .ToList();

            // 缓存命中项与本次探测项一起参与去重：命中项可能比新探测的版本更高。
            // 手动添加的 Java 优先被留下并整体置顶：用户特意指定过的运行时，不该被扫描到的同版本 Java 顶掉。
            var manual = _manualList.Paths;

            var result = restored.Concat(installed)
                .GroupBy(info => (info.MajorVersion, info.Is64Bit))
                .Select(group => group
                    .OrderBy(info => ManualRank(manual, info.Path))
                    .ThenByDescending(info => info.Version, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(info => info.Path.Length)
                    .First())
                .Select(info => WithSource(info, manual))
                .OrderBy(info => ManualRank(manual, info.Path))
                .ThenByDescending(info => info.MajorVersion)
                .ThenByDescending(info => info.Version, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidates)
                .ToArray();

            _cache = result;

            if (useCache)
            {
                SaveCache(candidates, cached, hitPaths, installed);
            }

            _log?.Info(
                $"Java 探测完成：{result.Length} 个（候选 {candidates.Count} 个，缓存命中 {hitPaths.Count} 个，重探 {pending.Count} 个）");
            return result;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <summary>缓存开关。设置服务未注入时按默认开启（与 <see cref="AppSettings.EnableJavaCache"/> 的默认值一致）。</summary>
    private bool IsJavaCacheEnabled => _settings?.Current.EnableJavaCache ?? true;

    /// <summary>
    /// 把缓存记录还原成 <see cref="JavaInfo"/>。
    /// HomeDirectory 不在缓存字段里，按可执行文件路径反推（与探测时的 <see cref="GuessHome"/> 同一规则）。
    /// </summary>
    private static JavaInfo ToJavaInfo(JavaCacheEntry entry) => new()
    {
        Path = entry.Path,
        Version = entry.Version,
        MajorVersion = entry.MajorVersion,
        Is64Bit = entry.Is64Bit,
        Vendor = entry.Vendor,
        HomeDirectory = GuessHome(entry.Path),
        Source = "cache",
    };

    /// <summary>
    /// 回写缓存：只保留本次扫描到的路径，被卸载的 Java 会被自然淘汰，缓存不会无限增长。
    /// 命中项原样保留（不刷新 CheckedAt：它记录的是「上次真正跑子进程的时间」）。
    /// </summary>
    private void SaveCache(
        IReadOnlyList<string> candidates,
        IReadOnlyDictionary<string, JavaCacheEntry> cached,
        HashSet<string> hitPaths,
        IReadOnlyList<JavaInfo> probed)
    {
        var probedByPath = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in probed)
        {
            probedByPath[info.Path] = info;
        }

        var entries = new List<JavaCacheEntry>(candidates.Count);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            if (hitPaths.Contains(path) && cached.TryGetValue(path, out var hit))
            {
                entries.Add(hit);
                written.Add(path);
            }
            else if (probedByPath.TryGetValue(path, out var info))
            {
                entries.Add(JavaCache.Capture(info));
                written.Add(path);
            }
        }

        // 被用户排除的 Java 不进候选（CollectCandidates 已过滤），但它的缓存条目原样保留：
        // 用户一键恢复之后不必再对每个被排除的 Java 重跑一次子进程。
        foreach (var pair in cached)
        {
            if (!written.Contains(pair.Key) && IsExcluded(pair.Key))
            {
                entries.Add(pair.Value);
                written.Add(pair.Key);
            }
        }

        _javaCache.Save(entries);
    }

    public async Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            return null;
        }

        try
        {
            var output = await RunAsync(javaPath, "-XshowSettings:properties -version", ProbeTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (output is null)
            {
                return null;
            }

            var properties = ParseProperties(output);
            var version = Get(properties, "java.version");
            var vendor = Get(properties, "java.vendor");
            var home = Get(properties, "java.home");
            var arch = Get(properties, "os.arch");
            var dataModel = Get(properties, "sun.arch.data.model");

            if (string.IsNullOrEmpty(version))
            {
                version = ParseVersionFromBanner(output);
            }

            if (string.IsNullOrEmpty(version))
            {
                // 不是有效的 Java 可执行文件。
                return null;
            }

            if (string.IsNullOrEmpty(home))
            {
                home = GuessHome(javaPath);
            }

            var bits64 = dataModel == "64" || (arch ?? string.Empty).Contains("64", StringComparison.OrdinalIgnoreCase);

            return new JavaInfo
            {
                Path = javaPath,
                Version = version,
                MajorVersion = ParseMajor(version),
                Is64Bit = bits64,
                Vendor = string.IsNullOrEmpty(vendor) ? GuessVendor(output) : vendor,
                HomeDirectory = home,
                Source = "scan",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Debug($"探测 Java 失败：{javaPath}（{ex.Message}）");
            return null;
        }
    }

    // ---------------- 手动管理（用户数据：java-manual.json / java-excluded.json） ----------------

    /// <summary>
    /// 把一个手动指定的 Java 加入候选列表。入参可以是 <c>java.exe</c>，也可以是 JDK 根目录
    /// （<c>&lt;home&gt;/bin/java.exe</c> 会被自动展开）。
    ///
    /// 探测顺序与 <see cref="DetectAsync"/> 一致：先看缓存（用户添加的是本机已探测过的 Java 时不重跑子进程），
    /// 缓存不可信才真跑一次 <c>-XshowSettings:properties -version</c>。失败返回 null，不抛异常。
    /// </summary>
    public async Task<JavaInfo?> AddManualAsync(string javaPath, CancellationToken cancellationToken = default)
    {
        var executable = ResolveJavaExecutable(javaPath);
        if (executable is null)
        {
            _log?.Warn($"手动添加 Java 失败：路径既不是文件也不是 JDK 目录（{javaPath}）");
            return null;
        }

        var info = TryLoadCached(executable)
                   ?? await ProbeAsync(executable, cancellationToken).ConfigureAwait(false);

        if (info is null)
        {
            _log?.Warn($"手动添加 Java 失败：探测没有结果（{executable}）");
            return null;
        }

        await _manualList.AddAsync(executable, cancellationToken).ConfigureAwait(false);

        // 手动加入即撤销此前的排除（PCL2 ManuallyImportJava 同款）：否则用户会得到一个
        // 「添加成功但列表里没有」的条目。
        await _excludedList.RemoveAsync(executable, cancellationToken).ConfigureAwait(false);

        _log?.Info($"已手动添加 Java {info.MajorVersion}：{executable}");
        return WithSource(info, _manualList.Paths);
    }

    /// <summary>
    /// 把一条 Java 移出候选列表并写进排除列表。之后任何候选源（PATH / JAVA_HOME / 官方 runtime）
    /// 扫到它都会被过滤掉，直到用户恢复。
    /// </summary>
    public async Task RemoveAsync(string javaPath, CancellationToken cancellationToken = default)
    {
        var executable = ResolveJavaExecutable(javaPath) ?? JavaPathList.Normalize(javaPath);
        if (executable is null)
        {
            return;
        }

        await _excludedList.AddAsync(executable, cancellationToken).ConfigureAwait(false);

        // 已手动添加过的项要一起撤掉：两个列表同时有它时排除优先，留着手动记录只会让「恢复」语义变糊。
        await _manualList.RemoveAsync(executable, cancellationToken).ConfigureAwait(false);

        _log?.Info($"已将 Java 移出候选并加入排除列表：{executable}");
    }

    /// <summary>当前排除列表（归一化路径，写入顺序）。</summary>
    public Task<IReadOnlyList<string>> GetExcludedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_excludedList.Paths);

    /// <summary>从排除列表撤销一条；不重新探测。</summary>
    public Task<bool> RestoreAsync(string javaPath, CancellationToken cancellationToken = default) =>
        _excludedList.RemoveAsync(javaPath, cancellationToken);

    /// <summary>清空排除列表并重新扫描一次。</summary>
    public async Task ClearExcludedAsync(CancellationToken cancellationToken = default)
    {
        await _excludedList.ClearAsync(cancellationToken).ConfigureAwait(false);

        _log?.Info("已清空 Java 排除列表，重新扫描");

        // 返回值没人用：这次扫描只是为了让内存缓存在下一次打开设置页之前就是新的。
        await DetectAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把「java.exe 或 JDK 根目录」统一成可执行文件绝对路径；都不是时返回 null。
    ///
    /// Windows 上固定按 <c>java.exe</c> → <c>javaw.exe</c> 的顺序找：<c>javaw.exe</c> 是 GUI 子系统程序，
    /// 它的控制台输出依赖父进程给的句柄，某些宿主下拿不到 <c>-XshowSettings</c> 的输出，探测会静默失败。
    /// 目录形式同时支持 JDK 根（<c>&lt;home&gt;/bin/java.exe</c>）与 bin 目录本身（<c>&lt;home&gt;/java.exe</c>）。
    /// </summary>
    public static string? ResolveJavaExecutable(string? javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            return null;
        }

        var trimmed = javaPath.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            if (File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            if (!Directory.Exists(trimmed))
            {
                return null;
            }

            var executableNames = PlatformService.IsWindows
                ? new[] { "java.exe", "javaw.exe" }
                : new[] { "java" };

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));

            foreach (var name in executableNames)
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct))
                {
                    return direct;
                }

                var inBin = Path.Combine(root, "bin", name);
                if (File.Exists(inBin))
                {
                    return inBin;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>缓存命中就免掉一次子进程；关闭缓存时一律返回 null（与 <see cref="DetectAsync"/> 同语义）。</summary>
    private JavaInfo? TryLoadCached(string executable)
    {
        EnsureUserJavaLists();

        if (!IsJavaCacheEnabled)
        {
            return null;
        }

        var cached = _javaCache.Load();

        // 只有元数据比对一致的记录才采信：换了文件、改了大小都会落到真探测。
        return cached.TryGetValue(executable, out var entry) && JavaCache.IsStillValid(entry)
            ? ToJavaInfo(entry)
            : null;
    }

    // ---------------- 用户列表的内部接线 ----------------

    private void OnUserJavaListChanged(object? sender, EventArgs e) => InvalidateCache();

    /// <summary>丢掉内存里的探测结果；下次 <see cref="DetectAsync"/> 按新的排除 / 手动列表重算。</summary>
    private void InvalidateCache() => _cache = null;

    /// <summary>两个用户列表都是几条路径的小文件，探测前一次性同步加载。</summary>
    private void EnsureUserJavaLists()
    {
        _excludedList.EnsureLoaded();
        _manualList.EnsureLoaded();
    }

    /// <summary>
    /// 路径是否被用户移除过。同时比对可执行文件与它的 JDK 根目录：排除列表允许手工编辑成
    /// <c>C:\jdk-17</c>，而候选路径一定是 <c>...\bin\java.exe</c>。
    /// </summary>
    private bool IsExcluded(string path) =>
        _excludedList.Contains(path) || _excludedList.Contains(GuessHome(path));

    /// <summary>手动项在列表里的位次；不在列表里返回 <see cref="int.MaxValue"/>（排最后）。</summary>
    private static int ManualRank(IReadOnlyList<string> manual, string path)
    {
        for (var i = 0; i < manual.Count; i++)
        {
            if (string.Equals(manual[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// 手动添加的项在结果里标成 <c>manual</c>。<see cref="JavaInfo"/> 的 init-only 属性改不了，
    /// 只能复制一份；缓存恢复的项来源是 <c>cache</c>，界面不区分的话就看不出用户自己加过。
    /// </summary>
    private static JavaInfo WithSource(JavaInfo info, IReadOnlyList<string> manual) =>
        ManualRank(manual, info.Path) == int.MaxValue
            ? info
            : new JavaInfo
            {
                Path = info.Path,
                Version = info.Version,
                MajorVersion = info.MajorVersion,
                Is64Bit = info.Is64Bit,
                Vendor = info.Vendor,
                HomeDirectory = info.HomeDirectory,
                Source = "manual",
            };

    /// <summary>
    /// 优先读本地版本 JSON 的 <c>javaVersion</c>（26.x 这类新版本硬编码表必然滞后），
    /// 拿不到再用内置表兜底。
    /// </summary>
    public JavaRequirement? GetRequirement(string minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return null;
        }

        return JavaRequirementTable.ReadFromDisk(_paths.VersionsDirectory, minecraftVersion)
               ?? JavaRequirementTable.Get(minecraftVersion);
    }

    /// <summary>
    /// 某版本允许的 Java 主版本区间（上下界 + 加载器条件的交集）。
    ///
    /// 与 <see cref="GetRequirement"/> 同源：本地版本 JSON 带 <c>javaVersion</c> 时按它算下界（GAME_JSON），
    /// 否则退回内置表。未知版本（快照 / Beta）返回 null。
    /// 供启动前置检查与本次的自动选 Java 使用；<see cref="IJavaService"/> 还没有这个成员。
    /// </summary>
    public (int Min, int Max)? GetAllowedRange(string minecraftVersion, LoaderKind loader = LoaderKind.Vanilla)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return null;
        }

        var declared = JavaRequirementTable.ReadFromDisk(_paths.VersionsDirectory, minecraftVersion);
        return JavaRequirementTable.AllowedRange(minecraftVersion, loader, declared?.RequiredMajor);
    }

    /// <summary>
    /// 同 <see cref="GetAllowedRange(string, LoaderKind)"/>，额外合并「加载器小版本」带来的上限。
    ///
    /// Forge 的 build 号、NeoForge 的版本号、Fabric Loader 的版本号会把上限压到比游戏版本更紧的位置
    /// （例如 1.19.4 Forge 45.0.21–45.0.65 只能到 Java 19），只按「游戏版本 + 加载器种类」算会漏掉这一层。
    /// <paramref name="loaderVersion"/> 为空时行为与旧重载完全一致——拿不到版本号就退回粗粒度判定，
    /// 不猜一个区间出来。
    ///
    /// 单独重载而不是给旧方法加可选参数：旧签名是 <see cref="IJavaService"/> 上已有的成员，
    /// 加默认值会让「调用方到底有没有把版本号传下来」在代码里看不出来。
    /// </summary>
    public (int Min, int Max)? GetAllowedRange(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return null;
        }

        var declared = JavaRequirementTable.ReadFromDisk(_paths.VersionsDirectory, minecraftVersion);
        return JavaRequirementTable.AllowedRange(minecraftVersion, loader, declared?.RequiredMajor, loaderVersion);
    }

    /// <summary>
    /// 接口实现：加载器只能从版本 id 猜（接口签名里没有 LoaderKind 参数）。
    /// 能拿到 <see cref="LoaderKind"/> 的调用方请用带加载器的重载，猜命名不如直接传准。
    /// </summary>
    public Task<JavaInfo?> ResolveForVersionAsync(
        string minecraftVersion,
        string? preferredPath = null,
        CancellationToken cancellationToken = default) =>
        ResolveForVersionAsync(minecraftVersion, DetectLoaderKind(minecraftVersion), preferredPath, cancellationToken);

    /// <summary>
    /// 为指定版本挑选 Java：先按 <see cref="GetAllowedRange"/> 过滤掉上下界外的运行时，
    /// 再在剩下的集合里取主版本最低的那个（越接近目标越少兼容问题）。
    ///
    /// 没有满足者时返回 null，并在日志里写明「需要 Java X–Y，本机只有 …」，
    /// 让 UI 能提示装哪个版本，而不是只说「没有可用的 Java」。
    /// </summary>
    public async Task<JavaInfo?> ResolveForVersionAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? preferredPath = null,
        CancellationToken cancellationToken = default)
    {
        var allowed = GetAllowedRange(minecraftVersion, loader);

        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var preferred = await ProbeAsync(preferredPath, cancellationToken).ConfigureAwait(false);
            if (preferred is not null && IsAllowed(preferred, allowed))
            {
                return preferred;
            }

            if (preferred is not null && allowed is { } allowedRange)
            {
                _log?.Warn(
                    $"指定的 Java {preferred.Version} 不在 {minecraftVersion}（{loader}）允许的 "
                    + $"{JavaRequirementTable.DescribeJavaRange(allowedRange)} 区间内，改用自动选择");
            }
        }

        var all = await DetectAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        var selected = SelectForRange(all, allowed);

        if (selected is null && allowed is { } range)
        {
            _log?.Warn(
                $"{minecraftVersion}（{loader}）需要 {JavaRequirementTable.DescribeJavaRange(range)}，"
                + $"本机只有 {DescribeInstalled(all)}");
        }

        return selected;
    }

    /// <summary>
    /// 从已探测的运行时里挑一个：区间内主版本最低者（同主版本优先 64 位）。
    /// 区间内没有候选返回 null；<paramref name="range"/> 为 null（未知版本）时取最高主版本，
    /// 因为现代快照更可能需要新 JVM。纯函数，前置检查与测试可以直接复用。
    /// </summary>
    public static JavaInfo? SelectForRange(IReadOnlyList<JavaInfo> installed, (int Min, int Max)? range)
    {
        ArgumentNullException.ThrowIfNull(installed);

        if (installed.Count == 0)
        {
            return null;
        }

        if (range is not { } allowed)
        {
            return installed.OrderByDescending(info => info.MajorVersion).First();
        }

        return installed
            .Where(info => info.MajorVersion >= allowed.Min && info.MajorVersion <= allowed.Max)
            .OrderBy(info => info.MajorVersion)
            .ThenByDescending(info => info.Is64Bit)
            .FirstOrDefault();
    }

    public async Task<string?> InstallRuntimeAsync(
        JavaRuntimePackage package,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var architecture = NormalizeArchitecture(package.Architecture);
        var targetDirectory = Path.Combine(_paths.RootDirectory, "runtimes", $"java-{package.MajorVersion}-{architecture}");

        var existing = ResolveExecutable(targetDirectory, package.ExecutableRelativePath);
        if (existing is not null)
        {
            _log?.Info($"Java {package.MajorVersion} 已存在：{existing}");
            return existing;
        }

        var url = string.IsNullOrWhiteSpace(package.DownloadUrl)
            ? BuildAdoptiumUrl(package.MajorVersion, architecture)
            : package.DownloadUrl;

        var archiveType = NormalizeArchiveType(package.ArchiveType, url);
        var archivePath = Path.Combine(_paths.CacheDirectory, "java", $"java-{package.MajorVersion}-{architecture}.{archiveType}");

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        var item = new DownloadItem
        {
            Url = url,
            DestinationPath = archivePath,
            Id = $"java-{package.MajorVersion}-{architecture}",
            Group = "Java",
        };

        // 走旁路：Java 运行时经常是在「安装游戏」的过程中被要的（版本需要 Java 25+，
        // 本机只有 8/21）。那时队列被安装的外部批次独占，走普通 DownloadAsync 会抛
        // 「安装批次正在进行」——不装 Java 就装不完游戏，不退出安装又装不了 Java。
        // 见票 27 缺陷 D 与 DownloadQueue.DownloadBypassAsync。
        var result = await _queue.DownloadBypassAsync([item], progress, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _log?.Error($"下载 Java {package.MajorVersion} 失败：{url}");
            return null;
        }

        var staging = Path.Combine(_paths.CacheDirectory, "java", $"staging-{package.MajorVersion}-{architecture}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            ExtractArchive(archivePath, archiveType, staging);
            PromoteToTarget(staging, targetDirectory);

            var executable = ResolveExecutable(targetDirectory, package.ExecutableRelativePath);
            if (executable is null)
            {
                _log?.Error($"Java 运行时解压后未找到可执行文件：{targetDirectory}");
                return null;
            }

            _log?.Info($"Java {package.MajorVersion} 安装完成：{executable}");
            return executable;
        }
        catch (Exception ex)
        {
            _log?.Error($"解压 Java 运行时失败：{archivePath}", ex);
            return null;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public async Task<string?> DownloadRuntimeAsync(
        int majorVersion,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (majorVersion <= 0)
        {
            return null;
        }

        // 先试 Mojang 官方 runtime：它走 piston-meta，已经在镜像表里，国内可用性明显好于
        // api.adoptium.net（不在镜像表，经常直接超时）。Mojang 只发布 8/16/17/21/25 五个组件，
        // 中间版本（11、22…）必然回落到 Adoptium。
        var mojang = await TryInstallMojangRuntimeAsync(majorVersion, progress, cancellationToken)
            .ConfigureAwait(false);

        if (mojang is not null)
        {
            return mojang;
        }

        var architecture = NormalizeArchitecture(Environment.Is64BitProcess ? "x64" : "x86");
        var isWindows = PlatformService.IsWindows;

        var package = new JavaRuntimePackage
        {
            Name = $"Temurin {majorVersion}",
            MajorVersion = majorVersion,
            DownloadUrl = BuildAdoptiumUrl(majorVersion, architecture),
            // Adoptium 对 Windows 发 zip，对 macOS / Linux 发 tar.gz。
            ArchiveType = isWindows ? "zip" : "tar.gz",
            ExecutableRelativePath = isWindows ? "bin/javaw.exe" : "bin/java",
            Architecture = architecture,
            Vendor = "Adoptium",
        };

        return await InstallRuntimeAsync(package, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 Mojang <c>java-runtime</c> 清单安装。失败（网络、清单缺组件、单文件下载失败）一律返回 null，
    /// 由调用方回落到 Adoptium——「另一种方式装不上」不该变成「用户装不了 Java」。
    /// </summary>
    private async Task<string?> TryInstallMojangRuntimeAsync(
        int majorVersion,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_httpFactory is null)
        {
            return null;
        }

        var component = MojangRuntimeCatalog.ComponentFor(majorVersion);

        if (component.Length == 0)
        {
            return null;
        }

        var isWindows = PlatformService.IsWindows;
        var executableName = isWindows ? "bin/javaw.exe" : "bin/java";
        var architecture = NormalizeArchitecture(Environment.Is64BitProcess ? "x64" : "x86");
        var targetDirectory = Path.Combine(_paths.RootDirectory, "runtimes", $"java-{majorVersion}-{architecture}");

        var existing = ResolveExecutable(targetDirectory, executableName);

        if (existing is not null)
        {
            _log?.Info($"Java {majorVersion} 已存在：{existing}");
            return existing;
        }

        var staging = Path.Combine(_paths.CacheDirectory, "java", $"mojang-{majorVersion}-{Guid.NewGuid():N}");

        try
        {
            var client = _httpFactory.Client;
            var platformKey = MojangRuntimeCatalog.CurrentPlatformKey();

            var allJson = await client
                .GetStringAsync(MojangRuntimeCatalog.AllProductsUrl, cancellationToken)
                .ConfigureAwait(false);

            var manifestUrl = MojangRuntimeCatalog.ParseManifestUrl(allJson, platformKey, component);

            if (manifestUrl is null)
            {
                _log?.Info($"Mojang 清单里没有 {component} / {platformKey}，改用 Adoptium。");
                return null;
            }

            // manifest 地址按「镜像优先」重写：清单里的 URL 都是 piston-meta，直接请求经常超时。
            var rewrittenManifestUrl = _httpFactory.Rewrite(manifestUrl);

            var manifestJson = await client
                .GetStringAsync(rewrittenManifestUrl, cancellationToken)
                .ConfigureAwait(false);

            var files = MojangRuntimeCatalog.ParseManifest(manifestJson);

            if (files.Count == 0)
            {
                _log?.Warn($"Mojang {component} 清单为空，改用 Adoptium。");
                return null;
            }

            Directory.CreateDirectory(staging);

            var items = new List<DownloadItem>(files.Count);
            var executablePaths = new List<string>();

            foreach (var file in files)
            {
                var destination = Path.Combine(
                    staging,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (file.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                if (!string.IsNullOrEmpty(file.LinkTarget))
                {
                    TryCreateSymbolicLink(destination, file.LinkTarget);
                    continue;
                }

                if (string.IsNullOrEmpty(file.Url))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                if (file.IsExecutable)
                {
                    executablePaths.Add(destination);
                }

                items.Add(new DownloadItem
                {
                    Url = file.Url,
                    DestinationPath = destination,
                    Id = file.RelativePath,
                    Group = "Java",
                    ExpectedSha1 = file.Sha1,
                    ExpectedSize = file.Size,
                });
            }

            if (items.Count == 0)
            {
                TryDeleteDirectory(staging);
                return null;
            }

            // 同样走旁路：这条路径最常被触发的时刻正是「安装 26.1.2 发现缺 Java 25」，
            // 而那一刻队列被安装的外部批次独占。见票 27 缺陷 D。
            var batch = await _queue.DownloadBypassAsync(items, progress, cancellationToken).ConfigureAwait(false);

            if (!batch.Success)
            {
                _log?.Error($"Mojang Java {majorVersion} 有 {batch.FailedFiles} 个文件下载失败，改用 Adoptium。");
                TryDeleteDirectory(staging);
                return null;
            }

            foreach (var path in executablePaths)
            {
                TrySetExecutable(path);
            }

            PromoteToTarget(staging, targetDirectory);

            var executable = ResolveExecutable(targetDirectory, executableName);

            if (executable is null)
            {
                _log?.Error($"Mojang Java {majorVersion} 安装后未找到可执行文件：{targetDirectory}");
                return null;
            }

            _log?.Info($"已从 Mojang 官方清单安装 Java {majorVersion}：{executable}");
            return executable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"从 Mojang 清单安装 Java {majorVersion} 失败，改用 Adoptium。", ex);
            return null;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// 建符号链接。清单里链接很少见（主要是 macOS 的 <c>jre/lib/libjli.dylib</c> 之类），
    /// Windows 上建链接要开发者模式或管理员，失败就跳过——缺一个链接不影响 java 启动。
    /// </summary>
    private void TryCreateSymbolicLink(string path, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex)
        {
            _log?.Debug($"跳过符号链接 {path} → {target}：{ex.Message}");
        }
    }

    /// <summary>Unix 上补可执行位。Windows 不需要，也调用不了这个 API。</summary>
    private void TrySetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            _log?.Debug($"设置可执行位失败 {path}：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _excludedList.Changed -= OnUserJavaListChanged;
        _manualList.Changed -= OnUserJavaListChanged;
        _excludedList.Dispose();
        _manualList.Dispose();
        _cacheGate.Dispose();
    }

    /// <summary>按 Adoptium v3 规则拼下载地址，供 UI 构造 <see cref="JavaRuntimePackage"/>。</summary>
    public static string BuildAdoptiumUrl(int majorVersion, string architecture)
    {
        var os = PlatformService.IsWindows ? "windows" : PlatformService.IsMacOs ? "mac" : "linux";
        var arch = NormalizeArchitecture(architecture);
        return $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/{os}/{arch}/jre/hotspot/normal/eclipse";
    }

    // ---------------- 内部实现 ----------------

    /// <summary>
    /// 枚举本机可能藏着 Java 的路径。不做子进程探测，只查文件存在性。
    ///
    /// 顺序即优先级：显式配置（环境变量）→ 平台安装目录与官方 runtime → PATH。
    /// 候选超过 <see cref="MaxCandidates"/> 时从尾部截断，所以 PATH 排在最后，
    /// 避免大量 PATH 条目把 <c>NEBULA_JAVA_PATHS</c> / <c>JAVA_HOME</c> 挤掉。
    /// 公开是为了让设置页的诊断与单元测试能直接看候选列表。
    /// </summary>
    public IReadOnlyList<string> CollectCandidates()
    {
        EnsureUserJavaLists();

        var candidates = new List<string>(48);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);

            // 排除优先：用户点过「移除」的 Java 不该从别的候选源（PATH、JAVA_HOME、官方 runtime）再钻回来。
            if (IsExcluded(full))
            {
                return;
            }

            if (seen.Add(full))
            {
                candidates.Add(full);
            }
        }

        // 0) 用户手动添加的 Java 排在最前：候选上限从尾部截断时不会把它们挤掉。
        foreach (var manual in _manualList.Paths)
        {
            AddCandidate(manual);
        }

        // 1) 显式配置优先。
        //    NEBULA_JAVA_PATHS 的语义照抄 HMCL 的 HMCL_JRES（JavaManager.java:463-471）：
        //    Path.PathSeparator 分隔多个条目，每个是 Java 安装根目录；顺手容忍直接写可执行文件。
        foreach (var entry in SplitPathList(Environment.GetEnvironmentVariable("NEBULA_JAVA_PATHS")))
        {
            if (File.Exists(entry))
            {
                AddCandidate(entry);
                continue;
            }

            foreach (var executable in EnumerateJavaInHome(entry))
            {
                AddCandidate(executable);
            }
        }

        foreach (var envName in new[] { "JAVA_HOME", "JDK_HOME" })
        {
            var home = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(home))
            {
                foreach (var executable in EnumerateJavaInHome(home))
                {
                    AddCandidate(executable);
                }
            }
        }

        // 2) 平台安装目录 + Mojang 官方 runtime 布局。
        foreach (var root in _platform.JavaSearchRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            // runtime/<component>/<platform>/<component>/bin/javaw.exe 四层，与通用展开并列。
            foreach (var executable in ExpandOfficialRuntimeLayout(root))
            {
                AddCandidate(executable);
            }

            foreach (var executable in EnumerateJavaInHome(root))
            {
                AddCandidate(executable);
            }

            foreach (var sub in SafeEnumerateDirectories(root))
            {
                foreach (var executable in EnumerateJavaInHome(sub))
                {
                    AddCandidate(executable);
                }

                // 多数发行版是 <root>/<发行商>/<jdk-x.y.z>/bin，再下一层。
                foreach (var deeper in SafeEnumerateDirectories(sub))
                {
                    foreach (var executable in EnumerateJavaInHome(deeper))
                    {
                        AddCandidate(executable);
                    }
                }
            }
        }

        // 3) PATH。Windows 上跳过 Oracle 的 javapath 桩目录（JavaManager.java:449-454 的同一条判据）。
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0 || IsOracleJavapathEntry(entry))
            {
                continue;
            }

            AddCandidate(Path.Combine(entry, _platform.JavaExecutableName));

            if (!PlatformService.IsWindows)
            {
                AddCandidate(Path.Combine(entry, "java"));
            }
        }

        return candidates.Count > MaxCandidates ? candidates.GetRange(0, MaxCandidates) : candidates;
    }

    /// <summary>
    /// 展开 Mojang 官方 runtime 布局：<c>&lt;root&gt;/&lt;component&gt;/&lt;platform&gt;/&lt;component&gt;/bin/java[w].exe</c>。
    ///
    /// 与 HMCL <c>JavaManager.tryAddJavaInComponentDir</c>（JavaManager.java:729-768）同构，
    /// component 可以是 <c>jre-legacy</c> / <c>java-runtime-alpha…epsilon</c> 或未来新增的任何名字。
    ///
    /// 与 HMCL 的差异：不做 <c>&lt;component&gt;.sha1</c> 清单校验。那份校验会逐行检查组件里每个文件是否存在，
    /// 几十个 runtime 加起来是上千次 IO，放在启动路径上不划算；这里只看可执行文件是否存在，
    /// 真正的版本号由 <see cref="ProbeAsync"/> 跑一次子进程拿到。
    /// </summary>
    public static IReadOnlyList<string> ExpandOfficialRuntimeLayout(string? runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();

        foreach (var component in SafeEnumerateDirectories(runtimeRoot))
        {
            var componentName = Path.GetFileName(component);
            if (string.IsNullOrEmpty(componentName))
            {
                continue;
            }

            foreach (var platformKey in OfficialRuntimePlatformKeys)
            {
                var home = Path.Combine(component, platformKey, componentName);
                var executable = FindJavaExecutableIn(home);

                // macOS 的官方 runtime 多一层 jre.bundle/Contents/Home（JavaManager.java:758-766）。
                if (executable is null && PlatformService.IsMacOs)
                {
                    executable = FindJavaExecutableIn(Path.Combine(home, "jre.bundle", "Contents", "Home"));
                }

                if (!string.IsNullOrEmpty(executable))
                {
                    result.Add(executable);
                }
            }
        }

        return result;
    }

    /// <summary>PATH 条目是否指向 Oracle 的 javapath 桩目录（其中的 java 会指向自动更新的 JRE，版本不可信）。</summary>
    public static bool IsOracleJavapathEntry(string? path) =>
        PlatformService.IsWindows
        && !string.IsNullOrWhiteSpace(path)
        && path.Contains(OracleJavapathMarker, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> EnumerateJavaInHome(string homeDirectory)
    {
        var bin = Path.Combine(homeDirectory, "bin");
        if (!Directory.Exists(bin))
        {
            yield break;
        }

        var preferred = Path.Combine(bin, _platform.JavaExecutableName);
        if (File.Exists(preferred))
        {
            yield return preferred;
            yield break;
        }

        if (PlatformService.IsWindows)
        {
            var fallback = Path.Combine(bin, "java.exe");
            if (File.Exists(fallback))
            {
                yield return fallback;
            }
        }
    }

    /// <summary>在 <c>&lt;home&gt;/bin</c> 下找 java 可执行文件（静态展开用，不依赖平台服务实例）。</summary>
    private static string? FindJavaExecutableIn(string homeDirectory)
    {
        var bin = Path.Combine(homeDirectory, "bin");

        if (PlatformService.IsWindows)
        {
            var javaw = Path.Combine(bin, "javaw.exe");
            if (File.Exists(javaw))
            {
                return javaw;
            }

            var java = Path.Combine(bin, "java.exe");
            return File.Exists(java) ? java : null;
        }

        var unix = Path.Combine(bin, "java");
        return File.Exists(unix) ? unix : null;
    }

    /// <summary>按 <see cref="Path.PathSeparator"/> 切分环境变量里的路径列表。</summary>
    private static IEnumerable<string> SplitPathList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0);
    }

    /// <summary>Java 主版本是否落在允许区间内；区间为 null（未知版本）时一律放行。</summary>
    private static bool IsAllowed(JavaInfo info, (int Min, int Max)? range) =>
        range is not { } allowed
        || (info.MajorVersion >= allowed.Min && info.MajorVersion <= allowed.Max);

    /// <summary>把本机已有 Java 写成 <c>8、17、21</c>，用于「需要 Java 21，本机只有 8、17」这类日志。</summary>
    private static string DescribeInstalled(IReadOnlyList<JavaInfo> installed)
    {
        var majors = installed
            .Select(info => info.MajorVersion)
            .Distinct()
            .OrderBy(major => major)
            .ToList();

        return majors.Count == 0 ? "无" : string.Join("、", majors);
    }

    /// <summary>
    /// 从版本 id 猜加载器：<c>1.12.2-forge-14.23.5.2859</c> → Forge、<c>neoforge-…</c> → NeoForge。
    /// 命名约定与 <c>Versions/VersionService.cs:433 DetectLoader</c> 一致；
    /// <see cref="IJavaService.ResolveForVersionAsync"/> 的签名里没有加载器参数，只能这样补。
    /// </summary>
    private static LoaderKind DetectLoaderKind(string? versionId)
    {
        var lower = (versionId ?? string.Empty).ToLowerInvariant();

        // neoforge 里含 "forge"，必须先判断。
        if (lower.Contains("neoforge", StringComparison.Ordinal))
        {
            return LoaderKind.NeoForge;
        }

        if (lower.Contains("quilt", StringComparison.Ordinal))
        {
            return LoaderKind.Quilt;
        }

        if (lower.Contains("fabric", StringComparison.Ordinal))
        {
            return LoaderKind.Fabric;
        }

        return lower.Contains("forge", StringComparison.Ordinal) ? LoaderKind.Forge : LoaderKind.Vanilla;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizeArchitecture(string? architecture)
    {
        var value = (architecture ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "x86_64" or "amd64" or "x64" or "" => "x64",
            "aarch64" or "arm64" => "aarch64",
            "x86" or "i386" or "i686" => "x86",
            _ => value,
        };
    }

    private static string NormalizeArchiveType(string? archiveType, string url)
    {
        var value = (archiveType ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (value is "zip" or "tar.gz" or "tgz" or "tar")
        {
            return value == "tgz" ? "tar.gz" : value;
        }

        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return "zip";
        }

        return "tar.gz";
    }

    private static string? ResolveExecutable(string directory, string relativePath)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            candidates.Add(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        if (PlatformService.IsWindows)
        {
            candidates.Add(Path.Combine(directory, "bin", "javaw.exe"));
            candidates.Add(Path.Combine(directory, "bin", "java.exe"));
        }
        else
        {
            candidates.Add(Path.Combine(directory, "bin", "java"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void ExtractArchive(string archivePath, string archiveType, string destination)
    {
        switch (archiveType)
        {
            case "zip":
                ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
                break;
            case "tar":
                TarFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
                break;
            default:
                using (var file = File.OpenRead(archivePath))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
                }

                break;
        }
    }

    /// <summary>把 staging 下的内容（通常只有一层顶层目录）搬到最终目录。</summary>
    private static void PromoteToTarget(string staging, string target)
    {
        var entries = Directory.GetFileSystemEntries(staging);
        Directory.CreateDirectory(target);

        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            MoveDirectoryContents(entries[0], target);
            return;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(target, name);
            if (Directory.Exists(entry))
            {
                MoveDirectoryContents(entry, destination);
            }
            else
            {
                File.Move(entry, destination, overwrite: true);
            }
        }
    }

    private static void MoveDirectoryContents(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = file.Replace(source, target, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 缓存目录残留无害。
        }
    }

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            // 只保留属性名形如 java.version 的行，避免把 banner 的「=」误当属性。
            if (key.Contains(' ') || key.Contains('"'))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string? Get(Dictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value : null;

    private static string ParseVersionFromBanner(string output)
    {
        var match = Regex.Match(output, "version \"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string GuessVendor(string output)
    {
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstLine))
        {
            return string.Empty;
        }

        return firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string GuessHome(string javaPath)
    {
        var bin = Path.GetDirectoryName(javaPath);
        return bin is null ? string.Empty : Path.GetDirectoryName(bin) ?? string.Empty;
    }

    /// <summary>把 <c>1.8.0_392</c> 解析为 8，<c>17.0.8+8</c> 解析为 17。</summary>
    public static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return 0;
        }

        var match = Regex.Match(version, @"(\d+)");
        if (!match.Success)
        {
            return 0;
        }

        var first = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (first != 1)
        {
            return first;
        }

        var legacy = Regex.Match(version, @"^1\.(\d+)");
        return legacy.Success ? int.Parse(legacy.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
    }

    /// <summary>
    /// 把 Java 版本串拆成「主版本 + 更新号」，主版本部分复用 <see cref="ParseMajor"/>（不新写一套规则）。
    ///
    /// 更新号的判定：
    /// <list type="bullet">
    /// <item><c>1.8.0_322</c> → 8 / 322。Java 8 的更新号在下划线后面，这是唯一带 <c>_</c> 的写法。</item>
    /// <item><c>11.0.20</c> → 11 / 20。九代以后的版本串是 <c>major.minor.patch</c>，第三段就是更新号。</item>
    /// <item><c>17.0.1+12</c> → 17 / 1。<c>+</c> 后面是 build 号（BUILD 12），不是更新号，
    /// 拿它去比「8u320 这类补丁上限」会得出完全错误的结论，所以先截掉。</item>
    /// <item><c>8</c> / <c>17</c> / 空串 / 垃圾串 → 更新号为 null（只知道主版本，不猜更新号）。</item>
    /// </list>
    ///
    /// 更新号为 null 时 <see cref="JavaLoadLimit.Exceeds(int, int?)"/> 一律判「未超出」——
    /// 补丁号级的上限只是建议，信息不足时宁可漏一次提示，也不能把一个能跑的运行时标成有问题。
    /// </summary>
    internal static (int Major, int? Update) ParseJavaVersion(string? version)
    {
        var major = ParseMajor(version);

        if (string.IsNullOrWhiteSpace(version) || major <= 0)
        {
            return (major, null);
        }

        var text = version.Trim();

        // Java 8 及更早：更新号在下划线后（1.8.0_322 / 1.7.0_80）。
        var underscored = Regex.Match(text, @"_(\d+)");
        if (underscored.Success
            && int.TryParse(
                underscored.Groups[1].Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var underscoredUpdate))
        {
            return (major, underscoredUpdate);
        }

        // 先砍掉构建号：17.0.1+12 的更新号是 1，不是 12。
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var segments = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 需要三段才有「更新号」：1.8.0 的第三段是 0（Java 8 的更新号在下划线里，已经处理过），
        // 11.0.20 / 17.0.1 的第三段才是真正的更新号。
        if (segments.Length < 3)
        {
            return (major, null);
        }

        return int.TryParse(
            segments[2],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var update)
            ? (major, update)
            : (major, null);
    }

    private async Task<string?> RunAsync(string fileName, string arguments, int timeoutMs, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 已经自行退出。
            }

            _log?.Debug($"探测超时：{fileName}");
            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout + "\n" + stderr;
    }
}
