namespace NebulaLauncher.Tests;

/// <summary>DocsCheck 测试共用的路径定位。</summary>
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
}
