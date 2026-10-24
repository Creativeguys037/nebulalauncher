using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// 资源包 / 数据包的 pack_format 对照表与兼容判定。
///
/// 表里的数字来自 minecraft.wiki 的 Pack format 页面（见 <see cref="PackFormatTable"/> 类注释），
/// 这里钉住的是「表与解析逻辑没有漂移」：边界版本、四种安装形态的版本串、四种判定结果。
/// </summary>
public sealed class CoreVersions_PackFormatTests
{
    [Theory]
    [InlineData("1.20", "1.20")]
    [InlineData("1.20.1", "1.20.1")]
    [InlineData("1.20.1-forge-47.4.28", "1.20.1")]
    [InlineData("fabric-loader-0.16.9-1.21.4", "1.21.4")]
    [InlineData("neoforge-21.4.50-1.21.4", "1.21.4")]
    public void 版本抽取_识别常见版本串(string input, string expected)
    {
        Assert.Equal(expected, PackFormatTable.ExtractMinecraftVersion(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("neoforge-21.4.50")]
    [InlineData("forge-47.4.28")]
    [InlineData("unknown")]
    public void 版本抽取_抽不出时返回null而不是猜(string input)
    {
        Assert.Null(PackFormatTable.ExtractMinecraftVersion(input));
    }

    [Theory]
    [InlineData("1.12.2", 3)]
    [InlineData("1.13", 4)]
    [InlineData("1.20", 15)]
    [InlineData("1.20.1", 15)]
    [InlineData("1.21", 34)]
    [InlineData("1.21.4", 46)]
    public void 资源包表_边界版本(string gameVersion, int expected)
    {
        Assert.True(PackFormatTable.TryGet(ContentKind.ResourcePack, gameVersion, out var format), gameVersion);
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("1.13", 4)]
    [InlineData("1.20", 15)]
    [InlineData("1.20.1", 15)]
    [InlineData("1.21", 48)]
    [InlineData("1.21.4", 61)]
    public void 数据包表_边界版本(string gameVersion, int expected)
    {
        Assert.True(PackFormatTable.TryGet(ContentKind.DataPack, gameVersion, out var format), gameVersion);
        Assert.Equal(expected, format);
    }

    [Fact]
    public void 数据包在1_13之前没有pack_format()
    {
        Assert.False(PackFormatTable.TryGet(ContentKind.DataPack, "1.12.2", out _));
        Assert.False(PackFormatTable.TryGet(ContentKind.DataPack, "1.12", out _));

        // 资源包同一时期已经存在（1.12.2 = format 3），它不该被数据包的「1.13 起」规则带偏。
        Assert.True(PackFormatTable.TryGet(ContentKind.ResourcePack, "1.12.2", out var resourceFormat));
        Assert.Equal(3, resourceFormat);
    }

    [Fact]
    public void 数据包在1_18的patch版本之间换format()
    {
        Assert.True(PackFormatTable.TryGet(ContentKind.DataPack, "1.18.1", out var first));
        Assert.True(PackFormatTable.TryGet(ContentKind.DataPack, "1.18.2", out var second));

        Assert.Equal(8, first);
        Assert.Equal(9, second);
    }

    [Fact]
    public void 超出表范围与不支持的类型都返回false()
    {
        // TryGet 是「旧式整数 pack_format」入口：1.21.9 起格式号带小数，统一走 TryGetExpected。
        Assert.False(PackFormatTable.TryGet(ContentKind.ResourcePack, "1.21.9", out _));
        Assert.False(PackFormatTable.TryGet(ContentKind.DataPack, "1.21.9", out _));

        // 早于资源包编号体系起点（1.6.1）。
        Assert.False(PackFormatTable.TryGet(ContentKind.ResourcePack, "1.6", out _));

        Assert.False(PackFormatTable.TryGet(ContentKind.Mod, "1.20.1", out _));
        Assert.False(PackFormatTable.TryGet(ContentKind.ShaderPack, "1.20.1", out _));
        Assert.False(PackFormatTable.TryGet(ContentKind.World, "1.20.1", out _));

        // 新入口同样只认表里有的版本：1.21.12 与 26.4 都晚于 wiki 当前最后一条（26.3）。
        Assert.False(PackFormatTable.TryGetExpected(ContentKind.DataPack, "1.21.12", out _));
        Assert.False(PackFormatTable.TryGetExpected(ContentKind.DataPack, "26.4", out _));
    }

    [Fact]
    public void 判定_兼容()
    {
        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, 15, "1.20.1");

        Assert.Equal(PackFormatVerdict.Compatible, assessment.Verdict);
        Assert.False(assessment.IsMismatch);
        Assert.Equal(15, assessment.ExpectedFormat);
        Assert.Equal("1.20.x", assessment.RequiredRange);
        Assert.Contains("一致", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 判定_过旧()
    {
        // format 15 的数据包装进 1.21.4（需要 61）。
        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, 15, "1.21.4");

        Assert.Equal(PackFormatVerdict.TooOld, assessment.Verdict);
        Assert.True(assessment.IsMismatch);
        Assert.Equal(61, assessment.ExpectedFormat);
        Assert.Equal("1.21.4", assessment.MinecraftVersion);
        Assert.Equal("1.20.x", assessment.RequiredRange);
        Assert.Contains("不匹配", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 判定_过新()
    {
        // format 46 的资源包用在 1.20.1（需要 15）。
        var assessment = PackFormatTable.Evaluate(ContentKind.ResourcePack, 46, "1.20.1");

        Assert.Equal(PackFormatVerdict.TooNew, assessment.Verdict);
        Assert.True(assessment.IsMismatch);
        Assert.Equal(15, assessment.ExpectedFormat);
        Assert.Equal("1.21.4", assessment.RequiredRange);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("neoforge-21.4.50")]
    [InlineData("1.21.12")]
    public void 判定_版本判不出时返回Unknown(string gameVersion)
    {
        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, 15, gameVersion);

        Assert.Equal(PackFormatVerdict.Unknown, assessment.Verdict);
        Assert.False(assessment.IsMismatch);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
    }

    [Fact]
    public void 判定_pack里没有合法format时为Unknown()
    {
        var assessment = PackFormatTable.Evaluate(ContentKind.ResourcePack, 0, "1.20.1");

        Assert.Equal(PackFormatVerdict.Unknown, assessment.Verdict);
        Assert.Contains("pack_format", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 判定_非资源包数据包类型为Unknown()
    {
        var assessment = PackFormatTable.Evaluate(ContentKind.Mod, 15, "1.20.1");

        Assert.Equal(PackFormatVerdict.Unknown, assessment.Verdict);
        Assert.False(assessment.IsMismatch);
    }

    [Fact]
    public void 从内容版本文案里取format数字()
    {
        // 数据包：ContentService 把 pack_format 写成「format 61」。
        Assert.Equal(61, PackFormatTable.ReadPackFormat(ContentKind.DataPack, "format 61"));

        // 资源包：ModMetadataReader 直接存数字串。
        Assert.Equal(15, PackFormatTable.ReadPackFormat(ContentKind.ResourcePack, "15"));

        Assert.Null(PackFormatTable.ReadPackFormat(ContentKind.DataPack, string.Empty));
        Assert.Null(PackFormatTable.ReadPackFormat(ContentKind.DataPack, "format abc"));
        Assert.Null(PackFormatTable.ReadPackFormat(ContentKind.Mod, "1.20.1"));
    }

    [Fact]
    public void 范围文案_覆盖整行与单点()
    {
        Assert.Equal("1.20.x", PackFormatTable.DescribeFormatRange(ContentKind.DataPack, 15));
        Assert.Equal("1.21.x", PackFormatTable.DescribeFormatRange(ContentKind.DataPack, 48));
        Assert.Equal("1.21.4", PackFormatTable.DescribeFormatRange(ContentKind.DataPack, 61));
        Assert.Equal("1.21.7–1.21.8", PackFormatTable.DescribeFormatRange(ContentKind.DataPack, 81));
        Assert.Equal("1.21.4", PackFormatTable.DescribeFormatRange(ContentKind.ResourcePack, 46));
        Assert.Null(PackFormatTable.DescribeFormatRange(ContentKind.DataPack, 999));
    }

    // ==================== 1.21.9+：min_format / max_format ====================

    [Theory]
    [InlineData(ContentKind.ResourcePack, "1.21.9", 69.0)]
    [InlineData(ContentKind.ResourcePack, "1.21.11", 75.0)]
    [InlineData(ContentKind.ResourcePack, "26.2", 88.0)]
    [InlineData(ContentKind.DataPack, "1.21.9", 88.0)]
    [InlineData(ContentKind.DataPack, "1.21.10", 88.0)]
    [InlineData(ContentKind.DataPack, "1.21.11", 94.1)]
    [InlineData(ContentKind.DataPack, "26.1", 101.1)]
    [InlineData(ContentKind.DataPack, "26.3", 121.0)]
    public void 新版游戏版本映射到小数format(ContentKind kind, string gameVersion, double expected)
    {
        Assert.True(PackFormatTable.TryGetExpected(kind, gameVersion, out var format), gameVersion);
        Assert.Equal(expected, format, precision: 3);
    }

    [Fact]
    public void 区间包_当前游戏落在区间内为兼容()
    {
        // 1.21.9 需要 88.0；包声明 [88, 94.1]，覆盖 1.21.9–1.21.11。
        var range = new PackFormatRange(88, 94.1);

        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, range, legacyFormat: null, "1.21.9");

        Assert.Equal(PackFormatVerdict.Compatible, assessment.Verdict);
        Assert.Equal(88.0, assessment.ExpectedPackFormat);
        Assert.Equal("1.21.9–1.21.11", assessment.RequiredRange);
        Assert.Contains("覆盖", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 区间包_游戏低于min为过旧()
    {
        // 包要求 1.21.11+（min 94.1），当前 1.21.9 只有 88.0。
        var range = new PackFormatRange(94.1, null);

        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, range, legacyFormat: null, "1.21.9");

        Assert.Equal(PackFormatVerdict.TooOld, assessment.Verdict);
        Assert.True(assessment.IsMismatch);
        Assert.Equal("1.21.11+", assessment.RequiredRange);
        Assert.Contains("1.21.11+", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 区间包_游戏高于max为过新()
    {
        // 只声明 max_format 81（1.21.7–1.21.8），当前 1.21.9 需要 88.0。
        var range = new PackFormatRange(null, 81);

        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, range, legacyFormat: null, "1.21.9");

        Assert.Equal(PackFormatVerdict.TooNew, assessment.Verdict);
        Assert.Equal("≤1.21.8", assessment.RequiredRange);
        Assert.Contains("≤1.21.8", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 区间包_只有min时上界开放()
    {
        // 只有 min_format 88：1.21.11 需要 94.1，仍在上界开放的区间里。
        var range = new PackFormatRange(88, null);

        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, range, legacyFormat: null, "1.21.11");

        Assert.Equal(PackFormatVerdict.Compatible, assessment.Verdict);
        Assert.Equal("1.21.9+", assessment.RequiredRange);
    }

    [Fact]
    public void 区间包_闭区间两端都算兼容()
    {
        var range = new PackFormatRange(88, 88);

        Assert.Equal(
            PackFormatVerdict.Compatible,
            PackFormatTable.Evaluate(ContentKind.DataPack, range, null, "1.21.9").Verdict);

        var above = PackFormatTable.Evaluate(ContentKind.DataPack, range, null, "1.21.11");

        Assert.Equal(PackFormatVerdict.TooNew, above.Verdict);
        Assert.Equal("1.21.9–1.21.10", above.RequiredRange);
    }

    [Fact]
    public void 区间包_边界值不在表里时如实写format数字()
    {
        var range = new PackFormatRange(88.5, 100);

        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, range, null, "1.21.9");

        Assert.Equal(PackFormatVerdict.TooOld, assessment.Verdict);
        Assert.Contains("format 88.5", assessment.RequiredRange, StringComparison.Ordinal);
        Assert.Contains("format 100", assessment.RequiredRange, StringComparison.Ordinal);
    }

    [Fact]
    public void 区间包_两端都没有时视作没有声明()
    {
        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, new PackFormatRange(null, null), null, "1.21.9");

        Assert.Equal(PackFormatVerdict.Unknown, assessment.Verdict);
        Assert.Contains("pack.mcmeta", assessment.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧式单个format_行为不变()
    {
        // 1.21.8 及更早：format 15 用在 1.20.1 上兼容。
        Assert.Equal(
            PackFormatVerdict.Compatible,
            PackFormatTable.Evaluate(ContentKind.DataPack, 15, "1.20.1").Verdict);

        // 同一份旧包用在 1.21.9 上：游戏要 88.0，判过旧。
        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, 15, "1.21.9");

        Assert.Equal(PackFormatVerdict.TooOld, assessment.Verdict);
        Assert.Equal("1.20.x", assessment.RequiredRange);
    }

    [Fact]
    public void 区间包_游戏版本不在表里时为Unknown()
    {
        var range = new PackFormatRange(88, 94.1);

        var assessment = PackFormatTable.Evaluate(ContentKind.DataPack, range, null, "1.21.12");

        Assert.Equal(PackFormatVerdict.Unknown, assessment.Verdict);
        Assert.False(assessment.IsMismatch);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
    }

    [Fact]
    public void 声明文案_区间与单值()
    {
        Assert.Equal("format 61", PackFormatTable.DescribeDeclaration(61, null));
        Assert.Equal("format 88–94.1", PackFormatTable.DescribeDeclaration(null, new PackFormatRange(88, 94.1)));
        Assert.Equal("format 88+", PackFormatTable.DescribeDeclaration(null, new PackFormatRange(88, null)));
        Assert.Equal("format ≤81", PackFormatTable.DescribeDeclaration(null, new PackFormatRange(null, 81)));
        Assert.Equal(string.Empty, PackFormatTable.DescribeDeclaration(null, null));
    }

    [Fact]
    public void 区间文案不会再被当成单个整数format()
    {
        // 旧实现按尾部数字解析，会把「format 88–94.1」读成 1；现在必须是 null。
        Assert.Null(PackFormatTable.ReadPackFormat(ContentKind.DataPack, "format 88–94.1"));
        Assert.Null(PackFormatTable.ReadPackFormat(ContentKind.DataPack, "format 88+"));
    }

    // ==================== 真实 pack.mcmeta 读取 ====================

    [Fact]
    public async Task 扫描数据包目录时读出min与max区间()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var pack = Path.Combine(game, "datapacks", "range-pack");
        Directory.CreateDirectory(pack);

        // 真实 pack.mcmeta：1.21.9 推荐写法，min/max 都带小数。
        await File.WriteAllTextAsync(
            Path.Combine(pack, "pack.mcmeta"),
            """
            { "pack": { "description": "区间数据包", "min_format": 88.0, "max_format": 94.1 } }
            """);

        var service = new ContentService(new FakeLauncherPaths(temp.Path));
        var items = await service.ScanAsync(game, ContentKind.DataPack);

        var item = Assert.Single(items);
        Assert.Equal(88.0, item.MinPackFormat);
        Assert.Equal(94.1, item.MaxPackFormat);
        Assert.Equal("format 88–94.1", item.Version);
        Assert.Equal("区间数据包", item.Description);
    }

    [Fact]
    public async Task 扫描数据包目录时只有min或只有max也能读()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");

        var onlyMin = Path.Combine(game, "datapacks", "only-min");
        Directory.CreateDirectory(onlyMin);
        await File.WriteAllTextAsync(
            Path.Combine(onlyMin, "pack.mcmeta"),
            """{ "pack": { "min_format": 88 } }""");

        var onlyMax = Path.Combine(game, "datapacks", "only-max");
        Directory.CreateDirectory(onlyMax);
        await File.WriteAllTextAsync(
            Path.Combine(onlyMax, "pack.mcmeta"),
            """{ "pack": { "max_format": 81 } }""");

        var service = new ContentService(new FakeLauncherPaths(temp.Path));
        var items = await service.ScanAsync(game, ContentKind.DataPack);

        var minItem = items.Single(item => item.DisplayName == "only-min");
        Assert.Equal(88.0, minItem.MinPackFormat);
        Assert.Null(minItem.MaxPackFormat);
        Assert.Equal("format 88+", minItem.Version);

        var maxItem = items.Single(item => item.DisplayName == "only-max");
        Assert.Null(maxItem.MinPackFormat);
        Assert.Equal(81.0, maxItem.MaxPackFormat);
        Assert.Equal("format ≤81", maxItem.Version);
    }

    [Fact]
    public async Task 扫描资源包zip时读出min与max区间()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        WritePackZip(
            Path.Combine(game, "resourcepacks", "range-rp.zip"),
            """{ "pack": { "description": "区间资源包", "min_format": 69.0, "max_format": 75.0 } }""");

        var service = new ContentService(new FakeLauncherPaths(temp.Path));
        var items = await service.ScanAsync(game, ContentKind.ResourcePack);

        var item = Assert.Single(items);
        Assert.Equal(69.0, item.MinPackFormat);
        Assert.Equal(75.0, item.MaxPackFormat);
        Assert.Equal("format 69–75", item.Version);
    }

    private static void WritePackZip(string path, string packMetaJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("pack.mcmeta");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(packMetaJson);
    }
}
