using System.Buffers.Binary;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Maintenance;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 本地皮肤库的校验与落盘规则（票据 #42）。
///
/// 这组用例锁的是「用户丢了张图进来会发生什么」：尺寸不对要在导入时说清楚，
/// 目录里多出来的非 PNG 文件不能把列表搞崩，索引可以丢但磁盘上的图不能丢。
/// </summary>
public sealed class CoreSkinning_LocalSkinStoreTests
{
    [Fact]
    public void 标准64x64皮肤通过校验()
    {
        Assert.Null(LocalSkinFormat.Validate(width: 64, height: 64, LocalSkinModel.Classic));
    }

    [Fact]
    public void 宽度不是64倍数时给出提示()
    {
        var error = LocalSkinFormat.Validate(width: 100, height: 64, LocalSkinModel.Classic);

        Assert.NotNull(error);
        Assert.Contains("64", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 高度不是32倍数时给出提示()
    {
        var error = LocalSkinFormat.Validate(width: 64, height: 50, LocalSkinModel.Classic);

        Assert.NotNull(error);
        Assert.Contains("32", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 细手臂皮肤必须恰好64x64()
    {
        Assert.Null(LocalSkinFormat.Validate(width: 64, height: 64, LocalSkinModel.Slim));

        var error = LocalSkinFormat.Validate(width: 128, height: 64, LocalSkinModel.Slim);
        Assert.NotNull(error);
        Assert.Contains("64", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 从PNG头部读出尺寸()
    {
        Assert.True(LocalSkinFormat.TryReadPngSize(PngHeader(64, 32), out var width, out var height));
        Assert.Equal(64, width);
        Assert.Equal(32, height);
    }

    [Fact]
    public void 非PNG内容读不出尺寸()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

        Assert.False(LocalSkinFormat.TryReadPngSize(jpeg, out _, out _));
        Assert.False(LocalSkinFormat.TryReadPngSize([], out _, out _));
    }

    [Fact]
    public async Task 扫描列出顶层PNG并跳过非PNG文件()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "steve.png"), PngHeader(64, 64));
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "alex.PNG"), PngHeader(64, 32));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "notes.txt"), "不是皮肤");
        Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "sub"));
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "sub", "nested.png"), PngHeader(64, 64));

        var result = await store.ScanAsync();

        Assert.Null(result.ErrorText);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, entry => entry.FileName == "steve.png" && entry.Width == 64 && entry.Height == 64);
        Assert.Contains(result.Entries, entry => entry.FileName == "alex.PNG" && entry.Width == 64 && entry.Height == 32);
        Assert.All(result.Entries, entry => Assert.Equal(LocalSkinModel.Classic, entry.Model));
    }

    [Fact]
    public async Task 扩展名是PNG但内容不是的文件被跳过并记原因()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "good.png"), PngHeader(64, 64));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "broken.png"), "这其实是个文本文件");

        var result = await store.ScanAsync();

        Assert.Null(result.ErrorText);
        Assert.Single(result.Entries);
        Assert.Equal("good.png", result.Entries[0].FileName);
        Assert.Contains("broken.png", result.SkippedFileNames);
    }

    [Fact]
    public async Task 目录不存在时扫描得到空列表且不报错()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);

        var result = await store.ScanAsync();

        Assert.Null(result.ErrorText);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task 从文件导入后立刻出现在扫描结果里()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        var source = temp.Combine("download.png");
        File.WriteAllBytes(source, PngHeader(64, 64));

        var result = await store.ImportAsync(source);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Entry);
        Assert.Equal("download.png", result.Entry!.FileName);
        Assert.True(File.Exists(result.Entry.FilePath));

        var scan = await store.ScanAsync();
        Assert.Single(scan.Entries);
        Assert.Equal("download.png", scan.Entries[0].FileName);
    }

    [Fact]
    public async Task 导入同名文件时自动改名不覆盖已有皮肤()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        var first = temp.Combine("steve.png");
        var second = temp.Combine("second", "steve.png");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllBytes(first, PngHeader(64, 64));
        File.WriteAllBytes(second, PngHeader(64, 32));

        var firstResult = await store.ImportAsync(first);
        var secondResult = await store.ImportAsync(second);

        Assert.True(firstResult.IsSuccess);
        Assert.True(secondResult.IsSuccess);
        Assert.Equal("steve.png", firstResult.Entry!.FileName);
        Assert.NotEqual("steve.png", secondResult.Entry!.FileName);
        Assert.Equal(2, (await store.ScanAsync()).Entries.Count);
        // 原来那张不能被覆盖：第一张还是 64×64。
        Assert.Equal(64, firstResult.Entry.Height);
    }

    [Fact]
    public async Task 导入尺寸不合规的PNG被拒绝且不落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        var source = temp.Combine("weird.png");
        File.WriteAllBytes(source, PngHeader(70, 64));

        var result = await store.ImportAsync(source);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Entry);
        Assert.Contains("64", result.ErrorText, StringComparison.Ordinal);
        Assert.Empty((await store.ScanAsync()).Entries);
    }

    [Fact]
    public async Task 导入非PNG文件被拒绝()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        var source = temp.Combine("fake.png");
        File.WriteAllText(source, "我不是图片");

        var result = await store.ImportAsync(source);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorText);
        Assert.Empty((await store.ScanAsync()).Entries);
    }

    [Fact]
    public async Task 保存网络皮肤时把模型写进索引并落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);

        var result = await store.SaveAsync("网络皮肤.png", PngHeader(64, 64), LocalSkinModel.Slim);

        Assert.True(result.IsSuccess);
        Assert.Equal(LocalSkinModel.Slim, result.Entry!.Model);

        // 换一个实例再扫：模型只能来自磁盘索引，不能是内存残留。
        var reopened = new LocalSkinStore(paths, NullAppLog.Instance);
        var scan = await reopened.ScanAsync();
        Assert.Single(scan.Entries);
        Assert.Equal(LocalSkinModel.Slim, scan.Entries[0].Model);
    }

    [Fact]
    public async Task 删除本地皮肤同时删掉文件和索引条目()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        var saved = await store.SaveAsync("mine.png", PngHeader(64, 64));

        var result = await store.DeleteAsync(saved.Entry!.FileName);

        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(saved.Entry.FilePath));
        Assert.Empty((await store.ScanAsync()).Entries);
    }

    [Fact]
    public async Task 删除时拒绝跳出皮肤目录的文件名()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        var victim = temp.Combine("important.txt");
        File.WriteAllText(victim, "别删我");

        var result = await store.DeleteAsync("../important.txt");

        Assert.False(result.IsSuccess);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task 清理缓存时不会删掉本地皮肤()
    {
        using var temp = new TempDirectory();

        // 让缓存类别直接指向皮肤目录：这是「类别定义写错」的极端情形，清理会真的遍历到皮肤文件。
        var paths = new CacheIsSkinsPaths(temp.Path);
        var store = new LocalSkinStore(paths, NullAppLog.Instance);
        await store.SaveAsync("steve.png", PngHeader(64, 64));
        var service = new StorageService(paths, NullAppLog.Instance);

        var result = await service.CleanAsync([StorageCategoryKeys.Cache]);

        // 清理被接受并跑完了，只是皮肤文件被保护名单挡住。
        Assert.Null(result.Rejected);
        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, "steve.png")));
        Assert.True(Directory.Exists(store.DirectoryPath));
    }

    /// <summary>只要 PNG 签名 + IHDR 的宽高，够校验用；不需要真的像素数据。</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[29];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    /// <summary>缓存目录被误配成皮肤目录，用来验证受保护顶层目录挡得住清理。</summary>
    private sealed class CacheIsSkinsPaths : ILauncherPaths
    {
        public CacheIsSkinsPaths(string root)
        {
            RootDirectory = root;
        }

        public string RootDirectory { get; }

        public string MinecraftDirectory => Path.Combine(RootDirectory, ".minecraft");

        public string VersionsDirectory => Path.Combine(MinecraftDirectory, "versions");

        public string LibrariesDirectory => Path.Combine(MinecraftDirectory, "libraries");

        public string AssetsDirectory => Path.Combine(MinecraftDirectory, "assets");

        public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

        public string LogsDirectory => Path.Combine(RootDirectory, "logs");

        public string CacheDirectory => Path.Combine(RootDirectory, "skins");

        public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

        public string AccountsFile => Path.Combine(RootDirectory, "accounts.json");

        public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

        public string ServersFile => Path.Combine(RootDirectory, "servers.json");

        public void SetMinecraftDirectory(string path)
        {
        }

        public void EnsureCreated()
        {
        }
    }
}
