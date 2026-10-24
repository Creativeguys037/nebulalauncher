using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本隔离的运行时状态：当前实例、按版本解析游戏目录、内容目标枚举。
///
/// 这组用例覆盖的是「模组页到底去哪个目录找文件」的判定链：
/// 以前各页直接读 <c>ILauncherPaths.MinecraftDirectory</c>，实例目录里的 mods 永远读不到。
/// </summary>
public class CoreLaunch_InstanceContextTests
{
    /// <summary>只回答「某个版本目录是什么」的版本服务，其余成员不会被本类调用。</summary>
    private sealed class StubVersionService : IVersionService
    {
        private readonly Dictionary<string, VersionInfo> _versions = new(StringComparer.Ordinal);

        public int GetVersionCalls { get; private set; }

        public StubVersionService Add(string name, LoaderKind loader, string? minecraftVersion, string? loaderVersion = null)
        {
            _versions[name] = new VersionInfo
            {
                Name = name,
                Loader = loader,
                MinecraftVersion = minecraftVersion,
                LoaderVersion = loaderVersion,
            };
            return this;
        }

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default)
        {
            GetVersionCalls++;
            return Task.FromResult(_versions.TryGetValue(versionName, out var info) ? info : null);
        }

        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionInfo>>(_versions.Values.ToArray());

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteVersion>>(Array.Empty<RemoteVersion>());

        public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordLaunchAsync(string versionName, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
            LoaderKind loader,
            string minecraftVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LoaderVersion>>(Array.Empty<LoaderVersion>());
    }

    /// <summary>组装一套落在临时目录里的真实服务（实例与设置都用真实实现，只有版本服务是桩）。</summary>
    private static (InstanceContext Context, FakeLauncherPaths Paths, InstanceService Instances, SettingsService Settings)
        Build(TempDirectory temp, StubVersionService versions)
    {
        var paths = new FakeLauncherPaths(temp.Path);
        var instances = new InstanceService(paths, NullAppLog.Instance);
        var settings = new SettingsService(paths, NullAppLog.Instance);

        var context = new InstanceContext(instances, paths, versions, settings, NullAppLog.Instance);

        return (context, paths, instances, settings);
    }

    [Fact]
    public async Task WithoutInstances_OnlyGlobalTarget()
    {
        using var temp = new TempDirectory();
        var (context, paths, _, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        var targets = await context.ListTargetsAsync();

        Assert.Single(targets);
        Assert.Equal(InstanceContext.GlobalTargetKey, targets[0].Key);
        Assert.Equal(ContentTargetKind.Global, targets[0].Kind);
        Assert.Equal(paths.MinecraftDirectory, targets[0].GameDirectory);
        Assert.Null(targets[0].InstanceId);
    }

    [Fact]
    public async Task NoInstances_GameDirectoryFallsBackToMinecraft()
    {
        using var temp = new TempDirectory();
        var (context, paths, _, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        Assert.Null(context.Current);
        Assert.Equal(paths.MinecraftDirectory, context.GameDirectory);
        Assert.Equal(LoaderKind.Vanilla, context.Loader);
        Assert.Null(context.MinecraftVersion);
    }

    [Fact]
    public async Task ListTargets_IncludesEveryInstanceWithResolvedLoader()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService()
            .Add("1.20.1", LoaderKind.Vanilla, "1.20.1")
            .Add("fabric-loader-0.16.9-1.20.1", LoaderKind.Fabric, "1.20.1", "0.16.9")
            .Add("1.12.2-forge-14.23.5.2859", LoaderKind.Forge, "1.12.2", "14.23.5.2859");

        var (context, _, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        var fabric = await instances.CreateAsync("模组生存", "fabric-loader-0.16.9-1.20.1");
        var forge = await instances.CreateAsync("旧版整合", "1.12.2-forge-14.23.5.2859");

        var targets = await context.ListTargetsAsync();

        Assert.Equal(3, targets.Count);

        var fabricTarget = targets.Single(t => t.Key == fabric.Id);
        Assert.Equal(ContentTargetKind.Instance, fabricTarget.Kind);
        Assert.Equal(LoaderKind.Fabric, fabricTarget.Loader);
        Assert.Equal("1.20.1", fabricTarget.MinecraftVersion);
        Assert.Equal("0.16.9", fabricTarget.LoaderVersion);
        Assert.Equal("Fabric", fabricTarget.LoaderLabel);
        Assert.Equal("模组生存", fabricTarget.DisplayName);
        Assert.Equal(instances.GetGameDirectory(fabric.Id), fabricTarget.GameDirectory);

        var forgeTarget = targets.Single(t => t.Key == forge.Id);
        Assert.Equal(LoaderKind.Forge, forgeTarget.Loader);
        Assert.Equal("1.12.2", forgeTarget.MinecraftVersion);
    }

    [Fact]
    public async Task ListTargets_WhenVersionDirectoryMissing_UsesInstanceMetadata()
    {
        using var temp = new TempDirectory();
        var (context, _, instances, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        // 版本目录不存在：版本服务返回 null，只能靠 instance.json 里记录的加载器兜底。
        var created = await instances.CreateAsync("孤儿实例", "fabric-loader-0.16.9-1.20.1");
        created.Loader = LoaderKind.Fabric;
        created.MinecraftVersion = "1.20.1";
        await instances.SaveAsync(created);

        var targets = await context.ListTargetsAsync();
        var target = targets.Single(t => t.Key == created.Id);

        Assert.Equal(LoaderKind.Fabric, target.Loader);
        Assert.Equal("1.20.1", target.MinecraftVersion);
        Assert.False(target.IsVersionInstalled);
    }

    [Fact]
    public async Task SelectAsync_ExistingInstance_SwitchesGameDirectory()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("fabric-loader-0.16.9-1.20.1", LoaderKind.Fabric, "1.20.1");

        var (context, _, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        var created = await instances.CreateAsync("模组生存", "fabric-loader-0.16.9-1.20.1");

        await context.SelectAsync(created.Id);

        Assert.NotNull(context.Current);
        Assert.Equal(created.Id, context.Current!.Id);
        Assert.Equal(instances.GetGameDirectory(created.Id), context.GameDirectory);
        Assert.Equal(LoaderKind.Fabric, context.Loader);
        Assert.Equal("1.20.1", context.MinecraftVersion);
    }

    [Fact]
    public async Task SelectAsync_UnknownInstance_FallsBackWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var (context, paths, _, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        await context.SelectAsync("does-not-exist");

        Assert.Null(context.Current);
        Assert.Equal(paths.MinecraftDirectory, context.GameDirectory);
    }

    [Fact]
    public async Task SelectAsync_Null_ReturnsToGlobalDirectory()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.Vanilla, "1.20.1");

        var (context, paths, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        var created = await instances.CreateAsync("生存", "1.20.1");
        await context.SelectAsync(created.Id);
        await context.SelectAsync(null);

        Assert.Null(context.Current);
        Assert.Equal(paths.MinecraftDirectory, context.GameDirectory);
        Assert.Null(settings.Current.LastInstanceId);
    }

    [Fact]
    public async Task SelectAsync_RaisesChangedOnlyWhenSelectionActuallyChanges()
    {
        using var temp = new TempDirectory();
        var (context, _, instances, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        var created = await instances.CreateAsync("生存", "1.20.1");

        var raised = 0;
        context.Changed += (_, _) => raised++;

        await context.SelectAsync(created.Id);
        await context.SelectAsync(created.Id);
        await context.SelectAsync(created.Id);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SelectAsync_WritesBackLoaderResolvedFromDisk()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.NeoForge, "1.20.1", "21.1.0");

        var (context, _, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        // 实例创建时不知道加载器（默认 Vanilla），磁盘上的版本 JSON 才是权威。
        var created = await instances.CreateAsync("被重装过的实例", "1.20.1");

        await context.SelectAsync(created.Id);

        var reloaded = instances.Instances.Single(i => i.Id == created.Id);
        Assert.Equal(LoaderKind.NeoForge, reloaded.Loader);
        Assert.Equal("1.20.1", reloaded.MinecraftVersion);
    }

    [Fact]
    public async Task LoadAsync_RestoresLastInstanceFromSettings()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.Fabric, "1.20.1", "0.16.9");

        var (context, _, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        var created = await instances.CreateAsync("模组生存", "1.20.1");
        await context.SelectAsync(created.Id);

        // 模拟重启：新的上下文实例读同一份 settings.json / instances 目录。
        var paths = new FakeLauncherPaths(temp.Path);
        var restartedInstances = new InstanceService(paths, NullAppLog.Instance);
        var restartedSettings = new SettingsService(paths, NullAppLog.Instance);
        var restarted = new InstanceContext(
            restartedInstances,
            paths,
            versions,
            restartedSettings,
            NullAppLog.Instance);

        await restartedSettings.LoadAsync();
        await restarted.LoadAsync();

        Assert.NotNull(restarted.Current);
        Assert.Equal(created.Id, restarted.Current!.Id);
        Assert.Equal(restartedInstances.GetGameDirectory(created.Id), restarted.GameDirectory);
    }

    [Fact]
    public async Task LoadAsync_LastInstanceDeleted_FallsBackToGlobal()
    {
        using var temp = new TempDirectory();
        var (context, paths, instances, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        var created = await instances.CreateAsync("临时实例", "1.20.1");
        await context.SelectAsync(created.Id);
        await instances.DeleteAsync(created.Id);

        await context.LoadAsync();

        Assert.Null(context.Current);
        Assert.Equal(paths.MinecraftDirectory, context.GameDirectory);
    }

    [Fact]
    public async Task GlobalTarget_DoesNotBindToAnyVersion()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.21.4", LoaderKind.Fabric, "1.21.4", "0.17.2");

        var (context, paths, _, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        settings.Current.LastSelectedVersion = "1.21.4";

        var targets = await context.ListTargetsAsync();
        var global = targets.Single(t => t.Key == InstanceContext.GlobalTargetKey);

        // 全局目录是 .minecraft 根目录本身，不属于任何版本，也不该被标成「1.21.4 原版」：
        // 旧实现把 lastSelectedVersion 塞进来，界面据此收起了模组入口。
        Assert.Null(global.VersionName);
        Assert.Equal(LoaderKind.Vanilla, global.Loader);
        Assert.Null(global.MinecraftVersion);
        Assert.True(global.IsVersionInstalled);
        Assert.Equal(paths.MinecraftDirectory, global.GameDirectory);
        Assert.Contains("全局", global.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTargetAsync_ReturnsNullForUnknownKey()
    {
        using var temp = new TempDirectory();
        var (context, _, _, settings) = Build(temp, new StubVersionService());

        await settings.LoadAsync();
        await context.LoadAsync();

        Assert.Null(await context.GetTargetAsync("no-such-target"));
        Assert.Null(await context.GetTargetAsync("  "));

        var global = await context.GetTargetAsync(InstanceContext.GlobalTargetKey);
        Assert.NotNull(global);
    }

    [Theory]
    [InlineData(ContentKind.Mod, "mods")]
    [InlineData(ContentKind.ResourcePack, "resourcepacks")]
    [InlineData(ContentKind.ShaderPack, "shaderpacks")]
    [InlineData(ContentKind.World, "saves")]
    public void ContentTarget_BuildsKindSubdirectory(ContentKind kind, string expected)
    {
        var target = new ContentTarget
        {
            Key = "global",
            DisplayName = "全局",
            GameDirectory = @"D:\games\.minecraft",
        };

        Assert.Equal(Path.Combine(@"D:\games\.minecraft", expected), target.ContentDirectory(kind));
    }

    // ---------------- 隔离策略 ----------------

    [Theory]
    [InlineData("never", IsolationPolicyKind.Never)]
    [InlineData("NEVER", IsolationPolicyKind.Never)]
    [InlineData("always", IsolationPolicyKind.Always)]
    [InlineData("modded", IsolationPolicyKind.Modded)]
    [InlineData("", IsolationPolicyKind.Modded)]
    [InlineData(null, IsolationPolicyKind.Modded)]
    [InlineData("garbage", IsolationPolicyKind.Modded)]
    public void IsolationPolicy_Parse(string? value, IsolationPolicyKind expected)
    {
        Assert.Equal(expected, IsolationPolicyResolver.Parse(value));
    }

    [Theory]
    [InlineData(IsolationPolicyKind.Never, LoaderKind.Vanilla, false)]
    [InlineData(IsolationPolicyKind.Never, LoaderKind.Fabric, false)]
    [InlineData(IsolationPolicyKind.Modded, LoaderKind.Vanilla, false)]
    [InlineData(IsolationPolicyKind.Modded, LoaderKind.Fabric, true)]
    [InlineData(IsolationPolicyKind.Modded, LoaderKind.Forge, true)]
    [InlineData(IsolationPolicyKind.Modded, LoaderKind.NeoForge, true)]
    [InlineData(IsolationPolicyKind.Modded, LoaderKind.Quilt, true)]
    [InlineData(IsolationPolicyKind.Always, LoaderKind.Vanilla, true)]
    [InlineData(IsolationPolicyKind.Always, LoaderKind.Fabric, true)]
    public void IsolationPolicy_ShouldIsolate(IsolationPolicyKind policy, LoaderKind loader, bool expected)
    {
        Assert.Equal(expected, IsolationPolicyResolver.ShouldIsolate(policy, loader));
    }

    /// <summary>
    /// 版本目录里已经有 mods/saves 时恒隔离（PCL2 <c>ModMinecraft.vb:252-256</c>）：
    /// 设置页改成「不隔离」不该让已有内容突然读不到。
    /// </summary>
    [Fact]
    public void IsolationPolicy_VersionDirectoryContentForcesIsolation()
    {
        Assert.True(IsolationPolicyResolver.ShouldIsolate(
            IsolationPolicyKind.Never, LoaderKind.Vanilla, versionDirectoryHasContent: true));
        Assert.True(IsolationPolicyResolver.ShouldIsolate(
            IsolationPolicyKind.Never, LoaderKind.Fabric, versionDirectoryHasContent: true));
        Assert.False(IsolationPolicyResolver.ShouldIsolate(
            IsolationPolicyKind.Never, LoaderKind.Vanilla, versionDirectoryHasContent: false));
    }

    [Fact]
    public void IsolationPolicy_ToSettingValue_RoundTrips()
    {
        foreach (var policy in new[]
                 {
                     IsolationPolicyKind.Never, IsolationPolicyKind.Modded, IsolationPolicyKind.Always,
                 })
        {
            Assert.Equal(policy, IsolationPolicyResolver.Parse(IsolationPolicyResolver.ToSettingValue(policy)));
        }
    }

    [Fact]
    public async Task ResolveLaunchTarget_ExistingInstance_IsReused()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.Vanilla, "1.20.1");

        var (context, _, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        var existing = await instances.CreateAsync("生存", "1.20.1");

        var resolution = await context.ResolveLaunchTargetAsync("1.20.1");

        Assert.True(resolution.Isolated);
        Assert.False(resolution.Created);
        Assert.Equal(existing.Id, resolution.Instance!.Id);
        Assert.Equal(instances.GetGameDirectory(existing.Id), resolution.GameDirectory);
        Assert.Single(instances.Instances);
    }

    [Fact]
    public async Task ResolveLaunchTarget_ModdedPolicy_AutoCreatesInstanceForFabric()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService()
            .Add("fabric-loader-0.16.9-1.20.1", LoaderKind.Fabric, "1.20.1", "0.16.9");

        var (context, _, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        settings.Current.IsolationPolicy = "modded";

        var resolution = await context.ResolveLaunchTargetAsync("fabric-loader-0.16.9-1.20.1");

        Assert.True(resolution.Isolated);
        Assert.True(resolution.Created);
        Assert.NotNull(resolution.Instance);
        Assert.Equal(LoaderKind.Fabric, resolution.Instance!.Loader);
        Assert.Equal("1.20.1", resolution.Instance.MinecraftVersion);
        Assert.Equal(instances.GetGameDirectory(resolution.Instance.Id), resolution.GameDirectory);

        // 上下文同时被切到了新实例，页面不用再自己同步一次。
        Assert.Equal(resolution.Instance.Id, context.Current?.Id);

        // 落到磁盘，下次启动能重新读回到同一个实例。
        await instances.LoadAsync();
        Assert.Single(instances.Instances);
        Assert.Equal(LoaderKind.Fabric, instances.Instances[0].Loader);
    }

    [Fact]
    public async Task ResolveLaunchTarget_ModdedPolicy_KeepsVanillaInGlobalDirectory()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.Vanilla, "1.20.1");

        var (context, paths, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        settings.Current.IsolationPolicy = "modded";

        var resolution = await context.ResolveLaunchTargetAsync("1.20.1");

        Assert.False(resolution.Isolated);
        Assert.False(resolution.Created);
        Assert.Null(resolution.Instance);
        Assert.Equal(paths.MinecraftDirectory, resolution.GameDirectory);
        Assert.Empty(instances.Instances);
    }

    [Fact]
    public async Task ResolveLaunchTarget_AlwaysPolicy_IsolatesVanilla()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.Vanilla, "1.20.1");

        var (context, _, _, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        settings.Current.IsolationPolicy = "always";

        var resolution = await context.ResolveLaunchTargetAsync("1.20.1");

        Assert.True(resolution.Isolated);
        Assert.True(resolution.Created);
        Assert.NotNull(resolution.Instance);
    }

    [Fact]
    public async Task ResolveLaunchTarget_NeverPolicy_KeepsEverythingGlobal()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("fabric-loader-0.16.9-1.20.1", LoaderKind.Fabric, "1.20.1");

        var (context, paths, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        settings.Current.IsolationPolicy = "never";

        var resolution = await context.ResolveLaunchTargetAsync("fabric-loader-0.16.9-1.20.1");

        Assert.False(resolution.Isolated);
        Assert.False(resolution.Created);
        Assert.Equal(paths.MinecraftDirectory, resolution.GameDirectory);
        Assert.Empty(instances.Instances);
    }

    [Fact]
    public async Task ResolveLaunchTarget_AutoCreatedInstance_IsIdempotent()
    {
        using var temp = new TempDirectory();
        var versions = new StubVersionService().Add("1.20.1", LoaderKind.Fabric, "1.20.1");

        var (context, paths, instances, settings) = Build(temp, versions);
        await settings.LoadAsync();
        await context.LoadAsync();

        settings.Current.IsolationPolicy = "always";

        // 另一个版本已有实例时，自动创建的实例仍用版本名作为名字与目录 ——
        // 实例目录就是 versions/<版本名>，名字不再需要「唯一化」。
        await instances.CreateAsync("1.19.4", "1.19.4");
        var resolution = await context.ResolveLaunchTargetAsync("1.20.1");

        Assert.NotNull(resolution.Instance);
        Assert.True(resolution.Created);
        Assert.Equal("1.20.1", resolution.Instance!.Name);
        Assert.Equal("1.20.1", resolution.Instance.VersionName);
        Assert.Equal(
            Path.Combine(paths.VersionsDirectory, "1.20.1"),
            resolution.GameDirectory,
            ignoreCase: true);

        // 同一个版本再解析一次必须复用已有实例，不能重建目录或改写 instance.json。
        var second = await context.ResolveLaunchTargetAsync("1.20.1");

        Assert.NotNull(second.Instance);
        Assert.False(second.Created);
        Assert.Equal(resolution.Instance.Id, second.Instance!.Id);
        Assert.Single(instances.Instances, i => i.VersionName == "1.20.1");
    }
}
