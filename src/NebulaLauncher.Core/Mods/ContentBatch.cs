using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>批量操作的类型。文案与统计共用，避免 UI 各自拼一套。</summary>
public enum ContentBatchOperation
{
    Enable = 0,
    Disable = 1,
    Delete = 2,
}

/// <summary>
/// 一次批量操作的结果。逐项执行、逐项容错：单个文件改名失败不影响其余项，
/// 失败项文件名留在 <see cref="FailedFiles"/> 里由 UI 汇总展示。
/// </summary>
public sealed record ContentBatchResult(
    ContentBatchOperation Operation,
    int Succeeded,
    int Failed,
    IReadOnlyList<string> FailedFiles)
{
    public static ContentBatchResult Empty(ContentBatchOperation operation) =>
        new(operation, 0, 0, Array.Empty<string>());

    public int Total => Succeeded + Failed;

    public bool HasFailures => Failed > 0;

    /// <summary>「成功 N / 失败 M」。逐项容错的批量操作必须把这两个数显示给用户。</summary>
    public string Summary => $"成功 {Succeeded} / 失败 {Failed}";
}

/// <summary>
/// 批量启用 / 禁用 / 删除内容文件。
///
/// 为什么不直接在页面里 for 循环调 <see cref="IContentService"/>：单个
/// <c>SetEnabledAsync</c> 把 <see cref="IOException"/>（文件被游戏占用）吞成日志并正常返回，
/// 调用方拿不到失败信号。这里的判据改成**磁盘上的真实结果**——目标扩展名是否存在——
/// 从而把「改名失败」如实计入失败数，而不是静默当成成功。
///
/// 整个循环放在 <c>Task.Run</c> 里：<c>SetEnabledAsync</c> 是同步实现
/// （返回已完成的 Task），在 UI 线程 await 它不会让出线程，700 个模组会把界面卡住。
/// </summary>
public static class ContentBatch
{
    /// <summary>
    /// 禁用态后缀，与 <see cref="ContentService"/> 保持一致。
    ///
    /// <c>.old</c> 是 PCL2 / 部分第三方工具用的写法（<c>LocalResourceFile.vb:41</c>），
    /// 迁移来的实例里大量存在；两种后缀同时出现时 <c>.disabled</c> 优先。
    /// </summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>PCL2 风格的禁用后缀。</summary>
    public const string LegacyDisabledSuffix = ".old";

    private static readonly string[] DisabledSuffixes = [DisabledSuffix, LegacyDisabledSuffix];

    /// <summary>
    /// 去掉禁用后缀：<c>a.jar.disabled</c> / <c>a.jar.old</c> → <c>a.jar</c>；已启用时原样返回。
    /// </summary>
    public static string EnabledPathOf(string path)
    {
        foreach (var suffix in DisabledSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^suffix.Length];
            }
        }

        return path;
    }

    /// <summary>加上禁用后缀。</summary>
    public static string DisabledPathOf(string path) => EnabledPathOf(path) + DisabledSuffix;

    /// <summary>
    /// 磁盘上的真实启用态：<c>true</c> 启用、<c>false</c> 禁用、<c>null</c> 所有候选都不存在。
    ///
    /// 返回 null 的情况必须由调用方决定怎么显示（文件已被删掉 / 扫描后被人手工移走），
    /// 不能当成「启用」——那会让卡片显示与磁盘不一致。
    /// </summary>
    public static bool? IsEnabledOnDisk(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var enabledPath = EnabledPathOf(item.FilePath);
        if (File.Exists(enabledPath))
        {
            return true;
        }

        foreach (var suffix in DisabledSuffixes)
        {
            if (File.Exists(enabledPath + suffix))
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>批量启用 / 禁用。只改扩展名，不移动目录、不删文件。</summary>
    public static Task<ContentBatchResult> SetEnabledAsync(
        IContentService content,
        IReadOnlyList<ContentItem> items,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        return RunAsync(
            enabled ? ContentBatchOperation.Enable : ContentBatchOperation.Disable,
            items,
            (item, token) => content.SetEnabledAsync(item, enabled, token),
            item => enabled
                ? File.Exists(EnabledPathOf(item.FilePath))
                : !File.Exists(EnabledPathOf(item.FilePath)) && HasDisabledVariant(EnabledPathOf(item.FilePath)),
            cancellationToken);
    }

    /// <summary>
    /// 批量删除。默认进回收站（与单个删除按钮一致）。
    /// <paramref name="permanent"/> 为 true 时彻底删除——UI 里由 Shift 或二次确认里的显式勾选触发。
    /// </summary>
    public static Task<ContentBatchResult> DeleteAsync(
        IContentService content,
        IReadOnlyList<ContentItem> items,
        bool permanent = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        return RunAsync(
            ContentBatchOperation.Delete,
            items,
            (item, token) => content.DeleteAsync(item, permanent, token),
            item => !File.Exists(item.FilePath)
                    && !Directory.Exists(item.FilePath)
                    && !File.Exists(EnabledPathOf(item.FilePath))
                    && !Directory.Exists(EnabledPathOf(item.FilePath))
                    && !HasDisabledVariant(EnabledPathOf(item.FilePath)),
            cancellationToken);
    }

    private static bool HasDisabledVariant(string enabledPath) =>
        DisabledSuffixes.Any(suffix => File.Exists(enabledPath + suffix));

    private static Task<ContentBatchResult> RunAsync(
        ContentBatchOperation operation,
        IReadOnlyList<ContentItem> items,
        Func<ContentItem, CancellationToken, Task> action,
        Func<ContentItem, bool> succeeded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return Task.FromResult(ContentBatchResult.Empty(operation));
        }

        return Task.Run(
            async () =>
            {
                var failed = new List<string>();
                var succeededCount = 0;

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await action(item, cancellationToken).ConfigureAwait(false);

                        if (succeeded(item))
                        {
                            succeededCount++;
                            continue;
                        }

                        failed.Add(DisplayNameOf(item));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // 逐项容错：一个文件动不了，其余项继续；失败项进汇总。
                        failed.Add(DisplayNameOf(item));
                    }
                }

                return new ContentBatchResult(operation, succeededCount, failed.Count, failed);
            },
            cancellationToken);
    }

    private static string DisplayNameOf(ContentItem item) =>
        string.IsNullOrWhiteSpace(item.FileName) ? Path.GetFileName(item.FilePath) : item.FileName;
}
