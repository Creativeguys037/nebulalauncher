using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// Java 约束集（EXTENSIONS 条目 2）与扫描扩展（条目 4）的用例。
///
/// 规则的 HMCL 出处逐条写在 <see cref="JavaRequirementTable.All"/> 的注释里，
/// 这里只断言「给定游戏版本 + 加载器 + Java 主版本」时算出来的上下界与判定结果。
/// </summary>
public class CoreAssets_JavaConstraintTests
{
    // ---------------- 条目 2：AllowedRange 上下界 ----------------

    [Theory]
    // LaunchWrapper ≤1.12 假定系统类加载器是 URLClassLoader，Java 9+ 直接 ClassCastException。
    [InlineData("1.12.2", LoaderKind.Forge, 8, 8)]
    [InlineData("1.12.2", LoaderKind.Vanilla, 8, 8)]
    [InlineData("1.7.10", LoaderKind.Forge, 8, 8)]
    [InlineData("1.8.9", LoaderKind.Vanilla, 8, 8)]
    // MODLAUNCHER_8（1.16.3～1.17.1 的 Forge）上界 17：1.16.5 Forge + Java 17 必须放行。
    [InlineData("1.16.5", LoaderKind.Forge, 8, 17)]
    [InlineData("1.17.1", LoaderKind.Forge, 16, 17)]
    // 1.17 原版需要 Java 16；1.17 的 Forge 落进 MODLAUNCHER_8 区间（1.16.3～1.17.1），上界 17。
    [InlineData("1.17", LoaderKind.Vanilla, 16, int.MaxValue)]
    [InlineData("1.17", LoaderKind.Forge, 16, 17)]
    // 原版 1.20.1 不能被任何 Forge 规则误伤。
    [InlineData("1.20.1", LoaderKind.Vanilla, 17, int.MaxValue)]
    [InlineData("1.20.1", LoaderKind.Forge, 17, int.MaxValue)]
    [InlineData("1.20.4", LoaderKind.Forge, 17, int.MaxValue)]
    [InlineData("1.20.5", LoaderKind.Vanilla, 21, int.MaxValue)]
    [InlineData("1.20.5", LoaderKind.Forge, 21, int.MaxValue)]
    [InlineData("1.21.4", LoaderKind.NeoForge, 21, int.MaxValue)]
    // 2026 年起 Mojang 改用年份版本号，26.x 需要 Java 25。
    [InlineData("26.1", LoaderKind.Vanilla, 25, int.MaxValue)]
    [InlineData("26.3", LoaderKind.Vanilla, 25, int.MaxValue)]
    public void 约束集给出Java上下界(string minecraftVersion, LoaderKind loader, int expectedMin, int expectedMax)
    {
        var range = JavaRequirementTable.AllowedRange(minecraftVersion, loader);

        Assert.True(range.HasValue, $"{minecraftVersion}（{loader}）应该给出 Java 区间");
        Assert.Equal(expectedMin, range!.Value.Min);
        Assert.Equal(expectedMax, range.Value.Max);
    }

    [Theory]
    [InlineData("23w31a")]
    [InlineData("b1.7.3")]
    [InlineData("")]
    [InlineData((string?)null)] // 必须显式转型，否则 C# 会把 null 解析成 params 空数组
    [InlineData("abc")]
    public void 未知版本没有约束区间(string? minecraftVersion)
    {
        Assert.Null(JavaRequirementTable.AllowedRange(minecraftVersion, LoaderKind.Forge));
    }

    [Fact]
    public void 版本JSON声明的javaVersion抬升下界()
    {
        // GAME_JSON 优先于内置表：1.20.1 的表值是 17，版本 JSON 说 21 就按 21 算。
        var range = JavaRequirementTable.AllowedRange("1.20.1", LoaderKind.Vanilla, jsonJavaMajor: 21);

        Assert.True(range.HasValue);
        Assert.Equal(21, range!.Value.Min);
    }

    // ---------------- 条目 2：Evaluate 的 mandatory / suggested 区分 ----------------

    [Theory]
    [InlineData("1.12.2", LoaderKind.Forge, 17, "LAUNCH_WRAPPER")]
    [InlineData("1.12.2", LoaderKind.Vanilla, 17, "LAUNCH_WRAPPER")]
    [InlineData("1.16.5", LoaderKind.Forge, 21, "MODLAUNCHER_8")]
    [InlineData("1.17.1", LoaderKind.Forge, 21, "MODLAUNCHER_8")]
    [InlineData("1.20.1", LoaderKind.Forge, 8, "VANILLA")]
    [InlineData("1.20.5", LoaderKind.Forge, 17, "VANILLA")]
    [InlineData("26.1", LoaderKind.Vanilla, 21, "VANILLA")]
    public void 违反的强制规则带ID(string minecraftVersion, LoaderKind loader, int javaMajor, string expectedId)
    {
        var (mandatory, _) = JavaRequirementTable.Evaluate(
            minecraftVersion, loader, jsonJavaMajor: null, javaMajor);

        Assert.True(mandatory.HasValue, $"{minecraftVersion}（{loader}）+ Java {javaMajor} 应该被判为强制违规");
        Assert.True(mandatory!.Value.Mandatory);
        Assert.Equal(expectedId, mandatory.Value.Id);
    }

    [Theory]
    // 1.16.5 Forge + Java 17 是合法组合：MODDED_JAVA_8 只是建议，不能拦。
    [InlineData("1.16.5", LoaderKind.Forge, 17, "MODDED_JAVA_8")]
    // 1.17 Forge + Java 17 同理：MODDED_JAVA_16 只是建议。
    [InlineData("1.17", LoaderKind.Forge, 17, "MODDED_JAVA_16")]
    // 1.20.1 Forge + Java 21：MODDED_JAVA_17 只是建议，不阻断启动。
    [InlineData("1.20.1", LoaderKind.Forge, 21, "MODDED_JAVA_17")]
    // ≤1.7.2 的 Forge：MODDED_JAVA_7 建议 Java 7，LegacyModFixer 能修，所以也只是建议。
    [InlineData("1.6.4", LoaderKind.Forge, 8, "MODDED_JAVA_7")]
    public void 建议级违规不阻断(string minecraftVersion, LoaderKind loader, int javaMajor, string expectedSuggestedId)
    {
        var (mandatory, suggested) = JavaRequirementTable.Evaluate(
            minecraftVersion, loader, jsonJavaMajor: null, javaMajor);

        Assert.Null(mandatory);
        Assert.True(suggested.HasValue, $"{minecraftVersion}（{loader}）+ Java {javaMajor} 应该给出建议级提示");
        Assert.False(suggested!.Value.Mandatory);
        Assert.Equal(expectedSuggestedId, suggested.Value.Id);
    }

    [Theory]
    [InlineData("1.12.2", LoaderKind.Forge, 8)]
    [InlineData("1.16.5", LoaderKind.Forge, 8)]
    [InlineData("1.16.5", LoaderKind.NeoForge, 8)] // NeoForge 不吃 Forge 的 MODDED_* 建议
    [InlineData("1.17", LoaderKind.Vanilla, 16)]
    [InlineData("1.20.1", LoaderKind.Vanilla, 17)]
    [InlineData("1.20.1", LoaderKind.Forge, 17)]
    [InlineData("1.20.5", LoaderKind.Vanilla, 21)]
    [InlineData("1.20.5", LoaderKind.Forge, 21)]
    [InlineData("1.21.4", LoaderKind.NeoForge, 21)]
    [InlineData("26.1", LoaderKind.Vanilla, 25)]
    public void 完全符合的Java不报任何问题(string minecraftVersion, LoaderKind loader, int javaMajor)
    {
        var (mandatory, suggested) = JavaRequirementTable.Evaluate(
            minecraftVersion, loader, jsonJavaMajor: null, javaMajor);

        Assert.Null(mandatory);
        Assert.Null(suggested);
    }

    [Fact]
    public void 版本JSON的下界判定优先于内置表()
    {
        var (mandatory, _) = JavaRequirementTable.Evaluate(
            "1.20.1", LoaderKind.Vanilla, jsonJavaMajor: 21, javaMajor: 17);

        Assert.True(mandatory.HasValue);
        Assert.Equal("GAME_JSON", mandatory!.Value.Id);
        Assert.Equal(21, mandatory.Value.Java.Min);
    }

    [Fact]
    public void 未知版本或读不到版本号时不做判断()
    {
        var unknownVersion = JavaRequirementTable.Evaluate(
            "23w31a", LoaderKind.Forge, jsonJavaMajor: null, javaMajor: 17);
        Assert.Null(unknownVersion.Mandatory);
        Assert.Null(unknownVersion.Suggested);

        var unknownJava = JavaRequirementTable.Evaluate(
            "1.12.2", LoaderKind.Forge, jsonJavaMajor: null, javaMajor: 0);
        Assert.Null(unknownJava.Mandatory);
        Assert.Null(unknownJava.Suggested);
    }

    // ---------------- 条目 2：规则表本身 ----------------

    [Fact]
    public void 规则表包含HMCL的强制与建议规则()
    {
        var ids = JavaRequirementTable.All.Select(constraint => constraint.Id).ToList();

        Assert.Equal(8, JavaRequirementTable.All.Length);
        Assert.Contains("LAUNCH_WRAPPER", ids);
        Assert.Contains("MODLAUNCHER_8", ids);
        Assert.Contains("VANILLA_JAVA_8_51", ids);

        foreach (var id in new[] { "MODDED_JAVA_7", "MODDED_JAVA_8", "MODDED_JAVA_16", "MODDED_JAVA_17", "MODDED_JAVA_21" })
        {
            Assert.Contains(id, ids);
        }

        // MODDED_JAVA_* 在 HMCL 里就不是 mandatory，抄过来必须保持：否则 1.16.5 Forge + Java 17 会被拦死。
        Assert.All(
            JavaRequirementTable.All.Where(constraint => constraint.Id!.StartsWith("MODDED_", StringComparison.Ordinal)),
            constraint => Assert.False(constraint.Mandatory));

        // 上界规则必须是强制的，否则「无满足者返回 null」永远触发不了。
        Assert.All(
            JavaRequirementTable.All.Where(constraint => constraint.Id is "LAUNCH_WRAPPER" or "MODLAUNCHER_8"),
            constraint => Assert.True(constraint.Mandatory));

        // 只有 Forge 规则带加载器条件，原版不会被误伤。
        Assert.All(
            JavaRequirementTable.All.Where(constraint => constraint.RequiresLoader is not null),
            constraint => Assert.Equal(LoaderKind.Forge, constraint.RequiresLoader));
    }

    [Theory]
    [InlineData("1.7.10", 7010)]
    [InlineData("1.12.2", 12002)]
    [InlineData("1.20.4", 20004)]
    [InlineData("1.20.5", 20005)]
    [InlineData("1.21.11", 21011)]
    [InlineData("26.1", 26001)]
    [InlineData("26.3", 26003)]
    public void 游戏版本键可比较(string minecraftVersion, int expectedKey)
    {
        Assert.True(JavaRequirementTable.TryGetGameKey(minecraftVersion, out var gameKey));
        Assert.Equal(expectedKey, gameKey);
    }

    [Theory]
    [InlineData(8, 8, "Java 8")]
    [InlineData(8, 17, "Java 8–17")]
    [InlineData(21, int.MaxValue, "Java 21+")]
    public void 区间文案可读(int min, int max, string expected) =>
        Assert.Equal(expected, JavaRequirementTable.DescribeJavaRange((min, max)));

    // ---------------- JavaService 与约束集的衔接 ----------------

    [Fact]
    public void 服务层从版本JSON取约束集()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var versionDirectory = Path.Combine(paths.VersionsDirectory, "1.20.1");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, "1.20.1.json"),
            """{"id":"1.20.1","javaVersion":{"component":"java-runtime-delta","majorVersion":21}}""");

        using var factory = new DownloadHttpClientFactory(DownloadSource.BmclApi);
        using var service = new JavaService(
            new FakePlatformService(), paths, new DownloadQueue(factory), NullAppLog.Instance);

        var range = service.GetAllowedRange("1.20.1");

        Assert.True(range.HasValue);
        Assert.Equal(21, range!.Value.Min);
        // GetRequirement 保持原有语义（UI 文案仍在下界），两者必须同源。
        Assert.Equal(21, service.GetRequirement("1.20.1")!.RequiredMajor);
    }

    [Fact]
    public void 从区间内挑主版本最低的运行时()
    {
        var installed = new[] { NewJava(21), NewJava(17), NewJava(8) };

        // 1.12.2 + Forge 的区间是 (8,8)：Java 17/21 都在上界之外，只能给 8。
        Assert.Equal(8, JavaService.SelectForRange(installed, (8, 8))!.MajorVersion);
        // 1.16.5 + Forge 的区间是 (8,17)：取最低的 8，不能给 21。
        Assert.Equal(8, JavaService.SelectForRange(installed, (8, 17))!.MajorVersion);
        // 1.20.5 起的区间是 (21,∞)：只有 21 满足。
        Assert.Equal(21, JavaService.SelectForRange(installed, (21, int.MaxValue))!.MajorVersion);
        // 区间内没有候选时必须返回 null（调用方据此写「本机只有 …」）。
        Assert.Null(JavaService.SelectForRange(installed, (25, int.MaxValue)));
        // 未知版本没有区间：保持原有行为，取最高主版本。
        Assert.Equal(21, JavaService.SelectForRange(installed, null)!.MajorVersion);
        Assert.Null(JavaService.SelectForRange(Array.Empty<JavaInfo>(), (8, 8)));
    }

    private static JavaInfo NewJava(int major) => new()
    {
        Path = Path.Combine("C:", "java", major.ToString(System.Globalization.CultureInfo.InvariantCulture), "bin", "java.exe"),
        Version = major.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0.1",
        MajorVersion = major,
        Is64Bit = true,
    };

    // ---------------- 条目 4：扫描扩展 ----------------

    [Fact]
    public void 官方runtime布局被展开()
    {
        using var temp = new TempDirectory();
        var platformKey = PlatformService.IsWindows ? "windows-x64" : PlatformService.IsMacOs ? "mac-os" : "linux";
        var executableName = PlatformService.IsWindows ? "javaw.exe" : "java";

        var runtimeRoot = Path.Combine(temp.Path, "runtime");
        var expected = Path.Combine(runtimeRoot, "java-runtime-delta", platformKey, "java-runtime-delta", "bin", executableName);
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, string.Empty);

        // 平台键之外的目录不能被当成 runtime（HMCL 也只试已知平台键）。
        var decoy = Path.Combine(runtimeRoot, "java-runtime-beta", "not-a-platform", "java-runtime-beta", "bin", executableName);
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        File.WriteAllText(decoy, string.Empty);

        var expanded = JavaService.ExpandOfficialRuntimeLayout(runtimeRoot);

        Assert.Contains(expected, expanded, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(decoy, expanded, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 不存在的runtime目录返回空且不抛()
    {
        Assert.Empty(JavaService.ExpandOfficialRuntimeLayout(null));
        Assert.Empty(JavaService.ExpandOfficialRuntimeLayout(string.Empty));
        Assert.Empty(JavaService.ExpandOfficialRuntimeLayout(Path.Combine(Path.GetTempPath(), "nebula-no-such-runtime")));
    }

    [Fact]
    public void NEBULA_JAVA_PATHS里的目录被收集()
    {
        using var temp = new TempDirectory();
        var javaHome = Path.Combine(temp.Path, "jdk-21");
        var bin = Path.Combine(javaHome, "bin");
        Directory.CreateDirectory(bin);

        // 两个名字都建：Windows 走 java.exe / javaw.exe，其他平台走 java。
        File.WriteAllText(Path.Combine(bin, "java.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "java"), string.Empty);

        var missing = Path.Combine(temp.Path, "not-installed");
        var previous = Environment.GetEnvironmentVariable("NEBULA_JAVA_PATHS");
        Environment.SetEnvironmentVariable(
            "NEBULA_JAVA_PATHS",
            javaHome + Path.PathSeparator + missing + Path.PathSeparator + " ");

        try
        {
            using var factory = new DownloadHttpClientFactory(DownloadSource.BmclApi);
            using var service = new JavaService(
                new FakePlatformService(), new FakeLauncherPaths(temp.Path), new DownloadQueue(factory), NullAppLog.Instance);

            var candidates = service.CollectCandidates();

            Assert.Contains(candidates, path => path.StartsWith(javaHome, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(candidates, path => path.StartsWith(missing, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEBULA_JAVA_PATHS", previous);
        }
    }

    [Fact]
    public void Oracle的javapath条目被识别()
    {
        if (!PlatformService.IsWindows)
        {
            // 这条过滤规则只加在 Windows 分支上（HMCL JavaManager.java:449-454 同样按 Windows 判断）。
            return;
        }

        Assert.True(JavaService.IsOracleJavapathEntry(@"C:\Program Files\Common Files\Oracle\Java\javapath"));
        Assert.True(JavaService.IsOracleJavapathEntry(@"C:\PROGRAM FILES\COMMON FILES\ORACLE\JAVA\JAVAPATH"));
        Assert.False(JavaService.IsOracleJavapathEntry(@"C:\Program Files\Java\jdk-17\bin"));
        Assert.False(JavaService.IsOracleJavapathEntry(string.Empty));
        Assert.False(JavaService.IsOracleJavapathEntry(null));
    }

    [Fact]
    public void Windows的搜索根包含官方runtime与其他启动器目录()
    {
        if (!PlatformService.IsWindows)
        {
            return;
        }

        var roots = new PlatformService().JavaSearchRoots;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        Assert.Contains(Path.Combine(appData, ".minecraft", "runtime"), roots);
        Assert.Contains(
            Path.Combine(localAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
            roots);
        Assert.Contains(Path.Combine(appData, ".hmcl", "java"), roots);
        Assert.Contains(Path.Combine(appData, "PrismLauncher", "java"), roots);
        Assert.Contains(Path.Combine(appData, "ModrinthApp", "meta", "java_versions"), roots);

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            Assert.Contains(Path.Combine(programFilesX86, "Minecraft Launcher", "runtime"), roots);
        }
    }
}
