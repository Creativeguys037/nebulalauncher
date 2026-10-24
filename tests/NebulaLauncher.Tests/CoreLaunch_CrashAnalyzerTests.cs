using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>崩溃归因规则必须能认出常见日志特征并给出中文建议。</summary>
public class CoreLaunch_CrashAnalyzerTests
{
    [Fact]
    public void Analyze_OutOfMemory_SuggestsMoreMemory()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [Render thread/INFO]: Backend library: LWJGL
            java.lang.OutOfMemoryError: Java heap space
                at net.minecraft.client.renderer.chunk.RenderChunk.<init>(RenderChunk.java:1)
            Exception in thread "Render thread" java.lang.OutOfMemoryError: Java heap space
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "内存不足");
        Assert.Contains(report.Suggestions, s => s.Severity == CrashSeverity.Error);
        Assert.All(report.Suggestions, s => Assert.False(string.IsNullOrWhiteSpace(s.Detail)));
    }

    [Fact]
    public void Analyze_UnsupportedClassVersion_SuggestsNewerJava()
    {
        var report = CrashAnalyzer.Analyze("""
            Error: LinkageError occurred while loading main class net.minecraft.client.main.Main
                java.lang.UnsupportedClassVersionError: net/minecraft/client/main/Main has been compiled by a more recent version of the Java Runtime (class file version 61.0)
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "Java 版本过低");
        Assert.Contains("17", suggestion.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_MixinFailure_SuggestsMixinConflict()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/ERROR]: Mixin apply failed create.mixins.json:BlockMixin -> net.minecraft.world.level.block.Block
            org.spongepowered.asm.mixin.transformer.throwables.InvalidMixinException: @Inject could not find target method
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "Mixin 冲突");
    }

    [Fact]
    public void Analyze_GlfwError_SuggestsGraphicsDriver()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [Render thread/ERROR]: GLFW error 65542: WGL: The driver does not appear to support OpenGL
            [12:00:00] [Render thread/ERROR]: Pixel format not accelerated
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "显卡驱动问题");
        Assert.Equal(CrashSeverity.Warning, suggestion.Severity);
    }

    [Fact]
    public void Analyze_NoSuchMethod_SuggestsModVersionMismatch()
    {
        var report = CrashAnalyzer.Analyze("""
            java.lang.NoSuchMethodError: 'net.minecraft.world.item.ItemStack.getItem()Lnet/minecraft/world/item/Item;'
                at com.example.mod.Main.onInit(Main.java:42)
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "模组版本不匹配");
    }

    [Fact]
    public void Analyze_AccessDenied_SuggestsPermissionIssue()
    {
        var report = CrashAnalyzer.Analyze("""
            java.nio.file.AccessDeniedException: C:\Program Files\Java\jdk-17\bin\server\jvm.dll
                at sun.nio.fs.WindowsException.translateToIOException(WindowsException.java:86)
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "文件权限被拒绝");
    }

    [Fact]
    public void Analyze_MissingForgeDependency_SuggestsInstallingPrerequisites()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/ERROR]: Missing or unsupported mandatory dependencies:
                Mod ID: 'curios', Requested by: 'some_mod', Expected range: '[5.0,)'
            """);

        Assert.True(report.IsCrash);
        Assert.Contains(report.Suggestions, s => s.Title == "Forge 依赖缺失");
    }

    [Fact]
    public void Analyze_HealthyLog_ReportsNoCrash()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/INFO]: Loading 42 mods
            [12:00:01] [Render thread/INFO]: Backend library: LWJGL version 3.3.2
            [12:00:02] [Render thread/INFO]: Sound engine started
            [12:00:03] [Render thread/INFO]: Stopping!
            """);

        Assert.False(report.IsCrash);
        Assert.Empty(report.Suggestions);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
    }

    [Fact]
    public void Analyze_OnlyLooksAtTail()
    {
        var lines = new List<string> { "java.lang.OutOfMemoryError: Java heap space" };

        // 把 OOM 挤出窗口，尾部是最新日志。
        for (var i = 0; i < CrashAnalyzer.DefaultTailLines + 20; i++)
        {
            lines.Add($"[12:00:{i % 60:00}] [Render thread/INFO]: tick {i}");
        }

        var report = CrashAnalyzer.Analyze(lines);

        Assert.False(report.IsCrash);
        Assert.Empty(report.Suggestions);
    }

    [Fact]
    public void Analyze_EmptyInput_DoesNotCrash()
    {
        Assert.False(CrashAnalyzer.Analyze((string?)null).IsCrash);
        Assert.False(CrashAnalyzer.Analyze(string.Empty).IsCrash);
        Assert.False(CrashAnalyzer.Analyze(Array.Empty<string>()).IsCrash);
    }

    [Fact]
    public void Format_IncludesTitleAndDetail()
    {
        var report = CrashAnalyzer.Analyze("java.lang.OutOfMemoryError: Java heap space");
        var text = CrashAnalyzer.Format(report);

        Assert.Contains("内存不足", text, StringComparison.Ordinal);
        Assert.Contains("最大内存", text, StringComparison.Ordinal);
    }

    // ==================== 崩溃报告定位：EXTENSIONS 条目 5 ====================

    [Fact]
    public void ExtractCrashReportPath_WithGameMarker_ReturnsPath()
    {
        var log = """
            [12:00:00] [main/INFO]: Loading 42 mods
            #@!@# Game crashed! Crash report saved to: #@!@# C:\x\crash-reports\crash-2026-01-01.txt
            [12:00:01] [main/INFO]: Stopping!
            """;

        Assert.Equal(
            @"C:\x\crash-reports\crash-2026-01-01.txt",
            CrashAnalyzer.ExtractCrashReportPath(log));
    }

    [Fact]
    public void ExtractCrashReportPath_PathWithSpaces_KeepsWholePath()
    {
        var log = """#@!@# Game crashed! Crash report saved to: #@!@# D:\My Games\Nebula\crash-reports\crash 2026-01-01.txt""";

        Assert.Equal(
            @"D:\My Games\Nebula\crash-reports\crash 2026-01-01.txt",
            CrashAnalyzer.ExtractCrashReportPath(log));
    }

    [Fact]
    public void ExtractCrashReportPath_WithoutMarker_ReturnsNull()
    {
        var log = """
            [12:00:00] [main/INFO]: Loading 42 mods
            [12:00:01] [main/INFO]: Sound engine started
            java.lang.OutOfMemoryError: Java heap space
            """;

        Assert.Null(CrashAnalyzer.ExtractCrashReportPath(log));
    }

    [Fact]
    public void ExtractCrashReportPath_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(CrashAnalyzer.ExtractCrashReportPath(null));
        Assert.Null(CrashAnalyzer.ExtractCrashReportPath(string.Empty));
        Assert.Null(CrashAnalyzer.ExtractCrashReportPath("   "));
    }

    [Fact]
    public void ExtractCrashSection_WithMarkers_ReturnsReportBody()
    {
        var log = """
            [12:00:00] [main/INFO]: Loading 42 mods
            ---- Minecraft Crash Report ----
            // I bet Cylons wouldn't have this problem.
            Description: Unexpected error
            java.lang.NullPointerException: null
            #@!@# Game crashed! Crash report saved to: #@!@# C:\logs\crash.txt
            [12:00:01] [main/INFO]: Stopping!
            """;

        var section = CrashAnalyzer.ExtractCrashSection(log);

        Assert.NotNull(section);
        Assert.StartsWith("---- Minecraft Crash Report ----", section, StringComparison.Ordinal);
        Assert.Contains("Description: Unexpected error", section, StringComparison.Ordinal);
        Assert.DoesNotContain("#@!@# Game crashed!", section, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping!", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractCrashSection_WithoutMarkers_ReturnsNull()
    {
        Assert.Null(CrashAnalyzer.ExtractCrashSection("""
            [12:00:00] [main/INFO]: Loading 42 mods
            java.lang.OutOfMemoryError: Java heap space
            """));

        // 只有结束标记、没有起始标记
        Assert.Null(CrashAnalyzer.ExtractCrashSection(
            """#@!@# Game crashed! Crash report saved to: #@!@# C:\x\crash.txt"""));

        Assert.Null(CrashAnalyzer.ExtractCrashSection(null));
    }

    // ==================== 肇事模组定位：EXTENSIONS 条目 5 ====================

    [Fact]
    public void FindSuspectPackageNames_DescriptionAndModStack_ReturnsExampleFirst()
    {
        var crashReport = """
            ---- Minecraft Crash Report ----
            Time: 2026-01-01 12:00:00
            Description: Ticking entity

            java.lang.RuntimeException: example mod exploded
                at com.example.mymod.Mixin.foo(Mixin.java:12)
                at net.minecraft.world.entity.Entity.tick(Entity.java:1)

            -- Entity being ticked --
            Details: Entity Type: example:thing

            A detailed walkthrough of the error, its code path and all known details is as follows:
            """;

        var suspects = CrashAnalyzer.FindSuspectPackageNames(crashReport);

        Assert.NotEmpty(suspects);
        Assert.Equal("example", suspects[0]);
        Assert.Contains("mymod", suspects);
    }

    [Fact]
    public void FindSuspectPackageNames_VanillaOnlyStack_ReturnsEmpty()
    {
        var crashReport = """
            Description: Rendering entity in world

            java.lang.NullPointerException: Cannot invoke "Object.hashCode()"
                at net.minecraft.client.renderer.chunk.RenderChunk.rebuild(RenderChunk.java:1)
                at net.minecraft.client.renderer.LevelRenderer.renderChunkLayer(LevelRenderer.java:2)

            A detailed walkthrough of the error, its code path and all known details is as follows:
            """;

        Assert.Empty(CrashAnalyzer.FindSuspectPackageNames(crashReport));
    }

    [Fact]
    public void FindSuspectPackageNames_NullInput_ReturnsEmpty()
    {
        Assert.Empty(CrashAnalyzer.FindSuspectPackageNames(null));
        Assert.Empty(CrashAnalyzer.FindSuspectPackageNames(string.Empty));
    }

    [Fact]
    public void FindSuspectPackageNames_WithoutWalkthroughMarker_StillFindsSuspects()
    {
        // Forge / Fabric / vanilla 三种报告的 Description 位置与收尾文案不同，
        // 缺少「A detailed walkthrough of the error」时也要能扫。
        var crashReport = """
            Description: Unexpected error

                at com.anothermod.core.Handler.run(Handler.java:7)
            """;

        var suspects = CrashAnalyzer.FindSuspectPackageNames(crashReport);

        Assert.Single(suspects);
        Assert.Equal("anothermod", suspects[0]);
    }

    // ==================== 规则扩容：EXTENSIONS 条目 5 ====================

    [Fact]
    public void Analyze_UnsupportedClassVersion_AlsoHitsTooOldJavaRule()
    {
        var report = CrashAnalyzer.Analyze("""
            java.lang.UnsupportedClassVersionError: com/example/mod/Main has been compiled by a more recent
            version of the Java Runtime (class file version 61.0), only recognizes up to 52.0
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "Java 版本低于模组要求");
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_OutOfMemory_SuggestsJavaSettingsAction()
    {
        var report = CrashAnalyzer.Analyze("java.lang.OutOfMemoryError: Java heap space");

        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "内存不足");
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_DuplicateMod_SuggestsModsFolderAction()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/ERROR]: Found a duplicate mod jei at C:\games\mods\jei-1.20.1.jar
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "存在重复的模组");
        Assert.Equal(CrashSeverity.Warning, suggestion.Severity);
        Assert.Equal(CrashAnalyzer.Actions.OpenModsFolder, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_Jvm32Bit_SuggestsJavaSettings()
    {
        var report = CrashAnalyzer.Analyze("""
            Error occurred during initialization of VM
            Could not reserve enough space for 4194304KB object heap
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "32 位 Java 或分配内存过大");
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_IncompleteForge_SuggestsRepairVersion()
    {
        var report = CrashAnalyzer.Analyze("""
            java.lang.IllegalStateException: Could not find net/minecraft/client/Minecraft.class in classloader SecureModuleClassLoader
            """);

        Assert.True(report.IsCrash);
        var suggestion = Assert.Single(report.Suggestions, s => s.Title == "Forge 安装不完整");
        Assert.Equal(CrashAnalyzer.Actions.RepairVersion, suggestion.ActionId);
    }

    [Fact]
    public void Analyze_ManyRulesMatch_TruncatesToThreeSuggestions()
    {
        // 一条日志同时命中 4 条 Error 规则，必须按严重度截断，否则 UI 会刷屏。
        var report = CrashAnalyzer.Analyze("""
            ModResolutionException: Could not find required mod: some_mod requires other_mod
            java.lang.VerifyError: Bad type on operand stack
            C:\games\mods\broken.jar is not a valid mod file
            """);

        Assert.True(report.IsCrash);
        Assert.Equal(CrashAnalyzer.MaxSuggestions, report.Suggestions.Count);
        Assert.Contains(report.Suggestions, s => s.Title == "模组依赖缺失");
        Assert.All(report.Suggestions, s => Assert.Equal(CrashSeverity.Error, s.Severity));
    }
}

