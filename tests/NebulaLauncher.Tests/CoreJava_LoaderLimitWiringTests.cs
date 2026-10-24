using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「加载器小版本 → Java 上限」这条链路的接线。
///
/// <c>JavaRequirementTable</c> 那一层（规则表与区间计算）已由 <c>CoreJava_LoaderLimitTests</c> 覆盖，
/// 这里只管三件事能不能真的串起来：
/// <b>版本号从哪来 → 怎么传到判定点 → 判定结果怎么变成用户看得见的阻断/提示</b>。
///
/// 这三段以前各自都对，但中间断了两根线：实例根本没存加载器版本，
/// 启动前的 Java 检查也只认「游戏版本 + 加载器种类」。断线时的表现是静默的——
/// 1.19.4 Forge 45.0.40 配 Java 20 会一路跑到启动阶段才崩，而不是在检查里被点出来。
/// </summary>
public class CoreJava_LoaderLimitWiringTests
{
    // ==================== 第一段：加载器版本落盘 ====================

    /// <summary>
    /// <c>instance.json</c> 往返：写进去的加载器版本必须能从磁盘读回来。
    ///
    /// 这条看着像废话，但它是 `Clone` 漏字段那类 bug 的第一道网——
    /// 对外暴露的实例对象全部来自 <c>Clone</c>，漏一行就是「保存一次就丢」。
    /// </summary>
    [Fact]
    public async Task 加载器版本能落盘并读回()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        var created = await service.CreateAsync("测试实例", "1.19.4-forge-45.0.40");
        created.Loader = LoaderKind.Forge;
        created.MinecraftVersion = "1.19.4";
        created.LoaderVersion = "45.0.40";
        await service.SaveAsync(created);

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        var saved = Assert.Single(reloaded.Instances);
        Assert.Equal("45.0.40", saved.LoaderVersion);
        Assert.Equal(LoaderKind.Forge, saved.Loader);

        // 再存一次不能把已有值抹掉——这就是 Clone 漏字段时的典型表现。
        saved.Name = "改个名字";
        await reloaded.SaveAsync(saved);

        var again = new InstanceService(paths, NullAppLog.Instance);
        await again.LoadAsync();
        Assert.Equal("45.0.40", Assert.Single(again.Instances).LoaderVersion);
    }

    /// <summary>旧 <c>instance.json</c> 没有这个字段时必须读成 null，而不是空串或异常。</summary>
    [Fact]
    public async Task 缺少加载器版本的旧实例读成null()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 实例布局是 versions/<版本名>/instance.json（目录本身就是游戏目录）。
        var directory = Path.Combine(paths.VersionsDirectory, "1.16.5-forge-36.2.25");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "instance.json"),
            """
            {
              "Id": "legacy",
              "Name": "老实例",
              "VersionName": "1.16.5-forge-36.2.25",
              "MinecraftVersion": "1.16.5"
            }
            """);

        var service = new InstanceService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        var legacy = Assert.Single(service.Instances);
        Assert.Null(legacy.LoaderVersion);
    }

    /// <summary>换加载器成功后，实例上的加载器版本要跟着换掉（否则 Java 上限还按旧 build 算）。</summary>
    [Fact]
    public async Task 换加载器写入加载器版本()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new StubInstallService { ResultVersionName = "1.19.4-forge-45.0.66" };
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var created = await service.CreateAsync("测试实例", "1.19.4");
        created.Loader = LoaderKind.Vanilla;
        created.MinecraftVersion = "1.19.4";
        await service.SaveAsync(created);

        var result = await service.SwitchLoaderAsync(created.Id, LoaderKind.Forge, "45.0.66");
        Assert.True(result.Success, result.Error);

        // 内存里的实例已经更新，且这个值经得住一次磁盘往返。
        Assert.Equal("45.0.66", service.Instances[0].LoaderVersion);

        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();
        Assert.Equal("45.0.66", Assert.Single(reloaded.Instances).LoaderVersion);
    }

    /// <summary>
    /// 装「最新版」（不指定版本号）时，版本号从刚装好的版本目录读回来，
    /// 而不是从安装器返回的目录名里猜——<c>1.20.1-forge-47.2.0</c> 与
    /// NeoForge 的 <c>1.20.1-neoforge-47.1.106</c> 编号规则不同，猜字符串会错。
    /// </summary>
    [Fact]
    public async Task 装最新版时从版本目录回读加载器版本()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new StubInstallService { ResultVersionName = "1.20.1-neoforge-47.1.106" };
        var versions = new StubVersionService
        {
            // 换加载器前实例指向的原版目录，与装完之后的新目录是两个不同的版本，
            // 必须按名字分开回答——否则「已经是目标加载器」的早退分支会被误触发。
            ByName =
            {
                ["1.20.1"] = new VersionInfo
                {
                    Name = "1.20.1",
                    MinecraftVersion = "1.20.1",
                    Loader = LoaderKind.Vanilla,
                },
                ["1.20.1-neoforge-47.1.106"] = new VersionInfo
                {
                    Name = "1.20.1-neoforge-47.1.106",
                    MinecraftVersion = "1.20.1",
                    Loader = LoaderKind.NeoForge,
                    LoaderVersion = "47.1.106",
                },
            },
        };

        var service = new InstanceService(paths, NullAppLog.Instance, versions, install);

        var created = await service.CreateAsync("测试实例", "1.20.1");
        created.Loader = LoaderKind.Vanilla;
        created.MinecraftVersion = "1.20.1";
        await service.SaveAsync(created);

        var result = await service.SwitchLoaderAsync(created.Id, LoaderKind.NeoForge);
        Assert.True(result.Success, result.Error);
        Assert.Equal("47.1.106", service.Instances[0].LoaderVersion);
    }

    // ==================== 第二段：区间与上限传进 Java 判定 ====================

    /// <summary>
    /// <c>GetAllowedRange</c> 带加载器版本时区间要被压窄；不带时与旧行为一字不差。
    ///
    /// 「不带时与旧行为一致」这条是硬要求：老实例没存版本号，
    /// 一旦新版重载在缺参数时给出更紧的区间，这些实例会集体起不来。
    /// </summary>
    [Fact]
    public void 加载器版本会把允许区间压窄()
    {
        using var temp = new TempDirectory();
        using var factory = new DownloadHttpClientFactory(DownloadSource.BmclApi);
        using var java = new JavaService(
            new FakePlatformService(),
            new FakeLauncherPaths(temp.Path),
            new DownloadQueue(factory),
            NullAppLog.Instance);

        var legacy = java.GetAllowedRange("1.19.4", LoaderKind.Forge);
        Assert.True(legacy.HasValue);

        // 既有表对 1.19.4 Forge 只有下界（Java 17 起），上界是「不限」。
        Assert.Equal(17, legacy!.Value.Min);
        Assert.Equal(int.MaxValue, legacy.Value.Max);

        // 45.0.40 这一段只能到 Java 19。
        var tightened = java.GetAllowedRange("1.19.4", LoaderKind.Forge, "45.0.40");
        Assert.True(tightened.HasValue);
        Assert.Equal(17, tightened!.Value.Min);
        Assert.Equal(19, tightened.Value.Max);

        // 拿不到版本号时退回旧结果。
        var missing = java.GetAllowedRange("1.19.4", LoaderKind.Forge, null);
        Assert.Equal(legacy, missing);
    }

    // ==================== 第三段：阻断 / 建议分流 ====================

    /// <summary>
    /// 阻断级：文案必须带上规则自己的 <see cref="JavaLoadLimit.Reason"/>。
    ///
    /// 只写「Java 版本与游戏版本不匹配」的话，用户看到 1.19.4 要求 Java 17–21、
    /// 自己装的 20 明明在区间里，会以为启动器在乱报——真正的原因是 Forge 45.0.40 这个 build 的 modlauncher。
    /// </summary>
    [Fact]
    public void 阻断级违规给出带原因文案的Blocker()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.19.4", LoaderKind.Forge, "45.0.40");
        Assert.NotNull(limit);

        var java = new JavaInfo { Path = @"C:\jdk\bin\java.exe", Version = "20.0.2+9", MajorVersion = 20, Is64Bit = true };
        var items = PreflightChecker.CheckLoaderLimit(java, limit);

        var item = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Blocker, item.Severity);
        Assert.Contains(limit!.Reason, item.Detail, StringComparison.Ordinal);
        Assert.Contains("Java 20", item.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 建议级：<c>8u322</c> 只提示、不阻断。
    ///
    /// 这是整个分级存在的理由：<c>8u320</c> 是当时社区验证过的最高补丁号，
    /// 不是硬边界。<c>8u322</c> 上的 1.16.5 Forge 通常照样能跑，
    /// 做成 Blocker 会把这个用户直接堵死——他没法为一个建议装更旧的 JDK。
    /// </summary>
    [Fact]
    public void 建议级违规只给Warning不阻断()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.25");
        Assert.NotNull(limit);
        Assert.False(limit!.IsBlocking);

        var java = new JavaInfo { Path = @"C:\jdk8\bin\java.exe", Version = "1.8.0_322", MajorVersion = 8, Is64Bit = true };
        var items = PreflightChecker.CheckLoaderLimit(java, limit);

        var item = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Warning, item.Severity);
        Assert.Contains(limit.Reason, item.Detail, StringComparison.Ordinal);
    }

    /// <summary>建议之内的运行时一条提示都不该有（8u320 本身完全合规）。</summary>
    [Fact]
    public void 落在建议范围内的Java没有提示()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.25");
        Assert.NotNull(limit);

        var java = new JavaInfo { Path = @"C:\jdk8\bin\java.exe", Version = "1.8.0_320", MajorVersion = 8, Is64Bit = true };

        Assert.Empty(PreflightChecker.CheckLoaderLimit(java, limit));
    }

    /// <summary>
    /// 整段 <see cref="PreflightChecker.CheckJava"/>：阻断级命中时以加载器规则为准，
    /// 不再叠一条泛化的「区间不匹配」。
    /// </summary>
    [Fact]
    public void 阻断时不再叠加泛化的区间提示()
    {
        var options = new LaunchOptions
        {
            Account = TestAccount,
            VersionName = "1.21.4-fabric-0.16.9",
        };

        var java = new JavaInfo { Path = @"C:\jdk25\bin\java.exe", Version = "25.0.1", MajorVersion = 25, Is64Bit = true };
        var limit = JavaRequirementTable.GetLoaderLimit("1.21.4", LoaderKind.Fabric, "0.16.9");
        var range = JavaRequirementTable.AllowedRange("1.21.4", LoaderKind.Fabric, null, "0.16.9");

        var report = PreflightChecker.CheckJava(options, java, range, limit);

        Assert.True(report.HasBlocker);

        var item = Assert.Single(report.Items, i => i.Severity == PreflightSeverity.Blocker);
        Assert.Contains(limit!.Reason, item.Detail, StringComparison.Ordinal);

        // 泛化文案的标题不该同时出现。
        Assert.DoesNotContain(
            report.Items,
            i => i.Title.Contains("与游戏版本不匹配", StringComparison.Ordinal));
    }

    /// <summary>建议级命中时报告不能有 Blocker，但要有提示项。</summary>
    [Fact]
    public void 建议级在整段检查里只留下提示()
    {
        var options = new LaunchOptions
        {
            Account = TestAccount,
            VersionName = "1.16.5-forge-36.2.25",
        };

        var java = new JavaInfo { Path = @"C:\jdk8\bin\java.exe", Version = "1.8.0_322", MajorVersion = 8, Is64Bit = true };
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.25");
        var range = JavaRequirementTable.AllowedRange("1.16.5", LoaderKind.Forge, null, "36.2.25");

        var report = PreflightChecker.CheckJava(options, java, range, limit);

        Assert.False(report.HasBlocker);
        Assert.True(report.HasWarning);

        var warning = Assert.Single(report.Items, i => i.Severity == PreflightSeverity.Warning);
        Assert.Contains(limit!.Reason, warning.Detail, StringComparison.Ordinal);
    }

    /// <summary>不传上限时行为与加参数之前一致：只做区间判定。</summary>
    [Fact]
    public void 没有加载器上限时退回区间判定()
    {
        var options = new LaunchOptions
        {
            Account = TestAccount,
            VersionName = "1.20.1",
        };

        var java = new JavaInfo { Path = @"C:\jdk21\bin\java.exe", Version = "21.0.1", MajorVersion = 21, Is64Bit = true };

        var withoutLimit = PreflightChecker.CheckJava(options, java, (17, 17));
        var withNullLimit = PreflightChecker.CheckJava(options, java, (17, 17), loadLimit: null);

        Assert.Equal(withoutLimit.Items.Count, withNullLimit.Items.Count);
        Assert.True(withNullLimit.HasBlocker);
    }

    // ==================== 第四段：Java 版本串的更新号 ====================

    [Theory]
    [InlineData("1.8.0_322", 8, 322)]
    [InlineData("1.8.0_320", 8, 320)]
    [InlineData("1.8.0_51", 8, 51)]
    [InlineData("11.0.20", 11, 20)]
    [InlineData("17.0.1+12", 17, 1)]
    [InlineData("21.0.5+11-LTS", 21, 5)]
    public void 版本串拆出主版本与更新号(string version, int expectedMajor, int expectedUpdate)
    {
        var (major, update) = JavaService.ParseJavaVersion(version);

        Assert.Equal(expectedMajor, major);
        Assert.Equal(expectedUpdate, update);
    }

    /// <summary>
    /// 只有主版本时更新号为 null，<c>Exceeds</c> 据此判「未超出」。
    /// 补丁号级上限只是建议，信息不足时漏一次提示好过误报。
    /// </summary>
    [Theory]
    [InlineData("8")]
    [InlineData("17")]
    [InlineData("1.8")]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void 拿不到更新号时返回null(string version)
    {
        var (_, update) = JavaService.ParseJavaVersion(version);

        Assert.Null(update);
    }

    [Fact]
    public void 垃圾串的主版本为0()
    {
        var (major, update) = JavaService.ParseJavaVersion("jdk-unknown");

        Assert.Equal(0, major);
        Assert.Null(update);
    }

    [Fact]
    public void 空版本串不抛异常()
    {
        var (major, update) = JavaService.ParseJavaVersion(null);

        Assert.Equal(0, major);
        Assert.Null(update);
    }

    /// <summary>接上 <see cref="JavaLoadLimit.Exceeds"/>：8u322 超建议、8u320 不超。</summary>
    [Fact]
    public void 更新号接上建议上限判定()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.25");
        Assert.NotNull(limit);

        var newer = JavaService.ParseJavaVersion("1.8.0_322");
        Assert.True(limit!.Exceeds(newer.Major, newer.Update));

        var exact = JavaService.ParseJavaVersion("1.8.0_320");
        Assert.False(limit.Exceeds(exact.Major, exact.Update));
    }

    // ==================== 辅助替身 ====================

    /// <summary>前置检查不读账户信息，给个最小样本即可。</summary>
    private static readonly Account TestAccount = new() { Username = "测试玩家" };

    /// <summary>只回答「装出来叫什么」的安装器替身，不写任何文件。</summary>
    private sealed class StubInstallService : IInstallService
    {
        public string? ResultVersionName { get; init; } = "installed-version";

        public DownloadSource Source { get; set; } = DownloadSource.BmclApi;

        public Task InstallAsync(
            InstallRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? fabricApiVersionId = null)
            => Task.CompletedTask;

        public Task<string?> InstallLoaderAsync(
            InstallRequest request,
            IProgress<DownloadProgress>? progress = null,
            bool overwriteExisting = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ResultVersionName);

        public Task<bool> VerifyAsync(
            string versionName,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    /// <summary>最小版本服务替身：按目录名回答「这个版本解析出什么」。</summary>
    private sealed class StubVersionService : IVersionService
    {
        public VersionInfo? Info { get; init; }

        /// <summary>按版本目录名分别回答；未命中时退回 <see cref="Info"/>。</summary>
        public Dictionary<string, VersionInfo> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DownloadSource Source { get; set; } = DownloadSource.BmclApi;

        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VersionInfo>>([]);

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default)
            => Task.FromResult(ByName.TryGetValue(versionName, out var info) ? info : Info);

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
