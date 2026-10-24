using System.Text.Json;

namespace NebulaLauncher.DocsCheck;

/// <summary>
/// 读取 <c>feature-blocks.json</c>：数据与判定逻辑分离，换一份数据就能换一组探测点。
/// </summary>
public static class FeatureBlockCatalog
{
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
                ToProbes(block.Tests)));
        }

        return blocks;
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

        public List<ProbeDocument>? Code { get; init; }

        public List<ProbeDocument>? Tests { get; init; }
    }

    private sealed class ProbeDocument
    {
        public string? Path { get; init; }

        public string? Symbol { get; init; }
    }
}
