using System.Buffers.Binary;
using System.Text;

namespace NebulaLauncher.Core.Servers;

/// <summary>
/// Minecraft 现代协议用到的二进制原语：VarInt、长度前缀、UTF-8 字符串。
/// 手写实现，避免为一次 ping 引入网络协议库。
/// </summary>
public static class MinecraftProtocol
{
    /// <summary>状态查询用的握手协议版本。服务器不对 status 做版本校验。</summary>
    public const int StatusProtocolVersion = 767;

    private const int MaxStringLength = 32767;
    private const int MaxVarIntBytes = 5;

    public static int GetVarIntSize(int value)
    {
        var unsigned = (uint)value;
        var size = 1;
        while (unsigned >= 0x80)
        {
            unsigned >>= 7;
            size++;
        }

        return size;
    }

    public static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;
        while (unsigned >= 0x80)
        {
            stream.WriteByte((byte)((unsigned & 0x7F) | 0x80));
            unsigned >>= 7;
        }

        stream.WriteByte((byte)unsigned);
    }

    public static int ReadVarInt(Stream stream)
    {
        var result = 0;
        var shift = 0;
        for (var i = 0; i < MaxVarIntBytes; i++)
        {
            var read = stream.ReadByte();
            if (read < 0)
            {
                throw new EndOfStreamException("读取 VarInt 时连接已关闭");
            }

            result |= (read & 0x7F) << shift;
            if ((read & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("VarInt 超过 5 字节");
    }

    public static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>从网络流读取 VarInt，等待每个字节时都响应取消或超时。</summary>
    public static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];
        for (var index = 0; index < MaxVarIntBytes; index++)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("读取 VarInt 时连接已关闭");
            }

            result |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("VarInt 超过 5 字节");
    }

    public static string ReadString(Stream stream)
    {
        var length = ReadVarInt(stream);
        if (length < 0 || length > MaxStringLength * 4)
        {
            throw new InvalidDataException($"字符串长度非法：{length}");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    public static void WriteUnsignedShort(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        stream.Write(buffer);
    }

    /// <summary>组装握手包（不含外层的长度前缀）。</summary>
    public static byte[] BuildHandshakeBody(string host, int port, int protocolVersion, int nextState)
    {
        using var body = new MemoryStream();
        WriteVarInt(body, 0x00);
        WriteVarInt(body, protocolVersion);
        WriteString(body, host);
        WriteUnsignedShort(body, port);
        WriteVarInt(body, nextState);
        return body.ToArray();
    }

    /// <summary>给包体加上 VarInt 长度前缀。</summary>
    public static byte[] Frame(byte[] body)
    {
        using var frame = new MemoryStream(body.Length + 5);
        WriteVarInt(frame, body.Length);
        frame.Write(body, 0, body.Length);
        return frame.ToArray();
    }

    /// <summary>status 请求包：只有一个 id 为 0 的空包。</summary>
    public static byte[] BuildStatusRequest() => Frame([0x00]);
}
