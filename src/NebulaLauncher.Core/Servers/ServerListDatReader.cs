using System.Globalization;
using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Servers;

/// <summary>
/// <c>servers.dat</c> 读取器：把 Minecraft 自己写的服务器列表读成 <see cref="ServerEntry"/>。
///
/// 为什么不复用 <c>Backups/NbtReader</c>：那个解析器的字段表是 <c>level.dat</c> 专用的
/// （只认 <c>Data.LevelName</c> / <c>Data.Version.Name</c> / <c>Data.GameType</c>），
/// 把它改成通用解析器会让存档备份也背上服务器列表的字段。这里照它的「跳过式解析」写一份。
///
/// 压缩：Minecraft 的 <c>NbtIo.write</c> 写 gzip，但部分版本与第三方工具写未压缩，
/// 所以靠魔数 <c>0x1F 0x8B</c> 判断，两种都读。
///
/// 容错：坏文件、截断、非 NBT、嵌套过深、集合长度异常一律返回空集合，异常只进日志——
/// 导入按钮不该因为一个坏文件把整个服务器页打成错误页。
/// </summary>
public static class ServerListDatReader
{
    /// <summary>Minecraft 缺省服务器端口。</summary>
    public const int DefaultPort = 25565;

    /// <summary>root compound 里放服务器列表的键名。</summary>
    public const string ServersKey = "servers";

    /// <summary>与 <c>Backups/NbtReader</c> 保持同一个深度上限。</summary>
    private const int MaxDepth = 64;

    /// <summary>单个数组 / 集合标签的体积上限，防止坏文件用超大长度把内存打爆。</summary>
    private const int MaxCollectionLength = 16 * 1024 * 1024;

    /// <summary>服务器条目数上限：真实玩家的列表最多几十条，四位数量级已远超合理范围。</summary>
    private const int MaxServerCount = 4096;

    /// <summary>文件名长度上限（NBT 字符串本身受 int16 长度约束，这里再加一道业务上限）。</summary>
    private const int MaxDisplayNameLength = 64;

    /// <summary>整个文件的上限：再大的 servers.dat 不可能是 Minecraft 生成的。</summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    /// <summary>
    /// 读取 <paramref name="path"/> 指向的 <c>servers.dat</c>。
    /// 文件不存在、不可读或格式不对都返回空集合，不抛异常。
    /// </summary>
    /// <param name="path">绝对路径；通常是 <c>&lt;gameDirectory&gt;/servers.dat</c>。</param>
    /// <param name="log">可选的日志出口，只在「读不出来」时写一行原因。</param>
    public static IReadOnlyList<ServerEntry> TryRead(string path, IAppLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return [];
            }

            if (file.Length > MaxFileBytes)
            {
                log?.Warn($"servers.dat 体积异常，已跳过：{path}（{file.Length} 字节）");
                return [];
            }

            // 游戏可能正在写这个文件，共享读比独占读更接近真实场景。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entries = Parse(stream, log);
            log?.Debug($"servers.dat 读到 {entries.Count} 个服务器：{path}");
            return entries;
        }
        catch (Exception ex)
        {
            log?.Warn($"读取 servers.dat 失败：{path}", ex);
            return [];
        }
    }

    /// <summary>
    /// 解析一个 <c>servers.dat</c> 流（gzip 与未压缩均可）。任何异常都吞掉并返回空集合。
    /// </summary>
    /// <param name="stream">文件流或内存流；不是 seekable 时会先复制进内存。</param>
    /// <param name="log">可选的日志出口。</param>
    public static IReadOnlyList<ServerEntry> Parse(Stream stream, IAppLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var source = EnsureSeekable(stream);
            if (IsGzip(source))
            {
                using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
                return ReadDocument(gzip);
            }

            return ReadDocument(source);
        }
        catch (Exception ex)
        {
            log?.Warn("解析 servers.dat 失败，按空列表处理", ex);
            return [];
        }
    }

    /// <summary>
    /// 拆分 <c>host</c> / <c>host:port</c>。不是合法端口的尾巴整串当地址，
    /// 交给后续 ping 去报「无法解析」——导入阶段不替用户猜。
    /// </summary>
    internal static bool TrySplitAddress(string address, out string host, out int port)
    {
        host = address;
        port = DefaultPort;

        if (address.Length == 0)
        {
            return false;
        }

        // IPv6 字面量（"::1" / "fe80::1" / "[::1]:25565" 的裸写形式）里的冒号不是端口分隔符。
        if (address.Contains("::", StringComparison.Ordinal))
        {
            return true;
        }

        var separator = address.LastIndexOf(':');
        if (separator <= 0 || separator == address.Length - 1)
        {
            return true;
        }

        if (!int.TryParse(address[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            return true;
        }

        host = address[..separator];
        port = parsed;
        return true;
    }

    // ---------------- 解析本体 ----------------

    private static List<ServerEntry> ReadDocument(Stream stream)
    {
        var type = ReadTagType(stream);
        if (type != TagCompound)
        {
            throw new InvalidDataException($"servers.dat 根标签不是 compound（0x{type:X2}）");
        }

        ReadString(stream); // root compound 的名字，Minecraft 写空串

        var entries = new List<ServerEntry>();

        while (true)
        {
            var tagType = ReadTagType(stream);
            if (tagType == TagEnd)
            {
                break;
            }

            var name = ReadString(stream);
            if (tagType == TagList && string.Equals(name, ServersKey, StringComparison.Ordinal))
            {
                ReadServers(stream, entries);
                continue;
            }

            SkipPayload(stream, tagType, 0);
        }

        return entries;
    }

    private static void ReadServers(Stream stream, List<ServerEntry> entries)
    {
        var elementType = ReadTagType(stream);
        var count = ReadInt32(stream);
        if (count < 0 || count > MaxServerCount)
        {
            throw new InvalidDataException($"servers 列表长度非法：{count}");
        }

        for (var i = 0; i < count; i++)
        {
            if (elementType == TagCompound)
            {
                var entry = ReadServer(stream);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            else
            {
                SkipPayload(stream, elementType, 1);
            }
        }
    }

    /// <summary>读一条服务器 compound：只取 <c>name</c> / <c>ip</c>，其余（icon、acceptTextures…）整体跳过。</summary>
    private static ServerEntry? ReadServer(Stream stream)
    {
        string? name = null;
        string? ip = null;

        while (true)
        {
            var type = ReadTagType(stream);
            if (type == TagEnd)
            {
                break;
            }

            var key = ReadString(stream);
            if (type == TagString && (string.Equals(key, "name", StringComparison.Ordinal)
                                      || string.Equals(key, "ip", StringComparison.Ordinal)))
            {
                var value = ReadString(stream);
                if (string.Equals(key, "name", StringComparison.Ordinal))
                {
                    name = value;
                }
                else
                {
                    ip = value;
                }

                continue;
            }

            SkipPayload(stream, type, 1);
        }

        if (string.IsNullOrWhiteSpace(ip))
        {
            // 没有地址的条目导不进来：收藏列表以「能连的地址」为主键。
            return null;
        }

        TrySplitAddress(ip.Trim(), out var host, out var port);
        if (host.Length == 0)
        {
            return null;
        }

        // 名字缺失（部分第三方工具只写 ip）时用地址兜底，而不是留一个空标题的卡片。
        var displayName = string.IsNullOrWhiteSpace(name) ? host : name.Trim();
        if (displayName.Length > MaxDisplayNameLength)
        {
            displayName = displayName[..MaxDisplayNameLength];
        }

        return new ServerEntry
        {
            Name = displayName,
            Host = host,
            Port = port,
        };
    }

    // ---------------- 跳过式解析的原语 ----------------

    private static bool IsGzip(Stream stream)
    {
        var position = stream.Position;
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = position;
        return first == 0x1F && second == 0x8B;
    }

    private static Stream EnsureSeekable(Stream stream)
    {
        if (stream.CanSeek)
        {
            return stream;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > MaxFileBytes)
            {
                throw new InvalidDataException("servers.dat 内容过大");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void SkipPayload(Stream stream, int type, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("NBT 嵌套过深");
        }

        switch (type)
        {
            case TagByte:
                SkipOrThrow(stream.ReadByte());
                break;
            case TagShort:
                Skip(stream, 2);
                break;
            case TagInt:
            case TagFloat:
                Skip(stream, 4);
                break;
            case TagLong:
            case TagDouble:
                Skip(stream, 8);
                break;
            case TagByteArray:
                var byteLength = ReadInt32(stream);
                EnsureLength(byteLength);
                Skip(stream, byteLength);
                break;
            case TagString:
                SkipString(stream);
                break;
            case TagList:
                var elementType = ReadTagType(stream);
                var count = ReadInt32(stream);
                EnsureLength(count);
                for (var i = 0; i < count; i++)
                {
                    SkipPayload(stream, elementType, depth + 1);
                }

                break;
            case TagCompound:
                SkipCompound(stream, depth + 1);
                break;
            case TagIntArray:
                var intCount = ReadInt32(stream);
                EnsureLength(intCount);
                Skip(stream, intCount * 4L);
                break;
            case TagLongArray:
                var longCount = ReadInt32(stream);
                EnsureLength(longCount);
                Skip(stream, longCount * 8L);
                break;
            default:
                throw new InvalidDataException($"未知 NBT 标签：{type}");
        }
    }

    private static void SkipCompound(Stream stream, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("NBT 嵌套过深");
        }

        while (true)
        {
            var type = ReadTagType(stream);
            if (type == TagEnd)
            {
                return;
            }

            SkipString(stream);
            SkipPayload(stream, type, depth + 1);
        }
    }

    private static void SkipString(Stream stream)
    {
        var length = ReadInt16(stream);
        if (length > 0)
        {
            Skip(stream, length);
        }
    }

    private static string ReadString(Stream stream)
    {
        var length = ReadInt16(stream);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    private static void EnsureLength(int length)
    {
        if (length < 0 || length > MaxCollectionLength)
        {
            throw new InvalidDataException($"NBT 长度非法：{length}");
        }
    }

    private static byte ReadTagType(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return (byte)value;
    }

    private static void SkipOrThrow(int value)
    {
        if (value < 0)
        {
            throw new EndOfStreamException();
        }
    }

    private static short ReadInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return (short)((buffer[0] << 8) | buffer[1]);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
    }

    private static void Skip(Stream stream, long count)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            var position = stream.Position + count;
            if (position > stream.Length)
            {
                throw new EndOfStreamException();
            }

            stream.Position = position;
            return;
        }

        var buffer = new byte[8192];
        var remaining = count;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            remaining -= read;
        }
    }
}