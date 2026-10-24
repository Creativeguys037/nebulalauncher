using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 主题设置的归一化与拷贝语义。
///
/// 为什么单独测：
/// 1) <c>settings.json</c> 按字段名序列化，用户手改 / 旧版本写入都可能留下非法主题值，
///    Normalize 是唯一兜底点，必须钉死三值合法、其余回落 dark；
/// 2) HANDOVER §5.1 记录过「给模型加字段忘同步 Clone」的事故（InstanceService 的
///    Loader / MinecraftVersion 就是这么丢的），主题是后加字段，用测试把 Clone 也钉住。
/// </summary>
public class CoreSettings_ThemeTests
{
    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("light", "light")]
    [InlineData("system", "system")]
    [InlineData("LIGHT", "light")]
    [InlineData("  System  ", "system")]
    [InlineData("purple", "dark")]
    [InlineData("", "dark")]
    [InlineData("   ", "dark")]
    [InlineData((string?)null, "dark")]
    public async Task Load_NormalizesThemeToThreeKnownValues(string? stored, string expected)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 用 JsonSerializer 的 Web 默认策略写文件，与 SettingsService 的读法保持一致。
        var json = stored is null ? "{}" : $"{{\"theme\":\"{stored}\"}}";
        await File.WriteAllTextAsync(paths.SettingsFile, json);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(expected, service.Current.Theme);
    }

    [Fact]
    public void Clone_CopiesTheme()
    {
        var original = new AppSettings { Theme = "light" };

        var clone = original.Clone();

        Assert.Equal("light", clone.Theme);

        // 字符串是不可变的，改副本不能影响原件；这条同时防止以后 Theme 被换成可变引用类型。
        clone.Theme = "system";
        Assert.Equal("light", original.Theme);
    }

    [Fact]
    public void Clone_KeepsOtherFieldsIndependent()
    {
        var original = new AppSettings { Theme = "system" };
        original.JvmPresets.Add(new JvmPreset { Name = "G1", Arguments = "-XX:+UseG1GC" });

        var clone = original.Clone();
        clone.JvmPresets.Add(new JvmPreset { Name = "ZGC", Arguments = "-XX:+UseZGC" });

        Assert.Equal("system", clone.Theme);
        Assert.Single(original.JvmPresets);
        Assert.Equal(2, clone.JvmPresets.Count);
    }
}
