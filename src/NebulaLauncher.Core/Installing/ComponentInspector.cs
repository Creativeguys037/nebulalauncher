using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 安装清单的组件检查器。纯读盘：不下载、不写清单、不改任何文件。
///
/// 分组规则：以 <see cref="McInstallManifest.ManagedFiles"/> 的 <c>Component</c> 为准，
/// 而不是只看 <see cref="McInstallManifest.Components"/>——写清单时 <c>libraries</c> 只会出现在
/// 受管文件的 <c>Component</c> 上（原版与加载器的文件记 <c>minecraft</c>），
/// 只按 Components 分组会把「库」整块漏掉。
///
/// 判据与 <see cref="McInstallManifest"/> 复核模组时一致：文件不存在 = 缺失；
/// 文件在但 mtime 与记录相差超过 2 秒 = 被改动（容差为跨文件系统的取整留余量）。
/// </summary>
public sealed class ComponentInspector : IComponentInspector
{
    /// <summary>
    /// 与 <see cref="McInstallManifest"/> 内部使用的容差保持一致。
    /// 两处必须是同一个数字，否则「检查说被改过、修复说没问题」会同时出现在界面上。
    /// </summary>
    private const double MtimeToleranceSeconds = 2;

    /// <summary>缺失 / 改动文件最多各列这么多条，计数不受影响。</summary>
    private const int MaxListedFiles = 50;

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public ComponentInspector(ILauncherPaths paths, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
    }

    /// <inheritdoc />
    public async Task<ComponentInspectionReport> InspectAsync(
        string versionName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return ComponentInspectionReport.Unavailable(string.Empty, "版本名为空，无法检查组件。");
        }

        var name = versionName.Trim();
        var versionDirectory = Path.Combine(_paths.VersionsDirectory, name);

        if (!Directory.Exists(versionDirectory))
        {
            return ComponentInspectionReport.Unavailable(name, $"找不到版本目录：{versionDirectory}");
        }

        McInstallManifest? manifest;

        try
        {
            manifest = await McInstallManifest.LoadAsync(versionDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取安装清单失败：{name}", ex);
            return ComponentInspectionReport.Unavailable(name, $"读取安装清单失败：{ex.GetBaseException().Message}");
        }

        if (manifest is null)
        {
            // 不返回「0 组件、0 缺失」的空报告：那会让用户以为版本完好无损。
            return ComponentInspectionReport.Unavailable(
                name,
                "该版本没有安装清单，无法按组件检查（重装该版本后会生成清单）。");
        }

        var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);

        // 先登记 Components 里声明过的组件：没有受管文件的组件（例如只装了个加载器壳）
        // 也要出现在列表里，否则用户会以为它没装上。
        foreach (var component in manifest.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Name))
            {
                continue;
            }

            var bucket = GetBucket(buckets, component.Name);
            bucket.Version ??= component.Version;
            bucket.InstalledUtc ??= component.InstalledUtc;
        }

        var missingFiles = new List<string>(Math.Min(MaxListedFiles, manifest.ManagedFiles.Count));
        var modifiedFiles = new List<string>(Math.Min(MaxListedFiles, manifest.ManagedFiles.Count));
        var missingCount = 0;
        var modifiedCount = 0;

        foreach (var file in manifest.ManagedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var componentName = string.IsNullOrWhiteSpace(file.Component) ? "unknown" : file.Component;
            var bucket = GetBucket(buckets, componentName);
            bucket.FileCount++;

            var status = CheckFile(file);

            if (status != FileStatus.Healthy)
            {
                var display = string.IsNullOrWhiteSpace(file.RelativePath) ? "(空路径)" : file.RelativePath;

                if (status == FileStatus.Missing)
                {
                    missingCount++;
                    bucket.MissingCount++;

                    if (missingFiles.Count < MaxListedFiles)
                    {
                        missingFiles.Add(display);
                    }
                }
                else
                {
                    modifiedCount++;
                    bucket.ModifiedCount++;

                    if (modifiedFiles.Count < MaxListedFiles)
                    {
                        modifiedFiles.Add(display);
                    }
                }
            }

            if (file.LastWriteTimeUtc > DateTimeOffset.MinValue)
            {
                bucket.LastWriteUtc = bucket.LastWriteUtc is { } current
                    ? (current > file.LastWriteTimeUtc ? current : file.LastWriteTimeUtc)
                    : file.LastWriteTimeUtc;
            }
        }

        var components = buckets.Values
            .OrderBy(bucket => OrderOf(bucket.Name))
            .ThenBy(bucket => bucket.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(bucket => new ComponentStatus(
                bucket.Name,
                bucket.Version,
                KindOf(bucket.Name),
                bucket.FileCount,
                bucket.MissingCount,
                bucket.ModifiedCount,
                bucket.LastWriteUtc ?? bucket.InstalledUtc))
            .ToList();

        var report = new ComponentInspectionReport(
            name,
            true,
            null,
            components,
            missingFiles,
            modifiedFiles,
            missingCount,
            modifiedCount,
            manifest.ManagedFiles.Count);

        _log?.Info(
            $"组件检查：{name} 共 {components.Count} 个组件 · 受管文件 {report.ManagedFileCount} · 缺失 {missingCount} · 改动 {modifiedCount}");

        return report;
    }

    /// <summary>单个受管文件的状态。缺失与改动分开，是因为它们的修法不同。</summary>
    private enum FileStatus
    {
        Healthy = 0,
        Missing = 1,
        Modified = 2,
    }

    private FileStatus CheckFile(McInstallManifestFile file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
            return FileStatus.Missing;
        }

        string path;

        try
        {
            // 清单里的路径相对游戏根目录，分隔符统一是 '/'（与 PCL2 写出的形式一致）。
            path = Path.Combine(_paths.MinecraftDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (ArgumentException)
        {
            // 手改坏的清单可能出现非法字符：算缺失，不要抛给页面。
            return FileStatus.Missing;
        }

        try
        {
            if (!File.Exists(path))
            {
                return FileStatus.Missing;
            }

            var recorded = file.LastWriteTimeUtc.UtcDateTime;
            var actual = File.GetLastWriteTimeUtc(path);

            return Math.Abs((actual - recorded).TotalSeconds) <= MtimeToleranceSeconds
                ? FileStatus.Healthy
                : FileStatus.Modified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到 mtime（被独占 / 权限）在检查语义上等价于「这个文件不可信」。
            return FileStatus.Missing;
        }
    }

    private static Bucket GetBucket(Dictionary<string, Bucket> buckets, string name)
    {
        if (!buckets.TryGetValue(name, out var bucket))
        {
            bucket = new Bucket { Name = name };
            buckets[name] = bucket;
        }

        return bucket;
    }

    /// <summary>组件名的中文种类。UI 直接显示，不在页面里重写一遍映射。</summary>
    public static string KindOf(string name) => name.ToLowerInvariant() switch
    {
        "minecraft" => "原版",
        "fabric" or "quilt" or "forge" or "neoforge" => "加载器",
        "libraries" or "library" => "库",
        "assets" => "资源",
        "mod" or "mods" => "模组",
        "unknown" => "未归类",
        _ => "其他",
    };

    /// <summary>列表排序：原版 → 加载器 → 库 → 资源 → 模组 → 其他。数量最多的库排在中间，不遮挡组件结构。</summary>
    private static int OrderOf(string name) => name.ToLowerInvariant() switch
    {
        "minecraft" => 0,
        "fabric" or "quilt" or "forge" or "neoforge" => 1,
        "libraries" or "library" => 2,
        "assets" => 3,
        "mod" or "mods" => 4,
        _ => 5,
    };

    private sealed class Bucket
    {
        public string Name { get; init; } = string.Empty;

        public string? Version { get; set; }

        public DateTimeOffset? InstalledUtc { get; set; }

        public int FileCount { get; set; }

        public int MissingCount { get; set; }

        public int ModifiedCount { get; set; }

        public DateTimeOffset? LastWriteUtc { get; set; }
    }
}
