using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「启动前把 javaw.exe 注册为高性能 GPU」的判据部分。
///
/// 真正写注册表的那一步不在这里测：它依赖 HKCU 的真实键，
/// 在 CI 上跑会把测试机的显卡设置改掉。这里只测写之前决定「要不要写 / 写什么」的纯逻辑。
/// </summary>
public class CorePlatforms_GpuPreferenceTests
{
    [Fact]
    public void 高性能值必须是GpuPreference等于2且带分号()
    {
        // 值名与值内容都要与 Windows「图形设置」自己写的逐字节一致，
        // 否则系统认不出来，功能等于没做。末尾分号是段分隔符，不能省。
        Assert.Equal(@"Software\Microsoft\DirectX\UserGpuPreferences", PlatformService.GpuPreferenceSubKey);
        Assert.Equal("GpuPreference=2;", PlatformService.GpuPreferenceHighPerformance);
    }

    [Fact]
    public void 值名规范化为绝对路径()
    {
        var relative = Path.Combine(".", "runtime", "bin", "javaw.exe");

        var normalized = PlatformService.NormalizeGpuPreferenceValueName(relative);

        // Windows 按绝对路径匹配进程：相对路径写进去不报错，但永远不会生效。
        Assert.True(Path.IsPathRooted(normalized));
        Assert.EndsWith("javaw.exe", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void 值名首尾空白被裁掉()
    {
        var path = Path.Combine(Path.GetTempPath(), "jdk", "bin", "javaw.exe");

        Assert.Equal(PlatformService.NormalizeGpuPreferenceValueName(path),
            PlatformService.NormalizeGpuPreferenceValueName("  " + path + "  "));
    }

    [Fact]
    public void 空路径返回空串()
    {
        // 空串是「不要写」的信号；调用方据此在碰注册表之前就返回 false。
        Assert.Equal(string.Empty, PlatformService.NormalizeGpuPreferenceValueName(string.Empty));
        Assert.Equal(string.Empty, PlatformService.NormalizeGpuPreferenceValueName("   "));
        Assert.Equal(string.Empty, PlatformService.NormalizeGpuPreferenceValueName(null!));
    }

    [Fact]
    public void 已存在高性能段时判定为满足()
    {
        Assert.True(PlatformService.IsGpuPreferenceSatisfied("GpuPreference=2;"));
        // 系统会在同一个值里追加别的段，写成无空格的紧凑形式。
        Assert.True(PlatformService.IsGpuPreferenceSatisfied("GpuPreference=2;SwapEffectUpgradeEnable=1;"));
        Assert.True(PlatformService.IsGpuPreferenceSatisfied(" SwapEffectUpgradeEnable=1 ; GpuPreference=2 ; "));
    }

    [Fact]
    public void 低性能或其它值判定为不满足()
    {
        Assert.False(PlatformService.IsGpuPreferenceSatisfied(null));
        Assert.False(PlatformService.IsGpuPreferenceSatisfied(string.Empty));
        Assert.False(PlatformService.IsGpuPreferenceSatisfied("   "));

        // 1 = 省电（核显），3 = 由应用决定。两者都要被改写成 2。
        Assert.False(PlatformService.IsGpuPreferenceSatisfied("GpuPreference=1;"));
        Assert.False(PlatformService.IsGpuPreferenceSatisfied("GpuPreference=3;"));

        // 前缀相同但值不同，不能被当成满足。
        Assert.False(PlatformService.IsGpuPreferenceSatisfied("GpuPreference=20;"));
        Assert.False(PlatformService.IsGpuPreferenceSatisfied("NotGpuPreference=2;"));
    }
}