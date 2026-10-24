using System.IO.Compression;
using System.Text;

namespace NebulaLauncher.Core.Backups;

/// <summary>
/// NBT 写回器：把 <see cref="NbtDocument"/> 按 NBT 规范（大端、<c>TAG_Compound</c> 根）写成字节。
///
/// 只负责序列化，不碰文件——<c>.bak</c> 备份、占用检测与原子替换都在
/// <see cref="BackupService.UpdateWorldAsync"/> 里，顺序不能颠倒。
///
/// 压缩形态跟随文档：源文件是 gzip 就写 gzip，否则写裸 NBT。Minecraft 读两种都认，
/// 但「与原文件一致」能让用户用同一个工具继续打开，也避免出现一半压缩一半不压缩的存档。
/// </summary>
internal static class NbtWriter
{
    /// <summary>序列化为字节；<paramref name="document"/>.IsGzip 决定是否 gzip 压缩。</summary>
    public static byte[] ToBytes(NbtDocument document)
    {
        using var buffer = new MemoryStream();
        if (document.IsGzip)
        {
            // 与 Java 的 DeflaterOutputStream 默认级别接近；用 Optimal 而不是 Fastest，
            // level.dat 只有几十 KB，压缩率比压缩速度更值钱。
            using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                WriteDocument(gzip, document);
            }
        }
        else
        {
            WriteDocument(buffer, document);
        }

        return buffer.ToArray();
    }

    /// <summary>写文档本体（不含压缩）。</summary>
    public static void WriteDocument(Stream stream, NbtDocument document)
    {
        stream.WriteByte((byte)NbtTagType.Compound);
        WriteName(stream, document.RootName);
        WriteCompoundPayload(stream, document.Root);
    }

    /// <summary>写到指定路径（覆盖）。调用方负责备份与替换。</summary>
    public static void WriteFile(string path, NbtDocument document)
    {
        var bytes = ToBytes(document);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteCompoundPayload(Stream stream, NbtCompound compound)
    {
        foreach (var entry in compound.Entries)
        {
            stream.WriteByte((byte)entry.Value.Type);
            WriteName(stream, entry.Key);
            WritePayload(stream, entry.Value);
        }

        stream.WriteByte((byte)NbtTagType.End);
    }

    private static void WritePayload(Stream stream, NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtTagType.Byte:
                stream.WriteByte(AsByte(tag));
                break;
            case NbtTagType.Short:
                WriteInt16(stream, AsShort(tag));
                break;
            case NbtTagType.Int:
                WriteInt32(stream, AsInt(tag));
                break;
            case NbtTagType.Long:
                WriteInt64(stream, AsLong(tag));
                break;
            case NbtTagType.Float:
                WriteSingle(stream, AsSingle(tag));
                break;
            case NbtTagType.Double:
                WriteDouble(stream, AsDouble(tag));
                break;
            case NbtTagType.ByteArray:
                var bytes = tag.Value as byte[] ?? Array.Empty<byte>();
                WriteInt32(stream, bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                break;
            case NbtTagType.String:
                WriteString(stream, tag.Value as string ?? string.Empty);
                break;
            case NbtTagType.List:
                WriteListPayload(stream, tag.Value as NbtList ?? new NbtList(NbtTagType.End, new List<NbtTag>()));
                break;
            case NbtTagType.Compound:
                WriteCompoundPayload(stream, tag.Value as NbtCompound ?? new NbtCompound());
                break;
            case NbtTagType.IntArray:
                var ints = tag.Value as int[] ?? Array.Empty<int>();
                WriteInt32(stream, ints.Length);
                foreach (var value in ints)
                {
                    WriteInt32(stream, value);
                }

                break;
            case NbtTagType.LongArray:
                var longs = tag.Value as long[] ?? Array.Empty<long>();
                WriteInt32(stream, longs.Length);
                foreach (var value in longs)
                {
                    WriteInt64(stream, value);
                }

                break;
            default:
                throw new InvalidDataException($"无法写入 NBT 标签：{tag.Type}");
        }
    }

    private static void WriteListPayload(Stream stream, NbtList list)
    {
        // 空列表的元素类型必须是 0（TAG_End），否则游戏读到的是「有 N 个元素的列表」
        // 后面却跟着结束符——直接判为坏档。
        var elementType = list.Items.Count == 0 ? NbtTagType.End : list.ElementType;
        stream.WriteByte((byte)elementType);
        WriteInt32(stream, list.Items.Count);
        foreach (var item in list.Items)
        {
            WritePayload(stream, item);
        }
    }

    private static byte AsByte(NbtTag tag) => tag.Value switch
    {
        byte value => value,
        short value => (byte)value,
        int value => (byte)value,
        long value => (byte)value,
        _ => 0,
    };

    private static short AsShort(NbtTag tag) => tag.Value switch
    {
        byte value => value,
        short value => value,
        int value => (short)value,
        long value => (short)value,
        _ => 0,
    };

    private static int AsInt(NbtTag tag) => tag.Value switch
    {
        byte value => value,
        short value => value,
        int value => value,
        long value => (int)value,
        _ => 0,
    };

    private static long AsLong(NbtTag tag) => tag.Value switch
    {
        byte value => value,
        short value => value,
        int value => value,
        long value => value,
        _ => 0,
    };

    private static float AsSingle(NbtTag tag) => tag.Value switch
    {
        float value => value,
        double value => (float)value,
        _ => 0,
    };

    private static double AsDouble(NbtTag tag) => tag.Value switch
    {
        float value => value,
        double value => value,
        _ => 0,
    };

    private static void WriteName(Stream stream, string name) =>
        WriteString(stream, name ?? string.Empty);

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > short.MaxValue)
        {
            throw new InvalidDataException("NBT 字符串或标签名过长");
        }

        WriteInt16(stream, (short)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        buffer[0] = (byte)(value >> 8);
        buffer[1] = (byte)value;
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        for (var index = 7; index >= 0; index--)
        {
            buffer[index] = (byte)value;
            value >>= 8;
        }

        stream.Write(buffer);
    }

    private static void WriteSingle(Stream stream, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        stream.Write(buffer);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        if (BitConverter.IsLittleEndian)
        {
            buffer.Reverse();
        }

        stream.Write(buffer);
    }
}