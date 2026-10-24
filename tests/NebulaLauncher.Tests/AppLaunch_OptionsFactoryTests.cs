using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 四个启动入口共用一份「全局设置 + 实例设置 → LaunchOptions」合成规则。
///
/// 为什么值得单独一组测试：这类偏差不报错。漏一个字段的结果是「同一个设置在 A 页生效、
/// B 页不生效」，而用户只会看到「模组全丢」「内存设置没反应」。这在仓库里已经发生过三次
/// （<c>RelaxForgeChecks</c> 漏过入口、<c>VersionArgumentInfo</c> 漏了首页、
/// 服务器页整段实例上下文缺失），所以除了逐字段断言，还留了一条「不许再出现手写构造」的守护。
/// </summary>
public class AppLaunch_OptionsFactoryTests
{
    private static readonly Account TestAccount = new() { Username = "测试" };

    // ==================== 合成规则 ====================

    [Fact]
    public void 实例设置优先于全局_且目录来自解析结果()
    {
        var instance = new InstanceInfo
        {
            Id = "alpha",
            Name = "整合包",
            VersionName = "1.21.4",
            MaxMemoryMb = 8192,
            JavaPath = @"C:\jdk21\bin\javaw.exe",
            JvmArguments = "-Dinstance=1",
            GameArguments = "--demo",
            GcKind = GcKind.G1,
            WindowTitle = "实例窗口",
            ServerAddress = "instance.example.com",
            VersionType = "release",
        };

        var settings = new AppSettings
        {
            MaxMemoryMb = 2048,
            JavaPath = @"C:\jdk8\bin\javaw.exe",
            ExtraJvmArguments = "-Dglobal=1",
            ExtraGameArguments = "--global",
            GameWindowTitle = "全局窗口",
            AutoMaxMemory = true,
        };

        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false),
            settings);

        // 目录只能来自解析结果：GameLauncher 对空目录会退回全局 .minecraft，
        // 那正是「隔离实例的模组全部失效」的成因。
        Assert.Equal(@"D:\instances\alpha", options.GameDirectory);
        Assert.Equal(8192, options.MaxMemoryMb);
        Assert.Equal(@"C:\jdk21\bin\javaw.exe", options.JavaPath);
        Assert.Equal("-Dinstance=1", options.ExtraJvmArguments);
        Assert.Equal("--demo", options.ExtraGameArguments);
        Assert.Equal(GcKind.G1, options.GcKind);
        Assert.Equal("实例窗口", options.WindowTitle);
        Assert.Equal("instance.example.com", options.ServerAddress);
        Assert.Equal("release", options.VersionArgumentInfo);
    }

    [Fact]
    public void 实例显式设了内存时不得开启自动推荐()
    {
        // 用户给某个大整合包单独调过内存，被「自动推荐」悄悄改掉是最难排查的一类配置问题。
        var instance = new InstanceInfo { Id = "alpha", Name = "A", VersionName = "1.21.4", MaxMemoryMb = 6144 };
        var settings = new AppSettings { AutoMaxMemory = true, MaxMemoryMb = 2048 };

        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false),
            settings);

        Assert.False(options.AutoMaxMemory);
        Assert.Equal(6144, options.MaxMemoryMb);
    }

    [Fact]
    public void 实例没设内存时自动推荐跟随全局开关()
    {
        var instance = new InstanceInfo { Id = "alpha", Name = "A", VersionName = "1.21.4" };
        var settings = new AppSettings { AutoMaxMemory = true, MaxMemoryMb = 2048 };

        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false),
            settings);

        // instance.MaxMemoryMb 为 null 时 MaxMemoryMb 先取到全局值，自动推荐仍可在此基础上调整。
        Assert.True(options.AutoMaxMemory);
        Assert.Equal(2048, options.MaxMemoryMb);
    }

    [Fact]
    public void 实例Jvm参数为空串时压掉预设_不能回落()
    {
        // 空串是「这个实例不要额外参数」的显式选择。套 FirstNonEmpty 会把它当成没设置，
        // 于是用户明明清了参数，启动时又冒出预设里的那一长串。
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-Dglobal=1",
            ActiveJvmPresetId = "preset-1",
            JvmPresets = [new JvmPreset { Id = "preset-1", Name = "预设", Arguments = "-Dpreset=1" }],
        };

        var instance = new InstanceInfo { Id = "alpha", Name = "A", VersionName = "1.21.4", JvmArguments = string.Empty };

        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false),
            settings);

        Assert.Equal(string.Empty, options.ExtraJvmArguments);
    }

    [Fact]
    public void 实例没设Jvm参数时走预设()
    {
        // 与上一条配对：null（没设置）才回落，空串（显式清空）不回落。
        var settings = new AppSettings
        {
            ExtraJvmArguments = "-Dglobal=1",
            ActiveJvmPresetId = "preset-1",
            JvmPresets = [new JvmPreset { Id = "preset-1", Name = "预设", Arguments = "-Dpreset=1" }],
        };

        var instance = new InstanceInfo { Id = "alpha", Name = "A", VersionName = "1.21.4" };

        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false),
            settings);

        Assert.Equal("-Dpreset=1", options.ExtraJvmArguments);
    }

    [Fact]
    public void 没有实例时全部跟随全局()
    {
        var settings = new AppSettings
        {
            JavaPath = @"C:\jdk17\bin\javaw.exe",
            MaxMemoryMb = 2048,
            MinMemoryMb = 1024,
            ExtraGameArguments = "--global",
            GameWindowTitle = "全局窗口",
            MaximizeGameWindow = true,
            AutoMaxMemory = false,
            RelaxForgeChecks = false,
        };

        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.20.1",
            new InstanceResolution(@"D:\global\.minecraft", Instance: null, Isolated: false, Created: false),
            settings);

        Assert.Equal(@"D:\global\.minecraft", options.GameDirectory);
        Assert.Equal(@"C:\jdk17\bin\javaw.exe", options.JavaPath);
        Assert.Equal(2048, options.MaxMemoryMb);
        Assert.Equal(1024, options.MinMemoryMb);
        Assert.Equal("--global", options.ExtraGameArguments);
        Assert.Equal("全局窗口", options.WindowTitle);
        Assert.True(options.MaximizeWindow);
        Assert.False(options.RelaxForgeChecks);

        // 没有实例就没有实例级版本类型：null = 跟随全局。
        Assert.Null(options.VersionArgumentInfo);
    }

    [Fact]
    public void 设置缺失时全部走默认值()
    {
        var options = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            new InstanceResolution(string.Empty, Instance: null, Isolated: false, Created: false),
            settings: null);

        Assert.Equal(512, options.MinMemoryMb);
        Assert.Equal(4096, options.MaxMemoryMb);
        Assert.Equal(854, options.WindowWidth);
        Assert.Equal(480, options.WindowHeight);
        Assert.False(options.AutoMaxMemory);
        Assert.True(options.RelaxForgeChecks);
        Assert.Equal(GcKind.Auto, options.GcKind);
    }

    [Fact]
    public void 服务器地址可以被调用方覆盖()
    {
        var instance = new InstanceInfo
        {
            Id = "alpha",
            Name = "A",
            VersionName = "1.21.4",
            ServerAddress = "instance.example.com",
        };

        var resolution = new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false);

        // 服务器页要把「这次点的那个服务器」传进来，否则会连到实例里存着的旧地址。
        var joined = LaunchOptionsFactory.Build(TestAccount, "1.21.4", resolution, new AppSettings(), "clicked.example.com");
        Assert.Equal("clicked.example.com", joined.ServerAddress);

        // 不传则沿用实例自己的记录（首页、导出脚本）。
        var launched = LaunchOptionsFactory.Build(TestAccount, "1.21.4", resolution, new AppSettings());
        Assert.Equal("instance.example.com", launched.ServerAddress);
    }

    // ==================== 服务器页：真实入口 ====================

    [Fact]
    public async Task 服务器页_隔离实例加入服务器时落到实例目录()
    {
        // 本次修复的核心用例：以前服务器页不设 GameDirectory，于是永远启动全局 .minecraft。
        var instance = new InstanceInfo
        {
            Id = "alpha",
            Name = "整合包",
            VersionName = "1.21.4",
            MaxMemoryMb = 8192,
            JvmArguments = "-Dalpha=1",
            VersionType = "release",
        };

        var resolution = new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false);
        var launcher = new RecordingLauncher();
        var settings = new AppSettings { LastSelectedVersion = "1.21.4", MaxMemoryMb = 2048 };

        var viewModel = new ServersViewModel(new FakeServices(
            (typeof(IGameLauncher), launcher),
            (typeof(IAccountService), new FakeAccounts()),
            (typeof(ISettingsService), new FakeSettings(settings)),
            (typeof(IInstanceContext), new FakeInstanceContext(resolution))));

        var card = new ServerCardViewModel(new ServerEntry { Host = "mc.example.com", Name = "示例服" });
        viewModel.Servers.Add(card);

        await viewModel.JoinServerCommand.ExecuteAsync(card);

        var options = launcher.LastOptions;
        Assert.NotNull(options);

        // 目录、内存、参数、版本类型都必须来自实例，而不是全局设置。
        Assert.Equal(@"D:\instances\alpha", options!.GameDirectory);
        Assert.Equal(8192, options.MaxMemoryMb);
        Assert.Equal("-Dalpha=1", options.ExtraJvmArguments);
        Assert.Equal("release", options.VersionArgumentInfo);
        Assert.False(options.AutoMaxMemory);

        // 服务器地址是这一次点的那个，不是实例里存的旧值。
        Assert.Equal("mc.example.com", options.ServerAddress);
        Assert.Equal("1.21.4", options.VersionName);
    }

    [Fact]
    public async Task 服务器页_没有实例上下文时退回当前生效目录()
    {
        // 依赖缺失不该让「加入服务器」把游戏起在全局目录里之外的地方；
        // 退回当前目录（此处为空 = 全局）是既有行为，但必须显式走一遍。
        var launcher = new RecordingLauncher();
        var settings = new AppSettings { LastSelectedVersion = "1.21.4" };

        var viewModel = new ServersViewModel(new FakeServices(
            (typeof(IGameLauncher), launcher),
            (typeof(IAccountService), new FakeAccounts()),
            (typeof(ISettingsService), new FakeSettings(settings))));

        var card = new ServerCardViewModel(new ServerEntry { Host = "mc.example.com" });
        viewModel.Servers.Add(card);

        await viewModel.JoinServerCommand.ExecuteAsync(card);

        Assert.NotNull(launcher.LastOptions);
        Assert.True(string.IsNullOrWhiteSpace(launcher.LastOptions!.GameDirectory));
    }

    [Fact]
    public void 导出脚本与真实启动用同一份参数()
    {
        // 导出的脚本必须能复现启动器真正拼出来的命令，否则「脚本能跑、启动器不能跑」
        // 就只剩猜。两个入口现在的差别只允许是「服务器地址传不传」。
        var instance = new InstanceInfo
        {
            Id = "alpha",
            Name = "整合包",
            VersionName = "1.21.4",
            MaxMemoryMb = 6144,
            JavaPath = @"C:\jdk21\bin\javaw.exe",
            JvmArguments = "-Dalpha=1",
            GameArguments = "--demo",
            GcKind = GcKind.G1,
            WindowTitle = "实例窗口",
            ServerAddress = "instance.example.com",
            VersionType = "release",
            LaunchBeforeCommand = "cmd.bat",
            LaunchAfterCommand = "after.bat",
        };

        var settings = new AppSettings { MaxMemoryMb = 2048, AutoMaxMemory = true };
        var resolution = new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false);

        var launched = LaunchOptionsFactory.Build(TestAccount, "1.21.4", resolution, settings);
        var exported = LaunchOptionsFactory.Build(
            TestAccount,
            "1.21.4",
            resolution,
            settings,
            instance.ServerAddress);

        Assert.Equal(launched.GameDirectory, exported.GameDirectory);
        Assert.Equal(launched.MaxMemoryMb, exported.MaxMemoryMb);
        Assert.Equal(launched.JavaPath, exported.JavaPath);
        Assert.Equal(launched.ExtraJvmArguments, exported.ExtraJvmArguments);
        Assert.Equal(launched.ExtraGameArguments, exported.ExtraGameArguments);
        Assert.Equal(launched.GcKind, exported.GcKind);
        Assert.Equal(launched.WindowTitle, exported.WindowTitle);
        Assert.Equal(launched.AutoMaxMemory, exported.AutoMaxMemory);
        Assert.Equal(launched.ProcessPriority, exported.ProcessPriority);
        Assert.Equal(launched.LaunchBeforeCommand, exported.LaunchBeforeCommand);
        Assert.Equal(launched.LaunchAfterCommand, exported.LaunchAfterCommand);
        Assert.Equal(launched.VersionArgumentInfo, exported.VersionArgumentInfo);
        Assert.Equal(launched.ServerAddress, exported.ServerAddress);
    }

    // ==================== 反手守护：不许再手写 ====================

    [Fact]
    public void 三个ViewModel里不再出现手写的LaunchOptions构造()
    {
        // 这条是防第四次的：只要有人在入口里再写一遍 new LaunchOptions，
        // 就一定会漏字段——前三次都是这么来的。
        var viewModels = FindViewModelDirectory();
        Assert.NotNull(viewModels);

        foreach (var name in new[] { "HomeViewModel.cs", "VersionsViewModel.cs", "ServersViewModel.cs" })
        {
            var path = Path.Combine(viewModels!, name);
            Assert.True(File.Exists(path), $"找不到 {path}");

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("new LaunchOptions", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 合成规则只存在于构建器里一处()
    {
        var services = FindAppServicesDirectory();
        Assert.NotNull(services);

        var text = File.ReadAllText(Path.Combine(services!, "LaunchOptionsFactory.cs"));

        // 恰好一处构造：多了说明有人又在工厂里复制了一份分支。
        var count = text.Split("new LaunchOptions", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, count);

        // 两条判据必须留在工厂里，否则「统一」等于把语义丢了。
        Assert.Contains("ResolveForInstance", text, StringComparison.Ordinal);
        Assert.Contains("AutoMaxMemory", text, StringComparison.Ordinal);
        Assert.Contains("VersionArgumentInfo", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 从测试输出目录往上找 App 的 ViewModels 目录。
    ///
    /// 仓库里没有「读源码文本」的既有先例（只有读数据文件的），所以在测试项目里
    /// 自己向上找：`tests/…/bin/&lt;config&gt;/net8.0` 逐级上溯，直到看到
    /// `src/NebulaLauncher.App`。找不到就返回 null，由调用方断言失败并给出明确原因。
    /// </summary>
    private static string? FindDirectoryUnderRepo(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(relative).ToArray());

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindViewModelDirectory() =>
        FindDirectoryUnderRepo("src", "NebulaLauncher.App", "ViewModels");

    private static string? FindAppServicesDirectory() =>
        FindDirectoryUnderRepo("src", "NebulaLauncher.App", "Services");

    // ==================== 替身 ====================

    /// <summary>按类型返回服务的容器，够 <see cref="ServersViewModel"/> 取值即可。</summary>
    private sealed class FakeServices : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();

        public FakeServices(params (Type Key, object Value)[] entries)
        {
            foreach (var (key, value) in entries)
            {
                _map[key] = value;
            }
        }

        public object? GetService(Type serviceType) =>
            _map.TryGetValue(serviceType, out var value) ? value : null;
    }

    /// <summary>记录「这次启动用的是什么参数」，不真的拉起进程。</summary>
    private sealed class RecordingLauncher : IGameLauncher
    {
        public LaunchOptions? LastOptions { get; private set; }

        public bool IsGameRunning => false;

        public IReadOnlyList<RunningGameInfo> RunningGames => [];

        public event EventHandler<GameOutputLine>? OutputReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionExitedEventArgs>? SessionExited
        {
            add { }
            remove { }
        }

        public bool IsInstanceRunning(string instanceKey) => false;

        public bool KillGame(string sessionId) => false;

        public void KillCurrentGame()
        {
        }

        public Task<string> BuildCommandLineAsync(
            LaunchOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task<LaunchResult> LaunchAsync(
            LaunchOptions options,
            IProgress<LaunchProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;

            return Task.FromResult(new LaunchResult(true, 0, TimeSpan.Zero, null));
        }

        public Task<string?> ExportLaunchScriptAsync(
            LaunchOptions options,
            string targetPath,
            bool maskAccessToken = true,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeAccounts : IAccountService
    {
        private static readonly Account Offline = new() { Username = "测试" };

        public IReadOnlyList<Account> Accounts => [Offline];

        public Account? ActiveAccount => Offline;

        public event EventHandler? AccountsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AccountResult> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccountResult.Ok(Offline));

        public Task<AccountResult> AddMicrosoftAccountAsync(
            IProgress<MicrosoftAuthProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(AccountResult.Fail("测试替身不登录"));

        public Task<AccountResult> RefreshAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccountResult.Ok(Offline));

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void SetActive(string accountId)
        {
        }

        public Task<Account?> EnsureValidActiveAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(Offline);
    }

    private sealed class FakeSettings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; } = current;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>实例上下文替身：只回答「启动这个版本该用哪个目录」。</summary>
    private sealed class FakeInstanceContext(InstanceResolution resolution) : IInstanceContext
    {
        public InstanceInfo? Current => resolution.Instance;

        public string GameDirectory => resolution.GameDirectory;

        public string? VersionName => resolution.Instance?.VersionName;

        public LoaderKind Loader => resolution.Instance?.Loader ?? LoaderKind.Vanilla;

        public string? MinecraftVersion => resolution.Instance?.MinecraftVersion;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentTarget>>([]);

        public Task<ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentTarget?>(null);

        public Task<InstanceResolution> ResolveLaunchTargetAsync(
            string versionName,
            CancellationToken cancellationToken = default) => Task.FromResult(resolution);
    }
}