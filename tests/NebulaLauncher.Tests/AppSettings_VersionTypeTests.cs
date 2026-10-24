using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本信息（<c>--versionType</c>）的两层接线：设置页全局值 + 实例级覆盖。
///
/// 覆盖三件事：
/// 1. 「实例 null → 全局兜底」「实例空串 → 压掉全局」这条解析链（PCL2 只有第一层，第二层是本项目加的）；
/// 2. <see cref="InstanceInfo.VersionType"/> 落盘往返，null 与空串不能混；
/// 3. 设置页 VM 的读取、截断与保存写回。
///
/// 不构造 <see cref="VersionsViewModel"/>：它的构造函数会触发 Avalonia 图标类的静态初始化，
/// 在无 UI 的测试进程里直接抛 <c>TypeInitializationException</c>。实例面板那两段三态转换
/// 因此被抽成 internal static 函数，在这里直接测。
/// </summary>
public class AppSettings_VersionTypeTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    // ==================== 解析链：实例优先、全局兜底、空串压掉全局 ====================

    [Fact]
    public async Task 实例没设置时回落到全局值()
    {
        var commandLine = await BuildCommandLineAsync(globalValue: "整合包 A", instanceValue: null);

        Assert.Contains("--versionType \"整合包 A\"", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 实例有值时压过全局()
    {
        var commandLine = await BuildCommandLineAsync(globalValue: "整合包 A", instanceValue: "整合包 B");

        Assert.Contains("--versionType \"整合包 B\"", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("整合包 A", commandLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// 这是整条链里唯一的本项目特有语义：实例的空串表示「明确不显示」，
    /// 必须把全局值一起压掉，而不是被 <c>string.IsNullOrWhiteSpace</c> 判成「没设置」再回落。
    /// </summary>
    [Fact]
    public async Task 实例空串时压掉全局值且不出现参数()
    {
        var commandLine = await BuildCommandLineAsync(globalValue: "整合包 A", instanceValue: string.Empty);

        var tokens = Tokenize(commandLine);
        Assert.DoesNotContain("--versionType", tokens);
        Assert.DoesNotContain("整合包 A", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 两边都没配置时不出现参数()
    {
        var commandLine = await BuildCommandLineAsync(globalValue: null, instanceValue: null);

        Assert.DoesNotContain("--versionType", Tokenize(commandLine));
    }

    [Fact]
    public async Task 实例值里的空格被自动加引号()
    {
        var commandLine = await BuildCommandLineAsync(globalValue: null, instanceValue: "Example 服务端");

        Assert.Contains("--versionType \"Example 服务端\"", commandLine, StringComparison.Ordinal);
    }

    // ==================== 实例字段的落盘往返 ====================

    /// <summary>
    /// null / 空串 / 有值三种状态都要原样穿过 instance.json。
    /// 源生成上下文没有参与 <see cref="InstanceInfo"/> 的序列化（纯反射），
    /// 所以这里测的是「字段确实进了 JSON」，防的是以后有人加 <c>JsonIgnore</c> 或改成源生成后漏属性。
    ///
    /// 三种取值各写一条 Fact 而不是 <c>[InlineData(null)]</c>：自制的 TestRunner 对单参数
    /// 且值为 null 的 Theory 会按「参数数组本身是 null」调用，直接抛 TargetParameterCountException。
    /// </summary>
    [Fact]
    public void 实例字段落盘读回_null表示跟随全局() => AssertRoundTrips(null);

    [Fact]
    public void 实例字段落盘读回_空串表示明确关闭() => AssertRoundTrips(string.Empty);

    [Fact]
    public void 实例字段落盘读回_自定义值() => AssertRoundTrips("整合包 A");

    private static void AssertRoundTrips(string? stored)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var original = new InstanceInfo { Name = "测试实例", VersionType = stored };

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<InstanceInfo>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(stored, restored!.VersionType);
    }

    /// <summary>旧 instance.json 没有这个字段：读出来必须是 null（跟随全局），不能变成空串（明确关闭）。</summary>
    [Fact]
    public void 旧实例文件缺字段时按跟随全局处理()
    {
        const string legacyJson = """
                                  {
                                    "Id": "abc123",
                                    "Name": "旧实例",
                                    "VersionName": "1.20.1"
                                  }
                                  """;

        var restored = JsonSerializer.Deserialize<InstanceInfo>(legacyJson);

        Assert.NotNull(restored);
        Assert.Null(restored!.VersionType);
    }

    /// <summary>
    /// 真实的 <c>instance.json</c> 是 <see cref="InstanceService"/> 用 <see cref="JsonSerializerDefaults.Web"/>
    /// （camelCase 写出、属性名大小写不敏感）读写的，而人手工写的文件是 PascalCase。
    /// 两种写法都必须落到同一个属性上——否则用户在磁盘上手填的值会被静默读成 null（「跟随全局」）。
    /// </summary>
    [Fact]
    public void 磁盘上的PascalCase字段用Web选项也能读回()
    {
        const string json = """
                            {
                              "Id": "demo",
                              "Notes": "备注",
                              "VersionType": "整合包 演示版"
                            }
                            """;

        var restored = JsonSerializer.Deserialize<InstanceInfo>(json, WebJsonOptions);

        Assert.NotNull(restored);
        Assert.Equal("整合包 演示版", restored!.VersionType);
    }

    // ==================== InstanceService 的 Clone 映射（漏字段守护） ====================

    /// <summary>
    /// <see cref="InstanceService"/> 对外暴露的实例对象全部来自手写的 <c>Clone(InstanceInfo)</c>，
    /// 新字段漏在那里等于「这个字段从来没被读过」——保存一次面板就退回「跟随全局」。
    ///
    /// 实机踩到过：<c>instance.json</c> 里明明写着 <c>VersionType</c>，面板却显示未勾选/空框。
    /// 这条用例走 SaveAsync → LoadAsync → Instances 的完整环路，与 ViewModel 拿到的数据同源。
    /// </summary>
    [Fact]
    public async Task 实例保存再读回时版本信息不丢()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.SaveAsync(new InstanceInfo
        {
            Name = "测试实例",
            VersionName = "1.20.1",
            VersionType = "整合包 演示版",
        });

        await service.LoadAsync();

        var loaded = Assert.Single(service.Instances);
        Assert.Equal("整合包 演示版", loaded.VersionType);
    }

    [Fact]
    public async Task 实例的明确关闭在保存后仍是空串而不是null()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new InstanceService(paths, NullAppLog.Instance);

        await service.SaveAsync(new InstanceInfo
        {
            Name = "测试实例",
            VersionName = "1.20.1",
            VersionType = string.Empty,
        });

        await service.LoadAsync();

        var loaded = Assert.Single(service.Instances);
        Assert.Equal(string.Empty, loaded.VersionType);
    }

    // ==================== 实例面板的三态转换 ====================

    [Theory]
    [InlineData(true, "随便什么", null)]      // 跟随全局：框里的内容被忽略
    [InlineData(false, "", "")]               // 明确不显示
    [InlineData(false, "   ", "")]            // 空白等同留空
    [InlineData(false, " 整合包 B ", "整合包 B")]
    public void 面板三态转换成落盘值(bool followGlobal, string editor, string? expected) =>
        Assert.Equal(expected, VersionsViewModel.ResolveInstanceVersionType(followGlobal, editor));

    [Fact]
    public void 落盘值回填时区分null与空串()
    {
        var following = VersionsViewModel.ParseInstanceVersionType(null);
        Assert.True(following.FollowGlobal);
        Assert.Equal(string.Empty, following.Value);

        var explicitlyOff = VersionsViewModel.ParseInstanceVersionType(string.Empty);
        Assert.False(explicitlyOff.FollowGlobal);
        Assert.Equal(string.Empty, explicitlyOff.Value);

        var custom = VersionsViewModel.ParseInstanceVersionType("整合包 B");
        Assert.False(custom.FollowGlobal);
        Assert.Equal("整合包 B", custom.Value);
    }

    [Fact]
    public void 三态往返不丢信息()
    {
        foreach (var stored in new[] { null, string.Empty, "整合包 B" })
        {
            var panel = VersionsViewModel.ParseInstanceVersionType(stored);
            var written = VersionsViewModel.ResolveInstanceVersionType(panel.FollowGlobal, panel.Value);

            Assert.Equal(stored, written);
        }
    }

    // ==================== 设置页 VM ====================

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  整合包 A  ", "整合包 A")]
    public void 设置页取值去首尾空白(string? input, string expected) =>
        Assert.Equal(expected, SettingsViewModel.ClipVersionArgumentInfo(input));

    [Fact]
    public void 超长输入被截到上限()
    {
        var overlength = new string('版', SettingsViewModel.VersionArgumentInfoMaxLength + 8);

        var clipped = SettingsViewModel.ClipVersionArgumentInfo(overlength);

        Assert.Equal(SettingsViewModel.VersionArgumentInfoMaxLength, clipped.Length);
    }

    [Fact]
    public void 属性赋值超长时就地截断()
    {
        var vm = new SettingsViewModel();

        vm.VersionArgumentInfo = new string('x', 40);

        Assert.Equal(SettingsViewModel.VersionArgumentInfoMaxLength, vm.VersionArgumentInfo.Length);
    }

    [Fact]
    public async Task 保存时把版本信息写进设置()
    {
        var settings = new StubSettingsService();
        var vm = new SettingsViewModel(settings: settings);

        vm.VersionArgumentInfo = "整合包 A";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("整合包 A", settings.Current.VersionArgumentInfo);
    }

    [Fact]
    public async Task 留空保存会清掉既有的版本信息()
    {
        var settings = new StubSettingsService();
        settings.Current.VersionArgumentInfo = "旧的整合包名";

        var vm = new SettingsViewModel(settings: settings);
        vm.VersionArgumentInfo = string.Empty;
        await vm.SaveCommand.ExecuteAsync(null);

        // 清空必须落成空串：GameLauncher 把空串翻译成「摘掉 --versionType」，
        // 留成旧值会让用户以为「清空了但主菜单还显示」。
        Assert.Equal(string.Empty, settings.Current.VersionArgumentInfo);
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 在实际拼出来的命令行上断言。走的是真正会执行的路径（<c>BuildCommandLineAsync</c>），
    /// 不是「直接读 AppSettings」——后者测不出 CmlLib 的默认值会不会把用户值盖掉。
    /// </summary>
    private static async Task<string> BuildCommandLineAsync(string? globalValue, string? instanceValue)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var settings = new StubSettingsService();
        settings.Current.VersionArgumentInfo = globalValue ?? string.Empty;

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
            VersionArgumentInfo = instanceValue,

            // 不写 options.txt、不归档崩溃报告：用例只关心命令行。
            AutoChineseLanguage = false,
            ArchiveCrashReport = false,
        });
    }

    private static List<string> Tokenize(string commandLine) =>
        [.. commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

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