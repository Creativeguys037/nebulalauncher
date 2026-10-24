using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// NeoForge 版本目录的解析与 MC 版本反推。
///
/// 这一层是纯字符串/JSON 逻辑，没有任何网络：真实端点（BMCLAPI 的
/// <c>/neoforge/meta/api/maven/details/releases/net/neoforged/{neoforge,forge}</c>）
/// 的形状有样本，但连通性依赖本机网络，不属于单测范围。
/// </summary>
public class CoreVersions_NeoForgeCatalogTests
{
    // BMCLAPI 形状：nexus 目录列表，files[] 里混着子目录与文件。
    private const string BmclApiCatalog = """
    {
      "name": "neoforge",
      "type": "DIRECTORY",
      "files": [
        { "name": "0.25w14craftmine.3-beta", "type": "DIRECTORY" },
        { "name": "20.4.30", "type": "DIRECTORY" },
        { "name": "21.1.264", "type": "DIRECTORY" },
        { "name": "21.1.266", "type": "DIRECTORY" },
        { "name": "21.1.75-beta", "type": "DIRECTORY" },
        { "name": "maven-metadata.xml", "type": "FILE" }
      ]
    }
    """;

    // 官方 maven API 形状：versions[] 平铺。
    private const string OfficialCatalog = """
    { "versions": [ "21.1.264", "1.20.1-47.1.106" ] }
    """;

    [Fact]
    public void 两种目录形状都能解析且只留子目录()
    {
        var fromMirror = VersionService.ParseNeoForgeCatalogNames(BmclApiCatalog);
        Assert.Equal(["0.25w14craftmine.3-beta", "20.4.30", "21.1.264", "21.1.266", "21.1.75-beta"], fromMirror);

        var fromOfficial = VersionService.ParseNeoForgeCatalogNames(OfficialCatalog);
        Assert.Equal(["21.1.264", "1.20.1-47.1.106"], fromOfficial);

        // 坏 JSON 退化成空列表（调用方据此降级成「只能装最新版」），不抛。
        Assert.Empty(VersionService.ParseNeoForgeCatalogNames("{ not json"));
    }

    [Theory]
    [InlineData("21.1.264", "1.21.1")]
    [InlineData("20.4.30", "1.20.4")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("21.1.75-beta", "1.21.1")]
    [InlineData("1.20.1-47.1.106", "1.20.1")]

    // 47.1.82 是 Legacy 时代发布、却没带 "1.20.1-" 前缀的异类。映射规则按字面给出 "1.47.1"
    // （不存在的 MC 版本），要靠名字白名单排除——PCL2 ModDownload.vb:941 也是按名字排它。
    [InlineData("47.1.82", "1.47.1")]
    [InlineData("0.25w14craftmine.3-beta", null)]
    public void 目录名反推MC版本(string name, string? expected)
    {
        Assert.Equal(expected, VersionService.NeoForgeGameVersionOf(name));
    }

    [Fact]
    public void 预发布后缀识别()
    {
        Assert.True(VersionService.IsNeoForgePrerelease("21.1.75-beta"));
        Assert.True(VersionService.IsNeoForgePrerelease("21.1.0-alpha.2"));
        Assert.True(VersionService.IsNeoForgePrerelease("21.1.264+snapshot-1"));
        Assert.False(VersionService.IsNeoForgePrerelease("21.1.264"));
        Assert.False(VersionService.IsNeoForgePrerelease("1.20.1-47.1.106"));
    }
}