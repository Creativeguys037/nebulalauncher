using System.Net;
using System.Net.Http;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// CurseForge 指纹命中按「请求指纹 → 匹配结果」关联（AUDIT-2026-10-23 第四节 P1）。
///
/// 旧实现按文件名关联：本地 jar 被改过名就识别失败，两个不同文件同名时又会串成同一份结果。
/// 这里覆盖四段：关联函数的纯逻辑、<c>fileFingerprint</c> 的补码解析、
/// 「响应缺指纹」的兼容兜底，以及一次端到端检查（本地改名、CF 返回原名仍能命中）。
/// </summary>
public class CoreMods_CurseForgeFingerprintTests
{
    /// <summary>"hello" 的 CurseForge 指纹，只用作一个高位为 1 的样本值。</summary>
    private const uint SampleFingerprint = 2788266382u;

    // ---------------- 关联函数 ----------------

    /// <summary>改名后的本地文件必须仍能命中：身份是指纹，不是文件名。</summary>
    [Fact]
    public void 关联_本地改名后仍按指纹命中()
    {
        var match = Match(4001, 238222, "jei-1.20.1-15.2.0.27.jar", SampleFingerprint);

        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(SampleFingerprint, "用户改过的名字.jar")],
            [match]);

        var hit = Assert.Single(associated);
        Assert.NotNull(hit);
        Assert.Equal(4001, hit!.FileId);
    }

    /// <summary>两个本地文件指纹不同、CF 返回同名时不能串：请求顺序与响应顺序也刻意相反。</summary>
    [Fact]
    public void 关联_同名不同指纹不互相串()
    {
        var first = Match(1001, 111, "same.jar", 1111u);
        var second = Match(2002, 222, "same.jar", 2222u);

        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(2222u, "same.jar"), (1111u, "same.jar")],
            [first, second]);

        Assert.Equal(2, associated.Count);
        Assert.Equal(2002, associated[0]!.FileId);
        Assert.Equal(1001, associated[1]!.FileId);
    }

    [Fact]
    public void 关联_部分命中时未命中的位置为空()
    {
        var match = Match(1001, 111, "hit.jar", 1111u);

        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(1111u, "hit.jar"), (9999u, "miss.jar")],
            [match]);

        Assert.Equal(1001, associated[0]!.FileId);
        Assert.Null(associated[1]);
    }

    /// <summary>整批响应都没带指纹时才有文件名兜底，否则旧响应会全部识别失败。</summary>
    [Fact]
    public void 关联_整批无指纹时按文件名兼容兜底()
    {
        var legacy = Match(1001, 111, "legacy.jar", 0);

        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(9999u, "legacy.jar")],
            [legacy]);

        Assert.Same(legacy, associated[0]);
    }

    /// <summary>只要响应带指纹，文件名撞上也不能采信——按名字关联正是这次要修掉的串号来源。</summary>
    [Fact]
    public void 关联_有指纹时不按文件名兜底()
    {
        var match = Match(1001, 111, "renamed.jar", 1111u);

        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(9999u, "renamed.jar")],
            [match]);

        Assert.Null(associated[0]);
    }

    [Fact]
    public void 关联_无指纹且文件名为空不崩溃()
    {
        var match = Match(1001, 111, string.Empty, 0);

        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(0u, string.Empty)],
            [match]);

        Assert.Null(Assert.Single(associated));
    }

    [Fact]
    public void 关联_空匹配列表返回全空()
    {
        var associated = ModUpdateService.AssociateFingerprintMatches(
            [(1u, "a.jar"), (2u, "b.jar")],
            []);

        Assert.Equal(2, associated.Count);
        Assert.Null(associated[0]);
        Assert.Null(associated[1]);
    }

    [Fact]
    public void 关联_空请求返回空数组()
    {
        var associated = ModUpdateService.AssociateFingerprintMatches(
            [],
            [Match(1, 2, "a.jar", 3u)]);

        Assert.Empty(associated);
    }

    // ---------------- 解析侧 ----------------

    /// <summary>CF 按 int32 传指纹，高位为 1 时是负数，必须按补码还原成本地的 uint。</summary>
    [Fact]
    public void 解析_fileFingerprint按int32补码还原()
    {
        var encoded = unchecked((int)SampleFingerprint);

        var json = $$"""
        {
          "data": {
            "exactMatches": [
              {
                "id": 4001,
                "file": {
                  "id": 4001,
                  "modId": 238222,
                  "fileName": "jei-1.20.1-15.2.0.27.jar",
                  "fileFingerprint": {{encoded}}
                },
                "latestFiles": []
              }
            ]
          }
        }
        """;

        var match = Assert.Single(CurseForgeService.ParseFingerprintMatches(json));
        Assert.Equal(SampleFingerprint, match.Fingerprint);
    }

    [Fact]
    public void 解析_没有fileFingerprint时指纹为零()
    {
        const string json = """
        {
          "data": {
            "exactMatches": [
              {
                "id": 4001,
                "file": { "id": 4001, "modId": 238222, "fileName": "jei.jar" },
                "latestFiles": []
              }
            ]
          }
        }
        """;

        var match = Assert.Single(CurseForgeService.ParseFingerprintMatches(json));
        Assert.Equal(0u, match.Fingerprint);
    }

    // ---------------- 端到端 ----------------

    /// <summary>
    /// 本地文件名与 CF 返回的文件名完全不同、指纹相同：改名前这条会落到「未识别」。
    /// 这里走真实 <see cref="CurseForgeService"/> 解析，验证补码、关联、结果组装整条链。
    /// </summary>
    [Fact]
    public async Task 检查更新_本地改名后仍命中CurseForge()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-cf-renamed");
        var file = await CreateModAsync(gameDirectory, "我改过的名字.jar", "jei-payload");

        var payload = await File.ReadAllBytesAsync(file);
        var fingerprint = ModFingerprint.CurseForgeFingerprint(payload);
        Assert.NotEqual(0u, fingerprint);

        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/v1/fingerprint", StringComparison.Ordinal))
            {
                return Json(FingerprintJson(fingerprint));
            }

            // Modrinth 两个端点都 404：这个文件只有 CurseForge 收录。
            return NotFound();
        });

        using var modrinth = new ModrinthService(new CoreAssetsLog(), paths, handler);
        using var curseForge = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new FingerprintTestSettings(),
            handler);

        var service = new ModUpdateService(modrinth, curseForge, paths, new CoreAssetsLog());

        var report = await service.CheckAsync(
            [Item(file, "15.2.0.27", LoaderKind.Forge)],
            gameDirectory,
            "1.20.1",
            LoaderKind.Forge);

        var info = Assert.Single(report.Items);
        Assert.True(info.Matched);
        Assert.Equal(ContentSourceKind.CurseForge, info.Source);
        Assert.Equal("238222", info.ProjectId);
        Assert.True(info.HasUpdate);
        Assert.Equal(ModUpdateService.StatusUpdateAvailable, info.Status);
        Assert.Equal("jei-1.20.1-15.3.0.30.jar", info.LatestVersion);
        Assert.Null(info.Error);
    }

    /// <summary>
    /// 请求形状本身就是被测对象。路径写成单数 <c>/v1/fingerprint</c> 时服务端回 404，
    /// 指纹按 int32 补码写成负数时回 400——两种都表现为「CurseForge 明明收录了却识别不出来」，
    /// 而补全链路把「认不出」当正常结果（不报错），所以只有钉住请求形状才防得住复发。
    /// 实测 2026-10-24：正确形状是 <c>POST /v1/fingerprints</c> + 无符号数组。
    /// </summary>
    [Fact]
    public async Task 指纹反查_请求用复数路径且指纹按无符号发送()
    {
        using var paths = new CoreAssetsPaths();
        HttpRequestMessage? captured = null;
        string? body = null;

        var handler = new RoutingHandler(request =>
        {
            captured = request;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":{"exactMatches":[],"exactFingerprints":[],"partialMatches":[]}}""");
        });

        using var service = new CurseForgeService(
            new CoreAssetsLog(),
            paths,
            new FingerprintTestSettings(),
            handler);

        await service.LookupByFingerprintsAsync([SampleFingerprint]);

        Assert.NotNull(captured);
        Assert.NotNull(body);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/v1/fingerprints", captured.RequestUri!.AbsolutePath);

        var negative = unchecked((int)SampleFingerprint).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(SampleFingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain(negative, body, StringComparison.Ordinal);
    }

    // ---------------- 构造与替身 ----------------

    private static CurseForgeFingerprintMatch Match(long fileId, long modId, string fileName, uint fingerprint) => new()
    {
        FileId = fileId,
        ModId = modId,
        FileName = fileName,
        Fingerprint = fingerprint,
    };

    private static string FingerprintJson(uint fingerprint)
    {
        var encoded = unchecked((int)fingerprint);

        return $$"""
        {
          "data": {
            "exactMatches": [
              {
                "id": 4001,
                "file": {
                  "id": 4001,
                  "modId": 238222,
                  "fileName": "jei-1.20.1-15.2.0.27.jar",
                  "displayName": "jei-1.20.1-15.2.0.27.jar",
                  "downloadUrl": "https://edge.forgecdn.net/files/4001/jei.jar",
                  "fileLength": 1234567,
                  "fileDate": "2024-05-01T12:00:00Z",
                  "releaseType": 1,
                  "gameVersions": ["1.20.1", "Forge"],
                  "fileFingerprint": {{encoded}}
                },
                "latestFiles": [
                  {
                    "id": 5001,
                    "modId": 238222,
                    "fileName": "jei-1.20.1-15.3.0.30.jar",
                    "displayName": "jei-1.20.1-15.3.0.30.jar",
                    "downloadUrl": "https://edge.forgecdn.net/files/5001/jei.jar",
                    "fileLength": 1334567,
                    "fileDate": "2024-06-01T12:00:00Z",
                    "releaseType": 1,
                    "gameVersions": ["1.20.1", "Forge"]
                  }
                ]
              }
            ]
          }
        }
        """;
    }

    private static ContentItem Item(string filePath, string version, LoaderKind loader) => new()
    {
        FilePath = filePath,
        FileName = Path.GetFileName(filePath),
        Kind = ContentKind.Mod,
        Id = Path.GetFileNameWithoutExtension(filePath),
        DisplayName = Path.GetFileNameWithoutExtension(filePath),
        Version = version,
        Loader = loader,
    };

    private static async Task<string> CreateModAsync(string gameDirectory, string fileName, string content)
    {
        var directory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    /// <summary>按 URL 路由的假 handler。</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = _responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>测试用设置服务：只暴露一份内存快照（API key 已内置，不需要再配）。</summary>
    private sealed class FingerprintTestSettings : ISettingsService
    {
        public FingerprintTestSettings() => Current = new AppSettings();

        public AppSettings Current { get; }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }
}
