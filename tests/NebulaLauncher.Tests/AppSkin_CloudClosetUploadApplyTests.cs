using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Skinning;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「从皮肤页把网络皮肤上传并应用到皮肤站角色」的编排断言（票 #61）。
///
/// <para>
/// 锁的是外部可见的请求顺序与文案：POST /texture → PUT /user/player/{pid}/textures →
/// GET /user/player/list。重复 tid 不再上传，未登录/多角色短路时不发任何写请求。
/// 不碰 Avalonia 控件树，也不测站点本身（那是 <see cref="CloudClosetClient"/> 的契约）。
/// </para>
/// </summary>
public sealed class AppSkin_CloudClosetUploadApplyTests
{
    private const string Site = "https://example.com";

    [Fact]
    public async Task 没登录时不发请求并提示先登录()
    {
        using var handler = new ScriptedHandler(RequestResponse.Default);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        var result = await vm.UploadAndApplyAsync("夏日皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.NeedsLogin);
        Assert.False(result.Succeeded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 多个角色且没选角色时提示先选并不上传()
    {
        using var handler = new ScriptedHandler(RequestResponse.Default);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(1, "Steve", "uuid-1", null, null));
        vm.Players.Add(new CloudPlayer(2, "Alex", "uuid-2", null, null));

        var result = await vm.UploadAndApplyAsync("夏日皮肤", Png(), isSlim: false, isPublic: true);

        Assert.False(result.Succeeded);
        Assert.Contains("多个角色", result.Text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 上传后应用并按角色列表确认结果()
    {
        using var handler = new ScriptedHandler(RequestResponse.Default);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(1, "Steve", "uuid-1", null, null));

        var result = await vm.UploadAndApplyAsync("夏日皮肤", Png(), isSlim: true, isPublic: false);

        Assert.True(result.Succeeded);
        Assert.Equal(123, result.Tid);
        Assert.Equal("Steve", result.PlayerName);
        Assert.Contains("已应用到皮肤站", result.Text);
        Assert.Contains("#123", result.Text);

        var writes = handler.Requests.Where(r => r.Path != "/auth/login").ToArray();
        Assert.Collection(
            writes,
            post =>
            {
                Assert.Equal(HttpMethod.Post, post.Method);
                Assert.Equal("/texture", post.Path);
                Assert.Contains("alex", post.Body);
                Assert.Contains("name=public", post.Body);
            },
            put =>
            {
                Assert.Equal(HttpMethod.Put, put.Method);
                Assert.Equal("/user/player/1/textures", put.Path);
                Assert.Contains("\"skin\":123", put.Body);
            },
            list =>
            {
                Assert.Equal(HttpMethod.Get, list.Method);
                Assert.Equal("/user/player/list", list.Path);
            });
    }

    [Fact]
    public async Task 重复tid不重复上传并直接应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.Duplicate);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.Succeeded);
        Assert.Equal(77, result.Tid);

        var writes = handler.Requests.Where(r => r.Path != "/auth/login").ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Single(writes, r => r.Path == "/texture");
        Assert.Contains(writes, r => r.Path == "/user/player/7/textures" && r.Body.Contains("\"skin\":77"));
    }

    [Fact]
    public async Task 站点拒绝上传时原文透传且不应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.Rejected);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(1, "Steve", "uuid-1", null, null));

        var result = await vm.UploadAndApplyAsync("坏图", Png(), isSlim: false, isPublic: true);

        Assert.False(result.Succeeded);
        Assert.Contains("图片太大了", result.Text);
        Assert.True(result.CanRetry);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task 上传时登录过期要求重新登录且不提示重试()
    {
        using var handler = new ScriptedHandler(RequestResponse.Unauthorized);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(1, "Steve", "uuid-1", null, null));

        var result = await vm.UploadAndApplyAsync("皮肤", Png(), isSlim: false, isPublic: true);

        Assert.False(result.Succeeded);
        Assert.True(result.NeedsLogin);
        Assert.False(result.CanRetry);
        Assert.Contains("登录", result.Text);
    }

    private static CloudClosetViewModel CreateVm(HttpClient http)
    {
        var client = new CloudClosetClient(http: http, baseAddress: Site);
        return new CloudClosetViewModel(new FakeLauncherPaths(CreateRootDirectory()), new FakePlatformService(), client: client);
    }

    /// <summary>沙箱里 %TEMP% 可能不可写，按测试套件的既有做法逐级回落。</summary>
    private static string CreateRootDirectory()
    {
        var name = "nebula-cape-" + Guid.NewGuid().ToString("N")[..10];
        foreach (var baseDirectory in new[]
                 {
                     Path.GetTempPath(),
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                 })
        {
            try
            {
                var candidate = Path.Combine(baseDirectory, name);
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception)
            {
                // 当前候选目录不可写，换下一个。
            }
        }

        throw new InvalidOperationException("找不到可写的测试临时目录。");
    }

    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    private enum RequestResponse
    {
        Default,
        Duplicate,
        Rejected,
        Unauthorized,
    }

    private sealed record RequestLog(HttpMethod Method, string Path, string Body);

    /// <summary>按路径应答的站点替身；同时记录方法、路径与请求体，供顺序断言。</summary>
    private sealed class ScriptedHandler(RequestResponse mode) : HttpMessageHandler
    {
        public List<RequestLog> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestLog(request.Method, path, body));

            if (path == "/auth/login")
            {
                return Html("<html><head><meta name=\"csrf-token\" content=\"csrf-1\"></head></html>");
            }

            if (path == "/texture")
            {
                return mode switch
                {
                    RequestResponse.Duplicate => Json("{\"code\":2,\"data\":{\"tid\":77},\"message\":\"已存在\"}"),
                    RequestResponse.Rejected => Json("{\"code\":1,\"message\":\"图片太大了\"}"),
                    RequestResponse.Unauthorized => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                    _ => Json("{\"code\":0,\"data\":{\"tid\":123},\"message\":\"上传成功\"}"),
                };
            }

            if (path.StartsWith("/user/player/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                return Json("{\"code\":0,\"message\":\"ok\"}");
            }

            if (path == "/user/player/list")
            {
                return Json("{\"data\":[{\"pid\":1,\"name\":\"Steve\",\"uuid\":\"uuid-1\",\"tid_skin\":123,\"tid_cape\":null}]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }
}
