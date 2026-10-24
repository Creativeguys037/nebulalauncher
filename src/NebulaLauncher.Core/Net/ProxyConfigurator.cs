using System.Net;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Net;

/// <summary>
/// 把 <see cref="AppSettings"/> 里的代理配置应用到进程级 <see cref="HttpClient.DefaultProxy"/>。
///
/// 为什么改 DefaultProxy 而不是给每个客户端塞 <c>handler.Proxy</c>：
/// 本项目的 HttpClient 分散在 Downloads / Mods / Launching 等模块（共 5 处
/// <c>new HttpClientHandler</c>），逐个改要动别人的文件，而且以后新加的客户端一定会漏。
/// DefaultProxy 是「没有显式给 handler.Proxy 赋值」的 handler 的取值来源，改一处即覆盖全部。
///
/// 生效时机：DefaultProxy 在建立连接池时读取，所以
/// 1) 还没有发过请求的 HttpClient —— 立即生效；
/// 2) 已经建好连接池的客户端 —— 已有连接不重建，但它之后新建的连接池（换主机、池被回收）会用新代理。
/// 设置页因此文案写成「保存后对后续请求生效」，不承诺「打断正在进行的那次下载」。
/// </summary>
public static class ProxyConfigurator
{
    /// <summary>
    /// 进程启动时的系统代理（.NET 的实现会读环境变量与 Windows 的 IE 设置）。
    ///
    /// .NET 没有公开 API 能重新构造那个实现，只能在类型第一次被访问时抓一份留用；
    /// 「跟随系统」模式把它原样赋回去，等价于「从未改过 DefaultProxy」。
    /// 静态字段的初始化先于本类任何方法的执行，因此这里抓到的必定是未被本类改过的值。
    /// </summary>
    private static readonly IWebProxy SystemProxy = HttpClient.DefaultProxy ?? DirectConnectionProxy.Instance;

    /// <summary>
    /// 按设置构造代理对象，不改全局状态。连通性测试用它在「未保存的草稿」上探测。
    /// </summary>
    public static IWebProxy Build(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.ProxyMode switch
        {
            // 「不走代理」必须是一个显式的「全都直连」代理：不赋值等于继续跟随环境变量，
            // 而用户选「无」的语义恰恰是「别管环境变量里那些 HTTP_PROXY」。
            ProxyModes.None => DirectConnectionProxy.Instance,
            ProxyModes.Custom => BuildCustom(settings),
            _ => SystemProxy,
        };
    }

    /// <summary>按设置更新 <see cref="HttpClient.DefaultProxy"/>，返回实际生效的代理对象。</summary>
    public static IWebProxy Apply(AppSettings settings)
    {
        var proxy = Build(settings);
        HttpClient.DefaultProxy = proxy;
        return proxy;
    }

    private static IWebProxy BuildCustom(AppSettings settings)
    {
        if (!ProxyAddressParser.TryParse(settings.ProxyAddress, out var address))
        {
            // Normalize 已保证「自定义」模式必定有可解析地址；走到这里说明设置是在运行期被手改的，
            // 退回系统代理比硬塞一个坏地址更接近用户预期（坏地址只会让所有请求超时）。
            return SystemProxy;
        }

        var proxy = new WebProxy(address);

        if (!string.IsNullOrEmpty(settings.ProxyUserName))
        {
            proxy.Credentials = new NetworkCredential(
                settings.ProxyUserName,
                settings.ProxyPassword ?? string.Empty);
        }

        return proxy;
    }

    /// <summary>
    /// 「直连」代理：告诉 HttpClient「目标地址不需要代理」。
    ///
    /// <see cref="WebProxy"/> 的 Address 为 null 时也有类似效果，但语义依赖实现细节；
    /// 显式实现 <see cref="IWebProxy"/> 才能保证 <c>UseProxy = true</c> 的 handler 也不转发任何请求。
    /// </summary>
    private sealed class DirectConnectionProxy : IWebProxy
    {
        public static readonly DirectConnectionProxy Instance = new();

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => destination;

        public bool IsBypassed(Uri host) => true;
    }
}