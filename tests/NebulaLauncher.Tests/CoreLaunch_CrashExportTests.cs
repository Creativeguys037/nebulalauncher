using System.IO.Compression;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Logging;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 崩溃报告导出。核心断言是**打开导出的 zip 真的读不到凭据**：
/// 只断言「调了 Redact」没有意义，必须对着落盘文件检查。
/// </summary>
public class CoreLaunch_CrashExportTests
{
    // 真实格式样本里的凭据值。这些字符串在导出结果里一个都不许出现。
    private const string AccessToken = "abc123";
    private const string ClientToken = "d4e5f6a7b8c9";
    private const string UserName = "alice";
    private const string HomePath = @"C:\Users\alice\AppData\Roaming\.minecraft";

    // ==================== 导出结构 ====================

    [Fact]
    public async Task 导出zip包含六个条目()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: SampleHsErr());

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ArchivePath);
        Assert.True(File.Exists(result.ArchivePath));

        var names = ReadEntryNames(result.ArchivePath!);

        Assert.Contains("crash-analysis.txt", names);
        Assert.Contains("game-output.txt", names);
        Assert.Contains("hs_err.txt", names);
        Assert.Contains("launcher-log.txt", names);
        Assert.Contains("mods-list.txt", names);
        Assert.Contains("system.txt", names);
        Assert.Equal(6, names.Count);
        Assert.Equal(6, result.Entries!.Count);
    }

    [Fact]
    public async Task 没有hs_err时写五个条目()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        Assert.True(result.Success, result.Error);

        var names = ReadEntryNames(result.ArchivePath!);

        Assert.Equal(5, names.Count);
        Assert.DoesNotContain("hs_err.txt", names);
        Assert.Contains("mods-list.txt", names);
    }

    [Fact]
    public async Task 分析文件头部写明版本时间与脱敏说明()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        var analysis = ReadEntry(result.ArchivePath!, "crash-analysis.txt");

        Assert.Contains("Nebula Launcher 崩溃报告", analysis, StringComparison.Ordinal);
        Assert.Contains("导出时间:", analysis, StringComparison.Ordinal);
        Assert.Contains("启动器版本: 9.9.9-test", analysis, StringComparison.Ordinal);
        Assert.Contains("脱敏说明:", analysis, StringComparison.Ordinal);
        Assert.Contains("[结论]", analysis, StringComparison.Ordinal);
        Assert.Contains("[建议]", analysis, StringComparison.Ordinal);
        Assert.Contains("[疑似模组]", analysis, StringComparison.Ordinal);
        Assert.Contains("[原始输出尾部]", analysis, StringComparison.Ordinal);

        // 结论与建议正文
        Assert.Contains("NoSuchMethodError", analysis, StringComparison.Ordinal);
        Assert.Contains("模组不兼容", analysis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 系统快照含平台_Java_游戏版本与加载器()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        var system = ReadEntry(result.ArchivePath!, "system.txt");

        Assert.Contains("平台:", system, StringComparison.Ordinal);
        Assert.Contains(".NET:", system, StringComparison.Ordinal);
        Assert.Contains("Java 版本: 17.0.9", system, StringComparison.Ordinal);
        Assert.Contains("游戏版本: 1.21.4", system, StringComparison.Ordinal);
        Assert.Contains("加载器: Fabric", system, StringComparison.Ordinal);
        Assert.Contains("可用内存:", system, StringComparison.Ordinal);
    }

    // ==================== 脱敏（核心） ====================

    [Fact]
    public async Task 导出后zip里不含访问令牌()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        foreach (var (name, content) in ReadAllEntries(result.ArchivePath!))
        {
            Assert.DoesNotContain(AccessToken, content, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientToken, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 导出后zip里不含账户名()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        foreach (var (name, content) in ReadAllEntries(result.ArchivePath!))
        {
            Assert.DoesNotContain(UserName, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 路径里的用户名被替换但其余部分保留()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        var gameOutput = ReadEntry(result.ArchivePath!, "game-output.txt");

        // 用户段替换成占位符
        Assert.Contains(@"C:\Users\<user>\AppData\Roaming\.minecraft", gameOutput, StringComparison.Ordinal);

        // 路径其余部分必须还在，否则接收方无法判读是哪个子目录出的问题
        Assert.Contains(@"\config\brokenmod.toml", gameOutput, StringComparison.Ordinal);
        Assert.Contains(@"\logs\latest.log", gameOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 崩溃报告路径也经过脱敏()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null);

        var analysis = ReadEntry(result.ArchivePath!, "crash-analysis.txt");

        Assert.Contains(@"C:\Users\<user>\AppData\Roaming\.minecraft\crash-reports", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain(UserName, analysis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task hs_err与启动器日志同样脱敏()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: SampleHsErr());

        var hsErr = ReadEntry(result.ArchivePath!, "hs_err.txt");
        var launcherLog = ReadEntry(result.ArchivePath!, "launcher-log.txt");

        Assert.DoesNotContain(UserName, hsErr, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, hsErr, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\<user>\", hsErr, StringComparison.Ordinal);

        // 启动器日志是真实写进临时目录的，内容要能读到且脱敏
        Assert.Contains("启动器已就绪", launcherLog, StringComparison.Ordinal);
        Assert.DoesNotContain(UserName, launcherLog, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, launcherLog, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor覆盖命令行参数形态()
    {
        var text = "args [--username, alice, --uuid, 069a79f444e94726a5befca90e38aaf5, --accessToken, abc123]";

        var redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(UserName, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("069a79f444e94726a5befca90e38aaf5", redacted, StringComparison.Ordinal);
        Assert.Contains("--username", redacted, StringComparison.Ordinal);
        Assert.Contains("--accessToken", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor覆盖AccessTokenJSON与Bearer()
    {
        var text = "{\"accessToken\":\"abc123\",\"clientToken\":\"d4e5f6a7b8c9\"} Authorization: Bearer eyJhbGciOiJIUzI1NiJ9";

        var redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain(AccessToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted, StringComparison.Ordinal);
        Assert.Contains("Authorization:", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor不误伤URL里的home路径()
    {
        const string text = "下载失败：https://example.com/home/alice/report.zip";

        var redacted = LogRedactor.Redact(text);

        // URL 不是本机主目录，不该被替换
        Assert.Contains("example.com/home/alice/report.zip", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor对空输入原样返回()
    {
        Assert.Null(LogRedactor.Redact(null));
        Assert.Equal(string.Empty, LogRedactor.Redact(string.Empty));
    }

    // ==================== hs_err 与 mods 列表 ====================

    [Fact]
    public async Task hs_err条目包含两份文件的分隔标记()
    {
        using var temp = new TempDirectory();
        var first = temp.Combine("hs_err_pid111.log");
        var second = temp.Combine("hs_err_pid222.log");

        File.WriteAllText(first, SampleHsErr());
        File.WriteAllText(second,
            "# A fatal error has been detected by the Java Runtime Environment" + Environment.NewLine +
            "# Problematic frame:" + Environment.NewLine +
            "# C  [win32u.dll+0x2b1c]" + Environment.NewLine +
            @"# java.exe path: C:\Users\alice\AppData\Roaming\.minecraft\runtime\bin\javaw.exe");

        var sections = GameLauncher.ReadHsErrSections([first, second]);
        Assert.Equal(2, sections.Count);

        var result = await ExportAsync(temp, hsErr: string.Join(Environment.NewLine, sections));

        Assert.True(result.Success, result.Error);

        var hsErr = ReadEntry(result.ArchivePath!, "hs_err.txt");

        Assert.Contains("===== hs_err_pid111.log =====", hsErr, StringComparison.Ordinal);
        Assert.Contains("===== hs_err_pid222.log =====", hsErr, StringComparison.Ordinal);
        Assert.Contains("nvoglv64.dll", hsErr, StringComparison.Ordinal);
        Assert.Contains("win32u.dll", hsErr, StringComparison.Ordinal);

        // 新增条目同样走脱敏链路。
        Assert.DoesNotContain(UserName, hsErr, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, hsErr, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\<user>\", hsErr, StringComparison.Ordinal);
    }

    [Fact]
    public void 崩溃场景的HsErrLogs非空且默认是空集合()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("hs_err_pid777.log");
        File.WriteAllText(path, SampleHsErr());

        var sections = GameLauncher.ReadHsErrSections([path]);
        var crashed = GameLauncher.BuildExitResult(
            exitCode: 1,
            duration: TimeSpan.FromSeconds(3),
            crashAnalysis: "疑似显卡驱动崩溃。",
            crashReport: null,
            hsErrLogs: sections);

        Assert.True(crashed.Started);
        Assert.Single(crashed.HsErrLogs);
        Assert.Contains("===== hs_err_pid777.log =====", crashed.HsErrLogs[0], StringComparison.Ordinal);
        Assert.Contains("nvoglv64.dll", crashed.HsErrLogs[0], StringComparison.Ordinal);

        // 非崩溃 / 未读到 hs_err 的路径默认是空集合，不是 null。
        Assert.Empty(new LaunchResult(true, 0, TimeSpan.Zero).HsErrLogs);
    }

    [Fact]
    public async Task mods列表按文件名排序并统计启用禁用()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<ModSnapshot> mods =
        [
            new("zeta.jar", Enabled: true, SizeBytes: 2_097_152, VersionText: "2.0.0"),
            new("alpha.jar.disabled", Enabled: false, SizeBytes: 1024),
            new("beta.jar", Enabled: true, SizeBytes: 1536, VersionText: "1.2.3"),
        ];

        var result = await ExportAsync(temp, hsErr: null, mods: mods);

        Assert.True(result.Success, result.Error);
        var list = ReadEntry(result.ArchivePath!, "mods-list.txt");

        Assert.Contains("总数: 3（启用: 2，禁用: 1）", list, StringComparison.Ordinal);
        Assert.Contains("[启用] beta.jar（版本 1.2.3）  1.5 KB", list, StringComparison.Ordinal);
        Assert.Contains("[启用] zeta.jar（版本 2.0.0）  2.0 MB", list, StringComparison.Ordinal);
        Assert.Contains("[禁用] alpha.jar.disabled  1.0 KB", list, StringComparison.Ordinal);

        var alpha = list.IndexOf("alpha.jar.disabled", StringComparison.Ordinal);
        var beta = list.IndexOf("beta.jar", StringComparison.Ordinal);
        var zeta = list.IndexOf("zeta.jar", StringComparison.Ordinal);

        Assert.True(alpha >= 0 && beta > alpha && zeta > beta, "mods-list.txt 应按文件名排序");
    }

    [Fact]
    public async Task 无mods时写未安装模组()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null, mods: Array.Empty<ModSnapshot>());

        Assert.True(result.Success, result.Error);
        var list = ReadEntry(result.ArchivePath!, "mods-list.txt");

        Assert.Contains("总数: 0（启用: 0，禁用: 0）", list, StringComparison.Ordinal);
        Assert.Contains("未安装模组", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task mods读取失败写说明且不影响导出()
    {
        using var temp = new TempDirectory();
        var result = await ExportAsync(temp, hsErr: null, modsError: "读取 mods 目录失败：拒绝访问。");

        Assert.True(result.Success, result.Error);
        var list = ReadEntry(result.ArchivePath!, "mods-list.txt");

        Assert.Contains("读取 mods 目录失败：拒绝访问。", list, StringComparison.Ordinal);
        Assert.DoesNotContain("未安装模组", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task mods列表里的令牌与用户名同样被脱敏()
    {
        using var temp = new TempDirectory();
        IReadOnlyList<ModSnapshot> mods =
        [
            new("mod.jar", true, 10, @"accessToken=abc123 C:\Users\alice\AppData\Roaming\.minecraft"),
        ];

        var result = await ExportAsync(temp, hsErr: null, mods: mods);

        Assert.True(result.Success, result.Error);
        var list = ReadEntry(result.ArchivePath!, "mods-list.txt");

        Assert.DoesNotContain(AccessToken, list, StringComparison.Ordinal);
        Assert.DoesNotContain(UserName, list, StringComparison.Ordinal);
        Assert.Contains("<user>", list, StringComparison.Ordinal);
    }

    // ==================== 文件命名与目录校验 ====================

    [Fact]
    public async Task 同名文件自动加序号不覆盖()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("export");

        var first = await ExportAsync(temp, hsErr: null, targetDirectory: directory);
        var second = await ExportAsync(temp, hsErr: null, targetDirectory: directory);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.ArchivePath, second.ArchivePath);
        Assert.True(File.Exists(first.ArchivePath));
        Assert.True(File.Exists(second.ArchivePath));
        Assert.Contains("-2.zip", second.ArchivePath!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空目录参数直接失败不抛异常()
    {
        using var temp = new TempDirectory();
        var service = new CrashExportService(new FakeLauncherPaths(temp.Path), new LauncherLogReader(new FakeLauncherPaths(temp.Path)));

        var result = await service.ExportAsync(Request(), "   ");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void CanWriteTo_存在的目录为真_不存在的为假()
    {
        using var temp = new TempDirectory();
        var service = new CrashExportService(new FakeLauncherPaths(temp.Path));

        Assert.True(service.CanWriteTo(temp.Path));
        Assert.False(service.CanWriteTo(Path.Combine(temp.Path, "no-such-dir")));
        Assert.False(service.CanWriteTo(null));
        Assert.False(service.CanWriteTo("  "));
    }

    [Fact]
    public async Task 导出到不存在的目录会自动创建()
    {
        using var temp = new TempDirectory();
        var nested = Path.Combine(temp.Path, "a", "b", "c");

        var result = await ExportAsync(temp, hsErr: null, targetDirectory: nested);

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(result.ArchivePath));
    }

    // ==================== 辅助 ====================

    private static IReadOnlyList<string> SampleGameOutput() =>
    [
        "[13:40:00] [main/INFO]: Setting user: alice",
        "--username, alice, --version, 1.21.4, --accessToken, abc123",
        "Access token: abc123",
        "{\"accessToken\":\"abc123\",\"clientToken\":\"d4e5f6a7b8c9\"}",
        "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9",
        "[13:40:05] [main/INFO]: Loaded 6 mods",
        "[13:40:06] [main/ERROR]: java.lang.NoSuchMethodError: net.example.Broken.init",
        "   at com.brokenmod.BrokenMod.init(BrokenMod.java:42)",
        @"[13:40:07] [main/INFO]: 读取配置 " + @"C:\Users\alice\AppData\Roaming\.minecraft\config\brokenmod.toml",
        @"[13:40:08] [main/INFO]: 日志 " + @"C:\Users\alice\AppData\Roaming\.minecraft\logs\latest.log",
    ];

    private static string SampleHsErr() =>
        "# A fatal error has been detected by the Java Runtime Environment" + Environment.NewLine +
        "# Problematic frame:" + Environment.NewLine +
        "# C  [nvoglv64.dll+0x9a1234]" + Environment.NewLine +
        @"# java.exe path: C:\Users\alice\AppData\Roaming\.minecraft\runtime\bin\javaw.exe" + Environment.NewLine +
        "# accessToken: abc123";

    private static CrashExportRequest Request(
        string? hsErr = null,
        IReadOnlyList<ModSnapshot>? mods = null,
        string? modsError = null) =>
        new(
            new CrashReport(
                IsCrash: true,
                Summary: "疑似模组 brokenmod 导致的 NoSuchMethodError。",
                Suggestions:
                [
                    new CrashSuggestion(
                        "模组不兼容",
                        "移除或升级 brokenmod 后再启动。",
                        CrashSeverity.Error,
                        CrashAnalyzer.Actions.OpenModsFolder),
                ],
                CrashReportPath: @"C:\Users\alice\AppData\Roaming\.minecraft\crash-reports\crash-2026-10-22_13.40.00-client.txt"),
            SampleGameOutput(),
            hsErr,
            new DiagnosticsInput
            {
                LauncherVersion = "9.9.9-test",
                DotNetVersion = ".NET 8.0.0",
                PlatformName = "Windows",
                DataDirectory = @"C:\Users\alice\AppData\Roaming\NebulaLauncher",
                GameDirectory = HomePath,
                AvailableMemoryBytes = 8L * 1024 * 1024 * 1024,
                JavaVersion = "17.0.9",
                MinecraftVersion = "1.21.4",
                Loader = "Fabric",
            },
            mods,
            modsError);

    private static async Task<CrashExportResult> ExportAsync(
        TempDirectory temp,
        string? hsErr,
        string? targetDirectory = null,
        IReadOnlyList<ModSnapshot>? mods = null,
        string? modsError = null)
    {
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLauncherLog(paths.LogsDirectory);

        var service = new CrashExportService(paths, new LauncherLogReader(paths));
        return await service.ExportAsync(Request(hsErr, mods, modsError), targetDirectory ?? temp.Combine("export"));
    }

    private static void WriteLauncherLog(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        var lines = new[]
        {
            "2026-10-22 13:39:58.100 [INFO ] 启动器已就绪",
            @"2026-10-22 13:39:59.200 [INFO ] 游戏目录：C:\Users\alice\AppData\Roaming\.minecraft",
            "2026-10-22 13:40:00.300 [DEBUG] args [--username, alice, --accessToken, abc123]",
            "2026-10-22 13:40:06.900 [ERROR] 游戏异常退出，退出码 1",
        };

        File.WriteAllText(
            Path.Combine(logsDirectory, "launcher-20261022.log"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static List<string> ReadEntryNames(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(entry => entry.FullName).ToList();
    }

    private static string ReadEntry(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    private static IEnumerable<(string Name, string Content)> ReadAllEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            // 先读完再 yield：yield 会挂起当前迭代，reader 若留到 yield 之后就会一直开着。
            string content;
            using (var reader = new StreamReader(entry.Open()))
            {
                content = reader.ReadToEnd();
            }

            yield return (entry.FullName, content);
        }
    }
}
