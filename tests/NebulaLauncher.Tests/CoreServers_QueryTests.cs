using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Tests;

/// <summary>
/// GameSpy4 Query 协议层测试。
///
/// 全部在本地完成：包构造与解析直接喂 <c>byte[]</c>；端到端用例在 127.0.0.1 上起一个
/// UDP 假服务器，不访问任何外网地址。
/// </summary>
public class CoreServers_QueryTests
{
    private static readonly string[] ExpectedMultiPlayers = ["Alpha", "Beta", "Gamma"];

    private static readonly string[] ExpectedLocalPlayers = ["Steve", "Alex"];

    [Fact]
    public void 挑战包_是_FE_FD_09_加四字节会话()
    {
        var packet = ServerQueryService.BuildHandshakePacket();

        Assert.Equal(7, packet.Length);
        Assert.Equal(new byte[] { 0xFE, 0xFD, 0x09, 0x00, 0x00, 0x00, 0x00 }, packet);
    }

    [Fact]
    public void FullStat请求_挑战值按大端写入且尾部四个零()
    {
        var packet = ServerQueryService.BuildFullStatRequest(0x11223344);

        Assert.Equal(new byte[] { 0xFE, 0xFD, 0x00, 0x11, 0x22, 0x33, 0x44, 0x00, 0x00, 0x00, 0x00 }, packet);

        Assert.True(ServerQueryService.TryReadChallenge([0x09, 0x11, 0x22, 0x33, 0x44], out var challenge));
        Assert.Equal(0x11223344, challenge);

        // 首字节不是 0x09：不是 challenge 响应。
        Assert.False(ServerQueryService.TryReadChallenge([0x00, 0x11, 0x22, 0x33, 0x44], out _));
        Assert.False(ServerQueryService.TryReadChallenge([0x09, 0x11], out _));
    }

    [Fact]
    public void 多包_按包序号拼装后再解析玩家名单()
    {
        var payload = BuildPayload("Multi Server", ["Alpha", "Beta", "Gamma"]);

        // 故意乱序传入：拼装必须按包序号而不是到达顺序。
        var datagrams = new[]
        {
            WrapPacket(0x81, 0x0A0B0C0D, payload[12..]),
            WrapPacket(0x00, 0x0A0B0C0D, payload[..12]),
        };

        var assembled = ServerQueryService.TryAssembleDatagrams(datagrams);

        Assert.NotNull(assembled);
        Assert.Equal(payload, assembled!);

        var parsed = ServerQueryService.ParseFullStatPayload(assembled!);
        Assert.NotNull(parsed);
        Assert.Equal("Multi Server", parsed!.Fields["hostname"]);
        Assert.Equal("3", parsed.Fields["numplayers"]);
        Assert.Equal(ExpectedMultiPlayers, parsed.Players);
    }

    [Fact]
    public void 多包_缺片时拒绝拼装()
    {
        var payload = BuildPayload("Broken", ["A"]);

        // 只有 0x00 与 0x81，缺 0x80|0x01 之外的中间片：首包 + 末包可以直接拼装，
        // 但把末包换成序号 2 就出现 1 号空洞，必须拒绝。
        var datagrams = new[]
        {
            WrapPacket(0x00, 1, payload[..10]),
            WrapPacket(0x82, 1, payload[10..]),
        };

        Assert.Null(ServerQueryService.TryAssembleDatagrams(datagrams));
    }

    [Fact]
    public void 单包_解析完整状态字段与空玩家列表()
    {
        var payload = BuildPayload("Solo", []);
        var parsed = ServerQueryService.ParseFullStatPayload(payload);

        Assert.NotNull(parsed);
        Assert.Equal("Solo", parsed!.Fields["hostname"]);
        Assert.Equal("SMP", parsed.Fields["gametype"]);
        Assert.Equal("MINECRAFT", parsed.Fields["game_id"]);
        Assert.Equal("1.20.4", parsed.Fields["version"]);
        Assert.Equal("world", parsed.Fields["map"]);
        Assert.Equal("100", parsed.Fields["maxplayers"]);
        Assert.Empty(parsed.Players);
    }

    [Fact]
    public async Task 端到端_本地假服务器返回状态与玩家名单()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        const int challenge = 0x0A1B2C3D;

        var serverTask = Task.Run(async () =>
        {
            var handshake = await server.ReceiveAsync();
            Assert.Equal(7, handshake.Buffer.Length);
            Assert.Equal(0xFE, handshake.Buffer[0]);
            Assert.Equal(0xFD, handshake.Buffer[1]);
            Assert.Equal(0x09, handshake.Buffer[2]);

            await server.SendAsync(
                new byte[] { 0x09, 0x0A, 0x1B, 0x2C, 0x3D },
                handshake.RemoteEndPoint);

            var fullStat = await server.ReceiveAsync();
            Assert.Equal(0xFE, fullStat.Buffer[0]);
            Assert.Equal(0xFD, fullStat.Buffer[1]);
            Assert.Equal(0x00, fullStat.Buffer[2]);
            Assert.Equal(challenge, (fullStat.Buffer[3] << 24) | (fullStat.Buffer[4] << 16)
                                    | (fullStat.Buffer[5] << 8) | fullStat.Buffer[6]);

            var payload = BuildPayload("Local Paper", ["Steve", "Alex"]);
            await server.SendAsync(WrapPacket(0x00, challenge, payload), fullStat.RemoteEndPoint);
        });

        var service = new ServerQueryService();
        var result = await service.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), 1);
        await serverTask;

        Assert.True(result.Available, result.Error);
        Assert.Null(result.Error);
        Assert.Equal("Local Paper", result.Hostname);
        Assert.Equal("SMP", result.GameType);
        Assert.Equal("1.20.4", result.Version);
        Assert.Equal(2, result.NumPlayers);
        Assert.Equal(100, result.MaxPlayers);
        Assert.Equal(25565, result.HostPort);
        Assert.Equal(ExpectedLocalPlayers, result.Players.Select(player => player.Name).ToArray());
        Assert.True(result.LatencyMs >= 0);
    }

    [Fact]
    public async Task 未开启Query_服务器不响应时归为看不到名单而不是错误()
    {
        // 端口绑定但不读不写：等价于「Query 关闭，UDP 没有响应」。
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;

        var service = new ServerQueryService();
        var stopwatch = Stopwatch.StartNew();
        var result = await service.QueryAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(300), 1);
        stopwatch.Stop();

        Assert.False(result.Available);
        Assert.True(result.IsQueryUnavailable, result.Outcome.ToString());
        Assert.True(
            result.Outcome is ServerQueryOutcome.Timeout or ServerQueryOutcome.QueryDisabled,
            result.Outcome.ToString());
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.True(stopwatch.ElapsedMilliseconds < 5000L);
    }

    [Fact]
    public async Task 协议不符_端口返回非GameSpy4内容时归类为协议不符()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var serverTask = Task.Run(async () =>
        {
            var handshake = await server.ReceiveAsync();
            await server.SendAsync(new byte[] { 0x42, 0x42, 0x42, 0x42, 0x42 }, handshake.RemoteEndPoint);
        });

        var service = new ServerQueryService();
        var result = await service.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), 1);
        await serverTask;

        Assert.Equal(ServerQueryOutcome.ProtocolMismatch, result.Outcome);
        Assert.False(result.Available);
        Assert.False(result.IsQueryUnavailable);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ---------------- 构造辅助 ----------------

    /// <summary>构造 full stat 的 payload（不含 type 与 session id），格式与真实服务器一致。</summary>
    private static byte[] BuildPayload(string hostname, string[] players)
    {
        using var buffer = new MemoryStream();

        buffer.Write("splitnum"u8);
        buffer.WriteByte(0x00);
        buffer.WriteByte(0x80);
        buffer.WriteByte(0x00);

        WritePair(buffer, "hostname", hostname);
        WritePair(buffer, "gametype", "SMP");
        WritePair(buffer, "game_id", "MINECRAFT");
        WritePair(buffer, "version", "1.20.4");
        WritePair(buffer, "plugins", "Paper on Bukkit on CraftBukkit");
        WritePair(buffer, "map", "world");
        WritePair(buffer, "numplayers", players.Length.ToString(CultureInfo.InvariantCulture));
        WritePair(buffer, "maxplayers", "100");
        WritePair(buffer, "hostport", "25565");
        WritePair(buffer, "hostip", "127.0.0.1");

        buffer.WriteByte(0x00);

        buffer.Write("\x01player_"u8);
        buffer.WriteByte(0x00);
        buffer.WriteByte(0x00);

        foreach (var player in players)
        {
            buffer.Write(Encoding.UTF8.GetBytes(player));
            buffer.WriteByte(0x00);
        }

        buffer.WriteByte(0x00);
        return buffer.ToArray();
    }

    private static void WritePair(Stream target, string key, string value)
    {
        target.Write(Encoding.UTF8.GetBytes(key));
        target.WriteByte(0x00);
        target.Write(Encoding.UTF8.GetBytes(value));
        target.WriteByte(0x00);
    }

    /// <summary>包首字节：type（0x00）、末包位（0x08）与包序号（低 4 位），后面是 session id。</summary>
    private static byte[] WrapPacket(byte flags, int sessionId, byte[] body)
    {
        var packet = new byte[ServerQueryService.DatagramHeaderLength + body.Length];
        packet[0] = flags;
        packet[1] = (byte)(sessionId >> 24);
        packet[2] = (byte)(sessionId >> 16);
        packet[3] = (byte)(sessionId >> 8);
        packet[4] = (byte)sessionId;
        body.CopyTo(packet, ServerQueryService.DatagramHeaderLength);
        return packet;
    }
}