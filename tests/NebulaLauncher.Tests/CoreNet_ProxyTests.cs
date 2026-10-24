using System.Net;
using System.Net.Http;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Net;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「网络代理」设置的持久化与生效判据。
///
/// 为什么单独测：
/// 1) 代理配错的后果是「启动器静默连不上网」，而界面上看不出任何异常——
///    所以落库值必须能在加载后被断言，而不是只信界面上选中的那张卡片；
/// 2) 「自定义」模式但没有可用地址时必须回落 <c>system</c>，否则会得到
///    「选了代理、实际直连」这种没法排查的组合；
/// 3) 「不走代理」必须真的盖掉环境变量，这是用户选「无」时的唯一语义。
/// 全部断言不联网：只有 <see cref="NetworkProbe"/> 的异常分类用了假 handler。
/// </summary>
public class CoreNet_ProxyTests
{
    [Fact]
    public void Defaults_FollowSystemProxy()
    {
        var settings = new AppSettings();

        // 默认必须是 system：以前启动器从不碰 DefaultProxy，等价于跟随环境。
        Assert.Equal(ProxyModes.System, settings.ProxyMode);
        Assert.Null(settings.ProxyAddress);
        Assert.Null(settings.ProxyUserName);
        Assert.Null(settings.ProxyPassword);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Load_UnknownMode_FallsBackToSystem(string mode)
    {
        using var temp = new TempDirectory();
        var service = await LoadAsync(temp, $"{{\"proxyMode\":\"{mode}\"}}");

        Assert.Equal(ProxyModes.System, service.Current.ProxyMode);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("not a url")]
    public async Task Load_CustomWithUnusableAddress_FallsBackToSystem(string address)
    {
        using var temp = new TempDirectory();
        var service = await LoadAsync(temp, $"{{\"proxyMode\":\"custom\",\"proxyAddress\":\"{address}\"}}");

        Assert.Equal(ProxyModes.System, service.Current.ProxyMode);
        Assert.Null(service.Current.ProxyAddress);

        // 连 address 字段都没有时同一条回落规则（等价于旧配置里的「手填了 custom 没填地址」）。
        using var missing = new TempDirectory();
        var missingService = await LoadAsync(missing, "{\"proxyMode\":\"custom\"}");

        Assert.Equal(ProxyModes.System, missingService.Current.ProxyMode);
        Assert.Null(missingService.Current.ProxyAddress);
    }

    [Theory]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("http://127.0.0.1:7890/", "http://127.0.0.1:7890")]
    [InlineData("https://proxy.corp.example:8443/path?x=1", "https://proxy.corp.example:8443")]
    public async Task Load_CustomAddress_IsNormalized(string raw, string expected)
    {
        using var temp = new TempDirectory();
        var service = await LoadAsync(
            temp,
            $"{{\"proxyMode\":\"custom\",\"proxyAddress\":\"{raw}\"}}");

        Assert.Equal(ProxyModes.Custom, service.Current.ProxyMode);
        Assert.Equal(expected, service.Current.ProxyAddress);
    }

    [Fact]
    public async Task Load_CustomWithCredentials_KeepsUserAndPassword()
    {
        using var temp = new TempDirectory();
        var service = await LoadAsync(
            temp,
            """
            {
              "proxyMode": "custom",
              "proxyAddress": "http://10.0.0.2:3128",
              "proxyUserName": "  alice  ",
              "proxyPassword": "p@ss word "
            }
            """);

        Assert.Equal("alice", service.Current.ProxyUserName);
        // 密码不 Trim：首尾空格可能是密码的一部分。
        Assert.Equal("p@ss word ", service.Current.ProxyPassword);

        // 只有密码、没有用户名时，密码必须被清掉——否则设置页会显示「一个填了内容的密码框」。
        using var temp2 = new TempDirectory();
        var service2 = await LoadAsync(
            temp2,
            """
            {
              "proxyMode": "custom",
              "proxyAddress": "http://10.0.0.2:3128",
              "proxyPassword": "orphan"
            }
            """);

        Assert.Null(service2.Current.ProxyUserName);
        Assert.Null(service2.Current.ProxyPassword);
    }

    [Fact]
    public void Clone_CopiesProxyFields()
    {
        var original = new AppSettings
        {
            ProxyMode = ProxyModes.Custom,
            ProxyAddress = "http://127.0.0.1:7890",
            ProxyUserName = "alice",
            ProxyPassword = "secret",
        };

        var clone = original.Clone();

        Assert.Equal(ProxyModes.Custom, clone.ProxyMode);
        Assert.Equal("http://127.0.0.1:7890", clone.ProxyAddress);
        Assert.Equal("alice", clone.ProxyUserName);
        Assert.Equal("secret", clone.ProxyPassword);
    }

    [Fact]
    public void Apply_None_MakesDefaultProxyBypassEverything()
    {
        var before = HttpClient.DefaultProxy;

        try
        {
            var applied = ProxyConfigurator.Apply(new AppSettings { ProxyMode = ProxyModes.None });
            var target = new Uri("https://meta.fabricmc.net/v2/versions/loader/1.20.1");

            // 「无」必须是显式的直连对象：只做到「不赋值」会继续跟随 HTTP_PROXY 环境变量。
            Assert.True(applied.IsBypassed(target));
            Assert.Same(applied, HttpClient.DefaultProxy);
            Assert.True(HttpClient.DefaultProxy.IsBypassed(target));
        }
        finally
        {
            // 全局状态必须还原，否则后续测试会继承一个「全部直连」的进程级代理。
            HttpClient.DefaultProxy = before;
        }
    }

    [Fact]
    public void Build_Custom_UsesAddressAndCredentials()
    {
        var settings = new AppSettings
        {
            ProxyMode = ProxyModes.Custom,
            ProxyAddress = "http://127.0.0.1:7890",
            ProxyUserName = "alice",
            ProxyPassword = "secret",
        };

        var proxy = ProxyConfigurator.Build(settings);

        var webProxy = Assert.IsType<WebProxy>(proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890/"), webProxy.Address);
        Assert.False(webProxy.IsBypassed(new Uri("https://api.modrinth.com/v2/search?limit=1")));

        var credential = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("alice", credential.UserName);
        Assert.Equal("secret", credential.Password);

        // 不带用户名的自定义代理不该塞一个空凭据：空凭据会让部分代理回 407 而不是直放。
        var anonymous = ProxyConfigurator.Build(new AppSettings
        {
            ProxyMode = ProxyModes.Custom,
            ProxyAddress = "127.0.0.1:7890",
        });

        Assert.Null(Assert.IsType<WebProxy>(anonymous).Credentials);
    }

    [Fact]
    public async Task ProbeAsync_NameResolutionFailure_IsClassifiedAsDns()
    {
        using var handler = new FailingHandler(
            new HttpRequestException(HttpRequestError.NameResolutionError, "name resolution failed"));
        using var client = new HttpClient(handler);

        var result = await NetworkProbe.ProbeAsync(
            client,
            new NetworkProbeTarget("Fake", "https://example.invalid/probe"));

        Assert.False(result.Ok);
        Assert.Equal(NetworkProbeFailure.Dns, result.Failure);
        Assert.Equal("NameResolutionError", result.Detail);
    }

    private static async Task<SettingsService> LoadAsync(TempDirectory temp, string json)
    {
        var paths = new FakeLauncherPaths(temp.Path);
        await File.WriteAllTextAsync(paths.SettingsFile, json);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();
        return service;
    }

    /// <summary>只抛固定异常的 handler：探测的失败分类不需要真实网络。</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public FailingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(_exception);
    }
}