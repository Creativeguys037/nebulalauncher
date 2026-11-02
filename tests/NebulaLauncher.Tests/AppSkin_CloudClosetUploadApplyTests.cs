using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Skinning;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「从皮肤页把网络皮肤上传并应用到皮肤站角色」的编排断言（票 #61、#63）。
///
/// <para>
/// 锁的是外部可见的请求顺序与文案：POST /texture →（重复 tid 时 GET /user/closet/ids，
/// 不在衣柜再 POST /user/closet）→ PUT /user/player/{pid}/textures → GET /user/player/list。
/// 重复 tid 不再上传；未登录/多角色短路时不发任何写请求。
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
        Assert.Contains("扣除站点积分", result.Text);

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
    public async Task 重复tid已在衣柜时不再收藏直接应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicateInCloset);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.Succeeded);
        Assert.Equal(77, result.Tid);

        var writes = handler.Requests.Where(r => r.Path != "/auth/login").ToArray();
        Assert.Collection(
            writes,
            post => Assert.Equal("/texture", post.Path),
            ids => Assert.Equal("/user/closet/ids", ids.Path),
            put =>
            {
                Assert.Equal(HttpMethod.Put, put.Method);
                Assert.Equal("/user/player/7/textures", put.Path);
            },
            list => Assert.Equal("/user/player/list", list.Path));
        Assert.DoesNotContain(writes, r => r.Path == "/user/closet");
        Assert.Contains(writes, r => r.Path == "/user/player/7/textures" && r.Body.Contains("\"skin\":77"));
    }

    [Fact]
    public async Task 重复tid不在衣柜时先收藏再应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicateNotInCloset);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;

        // 先走一次衣柜刷新，把 _hasLoaded 置为 true；应用成功后应再刷新第一页，
        // 这样从云端衣柜页回来就能看到刚收藏的材质。
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Players.Clear();
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));
        vm.SelectedPlayer = vm.Players[0];
        handler.Requests.Clear();

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.Succeeded);
        Assert.Equal(77, result.Tid);
        Assert.Contains("扣除站点积分", result.Text);

        var calls = handler.Requests
            .Where(r => r.Path != "/auth/login"
                        && !r.Path.StartsWith("/texture/", StringComparison.Ordinal)
                        && !r.Path.StartsWith("/textures/", StringComparison.Ordinal)
                        && !r.Path.StartsWith("/preview/", StringComparison.Ordinal))
            .ToArray();
        Assert.Collection(
            calls,
            post => Assert.Equal("/texture", post.Path),
            ids =>
            {
                Assert.Equal(HttpMethod.Get, ids.Method);
                Assert.Equal("/user/closet/ids", ids.Path);
            },
            add =>
            {
                Assert.Equal(HttpMethod.Post, add.Method);
                Assert.Equal("/user/closet", add.Path);
                using var document = JsonDocument.Parse(add.Body);
                Assert.Equal(77, document.RootElement.GetProperty("tid").GetInt32());
                Assert.Equal("旧皮肤", document.RootElement.GetProperty("name").GetString());
            },
            put =>
            {
                Assert.Equal(HttpMethod.Put, put.Method);
                Assert.Equal("/user/player/7/textures", put.Path);
                using var document = JsonDocument.Parse(put.Body);
                Assert.Equal(77, document.RootElement.GetProperty("skin").GetInt32());
            },
            list => Assert.Equal("/user/player/list", list.Path),
            closet =>
            {
                Assert.Equal(HttpMethod.Get, closet.Method);
                Assert.Equal("/user/closet/list", closet.Path);
            });
    }

    [Fact]
    public async Task 重复tid收藏失败时不应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicateAddRejected);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.False(result.Succeeded);
        Assert.Contains("积分不足", result.Text);
        Assert.Contains("自动加入失败", result.Text);
        Assert.True(result.CanRetry);

        var calls = handler.Requests.Where(r => r.Path != "/auth/login").ToArray();
        var idsIndex = Array.FindIndex(calls, r => r.Path == "/user/closet/ids");
        var addIndex = Array.FindIndex(calls, r => r.Path == "/user/closet");
        Assert.True(idsIndex >= 0, "应先查询衣柜归属。");
        Assert.True(addIndex > idsIndex, "只有确认不在衣柜后才应尝试加入衣柜。");
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task 收藏成功但应用失败时说明材质已入衣柜并刷新()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicateApplyRejected);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Players.Clear();
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));
        vm.SelectedPlayer = vm.Players[0];

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.False(result.Succeeded);
        Assert.Contains("材质已加入你的衣柜并扣除站点积分", result.Text);
        Assert.Equal("没有应用材质。", vm.StatusText);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/user/closet/list"));
        Assert.Equal("没有应用材质。", vm.StatusText);
    }

    [Fact]
    public async Task 收藏后应用被取消时说明材质已入衣柜并刷新()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new ScriptedHandler(RequestResponse.DuplicateApplyCancelled, cts.Cancel);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Players.Clear();
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));
        vm.SelectedPlayer = vm.Players[0];

        var result = await vm.UploadAndApplyAsync(
            "旧皮肤",
            Png(),
            isSlim: false,
            isPublic: true,
            cts.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("操作已取消", result.Text);
        Assert.Contains("材质已经加入你的衣柜并扣除站点积分", result.Text);
        Assert.Equal("没有应用材质。", vm.StatusText);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/user/closet/list"));
    }

    [Fact]
    public async Task 加入衣柜遇到竞态时重查并继续应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicateAddRejectedButRaced);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/user/closet/ids"));
        Assert.Equal(1, handler.Requests.Count(r => r.Path == "/user/closet" && r.Method == HttpMethod.Post));
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task 应用成功后刷新衣柜保留用户选中的材质()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicatePreserveSelection);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Players.Clear();
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));
        vm.SelectedPlayer = vm.Players[0];
        vm.SelectedItem = vm.Items.Single(item => item.Tid == 88);

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.Succeeded);
        Assert.Equal(1, vm.Page);
        Assert.Equal(88, vm.SelectedItem?.Tid);
        Assert.True(
            handler.Requests.Count(r => r.Path == "/user/closet/list") == 2,
            "应用成功后应重新拉取衣柜，而不是只保留旧列表。");
    }

    [Fact]
    public async Task 应用成功后刷新衣柜遇到登录失效时不静默登出()
    {
        using var handler = new ScriptedHandler(RequestResponse.ClosetRefreshUnauthorized);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Players.Clear();
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));
        vm.SelectedPlayer = vm.Players[0];

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.True(result.Succeeded);
        Assert.True(vm.IsLoggedIn, "后台刷新衣柜失败不应把已经登录的用户静默登出。");
        Assert.True(
            handler.Requests.Count(r => r.Path == "/user/closet/list") == 2,
            "登录失效也应来自应用后的第二次刷新，而不是没有刷新。");
    }

    [Fact]
    public async Task 查询衣柜失败时不应用()
    {
        using var handler = new ScriptedHandler(RequestResponse.DuplicateIdsUnavailable);
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);

        vm.IsLoggedIn = true;
        vm.Players.Add(new CloudPlayer(7, "Alex", "uuid-7", null, null));

        var result = await vm.UploadAndApplyAsync("旧皮肤", Png(), isSlim: false, isPublic: true);

        Assert.False(result.Succeeded);
        Assert.Contains("皮肤站暂时不可用", result.Text);
        Assert.True(result.CanRetry);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/user/closet");
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
        DuplicateInCloset,
        DuplicateNotInCloset,
        DuplicateAddRejected,
        DuplicateIdsUnavailable,
        DuplicatePreserveSelection,
        DuplicateApplyRejected,
        DuplicateApplyCancelled,
        DuplicateAddRejectedButRaced,
        ClosetRefreshUnauthorized,
        Rejected,
        Unauthorized,
    }

    private sealed record RequestLog(HttpMethod Method, string Path, string Body);

    /// <summary>按路径应答的站点替身；同时记录方法、路径与请求体，供顺序断言。</summary>
    private sealed class ScriptedHandler(RequestResponse mode, Action? beforeApply = null) : HttpMessageHandler
    {
        public List<RequestLog> Requests { get; } = [];

        private int _closetListCalls;
        private int _closetIdsCalls;

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
                    RequestResponse.DuplicateInCloset
                        or RequestResponse.DuplicateNotInCloset
                        or RequestResponse.DuplicateAddRejected
                        or RequestResponse.DuplicateIdsUnavailable
                        or RequestResponse.DuplicatePreserveSelection
                        or RequestResponse.DuplicateApplyRejected
                        or RequestResponse.DuplicateApplyCancelled
                        or RequestResponse.DuplicateAddRejectedButRaced
                        or RequestResponse.ClosetRefreshUnauthorized
                        => Json("{\"code\":2,\"data\":{\"tid\":77},\"message\":\"已存在\"}"),
                    RequestResponse.Rejected => Json("{\"code\":1,\"message\":\"图片太大了\"}"),
                    RequestResponse.Unauthorized => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                    _ => Json("{\"code\":0,\"data\":{\"tid\":123},\"message\":\"上传成功\"}"),
                };
            }

            if (path == "/user/closet/ids")
            {
                if (mode == RequestResponse.DuplicateAddRejectedButRaced)
                {
                    _closetIdsCalls++;
                    return Json(_closetIdsCalls == 1 ? "[]" : "[77]");
                }

                return mode switch
                {
                    RequestResponse.DuplicateInCloset => Json("[77]"),
                    RequestResponse.DuplicatePreserveSelection => Json("[77]"),
                    RequestResponse.ClosetRefreshUnauthorized => Json("[77]"),
                    RequestResponse.DuplicateNotInCloset or RequestResponse.DuplicateAddRejected => Json("[]"),
                    RequestResponse.DuplicateIdsUnavailable => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    _ => Json("[]"),
                };
            }

            if (path == "/user/closet" && request.Method == HttpMethod.Post)
            {
                return mode switch
                {
                    RequestResponse.DuplicateAddRejected => Json("{\"code\":1,\"message\":\"积分不足\"}"),
                    RequestResponse.DuplicateAddRejectedButRaced => Json("{\"code\":1,\"message\":\"已经在衣柜里了\"}"),
                    _ => Json("{\"code\":0,\"message\":\"已加入衣柜\"}"),
                };
            }

            if (path == "/user/closet/list")
            {
                _closetListCalls++;
                if (mode == RequestResponse.ClosetRefreshUnauthorized && _closetListCalls > 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                if (mode == RequestResponse.DuplicatePreserveSelection)
                {
                    return Json(
                        """{"current_page":1,"last_page":1,"data":[{"tid":77,"name":"旧皮肤","type":"steve","hash":"hash-77","likes":0,"public":true},{"tid":88,"name":"我的皮肤","type":"steve","hash":"hash-88","likes":0,"public":true}]}""");
                }

                return Json(
                    "{\"current_page\":1,\"last_page\":1,\"data\":[{\"tid\":77,\"name\":\"旧皮肤\",\"type\":\"steve\",\"hash\":\"hash-77\",\"likes\":0,\"public\":true}]}");
            }

            if (path.StartsWith("/user/player/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                if (mode == RequestResponse.DuplicateApplyCancelled)
                {
                    beforeApply?.Invoke();
                    throw new OperationCanceledException();
                }

                return mode == RequestResponse.DuplicateApplyRejected
                    ? Json("{\"code\":1,\"message\":\"角色不存在\"}")
                    : Json("{\"code\":0,\"message\":\"ok\"}");
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
