using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// NeoForge 安装桥接里的纯逻辑：版本目录解析、构建选择、目录名与 installer jar 地址。
///
/// 网络与真装都不在这里（本机 <c>maven.neoforged.net</c> 不可达，安装流程靠
/// <c>tools/NeoForgeProbe</c> 与 <c>tools/InstallProbe</c> 取证）。这些用例锁的是
/// 「换个版本的目录、换个写法进来，选出来的是不是同一个构建」。
/// </summary>
public class CoreInstalling_NeoForgeTests
{
    /// <summary>BMCLAPI details 的形状：目录与普通文件混在一起，只有 type=DIRECTORY 是构建。</summary>
    private const string MirrorJson = """
        {"name":"neoforge","files":[
          {"name":"21.1.264","type":"DIRECTORY"},
          {"name":"21.1.72","type":"DIRECTORY"},
          {"name":"21.1.265-beta","type":"DIRECTORY"},
          {"name":"neoforge-21.1.264-installer.jar","type":"FILE"},
          {"name":"neoforge-21.1.264.pom.md5","type":"FILE"}
        ]}
        """;

    /// <summary>官方 maven versions 接口的形状。</summary>
    private const string OfficialJson = """
        {"isSnapshot":false,"versions":["21.1.264","21.1.72","21.1.265-beta"]}
        """;

    [Fact]
    public void 两种目录形状都解析成构建名且跳过普通文件()
    {
        var mirror = NeoForgeInstallerBridge.ParseCatalogNames(MirrorJson);
        var official = NeoForgeInstallerBridge.ParseCatalogNames(OfficialJson);

        Assert.Equal(3, mirror.Count);
        Assert.DoesNotContain("neoforge-21.1.264-installer.jar", mirror);
        Assert.DoesNotContain("neoforge-21.1.264.pom.md5", mirror);
        Assert.Equal(3, official.Count);
    }

    [Fact]
    public void 坏JSON返回空集合而不是抛异常()
    {
        Assert.Empty(NeoForgeInstallerBridge.ParseCatalogNames("<html>403 Forbidden</html>"));
        Assert.Empty(NeoForgeInstallerBridge.ParseCatalogNames(string.Empty));
        Assert.Empty(NeoForgeInstallerBridge.ParseCatalogNames("[]"));
    }

    [Fact]
    public void 现代构建按去掉1后的游戏版本前缀过滤且不串版本()
    {
        string[] names = ["20.4.30", "21.1.72", "21.1.264", "21.10.3", "0.25w14craftmine.3-beta"];

        var for1211 = NeoForgeInstallerBridge.ForMinecraftVersion(names, "1.21.1");
        var for12110 = NeoForgeInstallerBridge.ForMinecraftVersion(names, "1.21.10");

        // 21.10.x 不能因为前缀相似被算进 1.21.1。
        Assert.Equal(["21.1.72", "21.1.264"], for1211);
        Assert.Equal(["21.10.3"], for12110);
    }

    [Fact]
    public void Legacy构建按完整游戏版本前缀过滤()
    {
        string[] names = ["1.20.1-47.1.5", "1.20.1-47.1.106", "21.1.264"];

        var for1201 = NeoForgeInstallerBridge.ForMinecraftVersion(names, "1.20.1");

        Assert.Equal(["1.20.1-47.1.5", "1.20.1-47.1.106"], for1201);
        Assert.True(NeoForgeInstallerBridge.IsLegacy("1.20.1"));
        Assert.False(NeoForgeInstallerBridge.IsLegacy("1.20.2"));
    }

    [Fact]
    public void 默认取最新稳定版且数字段按数值比较()
    {
        // 字符串序会把 "21.1.9" 排在 "21.1.10" 之后，这里必须是数值序。
        var sorted = NeoForgeInstallerBridge.SortByLoaderVersion(
            ["21.1.10", "21.1.9", "21.1.264", "21.1.265-beta"],
            "1.21.1");

        Assert.Equal(["21.1.9", "21.1.10", "21.1.264", "21.1.265-beta"], sorted);
        Assert.Equal("21.1.264", NeoForgeInstallerBridge.SelectApiName(sorted, null, "1.21.1"));
    }

    [Fact]
    public void 只有beta时默认退到最新beta()
    {
        var sorted = NeoForgeInstallerBridge.SortByLoaderVersion(["20.2.3-beta", "20.2.4-beta"], "1.20.2");

        Assert.Equal("20.2.4-beta", NeoForgeInstallerBridge.SelectApiName(sorted, null, "1.20.2"));
    }

    [Fact]
    public void 用户三种写法都能匹配到同一个构建()
    {
        var modern = NeoForgeInstallerBridge.SortByLoaderVersion(["21.1.72", "21.1.264"], "1.21.1");

        Assert.Equal("21.1.72", NeoForgeInstallerBridge.SelectApiName(modern, "21.1.72", "1.21.1"));
        Assert.Equal("21.1.72", NeoForgeInstallerBridge.SelectApiName(modern, "neoforge-21.1.72", "1.21.1"));
        Assert.Equal("21.1.72", NeoForgeInstallerBridge.SelectApiName(modern, " 1.21.1-21.1.72 ", "1.21.1"));
        Assert.Null(NeoForgeInstallerBridge.SelectApiName(modern, "21.1.999", "1.21.1"));

        // Legacy：用户只写加载器版本号时自动补回 1.20.1- 前缀。
        var legacy = NeoForgeInstallerBridge.SortByLoaderVersion(["1.20.1-47.1.5", "1.20.1-47.1.106"], "1.20.1");

        Assert.Equal("1.20.1-47.1.106", NeoForgeInstallerBridge.SelectApiName(legacy, "47.1.106", "1.20.1"));
        Assert.Equal("1.20.1-47.1.106", NeoForgeInstallerBridge.SelectApiName(legacy, "1.20.1-47.1.106", "1.20.1"));
    }

    [Fact]
    public void 目录名现代保留版本号Legacy去掉游戏版本前缀()
    {
        Assert.Equal("neoforge-21.1.264", NeoForgeInstallerBridge.VersionFolderName("1.21.1", "21.1.264"));
        Assert.Equal("neoforge-47.1.106", NeoForgeInstallerBridge.VersionFolderName("1.20.1", "1.20.1-47.1.106"));
    }

    [Fact]
    public void 目录地址按坐标选包名()
    {
        var modern = NeoForgeInstallerBridge.CatalogUrls("1.21.1");
        var legacy = NeoForgeInstallerBridge.CatalogUrls("1.20.1");

        Assert.Contains("/net/neoforged/neoforge", modern[0]);
        Assert.StartsWith("https://bmclapi2.bangbang93.com/", modern[0]);
        Assert.StartsWith("https://maven.neoforged.net/api/maven/versions/releases/", modern[1]);

        Assert.Contains("/net/neoforged/forge", legacy[0]);
        Assert.Contains("/net/neoforged/forge", legacy[1]);
        Assert.DoesNotContain("neoforge", legacy[0].Split("net/neoforged/")[1]);
    }

    [Fact]
    public void 镜像映射覆盖NeoForge的releases路径但不碰版本目录接口()
    {
        const string installerJar =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar";

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar",
            DownloadMirror.Rewrite(installerJar, DownloadSource.Mirror));

        // 版本目录接口在 /api/maven/versions/releases/ 下。若映射写成 "maven.neoforged.net/"
        // 整段替换，这条会被改成 bmclapi 的 /maven/api/maven/... （404），目录就永远查不到。
        const string catalogApi = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
        Assert.Equal(catalogApi, DownloadMirror.Rewrite(catalogApi, DownloadSource.Mirror));

        // 镜像重定向校验：BMCLAPI 与 neoforged 官方都在已知下载域内。
        Assert.True(DownloadMirror.IsKnownDownloadHost(
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar"));
        Assert.True(DownloadMirror.IsKnownDownloadHost(installerJar));

        // BMCLAPI 的 /maven/ 未命中时会 302 到教育网镜像。这两个域若判成「未知主机」，
        // 安装器 jar 会直接放弃镜像候选，而官方源在国内不可达 —— 必然失败。
        Assert.True(DownloadMirror.IsKnownDownloadHost(
            "https://mirrors.cernet.edu.cn/bmclapi/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar"));
        Assert.True(DownloadMirror.IsKnownDownloadHost(
            "https://cmcc.mirrors.ustc.edu.cn/bmclapi/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar"));
        Assert.False(DownloadMirror.IsKnownDownloadHost("https://random-cdn.example.com/x.jar"));

        // 轮换兜底：BMCLAPI 会把同一份文件签到**不同域名**的分发 CDN 上（实测到四族），
        // 域名枚举必然漏。带 `/download/<sha1>` 或 `/bmclapi/<xx>/<sha1>` 形状的签名直链一律放行。
        Assert.True(DownloadMirror.IsKnownDownloadHost(
            "https://open-bmcl-api.lunar.cc:443/download/f67f5ffaa5f5130cf6fb9b133da00c7df3b532a5?name=x.jar&s=abc&e=xyz"));
        Assert.True(DownloadMirror.IsKnownDownloadHost(
            "https://minio.749333.xyz/bmclapi/f8/f801601bafa7f5957295353930e2cb61dcfddc6c?X-Amz-Signature=abc"));
        // 形状不符的第三方地址仍然拦住。
        Assert.False(DownloadMirror.IsKnownDownloadHost("https://random-cdn.example.com/download/abc?name=x.jar"));
    }

    [Fact]
    public void Legacy的installer地址被改到forge坐标其它地址不动()
    {
        const string apiName = "1.20.1-47.1.106";
        var cmlLibUrl = NeoForgeInstallerBridge.CmlLibInstallerUrl(apiName);

        // CmlLib 把包名硬编码成 neoforge，这个地址在 1.20.1 上必然 404（实测）。
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/1.20.1-47.1.106/neoforge-1.20.1-47.1.106-installer.jar",
            cmlLibUrl);

        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
            NeoForgeInstallerBridge.RewriteInstallerUrl(cmlLibUrl, "1.20.1", apiName));

        // 同一次安装里的库文件不能被改。
        const string library = "https://maven.neoforged.net/releases/net/neoforged/mergetool/1.1.5/mergetool-1.1.5-data.jar";
        Assert.Equal(library, NeoForgeInstallerBridge.RewriteInstallerUrl(library, "1.20.1", apiName));

        // 现代版本本来就在 neoforge 坐标上，不动。
        const string modernApi = "21.1.264";
        Assert.Equal(
            NeoForgeInstallerBridge.CmlLibInstallerUrl(modernApi),
            NeoForgeInstallerBridge.RewriteInstallerUrl(
                NeoForgeInstallerBridge.CmlLibInstallerUrl(modernApi), "1.21.1", modernApi));
    }
}