using System.Text;
using NebulaLauncher.Core.Mods;

// Modrinth 的哈希算法就是 SHA1，测试要按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// <see cref="ModFingerprint"/> 的算法与缓存测试。
///
/// 期望值不是「跑一遍代码把结果抄进来」得到的：CurseForge 指纹按 PCL2
/// <c>LocalResourceFile.CurseForgeHash</c> 的算法另用一份 Python 实现算出，
/// 两份实现逐字节一致才写进断言。这样一旦有人「顺手优化」了位移或常数，
/// 测试会立刻红，而不是等到线上反查全部失败才发现。
///
/// 缓存用例用 <see cref="ModFingerprint.ComputedFileCount"/> 判断是否真的命中：
/// 命中与否的返回值完全一样，只看返回值区分不出「缓存生效」和「重算得到同一答案」。
/// </summary>
public class CoreAssets_ModFingerprintTests
{
    /// <summary>空文件只剩收尾三步，用来确认常数与移位方向。</summary>
    [Fact]
    public void CurseForge指纹_与已知向量一致()
    {
        Assert.Equal(1540447798u, ModFingerprint.CurseForgeFingerprint(ReadOnlySpan<byte>.Empty));
        Assert.Equal(2788266382u, ModFingerprint.CurseForgeFingerprint(Encoding.UTF8.GetBytes("hello")));
        Assert.Equal(2824650221u, ModFingerprint.CurseForgeFingerprint(Encoding.UTF8.GetBytes("hello world")));
        Assert.Equal(
            3751777527u,
            ModFingerprint.CurseForgeFingerprint(Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog")));
    }

    /// <summary>0–31 的完整字节序列覆盖「过滤 / 不过滤」两类值，是校验 <c>IsSkipped</c> 的最短向量。</summary>
    [Fact]
    public void CurseForge指纹_过滤空白字节()
    {
        var payload = new byte[32];

        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)index;
        }

        Assert.Equal(104916955u, ModFingerprint.CurseForgeFingerprint(payload));
    }

    /// <summary>
    /// 剔除 9/10/13/32 的直接后果：CRLF 与 LF 结尾的同内容文本必须是同一个指纹。
    /// 这条不是「顺便测一下」，整合包作者在不同系统上改过的配置会让同一份文件出现两种换行。
    /// </summary>
    [Fact]
    public void CurseForge指纹_换行风格不影响结果()
    {
        var crlf = ModFingerprint.CurseForgeFingerprint(Encoding.UTF8.GetBytes("line1\r\nline2"));
        var lf = ModFingerprint.CurseForgeFingerprint(Encoding.UTF8.GetBytes("line1\nline2"));

        Assert.Equal(2537081003u, crlf);
        Assert.Equal(crlf, lf);
    }

    [Fact]
    public void Sha1_输出大写十六进制()
    {
        Assert.Equal("AAF4C61DDCC5E8A2DABEDE0F3B482CD9AEA9434D", ModFingerprint.Sha1Hex(Encoding.UTF8.GetBytes("hello")));
        Assert.Equal("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709", ModFingerprint.Sha1Hex(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public async Task 缓存_第二次调用不重算()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-cache");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", Encoding.UTF8.GetBytes("sodium-payload"));

        var fingerprint = new ModFingerprint(paths.CacheDirectory, new CoreAssetsLog());

        var first = await fingerprint.GetHashesAsync(file, gameDirectory);
        var second = await fingerprint.GetHashesAsync(file, gameDirectory);

        Assert.Equal(1, fingerprint.ComputedFileCount);
        Assert.NotNull(first.Sha1);
        Assert.Equal(first.Sha1, second.Sha1);
        Assert.Equal(first.CurseForgeFingerprint, second.CurseForgeFingerprint);

        Assert.NotNull(fingerprint.CacheFilePath);
        Assert.True(File.Exists(fingerprint.CacheFilePath), "第一次计算后应当写出缓存文件");
    }

    [Fact]
    public async Task 缓存_文件变化后自动失效()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-invalidate");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", Encoding.UTF8.GetBytes("v1"));

        var fingerprint = new ModFingerprint(paths.CacheDirectory, new CoreAssetsLog());
        var first = await fingerprint.GetHashesAsync(file, gameDirectory);

        // 长度与时间戳都必须变：缓存键把两者都算进去了。
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("v2-longer"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

        var second = await fingerprint.GetHashesAsync(file, gameDirectory);

        Assert.Equal(2, fingerprint.ComputedFileCount);
        Assert.NotEqual(first.Sha1, second.Sha1);
    }

    /// <summary>
    /// 同相对路径、同内容的两个实例目录应当共用一条缓存：
    /// 整合包整体挪个位置就重算几百个 jar 是不可接受的。
    /// </summary>
    [Fact]
    public async Task 缓存_同相对路径换安装位置仍命中()
    {
        using var paths = new CoreAssetsPaths();
        var instanceA = Path.Combine(paths.RootDirectory, "instance-a");
        var instanceB = Path.Combine(paths.RootDirectory, "instance-b");
        var payload = Encoding.UTF8.GetBytes("same-bytes");
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var fileA = await CreateModAsync(instanceA, "x.jar", payload);
        var fileB = await CreateModAsync(instanceB, "x.jar", payload);
        File.SetLastWriteTimeUtc(fileA, stamp);
        File.SetLastWriteTimeUtc(fileB, stamp);

        var fingerprint = new ModFingerprint(paths.CacheDirectory, new CoreAssetsLog());

        await fingerprint.GetHashesAsync(fileA, instanceA);
        await fingerprint.GetHashesAsync(fileB, instanceB);

        Assert.Equal(1, fingerprint.ComputedFileCount);
    }

    /// <summary>缓存只是加速手段，被安全软件锁住 / 被用户改坏时必须静默退化成「重新计算」。</summary>
    [Fact]
    public async Task 缓存_损坏时不抛异常仍能算出哈希()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-broken-cache");
        var payload = Encoding.UTF8.GetBytes("payload");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", payload);

        Directory.CreateDirectory(paths.CacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.CacheDirectory, ModFingerprint.CacheFileName), "{ this is not json");

        var fingerprint = new ModFingerprint(paths.CacheDirectory, new CoreAssetsLog());
        var hashes = await fingerprint.GetHashesAsync(file, gameDirectory);

        Assert.Equal(ModFingerprint.Sha1Hex(payload), hashes.Sha1);
        Assert.Equal(ModFingerprint.CurseForgeFingerprint(payload), hashes.CurseForgeFingerprint);
    }

    [Fact]
    public async Task 文件不存在_返回空哈希不抛异常()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-missing");
        Directory.CreateDirectory(gameDirectory);

        var fingerprint = new ModFingerprint(paths.CacheDirectory, new CoreAssetsLog());
        var hashes = await fingerprint.GetHashesAsync(Path.Combine(gameDirectory, "missing.jar"), gameDirectory);

        Assert.Null(hashes.Sha1);
        Assert.Equal(0u, hashes.CurseForgeFingerprint);
        Assert.Equal(0, fingerprint.ComputedFileCount);
    }

    /// <summary>
    /// 流式实现的边界：80 KB 的缓冲区不是 4 的倍数，4 字节分组会横跨两块。
    /// 这条用例用 200 KB 数据把「整块纯函数」与「分块读盘」两条路径对齐。
    /// </summary>
    [Fact]
    public async Task 指纹_分块读取与整块计算一致()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-chunk");
        var payload = new byte[200 * 1024];
        new Random(20261021).NextBytes(payload);

        // 混入空白，确保过滤逻辑也在跨块边界上被测到。
        for (var index = 0; index < payload.Length; index += 997)
        {
            payload[index] = (byte)' ';
        }

        var file = await CreateModAsync(gameDirectory, "big.jar", payload);

        var fingerprint = new ModFingerprint(paths.CacheDirectory, new CoreAssetsLog());
        var hashes = await fingerprint.GetHashesAsync(file, gameDirectory);

        Assert.Equal(ModFingerprint.Sha1Hex(payload), hashes.Sha1);
        Assert.Equal(ModFingerprint.CurseForgeFingerprint(payload), hashes.CurseForgeFingerprint);
        Assert.Equal(payload.Length, hashes.SizeBytes);
    }

    /// <summary>缓存目录为 null（启动器路径尚未初始化）时退化成「只算不缓存」，不能崩。</summary>
    [Fact]
    public async Task 未配置缓存目录_只计算不落盘()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-no-cache");
        var payload = Encoding.UTF8.GetBytes("no-cache-payload");
        var file = await CreateModAsync(gameDirectory, "sodium.jar", payload);

        var fingerprint = new ModFingerprint(cacheDirectory: null, new CoreAssetsLog());
        var hashes = await fingerprint.GetHashesAsync(file, gameDirectory);

        Assert.Null(fingerprint.CacheFilePath);
        Assert.Equal(ModFingerprint.Sha1Hex(payload), hashes.Sha1);
    }

    private static async Task<string> CreateModAsync(string gameDirectory, string fileName, byte[] payload)
    {
        var directory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(path, payload);
        return path;
    }
}
