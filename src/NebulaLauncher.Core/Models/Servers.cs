namespace NebulaLauncher.Core.Models;

/// <summary>一个收藏的服务器条目。</summary>
public sealed class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public required string Host { get; set; }

    public int Port { get; set; } = 25565;

    public string? Note { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Address => Port == 25565 ? Host : $"{Host}:{Port}";
}

/// <summary>服务器状态 ping 结果（Server List Ping 协议）。</summary>
public sealed class ServerStatus
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public bool Online { get; init; }

    public string? Error { get; init; }

    public int LatencyMs { get; init; }

    public int OnlinePlayers { get; init; }

    public int MaxPlayers { get; init; }

    /// <summary>MOTD，已去除 §颜色码，保留换行。</summary>
    public string? Motd { get; init; }

    /// <summary>MOTD 原始带格式文本，供 UI 做 § 解析上色。</summary>
    public string? MotdRaw { get; init; }

    public string? VersionName { get; init; }

    public int ProtocolVersion { get; init; }

    /// <summary>服务器图标的 base64 data URI；无图标为 null。</summary>
    public string? FaviconDataUri { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 一次 GameSpy4 Query（UDP 25565）的结果分类。
///
/// 为什么不用「成功 + 错误字符串」两态：Query 与 SLP 是两条独立通道，
/// 服务器 TCP 在线而 Query 关闭（<c>enable-query=false</c>，这是绝大多数服务器的默认值）
/// 是最常见的结果，界面上必须显示成「没开这个功能」而不是「服务器坏了」。
/// </summary>
public enum ServerQueryOutcome
{
    /// <summary>拿到完整状态与玩家名单。</summary>
    Available = 0,

    /// <summary>UDP 端口没有监听（收到 ICMP 端口不可达），即服务器关闭了 Query。</summary>
    QueryDisabled = 1,

    /// <summary>Query 端口没有在超时时间内响应。可能是 Query 关闭但被防火墙吞掉了 ICMP，也可能是丢包。</summary>
    Timeout = 2,

    /// <summary>地址解析失败或本机网络不可达。</summary>
    NoResponse = 3,

    /// <summary>收到 UDP 包但不符合 GameSpy4 格式（端口上跑的是别的服务、或响应被中间设备改写）。</summary>
    ProtocolMismatch = 4,

    /// <summary>当前实现不支持 Query（接口默认实现的返回值），界面应退化为「看不到玩家名单」。</summary>
    Unsupported = 5,
}

/// <summary>Query 返回的一名在线玩家。名字来自 <c>player_</c> 段，按服务器给出的顺序排列。</summary>
public sealed class ServerQueryPlayer
{
    public required string Name { get; init; }
}

/// <summary>
/// 一次 Query 的完整结果。
///
/// 字段对应 GameSpy4 full stat 响应的 key/value 段：<c>hostname</c> / <c>gametype</c> /
/// <c>game_id</c> / <c>version</c> / <c>plugins</c> / <c>map</c> / <c>numplayers</c> /
/// <c>maxplayers</c> / <c>hostport</c> / <c>hostip</c>，玩家名单来自之后的 <c>player_</c> 段。
/// 缺字段时保持 null / 0，不抛异常。
/// </summary>
public sealed class ServerQueryResult
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public ServerQueryOutcome Outcome { get; init; } = ServerQueryOutcome.NoResponse;

    /// <summary>是否拿到了状态与玩家名单。等价于 <see cref="Outcome"/> == <see cref="ServerQueryOutcome.Available"/>。</summary>
    public bool Available => Outcome == ServerQueryOutcome.Available;

    /// <summary>失败原因（中文），成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>服务器名（Query 的 <c>hostname</c>，通常就是 MOTD 的纯文本版）。</summary>
    public string? Hostname { get; init; }

    public string? GameType { get; init; }

    public string? GameId { get; init; }

    /// <summary>服务器实现标识，Vanilla 通常是 <c>1.20.4</c>。</summary>
    public string? Version { get; init; }

    /// <summary>插件列表，形如 <c>Paper on Bukkit on CraftBukkit on Spigot</c>；Vanilla 为空。</summary>
    public string? Plugins { get; init; }

    public string? Map { get; init; }

    /// <summary>服务器自报的在线人数。与 <see cref="Players"/> 数量不一致时以玩家名单为准做提示。</summary>
    public int NumPlayers { get; init; }

    public int MaxPlayers { get; init; }

    public int HostPort { get; init; }

    public string? HostIp { get; init; }

    /// <summary>在线玩家名，按服务器返回顺序排列。空集合表示确实没有玩家在线。</summary>
    public IReadOnlyList<ServerQueryPlayer> Players { get; init; } = [];

    /// <summary>从发出 handshake 到收齐 full stat 的耗时（毫秒）。</summary>
    public int LatencyMs { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Query 关闭或没有响应：界面应提示「看不到玩家名单」而不是报错。</summary>
    public bool IsQueryUnavailable => Outcome is ServerQueryOutcome.QueryDisabled
        or ServerQueryOutcome.Timeout
        or ServerQueryOutcome.Unsupported;

    /// <summary>接口缺省实现用的结果：不联网，告诉调用方「这条通道没实现」。</summary>
    public static ServerQueryResult Unsupported(string host, int port) => new()
    {
        Host = host ?? string.Empty,
        Port = port,
        Outcome = ServerQueryOutcome.Unsupported,
        Error = "当前实现不支持 Query 协议。",
    };
}
