using System.Runtime.InteropServices;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例目录的五个快捷入口：存档 / 模组 / 资源包 / 截图 / 日志。
///
/// 出处是 PCL2 <c>Pages/PageInstance/PageInstanceOverall.xaml.vb</c> 的目录按钮：
/// 它在打开前先 <c>DirectoryUtils.Create</c> 再 <c>OpenExplorer</c>，所以「目录不存在」
/// 不是错误路径。这里保留同一套顺序与相对名（<c>saves</c> / <c>mods</c> /
/// <c>resourcepacks</c> / <c>screenshots</c> / <c>logs</c>）。
///
/// 三条边界：
/// 1. **只返回相对名**，绝对路径由 <see cref="ResolvePath"/> 现拼：同一个实例可能在不同根下解析。
/// 2. **目录不存在也返回**（列表长度恒为 5），否则芯片会随「玩家还没截过图」忽隐忽现。
/// 3. **本类不写盘**。创建目录是界面在开目录前做的事——扫描路径上建目录等于替用户改游戏目录。
/// </summary>
public static class InstanceFolders
{
    /// <summary><c>saves/</c>（存档）。</summary>
    public const string SavesKey = "saves";

    /// <summary><c>mods/</c>（模组）。</summary>
    public const string ModsKey = "mods";

    /// <summary><c>resourcepacks/</c>（资源包）。</summary>
    public const string ResourcePacksKey = "resourcepacks";

    /// <summary><c>screenshots/</c>（截图）。游戏只在按下 F2 时才建它，通常不存在。</summary>
    public const string ScreenshotsKey = "screenshots";

    /// <summary><c>logs/</c>（日志）。</summary>
    public const string LogsKey = "logs";

    // 顺序即界面顺序：存档 / 模组 / 资源包是玩家最常进的三个，截图与日志跟在后面。
    private static readonly InstanceFolder[] Catalog =
    {
        new(SavesKey, "存档", "saves", CreateOnOpen: true),
        new(ModsKey, "模组", "mods", CreateOnOpen: true),
        new(ResourcePacksKey, "资源包", "resourcepacks", CreateOnOpen: true),
        new(ScreenshotsKey, "截图", "screenshots", CreateOnOpen: true),
        new(LogsKey, "日志", "logs", CreateOnOpen: true),
    };

    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>固定五项（不含磁盘状态，<c>Exists</c> 恒为 false）。</summary>
    public static IReadOnlyList<InstanceFolder> All => Catalog;

    /// <summary>
    /// 按磁盘事实列出快捷入口。<paramref name="gameDirectory"/> 为空、不存在或非法时
    /// 五项照常返回（<c>Exists</c> 全为 false），**不抛异常**：实例目录被外部删掉时，
    /// 卡片仍要能渲染，芯片点了会按需建目录。
    /// </summary>
    public static IReadOnlyList<InstanceFolder> Enumerate(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            // 没有游戏目录就谈不上「在哪个目录下」：返回空列表，界面据此不画芯片。
            return Array.Empty<InstanceFolder>();
        }

        string root;

        try
        {
            root = Path.GetFullPath(gameDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<InstanceFolder>();
        }

        var result = new InstanceFolder[Catalog.Length];

        for (var i = 0; i < Catalog.Length; i++)
        {
            var folder = Catalog[i];
            result[i] = folder with { Exists = DirectoryExists(Path.Combine(root, folder.RelativePath)) };
        }

        return result;
    }

    /// <summary>按 Key 取定义（不含磁盘状态）；未知 Key 返回 <c>null</c>。Key 大小写不敏感。</summary>
    public static InstanceFolder? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var folder in Catalog)
        {
            if (string.Equals(folder.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>
    /// 拼出「游戏目录 + 相对名」的绝对路径，并校验结果严格位于游戏目录之下。
    ///
    /// 越界判据与 <c>StorageService</c> 同一套写法：比较「根 + 分隔符」前缀，
    /// 只比 <c>StartsWith(root)</c> 会把 <c>mods-backup</c> 这类同名前缀的兄弟目录误判成根内。
    /// 未知 Key、空目录参数、非法路径一律返回 <c>null</c>（调用方据此写状态行，不抛异常）。
    /// </summary>
    public static string? ResolvePath(string? gameDirectory, string? key)
    {
        var folder = Find(key);

        if (folder is null || string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
            var candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(root, folder.RelativePath)));

            // 游戏目录本身不算「它的子目录」：拼出来等于根说明相对名被吃掉或为空。
            if (string.Equals(root, candidate, PathComparison))
            {
                return null;
            }

            return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
                ? candidate
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 存在性判断失败只是「状态未知」：当成不存在，点击时会再试一次创建。
            return false;
        }
    }
}

/// <summary>
/// <see cref="IInstanceFolderService"/> 的默认实现：<see cref="InstanceFolders"/> 的薄包装。
///
/// 不持有任何状态、不缓存磁盘结果——每次 <see cref="List"/> 都读一次磁盘，
/// 这样「用户刚在资源管理器里建了 screenshots/」在下次刷新时就能看到。
/// </summary>
public sealed class InstanceFolderService : IInstanceFolderService
{
    /// <inheritdoc />
    public IReadOnlyList<InstanceFolder> List(string? gameDirectory) => InstanceFolders.Enumerate(gameDirectory);

    /// <inheritdoc />
    public InstanceFolder? Find(string? key) => InstanceFolders.Find(key);

    /// <inheritdoc />
    public string? ResolvePath(string? gameDirectory, string? key) =>
        InstanceFolders.ResolvePath(gameDirectory, key);
}