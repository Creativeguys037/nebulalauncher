using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>实例选择规则：版本页与首页共用同一套匹配顺序，这里锁住行为。</summary>
public class CoreLaunch_InstanceLocatorTests
{
    private static readonly string[] ExistingVersionAndSuffix = { "1.21.4", "1.21.4 (2)" };

    private static readonly string[] LowercaseFabric = { "fabric" };

    private static readonly string[] OtherVersion = { "1.20.1" };

    private static InstanceInfo Instance(string id, string name, string version) => new()
    {
        Id = id,
        Name = name,
        VersionName = version,
    };

    [Fact]
    public void FindForVersion_WithoutInstances_ReturnsNull()
    {
        Assert.Null(InstanceLocator.FindForVersion(null, "1.20.1"));
        Assert.Null(InstanceLocator.FindForVersion(Array.Empty<InstanceInfo>(), "1.20.1"));
    }

    [Fact]
    public void FindForVersion_MatchesByVersionName()
    {
        var instances = new[]
        {
            Instance("a", "生存", "1.21.4"),
            Instance("b", "模组", "1.20.1"),
        };

        var found = InstanceLocator.FindForVersion(instances, "1.20.1");

        Assert.NotNull(found);
        Assert.Equal("b", found!.Id);
    }

    [Fact]
    public void FindForVersion_UnknownVersion_ReturnsNull()
    {
        var instances = new[] { Instance("a", "生存", "1.21.4") };

        Assert.Null(InstanceLocator.FindForVersion(instances, "1.19.2"));
        Assert.Null(InstanceLocator.FindForVersion(instances, null));
        Assert.Null(InstanceLocator.FindForVersion(instances, "  "));
    }

    [Fact]
    public void FindForVersion_PrefersExplicitInstanceOfSameVersion()
    {
        var instances = new[]
        {
            Instance("a", "生存", "1.21.4"),
            Instance("b", "创造", "1.21.4"),
        };

        var found = InstanceLocator.FindForVersion(instances, "1.21.4", "b");

        Assert.NotNull(found);
        Assert.Equal("b", found!.Id);
    }

    [Fact]
    public void FindForVersion_IgnoresPreferredInstanceOfOtherVersion()
    {
        // 选中实例属于 1.20.1，却要启动 1.21.4：必须换用 1.21.4 自己的实例，
        // 否则会把 1.20.1 的 mods / saves 写进 1.21.4 的运行过程。
        var instances = new[]
        {
            Instance("a", "旧版生存", "1.20.1"),
            Instance("b", "新版", "1.21.4"),
        };

        var found = InstanceLocator.FindForVersion(instances, "1.21.4", "a");

        Assert.NotNull(found);
        Assert.Equal("b", found!.Id);
    }

    [Fact]
    public void FindForVersion_PreferredOtherVersionAndNoMatch_ReturnsNull()
    {
        var instances = new[] { Instance("a", "旧版生存", "1.20.1") };

        Assert.Null(InstanceLocator.FindForVersion(instances, "1.21.4", "a"));
    }

    [Fact]
    public void MakeUniqueName_KeepsFreeName()
    {
        Assert.Equal("1.21.4", InstanceLocator.MakeUniqueName("1.21.4", OtherVersion));
        Assert.Equal("1.21.4", InstanceLocator.MakeUniqueName("  1.21.4  ", Array.Empty<string>()));
        Assert.Equal("1.21.4", InstanceLocator.MakeUniqueName("1.21.4", null));
    }

    [Fact]
    public void MakeUniqueName_AppendsIncreasingSuffix()
    {
        Assert.Equal("1.21.4 (3)", InstanceLocator.MakeUniqueName("1.21.4", ExistingVersionAndSuffix));
    }

    [Fact]
    public void MakeUniqueName_IsCaseInsensitive()
    {
        Assert.Equal("Fabric (2)", InstanceLocator.MakeUniqueName("Fabric", LowercaseFabric));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData((string?)null)]
    public void MakeUniqueName_FallsBackToDefaultForBlankName(string? name)
    {
        Assert.Equal("新实例", InstanceLocator.MakeUniqueName(name, Array.Empty<string>()));
    }
}
