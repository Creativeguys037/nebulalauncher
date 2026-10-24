using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// JVM 参数装配（PCL2 <c>McLaunchArgumentsJVM</c> 的 5.3 / 5.4）。
///
/// 覆盖三块：GC 族的版本分支、用户旧 GC 参数的清洗、编码参数与「用户已自定义」的让位。
/// 全部只依赖纯函数，不起 JVM、不读注册表。
/// </summary>
public class CoreLaunch_JvmArgumentTests
{
    // ==================== 验收基准 ====================

    [Fact]
    public void Build_G1OnJava21_ContainsG1AndHeapFreeRatio()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.G1, 21, extraJvmArguments: null);

        Assert.Contains("UseG1GC", arguments, StringComparison.Ordinal);
        Assert.Contains("MaxHeapFreeRatio=40", arguments, StringComparison.Ordinal);
        Assert.Contains("MinHeapFreeRatio=25", arguments, StringComparison.Ordinal);
        Assert.Contains("G1NewSizePercent=20", arguments, StringComparison.Ordinal);
        Assert.Contains("G1ReservePercent=20", arguments, StringComparison.Ordinal);
        Assert.Contains("G1HeapRegionSize=32M", arguments, StringComparison.Ordinal);
        Assert.Contains("MaxGCPauseMillis=50", arguments, StringComparison.Ordinal);

        // ParallelRefProcEnabled 只给 Java 8。
        Assert.DoesNotContain("ParallelRefProcEnabled", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGcArguments_Auto_ReturnsEmpty()
    {
        // 验收判据里的「GcKind.Auto → 空串」指 GC 部分：Build 还负责编码参数，
        // 编码与 GC 选择无关，任何 GcKind 下都按 Java 版本补（见下面的编码用例）。
        Assert.Equal(string.Empty, JvmArgumentBuilder.BuildGcArguments(GcKind.Auto, 21));
        Assert.Equal(string.Empty, JvmArgumentBuilder.BuildGcArguments(GcKind.Auto, 8));
        Assert.Equal(string.Empty, JvmArgumentBuilder.BuildGcArguments(GcKind.Auto, 0));
    }

    [Fact]
    public void Build_AutoKind_HasNoGcFlags()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.Auto, 21, extraJvmArguments: null);

        // 断言具体 GC 族参数，而不是「没有任何 -XX: 」：Auto 不选 GC 这个约束没变，
        // 但启动器现在无条件注入 -XX:-OmitStackTraceInFastThrow（诊断堆栈，见
        // JvmArgumentBuilder 类注释），用「无 -XX: 」描述会把无关参数也算成 GC。
        Assert.DoesNotContain("UseG1GC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UseZGC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UseParallelGC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UseSerialGC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UnlockExperimentalVMOptions", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UserHasSerialGc_OutputHasNoSerialGc()
    {
        var arguments = JvmArgumentBuilder.Build(
            GcKind.G1,
            21,
            "-XX:+UseSerialGC -Dfml.ignoreInvalidMinecraftCertificates=True");

        Assert.DoesNotContain("UseSerialGC", arguments, StringComparison.Ordinal);
        Assert.Contains("UseG1GC", arguments, StringComparison.Ordinal);
    }

    // ==================== GC 族：G1 ====================

    [Fact]
    public void Build_G1OnJava8_AddsParallelRefProcWithoutHeapFreeRatio()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.G1, 8, null);

        Assert.Contains("UseG1GC", arguments, StringComparison.Ordinal);
        Assert.Contains("ParallelRefProcEnabled", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeapFreeRatio", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_G1OnJava7_OnlyBaseFlags()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.G1, 7, null);

        Assert.Contains("UseG1GC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ParallelRefProcEnabled", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeapFreeRatio", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ZGenerational", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_G1OnJava6_ReturnsNoGcFlags()
    {
        // Java 6 没有 G1：加 -XX:+UseG1GC 会让 JVM 直接拒绝启动。
        Assert.Equal(string.Empty, JvmArgumentBuilder.BuildGcArguments(GcKind.G1, 6));
    }

    [Fact]
    public void Build_G1OnUnknownJava_TreatedAsModernWithoutJava8OnlyFlags()
    {
        // 主版本未知（探测失败）时按现代 JVM 处理：基础参数与堆空闲比在 Java 7+ 都合法，
        // 只有「Java 8 专属」的 ParallelRefProcEnabled 不猜，避免多出无意义的参数。
        var arguments = JvmArgumentBuilder.BuildGcArguments(GcKind.G1, 0);

        Assert.Contains("UseG1GC", arguments, StringComparison.Ordinal);
        Assert.Contains("MaxHeapFreeRatio", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ParallelRefProcEnabled", arguments, StringComparison.Ordinal);
    }

    // ==================== GC 族：ZGC ====================

    [Fact]
    public void Build_ZgcOnJava21_AddsGenerational()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.Z, 21, null);

        Assert.Contains("UseZGC", arguments, StringComparison.Ordinal);
        Assert.Contains("ZGenerational", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ZgcOnJava22_AddsGenerational()
    {
        Assert.Contains("ZGenerational", JvmArgumentBuilder.Build(GcKind.Z, 22, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ZgcOnJava17_NoGenerational()
    {
        var arguments = JvmArgumentBuilder.BuildGcArguments(GcKind.Z, 17);

        Assert.Contains("UseZGC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ZGenerational", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ZgcOnJava23_NoGenerational()
    {
        // 23 起分代是默认值，再传 -XX:+ZGenerational 会被 JVM 告警。
        Assert.DoesNotContain("ZGenerational", JvmArgumentBuilder.BuildGcArguments(GcKind.Z, 23), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ZgcOnJava14_ReturnsNoGcFlags()
    {
        Assert.Equal(string.Empty, JvmArgumentBuilder.BuildGcArguments(GcKind.Z, 14));
    }

    // ==================== GC 族：Parallel / Serial ====================

    [Fact]
    public void Build_Parallel_AddsUseParallelGc()
    {
        Assert.Equal("-XX:+UseParallelGC", JvmArgumentBuilder.BuildGcArguments(GcKind.Parallel, 21));
    }

    [Fact]
    public void Build_Serial_AddsUseSerialGc()
    {
        Assert.Equal("-XX:+UseSerialGC", JvmArgumentBuilder.BuildGcArguments(GcKind.Serial, 21));
    }

    // ==================== 清洗用户旧参数 ====================

    [Fact]
    public void StripGcArguments_RemovesAllGcFamilyFlags()
    {
        const string raw =
            "-XX:+UseSerialGC -XX:MaxHeapFreeRatio=70 -XX:+ZGenerational -XX:G1HeapRegionSize=8M "
            + "-Dfml.ignorePatchDiscrepancies=True";

        var stripped = JvmArgumentBuilder.StripGcArguments(raw);

        Assert.DoesNotContain("UseSerialGC", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeapFreeRatio", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("ZGenerational", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("G1HeapRegionSize", stripped, StringComparison.Ordinal);
        Assert.Contains("-Dfml.ignorePatchDiscrepancies=True", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void StripGcArguments_KeepsQuotedArgumentsIntact()
    {
        const string raw = "-Djava.library.path=\"C:\\Game Dir\\natives\" -XX:+UseG1GC";

        var stripped = JvmArgumentBuilder.StripGcArguments(raw);

        Assert.Equal("-Djava.library.path=\"C:\\Game Dir\\natives\"", stripped);
    }

    [Fact]
    public void StripGcArguments_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, JvmArgumentBuilder.StripGcArguments(null));
        Assert.Equal(string.Empty, JvmArgumentBuilder.StripGcArguments("   "));
    }

    [Fact]
    public void StripGcArguments_KeepsUnrelatedXxFlags()
    {
        const string raw = "-XX:-OmitStackTraceInFastThrow -XX:+UseG1GC";

        var stripped = JvmArgumentBuilder.StripGcArguments(raw);

        Assert.Contains("-XX:-OmitStackTraceInFastThrow", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("UseG1GC", stripped, StringComparison.Ordinal);
    }

    // ==================== 编码参数 ====================

    [Fact]
    public void BuildEncodingArguments_Java21_UsesRenamedKeysAndCompatFileEncoding()
    {
        var arguments = JvmArgumentBuilder.BuildEncodingArguments(21, null);

        Assert.Contains("-Dstdout.encoding=", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dstderr.encoding=", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("sun.stdout.encoding", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dfile.encoding=COMPAT", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEncodingArguments_Java17_UsesSunKeysWithoutFileEncoding()
    {
        var arguments = JvmArgumentBuilder.BuildEncodingArguments(17, null);

        Assert.Contains("-Dsun.stdout.encoding=", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dsun.stderr.encoding=", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("file.encoding", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEncodingArguments_UserAlreadySetFileEncoding_DoesNotOverride()
    {
        var arguments = JvmArgumentBuilder.BuildEncodingArguments(21, "-Dfile.encoding=UTF-8");

        Assert.DoesNotContain("COMPAT", arguments, StringComparison.Ordinal);
        Assert.Contains("-Dstdout.encoding=", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEncodingArguments_UserAlreadySetStdoutEncoding_SkipsBoth()
    {
        var arguments = JvmArgumentBuilder.BuildEncodingArguments(21, "-Dsun.stdout.encoding=UTF-8");

        Assert.DoesNotContain("stdout.encoding", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr.encoding", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeEncodingName_GbkCodePage_MapsToGb18030()
    {
        Assert.Equal("GB18030", JvmArgumentBuilder.NormalizeEncodingName(936));
    }

    [Fact]
    public void NormalizeEncodingName_KnownCodePages()
    {
        Assert.Equal("Big5", JvmArgumentBuilder.NormalizeEncodingName(950));
        Assert.Equal("MS932", JvmArgumentBuilder.NormalizeEncodingName(932));
        Assert.Equal("windows-1252", JvmArgumentBuilder.NormalizeEncodingName(1252));
        Assert.Equal("UTF-8", JvmArgumentBuilder.NormalizeEncodingName(65001));
    }

    [Fact]
    public void NormalizeEncodingName_UnknownCodePage_FallsBackToUtf8()
    {
        Assert.Equal("UTF-8", JvmArgumentBuilder.NormalizeEncodingName(0));
        Assert.Equal("UTF-8", JvmArgumentBuilder.NormalizeEncodingName(437));
    }

    [Fact]
    public void ResolveSystemEncodingName_NotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(JvmArgumentBuilder.ResolveSystemEncodingName()));
    }

    // ==================== 组合 ====================

    [Fact]
    public void Build_AutoKindWithNoJava_FallsBackToEncodingOnly()
    {
        // Java 未知时不加任何版本专属 GC/编码项之外的项，至少保证参数串里没有半截项。
        // 与上一条同理：-XX:-OmitStackTraceInFastThrow 与 Java 版本无关，允许出现；
        // GC 族与 24+ 专属的压缩对象头必须缺席。
        var arguments = JvmArgumentBuilder.Build(GcKind.Auto, 0, null);

        Assert.DoesNotContain("UseG1GC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UseZGC", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UseCompactObjectHeaders", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("UnlockExperimentalVMOptions", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_G1OnJava21_NoLeadingOrTrailingSpaces()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.G1, 21, null);

        Assert.Equal(arguments.Trim(), arguments);
        Assert.False(arguments.StartsWith(' '), "参数串不应以空格开头");
    }
}
