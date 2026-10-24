using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// 整合包导入后的版本目录名是「整合包实例名」，不含加载器字样；
/// 加载器信息只在 <c>inheritsFrom</c> 指向的父版本名里。
/// 这组用例锁住 <c>VersionService</c> 沿父版本名识别加载器的行为：
/// 认不出就退回「原版」，会让导入的整包在版本页与模组页里丢掉 Forge / Fabric 标签。
/// </summary>
public class CoreVersions_InheritedVersionTests
{
    [Fact]
    public async Task 整合包版本从Forge父版本名识别加载器()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        // 导入产出：versions/<整包名>/<整包名>.json，inheritsFrom 指向共享的 Forge 版本。
        FakeVersionDirectory.Write(root, "1.20.1", inheritsFrom: null);
        FakeVersionDirectory.Write(root, "1.20.1-forge-47.2.0", inheritsFrom: "1.20.1");
        FakeVersionDirectory.Write(root, "Pack A", inheritsFrom: "1.20.1-forge-47.2.0");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        var pack = Assert.Single(installed, version => version.Name == "Pack A");

        Assert.Equal(LoaderKind.Forge, pack.Loader);
        Assert.Equal("47.2.0", pack.LoaderVersion);
        Assert.Equal("1.20.1", pack.MinecraftVersion);
        Assert.Equal("1.20.1-forge-47.2.0", pack.InheritsFrom);
        Assert.Null(pack.BrokenReason);
    }

    [Fact]
    public async Task 整合包版本从Fabric父版本名同时剥出加载器版本()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.20.1", inheritsFrom: null);
        FakeVersionDirectory.Write(root, "fabric-loader-0.16.9-1.20.1", inheritsFrom: "1.20.1");
        FakeVersionDirectory.Write(root, "重整合包", inheritsFrom: "fabric-loader-0.16.9-1.20.1");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        var pack = Assert.Single(installed, version => version.Name == "重整合包");

        Assert.Equal(LoaderKind.Fabric, pack.Loader);

        // 原版版本号挂在父版本名尾部（fabric-loader-<loader>-<mc>），必须用沿链解析出的
        // 原版版本把它剥掉，否则加载器版本会变成 "0.16.9-1.20.1"。
        Assert.Equal("0.16.9", pack.LoaderVersion);
        Assert.Equal("1.20.1", pack.MinecraftVersion);
    }

    [Fact]
    public async Task 父版本是原版时整合包版本不被误判成加载器版本()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.20.1", inheritsFrom: null);
        FakeVersionDirectory.Write(root, "原版整合包", inheritsFrom: "1.20.1");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        var pack = Assert.Single(installed, version => version.Name == "原版整合包");

        Assert.Equal(LoaderKind.Vanilla, pack.Loader);
        Assert.Null(pack.LoaderVersion);
        Assert.Equal("1.20.1", pack.MinecraftVersion);
    }

    [Fact]
    public async Task 包名含加载器字眼时以父版本名为准()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        // 标题里带 "[FORGE]"、实际加载器却是 Fabric：不能按目录名抠出 " ] BMC4" 之类的假版本号。
        FakeVersionDirectory.Write(root, "1.20.1", inheritsFrom: null);
        FakeVersionDirectory.Write(root, "fabric-loader-0.16.9-1.20.1", inheritsFrom: "1.20.1");
        FakeVersionDirectory.Write(root, "Better MC [FORGE] BMC4", inheritsFrom: "fabric-loader-0.16.9-1.20.1");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        var pack = Assert.Single(installed, version => version.Name == "Better MC [FORGE] BMC4");

        Assert.Equal(LoaderKind.Fabric, pack.Loader);
        Assert.Equal("0.16.9", pack.LoaderVersion);
        Assert.Equal("1.20.1", pack.MinecraftVersion);
    }
}
