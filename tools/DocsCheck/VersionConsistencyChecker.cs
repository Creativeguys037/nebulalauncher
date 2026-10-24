using System.Text.RegularExpressions;

namespace NebulaLauncher.DocsCheck;

/// <summary>版本口径检查结果。</summary>
/// <param name="Version"><c>Directory.Build.props</c> 里的 <c>&lt;Version&gt;</c>；读不到为空串。</param>
/// <param name="ReleaseTag"><c>CONVENTIONS.md</c> 发布命令里的 tag；读不到为空串。</param>
/// <param name="Failures">失败说明；空表示三处口径一致、release notes 齐全且无旧口径残留。</param>
public sealed record VersionConsistencyReport(
    string Version,
    string ReleaseTag,
    IReadOnlyList<string> Failures)
{
    /// <summary>没有失败即通过。</summary>
    public bool Passed => Failures.Count == 0;

    /// <summary>是否真的跑过这项检查；空结果显示「未检查」而不是「通过」。</summary>
    public bool Checked => Version.Length > 0 || ReleaseTag.Length > 0 || Failures.Count > 0;
}

/// <summary>
/// 版本口径漂移检查（票 #25）：<c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>、
/// <c>CONVENTIONS.md</c> 的发布 tag、<c>docs/PROJECT.md</c> 的版本口径三处必须一致；
/// release notes 必须有四节；三处文档不得留旧口径。
///
/// 版本号只从 <c>Directory.Build.props</c> 读，不在这里写死；文件读不到或正则不命中
/// 一律记进 <see cref="VersionConsistencyReport.Failures"/>，不抛异常。
/// </summary>
public static class VersionConsistencyChecker
{
    /// <summary>版本号的唯一来源。</summary>
    public const string BuildPropsPath = "Directory.Build.props";

    /// <summary>发布命令所在文档。</summary>
    public const string ConventionsPath = "CONVENTIONS.md";

    /// <summary>对外版本口径所在文档。</summary>
    public const string ProjectPath = "docs/PROJECT.md";

    /// <summary>release notes 目录（票 #24 定稿）。</summary>
    public const string ReleaseNotesDirectory = "docs/release-notes";

    /// <summary>release notes 必须有的四个二级标题。</summary>
    public static readonly IReadOnlyList<string> RequiredSections =
        ["概览", "安装与体积", "已知限制", "证据链与签名说明"];

    /// <summary>三处文档里不许再出现的旧口径原话。</summary>
    public static readonly IReadOnlyList<string> LegacyPhrases =
        ["体积以本表为准", "引用轮次号而不是编"];

    /// <summary>
    /// 没有做版本口径检查时的占位结果。只给「临时文档树没有 <c>Directory.Build.props</c>」这种
    /// 不适用场景用；真实仓库缺文件时 <see cref="Check"/> 会逐条记失败，不走这里。
    /// </summary>
    public static VersionConsistencyReport Empty { get; } = new(string.Empty, string.Empty, []);

    private static readonly Regex VersionElement = new(
        @"<Version>\s*(?<version>[^<\s]+)\s*</Version>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReleaseCommand = new(
        @"gh\s+release\s+create\s+(?<tag>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionMention = new(
        @"v\d+(?:\.\d+)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SecondLevelHeading = new(
        @"^##[ \t]+(?<title>.+?)[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>标题的序号前缀（<c>一、</c>、<c>1. </c>、<c>第一章 </c>）；节名不受编号形态影响。</summary>
    private static readonly Regex SectionNumberPrefix = new(
        @"^(?:[一二三四五六七八九十百零〇]+、|\d+[.、]\s*|第[一二三四五六七八九十百零〇\d]+[章节]\s*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>跑完三类版本口径检查。</summary>
    /// <param name="root">仓库根绝对路径。</param>
    /// <returns>检查结果；任何读不到或对不上的地方都记进失败清单。</returns>
    public static VersionConsistencyReport Check(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var failures = new List<string>();

        var buildProps = ReadDocument(root, BuildPropsPath, "读不到 <Version>", failures);
        var conventions = ReadDocument(root, ConventionsPath, "读不到发布 tag", failures);
        var project = ReadDocument(root, ProjectPath, "读不到版本口径", failures);

        var version = buildProps is null ? null : ParseVersion(buildProps, failures);
        var tag = conventions is null ? null : ParseReleaseTag(conventions, failures);

        string? notes = null;
        var notesDocument = string.Empty;

        if (version is not null)
        {
            notesDocument = $"{ReleaseNotesDirectory}/v{version}.md";
            notes = ReadDocument(root, notesDocument, "读不到 release notes", failures);

            var expectedTag = "v" + version;
            if (tag is not null && !string.Equals(tag, expectedTag, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{ConventionsPath}：发布 tag 是 {tag}，与 {BuildPropsPath} 的 <Version> {version} 不一致（期望 {expectedTag}）");
            }

            if (project is not null)
            {
                CheckProject(project, version, failures);
            }

            if (notes is not null)
            {
                CheckReleaseNotes(notesDocument, notes, failures);
            }
        }

        CheckLegacyWording(ConventionsPath, conventions, failures);
        CheckLegacyWording(ProjectPath, project, failures);
        CheckLegacyWording(notesDocument, notes, failures);

        return new VersionConsistencyReport(version ?? string.Empty, tag ?? string.Empty, failures);
    }

    /// <summary>读文档；文件不存在或读失败都记一条失败并返回 null。</summary>
    private static string? ReadDocument(
        string root,
        string document,
        string purpose,
        List<string> failures)
    {
        var path = RepoPaths.Resolve(root, document);
        if (!File.Exists(path))
        {
            failures.Add($"{document}：文件不存在，{purpose}");
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{document}：读不到（{ex.Message}）");
            return null;
        }
    }

    /// <summary>从 <c>Directory.Build.props</c> 的正文里取 <c>&lt;Version&gt;</c>。</summary>
    private static string? ParseVersion(string text, List<string> failures)
    {
        var match = VersionElement.Match(text);
        if (!match.Success)
        {
            failures.Add($"{BuildPropsPath}：没有 <Version> 元素，读不到版本号");
            return null;
        }

        return match.Groups["version"].Value;
    }

    /// <summary>从 <c>CONVENTIONS.md</c> 的正文里取 <c>gh release create &lt;tag&gt;</c> 的 tag。</summary>
    private static string? ParseReleaseTag(string text, List<string> failures)
    {
        var match = ReleaseCommand.Match(text);
        if (!match.Success)
        {
            failures.Add($"{ConventionsPath}：没有 gh release create <tag> 形式的发布命令，读不到 tag");
            return null;
        }

        return match.Groups["tag"].Value.Trim('"', '\'', '`');
    }

    /// <summary>校验 <c>docs/PROJECT.md</c> 同时出现发布号与 <c>&lt;Version&gt;</c> 原文。</summary>
    private static void CheckProject(string text, string version, List<string> failures)
    {
        var expectedTag = "v" + version;
        if (!text.Contains(expectedTag, StringComparison.Ordinal))
        {
            failures.Add(
                $"{ProjectPath}：没有出现发布号 {expectedTag}（期望与 {BuildPropsPath} 的 Version {version} 一致；"
                + $"实际出现的发布号：{DescribeTags(text)}）");
        }

        var expectedElement = $"<Version>{version}</Version>";
        if (!text.Contains(expectedElement, StringComparison.Ordinal))
        {
            failures.Add(
                $"{ProjectPath}：没有出现 {expectedElement}（期望与 {BuildPropsPath} 的 Version {version} 一致；"
                + $"实际出现的版本元素：{DescribeElements(text)}）");
        }
    }

    /// <summary>校验 release notes 四节齐全；缺节逐条报，文件不存在由 <see cref="ReadDocument"/> 单独报。</summary>
    private static void CheckReleaseNotes(string document, string text, List<string> failures)
    {
        var headings = SecondLevelHeading.Matches(text)
            .Select(match => NormalizeHeading(match.Groups["title"].Value))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var section in RequiredSections)
        {
            if (!headings.Contains(section))
            {
                failures.Add($"{document}：缺二级标题「{section}」");
            }
        }
    }

    /// <summary>三处文档里出现旧口径原话即失败，消息带文件名、行号与命中片段。</summary>
    private static void CheckLegacyWording(string document, string? text, List<string> failures)
    {
        if (text is null)
        {
            return;
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            foreach (var phrase in LegacyPhrases)
            {
                if (lines[index].Contains(phrase, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{document}:{index + 1}：残留旧口径「{phrase}」（命中片段：{Snippet(lines[index])}）");
                }
            }
        }
    }

    /// <summary>去掉标题的序号前缀，只留节名。</summary>
    private static string NormalizeHeading(string title)
        => SectionNumberPrefix.Replace(title.Trim(), string.Empty).Trim();

    private static string DescribeTags(string text)
    {
        var tags = VersionMention.Matches(text)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return tags.Count == 0 ? "无" : string.Join("、", tags);
    }

    private static string DescribeElements(string text)
    {
        var elements = VersionElement.Matches(text)
            .Select(match => $"<Version>{match.Groups["version"].Value}</Version>")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return elements.Count == 0 ? "无" : string.Join("、", elements);
    }

    /// <summary>命中行压成一行片段，避免把整行表格塞进报告。</summary>
    private static string Snippet(string line)
    {
        var text = line.Trim();
        return text.Length <= 60 ? text : text[..60] + "…";
    }
}
