using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例原地换加载器（本仓库自有的实例维度能力）。
///
/// 全部走 <see cref="IInstallService"/> 的替身，**不联网、不写 versions/ 的真实文件**：
/// 这里验证的是「决策与回滚」——跨版本拒绝、兼容性提示、失败不留脏状态；
/// 真装能不能成，由安装器自己的测试与 <c>tools/InstallProbe</c> 负责。
/// </summary>
public class CoreInstances_LoaderSwitchTests
{
    [Fact]
    public async Task 原版实例加装Fabric后指向新版本且允许覆盖()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService { ResultVersionName = "fabric-loader-0.15.0-1.20.1" };
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var instance = await CreateInstanceAsync(service, "1.20.1", LoaderKind.Vanilla, "1.20.1");

        var result = await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric, "0.15.0");

        Assert.True(result.Success, result.Error);
        Assert.False(result.AlreadyMatches);
        Assert.Equal("fabric-loader-0.15.0-1.20.1", result.NewVersionName);
        Assert.Equal("1.20.1", result.PreviousVersionName);
        Assert.Equal("1.20.1", result.MinecraftVersion);

        // 原版 → 加载器不改变任何现有 mods 的可用性（原版根本不加载 mods）。
        Assert.False(result.RequiresModConfirmation);

        // 换加载器必须是「按目标重装一遍」，不是「装过就跳过」。
        var request = Assert.Single(install.Requests);
        Assert.Equal(LoaderKind.Fabric, request.Loader);
        Assert.Equal("1.20.1", request.MinecraftVersion);
        Assert.Equal("0.15.0", request.LoaderVersion);
        Assert.True(Assert.Single(install.OverwriteFlags));

        // 落盘过：从磁盘重新加载也指向新版本。
        var reloaded = new InstanceService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();
        var saved = Assert.Single(reloaded.Instances);
        Assert.Equal("fabric-loader-0.15.0-1.20.1", saved.VersionName);
        Assert.Equal(LoaderKind.Fabric, saved.Loader);
    }

    [Fact]
    public async Task 跨Minecraft版本被拒且不触发安装()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService { ResultVersionName = "1.21-fabric-0.15.0" };
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var instance = await CreateInstanceAsync(service, "1.20.1", LoaderKind.Vanilla, "1.20.1");

        var result = await service.SwitchLoaderAsync(
            instance.Id,
            LoaderKind.Fabric,
            "0.15.0",
            targetMinecraftVersion: "1.21");

        Assert.False(result.Success);
        Assert.Contains("1.20.1", result.Error, StringComparison.Ordinal);
        Assert.Contains("1.21", result.Error, StringComparison.Ordinal);

        // 拒绝必须在调用安装之前，否则会先下载几十 MB 再告诉用户不行。
        Assert.Empty(install.Requests);
        Assert.Equal("1.20.1", service.Instances[0].VersionName);
    }

    [Fact]
    public async Task Forge换成Fabric需要确认且说明后果()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService { ResultVersionName = "fabric-loader-0.15.0-1.20.1" };
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var instance = await CreateInstanceAsync(service, "1.20.1-forge-47.2.0", LoaderKind.Forge, "1.20.1");

        var result = await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric, "0.15.0");

        Assert.True(result.Success, result.Error);
        Assert.True(result.RequiresModConfirmation);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfirmationMessage));
    }

    [Theory]
    [InlineData(LoaderKind.Vanilla, LoaderKind.Forge, false)]
    [InlineData(LoaderKind.Fabric, LoaderKind.Quilt, false)]
    [InlineData(LoaderKind.Quilt, LoaderKind.Fabric, false)]
    [InlineData(LoaderKind.Vanilla, LoaderKind.Vanilla, false)]
    [InlineData(LoaderKind.Fabric, LoaderKind.Forge, true)]
    [InlineData(LoaderKind.Forge, LoaderKind.NeoForge, true)]
    [InlineData(LoaderKind.Fabric, LoaderKind.Vanilla, true)]
    public void 兼容性提示只在mods真的会失效时出现(LoaderKind from, LoaderKind to, bool expected)
    {
        Assert.Equal(expected, InstanceLoaderAdvice.RequiresModConfirmation(from, to));
        Assert.Equal(expected, InstanceLoaderAdvice.DescribeConsequence(from, to) is not null);
    }

    [Fact]
    public async Task 安装失败时实例仍指向原版本()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService { FailWith = new InvalidOperationException("模拟下载中断") };
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var instance = await CreateInstanceAsync(service, "1.20.1", LoaderKind.Vanilla, "1.20.1");

        var result = await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric, "0.15.0");

        Assert.False(result.Success);
        Assert.Contains("模拟下载中断", result.Error, StringComparison.Ordinal);
        Assert.Equal("1.20.1", service.Instances[0].VersionName);
        Assert.Equal(LoaderKind.Vanilla, service.Instances[0].Loader);
    }

    [Fact]
    public async Task 失败时清掉本次新建的半成品版本目录()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService
        {
            FailWith = new InvalidOperationException("模拟安装器崩溃"),
            CreateDirectoryBeforeFail = "fabric-loader-0.15.0-1.20.1",
            VersionsRoot = paths.VersionsDirectory,
        };

        var service = new InstanceService(paths, NullAppLog.Instance, install: install);
        var instance = await CreateInstanceAsync(service, "1.20.1", LoaderKind.Vanilla, "1.20.1");

        await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric, "0.15.0");

        // 装前不存在的目录由失败路径删掉；留着会被 LoadAsync / 版本页当成一个能启动的版本。
        Assert.False(Directory.Exists(Path.Combine(paths.VersionsDirectory, "fabric-loader-0.15.0-1.20.1")));
    }

    [Fact]
    public async Task 已经是目标加载器且未指定版本时直接返回一致()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService();
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var instance = await CreateInstanceAsync(
            service, "fabric-loader-0.15.0-1.20.1", LoaderKind.Fabric, "1.20.1");

        var result = await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric);

        Assert.True(result.Success);
        Assert.True(result.AlreadyMatches);

        // 没有指定版本号 = 不是「升级加载器」，不该在用户只是打开面板时就开始下载。
        Assert.Empty(install.Requests);
    }

    [Fact]
    public async Task 安装服务没实现时返回失败而不是静默成功()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 这个替身**不覆写** InstallLoaderAsync，走的是接口默认实现（返回 null）。
        var install = new FakeInstallServiceWithoutLoaderOverride();
        var service = new InstanceService(paths, NullAppLog.Instance, install: install);

        var instance = await CreateInstanceAsync(service, "1.20.1", LoaderKind.Vanilla, "1.20.1");

        var result = await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric, "0.15.0");

        Assert.False(result.Success);
        Assert.Contains("版本名", result.Error, StringComparison.Ordinal);
        Assert.Equal("1.20.1", service.Instances[0].VersionName);
    }

    [Fact]
    public async Task 实例不存在时返回可展示的失败原因()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance, install: new FakeInstallService());

        var result = await service.SwitchLoaderAsync("not-a-real-id", LoaderKind.Fabric, "0.15.0");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task 以磁盘上的版本信息覆盖过期的缓存字段()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var install = new FakeInstallService { ResultVersionName = "fabric-loader-0.15.0-1.21" };

        // 缓存写的是 1.20.1，磁盘上的版本目录其实是 1.21：以磁盘为准。
        var versions = new StubVersionService
        {
            Info = new VersionInfo { Name = "1.21", MinecraftVersion = "1.21", Loader = LoaderKind.Vanilla },
        };

        var service = new InstanceService(paths, NullAppLog.Instance, versions, install);
        var instance = await CreateInstanceAsync(service, "1.21", LoaderKind.Vanilla, "1.20.1");

        var result = await service.SwitchLoaderAsync(instance.Id, LoaderKind.Fabric, "0.15.0");

        Assert.True(result.Success, result.Error);
        Assert.Equal("1.21", Assert.Single(install.Requests).MinecraftVersion);
        Assert.Equal("1.21", service.Instances[0].MinecraftVersion);
    }

    // ---------------- 辅助 ----------------

    private static async Task<InstanceInfo> CreateInstanceAsync(
        InstanceService service,
        string versionName,
        LoaderKind loader,
        string minecraftVersion)
    {
        var created = await service.CreateAsync("测试实例", versionName);
        created.Loader = loader;
        created.MinecraftVersion = minecraftVersion;
        await service.SaveAsync(created);

        return created;
    }

    /// <summary>记录请求的安装器替身，不写任何文件。</summary>
    private sealed class FakeInstallService : IInstallService
    {
        public List<InstallRequest> Requests { get; } = [];

        public List<bool> OverwriteFlags { get; } = [];

        public string? ResultVersionName { get; init; } = "installed-version";

        public Exception? FailWith { get; init; }

        /// <summary>非空时在失败前先造出这个版本目录，用来验证失败路径会清掉半成品。</summary>
        public string? CreateDirectoryBeforeFail { get; init; }

        /// <summary>造目录用的 versions 根目录（由测试传入）。</summary>
        public string? VersionsRoot { get; init; }

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
        {
            Requests.Add(request);
            OverwriteFlags.Add(overwriteExisting);

            if (CreateDirectoryBeforeFail is not null && VersionsRoot is not null)
            {
                Directory.CreateDirectory(Path.Combine(VersionsRoot, CreateDirectoryBeforeFail));
            }

            if (FailWith is not null)
            {
                return Task.FromException<string?>(FailWith);
            }

            return Task.FromResult(ResultVersionName);
        }

        public Task<bool> VerifyAsync(
            string versionName,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    /// <summary>
    /// 只实现接口必填成员、**不写** <c>InstallLoaderAsync</c> 的替身。
    ///
    /// 这样接口的默认实现（返回 null）才会真的生效——这正是「服务没实现」时的真实形态。
    /// 继承 <see cref="FakeInstallService"/> 是没用的：它已经覆写了那个方法。
    /// </summary>
    private sealed class FakeInstallServiceWithoutLoaderOverride : IInstallService
    {
        public DownloadSource Source { get; set; } = DownloadSource.BmclApi;

        public Task InstallAsync(
            InstallRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? fabricApiVersionId = null)
            => Task.CompletedTask;

        public Task<bool> VerifyAsync(
            string versionName,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    /// <summary>最小版本服务替身：只回答「这个版本目录解析出什么」。</summary>
    private sealed class StubVersionService : IVersionService
    {
        public VersionInfo? Info { get; init; }

        public DownloadSource Source { get; set; } = DownloadSource.BmclApi;

        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VersionInfo>>([]);

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default)
            => Task.FromResult(Info);

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