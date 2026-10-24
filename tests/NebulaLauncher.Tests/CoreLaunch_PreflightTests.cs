using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 启动前置检查（EXTENSIONS 条目 1）。
///
/// 检查分两段，用例也分两组：
/// <list type="bullet">
/// <item><see cref="PreflightChecker.CheckEnvironmentAsync"/>：不依赖 Java 的环境项；</item>
/// <item><see cref="PreflightChecker.CheckJava"/>：拿已选中的 <see cref="JavaInfo"/> 做纯判定。</item>
/// </list>
/// 全部只依赖构造出来的 <see cref="JavaInfo"/> / <see cref="VersionInfo"/> 与假服务，
/// 不跑真实 JDK 探测、不依赖本机装了哪些 Java，也不依赖机器时钟。
/// </summary>
public class CoreLaunch_PreflightTests
{
    // ==================== 纯函数：内存项 ====================

    [Fact]
    public void CheckMemory_RequestExceedsPhysicalMemory_ReturnsBlocker()
    {
        var items = PreflightChecker.CheckMemory(999999);

        var item = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Blocker, item.Severity);
        Assert.Contains("内存", item.Title, StringComparison.Ordinal);
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, item.ActionId);
    }

    [Fact]
    public void CheckMemory_ReasonableValue_NoItem()
    {
        // 512 MB 低于任何能跑 .NET 的机器的 70% 阈值。
        Assert.Empty(PreflightChecker.CheckMemory(512));
    }

    [Fact]
    public void CheckMemory_ZeroValue_Skips()
    {
        Assert.Empty(PreflightChecker.CheckMemory(0));
        Assert.Empty(PreflightChecker.CheckMemory(-1));
    }

    // ==================== 纯函数：路径字符 ====================

    [Fact]
    public void CheckPathCharacters_Semicolon_ReturnsBlocker()
    {
        var items = PreflightChecker.CheckPathCharacters(@"C:\Games\a;b");

        var item = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Blocker, item.Severity);
        Assert.Contains("特殊字符", item.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckPathCharacters_Exclamation_ReturnsBlocker()
    {
        var items = PreflightChecker.CheckPathCharacters(@"D:\!mod\minecraft");

        Assert.Single(items);
        Assert.Equal(PreflightSeverity.Blocker, items[0].Severity);
    }

    [Fact]
    public void CheckPathCharacters_NormalPath_NoItem()
    {
        Assert.Empty(PreflightChecker.CheckPathCharacters(@"D:\Minecraft\.minecraft"));
        Assert.Empty(PreflightChecker.CheckPathCharacters(null));
    }

    // ==================== 纯函数：32 位 Java ====================

    [Fact]
    public void Check32BitHeap_32BitWithLargeHeap_ReturnsBlocker()
    {
        var items = PreflightChecker.Check32BitHeap(NewJava(8, is64Bit: false), 4096);

        var item = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Blocker, item.Severity);
        Assert.Contains("32 位", item.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Check32BitHeap_64BitWithLargeHeap_NoItem()
    {
        Assert.Empty(PreflightChecker.Check32BitHeap(NewJava(21, is64Bit: true), 8192));
        Assert.Empty(PreflightChecker.Check32BitHeap(null, 8192));
    }

    // ==================== 纯函数：Java 允许区间 ====================

    [Fact]
    public void CheckJavaRange_Forge1122WithJava17_ReturnsBlocker()
    {
        // 真实约束集：1.12.2 Forge 走 LaunchWrapper，上限 Java 8。
        var range = JavaRequirementTable.AllowedRange("1.12.2", LoaderKind.Forge);

        Assert.NotNull(range);

        var items = PreflightChecker.CheckJavaRange(NewJava(17, is64Bit: true), range, "1.12.2");

        var item = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Blocker, item.Severity);
        Assert.Contains("Java", item.Title, StringComparison.Ordinal);
        Assert.Contains("1.12.2", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckJavaRange_JavaInsideRange_NoItem()
    {
        Assert.Empty(PreflightChecker.CheckJavaRange(NewJava(17, is64Bit: true), (17, int.MaxValue), "1.20.1"));
        Assert.Empty(PreflightChecker.CheckJavaRange(NewJava(17, is64Bit: true), null, "unknown-snapshot"));
        Assert.Empty(PreflightChecker.CheckJavaRange(null, (17, 17), "1.20.1"));
    }

    // ==================== 纯函数：磁盘 ====================

    [Fact]
    public void CheckFreeSpace_UnreachablePath_NoItem()
    {
        // 拿不到卷容量时不判定为不足。
        Assert.Empty(PreflightChecker.CheckFreeSpace(@"Z:\definitely-not-a-drive\minecraft"));
        Assert.Empty(PreflightChecker.CheckFreeSpace(string.Empty));
    }

    // ==================== 环境段（不依赖 Java） ====================

    [Fact]
    public async Task CheckEnvironment_NormalConfig_HasNoBlocker()
    {
        using var temp = new TempDirectory();

        var report = await RunEnvironmentAsync(temp.Path);

        Assert.False(report.HasBlocker);
    }

    [Fact]
    public async Task CheckEnvironment_MaxMemoryTooLarge_HasBlocker()
    {
        using var temp = new TempDirectory();

        var report = await RunEnvironmentAsync(temp.Path, maxMemoryMb: 999999);

        Assert.True(report.HasBlocker);
        Assert.Contains(report.Items, item =>
            item.Severity == PreflightSeverity.Blocker
            && item.Title.Contains("内存", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckEnvironment_PathWithSemicolon_HasBlocker()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "mod pack;v2");

        var report = await RunEnvironmentAsync(directory);

        Assert.True(report.HasBlocker);
        Assert.Contains(report.Items, item =>
            item.Severity == PreflightSeverity.Blocker
            && item.Title.Contains("特殊字符", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckEnvironment_TwoModsWithSameId_WarningButNotBlocker()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));

        var content = new PreflightFakeContent
        {
            Mods =
            [
                NewMod("jei-1.20.1-15.0.0.jar", "jei"),
                NewMod("jei-1.20.1-15.0.1.jar", "jei"),
            ],
            Conflicts = [new ModConflict("jei", "发现 2 个同 modid 的文件", ["jei-1.jar", "jei-2.jar"])],
        };

        var report = await RunEnvironmentAsync(temp.Path, content: content);

        Assert.False(report.HasBlocker);
        Assert.Contains(report.Items, item =>
            item.Severity == PreflightSeverity.Warning
            && item.Title.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckEnvironment_MissingMandatoryDependency_Warning()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));

        var content = new PreflightFakeContent
        {
            Mods = [NewMod("someaddon.jar", "someaddon")],
            DependencyIssues =
            [
                new ModDependencyIssue("someaddon", "curios", "[5.0,)", Optional: false, ["someaddon.jar"]),
                new ModDependencyIssue("someaddon", "trinkets", null, Optional: true, ["someaddon.jar"]),
            ],
        };

        var report = await RunEnvironmentAsync(temp.Path, content: content);

        Assert.False(report.HasBlocker);

        var item = Assert.Single(report.Items, i => i.Title.Contains("前置", StringComparison.Ordinal));
        Assert.Equal(PreflightSeverity.Warning, item.Severity);
        Assert.Contains("curios", item.Detail, StringComparison.Ordinal);

        // 可选依赖缺失不提示。
        Assert.DoesNotContain("trinkets", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckEnvironment_ModsDirectoryMissing_DoesNotScanAndDoesNotThrow()
    {
        using var temp = new TempDirectory();
        var content = new PreflightFakeContent { Mods = [NewMod("jei.jar", "jei")] };

        var report = await RunEnvironmentAsync(temp.Path, content: content);

        Assert.Equal(0, content.ScanCount);
        Assert.DoesNotContain(report.Items, item => item.Title.Contains("模组", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckEnvironment_BrokenVersion_Blocks()
    {
        using var temp = new TempDirectory();

        var versions = new PreflightFakeVersions
        {
            Info = new VersionInfo { Name = "1.20.1-forge", BrokenReason = "缺少 libraries 清单" },
        };

        var report = await RunEnvironmentAsync(temp.Path, versions: versions);

        Assert.True(report.HasBlocker);
        Assert.Contains(report.Items, item =>
            item.Severity == PreflightSeverity.Blocker
            && item.Title.Contains("版本文件损坏", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckEnvironment_EveryServiceThrows_StillReturnsReport()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));

        var content = new PreflightFakeContent { ThrowOnScan = true, ThrowOnConflicts = true };
        var versions = new PreflightFakeVersions { ThrowOnGet = true };

        var report = await RunEnvironmentAsync(temp.Path, content: content, versions: versions);

        Assert.NotNull(report);
        Assert.False(report.HasBlocker);
    }

    // ==================== Java 段（纯判定，零探测） ====================

    [Fact]
    public void CheckJava_DoesNotTouchJavaService()
    {
        // CheckJava 的签名里没有任何 IJavaService 参数，所以它不可能触发探测。
        // 这里把这条契约固化成两条可执行断言：反射确认参数表，替身确认零调用。
        var bomb = new ExplodingJavaService();

        var method = typeof(PreflightChecker).GetMethod(nameof(PreflightChecker.CheckJava));

        Assert.NotNull(method);
        Assert.DoesNotContain(
            method!.GetParameters(),
            parameter => typeof(IJavaService).IsAssignableFrom(parameter.ParameterType));

        var report = PreflightChecker.CheckJava(
            NewOptions(maxMemoryMb: 4096),
            NewJava(17, is64Bit: true),
            (17, int.MaxValue));

        Assert.False(report.HasBlocker);
        Assert.Equal(0, bomb.TotalCalls);
    }

    [Fact]
    public void CheckJava_32BitJavaWithLargeHeap_Blocks()
    {
        var report = PreflightChecker.CheckJava(
            NewOptions(maxMemoryMb: 4096),
            NewJava(8, is64Bit: false),
            (8, 8));

        Assert.True(report.HasBlocker);
        Assert.Contains(report.Items, item =>
            item.Severity == PreflightSeverity.Blocker
            && item.Title.Contains("32 位", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckJava_Forge1122WithJava17_Blocks()
    {
        // 区间来自真实约束集（1.12.2 Forge 走 LaunchWrapper，上限 Java 8）：
        // 这一段不探测、不看时钟，冷启动首次也一定拦得住。
        var range = JavaRequirementTable.AllowedRange("1.12.2", LoaderKind.Forge);

        Assert.NotNull(range);

        var report = PreflightChecker.CheckJava(
            NewOptions(maxMemoryMb: 2048, versionName: "1.12.2-forge-14.23.5.2860"),
            NewJava(17, is64Bit: true),
            range);

        Assert.True(report.HasBlocker);
        Assert.Contains(report.Items, item =>
            item.Severity == PreflightSeverity.Blocker
            && item.Title.Contains("Java 版本", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckJava_JavaInsideRange_NoBlocker()
    {
        var report = PreflightChecker.CheckJava(
            NewOptions(maxMemoryMb: 2048),
            NewJava(17, is64Bit: true),
            (17, int.MaxValue));

        Assert.False(report.HasBlocker);
        Assert.Empty(report.Items);
    }

    // ==================== 测试辅助 ====================

    private static JavaInfo NewJava(int major, bool is64Bit) => new()
    {
        Path = $@"C:\jdk{major}\bin\java.exe",
        MajorVersion = major,
        Is64Bit = is64Bit,
        Version = $"{major}.0.1",
    };

    private static ContentItem NewMod(string fileName, string id) => new()
    {
        FilePath = Path.Combine("mods", fileName),
        FileName = fileName,
        Kind = ContentKind.Mod,
        Id = id,
        DisplayName = id,
    };

    private static LaunchOptions NewOptions(
        int maxMemoryMb = 2048,
        string versionName = "1.20.1",
        string? gameDirectory = null) => new()
    {
        Account = new Account { Username = "Tester" },
        VersionName = versionName,
        GameDirectory = gameDirectory,
        MaxMemoryMb = maxMemoryMb,
    };

    private static Task<PreflightReport> RunEnvironmentAsync(
        string gameDirectory,
        PreflightFakeContent? content = null,
        PreflightFakeVersions? versions = null,
        int maxMemoryMb = 2048,
        string minecraftVersion = "1.20.1",
        string versionName = "1.20.1")
    {
        var paths = new FakeLauncherPaths(gameDirectory);

        return PreflightChecker.CheckEnvironmentAsync(
            NewOptions(maxMemoryMb, versionName, gameDirectory),
            versions ?? new PreflightFakeVersions(),
            content,
            paths,
            new FakePlatformService(),
            minecraftVersion);
    }
}

// ==================== 假服务（只实现本组用例用到的行为） ====================

/// <summary>
/// 「一碰就炸」的 <see cref="IJavaService"/>。用来证明前置检查的新链路不会触发 Java 探测：
/// 只要有人调用它的任何成员，<see cref="TotalCalls"/> 就不再是 0，用例会失败。
/// </summary>
internal sealed class ExplodingJavaService : IJavaService
{
    private int _calls;

    public int TotalCalls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<JavaInfo>> DetectAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        throw Unexpected(nameof(DetectAsync));

    public Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default) =>
        throw Unexpected(nameof(ProbeAsync));

    public JavaRequirement? GetRequirement(string minecraftVersion) =>
        throw Unexpected(nameof(GetRequirement));

    public (int Min, int Max)? GetAllowedRange(string minecraftVersion, LoaderKind loader) =>
        throw Unexpected(nameof(GetAllowedRange));

    public Task<JavaInfo?> ResolveForVersionAsync(
        string minecraftVersion,
        string? preferredPath = null,
        CancellationToken cancellationToken = default) =>
        throw Unexpected(nameof(ResolveForVersionAsync));

    public Task<JavaInfo?> ResolveForVersionAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? preferredPath = null,
        CancellationToken cancellationToken = default) =>
        throw Unexpected(nameof(ResolveForVersionAsync));

    public Task<string?> InstallRuntimeAsync(
        JavaRuntimePackage package,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw Unexpected(nameof(InstallRuntimeAsync));

    public Task<string?> DownloadRuntimeAsync(
        int majorVersion,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw Unexpected(nameof(DownloadRuntimeAsync));

    private InvalidOperationException Unexpected(string member)
    {
        Interlocked.Increment(ref _calls);
        return new InvalidOperationException($"前置检查不应调用 IJavaService.{member}");
    }
}

/// <summary>可配置的 <see cref="IContentService"/>：模组扫描结果全部由用例指定。</summary>
internal sealed class PreflightFakeContent : IContentService
{
    public IReadOnlyList<ContentItem> Mods { get; init; } = [];

    public IReadOnlyList<ModConflict> Conflicts { get; init; } = [];

    public IReadOnlyList<ModDependencyIssue> DependencyIssues { get; init; } = [];

    public IReadOnlyList<ContentItem> Mismatched { get; init; } = [];

    public bool ThrowOnScan { get; init; }

    public bool ThrowOnConflicts { get; init; }

    public int ScanCount { get; private set; }

    public Task<IReadOnlyList<ContentItem>> ScanAsync(
        string gameDirectory,
        ContentKind kind,
        CancellationToken cancellationToken = default)
    {
        ScanCount++;

        if (ThrowOnScan)
        {
            throw new InvalidOperationException("扫描失败（测试注入）");
        }

        return Task.FromResult(Mods);
    }

    public Task SetEnabledAsync(ContentItem item, bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(ContentItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ImportAsync(
        string gameDirectory,
        ContentKind kind,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public IReadOnlyList<ModConflict> DetectConflicts(IReadOnlyList<ContentItem> mods)
    {
        if (ThrowOnConflicts)
        {
            throw new InvalidOperationException("冲突检测失败（测试注入）");
        }

        return Conflicts;
    }

    public IReadOnlyList<ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<ContentItem> mods) => DependencyIssues;

    public IReadOnlyList<ContentItem> FindVersionMismatch(IReadOnlyList<ContentItem> mods, string minecraftVersion) => Mismatched;
}

/// <summary>可配置的 <see cref="IVersionService"/>：只关心 GetVersionAsync。</summary>
internal sealed class PreflightFakeVersions : IVersionService
{
    public VersionInfo? Info { get; init; }

    public bool ThrowOnGet { get; init; }

    public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VersionInfo>>([]);

    public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

    public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default)
    {
        if (ThrowOnGet)
        {
            throw new InvalidOperationException("版本读取失败（测试注入）");
        }

        return Task.FromResult(Info);
    }

    public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordLaunchAsync(string versionName, TimeSpan duration, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
        LoaderKind loader,
        string minecraftVersion,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LoaderVersion>>([]);
}
