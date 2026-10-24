using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Paths;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 游戏目录（%APPDATA%\.minecraft 或自定义）的解析、创建容错与可写性探测。
///
/// 这些用例对应一次真实故障：安装 Fabric 时因为某个托管目录创建失败，
/// 整个安装被连坐成 Access denied。路径创建必须逐目录容错，安装只关心 versions/libraries/assets。
/// </summary>
public class CorePaths_LauncherPathsTests
{
    [Fact]
    public void Constructor_ReadsMinecraftDirectoryFromSettings()
    {
        using var temp = new TempDirectory();
        var custom = temp.Combine("custom-game");
        File.WriteAllText(
            Path.Combine(temp.Path, "settings.json"),
            $$"""{"minecraftDirectory": "{{custom.Replace("\\", "\\\\")}}"}""");

        var paths = new LauncherPaths(temp.Path, null);

        Assert.Equal(Path.GetFullPath(custom), paths.MinecraftDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(custom, "versions")), paths.VersionsDirectory);
    }

    [Fact]
    public void Constructor_MinecraftOverrideWinsOverSettings()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "settings.json"),
            """{"minecraftDirectory": "C:\\from-settings"}""");

        var overridden = temp.Combine("from-argument");
        var paths = new LauncherPaths(temp.Path, overridden);

        Assert.Equal(Path.GetFullPath(overridden), paths.MinecraftDirectory);
    }

    [Fact]
    public void Constructor_IgnoresBrokenSettingsFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), "{ 这不是 JSON");

        // 关掉「默认不可写就回退」：本机 %APPDATA%\.minecraft 在受限环境不可写，
        // 开着回退会让这个用例断言到回退目录，与它要验证的「坏配置不崩」无关。
        var paths = new LauncherPaths(temp.Path, null, enableFallback: false);

        Assert.Equal(LauncherPaths.ResolveDefaultMinecraft(), paths.MinecraftDirectory);
    }

    [Fact]
    public void EnsureCreated_DoesNotThrow_WhenManagedPathIsAFile()
    {
        using var temp = new TempDirectory();
        // 模拟故障现场：某个托管目录的位置被一个同名文件占住（或被杀软拦截无法建目录）。
        File.WriteAllText(temp.Combine("cache"), "not a directory");

        var game = temp.Combine("game");
        var paths = new LauncherPaths(temp.Path, game);

        paths.EnsureCreated();

        Assert.Contains(paths.CreationFailures, f => f.Kind == "缓存");
        Assert.True(Directory.Exists(paths.VersionsDirectory));
        Assert.True(Directory.Exists(paths.LibrariesDirectory));
        Assert.True(Directory.Exists(paths.AssetsDirectory));
    }

    [Fact]
    public void EnsureCreated_RetriesUntilAllDirectoriesSucceed()
    {
        using var temp = new TempDirectory();
        var blocker = temp.Combine("cache");
        File.WriteAllText(blocker, "not a directory");

        var paths = new LauncherPaths(temp.Path, temp.Combine("game"));
        paths.EnsureCreated();
        Assert.NotEmpty(paths.CreationFailures);

        File.Delete(blocker);
        paths.EnsureCreated();

        Assert.Empty(paths.CreationFailures);
        Assert.True(Directory.Exists(paths.CacheDirectory));
    }

    [Fact]
    public void SetMinecraftDirectory_EmptyRestoresDefault()
    {
        using var temp = new TempDirectory();
        var paths = new LauncherPaths(temp.Path, temp.Combine("game"));

        paths.SetMinecraftDirectory(string.Empty);

        Assert.Equal(LauncherPaths.ResolveDefaultMinecraft(), paths.MinecraftDirectory);
    }

    [Fact]
    public void SetMinecraftDirectory_UpdatesDerivedDirectories()
    {
        using var temp = new TempDirectory();
        var paths = new LauncherPaths(temp.Path, temp.Combine("game"));

        var next = temp.Combine("another-game");
        paths.SetMinecraftDirectory(next);

        Assert.Equal(Path.GetFullPath(next), paths.MinecraftDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(next), "libraries"), paths.LibrariesDirectory);
        Assert.True(Directory.Exists(Path.Combine(next, "assets")));
    }

    [Fact]
    public void PathProbe_TestWritable_ReturnsNullForWritableDirectory()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("writable", "nested");

        Assert.Null(PathProbe.TestWritable(target));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void PathProbe_TestWritable_ReportsErrorWhenPathIsAFile()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("occupied");
        File.WriteAllText(file, "x");

        var reason = PathProbe.TestWritable(file);

        Assert.NotNull(reason);
        Assert.Contains("写入失败", reason);
    }

    [Fact]
    public void PathProbe_TestWritable_ReportsEmptyPath()
    {
        Assert.NotNull(PathProbe.TestWritable((string?)null));
        Assert.NotNull(PathProbe.TestWritable("   "));
    }

    [Fact]
    public void PathProbe_CheckFreeSpace_ReturnsNullWhenEnoughSpace()
    {
        using var temp = new TempDirectory();

        Assert.Null(PathProbe.CheckFreeSpace(temp.Path, requiredBytes: 0));
    }

    [Fact]
    public async Task SettingsService_NormalizesBlankMinecraftDirectoryToNull()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        File.WriteAllText(
            paths.SettingsFile,
            """{"minecraftDirectory": "   ", "maxMemoryMb": 4096}""");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Null(service.Current.MinecraftDirectory);
    }
}
