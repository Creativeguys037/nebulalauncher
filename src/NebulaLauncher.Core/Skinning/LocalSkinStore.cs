using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Skinning;

/// <summary>皮肤的模型类型。Core 不认识 App 侧的显示名，只区分手臂粗细。</summary>
public enum LocalSkinModel
{
    /// <summary>经典（Steve）模型，手臂 4 像素宽。</summary>
    Classic,

    /// <summary>细手臂（Alex）模型，手臂 3 像素宽。</summary>
    Slim,
}

/// <summary>
/// 皮肤 PNG 的格式与尺寸规则。
///
/// 为什么自己读 IHDR 而不解码：校验只需要宽高，解码一张 64×64 的图要几百 KB 内存与一次完整的
/// zlib 解压，而扫描一百个文件时这些开销全落在启动路径上。PNG 的宽高就在固定偏移（16、20）的
/// big-endian int32 里，读 24 个字节就够。
/// </summary>
public static class LocalSkinFormat
{
    /// <summary>PNG 文件头：89 50 4E 47 0D 0A 1A 0A。</summary>
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>从 PNG 内容里读出宽高。文件头不对或尺寸非正时返回 false。</summary>
    public static bool TryReadPngSize(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 8 字节签名 + 4 字节 chunk 长度 + 4 字节 "IHDR" + 8 字节宽高 = 24。
        if (content.Length < 24 || !content[..8].SequenceEqual(Signature))
        {
            return false;
        }

        // 第一块必须是 IHDR，否则这不是一张能当皮肤用的 PNG。
        if (!content.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var parsedWidth = BinaryPrimitives.ReadInt32BigEndian(content.Slice(16, 4));
        var parsedHeight = BinaryPrimitives.ReadInt32BigEndian(content.Slice(20, 4));
        if (parsedWidth <= 0 || parsedHeight <= 0)
        {
            return false;
        }

        width = parsedWidth;
        height = parsedHeight;
        return true;
    }

    /// <summary>
    /// 校验尺寸是否能用。返回 null 表示通过，否则是给用户看的中文原因。
    ///
    /// 规则来自游戏本身：皮肤按 64×64 贴，旧版是 64×32，所以宽必须 64 的倍数、高必须 32 的倍数；
    /// 细手臂模型的手臂在 64×64 的内层框里，其他尺寸放不下，只能恰好 64×64。
    /// </summary>
    public static string? Validate(int width, int height, LocalSkinModel model)
    {
        if (width <= 0 || height <= 0)
        {
            return "皮肤图尺寸无效，宽高都必须是正数。";
        }

        if (model == LocalSkinModel.Slim && (width != 64 || height != 64))
        {
            return $"细手臂（Alex）皮肤必须恰好 64×64，这张是 {width}×{height}。";
        }

        if (width % 64 != 0)
        {
            return $"皮肤宽度必须是 64 的倍数，这张是 {width} 像素宽。";
        }

        if (height % 32 != 0)
        {
            return $"皮肤高度必须是 32 的倍数，这张是 {height} 像素高。";
        }

        return null;
    }
}

/// <summary>本地皮肤目录里的一个文件。路径是绝对路径，用户可能把它拷到别处去。</summary>
public sealed class LocalSkinEntry
{
    public LocalSkinEntry(
        string fileName,
        string filePath,
        int width,
        int height,
        LocalSkinModel model,
        DateTimeOffset addedAt)
    {
        FileName = fileName;
        FilePath = filePath;
        Width = width;
        Height = height;
        Model = model;
        AddedAt = addedAt;
    }

    public string FileName { get; }

    public string FilePath { get; }

    public int Width { get; }

    public int Height { get; }

    public LocalSkinModel Model { get; }

    /// <summary>进库时间。索引里有就用索引的，手放进来的文件用文件写入时间。</summary>
    public DateTimeOffset AddedAt { get; }
}

/// <summary>一次扫描的结果。部分文件坏掉不影响其它文件，坏掉的记在 <see cref="SkippedFileNames"/> 里。</summary>
public sealed class LocalSkinScanResult
{
    public LocalSkinScanResult(
        IReadOnlyList<LocalSkinEntry> entries,
        IReadOnlyList<string> skippedFileNames,
        string? errorText)
    {
        Entries = entries;
        SkippedFileNames = skippedFileNames;
        ErrorText = errorText;
    }

    public IReadOnlyList<LocalSkinEntry> Entries { get; }

    /// <summary>扩展名像 PNG 但读不出尺寸、或者尺寸不合规的文件名（不带路径）。</summary>
    public IReadOnlyList<string> SkippedFileNames { get; }

    /// <summary>整个目录都读不了时的错误文案；null 表示这次扫描没有出问题。</summary>
    public string? ErrorText { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
}

/// <summary>
/// 本地皮肤库：<c>&lt;数据根&gt;/skins</c>。
///
/// 为什么以目录为唯一事实来源、索引只做补充：用户完全可以绕开启动器往目录里丢文件（票面 AC4
/// 明确要求这种用法能用）。所以列表的权威来源永远是磁盘上的 PNG，索引只记住仓库记不住的东西——
/// 模型的粗细（尺寸分不出 slim）和进库时间。索引丢了、坏了、过时了，最坏退化成「全按经典模型显示」，
/// 而不是「皮肤不见了」。
/// </summary>
public sealed class LocalSkinStore
{
    private const string SkinsFolderName = "skins";
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new LocalSkinJsonContext(),
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public LocalSkinStore(ILauncherPaths paths, IAppLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _log = log;
    }

    /// <summary>本地皮肤目录的绝对路径。可能还不存在，扫描或导入会把它建出来。</summary>
    public string DirectoryPath => Path.Combine(_paths.RootDirectory, SkinsFolderName);

    /// <summary>索引文件的绝对路径。</summary>
    public string IndexPath => Path.Combine(DirectoryPath, IndexFileName);

    /// <summary>扫描本地皮肤目录。目录不存在时顺手建出来并返回空列表。</summary>
    public Task<LocalSkinScanResult> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(), cancellationToken);

    /// <summary>把磁盘上的一个 PNG 拷进本地皮肤目录。同名不覆盖，自动加序号。</summary>
    public Task<LocalSkinResult> ImportAsync(
        string sourcePath,
        LocalSkinModel? model = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return LocalSkinResult.Failure("找不到要导入的文件。");
            }

            try
            {
                var content = File.ReadAllBytes(sourcePath);
                var fileName = SanitizeFileName(Path.GetFileName(sourcePath));
                return SaveCore(fileName, content, model ?? LocalSkinModel.Classic);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log?.Warn($"导入本地皮肤失败：{sourcePath}", ex);
                return LocalSkinResult.Failure($"导入失败：{ex.Message}");
            }
        }, cancellationToken);

    /// <summary>把内存里的 PNG 字节存进本地皮肤目录（网络皮肤「下载到本地」走这里）。</summary>
    public Task<LocalSkinResult> SaveAsync(
        string fileName,
        byte[] content,
        LocalSkinModel? model = null,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                try
                {
                    return SaveCore(SanitizeFileName(fileName), content ?? [], model ?? LocalSkinModel.Classic);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _log?.Warn($"保存本地皮肤失败：{fileName}", ex);
                    return LocalSkinResult.Failure($"保存失败：{ex.Message}");
                }
            },
            cancellationToken);

    /// <summary>删除本地皮肤目录里的一个文件。文件已经不在了也算成功（幂等）。</summary>
    public Task<LocalSkinResult> DeleteAsync(string fileName, CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                var safeName = Path.GetFileName(fileName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(safeName) || !string.Equals(safeName, fileName, StringComparison.Ordinal))
                {
                    return LocalSkinResult.Failure("文件名不合法，已拒绝删除。");
                }

                try
                {
                    var target = Path.Combine(DirectoryPath, safeName);
                    if (!IsInsideDirectory(target))
                    {
                        return LocalSkinResult.Failure("文件名不合法，已拒绝删除。");
                    }

                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    var index = ReadIndex();
                    if (index.Remove(safeName))
                    {
                        WriteIndex(index);
                    }

                    return LocalSkinResult.Success(entry: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _log?.Warn($"删除本地皮肤失败：{fileName}", ex);
                    return LocalSkinResult.Failure($"删除失败：{ex.Message}");
                }
            },
            cancellationToken);

    private LocalSkinResult SaveCore(string fileName, byte[] content, LocalSkinModel model)
    {
        if (!LocalSkinFormat.TryReadPngSize(content, out var width, out var height))
        {
            return LocalSkinResult.Failure("这个文件不是有效的 PNG，没法当皮肤用。");
        }

        if (LocalSkinFormat.Validate(width, height, model) is { } error)
        {
            return LocalSkinResult.Failure(error);
        }

        Directory.CreateDirectory(DirectoryPath);
        var (reservedName, fullPath) = ReserveFileName(fileName);
        File.WriteAllBytes(fullPath, content);

        var addedAt = DateTimeOffset.Now;
        var index = ReadIndex();
        index[reservedName] = new LocalSkinIndexEntry
        {
            FileName = reservedName,
            Model = model,
            AddedAt = addedAt,
        };
        WriteIndex(index);

        return LocalSkinResult.Success(new LocalSkinEntry(reservedName, fullPath, width, height, model, addedAt));
    }

    /// <summary>同名文件已存在时依次尝试「名字 (2).png」「名字 (3).png」，最多到 99。</summary>
    private (string FileName, string FullPath) ReserveFileName(string fileName)
    {
        var candidate = fileName;
        for (var suffix = 2; suffix < 100 && File.Exists(Path.Combine(DirectoryPath, candidate)); suffix++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(fileName)} ({suffix}){Path.GetExtension(fileName)}";
        }

        return (candidate, Path.Combine(DirectoryPath, candidate));
    }

    /// <summary>只留文件名部分，去掉非法字符，保证扩展名是 .png。</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "skin.png";
        }

        return string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".png";
    }

    private bool IsInsideDirectory(string path)
    {
        var root = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase);
    }

    private LocalSkinScanResult Scan()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Warn($"创建本地皮肤目录失败：{DirectoryPath}", ex);
            return new LocalSkinScanResult([], [], $"读不了本地皮肤目录：{ex.Message}");
        }

        // 索引读失败只是一条日志：磁盘上的图才是事实来源，退化成全经典模型即可。
        var index = ReadIndex();

        var entries = new List<LocalSkinEntry>();
        var skipped = new List<string>();

        List<string> files;
        try
        {
            // 不用 "*.png" 通配符：它在大小写敏感的文件系统上会漏掉 ALEX.PNG。先全取再按扩展名比。
            files = Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Warn($"枚举本地皮肤目录失败：{DirectoryPath}", ex);
            return new LocalSkinScanResult([], [], $"读不了本地皮肤目录：{ex.Message}");
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            var size = TryReadFileSize(path);
            if (size is null)
            {
                skipped.Add(fileName);
                _log?.Warn($"本地皮肤不是有效 PNG，已跳过：{fileName}");
                continue;
            }

            index.TryGetValue(fileName, out var indexed);
            var model = indexed?.Model ?? LocalSkinModel.Classic;

            // 索引说它是 slim、但文件已经被换成别的尺寸：尺寸是硬的，模型是软的，以尺寸为准退回经典。
            if (LocalSkinFormat.Validate(size.Value.Width, size.Value.Height, model) is not null)
            {
                if (model == LocalSkinModel.Slim)
                {
                    model = LocalSkinModel.Classic;
                }

                if (LocalSkinFormat.Validate(size.Value.Width, size.Value.Height, model) is not null)
                {
                    skipped.Add(fileName);
                    _log?.Warn($"本地皮肤尺寸不合规，已跳过：{fileName}（{size.Value.Width}×{size.Value.Height}）");
                    continue;
                }
            }

            entries.Add(new LocalSkinEntry(
                fileName,
                path,
                size.Value.Width,
                size.Value.Height,
                model,
                indexed?.AddedAt ?? ReadWriteTime(path)));
        }

        // 新导入的排前面；同一批按文件名，保证列表顺序稳定。
        entries.Sort(static (left, right) =>
        {
            var byTime = right.AddedAt.CompareTo(left.AddedAt);
            return byTime != 0 ? byTime : string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
        });

        return new LocalSkinScanResult(entries, skipped, null);
    }

    private static DateTimeOffset ReadWriteTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static (int Width, int Height)? TryReadFileSize(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 24,
                FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[24];
            var read = 0;
            while (read < header.Length)
            {
                var chunk = stream.Read(header[read..]);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            return LocalSkinFormat.TryReadPngSize(header[..read], out var width, out var height)
                ? (width, height)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteIndex(Dictionary<string, LocalSkinIndexEntry> index)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var file = new LocalSkinIndexFile
            {
                Entries = [.. index.Values.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)],
            };
            var json = JsonSerializer.Serialize(file, JsonOptions.Of<LocalSkinIndexFile>());
            var temporary = IndexPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 索引写不进去不影响这次操作已经成功的文件：下次扫描退化成默认模型，皮肤还在。
            _log?.Warn($"写本地皮肤索引失败：{IndexPath}", ex);
        }
    }

    private Dictionary<string, LocalSkinIndexEntry> ReadIndex()
    {
        var index = new Dictionary<string, LocalSkinIndexEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(IndexPath))
            {
                return index;
            }

            var json = File.ReadAllText(IndexPath);
            var file = JsonSerializer.Deserialize(json, JsonOptions.Of<LocalSkinIndexFile>());
            foreach (var entry in file?.Entries ?? [])
            {
                if (!string.IsNullOrWhiteSpace(entry.FileName))
                {
                    index[entry.FileName] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 索引是补充信息，读不出来就当作没有：皮肤列表仍然能从磁盘恢复。
            _log?.Warn($"读本地皮肤索引失败，本次按默认模型显示：{IndexPath}", ex);
        }

        return index;
    }
}

/// <summary>导入 / 保存 / 删除共用的结果。<see cref="ErrorText"/> 为 null 表示成功。</summary>
public sealed class LocalSkinResult
{
    private LocalSkinResult(LocalSkinEntry? entry, string? errorText)
    {
        Entry = entry;
        ErrorText = errorText;
    }

    /// <summary>导入或保存成功后的条目；删除成功时为 null。</summary>
    public LocalSkinEntry? Entry { get; }

    /// <summary>失败原因（中文，可直接显示给用户）；成功时为 null。</summary>
    public string? ErrorText { get; }

    public bool IsSuccess => ErrorText is null;

    public static LocalSkinResult Success(LocalSkinEntry? entry) => new(entry, null);

    public static LocalSkinResult Failure(string errorText) => new(null, errorText);
}

/// <summary>本地皮肤索引文件。只记仓库自己记不住的信息，见 <see cref="LocalSkinStore"/> 的类型注释。</summary>
internal sealed class LocalSkinIndexFile
{
    public List<LocalSkinIndexEntry> Entries { get; set; } = [];
}

internal sealed class LocalSkinIndexEntry
{
    public string FileName { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter<LocalSkinModel>))]
    public LocalSkinModel Model { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

[JsonSerializable(typeof(LocalSkinIndexFile))]
internal sealed partial class LocalSkinJsonContext : JsonSerializerContext
{
}
