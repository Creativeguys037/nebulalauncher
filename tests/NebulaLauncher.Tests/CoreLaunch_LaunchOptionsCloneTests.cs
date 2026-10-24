using System.Reflection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// <see cref="LaunchOptions"/> 的复制路径（<c>GameLauncher.CloneWithMaxMemory</c>）的守护用例。
///
/// 为什么需要反射：<see cref="LaunchOptions"/> 是 class + init 属性，没有 <c>with</c> 表达式，
/// 自动内存路径只能逐字段复制。漏掉一个字段不会编译报错，只在「开了自动内存」的机器上
/// 静默回落到默认值——用户看到的是「我明明开了中文补全/崩溃归档，怎么不好使」。
/// 这类缺陷靠注释提醒不可靠，所以这里用反射遍历全部公开可读属性，漏一个就测试失败。
/// </summary>
public class CoreLaunch_LaunchOptionsCloneTests
{
    // ==================== 漏字段守护 ====================

    [Fact]
    public void CloneWithMaxMemory_CopiesEveryPropertyExceptMaxMemory()
    {
        var original = CreateFullyNonDefaultOptions();

        var clone = CloneWithMaxMemory(original, maxMemoryMb: 8192);

        // 唯一允许被改写的属性：克隆的目的就是换这个值。
        var allowedToChange = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LaunchOptions.MaxMemoryMb),
        };

        var mismatches = new List<string>();

        foreach (var property in ReadableProperties())
        {
            if (allowedToChange.Contains(property.Name))
            {
                continue;
            }

            var expected = property.GetValue(original);
            var actual = property.GetValue(clone);

            if (!ValueEquals(expected, actual))
            {
                mismatches.Add(
                    $"{property.Name}: 期望 {Describe(expected)}，实际 {Describe(actual)}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "CloneWithMaxMemory 漏复制或改写了以下属性（给 LaunchOptions 加字段时必须同步该方法）："
            + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", mismatches));
    }

    [Fact]
    public void CloneWithMaxMemory_ChangesOnlyMaxMemoryMb()
    {
        var original = CreateFullyNonDefaultOptions();

        var clone = CloneWithMaxMemory(original, maxMemoryMb: 8192);

        Assert.Equal(8192, clone.MaxMemoryMb);
        Assert.Equal(2048, original.MaxMemoryMb);
    }

    /// <summary>
    /// 守护用例的前提：样本里每个属性都<b>不是</b>默认值。
    /// 少了这条自检，某个属性哪天回到默认值，漏拷就再也测不出来（期望值与实际值恰好相等）。
    /// </summary>
    [Fact]
    public void SampleOptions_EveryPropertyDiffersFromItsDefault()
    {
        var sample = CreateFullyNonDefaultOptions();
        var defaults = new LaunchOptions
        {
            Account = new Account { Username = "default-user" },
            VersionName = "default-version",
        };

        var sameAsDefault = ReadableProperties()
            .Where(property => ValueEquals(property.GetValue(sample), property.GetValue(defaults)))
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            sameAsDefault.Count == 0,
            "以下属性在测试样本里仍等于默认值，漏拷将无法被测出：" + string.Join("、", sameAsDefault));
    }

    // ==================== 三个曾经漏掉的字段（精确回归） ====================

    [Fact]
    public void CloneWithMaxMemory_KeepsProcessPriority()
    {
        // 漏拷的表现：设置里选了「高优先级」，开自动内存后回到 Normal。
        var original = CreateFullyNonDefaultOptions();

        Assert.Equal(GameProcessPriority.High, CloneWithMaxMemory(original, 8192).ProcessPriority);
    }

    [Fact]
    public void CloneWithMaxMemory_KeepsAutoChineseLanguage()
    {
        var original = CreateFullyNonDefaultOptions();

        Assert.False(CloneWithMaxMemory(original, 8192).AutoChineseLanguage);
    }

    [Fact]
    public void CloneWithMaxMemory_KeepsArchiveCrashReport()
    {
        var original = CreateFullyNonDefaultOptions();

        Assert.False(CloneWithMaxMemory(original, 8192).ArchiveCrashReport);
    }

    [Fact]
    public void CloneWithMaxMemory_KeepsRelaxForgeChecks()
    {
        // 这个字段是 task-8 补进来的，守护用例同样盯着它，避免下一次改动把它挤掉。
        var original = CreateFullyNonDefaultOptions();

        Assert.False(CloneWithMaxMemory(original, 8192).RelaxForgeChecks);
    }

    // ==================== 样本与反射辅助 ====================

    /// <summary>
    /// 每个属性的值都与默认值不同。新增属性时这里会编译失败（init 属性写在对象初始化器里），
    /// 强迫作者同时想清楚「它该不该被 CloneWithMaxMemory 复制」。
    /// </summary>
    private static LaunchOptions CreateFullyNonDefaultOptions() => new()
    {
        Account = new Account
        {
            Id = "acc-0001",
            Type = AccountType.Authlib,
            Username = "probe-user",
            Uuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "token-value",
            RefreshToken = "refresh-value",
            SkinModel = "slim",
            AuthServerUrl = "https://example.com/api/yggdrasil",
        },
        VersionName = "1.20.1-probe",
        GameDirectory = @"D:\agent_workspace\.probe\data-clone\minecraft",
        JavaPath = @"C:\fake\javaw.exe",

        // 默认 null（不知道加载器版本）。样本必须给非 null 值，否则「漏拷」与「拷了但为 null」
        // 在断言里分不出来——这正是 SampleOptions_EveryPropertyDiffersFromItsDefault 要防的。
        // 取值挑一个真的会改变 Java 上限的 build（1.19.4 Forge 45.0.40 → 最高 Java 19），
        // 漏拷时不只是字段为空，而是整条 Java 上限判定静默失效。
        LoaderVersion = "45.0.40",
        // 与默认值 512 / 4096 都不同；MaxMemoryMb 会被克隆改写，这里只是保证「原对象未被改动」。
        MinMemoryMb = 1024,
        MaxMemoryMb = 2048,
        WindowWidth = 1280,
        WindowHeight = 720,
        Fullscreen = true,
        ServerAddress = "example.com:25565",
        ExtraJvmArguments = "-Dprobe.flag=1",
        ExtraGameArguments = "--demo",
        DemoMode = true,
        AutoMaxMemory = true,
        GcKind = GcKind.Z,
        WindowTitle = "{version} - {user}",
        MaximizeWindow = true,
        LaunchBeforeCommand = "echo before",
        LaunchAfterCommand = "echo after",
        WaitForLaunchCommands = false,
        ProcessPriority = GameProcessPriority.High,
        AutoChineseLanguage = false,
        ArchiveCrashReport = false,
        RelaxForgeChecks = false,

        // 默认是 null（跟随全局）。样本必须给个非 null 值，否则「漏拷」与「拷了但为 null」
        // 在断言里分不出来——这条正是 SampleOptions_EveryPropertyDiffersFromItsDefault 要防的。
        VersionArgumentInfo = "probe 整合包",

        // 同上：默认 null，表示「跟随全局 account 的验证服务器」。
        // 实例级覆盖（整合包自带的登录服）漏拷会让玩家在开自动内存后连回官方验证，
        // 症状是「登录成功但 skin / 用户名不对」，比直接报错更难查。
        InstanceAuthServerUrl = "https://example.com/api/yggdrasil/probe",
    };

    private static IEnumerable<PropertyInfo> ReadableProperties() =>
        typeof(LaunchOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0);

    /// <summary>
    /// 逐属性比较。用 <see cref="object.Equals(object, object)"/> 而不是引用比较：
    /// 字符串属性的复制是同一个实例，但哪天有人把某个字段换成值语义的对象，
    /// 这里仍然给出「内容相等」的结论，而不是误报。
    /// </summary>
    private static bool ValueEquals(object? left, object? right) => Equals(left, right);

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        _ => value.ToString() ?? value.GetType().Name,
    };

    /// <summary>
    /// 反射调用私有的 <c>CloneWithMaxMemory</c>。它是自动内存路径的内部实现，
    /// 没有公开入口能把「复制后的 LaunchOptions」取出来——公开路径只会返回命令行字符串。
    /// </summary>
    private static LaunchOptions CloneWithMaxMemory(LaunchOptions options, int maxMemoryMb)
    {
        var method = typeof(GameLauncher).GetMethod(
            "CloneWithMaxMemory",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "找不到 GameLauncher.CloneWithMaxMemory：方法被重命名或移走了，"
                + "请同步更新本测试（它是漏字段守护用例的唯一入口）。");

        return (LaunchOptions)method.Invoke(null, [options, maxMemoryMb])!;
    }

    // ==================== -Xmx / -Xms：与启动器内存设置的关系 ====================

    /// <summary>
    /// 用户在 <c>ExtraJvmArguments</c> 里写 <c>-Xmx4G</c> 时，命令行里到底有几条 <c>-Xmx</c>。
    ///
    /// 实测（CmlLib.Core 4.1.0）：<b>只有一条</b>，就是用户那条。MLauncher 在按
    /// <see cref="LaunchOptions.MaxMemoryMb"/> 生成内存参数之前会检查额外 JVM 参数，
    /// 已经有 <c>-Xmx</c> 就不再生成自己的——所以用户值生效，也不会出现两条并存。
    /// 这个检查是逐项的：用户没写 <c>-Xms</c>，启动器照旧按 MinMemoryMb 生成。
    /// </summary>
    [Fact]
    public async Task RealLaunch_UserXmx_NotDuplicatedAndWins()
    {
        var commandLine = await BuildCommandLineAsync(extraJvmArguments: "-Xmx4G");
        var tokens = Tokenize(commandLine);
        var jvm = JvmTokensOf(tokens, commandLine);

        var xmx = jvm.Where(token => token.StartsWith("-Xmx", StringComparison.Ordinal)).ToList();

        Assert.True(
            xmx.Count == 1,
            "期望只有用户那一条 -Xmx，实际 " + xmx.Count + " 条。" + DescribeJvm(jvm));
        Assert.Equal("-Xmx4G", xmx[0]);

        // 逐项判断的另一半证据：用户没写 -Xms，启动器仍然按 MinMemoryMb=512 生成。
        Assert.Contains("-Xms512m", jvm);
    }

    [Fact]
    public async Task RealLaunch_UserXms_NotDuplicated()
    {
        // 同一个机制的镜像用例：用户给了 -Xms，启动器不再生成 -Xms512m；
        // 但 -Xmx 用户没给，仍由启动器按 MaxMemoryMb=1024 生成。
        var commandLine = await BuildCommandLineAsync(extraJvmArguments: "-Xms2G");
        var tokens = Tokenize(commandLine);
        var jvm = JvmTokensOf(tokens, commandLine);

        var xms = jvm.Where(token => token.StartsWith("-Xms", StringComparison.Ordinal)).ToList();

        Assert.True(
            xms.Count == 1,
            "期望只有用户那一条 -Xms，实际 " + xms.Count + " 条。" + DescribeJvm(jvm));
        Assert.Equal("-Xms2G", xms[0]);
        Assert.Contains(jvm, token => token.StartsWith("-Xmx", StringComparison.Ordinal));
    }

    // ==================== 真实命令行辅助 ====================

    private static async Task<string> BuildCommandLineAsync(string? extraJvmArguments)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var launcher = new GameLauncher(paths, NullAppLog.Instance, new StubJavaService(), new StubVersionService());

        // MaxMemoryMb 保持 1024（AutoMaxMemory 关）：自动内存会按本机可用内存算出不定值，用例无法断言。
        return await launcher.BuildCommandLineAsync(CreateOptions(extraJvmArguments));
    }

    private static LaunchOptions CreateOptions(string? extraJvmArguments = null) => new()
    {
        Account = new Account
        {
            Type = AccountType.Offline,
            Username = "player",
            Uuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "token-value",
        },
        VersionName = "1.20.1",
        // 固定小内存：前置检查会拿可用内存做上界，用例不能因为机器内存小而变成 Blocker。
        MinMemoryMb = 512,
        MaxMemoryMb = 1024,
        ExtraJvmArguments = extraJvmArguments,
        // 不写 options.txt、不归档崩溃报告：用例只关心命令行。
        AutoChineseLanguage = false,
        ArchiveCrashReport = false,
    };

    private static List<string> Tokenize(string commandLine) =>
        [.. commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>主类之前的那段就是 JVM 参数（含 classpath）。</summary>
    private static List<string> JvmTokensOf(List<string> tokens, string commandLine)
    {
        var mainClass = tokens.IndexOf("net.minecraft.client.main.Main");
        Assert.True(mainClass > 0, "命令行里没有主类：" + commandLine);
        return [.. tokens.Take(mainClass)];
    }

    /// <summary>
    /// 失败信息只列 <c>-X</c> 开头的参数：完整命令行含几十个 classpath 片段，
    /// 原样塞进断言消息会淹没真正要看的那几项。
    /// </summary>
    private static string DescribeJvm(IReadOnlyList<string> jvm) =>
        "JVM 段里的 -X*：" + string.Join(
            " | ",
            jvm.Where(token => token.StartsWith("-X", StringComparison.Ordinal)));

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