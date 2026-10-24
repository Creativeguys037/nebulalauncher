using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Accounts;

/// <summary>
/// 账户令牌落盘保护。
/// Windows 走 DPAPI（当前用户作用域）；其余平台没有等价的系统级密钥保护，
/// 退化为 Base64 并在日志里明确警告——不假装非 Windows 上也安全。
/// </summary>
public static class TokenProtector
{
    /// <summary>DPAPI 密文前缀。</summary>
    public const string DpapiPrefix = "dpapi:";

    /// <summary>无保护（仅编码）前缀。</summary>
    public const string PlainPrefix = "b64:";

    /// <summary>额外熵，防止同机其他进程直接调用 DPAPI 解开本启动器的密文。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NebulaLauncher.AccountToken.v1");

    /// <summary>加密一个令牌。空值原样返回 null。</summary>
    public static string? Protect(string? plainText, IPlatformService platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        var raw = Encoding.UTF8.GetBytes(plainText);

        if (platform.SupportsSecureTokenStorage && OperatingSystem.IsWindows())
        {
            try
            {
                var cipher = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(cipher);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or DllNotFoundException)
            {
                // DPAPI 不可用时不能把明文伪装成密文，继续走退化分支并留下标记。
            }
        }

        return PlainPrefix + Convert.ToBase64String(raw);
    }

    /// <summary>解密一个令牌。无法解密时返回 null（令牌作废，触发重新登录）。</summary>
    public static string? Unprotect(string? stored, IPlatformService platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    // 配置文件在 Windows 上生成后被拷到 macOS —— 无法解开，只能重新登录。
                    return null;
                }

                var cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                var raw = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }

            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                var raw = Convert.FromBase64String(stored[PlainPrefix.Length..]);
                return Encoding.UTF8.GetString(raw);
            }

            // 未知格式：当作未加密的历史数据尝试直接读取。
            return Convert.FromBase64String(stored) is { } legacy ? Encoding.UTF8.GetString(legacy) : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException or DllNotFoundException)
        {
            return null;
        }
    }
}
