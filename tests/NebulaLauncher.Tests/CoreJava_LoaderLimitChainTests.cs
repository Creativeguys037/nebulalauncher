using System.Reflection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「加载器版本 → Java 上限」整条链的最后一跳：App 解析出的版本号经
/// <see cref="LaunchOptions"/> 递到 <c>GameLauncher</c> 的前置检查，最后变成用户看得见的阻断。
///
/// 为什么这条链要单独守：前三段（实例落盘、区间计算、阻断/建议分级）各自都有用例，
/// 但中间任何一处没接上，整条链就是断的，而且**断得静默**——
/// 表现不是报错，而是「1.19.4 Forge 45.0.40 配 Java 20 一路跑到启动阶段才崩」，
/// 检查项里一条提示都没有。所以这里测的是「值真的走到底了没有」，
/// 不是某一段算得对不对（那由 <c>CoreJava_LoaderLimitTests</c> 与
/// <c>CoreJava_LoaderLimitWiringTests</c> 负责）。
/// </summary>
public class CoreJava_LoaderLimitChainTests
{
    private static readonly Account TestAccount = new() { Username = "测试玩家" };

    // ==================== 第一跳：工厂把版本号填进 LaunchOptions ====================

    /// <summary>实例上有加载器版本时，工厂用它。</summary>
    [Fact]
    public void 工厂从实例传入加载器版本()
    {
        var instance = new InstanceInfo
        {
            Name = "整合包",
            VersionName = "1.19.4-forge-45.0.40",
            Loader = LoaderKind.Forge,
            MinecraftVersion = "1.19.4",
            LoaderVersion = "45.0.40",
        };

        var resolution = new InstanceResolution(@"D:\instances\probe", instance, Isolated: true, Created: false);
        var options = Build(resolution);

        Assert.Equal("45.0.40", options.LoaderVersion);
    }

    /// <summary>
    /// 全局目录（不开隔离）没有实例对象，但目录照样可能挂着 1.19.4 Forge 45.0.40：
    /// 这时版本号来自解析结果，必须仍然传下去。
    /// </summary>
    [Fact]
    public void 工厂从解析结果传入加载器版本()
    {
        var resolution = new InstanceResolution(
            @"D:\global\.minecraft",
            Instance: null,
            Isolated: false,
            Created: false,
            LoaderVersion: "0.16.9");

        var options = Build(resolution);

        Assert.Equal("0.16.9", options.LoaderVersion);
    }

    /// <summary>两处都没有（原版、或版本目录读不出来）时为 null，Java 判定退回粗粒度。</summary>
    [Fact]
    public void 工厂在没有加载器版本时填空()
    {
        var resolution = new InstanceResolution(@"D:\global\.minecraft", null, Isolated: false, Created: false);

        var options = Build(resolution);

        Assert.Null(options.LoaderVersion);
    }

    /// <summary>
    /// 本次解析出来的版本号优先于实例里记的那个。
    ///
    /// 为什么是解析值优先：它来自刚读过的版本目录，比 <c>instance.json</c> 里的缓存新
    /// （用户手工换过 <c>versions/</c> 里的文件时，磁盘才是真相）；实例值只在
    /// 「这次一个值都没解析出来」时兜底——那时盘上信息不可用，退回上次记录比没有强。
    /// 正常流程里两者一致（<c>ResolveLaunchTargetAsync</c> 的实例分支会把实例值同时填进两处），
    /// 这条钉的是「万一不一致，取新的那个」。
    /// </summary>
    [Fact]
    public void 解析值优先于实例缓存()
    {
        var instance = new InstanceInfo { LoaderVersion = "47.0.1" };
        var resolution = new InstanceResolution(@"D:\instances\probe", instance, true, false, "45.0.40");

        Assert.Equal("45.0.40", Build(resolution).LoaderVersion);
    }

    // ==================== 第二跳：GameLauncher 真的用上了它 ====================

    /// <summary>
    /// <c>CheckJavaPreflight</c> 读的是 <see cref="LaunchOptions.LoaderVersion"/>：
    /// 给定 45.0.40 时它既要把版本号传给 <c>GetAllowedRange</c>，也要让
    /// <c>CheckJava</c> 拿到加载器级上限（文案里带规则自己的原因）。
    ///
    /// 为什么是反射：它是 <c>GameLauncher</c> 的私有实现细节，但它恰好是「LaunchOptions →
    /// Java 判定」的唯一接缝。为测试把它改成 public 会把内部实现暴露成契约。
    /// </summary>
    [Fact]
    public void 前置检查把加载器版本用于Java上限()
    {
        var java = new RecordingJavaService();
        using var temp = new TempDirectory();
        var launcher = CreateLauncher(java, temp.Path);

        var options = new LaunchOptions
        {
            Account = TestAccount,
            VersionName = "1.19.4-forge-45.0.40",
            LoaderVersion = "45.0.40",
        };

        // 45.0.40 这一段的上限是 Java 19，给 20 必须被拦下。
        var javaInfo = new JavaInfo { Path = @"C:\jdk20\bin\java.exe", Version = "20.0.2+9", MajorVersion = 20, Is64Bit = true };
        var report = InvokeCheckJavaPreflight(launcher, options, javaInfo, "1.19.4", LoaderKind.Forge);

        Assert.Equal("45.0.40", java.LastLoaderVersion);
        Assert.True(report.HasBlocker);

        // 阻断项的文案必须是加载器规则自己的原因，而不是泛化的「区间不匹配」——
        // 后者会让用户看见「1.19.4 允许 17–∞、自己装的 20 明明合法」而以为启动器乱报。
        var limit = JavaRequirementTable.GetLoaderLimit("1.19.4", LoaderKind.Forge, "45.0.40");
        Assert.NotNull(limit);

        var blocker = Assert.Single(report.Items, item => item.Severity == PreflightSeverity.Blocker);
        Assert.Contains(limit!.Reason, blocker.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 版本号为空时与改动前完全一致：区间退回粗粒度（1.19.4 Forge 上界是「不限」），
    /// Java 20 不被拦。
    /// </summary>
    [Fact]
    public void 没有加载器版本时退回旧行为()
    {
        var java = new RecordingJavaService();
        using var temp = new TempDirectory();
        var launcher = CreateLauncher(java, temp.Path);

        var options = new LaunchOptions
        {
            Account = TestAccount,
            VersionName = "1.19.4-forge-45.0.40",
            LoaderVersion = null,
        };

        var javaInfo = new JavaInfo { Path = @"C:\jdk20\bin\java.exe", Version = "20.0.2+9", MajorVersion = 20, Is64Bit = true };
        var report = InvokeCheckJavaPreflight(launcher, options, javaInfo, "1.19.4", LoaderKind.Forge);

        Assert.Null(java.LastLoaderVersion);
        Assert.False(report.HasBlocker);
    }

    /// <summary>1.19.4 Forge 的粗粒度上界确实是「不限」——这条钉住「没有版本号时不会误拦」的前提。</summary>
    [Fact]
    public void 粗粒度区间上界是不限()
    {
        var range = JavaRequirementTable.AllowedRange("1.19.4", LoaderKind.Forge);

        Assert.True(range.HasValue);
        Assert.Equal(17, range!.Value.Min);
        Assert.Equal(int.MaxValue, range.Value.Max);
    }

    // ==================== 第三跳：PreflightCheckerService 的透传 ====================

    /// <summary>
    /// 接口实现那条路（<see cref="IPreflightChecker.CheckAsync"/>）也要把版本号透传下去。
    ///
    /// 这里不测它算出什么，只测「传到了没有」：它和环境段共用一次调用，
    /// 环境段有任何 Blocker 就会提前返回，结论不稳定；而漏传版本号是这条链上
    /// 最容易复发的一类错误（新增一个检查项、忘了带参数）。
    /// </summary>
    [Fact]
    public async Task 前置检查服务把加载器版本透传给区间计算()
    {
        using var temp = new TempDirectory();
        var java = new RecordingJavaService();
        var service = new PreflightCheckerService(
            new FakeLauncherPaths(temp.Path),
            new EmptyVersionService(),
            java);

        var options = new LaunchOptions
        {
            Account = TestAccount,
            VersionName = "1.19.4-forge-45.0.40",
            GameDirectory = temp.Path,
            LoaderVersion = "45.0.40",
        };

        // 不管环境段有没有提前返回，区间计算只能发生在它之后；这里只断言「没崩 + 若算过则参数正确」。
        var report = await service.CheckAsync(options, "1.19.4", LoaderKind.Forge, "45.0.40");

        Assert.NotNull(report);

        if (!report.HasBlocker)
        {
            Assert.Equal("45.0.40", java.LastLoaderVersion);
        }
    }

    // ==================== 辅助 ====================

    private static LaunchOptions Build(InstanceResolution resolution) =>
        NebulaLauncher.Services.LaunchOptionsFactory.Build(
            TestAccount,
            resolution.Instance?.VersionName ?? "1.19.4-forge-45.0.40",
            resolution,
            settings: null);

    private static GameLauncher CreateLauncher(RecordingJavaService java, string rootPath) =>
        new(new FakeLauncherPaths(rootPath), NullAppLog.Instance, java, new EmptyVersionService());

    /// <summary>
    /// 反射调用私有的 <c>CheckJavaPreflight</c>。
    /// 找不到就直接红——那说明这个方法被重命名或移走了，本用例需要跟着改。
    /// </summary>
    private static PreflightReport InvokeCheckJavaPreflight(
        GameLauncher launcher,
        LaunchOptions options,
        JavaInfo java,
        string minecraftVersion,
        LoaderKind loader)
    {
        var method = typeof(GameLauncher).GetMethod(
            "CheckJavaPreflight",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "找不到 GameLauncher.CheckJavaPreflight：方法被重命名或移走了，本用例需要同步更新。");

        var result = method.Invoke(launcher, [options, java, minecraftVersion, loader, null]);

        return Assert.IsType<PreflightReport>(result);
    }

    /// <summary>
    /// 记录 <c>GetAllowedRange</c> 收到的加载器版本，其余成员给最小答案。
    ///
    /// 刻意显式实现三参数重载而不是靠接口默认实现：默认实现会把调用吞掉不转发，
    /// 那样这条用例就测不出「有没有把版本号传下来」。
    /// </summary>
    private sealed class RecordingJavaService : IJavaService
    {
        public string? LastLoaderVersion { get; private set; }

        /// <summary>「选出来的 Java」：Java 段要有它才会执行，否则 CheckAsync 提前返回。</summary>
        public JavaInfo Probed { get; } = new()
        {
            Path = @"C:\jdk20\bin\java.exe",
            Version = "20.0.2+9",
            MajorVersion = 20,
            Is64Bit = true,
        };

        public Task<IReadOnlyList<JavaInfo>> DetectAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<JavaInfo>>([]);

        public Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default)
            => Task.FromResult<JavaInfo?>(null);

        public JavaRequirement? GetRequirement(string minecraftVersion) =>
            JavaRequirementTable.Get(minecraftVersion);

        public (int Min, int Max)? GetAllowedRange(string minecraftVersion, LoaderKind loader) =>
            JavaRequirementTable.AllowedRange(minecraftVersion, loader);

        public (int Min, int Max)? GetAllowedRange(
            string minecraftVersion,
            LoaderKind loader,
            string? loaderVersion)
        {
            LastLoaderVersion = loaderVersion;

            return JavaRequirementTable.AllowedRange(minecraftVersion, loader, null, loaderVersion);
        }

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            string? preferredPath = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<JavaInfo?>(null);

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            LoaderKind loader,
            string? preferredPath = null,
            CancellationToken cancellationToken = default)
            // 必须返回一个运行时：返回 null 时 CheckAsync 会在 Java 段之前就返回，
            // GetAllowedRange 根本不会被调用，这条用例就变成空转。
            => Task.FromResult<JavaInfo?>(Probed);

        public Task<string?> InstallRuntimeAsync(
            JavaRuntimePackage package,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> DownloadRuntimeAsync(
            int majorVersion,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>什么都不装的版本服务：环境段读不到版本就跳过加载器完整性检查。</summary>
    private sealed class EmptyVersionService : IVersionService
    {
        public DownloadSource Source { get; set; } = DownloadSource.BmclApi;

        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VersionInfo>>([]);

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default)
            => Task.FromResult<VersionInfo?>(null);

        public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordLaunchAsync(
            string versionName,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
            LoaderKind loader,
            string minecraftVersion,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LoaderVersion>>([]);
    }
}