namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 补全本地内容的「在线身份」：项目名、slug、摘要、图标，以及据此查出的 MC 百科译名。
///
/// <para/>
/// 覆盖模组 / 资源包 / 光影包 / 数据包：资源站对四类内容都有项目，
/// 而包内嵌图标只是少数，资源站图标才是卡片能显示图标的常态。
///
/// <para/>
/// 与 <see cref="IModUpdateService"/> 分开的理由：更新检查必须实时（有新版就要立刻看到），
/// 而项目名 / 译名 / 图标几乎不变，可以放心缓存 6 小时。混在一个接口里会让两者共用一套失效策略，
/// 结果要么更新状态过期，要么每次进页面都重新拉几十个图标。
/// </summary>
public interface IModProjectInfoService
{
    /// <summary>最近一次整体失败的原因（中文）。逐条未识别不算失败。</summary>
    string? LastError { get; }

    /// <summary>
    /// 反查并补全一批本地内容的在线信息。返回顺序与 <paramref name="items"/> 对应。
    ///
    /// 只处理磁盘上真实存在的文件：数据包的目录形态算不出哈希，会被跳过而不是产生一条空结果。
    /// 不抛异常：网络失败写 <see cref="LastError"/> 并返回已拿到的部分结果。
    /// </summary>
    Task<Models.ModProjectLookupResult> EnrichAsync(
        IReadOnlyList<Models.ContentItem> items,
        string gameDirectory,
        CancellationToken cancellationToken = default);
}
