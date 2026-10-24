using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 首页导出崩溃报告时的 mods 收集（<see cref="HomeViewModel.CollectModsAsync"/>）。
///
/// 这段逻辑依赖 <see cref="IInstanceContext"/>，所以留在 App 层；App 已对测试程序集开放 internal。
/// 用例全部在临时目录里真实建文件，覆盖启用/禁用、大小、排序、空目录、路径异常、
/// 子目录过滤与内容服务异常。
/// </summary>
public class AppHome_ModsCollectionTests
{
    [Fact]
    public async Task 收集mods_启用禁用大小与文件名排序都正确()
    {
        using var temp = new TempDirectory();
        var modsDirectory = CreateModsDirectory(temp);
        WriteFile(modsDirectory, "zeta.jar", 10);
        WriteFile(modsDirectory, "alpha.jar.disabled", 20);
        WriteFile(modsDirectory, "beta.jar", 30);

        var (mods, error) = await CreateViewModel(temp).CollectModsAsync();

        Assert.Null(error);
        Assert.Collection(
            mods,
            item =>
            {
                Assert.Equal("alpha.jar.disabled", item.FileName);
                Assert.False(item.Enabled);
                Assert.Equal(20, item.SizeBytes);
                Assert.Null(item.VersionText);
            },
            item =>
            {
                Assert.Equal("beta.jar", item.FileName);
                Assert.True(item.Enabled);
                Assert.Equal(30, item.SizeBytes);
            },
            item =>
            {
                Assert.Equal("zeta.jar", item.FileName);
                Assert.True(item.Enabled);
                Assert.Equal(10, item.SizeBytes);
            });
    }

    [Fact]
    public async Task 收集mods_目录不存在时为空且不是错误()
    {
        using var temp = new TempDirectory();

        // 实例目录存在，但没有 mods 子目录。
        var (mods, error) = await CreateViewModel(temp).CollectModsAsync();

        Assert.Empty(mods);
        Assert.Null(error);
    }

    [Fact]
    public async Task 收集mods_路径是文件时返回错误而不是抛异常()
    {
        using var temp = new TempDirectory();
        var gameDirectory = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, "mods"), "not a directory");

        var (mods, error) = await CreateViewModel(temp).CollectModsAsync();

        Assert.Empty(mods);
        Assert.NotNull(error);
        Assert.Contains("不是目录", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 收集mods_忽略子目录只收文件()
    {
        using var temp = new TempDirectory();
        var modsDirectory = CreateModsDirectory(temp);
        WriteFile(modsDirectory, "top.jar", 1);

        var subDirectory = Path.Combine(modsDirectory, "子目录");
        Directory.CreateDirectory(subDirectory);
        WriteFile(subDirectory, "nested.jar", 2);

        var (mods, error) = await CreateViewModel(temp).CollectModsAsync();

        Assert.Null(error);
        var item = Assert.Single(mods);
        Assert.Equal("top.jar", item.FileName);
    }

    [Fact]
    public async Task 收集mods_内容服务异常时返回错误而不是抛异常()
    {
        using var temp = new TempDirectory();
        CreateModsDirectory(temp);

        var viewModel = new HomeViewModel(
            contentService: new ThrowingContentService(),
            instanceContext: new FakeInstanceContext(temp.Combine("game")));

        var (mods, error) = await viewModel.CollectModsAsync();

        Assert.Empty(mods);
        Assert.NotNull(error);
        Assert.Contains("读取 mods 目录失败", error, StringComparison.Ordinal);
        Assert.Contains("拒绝访问", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 收集mods_优先采用内容服务的版本号并排序()
    {
        using var temp = new TempDirectory();
        CreateModsDirectory(temp);

        var viewModel = new HomeViewModel(
            contentService: new FixedContentService(
            [
                MakeItem("zeta.jar", enabled: true, size: 300, version: "2.0.0"),
                MakeItem("alpha.jar", enabled: true, size: 100, version: "1.0.0"),
                MakeItem("beta.jar.disabled", enabled: false, size: 200, version: string.Empty),
            ]),
            instanceContext: new FakeInstanceContext(temp.Combine("game")));

        var (mods, error) = await viewModel.CollectModsAsync();

        Assert.Null(error);
        Assert.Collection(
            mods,
            item =>
            {
                Assert.Equal("alpha.jar", item.FileName);
                Assert.Equal("1.0.0", item.VersionText);
            },
            item =>
            {
                Assert.Equal("beta.jar.disabled", item.FileName);
                Assert.False(item.Enabled);
                Assert.Null(item.VersionText);
                Assert.Equal(200, item.SizeBytes);
            },
            item =>
            {
                Assert.Equal("zeta.jar", item.FileName);
                Assert.Equal("2.0.0", item.VersionText);
                Assert.Equal(300, item.SizeBytes);
            });
    }

    [Fact]
    public async Task 收集mods_实例上下文缺失时返回错误()
    {
        var viewModel = new HomeViewModel();

        var (mods, error) = await viewModel.CollectModsAsync();

        Assert.Empty(mods);
        Assert.NotNull(error);
        Assert.Contains("实例上下文尚未就绪", error, StringComparison.Ordinal);
    }

    // ==================== 辅助 ====================

    private static HomeViewModel CreateViewModel(TempDirectory temp) =>
        new(instanceContext: new FakeInstanceContext(temp.Combine("game")));

    private static string CreateModsDirectory(TempDirectory temp)
    {
        var gameDirectory = temp.Combine("game");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        return modsDirectory;
    }

    private static void WriteFile(string directory, string name, int size)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private static ContentItem MakeItem(string fileName, bool enabled, long size, string version) =>
        new()
        {
            FilePath = Path.Combine(Path.GetTempPath(), fileName),
            FileName = fileName,
            Kind = ContentKind.Mod,
            Version = version,
            FileSize = size,
            IsEnabled = enabled,
        };

    /// <summary>只提供实例目录的 <see cref="IInstanceContext"/>；其余成员不会被测试触达。</summary>
    private sealed class FakeInstanceContext(string gameDirectory) : IInstanceContext
    {
        public InstanceInfo? Current => null;

        public string GameDirectory { get; } = gameDirectory;

        public string? VersionName => null;

        public LoaderKind Loader => LoaderKind.Vanilla;

        public string? MinecraftVersion => "1.20.1";

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentTarget>>(Array.Empty<ContentTarget>());

        public Task<ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentTarget?>(null);

        public Task<InstanceResolution> ResolveLaunchTargetAsync(
            string versionName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceResolution(GameDirectory, null, Isolated: false, Created: false));
    }

    /// <summary>扫描时直接抛异常的 <see cref="IContentService"/>，用来验证 HomeViewModel 的兜底文案。</summary>
    private sealed class ThrowingContentService : IContentService
    {
        public Task<IReadOnlyList<ContentItem>> ScanAsync(
            string gameDirectory,
            ContentKind kind,
            CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("拒绝访问 mods 目录");

        public Task SetEnabledAsync(ContentItem item, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ContentItem item, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ImportAsync(
            string gameDirectory,
            ContentKind kind,
            IReadOnlyList<string> sourceFiles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<ModConflict> DetectConflicts(IReadOnlyList<ContentItem> mods) =>
            Array.Empty<ModConflict>();

        public IReadOnlyList<ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<ContentItem> mods) =>
            Array.Empty<ModDependencyIssue>();

        public IReadOnlyList<ContentItem> FindVersionMismatch(
            IReadOnlyList<ContentItem> mods,
            string minecraftVersion) =>
            Array.Empty<ContentItem>();
    }

    /// <summary>返回固定扫描结果的 <see cref="IContentService"/>。</summary>
    private sealed class FixedContentService(IReadOnlyList<ContentItem> items) : IContentService
    {
        public Task<IReadOnlyList<ContentItem>> ScanAsync(
            string gameDirectory,
            ContentKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(items);

        public Task SetEnabledAsync(ContentItem item, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ContentItem item, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ImportAsync(
            string gameDirectory,
            ContentKind kind,
            IReadOnlyList<string> sourceFiles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<ModConflict> DetectConflicts(IReadOnlyList<ContentItem> mods) =>
            Array.Empty<ModConflict>();

        public IReadOnlyList<ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<ContentItem> mods) =>
            Array.Empty<ModDependencyIssue>();

        public IReadOnlyList<ContentItem> FindVersionMismatch(
            IReadOnlyList<ContentItem> mods,
            string minecraftVersion) =>
            Array.Empty<ContentItem>();
    }
}
