using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// level.dat 的写回：构造临时存档 → 改字段 → 写回 → 再读回。
///
/// 每个用例都用 <see cref="CoreAssetsPaths"/> 建独立临时目录，自己拼 NBT 字节，
/// 不联网、不依赖真实存档、不读用户机器上的 .minecraft。
/// </summary>
public class CoreAssets_LevelDatTests
{
    [Fact]
    public async Task 往返_改种子模式难度作弊与世界名_读回一致且仍是gzip()
    {
        using var fixture = new LevelDatFixture(gzip: true);

        var result = await fixture.UpdateAsync(new WorldEditRequest
        {
            Seed = -8877665544332211L,
            GameMode = 1,
            Difficulty = 3,
            AllowCommands = true,
            KeepInventory = true,
            LevelName = "改过的世界",
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(fixture.BackupPath, result.BackupPath);

        var world = await fixture.ReadWorldAsync();
        Assert.Equal("改过的世界", world.Name);
        Assert.Equal(-8877665544332211L, world.Seed);
        Assert.Equal(1, world.GameType);
        Assert.Equal("创造", world.GameMode);
        Assert.Equal(3, world.Difficulty);
        Assert.True(world.AllowCommands);
        Assert.True(world.KeepInventory);
        Assert.Equal("1.20.1", world.VersionName);

        // 源文件是 gzip，写回后必须还是 gzip（Minecraft 两种都认，但不一致会让外部工具打不开）。
        Assert.True(LevelDatFixture.IsGzip(fixture.LevelDatPath));
    }

    [Fact]
    public async Task 往返_非gzip存档写回仍非gzip()
    {
        using var fixture = new LevelDatFixture(gzip: false);

        var result = await fixture.UpdateAsync(new WorldEditRequest { Seed = 42L });
        Assert.True(result.Success, result.Error);

        Assert.False(LevelDatFixture.IsGzip(fixture.LevelDatPath));
        var world = await fixture.ReadWorldAsync();
        Assert.Equal(42L, world.Seed);
        Assert.Equal("新世界", world.Name);
    }

    [Fact]
    public async Task 只改种子时_未知模组标签与版本信息原样保留()
    {
        using var fixture = new LevelDatFixture(gzip: true);
        var before = LevelDatFixture.DecodedText(fixture.LevelDatPath);

        var result = await fixture.UpdateAsync(new WorldEditRequest { Seed = 7L });
        Assert.True(result.Success, result.Error);

        var after = LevelDatFixture.DecodedText(fixture.LevelDatPath);

        // 模组写进去的字段（本启动器不认识）必须原样留在文件里。
        foreach (var marker in new[] { "ForgeModMarker", "mod-marker-value", "SomeModData", "TopLevelModTag", "alpha", "beta" })
        {
            Assert.Contains(marker, before, StringComparison.Ordinal);
            Assert.Contains(marker, after, StringComparison.Ordinal);
        }

        // 没被点名的字段也不许被改动。
        var world = await fixture.ReadWorldAsync();
        Assert.Equal("新世界", world.Name);
        Assert.Equal("1.20.1", world.VersionName);
        Assert.Equal(0, world.GameType);
        Assert.Equal(2, world.Difficulty);
    }

    [Fact]
    public async Task 改保留物品栏_GameRules里其它规则不被吞掉()
    {
        using var fixture = new LevelDatFixture(gzip: true);

        var result = await fixture.UpdateAsync(new WorldEditRequest { KeepInventory = true });
        Assert.True(result.Success, result.Error);

        var world = await fixture.ReadWorldAsync();
        Assert.True(world.KeepInventory);

        var text = LevelDatFixture.DecodedText(fixture.LevelDatPath);
        foreach (var rule in new[] { "keepInventory", "doDaylightCycle", "randomTickSpeed", "mobGriefing" })
        {
            Assert.Contains(rule, text, StringComparison.Ordinal);
        }

        // 其它规则的原始值还在（doDaylightCycle=true / randomTickSpeed=3）。
        Assert.Contains("doDaylightCycle", text, StringComparison.Ordinal);
        Assert.Contains("randomTickSpeed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写入前生成nebula备份_内容等于改动前()
    {
        using var fixture = new LevelDatFixture(gzip: true);
        var original = File.ReadAllBytes(fixture.LevelDatPath);

        var result = await fixture.UpdateAsync(new WorldEditRequest { LevelName = "备份测试" });
        Assert.True(result.Success, result.Error);

        Assert.True(File.Exists(fixture.BackupPath));
        Assert.Equal(original, File.ReadAllBytes(fixture.BackupPath));
        Assert.NotEqual(original, File.ReadAllBytes(fixture.LevelDatPath));
    }

    [Fact]
    public async Task session_lock被占用时拒绝写入_文件不变()
    {
        using var fixture = new LevelDatFixture(gzip: true);
        var original = File.ReadAllBytes(fixture.LevelDatPath);
        var lockPath = Path.Combine(fixture.WorldDirectory, "session.lock");
        File.WriteAllBytes(lockPath, [0xDE, 0xAD, 0xBE, 0xEF]);

        WorldEditResult result;
        await using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await fixture.UpdateAsync(new WorldEditRequest { Seed = 1L });
        }

        Assert.False(result.Success);
        Assert.Contains("session.lock", result.Error, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(fixture.LevelDatPath));
        Assert.False(File.Exists(fixture.BackupPath));
    }

    [Fact]
    public async Task 种子可写超int范围的长整数()
    {
        using var fixture = new LevelDatFixture(gzip: true);

        foreach (var seed in new[] { long.MaxValue - 1, long.MinValue, 0L })
        {
            var result = await fixture.UpdateAsync(new WorldEditRequest { Seed = seed });
            Assert.True(result.Success, result.Error);

            var world = await fixture.ReadWorldAsync();
            Assert.Equal(seed, world.Seed);
        }
    }

    [Fact]
    public async Task 缺少Data段时拒绝写入_文件不变()
    {
        using var fixture = new LevelDatFixture(gzip: true, withData: false);
        var original = File.ReadAllBytes(fixture.LevelDatPath);

        var result = await fixture.UpdateAsync(new WorldEditRequest { Seed = 1L });

        Assert.False(result.Success);
        Assert.Contains("Data", result.Error, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(fixture.LevelDatPath));
    }

    // ---------------- 测试脚手架 ----------------

    /// <summary>
    /// 一个临时世界：<c>&lt;tmp&gt;/game/saves/TestWorld/level.dat</c>。
    /// 构造的 level.dat 含真 level.dat 常见字段、四条 gamerule、以及若干「模组写的、我们不认识」的标签。
    /// </summary>
    private sealed class LevelDatFixture : IDisposable
    {
        private readonly CoreAssetsPaths _paths = new();

        public LevelDatFixture(bool gzip, bool withData = true)
        {
            WorldDirectory = Path.Combine(_paths.MinecraftDirectory, "saves", "TestWorld");
            Directory.CreateDirectory(WorldDirectory);
            LevelDatPath = Path.Combine(WorldDirectory, "level.dat");
            File.WriteAllBytes(LevelDatPath, BuildLevelDat(gzip, withData));
        }

        public string WorldDirectory { get; }

        public string LevelDatPath { get; }

        public string BackupPath => LevelDatPath + ".nebula.bak";

        public Task<WorldEditResult> UpdateAsync(WorldEditRequest request) =>
            new BackupService(_paths, new CoreAssetsLog()).UpdateWorldAsync(WorldDirectory, request);

        public async Task<WorldInfo> ReadWorldAsync()
        {
            var service = new BackupService(_paths, new CoreAssetsLog());
            var worlds = await service.ListWorldsAsync(_paths.MinecraftDirectory);
            return Assert.Single(worlds);
        }

        public static bool IsGzip(string path)
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 0x1F && stream.ReadByte() == 0x8B;
        }

        public static string DecodedText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                bytes = output.ToArray();
            }

            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose() => _paths.Dispose();

        private static byte[] BuildLevelDat(bool gzip, bool withData)
        {
            var builder = new NbtBuilder();
            builder.Compound(string.Empty, root =>
            {
                root.Long("TopLevelModTag", 777L);

                if (!withData)
                {
                    return;
                }

                root.Compound("Data", data =>
                {
                    data.String("LevelName", "新世界");
                    data.Int("GameType", 0);
                    data.Long("RandomSeed", 12345L);
                    data.ByteTag("Difficulty", 2);
                    data.ByteTag("allowCommands", 0);
                    data.ByteTag("hardcore", 0);
                    data.Long("LastPlayed", 1729500000000L);
                    data.Int("SpawnX", 0);

                    data.Compound("Version", version =>
                    {
                        version.String("Name", "1.20.1");
                        version.Int("Id", 3465);
                    });

                    data.Compound("GameRules", rules =>
                    {
                        rules.String("keepInventory", "false");
                        rules.String("doDaylightCycle", "true");
                        rules.String("randomTickSpeed", "3");
                        rules.String("mobGriefing", "true");
                    });

                    // 下面这些模拟「模组写进去、启动器不认识」的标签：
                    // 它们必须原样写回，不能被吞掉。
                    data.Int("SomeModData", 987654321);
                    data.ByteArray("Checksum", [1, 2, 3, 4, 5]);
                    data.Compound("ForgeModMarker", mod =>
                    {
                        mod.String("Marker", "mod-marker-value");
                        mod.StringList("List", "alpha", "beta");
                        mod.IntArray("Ints", 1, 2, 3);
                        mod.LongArray("Longs", -1L, 9000000000L);
                    });
                });
            });

            var raw = builder.ToArray();
            if (!gzip)
            {
                return raw;
            }

            using var output = new MemoryStream();
            using (var gzipStream = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzipStream.Write(raw, 0, raw.Length);
            }

            return output.ToArray();
        }
    }

    /// <summary>
    /// 测试用的最小 NBT 构造器（大端手写）。
    ///
    /// 故意不复用 <c>NbtWriter</c>：那样就是「自己写、自己读」，写反了也照样通过。
    /// </summary>
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
            WriteInt32(value);
            return this;
        }

        public NbtBuilder Long(string name, long value)
        {
            Header(4, name);
            WriteInt64(value);
            return this;
        }

        public NbtBuilder ByteTag(string name, byte value)
        {
            Header(1, name);
            _bytes.Add(value);
            return this;
        }

        public NbtBuilder ByteArray(string name, byte[] value)
        {
            Header(7, name);
            WriteInt32(value.Length);
            _bytes.AddRange(value);
            return this;
        }

        public NbtBuilder IntArray(string name, params int[] value)
        {
            Header(11, name);
            WriteInt32(value.Length);
            foreach (var item in value)
            {
                WriteInt32(item);
            }

            return this;
        }

        public NbtBuilder LongArray(string name, params long[] value)
        {
            Header(12, name);
            WriteInt32(value.Length);
            foreach (var item in value)
            {
                WriteInt64(item);
            }

            return this;
        }

        public NbtBuilder StringList(string name, params string[] value)
        {
            Header(9, name);
            _bytes.Add(8);
            WriteInt32(value.Length);
            foreach (var item in value)
            {
                WriteString(item);
            }

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

        private void WriteInt32(int value)
        {
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        private void WriteInt64(long value)
        {
            for (var index = 7; index >= 0; index--)
            {
                _bytes.Add((byte)(value >> (index * 8)));
            }
        }
    }
}