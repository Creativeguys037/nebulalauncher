using System;
using System.Collections.Generic;
using System.IO;

namespace NebulaLauncher.Core.Paths;

/// <summary>
/// 目录可用性探测。安装/切换目录之前先用它拿一句能给用户看的原因，
/// 而不是等到下载到一半抛出 <see cref="UnauthorizedAccessException"/>。
/// </summary>
public static class PathProbe
{
    /// <summary>安装一个现代版本大致需要的可用空间下限（客户端 + 库 + 资源 + 余量）。</summary>
    public const long MinimumFreeBytesForInstall = 1024L * 1024 * 1024;

    /// <summary>
    /// 测试目录能否创建并写入。可写返回 <c>null</c>，否则返回中文原因（含底层错误）。
    ///
    /// 分两步探测：先写文件，再建子目录。只做一次文件探测会误报——
    /// 360 主动防御一类安全软件会拦截未签名进程在用户目录里创建「. 开头的临时文件」，
    /// 但同一目录下正常写文件、建子目录是放行的。两种都失败才判定不可写。
    /// </summary>
    public static string? TestWritable(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "路径为空";
        }

        string full;

        try
        {
            full = Path.GetFullPath(directory);
            Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }

        var failures = new List<string>();

        // 方式 1：普通文件。用 .log 而不是 .tmp / .exe，避开杀软的可疑扩展名规则。
        var fileProbe = Path.Combine(full, $".nebula-write-test-{Guid.NewGuid():N}.log");

        try
        {
            using (var stream = new FileStream(fileProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(fileProbe);
            return null;
        }
        catch (Exception ex)
        {
            TryDeleteFile(fileProbe);
            failures.Add($"写入文件失败：{Describe(ex)}");
        }

        // 方式 2：临时子目录。部分安全软件只拦「创建文件」，建目录是放行的。
        var directoryProbe = Path.Combine(full, $".nebula-write-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directoryProbe);
            Directory.Delete(directoryProbe);
            return null;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(directoryProbe);
            failures.Add($"创建子目录失败：{Describe(ex)}");
        }

        return string.Join("；", failures);
    }

    /// <summary>
    /// 清除目录及其一级文件的 <see cref="FileAttributes.ReadOnly"/> 属性。
    ///
    /// 用户从别处拷来的 <c>.minecraft</c> 常带只读属性，此时写入会在建库文件时抛
    /// <see cref="UnauthorizedAccessException"/>，而用户并不知道要先改属性。
    ///
    /// 只处理**目录本身与一级文件**：递归扫 <c>assets/</c> 那种几万文件的目录代价太高，
    /// 而「从只读介质拷来的目录」通常是整棵树一起带属性、且真正被覆盖的是根下这几个文件
    /// （<c>options.txt</c>、<c>launcher_profiles.json</c>），一级足够。
    ///
    /// **只在 Windows 启用**：Unix 上「只读」是权限位（<c>chmod</c>），
    /// <see cref="FileAttributes.ReadOnly"/> 在 .NET 里只是把 owner 的写位去掉，
    /// 在没有相应权限时会抛异常；语义不同，不要跨平台照搬。
    ///
    /// 成功返回 <c>null</c>；失败返回中文原因，调用方应把它拼进最终错误文案
    /// （受组策略控制的目录、OneDrive 占位文件都可能清不掉）。
    /// </summary>
    public static string? TryMakeWritable(string? directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "只有 Windows 才支持自动清除只读属性";
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return "路径为空";
        }

        string full;

        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }

        if (!Directory.Exists(full))
        {
            return "目录不存在";
        }

        var failures = new List<string>();

        try
        {
            ClearReadOnly(full);
        }
        catch (Exception ex)
        {
            failures.Add($"目录：{Describe(ex)}");
        }

        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(full);
        }
        catch (Exception ex)
        {
            failures.Add($"枚举文件：{Describe(ex)}");
            return string.Join("；", failures);
        }

        foreach (var file in files)
        {
            try
            {
                ClearReadOnly(file);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}：{Describe(ex)}");
            }
        }

        return failures.Count == 0 ? null : string.Join("；", failures);
    }

    /// <summary>去掉单个路径的只读位；本来不是只读时什么都不做（避免无谓的属性写入）。</summary>
    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => $"没有写入权限（{ex.Message}）",
        DirectoryNotFoundException => $"父目录不存在（{ex.Message}）",
        PathTooLongException => $"路径过长（{ex.Message}）",
        IOException => $"写入失败，可能被占用或磁盘已满（{ex.Message}）",
        ArgumentException => $"路径非法（{ex.Message}）",
        NotSupportedException => $"路径格式不受支持（{ex.Message}）",
        _ => ex.Message,
    };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 探测残留文件不该影响结论。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 同上。
        }
    }

    /// <summary>逐个目录探测，返回「目录 → 原因」的失败列表；全部可写时返回空列表。</summary>
    public static IReadOnlyList<(string Directory, string Reason)> TestWritable(IEnumerable<string>? directories)
    {
        var failures = new List<(string, string)>();
        if (directories is null)
        {
            return failures;
        }

        foreach (var directory in directories)
        {
            var reason = TestWritable(directory);
            if (reason is not null)
            {
                failures.Add((directory ?? string.Empty, reason));
            }
        }

        return failures;
    }

    /// <summary>所在卷的可用字节数；拿不到时返回 <c>null</c>（例如 UNC 路径）。</summary>
    public static long? GetFreeBytes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>空间不足时返回中文提示，足够或拿不到容量时返回 <c>null</c>。</summary>
    public static string? CheckFreeSpace(string? path, long requiredBytes = MinimumFreeBytesForInstall)
    {
        var free = GetFreeBytes(path);
        if (free is null || free >= requiredBytes)
        {
            return null;
        }

        return $"磁盘可用空间不足（剩余 {FormatSize(free.Value)}，建议至少 {FormatSize(requiredBytes)}）";
    }

    /// <summary>把字节数格式化成 MB/GB，用于错误提示。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):0.0} MB";
        }

        return $"{bytes} B";
    }
}
