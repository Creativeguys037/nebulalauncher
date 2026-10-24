using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 崩溃规则表第三批扩充的验收用例（PCL2 第二轮复查缺口 R29-1 / R29-2）。
///
/// 两条规则都是「兜底性质」的，所以这里除了验命中，更重要的一半是验<b>不该命中</b>：
/// <list type="number">
/// <item><b>游戏没能启动</b>：五个条件（无 <c>at net.</c>、无 <c>INFO]</c>、无 hs_err、
/// 无 crash report、输出不足 100 字符）必须同时成立。任意一条不成立都不能判「没能启动」，
/// 否则一份正常的 Forge 日志会被误判成启动失败。</item>
/// <item><b>版本 JSON 中存在多个 Forge</b>：精确整串匹配。模糊片段会被正常启动参数
/// <c>--fml.forgeVersion</c> 命中，等于把每份 Forge 日志都判成安装损坏。</item>
/// </list>
/// 另一半是<b>不抢具体结论</b>：极短输出同时命中具体规则时（例如只有一行
/// <c>OutOfMemoryError</c>），必须给出具体成因而不是「没能启动」。
/// </summary>
public class CoreLaunch_CrashRulesTests
{
    // ==================== 缺口 1：极短输出 ====================

    /// <summary>
    /// 最短的真实形态：Java 进程只吐了一行就被杀掉（内存参数非法时就是这样），
    /// 既没有堆栈也没有日志框架的前缀。
    /// </summary>
    [Fact]
    public void Analyze_极短且无任何特征_判为游戏没能启动()
    {
        var report = CrashAnalyzer.Analyze("Error: could not create the Java Virtual Machine.");

        Assert.True(report.IsCrash);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("游戏没能启动", suggestion.Title);
        Assert.Equal(CrashSeverity.Warning, suggestion.Severity);
        Assert.Equal(CrashAnalyzer.Actions.OpenJavaSettings, suggestion.ActionId);

        // 文案必须给下一步，不能只说「没能启动」。
        Assert.Contains("设置 → Java", suggestion.Detail, StringComparison.Ordinal);
        Assert.Contains("最大内存", suggestion.Detail, StringComparison.Ordinal);
    }

    /// <summary>空输出附近的最小情形：只有空白与几个字符。</summary>
    [Fact]
    public void Analyze_只有几个字符_判为游戏没能启动()
    {
        var report = CrashAnalyzer.Analyze("Error");

        Assert.Contains(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>条件（1）：只要出现 <c>at net.</c> 就是有堆栈，不能判「没能启动」。</summary>
    [Fact]
    public void Analyze_极短但含atNet_不判没能启动()
    {
        var text = "Exception at net.minecraft.client.main.Main.main(Main.java:1)";

        Assert.True(text.Length < 100, "样本必须短于 100 字符，否则这条用例证明不了条件（1）");

        var report = CrashAnalyzer.Analyze(text);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>条件（2）：有 <c>INFO]</c> 说明日志框架正常初始化过。</summary>
    [Fact]
    public void Analyze_极短但含INFO标记_不判没能启动()
    {
        var text = "[12:00:00] [main/INFO] [fml/]: Loading 12 mods";

        Assert.True(text.Length < 100, "样本必须短于 100 字符，否则这条用例证明不了条件（2）");

        var report = CrashAnalyzer.Analyze(text);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>条件（3）：hs_err 的头行意味着 JVM 原生层崩了，有专属规则。</summary>
    [Fact]
    public void Analyze_极短但含hsErr头_不判没能启动()
    {
        var text = "A fatal error has been detected by the Java Runtime Environment";

        Assert.True(text.Length < 100, "样本必须短于 100 字符，否则这条用例证明不了条件（3）");

        var report = CrashAnalyzer.Analyze(text);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>条件（4）：crash report 的起始标记意味着 Java 层崩了，有专属规则。</summary>
    [Fact]
    public void Analyze_极短但含crashReport标记_不判没能启动()
    {
        var text = "#---- Minecraft Crash Report ----#";

        Assert.True(text.Length < 100, "样本必须短于 100 字符，否则这条用例证明不了条件（4）");

        var report = CrashAnalyzer.Analyze(text);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>条件（5）的上边界：99 个字符算「短」，100 个字符就不算了（PCL2 是 <c>&lt; 100</c>）。</summary>
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, false)]
    [InlineData(101, false)]
    public void Analyze_长度边界与100字符判据一致(int length, bool expected)
    {
        var text = new string('x', length);

        var report = CrashAnalyzer.Analyze(text);

        Assert.Equal(
            expected,
            report.Suggestions.Any(s => s.Title == "游戏没能启动"));
    }

    /// <summary>
    /// 原版崩溃提示（<c>The game crashed whilst</c>）本身就是「游戏起来了并且崩了」的证据，
    /// 不能同时出现一条矛盾的「没能启动」。
    /// </summary>
    [Fact]
    public void Analyze_极短但含原版崩溃提示_不判没能启动()
    {
        var report = CrashAnalyzer.Analyze("The game crashed whilst initializing game");

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>
    /// 兜底不能抢具体结论：一行 <c>OutOfMemoryError</c> 同样不足 100 字符，
    /// 但它有明确成因，必须给出「内存不足」而不是「没能启动」。
    /// </summary>
    [Fact]
    public void Analyze_极短但命中具体规则_给具体成因而不是没能启动()
    {
        var text = "java.lang.OutOfMemoryError: Java heap space";

        Assert.True(text.Length < 100);

        var report = CrashAnalyzer.Analyze(text);

        Assert.Contains(report.Suggestions, s => s.Title == "内存不足");
        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    /// <summary>
    /// Forge 1.17+ 的真实形态：加载日志在别的文件里，latest.log 尾部只剩很短一截，
    /// 但这一截带完整日志格式。不能因为短就判「没能启动」。
    /// </summary>
    [Fact]
    public void Analyze_短但格式完整的Forge加载日志_不判没能启动()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/INFO] [mixin/]: SpongePowered MIXIN IS DISABLED
            """);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "游戏没能启动");
    }

    // ==================== 缺口 2：版本 JSON 中存在多个 Forge ====================

    /// <summary>PCL2 <c>ModCrash.vb:491</c> 的原始样本形态。</summary>
    private const string MultipleForgeLog = """
        [main/ERROR] [net.minecraftforge.fml.loading.arguments.Arguments/ARGUMENTS]: Failed to parse arguments
        Exception in thread "main" java.lang.IllegalArgumentException: Found multiple arguments for option fml.forgeVersion, but you asked for only one
            at joptsimple.OptionSet.valueOf(OptionSet.java:209)
            at joptsimple.OptionParser.parse(OptionParser.java:132)
            at net.minecraftforge.fml.loading.arguments.Arguments.parseArguments(Arguments.java:42)
            at cpw.mods.bootstraplauncher.BootstrapLauncher.main(BootstrapLauncher.java:69)
        """;

    [Fact]
    public void Analyze_版本JSON存在多个Forge_给出重装动作()
    {
        var report = CrashAnalyzer.Analyze(MultipleForgeLog);

        Assert.True(report.IsCrash);

        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal("版本 JSON 中存在多个 Forge", suggestion.Title);
        Assert.Equal(CrashSeverity.Error, suggestion.Severity);

        // 文案要指出具体文件与具体参数，否则用户只能重装碰运气。
        Assert.Contains("versions\\<id>\\<id>.json", suggestion.Detail, StringComparison.Ordinal);
        Assert.Contains("--fml.forgeVersion", suggestion.Detail, StringComparison.Ordinal);
        Assert.Contains("重新安装一次 Forge", suggestion.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 关键回归：这条日志里有 Forge 包名的 <c>at</c> 行，修好之前会掉进堆栈归因，
    /// 被判成「疑似模组导致崩溃」。现在必须是「多个 Forge」这一条根因。
    /// </summary>
    [Fact]
    public void Analyze_版本JSON存在多个Forge_不再落到疑似模组导致崩溃()
    {
        var report = CrashAnalyzer.Analyze(MultipleForgeLog);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "疑似模组导致崩溃");
        Assert.DoesNotContain(report.Suggestions, s => s.Title == "模组解析失败");
    }

    /// <summary>
    /// 精确串的反面：正常启动参数里带 <c>--fml.forgeVersion</c> 时绝不能被判成安装损坏，
    /// 否则每份 Forge 日志都会报「版本 JSON 中存在多个 Forge」。
    /// </summary>
    [Fact]
    public void Analyze_正常的forgeVersion启动参数_不判多个Forge()
    {
        var report = CrashAnalyzer.Analyze("""
            [12:00:00] [main/INFO] [fml/]: --fml.forgeVersion 47.2.0
            --fml.mcVersion 1.20.1 --fml.forgeGroup net.minecraftforge
            """);

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "版本 JSON 中存在多个 Forge");
    }

    /// <summary>只出现报错的半句（去掉逗号后的部分）也不该命中——精确整串匹配。</summary>
    [Fact]
    public void Analyze_只匹配报错前半句_不判多个Forge()
    {
        var report = CrashAnalyzer.Analyze(
            "Found multiple arguments for option fml.forgeVersion");

        Assert.DoesNotContain(report.Suggestions, s => s.Title == "版本 JSON 中存在多个 Forge");
    }
}