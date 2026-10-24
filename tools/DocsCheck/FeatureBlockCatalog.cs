using System.Text.Json;

namespace NebulaLauncher.DocsCheck;

/// <summary>
/// 读取 <c>feature-blocks.json</c>：数据与判定逻辑分离，换一份数据就能换一组探测点。
/// </summary>
public static class FeatureBlockCatalog
{
    /// <summary>
    /// 探测点数据在仓库里的相对位置。这是仓库约定（票 #8 定的目录布局），不是可配置项：
    /// 数据文件跟着工具走，装机目录里的副本只作兜底，所以不开放命令行开关。
    /// </summary>
    public const string DefaultRelativePath = "tools/DocsCheck/feature-blocks.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从 JSON 文件读取功能点清单。</summary>
    /// <param name="jsonPath">JSON 文件的绝对路径。</param>
    public static IReadOnlyList<FeatureBlock> Load(string jsonPath)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);

        var json = File.ReadAllText(jsonPath);
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, Options);
        if (document?.Blocks is null)
        {
            return [];
        }

        var blocks = new List<FeatureBlock>(document.Blocks.Count);
        foreach (var block in document.Blocks)
        {
            blocks.Add(new FeatureBlock(
                block.Id ?? string.Empty,
                ToProbes(block.Code),
                ToProbes(block.Tests),
                block.Title ?? string.Empty,
                block.Summary ?? string.Empty,
                ToColumns(block.Columns)));
        }

        return blocks;
    }

    private static FeatureColumns? ToColumns(ColumnsDocument? columns)
    {
        if (columns is null)
        {
            return null;
        }

        return new FeatureColumns(
            columns.Entry ?? string.Empty,
            columns.ViewModel ?? string.Empty,
            columns.Core ?? string.Empty,
            columns.Storage ?? string.Empty);
    }

    private static List<CodeProbe> ToProbes(List<ProbeDocument>? probes)
    {
        var result = new List<CodeProbe>();
        if (probes is null)
        {
            return result;
        }

        foreach (var probe in probes)
        {
            if (string.IsNullOrWhiteSpace(probe.Path))
            {
                continue;
            }

            result.Add(new CodeProbe(probe.Path, string.IsNullOrWhiteSpace(probe.Symbol) ? null : probe.Symbol));
        }

        return result;
    }

    private sealed class CatalogDocument
    {
        public List<BlockDocument>? Blocks { get; init; }
    }

    private sealed class BlockDocument
    {
        public string? Id { get; init; }

        public string? Title { get; init; }

        public string? Summary { get; init; }

        public ColumnsDocument? Columns { get; init; }

        public List<ProbeDocument>? Code { get; init; }

        public List<ProbeDocument>? Tests { get; init; }
    }

    private sealed class ColumnsDocument
    {
        public string? Entry { get; init; }

        public string? ViewModel { get; init; }

        public string? Core { get; init; }

        public string? Storage { get; init; }
    }

    private sealed class ProbeDocument
    {
        public string? Path { get; init; }

        public string? Symbol { get; init; }
    }
}
