using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

// Modrinth 的 files[].hashes.sha1 就是 SHA1，测试必须按同一算法构造期望值。
#pragma warning disable CA5350

namespace NebulaLauncher.Tests;

/// <summary>
/// Modrinth 依赖解析与计划安装的测试。
///
/// 全程走假 <see cref="HttpMessageHandler"/>：依赖图（链式必需、成环、optional、incompatible、
/// embedded、只给 version_id、Quilt 回退）都写成「项目 → 版本 JSON」的映射，不碰真实网络。
/// </summary>
public class CoreAssets_ModrinthPlanTests
{
    /// <summary>A↔B 成环时的期望顺序：深度优先后序，B 在 A 之前。</summary>
    private static readonly string[] ExpectedCycleOrder = ["BBBB", "AAAA"];

    // ---------------- 解析 ----------------

    [Fact]
    public void 解析依赖_四种类型与未知值按必需处理()
    {
        const string json = """
        [
          {
            "id": "ROOTV",
            "project_id": "ROOT",
            "name": "Root",
            "version_number": "1.0.0",
            "files": [
              { "url": "https://cdn.modrinth.com/fake/root.jar", "filename": "root.jar", "primary": true, "size": 10 }
            ],
            "dependencies": [
              { "version_id": null, "project_id": "AAAA", "file_name": "fabric-api.jar", "dependency_type": "required" },
              { "version_id": null, "project_id": "BBBB", "file_name": "cloth.jar", "dependency_type": "optional" },
              { "version_id": null, "project_id": "CCCC", "file_name": "old-api.jar", "dependency_type": "incompatible" },
              { "version_id": null, "project_id": "DDDD", "file_name": null, "dependency_type": "embedded" },
              { "version_id": null, "project_id": "EEEE", "file_name": null, "dependency_type": "brand-new-value" },
              { "version_id": "VID_ONLY", "project_id": null, "file_name": null, "dependency_type": "required" }
            ]
          }
        ]
        """;

        var version = ModrinthService.ParseVersions(json).Single();

        Assert.Equal(6, version.Dependencies.Count);

        Assert.Equal(ModrinthDependencyType.Required, version.Dependencies[0].Type);
        Assert.Equal("AAAA", version.Dependencies[0].ProjectId);
        Assert.Equal("fabric-api.jar", version.Dependencies[0].FileName);

        Assert.Equal(ModrinthDependencyType.Optional, version.Dependencies[1].Type);
        Assert.Equal(ModrinthDependencyType.Incompatible, version.Dependencies[2].Type);
        Assert.Equal(ModrinthDependencyType.Embedded, version.Dependencies[3].Type);

        // 未知取值按 Required：漏装前置会崩在启动期，多装一个只是多占体积。
        Assert.Equal(ModrinthDependencyType.Required, version.Dependencies[4].Type);

        // 只给 version_id 的依赖保留 VersionId，ProjectId 为 null（由解析器降级处理）。
        Assert.Equal("VID_ONLY", version.Dependencies[5].VersionId);
        Assert.Null(version.Dependencies[5].ProjectId);
    }

    [Fact]
    public void 解析依赖_没有Id的条目被丢弃()
    {
        const string json = """
        [
          {
            "id": "ROOTV",
            "project_id": "ROOT",
            "version_number": "1.0.0",
            "files": [ { "url": "https://cdn.modrinth.com/fake/root.jar", "filename": "root.jar", "primary": true, "size": 1 } ],
            "dependencies": [
              { "dependency_type": "required" },
              { "project_id": "  ", "version_id": null, "dependency_type": "required" }
            ]
          }
        ]
        """;

        Assert.Empty(ModrinthService.ParseVersions(json).Single().Dependencies);
    }

    // ---------------- 依赖展开 ----------------

    [Fact]
    public async Task 计划_链式必需依赖全部收集且前置在前()
    {
        var server = new PlanServer()
            .AddProject("BBBB", Versions("B1", "BBBB", "b.jar", Payload("b"), DepJson("CCCC")))
            .AddProject("CCCC", Versions("C1", "CCCC", "c.jar", Payload("c")));

        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion("AAAA", "a.jar", Payload("a"), Required("BBBB", "b.jar"));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Empty(plan.Blocked);
        Assert.Equal(2, plan.Required.Count);

        // 深度优先的后序：C 先于 B，落盘时前置一定在依赖它的模组之前。
        Assert.Equal("CCCC", plan.Required[0].ProjectId);
        Assert.Equal("BBBB", plan.Required[1].ProjectId);
        Assert.Equal(3, plan.TotalFiles);
        Assert.True(plan.CanInstall);
    }

    [Fact]
    public async Task 计划_成环不死循环()
    {
        var server = new PlanServer()
            .AddProject("AAAA", Versions("A1", "AAAA", "a.jar", Payload("a"), DepJson("BBBB")))
            .AddProject("BBBB", Versions("B1", "BBBB", "b.jar", Payload("b"), DepJson("AAAA")));

        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        // 根项目 X 指向 A，A↔B 成环：回访必须被 projectId 去重挡掉。
        var root = RootVersion("XXXX", "x.jar", Payload("x"), Required("AAAA", "a.jar"));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Equal(ExpectedCycleOrder, plan.Required.Select(version => version.ProjectId).ToArray());
        Assert.True(plan.CanInstall);

        // 每个项目只查一次：环上的第二次回访没有发出请求。
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task 计划_互斥依赖进阻断列表且不请求它()
    {
        var server = new PlanServer();
        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion(
            "AAAA",
            "a.jar",
            Payload("a"),
            new ModrinthDependency(null, "CCCC", "old-api.jar", ModrinthDependencyType.Incompatible));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Single(plan.Blocked);
        Assert.Contains("old-api.jar", plan.Blocked[0], StringComparison.Ordinal);
        Assert.False(plan.CanInstall);
        Assert.Empty(plan.Required);

        // 互斥就地阻断，不必为它发请求。
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task 计划_可选依赖只收集不递归()
    {
        var server = new PlanServer()
            .AddProject("OOOO", Versions("O1", "OOOO", "o.jar", Payload("o"), DepJson("PPPP")))
            .AddProject("PPPP", Versions("P1", "PPPP", "p.jar", Payload("p")));

        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion(
            "AAAA",
            "a.jar",
            Payload("a"),
            new ModrinthDependency(null, "OOOO", "o.jar", ModrinthDependencyType.Optional));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Empty(plan.Required);
        Assert.Single(plan.Optional);
        Assert.Equal("OOOO", plan.Optional[0].ProjectId);

        // P 是 O 的前置，但 O 本身不会被自动安装：递归它只是白花一次请求。
        Assert.DoesNotContain(server.Requests, url => url.Contains("PPPP", StringComparison.Ordinal));
        Assert.Equal(1, plan.TotalFiles);
    }

    [Fact]
    public async Task 计划_内嵌依赖直接跳过()
    {
        var server = new PlanServer();
        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion(
            "AAAA",
            "a.jar",
            Payload("a"),
            new ModrinthDependency(null, "DDDD", null, ModrinthDependencyType.Embedded));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Empty(plan.Required);
        Assert.Empty(plan.Optional);
        Assert.Empty(plan.Blocked);
        Assert.Empty(plan.Warnings);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task 计划_只给版本ID的依赖降级为提示()
    {
        var server = new PlanServer();
        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion(
            "AAAA",
            "a.jar",
            Payload("a"),
            new ModrinthDependency("VID_ONLY", null, "mystery.jar", ModrinthDependencyType.Required));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Empty(plan.Required);
        Assert.Empty(plan.Blocked);
        Assert.Single(plan.Warnings);
        Assert.Contains("版本 ID", plan.Warnings[0], StringComparison.Ordinal);
        Assert.True(plan.CanInstall);

        // 不查 /v2/version/{id}：一条依赖多一次请求会成倍放大递归量。
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task 计划_Quilt目标回退到Fabric构建()
    {
        var server = new PlanServer()
            .AddFabricOnlyProject("BBBB", Versions("B1", "BBBB", "b.jar", Payload("b")));

        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion("AAAA", "a.jar", Payload("a"), Required("BBBB", "b.jar"));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Quilt);

        Assert.Single(plan.Required);
        Assert.Equal("BBBB", plan.Required[0].ProjectId);

        // 第一次按 quilt 查为空，第二次按 fabric 命中。
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task 计划_深度超过上限时停止展开()
    {
        var server = new PlanServer();

        // ROOT → P00 → P01 → … → P10，超过 MaxDepth 的那一层只提示、不再请求。
        for (var index = 0; index <= 10; index++)
        {
            var projectId = ProjectIdOf(index);
            var dependencies = index < 10 ? DepJson(ProjectIdOf(index + 1)) : "[]";

            server.AddProject(projectId, Versions($"{projectId}V", projectId, $"{projectId}.jar", Payload(projectId), dependencies));
        }

        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var root = RootVersion("ROOT", "root.jar", Payload("root"), Required(ProjectIdOf(0), $"{ProjectIdOf(0)}.jar"));

        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        Assert.Equal(ModrinthInstallPlanner.MaxDepth, plan.Required.Count);
        Assert.Contains(plan.Warnings, warning => warning.Contains("超过", StringComparison.Ordinal));
        Assert.True(plan.CanInstall);
    }

    // ---------------- 计划安装 ----------------

    [Fact]
    public async Task 安装_先装前置再装本体()
    {
        var dependencyPayload = Payload("dep");
        var rootPayload = Payload("root");

        var server = new PlanServer()
            .AddProject("BBBB", Versions("B1", "BBBB", "b.jar", dependencyPayload))
            .AddFile(DownloadUrl("b.jar"), dependencyPayload)
            .AddFile(DownloadUrl("root.jar"), rootPayload);

        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-plan-order");
        using var service = new ModrinthService(new CoreAssetsLog(), paths, server);

        var root = RootVersion("AAAA", "root.jar", rootPayload, Required("BBBB", "b.jar"));
        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        var result = await service.InstallPlanAsync(plan, gameDirectory, ContentKind.Mod);

        Assert.True(result.Success, result.Files.FirstOrDefault(file => file.Error is not null)?.Error);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(2, result.InstalledCount);
        Assert.Equal(0, result.FailedCount);

        Assert.True(result.Files[0].IsDependency);
        Assert.Equal("BBBB", result.Files[0].Version.ProjectId);
        Assert.False(result.Files[1].IsDependency);
        Assert.Equal("AAAA", result.Files[1].Version.ProjectId);

        var mods = Path.Combine(gameDirectory, "mods");
        Assert.True(File.Exists(Path.Combine(mods, "b.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "root.jar")));
    }

    [Fact]
    public async Task 安装_单个文件失败不中断其余()
    {
        var dependencyPayload = Payload("dep");
        var rootPayload = Payload("root");

        var server = new PlanServer()
            .AddProject("BBBB", Versions("B1", "BBBB", "b.jar", dependencyPayload))
            .AddFile(DownloadUrl("b.jar"), Payload("corrupted"))
            .AddFile(DownloadUrl("root.jar"), rootPayload);

        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-plan-partial");
        using var service = new ModrinthService(new CoreAssetsLog(), paths, server);

        var root = RootVersion("AAAA", "root.jar", rootPayload, Required("BBBB", "b.jar"));
        var plan = await service.PlanInstallAsync(root, "1.20.1", LoaderKind.Fabric);

        var result = await service.InstallPlanAsync(plan, gameDirectory, ContentKind.Mod);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.InstalledCount);
        Assert.Contains("SHA1", result.Files[0].Error ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(result.Files[0].FilePath);

        var mods = Path.Combine(gameDirectory, "mods");
        Assert.False(File.Exists(Path.Combine(mods, "b.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "root.jar")));
    }

    [Fact]
    public async Task 安装_同名文件已存在时计划里也改名落盘()
    {
        var rootPayload = Payload("root");

        var server = new PlanServer()
            .AddFile(DownloadUrl("root.jar"), rootPayload);

        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-plan-rename");
        var mods = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "root.jar"), "old");

        using var service = new ModrinthService(new CoreAssetsLog(), paths, server);

        var root = RootVersion("AAAA", "root.jar", rootPayload);
        var plan = new ModrinthInstallPlan { Root = root };

        var result = await service.InstallPlanAsync(plan, gameDirectory, ContentKind.Mod);

        Assert.True(result.Success);
        Assert.EndsWith("root (1).jar", result.Files[0].FilePath ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(mods, "root.jar")));
    }

    [Fact]
    public async Task 安装_计划被阻断时不发任何请求()
    {
        var server = new PlanServer();
        using var service = new ModrinthService(new CoreAssetsLog(), null, server);

        var plan = new ModrinthInstallPlan
        {
            Root = RootVersion("AAAA", "a.jar", Payload("a")),
            Blocked = new[] { "「A」声明与「B」互斥。" },
        };

        var result = await service.InstallPlanAsync(plan, Path.GetTempPath(), ContentKind.Mod);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Files);
        Assert.Empty(server.Requests);
    }

    // ---------------- 构造辅助 ----------------

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes("payload-" + text);

    private static string ProjectIdOf(int index) => $"P{index:D2}";

    private static string DownloadUrl(string fileName) => $"https://cdn.modrinth.com/fake/{fileName}";

    private static string Sha1Of(byte[] payload) => Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

    /// <summary>一条 required 依赖的 JSON 片段。</summary>
    private static string DepJson(string projectId, string type = "required")
    {
        return $$"""[{ "project_id": "{{projectId}}", "file_name": "{{projectId.ToLowerInvariant()}}.jar", "dependency_type": "{{type}}" }]""";
    }

    private static ModrinthDependency Required(string projectId, string fileName) =>
        new(null, projectId, fileName, ModrinthDependencyType.Required);

    /// <summary>一个项目的版本数组：单个 release 版本，主文件带正确 sha1。</summary>
    private static string Versions(
        string versionId,
        string projectId,
        string fileName,
        byte[] payload,
        string dependencies = "[]")
    {
        return $$"""
        [
          {
            "id": "{{versionId}}",
            "project_id": "{{projectId}}",
            "name": "{{projectId}} build",
            "version_number": "{{versionId}}-1.0.0",
            "version_type": "release",
            "game_versions": ["1.20.1"],
            "loaders": ["fabric"],
            "date_published": "2026-01-01T00:00:00Z",
            "files": [
              {
                "hashes": { "sha1": "{{Sha1Of(payload)}}" },
                "url": "{{DownloadUrl(fileName)}}",
                "filename": "{{fileName}}",
                "primary": true,
                "size": {{payload.Length}}
              }
            ],
            "dependencies": {{dependencies}}
          }
        ]
        """;
    }

    private static ModrinthVersion RootVersion(
        string projectId,
        string fileName,
        byte[] payload,
        params ModrinthDependency[] dependencies) => new()
    {
        Id = projectId + "V",
        ProjectId = projectId,
        Name = projectId + " root",
        VersionNumber = "1.0.0",
        FileName = fileName,
        DownloadUrl = DownloadUrl(fileName),
        FileSize = payload.Length,
        Sha1 = Sha1Of(payload),
        Dependencies = dependencies,
    };

    /// <summary>
    /// 按 URL 分派的假 Modrinth：<c>/project/{id}/version</c> 返回预置 JSON，其他地址按下载文件返回字节。
    /// </summary>
    private sealed class PlanServer : HttpMessageHandler
    {
        private readonly Dictionary<string, ProjectResponse> _projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        /// <summary>按顺序记录收到的请求，用来断言「不该发的请求没发」。</summary>
        public List<string> Requests { get; } = new();

        public PlanServer AddProject(string projectId, string versionsJson)
        {
            _projects[projectId] = new ProjectResponse(versionsJson, FabricOnly: false);
            return this;
        }

        /// <summary>只发 fabric 构建的项目：按 quilt 过滤时返回空，用来验证宽松匹配回退。</summary>
        public PlanServer AddFabricOnlyProject(string projectId, string versionsJson)
        {
            _projects[projectId] = new ProjectResponse(versionsJson, FabricOnly: true);
            return this;
        }

        public PlanServer AddFile(string url, byte[] payload)
        {
            _files[url] = payload;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var projectIndex = url.IndexOf("/project/", StringComparison.Ordinal);

            if (projectIndex >= 0)
            {
                var start = projectIndex + "/project/".Length;
                var end = url.IndexOf("/version", start, StringComparison.Ordinal);
                var projectId = end > start ? url[start..end] : url[start..];

                if (_projects.TryGetValue(projectId, out var project) &&
                    !(project.FabricOnly && url.Contains("quilt", StringComparison.Ordinal)))
                {
                    return Task.FromResult(Json(project.Json));
                }

                // 没有构建：与真实接口一致，返回空数组而不是 404。
                return Task.FromResult(Json("[]"));
            }

            if (_files.TryGetValue(url, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private sealed record ProjectResponse(string Json, bool FabricOnly);
    }
}
