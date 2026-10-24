using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 缺口 G9 / G10 的回归用例：
/// <list type="bullet">
/// <item><c>--versionType</c> 自定义：空值时连同值一起删掉，非空时原样传给 MC（含空格也安全）。</item>
/// <item><c>options.txt</c> 的语言：按 MC 版本分档（1.1~1.10 必须大写的 <c>zh_CN</c>）、
///       按 <c>saves</c> 判「是不是首次设置」、写两次触发缓存刷新、Yosbr mod 兼容路径。</item>
/// </list>
/// 所有文件操作都落在 <see cref="TempDirectory"/> 下，不触碰真实 <c>.minecraft</c>。
/// </summary>
public sealed class CoreLaunch_OptionsTxtTests
{
    // ==================== options.txt：版本分档 ====================

    /// <summary>1.1~1.5 必须写 <c>zh_CN</c>：写 <c>zh_cn</c> 会让这几版直接 NPE 崩溃。</summary>
    [Fact]
    public void Language_15_WritesUppercaseCode()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:en_us\nfov:0.5"));

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 5);

        Assert.NotNull(changed);
        Assert.Equal("lang:zh_CN", ReadLanguageLine(gameDir));
        // 其余键一个都不能丢：options.txt 里有上百个玩家的设置。
        Assert.Contains("fov:0.5", File.ReadAllLines(Path.Combine(gameDir, "options.txt")));
    }

    /// <summary>1.6~1.10 同样要大写，否则游戏静默回落英文。</summary>
    [Fact]
    public void Language_10_WritesUppercaseCode()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:en_us"));

        GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 10);

        Assert.Equal("lang:zh_CN", ReadLanguageLine(gameDir));
    }

    /// <summary>1.11+ 必须小写：写 <c>zh_CN</c> 会让语言设置显示成「英文」。</summary>
    [Fact]
    public void Language_20_WritesLowercaseCode()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:en_us"));

        GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        Assert.Equal("lang:zh_cn", ReadLanguageLine(gameDir));
    }

    /// <summary>1.0 及更早没有语言选项，整份文件都不该被动。</summary>
    [Fact]
    public void Language_10Release_LeavesFileUntouched()
    {
        const string content = "lang:en_us\nfov:0.5";
        var gameDir = CreateGameDirectory(("options.txt", content));

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 0);

        Assert.Null(changed);
        Assert.Equal(content, File.ReadAllText(Path.Combine(gameDir, "options.txt")));
    }

    // ==================== options.txt：saves 判据 ====================

    /// <summary>
    /// saves 不存在 = 首次设置，按启动器开关改成中文。
    /// 这正是「整包导入后第一次启动自动变中文」的路径。
    /// </summary>
    [Fact]
    public void Language_NoSavesDirectory_TreatsAsFirstRun()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:en_us"));

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        Assert.NotNull(changed);
        Assert.Equal("lang:zh_cn", ReadLanguageLine(gameDir));
    }

    /// <summary>
    /// saves 存在 = 玩家已经玩过，语言是他自己选的，不能按启动器开关改。
    /// 判据必须是 saves 而不是「当前语言是不是英文」：整合包可以自带一个 en_us 的 options.txt。
    /// </summary>
    [Fact]
    public void Language_ExistingSaves_KeepsPlayerChoice()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:ja_jp"), ("saves/keep/.keep", ""));

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        Assert.Null(changed);
        Assert.Equal("lang:ja_jp", ReadLanguageLine(gameDir));
    }

    /// <summary>
    /// saves 存在时只做「大小写规范化」：PCL2 的 <c>CurrentLang.Lower</c> 分支。
    /// 1.13+ 的 <c>zh_CN</c> 会被改成 <c>zh_cn</c>——这不是覆盖玩家的选择，是修正错误写法。
    /// </summary>
    [Fact]
    public void Language_ExistingSaves_NormalizesUppercaseCode()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:zh_CN"), ("saves/keep/.keep", ""));

        GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        Assert.Equal("lang:zh_cn", ReadLanguageLine(gameDir));
    }

    // ==================== options.txt：写两次 ====================

    /// <summary>
    /// 先写 <c>-</c> 再写目标值：游戏按内容缓存语言列表，直接覆盖同长度值不触发刷新。
    /// 用回调核对顺序，而不只是看最终内容（最终内容两种写法都一样）。
    /// </summary>
    [Fact]
    public void Language_WritesDashThenFinalValue()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:en_us"));
        var writes = new List<string>();

        GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20, writes.Add);

        Assert.Equal(["-", "zh_cn"], writes);
    }

    /// <summary>已经等于目标值时不该再写一次——否则每次启动都动一次文件。</summary>
    [Fact]
    public void Language_AlreadyCorrect_WritesNothing()
    {
        var gameDir = CreateGameDirectory(("options.txt", "lang:zh_cn"));
        var writes = new List<string>();

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20, writes.Add);

        Assert.Null(changed);
        Assert.Empty(writes);
    }

    // ==================== options.txt：Yosbr 与缺文件 ====================

    /// <summary>
    /// Yosbr mod 把默认 options.txt 放在 config/yosbr/ 下并在启动时覆盖根目录那份：
    /// 只改根目录等于没改。它的 lang 先被写成 none（忽略默认语言），再写目标值。
    /// </summary>
    [Fact]
    public void Language_YosbrFile_UsedWhenRootOptionsMissing()
    {
        var gameDir = CreateGameDirectory(("config/yosbr/options.txt", "lang:en_us"));
        var writes = new List<string>();

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20, writes.Add);

        Assert.NotNull(changed);
        Assert.Equal(
            Path.Combine(gameDir, "config", "yosbr", "options.txt"),
            changed);
        Assert.Equal([IgnoreLanguageProbe, "-", "zh_cn"], writes);

        // 根目录那份不存在：启动器不新建，交给游戏自己生成完整默认值。
        Assert.False(File.Exists(Path.Combine(gameDir, "options.txt")));
    }

    /// <summary>两份都不存在：什么都不做（新建 options.txt 会丢玩家全部默认设置）。</summary>
    [Fact]
    public void Language_NoOptionsFile_DoesNothing()
    {
        var gameDir = CreateGameDirectory();

        var changed = GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        Assert.Null(changed);
        Assert.False(File.Exists(Path.Combine(gameDir, "options.txt")));
    }

    /// <summary>
    /// Yosbr 路径即使实例已经玩过（saves 存在）也会被改成中文——这是 PCL2 的既成行为，不是本实现的取舍：
    /// 它先把 Yosbr 的 lang 写成 <c>none</c>，随后读到 <c>none</c>，<c>saves</c> 判据已经不起作用。
    /// 保留这条用例是为了防止后来的人把它当 bug「修」掉，从而和上游分叉。
    /// </summary>
    [Fact]
    public void Language_YosbrWithSaves_StillWritesChinese()
    {
        var gameDir = CreateGameDirectory(
            ("config/yosbr/options.txt", "lang:ja_jp"),
            ("saves/keep/.keep", ""));

        GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        var yosbr = Path.Combine(gameDir, "config", "yosbr", "options.txt");
        Assert.Contains("lang:zh_cn", File.ReadAllLines(yosbr));
    }

    /// <summary>文件里没有 lang 行时追加一行，其余内容保留。</summary>
    [Fact]
    public void Language_MissingLangLine_Appends()
    {
        var gameDir = CreateGameDirectory(("options.txt", "fov:0.5\nrenderDistance:8"));

        GameLauncher.ApplyChineseLanguageCore(gameDir, vanillaMinor: 20);

        var lines = File.ReadAllLines(Path.Combine(gameDir, "options.txt"));
        Assert.Contains("lang:zh_cn", lines);
        Assert.Contains("fov:0.5", lines);
        Assert.Contains("renderDistance:8", lines);
    }

    /// <summary>语言标记 none 的探针值，与实现里的常量保持一致（测试独立写死，防止实现悄悄改值）。</summary>
    private const string IgnoreLanguageProbe = "none";

    // ==================== --versionType ====================

    /// <summary>
    /// 没配置版本信息时必须把 <c>--versionType</c> 和它的值两个 token 一起删掉。
    /// 留空串会让主菜单标题变成空白，留默认的 release 会盖掉整合包名。
    /// </summary>
    [Fact]
    public async Task VersionType_NotConfigured_RemovesKeyAndValue()
    {
        var commandLine = await BuildCommandLineAsync(versionArgumentInfo: null);

        var tokens = Tokenize(commandLine);
        Assert.DoesNotContain("--versionType", tokens);
        // CmlLib 会拼出 --versionType ""；空 token 也必须消失，否则那是「一个空字符串值」。
        Assert.DoesNotContain("", tokens);

        Console.WriteLine("主类之后的参数段: " + string.Join(' ', GameArgumentsOf(tokens)));
    }

    /// <summary>空串与 null 等价：界面上清空输入框就是「不显示版本信息」。</summary>
    [Fact]
    public async Task VersionType_EmptyString_RemovesKeyAndValue()
    {
        var commandLine = await BuildCommandLineAsync(versionArgumentInfo: "   ");

        Assert.DoesNotContain("--versionType", Tokenize(commandLine));
    }

    /// <summary>自定义值原样传给 MC，含空格时由 CmlLib 自动加引号，切分不会被破坏。</summary>
    [Fact]
    public async Task VersionType_CustomValueWithSpaces_IsQuoted()
    {
        const string value = "Example 服务端";
        var commandLine = await BuildCommandLineAsync(value);

        Assert.Contains("--versionType \"Example 服务端\"", commandLine, StringComparison.Ordinal);

        var start = commandLine.IndexOf("--versionType", StringComparison.Ordinal);
        Console.WriteLine("版本信息段: " + commandLine[start..]);
    }

    // ==================== 辅助 ====================

    /// <summary>在临时目录里造一个游戏目录，可选按 <c>相对路径 → 内容</c> 写文件。</summary>
    private static string CreateGameDirectory(params (string RelativePath, string Content)[] files)
    {
        var temp = new TempDirectory();
        var gameDir = temp.Combine("game");
        Directory.CreateDirectory(gameDir);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(gameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            if (Path.GetFileName(fullPath) == ".keep")
            {
                // 占位文件：目录必须存在，内容无所谓。
                File.WriteAllText(fullPath, string.Empty);
                continue;
            }

            File.WriteAllText(fullPath, content);
        }

        // 临时目录的清理交给进程退出，这里只保证用例期间存在。
        return gameDir;
    }

    private static string? ReadLanguageLine(string gameDir)
    {
        foreach (var line in File.ReadAllLines(Path.Combine(gameDir, "options.txt")))
        {
            if (line.StartsWith("lang:", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }

    private static async Task<string> BuildCommandLineAsync(string? versionArgumentInfo)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var settings = new StubSettingsService();
        settings.Current.VersionArgumentInfo = versionArgumentInfo ?? string.Empty;

        var launcher = new GameLauncher(
            paths,
            NullAppLog.Instance,
            new StubJavaService(),
            new StubVersionService(),
            settings: settings);

        return await launcher.BuildCommandLineAsync(new LaunchOptions
        {
            Account = new Account
            {
                Type = AccountType.Offline,
                Username = "player",
                Uuid = "0123456789abcdef0123456789abcdef",
                AccessToken = "token-value",
            },
            VersionName = "1.20.1",
            MinMemoryMb = 512,
            MaxMemoryMb = 1024,
            // 不写 options.txt、不归档崩溃报告：用例只关心命令行。
            AutoChineseLanguage = false,
            ArchiveCrashReport = false,
        });
    }

    private static List<string> Tokenize(string commandLine) =>
        [.. commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>主类之后的那一段就是游戏参数；报告里只贴这一段，避免整条 classpath 淹没证据。</summary>
    private static List<string> GameArgumentsOf(List<string> tokens)
    {
        var mainClass = tokens.IndexOf("net.minecraft.client.main.Main");
        Assert.True(mainClass >= 0, "命令行里没有主类");
        return [.. tokens.Skip(mainClass + 1)];
    }

    /// <summary>设置服务替身：只需要 Current，事件用空访问器避免 CS0067。</summary>
    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>写一份 CmlLib 能据此拼出完整命令行的版本 JSON（无库、无资源文件，纯离线）。</summary>
    private static void WriteLaunchableVersion(string versionsRoot, string name)
    {
        var directory = Path.Combine(versionsRoot, name);
        Directory.CreateDirectory(directory);

        var json = $$"""
                     {
                       "id": "{{name}}",
                       "type": "release",
                       "mainClass": "net.minecraft.client.main.Main",
                       "assets": "5",
                       "assetIndex": {
                         "id": "5",
                         "sha1": "0000000000000000000000000000000000000000",
                         "size": 0,
                         "totalSize": 0,
                         "url": "https://example.invalid/5.json"
                       },
                       "downloads": {
                         "client": {
                           "sha1": "0000000000000000000000000000000000000000",
                           "size": 0,
                           "url": "https://example.invalid/client.jar"
                         }
                       },
                       "libraries": [],
                       "arguments": {
                         "game": [
                           "--username", "${auth_player_name}",
                           "--version", "${version_name}",
                           "--gameDir", "${game_directory}",
                           "--assetsDir", "${assets_root}",
                           "--assetIndex", "${assets_index_name}",
                           "--uuid", "${auth_uuid}",
                           "--accessToken", "${auth_access_token}"
                         ],
                         "jvm": []
                       },
                       "javaVersion": { "component": "jdk-17", "majorVersion": 17 }
                     }
                     """;

        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    /// <summary>固定返回一个 64 位 Java 21。</summary>
    private sealed class StubJavaService : IJavaService
    {
        private static readonly JavaInfo Java = new()
        {
            Path = @"C:\fake\javaw.exe",
            Version = "21.0.1",
            MajorVersion = 21,
            Is64Bit = true,
            Vendor = "Test",
        };

        public Task<IReadOnlyList<JavaInfo>> DetectAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaInfo>>([Java]);

        public Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaInfo?>(Java);

        public JavaRequirement? GetRequirement(string minecraftVersion) => null;

        public (int Min, int Max)? GetAllowedRange(string minecraftVersion, LoaderKind loader) => null;

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaInfo?>(Java);

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            LoaderKind loader,
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaInfo?>(Java);

        public Task<string?> InstallRuntimeAsync(
            JavaRuntimePackage package,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> DownloadRuntimeAsync(
            int majorVersion,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>没有已安装版本的版本服务：只影响发行年与前置检查的加载器项，不阻断用例。</summary>
    private sealed class StubVersionService : IVersionService
    {
        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionInfo>>([]);

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionInfo?>(null);

        public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordLaunchAsync(
            string versionName,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
            LoaderKind loader,
            string minecraftVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LoaderVersion>>([]);
    }
}