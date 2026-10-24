using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 原版目标在模组页里的表现。
///
/// 产品语义是两级分类：左栏先分「版本」（原版也必须列出来——用户要能找到自己的版本，
/// 而不是在启动器里找不到它），右侧再分「这个版本里的内容类型」。
/// 原版没有 mod API，所以第二级里的「模组」入口收起、页面停在资源包；
/// 左栏这一级不能少东西。
///
/// 不触碰任何 Avalonia 类型（见 CONVENTIONS §7.1）：入口的收起动画属于视图层，由截图验收。
/// </summary>
public class AppMods_VanillaTargetTests
{
    [Fact]
    public async Task 左栏保留原版目标()
    {
        var mods = CreateViewModel(
            Target("global", "全局目录 (.minecraft)", ContentTargetKind.Global, LoaderKind.Vanilla),
            Target("vanilla-1.21", "1.21", ContentTargetKind.Instance, LoaderKind.Vanilla),
            Target("fabric-1.21", "fabric-loader-0.19.5-1.21", ContentTargetKind.Instance, LoaderKind.Fabric));

        await mods.OnNavigatedToAsync();

        // 全局目录独立置顶，不参与加载器分组；分组里只剩实例。
        Assert.NotNull(mods.GlobalTarget);
        Assert.Equal("global", mods.GlobalTarget!.Target.Key);
        Assert.Equal("所有版本共用", mods.GlobalTarget.MetaText);

        Assert.Equal(2, mods.TargetGroups.Count);

        var vanilla = mods.TargetGroups.Single(group => group.Loader == LoaderKind.Vanilla);
        Assert.Single(vanilla.Items);
        Assert.Equal("vanilla-1.21", vanilla.Items[0].Target.Key);
    }

    [Fact]
    public async Task 原版目标_模组入口收起并停在资源包()
    {
        var mods = CreateViewModel(
            Target("global", "全局目录 (.minecraft)", ContentTargetKind.Global, LoaderKind.Vanilla),
            Target("fabric-1.21", "fabric-loader-0.19.5-1.21", ContentTargetKind.Instance, LoaderKind.Fabric),
            Target("vanilla-1.21", "1.21", ContentTargetKind.Instance, LoaderKind.Vanilla));

        await mods.OnNavigatedToAsync();

        // 没有页内记忆时首选全局目标。全局目录不是版本，模组入口必须可用——
        // 否则用户连 .minecraft/mods 都打不开（旧实现就是把它当成了「1.21 原版」）。
        Assert.Equal("global", mods.SelectedTarget?.Target.Key);
        Assert.True(mods.CanLoadMods);

        var vanilla = mods.TargetGroups.SelectMany(group => group.Items)
            .Single(item => item.Target.Key == "vanilla-1.21");

        await mods.SelectTargetCommand.ExecuteAsync(vanilla);

        // 原版实例没有 mod API：收起入口并把分类从模组挪到资源包。
        Assert.False(mods.CanLoadMods);
        Assert.Equal(ContentKind.ResourcePack, mods.ActiveKind);
    }

    [Fact]
    public async Task 切到带加载器的目标_模组入口恢复()
    {
        var mods = CreateViewModel(
            Target("global", "全局目录 (.minecraft)", ContentTargetKind.Global, LoaderKind.Vanilla),
            Target("vanilla-1.21", "1.21", ContentTargetKind.Instance, LoaderKind.Vanilla),
            Target("fabric-1.21", "fabric-loader-0.19.5-1.21", ContentTargetKind.Instance, LoaderKind.Fabric));

        await mods.OnNavigatedToAsync();

        var vanilla = mods.TargetGroups.SelectMany(group => group.Items)
            .Single(item => item.Target.Key == "vanilla-1.21");
        await mods.SelectTargetCommand.ExecuteAsync(vanilla);
        Assert.False(mods.CanLoadMods);

        var fabric = mods.TargetGroups.SelectMany(group => group.Items)
            .Single(item => item.Target.Key == "fabric-1.21");
        await mods.SelectTargetCommand.ExecuteAsync(fabric);

        Assert.True(mods.CanLoadMods);
    }

    [Fact]
    public async Task 从模组分类切到原版目标_自动落到资源包()
    {
        // 当前实例是 Fabric：进页面时首选它，分类停在模组。
        var mods = CreateWithCurrent(
            "fabric-1.21",
            Target("global", "全局目录 (.minecraft)", ContentTargetKind.Global, LoaderKind.Vanilla),
            Target("fabric-1.21", "fabric-loader-0.19.5-1.21", ContentTargetKind.Instance, LoaderKind.Fabric),
            Target("vanilla-1.21", "1.21", ContentTargetKind.Instance, LoaderKind.Vanilla));

        await mods.OnNavigatedToAsync();

        Assert.Equal("fabric-1.21", mods.SelectedTarget?.Target.Key);
        Assert.True(mods.CanLoadMods);
        Assert.Equal(ContentKind.Mod, mods.ActiveKind);

        var vanilla = mods.TargetGroups.SelectMany(group => group.Items)
            .Single(item => item.Target.Key == "vanilla-1.21");
        await mods.SelectTargetCommand.ExecuteAsync(vanilla);

        Assert.False(mods.CanLoadMods);
        Assert.Equal(ContentKind.ResourcePack, mods.ActiveKind);
    }

    [Fact]
    public void 服务缺失时的占位目标_模组入口不收起()
    {
        // 没有 IInstanceContext：页面自建占位全局目标（设计器预览、单测的常态）。
        // 它的 Loader 只是字段默认值，不是「原版」的证据；按它收起入口会让页面首帧就没有模组。
        var mods = new ModsViewModel(new ServiceMap());

        Assert.True(mods.CanLoadMods);
    }

    // ==================== 辅助 ====================

    private static ModsViewModel CreateViewModel(params ContentTarget[] targets) =>
        Create(new FakeInstanceContext(null, targets));

    private static ModsViewModel CreateWithCurrent(string currentInstanceId, params ContentTarget[] targets) =>
        Create(new FakeInstanceContext(currentInstanceId, targets));

    private static ModsViewModel Create(FakeInstanceContext context) =>
        new(new ServiceMap()
            .Add<IInstanceContext>(context)
            .Add<IContentService>(new EmptyContentService()));

    private static ContentTarget Target(
        string key,
        string displayName,
        ContentTargetKind kind,
        LoaderKind loader,
        string minecraftVersion = "1.21") =>
        new()
        {
            Key = key,
            Kind = kind,
            DisplayName = displayName,
            GameDirectory = Path.Combine(@"C:\game", key),
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            IsVersionInstalled = true,
        };

    /// <summary>只按类型索引的服务容器替身；未注册的类型返回 null，与真实 DI 的解析结果一致。</summary>
    private sealed class ServiceMap : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public ServiceMap Add<T>(T service)
            where T : class
        {
            _services[typeof(T)] = service;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    /// <summary>只回答「有哪些内容目标」；当前实例用于验证「进页面时的首选目标」。</summary>
    private sealed class FakeInstanceContext(string? currentInstanceId, ContentTarget[] targets) : IInstanceContext
    {
        public InstanceInfo? Current => currentInstanceId is null
            ? null
            : new InstanceInfo { Id = currentInstanceId, Name = currentInstanceId };

        public string GameDirectory => @"C:\game";

        public string? VersionName => null;

        public LoaderKind Loader => LoaderKind.Vanilla;

        public string? MinecraftVersion => "1.21";

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentTarget>>(targets);

        public Task<ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentTarget?>(targets.FirstOrDefault(target => target.Key == key));

        public Task<InstanceResolution> ResolveLaunchTargetAsync(
            string versionName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceResolution(GameDirectory, null, Isolated: false, Created: false));
    }

    /// <summary>
    /// 扫描结果恒为空的内容服务：这些用例只关心目标与分类，右栏内容一律为空更好断言。
    /// 写操作不应该被触达，真被调用就抛出，避免测试悄悄覆盖了别的行为。
    /// </summary>
    private sealed class EmptyContentService : IContentService
    {
        public Task<IReadOnlyList<ContentItem>> ScanAsync(
            string gameDirectory,
            ContentKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentItem>>([]);

        public Task SetEnabledAsync(
            ContentItem item,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ContentItem item, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ImportAsync(
            string gameDirectory,
            ContentKind kind,
            IReadOnlyList<string> sourceFiles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<ModConflict> DetectConflicts(IReadOnlyList<ContentItem> mods) => [];

        public IReadOnlyList<ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<ContentItem> mods) => [];

        public IReadOnlyList<ContentItem> FindVersionMismatch(
            IReadOnlyList<ContentItem> mods,
            string minecraftVersion) => [];
    }
}
