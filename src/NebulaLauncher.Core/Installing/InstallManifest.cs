using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 版本目录的安装清单，落在 <c>versions/&lt;id&gt;/PCL/install-manifest.json</c>。
///
/// 为什么需要它：修复功能要回答「这个文件还是我装进去的那个吗」。只靠 SHA1 得全量重算，
/// 1.12.2 这类版本有上千个文件，每次打开版本页都算一遍不现实。PCL2 的
/// <c>PCL\PCLCS\Minecraft\McInstallManifest.cs</c> 用「安装时记录的最后写入时间（mtime）」做廉价判据：
/// mtime 变了说明文件被替换过，对应的组件记录就不再成立。字段名与 PCL2 对齐，便于两边互通。
/// </summary>
public sealed class McInstallManifest
{
    /// <summary>清单 schema 版本。字段增删时递增。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>清单相对版本目录的路径。PCL2 用 <c>PCL</c> 子目录收纳自有元数据。</summary>
    public static string RelativePath => Path.Combine("PCL", "install-manifest.json");

    /// <summary>
    /// mtime 比对容差。
    ///
    /// 同一文件系统上「记录时读一次、比对时再读一次」的值是稳定的，容差只为跨文件系统
    /// （FAT 的 2 秒粒度、复制 / 解压造成的取整）留余量，避免把「没动过」判成「动过」。
    /// </summary>
    private const double MtimeToleranceSeconds = 2;

    private const string ModComponent = "mod";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = new InstallManifestJsonContext(),
    };

    /// <summary>清单格式版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>该版本由哪些组件组成（minecraft / fabric / forge / mod …）。</summary>
    public List<McInstallComponent> Components { get; set; } = new();

    /// <summary>清单最后一次写入时间。</summary>
    public DateTimeOffset LastModifiedUtc { get; set; }

    /// <summary>受管文件。路径相对游戏根目录。</summary>
    public List<McInstallManifestFile> ManagedFiles { get; set; } = new();

    /// <summary>某个版本目录下清单的绝对路径。</summary>
    public static string GetPath(string versionDirectory) =>
        Path.Combine(versionDirectory, RelativePath);

    /// <summary>
    /// 读取清单。文件不存在、JSON 损坏、结构不可用时返回 null——清单是缓存性质的元数据，
    /// 坏掉的清单不能阻断修复流程（修复会按版本 JSON 重新校验并重写）。
    /// </summary>
    public static async Task<McInstallManifest?> LoadAsync(
        string versionDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionDirectory))
        {
            return null;
        }

        var path = GetPath(versionDirectory);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, JsonOptions.Of<McInstallManifest>());
            if (manifest is null || manifest.SchemaVersion > CurrentSchemaVersion)
            {
                return null;
            }

            // System.Text.Json 对缺失集合字段会保留属性初始化器，但显式写 null 的 JSON 不会。
            manifest.Components ??= new List<McInstallComponent>();
            manifest.ManagedFiles ??= new List<McInstallManifestFile>();
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>写清单。先写临时文件再改名，避免中途失败留下半截 JSON。</summary>
    public async Task SaveAsync(string versionDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);

        LastModifiedUtc = DateTimeOffset.UtcNow;

        var path = GetPath(versionDirectory);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(this, JsonOptions.Of<McInstallManifest>());
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 按 mtime 复核模组类组件是否还在。
    ///
    /// 「组件还在」的判据是：清单里该组件名下的受管文件仍存在，且 mtime 与安装时记录一致。
    /// 只要一个文件被删或被动过，就把这条受管文件连同「名下已无文件的组件」一起清掉，
    /// 让后续 UI 显示「组件已失效」而不是继续声称装过。
    /// </summary>
    /// <param name="minecraftDirectory">游戏根目录，清单里的 <c>RelativePath</c> 相对它。</param>
    /// <returns>被判为失效并移除的受管文件数。</returns>
    public int RefreshModComponents(string minecraftDirectory)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
        {
            return 0;
        }

        var invalid = new List<McInstallManifestFile>();

        foreach (var file in ManagedFiles)
        {
            if (!IsModComponent(file.Component))
            {
                continue;
            }

            if (!IsStillPresent(minecraftDirectory, file))
            {
                invalid.Add(file);
            }
        }

        if (invalid.Count == 0)
        {
            return 0;
        }

        foreach (var file in invalid)
        {
            ManagedFiles.Remove(file);
        }

        var liveComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ManagedFiles)
        {
            if (!string.IsNullOrWhiteSpace(file.Component))
            {
                liveComponents.Add(file.Component);
            }
        }

        Components.RemoveAll(component => IsModComponent(component.Name) && !liveComponents.Contains(component.Name));

        return invalid.Count;
    }

    /// <summary>
    /// 两份清单是否等价。忽略 <see cref="LastModifiedUtc"/>（每次写都会变），
    /// 只比较组件集合与受管文件（路径 + SHA1 + mtime），用于「内容没变就别再写盘」。
    /// </summary>
    public bool IsEquivalentTo(McInstallManifest? other)
    {
        if (other is null || SchemaVersion != other.SchemaVersion)
        {
            return false;
        }

        if (Components.Count != other.Components.Count || ManagedFiles.Count != other.ManagedFiles.Count)
        {
            return false;
        }

        var mine = Components
            .Select(component => (component.Name, component.Version))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var theirs = other.Components
            .Select(component => (component.Name, component.Version))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < mine.Length; i++)
        {
            if (!string.Equals(mine[i].Name, theirs[i].Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(mine[i].Version, theirs[i].Version, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var myFiles = ToComparable(ManagedFiles);
        var theirFiles = ToComparable(other.ManagedFiles);

        for (var i = 0; i < myFiles.Length; i++)
        {
            if (myFiles[i] != theirFiles[i])
            {
                return false;
            }
        }

        return true;

        static string[] ToComparable(IEnumerable<McInstallManifestFile> files) =>
            files
                .Select(file => string.Join(
                    '|',
                    file.RelativePath,
                    file.Sha1 ?? string.Empty,
                    file.LastWriteTimeUtc.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    file.Component))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static bool IsModComponent(string? component) =>
        string.Equals(component, ModComponent, StringComparison.OrdinalIgnoreCase);

    private static bool IsStillPresent(string minecraftDirectory, McInstallManifestFile file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
            return false;
        }

        string path;
        try
        {
            path = Path.Combine(minecraftDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var recorded = file.LastWriteTimeUtc.UtcDateTime;
            var actual = File.GetLastWriteTimeUtc(path);
            return Math.Abs((actual - recorded).TotalSeconds) <= MtimeToleranceSeconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>清单里的一个组件。PCL2 用它描述「这个版本装了哪些东西、各自哪个版本」。</summary>
public sealed class McInstallComponent
{
    /// <summary>组件名：<c>minecraft</c> / <c>fabric</c> / <c>forge</c> / <c>mod</c> …</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>组件版本；原版以外的组件可能为空。</summary>
    public string? Version { get; set; }

    /// <summary>写入该组件记录的时间。</summary>
    public DateTimeOffset? InstalledUtc { get; set; }
}

/// <summary>清单里的一个受管文件。</summary>
public sealed class McInstallManifestFile
{
    /// <summary>相对游戏根目录的路径，分隔符统一用 <c>/</c>（与 PCL2 写出的形式一致）。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>文件内容的 SHA1；未知时为 null（大文件不重算，直接取版本 JSON 里声明的值）。</summary>
    public string? Sha1 { get; set; }

    /// <summary>安装时的最后写入时间，用于廉价判断文件是否被动过。</summary>
    public DateTimeOffset LastWriteTimeUtc { get; set; }

    /// <summary>归属组件名。</summary>
    public string Component { get; set; } = string.Empty;
}

/// <summary>
/// 安装清单的 Source Generator 上下文。
///
/// 这张票（#26）最早暴露的就是这条链路：清单写不进去意味着重装时无法判断「哪些文件是我装的」，
/// 会在用户目录里留下无法回收的孤儿文件。
/// </summary>
[JsonSerializable(typeof(McInstallManifest))]
internal sealed partial class InstallManifestJsonContext : JsonSerializerContext
{
}
