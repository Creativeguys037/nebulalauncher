using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 下载候选与主机白名单。
///
/// 背景（实测）：BMCLAPI 的部分 maven 构件会 302 到 2.749333.xyz，该域名在本机网络不可达；
/// 另一部分构件直接 404。旧实现只对 404/403 回退官方，遇到「302 到未知 CDN 超时」就整体失败。
/// </summary>
public class CoreDownloads_MirrorTests
{
    private const string LibraryUrl =
        "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-arm64.jar";

    [Fact]
    public void 镜像源下候选是镜像在前官方在后()
    {
        var candidates = DownloadMirror.Candidates(LibraryUrl, DownloadSource.BmclApi);

        Assert.Equal(2, candidates.Count);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/maven/", candidates[0]);
        Assert.Equal(LibraryUrl, candidates[1]);
    }

    [Fact]
    public void 官方源下候选是官方在前镜像在后()
    {
        var candidates = DownloadMirror.Candidates(LibraryUrl, DownloadSource.Official);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(LibraryUrl, candidates[0]);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/maven/", candidates[1]);
    }

    [Fact]
    public void 不可重写的地址只有自身一个候选()
    {
        var candidates = DownloadMirror.Candidates("https://example.org/file.bin", DownloadSource.BmclApi);

        Assert.Single(candidates);
        Assert.Equal("https://example.org/file.bin", candidates[0]);
    }

    [Fact]
    public void 空地址没有候选()
    {
        Assert.Empty(DownloadMirror.Candidates(string.Empty, DownloadSource.BmclApi));
    }

    [Theory]
    // BMCLAPI 现在随机签发给两族 CDN：bmclapi<N>.sakura.* 与 <N>.749333.xyz。
    // 实测 8 次采样有 3 次首跳直接落在 749333.xyz（2026-10-22），因此它属于「镜像自己的主机」。
    // 判成未知主机的后果是：镜像候选被直接放弃 → 官方 maven.neoforged.net 不可达 → 加载器装不上。
    [InlineData("https://2.749333.xyz/download/abc?name=x.jar", true)]
    [InlineData("https://4.749333.xyz/download/abc", true)]
    [InlineData("https://bmclapi7.sakura.sld.tw/download/abc?name=x.jar", true)]
    [InlineData("https://bmclapi2.sakura.exp.dog/download/abc?name=x.jar", true)]
    [InlineData("https://bmclapi2.bangbang93.com/maven/org/lwjgl/x.jar", true)]
    [InlineData("https://libraries.minecraft.net/org/lwjgl/x.jar", true)]
    [InlineData("https://piston-data.mojang.com/v1/objects/aa/bb", true)]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar", true)]
    [InlineData("https://meta.fabricmc.net/v2/versions/loader/1.20.1", true)]
    [InlineData("https://cdn.modrinth.com/data/abc.jar", true)]
    [InlineData("https://random-cdn.example.com/download/abc?name=x.jar", false)]
    [InlineData("https://files.thirdparty-cdn.net/x.jar", false)]
    public void 已知下载主机白名单(string url, bool expected)
    {
        Assert.Equal(expected, DownloadMirror.IsKnownDownloadHost(url));
    }

    [Fact]
    public void 识别镜像地址()
    {
        Assert.True(DownloadMirror.IsMirrorUrl("https://bmclapi2.bangbang93.com/maven/x.jar"));
        Assert.False(DownloadMirror.IsMirrorUrl("https://libraries.minecraft.net/x.jar"));
        Assert.False(DownloadMirror.IsMirrorUrl(string.Empty));
    }
}
