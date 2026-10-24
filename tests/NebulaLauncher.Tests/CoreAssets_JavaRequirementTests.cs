using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

public class CoreAssets_JavaRequirementTests
{
    [Theory]
    [InlineData("1.7.10", 8)]
    [InlineData("1.8.9", 8)]
    [InlineData("1.12.2", 8)]
    [InlineData("1.16", 8)]
    [InlineData("1.16.5", 8)]
    // Mojang 的 1.17 版本 JSON 声明 javaVersion=16（java-runtime-alpha）。
    // 原用例按「1.17 及以后一律 17」写死，会让 1.17 实例被推荐 Java 17，与官方清单不一致。
    [InlineData("1.17", 16)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.18", 17)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.20", 17)]
    [InlineData("1.20.1", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.6", 21)]
    [InlineData("1.21", 21)]
    [InlineData("1.21.4", 21)]
    [InlineData("1.21.11", 21)]
    // 2026 年起 Mojang 改用年份版本号，26.x 需要 Java 25（java-runtime-epsilon）。
    [InlineData("26.1", 25)]
    [InlineData("26.3", 25)]
    public void 需求表覆盖主线版本(string minecraftVersion, int expectedMajor)
    {
        var requirement = JavaRequirementTable.Get(minecraftVersion);

        Assert.NotNull(requirement);
        Assert.Equal(expectedMajor, requirement!.RequiredMajor);
        Assert.False(string.IsNullOrWhiteSpace(requirement.Reason));
    }

    [Theory]
    [InlineData("26.3", 26)]
    [InlineData("26.1", 26)]
    public void 年份版本号解析(string version, int expectedYear)
    {
        Assert.True(JavaRequirementTable.TryParseYearRelease(version, out var year));
        Assert.Equal(expectedYear, year);

        // 年份版本号不能被 1.x 的正则误判。
        Assert.False(JavaRequirementTable.TryParseRelease(version, out _, out _));
    }

    [Fact]
    public void 年份版本号之外不误判()
    {
        Assert.False(JavaRequirementTable.TryParseYearRelease("1.20.1", out _));
        Assert.False(JavaRequirementTable.TryParseYearRelease("25w31a", out _));
        Assert.False(JavaRequirementTable.TryParseYearRelease((string?)null, out _));
    }

    [Fact]
    public void 优先读取本地版本JSON的javaVersion()
    {
        using var temp = new TempDirectory();
        var versions = Path.Combine(temp.Path, "versions");
        var directory = Path.Combine(versions, "26.3");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "26.3.json"),
            """{"id":"26.3","javaVersion":{"component":"java-runtime-epsilon","majorVersion":25}}""");

        var requirement = JavaRequirementTable.ReadFromDisk(versions, "26.3");

        Assert.NotNull(requirement);
        Assert.Equal(25, requirement!.RequiredMajor);
        Assert.Contains("java-runtime-epsilon", requirement.Reason);
    }

    [Fact]
    public void 加载器版本沿inheritsFrom查找原版javaVersion()
    {
        using var temp = new TempDirectory();
        var versions = Path.Combine(temp.Path, "versions");

        var vanilla = Path.Combine(versions, "26.3");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(
            Path.Combine(vanilla, "26.3.json"),
            """{"id":"26.3","javaVersion":{"component":"java-runtime-epsilon","majorVersion":25}}""");

        var loader = Path.Combine(versions, "fabric-loader-0.19.5-26.3");
        Directory.CreateDirectory(loader);
        File.WriteAllText(
            Path.Combine(loader, "fabric-loader-0.19.5-26.3.json"),
            """{"id":"fabric-loader-0.19.5-26.3","inheritsFrom":"26.3"}""");

        var requirement = JavaRequirementTable.ReadFromDisk(versions, "fabric-loader-0.19.5-26.3");

        Assert.NotNull(requirement);
        Assert.Equal(25, requirement!.RequiredMajor);
    }

    [Fact]
    public void 读不到版本JSON时返回null()
    {
        using var temp = new TempDirectory();

        Assert.Null(JavaRequirementTable.ReadFromDisk(temp.Path, "26.3"));
        Assert.Null(JavaRequirementTable.ReadFromDisk(temp.Path, null));
    }

    [Theory]
    [InlineData("23w31a")]
    [InlineData("b1.7.3")]
    [InlineData("")]
    [InlineData((string?)null)]   // 必须显式转型，否则 C# 会把 null 解析成 params 空数组，变成零参数用例
    [InlineData("abc")]
    public void 未知版本返回null(string? minecraftVersion)
    {
        Assert.Null(JavaRequirementTable.Get(minecraftVersion));
    }

    [Theory]
    [InlineData("1.20.5-pre1", 20, 5)]
    [InlineData("1.20.4", 20, 4)]
    [InlineData("1.20", 20, 0)]
    [InlineData("1.7.10", 7, 10)]
    public void 版本号解析(string version, int expectedMinor, int expectedPatch)
    {
        Assert.True(JavaRequirementTable.TryParseRelease(version, out var minor, out var patch));
        Assert.Equal(expectedMinor, minor);
        Assert.Equal(expectedPatch, patch);
    }

    [Theory]
    [InlineData("1.7.10")]
    [InlineData("1.12.2")]
    [InlineData("1.17")]
    [InlineData("1.20.1")]
    [InlineData("1.20.5")]
    [InlineData("26.1")]
    public void 约束集下界与需求表一致(string minecraftVersion)
    {
        var requirement = JavaRequirementTable.Get(minecraftVersion);
        var range = JavaRequirementTable.AllowedRange(minecraftVersion, LoaderKind.Vanilla);

        Assert.NotNull(requirement);
        Assert.True(range.HasValue);

        // Get 只给下界（UI 文案仍在用），AllowedRange 在同一个下界上再叠加载器相关的上界，
        // 两者下界必须完全一致，否则「提示 Java 17」和「判定需要 21」会互相矛盾。
        Assert.Equal(requirement!.RequiredMajor, range!.Value.Min);
        Assert.True(range.Value.Max >= requirement.RequiredMajor);
    }

    [Theory]
    [InlineData("1.8.0_392", 8)]
    [InlineData("1.8", 8)]
    [InlineData("11.0.21", 11)]
    [InlineData("17.0.8+8", 17)]
    [InlineData("17.0.8+8-LTS", 17)]
    [InlineData("21", 21)]
    [InlineData("21.0.1", 21)]
    [InlineData("", 0)]
    public void Java主版本解析(string version, int expected)
    {
        Assert.Equal(expected, JavaService.ParseMajor(version));
    }
}
