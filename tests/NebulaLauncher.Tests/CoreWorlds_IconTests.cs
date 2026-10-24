using System.Text;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 存档缩略图：从 level.dat 的 <c>Data.icon</c>（base64 PNG）提取并缓存。
///
/// 每个用例自己拼 NBT 字节、自己造 PNG 字节，不读演示脚本、不依赖真实存档：
/// 演示脚本将来改格式不应让这些用例跟着红。PNG 只校验魔数，所以这里用的「PNG」是
/// 魔数 + 任意载荷——服务本身也只认魔数，不解析 IHDR/IDAT。
/// </summary>
public class CoreWorlds_IconTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task 有图标_解出缓存文件且PNG头正确()
    {
        using var paths = new CoreAssetsPaths();
        var png = Png(marker: 0x11, extraBytes: 8);
        var world = CreateWorld(paths, Convert.ToBase64String(png));

        var service = new WorldIconService(paths);
        var cachePath = await service.GetIconPathAsync(world);

        Assert.NotNull(cachePath);
        Assert.True(File.Exists(cachePath), cachePath);
        Assert.StartsWith(Path.Combine(paths.CacheDirectory, "world-icons"), cachePath, StringComparison.OrdinalIgnoreCase);

        var written = File.ReadAllBytes(cachePath!);
        Assert.Equal(png, written);
        Assert.Equal(PngMagic, written[..PngMagic.Length]);
    }

    [Fact]
    public async Task 无图标_返回null()
    {
        using var paths = new CoreAssetsPaths();
        var world = CreateWorld(paths, iconBase64: null);

        var service = new WorldIconService(paths);
        Assert.Null(await service.GetIconPathAsync(world));
        Assert.False(Directory.Exists(Path.Combine(paths.CacheDirectory, "world-icons")));
    }

    [Fact]
    public async Task 标签存在但为空_返回null()
    {
        using var paths = new CoreAssetsPaths();
        var world = CreateWorld(paths, iconBase64: string.Empty);

        var service = new WorldIconService(paths);
        Assert.Null(await service.GetIconPathAsync(world));
    }

    [Fact]
    public async Task 坏base64_返回null()
    {
        using var paths = new CoreAssetsPaths();
        var world = CreateWorld(paths, iconBase64: "这不是 base64 编码的内容!!!");

        var service = new WorldIconService(paths);
        Assert.Null(await service.GetIconPathAsync(world));
    }

    [Fact]
    public async Task 非PNG载荷_返回null()
    {
        using var paths = new CoreAssetsPaths();
        var world = CreateWorld(paths, Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text, not a png")));

        var service = new WorldIconService(paths);
        Assert.Null(await service.GetIconPathAsync(world));
    }

    [Fact]
    public async Task 原始base64超限_返回null()
    {
        using var paths = new CoreAssetsPaths();

        // 96KB 上限之外：base64(96KB 字节) = 128KB 字符，超过 96KB 字符上限。
        var huge = new byte[WorldIconService.MaxBase64Chars];
        var world = CreateWorld(paths, Convert.ToBase64String(huge));

        var service = new WorldIconService(paths);
        Assert.Null(await service.GetIconPathAsync(world));
    }

    [Fact]
    public async Task 解码后超限_返回null()
    {
        using var paths = new CoreAssetsPaths();

        // base64 长度仍在 96KB 以内（约 87KB 字符），但解码后是 64KB+1 字节。
        var over = Png(marker: 0x22, extraBytes: WorldIconService.MaxPngBytes - PngMagic.Length + 1);
        Assert.True(over.Length > WorldIconService.MaxPngBytes);
        Assert.True(Convert.ToBase64String(over).Length <= WorldIconService.MaxBase64Chars);

        var world = CreateWorld(paths, Convert.ToBase64String(over));

        var service = new WorldIconService(paths);
        Assert.Null(await service.GetIconPathAsync(world));
    }

    [Fact]
    public async Task 缓存复用_第二次不再写文件()
    {
        using var paths = new CoreAssetsPaths();
        var png = Png(marker: 0x33, extraBytes: 16);
        var base64 = Convert.ToBase64String(png);
        var world = CreateWorld(paths, base64);

        var service = new WorldIconService(paths);
        var cachePath = await service.GetIconPathAsync(world);
        Assert.NotNull(cachePath);

        // 独占打开缓存 png：第二次调用若还想写盘就会失败并返回 null。
        // 这是「命中缓存不写文件」最直接的判据——比比较 mtime 可靠（同一时间片内重写 mtime 可能不变）。
        using (var hold = new FileStream(cachePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(hold.Length > 0);
            var second = await service.GetIconPathAsync(world);
            Assert.Equal(cachePath, second);
        }

        Assert.Equal(png, File.ReadAllBytes(cachePath!));
    }

    [Fact]
    public async Task levelDat变更后_缓存失效并重写()
    {
        using var paths = new CoreAssetsPaths();
        var world = CreateWorld(paths, Convert.ToBase64String(Png(marker: 0x44, extraBytes: 4)));

        var service = new WorldIconService(paths);
        var cachePath = await service.GetIconPathAsync(world);
        Assert.NotNull(cachePath);

        // 重新生成 level.dat：内容与长度都变（不同的 PNG 载荷长度），等同游戏写完存档。
        var newPng = Png(marker: 0x55, extraBytes: 64);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), BuildLevelDat(Convert.ToBase64String(newPng), "TestWorld"));

        var rebuilt = await service.GetIconPathAsync(world);
        Assert.Equal(cachePath, rebuilt);
        Assert.Equal(newPng, File.ReadAllBytes(rebuilt!));
    }

    [Fact]
    public async Task 世界目录不存在_返回null不抛()
    {
        using var paths = new CoreAssetsPaths();
        var service = new WorldIconService(paths);
        var missing = Path.Combine(paths.MinecraftDirectory, "saves", "NotThere");

        Assert.Null(await service.GetIconPathAsync(missing));
        Assert.Null(await service.GetIconPathAsync(string.Empty));
    }

    [Fact]
    public async Task 扫描列表_填充HasIcon且不解码图标()
    {
        using var paths = new CoreAssetsPaths();
        CreateWorld(paths, Convert.ToBase64String(Png(marker: 0x66, extraBytes: 4)), name: "有图标的世界");
        CreateWorld(paths, iconBase64: null, name: "没图标的世界");

        var service = new BackupService(paths, new CoreAssetsLog());
        var worlds = await service.ListWorldsAsync(paths.MinecraftDirectory);

        Assert.Equal(2, worlds.Count);
        var withIcon = Assert.Single(worlds, world => world.Name == "有图标的世界");
        var withoutIcon = Assert.Single(worlds, world => world.Name == "没图标的世界");
        Assert.True(withIcon.HasIcon);
        Assert.False(withoutIcon.HasIcon);

        // 扫描只探测标签：缓存路径留给按需加载，扫描路径不写盘。
        Assert.Null(withIcon.IconCachePath);
        Assert.False(Directory.Exists(Path.Combine(paths.CacheDirectory, "world-icons")));
    }

    // ---------------- 测试脚手架 ----------------

    private static string CreateWorld(CoreAssetsPaths paths, string? iconBase64, string name = "TestWorld")
    {
        var world = Path.Combine(paths.MinecraftDirectory, "saves", name);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), BuildLevelDat(iconBase64, name));
        return world;
    }

    /// <summary>魔数 + 填充字节；服务只校验魔数，不解析 PNG 结构。</summary>
    private static byte[] Png(byte marker, int extraBytes)
    {
        var bytes = new byte[PngMagic.Length + extraBytes];
        PngMagic.CopyTo(bytes, 0);
        for (var index = PngMagic.Length; index < bytes.Length; index++)
        {
            bytes[index] = marker;
        }

        return bytes;
    }

    private static byte[] BuildLevelDat(string? iconBase64, string levelName)
    {
        var builder = new NbtBuilder();
        builder.Compound(string.Empty, root =>
        {
            root.Compound("Data", data =>
            {
                data.String("LevelName", levelName);
                data.Int("GameType", 0);
                data.Compound("Version", version => version.String("Name", "1.21.4"));
                if (iconBase64 is not null)
                {
                    data.String("icon", iconBase64);
                }
            });
        });

        return builder.ToArray();
    }

    /// <summary>手写最小 NBT（大端序）。不复用 NbtWriter：自己写、自己读的往返测不出写错。</summary>
    private sealed class NbtBuilder
    {
        private readonly List<byte> _bytes = new();

        public NbtBuilder Compound(string name, Action<NbtBuilder> body)
        {
            Header(10, name);
            body(this);
            _bytes.Add(0);
            return this;
        }

        public NbtBuilder String(string name, string value)
        {
            Header(8, name);
            WriteString(value);
            return this;
        }

        public NbtBuilder Int(string name, int value)
        {
            Header(3, name);
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();

        private void Header(byte type, string name)
        {
            _bytes.Add(type);
            WriteString(name);
        }

        private void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            _bytes.Add((byte)(bytes.Length >> 8));
            _bytes.Add((byte)bytes.Length);
            _bytes.AddRange(bytes);
        }
    }
}