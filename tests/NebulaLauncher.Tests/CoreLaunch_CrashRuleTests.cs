using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 崩溃规则扩容（三层短路 + 规则表扩容 + 堆栈归因）的验收用例。
///
/// 覆盖四类判据：
/// <list type="number">
/// <item>Crit2 的上下文二分：<c>Could not reserve enough space</c> 要看有没有 <c>object heap</c>；</item>
/// <item>Crit1 的既有规则与新规则都必须命中（不回归 + 扩容生效）；</item>
/// <item>堆栈归因只在「有加载器特征 / 有崩溃标记」时启动，且结论只能写「疑似」；</item>
/// <item>规则条数与 Info 日志：规则表装载多少条、命中哪几条，都要能在日志里核对。</item>
/// </list>
/// </summary>
public class CoreLaunch_CrashRuleTests
{
    /// <summary>测试用关键字数组（抽成字段，避免重复调用时反复新建数组）。</summary>
    private static readonly string[] JeiKeyword = ["jei"];

    /// <summary>测试用关键字数组（抽成字段，避免重复调用时反复新建数组）。</summary>
    private static readonly string[] CreateKeyword = ["create"];

    // ==================== Crit2：上下文二分 ====================

    [Fact]
    public void Analyze_ReserveSpaceWithObjectHeap_Concludes32BitJava()
    {
        var report = CrashAnalyzer.Analyze("""
            Error occurred during initialization of VM
            Could not reserve enough space for 1048576KB object heap
            """);

        Assert.True(report.IsCrash);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("32 位", suggestion.Title, StringComparison.Ordinal);
        Assert.Equal(CrashSeverity.Error, suggestion.Severity);
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);
        Assert.Contains("64 位 Java", suggestion.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ReserveSpaceWithoutObjectHeap_ConcludesOutOfMemory()
    {
        var report = CrashAnalyzer.Analyze("""
            Error occurred during initialization of VM
            Could not reserve enough space
            """);

        Assert.True(report.IsCrash);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("内存不足", suggestion.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("32 位", suggestion.Title, StringComparison.Ordinal);
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_HsErrWithNvidiaModuleBlamesDriverNotMods()
    {
        // 同一症状（EXCEPTION_ACCESS_VIOLATION）靠 hs_err 的模块名分流到三家驱动。
        var report = CrashAnalyzer.Analyze(
            "#\n"
            + "# EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb12340000, pid=1234, tid=5678\n"
            + "#\n"
            + "# C  [nvoglv64.dll+0x9a1234]\n");

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("NVIDIA", suggestion.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("模组", suggestion.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_Crit1HitDoesNotFallThroughToCrit2()
    {
        // 同时命中 Crit1（OutOfMemoryError）与 Crit2（Could not reserve enough space 无 object heap）：
        // 短路后只应留下 Crit1 的结论。
        var report = CrashAnalyzer.Analyze("""
            java.lang.OutOfMemoryError: Java heap space
            Error occurred during initialization of VM
            Could not reserve enough space
            """);

        Assert.Contains(report.Suggestions, s => s.Title == "内存不足");
        Assert.DoesNotContain(report.Suggestions, s => s.Title.Contains("预留内存失败", StringComparison.Ordinal));
    }

    // ==================== Crit1：验收点名的既有 / 新增规则 ====================

    [Fact]
    public void Analyze_JavaHeapSpace_ConcludesOutOfMemory()
    {
        var report = CrashAnalyzer.Analyze("java.lang.OutOfMemoryError: Java heap space");

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("内存不足", suggestion.Title);
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_MissingMandatoryDependencies_ConcludesMissingPrerequisites()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/ERROR]: Missing or unsupported mandatory dependencies:
                Mod ID: 'curios', Requested by: 'some_mod', Expected range: '[5.0,)'
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("Forge 依赖缺失", suggestion.Title);
        Assert.Equal(CrashAnalyzer.Actions.OpenModsFolder, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_DuplicateModsFound_ConcludesDuplicateMods()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/ERROR]: Duplicate mods found
                Mod ID: 'jei' from mod files: jei-1.20.1.jar, jei-1.20.1-old.jar
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("重复", suggestion.Title, StringComparison.Ordinal);
        Assert.Equal(CrashAnalyzer.Actions.OpenModsFolder, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_FoundDuplicateMod_ConcludesDuplicateMods()
    {
        var report = CrashAnalyzer.Analyze("""
            java.lang.RuntimeException: Found duplicate mods:
                Mod ID: 'create', from mod files: create-1.0.jar, create-1.1.jar
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_JdkInsteadOfJre_SuggestsSwitchingToJre()
    {
        var report = CrashAnalyzer.Analyze("""
            java.lang.ClassCastException: java.base/jdk.internal.loader.ClassLoaders$AppClassLoader
            """);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("JDK", suggestion.Title, StringComparison.Ordinal);
        Assert.Contains("jre", suggestion.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_ExistingGraphicsRule_StillMatches()
    {
        // 不回归采样：既有 30 条里的一条。
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [Render thread/ERROR]: GLFW error 65542: WGL: The driver does not appear to support OpenGL
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("显卡驱动问题", suggestion.Title);
        Assert.Equal(CrashSeverity.Warning, suggestion.Severity);
    }

    // ==================== 堆栈归因 ====================

    [Fact]
    public void Analyze_CrashReportWithForeignPackage_SuggestsSuspectMod()
    {
        var report = CrashAnalyzer.Analyze("""
            ---- Minecraft Crash Report ----
            // Don't be sad, have a hug!
            Time: 2026-01-01 12:00:00
            Description: Unexpected error

            The game crashed whilst initializing game
            java.lang.NullPointerException: null
                at com.example.foo.Bar.baz(Bar.java:10)
                at net.minecraft.client.Minecraft.run(Minecraft.java:1)
            """);

        Assert.True(report.IsCrash);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("疑似", suggestion.Title, StringComparison.Ordinal);
        Assert.Contains("example", suggestion.Detail, StringComparison.Ordinal);
        Assert.Contains("foo", suggestion.Detail, StringComparison.Ordinal);
        Assert.Equal(CrashSeverity.Warning, suggestion.Severity);
        Assert.Equal(CrashAnalyzer.Actions.OpenModsFolder, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_StackWithModList_NamesTheJarFile()
    {
        var report = CrashAnalyzer.Analyze("""
            ---- Minecraft Crash Report ----
            Description: Unexpected error

            java.lang.NullPointerException: null
                at com.example.foo.Bar.baz(Bar.java:10)

            Mod List:
                foo (foo-2.0.1.jar)
            """);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Contains("疑似", suggestion.Title, StringComparison.Ordinal);
        Assert.Contains("foo-2.0.1.jar", suggestion.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ForeignPackageWithoutCrashContext_DoesNotBlameMods()
    {
        // 只是日志里出现了一个第三方包名的 at 行，没有加载器特征也没有崩溃标记：不许归因。
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/INFO]: Loading 1 mod
                at com.example.foo.Bar.baz(Bar.java:10)
            """);

        Assert.Empty(report.Suggestions);
        Assert.False(report.IsCrash);
    }

    [Fact]
    public void FindSuspectPackageNames_LoaderAndRuntimePrefixes_AreDiscarded()
    {
        var crashReport = """
            Description: Unexpected error

                at net.minecraftforge.fml.javafmlmod.FMLModContainer.constructMod(FMLModContainer.java:1)
                at org.spongepowered.asm.mixin.transformer.MixinProcessor.applyMixins(MixinProcessor.java:2)
                at java.util.HashMap.get(HashMap.java:3)

            A detailed walkthrough of the error, its code path and all known details is as follows:
            """;

        Assert.Empty(CrashAnalyzer.FindSuspectPackageNames(crashReport));
    }

    [Fact]
    public void FindSuspectModFiles_FromModList_ReturnsJarName()
    {
        var crashReport = """
            Mod List:
                jei (jei-1.20.1-forge-15.2.0.27.jar)
                create (create-0.5.1.f.jar)
            """;

        var files = CrashAnalyzer.FindSuspectModFiles(crashReport, JeiKeyword);

        Assert.Equal("jei-1.20.1-forge-15.2.0.27.jar", Assert.Single(files));
    }

    [Fact]
    public void FindSuspectModFiles_FromValidModFileLine_ReturnsJarName()
    {
        var debugLog = """
            [12:00:00] [main/DEBUG] [mixin/]: Found valid mod file C:\games\mods\create-1.20.1.jar with 42 mods inside
            """;

        var files = CrashAnalyzer.FindSuspectModFiles(debugLog, CreateKeyword);

        Assert.Equal("create-1.20.1.jar", Assert.Single(files));
    }

    [Fact]
    public void FindSuspectModFiles_WithoutKeywordHit_ReturnsEmpty()
    {
        Assert.Empty(CrashAnalyzer.FindSuspectModFiles("Mod List:\n\tjei (jei.jar)", CreateKeyword));
        Assert.Empty(CrashAnalyzer.FindSuspectModFiles("Mod List:\n\tjei (jei.jar)", []));
        Assert.Empty(CrashAnalyzer.FindSuspectModFiles(null, JeiKeyword));
    }

    // ==================== Crit3：兜底 ====================

    [Fact]
    public void Analyze_UnknownCrashReport_FallsBackToCrit3()
    {
        var report = CrashAnalyzer.Analyze("""
            ---- Minecraft Crash Report ----
            Description: Unexpected error
            java.lang.IllegalStateException: boom
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal(CrashSeverity.Warning, suggestion.Severity);
        Assert.Equal(CrashAnalyzer.Actions.OpenCrashReport, suggestion.ActionId);
    }

    // ==================== 大日志截断 ====================

    [Fact]
    public void AnalyzeHeadTail_KeepsHeadCauseOutsideTailWindow()
    {
        var lines = new List<string> { "java.lang.OutOfMemoryError: Java heap space" };

        for (var i = 0; i < 3000; i++)
        {
            lines.Add($"[12:00:{i % 60:00}] [Render thread/INFO]: tick {i}");
        }

        // 纯尾部窗口里已经没有 OOM 了。
        Assert.DoesNotContain(CrashAnalyzer.Analyze(lines).Suggestions, s => s.Title == "内存不足");

        // 头部 1500 行 + 尾部 500 行的组合窗口能捞回来。
        Assert.Contains(CrashAnalyzer.AnalyzeHeadTail(lines).Suggestions, s => s.Title == "内存不足");
    }

    // ==================== 规则条数与日志 ====================

    [Fact]
    public void RuleCount_IsAtLeastSixty()
    {
        Assert.True(
            CrashAnalyzer.RuleCount >= 60,
            $"规则条数只有 {CrashAnalyzer.RuleCount}，低于验收要求的 60 条");

        Assert.Equal(
            CrashAnalyzer.RuleCount,
            CrashAnalyzer.Crit1RuleCount + CrashAnalyzer.Crit2RuleCount + CrashAnalyzer.Crit3RuleCount);
        Assert.True(CrashAnalyzer.Crit1RuleCount > 0);
        Assert.True(CrashAnalyzer.Crit2RuleCount > 0);
        Assert.True(CrashAnalyzer.Crit3RuleCount > 0);
    }

    [Fact]
    public void Analyze_WithAppLog_WritesRuleCountAndHitAtInfo()
    {
        var log = new RecordingAppLog();

        var report = CrashAnalyzer.Analyze("java.lang.OutOfMemoryError: Java heap space", log);

        Assert.Contains(log.Infos, m => m.Contains($"{CrashAnalyzer.RuleCount} 条", StringComparison.Ordinal));
        Assert.Contains(log.Infos, m => m.Contains("内存不足", StringComparison.Ordinal));
        Assert.Contains(report.Suggestions, s => s.Title == "内存不足");
    }

    [Fact]
    public void Analyze_WithoutAppLog_DoesNotWriteAnything()
    {
        var report = CrashAnalyzer.Analyze("java.lang.OutOfMemoryError: Java heap space", null);

        Assert.Contains(report.Suggestions, s => s.Title == "内存不足");
    }

    // ==================== 归因动作：能不能一键禁用 ====================

    /// <summary>
    /// 归因只定位到一个 jar 时才给「禁用」动作。
    ///
    /// 这一条锁的是「二分法一次只改一个变量」：候选不唯一时禁用谁都是猜，
    /// 猜错就把玩家的模组包砸掉，而且下一次崩溃会更难判断。
    /// </summary>
    [Fact]
    public void Analyze_归因到单个肇事文件_给出DisableMod动作()
    {
        var report = CrashAnalyzer.Analyze("""
            ---- Minecraft Crash Report ----
            Description: Rendering overlay

            java.lang.NullPointerException: Cannot invoke "Screen.render"
                at com.example.jei.JeiRenderer.render(JeiRenderer.java:42)

            A detailed walkthrough of the error, its code path and all known details is as follows:

            Mod List:
                jei (jei-1.20.1-forge-15.2.0.27.jar)
            """);

        var suggestion = Assert.Single(report.Suggestions);

        Assert.Equal(CrashAnalyzer.Actions.DisableMod, suggestion.ActionId);
        Assert.Equal("jei-1.20.1-forge-15.2.0.27.jar", suggestion.ActionArgument);
        Assert.Contains("可以直接禁用", suggestion.Detail, StringComparison.Ordinal);
    }

    /// <summary>多个候选文件时只给「打开模组目录」，并明确不带参数。</summary>
    [Fact]
    public void Analyze_归因到多个肇事文件_不给DisableMod动作()
    {
        var report = CrashAnalyzer.Analyze("""
            ---- Minecraft Crash Report ----
            Description: Rendering overlay

            java.lang.NullPointerException: Cannot invoke "Screen.render"
                at com.example.jei.JeiRenderer.render(JeiRenderer.java:42)

            A detailed walkthrough of the error, its code path and all known details is as follows:

            Mod List:
                jei (jei-1.20.1-forge-15.2.0.27.jar)
                jei-extra (jei-extra-1.0.jar)
            """);

        var suggestion = Assert.Single(report.Suggestions);

        Assert.Equal(CrashAnalyzer.Actions.OpenModsFolder, suggestion.ActionId);
        Assert.Null(suggestion.ActionArgument);
        Assert.DoesNotContain("可以直接禁用", suggestion.Detail, StringComparison.Ordinal);
    }

    /// <summary>只记录、不落盘的 IAppLog，用来断言 Info 文案。</summary>
    private sealed class RecordingAppLog : IAppLog
    {
        private readonly List<string> _infos = [];

        public IReadOnlyList<string> Infos => _infos;

        public string LogFilePath => string.Empty;

        public void Debug(string message)
        {
        }

        public void Info(string message) => _infos.Add(message);

        public void Warn(string message, Exception? ex = null)
        {
        }

        public void Error(string message, Exception? ex = null)
        {
        }
    }
}
