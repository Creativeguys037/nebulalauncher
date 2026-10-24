using System.IO.Compression;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 崩溃现场的两个补齐：<c>hs_err_pid*.log</c> 纳入归因、外部报告手动导入。
///
/// 覆盖三类判据：
/// <list type="number">
/// <item>hs_err 解析：信号 / 异常类型、Problematic frame、native 模块名；</item>
/// <item>6 条 hs_err 专属规则各自命中（显卡驱动 / 原生内存 / 栈溢出 / JNI / 未知模块越界 / 报告不完整）；</item>
/// <item>外部报告导入：txt / zip 取文件、超大文件头尾截断、来源文件名标注、读取失败不静默。</item>
/// </list>
/// </summary>
public class CoreLaunch_HsErrTests
{
    /// <summary>完整 hs_err 样本：与 <c>.probe\crash-samples\hs_err_pid12345.log</c> 同内容。</summary>
    private const string NvidiaHsErr = """
        #
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb3a1b2c3d, pid=12345, tid=14568
        #
        # JRE version: Java(TM) SE Runtime Environment (17.0.9+9) (build 17.0.9+9-LTS-211)
        # Java VM: Java HotSpot(TM) 64-Bit Server VM (17.0.9+9-LTS-211, mixed mode, sharing, tiered, compressed oops, compressed class ptrs, g1 gc, windows-amd64)
        # Problematic frame:
        # C  [nvoglv64.dll+0x9a1234]
        #
        # Core dump will be written. Default location: C:\Users\NebulaPilot\AppData\Local\Temp\hs_err_pid12345.mdmp
        #
        # If you would like to submit a bug report, please visit:
        #   https://bugreport.java.com/bugreport/crash.jsp
        #

        ---------------  S U M M A R Y ------------

        Command Line: -Xmx4096m -Djava.library.path=C:\Users\NebulaPilot\AppData\Roaming\.minecraft\versions\1.20.1-forge\natives net.minecraft.client.main.Main

        Host: Intel(R) Core(TM) i7-12700K, 12 cores, 31G, Windows 10 , 10.0, amd64
        Time: Thu Oct 22 10:00:00 2026
        Elapsed time: 47.213 seconds (JVM running for 62.331)

        ---------------  T H R E A D  ---------------

        Current thread (0x000001f8c1a2b000):  JavaThread "Render thread" [_thread_in_native, id=14568, stack(0x000000a1c0000000,0x000000a1c0100000)]

        Stack: [0x000000a1c0000000,0x000000a1c0100000],  sp=0x000000a1c00fe8a0,  free space=1021k
        Native frames: (J=compiled Java code, j=interpreted, Vv=VM code, C=native code)
        C  [nvoglv64.dll+0x9a1234]

        Java frames: (J=compiled Java code, j=interpreted, Vv=VM code)
        j  com.mojang.blaze3d.platform.GlStateManager._glDrawElements(IJ)V+0
        j  net.minecraft.client.renderer.LevelRenderer.renderChunkLayer(Lnet/minecraft/client/renderer/RenderType;Lcom/mojang/blaze3d/vertex/PoseStack;DDD)V+412
        j  net.minecraft.client.renderer.LevelRenderer.renderLevel(Lcom/mojang/blaze3d/vertex/PoseStack;FJZ)V+1024
        """;

    /// <summary>Forge 客户端的 crash report 样本：含 Description 与模组堆栈、Mod List。</summary>
    private const string ForgeCrashReport = """
        ---- Minecraft Crash Report ----
        // Don't be sad, have a hug! <3

        Time: 2026-10-22 10:00:00
        Description: Rendering overlay

        java.lang.NullPointerException: Cannot invoke "net.minecraft.world.item.ItemStack.isEmpty()" because "stack" is null
        	at com.example.mymod.client.HudOverlay.render(HudOverlay.java:42)
        	at net.minecraft.client.gui.Gui.render(Gui.java:312)
        	at net.minecraft.client.Minecraft.runTick(Minecraft.java:1102)
        	at com.example.mymod.core.HudTicker.tick(HudTicker.java:18)

        A detailed walkthrough of the error, its code path and all known details is as follows:
        ---------------------------------------------------------------------------------------

        -- Head --
        Thread: Render thread
        Stacktrace:
        	at com.example.mymod.client.HudOverlay.render(HudOverlay.java:42)
        	at net.minecraft.client.gui.Gui.render(Gui.java:312)

        -- System Details --
        Details:
        	Minecraft Version: 1.20.1
        	Minecraft Version ID: 1.20.1-forge-47.2.20
        	Operating System: Windows 10 (amd64) version 10.0
        	Java Version: 17.0.9, Microsoft
        	Java VM Version: OpenJDK 64-Bit Server VM (mixed mode), Microsoft
        	Memory: 1835522136 bytes (1750 MiB) / 4294967296 bytes (4096 MiB) up to 4294967296 bytes (4096 MiB)
        	Mod List:
        		minecraft (minecraft-1.20.1-client.jar)
        		examplemod (examplemod-2.1.0.jar)
        """;

    // ==================== hs_err 解析 ====================

    [Fact]
    public void ParseHsErr_NvidiaAccessViolation_ExtractsSignalFrameAndModule()
    {
        var info = CrashAnalyzer.ParseHsErr(NvidiaHsErr);

        Assert.NotNull(info);
        Assert.Equal("EXCEPTION_ACCESS_VIOLATION", info.Signal);
        Assert.Equal("C  [nvoglv64.dll+0x9a1234]", info.ProblematicFrame);
        Assert.Equal("nvoglv64.dll", info.NativeModule);
    }

    [Fact]
    public void ParseHsErr_LinuxSigsegv_ExtractsSignalAndModule()
    {
        var info = CrashAnalyzer.ParseHsErr("""
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  SIGSEGV (0xb) at pc=0x00007f2c8a1b2c3d, pid=4242, tid=4243
            #
            # Problematic frame:
            # C  [libnvidia-glcore.so.545.29.06+0x1a2b3c]
            #
            """);

        Assert.NotNull(info);
        Assert.Equal("SIGSEGV", info.Signal);
        Assert.Equal("libnvidia-glcore.so.545.29.06", info.NativeModule);
    }

    [Fact]
    public void ParseHsErr_PlainLog_ReturnsNull()
    {
        Assert.Null(CrashAnalyzer.ParseHsErr(null));
        Assert.Null(CrashAnalyzer.ParseHsErr(string.Empty));
        Assert.Null(CrashAnalyzer.ParseHsErr("[12:00:00] [main/INFO]: Loading 42 mods"));
    }

    [Fact]
    public void Analyze_ThreeInputs_CombinesTailReportAndHsErr()
    {
        var report = CrashAnalyzer.Analyze(
            "[12:00:03] [Render thread/INFO]: Stopping!",
            ForgeCrashReport,
            NvidiaHsErr);

        Assert.True(report.IsCrash);
        Assert.Contains("EXCEPTION_ACCESS_VIOLATION", report.Summary, StringComparison.Ordinal);
        Assert.Contains("nvoglv64.dll", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Suggestions, s => s.Title == "疑似显卡驱动崩溃");
    }

    [Fact]
    public void Analyze_WithoutHsErr_DoesNotThrowAndStaysNotCrash()
    {
        // hs_err 不存在时走的是 null 分支：不能抛异常，也不能凭空判成崩溃。
        var report = CrashAnalyzer.Analyze(null, null, null);

        Assert.False(report.IsCrash);
        Assert.Empty(report.Suggestions);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));

        var healthy = CrashAnalyzer.Analyze(
            "[12:00:00] [main/INFO]: Loading 42 mods\r\n[12:00:03] [Render thread/INFO]: Stopping!",
            null,
            null);

        Assert.False(healthy.IsCrash);
        Assert.Empty(healthy.Suggestions);
    }

    // ==================== 6 条 hs_err 专属规则 ====================

    [Fact]
    public void Analyze_HsErrNvidiaFrame_HitsGraphicsRule()
    {
        var report = CrashAnalyzer.Analyze(null, null, NvidiaHsErr);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("疑似显卡驱动崩溃", suggestion.Title);
        Assert.Contains("驱动", suggestion.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_HsErrNativeMemory_HitsNativeMemoryRule()
    {
        var report = CrashAnalyzer.Analyze(null, null, """
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  Out of Memory Error (os_windows.cpp:3601), pid=4242, tid=4243
            #
            # Native memory allocation (malloc) failed to allocate 1048576 bytes for Chunk::new
            #
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "疑似原生内存耗尽");
    }

    [Fact]
    public void Analyze_HsErrStackOverflow_HitsStackOverflowRule()
    {
        var report = CrashAnalyzer.Analyze(null, null, """
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  Internal Error (stack overflow), pid=4242, tid=4243
            #
            # Current thread (0x0000021a3c00b000):  JavaThread "Server thread" [_thread_in_Java]
            # StackOverflowError: null
            #
            """);

        Assert.True(report.IsCrash);

        // 兜底规则「报告不完整」必须被 stack overflow 专属结论挡住，不能同时报两条。
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("疑似栈溢出", suggestion.Title);
    }

    [Fact]
    public void Analyze_HsErrMissingJniSymbol_HitsJniRule()
    {
        var report = CrashAnalyzer.Analyze(null, null, """
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  Internal Error (jni_findClass), pid=4242, tid=4243
            #
            # Problematic frame:
            # C  [jvm.dll+0x1a2b3c]
            #
            # JNI_ERR in thread "Render thread"
            #
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("疑似 JNI 本地符号缺失", suggestion.Title);
    }

    [Fact]
    public void Analyze_HsErrUnknownModuleAccessViolation_HitsUnknownModuleRule()
    {
        var report = CrashAnalyzer.Analyze(null, null, """
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb3a1b2c3d, pid=4242, tid=4243
            #
            # Problematic frame:
            # C  [win32u.dll+0x2b1c]
            #
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("疑似未知 native 模块访问越界", suggestion.Title);
        Assert.Contains("输入法", suggestion.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_HsErrWithoutProblematicFrame_HitsIncompleteRule()
    {
        var report = CrashAnalyzer.Analyze(null, null, """
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  Internal Error (os_windows.cpp:3601), pid=4242, tid=4243
            #
            # Core dump will be written. Default location: C:\Users\NebulaPilot\hs_err_pid4242.mdmp
            #
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("疑似 hs_err 报告不完整", suggestion.Title);
        Assert.Contains("Problematic frame", suggestion.Detail, StringComparison.Ordinal);
    }

    // ==================== 外部报告导入：txt ====================

    [Fact]
    public void AnalyzeExternalReport_CrashReportFile_MarksSourceAndFindsMod()
    {
        using var paths = new CoreAssetsPaths();
        var path = Path.Combine(paths.RootDirectory, "crash-2026-10-22_10.00.00-client.txt");
        File.WriteAllText(path, ForgeCrashReport);

        var report = CrashAnalyzer.AnalyzeExternalReport(path);

        Assert.True(report.IsCrash);
        Assert.StartsWith("来源：crash-2026-10-22_10.00.00-client.txt", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Suggestions, s => s.Title == "疑似模组导致崩溃");
        Assert.Contains(report.Suggestions, s => s.Detail.Contains("examplemod", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeExternalReport_HealthyLog_ReturnsNotCrashWithSource()
    {
        using var paths = new CoreAssetsPaths();
        var path = Path.Combine(paths.RootDirectory, "normal.log");
        File.WriteAllText(path, """
            [12:00:00] [main/INFO]: Loading 42 mods
            [12:00:03] [Render thread/INFO]: Stopping!
            """);

        var report = CrashAnalyzer.AnalyzeExternalReport(path);

        Assert.False(report.IsCrash);
        Assert.Empty(report.Suggestions);
        Assert.Contains("normal.log", report.Summary, StringComparison.Ordinal);
    }

    // ==================== 外部报告导入：zip ====================

    [Fact]
    public void AnalyzeExternalReport_Zip_PrefersCrashTxtEntry()
    {
        using var paths = new CoreAssetsPaths();
        var zipPath = Path.Combine(paths.RootDirectory, "玩家发来的崩溃.zip");

        // hs_err 排在前、crash txt 放在子目录里：仍应优先取 crash-*.txt。
        WriteZip(
            zipPath,
            ("hs_err_pid9001.log", NvidiaHsErr),
            ("logs/crash-2026-10-22_10.00.00-client.txt", ForgeCrashReport));

        var report = CrashAnalyzer.AnalyzeExternalReport(zipPath);

        Assert.True(report.IsCrash);
        Assert.Contains("crash-2026-10-22_10.00.00-client.txt", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("hs_err_pid9001.log", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Suggestions, s => s.Title == "疑似模组导致崩溃");
    }

    [Fact]
    public void AnalyzeExternalReport_ZipWithOnlyHsErr_ReadsHsErrEntry()
    {
        using var paths = new CoreAssetsPaths();
        var zipPath = Path.Combine(paths.RootDirectory, "hs-err-only.zip");
        WriteZip(zipPath, ("hs_err_pid12345.log", NvidiaHsErr));

        var report = CrashAnalyzer.AnalyzeExternalReport(zipPath);

        Assert.True(report.IsCrash);
        Assert.Contains("hs_err_pid12345.log", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Suggestions, s => s.Title == "疑似显卡驱动崩溃");
    }

    [Fact]
    public void AnalyzeExternalReport_ZipWithoutReport_ReturnsClearError()
    {
        using var paths = new CoreAssetsPaths();
        var zipPath = Path.Combine(paths.RootDirectory, "resources.zip");
        WriteZip(zipPath, ("readme.md", "not a crash report"));

        var report = CrashAnalyzer.AnalyzeExternalReport(zipPath);

        Assert.False(report.IsCrash);
        Assert.Empty(report.Suggestions);
        Assert.Contains("压缩包里没有", report.Summary, StringComparison.Ordinal);
        Assert.Contains("resources.zip", report.Summary, StringComparison.Ordinal);
    }

    // ==================== 读取失败与超大文件截断 ====================

    [Fact]
    public void AnalyzeExternalReport_MissingFile_ReturnsClearError()
    {
        using var paths = new CoreAssetsPaths();
        var path = Path.Combine(paths.RootDirectory, "not-there.txt");

        var report = CrashAnalyzer.AnalyzeExternalReport(path);

        Assert.False(report.IsCrash);
        Assert.Empty(report.Suggestions);
        Assert.Contains("找不到报告文件", report.Summary, StringComparison.Ordinal);
        Assert.Contains("not-there.txt", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadExternalReport_NullPath_ReturnsClearError()
    {
        var content = CrashAnalyzer.ReadExternalReport(null);

        Assert.False(content.IsSuccess);
        Assert.Contains("没有选择报告文件", content.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadExternalReport_LargeFile_TruncatesHeadTail()
    {
        using var paths = new CoreAssetsPaths();
        var path = Path.Combine(paths.RootDirectory, "huge.log");
        var lines = new List<string>(2600) { "HEAD-MARKER" };

        for (var i = 0; i < 2598; i++)
        {
            lines.Add($"[12:00:00] [Render thread/INFO]: tick {i}");
        }

        lines.Add("TAIL-MARKER");
        File.WriteAllLines(path, lines);

        var content = CrashAnalyzer.ReadExternalReport(path);

        Assert.True(content.IsSuccess);
        Assert.True(content.Truncated);
        Assert.Contains("HEAD-MARKER", content.Text, StringComparison.Ordinal);
        Assert.Contains("TAIL-MARKER", content.Text, StringComparison.Ordinal);
        Assert.Contains("此处省略 600 行", content.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tick 1700", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeExternalReport_LargeFile_KeepsHeadCause()
    {
        using var paths = new CoreAssetsPaths();
        var path = Path.Combine(paths.RootDirectory, "huge-crash.txt");
        var lines = new List<string>(2600) { "java.lang.OutOfMemoryError: Java heap space" };

        for (var i = 0; i < 2599; i++)
        {
            lines.Add($"[12:00:00] [Render thread/INFO]: tick {i}");
        }

        File.WriteAllLines(path, lines);

        var report = CrashAnalyzer.AnalyzeExternalReport(path);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "内存不足");
        Assert.Contains("仅分析头 1500 行与尾 500 行", report.Summary, StringComparison.Ordinal);
        Assert.Contains("huge-crash.txt", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>把若干文本条目写成 zip；条目顺序即压缩包内顺序。</summary>
    private static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
    }
}
