namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 安装目标目录不可用（不可写 / 空间不足 / 被安全软件拦截）。
///
/// 单独成类型是为了让 UI 不必靠解析异常文本判断「这是目录问题」，
/// 从而给出「换一个游戏目录」这类可执行的操作入口，而不是笼统的「安装失败」。
/// </summary>
public sealed class InstallTargetException : Exception
{
    public InstallTargetException(string targetPath, string message)
        : base(message)
    {
        TargetPath = targetPath;
    }

    public InstallTargetException(string targetPath, string message, Exception innerException)
        : base(message, innerException)
    {
        TargetPath = targetPath;
    }

    /// <summary>出问题的目录（通常是当前 MinecraftDirectory）。</summary>
    public string TargetPath { get; }
}
