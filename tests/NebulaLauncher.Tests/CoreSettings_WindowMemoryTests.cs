using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Platforms;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 启动器窗口尺寸记忆的持久化与判据。
///
/// 为什么单独测：
/// 1) 这是与「游戏窗口」<see cref="AppSettings.WindowWidth"/> 完全不同的一组字段，
///    复用会把游戏分辨率写进启动器；字段名一旦被手改坏，只能靠 Normalize 兜住；
/// 2) 「给模型加字段忘同步 Clone」的事故出过，
///    这四个新字段必须被 Clone 断言钉住；
/// 3) 「记住窗口大小」关掉后两端都不许读写，判据抽在 <see cref="WindowMemory"/>，
///    组合能在这里钉死，窗口侧只负责调用。
/// </summary>
public class CoreSettings_WindowMemoryTests
{
    [Fact]
    public void Defaults_UseLauncherWindowDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal(1240, settings.LauncherWindowWidth);
        Assert.Equal(800, settings.LauncherWindowHeight);
        Assert.False(settings.LauncherWindowMaximized);
        Assert.True(settings.RememberWindowSize);
    }

    [Fact]
    public async Task Load_KeepsValidLauncherWindowSize()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "launcherWindowWidth": 1500,
              "launcherWindowHeight": 900,
              "launcherWindowMaximized": true,
              "rememberWindowSize": false
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(1500, service.Current.LauncherWindowWidth);
        Assert.Equal(900, service.Current.LauncherWindowHeight);
        Assert.True(service.Current.LauncherWindowMaximized);
        Assert.False(service.Current.RememberWindowSize);

        // 游戏窗口尺寸必须保持自己的默认值，不能被启动器窗口字段污染。
        Assert.Equal(854, service.Current.WindowWidth);
        Assert.Equal(480, service.Current.WindowHeight);
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(-1, 600)]
    [InlineData(1023, 659)]
    [InlineData(320, 240)]
    public async Task Load_BelowMinimum_FallsBackToDefaults(int width, int height)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            $"{{\"launcherWindowWidth\":{width},\"launcherWindowHeight\":{height}}}");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(AppSettings.DefaultLauncherWindowWidth, service.Current.LauncherWindowWidth);
        Assert.Equal(AppSettings.DefaultLauncherWindowHeight, service.Current.LauncherWindowHeight);
    }

    [Theory]
    [InlineData(1024, 660)]
    [InlineData(1240, 800)]
    public async Task Load_AtLowerBound_KeepsValue(int width, int height)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            $"{{\"launcherWindowWidth\":{width},\"launcherWindowHeight\":{height}}}");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(width, service.Current.LauncherWindowWidth);
        Assert.Equal(height, service.Current.LauncherWindowHeight);
    }

    [Theory]
    [InlineData(8001, 8000)]
    [InlineData(int.MaxValue, 8000)]
    public async Task Load_AboveMaximum_ClampsToMax(int width, int height)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            $"{{\"launcherWindowWidth\":{width},\"launcherWindowHeight\":{height}}}");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(AppSettings.MaximumLauncherWindowSize, service.Current.LauncherWindowWidth);
        Assert.Equal(AppSettings.MaximumLauncherWindowSize, service.Current.LauncherWindowHeight);
    }

    [Fact]
    public async Task Load_NonFiniteSize_FallsBackToDefaults()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // JSON 里的 1e999 超出 int 可表示范围，反序列化阶段就会失败：
        // 整个文件按「损坏」处理并回退默认值，绝不能把无穷大当成合法窗口尺寸。
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "launcherWindowWidth": 1e999,
              "launcherWindowHeight": 1e999
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(AppSettings.DefaultLauncherWindowWidth, service.Current.LauncherWindowWidth);
        Assert.Equal(AppSettings.DefaultLauncherWindowHeight, service.Current.LauncherWindowHeight);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsLauncherWindowFields()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var first = new SettingsService(paths, NullAppLog.Instance);
        await first.LoadAsync();

        first.Current.LauncherWindowWidth = 1600;
        first.Current.LauncherWindowHeight = 1000;
        first.Current.LauncherWindowMaximized = true;
        first.Current.RememberWindowSize = false;

        await first.SaveAsync();

        var second = new SettingsService(paths, NullAppLog.Instance);
        await second.LoadAsync();

        Assert.Equal(1600, second.Current.LauncherWindowWidth);
        Assert.Equal(1000, second.Current.LauncherWindowHeight);
        Assert.True(second.Current.LauncherWindowMaximized);
        Assert.False(second.Current.RememberWindowSize);

        var json = await File.ReadAllTextAsync(paths.SettingsFile);
        Assert.Contains("\"launcherWindowWidth\"", json, StringComparison.Ordinal);
        Assert.Contains("\"launcherWindowHeight\"", json, StringComparison.Ordinal);
        Assert.Contains("\"launcherWindowMaximized\"", json, StringComparison.Ordinal);
        Assert.Contains("\"rememberWindowSize\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_CopiesLauncherWindowFields()
    {
        var original = new AppSettings
        {
            LauncherWindowWidth = 1600,
            LauncherWindowHeight = 1000,
            LauncherWindowMaximized = true,
            RememberWindowSize = false,
        };

        var clone = original.Clone();

        Assert.Equal(1600, clone.LauncherWindowWidth);
        Assert.Equal(1000, clone.LauncherWindowHeight);
        Assert.True(clone.LauncherWindowMaximized);
        Assert.False(clone.RememberWindowSize);

        // 值类型字段改副本不能影响原件；这条同时防止以后把它们换成可变引用类型时漏拷贝。
        clone.LauncherWindowWidth = 1240;
        clone.LauncherWindowMaximized = false;
        Assert.Equal(1600, original.LauncherWindowWidth);
        Assert.True(original.LauncherWindowMaximized);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void WindowMemory_ShouldPersist_RequiresToggleOnAndNoScreenshot(bool remember, bool screenshot, bool expected)
    {
        Assert.Equal(expected, WindowMemory.ShouldPersist(remember, screenshot));
    }
}
