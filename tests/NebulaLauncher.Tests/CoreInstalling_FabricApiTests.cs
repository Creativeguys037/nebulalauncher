using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「装 Fabric 时一并装 Fabric API」的版本选择逻辑。
///
/// 背景：Fabric 生态里绝大多数模组依赖 Fabric API，漏装的症状是启动时
/// <c>NoClassDefFoundError</c>——日志里那行类名对新手毫无指向性。
/// </summary>
public class CoreInstalling_FabricApiTests
{
    [Fact]
    public void 项目slug写死为fabric_api()
    {
        // 写死而不是每次搜索：slug 是项目永久别名，而按名字搜的首条可能是第三方兼容层。
        Assert.Equal("fabric-api", InstallService.FabricApiSlug);
    }

    [Fact]
    public void 界面给的版本Id命中时用那一条()
    {
        var versions = new[]
        {
            Version("newest", "0.92.3+1.20.1"),
            Version("target", "0.92.2+1.20.1"),
            Version("oldest", "0.90.0+1.20.1"),
        };

        var picked = InstallService.PickFabricApiVersion(versions, "target");

        Assert.NotNull(picked);
        Assert.Equal("target", picked!.Id);
    }

    [Fact]
    public void 版本Id大小写不敏感且忽略首尾空白()
    {
        var versions = new[] { Version("AbC123", "0.92.2+1.20.1"), Version("other", "0.90.0+1.20.1") };

        var picked = InstallService.PickFabricApiVersion(versions, "  abc123 ");

        Assert.NotNull(picked);
        Assert.Equal("AbC123", picked!.Id);
    }

    [Fact]
    public void 版本Id查不到时退回首项而不是放弃()
    {
        // 从打开面板到点「开始安装」之间 Modrinth 可能刚好撤掉那一版；
        // 此时装个相邻的兼容版本远好于「因为可选前置装不上而整个安装失败」。
        var versions = new[]
        {
            Version("newest", "0.92.3+1.20.1"),
            Version("older", "0.90.0+1.20.1"),
        };

        var picked = InstallService.PickFabricApiVersion(versions, "gone");

        Assert.NotNull(picked);
        Assert.Equal("newest", picked!.Id);
    }

    [Fact]
    public void 没有给Id时取首项()
    {
        var versions = new[] { Version("newest", "0.92.3+1.20.1"), Version("older", "0.90.0+1.20.1") };

        Assert.Equal("newest", InstallService.PickFabricApiVersion(versions, null)!.Id);
        Assert.Equal("newest", InstallService.PickFabricApiVersion(versions, "   ")!.Id);
    }

    [Fact]
    public void 候选为空时返回null()
    {
        // 空候选意味着「这个 Minecraft 版本没有兼容的 Fabric API」：
        // 返回 null，调用方跳过这一步并记日志，不阻断加载器安装。
        Assert.Null(InstallService.PickFabricApiVersion(Array.Empty<ModrinthVersion>(), "anything"));
    }

    private static ModrinthVersion Version(string id, string versionNumber) => new()
    {
        Id = id,
        ProjectId = "P7dR8mSH",
        Name = $"Fabric API {versionNumber}",
        VersionNumber = versionNumber,
        FileName = $"fabric-api-{versionNumber}.jar",
        DownloadUrl = $"https://cdn.modrinth.com/data/P7dR8mSH/versions/{id}/fabric-api-{versionNumber}.jar",
        GameVersions = ["1.20.1"],
        Loaders = ["fabric"],
    };
}