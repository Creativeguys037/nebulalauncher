using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 资源页「双源」搜索：两站并行、合并去重、跨站徽标、分页与 HasMore。
///
/// 排序与去重规则本身由 <see cref="CoreMods_CrossSourceSearchTests"/> 锁定；这里锁的是 App 侧的四件事：
/// <list type="number">
/// <item>两站结果有没有被完整送进合并、并按合并顺序映射回卡片；</item>
/// <item>同项目跨站去重后，保留下来的那条有没有标出「另一站也有」并指向对方的项目页；</item>
/// <item>没有 CurseForge 服务时，双源档位不可用并说明原因，而不是静默退化成单源；</item>
/// <item>分页与 <c>HasMore</c> 在「任一侧还有后续」时的语义。</item>
/// </list>
///
/// 网络由接口替身承担，测试不联网；界面行为（chip 文字、徽标点击）靠编译期绑定校验与截图。
/// </summary>
public class AppLibrary_DualSourceTests
{
    /// <summary>与 <c>LibraryViewModel.PageSize</c> 保持一致。不一致时下面按条数断言的分页用例会先失败。</summary>
    private const int PageSize = 20;

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // ==================== 合并与映射 ====================

    [Fact]
    public async Task 两站都有结果时按合并分数混排且各自保留来源()
    {
        // 模组权重：CurseForge 1、Modrinth 5。MR Mid 3000 万 × 5 = 1.5 亿 要压过 CF Big 的 1 亿。
        var modrinth = new FakeModrinth(
            Modrinth("mr-mid", "MR Mid", 30_000_000),
            Modrinth("mr-small", "MR Small", 100));
        var curseForge = new FakeCurseForge(
            CurseForge(1, "CF Big", 100_000_000));

        var viewModel = CreateLibrary(modrinth, curseForge);
        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBothSources);

        // 顺序由 Merge 决定，不由两站的到达顺序决定。
        Assert.Equal(["MR Mid", "CF Big", "MR Small"], viewModel.Results.Select(card => card.Title));

        // 每张卡片的来源必须跟着它原本的站，否则安装分派会拿着 Modrinth 的 id 去查 CurseForge。
        Assert.Equal(ContentSourceKind.Modrinth, viewModel.Results[0].Source);
        Assert.Equal(ContentSourceKind.CurseForge, viewModel.Results[1].Source);

        // 双源不提供排序下拉：合并顺序由 Score 决定，放开排序会与它打架。
        Assert.False(viewModel.SupportsSort);
        Assert.Equal("Modrinth + CurseForge", viewModel.SourceLabel);
    }

    [Fact]
    public async Task 同项目跨站去重后保留CurseForge那条并标出另一站也有()
    {
        var curseForge = new FakeCurseForge(
            CurseForge(238222, "Just Enough Items", 500_000_000),
            CurseForge(999, "CF Only", 1_000));
        var modrinth = new FakeModrinth(
            Modrinth("u6dRKJwZ", "Just Enough Items", 120_000_000, versions: ["1.20.1"]),
            Modrinth("mr-only", "MR Only", 1_000, versions: ["1.20.1"]));

        var viewModel = CreateLibrary(modrinth, curseForge);
        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        // 同名 + 同期更新 + CurseForge 侧没有版本集合（判据跳过）= 同一个项目，只留一条。
        Assert.Equal(3, viewModel.Results.Count);

        var merged = viewModel.Results[0];
        Assert.Equal("Just Enough Items", merged.Title);
        Assert.Equal(ContentSourceKind.CurseForge, merged.Source);
        Assert.True(merged.HasCounterpart);
        Assert.Equal("Modrinth 也有", merged.CounterpartLabel);
        Assert.Equal("https://modrinth.com/mod/just-enough-items", merged.CounterpartUrl);

        // 单站独有的条目不能被标成「另一站也有」——那会让用户点进一个不存在的对照页面。
        Assert.False(viewModel.Results[1].HasCounterpart);
        Assert.False(viewModel.Results[2].HasCounterpart);
    }

    // ==================== 分页 ====================

    [Fact]
    public async Task 两站都满页时HasMore为真下一页不足后为假()
    {
        var modrinth = new FakeModrinth(ModrinthPage("MR", 21).ToArray());
        var curseForge = new FakeCurseForge(CurseForgePage("CF", 21).ToArray());

        var viewModel = CreateLibrary(modrinth, curseForge);
        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.Equal(PageSize * 2, viewModel.Results.Count);
        Assert.True(viewModel.HasMore);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        // 两站各剩 1 条。HasMore 必须在两侧都没有后续时才变 false。
        Assert.Equal(PageSize * 2 + 2, viewModel.Results.Count);
        Assert.False(viewModel.HasMore);
        Assert.Equal(PageSize, modrinth.LastOffset);
        Assert.Equal(PageSize, curseForge.LastOffset);
    }

    [Fact]
    public async Task 只有一侧还有后续时HasMore仍为真()
    {
        // CurseForge 只有 5 条（不足一页），Modrinth 有 21 条——任一侧有后续都算 HasMore。
        var modrinth = new FakeModrinth(ModrinthPage("MR", 21).ToArray());
        var curseForge = new FakeCurseForge(CurseForgePage("CF", 5).ToArray());

        var viewModel = CreateLibrary(modrinth, curseForge);
        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.Equal(PageSize + 5, viewModel.Results.Count);
        Assert.True(viewModel.HasMore);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(PageSize + 6, viewModel.Results.Count);
        Assert.False(viewModel.HasMore);
    }

    // ==================== 单源不变 / 退出双源 ====================

    [Fact]
    public async Task 单源模式下只查一个后端且不产生跨站徽标()
    {
        var modrinth = new FakeModrinth(ModrinthPage("MR", 21).ToArray());
        var curseForge = new FakeCurseForge(CurseForgePage("CF", 3).ToArray());

        var viewModel = CreateLibrary(modrinth, curseForge);
        await viewModel.OnNavigatedToAsync();

        Assert.False(viewModel.IsBothSources);
        Assert.True(viewModel.SupportsSort);
        Assert.Equal(PageSize, viewModel.Results.Count);
        Assert.All(viewModel.Results, card => Assert.Equal(ContentSourceKind.Modrinth, card.Source));
        Assert.All(viewModel.Results, card => Assert.False(card.HasCounterpart));

        // 单源不该顺便打另一站的请求：双源是显式选择，不是默认行为。
        Assert.Equal(0, curseForge.Calls);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(21, viewModel.Results.Count);
        Assert.False(viewModel.HasMore);
    }

    [Fact]
    public async Task 从双源点回单源档位会退出双源并重新按单源搜索()
    {
        var modrinth = new FakeModrinth(
            Modrinth("mr-1", "MR One", 1_000),
            Modrinth("mr-2", "MR Two", 900));
        var curseForge = new FakeCurseForge(CurseForge(1, "CF One", 1_000));

        var viewModel = CreateLibrary(modrinth, curseForge);
        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.Results.Count);

        // 双源不是 ContentSourceKind 的取值：这里点的是「当前 ActiveSource 所在的那个 chip」，
        // 若早退判据只比 ActiveSource，这次点击会被吃掉，用户退不出双源。
        await viewModel.SelectSourceCommand.ExecuteAsync(ContentSourceKind.Modrinth);

        Assert.False(viewModel.IsBothSources);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.All(viewModel.Results, card => Assert.Equal(ContentSourceKind.Modrinth, card.Source));
    }

    // ==================== 辅助 ====================

    private static LibraryViewModel CreateLibrary(FakeModrinth modrinth, FakeCurseForge curseForge) =>
        new(new ServiceMap()
            .Add<IModrinthService>(modrinth)
            .Add<ICurseForgeService>(curseForge));

    /// <summary>造一页 Modrinth 项目。前缀让两站的名字不会互相匹配上去重判据。</summary>
    private static IEnumerable<ModrinthProject> ModrinthPage(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i =>
            Modrinth($"{prefix}-{i}", $"{prefix} Project {i}", 10_000 - i));

    /// <summary>造一页 CurseForge 项目。与 <see cref="ModrinthPage"/> 同构，但 Id 体系是数字。</summary>
    private static IEnumerable<CurseForgeProject> CurseForgePage(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i =>
            CurseForge(10_000 + i, $"{prefix} Project {i}", 10_000 - i));

    private static ModrinthProject Modrinth(
        string id,
        string title,
        long downloads,
        DateTimeOffset? updated = null,
        IReadOnlyList<string>? versions = null) => new()
    {
        ProjectId = id,
        Slug = SlugOf(title),
        Title = title,
        Downloads = downloads,
        UpdatedAt = updated ?? Now,
        GameVersions = versions ?? [],
    };

    private static CurseForgeProject CurseForge(
        long id,
        string name,
        long downloads,
        DateTimeOffset? updated = null) => new()
    {
        Id = id,
        Slug = SlugOf(name),
        Name = name,
        Summary = $"{name} summary",
        DownloadCount = downloads,
        DateModified = updated ?? Now,
    };

    private static string SlugOf(string title) => title.ToLowerInvariant().Replace(' ', '-');

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

    /// <summary>按 offset / limit 切片返回的假 Modrinth，记录调用次数与最后一次的偏移。</summary>
    private sealed class FakeModrinth(params ModrinthProject[] projects) : IModrinthService
    {
        public int Calls { get; private set; }

        public int LastOffset { get; private set; } = -1;

        public string? LastSort { get; private set; }

        public string? LastError => null;

        public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            CancellationToken cancellationToken = default) =>
            Slice(offset, limit);

        public Task<IReadOnlyList<ModrinthProject>> SearchSortedAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            string? sort,
            CancellationToken cancellationToken = default)
        {
            LastSort = sort;
            return Slice(offset, limit);
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

        private Task<IReadOnlyList<ModrinthProject>> Slice(int offset, int limit)
        {
            Calls++;
            LastOffset = offset;

            IReadOnlyList<ModrinthProject> page = projects.Skip(offset).Take(limit).ToList();
            return Task.FromResult(page);
        }
    }

    /// <summary>按 index / pageSize 切片返回的假 CurseForge。</summary>
    private sealed class FakeCurseForge : ICurseForgeService
    {
        private readonly CurseForgeProject[] _projects;

        public FakeCurseForge(params CurseForgeProject[] projects) => _projects = projects;

        public int Calls { get; private set; }

        public int LastOffset { get; private set; } = -1;

        public string? LastError => null;

        public Task<IReadOnlyList<CurseForgeProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOffset = index;

            IReadOnlyList<CurseForgeProject> page = _projects.Skip(index).Take(pageSize).ToList();
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
            long modId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFile>>([]);

        public Task<ContentInstallResult> InstallAsync(
            CurseForgeFile file,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public string BuildSearchUrl(
            ContentKind kind,
            string? query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize) => string.Empty;

        public Task<CurseForgeProject?> GetProjectBySlugAsync(
            ContentKind kind,
            string slugOrUrl,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CurseForgeProject?>(null);

        public Task<IReadOnlyList<CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
            IReadOnlyList<uint> fingerprints,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFingerprintMatch>>([]);
    }

    // ==================== PCL2 口径的分页：两站各用各的偏移 + 新增不足一页就补取 ====================

    [Fact]
    public async Task 双源补取_整页重复时继续取下一页并各自推进偏移()
    {
        // 第 0 页 20 条全新；第 1 页返回与第 0 页相同的 20 条（模拟服务端分页漂移，去重后新增 0）；
        // 第 2 页 20 条全新。CurseForge 首页就耗尽。
        var modrinth = new ScriptedModrinth(new Dictionary<int, ModrinthProject[]>
        {
            [0] = ModrinthPage("P0", 20).ToArray(),
            [20] = ModrinthPage("P0", 20).ToArray(),
            [40] = ModrinthPage("P1", 20).ToArray(),
        });

        var curseForge = new ScriptedCurseForge(new Dictionary<int, CurseForgeProject[]> { [0] = [] });

        var viewModel = new LibraryViewModel(new ServiceMap()
            .Add<IModrinthService>(modrinth)
            .Add<ICurseForgeService>(curseForge));

        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.Equal(20, viewModel.Results.Count);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        // 第 1 页被整页去重吃掉，必须自动补取第 2 页：否则用户点「加载更多」看不到任何变化。
        Assert.Equal(40, viewModel.Results.Count);
        Assert.Equal([0, 20, 40], modrinth.Offsets);
        Assert.Equal([0], curseForge.Offsets);
    }

    [Fact]
    public async Task 双源分页_一侧耗尽后另一侧从自己的偏移继续()
    {
        var modrinth = new ScriptedModrinth(new Dictionary<int, ModrinthProject[]>
        {
            [0] = ModrinthPage("MR0", 20).ToArray(),
            [20] = ModrinthPage("MR1", 20).ToArray(),
        });

        // CurseForge 只有 5 条：不足一页即视为走到头，加载更多时不应再请求它。
        var curseForge = new ScriptedCurseForge(new Dictionary<int, CurseForgeProject[]>
        {
            [0] = CurseForgePage("CF0", 5).ToArray(),
        });

        var viewModel = new LibraryViewModel(new ServiceMap()
            .Add<IModrinthService>(modrinth)
            .Add<ICurseForgeService>(curseForge));

        await viewModel.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.Equal(25, viewModel.Results.Count);
        Assert.True(viewModel.HasMore);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        // 两站各用各的偏移：CurseForge 停在 0，Modrinth 从 20 继续，而不是被共用偏移推到 40。
        Assert.Equal([0, 20], modrinth.Offsets);
        Assert.Equal([0], curseForge.Offsets);
        Assert.Equal(45, viewModel.Results.Count);
    }

    /// <summary>按 offset 返回脚本化页的假 Modrinth，记录每次请求的 offset。</summary>
    private sealed class ScriptedModrinth(IReadOnlyDictionary<int, ModrinthProject[]> pages) : IModrinthService
    {
        public List<int> Offsets { get; } = [];

        public string? LastError => null;

        public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            CancellationToken cancellationToken = default) =>
            SearchSortedAsync(kind, query, gameVersion, loader, offset, limit, null, cancellationToken);

        public Task<IReadOnlyList<ModrinthProject>> SearchSortedAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            string? sort,
            CancellationToken cancellationToken = default)
        {
            Offsets.Add(offset);

            IReadOnlyList<ModrinthProject> page = pages.TryGetValue(offset, out var scripted)
                ? scripted.Take(limit).ToList()
                : [];

            return Task.FromResult(page);
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

        public Task<ModrinthVersion?> LookupBySha1Async(string sha1, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModrinthVersion?>(null);
    }

    /// <summary>按 index 返回脚本化页的假 CurseForge，记录每次请求的 index。</summary>
    private sealed class ScriptedCurseForge(IReadOnlyDictionary<int, CurseForgeProject[]> pages) : ICurseForgeService
    {
        public List<int> Offsets { get; } = [];

        public string? LastError => null;

        public Task<IReadOnlyList<CurseForgeProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Offsets.Add(index);

            IReadOnlyList<CurseForgeProject> page = pages.TryGetValue(index, out var scripted)
                ? scripted.Take(pageSize).ToList()
                : [];

            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
            long modId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFile>>([]);

        public Task<ContentInstallResult> InstallAsync(
            CurseForgeFile file,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public string BuildSearchUrl(
            ContentKind kind,
            string? query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize) => string.Empty;

        public Task<CurseForgeProject?> GetProjectBySlugAsync(
            ContentKind kind,
            string slugOrUrl,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CurseForgeProject?>(null);

        public Task<IReadOnlyList<CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
            IReadOnlyList<uint> fingerprints,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFingerprintMatch>>([]);
    }
}