using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 启动器数据目录的占用统计与安全清理。
///
/// 为什么统计放在 Core 而不是页面里：<c>&lt;data&gt;/cache</c> 与 <c>&lt;data&gt;/logs</c> 的
/// 目录布局由 <see cref="ILauncherPaths"/> 决定，页面拿不到「loader-install 属于 cache 子目录、
/// 必须从 cache 统计里扣掉」这类知识；写在页面里就变成三处各猜一次。
///
/// 安全边界写在实现里，而不是只写在按钮上：清理是**不可撤销**的破坏性操作，
/// 任何调用方（今天的设置页、明天的命令行维护入口）都必须经过同一套校验。
///
/// 契约约束（与其余服务同款）：成员一律带默认实现，保证「契约先落地、实现后跟上」的中间态可编译；
/// 默认实现返回空 / 拒绝结果，绝不抛异常。
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// 扫描全部类别。目录不存在、无权访问、单个文件被占用都只跳过并记日志，整体不抛异常。
    /// </summary>
    Task<StorageReport> ScanAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(StorageReport.Empty());

    /// <summary>
    /// 清理指定类别。<paramref name="categoryKeys"/> 里的未知键会被忽略。
    ///
    /// 只删除各类别根目录下的文件与空目录；类别根目录本身与数据根目录保留。
    /// 每项独立 try/catch，失败计入 <see cref="StorageCleanResult.FailedItems"/>，不中断整批。
    /// 路径规范化后不在数据根之下时该类别整体拒绝，一个文件都不删。
    /// </summary>
    Task<StorageCleanResult> CleanAsync(
        IReadOnlyCollection<string> categoryKeys,
        CancellationToken cancellationToken = default)
        => Task.FromResult(StorageCleanResult.Reject("存储服务尚未实现清理能力。", categoryKeys));

    /// <summary>清理全部类别。</summary>
    Task<StorageCleanResult> CleanAllAsync(CancellationToken cancellationToken = default)
        => CleanAsync(StorageCategoryKeys.All, cancellationToken);

    /// <summary>清理单个类别，供卡片上的「清理」按钮使用。</summary>
    Task<StorageCleanResult> CleanCategoryAsync(
        string categoryKey,
        CancellationToken cancellationToken = default)
        => CleanAsync([categoryKey], cancellationToken);
}