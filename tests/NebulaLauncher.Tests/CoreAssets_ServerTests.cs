using System.Net;
using System.Net.Sockets;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Tests;

public class CoreAssets_ServerTests
{
    [Fact]
    public async Task Ping_解析真实协议的握手与状态响应()
    {
        const string json = """
        {
          "version": { "name": "Paper 1.20.4", "protocol": 765 },
          "players": { "max": 100, "online": 7 },
          "description": {
            "text": "§a欢迎来到 ",
            "extra": [ { "text": "测试服", "color": "gold", "bold": true } ]
          },
          "favicon": "data:image/png;base64,AAAA"
        }
        """;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var handshakeLength = MinecraftProtocol.ReadVarInt(stream);
            var handshake = new byte[handshakeLength];
            stream.ReadExactly(handshake, 0, handshakeLength);

            var requestLength = MinecraftProtocol.ReadVarInt(stream);
            var request = new byte[requestLength];
            stream.ReadExactly(request, 0, requestLength);

            using var body = new MemoryStream();
            MinecraftProtocol.WriteVarInt(body, 0x00);
            MinecraftProtocol.WriteString(body, json);
            var framed = MinecraftProtocol.Frame(body.ToArray());

            await stream.WriteAsync(framed);
            await stream.FlushAsync();
        });

        using var service = new ServerService(new CoreAssetsPaths(), new CoreAssetsLog());
        var status = await service.PingAsync("127.0.0.1", port);

        await serverTask;

        Assert.True(status.Online, status.Error);
        Assert.Null(status.Error);
        Assert.Equal("Paper 1.20.4", status.VersionName);
        Assert.Equal(765, status.ProtocolVersion);
        Assert.Equal(7, status.OnlinePlayers);
        Assert.Equal(100, status.MaxPlayers);
        Assert.Equal("欢迎来到 测试服", status.Motd);
        Assert.Contains("§a", status.MotdRaw);
        Assert.Contains("§6", status.MotdRaw);
        Assert.Equal("data:image/png;base64,AAAA", status.FaviconDataUri);
        Assert.True(status.LatencyMs >= 0);
    }

    [Fact]
    public async Task Ping_端口未开放时返回离线与中文原因()
    {
        using var service = new ServerService(new CoreAssetsPaths(), new CoreAssetsLog());
        var status = await service.PingAsync("127.0.0.1", 1);

        Assert.False(status.Online);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        Assert.Equal("127.0.0.1", status.Host);
        Assert.Equal(1, status.Port);
    }

    [Fact]
    public async Task Ping_空地址直接返回错误()
    {
        using var service = new ServerService(new CoreAssetsPaths(), new CoreAssetsLog());
        var status = await service.PingAsync(string.Empty, 25565);

        Assert.False(status.Online);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }

    [Fact]
    public async Task 服务器列表_增删与持久化()
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());

        await service.LoadAsync();
        Assert.Empty(service.Servers);

        await service.AddAsync(new ServerEntry { Name = "主服", Host = "mc.example.com", Port = 25566 });
        await service.AddAsync(new ServerEntry { Name = "备服", Host = "backup.example.com" });

        Assert.Equal(2, service.Servers.Count);
        Assert.True(File.Exists(paths.ServersFile));

        // 新实例从磁盘读回
        using var reloaded = new ServerService(paths, new CoreAssetsLog());
        await reloaded.LoadAsync();
        Assert.Equal(2, reloaded.Servers.Count);

        var target = reloaded.Servers.Single(entry => entry.Host == "mc.example.com");
        Assert.Equal(25566, target.Port);
        Assert.Equal("mc.example.com:25566", target.Address);

        await reloaded.RemoveAsync(target.Id);
        Assert.Single(reloaded.Servers);

        using var reloadedAgain = new ServerService(paths, new CoreAssetsLog());
        await reloadedAgain.LoadAsync();
        Assert.Single(reloadedAgain.Servers);
    }

    [Fact]
    public async Task 服务器列表_相同地址重复添加只保留一条()
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());

        await service.AddAsync(new ServerEntry { Name = "旧名字", Host = "same.example.com" });
        await service.AddAsync(new ServerEntry { Name = "新名字", Host = "same.example.com" });

        var entry = Assert.Single(service.Servers);
        Assert.Equal("新名字", entry.Name);
    }
}
