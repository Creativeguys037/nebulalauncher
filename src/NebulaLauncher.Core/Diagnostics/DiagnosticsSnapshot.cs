using System.Globalization;
using System.Reflection;

namespace NebulaLauncher.Core.Diagnostics;

/// <summary>
/// 诊断快照的输入。**所有字段可选**：调用方拿不到某个值就留空，输出里自动省略那一行。
///
/// 为什么要一个共享结构而不是两个格式化方法：设置页的「复制诊断信息」与崩溃导出的
/// <c>system.txt</c> 必须给出同一套字段名，否则用户把两段文本贴到同一条 issue 里，
/// 别人看到的是两种口径。这里是唯一的格式定义。
/// </summary>
public sealed record DiagnosticsInput
{
    /// <summary>首行标题。null 时用 <see cref="DiagnosticsSnapshot.DefaultTitle"/>。</summary>
    public string? Title { get; init; }

    /// <summary>生成时间。null 时取当前本地时间。</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    public string? LauncherVersion { get; init; }

    public string? DotNetVersion { get; init; }

    public string? PlatformName { get; init; }

    public string? DataDirectory { get; init; }

    public string? GameDirectory { get; init; }

    public string? LogFilePath { get; init; }

    /// <summary>可用内存字节数。&lt;= 0 表示未知，输出「未知」。</summary>
    public long AvailableMemoryBytes { get; init; }

    public int? MinMemoryMb { get; init; }

    public int? MaxMemoryMb { get; init; }

    public bool? AutoMaxMemory { get; init; }

    public string? DownloadSource { get; init; }

    public int? DownloadThreads { get; init; }

    public int? DownloadSpeedLimitKb { get; init; }

    public string? IsolationPolicy { get; init; }

    public string? Theme { get; init; }

    public string? WindowMaterial { get; init; }

    public bool? EnableAnimations { get; init; }

    public bool? RememberWindowSize { get; init; }

    // ---------------- 崩溃导出独有的三项（设置页拿不到，留空即不输出） ----------------

    public string? JavaVersion { get; init; }

    public string? MinecraftVersion { get; init; }

    public string? Loader { get; init; }
}

/// <summary>
/// 「诊断信息」文本的唯一生成入口。设置页的「复制诊断信息」与崩溃导出的 <c>system.txt</c>
/// 都调 <see cref="Create"/>，字段名、顺序、单位因此天然一致。
///
/// 只收环境与设置摘要：账户名、令牌、完整日志一律不进——这类文本通常会被贴到公开的 issue 里。
/// </summary>
public static class DiagnosticsSnapshot
{
    /// <summary>没指定标题时的首行。</summary>
    public const string DefaultTitle = "Nebula Launcher 诊断信息";

    /// <summary>程序集里读不出版本号时的占位值。展示层不会给它加 <c>v</c> 前缀。</summary>
    public const string UnknownVersion = "未知";

    /// <summary>把输入压成多行文本。空字段整行省略，不会出现「平台: 」这种半截行。</summary>
    public static string Create(DiagnosticsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var availableMb = input.AvailableMemoryBytes > 0 ? input.AvailableMemoryBytes / 1024 / 1024 : 0;

        var lines = new List<string>(24)
        {
            string.IsNullOrWhiteSpace(input.Title) ? DefaultTitle : input.Title!,
            $"时间: {(input.GeneratedAt ?? DateTimeOffset.Now):yyyy-MM-dd HH:mm:ss zzz}",
        };

        AppendIfPresent(lines, "版本", DisplayVersion(input.LauncherVersion));
        AppendIfPresent(lines, ".NET", input.DotNetVersion);
        AppendIfPresent(lines, "平台", input.PlatformName);
        AppendIfPresent(lines, "数据目录", input.DataDirectory);
        AppendIfPresent(lines, "游戏目录", input.GameDirectory);
        AppendIfPresent(lines, "日志", input.LogFilePath);

        lines.Add($"可用内存: {(availableMb > 0 ? $"{availableMb} MB" : "未知")}");

        if (input.MinMemoryMb is { } min && input.MaxMemoryMb is { } max)
        {
            var auto = input.AutoMaxMemory is { } autoValue ? autoValue.ToString() : "未知";
            lines.Add($"内存设置: {min}-{max} MB（自动分配={auto}）");
        }

        if (input.DownloadSource is not null || input.DownloadThreads is not null || input.DownloadSpeedLimitKb is not null)
        {
            lines.Add(
                $"下载: 源={input.DownloadSource ?? "未知"}，" +
                $"并发={input.DownloadThreads?.ToString(CultureInfo.InvariantCulture) ?? "未知"}，" +
                $"限速={input.DownloadSpeedLimitKb?.ToString(CultureInfo.InvariantCulture) ?? "未知"} KB/s");
        }

        AppendIfPresent(lines, "隔离策略", input.IsolationPolicy);

        if (input.Theme is not null || input.WindowMaterial is not null)
        {
            lines.Add(
                $"界面: 主题={input.Theme ?? "未知"}，材质={input.WindowMaterial ?? "未知"}，" +
                $"动效={(input.EnableAnimations?.ToString() ?? "未知")}，" +
                $"记住窗口={(input.RememberWindowSize?.ToString() ?? "未知")}");
        }

        // 崩溃导出独有：设置页不传这几项，输出里就不会出现。
        AppendIfPresent(lines, "Java 版本", input.JavaVersion);
        AppendIfPresent(lines, "游戏版本", input.MinecraftVersion);
        AppendIfPresent(lines, "加载器", input.Loader);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 从程序集信息里取语义版本号。CI 会把 commit hash 拼在 <c>+</c> 后面，这里剥掉。
    /// 抽出来是为了让设置页与崩溃导出显示同一个版本号。
    /// </summary>
    public static string LauncherVersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return FormatLauncherVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);
    }

    /// <summary>
    /// 程序集 → 对外展示版本（<c>v1.0.0</c>）。设置页「关于」用的就是它；
    /// 崩溃导出与诊断文本走同一条链，三处因此不会各显示一套。
    /// </summary>
    public static string DisplayVersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return DisplayVersion(LauncherVersionOf(assembly)) ?? UnknownVersion;
    }

    /// <summary>
    /// 版本号的解析规则本体。抽成纯函数是为了能直接测「带 <c>+hash</c>」这类只有 CI 才产出的输入：
    /// <see cref="LauncherVersionOf"/> 只能读当前进程里的程序集，喂不进构造出来的字符串。
    ///
    /// 规则：优先用 InformationalVersion 并剥掉 <c>+</c> 之后的部分；取不到退回程序集版本；
    /// 两者都没有才给「未知」。返回值不带 <c>v</c> 前缀——前缀属于展示层。
    /// </summary>
    public static string FormatLauncherVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informationalVersion[..plus] : informationalVersion;
        }

        return assemblyVersion?.ToString() ?? UnknownVersion;
    }

    /// <summary>
    /// 对外展示的版本号：给语义版本补 <c>v</c> 前缀。展示规则只在这里定义一次，
    /// 设置页「关于」、诊断文本与崩溃导出因此不会出现「一处 v1.0.0、一处 1.0.0」。
    ///
    /// 幂等：已经带前缀的串原样返回，所以调用方传裸版本还是传展示串都安全。
    /// 空值与 <see cref="UnknownVersion"/> 原样返回——前者让诊断文本整行省略，后者不是版本号。
    /// </summary>
    public static string? DisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == UnknownVersion)
        {
            return version;
        }

        return version[0] is 'v' or 'V' ? version : "v" + version;
    }

    private static void AppendIfPresent(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }
}
