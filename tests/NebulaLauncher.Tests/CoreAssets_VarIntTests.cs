using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Tests;

public class CoreAssets_VarIntTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(300)]
    [InlineData(25565)]
    [InlineData(2097151)]
    [InlineData(2147483647)]
    [InlineData(-1)]
    [InlineData(-2147483648)]
    public void VarInt_流式往返(int value)
    {
        using var stream = new MemoryStream();
        MinecraftProtocol.WriteVarInt(stream, value);
        stream.Position = 0;

        Assert.Equal(value, MinecraftProtocol.ReadVarInt(stream));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    [InlineData(16383, 2)]
    [InlineData(16384, 3)]
    [InlineData(25565, 3)]
    [InlineData(2097151, 3)]
    [InlineData(2097152, 4)]
    [InlineData(int.MaxValue, 5)]
    [InlineData(-1, 5)]
    public void VarInt_编码长度符合协议(int value, int expectedSize)
    {
        using var stream = new MemoryStream();
        MinecraftProtocol.WriteVarInt(stream, value);

        Assert.Equal(expectedSize, stream.Length);
        Assert.Equal(expectedSize, MinecraftProtocol.GetVarIntSize(value));
    }

    [Fact]
    public void VarInt_与协议规定的字节序列一致()
    {
        // 协议文档里的示例：25565 = DD C7 01，2097151 = FF FF 7F。
        using var maxThreeBytes = new MemoryStream();
        MinecraftProtocol.WriteVarInt(maxThreeBytes, 2097151);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x7F }, maxThreeBytes.ToArray());

        using var port = new MemoryStream();
        MinecraftProtocol.WriteVarInt(port, 25565);
        Assert.Equal(new byte[] { 0xDD, 0xC7, 0x01 }, port.ToArray());

        using var negative = new MemoryStream();
        MinecraftProtocol.WriteVarInt(negative, -1);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, negative.ToArray());
    }

    [Fact]
    public void VarInt_超长输入被拒绝()
    {
        using var stream = new MemoryStream([0x80, 0x80, 0x80, 0x80, 0x80, 0x01]);
        Assert.Throws<InvalidDataException>(() => MinecraftProtocol.ReadVarInt(stream));
    }

    [Fact]
    public void VarInt_截断输入抛EndOfStream()
    {
        using var stream = new MemoryStream([0x80]);
        Assert.Throws<EndOfStreamException>(() => MinecraftProtocol.ReadVarInt(stream));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("mc.example.com")]
    [InlineData("中文服务器.测试")]
    public void String_流式往返(string value)
    {
        using var stream = new MemoryStream();
        MinecraftProtocol.WriteString(stream, value);
        stream.Position = 0;

        Assert.Equal(value, MinecraftProtocol.ReadString(stream));
    }

    [Fact]
    public void 握手包_长度前缀与字段顺序正确()
    {
        var body = MinecraftProtocol.BuildHandshakeBody("localhost", 25565, 767, 1);
        var framed = MinecraftProtocol.Frame(body);

        using var stream = new MemoryStream(framed);
        var length = MinecraftProtocol.ReadVarInt(stream);
        Assert.Equal(body.Length, length);

        Assert.Equal(0x00, MinecraftProtocol.ReadVarInt(stream));
        Assert.Equal(767, MinecraftProtocol.ReadVarInt(stream));
        Assert.Equal("localhost", MinecraftProtocol.ReadString(stream));
        Assert.Equal(25565, (stream.ReadByte() << 8) | stream.ReadByte());
        Assert.Equal(1, MinecraftProtocol.ReadVarInt(stream));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void 状态请求包是_frame_包裹的单字节零()
    {
        Assert.Equal(new byte[] { 0x01, 0x00 }, MinecraftProtocol.BuildStatusRequest());
    }
}
