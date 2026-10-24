namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 按安装清单检查一个版本由哪些组件组成、各组件名下的受管文件还在不在。
///
/// 与「修复」的分工：本接口**只读盘、不下载、不改清单**。修复要联网且会写盘，
/// 用户先想知道「到底是哪个组件坏了」，再决定要不要修——这两件事不能绑在一个按钮上。
///
/// 判据与 <c>InstallService</c> 写清单 / <see cref="Installing.McInstallManifest"/> 复核时一致：
/// 文件不存在 = 缺失；文件在但 mtime 与安装时记录差超过容差 = 被改动过。
/// </summary>
public interface IComponentInspector
{
    /// <summary>
    /// 检查 <paramref name="versionName"/> 对应的版本目录。没有清单时返回
    /// <see cref="ComponentInspectionReport.HasManifest"/> = false 与可读原因，而不是空报告。
    /// 除取消除外不抛异常。
    /// </summary>
    Task<ComponentInspectionReport> InspectAsync(
        string versionName,
        CancellationToken cancellationToken = default);
}

/// <summary>一个组件的检查结果。计数是清单里该组件名下受管文件的真实统计。</summary>
public sealed record ComponentStatus(
    string Name,
    string? Version,
    string Kind,
    int FileCount,
    int MissingCount,
    int ModifiedCount,
    DateTimeOffset? LastModifiedUtc)
{
    /// <summary>该组件名下没有缺失也没有被改动的文件。</summary>
    public bool IsHealthy => MissingCount == 0 && ModifiedCount == 0;

    /// <summary>有问题的文件总数（缺失 + 改动）。</summary>
    public int IssueCount => MissingCount + ModifiedCount;
}

/// <summary>
/// 一次组件检查的结果。
///
/// <see cref="MissingFiles"/> / <see cref="ModifiedFiles"/> 最多各列 50 条（大版本有上千个库文件，
/// 全量塞进 UI 既慢又没人看），但 <see cref="MissingCount"/> / <see cref="ModifiedCount"/> 是完整计数。
/// UI 用 <see cref="IsTruncated"/> 决定要不要显示「仅列出前 50 条」。
/// </summary>
public sealed record ComponentInspectionReport(
    string VersionName,
    bool HasManifest,
    string? Message,
    IReadOnlyList<ComponentStatus> Components,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> ModifiedFiles,
    int MissingCount,
    int ModifiedCount,
    int ManagedFileCount)
{
    /// <summary>清单存在且所有受管文件都在、都没有被动过。</summary>
    public bool IsHealthy => HasManifest && MissingCount == 0 && ModifiedCount == 0;

    /// <summary>有缺失或被改动的文件（不代表清单缺失）。</summary>
    public bool HasFindings => HasManifest && (MissingCount > 0 || ModifiedCount > 0);

    /// <summary>问题文件超过 50 条，列表被截断。</summary>
    public bool IsTruncated => MissingCount > MissingFiles.Count || ModifiedCount > ModifiedFiles.Count;

    public int ComponentCount => Components.Count;

    /// <summary>顶部汇总文案。清单缺失时就是原因，UI 不用自己判断该显示哪一句。</summary>
    public string Summary => !HasManifest
        ? Message ?? "该版本没有安装清单，无法按组件检查。"
        : IsHealthy
            ? $"共 {ComponentCount} 个组件，{ManagedFileCount} 个受管文件全部完好。"
            : MissingCount > 0 && ModifiedCount > 0
                ? $"共 {ComponentCount} 个组件，{MissingCount} 个文件缺失、{ModifiedCount} 个文件被改动。"
                : MissingCount > 0
                    ? $"共 {ComponentCount} 个组件，{MissingCount} 个文件缺失。"
                    : $"共 {ComponentCount} 个组件，{ModifiedCount} 个文件被改动。";

    /// <summary>清单缺失 / 版本目录不存在时的报告。</summary>
    public static ComponentInspectionReport Unavailable(string versionName, string message) =>
        new(versionName, false, message, Array.Empty<ComponentStatus>(),
            Array.Empty<string>(), Array.Empty<string>(), 0, 0, 0);
}
