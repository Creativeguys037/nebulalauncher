using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>本地版本扫描、加载器识别、原版版本回溯与启动记录。</summary>
public class CoreLaunch_VersionServiceTests
{
    [Fact]
    public async Task GetInstalled_RecognizesAllLoaders()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.20.1", null);
        FakeVersionDirectory.Write(root, "fabric-loader-0.15.0-1.20.1", "1.20.1");
        FakeVersionDirectory.Write(root, "quilt-loader-0.23.1-1.20.1", "1.20.1");
        FakeVersionDirectory.Write(root, "1.20.1-forge-47.2.0", "1.20.1");
        FakeVersionDirectory.Write(root, "neoforge-21.1.80", "1.21.1");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        Assert.Equal(5, installed.Count);

        var vanilla = Single(installed, "1.20.1");
        Assert.Equal(LoaderKind.Vanilla, vanilla.Loader);
        Assert.Null(vanilla.LoaderVersion);
        Assert.Null(vanilla.InheritsFrom);
        Assert.Equal("1.20.1", vanilla.MinecraftVersion);

        var fabric = Single(installed, "fabric-loader-0.15.0-1.20.1");
        Assert.Equal(LoaderKind.Fabric, fabric.Loader);
        Assert.Equal("0.15.0", fabric.LoaderVersion);
        Assert.Equal("1.20.1", fabric.InheritsFrom);
        Assert.Equal("1.20.1", fabric.MinecraftVersion);

        var quilt = Single(installed, "quilt-loader-0.23.1-1.20.1");
        Assert.Equal(LoaderKind.Quilt, quilt.Loader);
        Assert.Equal("0.23.1", quilt.LoaderVersion);
        Assert.Equal("1.20.1", quilt.MinecraftVersion);

        var forge = Single(installed, "1.20.1-forge-47.2.0");
        Assert.Equal(LoaderKind.Forge, forge.Loader);
        Assert.Equal("47.2.0", forge.LoaderVersion);
        Assert.Equal("1.20.1", forge.MinecraftVersion);

        var neoForge = Single(installed, "neoforge-21.1.80");
        Assert.Equal(LoaderKind.NeoForge, neoForge.Loader);
        Assert.Equal("21.1.80", neoForge.LoaderVersion);
        Assert.Equal("1.21.1", neoForge.MinecraftVersion);
    }

    [Fact]
    public async Task GetInstalled_FollowsMultiLevelInheritanceChain()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.20.1", null);
        FakeVersionDirectory.Write(root, "fabric-loader-0.15.0-1.20.1", "1.20.1");
        FakeVersionDirectory.Write(root, "my-custom-fabric-pack", "fabric-loader-0.15.0-1.20.1");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        // 多层继承里只有最后一跳才是原版版本。
        var pack = Single(installed, "my-custom-fabric-pack");
        Assert.Equal(LoaderKind.Fabric, pack.Loader);
        Assert.Equal("1.20.1", pack.MinecraftVersion);
    }

    [Fact]
    public async Task GetInstalled_SortsDescendingByVersion()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.19.4", null);
        FakeVersionDirectory.Write(root, "1.20.1", null);
        FakeVersionDirectory.Write(root, "1.9.4", null);
        FakeVersionDirectory.Write(root, "1.20.10", null);

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        Assert.Equal(
            ["1.20.10", "1.20.1", "1.19.4", "1.9.4"],
            installed.Select(v => v.Name).ToArray());
    }

    [Fact]
    public async Task GetInstalled_MarksBrokenJson()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var directory = Path.Combine(paths.VersionsDirectory, "broken");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "{ not valid json");

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        var broken = Assert.Single(installed);
        Assert.Equal("broken", broken.Name);
        Assert.False(string.IsNullOrEmpty(broken.BrokenReason));
    }

    [Fact]
    public async Task GetInstalled_MissingJson_IsReportedAsBroken()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        Directory.CreateDirectory(Path.Combine(paths.VersionsDirectory, "empty-dir"));

        var service = new VersionService(paths, NullAppLog.Instance);
        var installed = await service.GetInstalledAsync();

        var entry = Assert.Single(installed);
        Assert.Equal("empty-dir", entry.Name);
        Assert.NotNull(entry.BrokenReason);
    }

    [Fact]
    public async Task GetVersion_ReturnsNullForUnknown()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var service = new VersionService(paths, NullAppLog.Instance);

        Assert.Null(await service.GetVersionAsync("does-not-exist"));
        Assert.Null(await service.GetVersionAsync("   "));
    }

    [Fact]
    public async Task RecordLaunch_AccumulatesPlayTime()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.20.1", null);

        var service = new VersionService(paths, NullAppLog.Instance);

        await service.RecordLaunchAsync("1.20.1", TimeSpan.FromMinutes(5));
        await service.RecordLaunchAsync("1.20.1", TimeSpan.FromMinutes(3));

        var info = await service.GetVersionAsync("1.20.1");

        Assert.NotNull(info);
        Assert.NotNull(info!.LastPlayedAt);
        Assert.Equal(480, info.TotalPlayTimeSeconds);

        var recordFile = Path.Combine(root, "1.20.1", "nebula.json");
        Assert.True(File.Exists(recordFile));
        Assert.Contains("lastPlayedAt", await File.ReadAllTextAsync(recordFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordLaunch_ForMissingVersion_DoesNothing()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new VersionService(paths, NullAppLog.Instance);

        await service.RecordLaunchAsync("ghost", TimeSpan.FromMinutes(1));

        Assert.False(Directory.Exists(Path.Combine(paths.VersionsDirectory, "ghost")));
    }

    [Fact]
    public async Task Delete_RemovesVersionDirectory()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var root = paths.VersionsDirectory;

        FakeVersionDirectory.Write(root, "1.20.1", null);

        var service = new VersionService(paths, NullAppLog.Instance);
        await service.DeleteAsync("1.20.1");

        Assert.False(Directory.Exists(Path.Combine(root, "1.20.1")));
        Assert.Empty(await service.GetInstalledAsync());
    }

    [Fact]
    public async Task GetLoaderVersions_UnsupportedLoader_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new VersionService(paths, NullAppLog.Instance);

        // Forge 从「永远为空」改成「拉 promoted 列表」之后，这里不再断言它：
        // 那条路径要联网，断言留在 CoreVersions_ForgeRecommendTests（用桩 HttpClient）。
        Assert.Empty(await service.GetLoaderVersionsAsync(LoaderKind.Vanilla, "1.20.1"));
        Assert.Empty(await service.GetLoaderVersionsAsync(LoaderKind.Fabric, " "));
    }

    private static VersionInfo Single(IReadOnlyList<VersionInfo> versions, string name) =>
        Assert.Single(versions, v => v.Name == name);
}

