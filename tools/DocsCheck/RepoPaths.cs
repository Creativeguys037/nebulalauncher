namespace NebulaLauncher.DocsCheck;

/// <summary>
/// 仓库相对路径到绝对路径的唯一转换点。文档里一律写 <c>/</c>，只有这里换一次平台分隔符——
/// 四个 checker 各拼一遍时，任何一处漏掉 <see cref="Path.DirectorySeparatorChar"/> 都只在 Windows 以外的机器上暴露。
/// </summary>
public static class RepoPaths
{
    /// <summary>仓库相对路径转绝对路径。</summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="relativePath">仓库相对路径（<c>/</c> 分隔）。</param>
    public static string Resolve(string repoRoot, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(relativePath);

        return Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
