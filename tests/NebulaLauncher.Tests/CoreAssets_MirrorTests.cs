using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

public class CoreAssets_MirrorTests
{
    [Theory]
    [InlineData(
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
        "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")]
    [InlineData(
        "https://launchermeta.mojang.com/mc/game/version_manifest.json",
        "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json")]
    [InlineData(
        "https://launcher.mojang.com/v1/objects/abc/client.jar",
        "https://bmclapi2.bangbang93.com/v1/objects/abc/client.jar")]
    [InlineData(
        "https://resources.download.minecraft.net/ab/abcdef0123",
        "https://bmclapi2.bangbang93.com/assets/ab/abcdef0123")]
    [InlineData(
        "https://libraries.minecraft.net/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar",
        "https://bmclapi2.bangbang93.com/maven/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar")]
    [InlineData(
        "https://piston-data.mojang.com/v1/objects/def/client.jar",
        "https://bmclapi2.bangbang93.com/v1/objects/def/client.jar")]

    // NeoForge / Forge 的 maven 构件：官方 maven 在国内不可达，必须跟着 MC 官方 CDN 一起走镜像。
    // 前缀带 `/releases/` 是刻意的——版本目录接口在 `/api/maven/...` 下，不能被一起搬走。
    [InlineData(
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar",
        "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar")]
    public void BmclApi_改写命中_官方地址(string official, string expected)
    {
        Assert.Equal(expected, DownloadMirror.Rewrite(official, DownloadSource.BmclApi));
        Assert.True(DownloadMirror.IsOfficialCdn(official));
    }

    [Fact]
    public void Official_源保持原样()
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        Assert.Equal(url, DownloadMirror.Rewrite(url, DownloadSource.Official));
    }

    [Theory]
    [InlineData("https://meta.fabricmc.net/v2/versions/loader/1.20.1")]
    [InlineData("https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse")]
    [InlineData("https://cdn.jsdelivr.net/gh/FabricMC/fabric-example-mod@1.0.0/examplemod-1.0.0.jar")]
    [InlineData("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge")]
    public void 非官方CDN地址不被改写(string url)
    {
        Assert.Equal(url, DownloadMirror.Rewrite(url, DownloadSource.BmclApi));
        Assert.False(DownloadMirror.IsOfficialCdn(url));
    }

    [Fact]
    public void 空URL返回空且不抛()
    {
        Assert.Equal(string.Empty, DownloadMirror.Rewrite(string.Empty, DownloadSource.BmclApi));
        Assert.False(DownloadMirror.IsOfficialCdn(string.Empty));
    }

    [Fact]
    public void 改写幂等_镜像地址不会被二次改写()
    {
        const string official = "https://resources.download.minecraft.net/ab/abcdef";
        var once = DownloadMirror.Rewrite(official, DownloadSource.BmclApi);
        var twice = DownloadMirror.Rewrite(once, DownloadSource.BmclApi);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Handler按当前Source重写请求地址()
    {
        using var factory = new DownloadHttpClientFactory(DownloadSource.BmclApi);
        Assert.Equal(DownloadSource.BmclApi, factory.Source);

        factory.Source = DownloadSource.Official;
        Assert.Equal(DownloadSource.Official, factory.Source);
        Assert.Equal(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
            factory.Rewrite("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
    }
}
