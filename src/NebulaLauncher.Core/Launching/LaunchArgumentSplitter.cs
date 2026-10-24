using System.Text;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 命令行参数切分：按空白分段，双引号内的空白保留，引号本身去掉。
///
/// 为什么不用 CmlLib 的 <c>Parser.ParseCommandLineToArguments</c>：它把 <c>\</c> 当转义符，
/// 于是 <c>-Djava.library.path="C:\Program Files\Java\bin"</c> 会被解析成
/// <c>-Djava.library.path=C:Program FilesJavabin</c>——Windows 路径的反斜杠被整段吃掉。
/// JVM 参数里反斜杠是普通字符而不是转义符，所以这里只认引号。
///
/// 单独成类是为了能直接单测：切分规则出错时表现为「参数悄悄变形」，
/// 只有对比解析结果才看得出来。
/// </summary>
internal static class LaunchArgumentSplitter
{
    public static List<string> Split(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var arguments = new List<string>(8);
        var buffer = new StringBuilder(input.Length);
        var inQuotes = false;
        var hasContent = false;

        foreach (var character in input)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                // 空引号 "" 也算一个参数：它可能是有意的空值（-Dkey=""）。
                hasContent = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (hasContent)
                {
                    arguments.Add(buffer.ToString());
                    buffer.Clear();
                    hasContent = false;
                }

                continue;
            }

            buffer.Append(character);
            hasContent = true;
        }

        if (hasContent)
        {
            arguments.Add(buffer.ToString());
        }

        return arguments;
    }
}
