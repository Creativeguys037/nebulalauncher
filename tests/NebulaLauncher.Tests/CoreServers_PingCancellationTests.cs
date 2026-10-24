using System.Net;
using System.Net.Sockets;
using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Tests;

public class CoreServers_PingCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_服务器未完整返回时仍响应调用方取消(bool partialBody)
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();

        var ping = service.PingAsync("127.0.0.1", port, cancellation.Token);
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await using var stream = client.GetStream();
        await ReadRequestAsync(stream);
        if (partialBody)
        {
            await stream.WriteAsync(new byte[] { 100, 0 });
        }

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ping.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_服务器未完整返回时内部超时有效(bool partialBody)
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var ping = service.PingAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(300));
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await using var stream = client.GetStream();
        await ReadRequestAsync(stream);
        if (partialBody)
        {
            await stream.WriteAsync(new byte[] { 100, 0 });
        }

        var result = await ping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Online);
        Assert.Contains("超时", result.Error);
    }

    [Fact]
    public async Task Ping_IPv6字面地址使用匹配的地址族()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var ping = service.PingAsync("::1", port);
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await using var stream = client.GetStream();
        await ReadRequestAsync(stream);
        using var payload = new MemoryStream();
        MinecraftProtocol.WriteVarInt(payload, 0);
        MinecraftProtocol.WriteString(payload, "{\"description\":\"IPv6\"}");
        await stream.WriteAsync(MinecraftProtocol.Frame(payload.ToArray()));

        var result = await ping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Online, result.Error);
        Assert.Equal("IPv6", result.Motd);
    }

    private static async Task ReadRequestAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 2; index++)
        {
            var length = await MinecraftProtocol.ReadVarIntAsync(stream, timeout.Token);
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, timeout.Token);
        }
    }
}
