using System.Net;
using System.Text;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Tests;

/// <summary>
/// <see cref="ResourceFetcher.GetStringAsync"/> 改成 <c>ResponseHeadersRead</c> + 自读 body 之后的语义。
///
/// 为什么改：<c>GetStringAsync</c> 内部是 <c>ResponseContentRead</c>，整个响应（含 body）只有
/// <c>HttpClient.Timeout</c> 一个兜底（本项目 10 分钟）。而镜像换源靠的是 handler 层的**响应头**
/// 超时窗口（镜像 5 秒 / 官方 30 秒）——头快、body 慢的镜像会让那个窗口完全失效。
///
/// 改法引入两个新的出错位置，全部钉在这里：
/// <list type="bullet">
/// <item>手写解码必须显式读 <c>Content-Type</c> 的 charset，不能退化成 UTF-8；</item>
/// <item>读 body 阶段的取消/超时也要落进「请求超时」分支，不能被当成用户取消。</item>
/// </list>
/// </summary>
public sealed class CoreDownloads_ResourceFetcherTests
{
    private const string Url = "https://meta.example.com/version.json";

    // ==================== 正常路径 ====================

    [Fact]
    public async Task 正常返回JSON内容()
    {
        using var client = CreateClient((_, _) => Json("""{"id":"1.20.1"}""", Encoding.UTF8, charset: "utf-8"));

        var text = await ResourceFetcher.GetStringAsync(client, [Url], CancellationToken.None, attemptsPerUrl: 1);

        Assert.Equal("""{"id":"1.20.1"}""", text);
    }

    [Fact]
    public async Task 没有charset时按UTF8解码()
    {
        using var client = CreateClient((_, _) => Json("中文内容", Encoding.UTF8, charset: null));

        var text = await ResourceFetcher.GetStringAsync(client, [Url], CancellationToken.None, attemptsPerUrl: 1);

        Assert.Equal("中文内容", text);
    }

    /// <summary>
    /// 镜像站返回 gb18030 是真实存在的形态。手写 <c>StreamReader</c> 不指定编码就会解成乱码，
    /// 然后 JSON 解析失败——用户看到的报错是「JSON 无效」，跟真正的原因（编码）毫无关系。
    /// </summary>
    [Fact]
    public async Task 按ContentType的charset解码而不是一律UTF8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var client = CreateClient((_, _) => Json("中文内容", Encoding.GetEncoding("gb18030"), charset: "gb18030"));

        var text = await ResourceFetcher.GetStringAsync(client, [Url], CancellationToken.None, attemptsPerUrl: 1);

        Assert.Equal("中文内容", text);
    }

    [Fact]
    public async Task 未知charset名退回UTF8而不抛异常()
    {
        // 有些镜像会写不认识的名字。退回 UTF-8 最多是乱码后解析失败，
        // 抛异常会把「能拿到内容」变成「一条道都走不通」。
        using var client = CreateClient((_, _) => Json("ok", Encoding.UTF8, charset: "totally-bogus-encoding"));

        var text = await ResourceFetcher.GetStringAsync(client, [Url], CancellationToken.None, attemptsPerUrl: 1);

        Assert.Equal("ok", text);
    }

    // ==================== 失败路径 ====================

    /// <summary>
    /// 非 2xx 必须抛 <c>HttpRequestException</c> 且消息里带状态码。
    /// 原来靠 <c>GetStringAsync</c> 抛；改成手写之后靠 <c>EnsureSuccessStatusCode</c> 保住，
    /// 很容易在重构时被顺手删掉。
    /// </summary>
    [Fact]
    public async Task 非2xx抛出带状态码的异常()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => ResourceFetcher.GetStringAsync(client, [Url], CancellationToken.None, attemptsPerUrl: 1));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Url, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 读 body 阶段抛出的取消（<c>HttpClient.Timeout</c> 到点的效果）必须落进「请求超时」分支。
    ///
    /// 这是改成 headers 模式后**新出现**的失败位置：等头与读 body 各有一段，两段的超时对用户
    /// 是同一件事。如果漏了这条，body 慢的地址会一条道走到黑，最后报「所有下载地址均失败」却
    /// 只字不提超时。
    ///
    /// 用 content 直接抛 <c>OperationCanceledException</c> 模拟——真的等 HttpClient 超时需要
    /// 把 <c>Timeout</c> 调到毫秒级再等，得到的异常类型与这里一致，但用例会不稳定。
    /// </summary>
    [Fact]
    public async Task 读body阶段超时归到请求超时而不是用户取消()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new CancellingContent(),
        }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => ResourceFetcher.GetStringAsync(client, [Url], CancellationToken.None, attemptsPerUrl: 1));

        Assert.Contains("请求超时", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用户主动取消要原样抛出，不能吞成「请求超时」再换个地址重试——那样 CancellationToken
    /// 就失去了「立刻停手」的意义。
    /// </summary>
    [Fact]
    public async Task 用户取消原样抛出()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResourceFetcher.GetStringAsync(client, [Url], cts.Token, attemptsPerUrl: 1));
    }

    // ==================== 多候选 ====================

    [Fact]
    public async Task 第一个候选404第二个成功()
    {
        var calls = 0;
        var first = Url;
        var second = "https://mirror.example.com/version.json";

        using var client = CreateClient((request, _) =>
        {
            calls++;
            return request.RequestUri!.AbsoluteUri == first
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Json("""{"ok":true}""", Encoding.UTF8, charset: "utf-8");
        });

        var text = await ResourceFetcher.GetStringAsync(client, [first, second], CancellationToken.None, attemptsPerUrl: 1);

        Assert.Equal("""{"ok":true}""", text);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 全部候选失败时异常里带每个地址()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ResourceFetcher.GetStringAsync(
            client,
            [Url, "https://mirror.example.com/version.json"],
            CancellationToken.None,
            attemptsPerUrl: 1));

        Assert.Contains("所有下载地址均失败", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Url, ex.Message, StringComparison.Ordinal);
        Assert.Contains("mirror.example.com", ex.Message, StringComparison.Ordinal);
    }

    // ==================== 辅助 ====================

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        new(new StubHandler(responder), disposeHandler: true);

    private static Task<HttpResponseMessage> Json(string body, Encoding encoding, string? charset)
    {
        var content = new ByteArrayContent(encoding.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        if (!string.IsNullOrEmpty(charset))
        {
            content.Headers.ContentType.CharSet = charset;
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }

    /// <summary>模拟「响应头到了、读 body 时取消」——<c>HttpClient.Timeout</c> 到点的表现。</summary>
    private sealed class CancellingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(new OperationCanceledException("模拟 HttpClient.Timeout 到点"));
    }
}