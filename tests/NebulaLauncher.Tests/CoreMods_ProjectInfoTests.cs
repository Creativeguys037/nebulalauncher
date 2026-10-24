using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 在线项目信息补全：哈希反查 → 项目名 / slug 摘要 / 中文名 / 图标 → 6 小时缓存。
///
/// 不碰网络：两个在线数据源都是替身，验证的是「组合逻辑与缓存语义」，
/// 真实接口的行为由 <c>CoreAssets_ModrinthTests</c> / <c>CoreAssets_CurseForgeTests</c> 覆盖。
/// </summary>
public class CoreMods_ProjectInfoTests
{
    [Fact]
    public async Task 补全_Modrinth命中时给出中文名与项目名()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-enrich");
        var items = await CreateModAsync(paths, gameDirectory, "ic2.jar", """{"id":"ic2","name":"IC2","version":"1.0"}""");

        var iconFile = Path.Combine(paths.RootDirectory, "icon.png");
        await File.WriteAllBytesAsync(iconFile, [0x89, 0x50, 0x4E, 0x47]);

        var modrinth = new FakeModrinth
        {
            Project = new ModrinthProject
            {
                ProjectId = "P1",
                Slug = "industrial-craft",
                Title = "Industrial Craft 2",
                Description = "工业模组",
                IconUrl = "https://example.invalid/ic2.png",
            },
            IconPath = iconFile,
        };

        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var result = await service.EnrichAsync(items, gameDirectory);

        var info = Assert.Single(result.Items);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Matched);
        Assert.True(info.Identified);
        Assert.Equal(ContentSourceKind.Modrinth, info.Source);
        Assert.Equal("industrial-craft", info.Slug);
        Assert.Equal("Industrial Craft 2", info.Title);

        // 译名来自嵌入的 MC 百科表（slug → 中文名），不是从网络拿的。
        Assert.Equal("工业时代2 (Industrial Craft 2)", info.ChineseName);
        Assert.Equal(iconFile, info.IconPath);
        Assert.Equal("https://modrinth.com/mod/industrial-craft", info.WebUrl);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 补全_第二次命中缓存不再发请求()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-cache");
        var items = await CreateModAsync(paths, gameDirectory, "sodium.jar", """{"id":"sodium","name":"Sodium","version":"0.5.8"}""");

        var modrinth = new FakeModrinth
        {
            Project = new ModrinthProject
            {
                ProjectId = "P2",
                Slug = "sodium",
                Title = "Sodium",
                Description = "渲染优化",
            },
        };

        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var first = await service.EnrichAsync(items, gameDirectory);
        Assert.True(Assert.Single(first.Items).Identified);
        Assert.Equal(1, modrinth.BatchCalls);

        var cacheFile = Path.Combine(paths.CacheDirectory, ModProjectInfoService.CacheFileName);
        Assert.True(File.Exists(cacheFile), "补全结果必须落盘，否则每次进页面都会重新请求");

        // 第二次：同一批文件、同一份缓存目录，应当完全走缓存。
        var second = await service.EnrichAsync(items, gameDirectory);
        var cached = Assert.Single(second.Items);

        Assert.Equal(1, modrinth.BatchCalls);
        Assert.Equal("sodium", cached.Slug);
        Assert.True(cached.Identified);
    }

    [Fact]
    public async Task 补全_CurseForge命中时用CF译名()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-cf");
        var items = await CreateModAsync(paths, gameDirectory, "forestry.jar", """{"id":"forestry","name":"Forestry","version":"5.8"}""");

        var curseForge = new FakeCurseForge
        {
            Project = new CurseForgeProject
            {
                Id = 123,
                Slug = "forestry",
                Name = "Forestry",
                Summary = "林业模组",
            },
        };

        var service = new ModProjectInfoService(modrinth: null, curseForge, paths, new CoreAssetsLog());

        var result = await service.EnrichAsync(items, gameDirectory);

        var info = Assert.Single(result.Items);
        Assert.Equal(ContentSourceKind.CurseForge, info.Source);
        Assert.Equal("forestry", info.Slug);
        Assert.Equal("林业 (Forestry)", info.ChineseName);
    }

    [Fact]
    public async Task 补全_两个数据源都没收录时不算失败()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-unknown");
        var items = await CreateModAsync(paths, gameDirectory, "handmade.jar", """{"id":"handmade","name":"Handmade","version":"1"}""");

        var modrinth = new FakeModrinth { Project = null };
        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var result = await service.EnrichAsync(items, gameDirectory);

        var info = Assert.Single(result.Items);
        Assert.False(info.Identified);
        Assert.Null(info.Source);
        Assert.Null(info.ChineseName);
        Assert.Equal(0, result.Matched);

        // 「未收录」是正常结果，不是错误：卡片仍要显示本地元数据。
        Assert.Null(result.Error);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 补全_无内嵌图标的资源包光影包也走哈希反查()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-kind");
        var resourcePackDirectory = Path.Combine(gameDirectory, "resourcepacks");
        var shaderPackDirectory = Path.Combine(gameDirectory, "shaderpacks");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(resourcePackDirectory, "pack.zip"),
            ("pack.mcmeta", """{"pack":{"pack_format":15,"description":"资源包"}}"""));

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(shaderPackDirectory, "complementary.zip"),
            ("shaders/shader.properties", "name=Complementary\nversion=1.0"));

        var content = new ContentService(paths);
        var items = new List<ContentItem>();
        items.AddRange(await content.ScanAsync(gameDirectory, ContentKind.ResourcePack));
        items.AddRange(await content.ScanAsync(gameDirectory, ContentKind.ShaderPack));

        Assert.Equal(2, items.Count);

        var modrinth = new FakeModrinth
        {
            Project = new ModrinthProject
            {
                ProjectId = "P9",
                Slug = "complementary-reimagined",
                Title = "Complementary Reimagined",
            },
        };

        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var result = await service.EnrichAsync(items, gameDirectory);

        // 资源站对资源包 / 光影包都有项目：图标只能从这条链路来（包里通常没有 pack.png）。
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Matched);
        Assert.Equal(1, modrinth.BatchCalls);
    }

    [Fact]
    public async Task 补全_已有内嵌图标的资源包不再请求网络()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-kind-icon");
        var resourcePackDirectory = Path.Combine(gameDirectory, "resourcepacks");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(resourcePackDirectory, "with-icon.zip"),
            ("pack.mcmeta", Encoding.UTF8.GetBytes("""{"pack":{"pack_format":15,"description":"自带图标的资源包"}}""")),
            ("pack.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D }));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.ResourcePack);
        var item = Assert.Single(items);
        Assert.NotNull(item.IconPath);

        var modrinth = new FakeModrinth();
        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var result = await service.EnrichAsync(items, gameDirectory);

        // 包里已经有 pack.png：再为它算两遍哈希（资源包动辄几十 MB）并打一轮网络请求不划算。
        Assert.Empty(result.Items);
        Assert.Equal(0, modrinth.BatchCalls);
        Assert.Equal(0, modrinth.ProjectCalls);
    }

    [Fact]
    public async Task 补全_跳过目录形态的数据包()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-dir-datapack");
        var directoryForm = Path.Combine(gameDirectory, "datapacks", "unzipped");
        Directory.CreateDirectory(directoryForm);
        await File.WriteAllTextAsync(
            Path.Combine(directoryForm, "pack.mcmeta"),
            """{"pack":{"pack_format":48,"description":"目录数据包"}}""");

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.DataPack);
        Assert.Single(items);

        var modrinth = new FakeModrinth();
        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());

        var result = await service.EnrichAsync(items, gameDirectory);

        // 目录算不出哈希：不能产生一条「未识别」的空结果，也不该发请求。
        Assert.Empty(result.Items);
        Assert.Equal(0, modrinth.BatchCalls);
    }

    [Fact]
    public async Task 补全_缓存过期后重新查询()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-expire");
        var items = await CreateModAsync(paths, gameDirectory, "iris.jar", """{"id":"iris","name":"Iris","version":"1.7"}""");

        var modrinth = new FakeModrinth
        {
            Project = new ModrinthProject
            {
                ProjectId = "P3",
                Slug = "iris",
                Title = "Iris Shaders",
            },
        };

        var service = new ModProjectInfoService(modrinth, curseForge: null, paths, new CoreAssetsLog());
        await service.EnrichAsync(items, gameDirectory);
        Assert.Equal(1, modrinth.BatchCalls);

        // 把落盘缓存的时间戳推到 7 小时前：超过 6 小时有效期后必须重新查询，
        // 而不是被「每次打开页面续期」永久钉死。
        var cacheFile = Path.Combine(paths.CacheDirectory, ModProjectInfoService.CacheFileName);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(cacheFile));

        Assert.NotNull(node);
        var entries = node!["Entries"]!.AsObject();
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            entry.Value!["StoredAt"] = DateTimeOffset.UtcNow.AddHours(-7);
        }

        await File.WriteAllTextAsync(cacheFile, node.ToJsonString());

        var refreshed = await service.EnrichAsync(items, gameDirectory);

        Assert.Equal(2, modrinth.BatchCalls);
        Assert.True(Assert.Single(refreshed.Items).Identified);
    }

    private static async Task<IReadOnlyList<ContentItem>> CreateModAsync(
        CoreAssetsPaths paths,
        string gameDirectory,
        string fileName,
        string fabricJson)
    {
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        CoreAssets_ContentTests.CreateJar(Path.Combine(modsDirectory, fileName), ("fabric.mod.json", fabricJson));

        return await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);
    }

    private sealed class FakeModrinth : IModrinthService
    {
        public string? LastError => null;

        /// <summary>反查要返回的项目；null 表示「两个源都没收录」。</summary>
        public ModrinthProject? Project { get; init; }

        /// <summary><see cref="CacheIconAsync"/> 的返回值。</summary>
        public string? IconPath { get; init; }

        public int BatchCalls { get; private set; }

        public int ProjectCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, ModrinthVersion>> LookupBySha1BatchAsync(
            IReadOnlyList<string> sha1s,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;

            var results = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);

            if (Project is not null)
            {
                foreach (var sha1 in sha1s)
                {
                    results[sha1] = new ModrinthVersion
                    {
                        Id = "version-1",
                        ProjectId = Project.ProjectId,
                        Name = "1.0",
                        VersionNumber = "1.0",
                        FileName = "mod.jar",
                        DownloadUrl = "https://example.invalid/mod.jar",
                    };
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(results);
        }

        public Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(
            IReadOnlyList<string> projectIds,
            CancellationToken cancellationToken = default)
        {
            ProjectCalls++;

            IReadOnlyList<ModrinthProject> results = Project is null ? [] : [Project];
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModrinthProject>>([]);

        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
            string projectId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModrinthVersion>>([]);

        public Task<ModrinthInstallResult> InstallAsync(
            ModrinthVersion version,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModrinthInstallResult { Success = false, Error = "测试替身不下载" });

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(IconPath);

        public Task<ModrinthVersion?> LookupBySha1Async(string sha1, CancellationToken cancellationToken = default)
            => Task.FromResult<ModrinthVersion?>(null);
    }

    private sealed class FakeCurseForge : ICurseForgeService
    {
        public string? LastError => null;

        public CurseForgeProject? Project { get; init; }

        public Task<IReadOnlyList<CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
            IReadOnlyList<uint> fingerprints,
            CancellationToken cancellationToken = default)
        {
            if (Project is null)
            {
                return Task.FromResult<IReadOnlyList<CurseForgeFingerprintMatch>>([]);
            }

            IReadOnlyList<CurseForgeFingerprintMatch> matches =
            [
                new CurseForgeFingerprintMatch
                {
                    FileId = 1,
                    ModId = Project.Id,
                    FileName = "forestry.jar",
                    Fingerprint = fingerprints.Count > 0 ? fingerprints[0] : 0,
                },
            ];

            return Task.FromResult(matches);
        }

        public Task<IReadOnlyList<CurseForgeProject>> GetProjectsAsync(
            IReadOnlyList<long> modIds,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CurseForgeProject> results = Project is null ? [] : [Project];
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<CurseForgeProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CurseForgeProject>>([]);

        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
            long modId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CurseForgeFile>>([]);

        public Task<ContentInstallResult> InstallAsync(
            CurseForgeFile file,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ContentInstallResult.Fail("测试替身不下载"));

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public string BuildSearchUrl(
            ContentKind kind,
            string? query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize)
            => string.Empty;

        public Task<CurseForgeProject?> GetProjectBySlugAsync(
            ContentKind kind,
            string slugOrUrl,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CurseForgeProject?>(null);
    }
}
