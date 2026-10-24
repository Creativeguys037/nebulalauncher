using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Net;

/// <summary>连通性测试的失败分类。界面据此给「DNS 解析失败 / 连接被拒 / 超时 / 需要认证 / TLS」的具体结论。</summary>
public enum NetworkProbeFailure
{
    /// <summary>请求成功（2xx/3xx）。</summary>
    None = 0,

    /// <summary>域名解析失败：多半是 DNS 被污染或没网。</summary>
    Dns,

    /// <summary>连接被拒绝或网络不可达：本机没有监听、或代理地址/端口填错。</summary>
    Refused,

    /// <summary>超时：链路慢、被防火墙丢弃，或代理不通。</summary>
    Timeout,

    /// <summary>代理要求认证（407）：用户名/密码缺失或错误。</summary>
    ProxyAuth,

    /// <summary>TLS 握手失败：证书问题、中间设备劫持，或代理不支持 CONNECT。</summary>
    Tls,

    /// <summary>拿到响应但状态码不是成功值。</summary>
    Http,

    /// <summary>无法归类。</summary>
    Unknown,
}

/// <summary>一个探测端点。</summary>
public sealed record NetworkProbeTarget(string Name, string Url);

/// <summary>一个端点的探测结果。</summary>
public sealed record NetworkProbeResult(
    string Name,
    string Url,
    bool Ok,
    int? StatusCode,
    TimeSpan Elapsed,
    NetworkProbeFailure Failure,
    string Detail);

/// <summary>
/// 「测试连通性」按钮的 Core 侧实现。
///
/// 用项目真实依赖的端点（Fabric meta / BMCLAPI / Modrinth API）而不是 example.com：
/// 用户要确认的是「启动器能不能下东西」，对一个和本项目无关的站点通不代表下载能走。
///
/// 请求走 <c>HttpClient</c> + 显式 <c>SocketsHttpHandler.Proxy</c>，因此可以拿「设置页里还没保存的草稿」
/// 去探测，不需要先写进全局 <see cref="HttpClient.DefaultProxy"/>。
/// </summary>
public static class NetworkProbe
{
    /// <summary>默认探测端点。改动这里等于改动按钮的行为，顺序即界面上的展示顺序。</summary>
    public static IReadOnlyList<NetworkProbeTarget> Targets { get; } =
    [
        new NetworkProbeTarget("Fabric Meta", "https://meta.fabricmc.net/v2/versions/loader/1.20.1"),
        new NetworkProbeTarget("BMCLAPI", "https://bmclapi2.bangbang93.com/forge/promos"),
        new NetworkProbeTarget("Modrinth API", "https://api.modrinth.com/v2/search?limit=1"),
    ];

    /// <summary>按给定设置（通常是界面草稿）并发探测全部端点。取消令牌只影响未开始的请求。</summary>
    public static async Task<IReadOnlyList<NetworkProbeResult>> RunAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var proxy = ProxyConfigurator.Build(settings);

        using var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
        };

        using var client = new HttpClient(handler)
        {
            // 实测这三个端点在直连下 1 秒内出结果；12 秒足够区分「慢」与「不通」。
            Timeout = TimeSpan.FromSeconds(12),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NebulaLauncher/1.0");

        var results = await Task.WhenAll(
                Targets.Select(target => ProbeAsync(client, target, cancellationToken)))
            .ConfigureAwait(false);

        return results;
    }

    /// <summary>探测单个端点。失败一律返回分类结果，不抛异常（取消除外）。</summary>
    public static async Task<NetworkProbeResult> ProbeAsync(
        HttpClient client,
        NetworkProbeTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);

        var watch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            watch.Stop();
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                return new NetworkProbeResult(
                    target.Name, target.Url, false, status, watch.Elapsed,
                    NetworkProbeFailure.ProxyAuth, "代理返回 407，需要认证");
            }

            if (status >= 200 && status < 400)
            {
                return new NetworkProbeResult(
                    target.Name, target.Url, true, status, watch.Elapsed,
                    NetworkProbeFailure.None, string.Empty);
            }

            return new NetworkProbeResult(
                target.Name, target.Url, false, status, watch.Elapsed,
                NetworkProbeFailure.Http, $"HTTP {status}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient 的 Timeout 到点抛 TaskCanceledException（.NET 8 起带 TimeoutException 内层）。
            watch.Stop();
            return new NetworkProbeResult(
                target.Name, target.Url, false, null, watch.Elapsed,
                NetworkProbeFailure.Timeout, "请求超时");
        }
        catch (Exception ex)
        {
            watch.Stop();
            var (kind, detail) = Classify(ex);
            return new NetworkProbeResult(
                target.Name, target.Url, false, null, watch.Elapsed, kind, detail);
        }
    }

    /// <summary>
    /// 沿 <see cref="Exception.InnerException"/> 链分类。
    ///
    /// 优先用 .NET 8 的 <see cref="HttpRequestException.HttpRequestError"/>：它把
    /// 「DNS 失败 / 连接失败 / TLS 失败 / 代理隧道失败」直接区分开了，比解析 SocketException 稳。
    /// 拿不到分类信息（自定义 handler、旧包装）时再退回 SocketException / AuthenticationException。
    /// </summary>
    private static (NetworkProbeFailure Kind, string Detail) Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException request)
            {
                var kind = request.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => NetworkProbeFailure.Dns,
                    HttpRequestError.ConnectionError => NetworkProbeFailure.Refused,
                    HttpRequestError.SecureConnectionError => NetworkProbeFailure.Tls,
                    HttpRequestError.ProxyTunnelError => NetworkProbeFailure.ProxyAuth,
                    HttpRequestError.UserAuthenticationError => NetworkProbeFailure.ProxyAuth,
                    _ => NetworkProbeFailure.Unknown,
                };

                if (kind != NetworkProbeFailure.Unknown)
                {
                    return (kind, request.HttpRequestError.ToString());
                }
            }

            if (current is AuthenticationException authentication)
            {
                return (NetworkProbeFailure.Tls, authentication.Message);
            }

            if (current is SocketException socket)
            {
                var kind = socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => NetworkProbeFailure.Dns,
                    SocketError.ConnectionRefused => NetworkProbeFailure.Refused,
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => NetworkProbeFailure.Refused,
                    SocketError.TimedOut => NetworkProbeFailure.Timeout,
                    _ => NetworkProbeFailure.Unknown,
                };

                return (kind, $"Socket {socket.SocketErrorCode}");
            }
        }

        return (NetworkProbeFailure.Unknown, exception.Message);
    }
}