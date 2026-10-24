using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Backups;

/// <summary>
/// 存档缩略图：从 <c>level.dat</c> 的 <c>Data.icon</c>（base64 编码的 PNG）提取，写到启动器缓存目录。
///
/// 为什么单独缓存而不是每次解码：世界页刷新一次就要遍历全部存档，图标是列表页的装饰性内容；
/// 每次刷新都重新读 level.dat、解 base64、解码 PNG，存档一多页面就明显卡。
/// 缓存键用「世界目录绝对路径的 SHA1」而不是世界名：世界名可以重名、也可以被玩家改（同名目录被换内容是常态），
/// 路径才是稳定标识；内容变更由 level.dat 的 (mtime, length) 元数据判定。
///
/// 失败一律返回 null 且不抛：坏图标只该让那张卡显示占位块，不该让整页读取失败。
/// </summary>
public sealed class WorldIconService : IWorldIconService
{
    /// <summary>原始 base64 字符串上限。真实图标（64×64 PNG）只有几 KB，96KB 已经远超正常值。</summary>
    internal const int MaxBase64Chars = 96 * 1024;

    /// <summary>解码后的 PNG 字节上限。超过这个尺寸的图标必然不是游戏写的缩略图。</summary>
    internal const int MaxPngBytes = 64 * 1024;

    /// <summary>缓存子目录名，落在 <see cref="ILauncherPaths.CacheDirectory"/> 下。</summary>
    internal const string CacheFolderName = "world-icons";

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public WorldIconService(ILauncherPaths paths, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
    }

    public Task<string?> GetIconPathAsync(string worldDirectory, CancellationToken cancellationToken = default)
    {
        // 读盘 + base64 解码放到线程池：世界页一次可能并发请求十几个存档图标。
        return Task.Run(() => TryGetIconPath(worldDirectory, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 只探测 <c>Data.icon</c> 标签是否存在，不解码 base64、不写缓存。
    ///
    /// 扫描世界列表时用：那份列表只需要知道「卡片要不要留缩略图位置」，
    /// 在这里解码大图标会把世界页的加载时间从毫秒级推到秒级。
    /// </summary>
    internal static bool HasIconTag(string levelDatPath)
    {
        var document = NbtReader.TryReadDocument(levelDatPath);

        // 值必须是 TAG_String：模组可能塞同名非字符串标签，GetString 对非字符串返回 null。
        // 空字符串同样按「没有图标」处理——它解不出任何 PNG，标成有只会让 UI 白跑一趟。
        return document?.Data?.GetString("icon") is { Length: > 0 };
    }

    /// <summary>
    /// 同步实现：路径不存在、图标缺失、格式非法、超限、写缓存失败都返回 null。
    /// </summary>
    internal string? TryGetIconPath(string worldDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worldDirectory))
        {
            return null;
        }

        string levelDatPath;
        string cachePath;
        try
        {
            // 目录不存在时 GetFullPath 仍会成功（它只做字符串规范化），所以下面用 File.Exists 判实际存在，
            // 不先判 Directory.Exists：那样反而会漏掉「目录在但 level.dat 被删」的正常缺图标情形。
            var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldDirectory));
            levelDatPath = Path.Combine(fullDirectory, "level.dat");
            cachePath = Path.Combine(
                _paths.CacheDirectory,
                CacheFolderName,
                HashPath(fullDirectory) + ".png");
        }
        catch (Exception ex)
        {
            // 非法路径（非法字符、超长、权限）：缓存路径算不出来就当没有图标。
            _log?.Warn($"计算存档图标缓存路径失败：{worldDirectory}", ex);
            return null;
        }

        if (!File.Exists(levelDatPath))
        {
            return null;
        }

        var document = NbtReader.TryReadDocument(levelDatPath);
        var base64 = document?.Data?.GetString("icon");
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        if (base64.Length > MaxBase64Chars)
        {
            _log?.Info($"存档图标原始 base64 超限，已跳过：{levelDatPath}（{base64.Length} 字符）");
            return null;
        }

        byte[] png;
        try
        {
            png = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // 损坏的图标不值得记 Error：真实世界里编辑过 level.dat 的工具会写出各种残渣。
            _log?.Info($"存档图标不是合法 base64，已跳过：{levelDatPath}");
            return null;
        }

        if (png.Length == 0 || png.Length > MaxPngBytes)
        {
            _log?.Info($"存档图标解码后超限，已跳过：{levelDatPath}（{png.Length} 字节）");
            return null;
        }

        if (!IsPng(png))
        {
            _log?.Info($"存档图标不是 PNG，已跳过：{levelDatPath}");
            return null;
        }

        return TryWriteCache(levelDatPath, png, cachePath, cancellationToken);
    }

    /// <summary>
    /// 缓存有效判据：png 在 + 旁记的 <c>.json</c> 里记录的 level.dat (mtime, length) 与当前一致。
    ///
    /// 为什么把元数据放旁记 json 而不是编进文件名：编进文件名意味着 level.dat 每变一次就多一个 png，
    /// 旧文件既不会命中也不会被清理；旁记文件只有两个数字，失配时原地覆盖，缓存目录里始终一世界一图。
    /// </summary>
    private string? TryWriteCache(string levelDatPath, byte[] png, string cachePath, CancellationToken cancellationToken)
    {
        var metaPath = Path.ChangeExtension(cachePath, ".json");
        string? tempPng = null;
        string? tempMeta = null;
        try
        {
            var levelDat = new FileInfo(levelDatPath);
            var length = levelDat.Length;
            var ticks = levelDat.LastWriteTimeUtc.Ticks;

            if (File.Exists(cachePath)
                && ReadMetadata(metaPath) is { } meta
                && meta.LevelDatLength == length
                && meta.LevelDatTicks == ticks)
            {
                // 命中：第二次调用不再写盘（测试用独占句柄验证过这一点）。
                return cachePath;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            // 先写临时文件再改名：直接原地写的话，进程在写一半时退出会留下截断的 png，
            // 而它同时带着「元数据匹配」的可能，会被当成有效缓存反复读。
            tempPng = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tempPng, png);
            File.Move(tempPng, cachePath, overwrite: true);
            tempPng = null;

            // 元数据最后落地：它是缓存的提交标记，png 写成功而元数据没写成时下次会重写而不是读半截内容。
            tempMeta = metaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tempMeta, JsonSerializer.SerializeToUtf8Bytes(new IconCacheMetadata(ticks, length)));
            File.Move(tempMeta, metaPath, overwrite: true);
            tempMeta = null;

            return cachePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"写入存档图标缓存失败：{cachePath}", ex);
            return null;
        }
        finally
        {
            TryDelete(tempPng);
            TryDelete(tempMeta);
        }
    }

    private static IconCacheMetadata? ReadMetadata(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<IconCacheMetadata>(File.ReadAllBytes(metaPath));
        }
        catch (Exception)
        {
            // 元数据损坏等价于没有缓存：重新写一份比修它便宜。
            return null;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉只是残留，不影响本次结果。
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= PngMagic.Length && bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic);

    /// <summary>世界目录绝对路径 → 缓存文件名。只做路径去重，不承担任何安全用途。</summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "缓存文件名只用 SHA1 做短哈希（需求指定）；不用于完整性校验或安全决策")]
    private static string HashPath(string fullDirectory) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(fullDirectory))).ToLowerInvariant();

    /// <summary>
    /// 旁记元数据：level.dat 的修改时间（UTC ticks）与长度。
    ///
    /// 用两个字段而不是内容哈希：图标提取本身已经要读一遍 level.dat，
    /// 再算整文件哈希等于多读一次；而 (mtime, length) 足以发现「游戏写完存档」这种整体替换。
    /// </summary>
    private sealed record IconCacheMetadata(long LevelDatTicks, long LevelDatLength);
}

/// <summary>
/// 存档图标的 DI 注册入口。
///
/// 与 <see cref="BackupsServiceCollectionExtensions"/> 分开一个类：注册点只有一个，
/// 但放在服务同文件里可以让本功能的新增内容集中在一处，不必与并行改动争同一份扩展文件。
/// </summary>
public static class WorldIconServiceCollectionExtensions
{
    public static IServiceCollection AddCoreWorldIcons(this IServiceCollection services)
    {
        services.AddSingleton<IWorldIconService, WorldIconService>();
        return services;
    }
}