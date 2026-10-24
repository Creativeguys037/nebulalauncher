using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 资源页排序：把界面上的排序选择翻译成 Modrinth 的 <c>index</c> 查询参数。
///
/// 这一层是纯字符串逻辑，不联网。<c>index</c> 是查询串的一部分，白名单校验是安全边界，
/// 所以反向用例（注入形状的值）要覆盖到。
/// </summary>
public class CoreMods_SearchSortTests
{
    [Theory]
    [InlineData("downloads", true, "downloads")]
    [InlineData("follows", true, "follows")]
    [InlineData("newest", false, "newest")]
    [InlineData("updated", false, "updated")]
    [InlineData("relevance", true, "relevance")]
    public void 白名单内的排序键原样保留(string sort, bool hasQuery, string expected)
    {
        Assert.Equal(expected, ModrinthService.NormalizeSortIndex(sort, hasQuery));
    }

    [Theory]
    [InlineData(null, true, "relevance")]
    [InlineData(null, false, "downloads")]
    [InlineData("", true, "relevance")]
    [InlineData("   ", false, "downloads")]
    [InlineData("relevance", false, "downloads")]
    [InlineData("DownloadCount", true, "relevance")]
    [InlineData("downloads&limit=99", true, "relevance")]
    public void 未知或越界的排序键回落到默认规则(string? sort, bool hasQuery, string expected)
    {
        Assert.Equal(expected, ModrinthService.NormalizeSortIndex(sort, hasQuery));
    }

    [Fact]
    public void 排序键进入URL且不改动facets()
    {
        const string sort = "newest";

        var url = ModrinthService.BuildSearchUrl(ContentKind.Mod, "jei", "1.20.1", LoaderKind.Fabric, 40, 20, sort);

        Assert.Contains("index=newest", url, StringComparison.Ordinal);
        Assert.Contains("offset=40", url, StringComparison.Ordinal);
        Assert.Contains("limit=20", url, StringComparison.Ordinal);

        // facets 与不传排序时完全一致：排序不该动过滤条件。
        var baseline = ModrinthService.BuildSearchUrl(ContentKind.Mod, "jei", "1.20.1", LoaderKind.Fabric, 40, 20);
        var baselineFacets = ModrinthService.BuildSearchFacets(ContentKind.Mod, "1.20.1", LoaderKind.Fabric);
        Assert.Contains(Uri.EscapeDataString(baselineFacets), url, StringComparison.Ordinal);
        Assert.Contains("index=relevance", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void 恶意排序值不会污染查询串()
    {
        var url = ModrinthService.BuildSearchUrl(ContentKind.Mod, "", null, LoaderKind.Vanilla, 0, 20, "downloads&limit=999&x=");

        // 未通过白名单 → 回落成「空查询用下载量」，注入片段不出现在 URL 里。
        Assert.Contains("index=downloads", url, StringComparison.Ordinal);
        Assert.DoesNotContain("limit=999", url, StringComparison.Ordinal);
    }
}