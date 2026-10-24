namespace NebulaLauncher.Core.Servers;

/// <summary>
/// 服务器地址的解析结果。
/// </summary>
/// <param name="Host">纯主机名 / IP 字面量（IPv6 去掉方括号）。</param>
/// <param name="Port">端口；未写时为 <see cref="ServerAddressParser.DefaultPort"/>。</param>
/// <param name="PortSpecified">地址里是否显式写了端口。调用方据它决定「用默认值」还是「保留传入的端口」。</param>
public readonly record struct ServerAddress(string Host, int Port, bool PortSpecified);

/// <summary>
/// 把用户填写的服务器地址拆成主机与端口。启动参数与 Ping 共用这一份。
///
/// 支持的形态：
/// <list type="bullet">
/// <item><c>example.com</c> / <c>example.com:25570</c>（端口非法按未写处理）</item>
/// <item><c>[::1]</c> / <c>[::1]:25570</c>（方括号里的 IPv6）</item>
/// <item><c>::1</c> / <c>2001:db8::1</c>（裸 IPv6，冒号 ≥ 2 个时整串是主机，不猜端口）</item>
/// </list>
///
/// 为什么需要一套统一规则：以前 <c>GameLauncher.SplitServerAddress</c> 对含 <c>]</c> 的地址
/// 直接返回 25565，`[::1]:25570` 会连错端口；<c>ServerService.PingAsync</c> 又用「含 :: 就跳过」
/// 的另一套判据。两处各自实现迟早分叉（AUDIT-2026-10-23 第四节 P2）。
/// </summary>
public static class ServerAddressParser
{
    /// <summary>Minecraft 默认端口。</summary>
    public const int DefaultPort = 25565;

    /// <summary>解析地址。不抛异常：任何不合法输入都退回「整串是主机 + 默认端口」。</summary>
    public static ServerAddress Parse(string? address)
    {
        var trimmed = address?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return new ServerAddress(string.Empty, DefaultPort, false);
        }

        // 方括号形态：IPv6 字面量，可能带端口。
        if (trimmed[0] == '[')
        {
            var close = trimmed.IndexOf(']');

            if (close < 0)
            {
                // 没闭合：整串当主机，交给后续解析报错。
                return new ServerAddress(trimmed, DefaultPort, false);
            }

            var bracketedHost = trimmed[1..close];
            var rest = trimmed[(close + 1)..];

            if (rest.Length > 1 && rest[0] == ':' && TryParsePort(rest[1..], out var bracketedPort))
            {
                return new ServerAddress(bracketedHost, bracketedPort, true);
            }

            return new ServerAddress(bracketedHost, DefaultPort, false);
        }

        // 裸 IPv6：至少两个冒号。整串是主机，不把最后一节当端口。
        if (CountColons(trimmed) >= 2)
        {
            return new ServerAddress(trimmed, DefaultPort, false);
        }

        var separator = trimmed.LastIndexOf(':');

        // 没有冒号、或冒号在首位（`:25565`）：整串是主机。
        if (separator <= 0)
        {
            return new ServerAddress(trimmed, DefaultPort, false);
        }

        var host = trimmed[..separator];
        var portText = trimmed[(separator + 1)..];

        return TryParsePort(portText, out var port)
            ? new ServerAddress(host, port, true)
            : new ServerAddress(trimmed, DefaultPort, false);
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, out port) && port is > 0 and <= 65535;

    private static int CountColons(string value)
    {
        var count = 0;

        foreach (var ch in value)
        {
            if (ch == ':')
            {
                count++;
            }
        }

        return count;
    }
}
