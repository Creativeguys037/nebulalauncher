using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 模组卡片「去资源站查找」：预填词清洗 + 跨页预填只搜一次。
///
/// 两条判据分开测：
/// 1. 预填词必须是<b>项目名</b>——文件名里的 <c>-1.20.1-forge-15.2.0</c> 丢进资源站命中率接近零；
/// 2. 预填必须发生在目标页自动搜索<b>之前</b>——否则先按空串搜一次、再按项目名搜一次，
///    界面闪两下还多打一次请求。用计数假服务盯住「搜了几次、搜的是什么」。
///
/// 不触碰任何 Avalonia 类型（见 CONVENTIONS §7.1）。
/// </summary>
public class AppMods_LibraryLookupTests
{
    // ==================== 预填词清洗 ====================

    [Fact]
    public void 预填词_优先用元数据里的项目名()
    {
        var item = MakeItem("sodium-fabric-0.5.8+mc1.20.1.jar", displayName: "Sodium");

        Assert.Equal("Sodium", ModsViewModel.BuildLookupQuery(item));
    }

    [Theory]
    // 文件名的典型形状：项目名 + 游戏版本 + 加载器 + 模组版本，段分隔符三种都见过。
    [InlineData("jei-1.20.1-forge-15.2.0.27.jar", "jei")]
    [InlineData("sodium-fabric-0.5.8+mc1.20.1.jar", "sodium")]
    [InlineData("appleskin-forge-mc1.20-2.5.1.jar", "appleskin")]
    [InlineData("iris-1.7.2.jar", "iris")]
    [InlineData("create-0.5.1-g.jar", "create")]
    [InlineData("Terralith_1.20_v2.4.jar", "Terralith")]
    [InlineData("Xaero's Minimap 24.2.0.jar", "Xaero's Minimap")]
    // 禁用态是改扩展名，必须能把两层扩展名都剥掉。
    [InlineData("sodium-fabric-0.5.8.jar.disabled", "sodium")]
    public void 预填词_没有元数据时剥掉版本与加载器段(string fileName, string expected)
    {
        // ContentService 在元数据缺失时会把 DisplayName 填成文件名主干，
        // 所以这里显式模拟那一种：DisplayName == 主干 != 项目名。
        var item = MakeItem(fileName, displayName: string.Empty);

        Assert.Equal(expected, ModsViewModel.BuildLookupQuery(item));
    }

    [Fact]
    public void 预填词_DisplayName是文件名兜底值时仍然剥版本()
    {
        // 与 ContentService 的真实行为一致：metadata.Name 为空 → DisplayName 取文件名主干。
        var item = MakeItem("jei-1.20.1-forge-15.2.0.27.jar", displayName: "jei-1.20.1-forge-15.2.0.27");

        Assert.Equal("jei", ModsViewModel.BuildLookupQuery(item));
    }

    [Fact]
    public void 预填词_干净的名字原样保留()
    {
        var item = MakeItem("sodium.jar", displayName: "Sodium");

        Assert.Equal("Sodium", ModsViewModel.BuildLookupQuery(item));
    }

    [Fact]
    public void 预填词_整个文件名就是版本号时不剥成空串()
    {
        // 剥完变空比不剥更糟：至少还能按原文搜一次。
        var item = MakeItem("1.20.1.jar", displayName: "1.20.1");

        Assert.Equal("1.20.1", ModsViewModel.BuildLookupQuery(item));
    }

    [Fact]
    public void 预填词_没有可用名字时退回modid()
    {
        // 单字符文件名搜不出任何东西，此时 modid 比文件名更可能是项目名。
        var item = MakeItem("a.jar", displayName: "a", id: "appleskin");

        Assert.Equal("appleskin", ModsViewModel.BuildLookupQuery(item));
    }

    [Fact]
    public void 预填词_名称为空且无id时返回空()
    {
        var item = new ContentItem { FilePath = @"C:\game\mods\.jar", FileName = ".jar" };

        Assert.Equal(string.Empty, ModsViewModel.BuildLookupQuery(item));
    }

    // ==================== 挂起槽 ====================

    [Fact]
    public void 挂起槽_取走即清空且后写覆盖先写()
    {
        LibraryLookupSlot.Clear();

        LibraryLookupSlot.Publish(new LibraryLookup("first", ContentKind.Mod));
        LibraryLookupSlot.Publish(new LibraryLookup("second", ContentKind.ResourcePack));

        var taken = LibraryLookupSlot.TryTake();

        Assert.NotNull(taken);
        Assert.Equal("second", taken!.Query);
        Assert.Equal(ContentKind.ResourcePack, taken.Kind);

        // 同一份请求不能被执行两次：第二次进资源页不该再被旧词劫持。
        Assert.Null(LibraryLookupSlot.TryTake());

        LibraryLookupSlot.Clear();
    }

    // ==================== 资源页消费 ====================

    [Fact]
    public async Task 资源页导航_消费预填并只搜一次()
    {
        LibraryLookupSlot.Clear();

        var modrinth = new RecordingModrinth();
        var viewModel = CreateLibrary(modrinth);

        LibraryLookupSlot.Publish(new LibraryLookup("Sodium", ContentKind.Mod));

        await viewModel.OnNavigatedToAsync();

        // 一次进入只允许一次搜索：多出来的那次必然是按空串搜的。
        var call = Assert.Single(modrinth.Calls);
        Assert.Equal("Sodium", call.Query);
        Assert.Equal(ContentKind.Mod, call.Kind);

        // 搜索框里要看得见预填了什么。
        Assert.Equal("Sodium", viewModel.SearchText);
        Assert.Single(viewModel.Results);
    }

    [Fact]
    public async Task 资源页导航_没有新请求时复用已有结果不再搜索()
    {
        LibraryLookupSlot.Clear();

        var modrinth = new RecordingModrinth();
        var viewModel = CreateLibrary(modrinth);

        LibraryLookupSlot.Publish(new LibraryLookup("Sodium", ContentKind.Mod));
        await viewModel.OnNavigatedToAsync();

        // 页面实例被缓存复用：已有结果时再进页面不该重新打请求。
        await viewModel.OnNavigatedToAsync();
        Assert.Single(modrinth.Calls);

        // 但带了新请求时必须绕过上面的复用分支，真的再搜一次。
        LibraryLookupSlot.Publish(new LibraryLookup("JEI", ContentKind.Mod));
        await viewModel.OnNavigatedToAsync();

        Assert.Equal(2, modrinth.Calls.Count);
        Assert.Equal("JEI", modrinth.Calls[1].Query);
        Assert.Equal("JEI", viewModel.SearchText);
    }

    [Fact]
    public async Task 资源页导航_预填切到条目对应的分类()
    {
        LibraryLookupSlot.Clear();

        var modrinth = new RecordingModrinth();
        var viewModel = CreateLibrary(modrinth);

        // 用户可能正停在资源包分类；用资源包的名字在模组分类里搜只会得到空列表。
        LibraryLookupSlot.Publish(new LibraryLookup("Faithful 32x", ContentKind.ResourcePack));

        await viewModel.OnNavigatedToAsync();

        Assert.Equal(ContentKind.ResourcePack, viewModel.ActiveKind);
        Assert.False(viewModel.IsModPackTabActive);
        Assert.Equal(ContentKind.ResourcePack, Assert.Single(modrinth.Calls).Kind);
    }

    [Fact]
    public async Task 资源页导航_预填保留当前实例的版本与加载器筛选()
    {
        LibraryLookupSlot.Clear();

        var modrinth = new RecordingModrinth();
        var services = new ServiceMap()
            .Add<IModrinthService>(modrinth)
            .Add<IInstanceContext>(new FakeInstanceContext("1.20.1", LoaderKind.Fabric));
        var viewModel = new LibraryViewModel(services);

        LibraryLookupSlot.Publish(new LibraryLookup("Sodium", ContentKind.Mod));

        await viewModel.OnNavigatedToAsync();

        // 筛选是当前实例的属性，不是这次跳转的输入：清掉它就会搜出一堆装不上的版本。
        var call = Assert.Single(modrinth.Calls);
        Assert.Equal("1.20.1", call.GameVersion);
        Assert.Equal(LoaderKind.Fabric, call.Loader);
    }

    [Fact]
    public async Task 资源页导航_数据包预填在CurseForge源下回到Modrinth()
    {
        LibraryLookupSlot.Clear();

        var modrinth = new RecordingModrinth();
        var settings = new FakeSettings { ContentSource = "curseforge" };
        var services = new ServiceMap()
            .Add<IModrinthService>(modrinth)
            .Add<ISettingsService>(settings);
        var viewModel = new LibraryViewModel(services);

        LibraryLookupSlot.Publish(new LibraryLookup("Terralith", ContentKind.DataPack));

        await viewModel.OnNavigatedToAsync();

        // CurseForge 没有数据包分类，沿用「切分类时回到 Modrinth」的既有处理。
        Assert.Equal(ContentSourceKind.Modrinth, viewModel.ActiveSource);
        Assert.Equal(ContentKind.DataPack, Assert.Single(modrinth.Calls).Kind);
    }

    // ==================== 模组页命令 ====================

    [Fact]
    public void 卡片命令_发布预填请求并导航到资源页()
    {
        LibraryLookupSlot.Clear();

        var navigation = new RecordingNavigation();
        var mods = new ModsViewModel(new ServiceMap().Add<INavigationService>(navigation));
        var card = new ContentCardViewModel(
            MakeItem("sodium-fabric-0.5.8+mc1.20.1.jar", displayName: "Sodium"),
            mods);

        mods.LookupInLibraryCommand.Execute(card);

        Assert.Equal(NavigationKeys.Library, navigation.LastKey);

        var lookup = LibraryLookupSlot.TryTake();

        Assert.NotNull(lookup);
        Assert.Equal("Sodium", lookup!.Query);
        Assert.Equal(ContentKind.Mod, lookup.Kind);

        // 取走即清空：不能让同一份请求劫持下一次进入资源页。
        Assert.Null(LibraryLookupSlot.TryTake());
    }

    [Fact]
    public void 卡片命令_导航服务缺失时不留下待消费请求()
    {
        LibraryLookupSlot.Clear();

        var mods = new ModsViewModel(new ServiceMap());
        var card = new ContentCardViewModel(MakeItem("sodium.jar", displayName: "Sodium"), mods);

        mods.LookupInLibraryCommand.Execute(card);

        // 没有消费方时发布出去，会让用户下次手动点进资源页时莫名其妙多搜一次。
        Assert.Null(LibraryLookupSlot.TryTake());
        Assert.False(string.IsNullOrWhiteSpace(mods.ErrorText));
    }

    // ==================== 辅助 ====================

    private static ContentItem MakeItem(
        string fileName,
        string? displayName = null,
        string? id = null,
        ContentKind kind = ContentKind.Mod) =>
        new()
        {
            FilePath = Path.Combine(@"C:\game\mods", fileName),
            FileName = fileName,
            DisplayName = displayName ?? string.Empty,
            Id = id ?? string.Empty,
            Kind = kind,
        };

    private static LibraryViewModel CreateLibrary(IModrinthService modrinth) =>
        new(new ServiceMap().Add(modrinth));

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

    /// <summary>只关心「跳到哪个页面」与「是否跳了」。</summary>
    private sealed class RecordingNavigation : INavigationService
    {
        public PageViewModelBase? Current => null;

        public string? LastKey { get; private set; }

        public event EventHandler<PageViewModelBase>? Navigated
        {
            add { }
            remove { }
        }

        public void Navigate(string key) => LastKey = key;

        public void Navigate(PageViewModelBase page) => LastKey = page.Key;
    }

    /// <summary>
    /// MC 百科搜索地址（PCL2 <c>PageInstanceMod.xaml.vb:772</c>）。
    ///
    /// 判据只有两条：查询词要 URL 编码（中文项目名必须能带进 query string），
    /// 以及必须带 <c>site=all&amp;filter=0</c>——少这两个参数会落到「只搜模组」的筛选里。
    /// </summary>
    [Theory]
    [InlineData("Sodium", "https://www.mcmod.cn/s?key=Sodium&site=all&filter=0")]
    [InlineData("虚无世界", "https://www.mcmod.cn/s?key=%E8%99%9A%E6%97%A0%E4%B8%96%E7%95%8C&site=all&filter=0")]
    [InlineData("Xaero's Minimap", "https://www.mcmod.cn/s?key=Xaero%27s%20Minimap&site=all&filter=0")]
    public void MC百科搜索地址_编码查询词并保留全部筛选(string query, string expected)
    {
        Assert.Equal(expected, ModsViewModel.McmodSearchUrl(query));
    }

    private sealed record SearchCall(
        ContentKind Kind,
        string Query,
        string? GameVersion,
        LoaderKind Loader);

    /// <summary>记录每次搜索的入参；只返回一条结果，让「已有结果就不再重搜」的分支成立。</summary>
    private sealed class RecordingModrinth : IModrinthService
    {
        private readonly List<SearchCall> _calls = [];

        public IReadOnlyList<SearchCall> Calls => _calls;

        public string? LastError => null;

        public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            CancellationToken cancellationToken = default)
        {
            _calls.Add(new SearchCall(kind, query, gameVersion, loader));

            IReadOnlyList<ModrinthProject> result =
            [
                new ModrinthProject { ProjectId = "p1", Slug = "p1", Title = "P1" },
            ];

            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
            string projectId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModrinthVersion>>([]);

        public Task<ModrinthInstallResult> InstallAsync(
            ModrinthVersion version,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<ModrinthVersion?> LookupBySha1Async(
            string sha1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ModrinthVersion?>(null);
    }

    /// <summary>
    /// 提供「当前实例 = 1.20.1 + Fabric」的上下文，用来验证预填不动筛选条件。
    ///
    /// 资源页的筛选现在来自页内的「将装到」下拉：<see cref="ListTargetsAsync"/> 必须把当前实例
    /// 列成一个可选目标，页面才会默认选中它并按 1.20.1 + Fabric 搜索。
    /// </summary>
    private sealed class FakeInstanceContext(string minecraftVersion, LoaderKind loader) : IInstanceContext
    {
        private const string InstanceKey = "fabric";

        public InstanceInfo? Current { get; } = new()
        {
            Id = InstanceKey,
            Name = "1.20.1 Fabric",
            VersionName = "fabric-1.20.1",
        };

        public string GameDirectory => @"C:\instances\fabric";

        public string? VersionName => "fabric-1.20.1";

        public LoaderKind Loader => loader;

        public string? MinecraftVersion => minecraftVersion;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentTarget>>(
            [
                new ContentTarget
                {
                    Key = "global",
                    Kind = ContentTargetKind.Global,
                    DisplayName = "全局目录 (.minecraft)",
                    GameDirectory = @"C:\instances\global",
                },
                new ContentTarget
                {
                    Key = InstanceKey,
                    Kind = ContentTargetKind.Instance,
                    DisplayName = "1.20.1 Fabric",
                    GameDirectory = GameDirectory,
                    InstanceId = InstanceKey,
                    VersionName = "fabric-1.20.1",
                    MinecraftVersion = minecraftVersion,
                    Loader = loader,
                },
            ]);

        public Task<ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentTarget?>(new ContentTarget
            {
                Key = key,
                DisplayName = "1.20.1 Fabric",
                GameDirectory = GameDirectory,
                MinecraftVersion = minecraftVersion,
                Loader = loader,
            });

        public Task<InstanceResolution> ResolveLaunchTargetAsync(
            string versionName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceResolution(GameDirectory, null, Isolated: false, Created: false));
    }

    /// <summary>只读出数据源偏好的设置服务；写入路径在预填流程里用不到。</summary>
    private sealed class FakeSettings : ISettingsService
    {
        public string ContentSource { get; init; } = "modrinth";

        public AppSettings Current { get; } = new();

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            Current.ContentSource = ContentSource;
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}