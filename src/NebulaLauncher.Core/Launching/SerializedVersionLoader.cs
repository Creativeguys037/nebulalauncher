using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 把 CmlLib 的版本清单求值串行化的装饰器（票 27 缺陷 A）。
///
/// ## 为什么需要它
/// CmlLib 4.1.0 的 <c>MojangJsonVersionLoaderV2</c> 每次 <c>GetVersionMetadatasAsync</c> 都会
/// <c>File.Create</c> 重写 <c>&lt;.minecraft&gt;/versions/version_manifest_v2.json</c>——
/// 它不做「有缓存就跳过」。而 <see cref="MinecraftLauncherFactory.Create"/> 每次都 new 一个
/// <c>MinecraftLauncher</c>，每个实例各持一个 loader，进程内没有任何跨调用互斥。
///
/// 于是只要同一时刻有两条链在跑，后到的 <c>File.Create</c> 就撞 <c>ERROR_SHARING_VIOLATION</c>，
/// 表现为安装以 <c>IOException: The process cannot access the file ... because it is being
/// used by another process</c> 失败。四条链都会碰它：安装（<c>InstallService</c>）、
/// 启动补全（<see cref="GameLauncher.LaunchAsync"/>）、命令行构建（<see cref="GameLauncher.BuildCommandLineAsync"/>）、
/// 修复（<c>InstallService.Repair</c>）。<c>InstallService._installGate</c> 只串行化它自己，
/// 另外三条根本不经过它。
///
/// ## 为什么是「串行化」而不是「换成本地解析」
/// 换成 <c>LocalJsonVersionLoader</c> 能彻底不写盘，但原版安装与「装一个新版本」都需要
/// 远程清单才知道版本 json 的下载地址（本地 <c>versions/</c> 里还没有那个目录）。
/// 所以必须保留远程能力，只把「同一时刻只能有一个在写这个缓存文件」这条约束补上。
///
/// ## 边界
/// 只串行化「加载器求值」这一小段（网络 + 写清单，通常几百毫秒），**不**串行化文件下载——
/// 否则安装会退化成单线程，等于用缺陷 A 的修法制造出票 27 缺陷 B 那种「下载慢」。
///
/// 不做结果缓存：缓存会让「用户手工放进去的新版本」看不见，
/// 也会让「刚装完的版本在下一个 launcher 里消失」这种时序问题变得难查。
/// </summary>
/// <param name="inner">被包装的真实加载器（库默认组合，含本地与远程两路）。</param>
internal sealed class SerializedVersionLoader(IVersionLoader inner) : IVersionLoader
{
    /// <summary>
    /// 进程级闸门。必须是静态的：锁的对象是磁盘上那一个缓存文件，
    /// 而每个 <see cref="MinecraftLauncher"/> 实例各持一个装饰器，实例字段锁不住它们。
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IVersionLoader _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public async ValueTask<VersionMetadataCollection> GetVersionMetadatasAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inner.GetVersionMetadatasAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
