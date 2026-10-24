using System.Text.RegularExpressions;

namespace NebulaLauncher.DocsCheck;

/// <summary>一条站内 markdown 链接。</summary>
/// <param name="Document">文档相对路径。</param>
/// <param name="Line">行号，从 1 开始。</param>
/// <param name="Text">链接文字。</param>
/// <param name="Target">链接目标原文（可能带 <c>#锚点</c> 或标题）。</param>
public sealed record DocLink(string Document, int Line, string Text, string Target);

/// <summary>链接目标不成立的一条记录。</summary>
/// <param name="Document">文档相对路径。</param>
/// <param name="Line">行号，从 1 开始。</param>
/// <param name="Text">链接文字。</param>
/// <param name="Target">链接目标原文。</param>
/// <param name="Reason">失败原因，目前只有 <see cref="TargetMissing"/>。</param>
public sealed record DocLinkFailure(string Document, int Line, string Text, string Target, string Reason)
{
    /// <summary>站内相对 <c>.md</c> 目标在仓库里不存在。</summary>
    public const string TargetMissing = "链接目标不存在";
}

/// <summary>链接校验结果。</summary>
/// <param name="Links">成立的链接。</param>
/// <param name="Failures">不成立的链接。</param>
public sealed record DocLinkReport(IReadOnlyList<DocLink> Links, IReadOnlyList<DocLinkFailure> Failures)
{
    /// <summary>所有站内链接都指向存在的文件即通过；没有链接的文档天然通过。</summary>
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// 站内 markdown 链接校验：只认 <c>[文本](目标)</c>，目标是相对 <c>.md</c> 路径（不是 http/https/mailto，
/// 不以 <c>#</c> 开头）时，要求目标文件在仓库里存在。
///
/// 反引号单元里的链接是示例，不算真链接——先按行剥掉行内代码再匹配。
/// 文档自身缺失由 <see cref="DocReferenceChecker"/> 统一报，这里跳过。
/// </summary>
public static class DocLinkChecker
{
    private static readonly Regex InlineLink = new(
        @"\[(?<text>[^\]\n]*)\]\((?<target>[^)\s]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>校验一组文档里的站内链接。</summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="documents">文档相对路径（<c>/</c> 分隔）；不存在的文档跳过。</param>
    public static DocLinkReport Check(string repoRoot, IReadOnlyList<string> documents)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(documents);

        var links = new List<DocLink>();
        var failures = new List<DocLinkFailure>();

        foreach (var document in documents)
        {
            var fullPath = RepoPaths.Resolve(repoRoot, document);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var documentDirectory = Path.GetDirectoryName(fullPath) ?? repoRoot;

            foreach (var prose in MarkdownScanner.ProseLines(fullPath))
            {
                foreach (Match match in InlineLink.Matches(MarkdownScanner.WithoutCodeSpans(prose.Text)))
                {
                    var target = match.Groups["target"].Value;
                    if (!IsLocalMarkdownTarget(target))
                    {
                        continue;
                    }

                    var text = match.Groups["text"].Value;
                    if (ResolveTarget(documentDirectory, target) is { } resolved && File.Exists(resolved))
                    {
                        links.Add(new DocLink(document, prose.Number, text, target));
                    }
                    else
                    {
                        failures.Add(new DocLinkFailure(
                            document,
                            prose.Number,
                            text,
                            target,
                            DocLinkFailure.TargetMissing));
                    }
                }
            }
        }

        return new DocLinkReport(links, failures);
    }

    /// <summary>目标是站内相对 <c>.md</c> 路径：无协议、不以 <c>#</c> 开头、去掉 <c>#锚点</c> 后以 <c>.md</c> 结尾。</summary>
    private static bool IsLocalMarkdownTarget(string target)
    {
        if (target.Length == 0 || target.StartsWith('#'))
        {
            return false;
        }

        if (target.Contains("://", StringComparison.Ordinal)
            || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith('/'))
        {
            return false;
        }

        var withoutFragment = StripFragment(target);
        return withoutFragment.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>链接目标按所在文档的目录解析（<c>../GLOSSARY.md</c> 这类同样成立）；越出仓库根时返回 null。</summary>
    private static string? ResolveTarget(string documentDirectory, string target)
    {
        var relative = StripFragment(target).Replace('/', Path.DirectorySeparatorChar);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(documentDirectory, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }

        return resolved;
    }

    private static string StripFragment(string target)
    {
        var hash = target.IndexOf('#');
        return hash < 0 ? target : target[..hash];
    }
}
