using System.Security.Cryptography;
using System.Text;

namespace NebulaLauncher.Core.Accounts;

/// <summary>离线账户 UUID 生成。与 Minecraft 服务端 <c>OfflinePlayer:{name}</c> 规则完全一致。</summary>
public static class OfflineAccountUuid
{
    /// <summary>
    /// 计算 <c>md5("OfflinePlayer:" + username)</c>，返回 32 位小写无连字符十六进制串。
    /// 大小写敏感：先按原样算，不做 trim / 转小写。
    /// </summary>
    public static string Create(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        // MD5 在这里不是安全哈希，而是 Minecraft 客户端与服务端共同遵循的离线 UUID 算法，
        // 换成 SHA 会让离线账户进不了任何服务器，因此只能保留。
#pragma warning disable CA5351
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
#pragma warning restore CA5351
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
