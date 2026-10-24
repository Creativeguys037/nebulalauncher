namespace NebulaLauncher.Core.Models;

/// <summary>
/// 存储统计类别的稳定键。
///
/// 为什么用英文键而不是显示名：键要跨语言、跨版本、进日志、进单测断言；
/// 显示名（「下载缓存」「日志」）随时可能改文案，键一旦变就会让历史记录与调用方对不上。
/// </summary>
public static class StorageCategoryKeys
{
    /// <summary>下载缓存与图标 / 头像缓存：<c>&lt;data&gt;/cache</c>，不含安装器临时目录。</summary>
    public const string Cache = "cache";

    /// <summary>启动器日志：<c>&lt;data&gt;/logs</c>。</summary>
    public const string Logs = "logs";

    /// <summary>加载器安装器临时目录：<c>&lt;data&gt;/cache/loader-install</c>。</summary>
    public const string Installer = "installer";

    /// <summary>实例回收站与内容回收站：<c>&lt;data&gt;/trash</c>（两者同根，合并统计）。</summary>
    public const string Trash = "trash";

    /// <summary>全部类别，按界面展示顺序。</summary>
    public static IReadOnlyList<string> All { get; } = [Cache, Logs, Installer, Trash];
}

/// <summary>
/// 一个存储类别的扫描结果。
/// </summary>
/// <param name="Key">稳定键，见 <see cref="StorageCategoryKeys"/>。</param>
/// <param name="DisplayName">界面显示名。</param>
/// <param name="Path">该类别的绝对路径。</param>
/// <param name="TotalBytes">目录下全部文件字节数之和。</param>
/// <param name="FileCount">文件数（不计目录）。</param>
/// <param name="OldestWriteTime">最旧文件最后写入时间；没有任何文件时为 <c>null</c>。</param>
/// <param name="CanClean">是否允许清理。手写 / 他人放置的目录可能被标成不可清理。</param>
/// <param name="CleanHint">清理说明文案，直接显示在卡片上。</param>
public sealed record StorageCategoryUsage(
    string Key,
    string DisplayName,
    string Path,
    long TotalBytes,
    int FileCount,
    DateTimeOffset? OldestWriteTime,
    bool CanClean,
    string CleanHint)
{
    /// <summary>空目录或无权限时的零值结果。</summary>
    public static StorageCategoryUsage Empty(
        string key,
        string displayName,
        string path,
        bool canClean,
        string cleanHint) =>
        new(key, displayName, path, 0, 0, null, canClean, cleanHint);
}

/// <summary>一次扫描的总览。</summary>
/// <param name="Categories">四个类别，顺序与 <see cref="StorageCategoryKeys.All"/> 一致。</param>
/// <param name="ScannedAt">扫描完成时刻，用于「刚刚统计」这类文案。</param>
public sealed record StorageReport(IReadOnlyList<StorageCategoryUsage> Categories, DateTimeOffset ScannedAt)
{
    /// <summary>所有类别合计字节数。</summary>
    public long TotalBytes => Categories.Sum(category => category.TotalBytes);

    /// <summary>所有类别合计文件数。</summary>
    public int TotalFiles => Categories.Sum(category => category.FileCount);

    /// <summary>服务未接入或扫描前的零值报告。</summary>
    public static StorageReport Empty() => new(Array.Empty<StorageCategoryUsage>(), DateTimeOffset.MinValue);
}

/// <summary>
/// 一次清理的结果。
///
/// <see cref="Rejected"/> 非空表示有类别被安全检查挡下（路径规范化后落在数据根之外、
/// 或路径非法），此时该类别一个文件都没删。被挡下也要计入 <see cref="FailedItems"/>，
/// 否则界面上会出现「清理成功但占用没变」这种无法解释的现象。
/// </summary>
/// <param name="DeletedBytes">实际删除的字节数（只累加删除成功的文件）。</param>
/// <param name="DeletedFiles">实际删除的文件数。</param>
/// <param name="FailedItems">删除失败或被拒绝处理的条目数。</param>
/// <param name="CategoryKeys">实际处理过的类别键；整批被安全规则拒绝的类别不列出（原因见 <see cref="Rejected"/>）。</param>
/// <param name="Rejected">被安全规则拒绝时的中文原因；没有拒绝为 <c>null</c>。</param>
public sealed record StorageCleanResult(
    long DeletedBytes,
    int DeletedFiles,
    int FailedItems,
    IReadOnlyList<string> CategoryKeys,
    string? Rejected)
{
    /// <summary>是否有类别被安全规则拒绝。</summary>
    public bool IsRejected => !string.IsNullOrWhiteSpace(Rejected);

    /// <summary>一项都没删成功（用来区分「清理过但都是空目录」与「根本没动」）。</summary>
    public bool DeletedNothing => DeletedBytes == 0 && DeletedFiles == 0;

    /// <summary>构造一个「整批被拒绝」的结果。</summary>
    public static StorageCleanResult Reject(string reason, IReadOnlyCollection<string>? categoryKeys = null) =>
        new(0, 0, 0, categoryKeys is null ? Array.Empty<string>() : [.. categoryKeys], reason);
}