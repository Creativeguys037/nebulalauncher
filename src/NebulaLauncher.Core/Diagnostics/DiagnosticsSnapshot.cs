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

        AppendIfPresent(lines, "版本", input.LauncherVersion);
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

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "未知";
    }

    private static void AppendIfPresent(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }
}
