using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Accounts;

/// <summary>
/// 账户皮肤头像：按 UUID 向皮肤服务查纹理地址，下载 PNG 并缓存到启动器缓存目录。
///
/// 为什么不是「每次启动都请求」：头像出现在账户页大卡、账户列表与首页三处，
/// 一次页面重建就会触发十几次同一个请求；皮肤服务对未认证调用有频率限制，
/// 缓存纹理地址（而不是只缓存图片）能把「每启动一次一次请求」压到「只有换皮才请求」。
///
/// 取不到真实皮肤（离线账户、无 UUID、第三方未填 API 根、网络失败、角色不存在）
/// 一律回落到内嵌的 Steve / Alex 默认皮肤，UI 不需要处理「没有头像」这种状态。
/// 失败只写日志不抛：头像缺失是装饰性问题，不该让整页账户读取失败。
/// </summary>
public sealed class SkinAvatarService : ISkinAvatarService, IDisposable
{
    /// <summary>缓存子目录名，落在 <see cref="ILauncherPaths.CacheDirectory"/> 下。</summary>
    internal const string CacheFolderName = "skin";

    /// <summary>Mojang 会话服务的纹理查询地址。</summary>
    internal const string MojangProfileRoot = "https://sessionserver.mojang.com/session/minecraft/profile/";

    /// <summary>authlib-injector 约定的纹理查询路径，接在第三方验证的 API 根后面。</summary>
    internal const string AuthlibProfilePath = "/sessionserver/session/minecraft/profile/";

    /// <summary>皮肤 PNG 上限。64×64 的皮肤只有几 KB，64KB 已经远超正常值。</summary>
    internal const int MaxSkinBytes = 64 * 1024;

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 共享 HttpClient 兜底。头像请求和账户登录不是一条链路，不共用超时设置：
    /// 头像 8 秒拿不到就回落默认皮肤，用户不该为了一个头像等半分钟。
    /// </summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;
    private readonly HttpClient _http;

    /// <summary>同一账户的并发请求合并成一次：列表页重建时多行会同时触发。</summary>
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);

    /// <summary>默认皮肤落盘串行化，见 <see cref="GetDefaultSkinPathAsync"/>。</summary>
    private readonly SemaphoreSlim _defaultSkinGate = new(1, 1);

    public SkinAvatarService(ILauncherPaths paths, IAppLog? log = null, HttpClient? httpClient = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
        _http = httpClient ?? SharedHttp;
    }

    /// <summary>
    /// 释放用于串行化默认皮肤落盘的信号量。注册为单例时由容器在退出时调用；
    /// 释放之后实例不应再被使用，这与容器的单例生命周期一致。
    /// </summary>
    public void Dispose() => _defaultSkinGate.Dispose();

    public async Task<string?> GetAvatarPathAsync(Account account, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var model = NormalizeModel(account?.SkinModel);
        if (account is null)
        {
            return await GetDefaultSkinPathAsync(model, cancellationToken).ConfigureAwait(false);
        }

        var profileUrl = BuildProfileUrl(account);
        if (profileUrl is null)
        {
            // 离线账户没有远端皮肤，也不必进缓存：默认皮肤是固定资源。
            return await GetDefaultSkinPathAsync(model, cancellationToken).ConfigureAwait(false);
        }

        var key = account.Id;
        if (!refresh && _inflight.TryGetValue(key, out var running))
        {
            return await running.ConfigureAwait(false);
        }

        var task = ResolveAsync(account, model, profileUrl, refresh, cancellationToken);
        _inflight[key] = task;
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void Invalidate(Account account)
    {
        if (account is null || string.IsNullOrWhiteSpace(account.Id))
        {
            return;
        }

        try
        {
            var metaPath = MetaPathOf(account.Id);
            var meta = ReadMetadata(metaPath);
            if (meta is not null && !string.IsNullOrWhiteSpace(meta.PngFileName))
            {
                TryDelete(Path.Combine(SkinDirectory, meta.PngFileName));
            }

            TryDelete(metaPath);
        }
        catch (Exception ex)
        {
            _log?.Warn($"清理皮肤头像缓存失败：{account.Id}", ex);
        }
    }

    /// <summary>
    /// 纹理查询地址。返回 null 表示这个账户没有远端皮肤：离线账户，或第三方账户没填 API 根。
    /// 与 PCL 的 <c>McSkinGetAddress</c> 同规则——微软账户固定打 Mojang，第三方账户打自己的 API 根。
    /// </summary>
    internal static string? BuildProfileUrl(Account account)
    {
        if (account is null || string.IsNullOrWhiteSpace(account.Uuid))
        {
            return null;
        }

        var uuid = account.Uuid.Trim().ToLowerInvariant();
        return account.Type switch
        {
            AccountType.Microsoft => MojangProfileRoot + uuid,
            AccountType.Authlib when !string.IsNullOrWhiteSpace(account.AuthServerUrl) =>
                account.AuthServerUrl.TrimEnd('/') + AuthlibProfilePath + uuid,
            _ => null,
        };
    }

    /// <summary>
    /// 从 profile JSON 里取纹理地址与皮肤模型。拿不到 <c>textures</c> 属性或解不出 <c>SKIN</c> 时返回 null。
    /// </summary>
    internal static (string Url, string Model)? ParseProfile(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var property in properties.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.Object
                    || !property.TryGetProperty("name", out var name)
                    || !string.Equals(name.GetString(), "textures", StringComparison.Ordinal)
                    || !property.TryGetProperty("value", out var value)
                    || value.GetString() is not { Length: > 0 } base64)
                {
                    continue;
                }

                return ParseTextures(base64);
            }
        }
        catch (JsonException)
        {
            // 服务端返回非 JSON（网关错误页、限流页）时按「没有皮肤」处理。
        }

        return null;
    }

    /// <summary><c>textures</c> 属性是 base64 包裹的 JSON，解开后 <c>textures.SKIN.url</c> 才是图片地址。</summary>
    internal static (string Url, string Model)? ParseTextures(string base64)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(decoded);
            if (!document.RootElement.TryGetProperty("textures", out var textures)
                || textures.ValueKind != JsonValueKind.Object
                || !textures.TryGetProperty("SKIN", out var skin)
                || skin.ValueKind != JsonValueKind.Object
                || !skin.TryGetProperty("url", out var url)
                || url.GetString() is not { Length: > 0 } skinUrl)
            {
                return null;
            }

            var model = "classic";
            if (skin.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("model", out var modelValue)
                && string.Equals(modelValue.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
            {
                model = "slim";
            }

            return (skinUrl, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>皮肤 PNG 尺寸校验：只接受 64 宽（64×64 与 64×32 两种历史规格），且必须是 PNG。</summary>
    internal static bool IsUsableSkin(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes.Length > MaxSkinBytes)
        {
            return false;
        }

        if (!bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
        {
            return false;
        }

        // IHDR 固定在偏移 8：长度(4) "IHDR"(4) 宽(4) 高(4)
        if (bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
        {
            return false;
        }

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width == 64 && (height == 64 || height == 32);
    }

    internal static string NormalizeModel(string? model)
        => string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";

    private string SkinDirectory => Path.Combine(_paths.CacheDirectory, CacheFolderName);

    private string MetaPathOf(string accountId)
    {
        var safe = accountId;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return Path.Combine(SkinDirectory, safe + ".json");
    }

    private async Task<string?> ResolveAsync(
        Account account,
        string model,
        string profileUrl,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var metaPath = MetaPathOf(account.Id);
        string? tempPng = null;
        string? tempMeta = null;
        try
        {
            if (refresh)
            {
                Invalidate(account);
            }
            else
            {
                // 缓存命中：纹理地址与 PNG 都在。不再打皮肤服务，这也是「启动一次只请求一次」的落点。
                var cached = ReadMetadata(metaPath);
                if (cached is not null && !string.IsNullOrWhiteSpace(cached.PngFileName))
                {
                    var cachedPng = Path.Combine(SkinDirectory, cached.PngFileName);
                    if (File.Exists(cachedPng))
                    {
                        return cachedPng;
                    }
                }
            }

            var texture = await FetchTextureAsync(profileUrl, cancellationToken).ConfigureAwait(false);
            if (texture is null)
            {
                return await GetDefaultSkinPathAsync(model, cancellationToken).ConfigureAwait(false);
            }

            var png = await FetchBytesAsync(texture.Value.Url, MaxSkinBytes, cancellationToken).ConfigureAwait(false);
            if (png is null || !IsUsableSkin(png))
            {
                _log?.Info($"皮肤图片不可用，已回落到默认皮肤：{account.Username}");
                return await GetDefaultSkinPathAsync(model, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(SkinDirectory);

            var fileName = HashUrl(texture.Value.Url) + ".png";
            var pngPath = Path.Combine(SkinDirectory, fileName);

            // 先写临时文件再改名：进程在写一半时退出会留下截断的 PNG，而它会被当成有效缓存反复读。
            tempPng = pngPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tempPng, png, cancellationToken).ConfigureAwait(false);
            File.Move(tempPng, pngPath, overwrite: true);
            tempPng = null;

            // 元数据最后落地：它是缓存的提交标记，PNG 没写好时下次会重拉而不是读半截内容。
            tempMeta = metaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var metadata = new SkinCacheMetadata(texture.Value.Url, texture.Value.Model, fileName);
            await File.WriteAllBytesAsync(
                tempMeta,
                JsonSerializer.SerializeToUtf8Bytes(metadata, SkinAvatarJsonContext.Default.SkinCacheMetadata),
                cancellationToken).ConfigureAwait(false);
            File.Move(tempMeta, metaPath, overwrite: true);
            tempMeta = null;

            return pngPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"获取皮肤头像失败：{account.Username}", ex);
            return await GetDefaultSkinPathAsync(model, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempPng);
            TryDelete(tempMeta);
        }
    }

    private async Task<(string Url, string Model)?> FetchTextureAsync(string profileUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(profileUrl, cancellationToken).ConfigureAwait(false);

        // 204 表示角色不存在（第三方服务对未在该服注册的 UUID 就这么回）。
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseProfile(json);
    }

    private async Task<byte[]?> FetchBytesAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        // 读满上限还有剩余内容说明这不是皮肤图，直接判失败而不是截断后塞给解码器。
        if (total == buffer.Length && stream.CanRead)
        {
            var probe = new byte[1];
            if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0)
            {
                return null;
            }
        }

        return total == 0 ? null : buffer[..total];
    }

    /// <summary>把内嵌的 Steve / Alex 皮肤解到缓存目录并返回路径。写不出来时返回 null（UI 会显示占位）。</summary>
    private async Task<string?> GetDefaultSkinPathAsync(string model, CancellationToken cancellationToken)
    {
        var name = model == "slim" ? "alex" : "steve";
        var path = Path.Combine(SkinDirectory, "default-" + name + ".png");
        if (File.Exists(path))
        {
            return path;
        }

        // 首次进账户页时大卡与列表行会同时要脸，两路都发现文件不存在就会并发写同一份默认皮肤；
        // Windows 上后到的 File.Move 会撞上另一个正在替换的目标，直接 Access denied。
        // 串行化之后第二路在闸门内重查一次即可拿到第一路写好的文件。
        await _defaultSkinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            string? temp = null;
            try
            {
                await using var stream = typeof(SkinAvatarService).Assembly
                    .GetManifestResourceStream("NebulaLauncher.Core.Resources.Skins." + name + ".png");
                if (stream is null)
                {
                    return null;
                }

                Directory.CreateDirectory(SkinDirectory);
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var file = File.Create(temp))
                {
                    await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: true);
                temp = null;
                return path;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn($"写出默认皮肤失败：{name}", ex);
                return null;
            }
            finally
            {
                TryDelete(temp);
            }
        }
        finally
        {
            _defaultSkinGate.Release();
        }
    }

    private static SkinCacheMetadata? ReadMetadata(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllBytes(metaPath),
                SkinAvatarJsonContext.Default.SkinCacheMetadata);
        }
        catch (Exception)
        {
            // 元数据损坏等价于没有缓存：重新拉一份比修它便宜。
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

    /// <summary>纹理地址 → 缓存文件名。只做去重，不承担任何安全用途。</summary>
    private static string HashUrl(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
}

/// <summary>纹理缓存的旁记元数据：地址、模型、PNG 文件名。</summary>
internal sealed record SkinCacheMetadata(string Url, string Model, string PngFileName);

/// <summary>皮肤头像的 DI 注册入口。</summary>
public static class SkinAvatarServiceCollectionExtensions
{
    public static IServiceCollection AddCoreSkinAvatars(this IServiceCollection services)
    {
        services.AddSingleton<ISkinAvatarService, SkinAvatarService>();
        return services;
    }
}

/// <summary>皮肤缓存的 Source Generator 上下文（裁剪发布下不能用运行时反射）。</summary>
[JsonSerializable(typeof(SkinCacheMetadata))]
internal sealed partial class SkinAvatarJsonContext : JsonSerializerContext
{
}
