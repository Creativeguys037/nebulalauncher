using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载韧性两项：Modrinth 镜像兜底（只在官方连不上时用）+ 只读目录覆盖。
///
/// 全部用例都不联网：Modrinth 的响应由 <see cref="MirrorRoutingHandler"/> 造，
/// 只读目录用临时目录造。判据是「官方 200 / 官方 404 / 官方超时」三种情况下的
/// **镜像命中次数**——这是「镜像只在官方失败后用」唯一能被自动化检查的证据。
/// </summary>
public class CoreDownloads_MirrorFallbackTests
{
    private const string OfficialSearch = "https://api.modrinth.com/v2/search?query=sodium&limit=20";

    /// <summary>GET /v2/search 的最小可用响应（字段按实测结构裁剪，只要解析得到一条命中）。</summary>
    private const string SearchJson = """
    {
      "hits": [
        {
          "project_id": "AANobbMI",
          "project_type": "mod",
          "slug": "sodium",
          "author": "jellysquid3",
          "title": "Sodium",
          "description": "A high-performance rendering engine replacement for Minecraft.",
          "categories": ["fabric", "neoforge", "optimization", "quilt"],
          "versions": ["1.20.1", "1.21.4"],
          "downloads": 250585660,
          "follows": 41999,
          "icon_url": null,
          "date_modified": "2026-10-16T01:51:36.06314+08:00"
        }
      ],
      "offset": 0,
      "limit": 20,
      "total_hits": 1
    }
    """;

    [Fact]
    public void 镜像候选_官方永远排第一_镜像只在开关打开时追加()
    {
        var enabled = DownloadMirror.ModrinthCandidates(OfficialSearch, useModrinthMirror: true);

        Assert.Equal(2, enabled.Count);
        Assert.Equal(OfficialSearch, enabled[0]);
        Assert.Equal("https://mod.mcimirror.top/modrinth/v2/search?query=sodium&limit=20", enabled[1]);

        // API 与 CDN 的镜像前缀不同：前者多一段 /modrinth，不能压成同一个前缀。
        var cdn = DownloadMirror.ModrinthCandidates("https://cdn.modrinth.com/data/AANobbMI/sodium.jar", true);
        Assert.Equal("https://mod.mcimirror.top/data/AANobbMI/sodium.jar", cdn[1]);

        // 开关关闭：候选表里只有官方，任何请求都不会发到第三方主机。
        var disabled = DownloadMirror.ModrinthCandidates(OfficialSearch, useModrinthMirror: false);
        Assert.Single(disabled);
        Assert.Equal(OfficialSearch, disabled[0]);
    }

    [Fact]
    public async Task 官方返回200_一个请求都不发到镜像()
    {
        var handler = new MirrorRoutingHandler(_ => Json(SearchJson));
        using var service = new ModrinthService(new CoreAssetsLog(), null, Settings(mirror: true), handler);

        var result = await service.SearchAsync(ContentKind.Mod, "sodium", "1.20.1", LoaderKind.Fabric, 0, 20);

        Assert.Single(result);
        Assert.Equal(0, handler.MirrorHits);
        Assert.Single(handler.Urls);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 官方返回404_不试镜像_直接报项目不存在()
    {
        var handler = new MirrorRoutingHandler(_ => Status(HttpStatusCode.NotFound));
        using var service = new ModrinthService(new CoreAssetsLog(), null, Settings(mirror: true), handler);

        var result = await service.SearchAsync(ContentKind.Mod, "definitely-not-a-project", null, LoaderKind.Vanilla, 0, 20);

        Assert.Empty(result);

        // 关键断言：404 是「资源不存在」，不是「网络不好」，不能换镜像再多等一轮超时。
        Assert.Equal(0, handler.MirrorHits);
        Assert.Contains("404", service.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 官方超时_换镜像并返回结果()
    {
        var handler = new MirrorRoutingHandler(_ => null) // 官方一律超时
        {
            MirrorResponder = _ => Json(SearchJson),
        };
        using var service = new ModrinthService(new CoreAssetsLog(), null, Settings(mirror: true), handler);

        var result = await service.SearchAsync(ContentKind.Mod, "sodium", "1.20.1", LoaderKind.Fabric, 0, 20);

        Assert.Single(result);
        Assert.Equal(1, handler.MirrorHits);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task 官方5xx_换镜像并返回结果()
    {
        var handler = new MirrorRoutingHandler(_ => Status(HttpStatusCode.BadGateway))
        {
            MirrorResponder = _ => Json(SearchJson),
        };
        using var service = new ModrinthService(new CoreAssetsLog(), null, Settings(mirror: true), handler);

        var result = await service.SearchAsync(ContentKind.Mod, "sodium", "1.20.1", LoaderKind.Fabric, 0, 20);

        Assert.Single(result);
        Assert.Equal(1, handler.MirrorHits);
    }

    [Fact]
    public async Task 开关关闭_官方5xx也不碰镜像()
    {
        var handler = new MirrorRoutingHandler(_ => Status(HttpStatusCode.BadGateway))
        {
            MirrorResponder = _ => Json(SearchJson),
        };
        using var service = new ModrinthService(new CoreAssetsLog(), null, Settings(mirror: false), handler);

        var result = await service.SearchAsync(ContentKind.Mod, "sodium", "1.20.1", LoaderKind.Fabric, 0, 20);

        Assert.Empty(result);
        Assert.Equal(0, handler.MirrorHits);
        Assert.NotNull(service.LastError);
    }

    [Fact]
    public void 设置克隆_镜像开关默认关且克隆保留取值()
    {
        Assert.False(new AppSettings().UseModrinthMirror);

        var clone = new AppSettings { UseModrinthMirror = true }.Clone();
        Assert.True(clone.UseModrinthMirror);
    }

    [Fact]
    public void 只读目录_清除只读属性后目录与一级文件都可写()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "options.txt");
        File.WriteAllText(file, "lang:en_us");

        if (!OperatingSystem.IsWindows())
        {
            // Unix 上只读位是权限位，不照搬 Windows 的 ReadOnly 语义——直接断言「不支持」。
            Assert.NotNull(PathProbe.TryMakeWritable(temp.Path));
            Assert.NotNull(PathProbe.TryMakeWritable(Path.Combine(temp.Path, "missing")));
            return;
        }

        File.SetAttributes(temp.Path, File.GetAttributes(temp.Path) | FileAttributes.ReadOnly);
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        Assert.Null(PathProbe.TryMakeWritable(temp.Path));
        Assert.True((File.GetAttributes(temp.Path) & FileAttributes.ReadOnly) == 0);
        Assert.True((File.GetAttributes(file) & FileAttributes.ReadOnly) == 0);

        // 清完能真的写进去（属性清了但权限没变才叫「修好」）。
        File.WriteAllText(file, "lang:zh_cn");

        // 清不掉的场景用不存在的目录代表：必须给出中文原因而不是静默成功。
        var missing = PathProbe.TryMakeWritable(Path.Combine(temp.Path, "missing"));
        Assert.NotNull(missing);
        Assert.Contains("不存在", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void 覆盖前备份_生成bak且内容是旧值_不累积()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "1.20.1.json");
        File.WriteAllText(target, """{"old":true}""");

        var backup = InstallService.BackupBeforeOverwrite(target);

        Assert.Equal(target + ".bak", backup);
        Assert.Equal("""{"old":true}""", File.ReadAllText(backup!));

        // 第二次覆盖：.bak 被新的旧值替换，不会出现 .bak.1 / .bak.2。
        File.WriteAllText(target, """{"new":true}""");
        Assert.NotNull(InstallService.BackupBeforeOverwrite(target));

        Assert.Equal("""{"new":true}""", File.ReadAllText(target + ".bak"));
        Assert.False(File.Exists(target + ".bak.1"));

        // 目标文件原本不存在时不需要备份。
        var fresh = Path.Combine(temp.Path, "fresh.json");
        Assert.Null(InstallService.BackupBeforeOverwrite(fresh));
    }

    private static MirrorSettings Settings(bool mirror) => new(mirror);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    /// <summary>测试用设置服务：只暴露一份可写的内存快照。</summary>
    private sealed class MirrorSettings(bool useModrinthMirror) : NebulaLauncher.Core.Abstractions.ISettingsService
    {
        public AppSettings Current { get; } = new() { UseModrinthMirror = useModrinthMirror };

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// 按主机分流并记录每次请求：官方地址走 <see cref="_officialResponder"/>，
    /// <c>mod.mcimirror.top</c> 走 <see cref="MirrorResponder"/>。
    /// 官方响应返回 <c>null</c> 时抛 <see cref="TaskCanceledException"/> 模拟超时。
    /// </summary>
    private sealed class MirrorRoutingHandler(Func<HttpRequestMessage, HttpResponseMessage?> officialResponder)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage?> _officialResponder = officialResponder;

        public Func<HttpRequestMessage, HttpResponseMessage?>? MirrorResponder { get; init; }

        public List<string> Urls { get; } = [];

        public List<bool> MirrorFlags { get; } = [];

        public int MirrorHits => MirrorFlags.Count(flag => flag);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var isMirror = url.Contains("mcimirror.top", StringComparison.OrdinalIgnoreCase);

            Urls.Add(url);
            MirrorFlags.Add(isMirror);

            var responder = isMirror ? MirrorResponder : _officialResponder;
            var response = responder?.Invoke(request);

            return response is null
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("模拟超时"))
                : Task.FromResult(response);
        }
    }

    /// <summary>
    /// 临时目录，退出时整棵删掉。
    ///
    /// 清理必须自己吞掉所有异常：Windows 上带 <see cref="FileAttributes.ReadOnly"/> 的**目录本身**
    /// 删不掉（实测 <c>Directory.Delete(recursive: true)</c> 报 "Access to the path ... is denied"，
    /// 即使里面每个文件都可写）。测试判据在断言里，清理失败不该把用例判成失败。
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = CreateRoot();
        }

        public string Path { get; }

        /// <summary>
        /// 多级回落：沙箱环境里 .NET 子进程可能不允许在 <c>%TEMP%</c> 下建目录
        /// （与 <c>CoreAssetsPaths</c> 的 <c>CreateRootDirectory</c> 同一套判据）。
        /// </summary>
        private static string CreateRoot()
        {
            var name = "nebula-mirror-test-" + Guid.NewGuid().ToString("N")[..8];

            foreach (var baseDirectory in new[]
                     {
                         System.IO.Path.GetTempPath(),
                         AppContext.BaseDirectory,
                         Directory.GetCurrentDirectory(),
                     })
            {
                try
                {
                    var candidate = System.IO.Path.Combine(baseDirectory, name);
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch (Exception)
                {
                    // 当前候选目录不可写，换下一个。
                }
            }

            throw new InvalidOperationException("无法创建测试临时目录");
        }

        public void Dispose()
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                // 目录自身的只读位同样要复位，否则删目录这一步会被拒绝。
                File.SetAttributes(Path, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录残留不影响结论。
            }
        }
    }
}