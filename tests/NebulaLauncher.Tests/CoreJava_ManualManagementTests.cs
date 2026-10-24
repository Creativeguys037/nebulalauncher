using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// Java 手动管理：<c>AddManualAsync</c> / <c>RemoveAsync</c> / 排除列表持久化。
///
/// 「探测成功」怎么造：<see cref="JavaService.ProbeAsync"/> 会真的跑
/// <c>java.exe -XshowSettings:properties -version</c>，而测试里能造出来的只有非法 PE 文件（必然启动失败）。
/// 所以成功路径统一走 <c>java-cache.json</c>：<see cref="JavaCache.IsStillValid"/> 只比对
/// 「存在 + 大小 + 修改时间」，预写一条匹配的记录就能让探测「命中」。
/// 这同时钉住了一条设计约束：用户手动添加一个本机已探测过的 Java 时不该重跑子进程。
///
/// 全部用例只用临时目录 + 纯逻辑，不碰任何 Avalonia 类型。
/// </summary>
public sealed class CoreJava_ManualManagementTests
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task 添加有效Java_返回探测结果并置顶且落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-17");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        using var scope = new JavaSearchScope();
        using var service = CreateService(paths);

        var info = await service.AddManualAsync(executable);

        Assert.NotNull(info);
        Assert.Equal(17, info!.MajorVersion);
        Assert.Equal("17.0.9", info.Version);
        Assert.Equal("manual", info.Source);
        Assert.Equal(executable, info.Path);

        var manualFile = Path.Combine(paths.RootDirectory, JavaService.ManualFileName);
        Assert.True(File.Exists(manualFile));
        Assert.Contains("jdk-17", await File.ReadAllTextAsync(manualFile), StringComparison.OrdinalIgnoreCase);

        // 探测结果里它必须在最前：界面标题写的是「已检测到的 Java」，手动项置顶是可见的承诺。
        var detected = await service.DetectAsync();
        Assert.Equal(executable, Assert.Single(detected).Path);
        Assert.Equal("manual", detected[0].Source);
    }

    [Fact]
    public async Task 添加时接受JDK根目录()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-21");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "21.0.2", 21));

        using var scope = new JavaSearchScope();
        using var service = CreateService(paths);

        // 传目录而不是 java.exe：PCL2 的选择器只给文件，但用户也可能把 JDK 目录粘进来。
        var info = await service.AddManualAsync(home);

        Assert.NotNull(info);
        Assert.Equal(executable, info!.Path);
        Assert.Equal(21, info.MajorVersion);
    }

    [Fact]
    public async Task 添加无效路径返回null且不落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        using var scope = new JavaSearchScope();
        using var service = CreateService(paths);

        var missing = await service.AddManualAsync(Path.Combine(temp.Path, "nope", "java.exe"));
        Assert.Null(missing);

        var empty = await service.AddManualAsync(string.Empty);
        Assert.Null(empty);

        // 失败不该留下半条记录：那个 Java 下次打开设置页会变成一条无效的手动项。
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, JavaService.ManualFileName)));
        Assert.Empty(await service.GetExcludedAsync());
    }

    [Fact]
    public async Task 移除后不再出现在探测结果且缓存条目仍保留()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-17");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        using var scope = new JavaSearchScope(home);
        using var service = CreateService(paths);

        Assert.Equal(executable, Assert.Single(await service.DetectAsync()).Path);

        await service.RemoveAsync(executable);

        // 排除项不进候选：再探测一次（缓存命中，很快）结果里就没有它了。
        Assert.Empty(await service.DetectAsync());

        // 但缓存条目必须留着：用户一键恢复后不必为它重跑一次子进程。
        var cache = new JavaCache(paths.CacheDirectory, NullAppLog.Instance);
        Assert.True(cache.Load().ContainsKey(executable));
    }

    [Fact]
    public async Task 排除列表写盘后新实例能读回()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-8");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "1.8.0_402", 8));

        using var scope = new JavaSearchScope();

        using (var first = CreateService(paths))
        {
            await first.RemoveAsync(executable);
            Assert.Equal(new[] { executable }, await first.GetExcludedAsync());
            Assert.True(File.Exists(Path.Combine(paths.RootDirectory, JavaService.ExcludedFileName)));
        }

        // 新实例 = 下次启动进程：只能从盘上读。
        using var reloaded = CreateService(paths);
        Assert.Equal(new[] { executable }, await reloaded.GetExcludedAsync());
    }

    [Fact]
    public async Task 坏json静默重建且后续可写()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-17");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        var excludedFile = Path.Combine(paths.RootDirectory, JavaService.ExcludedFileName);
        await File.WriteAllTextAsync(excludedFile, "this is not json");

        using var scope = new JavaSearchScope();
        using var service = CreateService(paths);

        // 坏文件按空列表处理，不抛、不阻断设置页。
        Assert.Empty(await service.GetExcludedAsync());

        // 立刻重建：用户下次进来不该再撞上同一条错误。
        var rebuilt = await File.ReadAllTextAsync(excludedFile);
        Assert.StartsWith("{", rebuilt.TrimStart(), StringComparison.Ordinal);

        // 重建后的文件仍能正常追加。
        await service.RemoveAsync(executable);
        Assert.Equal(new[] { executable }, await service.GetExcludedAsync());
        var saved = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(excludedFile));
        Assert.Equal(1, saved.GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public async Task 手动添加会撤销此前的排除()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-17");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        using var scope = new JavaSearchScope();
        using var service = CreateService(paths);

        await service.RemoveAsync(executable);
        Assert.Single(await service.GetExcludedAsync());

        var info = await service.AddManualAsync(executable);

        // PCL2 ManuallyImportJava 同款：导入即把路径从 JavaRemovedList 里删掉，
        // 否则用户会得到一个「添加成功但列表里没有」的条目。
        Assert.NotNull(info);
        Assert.Empty(await service.GetExcludedAsync());
        Assert.Equal(executable, Assert.Single(await service.DetectAsync()).Path);
    }

    [Fact]
    public async Task 恢复单个排除项时大小写不敏感()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-17");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        using var scope = new JavaSearchScope();
        using var service = CreateService(paths);

        await service.RemoveAsync(executable);

        // Windows 路径大小写不敏感：换个大小写传进来仍要能撤销。
        Assert.True(await service.RestoreAsync(executable.ToUpperInvariant()));
        Assert.Empty(await service.GetExcludedAsync());

        // 已经不在列表里时返回 false，而不是静默成功。
        Assert.False(await service.RestoreAsync(executable));
    }

    [Fact]
    public async Task 清空排除列表后重新扫描()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var home = temp.Combine("jdk-17");
        var executable = CreateFakeJava(home);
        WriteCache(paths.CacheDirectory, NewEntry(executable, "17.0.9", 17));

        // 让假 java 从扫描源（NEBULA_JAVA_PATHS）就能被枚举到：这样「清空排除后它回到候选」
        // 才是扫描链自己算出来的结果，而不是靠某个缓存条目的施舍。
        using var scope = new JavaSearchScope(home);
        using var service = CreateService(paths);

        await service.RemoveAsync(executable);
        Assert.Single(await service.GetExcludedAsync());

        // 排除生效期：候选来源里有它，但候选列表不包含它。
        Assert.DoesNotContain(executable, service.CollectCandidates(), StringComparer.OrdinalIgnoreCase);

        await service.ClearExcludedAsync();

        Assert.Empty(await service.GetExcludedAsync());

        // 断言落在候选列表而不是 DetectAsync：假 java 不是有效 PE，真探测必然失败，
        // 用「探测结果非空」断言等于把测试绑在不可控的子进程上（这就是原先失败的那一例）。
        Assert.Contains(executable, service.CollectCandidates(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 路径归一化忽略大小写与尾部分隔符()
    {
        using var temp = new TempDirectory();
        using var list = new JavaPathList(temp.Path, "java-paths.json", NullAppLog.Instance);

        var directory = temp.Combine("jdk-17");
        Directory.CreateDirectory(directory);

        await list.AddAsync(directory + Path.DirectorySeparatorChar);

        Assert.True(list.Contains(directory));
        Assert.True(list.Contains(directory.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.Equal(new[] { directory }, list.Paths);

        // 重复添加同一个路径（换了大小写与尾部斜杠）不该产生第二条记录。
        await list.AddAsync(directory.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Assert.Single(list.Paths);
    }

    [Fact]
    public async Task 路径列表坏文件不会作废已有条目()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "java-paths.json");
        await File.WriteAllTextAsync(file, """
        {
          "paths": [
            "C:\\jdk-17",
            "C:\\jdk-17\\",
            "",
            null,
            "C:\\jdk-21"
          ]
        }
        """);

        using var list = new JavaPathList(temp.Path, "java-paths.json", NullAppLog.Instance);

        // 重复项、空串与 null 都要被吃掉：界面上同一个 Java 出现两行是最典型的坏数据表现。
        Assert.Equal(2, list.Paths.Count);
        Assert.True(list.Contains(@"C:\jdk-17"));
        Assert.True(list.Contains(@"C:\jdk-21"));
    }

    // ---------------- 测试基建 ----------------

    /// <summary>造一个内容非法的假 java；返回 <c>&lt;root&gt;/bin/java.exe</c> 的绝对路径。</summary>
    private static string CreateFakeJava(string home)
    {
        var bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);

        // 不是有效 PE 镜像：真去 ProbeAsync 时 Process.Start 会失败并返回 null。
        var payload = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Path.Combine(bin, "java.exe"), payload);
        File.WriteAllBytes(Path.Combine(bin, "java"), payload);

        return Path.Combine(bin, "java.exe");
    }

    private static JavaService CreateService(FakeLauncherPaths paths, ISettingsService? settings = null)
    {
        // 工厂只用来满足 DownloadQueue 的构造；Java 探测路径不碰网络，方法结束前释放即可。
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
        var json = JsonSerializer.Serialize(entries, CacheJsonOptions);

        File.WriteAllText(Path.Combine(cacheDirectory, JavaCache.FileName), json);
    }

    /// <summary>
    /// 把 Java 候选来源收敛到测试自己的目录：清空 PATH / JAVA_HOME，只用 NEBULA_JAVA_PATHS。
    /// 否则本机真实安装的 Java（<c>JAVA_HOME</c> 里的 JDK 21）会被一起探测，
    /// 「移除后探测结果为空」这类断言会随机器而变。
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
}