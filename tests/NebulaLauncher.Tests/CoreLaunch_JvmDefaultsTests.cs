using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 启动器默认注入的 JVM 参数（缺口 G2 / G4 / G6）。
///
/// 覆盖四组判据：
/// <list type="number">
/// <item>G2：PCL2 <c>LaunchAdvanceJvm</c> 的四条默认值是否注入，以及设置开关与用户显式覆盖的让位规则。</item>
/// <item>G4：authlib 注入片段在 Windows 上追加 <c>WINDOWS-ROOT</c>、在别的平台必须不追加。</item>
/// <item>G6：压缩对象头只在 Java 24+ 出现，24 需要实验解锁、25 起不需要，未知版本不加。</item>
/// <item>GC 清理正则不能把上述新参数当成 GC 族删掉。</item>
/// </list>
///
/// 全部是纯函数用例：不启动 JVM、不读注册表、不碰文件。
/// </summary>
public class CoreLaunch_JvmDefaultsTests
{
    // ==================== G2：默认四条 ====================

    [Fact]
    public void Build_Default_ContainsAllPclJvmDefaults()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.Auto, 21, extraJvmArguments: null);

        Assert.Contains("-XX:-OmitStackTraceInFastThrow", arguments, StringComparison.Ordinal);
        Assert.Contains("-Djdk.lang.Process.allowAmbiguousCommands=True", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dfml.ignoreInvalidMinecraftCertificates=True", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dfml.ignorePatchDiscrepancies=True", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RelaxForgeChecksOff_KeepsStackTraceFlagOnly()
    {
        // 关掉开关只该撤掉「放宽校验」那三条；关掉异常堆栈诊断不在这个开关的语义里，
        // 它是排查能力（崩溃分析要靠带堆栈的 NPE），与信任本地文件无关。
        var arguments = JvmArgumentBuilder.Build(GcKind.Auto, 21, extraJvmArguments: null, relaxForgeChecks: false);

        Assert.Contains("-XX:-OmitStackTraceInFastThrow", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("allowAmbiguousCommands", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("fml.ignoreInvalidMinecraftCertificates", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("fml.ignorePatchDiscrepancies", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UserSetForgeOption_DoesNotOverrideIt()
    {
        // 用户显式写 False 就是要严格校验：启动器不能再补一条 True 把它顶掉
        // （Build 只产出启动器生成段，用户原文由调用方单独拼接，所以这里断言「不补」）。
        // 只让位同名键，另外两条仍然补上——开关的作用域必须按参数、不按整组。
        var arguments = JvmArgumentBuilder.Build(
            GcKind.Auto,
            21,
            "-Dfml.ignorePatchDiscrepancies=False");

        Assert.DoesNotContain("fml.ignorePatchDiscrepancies", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dfml.ignoreInvalidMinecraftCertificates=True", arguments, StringComparison.Ordinal);
        Assert.Contains("-Djdk.lang.Process.allowAmbiguousCommands=True", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCompatibilityArguments_NoUserOverride_ReturnsFourTokens()
    {
        var arguments = JvmArgumentBuilder.BuildCompatibilityArguments(relaxForgeChecks: true, extraJvmArguments: null);

        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, tokens.Length);
        Assert.Equal(JvmArgumentBuilder.FastThrowDiagnosticsOption, tokens[0]);
    }

    [Fact]
    public void BuildCompatibilityArguments_RelaxOff_ReturnsOnlyStackTraceFlag()
    {
        Assert.Equal(
            JvmArgumentBuilder.FastThrowDiagnosticsOption,
            JvmArgumentBuilder.BuildCompatibilityArguments(relaxForgeChecks: false, extraJvmArguments: null));
    }

    // ==================== G4：authlib 信任库 ====================

    [Fact]
    public void BuildTrustStoreArguments_OnWindows_AddsWindowsRoot()
    {
        Assert.Equal(
            "-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT",
            AuthlibInjectorService.BuildTrustStoreArguments(isWindows: true));
    }

    [Fact]
    public void BuildTrustStoreArguments_OffWindows_ReturnsEmpty()
    {
        // WINDOWS-ROOT 是 SunJSSE 的 Windows 专有 KeyStore 类型；macOS / Linux 上传了它
        // JVM 会抛 KeyStoreException 直接拒绝启动，比证书校验失败严重得多。
        Assert.Equal(string.Empty, AuthlibInjectorService.BuildTrustStoreArguments(isWindows: false));
    }

    [Fact]
    public void ComposeArguments_KeepsPclThreeArgumentPrefix()
    {
        var composed = AuthlibInjectorService.ComposeArguments(
            @"C:\plain\authlib-injector-1.2.8.jar",
            "https://example.com/api/yggdrasil",
            "Zm9v");

        var tokens = composed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("-javaagent:C:\\plain\\authlib-injector-1.2.8.jar=https://example.com/api/yggdrasil", tokens[0]);
        Assert.Equal("-Dauthlibinjector.yggdrasil.prefetched=Zm9v", tokens[1]);
        Assert.Equal("-Dauthlibinjector.side=client", tokens[2]);
        Assert.Equal(OperatingSystem.IsWindows() ? 4 : 3, tokens.Length);
    }

    // ==================== G6：压缩对象头 ====================

    [Fact]
    public void BuildCompactObjectHeadersArguments_Java24_NeedsExperimentalUnlock()
    {
        var arguments = JvmArgumentBuilder.BuildCompactObjectHeadersArguments(24, extraJvmArguments: null);

        // 24 上它是实验特性：只写 UseCompactObjectHeaders 会被 JVM 判 Unrecognized VM option。
        Assert.Equal("-XX:+UnlockExperimentalVMOptions -XX:+UseCompactObjectHeaders", arguments);
    }

    [Fact]
    public void BuildCompactObjectHeadersArguments_Java25_NoUnlockNeeded()
    {
        // 25 起转正（JEP 519）：再传解锁开关没有意义，输出保持最小。
        Assert.Equal(
            "-XX:+UseCompactObjectHeaders",
            JvmArgumentBuilder.BuildCompactObjectHeadersArguments(25, extraJvmArguments: null));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(21)]
    [InlineData(17)]
    [InlineData(8)]
    [InlineData(0)]
    public void BuildCompactObjectHeadersArguments_BelowJava24_ReturnsEmpty(int javaMajorVersion)
    {
        // 0 是「探测失败」：宁可少一点性能收益，也不能让一个不存在的标志把 JVM 顶死。
        Assert.Equal(
            string.Empty,
            JvmArgumentBuilder.BuildCompactObjectHeadersArguments(javaMajorVersion, extraJvmArguments: null));
    }

    [Theory]
    [InlineData(GcKind.Auto)]
    [InlineData(GcKind.G1)]
    [InlineData(GcKind.Z)]
    [InlineData(GcKind.Parallel)]
    [InlineData(GcKind.Serial)]
    public void Build_Java24_AddsCompactObjectHeadersForEveryGcKind(GcKind gcKind)
    {
        // 压缩对象头与 GC 选择无关，只跟 Java 版本有关：只加在 G1 分支会让
        // 「Auto / ZGC / Parallel 用户拿不到这个收益」，是本次要修的缺口。
        var arguments = JvmArgumentBuilder.Build(gcKind, 24, extraJvmArguments: null);

        Assert.Contains("UseCompactObjectHeaders", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Java24G1_UnlockExperimentalVmOptionsAppearsOnce()
    {
        // G1 分支与压缩对象头分支都需要 UnlockExperimentalVMOptions，去重后只该出现一次。
        var arguments = JvmArgumentBuilder.Build(GcKind.G1, 24, extraJvmArguments: null);

        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = tokens.Count(token => token == "-XX:+UnlockExperimentalVMOptions");

        Assert.Equal(1, count);
        Assert.Contains("-XX:+UseCompactObjectHeaders", tokens);
    }

    [Fact]
    public void Build_Java23_NoCompactObjectHeaders()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.G1, 23, extraJvmArguments: null);

        Assert.DoesNotContain("UseCompactObjectHeaders", arguments, StringComparison.Ordinal);
    }

    // ==================== GC 清理正则与新参数的边界 ====================

    [Fact]
    public void StripGcArguments_KeepsCompactObjectHeaders()
    {
        // PCL2 的清理正则含 UseCompactObjectHeaders（每次按版本重建），Nebula 不含它：
        // 清掉又不补（Java < 24 或用户关掉）等于吞掉用户显式配置。
        var stripped = JvmArgumentBuilder.StripGcArguments(
            "-XX:+UseCompactObjectHeaders -XX:+UseG1GC -Dfml.ignorePatchDiscrepancies=True");

        Assert.Contains("-XX:+UseCompactObjectHeaders", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("UseG1GC", stripped, StringComparison.Ordinal);
        Assert.Contains("-Dfml.ignorePatchDiscrepancies=True", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void StripGcArguments_KeepsStackTraceAndAmbiguousCommandFlags()
    {
        var stripped = JvmArgumentBuilder.StripGcArguments(
            "-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -XX:+UseZGC");

        Assert.Contains("-XX:-OmitStackTraceInFastThrow", stripped, StringComparison.Ordinal);
        Assert.Contains("-Djdk.lang.Process.allowAmbiguousCommands=True", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("UseZGC", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Java24_UserAlreadyWroteCompactObjectHeaders_NotDuplicated()
    {
        // 用户自己写了这条（Java 24+ 上的常见做法）时，启动器只补自己缺的解锁开关，
        // 不再追加第二条 UseCompactObjectHeaders——否则「查看启动命令」里同一条参数出现两次。
        var launched = JvmArgumentBuilder.Build(
            GcKind.Auto,
            24,
            "-XX:+UseCompactObjectHeaders");

        Assert.DoesNotContain("UseCompactObjectHeaders", launched, StringComparison.Ordinal);
        Assert.Contains("UnlockExperimentalVMOptions", launched, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Java24_UserExplicitlyDisabledCompactObjectHeaders_Respected()
    {
        // 显式写负号表示关闭：ContainsOption 同样命中，启动器不加正向开关。
        var launched = JvmArgumentBuilder.Build(
            GcKind.Auto,
            24,
            "-XX:-UseCompactObjectHeaders");

        Assert.DoesNotContain("+UseCompactObjectHeaders", launched, StringComparison.Ordinal);
    }

    // ==================== 设置字段与 Clone ====================

    [Fact]
    public void AppSettings_RelaxForgeChecks_DefaultsOn()
    {
        Assert.True(new AppSettings().RelaxForgeChecks);
    }

    [Fact]
    public void AppSettings_Clone_CopiesRelaxForgeChecks()
    {
        // 漏拷的表现：设置页关掉 → 保存 → 重启后又打开了。
        var settings = new AppSettings { RelaxForgeChecks = false };

        Assert.False(settings.Clone().RelaxForgeChecks);
    }
}