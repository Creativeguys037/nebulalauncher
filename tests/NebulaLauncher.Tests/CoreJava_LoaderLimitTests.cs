using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 加载器版本级的 Java 上限（PCL2 <c>ModJava.vb:GetJavaRequirement</c> 的专属上限段）。
///
/// 表里每条规则至少一正一反：命中区间要给出预期上限，越过区间要回落到「没有专属上限」，
/// 否则把区间边界写错（例如 37.0.79 写成 37.0.9）用例也不会红。
///
/// 另一组用例守的是「建议」与「阻断」的分界——<c>8u320</c> 这类补丁号上限一旦被当成硬边界，
/// 用户在 <c>8u322</c> 上就再也起不来，而那个组合实际是能跑的。
/// </summary>
public class CoreJava_LoaderLimitTests
{
    // ==================== Forge 1.16.3–1.16.5 ====================

    /// <summary>build 34.0.0–36.2.25 落在「最高 8u320」这一段，且只能是建议级。</summary>
    [Theory]
    [InlineData("1.16.3", "34.0.0")]
    [InlineData("1.16.4", "35.1.4")]
    [InlineData("1.16.5", "36.1.0")]
    [InlineData("1.16.5", "36.2.25")]
    public void Forge_1_16_旧build只给建议上限(string minecraftVersion, string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Forge, build);

        Assert.NotNull(limit);
        Assert.False(limit!.IsBlocking);
        Assert.Equal(8, limit.MaxMajor);
        Assert.Equal("8u320", limit.MaxPatch);
        Assert.Equal("FORGE_1_16_BUILD_8U320", limit.Id);
    }

    /// <summary>36.2.26 起换成「&lt; J24」这一段，且是阻断级。</summary>
    [Theory]
    [InlineData("1.16.5", "36.2.26")]
    [InlineData("1.16.5", "36.2.30")]
    public void Forge_1_16_新build阻断在Java24(string minecraftVersion, string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Forge, build);

        Assert.NotNull(limit);
        Assert.True(limit!.IsBlocking);
        Assert.Equal(23, limit.MaxMajor);
        Assert.Null(limit.MaxPatch);
        Assert.Equal("FORGE_1_16_BUILD_J24", limit.Id);
    }

    /// <summary>1.16 的两个区间之外的游戏版本不吃这张表。</summary>
    [Theory]
    [InlineData("1.16.2", "36.2.25")]
    [InlineData("1.12.2", "14.23.5.2859")]
    [InlineData("1.16.5", "37.0.0")]
    [InlineData("1.16.5", "40.1.0")]
    [InlineData("1.16.3", "99.0.0")]
    public void Forge_1_16区间外的游戏版本没有专属上限(string minecraftVersion, string build) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Forge, build));

    // ==================== Forge 1.17.1 ====================

    [Theory]
    [InlineData("37.0.0")]
    [InlineData("37.0.30")]
    [InlineData("37.0.79")]
    public void Forge_1_17_区间内阻断在Java17(string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.17.1", LoaderKind.Forge, build);

        Assert.NotNull(limit);
        Assert.True(limit!.IsBlocking);
        Assert.Equal(16, limit.MaxMajor);
        Assert.Equal("FORGE_1_17_BUILD_J17", limit.Id);
    }

    /// <summary>37.0.80 已经不在 PCL2 那段区间里，没有专属上限——边界差一位必须能测出来。</summary>
    [Theory]
    [InlineData("37.0.80")]
    [InlineData("37.1.0")]
    [InlineData("40.2.0")]
    public void Forge_1_17_区间外没有专属上限(string build) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit("1.17.1", LoaderKind.Forge, build));

    // ==================== Forge 1.19.4 ====================

    [Theory]
    [InlineData("45.0.21")]
    [InlineData("45.0.40")]
    [InlineData("45.0.65")]
    public void Forge_1_19_4_早build阻断在Java20(string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.19.4", LoaderKind.Forge, build);

        Assert.NotNull(limit);
        Assert.True(limit!.IsBlocking);
        Assert.Equal(19, limit.MaxMajor);
        Assert.Equal("FORGE_1_19_4_BUILD_J20", limit.Id);
    }

    [Theory]
    [InlineData("45.0.66")]
    [InlineData("46.0.14")]
    [InlineData("47.4.8")]
    public void Forge_1_19_4_晚build阻断在Java22(string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.19.4", LoaderKind.Forge, build);

        Assert.NotNull(limit);
        Assert.True(limit!.IsBlocking);
        Assert.Equal(21, limit.MaxMajor);
        Assert.Equal("FORGE_1_19_4_BUILD_J22", limit.Id);
    }

    [Theory]
    [InlineData("45.0.20")]
    [InlineData("47.4.9")]
    [InlineData("48.0.0")]
    public void Forge_1_19_4_区间外没有专属上限(string build) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit("1.19.4", LoaderKind.Forge, build));

    // ==================== NeoForge ====================

    /// <summary>1.20.1 的 NeoForge 沿用 Forge 编号（47.1.x），只能按游戏版本认。</summary>
    [Fact]
    public void NeoForge_1_20_1阻断在Java22()
    {
        // build 号为 null 也要命中：这条规则只看游戏版本。
        foreach (var build in new[] { "47.1.106", "47.1.0", null })
        {
            var limit = JavaRequirementTable.GetLoaderLimit("1.20.1", LoaderKind.NeoForge, build);

            Assert.NotNull(limit);
            Assert.True(limit!.IsBlocking);
            Assert.Equal(21, limit.MaxMajor);
            Assert.Equal("NEOFORGE_1_20_1_J22", limit.Id);
        }
    }

    [Theory]
    [InlineData("1.20.2", "20.2.62-beta")]
    [InlineData("1.20.2", "20.2.62")]
    [InlineData("1.20.3", "20.2.62-beta")]
    public void NeoForge_早期build阻断在Java22(string minecraftVersion, string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.NeoForge, build);

        Assert.NotNull(limit);
        Assert.True(limit!.IsBlocking);
        Assert.Equal(21, limit.MaxMajor);
        Assert.Equal("NEOFORGE_EARLY_J22", limit.Id);
    }

    /// <summary>20.4.x 已经越过「≤ 20.2.62-beta」的边界，不再有专属上限。</summary>
    [Theory]
    [InlineData("1.20.4", "20.4.10-beta")]
    [InlineData("1.20.4", "20.4.0")]
    public void NeoForge_20_4起没有专属上限(string minecraftVersion, string build) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.NeoForge, build));

    [Theory]
    [InlineData("1.21.1", "21.1.77")]
    [InlineData("1.20.6", "20.6.119")]
    public void NeoForge_新build没有专属上限(string minecraftVersion, string build) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.NeoForge, build));

    // ==================== Fabric ====================

    [Theory]
    [InlineData("1.21.4", "0.16.9")]
    [InlineData("1.20.1", "0.15.11")]
    [InlineData("1.16.5", "0.14.24")]
    public void Fabric_旧loader阻断在Java25(string minecraftVersion, string build)
    {
        var limit = JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Fabric, build);

        Assert.NotNull(limit);
        Assert.True(limit!.IsBlocking);
        Assert.Equal(24, limit.MaxMajor);
        Assert.Equal("FABRIC_LOADER_J25", limit.Id);
    }

    /// <summary>0.17.0 本身不在「&lt; 0.17.0」里——开区间边界必须能测出来。</summary>
    [Theory]
    [InlineData("1.21.4", "0.17.0")]
    [InlineData("1.21.4", "0.17.1")]
    [InlineData("1.21.8", "0.18.4")]
    public void Fabric_新loader没有专属上限(string minecraftVersion, string build) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Fabric, build));

    // ==================== 脏数据：不该崩，也不该瞎猜 ====================

    [Theory]
    [InlineData("1.16.5", LoaderKind.Forge, null)]
    [InlineData("1.16.5", LoaderKind.Forge, "")]
    [InlineData("1.16.5", LoaderKind.Forge, "   ")]
    [InlineData("1.16.5", LoaderKind.Forge, "not-a-version")]
    [InlineData("1.16.5", LoaderKind.Forge, "abc.def")]
    public void 加载器版本缺失或不可解析时返回null(string minecraftVersion, LoaderKind loader, string? loaderVersion) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, loader, loaderVersion));

    [Theory]
    [InlineData("23w31a", "36.2.25")]
    [InlineData("b1.7.3", "36.2.25")]
    [InlineData("", "36.2.25")]
    [InlineData((string?)null, "36.2.25")]
    public void 游戏版本不可解析时返回null(string? minecraftVersion, string loaderVersion) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Forge, loaderVersion));

    [Theory]
    [InlineData("1.16.5", "36.2.25")]
    [InlineData("1.21.4", "0.16.9")]
    public void 原版不吃加载器上限(string minecraftVersion, string loaderVersion) =>
        Assert.Null(JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Vanilla, loaderVersion));

    /// <summary>
    /// 加载器版本号被连带写进原版号（<c>1.20.1-47.1.106</c>）时必须剥掉前缀再比较，
    /// 否则第一段是 1，永远落在所有 Forge 区间之外——这个组合会静默丢掉上限。
    /// </summary>
    [Theory]
    [InlineData("1.19.4", "1.19.4-45.0.40", "FORGE_1_19_4_BUILD_J20")]
    [InlineData("1.17.1", "1.17.1-37.0.30", "FORGE_1_17_BUILD_J17")]
    [InlineData("1.20.1", "1.20.1-neoforge-47.1.106", "NEOFORGE_1_20_1_J22")]
    public void 带原版前缀的加载器版本照样能解析(
        string minecraftVersion,
        string loaderVersion,
        string expectedId)
    {
        var limit = JavaRequirementTable.GetLoaderLimit(minecraftVersion, LoaderKind.Forge, loaderVersion);

        // 第三条是 NeoForge 的写法，用 Forge 查表不命中，单独断言解析结果。
        if (minecraftVersion == "1.20.1")
        {
            Assert.True(JavaRequirementTable.TryParseLoaderVersion(loaderVersion, minecraftVersion, out var parts));
            Assert.Equal(47, parts[0]);
            return;
        }

        Assert.NotNull(limit);
        Assert.Equal(expectedId, limit!.Id);
    }

    // ==================== 建议 vs 阻断：8u322 不能被误杀 ====================

    /// <summary>
    /// <c>8u320</c> 是建议上限，不是硬边界。<c>8u322</c> 会命中「超出建议」，
    /// 但那条规则 <see cref="JavaLoadLimit.IsBlocking"/> 是 false，
    /// 所以它既不会进 <c>AllowedRange</c> 的上界，也不会让 <c>Evaluate</c> 返回 mandatory。
    /// </summary>
    [Fact]
    public void 补丁号超限只是建议不会阻断()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.25");

        Assert.NotNull(limit);
        Assert.False(limit!.IsBlocking);

        // 8u322 超出建议的 8u320。
        Assert.True(limit.Exceeds(8, 322));

        // 但上限是建议级 → 区间不受影响：仍然是 MODLAUNCHER_8 给的 17。
        var range = JavaRequirementTable.AllowedRange("1.16.5", LoaderKind.Forge, jsonJavaMajor: null, "36.2.25");
        Assert.True(range.HasValue);
        Assert.Equal(17, range!.Value.Max);

        // Evaluate 也不能因此产出 mandatory：Java 8 本身完全合法。
        var (mandatory, _) = JavaRequirementTable.Evaluate("1.16.5", LoaderKind.Forge, null, 8, "36.2.25");
        Assert.Null(mandatory);
    }

    /// <summary>知道主版本但不知道补丁号时不判「超出」——宁可漏一次提示，也不拿未知当违规。</summary>
    [Fact]
    public void 不知道补丁号时不判超出()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.25");

        Assert.NotNull(limit);
        Assert.False(limit!.Exceeds(8));
        Assert.False(limit.Exceeds(8, null));

        // 主版本越界是确定的，与补丁号无关。
        Assert.True(limit.Exceeds(17));
    }

    [Fact]
    public void 补丁号上限能被拆成主版本与更新号()
    {
        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "34.0.0");

        Assert.NotNull(limit);
        Assert.Equal(8, limit!.MaxPatchMajor);
        Assert.Equal(320, limit.MaxPatchUpdate);
    }

    // ==================== 并入 AllowedRange / Evaluate ====================

    /// <summary>阻断级上限把区间上界压下来；不传加载器版本时行为与原来一致。</summary>
    [Fact]
    public void 区间重载会收紧阻断级上界()
    {
        var withoutLoaderVersion = JavaRequirementTable.AllowedRange("1.17.1", LoaderKind.Forge);
        Assert.True(withoutLoaderVersion.HasValue);
        Assert.Equal(17, withoutLoaderVersion!.Value.Max);

        var withLoaderVersion = JavaRequirementTable.AllowedRange("1.17.1", LoaderKind.Forge, null, "37.0.79");
        Assert.True(withLoaderVersion.HasValue);
        Assert.Equal(16, withLoaderVersion!.Value.Max);
        Assert.Equal(withoutLoaderVersion.Value.Min, withLoaderVersion.Value.Min);
    }

    [Theory]
    [InlineData("1.19.4", "45.0.40", "FORGE_1_19_4_BUILD_J20")]
    [InlineData("1.19.4", "47.0.1", "FORGE_1_19_4_BUILD_J22")]
    [InlineData("1.20", "46.0.14", "FORGE_1_19_4_BUILD_J22")]
    [InlineData("1.20.1", "47.4.8", "FORGE_1_19_4_BUILD_J22")]
    [InlineData("1.21.4", "0.16.9", "FABRIC_LOADER_J25")]
    public void 超上限的Java被判定为mandatory违规(
        string minecraftVersion,
        string loaderVersion,
        string expectedId)
    {
        var loader = minecraftVersion == "1.21.4" ? LoaderKind.Fabric : LoaderKind.Forge;
        var range = JavaRequirementTable.AllowedRange(minecraftVersion, loader, null, loaderVersion);

        Assert.True(range.HasValue);

        var tooNew = range!.Value.Max + 1;
        var (mandatory, _) = JavaRequirementTable.Evaluate(minecraftVersion, loader, null, tooNew, loaderVersion);

        Assert.NotNull(mandatory);
        Assert.Equal(expectedId, mandatory.Value.Id);
        Assert.True(mandatory.Value.Mandatory);
    }

    /// <summary>
    /// Forge 36.2.26 修复了旧 ModLauncher 上限；具体构建规则替代通用上限。
    /// </summary>
    [Fact]
    public void Forge_1_16新build使用修复后的Java上限()
    {
        var range = JavaRequirementTable.AllowedRange("1.16.5", LoaderKind.Forge, null, "36.2.30");

        Assert.True(range.HasValue);
        Assert.Equal(23, range!.Value.Max);
        Assert.Equal(8, range.Value.Min);

        var limit = JavaRequirementTable.GetLoaderLimit("1.16.5", LoaderKind.Forge, "36.2.30");
        Assert.NotNull(limit);
        Assert.Equal(23, limit!.MaxMajor);

        foreach (var javaMajor in new[] { 18, 21, 23 })
        {
            Assert.Null(JavaRequirementTable.Evaluate("1.16.5", LoaderKind.Forge, null, javaMajor, "36.2.30").Mandatory);
        }

        var (mandatory, _) = JavaRequirementTable.Evaluate("1.16.5", LoaderKind.Forge, null, 24, "36.2.30");
        Assert.NotNull(mandatory);
        Assert.Equal("FORGE_1_16_BUILD_J24", mandatory.Value.Id);

        Assert.Equal(17, JavaRequirementTable.AllowedRange("1.16.5", LoaderKind.Forge, null, "36.2.25")!.Value.Max);
        Assert.Equal(17, JavaRequirementTable.AllowedRange("1.16.5", LoaderKind.Forge, null, null)!.Value.Max);
        Assert.Equal("VANILLA", JavaRequirementTable.Evaluate("1.16.5", LoaderKind.Forge, null, 7, "36.2.30").Mandatory!.Value.Id);
    }

    [Fact]
    public void 上限之内的Java不会误判()
    {
        // 45.0.40 这一段上限是 Java 19：17/18/19 都必须放行。
        // 不列 8 与 11——它们是被下界规则（VANILLA 要求 1.19.4 ≥17）挡下的，与本表无关。
        foreach (var javaMajor in new[] { 17, 18, 19 })
        {
            var (mandatory, _) = JavaRequirementTable.Evaluate("1.19.4", LoaderKind.Forge, null, javaMajor, "45.0.40");

            Assert.Null(mandatory);
        }

        // 20 必须被拦。
        var (blocked, _) = JavaRequirementTable.Evaluate("1.19.4", LoaderKind.Forge, null, 20, "45.0.40");
        Assert.NotNull(blocked);
    }

    /// <summary>加载器版本缺失时两个重载都退回旧行为，不能因为「不知道」就收紧。</summary>
    [Fact]
    public void 加载器版本缺失时行为与旧签名一致()
    {
        var legacy = JavaRequirementTable.AllowedRange("1.19.4", LoaderKind.Forge);
        var withNull = JavaRequirementTable.AllowedRange("1.19.4", LoaderKind.Forge, null, null);

        Assert.Equal(legacy, withNull);

        var legacyEvaluation = JavaRequirementTable.Evaluate("1.19.4", LoaderKind.Forge, null, 21);
        var nullEvaluation = JavaRequirementTable.Evaluate("1.19.4", LoaderKind.Forge, null, 21, null);

        Assert.Equal(legacyEvaluation, nullEvaluation);
    }

    // ==================== 解析器本身 ====================

    // CA1861 在属性实参上是误报：InlineData 的数组实参必须是编译期常量或数组创建表达式，
    // 换成 static readonly 字段会得到 CS0182（属性实参必须是常量）。分析器 9.0 才修这个误报。
#pragma warning disable CA1861
    [Theory]
    [InlineData("36.2.25", new[] { 36, 2, 25 })]
    [InlineData("14.23.5.2859", new[] { 14, 23, 5, 2859 })]
    [InlineData("0.16.9", new[] { 0, 16, 9 })]
    [InlineData("20.2.62-beta", new[] { 20, 2, 62 })]
    [InlineData("21.1.77", new[] { 21, 1, 77 })]
    public void 加载器版本号被拆成数字段(string loaderVersion, int[] expected)
    {
        Assert.True(JavaRequirementTable.TryParseLoaderVersion(loaderVersion, null, out var parts));
        Assert.Equal(expected, parts);
    }
#pragma warning restore CA1861

    /// <summary>
    /// 无法解析的加载器版本号一律返回 false，不抛异常。
    /// 三种取值各写一条断言而不是 Theory：自制的 TestRunner 对「单参数且值为 null」的
    /// Theory 会按「参数数组本身是 null」调用，直接抛 TargetParameterCountException。
    /// </summary>
    [Fact]
    public void 空加载器版本号返回false() =>
        Assert.False(JavaRequirementTable.TryParseLoaderVersion(null, null, out _));

    [Fact]
    public void 空白加载器版本号返回false() =>
        Assert.False(JavaRequirementTable.TryParseLoaderVersion("   ", null, out _));

    [Fact]
    public void 全是字母的加载器版本号返回false() =>
        Assert.False(JavaRequirementTable.TryParseLoaderVersion("abc", null, out _));
}
