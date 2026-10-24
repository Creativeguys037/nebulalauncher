using System.Net;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 加载器 meta 的「候选地址 + 失败换源」。
///
/// 背景：用户机器访问 meta.fabricmc.net 会随机 DNS 失败（SocketException 11001），
/// 单地址请求会让「安装 Fabric」直接失败在第一步。这里验证候选列表与回退顺序。
/// </summary>
public class CoreDownloads_LoaderMetaTests
{
    [Fact]
    public void Fabric在镜像模式下镜像优先()
    {
        var urls = LoaderMetaUrls.Versions(LoaderKind.Fabric, "1.20.1", DownloadSource.BmclApi);

        Assert.Equal(2, urls.Count);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/fabric-meta/", urls[0]);
        Assert.StartsWith("https://meta.fabricmc.net/", urls[1]);
    }

    [Fact]
    public void Fabric在官方模式下官方优先但保留镜像兜底()
    {
        var urls = LoaderMetaUrls.Versions(LoaderKind.Fabric, "1.20.1", DownloadSource.Official);

        Assert.Equal(2, urls.Count);
        Assert.StartsWith("https://meta.fabricmc.net/", urls[0]);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/fabric-meta/", urls[1]);
    }

    [Fact]
    public void Quilt没有镜像只给官方地址()
    {
        var urls = LoaderMetaUrls.Versions(LoaderKind.Quilt, "1.20.1", DownloadSource.BmclApi);

        Assert.Single(urls);
        Assert.StartsWith("https://meta.quiltmc.org/", urls[0]);
    }

    [Fact]
    public void Forge没有meta地址()
    {
        Assert.Empty(LoaderMetaUrls.Versions(LoaderKind.Forge, "1.20.1", DownloadSource.BmclApi));
        Assert.Empty(LoaderMetaUrls.Profile(LoaderKind.Forge, "1.20.1", "47.2.0", DownloadSource.BmclApi));
    }

    [Fact]
    public void Profile地址包含版本号且镜像优先()
    {
        var urls = LoaderMetaUrls.Profile(LoaderKind.Fabric, "1.20.1", "0.16.9", DownloadSource.BmclApi);

        Assert.Equal(2, urls.Count);
        Assert.Contains("/1.20.1/0.16.9/profile/json", urls[0]);
        Assert.Contains("fabric-meta", urls[0]);
    }

    [Fact]
    public async Task 首个地址失败时自动换下一个()
    {
        var handler = new StubHandler(request =>
        {
            // 第一个候选（mirror）失败，第二个（official）成功。
            if (request.RequestUri!.Host.Contains("mirror", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"loader\":{\"version\":\"0.16.9\",\"stable\":true}}]"),
            };
        });

        using var client = new HttpClient(handler);
        var urls = new[] { "https://mirror.example.invalid/a", "https://official.example.invalid/b" };

        var body = await ResourceFetcher.GetStringAsync(client, urls, CancellationToken.None, log: null, attemptsPerUrl: 1);

        Assert.Contains("0.16.9", body);
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public async Task 全部地址失败时抛出带各地址原因的异常()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var client = new HttpClient(handler);
        var urls = new[] { "https://a.example.invalid/x", "https://b.example.invalid/y" };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ResourceFetcher.GetStringAsync(client, urls, CancellationToken.None, log: null, attemptsPerUrl: 1));

        Assert.Contains("a.example.invalid", ex.Message);
        Assert.Contains("b.example.invalid", ex.Message);
    }

    [Fact]
    public async Task 空候选列表直接失败()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResourceFetcher.GetStringAsync(client, Array.Empty<string>(), CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requested)
            {
                if (request.RequestUri is not null)
                {
                    Requested.Add(request.RequestUri);
                }
            }

            return Task.FromResult(respond(request));
        }
    }
}
