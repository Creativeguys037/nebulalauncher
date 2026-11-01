using System.Text.Json;
using System.Text.Json.Serialization;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Skinning;

/// <summary>披风 PNG 的尺寸规则。披风与皮肤是两种贴图布局，不能共用皮肤校验。</summary>
public static class LocalCapeFormat
{
    /// <summary>
    /// 披风只接受 Minecraft 的 64×32 标准布局，以及皮肤站仍常见的旧版 22×17 内容区。
    /// 22×17 会在渲染时被映射到 64×32 的左上角，不能把它当皮肤存进 skins 目录。
    /// </summary>
    public static string? Validate(int width, int height)
    {
        if (width == 64 && height == 32)
        {
            return null;
        }

        if (width == 22 && height == 17)
        {
            return null;
        }

        return $"披风 PNG 尺寸应为 64×32 或 22×17，当前是 {width}×{height}。";
    }
}

/// <summary>本地披风库里的一项。</summary>
public sealed record LocalCapeEntry(
    string FileName,
    string FilePath,
    int Width,
    int Height,
    DateTimeOffset AddedAt);

/// <summary>扫描本地披风目录的结果。<see cref="ErrorText"/> 为 null 表示目录可读。</summary>
public sealed record LocalCapeScanResult(
    IReadOnlyList<LocalCapeEntry> Entries,
    IReadOnlyList<string> SkippedFiles,
    string? ErrorText);

/// <summary>保存 / 删除本地披风的结果。</summary>
public sealed class LocalCapeResult
{
    private LocalCapeResult(LocalCapeEntry? entry, string? errorText)
    {
        Entry = entry;
        ErrorText = errorText;
    }

    public LocalCapeEntry? Entry { get; }

    public string? ErrorText { get; }

    public bool IsSuccess => ErrorText is null;

    public static LocalCapeResult Success(LocalCapeEntry? entry) => new(entry, null);

    public static LocalCapeResult Failure(string errorText) => new(null, errorText);
}

/// <summary>
/// 本地披风库：<c>&lt;数据根&gt;/capes</c>。
///
/// 为什么不复用 <see cref="LocalSkinStore"/>：皮肤校验按 64 的倍数算，22×17 的披风会被直接拒绝；
/// 而且披风列表不需要模型信息，单独目录才能让「下载到本地」的披风不污染皮肤列表。
/// </summary>
public sealed class LocalCapeStore
{
    private const string CapesFolderName = "capes";
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new LocalCapeJsonContext(),
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public LocalCapeStore(ILauncherPaths paths, IAppLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _log = log;
    }

    public string DirectoryPath => Path.Combine(_paths.RootDirectory, CapesFolderName);

    public string IndexPath => Path.Combine(DirectoryPath, IndexFileName);

    public Task<LocalCapeScanResult> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run(Scan, cancellationToken);

    public Task<LocalCapeResult> SaveAsync(
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                try
                {
                    return SaveCore(SanitizeFileName(fileName), content ?? []);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _log?.Warn($"保存本地披风失败：{fileName}", ex);
                    return LocalCapeResult.Failure($"保存失败：{ex.Message}");
                }
            },
            cancellationToken);

    public Task<LocalCapeResult> DeleteAsync(string fileName, CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                var safeName = Path.GetFileName(fileName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(safeName) || !string.Equals(safeName, fileName, StringComparison.Ordinal))
                {
                    return LocalCapeResult.Failure("文件名不合法，已拒绝删除。");
                }

                try
                {
                    var target = Path.Combine(DirectoryPath, safeName);
                    if (!IsInsideDirectory(target))
                    {
                        return LocalCapeResult.Failure("文件名不合法，已拒绝删除。");
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

                    return LocalCapeResult.Success(entry: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _log?.Warn($"删除本地披风失败：{fileName}", ex);
                    return LocalCapeResult.Failure($"删除失败：{ex.Message}");
                }
            },
            cancellationToken);

    private LocalCapeResult SaveCore(string fileName, byte[] content)
    {
        if (!LocalSkinFormat.TryReadPngSize(content, out var width, out var height))
        {
            return LocalCapeResult.Failure("这个文件不是有效的 PNG，没法当披风用。");
        }

        if (LocalCapeFormat.Validate(width, height) is { } error)
        {
            return LocalCapeResult.Failure(error);
        }

        Directory.CreateDirectory(DirectoryPath);
        var (reservedName, fullPath) = ReserveFileName(fileName);
        File.WriteAllBytes(fullPath, content);

        var addedAt = DateTimeOffset.Now;
        var index = ReadIndex();
        index[reservedName] = new LocalCapeIndexEntry { FileName = reservedName, AddedAt = addedAt };
        WriteIndex(index);

        return LocalCapeResult.Success(new LocalCapeEntry(reservedName, fullPath, width, height, addedAt));
    }

    private (string FileName, string FullPath) ReserveFileName(string fileName)
    {
        var candidate = fileName;
        for (var suffix = 2; suffix < 100 && File.Exists(Path.Combine(DirectoryPath, candidate)); suffix++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(fileName)} ({suffix}){Path.GetExtension(fileName)}";
        }

        return (candidate, Path.Combine(DirectoryPath, candidate));
    }

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
            name = "cape.png";
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
               && !string.Equals(full, Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase);
    }

    private LocalCapeScanResult Scan()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Warn($"创建本地披风目录失败：{DirectoryPath}", ex);
            return new LocalCapeScanResult([], [], $"读不了本地披风目录：{ex.Message}");
        }

        var index = ReadIndex();
        var entries = new List<LocalCapeEntry>();
        var skipped = new List<string>();

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Warn($"枚举本地披风目录失败：{DirectoryPath}", ex);
            return new LocalCapeScanResult([], [], $"读不了本地披风目录：{ex.Message}");
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
            if (size is null || LocalCapeFormat.Validate(size.Value.Width, size.Value.Height) is not null)
            {
                skipped.Add(fileName);
                _log?.Warn($"本地披风尺寸或格式不合规，已跳过：{fileName}");
                continue;
            }

            index.TryGetValue(fileName, out var indexed);
            entries.Add(new LocalCapeEntry(
                fileName,
                path,
                size.Value.Width,
                size.Value.Height,
                indexed?.AddedAt ?? ReadWriteTime(path)));
        }

        entries.Sort(static (left, right) =>
        {
            var byTime = right.AddedAt.CompareTo(left.AddedAt);
            return byTime != 0 ? byTime : string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
        });

        return new LocalCapeScanResult(entries, skipped, null);
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
            var bytes = File.ReadAllBytes(path);
            return LocalSkinFormat.TryReadPngSize(bytes, out var width, out var height)
                ? (width, height)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteIndex(Dictionary<string, LocalCapeIndexEntry> index)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var file = new LocalCapeIndexFile
            {
                Entries = [.. index.Values.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)],
            };
            var json = JsonSerializer.Serialize(file, JsonOptions.Of<LocalCapeIndexFile>());
            var temporary = IndexPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log?.Warn($"写本地披风索引失败：{IndexPath}", ex);
        }
    }

    private Dictionary<string, LocalCapeIndexEntry> ReadIndex()
    {
        var index = new Dictionary<string, LocalCapeIndexEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(IndexPath))
            {
                return index;
            }

            var json = File.ReadAllText(IndexPath);
            var file = JsonSerializer.Deserialize(json, JsonOptions.Of<LocalCapeIndexFile>());
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
            _log?.Warn($"读本地披风索引失败，本次按文件时间排序：{IndexPath}", ex);
        }

        return index;
    }
}

internal sealed class LocalCapeIndexFile
{
    public List<LocalCapeIndexEntry> Entries { get; set; } = [];
}

internal sealed class LocalCapeIndexEntry
{
    public string FileName { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }
}

[JsonSerializable(typeof(LocalCapeIndexFile))]
internal sealed partial class LocalCapeJsonContext : JsonSerializerContext
{
}
