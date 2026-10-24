namespace NebulaLauncher.DocsCheck;

/// <summary>命令行参数解析结果。</summary>
/// <param name="Root">仓库根绝对路径。</param>
/// <param name="Documents">知识文档相对路径（<c>/</c> 分隔）：既校验引用与链接，也计入行数预算。</param>
/// <param name="Json">是否输出机读 JSON。</param>
/// <param name="AllowMissing">是否允许文档缺失（缺失时记「跳过」而不是失败）。</param>
/// <param name="EmitFeatures">传了 <c>--emit-features</c> 时是要写出的文档路径，否则为空。</param>
/// <param name="Help">传了 <c>--help</c>：只打印用法与退出码表。</param>
/// <param name="Evidence">证据链文档：只校验引用与链接，不进行数预算；为空表示没有。</param>
public sealed record DocCheckOptions(
    string Root,
    IReadOnlyList<string> Documents,
    bool Json,
    bool AllowMissing = false,
    string? EmitFeatures = null,
    bool Help = false,
    IReadOnlyList<string>? Evidence = null)
{
    /// <summary>缺省扫描的五份文档（票 #8 规定），默认都属必需文档，缺失即失败。</summary>
    public static readonly IReadOnlyList<string> DefaultDocuments =
    [
        "docs/PROJECT.md",
        "docs/CODE.md",
        "docs/FEATURES.md",
        "docs/PITFALLS.md",
        "GLOSSARY.md",
    ];

    /// <summary>证据链文档清单，<c>--evidence</c> 没传时为空。</summary>
    public IReadOnlyList<string> EvidenceDocuments => Evidence ?? [];

    /// <summary>参与引用与链接校验的全部文档：知识文档在前，证据链文档在后，去重保序。</summary>
    public IReadOnlyList<string> CheckedDocuments =>
        Documents.Concat(EvidenceDocuments).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>解析参数。<c>--root</c> 缺省为当前目录，<c>--docs</c> 缺省为 <see cref="DefaultDocuments"/>。</summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="currentDirectory">当前目录，用于解析相对路径。</param>
    public static DocCheckOptions Parse(IReadOnlyList<string> args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(currentDirectory);

        var basePath = Path.GetFullPath(currentDirectory);
        var root = basePath;
        var documents = new List<string>();
        var evidence = new List<string>();
        var json = false;
        var allowMissing = false;
        var help = false;
        string? emitFeatures = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--root":
                    if (index + 1 < args.Count && !IsOption(args[index + 1]))
                    {
                        root = args[++index];
                    }

                    break;

                case "--docs":
                    while (index + 1 < args.Count && !IsOption(args[index + 1]))
                    {
                        documents.Add(args[++index]);
                    }

                    break;

                case "--evidence":
                    while (index + 1 < args.Count && !IsOption(args[index + 1]))
                    {
                        evidence.Add(args[++index]);
                    }

                    break;

                case "--emit-features":
                    if (index + 1 < args.Count && !IsOption(args[index + 1]))
                    {
                        emitFeatures = args[++index];
                    }

                    break;

                case "--json":
                    json = true;
                    break;

                case "--allow-missing":
                    allowMissing = true;
                    break;

                case "--help":
                    help = true;
                    break;
            }
        }

        var rootPath = Path.GetFullPath(root, basePath);
        return new DocCheckOptions(
            rootPath,
            documents.Count == 0 ? DefaultDocuments : documents,
            json,
            allowMissing,
            emitFeatures is null ? null : Path.GetFullPath(emitFeatures, rootPath),
            help,
            evidence.Count == 0 ? null : evidence);
    }

    private static bool IsOption(string value) => value.StartsWith("--", StringComparison.Ordinal);
}
