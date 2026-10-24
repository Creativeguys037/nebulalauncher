namespace NebulaLauncher.Tests;

/// <summary>DocsCheck 测试共用的路径定位与临时树写入。</summary>
internal static class DocsCheckTestPaths
{
    /// <summary>从测试输出目录向上找 <c>NebulaLauncher.sln</c>，得到真实仓库根。</summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NebulaLauncher.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("找不到仓库根：从测试输出目录向上没有 NebulaLauncher.sln。");
        }

        return directory.FullName;
    }

    /// <summary>
    /// 往临时树里写一份文件，返回绝对路径；父目录不存在就建。
    /// 五份 DocsCheck 测试原先各有一份完全相同的实现，统一收到这里。
    /// </summary>
    /// <param name="temp">临时树。</param>
    /// <param name="relativePath">相对路径，<c>/</c> 分隔。</param>
    /// <param name="content">文件内容。</param>
    public static string Write(TempDirectory temp, string relativePath, string content)
    {
        var path = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
