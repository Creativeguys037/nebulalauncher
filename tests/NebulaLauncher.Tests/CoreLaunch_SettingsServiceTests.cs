using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>设置的读写往返、损坏回退、原子写与变更通知。</summary>
public class CoreLaunch_SettingsServiceTests
{
    [Fact]
    public async Task Load_WhenFileMissing_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new SettingsService(paths, NullAppLog.Instance);

        await service.LoadAsync();

        Assert.Equal(4096, service.Current.MaxMemoryMb);
        Assert.Equal(DownloadSource.BmclApi, service.Current.DownloadSource);
        Assert.Equal("dark", service.Current.Theme);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var first = new SettingsService(paths, NullAppLog.Instance);
        await first.LoadAsync();

        first.Current.MinecraftDirectory = @"D:\games\.minecraft";
        first.Current.JavaPath = @"C:\Java\jdk17\bin\javaw.exe";
        first.Current.MinMemoryMb = 1024;
        first.Current.MaxMemoryMb = 8192;
        first.Current.WindowWidth = 1280;
        first.Current.WindowHeight = 720;
        first.Current.Fullscreen = true;
        first.Current.ExtraJvmArguments = "-XX:+UseG1GC";
        first.Current.DownloadSource = DownloadSource.Official;
        first.Current.DownloadThreads = 32;
        first.Current.Theme = "light";
        first.Current.EnableAnimations = false;
        first.Current.EnableBackgroundEffects = false;
        first.Current.WindowMaterial = "acrylic";
        first.Current.ContentSource = "curseforge";
        first.Current.LastSelectedVersion = "1.20.1";
        first.Current.LastAccountId = "abc123";
        first.Current.CloseToTray = true;
        first.Current.CheckUpdatesOnStartup = false;
        first.Current.CloseLauncherAfterLaunch = true;

        await first.SaveAsync();

        var second = new SettingsService(paths, NullAppLog.Instance);
        await second.LoadAsync();

        var actual = second.Current;

        Assert.Equal(@"D:\games\.minecraft", actual.MinecraftDirectory);
        Assert.Equal(@"C:\Java\jdk17\bin\javaw.exe", actual.JavaPath);
        Assert.Equal(1024, actual.MinMemoryMb);
        Assert.Equal(8192, actual.MaxMemoryMb);
        Assert.Equal(1280, actual.WindowWidth);
        Assert.Equal(720, actual.WindowHeight);
        Assert.True(actual.Fullscreen);
        Assert.Equal("-XX:+UseG1GC", actual.ExtraJvmArguments);
        Assert.Equal(DownloadSource.Official, actual.DownloadSource);
        Assert.Equal(32, actual.DownloadThreads);
        Assert.Equal("light", actual.Theme);
        Assert.False(actual.EnableAnimations);
        Assert.False(actual.EnableBackgroundEffects);
        Assert.Equal("acrylic", actual.WindowMaterial);
        Assert.Equal("curseforge", actual.ContentSource);
        Assert.Equal("1.20.1", actual.LastSelectedVersion);
        Assert.Equal("abc123", actual.LastAccountId);
        Assert.True(actual.CloseToTray);
        Assert.False(actual.CheckUpdatesOnStartup);
        Assert.True(actual.CloseLauncherAfterLaunch);
    }

    [Fact]
    public async Task Save_WritesCamelCaseAndKeepsNoTempFile()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new SettingsService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(paths.SettingsFile);

        Assert.Contains("\"maxMemoryMb\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MaxMemoryMb\"", json, StringComparison.Ordinal);

        // 下载源三态化之后，枚举主名从 BmclApi 改成 Mirror（旧名只用于读历史配置）。
        // 这条断言是「枚举按名字序列化」的样本，所以跟着改成新主名。
        Assert.Contains("\"downloadSource\": \"Mirror\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public async Task Load_WhenFileCorrupt_FallsBackToDefaultsAndBacksUp()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(paths.SettingsFile, "{ this is not json ");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(4096, service.Current.MaxMemoryMb);
        Assert.True(File.Exists(paths.SettingsFile + ".bak"));
        Assert.Contains("not json", await File.ReadAllTextAsync(paths.SettingsFile + ".bak"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_RaisesChangedEvent()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new SettingsService(paths, NullAppLog.Instance);

        await service.LoadAsync();

        AppSettings? notified = null;
        service.Changed += (_, settings) => notified = settings;

        service.Current.Theme = "light";
        await service.SaveAsync();

        Assert.NotNull(notified);
        Assert.Equal("light", notified!.Theme);
    }

    [Fact]
    public async Task Load_ClampsOutOfRangeValues()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "downloadThreads": 1000,
              "minMemoryMb": 8192,
              "maxMemoryMb": 1024,
              "windowWidth": 10,
              "windowHeight": 10,
              "theme": ""
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(256, service.Current.DownloadThreads);
        Assert.Equal(8192, service.Current.MinMemoryMb);
        Assert.Equal(8192, service.Current.MaxMemoryMb);
        Assert.Equal(320, service.Current.WindowWidth);
        Assert.Equal(240, service.Current.WindowHeight);
        Assert.Equal("dark", service.Current.Theme);
    }
}
