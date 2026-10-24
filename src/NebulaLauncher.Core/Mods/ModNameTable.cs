using System.IO;
using System.Reflection;
using System.Text;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// MC 百科译名表：按资源站 slug 查模组的中文名。
///
/// <para/>
/// 数据来源与解析规则均来自 PCL 的 <c>PCLCS/Resource/WikiEntry.cs</c> +
/// <c>PCLCS/Resource/WikiEntries.txt</c>（中华 MOD 百科条目，由 PCL 整理），
/// 本仓库按 PCL《存储库合理使用指南》引入该数据文件并在
/// <c>README.md</c> 署名。
///
/// <para/>
/// 为什么要联网才能用：表里只有 slug，没有 modid。本地 jar 里的 modid（<c>ic2</c>）
/// 与资源站 slug（<c>industrial-craft</c>）不是一套命名，猜不出来——所以必须先用
/// SHA1 / 指纹反查到工程，再拿 slug 来查这张表。离线时没有译名是正常结果。
/// </summary>
internal sealed class ModNameTable
{
    /// <summary>条目之间用 U+00A8 分隔（一行可以放多个条目，按同一条百科关系合并）。</summary>
    private const char EntrySeparator = '\u00A8';

    private const char FieldSeparator = '|';

    /// <summary>slug 位上的 <c>@</c>：前缀表示只属于 Modrinth，后缀表示两边同名，中间表示「CF@MR」。</summary>
    private const char PlatformSeparator = '@';

    /// <summary>译名里的 <c>*</c> 是「补上英文名」的占位符，例如 <c>林业*</c>。</summary>
    private const char EnglishNamePlaceholder = '*';

    private readonly Dictionary<string, string> _curseForge;
    private readonly Dictionary<string, string> _modrinth;

    private ModNameTable(Dictionary<string, string> curseForge, Dictionary<string, string> modrinth)
    {
        _curseForge = curseForge;
        _modrinth = modrinth;
    }

    /// <summary>
    /// 可查的 slug 键数量（CurseForge 键 + Modrinth 键）。
    /// 会比原始条目数少：同一 slug 在多行重复出现时只保留首条，且一条百科条目可能只登记了一个平台。
    /// 用于日志与自检（表没加载成功时是 0）。
    /// </summary>
    public int Count => _curseForge.Count + _modrinth.Count;

    public static ModNameTable Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>按 CurseForge slug 查中文名；没有返回 null（用 <c>?</c> 而不是空串，好让上层回退到原名）。</summary>
    public string? FindCurseForge(string? slug) => Find(_curseForge, slug);

    /// <summary>按 Modrinth slug 查中文名。</summary>
    public string? FindModrinth(string? slug) => Find(_modrinth, slug);

    private static string? Find(Dictionary<string, string> index, string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : index.GetValueOrDefault(slug.Trim());

    /// <summary>
    /// 解析整份译名表。
    ///
    /// <para/>
    /// 每行末尾的 **最后一行** 是 base86 编码的浏览量序列（每 3 字符一个数），与译名无关，
    /// 解析时整行丢弃——这与 PCL 的 <c>WikiEntry.All</c> 一致，不是容错而是格式约定。
    /// </summary>
    public static ModNameTable Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Empty;
        }

        var curseForge = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modrinth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = content.Split('\n');
        // 末行是浏览量，不参与译名索引。空表 / 只有一行时同样安全。
        var lineCount = lines.Length > 0 ? lines.Length - 1 : 0;

        for (var i = 0; i < lineCount; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.Length == 0)
            {
                continue;
            }

            foreach (var entry in line.Split(EntrySeparator))
            {
                ParseEntry(entry, curseForge, modrinth);
            }
        }

        return new ModNameTable(curseForge, modrinth);
    }

    private static void ParseEntry(
        string entry,
        Dictionary<string, string> curseForge,
        Dictionary<string, string> modrinth)
    {
        var fields = entry.Split(FieldSeparator);
        var slugField = fields[0].Trim();

        if (slugField.Length == 0)
        {
            return;
        }

        // 没有第二段 = 只有 slug、没有译名。这类条目对「显示中文名」无用，跳过。
        if (fields.Length < 2)
        {
            return;
        }

        var chineseName = fields[^1].Trim();

        if (chineseName.Length == 0)
        {
            return;
        }

        var (curseForgeSlug, modrinthSlug) = ParseSlugs(slugField);

        if (chineseName.Contains(EnglishNamePlaceholder))
        {
            var english = Capitalize(SlugToWords(curseForgeSlug ?? modrinthSlug ?? slugField));
            chineseName = chineseName.Replace("*", $" ({english})", StringComparison.Ordinal);
        }

        if (curseForgeSlug is not null)
        {
            // 同一 slug 出现多次时保留第一次：PCL 用 FirstOrDefault 取首条，后出现的多为别名。
            curseForge.TryAdd(curseForgeSlug, chineseName);
        }

        if (modrinthSlug is not null)
        {
            modrinth.TryAdd(modrinthSlug, chineseName);
        }
    }

    /// <summary>
    /// 拆解 slug 字段的四种写法：
    /// <c>slug@</c>（两边同名）、<c>@slug</c>（仅 Modrinth）、<c>cf@mr</c>（各一个）、
    /// <c>slug</c>（仅 CurseForge）。
    /// </summary>
    private static (string? CurseForge, string? Modrinth) ParseSlugs(string field)
    {
        if (field.StartsWith(PlatformSeparator))
        {
            var single = field.TrimStart(PlatformSeparator).Trim();
            return single.Length == 0 ? (null, null) : (null, single);
        }

        if (field.EndsWith(PlatformSeparator))
        {
            var shared = field.TrimEnd(PlatformSeparator).Trim();
            return shared.Length == 0 ? (null, null) : (shared, shared);
        }

        var separator = field.IndexOf(PlatformSeparator);

        if (separator < 0)
        {
            return (field.Trim(), null);
        }

        var left = field[..separator].Trim();
        var right = field[(separator + 1)..].Trim();

        return (left.Length == 0 ? null : left, right.Length == 0 ? null : right);
    }

    private static string SlugToWords(string slug) => slug.Replace('-', ' ').Trim();

    /// <summary>与 PCL 的 <c>Capitalize</c> 一致：首字母大写、其余小写（<c>industrial craft</c> → <c>Industrial craft</c>）。</summary>
    private static string Capitalize(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        return string.Concat(char.ToUpperInvariant(text[0]).ToString(), text[1..].ToLowerInvariant());
    }
}

/// <summary>
/// 嵌入译名表的进程内单例。
///
/// <para/>
/// 解析 978 KB / 2.6 万条大约几十毫秒，但只在第一次需要中文名时发生：
/// 用户从不进模组页、或从不联网补全时，这份表根本不会被读。
/// </summary>
internal static class ModNameTranslations
{
    /// <summary>资源名由 <c>RootNamespace</c> + 相对路径生成。</summary>
    private const string ResourceName = "NebulaLauncher.Core.Resources.WikiEntries.txt";

    private static readonly Lazy<ModNameTable> Instance = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>已加载的条目数；0 表示资源缺失（典型原因：发布时漏了 EmbeddedResource）。</summary>
    public static int Count => Instance.Value.Count;

    public static string? FindByCurseForgeSlug(string? slug) => Instance.Value.FindCurseForge(slug);

    public static string? FindByModrinthSlug(string? slug) => Instance.Value.FindModrinth(slug);

    private static ModNameTable Load()
    {
        try
        {
            var assembly = typeof(ModNameTranslations).GetTypeInfo().Assembly;

            using var stream = assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return ModNameTable.Empty;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ModNameTable.Parse(reader.ReadToEnd());
        }
        catch (IOException)
        {
            // 资源读失败不该让模组页崩：没有译名就显示原名，和离线时的表现一致。
            return ModNameTable.Empty;
        }
    }
}
