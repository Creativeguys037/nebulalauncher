using System.Diagnostics.CodeAnalysis;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例目录的文件系统操作。
///
/// 为什么单独抽出来：克隆、移入回收站、恢复、清理都要做「复制目录 / 移动目录 / 统计大小」，
/// 三处各写一遍必然出现不同的容错策略——有的抛、有的吞、有的漏掉半份文件。
/// 这里统一成「能继续就继续，失败只在日志里留证据」。
/// </summary>
internal static class InstanceFileSystem
{
    /// <summary>递归复制目录内容。<paramref name="source"/> 不存在时直接返回，不创建目标。</summary>
    public static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceDirectory = new DirectoryInfo(source);

        if (!sourceDirectory.Exists)
        {
            return;
        }

        Directory.CreateDirectory(destination);

        foreach (var child in sourceDirectory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyDirectoryAsync(child.FullName, Path.Combine(destination, child.Name), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in sourceDirectory.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyFileAsync(file.FullName, Path.Combine(destination, file.Name), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 异步复制单个文件。用流而不是 <see cref="File.Copy(string, string, bool)"/>：
    /// 克隆一个装满模组的实例可能是几百 MB，同步复制会把 UI 线程整个卡住。
    /// </summary>
    public static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 81920;

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 移动目录。先试原子性的 <see cref="Directory.Move(string, string)"/>；
    /// 失败时（跨卷、或源目录被别的句柄短暂占用）退化为「复制 + 删除源」，并把退化原因写进日志。
    /// </summary>
    /// <returns>移动成功为 true；复制或删除失败由异常向上抛，调用方负责转成失败结果。</returns>
    public static async Task<bool> MoveDirectoryAsync(
        string source,
        string destination,
        IAppLog log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 跨卷移动在 .NET 上就是 IOException；这里不区分原因，因为对调用方来说处理方式相同。
            log.Info($"目录移动失败，改为复制后删除（{ex.GetType().Name}）：{source} → {destination}");
        }

        await CopyDirectoryAsync(source, destination, cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目标已经完整复制成功，源目录删不掉只影响磁盘占用，不该让整个操作算失败。
            log.Warn($"复制已完成，但删除源目录失败：{source}", ex);
        }

        return true;
    }

    /// <summary>统计目录占用字节数。任何读取失败都按 0 计，回收站条目的「大小」不是关键数据。</summary>
    public static long GetDirectorySize(string directory)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件读不到就跳过，继续统计其余文件。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录整体不可读：返回已经统计到的部分。
        }

        return total;
    }

    /// <summary>递归枚举文件。枚举过程中目录被删掉时停止，不抛给调用方。</summary>
    public static IEnumerable<string> EnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;

        try
        {
            enumerator = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;

                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 游戏正在写日志时目录结构会变，枚举中断只代表「剩下的下次再清」。
                    yield break;
                }

                yield return current;
            }
        }
    }

    /// <summary>递归删除目录，失败只记日志。用于「克隆失败后清理半成品」这类收尾动作。</summary>
    public static void DeleteDirectoryQuietly(string directory, IAppLog log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"清理目录失败：{directory}", ex);
        }
    }

    /// <summary>删除单个文件，失败只记日志。</summary>
    public static void DeleteFileQuietly(string path, IAppLog? log = null)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warn($"删除文件失败：{path}", ex);
        }
    }

    /// <summary>
    /// 目录名是否可安全地拼进路径。实例 Id 与回收站条目 Id 都只是目录名，
    /// 放任 <c>..</c> 或分隔符进来就等于把删除操作指向任意位置。
    /// </summary>
    public static bool IsSafeDirectoryName([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && !name.Contains("..", StringComparison.Ordinal)
               && !name.Contains(Path.DirectorySeparatorChar)
               && !name.Contains(Path.AltDirectorySeparatorChar);
    }
}
