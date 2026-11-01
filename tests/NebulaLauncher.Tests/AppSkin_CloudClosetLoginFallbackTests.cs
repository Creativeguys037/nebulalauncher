using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Skinning;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 登录链路的异常边界（票 #62）：站点返回非 JSON 时，异常必须在 ViewModel 的上边界
/// 变成错误文案，不能穿透 AsyncRelayCommand 把进程带走。
/// </summary>
public sealed class AppSkin_CloudClosetLoginFallbackTests
{
    private const string Site = "https://example.com";

    [Fact]
    public async Task 登录响应不是JSON时不崩溃并给出错误文案()
    {
        using var handler = new HtmlLoginHandler();
        using var http = new HttpClient(handler);
        using var vm = CreateVm(http);
        vm.Username = "me";
        vm.Password = "pw";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.False(vm.IsLoggedIn);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("皮肤站登录过程出错", vm.ErrorText!);
    }

    private static CloudClosetViewModel CreateVm(HttpClient http)
    {
        var client = new CloudClosetClient(http: http, baseAddress: Site);
        return new CloudClosetViewModel(new FakeLauncherPaths(CreateRootDirectory()), new FakePlatformService(), client: client);
    }

    /// <summary>沙箱里 %TEMP% 可能不可写，按测试套件的既有做法逐级回落。</summary>
    private static string CreateRootDirectory()
    {
        var name = "nebula-login-" + Guid.NewGuid().ToString("N")[..10];
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

        throw new InvalidOperationException("找不到可写的测试目录。");
    }

    private sealed class HtmlLoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Method == HttpMethod.Get
                ? "<meta name=\"csrf-token\" content=\"tok\">"
                : "<html>login</html>";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            };
            return Task.FromResult(response);
        }
    }
}
