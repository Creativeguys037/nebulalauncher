using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Servers;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// <c>servers.dat</c>（Minecraft 自己写的 NBT 服务器列表）的读取与导入。
///
/// 样本全部用代码构造字节数组：不依赖本机 <c>.minecraft</c>，也不依赖任何外部 NBT 库。
/// </summary>
public class CoreServers_ServerListDatTests
{
    // ---------------- 读取器 ----------------

    [Fact]
    public void 读取_未压缩样本_得到名字地址与缺省端口()
    {
        var dat = BuildServersDat([
            ("主服", "mc.example.com"),
            ("小游戏", "play.example.com"),
        ]);

        var entries = ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog());

        Assert.Equal(2, entries.Count);
        Assert.Equal("主服", entries[0].Name);
        Assert.Equal("mc.example.com", entries[0].Host);
        Assert.Equal(25565, entries[0].Port);
        Assert.Equal("mc.example.com", entries[0].Address);
        Assert.Equal("小游戏", entries[1].Name);
    }

    [Fact]
    public void 读取_gzip样本_一样能读()
    {
        var dat = BuildServersDat([("压缩服", "gz.example.com:25570")]);
        var compressed = Gzip(dat);

        // 前置断言：确实是 gzip 魔数，否则这条测试测不到压缩分支。
        Assert.Equal(0x1F, compressed[0]);
        Assert.Equal(0x8B, compressed[1]);

        var entries = ServerListDatReader.Parse(new MemoryStream(compressed), new CoreAssetsLog());

        var entry = Assert.Single(entries);
        Assert.Equal("压缩服", entry.Name);
        Assert.Equal("gz.example.com", entry.Host);
        Assert.Equal(25570, entry.Port);
        Assert.Equal("gz.example.com:25570", entry.Address);
    }

    [Fact]
    public void 读取_ip带端口时拆成host与port()
    {
        var dat = BuildServersDat([
            ("带端口", "play.example.com:25566"),
            ("缺省端口", "plain.example.com"),
            ("正好25565", "same.example.com:25565"),
        ]);

        var entries = ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog());

        Assert.Equal(3, entries.Count);
        Assert.Equal(("play.example.com", 25566), (entries[0].Host, entries[0].Port));
        Assert.Equal(("plain.example.com", 25565), (entries[1].Host, entries[1].Port));

        // 显式写 :25565 与不写端口在收藏里是同一条地址，否则去重会漏。
        Assert.Equal(("same.example.com", 25565), (entries[2].Host, entries[2].Port));
        Assert.Equal("same.example.com", entries[2].Address);
    }

    [Fact]
    public void 读取_ipv6字面量不拆端口()
    {
        var dat = BuildServersDat([("v6", "::1")]);

        var entry = Assert.Single(ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog()));

        Assert.Equal("::1", entry.Host);
        Assert.Equal(25565, entry.Port);
    }

    [Fact]
    public void 读取_缺name时用地址兜底()
    {
        var dat = BuildServersDat([
            (null, "noname.example.com"),
            ("   ", "blank.example.com:25580"),
        ]);

        var entries = ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog());

        Assert.Equal(2, entries.Count);
        Assert.Equal("noname.example.com", entries[0].Name);
        Assert.Equal("blank.example.com", entries[1].Name);
    }

    [Fact]
    public void 读取_没有地址的条目被丢弃()
    {
        var dat = BuildServersDat([
            ("空地址", ""),
            ("正常", "ok.example.com"),
        ]);

        var entry = Assert.Single(ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog()));

        Assert.Equal("正常", entry.Name);
    }

    [Fact]
    public void 读取_坏文件返回空集合并写日志()
    {
        var log = new CoreAssetsLog();
        var garbage = Encoding.UTF8.GetBytes("这根本不是 NBT 文件");

        var entries = ServerListDatReader.Parse(new MemoryStream(garbage), log);

        Assert.Empty(entries);
        Assert.NotEmpty(log.Warnings);
    }

    [Fact]
    public void 读取_截断文件返回空集合()
    {
        var dat = BuildServersDat([("被截断", "cut.example.com")]);
        var truncated = dat[..(dat.Length / 2)];

        var entries = ServerListDatReader.Parse(new MemoryStream(truncated), new CoreAssetsLog());

        Assert.Empty(entries);
    }

    [Fact]
    public void 读取_gzip魔数但内容损坏返回空集合()
    {
        var fake = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };

        var entries = ServerListDatReader.Parse(new MemoryStream(fake), new CoreAssetsLog());

        Assert.Empty(entries);
    }

    [Fact]
    public void 读取_列表长度超上限返回空集合()
    {
        var dat = BuildServersDat([("假装很多", "many.example.com")], declaredCount: 100_000);

        var entries = ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog());

        Assert.Empty(entries);
    }

    [Fact]
    public void 读取_嵌套过深返回空集合()
    {
        var dat = BuildDeeplyNestedDat(70, ("深处", "deep.example.com"));

        var entries = ServerListDatReader.Parse(new MemoryStream(dat), new CoreAssetsLog());

        Assert.Empty(entries);
    }

    [Fact]
    public void 读取_文件不存在返回空集合且不抛()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nebula-nope-" + Guid.NewGuid().ToString("N"), "servers.dat");

        var entries = ServerListDatReader.TryRead(missing, new CoreAssetsLog());

        Assert.Empty(entries);
    }

    [Fact]
    public void 读取_真实文件路径走TryRead()
    {
        using var paths = new CoreAssetsPaths();
        var path = WriteDat(paths.MinecraftDirectory, Gzip(BuildServersDat([("磁盘上的服", "disk.example.com:25590")])));

        var entries = ServerListDatReader.TryRead(path, new CoreAssetsLog());

        var entry = Assert.Single(entries);
        Assert.Equal("磁盘上的服", entry.Name);
        Assert.Equal(25590, entry.Port);
    }

    // ---------------- 合并进收藏 ----------------

    [Fact]
    public async Task 导入_新增条目并落盘()
    {
        using var paths = new CoreAssetsPaths();
        WriteDat(paths.MinecraftDirectory, BuildServersDat([
            ("甲服", "a.example.com"),
            ("乙服", "b.example.com:25570"),
        ]));

        using var service = new ServerService(paths, new CoreAssetsLog());
        var added = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        Assert.Equal(2, added.Count);
        Assert.Equal(2, service.Servers.Count);
        Assert.NotNull(service.LastServersDatImport);
        Assert.Equal(2, service.LastServersDatImport!.Total);
        Assert.Equal(0, service.LastServersDatImport.Skipped);
        Assert.True(service.LastServersDatImport.FileExists);

        // 独立实例从磁盘读回：导入必须真的写进了 servers.json。
        using var reloaded = new ServerService(paths, new CoreAssetsLog());
        await reloaded.LoadAsync();
        Assert.Equal(2, reloaded.Servers.Count);
        Assert.Contains(reloaded.Servers, entry => entry.Address == "b.example.com:25570");
    }

    [Fact]
    public async Task 导入_已存在的地址跳过且不覆盖用户改过的名字()
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());

        await service.LoadAsync();
        await service.AddAsync(new ServerEntry { Name = "我改过的名字", Host = "mc.example.com", Port = 25565 });

        WriteDat(paths.MinecraftDirectory, BuildServersDat([
            ("Minecraft 里的旧名字", "mc.example.com"),
            ("新服", "new.example.com"),
        ]));

        var added = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        var entry = Assert.Single(added);
        Assert.Equal("new.example.com", entry.Host);

        var existing = service.Servers.Single(server => server.Host == "mc.example.com");
        Assert.Equal("我改过的名字", existing.Name);
        Assert.Equal(2, service.Servers.Count);

        Assert.Equal(2, service.LastServersDatImport!.Total);
        Assert.Equal(1, service.LastServersDatImport.Added);
        Assert.Equal(1, service.LastServersDatImport.Skipped);
    }

    [Fact]
    public async Task 导入_重复导入不产生重复条目()
    {
        using var paths = new CoreAssetsPaths();
        WriteDat(paths.MinecraftDirectory, BuildServersDat([("甲服", "dup.example.com")]));

        using var service = new ServerService(paths, new CoreAssetsLog());
        var first = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);
        var second = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(service.Servers);
        Assert.Equal(1, service.LastServersDatImport!.Skipped);
    }

    [Fact]
    public async Task 导入_同一文件内的重复地址只进一条()
    {
        using var paths = new CoreAssetsPaths();
        WriteDat(paths.MinecraftDirectory, BuildServersDat([
            ("第一次", "same.example.com"),
            ("第二次", "same.example.com:25565"),
        ]));

        using var service = new ServerService(paths, new CoreAssetsLog());
        var added = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        var entry = Assert.Single(added);
        Assert.Equal("第一次", entry.Name);
        Assert.Equal(2, service.LastServersDatImport!.Total);
        Assert.Equal(1, service.LastServersDatImport.Skipped);
    }

    [Fact]
    public async Task 导入_gzip文件也能合并()
    {
        using var paths = new CoreAssetsPaths();
        WriteDat(paths.MinecraftDirectory, Gzip(BuildServersDat([("压缩", "gz2.example.com")])));

        using var service = new ServerService(paths, new CoreAssetsLog());
        var added = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        Assert.Single(added);
        Assert.Equal("gz2.example.com", service.Servers[0].Host);
    }

    [Fact]
    public async Task 导入_没有servers_dat时返回空并标记文件不存在()
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());

        var added = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        Assert.Empty(added);
        Assert.NotNull(service.LastServersDatImport);
        Assert.False(service.LastServersDatImport!.FileExists);
        Assert.Equal(0, service.LastServersDatImport.Total);
    }

    [Fact]
    public async Task 导入_坏文件返回空集合且不影响已有收藏()
    {
        using var paths = new CoreAssetsPaths();
        File.WriteAllBytes(Path.Combine(paths.MinecraftDirectory, "servers.dat"), [0x00, 0x01, 0x02, 0x03, 0x04]);

        using var service = new ServerService(paths, new CoreAssetsLog());
        await service.LoadAsync();
        await service.AddAsync(new ServerEntry { Name = "原有", Host = "keep.example.com" });

        var added = await service.ImportFromServersDatAsync(paths.MinecraftDirectory);

        Assert.Empty(added);
        Assert.Single(service.Servers);
        Assert.Equal("keep.example.com", service.Servers[0].Host);
        Assert.True(service.LastServersDatImport!.FileExists);
        Assert.Equal(0, service.LastServersDatImport.Total);
    }

    [Fact]
    public async Task 导入_空游戏目录不抛异常()
    {
        using var paths = new CoreAssetsPaths();
        using var service = new ServerService(paths, new CoreAssetsLog());

        var added = await service.ImportFromServersDatAsync("   ");

        Assert.Empty(added);
        Assert.NotNull(service.LastServersDatImport);
        Assert.False(service.LastServersDatImport!.FileExists);
    }

    // ---------------- 界面文案 ----------------

    [Fact]
    public void 提示文案_新增与跳过都有数字()
    {
        var summary = new ServerImportSummary { FileExists = true, Total = 5, Added = 3, Skipped = 2 };

        var text = ServersViewModel.BuildImportMessage(3, summary);

        Assert.Contains("3", text, StringComparison.Ordinal);
        Assert.Contains("2", text, StringComparison.Ordinal);
        Assert.Contains("导入", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 提示文案_全部重复时说明没有新增()
    {
        var summary = new ServerImportSummary { FileExists = true, Total = 4, Added = 0, Skipped = 4 };

        var text = ServersViewModel.BuildImportMessage(0, summary);

        Assert.Contains("没有新增", text, StringComparison.Ordinal);
        Assert.Contains("4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 提示文案_空文件说明没有可导入的服务器()
    {
        var summary = new ServerImportSummary { FileExists = true, Total = 0, Added = 0, Skipped = 0 };

        var text = ServersViewModel.BuildImportMessage(0, summary);

        Assert.Contains("没有可导入", text, StringComparison.Ordinal);
    }

    // ---------------- NBT 构造器 ----------------

    /// <summary>
    /// 构造一份最小的 <c>servers.dat</c>：root compound → <c>servers</c> list of compound。
    /// 每个条目故意把 <c>acceptTextures</c> 插在 <c>ip</c> 前后，验证解析器会跳过非目标标签。
    /// </summary>
    /// <param name="declaredCount">list 头里写的条目数；不同于实际条目时用来模拟坏文件。</param>
    private static byte[] BuildServersDat(
        IEnumerable<(string? Name, string Ip)> servers,
        int? declaredCount = null)
    {
        var items = servers.ToList();

        using var buffer = new MemoryStream();
        WriteByte(buffer, TagCompound);
        WriteName(buffer, string.Empty); // root 名字，Minecraft 写空串

        WriteByte(buffer, TagList);
        WriteName(buffer, ServerListDatReader.ServersKey);
        WriteByte(buffer, TagCompound);
        WriteInt32(buffer, declaredCount ?? items.Count);

        foreach (var (name, ip) in items)
        {
            if (name is not null)
            {
                WriteByte(buffer, TagString);
                WriteName(buffer, "name");
                WriteString(buffer, name);
            }

            WriteByte(buffer, TagByte);
            WriteName(buffer, "acceptTextures");
            WriteByte(buffer, 0);

            WriteByte(buffer, TagString);
            WriteName(buffer, "ip");
            WriteString(buffer, ip);

            // 真实文件里还有 icon（base64 data URI），顺手塞一个，验证跳过逻辑。
            WriteByte(buffer, TagString);
            WriteName(buffer, "icon");
            WriteString(buffer, "data:image/png;base64,AAAA");

            WriteByte(buffer, TagEnd); // 结束这一条 compound
        }

        WriteByte(buffer, TagEnd); // 结束 root compound
        return buffer.ToArray();
    }

    /// <summary>构造「深链 compound + 正常 servers 列表」：深度限制生效时整份文件读不出来。</summary>
    private static byte[] BuildDeeplyNestedDat(int depth, (string? Name, string Ip) server)
    {
        using var buffer = new MemoryStream();
        WriteByte(buffer, TagCompound);
        WriteName(buffer, string.Empty);

        for (var i = 0; i < depth; i++)
        {
            WriteByte(buffer, TagCompound);
            WriteName(buffer, "n" + i);
        }

        for (var i = 0; i < depth; i++)
        {
            WriteByte(buffer, TagEnd);
        }

        WriteByte(buffer, TagList);
        WriteName(buffer, ServerListDatReader.ServersKey);
        WriteByte(buffer, TagCompound);
        WriteInt32(buffer, 1);
        WriteByte(buffer, TagString);
        WriteName(buffer, "name");
        WriteString(buffer, server.Name ?? string.Empty);
        WriteByte(buffer, TagString);
        WriteName(buffer, "ip");
        WriteString(buffer, server.Ip);
        WriteByte(buffer, TagEnd);

        WriteByte(buffer, TagEnd);
        return buffer.ToArray();
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static string WriteDat(string directory, byte[] bytes)
    {
        var path = Path.Combine(directory, "servers.dat");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteByte(Stream stream, int value) => stream.WriteByte((byte)value);

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
        stream.Write(buffer);
    }

    private static void WriteName(Stream stream, string name) => WriteString(stream, name);

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        length[0] = (byte)(bytes.Length >> 8);
        length[1] = (byte)bytes.Length;
        stream.Write(length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
}