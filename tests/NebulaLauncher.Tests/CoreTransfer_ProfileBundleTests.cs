using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Servers;
using NebulaLauncher.Core.Settings;
using NebulaLauncher.Core.Transfer;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// 配置包的导出 / 导入。
///
/// 断言全部打在**真实的 zip 字节**上：把包当一个外部文件打开，逐条目检查里面到底有什么。
/// 只看服务返回值的话，「返回里说脱敏了、实际写进去没脱敏」这类错误完全测不出来，
/// 而这正是这个功能唯一不可出错的地方。
///
/// 每个用例都有两个独立的数据根：源环境（造包）与目标环境（导入），
/// 否则「导入合并」会被造包过程写进去的服务器列表污染。
/// </summary>
public class CoreTransfer_ProfileBundleTests
{
    private const string LastAccountIdValue = "acc-42-keep-out";
    private const string AccountTokenValue = "mc-token-SECRET-1234567890";
    private const string ProxyUserNameValue = "proxy-user-keep-out";
    private const string ProxyPasswordValue = "proxy-pass-SECRET-abcdef";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// 代理凭据与代理地址都不进配置包。
    ///
    /// 这一例是补的：代理设置（`ProxyMode` / `ProxyAddress` / `ProxyUserName` / `ProxyPassword`）
    /// 晚于配置包功能加入，加的时候漏了 <see cref="ProfileSettingsRedaction.Fields"/>，
    /// 等于把明文口令写进一个专门用来发给别人的 zip。代理地址同属「机器相关」——
    /// 另一台机器不一定有同一个代理，带过去只会让导入方整台机器连不上。
    /// </summary>
    [Fact]
    public async Task 导出_包里没有代理凭据()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var settings = await CreateSettingsAsync(paths, current =>
        {
            current.ProxyMode = ProxyModes.Custom;
            current.ProxyAddress = "http://10.0.0.9:8080";
            current.ProxyUserName = ProxyUserNameValue;
            current.ProxyPassword = ProxyPasswordValue;
            current.Theme = "dark";
        });

        var service = CreateService(paths, settings);
        var result = await service.ExportAsync(temp.Path);

        var entries = ReadArchive(result.FilePath);
        var allText = string.Join('\n', entries.Values);

        // 1. 任何条目的内容里都不出现凭据值。
        Assert.DoesNotContain(ProxyPasswordValue, allText, StringComparison.Ordinal);
        Assert.DoesNotContain(ProxyUserNameValue, allText, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.9", allText, StringComparison.Ordinal);

        // 2. settings.json 里连字段名都不留。
        var settingsJson = entries["settings.json"];
        Assert.DoesNotContain("proxyPassword", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyUserName", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyAddress", settingsJson, StringComparison.Ordinal);

        // 3. 但「用不用代理」是偏好，本身不泄露任何东西，必须留着。
        Assert.Contains("\"proxyMode\"", settingsJson, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", settingsJson, StringComparison.Ordinal);

        // 4. manifest 里要如实列出抹掉了哪些字段。
        Assert.Contains("proxyPassword", result.RedactedFields);
    }

    [Fact]
    public async Task 导出_包里没有账户与凭据()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var settings = await CreateSettingsAsync(paths, current =>
        {
            current.LastAccountId = LastAccountIdValue;
            current.MinecraftDirectory = @"C:\Users\someone\.minecraft";
            current.JavaPath = @"C:\Program Files\Java\jdk-21\bin\javaw.exe";
            current.LastInstanceId = "instance-abc";
            current.Theme = "light";
        });

        await WriteServersAsync(paths, Server("mc.example.com", 25565, "主服"));
        await WriteFavoritesAsync(paths, ["1.20.1"], ["1.7.10"]);
        await WriteAccountsAsync(paths);

        var service = CreateService(paths, settings);
        var result = await service.ExportAsync(temp.Path);

        var entries = ReadArchive(result.FilePath);

        // 1. accounts.json 根本不在包里。
        Assert.DoesNotContain("accounts.json", entries.Keys);

        // 2. 任何条目的内容里都不出现凭据值（含被改名 / 换个文件塞进去的情况）。
        var allText = string.Join('\n', entries.Values);
        Assert.DoesNotContain(LastAccountIdValue, allText, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountTokenValue, allText, StringComparison.Ordinal);

        // 3. settings.json 里连字段名都不该留：留着空字段会让导入方以为「原机器上配过」。
        Assert.Contains("settings.json", entries.Keys);
        var settingsJson = entries["settings.json"];
        Assert.DoesNotContain("lastAccountId", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("lastInstanceId", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("minecraftDirectory", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("javaPath", settingsJson, StringComparison.Ordinal);

        // 4. 该带的偏好还在（不是靠「整份删掉」来实现脱敏的）。
        Assert.Contains("\"theme\": \"light\"", settingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 导出_manifest字段齐全()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var settings = await CreateSettingsAsync(paths, current => current.Theme = "system");
        await WriteServersAsync(paths, Server("a.example.com", 25565, "A"), Server("b.example.com", 25566, "B"));
        await WriteFavoritesAsync(paths, ["1.20.1", "1.21.4"], ["1.7.10"]);

        var service = CreateService(paths, settings);
        var result = await service.ExportAsync(temp.Path);
        var manifest = ReadArchive(result.FilePath)["manifest.json"];

        Assert.Contains("\"bundleVersion\": 1", manifest, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"nebula-profile\"", manifest, StringComparison.Ordinal);

        // launcherVersion 与诊断文本、关于页同源：仓库级构建属性（Directory.Build.props 的 <Version>）
        // 解析出的裸版本，不带 v 前缀、不是 GetName().Version 的四段形态。
        // 期望值用同源调用算出来，改版本时不用动这条断言。
        Assert.Contains(
            $"\"launcherVersion\": \"{DiagnosticsSnapshot.LauncherVersionOf(typeof(ProfileBundleService).Assembly)}\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains("\"exportedAt\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"files\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"redactedFields\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"settingsWhitelist\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"containsAccounts\": false", manifest, StringComparison.Ordinal);

        // 计数写进 manifest，与 zip 内实际条目一致。
        Assert.Contains("\"favoriteCount\": 2", manifest, StringComparison.Ordinal);
        Assert.Contains("\"hiddenCount\": 1", manifest, StringComparison.Ordinal);
        Assert.Contains("\"serverCount\": 2", manifest, StringComparison.Ordinal);

        Assert.Equal(2, result.FavoriteCount);
        Assert.Equal(1, result.HiddenCount);
        Assert.Equal(2, result.ServerCount);
        Assert.Contains("settings.json", result.Files);
    }

    [Fact]
    public async Task 导出_文件名带时间戳且不覆盖已有包()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var settings = await CreateSettingsAsync(paths, _ => { });
        var service = CreateService(paths, settings);

        var first = await service.ExportAsync(temp.Path);
        var second = await service.ExportAsync(temp.Path);

        Assert.True(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
        Assert.NotEqual(first.FilePath, second.FilePath);

        foreach (var path in new[] { first.FilePath, second.FilePath })
        {
            var name = Path.GetFileName(path);
            Assert.StartsWith("nebula-profile-", name, StringComparison.Ordinal);
            Assert.EndsWith(".zip", name, StringComparison.Ordinal);
            Assert.Matches(@"^nebula-profile-\d{8}-\d{6}(-\d+)?\.zip$", name);
        }
    }

    [Fact]
    public async Task 导出_没有收藏与服务器时仍出包且计数为零()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var settings = await CreateSettingsAsync(paths, _ => { });
        var service = CreateService(paths, settings);

        var result = await service.ExportAsync(temp.Path);
        var entries = ReadArchive(result.FilePath);

        Assert.Equal(0, result.FavoriteCount);
        Assert.Equal(0, result.HiddenCount);
        Assert.Equal(0, result.ServerCount);
        Assert.Contains("favorites.json", entries.Keys);
        Assert.Contains("servers.json", entries.Keys);
        Assert.Equal("[]", entries["servers.json"].Trim());
    }

    [Fact]
    public async Task 导入_服务器按地址合并且同名被包里覆盖()
    {
        using var temp = new TempDirectory();

        // 目标环境：本地两个服务器。
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));
        var localServers = new ServerService(paths, NullAppLog.Instance);
        await localServers.LoadAsync();
        await localServers.AddAsync(Server("alpha.example.com", 25565, "A 服"));
        await localServers.AddAsync(Server("beta.example.com", 25565, "B 服"));

        // 包里：alpha 换了名字（同地址），外加一个新服 gamma。
        var bundle = await BuildBundleAsync(
            Path.Combine(temp.Path, "bundles"),
            servers: [Server("alpha.example.com", 25565, "A 服（新机器改名）"), Server("gamma.example.com", 25565, "C 服")],
            favorites: []);

        var service = CreateService(paths, settings: null, servers: localServers, favorites: null);
        var result = await service.ImportAsync(bundle);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Servers);
        Assert.Equal(1, result.ServersAdded);

        var servers = localServers.Servers;
        Assert.Equal(3, servers.Count);
        Assert.Equal("A 服（新机器改名）", servers.Single(s => s.Host == "alpha.example.com").Name);
        Assert.Contains(servers, s => s.Host == "gamma.example.com");

        // 合并结果必须真的写进 servers.json：只改内存的话重启就丢。
        var onDisk = await File.ReadAllTextAsync(paths.ServersFile);
        Assert.Contains("gamma.example.com", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 导入_收藏按版本名合并且已有的不重复()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));

        var localFavorites = new VersionFavorites(paths, NullAppLog.Instance);
        await localFavorites.LoadAsync();
        await localFavorites.ToggleFavoriteAsync("1.20.1");

        var bundle = await BuildBundleAsync(
            Path.Combine(temp.Path, "bundles"),
            servers: [],
            favorites: ["1.20.1", "1.21.4"],
            hidden: ["1.7.10"]);

        var service = CreateService(paths, settings: null, servers: null, favorites: localFavorites);
        var result = await service.ImportAsync(bundle);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Favorites);
        Assert.Equal(1, result.Hidden);

        Assert.True(localFavorites.IsFavorite("1.20.1"));
        Assert.True(localFavorites.IsFavorite("1.21.4"));
        Assert.True(localFavorites.IsHidden("1.7.10"));

        // 已经是收藏的条目不能被 toggle 掉（接口只有 toggle，顺序错了就会把收藏取消）。
        Assert.Equal(2, localFavorites.Favorites.Count);

        localFavorites.Dispose();
    }

    [Fact]
    public async Task 导入_默认不导入设置()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));

        var settings = await CreateSettingsAsync(paths, current =>
        {
            current.Theme = "dark";
            current.DownloadThreads = 64;
        });

        var bundle = await BuildBundleAsync(
            Path.Combine(temp.Path, "bundles"),
            servers: [],
            favorites: [],
            settings: current =>
            {
                current.Theme = "light";
                current.DownloadThreads = 128;
            });

        var service = CreateService(paths, settings);
        var result = await service.ImportAsync(bundle, includeSettings: false);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.Settings);
        Assert.Equal("dark", settings.Current.Theme);
        Assert.Equal(64, settings.Current.DownloadThreads);
    }

    [Fact]
    public async Task 导入_设置只覆盖白名单键()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));

        var settings = await CreateSettingsAsync(paths, current =>
        {
            current.Theme = "dark";
            current.DownloadThreads = 64;
            current.MinecraftDirectory = @"C:\local\.minecraft";
            current.JavaPath = @"C:\local\java\bin\javaw.exe";
        });

        var bundle = await BuildBundleAsync(
            Path.Combine(temp.Path, "bundles"),
            servers: [],
            favorites: [],
            settings: current =>
            {
                current.Theme = "light";
                current.WindowMaterial = "acrylic";
                current.DownloadThreads = 32;
                current.MinecraftDirectory = @"D:\other-machine\.minecraft";
                current.JavaPath = @"D:\other-machine\java\bin\javaw.exe";
            });

        var service = CreateService(paths, settings);
        var result = await service.ImportAsync(bundle, includeSettings: true);

        Assert.True(result.IsValid);

        // 白名单内：被包里的值覆盖。
        Assert.Equal("light", settings.Current.Theme);
        Assert.Equal("acrylic", settings.Current.WindowMaterial);
        Assert.Equal(32, settings.Current.DownloadThreads);

        // 白名单外：本机的值一个都没动。
        Assert.Equal(@"C:\local\.minecraft", settings.Current.MinecraftDirectory);
        Assert.Equal(@"C:\local\java\bin\javaw.exe", settings.Current.JavaPath);

        // 计数只算白名单内的键；包里的 settings.json 是「完整设置去敏感字段」，
        // 所以命中数应等于白名单大小。
        Assert.Equal(ProfileSettingsWhitelist.Keys.Count, result.Settings);
        Assert.Contains("theme", result.SettingKeys);
        Assert.Contains("windowMaterial", result.SettingKeys);
        Assert.DoesNotContain("minecraftDirectory", result.SettingKeys);
    }

    [Fact]
    public async Task 导入_设置落盘而不是只改内存()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));
        var settings = await CreateSettingsAsync(paths, current => current.Theme = "dark");

        var bundle = await BuildBundleAsync(
            Path.Combine(temp.Path, "bundles"),
            servers: [],
            favorites: [],
            settings: current => current.Theme = "light");

        var service = CreateService(paths, settings);
        await service.ImportAsync(bundle, includeSettings: true);

        // 换一个 SettingsService 从磁盘重读：只改了内存对象的话这里还是 dark。
        var reloaded = new SettingsService(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();
        Assert.Equal("light", reloaded.Current.Theme);
    }

    [Fact]
    public async Task 导入_缺manifest的包被拒绝且原因可读()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));

        var bundle = Path.Combine(temp.Path, "not-a-profile.zip");
        await using (var stream = new FileStream(bundle, FileMode.CreateNew, FileAccess.Write))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            await WriteEntryAsync(archive, "servers.json", "[]");
        }

        var service = CreateService(paths, new SettingsService(paths, NullAppLog.Instance));

        var inspection = await service.InspectAsync(bundle);
        Assert.False(inspection.IsValid);
        Assert.Contains("manifest.json", inspection.Error!, StringComparison.Ordinal);

        var result = await service.ImportAsync(bundle);
        Assert.False(result.IsValid);
        Assert.Contains("manifest.json", result.Error!, StringComparison.Ordinal);

        // 拒绝时不能留下任何东西。
        Assert.False(File.Exists(paths.ServersFile));
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "favorites.json")));
    }

    [Fact]
    public async Task 导入_manifest版本过高被拒绝()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));

        var bundle = Path.Combine(temp.Path, "future.zip");
        await using (var stream = new FileStream(bundle, FileMode.CreateNew, FileAccess.Write))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            await WriteEntryAsync(archive, "manifest.json",
                """{"bundleVersion":99,"kind":"nebula-profile","files":[],"redactedFields":[]}""");
        }

        var service = CreateService(paths, new SettingsService(paths, NullAppLog.Instance));
        var result = await service.ImportAsync(bundle);

        Assert.False(result.IsValid);
        Assert.Contains("99", result.Error!, StringComparison.Ordinal);
        Assert.Contains("升级启动器", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 导入_不是zip的文件被拒绝()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));

        var bundle = Path.Combine(temp.Path, "garbage.zip");
        await File.WriteAllTextAsync(bundle, "这不是 zip");

        var service = CreateService(paths, new SettingsService(paths, NullAppLog.Instance));
        var result = await service.ImportAsync(bundle);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task 检查_摘要计数与包内容一致()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "target"));
        var settings = await CreateSettingsAsync(paths, current => current.Theme = "light");

        var bundle = await BuildBundleAsync(
            Path.Combine(temp.Path, "bundles"),
            servers: [Server("a.example.com", 25565, "A"), Server("b.example.com", 25565, "B")],
            favorites: ["1.20.1", "1.21.4"],
            hidden: ["1.7.10"],
            settings: current => current.Theme = "dark");

        var service = CreateService(paths, settings);
        var inspection = await service.InspectAsync(bundle);

        Assert.True(inspection.IsValid);
        Assert.Equal(2, inspection.ServerCount);
        Assert.Equal(2, inspection.FavoriteCount);
        Assert.Equal(1, inspection.HiddenCount);
        Assert.Equal(ProfileSettingsWhitelist.Keys.Count, inspection.SettingCount);
        Assert.NotNull(inspection.ExportedAt);
        Assert.Contains("settings.json", inspection.Files);
    }

    // ==================== 辅助 ====================

    private static ProfileBundleService CreateService(
        FakeLauncherPaths paths,
        ISettingsService? settings,
        IServerService? servers = null,
        IVersionFavorites? favorites = null) =>
        new(paths, NullAppLog.Instance, settings, servers, favorites);

    private static async Task<SettingsService> CreateSettingsAsync(
        FakeLauncherPaths paths,
        Action<AppSettings> configure)
    {
        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();
        configure(service.Current);
        await service.SaveAsync();
        return service;
    }

    private static ServerEntry Server(string host, int port, string name) => new()
    {
        Host = host,
        Port = port,
        Name = name,
    };

    private static async Task WriteServersAsync(FakeLauncherPaths paths, params ServerEntry[] servers)
    {
        var json = JsonSerializer.Serialize(servers, WriteOptions);

        await File.WriteAllTextAsync(paths.ServersFile, json);
    }

    private static async Task WriteFavoritesAsync(
        FakeLauncherPaths paths,
        IReadOnlyList<string> favorites,
        IReadOnlyList<string> hidden)
    {
        var favoritesJson = string.Join(",",
            favorites.Select(name => $"{{\"versionName\":\"{name}\",\"pinnedAtUtc\":\"2026-01-01T00:00:00+00:00\"}}"));
        var hiddenJson = string.Join(",",
            hidden.Select(name => $"{{\"versionName\":\"{name}\",\"hiddenAtUtc\":\"2026-01-01T00:00:00+00:00\"}}"));

        var json = $"{{\"favorites\":[{favoritesJson}],\"hidden\":[{hiddenJson}]}}";
        await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "favorites.json"), json);
    }

    private static async Task WriteAccountsAsync(FakeLauncherPaths paths)
    {
        var json = $$"""
                     [
                       { "id": "acc-1", "name": "Tester", "accessToken": "{{AccountTokenValue}}" }
                     ]
                     """;
        await File.WriteAllTextAsync(paths.AccountsFile, json);
    }

    /// <summary>
    /// 用真实的导出逻辑造一个包：源数据落在独立的临时数据根里，与导入目标环境互不影响。
    /// 这样包结构与正式导出完全一致，测试不需要手写 manifest 的字段拼装。
    /// </summary>
    private static async Task<string> BuildBundleAsync(
        string bundleDirectory,
        IReadOnlyList<ServerEntry> servers,
        IReadOnlyList<string> favorites,
        IReadOnlyList<string>? hidden = null,
        Action<AppSettings>? settings = null)
    {
        using var sourceTemp = new TempDirectory();
        var sourcePaths = new FakeLauncherPaths(Path.Combine(sourceTemp.Path, "source"));

        await WriteServersAsync(sourcePaths, [.. servers]);
        await WriteFavoritesAsync(sourcePaths, favorites, hidden ?? []);

        var settingsService = new SettingsService(sourcePaths, NullAppLog.Instance);
        await settingsService.LoadAsync();

        if (settings is not null)
        {
            settings(settingsService.Current);
            await settingsService.SaveAsync();
        }

        Directory.CreateDirectory(bundleDirectory);

        var exporter = new ProfileBundleService(sourcePaths, NullAppLog.Instance, settingsService);
        var result = await exporter.ExportAsync(bundleDirectory);
        return result.FilePath;
    }

    private static Dictionary<string, string> ReadArchive(string path)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(path);

        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            entries[entry.FullName] = reader.ReadToEnd();
        }

        return entries;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }
}
