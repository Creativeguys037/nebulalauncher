using System.IO.Compression;
using System.Text;

namespace NebulaLauncher.Core.Backups;

/// <summary>NBT 标签类型编号（<c>TAG_End</c> = 0）。写回只依赖这 13 种，够 level.dat 用。</summary>
internal enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>
/// 一个 NBT 标签：类型 + 值。
///
/// 值装载规则：数值用对应的 CLR 基础类型（<see cref="byte"/> / <see cref="short"/> / <see cref="int"/> /
/// <see cref="long"/> / <see cref="float"/> / <see cref="double"/>），<c>TAG_String</c> 用 <see cref="string"/>，
/// <c>TAG_List</c> 用 <see cref="NbtList"/>，<c>TAG_Compound</c> 用 <see cref="NbtCompound"/>，
/// 三种数组用 <see cref="byte"/>[] / <see cref="int"/>[] / <see cref="long"/>[]。
/// </summary>
internal sealed class NbtTag
{
    private NbtTag(NbtTagType type, object? value)
    {
        Type = type;
        Value = value;
    }

    public NbtTagType Type { get; }

    public object? Value { get; set; }

    public static NbtTag Byte(byte value) => new(NbtTagType.Byte, value);

    public static NbtTag Short(short value) => new(NbtTagType.Short, value);

    public static NbtTag Int(int value) => new(NbtTagType.Int, value);

    public static NbtTag Long(long value) => new(NbtTagType.Long, value);

    public static NbtTag Float(float value) => new(NbtTagType.Float, value);

    public static NbtTag Double(double value) => new(NbtTagType.Double, value);

    public static NbtTag String(string value) => new(NbtTagType.String, value ?? string.Empty);

    public static NbtTag ByteArray(byte[] value) => new(NbtTagType.ByteArray, value);

    public static NbtTag IntArray(int[] value) => new(NbtTagType.IntArray, value);

    public static NbtTag LongArray(long[] value) => new(NbtTagType.LongArray, value);

    public static NbtTag Compound(NbtCompound value) => new(NbtTagType.Compound, value);

    public static NbtTag List(NbtList value) => new(NbtTagType.List, value);
}

/// <summary>
/// <c>TAG_List</c>：元素类型 + 元素序列。
///
/// NBT 的列表要求所有元素同类型，但读写时保留 <see cref="ElementType"/> 而不是从元素推断——
/// 空列表必须写出原始的 <c>TAG_End</c>（0），否则游戏读到的是坏档。
/// </summary>
internal sealed class NbtList
{
    public NbtList(NbtTagType elementType, List<NbtTag> items)
    {
        ElementType = elementType;
        Items = items;
    }

    public NbtTagType ElementType { get; }

    public List<NbtTag> Items { get; }
}

/// <summary>
/// <c>TAG_Compound</c>：有序的「名字 → 标签」序列。
///
/// 用列表而不是字典：NBT 不禁止重名，字典会静默丢标签，而丢标签就是写坏存档。
/// 顺序也按读入顺序保留，写回时原样重放。
/// </summary>
internal sealed class NbtCompound
{
    private readonly List<KeyValuePair<string, NbtTag>> _entries = new();

    public IReadOnlyList<KeyValuePair<string, NbtTag>> Entries => _entries;

    public int Count => _entries.Count;

    public void Add(string name, NbtTag tag) => _entries.Add(new KeyValuePair<string, NbtTag>(name, tag));

    public NbtTag? Get(string name)
    {
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Key, name, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>写入或覆盖：同名标签保持原位置（顺序不变），不存在则追加到末尾。</summary>
    public void Set(string name, NbtTag tag)
    {
        for (var index = 0; index < _entries.Count; index++)
        {
            if (string.Equals(_entries[index].Key, name, StringComparison.Ordinal))
            {
                _entries[index] = new KeyValuePair<string, NbtTag>(name, tag);
                return;
            }
        }

        Add(name, tag);
    }

    public NbtCompound? GetCompound(string name) => Get(name)?.Value as NbtCompound;

    public string? GetString(string name) => Get(name)?.Value as string;

    public int? GetInt(string name)
    {
        var value = Get(name)?.Value;
        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            _ => null,
        };
    }

    public long? GetLong(string name)
    {
        var value = Get(name)?.Value;
        return value switch
        {
            byte b => b,
            short s => s,
            int i => i,
            long l => l,
            _ => null,
        };
    }

    /// <summary>取布尔标记：既接受 <c>TAG_Byte</c>（0/1），也接受 gamerule 那种 <c>TAG_String</c>。</summary>
    public bool? GetFlag(string name)
    {
        var tag = Get(name);
        if (tag is null)
        {
            return null;
        }

        return tag.Value switch
        {
            byte b => b != 0,
            short s => s != 0,
            int i => i != 0,
            long l => l != 0,
            string text => ParseBool(text),
            _ => null,
        };
    }

    private static bool? ParseBool(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };
}

/// <summary>
/// 一份完整的 NBT 文档：根标签名 + 根 compound + 源文件是否 gzip。
///
/// 写回时要「保持与原文件一致」的压缩形态，所以 <see cref="IsGzip"/> 跟着文档走，
/// 不在写的时候重新猜。
/// </summary>
internal sealed class NbtDocument
{
    public NbtDocument(string rootName, NbtCompound root, bool isGzip)
    {
        RootName = rootName;
        Root = root;
        IsGzip = isGzip;
    }

    public string RootName { get; }

    public NbtCompound Root { get; }

    public bool IsGzip { get; set; }

    /// <summary><c>level.dat</c> 的 <c>Data</c> 段；不是合法存档时为 null。</summary>
    public NbtCompound? Data => Root.GetCompound("Data");
}

/// <summary><c>level.dat</c> 里世界列表与编辑面板需要的字段。</summary>
internal sealed record LevelSummary(
    string? LevelName = null,
    string? VersionName = null,
    int? GameType = null,
    long? Seed = null,
    int? Difficulty = null,
    bool? AllowCommands = null,
    bool? KeepInventory = null,
    bool? Hardcore = null)
{
    public string? GameMode => GameType switch
    {
        0 => "生存",
        1 => "创造",
        2 => "冒险",
        3 => "旁观",
        _ => null,
    };
}

/// <summary>
/// NBT 读取器：把 <c>level.dat</c> 解析成完整标签树。
///
/// 与「只取几个字段然后跳过其余」的读法不同，这里解析全部标签——写回时整棵树按原类型、
/// 原顺序重放，不认识的模组标签也能原样留在文件里。解析失败（非法长度、嵌套过深、
/// 未知标签类型）一律抛异常，由调用方拒绝写入，绝不产出半棵树。
/// </summary>
internal static class NbtReader
{
    private const int MaxDepth = 64;
    private const int MaxCollectionLength = 16 * 1024 * 1024;

    /// <summary>解析 level.dat；文件缺失或格式不对返回 null。</summary>
    public static LevelSummary? TryReadLevelDat(string levelDatPath)
    {
        var document = TryReadDocument(levelDatPath);
        return document is null ? null : Summarize(document);
    }

    /// <summary>解析 level.dat 的完整标签树；文件缺失或格式不对返回 null。</summary>
    public static NbtDocument? TryReadDocument(string levelDatPath)
    {
        if (!File.Exists(levelDatPath))
        {
            return null;
        }

        try
        {
            using var file = new FileStream(levelDatPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadDocument(file);
        }
        catch (Exception)
        {
            // 存档损坏 / 非 NBT 文件：调用方只需要知道「读不出来」。
            return null;
        }
    }

    /// <summary>
    /// 从 <c>level.dat</c> 路径读取完整文档。读不出来直接抛异常——
    /// 写回路径必须拿到「读失败了」这个事实，不能把它当成空文档继续写。
    /// </summary>
    public static NbtDocument ReadDocument(string levelDatPath)
    {
        using var file = new FileStream(levelDatPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadDocument(file);
    }

    private static NbtDocument ReadDocument(Stream stream)
    {
        var isGzip = IsGzip(stream);
        using Stream source = isGzip ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : stream;
        return ParseDocument(source, isGzip);
    }

    /// <summary>解析一个已经解压好的 NBT 流。根必须是 <c>TAG_Compound</c>。</summary>
    public static NbtDocument ParseDocument(Stream stream, bool isGzip)
    {
        var type = stream.ReadByte();
        if (type != (byte)NbtTagType.Compound)
        {
            throw new InvalidDataException("根标签不是 compound");
        }

        var rootName = ReadName(stream);
        var root = ReadCompoundPayload(stream, 0);
        return new NbtDocument(rootName, root, isGzip);
    }

    /// <summary>兼容旧调用点：解析后只回摘要。</summary>
    public static LevelSummary? Parse(Stream stream)
    {
        var document = ParseDocument(stream, isGzip: false);
        return Summarize(document);
    }

    public static LevelSummary Summarize(NbtDocument document)
    {
        var data = document.Data;
        if (data is null)
        {
            return new LevelSummary();
        }

        var version = data.GetCompound("Version");
        var rules = data.GetCompound("GameRules");

        return new LevelSummary(
            LevelName: data.GetString("LevelName"),
            VersionName: version?.GetString("Name"),
            GameType: data.GetInt("GameType"),
            Seed: data.GetLong("RandomSeed"),
            Difficulty: data.GetInt("Difficulty"),
            AllowCommands: data.GetFlag("allowCommands"),
            KeepInventory: rules?.GetFlag("keepInventory"),
            Hardcore: data.GetFlag("hardcore"));
    }

    private static bool IsGzip(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        var position = stream.Position;
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = position;
        return first == 0x1F && second == 0x8B;
    }

    private static NbtCompound ReadCompoundPayload(Stream stream, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("NBT 嵌套过深");
        }

        var compound = new NbtCompound();
        while (true)
        {
            var type = stream.ReadByte();
            if (type < 0)
            {
                throw new EndOfStreamException();
            }

            if (type == (byte)NbtTagType.End)
            {
                return compound;
            }

            if (type > (byte)NbtTagType.LongArray)
            {
                throw new InvalidDataException($"未知 NBT 标签：{type}");
            }

            var name = ReadName(stream);
            compound.Add(name, ReadPayload(stream, (NbtTagType)type, depth + 1));
        }
    }

    private static NbtTag ReadPayload(Stream stream, NbtTagType type, int depth) => type switch
    {
        NbtTagType.Byte => NbtTag.Byte((byte)ReadByte(stream)),
        NbtTagType.Short => NbtTag.Short(ReadInt16(stream)),
        NbtTagType.Int => NbtTag.Int(ReadInt32(stream)),
        NbtTagType.Long => NbtTag.Long(ReadInt64(stream)),
        NbtTagType.Float => NbtTag.Float(ReadSingle(stream)),
        NbtTagType.Double => NbtTag.Double(ReadDouble(stream)),
        NbtTagType.ByteArray => NbtTag.ByteArray(ReadByteArray(stream)),
        NbtTagType.String => NbtTag.String(ReadString(stream)),
        NbtTagType.List => NbtTag.List(ReadListPayload(stream, depth)),
        NbtTagType.Compound => NbtTag.Compound(ReadCompoundPayload(stream, depth)),
        NbtTagType.IntArray => NbtTag.IntArray(ReadIntArray(stream)),
        NbtTagType.LongArray => NbtTag.LongArray(ReadLongArray(stream)),
        _ => throw new InvalidDataException($"未知 NBT 标签：{(byte)type}"),
    };

    private static NbtList ReadListPayload(Stream stream, int depth)
    {
        var elementType = (NbtTagType)ReadByte(stream);
        var count = ReadInt32(stream);
        EnsureLength(count);

        var items = new List<NbtTag>(Math.Min(count, 4096));
        for (var index = 0; index < count; index++)
        {
            items.Add(ReadPayload(stream, elementType, depth + 1));
        }

        return new NbtList(elementType, items);
    }

    private static string ReadName(Stream stream) => ReadString(stream);

    private static byte[] ReadByteArray(Stream stream)
    {
        var length = ReadInt32(stream);
        EnsureLength(length);
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return buffer;
    }

    private static int[] ReadIntArray(Stream stream)
    {
        var length = ReadInt32(stream);
        EnsureLength(length);
        var result = new int[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = ReadInt32(stream);
        }

        return result;
    }

    private static long[] ReadLongArray(Stream stream)
    {
        var length = ReadInt32(stream);
        EnsureLength(length);
        var result = new long[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = ReadInt64(stream);
        }

        return result;
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

    private static int ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return value;
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

    private static long ReadInt64(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        long value = 0;
        foreach (var b in buffer)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static float ReadSingle(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        return BitConverter.ToSingle(buffer);
    }

    private static double ReadDouble(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        return BitConverter.ToDouble(buffer);
    }
}