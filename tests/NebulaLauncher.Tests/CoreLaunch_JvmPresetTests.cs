using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// JVM 参数预设：解析优先级（实例 &gt; 预设 &gt; 全局原文）与配置清理。
///
/// 这里只覆盖纯函数与 settings.json 的载入路径。三个启动入口的接线靠构建 +
/// 设置页截图兜底——本机不能起游戏进程，也不该为一条 <c>LaunchOptions</c> 赋值去 mock 整个启动器。
/// </summary>
public class CoreLaunch_JvmPresetTests
{
    // ==================== Resolve：空设置 / 无选中 / 选中 ====================

    [Fact]
    public void Resolve_NullSettings_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, JvmPresetResolver.Resolve(null));
    }

    [Fact]
    public void Resolve_NoActivePreset_FallsBackToGlobalArguments()
    {
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-XX:+UseSerialGC",
            JvmPresets =
            {
                new JvmPreset { Id = "p1", Name = "G1", Arguments = "-XX:+UseG1GC" },
            },
            ActiveJvmPresetId = null,
        };

        Assert.Equal("-XX:+UseSerialGC", JvmPresetResolver.Resolve(settings));
    }

    [Fact]
    public void Resolve_ActivePresetFound_UsesPresetArguments()
    {
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-XX:+UseSerialGC",
            JvmPresets =
            {
                new JvmPreset { Id = "p1", Name = "G1", Arguments = "-XX:+UseG1GC" },
                new JvmPreset { Id = "p2", Name = "ZGC", Arguments = "-XX:+UseZGC" },
            },
            ActiveJvmPresetId = "p2",
        };

        Assert.Equal("-XX:+UseZGC", JvmPresetResolver.Resolve(settings));
    }

    [Fact]
    public void Resolve_ActivePresetMissing_FallsBackToGlobalArguments()
    {
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-XX:+UseSerialGC",
            JvmPresets =
            {
                new JvmPreset { Id = "p1", Name = "G1", Arguments = "-XX:+UseG1GC" },
            },
            ActiveJvmPresetId = "ghost",
        };

        // 预设被删掉但指针还在：退回原文，不能让启动拿到空参数集。
        Assert.Equal("-XX:+UseSerialGC", JvmPresetResolver.Resolve(settings));
    }

    // ==================== ResolveForInstance：null 与空串语义相反 ====================

    [Fact]
    public void ResolveForInstance_NullInstanceArguments_FallsBackToPreset()
    {
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-XX:+UseSerialGC",
            JvmPresets =
            {
                new JvmPreset { Id = "p1", Name = "G1", Arguments = "-XX:+UseG1GC" },
            },
            ActiveJvmPresetId = "p1",
        };

        Assert.Equal("-XX:+UseG1GC", JvmPresetResolver.ResolveForInstance(settings, null));
    }

    [Fact]
    public void ResolveForInstance_EmptyInstanceArguments_MeansExplicitlyNoArguments()
    {
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-XX:+UseSerialGC",
            JvmPresets =
            {
                new JvmPreset { Id = "p1", Name = "G1", Arguments = "-XX:+UseG1GC" },
            },
            ActiveJvmPresetId = "p1",
        };

        // 空串是「这个实例不要任何额外参数」，不能回落到预设——这正是与 FirstNonEmpty 的关键差异。
        Assert.Equal(string.Empty, JvmPresetResolver.ResolveForInstance(settings, string.Empty));
    }

    [Fact]
    public void ResolveForInstance_ExplicitInstanceArguments_Wins()
    {
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-XX:+UseSerialGC",
            JvmPresets =
            {
                new JvmPreset { Id = "p1", Name = "G1", Arguments = "-XX:+UseG1GC" },
            },
            ActiveJvmPresetId = "p1",
        };

        Assert.Equal("-Xmx2G", JvmPresetResolver.ResolveForInstance(settings, "-Xmx2G"));
    }

    [Fact]
    public void ResolveForInstance_NullSettingsAndNullInstanceArguments_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, JvmPresetResolver.ResolveForInstance(null, null));
    }

    [Fact]
    public void Find_IdComparisonIsOrdinalAndBlankIdMatchesNothing()
    {
        var settings = new AppSettings
        {
            JvmPresets =
            {
                new JvmPreset { Id = "ABC", Name = "upper", Arguments = "-Xmx1G" },
            },
        };

        Assert.NotNull(JvmPresetResolver.Find(settings, "ABC"));
        Assert.Null(JvmPresetResolver.Find(settings, "abc"));
        Assert.Null(JvmPresetResolver.Find(settings, "  "));
        Assert.Null(JvmPresetResolver.Find(settings, null));
    }

    // ==================== NormalizeJvmPresets：去重 / 悬空 Id ====================

    [Fact]
    public async Task Load_DuplicatePresetIds_AreMadeUnique()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "jvmPresets": [
                { "id": "dup", "name": "A", "arguments": "-XX:+UseG1GC" },
                { "id": "dup", "name": "B", "arguments": "-XX:+UseZGC" }
              ]
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        var presets = service.Current.JvmPresets;

        Assert.Equal(2, presets.Count);
        Assert.Equal(2, presets.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count());

        // 第一条保留原 Id，后来的重复项换新 Id，内容不动。
        Assert.Equal("dup", presets[0].Id);
        Assert.NotEqual("dup", presets[1].Id);
        Assert.Equal("B", presets[1].Name);
        Assert.Equal("-XX:+UseZGC", presets[1].Arguments);
    }

    [Fact]
    public async Task Load_BlankNameAndNullArguments_AreNormalized()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "jvmPresets": [
                { "id": "p1", "name": "  ", "arguments": null }
              ]
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        var preset = Assert.Single(service.Current.JvmPresets);

        Assert.Equal("p1", preset.Id);
        Assert.Equal("未命名预设", preset.Name);
        Assert.Equal(string.Empty, preset.Arguments);
    }

    [Fact]
    public async Task Load_DanglingActivePresetId_IsReset()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "extraJvmArguments": "-XX:+UseSerialGC",
              "activeJvmPresetId": "gone",
              "jvmPresets": [
                { "id": "keep", "name": "Keep", "arguments": "-Xmx2G" }
              ]
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Null(service.Current.ActiveJvmPresetId);
        Assert.Equal("-XX:+UseSerialGC", JvmPresetResolver.Resolve(service.Current));
    }

    [Fact]
    public async Task Load_ValidActivePresetId_SurvivesNormalization()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "extraJvmArguments": "-XX:+UseSerialGC",
              "activeJvmPresetId": " keep ",
              "jvmPresets": [
                { "id": "keep", "name": "Keep", "arguments": "-Xmx2G" }
              ]
            }
            """);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        // 首尾空白在载入时被裁掉，Id 仍指向同一组预设。
        Assert.Equal("keep", service.Current.ActiveJvmPresetId);
        Assert.Equal("-Xmx2G", JvmPresetResolver.Resolve(service.Current));
    }
}
