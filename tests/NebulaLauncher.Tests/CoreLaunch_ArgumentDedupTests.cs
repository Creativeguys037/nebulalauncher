using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 启动参数去重的回归用例（PCL2 <c>DeduplicateJavaArguments</c> 的两种语义）。
///
/// 判据只有两条：
/// 1. 游戏参数里同键只留一条，且留下的是后出现的值——重复键会让 MC 把两条都判无效，
///    这是「设置里填了 --width 没用」的直接成因；
/// 2. JVM 参数不能被覆盖——<c>-Xmx</c> / <c>-javaagent</c> 的位置本身决定生效结果，
///    把后出现的值搬进先出现的位置会悄悄改变 JVM 的行为。
/// </summary>
public sealed class CoreLaunch_ArgumentDedupTests
{
    // ==================== 游戏参数：后出现者覆盖 ====================

    [Fact]
    public void GameArguments_LaterValueWins()
    {
        var result = LaunchArgumentDeduplicator.Deduplicate(["--width", "1280", "--width", "1920"], isJvmArguments: false);

        Assert.Equal(["--width", "1920"], result);
    }

    [Fact]
    public void GameArguments_NegativeValueIsNotTreatedAsKey()
    {
        // -50 以 - 开头，但 - 之后是数字，所以它是 -xPos 的值而不是新参数。
        // 判错会得到 [-xPos 23, -xPos] 这种把值吃掉的残破列表。
        var result = LaunchArgumentDeduplicator.Deduplicate(["-xPos", "23", "-xPos", "-50"], isJvmArguments: false);

        Assert.Equal(["-xPos", "-50"], result);
    }

    [Fact]
    public void GameArguments_KeepsEveryTweakClass()
    {
        // OptiFine 与 Forge 可以同时挂多个 tweakClass，覆盖掉其中一个会让另一套补丁失效。
        var result = LaunchArgumentDeduplicator.Deduplicate(
            ["--tweakClass", "optifine.OptiFineTweaker", "--tweakClass", "net.minecraftforge.fml.common.launcher.FMLTweaker"],
            isJvmArguments: false);

        Assert.Equal(4, result.Count);
        Assert.Equal("optifine.OptiFineTweaker", result[1]);
        Assert.Equal("net.minecraftforge.fml.common.launcher.FMLTweaker", result[3]);
    }

    [Fact]
    public void GameArguments_DropsExactlyRepeatedTweakClass()
    {
        // PCL2 对 --tweakClass 也不保留完全重复的一项：键和值都一样时没有任何额外效果。
        var result = LaunchArgumentDeduplicator.Deduplicate(["--tweakClass", "a", "--tweakClass", "a"], isJvmArguments: false);

        Assert.Equal(["--tweakClass", "a"], result);
    }

    [Fact]
    public void DuplicateStandaloneArguments_AreDropped()
    {
        var result = LaunchArgumentDeduplicator.Deduplicate(["--fullscreen", "--fullscreen"], isJvmArguments: false);

        Assert.Equal(["--fullscreen"], result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(LaunchArgumentDeduplicator.Deduplicate([], isJvmArguments: false));
        Assert.Empty(LaunchArgumentDeduplicator.Deduplicate([], isJvmArguments: true));
    }

    [Fact]
    public void KeyWithoutValue_IsKept()
    {
        // 末尾只有一个键时无法配成键值对，原样保留：缺值是用户写错了，让游戏自己报错。
        var result = LaunchArgumentDeduplicator.Deduplicate(["--tweakClass"], isJvmArguments: false);

        Assert.Equal(["--tweakClass"], result);
    }

    [Fact]
    public void ServerArguments_AreRemovedWhenLauncherOwnsServerAddress()
    {
        var result = LaunchArgumentDeduplicator.RemoveServerArguments(
            ["--server", "old.example", "--port", "25570", "--username", "player"]);

        Assert.Equal(["--username", "player"], result);
    }

    [Fact]
    public void ServerArguments_KeepMalformedMissingValuesOutOfTheResult()
    {
        var result = LaunchArgumentDeduplicator.RemoveServerArguments(
            ["--server", "--port", "25570", "--demo"]);

        Assert.Equal(["--demo"], result);
    }

    [Fact]
    public void ServerArguments_PreserveUnrelatedArguments()
    {
        var result = LaunchArgumentDeduplicator.RemoveServerArguments(
            ["--username", "player", "--server", "play.example", "--demo"]);

        Assert.Equal(["--username", "player", "--demo"], result);
    }

    [Fact]
    public void ServerArguments_RemoveQuickPlayAndEqualsForms()
    {
        var result = LaunchArgumentDeduplicator.RemoveServerArguments(
            ["--quickPlayMultiplayer", "old.example", "--server=older.example", "--port=25570",
                "--quickPlayMultiplayer=another.example", "--width", "1280"]);

        Assert.Equal(["--width", "1280"], result);
    }

    [Fact]
    public void GameArguments_KeepUnrelatedPairsInOrder()
    {
        var result = LaunchArgumentDeduplicator.Deduplicate(
            ["--width", "1280", "--username", "player", "--width", "1920"],
            isJvmArguments: false);

        Assert.Equal(["--width", "1920", "--username", "player"], result);
    }

    // ==================== JVM 参数：不覆盖 ====================

    [Fact]
    public void JvmArguments_DoNotOverwriteEarlierValue()
    {
        // PCL2 原文（ModLaunch.vb:1639-1649）只在游戏参数分支改值；JVM 分支仅在键与值都相同时丢弃。
        // 所以 -Dfoo 的两条不同取值都会留下，由 JVM 按「最后一个生效」自己裁决——
        // 这正是「不要覆盖」的落地方式：启动器不去动那套顺序。
        var result = LaunchArgumentDeduplicator.Deduplicate(["-Dfoo=1", "-Dfoo=2"], isJvmArguments: true);

        Assert.Equal(["-Dfoo=1", "-Dfoo=2"], result);
    }

    [Fact]
    public void JvmArguments_DropExactlyRepeatedPair()
    {
        var result = LaunchArgumentDeduplicator.Deduplicate(["-Dfoo=1", "-Dfoo=1"], isJvmArguments: true);

        Assert.Equal(["-Dfoo=1"], result);
    }

    [Fact]
    public void JvmArguments_KeepDistinctStandaloneOptions()
    {
        // -Xmx2G 这类是单 token；值不同就是两个独立开关，不能算重复。
        var result = LaunchArgumentDeduplicator.Deduplicate(
            ["-Xmx2G", "-Xmx4G", "-Xmx2G"],
            isJvmArguments: true);

        Assert.Equal(["-Xmx2G", "-Xmx4G"], result);
    }

    [Fact]
    public void JvmArguments_DoNotCollapseUserPairWithGeneratedPair()
    {
        // 生成器追加 -Dstdout.encoding=GB18030，用户又写了同键不同值：两条都留着，
        // JVM 取最后一条；启动器不替用户改值，也不动生成参数的位置。
        var result = LaunchArgumentDeduplicator.Deduplicate(
            ["-Dstdout.encoding=UTF-8", "-Dstdout.encoding=GB18030"],
            isJvmArguments: true);

        Assert.Equal(["-Dstdout.encoding=UTF-8", "-Dstdout.encoding=GB18030"], result);
    }

    // ==================== 窗口参数吸收 ====================

    [Fact]
    public void Absorb_MapsWindowOptionsToProperties()
    {
        var absorbed = LaunchArgumentDeduplicator.AbsorbKnownOptions(
            ["--width", "1280", "--height", "720", "--fullscreen"]);

        Assert.Empty(absorbed.Remaining);
        Assert.Equal(1280, absorbed.WindowWidth);
        Assert.Equal(720, absorbed.WindowHeight);
        Assert.True(absorbed.Fullscreen);
        Assert.Null(absorbed.Demo);
    }

    [Fact]
    public void Absorb_LeavesUnknownArgumentsUntouched()
    {
        var absorbed = LaunchArgumentDeduplicator.AbsorbKnownOptions(["--tweakClass", "a", "--demo"]);

        Assert.Equal(["--tweakClass", "a"], absorbed.Remaining);
        Assert.True(absorbed.Demo);
        Assert.Null(absorbed.WindowWidth);
    }

    [Fact]
    public void Absorb_KeepsUnparseableWidthForGameToReject()
    {
        // 非数字宽度不猜、不吞：原样留给游戏报参数错误，用户才能发现是自己写错了。
        var absorbed = LaunchArgumentDeduplicator.AbsorbKnownOptions(["--width", "abc"]);

        Assert.Equal(["--width", "abc"], absorbed.Remaining);
        Assert.Null(absorbed.WindowWidth);
    }

    // ==================== 真实参数组装 ====================

    [Fact]
    public async Task RealLaunch_UserWidthOverridesLauncherWidth()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        var commandLine = await launcher.BuildCommandLineAsync(CreateOptions("--width 1280 --width 1920 --tweakClass a --tweakClass b"));

        var gameArguments = GameArgumentsOf(commandLine);
        Console.WriteLine("游戏参数段: " + string.Join(' ', gameArguments));

        // CmlLib 会按 WindowWidth=854 自己生成一条 --width；用户的两条必须收敛成一条 1920，
        // 否则三条 --width 并存，MC 全部判无效，用户的设置静默失效。
        Assert.Equal(1, CountKey(gameArguments, "--width"));
        Assert.Equal("1920", ValueOf(gameArguments, "--width"));
        Assert.Equal(1, CountKey(gameArguments, "--height"));
        Assert.Equal("480", ValueOf(gameArguments, "--height"));

        // --tweakClass 不在覆盖范围内：两条不同值都要活着。
        Assert.Equal(2, CountKey(gameArguments, "--tweakClass"));
    }

    [Theory]
    [InlineData("new.example:25565", "new.example")]
    [InlineData("new.example:25571", "new.example:25571")]
    public async Task RealLaunch_ExplicitServerAddressDoesNotDuplicateUserServerArguments(
        string serverAddress, string expectedAddress)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        var options = CreateOptions(
            "--server old.example --port 25570 --demo",
            serverAddress: serverAddress);

        var commandLine = await launcher.BuildCommandLineAsync(options);
        var gameArguments = GameArgumentsOf(commandLine);

        Assert.Equal(1, CountKey(gameArguments, "--quickPlayMultiplayer"));
        Assert.Equal(expectedAddress, ValueOf(gameArguments, "--quickPlayMultiplayer"));
        Assert.Equal(0, CountKey(gameArguments, "--server"));
        Assert.Equal(0, CountKey(gameArguments, "--port"));
        Assert.DoesNotContain("old.example", gameArguments);
        Assert.DoesNotContain("25570", gameArguments);
    }

    [Fact]
    public async Task RealLaunch_WithoutServerAddressKeepsUserServerArguments()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");
        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        var commandLine = await launcher.BuildCommandLineAsync(
            CreateOptions("--server custom.example --port 25570"));
        var gameArguments = GameArgumentsOf(commandLine);

        Assert.Equal("custom.example", ValueOf(gameArguments, "--server"));
        Assert.Equal("25570", ValueOf(gameArguments, "--port"));
    }

    [Fact]
    public async Task RealLaunch_QuickPlayAddressDoesNotKeepLegacyServerArguments()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");
        var versionPath = Path.Combine(paths.VersionsDirectory, "1.20.1", "1.20.1.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(versionPath))!;
        manifest["arguments"]!["game"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""
            { "rules": [{ "action": "allow", "features": { "is_quick_play_multiplayer": true } }],
              "value": ["--quickPlayMultiplayer", "${quickPlayMultiplayer}"] }
            """));
        await File.WriteAllTextAsync(versionPath, manifest.ToJsonString());
        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        var commandLine = await launcher.BuildCommandLineAsync(CreateOptions(
            "--server old.example --port 25570 --quickPlayMultiplayer another.example",
            serverAddress: "new.example:25571"));
        var gameArguments = GameArgumentsOf(commandLine);

        Assert.Equal(1, CountKey(gameArguments, "--quickPlayMultiplayer"));
        Assert.Equal("new.example:25571", ValueOf(gameArguments, "--quickPlayMultiplayer"));
        Assert.Equal(0, CountKey(gameArguments, "--server"));
        Assert.Equal(0, CountKey(gameArguments, "--port"));
        Assert.DoesNotContain("old.example", gameArguments);
        Assert.DoesNotContain("another.example", gameArguments);
    }

    [Fact]
    public async Task RealLaunch_LegacyVersionUsesSingleExplicitServerAndPort()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.19.2");
        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        var commandLine = await launcher.BuildCommandLineAsync(CreateOptions(
            "--server old.example --port 25570",
            serverAddress: "new.example:25571", versionName: "1.19.2"));
        var gameArguments = GameArgumentsOf(commandLine);

        Assert.Equal(1, CountKey(gameArguments, "--server"));
        Assert.Equal("new.example", ValueOf(gameArguments, "--server"));
        Assert.Equal(1, CountKey(gameArguments, "--port"));
        Assert.Equal("25571", ValueOf(gameArguments, "--port"));
        Assert.Equal(0, CountKey(gameArguments, "--quickPlayMultiplayer"));
    }

    [Fact]
    public async Task RealLaunch_KeepsTweakClassAndDropsDuplicateJvmPair()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        var commandLine = await launcher.BuildCommandLineAsync(
            CreateOptions("--tweakClass a", "--Dcustom.flag=1 -Dcustom.flag=1"));

        var tokens = Tokenize(commandLine);

        // JVM 侧：完全相同的键值对只留一条，避免同一条 -D 在命令行里重复出现。
        Assert.Equal(1, tokens.Count(token => token == "-Dcustom.flag=1"));

        // 游戏侧：--tweakClass 保留。
        Assert.Equal(1, tokens.Count(token => token == "--tweakClass"));
    }

    // ==================== 辅助 ====================

    private static LaunchOptions CreateOptions(
        string extraGameArguments,
        string? extraJvmArguments = null,
        string? serverAddress = null,
        string versionName = "1.20.1") =>
        new()
        {
            Account = new Account
            {
                Type = AccountType.Offline,
                Username = "player",
                Uuid = "0123456789abcdef0123456789abcdef",
                AccessToken = "token-value",
            },
            VersionName = versionName,
            // 固定小内存：前置检查会拿可用内存做上界，用例不能因为机器内存小而变成 Blocker。
            MinMemoryMb = 512,
            MaxMemoryMb = 1024,
            // 故意用与 --width 1280 不同的默认值：用户参数若没生效，断言能直接看见 854。
            WindowWidth = 854,
            WindowHeight = 480,
            ExtraGameArguments = extraGameArguments,
            ExtraJvmArguments = extraJvmArguments,
            ServerAddress = serverAddress,
            AutoChineseLanguage = false,
            ArchiveCrashReport = false,
        };

    private static List<string> Tokenize(string commandLine) =>
        [.. commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>取主类之后的那段：之前是 JVM 参数与 classpath，与游戏参数无关。</summary>
    private static List<string> GameArgumentsOf(string commandLine)
    {
        var tokens = Tokenize(commandLine);
        var mainClass = tokens.IndexOf("net.minecraft.client.main.Main");
        Assert.True(mainClass >= 0, "命令行里没有主类：" + commandLine);
        return [.. tokens.Skip(mainClass + 1)];
    }

    private static int CountKey(IReadOnlyList<string> arguments, string key) =>
        arguments.Count(token => token == key);

    private static string? ValueOf(IReadOnlyList<string> arguments, string key)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] == key)
            {
                return arguments[index + 1];
            }
        }

        return null;
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
