using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// 加载器候选合并：Forge 上游只给推荐版，已安装的旧构建必须仍能出现在换加载器面板里，
/// 否则实例升到新构建后无法回滚（PCL2 组件修改器「还原」的等价能力）。
/// </summary>
public class CoreVersions_LoaderVersionCatalogTests
{
    [Fact]
    public void MergeInstalled_AddsOlderInstalledBuild()
    {
        var remote = new[] { Loader("47.2.0", recommended: true) };
        var installed = new[] { Version("1.20.1-forge-36.2.39", LoaderKind.Forge, "1.20.1", "36.2.39") };

        var merged = LoaderVersionCatalog.MergeInstalled(remote, installed, LoaderKind.Forge, "1.20.1");

        Assert.Equal(["47.2.0", "36.2.39"], merged.Select(item => item.Version));
        Assert.True(merged[0].IsRecommended);
        Assert.False(merged[0].IsInstalled);
        Assert.True(merged[1].IsInstalled);
    }

    [Fact]
    public void MergeInstalled_MarksRemoteVersionThatIsAlreadyInstalled()
    {
        var remote = new[] { Loader("47.2.0", recommended: true) };
        var installed = new[] { Version("1.20.1-forge-47.2.0", LoaderKind.Forge, "1.20.1", "47.2.0") };

        var merged = LoaderVersionCatalog.MergeInstalled(remote, installed, LoaderKind.Forge, "1.20.1");

        var only = Assert.Single(merged);
        Assert.Equal("47.2.0", only.Version);
        Assert.True(only.IsRecommended);
        Assert.True(only.IsInstalled);
    }

    [Fact]
    public void MergeInstalled_IgnoresOtherLoadersAndGameVersions()
    {
        var remote = Array.Empty<LoaderVersion>();
        var installed = new[]
        {
            Version("1.20.1-forge-36.2.39", LoaderKind.Forge, "1.20.1", "36.2.39"),
            Version("1.20.1-fabric-0.16.9", LoaderKind.Fabric, "1.20.1", "0.16.9"),
            Version("1.19.2-forge-43.2.0", LoaderKind.Forge, "1.19.2", "43.2.0"),
            Version("1.20.1", LoaderKind.Vanilla, "1.20.1", null),
        };

        var merged = LoaderVersionCatalog.MergeInstalled(remote, installed, LoaderKind.Forge, "1.20.1");

        var only = Assert.Single(merged);
        Assert.Equal("36.2.39", only.Version);
        Assert.True(only.IsInstalled);
    }

    [Fact]
    public void MergeInstalled_OrdersInstalledVersionsDescending()
    {
        var remote = Array.Empty<LoaderVersion>();
        var installed = new[]
        {
            Version("1.20.1-forge-36.2.39", LoaderKind.Forge, "1.20.1", "36.2.39"),
            Version("1.20.1-forge-47.2.0", LoaderKind.Forge, "1.20.1", "47.2.0"),
            Version("1.20.1-forge-45.0.40", LoaderKind.Forge, "1.20.1", "45.0.40"),
        };

        var merged = LoaderVersionCatalog.MergeInstalled(remote, installed, LoaderKind.Forge, "1.20.1");

        Assert.Equal(["47.2.0", "45.0.40", "36.2.39"], merged.Select(item => item.Version));
    }

    [Fact]
    public void MergeInstalled_WithoutMinecraftVersionReturnsRemoteUntouched()
    {
        var remote = new[] { Loader("47.2.0", recommended: true) };
        var installed = new[] { Version("x", LoaderKind.Forge, "1.20.1", "36.2.39") };

        var merged = LoaderVersionCatalog.MergeInstalled(remote, installed, LoaderKind.Forge, " ");

        Assert.Same(remote, merged);
    }

    [Fact]
    public void MergeInstalled_OrdersByNumericSegmentsNotStringOrder()
    {
        var remote = Array.Empty<LoaderVersion>();
        var installed = new[]
        {
            Version("1.20.1-forge-47.9.0", LoaderKind.Forge, "1.20.1", "47.9.0"),
            Version("1.20.1-forge-47.10.0", LoaderKind.Forge, "1.20.1", "47.10.0"),
            Version("1.20.1-forge-47.2.0", LoaderKind.Forge, "1.20.1", "47.2.0"),
        };

        var merged = LoaderVersionCatalog.MergeInstalled(remote, installed, LoaderKind.Forge, "1.20.1");

        // 字典序会把 47.9.0 排在 47.10.0 前；按数值段应为 47.10.0 → 47.9.0 → 47.2.0。
        Assert.Equal(["47.10.0", "47.9.0", "47.2.0"], merged.Select(item => item.Version));
    }

    [Fact]
    public void VersionComparer_HandlesPrereleaseAndShortForms()
    {
        Assert.True(LoaderVersionComparer.Instance.Compare("47.10.0", "47.9.0") > 0);
        Assert.True(LoaderVersionComparer.Instance.Compare("20.2.100", "20.2.62") > 0);
        Assert.True(LoaderVersionComparer.Instance.Compare("47.10.0", "47.10.0-beta") > 0);
        Assert.Equal(0, LoaderVersionComparer.Instance.Compare("47.10", "47.10.0"));
        Assert.True(LoaderVersionComparer.Instance.Compare(null, "1.0") < 0);
    }

    private static LoaderVersion Loader(string version, bool recommended = false) => new()
    {
        Version = version,
        Stable = true,
        GameVersion = "1.20.1",
        IsRecommended = recommended,
    };

    private static VersionInfo Version(
        string name,
        LoaderKind loader,
        string minecraftVersion,
        string? loaderVersion) => new()
    {
        Name = name,
        Loader = loader,
        MinecraftVersion = minecraftVersion,
        LoaderVersion = loaderVersion,
    };
}
