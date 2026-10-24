using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 整合包导入闭环的测试：格式判定（HMCL / MCBBS / 普通包）与「导入时顺带装游戏与加载器」。
///
/// 不追求覆盖率：安装本身由 <see cref="RecordingInstallService"/> 替身承担，
/// 这里只验证 ModpackService 自己的判据——格式认对没认对、装完的版本名对不对、
/// 失败时有没有留下半成品。
/// </summary>
public class CoreAssets_ModpackInstallTests
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task HMCL整合包识别为HmclZip并读出游戏版本()
    {
        using var env = new InstallEnvironment();
        var archive = env.CreateZip("hmcl.zip", new Dictionary<string, string>
        {
            ["modpack.json"] = """
            {
              "manifestType": "minecraftModpack",
              "manifestVersion": 1,
              "name": "HMCL 测试包",
              "version": "1.2",
              "author": "某人",
              "gameVersion": "1.20.1",
              "modLoader": "forge",
              "modLoaderVersion": "47.1.0"
            }
            """,
            ["minecraft/mods/example.jar"] = "jar",
        });

        var preview = await env.Service.InspectAsync(archive);

        Assert.Equal(ModpackFormat.HmclZip, preview.Format);
        Assert.Equal("HMCL 测试包", preview.Name);
        Assert.Equal("1.20.1", preview.MinecraftVersion);
        Assert.Equal(LoaderKind.Forge, preview.Loader);
        Assert.Equal("47.1.0", preview.LoaderVersion);
        Assert.True(preview.RequiresGameInstall);
    }

    [Fact]
    public async Task 带addons的manifest识别为MCBBS而不是CurseForge()
    {
        using var env = new InstallEnvironment();

        // MCBBS 的 manifest.json 与 CurseForge 同构，唯一差别是有 addons 数组。
        var archive = env.CreateZip("mcbbs.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "name": "MCBBS 测试包",
              "version": "1.0",
              "minecraft": { "version": "1.12.2", "modLoaders": [ { "id": "forge-14.23.5.2860", "primary": true } ] },
              "addons": [ { "id": "example", "version": "1.0" } ]
            }
            """,
            ["overrides/config/a.cfg"] = "cfg",
        });

        var preview = await env.Service.InspectAsync(archive);

        Assert.Equal(ModpackFormat.McbbsZip, preview.Format);
        Assert.Equal("1.12.2", preview.MinecraftVersion);
        Assert.Equal(LoaderKind.Forge, preview.Loader);
        Assert.Equal("14.23.5.2860", preview.LoaderVersion);
    }

    [Theory]
    [InlineData("mcbbs.packmeta")]
    [InlineData("manifest.json")]
    public async Task MCBBS标准addons安装声明的游戏与加载器(string manifestName)
    {
        using var environment = new InstallEnvironment();
        var archive = environment.CreateZip("mcbbs-standard.zip", new Dictionary<string, string>
        {
            [manifestName] = """
            {
              "name": "MCBBS 标准包",
              "version": "1.0",
              "addons": [
                { "id": "game", "version": "1.12.2" },
                { "id": "forge", "version": "14.23.5.2860" }
              ]
            }
            """,
            ["overrides/config/test.cfg"] = "cfg",
        });
        environment.Install.InstalledVersionName = "1.12.2-forge-14.23.5.2860";

        var directory = await environment.Service.ImportAsync(archive, "MCBBS 标准实例");
        var request = Assert.Single(environment.Install.Requests);

        Assert.Equal("1.12.2", request.MinecraftVersion);
        Assert.Equal(LoaderKind.Forge, request.Loader);
        Assert.Equal("14.23.5.2860", request.LoaderVersion);
        Assert.True(environment.Service.LastImport!.GameInstalled);
        Assert.Equal(0, environment.Service.LastImport.AddonsTotal);

        // 版本目录 = 整合包实例名（PCL2 模型）：装出来的加载器版本只当父版本，不占实例目录。
        Assert.Equal("MCBBS 标准实例", (await ReadInstanceAsync(Path.Combine(directory, "instance.json"))).VersionName);
        Assert.Equal(
            "1.12.2-forge-14.23.5.2860",
            (await ReadVersionJsonAsync(directory, "MCBBS 标准实例")).GetProperty("inheritsFrom").GetString());
    }

    [Fact]
    public async Task MCBBS字典addons读取游戏与Fabric版本()
    {
        using var environment = new InstallEnvironment();
        var archive = environment.CreateZip("mcbbs-dictionary.zip", new Dictionary<string, string>
        {
            ["mcbbs.packmeta"] = """{"name":"字典包","addons":{"game":"1.20.1","fabric":{"version":"0.16.9"}}}""",
        });

        var preview = await environment.Service.InspectAsync(archive);

        Assert.Equal("1.20.1", preview.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, preview.Loader);
        Assert.Equal("0.16.9", preview.LoaderVersion);
        Assert.True(preview.RequiresGameInstall);
    }

    [Fact]
    public async Task 深层目录里的manifest不把普通压缩包误判成整合包()
    {
        using var env = new InstallEnvironment();
        var archive = env.CreateZip("plain.zip", new Dictionary<string, string>
        {
            // 只在一级子目录之外（两层）出现 manifest.json：不该被当成 CurseForge 包。
            ["some/nested/manifest.json"] = """{ "name": "不该被读" }""",
            ["readme.txt"] = "hello",
        });

        var preview = await env.Service.InspectAsync(archive);

        Assert.Equal(ModpackFormat.PlainZip, preview.Format);
        Assert.False(preview.RequiresGameInstall);
    }

    [Fact]
    public async Task 导入时安装游戏并把实例版本写成继承版本()
    {
        using var env = new InstallEnvironment();
        var archive = env.CreateZip("cf.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "name": "Forge 包",
              "version": "1.0",
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.1.0", "primary": true } ] }
            }
            """,
            ["overrides/mods/a.jar"] = "jar",
        });

        // 安装器把版本目录命名成自己的规则（1.20.1-forge-47.1.0），
        // 与整合包声明里写的 47.1.0 不是同一份字符串，所以父版本必须按 marker 反查。
        env.Install.InstalledVersionName = "1.20.1-forge-47.1.0";

        var directory = await env.Service.ImportAsync(archive, "测试实例");
        var result = env.Service.LastImport;

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);
        Assert.True(result.GameInstalled);

        // 实例自己的版本目录名 = 实例名；装出来的共享版本只出现在 inheritsFrom 里。
        Assert.Equal("测试实例", result.InstalledVersionName);
        Assert.Equal(Path.Combine(env.Paths.VersionsDirectory, "测试实例"), directory);
        Assert.Equal("1.20.1-forge-47.1.0", (await ReadVersionJsonAsync(directory, "测试实例")).GetProperty("inheritsFrom").GetString());

        var request = Assert.Single(env.Install.Requests);
        Assert.Equal("1.20.1", request.MinecraftVersion);
        Assert.Equal(LoaderKind.Forge, request.Loader);
        Assert.Equal("47.1.0", request.LoaderVersion);

        var instance = await ReadInstanceAsync(Path.Combine(directory, "instance.json"));
        Assert.Equal("测试实例", instance.VersionName);
        Assert.Equal(LoaderKind.Forge, instance.Loader);
        Assert.Equal("1.20.1", instance.MinecraftVersion);
        Assert.Equal("curseforge", instance.ManagedPack?.Type);

        // 覆盖层按 overrides/ 前缀解出来，manifest.json 本身不该落进实例目录。
        Assert.True(File.Exists(Path.Combine(directory, "mods", "a.jar")));
        Assert.False(File.Exists(Path.Combine(directory, "manifest.json")));
    }

    [Fact]
    public async Task 同一MC版本导入两个不同整包得到两个独立版本目录()
    {
        using var env = new InstallEnvironment();

        // 两次安装都落到同一个共享版本目录：游戏本体只该有一份。
        env.Install.InstalledVersionName = "1.20.1-forge-47.1.0";

        var archiveA = env.CreateZip("pack-a.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "name": "Pack A",
              "version": "1.0",
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.1.0", "primary": true } ] }
            }
            """,
            ["overrides/mods/a.jar"] = "jar",
        });
        var archiveB = env.CreateZip("pack-b.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "name": "Pack B",
              "version": "1.0",
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.1.0", "primary": true } ] }
            }
            """,
            ["overrides/mods/b.jar"] = "jar",
        });

        var directoryA = await env.Service.ImportAsync(archiveA, "Pack A");
        var resultA = env.Service.LastImport!;
        var directoryB = await env.Service.ImportAsync(archiveB, "Pack B");
        var resultB = env.Service.LastImport!;

        // 旧行为在这里会把第二个包顶回「版本 X 已经有一个实例了」。
        Assert.True(resultA.Success, resultA.Error);
        Assert.True(resultB.Success, resultB.Error);
        Assert.DoesNotContain("已经有一个实例", resultA.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("已经有一个实例", resultB.Error ?? string.Empty, StringComparison.Ordinal);

        Assert.Equal(Path.Combine(env.Paths.VersionsDirectory, "Pack A"), directoryA);
        Assert.Equal(Path.Combine(env.Paths.VersionsDirectory, "Pack B"), directoryB);

        foreach (var (directory, versionName) in new[] { (directoryA, "Pack A"), (directoryB, "Pack B") })
        {
            Assert.True(File.Exists(Path.Combine(directory, "instance.json")));

            var version = await ReadVersionJsonAsync(directory, versionName);
            Assert.Equal(versionName, version.GetProperty("id").GetString());
            Assert.Equal("1.20.1-forge-47.1.0", version.GetProperty("inheritsFrom").GetString());
        }

        Assert.True(File.Exists(Path.Combine(directoryA, "mods", "a.jar")));
        Assert.True(File.Exists(Path.Combine(directoryB, "mods", "b.jar")));
    }

    [Fact]
    public async Task 同一实例名再导入一次时版本目录名加序号()
    {
        using var env = new InstallEnvironment();
        env.Install.InstalledVersionName = "1.20.1";

        var archive = env.CreateZip("again.zip", new Dictionary<string, string>
        {
            ["modrinth.index.json"] = """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.0",
              "name": "再导一次",
              "dependencies": { "minecraft": "1.20.1" },
              "files": []
            }
            """,
            ["overrides/mods/a.jar"] = "jar",
        });

        var first = await env.Service.ImportAsync(archive, "Pack A");
        var second = await env.Service.ImportAsync(archive, "Pack A");

        // 重名不再拒绝导入（旧行为是「版本 X 已经有一个实例了」），而是按资源管理器的习惯加序号。
        Assert.Equal(Path.Combine(env.Paths.VersionsDirectory, "Pack A"), first);
        Assert.Equal(Path.Combine(env.Paths.VersionsDirectory, "Pack A (2)"), second);

        Assert.Equal("Pack A (2)", (await ReadInstanceAsync(Path.Combine(second, "instance.json"))).VersionName);
        Assert.Equal("Pack A (2)", (await ReadVersionJsonAsync(second, "Pack A (2)")).GetProperty("id").GetString());

        // 第一个目录的内容不能被第二次导入覆盖。
        Assert.Equal("Pack A", (await ReadInstanceAsync(Path.Combine(first, "instance.json"))).VersionName);
    }

    [Fact]
    public async Task 超长实例名重名时序号不会被长度清洗截掉()
    {
        using var env = new InstallEnvironment();
        env.Install.InstalledVersionName = "1.20.1";

        var archive = env.CreateZip("long.zip", new Dictionary<string, string>
        {
            ["modrinth.index.json"] = """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.0",
              "name": "超长名",
              "dependencies": { "minecraft": "1.20.1" },
              "files": []
            }
            """,
        });

        // 100 是 PathSafety 的单段上限：直接拼 " (2)" 会超限，第二次清洗会把后缀整段截掉，
        // 于是又写回第一个目录并覆盖它的 instance.json。
        var longName = new string('长', 100);

        var first = await env.Service.ImportAsync(archive, longName);
        var second = await env.Service.ImportAsync(archive, longName);

        Assert.NotEqual(first, second);
        Assert.Equal(longName, Path.GetFileName(first));
        Assert.EndsWith(" (2)", Path.GetFileName(second), StringComparison.Ordinal);
        Assert.True(Path.GetFileName(second).Length <= 100);

        Assert.Equal(longName, (await ReadInstanceAsync(Path.Combine(first, "instance.json"))).Name);
        Assert.Equal(Path.GetFileName(second), (await ReadVersionJsonAsync(second, Path.GetFileName(second))).GetProperty("id").GetString());
    }

    [Fact]
    public async Task 继承版本json只声明id与inheritsFrom且不复制父版本字段()
    {
        using var env = new InstallEnvironment();
        env.Install.InstalledVersionName = "fabric-loader-0.16.9-1.20.1";

        var archive = env.CreateZip("inherit.zip", new Dictionary<string, string>
        {
            ["modrinth.index.json"] = """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.0",
              "name": "继承包",
              "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.16.9" },
              "files": []
            }
            """,
        });

        var directory = await env.Service.ImportAsync(archive, "继承实例");
        var version = await ReadVersionJsonAsync(directory, "继承实例");

        Assert.Equal("继承实例", version.GetProperty("id").GetString());
        Assert.Equal("fabric-loader-0.16.9-1.20.1", version.GetProperty("inheritsFrom").GetString());
        Assert.Equal("release", version.GetProperty("type").GetString());
        Assert.True(DateTimeOffset.TryParse(version.GetProperty("time").GetString(), out _));
        Assert.True(DateTimeOffset.TryParse(version.GetProperty("releaseTime").GetString(), out _));

        // 空壳：libraries / arguments 全是空的，原版与加载器的文件由 CmlLib 沿链补全。
        Assert.Equal(0, version.GetProperty("libraries").GetArrayLength());
        Assert.Equal(0, version.GetProperty("arguments").GetProperty("game").GetArrayLength());
        Assert.Equal(0, version.GetProperty("arguments").GetProperty("jvm").GetArrayLength());

        // mainClass / assetIndex 必须靠继承拿：复制父版本 json 就等于把快照钉死在这一刻。
        Assert.False(version.TryGetProperty("mainClass", out _));
        Assert.False(version.TryGetProperty("assetIndex", out _));
    }

    [Fact]
    public async Task 加载器安装失败时不留下实例目录()
    {
        using var env = new InstallEnvironment();
        var archive = env.CreateZip("bad.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "name": "装不上的包",
              "version": "1.0",
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "neoforge-21.1.0", "primary": true } ] }
            }
            """,
            ["overrides/mods/a.jar"] = "jar",
        });

        env.Install.FailWith = new InvalidOperationException("镜像不可达");

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.ImportAsync(archive, "失败实例"));

        var result = env.Service.LastImport;
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("安装失败", result.Error);
        Assert.Contains("镜像不可达", result.Error);

        // 实例目录（= versions/<实例名>）压根不该被创建：装游戏排在解压之前就是为了这个。
        var instanceDirectory = Path.Combine(env.Paths.VersionsDirectory, "失败实例");
        Assert.False(Directory.Exists(instanceDirectory));
    }

    [Fact]
    public async Task 关闭开关时不调用安装服务()
    {
        using var env = new InstallEnvironment();
        env.Service.InstallGameOnImport = false;

        var archive = env.CreateZip("fabric.zip", new Dictionary<string, string>
        {
            ["modrinth.index.json"] = """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.0",
              "name": "只解压",
              "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.16.9" },
              "files": []
            }
            """,
            ["overrides/mods/a.jar"] = "jar",
        });

        var directory = await env.Service.ImportAsync(archive, "只解压实例");
        var result = env.Service.LastImport!;

        Assert.Empty(env.Install.Requests);
        Assert.True(File.Exists(Path.Combine(directory, "mods", "a.jar")));

        // 没装游戏时不写版本描述：实例目录只有 instance.json，版本页会把这条版本报成「未安装」，
        // 导入结果的 GameInstalled=false 让文案说「未安装游戏」（文案本身在 LibraryViewModel 侧）。
        var instance = await ReadInstanceAsync(Path.Combine(directory, "instance.json"));
        Assert.Equal("只解压实例", instance.VersionName);
        Assert.Equal("只解压实例", result.InstalledVersionName);
        Assert.False(result.GameInstalled);
        Assert.False(File.Exists(Path.Combine(directory, "只解压实例.json")));
    }

    [Fact]
    public async Task 父版本名无从确认时如实报未安装游戏且不写版本描述()
    {
        using var env = new InstallEnvironment();

        // 装成功但没写 nebula-loader.json：Forge 目录名没有稳定命名规则，父版本名推不出来。
        env.Install.SkipMarker = true;

        var archive = env.CreateZip("forge-nomarker.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "manifestType": "minecraftModpack",
              "manifestVersion": 1,
              "name": "无标记包",
              "version": "1.0",
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.1.0", "primary": true } ] },
              "files": []
            }
            """,
            ["overrides/mods/a.jar"] = "jar",
        });

        var directory = await env.Service.ImportAsync(archive, "无标记实例");
        var result = env.Service.LastImport!;

        Assert.True(result.Success);

        // 不写版本描述 → 实例启动会被前置检查挡下；结果必须如实说「未安装游戏」，
        // 否则 UI 会给一个看起来成功、实际启动不了的结果。
        Assert.False(result.GameInstalled);
        Assert.Contains("未能确认", result.Warning, StringComparison.Ordinal);

        var instance = await ReadInstanceAsync(Path.Combine(directory, "instance.json"));
        Assert.Equal("无标记实例", instance.VersionName);
        Assert.Equal("无标记实例", result.InstalledVersionName);
        Assert.False(File.Exists(Path.Combine(directory, "无标记实例.json")));
    }

    private static async Task<InstanceInfo> ReadInstanceAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<InstanceInfo>(json, ReadOptions)!;
    }

    /// <summary>读实例自己的版本描述（<c>versions/&lt;实例名&gt;/&lt;实例名&gt;.json</c>）。</summary>
    private static async Task<JsonElement> ReadVersionJsonAsync(string versionDirectory, string versionName)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(versionDirectory, versionName + ".json"));

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class InstallEnvironment : IDisposable
    {
        private readonly CoreAssetsLog _log = new();

        public InstallEnvironment()
        {
            Paths = new CoreAssetsPaths();
            Install = new RecordingInstallService(Paths);
            Service = new ModpackService(Paths, new DownloadQueue(new HttpClient()), _log, install: Install);
        }

        public CoreAssetsPaths Paths { get; }

        public RecordingInstallService Install { get; }

        public ModpackService Service { get; }

        public string CreateZip(string name, IReadOnlyDictionary<string, string> entries)
        {
            var path = Path.Combine(Paths.RootDirectory, name);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            foreach (var (entryPath, content) in entries)
            {
                var entry = archive.CreateEntry(entryPath);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }

            return path;
        }

        public void Dispose() => Paths.Dispose();
    }

    /// <summary>
    /// 安装替身：只写 <c>nebula-loader.json</c>（真实 <c>InstallService</c> 的副作用），不联网、不落 jar。
    /// </summary>
    private sealed class RecordingInstallService : IInstallService
    {
        private readonly CoreAssetsPaths _paths;

        public RecordingInstallService(CoreAssetsPaths paths) => _paths = paths;

        public List<InstallRequest> Requests { get; } = new();

        /// <summary>模拟安装器自己的版本目录命名规则。</summary>
        public string InstalledVersionName { get; set; } = "1.20.1";

        /// <summary>
        /// 模拟「装成功但没写成 nebula-loader.json」：Forge / NeoForge 的目录名推不出来，
        /// 导入链拿不到父版本名（这条路径必须如实报「未安装游戏」）。
        /// </summary>
        public bool SkipMarker { get; set; }

        public Exception? FailWith { get; set; }

        public DownloadSource Source { get; set; } = DownloadSource.BmclApi;

        public Task InstallAsync(
            InstallRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? fabricApiVersionId = null)
        {
            Requests.Add(request);

            if (FailWith is not null)
            {
                throw FailWith;
            }

            var directory = Path.Combine(_paths.VersionsDirectory, InstalledVersionName);
            Directory.CreateDirectory(directory);

            if (!SkipMarker)
            {
                File.WriteAllText(
                    Path.Combine(directory, "nebula-loader.json"),
                    JsonSerializer.Serialize(new
                    {
                        loader = request.Loader.ToString(),
                        loaderVersion = request.LoaderVersion,
                        minecraftVersion = request.MinecraftVersion,
                        installedAt = DateTimeOffset.UtcNow.ToString("O"),
                    }));
            }

            return Task.CompletedTask;
        }

        public Task<bool> VerifyAsync(
            string versionName,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
