using System.Text;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 把一条完整启动命令落成可执行脚本。
///
/// 抄 PCL2 的 <c>LatestLaunch.bat</c>（<c>ModLaunch.cs:2871-2880</c>）：PCL2 无条件写这个文件，
/// 用户报错时直接把它贴给别人就能复现。NebulaLauncher 原来只能「查看命令」却不能保存，
/// 想用 IDE 调试或 profiler 拉起同一个实例就得手抄。
///
/// 两个安全约束：
/// <list type="bullet">
/// <item>命令里含 <c>--accessToken</c>，默认替换成等长的 <c>*</c>，避免脚本被随手贴到群里就泄露账号；</item>
/// <item>脚本落在用户指定路径，不落在实例目录——删除实例时不应该连脚本一起丢。</item>
/// </list>
/// </summary>
public static class LaunchScriptExporter
{
    /// <summary>导出脚本；返回写入成功的绝对路径，失败返回 null。</summary>
    public static async Task<string?> ExportAsync(
        string commandLine,
        string gameDirectory,
        string targetPath,
        string? accessToken = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var masked = MaskAccessToken(commandLine, accessToken);
        var isWindowsScript = targetPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                              || targetPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

        var content = isWindowsScript
            ? BuildBatch(masked, gameDirectory, title)
            : BuildShell(masked, gameDirectory, title);

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 无 BOM：cmd.exe 解析 .bat 时前置 BOM 会变成乱码乱码字符，第一行 `@echo off` 就失效。
            await File.WriteAllTextAsync(targetPath, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            return Path.GetFullPath(targetPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把 <c>--accessToken &lt;value&gt;</c> 的取值替换成等长星号。
    /// 传了 <paramref name="accessToken"/> 就精确替换（更可靠：token 可能带特殊字符），
    /// 没传则按参数名找下一个 token。
    /// </summary>
    public static string MaskAccessToken(string commandLine, string? accessToken = null)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(accessToken))
        {
            return commandLine.Replace(accessToken, new string('*', accessToken.Length), StringComparison.Ordinal);
        }

        return MaskByFlag(commandLine, "--accessToken");
    }

    /// <summary>按参数名把紧随其后的取值打码。</summary>
    private static string MaskByFlag(string commandLine, string flag)
    {
        var index = commandLine.IndexOf(flag, StringComparison.Ordinal);
        if (index < 0)
        {
            return commandLine;
        }

        var valueStart = index + flag.Length;
        while (valueStart < commandLine.Length && commandLine[valueStart] == ' ')
        {
            valueStart++;
        }

        if (valueStart >= commandLine.Length)
        {
            return commandLine;
        }

        // 值可能被引号包住：保留引号本身，只打码里面的内容。
        var quoted = commandLine[valueStart] == '"';
        var contentStart = quoted ? valueStart + 1 : valueStart;
        var contentEnd = contentStart;

        while (contentEnd < commandLine.Length && commandLine[contentEnd] != (quoted ? '"' : ' '))
        {
            contentEnd++;
        }

        if (contentEnd <= contentStart)
        {
            return commandLine;
        }

        var masked = new string('*', contentEnd - contentStart);
        return string.Concat(commandLine.AsSpan(0, contentStart), masked, commandLine.AsSpan(contentEnd));
    }

    /// <summary>生成 Windows 批处理脚本内容。</summary>
    public static string BuildBatch(string commandLine, string gameDirectory, string? title = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");

        // 65001 = UTF-8。Java 输出与路径都可能含中文，不切代码页会显示成乱码。
        builder.AppendLine("chcp 65001 > nul");

        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "Minecraft" : title!;
        builder.Append("title ").AppendLine(resolvedTitle);

        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            builder.Append("cd /D \"").Append(gameDirectory).AppendLine("\"");
        }

        builder.AppendLine("echo 由 NebulaLauncher 导出的启动脚本");
        builder.AppendLine(commandLine);

        // 双击运行后窗口立刻消失会看不到报错；pause 让用户能读到最后的输出。
        builder.AppendLine("pause");
        return builder.ToString();
    }

    /// <summary>生成 POSIX shell 脚本内容。</summary>
    public static string BuildShell(string commandLine, string gameDirectory, string? title = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#!/bin/sh");

        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("# ").AppendLine(title);
        }

        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            builder.Append("cd \"").Append(gameDirectory).AppendLine("\" || exit 1");
        }

        builder.AppendLine("echo \"由 NebulaLauncher 导出的启动脚本\"");
        builder.AppendLine(commandLine);
        return builder.ToString();
    }
}
