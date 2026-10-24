using System.Security.Cryptography;
using System.Text;

namespace NebulaLauncher.Core.Net;

/// <summary>
/// 代理密码的落盘保护。与 <see cref="Accounts.TokenProtector"/> 同一套编码约定
/// （前缀标明是否真的加密过），但熵不同：同一个 DPAPI 上下文里混用熵，会让「解出来的是谁的密文」失去区分度。
///
/// 为什么不用 <c>TokenProtector</c>：账户令牌是「解不开就重新登录」，代理密码是「解不开就连不上网」。
/// 前者失败可以作废，后者失败必须还能用，所以这里的降级策略是「退化为 Base64 并在日志里明说」，
/// 而不是返回 null 让用户以为密码丢了。
/// </summary>
public static class ProxyCredentialStore
{
    /// <summary>DPAPI 密文前缀。</summary>
    public const string DpapiPrefix = "dpapi:";

    /// <summary>无保护（仅 Base64 编码）前缀。</summary>
    public const string PlainPrefix = "b64:";

    /// <summary>额外熵，防止同机其他进程直接调用 DPAPI 解开本启动器写在配置里的密文。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NebulaLauncher.ProxyPassword.v1");

    /// <summary>这段存储值是否真的用系统密钥加密过（而不是退化编码）。</summary>
    public static bool IsProtected(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(DpapiPrefix, StringComparison.Ordinal);

    /// <summary>是否存了密码（不论加密与否）。</summary>
    public static bool IsStored(string? stored) => !string.IsNullOrEmpty(stored);

    /// <summary>
    /// 加密一个密码。空值返回 null（不写空条目）。
    ///
    /// 非 Windows 或 DPAPI 不可用时退化为 <see cref="PlainPrefix"/> + Base64：
    /// 这不是加密，只是避免明文赤裸裸躺在配置里；调用方靠 <see cref="IsProtected"/> 区分并如实告知用户。
    /// </summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        var raw = Encoding.UTF8.GetBytes(plainText);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var cipher = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(cipher);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or DllNotFoundException)
            {
                // 不能把明文伪装成密文：落到下面的退化分支，前缀会如实标明。
            }
        }

        return PlainPrefix + Convert.ToBase64String(raw);
    }

    /// <summary>
    /// 解出密码。任何失败都返回 null（配置坏了/换了机器/密文被截断），不抛。
    ///
    /// 注意：换了 Windows 账户或换了机器时 DPAPI 解不开是**预期行为**，
    /// 用户重新填一次密码即可，不该让启动器报错或退出。
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                var cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                var raw = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }

            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException or DllNotFoundException)
        {
            return null;
        }

        // 没有前缀：历史文件里直接存的就是明文（旧版本从没加密过），原样当密码用。
        return stored;
    }

    /// <summary>给界面用的一句话：这段存储值到底怎么保护的。</summary>
    public static string Describe(string? stored)
    {
        if (!IsStored(stored))
        {
            return "未设置";
        }

        return IsProtected(stored)
            ? "已加密保存（当前 Windows 账户）"
            : "未加密保存（当前系统不支持系统级密钥保护）";
    }
}