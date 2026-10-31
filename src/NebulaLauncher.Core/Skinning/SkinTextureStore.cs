using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Skinning;

/// <summary>
/// 皮肤纹理的落盘缓存（<c>&lt;data&gt;/cache/skins/</c>），账户纹理与公开皮肤库共用一套读写。
///
/// 为什么要有这一层：账户头像与皮肤站预览是两条互不认识的取图链路，但断网时都要能显示
/// 「上次看过的皮肤」。两条链路各写一套「先落盘、再读回、判断是否命中」的代码，就会各自
/// 踩一遍截断 PNG、半截元数据、目录名不兼容。这里把落盘规则收成唯一实现。
///
/// 分层（投票 #54 定案）：
/// <list type="bullet">
/// <item><description><c>&lt;data&gt;/cache/skins/default-steve.png</c> / <c>default-alex.png</c>：内嵌默认皮肤，放根下，不跟着账户重复。</description></item>
/// <item><description><c>&lt;data&gt;/cache/skins/&lt;uuid&gt;/current.json</c> + <c>&lt;hash&gt;.png</c>：账户自有纹理。</description></item>
/// <item><description><c>&lt;data&gt;/cache/skins/&lt;site&gt;/&lt;tid&gt;.json</c> + <c>&lt;hash&gt;.png</c>：公开皮肤库，按站点主机名分目录。</description></item>
/// </list>
///
/// 目录归属：整个 <c>cache/</c> 都是「下载缓存」清理类别，所以这里的文件会被一键清掉并自动重建，
/// 而 <c>&lt;data&gt;/skins/</c>（用户导入的本地皮肤）不受影响。
///
/// 写入顺序：PNG 先落临时文件再改名，元数据最后落地。元数据是提交标记——它存在就代表 PNG 已经
/// 完整写好；进程在写一半时退出只会留下 <c>.tmp</c>，下次照样当没缓存重拉。
/// </summary>
public sealed class SkinTextureStore
{
    /// <summary>缓存子目录名，落在 <see cref="ILauncherPaths.CacheDirectory"/> 下。</summary>
    internal const string CacheFolderName = "skins";

    /// <summary>账户纹理的元数据文件名，内容是当前生效的那张 PNG 的哈希。</summary>
    private const string CurrentFileName = "current.json";

    private const string PngSuffix = ".png";
    private const string JsonSuffix = ".json";

    /// <summary>scope 归一后什么都不剩时的兜底名，保证永远不会拼出空目录。</summary>
    private const string UnknownScope = "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new SkinTextureJsonContext(),
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;

    public SkinTextureStore(ILauncherPaths paths, IAppLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _log = log;
    }

    /// <summary>缓存根目录（<c>&lt;data&gt;/cache/skins</c>）。默认皮肤就放在这一层。</summary>
    public string Root => Path.Combine(_paths.CacheDirectory, CacheFolderName);

    /// <summary>某个 scope 的目录：账户传 UUID，公开库传站点主机名。</summary>
    public string RootOf(string scope) => Path.Combine(Root, NormalizeScope(scope));

    /// <summary>某张纹理的落盘路径。哈希只用来去重，不承担安全用途。</summary>
    public string PathOf(string scope, string hash) => Path.Combine(RootOf(scope), NormalizeScope(hash) + PngSuffix);

    /// <summary>账户纹理的元数据路径（<c>current.json</c>）。</summary>
    public string MetaPathOf(string scope) => Path.Combine(RootOf(scope), CurrentFileName);

    /// <summary>公开库某套皮肤的元数据路径（<c>&lt;tid&gt;.json</c>）。</summary>
    public string TidMetaPathOf(string site, int tid)
        => Path.Combine(RootOf(site), tid.ToString(CultureInfo.InvariantCulture) + JsonSuffix);

    /// <summary>
    /// 把纹理写进账户 scope（<c>current.json</c> 作提交标记）。返回落盘路径；PNG 不合法或写失败返回 null。
    /// </summary>
    public Task<string?> SaveAsync(
        string scope,
        string hash,
        byte[] png,
        SkinTextureMeta? meta,
        CancellationToken cancellationToken = default)
        => SaveCoreAsync(scope, CurrentFileName, hash, png, meta, cancellationToken);

    /// <summary>
    /// 把纹理写进公开库 scope（<c>&lt;tid&gt;.json</c> 作提交标记）。返回落盘路径；写失败返回 null。
    /// </summary>
    public Task<string?> SaveTidAsync(
        string site,
        int tid,
        string hash,
        byte[] png,
        SkinTextureMeta? meta,
        CancellationToken cancellationToken = default)
        => SaveCoreAsync(
            site,
            tid.ToString(CultureInfo.InvariantCulture) + JsonSuffix,
            hash,
            png,
            meta,
            cancellationToken);

    /// <summary>
    /// 按哈希取已落盘的纹理路径；没落盘（或只有半截临时文件）返回 null。
    ///
    /// 命中判定是预览与头像的快速路径，读的只是几百字节的旁记 JSON，所以这里刻意用同步 IO
    /// 不走 Task + CancellationToken：为一个 File.Exists 造一次线程切换得不偿失，
    /// 与 <c>LocalSkinStore</c> 读索引的口径一致。写盘仍然是异步且带取消。
    /// </summary>
    public string? TryGetPath(string scope, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        try
        {
            var path = PathOf(scope, hash);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return null;
        }
    }

    /// <summary>账户 scope 的当前纹理路径：读 <c>current.json</c> 拿到哈希再定位 PNG。</summary>
    public string? TryGetCurrentPath(string scope)
    {
        var meta = ReadMeta(MetaPathOf(scope));
        return meta is null || string.IsNullOrWhiteSpace(meta.Hash) ? null : TryGetPath(scope, meta.Hash);
    }

    /// <summary>公开库某套皮肤已落盘的纹理路径：读 <c>&lt;tid&gt;.json</c> 拿到哈希再定位 PNG。</summary>
    public string? TryGetTidPath(string site, int tid)
    {
        var meta = ReadMeta(TidMetaPathOf(site, tid));
        return meta is null || string.IsNullOrWhiteSpace(meta.Hash) ? null : TryGetPath(site, meta.Hash);
    }

    /// <summary>读某个 scope 的元数据；没有或损坏返回 null。</summary>
    public SkinTextureMeta? TryGetMeta(string scope) => ReadMeta(MetaPathOf(scope));

    /// <summary>清掉某个 scope 的整个目录（换皮、站点换地址时用）。默认皮肤在根下，不受影响。</summary>
    public void Invalidate(string scope)
    {
        var directory = RootOf(scope);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            // 删不掉只是残留，下次写入会覆盖同名文件，不值得让调用方失败。
            _log?.Warn($"清理皮肤纹理缓存失败：{scope}", ex);
        }
    }

    /// <summary>把 scope 归一成安全的目录名：非法文件名字符换 <c>_</c>，统一小写。</summary>
    public static string NormalizeScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UnknownScope;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            builder.Append(Array.IndexOf(InvalidFileNameChars, ch) >= 0 ? '_' : ch);
        }

        var normalized = builder.ToString().Trim().ToLowerInvariant();

        // "." / ".." 不是非法文件名字符，但能让路径跳出缓存根，必须挡掉。
        return normalized.Length == 0 || normalized.Trim('.').Length == 0 ? UnknownScope : normalized;
    }

    /// <summary>
    /// 纹理地址 → 缓存哈希。末段本身就是 32 位以上的十六进制（皮肤站的纹理名）就直接用，
    /// 否则退化成整条 URL 的 SHA-256：同一个地址永远得到同一个文件名，地址变了文件名就变。
    /// </summary>
    public static string HashOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var segment = LastSegment(url);
        if (IsTextureToken(segment))
        {
            return segment.ToLowerInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
    }

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static bool IsStorageException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException;

    private static bool IsTextureToken(string segment)
    {
        if (segment.Length < 32)
        {
            return false;
        }

        foreach (var ch in segment)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string LastSegment(string url)
    {
        var span = url.AsSpan();

        var cut = span.IndexOfAny('?', '#');
        if (cut >= 0)
        {
            span = span[..cut];
        }

        span = span.TrimEnd('/');
        var slash = span.LastIndexOf('/');
        return (slash >= 0 ? span[(slash + 1)..] : span).ToString();
    }

    private async Task<string?> SaveCoreAsync(
        string scope,
        string metaFileName,
        string hash,
        byte[] png,
        SkinTextureMeta? meta,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hash) || png is null || png.Length == 0)
        {
            return null;
        }

        // 校验放在写盘之前：截断或非 PNG 的字节一旦落盘，下次会被当成有效缓存反复读出来。
        if (!SkinAvatarService.IsUsableSkin(png))
        {
            _log?.Debug($"皮肤纹理不是可用的 PNG，已跳过缓存：{scope}");
            return null;
        }

        var directory = RootOf(scope);
        var pngPath = Path.Combine(directory, NormalizeScope(hash) + PngSuffix);
        var metaPath = Path.Combine(directory, metaFileName);
        string? temporaryPng = null;
        string? temporaryMeta = null;

        try
        {
            Directory.CreateDirectory(directory);

            temporaryPng = pngPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporaryPng, png, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPng, pngPath, overwrite: true);
            temporaryPng = null;

            if (meta is not null)
            {
                // 元数据最后落地：它是提交标记，PNG 没写好时下次会重拉而不是读半截内容。
                temporaryMeta = metaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(
                    temporaryMeta,
                    JsonSerializer.SerializeToUtf8Bytes(meta, JsonOptions.Of<SkinTextureMeta>()),
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporaryMeta, metaPath, overwrite: true);
                temporaryMeta = null;
            }

            return pngPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            // 缓存写不进去不该影响这次显示：调用方已经拿到图上屏了。
            _log?.Warn($"写皮肤纹理缓存失败：{pngPath}", ex);
            return null;
        }
        finally
        {
            TryDelete(temporaryPng);
            TryDelete(temporaryMeta);
        }
    }

    private SkinTextureMeta? ReadMeta(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllBytes(metaPath), JsonOptions.Of<SkinTextureMeta>());
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            // 元数据损坏等价于没有缓存：重新拉一份比修它便宜。
            _log?.Debug($"皮肤纹理元数据不可读：{metaPath}（{ex.GetType().Name}）");
            return null;
        }
    }

    /// <summary>删临时文件；删不掉只是留个残渣，不影响调用方结论。供同程序集的调用方复用。</summary>
    internal static void TryDelete(string? path)
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
}

/// <summary>纹理缓存的旁记元数据：哈希、模型、来源地址。存在即代表对应的 PNG 已完整落盘。</summary>
public sealed record SkinTextureMeta(string Hash, string Model, string Url);

/// <summary>纹理缓存的 Source Generator 上下文（裁剪发布下不能用运行时反射）。</summary>
[JsonSerializable(typeof(SkinTextureMeta))]
internal sealed partial class SkinTextureJsonContext : JsonSerializerContext
{
}
