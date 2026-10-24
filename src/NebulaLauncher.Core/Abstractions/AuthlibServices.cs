namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// authlib-injector 的本地准备与 JVM 参数生成。
///
/// 第三方验证（统一通行证 / 皮肤站）不能只靠账号密码：游戏客户端本身不认识这些服务器，
/// 必须把 authlib-injector 作为 javaagent 注入，并把 API 根的元数据预取后塞进系统属性。
/// 抄 PCL2 <c>ModLaunch.vb:1101-1138</c> 的三参数形态。
/// </summary>
public interface IAuthlibInjectorService
{
    /// <summary>最近一次失败的用户可读原因（中文）；成功或未请求时为 null。</summary>
    string? LastError { get; }

    /// <summary>
    /// 确保本地有可用的 <c>authlib-injector.jar</c>，返回绝对路径；失败返回 null 并写 <see cref="LastError"/>。
    ///
    /// 实现必须：优先用本地缓存（带版本号），版本清单拉不到时退化为已有的任意版本；
    /// 下载到临时文件 + 校验 SHA-256 后再落盘，避免半个 jar 被当成可用。
    /// </summary>
    Task<string?> EnsureInjectorAsync(
        string apiRoot,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成注入了 authlib-injector 的完整 JVM 参数片段。
    ///
    /// 形态：<c>-javaagent:&lt;jar&gt;=&lt;apiRoot&gt; -Dauthlibinjector.yggdrasil.prefetched=&lt;base64&gt;</c>，
    /// 其中 base64 是对 <c>GET &lt;apiRoot&gt;</c> 原始响应的 Base64 编码。
    /// 失败返回 null（调用方应阻止启动并给出 <see cref="LastError"/>，而不是让游戏拿不到角色）。
    /// </summary>
    Task<Models.AuthlibInjection?> BuildInjectionAsync(
        string apiRoot,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
