using System.Reflection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例级登录服务器（PCL2 的 <c>VersionServerAuthServer</c> / <c>VersionServerAuthRegister</c> /
/// <c>VersionServerAuthName</c>）。
///
/// 缺口背景：这些设置原本只有账户级，于是「服主发的整合包绑定自己的验证服」表达不了——
/// 玩家要先改掉自己账户里的全局地址，从自己的服切到这个包时再改回来。
///
/// 三条硬约束，每条都有对应用例：
/// <list type="number">
/// <item>只覆盖服务器地址，用户名密码仍走账户 —— <c>instance.json</c> 会被打包分享，
/// 里面不允许出现任何凭据（见 <see cref="落盘文件只写服务器地址不写任何凭据"/>）。</item>
/// <item>三态与 <c>VersionType</c> 完全一致：<c>null</c> 跟随账户、<c>""</c> 明确不注入、值则覆盖。
/// 两个「空」不能合并，否则用户在实例上清空输入框只会把账户地址又拿回来。</item>
/// <item>注册链接只做展示，且只在 <c>http://</c> / <c>https://</c> 时显示（PCL2 的 <c>ValidateHttp</c>）。</item>
/// </list>
/// </summary>
public class AppInstances_AuthServerTests
{
    private const string AccountApiRoot = "https://account.example.com/api/yggdrasil";
    private const string InstanceApiRoot = "https://server-pack.example.com/api/yggdrasil";

    // ==================== 三态 → LaunchOptions（工厂） ====================

    [Fact]
    public void 实例地址覆盖账户地址()
    {
        var options = BuildThroughFactory(
            new InstanceInfo { Name = "整合包", VersionName = "1.20.1", AuthServerUrl = InstanceApiRoot });

        Assert.Equal(InstanceApiRoot, options.InstanceAuthServerUrl);
    }

    /// <summary>
    /// 实例没设置（<c>null</c>）时工厂必须原样传 <c>null</c>，不能替 GameLauncher 提前解析账户值：
    /// 账户设置在启动那一刻才是最新的，提前解析会把「还没选账户」固化成启动页打开时的旧值。
    /// 这条同时保证 <see cref="LaunchOptions.InstanceAuthServerUrl"/> 的三态不会被工厂合并掉。
    /// </summary>
    [Fact]
    public void 实例未设置时传null交给账户兜底()
    {
        var options = BuildThroughFactory(
            new InstanceInfo { Name = "整合包", VersionName = "1.20.1", AuthServerUrl = null });

        Assert.Null(options.InstanceAuthServerUrl);

        // 账户值仍在 LaunchOptions 里，兜底在 GameLauncher 做（见下面「跟随账户」用例）。
        Assert.Equal(AccountApiRoot, options.Account.AuthServerUrl);
    }

    /// <summary>
    /// 空串是「明确关闭」，不是「没设置」。工厂若套 FirstNonEmpty 会把它当成空值丢掉，
    /// 用户就再也关不掉附件注入。
    /// </summary>
    [Fact]
    public void 实例明确关闭时空串原样传下去()
    {
        var options = BuildThroughFactory(
            new InstanceInfo { Name = "整合包", VersionName = "1.20.1", AuthServerUrl = string.Empty });

        Assert.Equal(string.Empty, options.InstanceAuthServerUrl);
    }

    // ==================== 注入决策（GameLauncher 端到端） ====================

    [Fact]
    public async Task 实例值优先于账户值_注入用实例地址()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new RecordingInjector();
        var launcher = CreateLauncher(paths, injector);

        var commandLine = await launcher.BuildCommandLineAsync(
            CreateOptions(AccountType.Authlib, AccountApiRoot, InstanceApiRoot));

        Assert.Equal(1, injector.BuildCalls);
        Assert.Equal(InstanceApiRoot, injector.LastApiRoot);

        // 注入参数真的进了命令行（否则前面两条只证明「服务被调用」）。
        Assert.NotNull(commandLine);
        Assert.Contains("-javaagent:", commandLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 实例未设置时跟随账户地址()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new RecordingInjector();
        var launcher = CreateLauncher(paths, injector);

        await launcher.BuildCommandLineAsync(
            CreateOptions(AccountType.Authlib, AccountApiRoot, instanceServer: null));

        Assert.Equal(1, injector.BuildCalls);
        Assert.Equal(AccountApiRoot, injector.LastApiRoot);
    }

    [Fact]
    public async Task 实例明确关闭时不注入javaagent()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new RecordingInjector();
        var launcher = CreateLauncher(paths, injector);

        // 账户本身是第三方验证账户、也有地址，但实例要求裸连。
        var commandLine = await launcher.BuildCommandLineAsync(
            CreateOptions(AccountType.Authlib, AccountApiRoot, string.Empty));

        Assert.Equal(0, injector.BuildCalls);
        Assert.NotNull(commandLine);
        Assert.DoesNotContain("-javaagent", commandLine!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 实例地址是空白时按明确关闭处理(string instanceServer)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new RecordingInjector();
        var launcher = CreateLauncher(paths, injector);

        // 用户把输入框清成空格也算「关掉」：空白地址没法注入，报错不如静默按关闭处理。
        await launcher.BuildCommandLineAsync(
            CreateOptions(AccountType.Authlib, AccountApiRoot, instanceServer));

        Assert.Equal(0, injector.BuildCalls);
    }

    /// <summary>
    /// 账户不是第三方验证账户时，实例里填了地址也不注入。
    ///
    /// 这条不是形式主义：导入的整合包会带着服主的地址，而用户的常用账户可能是离线 / 微软账户。
    /// 若「有地址就注入」，这类用户每次启动都会先去下 authlib-injector 再预取元数据，
    /// 断网时直接因为「注入失败」被阻断启动——一个只想单人玩的离线用户被一个他没设过的字段拦住。
    /// </summary>
    [Fact]
    public async Task 账户不是第三方验证账户时实例地址不触发注入()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new RecordingInjector();
        var launcher = CreateLauncher(paths, injector);

        await launcher.BuildCommandLineAsync(
            CreateOptions(AccountType.Offline, accountServer: null, InstanceApiRoot));

        Assert.Equal(0, injector.BuildCalls);
    }

    // ==================== instance.json 往返（Clone 是手写映射） ====================

    /// <summary>
    /// <c>InstanceService</c> 对外暴露的实例对象全部来自手写的 <c>Clone(InstanceInfo)</c>，
    /// 新字段漏在那里等于「这个字段从未被读过」——<c>instance.json</c> 里写着地址，面板永远显示空、
    /// 启动也永远走账户地址。上一轮 <c>VersionType</c> 就是这么丢的。
    /// </summary>
    [Fact]
    public async Task 三个字段保存后从实例列表读回()
    {
        using var temp = new TempDirectory();
        var service = new InstanceService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("服主发的包", "1.20.1");

        instance.AuthServerUrl = InstanceApiRoot;
        instance.AuthRegisterUrl = "https://server-pack.example.com/register";
        instance.AuthServerName = "示例验证服";
        await service.SaveAsync(instance);

        var saved = Assert.Single(service.Instances);
        Assert.Equal(InstanceApiRoot, saved.AuthServerUrl);
        Assert.Equal("https://server-pack.example.com/register", saved.AuthRegisterUrl);
        Assert.Equal("示例验证服", saved.AuthServerName);
    }

    [Fact]
    public async Task 明确关闭的空串往返后仍是空串()
    {
        using var temp = new TempDirectory();
        var service = new InstanceService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("裸连实例", "1.20.1");
        instance.AuthServerUrl = string.Empty;
        await service.SaveAsync(instance);

        // 空串若被归一成 null，「明确不注入」就会在下次读盘时退回「跟随账户」。
        var saved = Assert.Single(service.Instances);
        Assert.Equal(string.Empty, saved.AuthServerUrl);

        // 换一个服务实例读，证明写的是磁盘而不是缓存。
        var reloaded = new InstanceService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);
        await reloaded.LoadAsync();
        Assert.Equal(string.Empty, Assert.Single(reloaded.Instances).AuthServerUrl);
    }

    /// <summary>
    /// <c>instance.json</c> 会随整合包一起被打包分享，里面不允许出现任何凭据。
    /// 这条用例盯着「有人图省事把用户名密码也塞进实例配置」的那一天。
    /// </summary>
    [Fact]
    public async Task 落盘文件只写服务器地址不写任何凭据()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("分享测试", "1.20.1");
        instance.AuthServerUrl = InstanceApiRoot;
        instance.AuthRegisterUrl = "https://server-pack.example.com/register";
        instance.AuthServerName = "示例验证服";
        await service.SaveAsync(instance);

        var json = await File.ReadAllTextAsync(
            Path.Combine(service.GetGameDirectory(instance.Id), "instance.json"));

        Assert.Contains("\"authServerUrl\"", json, StringComparison.Ordinal);
        Assert.Contains("\"authRegisterUrl\"", json, StringComparison.Ordinal);
        Assert.Contains("\"authServerName\"", json, StringComparison.Ordinal);

        // 大小写不敏感地扫一遍：JSON 的键是 camelCase，值里也可能出现这些词。
        foreach (var forbidden in new[] { "password", "accessToken", "refreshToken" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ==================== 面板三态转换 ====================

    [Theory]
    [InlineData(true, "随便什么", null)]                                  // 跟随账户：框里的内容被忽略
    [InlineData(false, "", "")]                                            // 明确不注入
    [InlineData(false, "   ", "")]                                         // 空白等同留空
    [InlineData(false, " https://a.example.com/api/yggdrasil ", "https://a.example.com/api/yggdrasil")]
    public void 面板三态转换成落盘值(bool followAccount, string editor, string? expected) =>
        Assert.Equal(expected, VersionsViewModel.ResolveInstanceAuthServer(followAccount, editor));

    [Fact]
    public void 落盘值回填时区分null与空串()
    {
        var following = VersionsViewModel.ParseInstanceAuthServer(null);
        Assert.True(following.FollowAccount);
        Assert.Equal(string.Empty, following.Value);

        var explicitlyOff = VersionsViewModel.ParseInstanceAuthServer(string.Empty);
        Assert.False(explicitlyOff.FollowAccount);
        Assert.Equal(string.Empty, explicitlyOff.Value);

        var custom = VersionsViewModel.ParseInstanceAuthServer(InstanceApiRoot);
        Assert.False(custom.FollowAccount);
        Assert.Equal(InstanceApiRoot, custom.Value);
    }

    [Fact]
    public void 三态往返不丢信息()
    {
        foreach (var stored in new[] { null, string.Empty, InstanceApiRoot })
        {
            var panel = VersionsViewModel.ParseInstanceAuthServer(stored);
            var written = VersionsViewModel.ResolveInstanceAuthServer(panel.FollowAccount, panel.Value);

            Assert.Equal(stored, written);
        }
    }

    // ==================== 注册链接：只展示，且只认 http(s) ====================

    [Theory]
    [InlineData("https://littleskin.cn/auth/register", true)]
    [InlineData("http://example.com/register", true)]
    [InlineData("  https://example.com/register  ", true)]
    [InlineData("ftp://example.com/register", false)]
    [InlineData("file:///C:/register.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("example.com/register", false)] // 少打 scheme：应提示而不是给一个点不开的按钮
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 注册链接只接受http协议(string? url, bool expected) =>
        Assert.Equal(expected, AuthServerAddressPolicy.IsHttpUrl(url));

    [Fact]
    public void 注册按钮只在地址合法时出现()
    {
        var viewModel = new VersionsViewModel();

        Assert.False(viewModel.HasEditorAuthRegisterLink);

        viewModel.EditorAuthRegisterUrl = "example.com/register";
        Assert.False(viewModel.HasEditorAuthRegisterLink);
        Assert.Contains("http://", viewModel.EditorAuthRegisterHint, StringComparison.Ordinal);

        viewModel.EditorAuthRegisterUrl = "https://littleskin.cn/auth/register";
        Assert.True(viewModel.HasEditorAuthRegisterLink);
        Assert.Contains("浏览器", viewModel.EditorAuthRegisterHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 注册链接只在地址合法时才交给系统浏览器()
    {
        var platform = new RecordingPlatform();
        var viewModel = new VersionsViewModel(platformService: platform)
        {
            EditorAuthRegisterUrl = "example.com/register",
        };

        // 少打 scheme：不打开浏览器，改为在状态行说清要补什么。
        await viewModel.OpenAuthRegisterCommand.ExecuteAsync(null);

        Assert.Empty(platform.OpenedUrls);
        Assert.Contains("http://", viewModel.InstanceEditorStatus ?? string.Empty, StringComparison.Ordinal);

        viewModel.EditorAuthRegisterUrl = "https://littleskin.cn/auth/register";

        await viewModel.OpenAuthRegisterCommand.ExecuteAsync(null);

        Assert.Equal(["https://littleskin.cn/auth/register"], platform.OpenedUrls);
    }

    [Fact]
    public void 跟随账户时提示行说明账户当前的地址()
    {
        var viewModel = new VersionsViewModel();

        // 没有账户服务时不能崩，也不能说「将使用空地址」——要说清当前没有账户。
        Assert.True(viewModel.FollowAccountAuthServer);
        Assert.Contains("没有选中账户", viewModel.EditorAuthServerHint, StringComparison.Ordinal);

        viewModel.FollowAccountAuthServer = false;
        Assert.Contains("不注入", viewModel.EditorAuthServerHint, StringComparison.Ordinal);

        viewModel.EditorAuthServerUrl = InstanceApiRoot;
        Assert.Contains("覆盖账户里的地址", viewModel.EditorAuthServerHint, StringComparison.Ordinal);
    }

    // ==================== LaunchOptions 复制路径（反射守护） ====================

    /// <summary>
    /// 自动内存路径会用 <c>CloneWithMaxMemory</c> 逐字段复制 <see cref="LaunchOptions"/>。
    /// 漏拷的表现是「开了自动分配内存的机器上，实例绑定的验证服静默失效、退回账户地址」，
    /// 只在开了那个开关的机器上复现。全量守护在 <c>CoreLaunch_LaunchOptionsCloneTests</c>，
    /// 这里补一条与新字段直接相关的回归。
    /// </summary>
    [Fact]
    public void 自动内存克隆保留实例登录服务器地址()
    {
        var original = new LaunchOptions
        {
            Account = new Account { Username = "player" },
            VersionName = "1.20.1",
            InstanceAuthServerUrl = InstanceApiRoot,
        };

        var clone = CloneWithMaxMemory(original, 8192);

        Assert.Equal(InstanceApiRoot, clone.InstanceAuthServerUrl);
        Assert.Equal(8192, clone.MaxMemoryMb);
    }

    /// <summary>空串与 null 在克隆后必须仍能区分，否则「明确关闭」会变成「跟随账户」。</summary>
    [Fact]
    public void 自动内存克隆保留明确关闭的空串()
    {
        var original = new LaunchOptions
        {
            Account = new Account { Username = "player" },
            VersionName = "1.20.1",
            InstanceAuthServerUrl = string.Empty,
        };

        var clone = CloneWithMaxMemory(original, 8192);

        Assert.Equal(string.Empty, clone.InstanceAuthServerUrl);
    }

    // ==================== 辅助 ====================

    /// <summary>走真实的工厂（唯一构造点），顺带守住「工厂把三个字段传下去」。</summary>
    private static LaunchOptions BuildThroughFactory(InstanceInfo instance) =>
        LaunchOptionsFactory.Build(
            new Account { Type = AccountType.Authlib, Username = "player", AuthServerUrl = AccountApiRoot },
            "1.20.1",
            new InstanceResolution(@"D:\instances\alpha", instance, Isolated: true, Created: false),
            new AppSettings());

    private static LaunchOptions CreateOptions(
        AccountType type,
        string? accountServer,
        string? instanceServer) => new()
        {
            Account = new Account
            {
                Type = type,
                Username = "player",
                Uuid = "0123456789abcdef0123456789abcdef",
                AccessToken = "token-value",
                AuthServerUrl = type == AccountType.Authlib ? accountServer : null,
            },
            VersionName = "1.20.1",

            // 固定小内存：前置检查会拿可用内存做上界，用例不能因为机器内存小而变成 Blocker。
            MinMemoryMb = 512,
            MaxMemoryMb = 1024,
            GcKind = GcKind.Auto,

            // 不写 options.txt、不归档崩溃报告：用例只关心命令行。
            AutoChineseLanguage = false,
            ArchiveCrashReport = false,

            InstanceAuthServerUrl = instanceServer,
        };

    private static GameLauncher CreateLauncher(FakeLauncherPaths paths, IAuthlibInjectorService injector) =>
        new(
            paths,
            NullAppLog.Instance,
            new StubJavaService(),
            new StubVersionService(),
            content: null,
            platform: null,
            authlib: injector);

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

    /// <summary>记录被要求打开的网址：注册按钮是否真的把地址交给了系统浏览器，只能这样证明。</summary>
    private sealed class RecordingPlatform : IPlatformService
    {
        public List<string> OpenedUrls { get; } = [];

        public string PlatformName => "Windows";

        public bool SupportsWindowMaterial => false;

        public bool SupportsSecureTokenStorage => false;

        public string DefaultMinecraftDirectory => Path.GetTempPath();

        public IReadOnlyList<string> JavaSearchRoots => Array.Empty<string>();

        public string JavaExecutableName => "java.exe";

        public Task OpenFolderAsync(string path) => Task.CompletedTask;

        public Task OpenUrlAsync(string url)
        {
            OpenedUrls.Add(url);
            return Task.CompletedTask;
        }

        public Task RevealInFileManagerAsync(string path) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;

        public void ApplyWindowMaterial(IntPtr windowHandle, string material)
        {
        }

        public void SetTaskbarProgress(IntPtr windowHandle, double progress)
        {
        }

        public IDisposable? AcquireSingleInstanceLock(string name) => null;
    }

    /// <summary>记录调用次数与最后一次收到的 API 根：三态是否生效全靠它判定。</summary>
    private sealed class RecordingInjector : IAuthlibInjectorService
    {
        public string? LastError { get; set; }

        public int BuildCalls { get; private set; }

        public int EnsureCalls { get; private set; }

        public string? LastApiRoot { get; private set; }

        public Task<string?> EnsureInjectorAsync(
            string apiRoot,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            LastApiRoot = apiRoot;
            return Task.FromResult<string?>(@"C:\cache\authlib-injector\authlib-injector-1.2.8.jar");
        }

        public Task<AuthlibInjection?> BuildInjectionAsync(
            string apiRoot,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            BuildCalls++;
            LastApiRoot = apiRoot;

            return Task.FromResult<AuthlibInjection?>(new AuthlibInjection(
                @"C:\cache\authlib-injector\authlib-injector-1.2.8.jar",
                apiRoot,
                $"-javaagent:C:\\cache\\authlib-injector\\authlib-injector-1.2.8.jar={apiRoot}"
                + " -Dauthlibinjector.yggdrasil.prefetched=e30="
                + " -Dauthlibinjector.side=client"));
        }
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