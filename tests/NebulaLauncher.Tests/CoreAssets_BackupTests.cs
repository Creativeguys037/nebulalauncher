using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Backups;

namespace NebulaLauncher.Tests;

public class CoreAssets_BackupTests
{
    [Fact]
    public async Task 列出世界_从level_dat读出名称版本与模式()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-worlds");
        WriteWorld(gameDirectory, "我的世界", "1.20.4", gameType: 1, files: ("level.dat_old", [1, 2, 3]));

        var service = new BackupService(paths);
        var worlds = await service.ListWorldsAsync(gameDirectory);

        var world = Assert.Single(worlds);
        Assert.Equal("我的世界", world.Name);
        Assert.Equal("1.20.4", world.VersionName);
        Assert.Equal("创造", world.GameMode);
        Assert.True(world.SizeBytes > 0);
    }

    [Fact]
    public async Task 列出世界_目录不存在时返回空集合()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);

        var worlds = await service.ListWorldsAsync(Path.Combine(paths.RootDirectory, "no-such-game"));

        Assert.NotNull(worlds);
        Assert.Empty(worlds);
    }

    [Fact]
    public async Task 备份_创建列表与还原全链路()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-backup");
        var worldDirectory = WriteWorld(gameDirectory, "World", "1.20.1", gameType: 0,
            files: ("session.lock", [9, 9, 9]));

        var service = new BackupService(paths);
        var progressValues = new List<double>();
        var entry = await service.CreateAsync(worldDirectory, "测试备份", new Progress<double>(progressValues.Add));

        Assert.True(File.Exists(entry.FilePath));
        Assert.Equal("World", entry.WorldName);
        Assert.Equal("测试备份", entry.Note);
        Assert.True(entry.SizeBytes > 0);
        Assert.True(File.Exists(entry.FilePath + ".meta.json"));
        Assert.Equal(1d, progressValues[^1]);

        var backups = await service.ListBackupsAsync();
        var listed = Assert.Single(backups);
        Assert.Equal(entry.FilePath, listed.FilePath);
        Assert.Equal("测试备份", listed.Note);

        // 还原到一个干净的游戏目录
        var otherGame = Path.Combine(paths.RootDirectory, "game-restore");
        Directory.CreateDirectory(otherGame);
        await service.RestoreAsync(listed, otherGame);

        var restoredLevelDat = Path.Combine(otherGame, "saves", "World", "level.dat");
        Assert.True(File.Exists(restoredLevelDat));

        var restored = await service.ListWorldsAsync(otherGame);
        var world = Assert.Single(restored);
        Assert.Equal("World", world.Name);

        await service.DeleteAsync(listed);
        Assert.False(File.Exists(entry.FilePath));
        Assert.False(File.Exists(entry.FilePath + ".meta.json"));
        Assert.Empty(await service.ListBackupsAsync());
    }

    [Fact]
    public async Task 备份_世界目录不存在时抛DirectoryNotFound()
    {
        using var paths = new CoreAssetsPaths();
        var service = new BackupService(paths);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => service.CreateAsync(Path.Combine(paths.RootDirectory, "missing-world")));
    }

    [Fact]
    public async Task 还原_非法条目被跳过不写出目标目录()
    {
        using var paths = new CoreAssetsPaths();
        var archivePath = Path.Combine(paths.BackupsDirectory, "evil.zip");
        Directory.CreateDirectory(paths.BackupsDirectory);

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("bad");
        }

        var service = new BackupService(paths);
        var targetGame = Path.Combine(paths.RootDirectory, "game-evil");
        Directory.CreateDirectory(targetGame);

        var entryModel = new NebulaLauncher.Core.Models.BackupEntry
        {
            Id = "evil",
            FilePath = archivePath,
            WorldName = "evil",
        };

        await service.RestoreAsync(entryModel, targetGame);

        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "saves", "..", "escaped.txt")));
    }

    private static string WriteWorld(
        string gameDirectory,
        string worldName,
        string minecraftVersion,
        int gameType,
        params (string Name, byte[] Content)[] files)
    {
        var worldDirectory = Path.Combine(gameDirectory, "saves", worldName);
        Directory.CreateDirectory(worldDirectory);

        var levelDat = Path.Combine(worldDirectory, "level.dat");
        using (var file = File.Create(levelDat))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        {
            NbtWriter.WriteLevelDat(gzip, worldName, minecraftVersion, gameType);
        }

        foreach (var (name, content) in files)
        {
            File.WriteAllBytes(Path.Combine(worldDirectory, name), content);
        }

        return worldDirectory;
    }

    /// <summary>测试用的最小 NBT 写出器，只产出 NbtReader 需要的那几个标签。</summary>
    private static class NbtWriter
    {
        public static void WriteLevelDat(Stream stream, string levelName, string versionName, int gameType)
        {
            stream.WriteByte(10); // TAG_Compound（根）
            WriteString(stream, string.Empty);

            WriteByteTag(stream, "Data", 10, null, openCompound: true);
            WriteStringTag(stream, "LevelName", levelName);
            WriteIntTag(stream, "GameType", gameType);

            WriteByteTag(stream, "Version", 10, null, openCompound: true);
            WriteStringTag(stream, "Name", versionName);
            stream.WriteByte(0); // 结束 Version

            WriteLongTag(stream, "RandomSeed", 123456789L);
            stream.WriteByte(0); // 结束 Data
            stream.WriteByte(0); // 结束根
        }

        private static void WriteByteTag(
            Stream stream,
            string name,
            byte type,
            byte[]? content,
            bool openCompound)
        {
            stream.WriteByte(type);
            WriteString(stream, name);
            if (openCompound)
            {
                return;
            }

            if (content is not null)
            {
                foreach (var b in content)
                {
                    stream.WriteByte(b);
                }
            }
        }

        private static void WriteStringTag(Stream stream, string name, string value)
        {
            stream.WriteByte(8);
            WriteString(stream, name);
            WriteString(stream, value);
        }

        private static void WriteIntTag(Stream stream, string name, int value)
        {
            stream.WriteByte(3);
            WriteString(stream, name);
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteLongTag(Stream stream, string name, long value)
        {
            stream.WriteByte(4);
            WriteString(stream, name);
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                stream.WriteByte((byte)(value >> shift));
            }
        }

        private static void WriteString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
