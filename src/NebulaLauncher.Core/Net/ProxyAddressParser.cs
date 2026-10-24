namespace NebulaLauncher.Core.Net;

/// <summary>
/// 自定义代理地址的解析与规范化。
///
/// 用户填进来的形态至少有四种：<c>127.0.0.1:7890</c>、<c>http://127.0.0.1:7890</c>、
/// <c>https://proxy.corp.example:8443</c>、带尾斜杠或路径的复制粘贴结果。
/// 设置页允许前两种（绝大多数代理软件给的是「host:port」），
/// 所以这里统一补 <c>http://</c> 再交给 <see cref="Uri"/> 判定。
/// </summary>
public static class ProxyAddressParser
{
    /// <summary>
    /// 解析成 <c>scheme://host[:port]</c> 形式的绝对地址。
    /// 非 http/https、缺主机名、无法解析时返回 false，调用方据此回落系统代理。
    /// </summary>
    public static bool TryParse(string? raw, out Uri? address)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();

        // 不补 scheme 的话 Uri.TryCreate("127.0.0.1:7890") 会把 "127.0.0.1" 当成 scheme，
        // 得到的是一个奇形怪状的绝对 URI 而不是 http 代理。
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        address = new Uri(Format(uri), UriKind.Absolute);
        return true;
    }

    /// <summary>
    /// 规范化后写回 settings.json 的字符串；解析失败返回 null（表示「没有可用地址」）。
    /// 只保留 scheme / host / port：代理地址里的路径与查询串没有意义，带上反而让 WebProxy 解析出意外结果。
    /// </summary>
    public static string? Normalize(string? raw) => TryParse(raw, out var uri) ? Format(uri!) : null;

    private static string Format(Uri uri) => uri.IsDefaultPort
        ? $"{uri.Scheme}://{uri.Host}"
        : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
}