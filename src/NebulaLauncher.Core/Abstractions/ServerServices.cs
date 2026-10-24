namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 服务端（<c>server.jar</c>）下载。
///
/// 数据来源是 Mojang 版本清单里每个版本的 <c>downloads.server</c>：不是所有版本都有
/// （旧版本、部分快照没有），所以列表接口返回的是「确实可下载」的子集。
/// 不做服务端安装与配置（Paper / Forge 服务端不在此列），也不生成 <c>eula.txt</c>。
/// </summary>
public interface IServerDownloadService
{
    /// <summary>
    /// 列出可下载服务端的版本，按发行时间降序。只含有 <c>downloads.server</c> 的版本。
    /// 网络不可用且没有缓存时返回空集合，不抛异常。
    /// </summary>
    Task<IReadOnlyList<ServerVersionInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载某个版本的服务端到 <c>&lt;targetDirectory&gt;/server-&lt;versionId&gt;.jar</c>。
    ///
    /// 落盘后校验 SHA1；校验失败或下载失败都会删除目标文件（不留半截文件）。
    /// 目标文件已存在且 SHA1 正确时直接返回「已存在」，不发网络请求。
    /// 下载源走 <c>DownloadHttpClientFactory</c>（镜像优先、失败回退官方）。
    /// </summary>
    Task<ServerDownloadResult> DownloadAsync(
        string versionId,
        string targetDirectory,
        IProgress<Models.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 带缓存来源的列表快照。与 <see cref="ListAsync"/> 的区别是它还会告诉调用方
    /// 「这批数据来自缓存还是刚扫描的」「是否有后台刷新在跑」，UI 才能给出准确的等待提示。
    ///
    /// 默认实现退化为一次完整查询（来源记为 Network），这样旧实现方不需要改代码。
    /// </summary>
    /// <param name="forceRefresh">true 时绕过缓存，强制重新扫描。</param>
    /// <param name="fullScan">true 时扫描全部版本而不是默认窗口（可能几十秒）。</param>
    async Task<ServerListSnapshot> ListSnapshotAsync(
        bool forceRefresh = false,
        bool fullScan = false,
        CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(cancellationToken).ConfigureAwait(false);
        return new ServerListSnapshot { Items = items, Source = ServerListSource.Network };
    }

    /// <summary>
    /// 等待正在进行的后台刷新（stale-while-revalidate）完成并返回新快照；
    /// 没有在跑的后台刷新时发起一次强制刷新。
    ///
    /// UI 拿到「来自缓存 + 正在后台更新」的快照后调用它，就不需要轮询。
    /// 默认实现退化为强制刷新。
    /// </summary>
    async Task<ServerListSnapshot> EnsureRefreshedAsync(CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(cancellationToken).ConfigureAwait(false);
        return new ServerListSnapshot { Items = items, Source = ServerListSource.Network };
    }
}

/// <summary>一份服务端版本清单的来源，用于 UI 提示。</summary>
public enum ServerListSource
{
    /// <summary>没有可用数据（网络失败且无缓存）。</summary>
    None = 0,

    /// <summary>来自落盘缓存 / 内存缓存。</summary>
    Cache = 1,

    /// <summary>本次刚扫描得到。</summary>
    Network = 2,
}

/// <summary>带来源信息的服务端版本清单快照。</summary>
public sealed class ServerListSnapshot
{
    public required IReadOnlyList<ServerVersionInfo> Items { get; init; }

    public ServerListSource Source { get; init; }

    /// <summary>缓存写入时间（UTC）；<see cref="ServerListSource.Network"/> 时是本次扫描完成的时刻。</summary>
    public DateTimeOffset? CachedAt { get; init; }

    /// <summary>有后台刷新在进行：UI 应显示「正在后台更新清单…」，并可用 <c>EnsureRefreshedAsync</c> 等待结果。</summary>
    public bool IsRefreshing { get; init; }

    /// <summary>清单里最新正式版的 id，作为缓存失效判据。</summary>
    public string? LatestRelease { get; init; }

    /// <summary>这份列表是否覆盖了「扫描全部」的完整集合。</summary>
    public bool CoversAll { get; init; }

    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// 一个可下载服务端的版本。
///
/// 放在契约文件里而不是 <c>Models/</c>：这两个 DTO 只服务 <see cref="IServerDownloadService"/>，
/// 独立文件可以让「服务端下载」这一块不被其它模块的模型改动牵连。
/// </summary>
public sealed class ServerVersionInfo
{
    public required string Id { get; init; }

    /// <summary>Mojang 清单里的原始类型：<c>release</c> / <c>snapshot</c> / <c>old_beta</c> / <c>old_alpha</c>。</summary>
    public string Type { get; init; } = "release";

    public DateTimeOffset ReleaseTime { get; init; }

    /// <summary>官方给出的大小（字节）；0 表示清单没写。</summary>
    public long Size { get; init; }

    /// <summary>官方给出的 SHA1（大写十六进制）。</summary>
    public string? Sha1 { get; init; }

    public string? Url { get; init; }

    /// <summary>发行渠道，供 UI 显示「正式版 / 快照 / 旧版」。</summary>
    public Models.VersionChannel Channel => Type switch
    {
        "snapshot" => Models.VersionChannel.Snapshot,
        "old_beta" => Models.VersionChannel.OldBeta,
        "old_alpha" => Models.VersionChannel.OldAlpha,
        _ => Models.VersionChannel.Release,
    };
}

/// <summary>一次服务端下载的结果。失败时 <see cref="Error"/> 带原因，且目标文件已被清理。</summary>
public sealed class ServerDownloadResult
{
    public required string VersionId { get; init; }

    /// <summary>目标路径（即使失败也会填，便于用户知道尝试写到了哪里）。</summary>
    public required string FilePath { get; init; }

    /// <summary>落盘后的实际大小；0 表示没写成功。</summary>
    public long Size { get; init; }

    /// <summary>实际计算出的 SHA1；失败或未下载时为 null。</summary>
    public string? Sha1 { get; init; }

    /// <summary>文件已存在且校验通过，本次没有下载。</summary>
    public bool AlreadyExisted { get; init; }

    public bool Success { get; init; }

    public string? Error { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 一次 <c>servers.dat</c> 导入的统计。
///
/// 放在契约文件里而不是 <c>Models/</c>：它只服务 <see cref="IServerService"/> 的导入能力，
/// 和 <see cref="ServerVersionInfo"/> 同一个理由——独立出来不让其它模块的模型改动牵连到它。
/// </summary>
public sealed class ServerImportSummary
{
    /// <summary>被读取的 <c>servers.dat</c> 绝对路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>文件是否存在。false 时 <see cref="Total"/> 必然为 0，界面应提示「没有找到 servers.dat」。</summary>
    public bool FileExists { get; init; }

    /// <summary>从文件里读到的有效条目数（已丢掉没有地址的坏条目）。</summary>
    public int Total { get; init; }

    /// <summary>本次真正加进收藏的条目数。</summary>
    public int Added { get; init; }

    /// <summary>地址已在收藏里、因此被跳过的条目数（含 <c>servers.dat</c> 内部的重复地址）。</summary>
    public int Skipped { get; init; }
}

/// <summary>
/// GameSpy4 Query（UDP）通道：拿服务器完整状态与**完整玩家名单**。
///
/// 与 <c>IServerService.PingAsync</c> 的分工：SLP 走 TCP、只给在线人数；
/// Query 走同一端口的 UDP、能给出在线玩家的名字。两者互不替代，Query 关闭的服务器
/// （<c>enable-query=false</c>）依然能 ping 通，只是看不到名单——所以本接口失败时也返回
/// 可读的分类结果，不抛异常。
///
/// 入口只接受单次查询：调用方必须在用户点击时逐台发起，不要对收藏列表批量轮询。
/// Query 是无认证的 UDP 请求，短时间内对大量地址发包会被服务端与网络设备判定为扫描。
/// </summary>
public interface IServerQueryService
{
    /// <summary>
    /// 查询一台服务器的完整状态与玩家名单。
    ///
    /// 超时（默认 2 秒 × 2 次尝试）、无响应、Query 关闭、协议不符都通过
    /// <see cref="Models.ServerQueryResult.Outcome"/> 表达，不抛异常（取消除外）。
    /// 缺省实现返回 <see cref="ServerQueryOutcome.Unsupported"/>，让没有这条通道的实现方不必改代码。
    /// </summary>
    Task<Models.ServerQueryResult> QueryAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.ServerQueryResult.Unsupported(host, port));
}
