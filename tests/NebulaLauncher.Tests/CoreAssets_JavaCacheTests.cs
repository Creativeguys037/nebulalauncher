using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// Java 探测缓存（<see cref="JavaCache"/>）与 <see cref="JavaService.DetectAsync"/> 的缓存衔接。
///
/// 「缓存命中时没有启动子进程」的判据：假 java 是内容为几个字节的非法 PE 文件，
/// 一旦真的跑 <c>ProbeAsync</c>，<c>Process.Start</c> 必然失败、返回 null。
/// 所以「命中缓存后还能拿到 JavaInfo」在逻辑上只能来自缓存，不需要再额外挂计数器。
/// </summary>
public class CoreAssets_JavaCacheTests
{
    private static readonly JsonSerializerOptions TestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public void 缓存记录保存后可读回()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var executable = CreateFakeJava(temp.Path, "jdk-21");

        var info = new JavaInfo
        {
            Path = executable,
            Version = "21.0.2",
            MajorVersion = 21,
            Is64Bit = true,
            Vendor = "Eclipse Adoptium",
        };

        var entry = JavaCache.Capture(info);

        Assert.True(JavaCache.IsStillValid(entry));
        Assert.Equal(new FileInfo(executable).Length, entry.PathSize);
        Assert.Equal(File.GetLastWriteTimeUtc(executable).Ticks, entry.PathLastWriteTicks);

        var cache = new JavaCache(paths.CacheDirectory, NullAppLog.Instance);
        cache.Save([entry]);

        var loaded = cache.Load();
        Assert.True(loaded.TryGetValue(executable, out var roundTrip));
        Assert.Equal("21.0.2", roundTrip!.Version);
        Assert.Equal(21, roundTrip.MajorVersion);
        Assert.True(roundTrip.Is64Bit);
        Assert.Equal("Eclipse Adoptium", roundTrip.Vendor);
    }

    [Fact]
    public void 可执行文件不存在或大小变化时缓存项不可信()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var executable = CreateFakeJava(temp.Path, "jdk-17");

        var entry = JavaCache.Capture(new JavaInfo
        {
            Path = executable,
            Version = "17.0.8",
            MajorVersion = 17,
        });

        Assert.True(JavaCache.IsStillValid(entry));

        // 大小变了（换了一个文件）。
        File.AppendAllText(executable, "extra");
        Assert.False(JavaCache.IsStillValid(entry));

        // 文件没了。
        File.Delete(executable);
        Assert.False(JavaCache.IsStillValid(entry));

        Assert.False(JavaCache.IsStillValid(null));
    }

    [Fact]
    public void 缓存文件损坏时返回空表且不抛()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        Directory.CreateDirectory(paths.CacheDirectory);
        File.WriteAllText(Path.Combine(paths.CacheDirectory, JavaCache.FileName), "not json");

        var cache = new JavaCache(paths.CacheDirectory, NullAppLog.Instance);

        Assert.Empty(cache.Load());
    }

    [Fact]
    public async Task 缓存命中时不再启动子进程()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = Path.Combine(temp.Path, "jdk-17");
        var executable = CreateFakeJava(temp.Path, "jdk-17");
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        using var scope = new JavaSearchScope(home);
        using var service = CreateService(paths);

        var detected = await service.DetectAsync();

        var info = Assert.Single(detected);
        Assert.Equal(executable, info.Path);
        Assert.Equal("17.0.9", info.Version);
        Assert.Equal(17, info.MajorVersion);
        Assert.Equal("cache", info.Source);
    }

    [Fact]
    public async Task 可执行文件被改动后缓存失效并重新探测()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = Path.Combine(temp.Path, "jdk-17");
        var executable = CreateFakeJava(temp.Path, "jdk-17");
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        using var scope = new JavaSearchScope(home);
        using (var first = CreateService(paths))
        {
            Assert.Single(await first.DetectAsync());
        }

        // 只改修改时间、不动内容：size 不变，判据必须能单独靠 LastWriteTimeUtc 拦住。
        File.SetLastWriteTimeUtc(executable, File.GetLastWriteTimeUtc(executable).AddMinutes(5));

        using var second = CreateService(paths);
        var again = await second.DetectAsync();

        // 假 java 不是有效可执行文件，重探必然失败 —— 结果为空即证明走了探测路径。
        Assert.Empty(again);

        var rebuilt = File.ReadAllText(Path.Combine(paths.CacheDirectory, JavaCache.FileName));
        Assert.DoesNotContain("jdk-17", rebuilt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 缓存文件损坏时静默重建()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = Path.Combine(temp.Path, "jdk-17");
        CreateFakeJava(temp.Path, "jdk-17");

        Directory.CreateDirectory(paths.CacheDirectory);
        File.WriteAllText(Path.Combine(paths.CacheDirectory, JavaCache.FileName), "not json");

        using var scope = new JavaSearchScope(home);
        using var service = CreateService(paths);

        var detected = await service.DetectAsync();

        Assert.Empty(detected);

        // 损坏文件已被合法 JSON 覆盖（探测失败时写空表）。
        var rebuilt = File.ReadAllText(Path.Combine(paths.CacheDirectory, JavaCache.FileName));
        Assert.StartsWith("[", rebuilt.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 关闭缓存时不写缓存文件()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = Path.Combine(temp.Path, "jdk-17");
        CreateFakeJava(temp.Path, "jdk-17");

        var settings = new FakeSettingsService();
        settings.Current.EnableJavaCache = false;

        using var scope = new JavaSearchScope(home);
        using var service = CreateService(paths, settings);

        var detected = await service.DetectAsync();

        Assert.Empty(detected);
        Assert.False(File.Exists(Path.Combine(paths.CacheDirectory, JavaCache.FileName)));
    }

    [Fact]
    public async Task 关闭缓存时不采信已有缓存()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = Path.Combine(temp.Path, "jdk-17");
        var executable = CreateFakeJava(temp.Path, "jdk-17");
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        var settings = new FakeSettingsService();
        settings.Current.EnableJavaCache = false;

        using var scope = new JavaSearchScope(home);
        using var service = CreateService(paths, settings);

        var detected = await service.DetectAsync();

        Assert.Empty(detected);
    }

    [Fact]
    public async Task 命中与重探混合时只返回可信结果()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var cachedHome = Path.Combine(temp.Path, "jdk-21");
        var freshHome = Path.Combine(temp.Path, "jdk-8");
        var cachedExecutable = CreateFakeJava(temp.Path, "jdk-21");
        CreateFakeJava(temp.Path, "jdk-8");

        WriteCache(paths.CacheDirectory, NewEntry(cachedExecutable, "21.0.2", 21));

        using var scope = new JavaSearchScope(cachedHome, freshHome);
        using var service = CreateService(paths);

        var detected = await service.DetectAsync();

        var info = Assert.Single(detected);
        Assert.Equal(cachedExecutable, info.Path);
        Assert.Equal(21, info.MajorVersion);
    }

    /// <summary>造一个内容非法的假 java；返回可执行文件绝对路径。</summary>
    private static string CreateFakeJava(string root, string name)
    {
        var bin = Path.Combine(root, name, "bin");
        Directory.CreateDirectory(bin);

        // 不是有效 PE 镜像：真去 ProbeAsync 时 Process.Start 会失败并返回 null。
        var payload = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Path.Combine(bin, "java.exe"), payload);
        File.WriteAllBytes(Path.Combine(bin, "java"), payload);

        return Path.Combine(bin, "java.exe");
    }

    private static JavaService CreateService(FakeLauncherPaths paths, ISettingsService? settings = null)
    {
        // 工厂只用来满足 DownloadQueue 的构造；Java 探测路径不碰网络，测试结束前释放即可。
        using var factory = new DownloadHttpClientFactory(DownloadSource.BmclApi);
        return new JavaService(
            new FakePlatformService(),
            paths,
            new DownloadQueue(factory, NullAppLog.Instance),
            NullAppLog.Instance,
            settings);
    }

    private static JavaCacheEntry NewEntry(string executable, string version, int majorVersion)
    {
        var file = new FileInfo(executable);
        return new JavaCacheEntry
        {
            Path = executable,
            MajorVersion = majorVersion,
            Version = version,
            Is64Bit = true,
            Vendor = "TestVendor",
            PathSize = file.Length,
            PathLastWriteTicks = file.LastWriteTimeUtc.Ticks,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void WriteCache(string cacheDirectory, params JavaCacheEntry[] entries)
    {
        Directory.CreateDirectory(cacheDirectory);
        var json = JsonSerializer.Serialize(entries, TestJsonOptions);

        File.WriteAllText(Path.Combine(cacheDirectory, JavaCache.FileName), json);
    }

    /// <summary>
    /// 把 Java 候选来源收敛到测试目录：清空 PATH / JAVA_HOME，只用 NEBULA_JAVA_PATHS。
    ///
    /// 不这样做的话，本机真实安装的 Java 会被一起探测：用例既随机器变慢，
    /// 结果也不再是「只有我们造的那一个候选」。
    /// </summary>
    private sealed class JavaSearchScope : IDisposable
    {
        private readonly string? _path;
        private readonly string? _javaHome;
        private readonly string? _jdkHome;
        private readonly string? _nebulaJavaPaths;

        public JavaSearchScope(params string[] roots)
        {
            _path = Environment.GetEnvironmentVariable("PATH");
            _javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            _jdkHome = Environment.GetEnvironmentVariable("JDK_HOME");
            _nebulaJavaPaths = Environment.GetEnvironmentVariable("NEBULA_JAVA_PATHS");

            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("JAVA_HOME", null);
            Environment.SetEnvironmentVariable("JDK_HOME", null);
            Environment.SetEnvironmentVariable("NEBULA_JAVA_PATHS", string.Join(Path.PathSeparator, roots));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _path);
            Environment.SetEnvironmentVariable("JAVA_HOME", _javaHome);
            Environment.SetEnvironmentVariable("JDK_HOME", _jdkHome);
            Environment.SetEnvironmentVariable("NEBULA_JAVA_PATHS", _nebulaJavaPaths);
        }
    }

    /// <summary>只要一个可写的 <see cref="AppSettings"/>，不落盘。</summary>
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
