namespace NebulaLauncher.Core.Models;

/// <summary>本机探测到的一个 Java 运行时。</summary>
public sealed class JavaInfo
{
    /// <summary>java / javaw 可执行文件绝对路径。</summary>
    public required string Path { get; init; }

    /// <summary>完整版本串，例如 <c>17.0.8+8-LTS</c>。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>主版本号，例如 8 / 17 / 21。</summary>
    public int MajorVersion { get; init; }

    public bool Is64Bit { get; init; }

    public string Vendor { get; init; } = string.Empty;

    /// <summary>Java 安装根目录，用于 UI 展示与自动生成的路径推断。</summary>
    public string HomeDirectory { get; init; } = string.Empty;

    /// <summary>来源：<c>registry</c> / <c>scan</c> / <c>manual</c> / <c>bundled</c>。</summary>
    public string Source { get; init; } = "scan";

    public string DisplayName =>
        string.IsNullOrEmpty(Version) ? Path : $"Java {(MajorVersion > 0 ? MajorVersion : Version)}";

    /// <summary>该 Java 是否满足某 Minecraft 版本要求。</summary>
    public bool Satisfies(int requiredMajor) => MajorVersion >= requiredMajor;
}

/// <summary>Java 下载条目（来自 Adoptium API 或 Mojang java runtime manifest）。</summary>
public sealed class JavaRuntimePackage
{
    public required string Name { get; init; }

    public required int MajorVersion { get; init; }

    public required string DownloadUrl { get; init; }

    public required string ArchiveType { get; init; }

    /// <summary>解压后相对根目录里 java 可执行文件的相对路径（Windows 为 <c>bin/javaw.exe</c>）。</summary>
    public string ExecutableRelativePath { get; init; } = "bin/java";

    public string Architecture { get; init; } = "x64";

    public string Vendor { get; init; } = "Adoptium";
}

/// <summary>某个 Minecraft 版本对 Java 的要求（只有下界，用于 UI 文案）。</summary>
public sealed record JavaRequirement(int RequiredMajor, string Reason);

/// <summary>
/// 一条 Java 约束：游戏版本区间 × Java 版本区间 × 加载器条件 × 是否强制。
///
/// 语义照抄 HMCL <c>JavaVersionConstraint</c>（JavaVersionConstraint.java:33-223）：
/// <list type="bullet">
/// <item><see cref="Mandatory"/> 为 <c>true</c> 表示违反即不可用（HMCL 的 <c>isMandatory</c>）；</item>
/// <item>为 <c>false</c> 表示只是建议，违反不阻断启动——HMCL 的 <c>findSuitableJava</c>
/// （JavaManager.java:318-360）只在没有「零建议违规」的运行时时才回退到违反建议的那个。</item>
/// </list>
///
/// 游戏版本区间用 <c>minor * 1000 + patch</c> 的整数键表达（1.20.4 → 20004、1.20.5 → 20005、26.1 → 26001），
/// 这样一个单区间就能分出 1.20.4 与 1.20.5 这种只差补丁号的分界；
/// Java 区间直接比较主版本号，<see cref="int.MaxValue"/> 表示无上界。
/// </summary>
public readonly record struct JavaConstraint(
    bool Mandatory,
    (int Min, int Max) Game,
    (int Min, int Max) Java,
    LoaderKind? RequiresLoader = null,
    string? Id = null)
{
    /// <summary>游戏版本键是否落在区间内。</summary>
    public bool MatchesGame(int gameKey) => gameKey >= Game.Min && gameKey <= Game.Max;

    /// <summary>加载器条件是否满足；<see cref="RequiresLoader"/> 为 null 表示任何加载器都适用。</summary>
    public bool MatchesLoader(LoaderKind loader) => RequiresLoader is null || RequiresLoader == loader;

    /// <summary>Java 主版本是否落在区间内（即这条规则是否被满足）。</summary>
    public bool MatchesJava(int javaMajor) => javaMajor >= Java.Min && javaMajor <= Java.Max;

    /// <summary>
    /// 这条规则是否适用于「该游戏版本 + 该加载器」。注意不含 Java 版本判断：
    /// 违反与否是 <see cref="MatchesJava"/> 取反，两件事分开才能表达「适用但被违反」。
    /// </summary>
    public bool AppliesTo(int gameKey, LoaderKind loader) => MatchesGame(gameKey) && MatchesLoader(loader);
}
