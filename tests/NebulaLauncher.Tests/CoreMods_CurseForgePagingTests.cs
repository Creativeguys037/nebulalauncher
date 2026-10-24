using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// CurseForge 文件列表的翻页取全。
///
/// 旧实现只发一次 <c>index=0&amp;pageSize=50</c>，JEI（238222）这类项目的历史版本在列表里根本不存在。
/// 这里的判据不是「发了几次请求」，而是「最后拿到的集合是不是全集」——
/// 所以每个用例都在假接口上把「服务端每页上限」调成 50，逼着实现真的翻页。
/// </summary>
public class CoreMods_CurseForgePagingTests
{
    private const long ModId = 238222;

    [Fact]
    public async Task 文件数超过单页上限时翻页取全()
    {
        // 137 条、服务端每页只给 50 → 必须是 3 次请求、137 条结果，而不是 50 条。
        var handler = new PagingHandler(totalCount: 137, serverCap: 50);
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, "1.20.1", LoaderKind.Forge);

        Assert.Equal(137, files.Count);
        Assert.Equal(3, handler.Urls.Count);

        // index 必须按服务端实际返回的条数推进，而不是按请求的 pageSize。
        Assert.Contains("index=0", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("index=50", handler.Urls[1], StringComparison.Ordinal);
        Assert.Contains("index=100", handler.Urls[2], StringComparison.Ordinal);

        // 请求里带的是大页（1000），服务端截断到 50 也能取全——这就是「对上限不敏感」。
        Assert.Contains("pageSize=1000", handler.Urls[0], StringComparison.Ordinal);

        AssertAllIdsUnique(files);
        Assert.Equal(137, files.Select(file => file.Id).Distinct().Count());
    }

    [Fact]
    public async Task 服务端接受大页时只发一次请求()
    {
        // 快速路径：resultCount == totalCount 时第一页就该收工。
        var handler = new PagingHandler(totalCount: 137, serverCap: 1000);
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        Assert.Equal(137, files.Count);
        Assert.Single(handler.Urls);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task 接口忽略index时去重并停止翻页()
    {
        // 有的镜像 / 缓存层会无视 index，永远返回同一批。不去重就会重复 137 条里的一批，
        // 而只按「已取到 137 条」判停会永远停不下来。
        var handler = new PagingHandler(totalCount: 137, serverCap: 50) { IgnoreIndex = true };
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        Assert.Equal(50, files.Count);
        AssertAllIdsUnique(files);

        // 第 2 页全都是见过的 Id → 立刻停，不应该把 200 页上限耗尽。
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains(log.Warnings, message => message.Contains("没有任何新文件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 跨页重复行按文件Id去重()
    {
        // 分页期间有并发上传时，CF 会把上一页的尾巴再吐一次（PCL2 issue #1330）。
        var handler = new PagingHandler(totalCount: 137, serverCap: 50) { Overlap = 5 };
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        AssertAllIdsUnique(files);
        Assert.Equal(137, files.Count);
    }

    [Fact]
    public async Task 缺少分页信息时退回单页行为()
    {
        var handler = new PagingHandler(totalCount: 137, serverCap: 50) { OmitPagination = true };
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        // 不知道还有没有下一页，就只能交出一页；但必须记 Warn，别静默截断。
        Assert.Equal(50, files.Count);
        Assert.Single(handler.Urls);
        Assert.Contains(log.Warnings, message => message.Contains("分页信息", StringComparison.Ordinal));
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task totalCount为零时退回单页行为()
    {
        var handler = new PagingHandler(totalCount: 137, serverCap: 50) { ReportedTotalCount = 0 };
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        Assert.Equal(50, files.Count);
        Assert.Single(handler.Urls);
        Assert.Contains(log.Warnings, message => message.Contains("分页信息", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 翻页拼接后仍按发布时间全局降序()
    {
        var handler = new PagingHandler(totalCount: 137, serverCap: 50);
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        Assert.Equal(137, files.Count);

        // 各页内部有序不代表拼接后有序：这里要求全局严格降序。
        for (var i = 1; i < files.Count; i++)
        {
            var previous = files[i - 1].FileDate ?? DateTimeOffset.MinValue;
            var current = files[i].FileDate ?? DateTimeOffset.MinValue;

            Assert.True(previous >= current, $"第 {i} 条比前一条新：{previous:O} < {current:O}");
        }
    }

    [Fact]
    public async Task 后续页失败时返回已取到的部分并写原因()
    {
        // 从 index=50 起一直 500（GetStringAsync 对 5xx 会重试一次，所以实际会打两次同一个 URL）。
        var handler = new PagingHandler(totalCount: 137, serverCap: 50) { FailWhenIndexAtLeast = 50 };
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        // 已经拿到的一页比什么都没有有用；但 LastError 要能说明后面失败了。
        Assert.Equal(50, files.Count);
        Assert.Contains(handler.Urls, url => url.Contains("index=50", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
    }

    [Fact]
    public async Task 第一页失败时返回空且不继续翻页()
    {
        var handler = new PagingHandler(totalCount: 137, serverCap: 50) { FailWhenIndexAtLeast = 0 };
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        Assert.Empty(files);

        // 只在第一页上重试，不能翻到 index=50 去。
        Assert.All(handler.Urls, url => Assert.Contains("index=0", url, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
    }

    [Fact]
    public async Task 页数上限阻止无限翻页()
    {
        // totalCount 报得离谱大，且每页都是新文件：必须靠页数上限停下并记 Warn，
        // 否则就是对着 CF 打到天荒地老。
        var handler = new PagingHandler(totalCount: 10_000_000, serverCap: 50);
        var log = new RecordingLog();
        using var service = CreateService(log, handler);

        var files = await service.GetFilesAsync(ModId, null, LoaderKind.Vanilla);

        Assert.Equal(50 * 200, files.Count);
        Assert.Equal(200, handler.Urls.Count);
        Assert.Contains(log.Warnings, message => message.Contains("翻页上限", StringComparison.Ordinal));
    }

    // ==================== 辅助 ====================

    private static CurseForgeService CreateService(RecordingLog log, HttpMessageHandler handler) =>
        new(log, paths: null, settings: new PagingSettings(), handler: handler);

    private static void AssertAllIdsUnique(IReadOnlyList<CurseForgeFile> files)
    {
        var ids = new HashSet<long>();

        foreach (var file in files)
        {
            Assert.True(ids.Add(file.Id), $"文件 Id {file.Id} 出现了两次");
        }
    }

    /// <summary>只内存持有的设置服务。API key 已内置，这里只留一个空快照。</summary>
    private sealed class PagingSettings : ISettingsService
    {
        public PagingSettings() => Current = new AppSettings();

        public AppSettings Current { get; }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>把 Warn 攒起来的日志替身：好几条「降级」行为只能靠日志证明发生过。</summary>
    private sealed class RecordingLog : IAppLog
    {
        public List<string> Warnings { get; } = [];

        public List<string> Errors { get; } = [];

        public string LogFilePath => "memory://test";

        public void Debug(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message, Exception? ex = null)
        {
            lock (Warnings)
            {
                Warnings.Add(message);
            }
        }

        public void Error(string message, Exception? ex = null)
        {
            lock (Errors)
            {
                Errors.Add(message);
            }
        }
    }

    /// <summary>
    /// 假 CF 文件列表接口。它把「服务端每页上限」做成可配置的，逼着被测实现去翻页，
    /// 而不是假设请求多少就返回多少。
    /// </summary>
    private sealed class PagingHandler : HttpMessageHandler
    {
        private readonly long _totalCount;

        public PagingHandler(long totalCount, int serverCap)
        {
            _totalCount = totalCount;
            ServerCap = serverCap;
        }

        /// <summary>服务端每页真正给出的上限（模拟文档写的 50，或更大）。</summary>
        public int ServerCap { get; }

        /// <summary>无视 index，永远返回第一页。</summary>
        public bool IgnoreIndex { get; init; }

        /// <summary>响应里不写 pagination 字段。</summary>
        public bool OmitPagination { get; init; }

        /// <summary>覆盖上报的 totalCount（null 表示按 <see cref="_totalCount"/>）。</summary>
        public long? ReportedTotalCount { get; init; }

        /// <summary>每页回退几条，模拟并发上传导致的跨页重复。</summary>
        public int Overlap { get; init; }

        /// <summary>第 N 次请求返回 HTTP 500（1 起算）。</summary>
        public int? FailAtRequest { get; init; }

        /// <summary>index 达到这个值的请求一律 500，用来模拟「翻到一半接口挂了」。</summary>
        public int? FailWhenIndexAtLeast { get; init; }

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri?.ToString() ?? string.Empty;

            lock (Urls)
            {
                Urls.Add(url);
            }

            var attempt = Urls.Count;
            var requested = QueryInt(url, "pageSize") ?? 50;
            var index = IgnoreIndex ? 0 : QueryInt(url, "index") ?? 0;

            if (FailAtRequest == attempt
                || (FailWhenIndexAtLeast is { } threshold && index >= threshold))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
                });
            }

            var take = Math.Min(requested, ServerCap);

            // 起点回退 = 模拟「上一页的尾巴又出现了一次」。
            var start = index > 0 && Overlap > 0 ? Math.Max(0, index - Overlap) : index;

            var count = (int)Math.Max(0, Math.Min(take, _totalCount - start));
            var files = new List<object>(count);

            for (var i = 0; i < count; i++)
            {
                // Id 从 1 起：真实 CF 的 fileId 不会为 0，而解析层会把 id <= 0 当成坏行丢掉。
                files.Add(FileNode(start + i + 1));
            }

            object body = OmitPagination
                ? new { data = files }
                : new
                {
                    data = files,
                    pagination = new
                    {
                        index,
                        pageSize = count,
                        resultCount = count,
                        totalCount = ReportedTotalCount ?? _totalCount,
                    },
                };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>Id 越大越新，便于验证「拼接后仍按时间降序」。</summary>
        private static object FileNode(long id) => new
        {
            id,
            modId = ModId,
            fileName = $"file-{id}.jar",
            displayName = $"File {id}",
            releaseType = 1,
            fileDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMinutes(id)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            fileLength = 1024 + id,
            downloadUrl = $"https://edge.forgecdn.net/files/{id}/file-{id}.jar",
            gameVersions = new[] { "1.20.1", "Forge" },
        };

        private static int? QueryInt(string url, string name)
        {
            var marker = name + "=";
            var start = url.IndexOf(marker, StringComparison.Ordinal);

            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = url.IndexOf('&', start);

            if (end < 0)
            {
                end = url.Length;
            }

            return int.TryParse(url[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }
}