using System.Text.RegularExpressions;

namespace NebulaLauncher.Core.Logging;

/// <summary>
/// 日志脱敏。抄 PCL2 <c>PclLogger.FilterAccessToken</c> 的做法：
/// 令牌一类的凭据一旦进了日志，就等于进了用户随手发出的求助帖。
///
/// 三类规则：
/// <list type="bullet">
/// <item><b>令牌</b>：<c>key=value</c> / JSON 字段 / <c>Bearer</c> 头 / 命令行参数（<c>--accessToken x</c>）。</item>
/// <item><b>账户名</b>：只在「形态确定」的位置替换——<c>--username x</c>、
/// <c>Setting user: x</c>、<c>Logged in as x</c>、<c>UUID of player x</c>。
/// 不做全文的「猜用户名」：本层拿不到账户表，全文替换会把正常文本也改坏。</item>
/// <item><b>本机路径的用户段</b>：<c>C:\Users\alice\…</c> → <c>C:\Users\&lt;user&gt;\…</c>，
/// 路径其余部分必须保留——否则接收方看不出是 <c>mods</c> 还是 <c>config</c> 出问题。</item>
/// </list>
/// </summary>
public static class LogRedactor
{
    /// <summary>替换成这个，保留「这里原本有个值」的信息，长度不泄露。</summary>
    public const string Mask = "***";

    /// <summary>
    /// 用户名与本机路径用户段的替换文本。用尖括号而不是 <see cref="Mask"/>：
    /// 读日志的人需要知道「这里原本是个用户名」，而 <c>***</c> 会让人以为是令牌。
    /// </summary>
    public const string UserMask = "<user>";

    private static readonly Regex TokenAssignment = new(
        @"(?<key>""?\b(?:(?:access|refresh|client)[\s_-]*token|session[\s_-]*id|uuid)\b""?\s*[=:]\s*""?)(?<value>[^""\s;&,}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BearerHeader = new(
        @"(?<key>\bBearer\s+)(?<value>[A-Za-z0-9._~+/\-]{8,}=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 命令行参数形式：<c>--accessToken abc123</c> / <c>--accessToken, abc123</c> / <c>--uuid=0123…</c>。
    ///
    /// 启动命令把这几个值原样交给 JVM，进程崩溃时它们常跟着
    /// <c>ModLauncher running: args […]</c> 一起打进日志。三种分隔符（空格 / 逗号 / 等号）都要认：
    /// CmlLib 打印的是逗号分隔的列表，手写脚本里常见等号形式。
    /// </summary>
    private static readonly Regex CommandLineCredential = new(
        @"(?<key>--(?:accessToken|access_token|clientToken|client_token|sessionId|session_id|uuid|username|userType|session)\b(?:\s*[,=]\s*|\s+))(?<value>[^\s""';,\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 游戏日志里的账户名。这三条是 Mojang 客户端与常见加载器实际会打的形态，不是凭空猜的字符串：
    /// 原版在启动时打 <c>Setting user: &lt;name&gt;</c>，Forge 在玩家加入时打 <c>UUID of player &lt;name&gt; is …</c>。
    /// </summary>
    private static readonly Regex AccountName = new(
        @"(?<key>(?:Setting user|Logged in as)\s*:?\s*|UUID of player\s+)(?<value>[^\s,;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 本机路径的用户段。三种形态：Windows <c>C:\Users\name\</c>、Linux <c>/home/name/</c>、macOS <c>/Users/name/</c>。
    /// 前置负向断言排除 URL 路径：<c>https://example.com/home/alice/</c> 不该被当成主目录。
    /// </summary>
    private static readonly Regex HomePath = new(
        @"(?<![A-Za-z0-9._\-])(?<prefix>(?:[A-Za-z]:[\\/]+Users[\\/]+|/home/|/Users/))(?<user>[^\\/\r\n""'<>]+)(?<rest>[\\/])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 把一段文本里的凭据、账户名与本机路径用户段替换掉。null / 空输入原样返回。
    ///
    /// 顺序有依赖：先处理「有明确 key 的凭据」，再处理账户名，最后才动路径——
    /// 反过来会让路径里的用户名先被替换掉，之后 <c>--username</c> 仍能命中，但日志里
    /// 同一用户的两种出现形式会用不同的掩码，读起来自相矛盾。
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = TokenAssignment.Replace(
            text,
            match => match.Groups["key"].Value + Mask);

        result = BearerHeader.Replace(
            result,
            match => match.Groups["key"].Value + Mask);

        result = CommandLineCredential.Replace(
            result,
            match => match.Groups["key"].Value + Mask);

        result = AccountName.Replace(
            result,
            match => match.Groups["key"].Value + UserMask);

        return HomePath.Replace(
            result,
            match => match.Groups["prefix"].Value + UserMask + match.Groups["rest"].Value);
    }
}
