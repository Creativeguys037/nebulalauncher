using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Servers;

/// <summary>
/// GameSpy4 Query 协议（UDP 25565）：先 handshake 拿 challenge，再发 full stat 拿完整状态与玩家名单。
///
/// 协议时序：
/// <code>
/// C -&gt; S : FE FD 09 00 00 00 00                      （handshake，末 4 字节是 session id，可任意）
/// S -&gt; C : 09 &lt;challenge int32 BE&gt;                   （挑战值）
/// C -&gt; S : FE FD 00 &lt;challenge int32 BE&gt; 00 00 00 00 （full stat）
/// S -&gt; C : 00 &lt;session int32&gt; &lt;payload&gt;               （可能拆成多个 UDP 包）
/// </code>
///
/// payload 结构：<c>splitnum\0\x80\0</c> + 若干 <c>key\0value\0</c> + <c>\0</c> +
/// <c>\x01player_\0\0</c> + 若干 <c>玩家名\0</c> + <c>\0</c>。
///
/// 分片：响应超过一个 MTU 时服务器会拆包，每包首字节低 4 位是包序号、最高位置 1 表示末包；
/// 每包前 5 字节（type + session id）是重复的，拼装时要去掉。
///
/// 只读：除 UDP 之外不碰任何文件、不写缓存。
/// </summary>
public sealed class ServerQueryService : IServerQueryService
{
    /// <summary>默认单次等待超时。Query 是局域网级 UDP，2 秒已经足够覆盖跨国链路。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>默认尝试次数（含首次）。丢一个包就放弃会让「Query 开着但网络抖」被误判为关闭。</summary>
    public const int DefaultAttempts = 2;

    /// <summary>每包首部：type(1) + session id(4)。拼装与解析都要跳过。</summary>
    internal const int DatagramHeaderLength = 5;

    /// <summary>分片最多 4 片（协议用低 2 位表示序号）。</summary>
    internal const int MaxDatagramCount = 4;

    /// <summary>非末包之间等待下一片的窗口。单包响应没有末包标记，靠这个窗口收尾，不能等到整轮超时。</summary>
    private static readonly TimeSpan FragmentWindow = TimeSpan.FromMilliseconds(300);

    private static readonly byte[] HandshakeTemplate = [0xFE, 0xFD, 0x09, 0x00, 0x00, 0x00, 0x00];

    private static ReadOnlySpan<byte> PlayerSectionMarker => "\x01player_"u8;

    private readonly IAppLog? _log;

    public ServerQueryService(IAppLog? log = null) => _log = log;

    /// <inheritdoc />
    public Task<ServerQueryResult> QueryAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
        => QueryAsync(host, port, DefaultTimeout, DefaultAttempts, cancellationToken);

    /// <summary>
    /// 带超时与尝试次数的入口。只给测试与需要缩短等待的调用方用，故意不放大公共 API。
    /// </summary>
    internal async Task<ServerQueryResult> QueryAsync(
        string host,
        int port,
        TimeSpan timeout,
        int attempts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Fail(host ?? string.Empty, port, ServerQueryOutcome.NoResponse, "服务器地址为空");
        }

        if (port is <= 0 or > 65535)
        {
            port = 25565;
        }

        if (timeout <= TimeSpan.Zero)
        {
            timeout = DefaultTimeout;
        }

        if (attempts <= 0)
        {
            attempts = DefaultAttempts;
        }

        IPAddress? address;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            address = addresses.FirstOrDefault(static item => item.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            _log?.Warn($"Query 解析服务器地址失败：{host}", ex);
            return Fail(host, port, ServerQueryOutcome.NoResponse, "无法解析服务器地址");
        }

        if (address is null)
        {
            return Fail(host, port, ServerQueryOutcome.NoResponse, "无法解析服务器地址");
        }

        var endpoint = new IPEndPoint(address, port);
        var lastError = "服务器没有在超时时间内响应 Query";

        try
        {
            using var socket = new UdpClient(address.AddressFamily);

            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(timeout);
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    await socket.SendAsync(BuildHandshakePacket(), endpoint, attemptCts.Token).ConfigureAwait(false);

                    var challengePacket = (await socket.ReceiveAsync(attemptCts.Token).ConfigureAwait(false)).Buffer;
                    if (!TryReadChallenge(challengePacket, out var challenge))
                    {
                        _log?.Info($"Query 响应不是 GameSpy4 challenge：{host}:{port}，首字节 {DescribeFirstByte(challengePacket)}");
                        return Fail(
                            host,
                            port,
                            ServerQueryOutcome.ProtocolMismatch,
                            "端口返回了无法识别的内容（可能不是 Minecraft Query）");
                    }

                    await socket.SendAsync(BuildFullStatRequest(challenge), endpoint, attemptCts.Token).ConfigureAwait(false);

                    var datagrams = await ReceiveStatDatagramsAsync(socket, timeout, attemptCts.Token).ConfigureAwait(false);
                    if (datagrams.Count == 0)
                    {
                        lastError = "服务器接受了 Query 但没有返回状态数据";
                        continue;
                    }

                    var payload = TryAssembleDatagrams(datagrams);
                    if (payload is null)
                    {
                        return Fail(
                            host,
                            port,
                            ServerQueryOutcome.ProtocolMismatch,
                            "Query 响应分包不完整或格式不符");
                    }

                    var parsed = ParseFullStatPayload(payload);
                    if (parsed is null)
                    {
                        return Fail(
                            host,
                            port,
                            ServerQueryOutcome.ProtocolMismatch,
                            "无法解析 Query 响应内容");
                    }

                    return BuildSuccess(host, port, parsed, (int)stopwatch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 本次尝试超时：重试一次再下结论。
                    lastError = "服务器没有在超时时间内响应 Query";
                }
                catch (SocketException ex) when (IsPortClosed(ex.SocketErrorCode))
                {
                    // UDP 收到 ICMP 端口不可达：端口上没有 Query 监听，即 enable-query=false。
                    _log?.Info($"Query 端口未开放：{host}:{port}（{ex.SocketErrorCode}）");
                    return Fail(
                        host,
                        port,
                        ServerQueryOutcome.QueryDisabled,
                        "该服务器未开启 Query（UDP 端口没有监听）");
                }
                catch (SocketException ex)
                {
                    lastError = $"网络错误：{ex.SocketErrorCode}";
                    _log?.Warn($"Query 网络错误：{host}:{port}", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"Query 失败：{host}:{port}", ex);
            return Fail(host, port, ServerQueryOutcome.NoResponse, $"查询失败：{ex.Message}");
        }

        return Fail(host, port, ServerQueryOutcome.Timeout, lastError);
    }

    // ---------------- 协议构造与解析（internal static：测试直接喂字节数组） ----------------

    /// <summary>handshake 请求：<c>FE FD 09</c> + 4 字节任意 session id（全 0）。</summary>
    internal static byte[] BuildHandshakePacket() => (byte[])HandshakeTemplate.Clone();

    /// <summary>full stat 请求：<c>FE FD 00</c> + challenge（int32 大端）+ 4 字节 padding。</summary>
    internal static byte[] BuildFullStatRequest(int challenge) =>
    [
        0xFE,
        0xFD,
        0x00,
        (byte)(challenge >> 24),
        (byte)(challenge >> 16),
        (byte)(challenge >> 8),
        (byte)challenge,
        0x00,
        0x00,
        0x00,
        0x00,
    ];

    /// <summary>解析 handshake 响应：首字节必须是 0x09，后 4 字节是 challenge（大端）。</summary>
    internal static bool TryReadChallenge(ReadOnlySpan<byte> datagram, out int challenge)
    {
        challenge = 0;
        if (datagram.Length < 5 || datagram[0] != 0x09)
        {
            return false;
        }

        challenge = (datagram[1] << 24) | (datagram[2] << 16) | (datagram[3] << 8) | datagram[4];
        return true;
    }

    /// <summary>
    /// 按包序号拼装分片。每包首字节：低 4 位是序号，0x08 位表示末包；
    /// 每包前 <see cref="DatagramHeaderLength"/> 字节是重复的 type + session id，跳过。
    /// 缺片、序号越界、type 非 0 都返回 null。
    /// </summary>
    internal static byte[]? TryAssembleDatagrams(IReadOnlyList<byte[]> datagrams)
    {
        if (datagrams.Count == 0)
        {
            return null;
        }

        var fragments = new byte[]?[MaxDatagramCount];
        var last = -1;

        foreach (var datagram in datagrams)
        {
            if (datagram is null || datagram.Length < DatagramHeaderLength)
            {
                return null;
            }

            var flags = datagram[0];
            var type = flags & 0xF0;
            if (type != 0x00 && type != 0x80)
            {
                return null;
            }

            var index = flags & 0x0F;
            if (index >= MaxDatagramCount)
            {
                return null;
            }

            fragments[index] = datagram[DatagramHeaderLength..];
            if ((flags & 0x08) != 0)
            {
                last = index;
            }
        }

        // 单包响应通常没有末包标记：以出现过的最大序号收尾。
        if (last < 0)
        {
            for (var index = 0; index < fragments.Length; index++)
            {
                if (fragments[index] is not null)
                {
                    last = index;
                }
            }
        }

        if (last < 0)
        {
            return null;
        }

        var total = 0;
        for (var index = 0; index <= last; index++)
        {
            if (fragments[index] is null)
            {
                return null;
            }

            total += fragments[index]!.Length;
        }

        var payload = new byte[total];
        var offset = 0;
        for (var index = 0; index <= last; index++)
        {
            var fragment = fragments[index]!;
            fragment.CopyTo(payload, offset);
            offset += fragment.Length;
        }

        return payload;
    }

    /// <summary>
    /// 解析 full stat 的 payload（不含 type 与 session id）。
    /// 结构：<c>splitnum\0\x80\0</c> + key/value 对 + 空 key 结束 + <c>\x01player_\0\0</c> + 玩家名。
    /// 宽容处理：缺少 <c>splitnum</c> 头时直接从第一个 key 开始解析。
    /// </summary>
    internal static ServerQueryPayload? ParseFullStatPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            return null;
        }

        var offset = 0;
        if (payload.Length >= 8 && payload[..8].SequenceEqual("splitnum"u8))
        {
            offset = 8;

            // splitnum 之后是 0x00 0x80 0x00 三个字节，某些实现只发 0x00 0x00。
            while (offset < payload.Length && payload[offset] == 0x00)
            {
                offset++;
            }

            if (offset < payload.Length && payload[offset] == 0x80)
            {
                offset++;
            }

            if (offset < payload.Length && payload[offset] == 0x00)
            {
                offset++;
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (offset < payload.Length)
        {
            var keyEnd = payload[offset..].IndexOf((byte)0x00);
            if (keyEnd < 0)
            {
                // 没有终止符的残余数据：能读多少算多少，但不把残片当 key。
                break;
            }

            if (keyEnd == 0)
            {
                // 空 key = key/value 段结束。
                offset += 1;
                break;
            }

            var key = Decode(payload.Slice(offset, keyEnd));
            offset += keyEnd + 1;

            var valueEnd = payload[offset..].IndexOf((byte)0x00);
            if (valueEnd < 0)
            {
                break;
            }

            var value = Decode(payload.Slice(offset, valueEnd));
            offset += valueEnd + 1;
            fields[key] = value;
        }

        var players = new List<string>();
        var rest = payload[offset..];

        var markerIndex = rest.IndexOf(PlayerSectionMarker);
        if (markerIndex >= 0)
        {
            rest = rest[(markerIndex + PlayerSectionMarker.Length)..];
        }

        var position = 0;
        while (position < rest.Length)
        {
            var end = rest[position..].IndexOf((byte)0x00);
            if (end < 0)
            {
                AddPlayer(rest[position..]);
                break;
            }

            AddPlayer(rest.Slice(position, end));
            position += end + 1;
        }

        return new ServerQueryPayload(fields, players);

        void AddPlayer(ReadOnlySpan<byte> raw)
        {
            var name = Decode(raw).Trim();
            if (name.Length > 0)
            {
                players.Add(name);
            }
        }
    }

    // ---------------- 内部实现 ----------------

    /// <summary>full stat 可能分片：收到末包标记即止；单包无标记时用短窗口收尾。</summary>
    private static async Task<List<byte[]>> ReceiveStatDatagramsAsync(
        UdpClient socket,
        TimeSpan timeout,
        CancellationToken timeoutToken)
    {
        var datagrams = new List<byte[]>(MaxDatagramCount);
        var window = timeout < FragmentWindow ? timeout : FragmentWindow;

        while (datagrams.Count < MaxDatagramCount)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeoutToken);
            wait.CancelAfter(window);

            byte[] datagram;
            try
            {
                datagram = (await socket.ReceiveAsync(wait.Token).ConfigureAwait(false)).Buffer;
            }
            catch (OperationCanceledException) when (!timeoutToken.IsCancellationRequested)
            {
                // 短窗口内没有下一片：把已收到的当作完整响应。
                break;
            }

            if (datagram.Length == 0)
            {
                break;
            }

            datagrams.Add(datagram);
            if ((datagram[0] & 0x08) != 0)
            {
                break;
            }
        }

        return datagrams;
    }

    private static ServerQueryResult BuildSuccess(
        string host,
        int port,
        ServerQueryPayload payload,
        int latencyMs)
    {
        var fields = payload.Fields;
        var players = payload.Players
            .Select(static name => new ServerQueryPlayer { Name = name })
            .ToList();

        var numPlayers = GetInt(fields, "numplayers");
        if (numPlayers <= 0 && players.Count > 0)
        {
            numPlayers = players.Count;
        }

        var hostPort = GetInt(fields, "hostport");
        if (hostPort is <= 0 or > 65535)
        {
            hostPort = port;
        }

        return new ServerQueryResult
        {
            Host = host,
            Port = port,
            Outcome = ServerQueryOutcome.Available,
            Hostname = TrimToNull(Get(fields, "hostname")),
            GameType = TrimToNull(Get(fields, "gametype")),
            GameId = TrimToNull(Get(fields, "game_id")),
            Version = TrimToNull(Get(fields, "version")),
            Plugins = TrimToNull(Get(fields, "plugins")),
            Map = TrimToNull(Get(fields, "map")),
            NumPlayers = numPlayers,
            MaxPlayers = GetInt(fields, "maxplayers"),
            HostPort = hostPort,
            HostIp = TrimToNull(Get(fields, "hostip")),
            Players = players,
            LatencyMs = latencyMs,
        };
    }

    private static ServerQueryResult Fail(string host, int port, ServerQueryOutcome outcome, string error) => new()
    {
        Host = host,
        Port = port,
        Outcome = outcome,
        Error = error,
    };

    private static bool IsPortClosed(SocketError error) =>
        error is SocketError.ConnectionRefused or SocketError.ConnectionReset;

    private static string? Get(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static int GetInt(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : 0;

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static string DescribeFirstByte(byte[] datagram) =>
        datagram.Length == 0 ? "empty" : datagram[0].ToString("X2", CultureInfo.InvariantCulture);
}

/// <summary>full stat 解析结果：key/value 段 + 玩家名列表。内部类型，只服务解析函数。</summary>
internal sealed class ServerQueryPayload
{
    public ServerQueryPayload(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<string> players)
    {
        Fields = fields;
        Players = players;
    }

    public IReadOnlyDictionary<string, string> Fields { get; }

    public IReadOnlyList<string> Players { get; }
}