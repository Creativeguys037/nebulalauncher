using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// MCBBS 整合包 <c>addons</c> 的解析与逐项反查。
///
/// 只测启动器自己的判据，不测网络：
/// <list type="bullet">
/// <item>解析部分直接喂 JSON 文本（<see cref="McbbsAddonParser"/> 是纯函数），
///       覆盖字段缺失、单条、多条与未知格式。</item>
/// <item>反查部分用接口替身表示「CurseForge / Modrinth 查得到 / 查不到」，
///       下载用固定内容的 <see cref="HttpMessageHandler"/>，全程不联网。</item>
/// </list>
/// </summary>
public class CoreAssets_McbbsAddonTests
{
    private const string ManifestWithSingleAddon = """
    {
      "name": "MCBBS 测试包",
      "version": "1.0",
      "minecraft": { "version": "1.12.2", "modLoaders": [ { "id": "forge-14.23.5.2860", "primary": true } ] },
      "addons": [
        { "projectID": 238222, "fileID": 3835743, "fileName": "jei.jar" }
      ]
    }
    """;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "addons": null }""")]
    [InlineData("""{ "addons": "oops" }""")]
    [InlineData("""{ "name": "只有名字" }""")]
    public void 字段缺失或未知格式时解析出空集合(string json)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = McbbsAddonParser.Parse(document.RootElement);

        Assert.Empty(parsed);
    }

    [Fact]
    public void 单个addon解析出项目文件与文件名()
    {
        using var document = JsonDocument.Parse(ManifestWithSingleAddon);

        var reference = Assert.Single(McbbsAddonParser.Parse(document.RootElement));

        // 数字 id 在 addons 里是 JSON number，解析后统一成字符串。
        Assert.Equal("238222", reference.ProjectId);
        Assert.Equal("3835743", reference.FileId);
        Assert.Equal("jei.jar", reference.FileName);
        Assert.Equal(ContentKind.Mod, reference.Kind);
        Assert.Equal(ModpackAddonSource.Auto, reference.Source);
        Assert.True(reference.HasNumericProjectId);
        Assert.Null(reference.TargetPath);
    }

    [Fact]
    public void 多个addon里跳过加载器声明与无项目标识的条目()
    {
        const string json = """
        {
          "addons": [
            { "id": "game", "version": "1.12.2" },
            { "id": "forge", "version": "14.23.5.2860" },
            { "projectID": "238222", "fileID": "3835743", "fileName": "jei.jar" },
            { "projectID": "modrinth:abc123", "fileID": "v41", "path": "resourcepacks/pack.zip" },
            { "version": "1.0" },
            "not-an-object"
          ]
        }
        """;

        using var document = JsonDocument.Parse(json);

        var parsed = McbbsAddonParser.Parse(document.RootElement);

        Assert.Equal(2, parsed.Count);

        Assert.Equal("238222", parsed[0].ProjectId);
        Assert.Equal(ContentKind.Mod, parsed[0].Kind);

        // id 带来源前缀、靠 path 推出内容类型：两条宽容解析都要成立。
        Assert.Equal("abc123", parsed[1].ProjectId);
        Assert.Equal(ModpackAddonSource.Modrinth, parsed[1].Source);
        Assert.Equal(ContentKind.ResourcePack, parsed[1].Kind);
        Assert.Equal("resourcepacks/pack.zip", parsed[1].TargetPath);
    }

    [Fact]
    public async Task addon没有声明文件名时回退到CurseForge返回的远程文件名()
    {
        const string json = """
        {
          "name": "MCBBS 无文件名",
          "version": "1.0",
          "minecraft": { "version": "1.12.2", "modLoaders": [ { "id": "forge-14.23.5.2860", "primary": true } ] },
          "addons": [ { "projectID": 238222, "fileID": 3835743 } ]
        }
        """;

        var curseForge = new FakeCurseForgeService
        {
            Files =
            [
                new CurseForgeFile
                {
                    Id = 3835743,
                    ModId = 238222,
                    FileName = "jei-1.12.2-4.16.1.302.jar",
                    DownloadUrl = "https://example.invalid/jei.jar",
                    FileLength = 3,
                },
            ],
        };

        using var env = new AddonEnvironment(curseForge: curseForge);
        var archive = env.CreateZip("nofilename.zip", json);

        var directory = await env.Service.ImportAsync(archive, "无文件名实例");
        var result = env.Service.LastImport;

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);
        Assert.Equal(1, result.AddonsTotal);
        Assert.Equal(1, result.AddonsDownloaded);
        Assert.False(result.HasPendingAddons);

        // 落点用远程文件名，不是空的 "mods/"。
        Assert.True(File.Exists(Path.Combine(directory, "mods", "jei-1.12.2-4.16.1.302.jar")));
    }

    [Fact]
    public async Task CurseForge标准files清单也下载模组文件()
    {
        const string manifest = """
        {
          "name": "CurseForge 标准包",
          "minecraft": { "version": "1.12.2", "modLoaders": [ { "id": "forge-14.23.5.2860", "primary": true } ] },
          "files": [ { "projectID": 238222, "fileID": 3835743, "required": true } ]
        }
        """;
        var curseForge = new FakeCurseForgeService
        {
            Files =
            [
                new CurseForgeFile
                {
                    Id = 3835743,
                    ModId = 238222,
                    FileName = "jei-standard.jar",
                    DownloadUrl = "https://example.invalid/jei-standard.jar",
                    FileLength = 3,
                },
            ],
        };
        using var environment = new AddonEnvironment(curseForge: curseForge);
        var archive = environment.CreateZip("standard-curseforge.zip", manifest);

        var directory = await environment.Service.ImportAsync(archive, "CurseForge 标准实例");
        var result = environment.Service.LastImport!;

        Assert.Equal(ModpackFormat.CurseForgeZip, result.Format);
        Assert.Equal(1, result.AddonsTotal);
        Assert.Equal(1, result.AddonsDownloaded);
        Assert.Empty(result.PendingAddons);
        Assert.Equal("jar", await File.ReadAllTextAsync(Path.Combine(directory, "mods", "jei-standard.jar")));
    }

    [Fact]
    public async Task 两家都查不到时全部进待手动安装清单且导入仍然成功()
    {
        const string json = """
        {
          "name": "MCBBS 查不到",
          "version": "1.0",
          "minecraft": { "version": "1.12.2", "modLoaders": [ { "id": "forge-14.23.5.2860", "primary": true } ] },
          "addons": [
            { "projectID": 238222, "fileID": 3835743 },
            { "id": "some-mod", "version": "abc" }
          ]
        }
        """;

        // CurseForge 返回空文件列表 + Modrinth 返回空：两条都反查不到。
        using var env = new AddonEnvironment(curseForge: new FakeCurseForgeService(), modrinth: new FakeModrinthService());
        var archive = env.CreateZip("unknown.zip", json);

        var directory = await env.Service.ImportAsync(archive, "查不到实例");
        var result = env.Service.LastImport;

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);
        Assert.Equal(2, result.AddonsTotal);
        Assert.Equal(0, result.AddonsDownloaded);
        Assert.Equal(2, result.AddonsPending);
        Assert.True(result.HasPendingAddons);

        // 前几条名字要能拼成一行文案：这是 WorldsViewModel 的「N 个 mod 需要手动安装」来源。
        Assert.Contains("238222", result.PendingAddonsPreview);
        Assert.Contains("some-mod", result.PendingAddonsPreview);

        // 失败条目必须带可点的网页入口，否则清单只是个死列表。
        Assert.All(result.PendingAddons, item => Assert.False(string.IsNullOrWhiteSpace(item.WebUrl)));
        Assert.Contains(result.PendingAddons, item => item.WebUrl!.Contains("curseforge", StringComparison.Ordinal));
        Assert.Contains(result.PendingAddons, item => item.WebUrl!.Contains("modrinth", StringComparison.Ordinal));

        // 实例目录仍可用：导入结果必须是成功，只是少几个 mod。
        Assert.True(File.Exists(Path.Combine(directory, "instance.json")));
    }

    [Fact]
    public async Task CurseForge查不到时用Modrinth兜底并把文件落到mods()
    {
        const string json = """
        {
          "name": "MCBBS 走 Modrinth",
          "version": "1.0",
          "minecraft": { "version": "1.12.2", "modLoaders": [ { "id": "forge-14.23.5.2860", "primary": true } ] },
          "addons": [ { "projectID": "jei", "version": "v41" } ]
        }
        """;

        var modrinth = new FakeModrinthService
        {
            Versions =
            [
                new ModrinthVersion
                {
                    Id = "v41",
                    ProjectId = "jei",
                    Name = "JEI 4.16",
                    VersionNumber = "4.16.1.302",
                    FileName = "jei-from-modrinth.jar",
                    DownloadUrl = "https://example.invalid/modrinth/jei.jar",
                    FileSize = 3,
                },
            ],
        };

        using var env = new AddonEnvironment(curseForge: new FakeCurseForgeService(), modrinth: modrinth);
        var archive = env.CreateZip("modrinth.zip", json);

        var directory = await env.Service.ImportAsync(archive, "Modrinth 实例");
        var result = env.Service.LastImport;

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);
        Assert.Equal(1, result.AddonsDownloaded);
        Assert.False(result.HasPendingAddons);

        // addon 没声明 fileName，用 Modrinth 版本里的远程文件名。
        Assert.True(File.Exists(Path.Combine(directory, "mods", "jei-from-modrinth.jar")));
    }

    /// <summary>整合包导入环境：临时路径 + 固定内容下载 + 可替换的两个在线数据源。</summary>
    private sealed class AddonEnvironment : IDisposable
    {
        private readonly HttpClient _http;

        public AddonEnvironment(
            ICurseForgeService? curseForge = null,
            IModrinthService? modrinth = null)
        {
            Paths = new CoreAssetsPaths();
            _http = new HttpClient(new StaticContentHandler(Encoding.UTF8.GetBytes("jar")));
            Service = new ModpackService(
                Paths,
                new DownloadQueue(_http),
                new CoreAssetsLog(),
                modrinth,
                install: null,
                curseForge: curseForge)
            {
                // 只测 addons 链路：装游戏由 InstallService 负责，有独立测试。
                InstallGameOnImport = false,
            };
        }

        public CoreAssetsPaths Paths { get; }

        public ModpackService Service { get; }

        public string CreateZip(string name, string entries)
        {
            var path = Path.Combine(Paths.RootDirectory, name);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            using (var stream = archive.CreateEntry("manifest.json").Open())
            {
                var bytes = Encoding.UTF8.GetBytes(entries);
                stream.Write(bytes, 0, bytes.Length);
            }

            using (var stream = archive.CreateEntry("overrides/config/a.cfg").Open())
            {
                var bytes = Encoding.UTF8.GetBytes("cfg");
                stream.Write(bytes, 0, bytes.Length);
            }

            return path;
        }

        public void Dispose()
        {
            _http.Dispose();
            Paths.Dispose();
        }
    }

    /// <summary>返回固定字节的 handler：让「反查成功以后确实落了盘」可断言，而不真的联网。</summary>
    private sealed class StaticContentHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StaticContentHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = new ByteArrayContent(_payload);
            content.Headers.ContentLength = _payload.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    private sealed class FakeCurseForgeService : ICurseForgeService
    {
        public string? LastError => null;

        public IReadOnlyList<CurseForgeFile> Files { get; init; } = [];

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
            => Task.FromResult(Files);

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

        public Task<IReadOnlyList<CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
            IReadOnlyList<uint> fingerprints,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CurseForgeFingerprintMatch>>([]);
    }

    private sealed class FakeModrinthService : IModrinthService
    {
        public string? LastError => null;

        public IReadOnlyList<ModrinthVersion> Versions { get; init; } = [];

        public IReadOnlyList<ModrinthProject> Projects { get; init; } = [];

        public List<string> QueriedProjectIds { get; } = [];

        public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            CancellationToken cancellationToken = default)
        {
            // 搜索兜底只应该在直接按 id 查不到时发生；记录一下便于排查。
            var matched = Projects
                .Where(project => string.Equals(project.Slug, query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult<IReadOnlyList<ModrinthProject>>(matched);
        }

        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
            string projectId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default)
        {
            lock (QueriedProjectIds)
            {
                QueriedProjectIds.Add(projectId);
            }

            var matched = Versions.Where(version => string.Equals(version.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IReadOnlyList<ModrinthVersion>>(matched);
        }

        public Task<ModrinthInstallResult> InstallAsync(
            ModrinthVersion version,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModrinthInstallResult { Success = false, Error = "测试替身不下载" });

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ModrinthVersion?> LookupBySha1Async(string sha1, CancellationToken cancellationToken = default)
            => Task.FromResult<ModrinthVersion?>(null);
    }
}
