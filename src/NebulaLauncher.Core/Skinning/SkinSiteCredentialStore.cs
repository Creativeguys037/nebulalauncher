using System.Text.Json;
using System.Text.Json.Serialization;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Skinning;

/// <summary>一条皮肤站登录凭据（内存态）。密码只在这里以明文短暂存在，落盘前由 <see cref="TokenProtector"/> 加密。</summary>
public sealed class SkinSiteCredential
{
    internal SkinSiteCredential(
        string baseAddress,
        string username,
        string password,
        IReadOnlyList<int> ownedTextureTids,
        DateTimeOffset savedAt)
    {
        BaseAddress = baseAddress;
        Username = username;
        Password = password;
        OwnedTextureTids = ownedTextureTids;
        SavedAt = savedAt;
    }

    public string BaseAddress { get; }

    public string Username { get; }

    /// <summary>明文密码，只在本次进程内用于自动登录，不写日志、不回显到界面。</summary>
    public string Password { get; }

    /// <summary>启动器自己上传成功的纹理 tid，用于「只删除自己创建过的云端纹理」。</summary>
    public IReadOnlyList<int> OwnedTextureTids { get; }

    public DateTimeOffset SavedAt { get; }
}

/// <summary>
/// 皮肤站凭据库：<c>&lt;数据根&gt;/skin-site-credentials.json</c>。
///
/// 为什么单独一个文件而不是塞进账户库：这是第三方站点的密码，生命周期和 Minecraft 账户完全不同；
/// 分开存才能让「忘记这个站点」不碰 Minecraft 账户，也方便用户直接删文件。
/// 密码经 <see cref="TokenProtector"/> 加密；不支持安全存储的平台退化为 base64，和现有令牌一致。
/// </summary>
public sealed class SkinSiteCredentialStore
{
    private const string FileName = "skin-site-credentials.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new SkinSiteCredentialJsonContext(),
    };

    private readonly ILauncherPaths _paths;
    private readonly IPlatformService _platform;
    private readonly IAppLog? _log;
    private readonly object _gate = new();

    public SkinSiteCredentialStore(ILauncherPaths paths, IPlatformService platform, IAppLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(platform);
        _paths = paths;
        _platform = platform;
        _log = log;
    }

    public string FilePath => Path.Combine(_paths.RootDirectory, FileName);

    public Task<SkinSiteCredential?> FindAsync(
        string baseAddress,
        string username,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                var record = FindRecord(baseAddress, username);
                return record is null ? null : ToCredential(record);
            },
            cancellationToken);

    public Task<IReadOnlyList<SkinSiteCredential>> ListAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<SkinSiteCredential>>(
            () => ReadFile().Accounts
                .Select(ToCredential)
                .Where(static credential => credential is not null)
                .Cast<SkinSiteCredential>()
                .OrderByDescending(static credential => credential.SavedAt)
                .ToList(),
            cancellationToken);

    /// <summary>保存或更新一条凭据。密码为空时不覆盖旧密码。</summary>
    public Task<bool> SaveAsync(
        string baseAddress,
        string username,
        string password,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                if (string.IsNullOrWhiteSpace(baseAddress) || string.IsNullOrWhiteSpace(username))
                {
                    return false;
                }

                lock (_gate)
                {
                    var file = ReadFile();
                    var record = FindRecord(file, baseAddress, username);
                    if (record is null)
                    {
                        record = new SkinSiteCredentialRecord
                        {
                            BaseAddress = baseAddress.Trim(),
                            Username = username.Trim(),
                        };
                        file.Accounts.Add(record);
                    }

                    if (!string.IsNullOrEmpty(password))
                    {
                        record.ProtectedPassword = TokenProtector.Protect(password, _platform) ?? string.Empty;
                    }

                    record.SavedAt = DateTimeOffset.Now;
                    return WriteFile(file);
                }
            },
            cancellationToken);

    /// <summary>删除该站点的用户名、密码和「自己上传过」记录。文件不存在也算成功。</summary>
    public Task ForgetAsync(
        string baseAddress,
        string username,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                lock (_gate)
                {
                    var file = ReadFile();
                    var removed = file.Accounts.RemoveAll(
                        record => SameSite(record.BaseAddress, baseAddress)
                                  && string.Equals(record.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (removed > 0)
                    {
                        WriteFile(file);
                    }
                }
            },
            cancellationToken);

    /// <summary>记住启动器新上传成功的纹理 tid。重复上传返回的已有 tid 不应调用这里。</summary>
    public Task RememberOwnedTextureAsync(
        string baseAddress,
        string username,
        int tid,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                lock (_gate)
                {
                    var file = ReadFile();
                    var record = FindRecord(file, baseAddress, username);
                    if (record is null || tid <= 0)
                    {
                        return;
                    }

                    if (!record.OwnedTextureTids.Contains(tid))
                    {
                        record.OwnedTextureTids.Add(tid);
                        WriteFile(file);
                    }
                }
            },
            cancellationToken);

    /// <summary>删除成功后从账本里移除 tid。</summary>
    public Task ForgetOwnedTextureAsync(
        string baseAddress,
        string username,
        int tid,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                lock (_gate)
                {
                    var file = ReadFile();
                    var record = FindRecord(file, baseAddress, username);
                    if (record is not null && record.OwnedTextureTids.Remove(tid))
                    {
                        WriteFile(file);
                    }
                }
            },
            cancellationToken);

    private SkinSiteCredentialRecord? FindRecord(string baseAddress, string username)
        => FindRecord(ReadFile(), baseAddress, username);

    private static SkinSiteCredentialRecord? FindRecord(
        SkinSiteCredentialFile file,
        string baseAddress,
        string username)
        => file.Accounts.FirstOrDefault(
            record => SameSite(record.BaseAddress, baseAddress)
                      && string.Equals(record.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool SameSite(string? left, string? right)
        => string.Equals(
            (left ?? string.Empty).Trim().TrimEnd('/'),
            (right ?? string.Empty).Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private SkinSiteCredential? ToCredential(SkinSiteCredentialRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        var password = TokenProtector.Unprotect(record.ProtectedPassword, _platform);
        if (string.IsNullOrEmpty(password))
        {
            // 加密数据换了机器/用户解不开：当作没有保存密码，但保留用户名与 tid 账本。
            return new SkinSiteCredential(
                record.BaseAddress,
                record.Username,
                string.Empty,
                record.OwnedTextureTids.ToList(),
                record.SavedAt);
        }

        return new SkinSiteCredential(
            record.BaseAddress,
            record.Username,
            password,
            record.OwnedTextureTids.ToList(),
            record.SavedAt);
    }

    private SkinSiteCredentialFile ReadFile()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new SkinSiteCredentialFile();
            }

            var json = File.ReadAllText(FilePath);
            var file = JsonSerializer.Deserialize(json, JsonOptions.Of<SkinSiteCredentialFile>());
            return file ?? new SkinSiteCredentialFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log?.Warn($"读皮肤站凭据失败，本次按未登录处理：{FilePath}", ex);
            return new SkinSiteCredentialFile();
        }
    }

    private bool WriteFile(SkinSiteCredentialFile file)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            var json = JsonSerializer.Serialize(file, JsonOptions.Of<SkinSiteCredentialFile>());
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log?.Warn($"写皮肤站凭据失败：{FilePath}", ex);
            return false;
        }
    }
}

internal sealed class SkinSiteCredentialFile
{
    public List<SkinSiteCredentialRecord> Accounts { get; set; } = [];
}

internal sealed class SkinSiteCredentialRecord
{
    public string BaseAddress { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string ProtectedPassword { get; set; } = string.Empty;

    public List<int> OwnedTextureTids { get; set; } = [];

    public DateTimeOffset SavedAt { get; set; }
}

[JsonSerializable(typeof(SkinSiteCredentialFile))]
internal sealed partial class SkinSiteCredentialJsonContext : JsonSerializerContext
{
}
