using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 资源页「安装目标」与「整合包」两条主线的纯逻辑与页内行为：
/// <list type="number">
/// <item>「将装到」下拉的条目构成：实例 → 未建实例的已安装版本 → 全局目录，以及三者的落点差异；</item>
/// <item>切换目标后筛选条件与落盘目录同步变化，且玩家在页内可见（不再从 lastSelectedVersion 隐式推断）；</item>
/// <item>整合包分区搜索不按游戏版本 / 加载器筛，双向数据源与游戏版本筛选都在选中包之后；</item>
/// <item>两个后端的整合包版本归一到同一条下载描述。</item>
/// </list>
///
/// 网络由假 handler / 接口替身承担，测试不联网。界面布局（下拉框、提示条）靠编译期绑定校验与用户实测。
/// </summary>
public class AppLibrary_TargetsAndModpacksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private const string MinecraftDir = @"C:\data\.minecraft";

    // ==================== 目标列表构成 ====================

    [Fact]
    public void 目标列表_实例在前未建实例版本居中全局目录在最后()
    {
        var instance = new ContentTarget
        {
            Key = "abc123",
            Kind = ContentTargetKind.Instance,
            DisplayName = "Fabric 生存",
            GameDirectory = @"C:\data\.minecraft\versions\fabric-1.20.1",
            InstanceId = "abc123",
            VersionName = "fabric-1.20.1",
            MinecraftVersion = "1.20.1",
            Loader = LoaderKind.Fabric,
        };

        var global = new ContentTarget
        {
            Key = "global",
            Kind = ContentTargetKind.Global,
            DisplayName = "全局目录 (.minecraft)",
            GameDirectory = MinecraftDir,
        };

        var options = LibraryViewModel.BuildInstallTargets(
            [global, instance],
            [
                Version("fabric-1.20.1", "1.20.1", LoaderKind.Fabric),
                Version("1.19.2-forge", "1.19.2", LoaderKind.Forge),
                Version("1.7.10", "1.7.10", LoaderKind.Vanilla),
            ],
            MinecraftDir);

        Assert.Equal(4, options.Count);
        Assert.Equal(["Fabric 生存", "1.19.2-forge", "1.7.10", "全局目录 (.minecraft)"],
            options.Select(option => option.DisplayName));

        // 已建实例的版本不再单独列一条：同一份版本出现两个落点不同的同名条目只会让人选错。
        Assert.DoesNotContain(options, option => option.DisplayName == "fabric-1.20.1");

        // 未建实例的版本落点是全局 .minecraft，副标题必须写明，否则玩家会以为进了版本目录。
        var versionOnly = options[1];
        Assert.Equal(MinecraftDir, versionOnly.GameDirectory);
        Assert.Equal("1.19.2", versionOnly.MinecraftVersion);
        Assert.Equal(LoaderKind.Forge, versionOnly.Loader);
        Assert.Contains("全局 .minecraft（该版本未建实例）", versionOnly.Detail, StringComparison.Ordinal);
        Assert.False(versionOnly.IsGlobal);

        // 全局目录没有版本身份：筛选时不带版本与加载器。
        var globalOption = options[^1];
        Assert.True(globalOption.IsGlobal);
        Assert.Null(globalOption.MinecraftVersion);
        Assert.Equal(LoaderKind.Vanilla, globalOption.Loader);
        Assert.Equal(MinecraftDir, globalOption.GameDirectory);
    }

    [Fact]
    public void 目标副标题_未知版本与加载器文案()
    {
        Assert.Equal(
            "Minecraft 1.20.1 · Fabric · 实例目录",
            LibraryViewModel.DescribeTargetDetail("1.20.1", LoaderKind.Fabric, "实例目录"));

        Assert.Equal(
            "版本未知 · 原版 · 全局 .minecraft（该版本未建实例）",
            LibraryViewModel.DescribeTargetDetail(null, LoaderKind.Vanilla, "全局 .minecraft（该版本未建实例）"));

        Assert.Equal(
            "Minecraft 1.20.1 · NeoForge · 实例目录",
            LibraryViewModel.DescribeTargetDetail("1.20.1", LoaderKind.NeoForge, "实例目录"));
    }

    // ==================== 游戏版本筛选取值 ====================

    [Fact]
    public void 整合包版本筛选值_只留MC版本且按版本号降序()
    {
        var versions = new List<LibraryVersionViewModel>
        {
            LayoutVersion("1.20.2", ["1.20.1", "1.20.2", "Forge", "Client"]),
            LayoutVersion("1.19.2", ["1.19.2", "Fabric", "Server"]),
            LayoutVersion("1.10", ["1.10"]),
            LayoutVersion("24w14a", ["24w14a"]),
            LayoutVersion("1.10.1", ["1.10.1"]),
        };

        var options = LibraryViewModel.CollectGameVersionOptions(versions);

        Assert.Equal(["1.20.2", "1.20.1", "1.19.2", "1.10.1", "1.10", "24w14a"], options);
    }

    [Theory]
    [InlineData("1.20.1", true)]
    [InlineData("1.7", true)]
    [InlineData("24w14a", true)]
    [InlineData("23w13a_or_b", true)]
    [InlineData("Forge", false)]
    [InlineData("Fabric", false)]
    [InlineData("Client", false)]
    [InlineData("1.20.1-pre1", false)]
    [InlineData("", false)]
    public void 游戏版本形态判定(string value, bool expected)
    {
        Assert.Equal(expected, LibraryViewModel.LooksLikeMinecraftVersion(value));
    }

    [Fact]
    public void 版本比较_发布版本降序且发布版排在快照前()
    {
        var values = new List<string> { "1.20.1", "1.7.10", "1.10", "24w14a", "1.20" };
        values.Sort(LibraryViewModel.CompareMinecraftVersions);

        Assert.Equal(["1.20.1", "1.20", "1.10", "1.7.10", "24w14a"], values);
    }

    [Fact]
    public void 版本项_按游戏版本筛选()
    {
        var version = LayoutVersion("1.20.1", ["1.20.1", "Forge"]);

        Assert.True(version.SupportsGameVersion("1.20.1"));
        Assert.False(version.SupportsGameVersion("1.19.2"));

        // 声明缺失按「未知」放行：拿不到声明不能被当成「不支持」，否则筛选会把整批条目藏掉。
        var unknown = LayoutVersion("legacy", []);
        Assert.True(unknown.SupportsGameVersion("1.20.1"));
    }

    // ==================== 页内行为：目标决定筛选与落点 ====================

    [Fact]
    public async Task 进入页面_默认选中当前实例并按其版本与加载器筛选()
    {
        var instanceTarget = InstanceTarget("abc123", "Fabric 生存", "fabric-1.20.1", "1.20.1", LoaderKind.Fabric);
        var page = CreatePage(
            new FakeInstanceContext(
                new InstanceInfo { Id = "abc123", Name = "Fabric 生存", VersionName = "fabric-1.20.1" },
                GlobalTarget(),
                instanceTarget),
            new FakeVersions([Version("fabric-1.20.1", "1.20.1", LoaderKind.Fabric)]),
            new FakeModrinth(Modrinth("sodium", "Sodium")),
            new FakeCurseForge());

        await page.OnNavigatedToAsync();

        Assert.Same(instanceTarget.Key, page.SelectedInstallTarget?.Key);
        Assert.Contains("筛选：Minecraft 1.20.1", page.FilterText, StringComparison.Ordinal);
        Assert.Contains("Fabric", page.FilterText, StringComparison.Ordinal);
        Assert.False(page.IsFilterLimited);

        Assert.Equal(
            Path.Combine(@"C:\data\.minecraft\versions\fabric-1.20.1", "mods"),
            page.InstallTargetPath);

        Assert.Equal("1.20.1", page.Modrinth.LastGameVersion);
        Assert.Equal(LoaderKind.Fabric, page.Modrinth.LastLoader);
    }

    [Fact]
    public async Task 选择未建实例的版本_筛选跟着版本走且落点是全局目录()
    {
        var page = CreatePage(
            new FakeInstanceContext(null, GlobalTarget()),
            new FakeVersions([
                Version("fabric-1.20.1", "1.20.1", LoaderKind.Fabric),
                Version("1.19.2-forge", "1.19.2", LoaderKind.Forge),
            ]),
            new FakeModrinth(Modrinth("jei", "JEI")),
            new FakeCurseForge());

        await page.OnNavigatedToAsync();

        var versionTarget = page.InstallTargets.Single(option => option.DisplayName == "1.19.2-forge");
        page.SelectedInstallTarget = versionTarget;

        Assert.Contains("筛选：Minecraft 1.19.2", page.FilterText, StringComparison.Ordinal);
        Assert.Contains("Forge", page.FilterText, StringComparison.Ordinal);

        // 该版本没有独立游戏目录：文件进全局 .minecraft/mods，与游戏实际读取的目录一致。
        Assert.Equal(Path.Combine(MinecraftDir, "mods"), page.InstallTargetPath);
        Assert.Equal("1.19.2-forge", page.InstallTargetName);

        await WaitForAsync(() => page.Modrinth.LastGameVersion == "1.19.2", "按新目标的版本重搜");
        Assert.Equal(LoaderKind.Forge, page.Modrinth.LastLoader);
    }

    [Fact]
    public async Task 选择全局目录_不筛选版本且不显示警告()
    {
        var page = CreatePage(
            new FakeInstanceContext(
                new InstanceInfo { Id = "abc123", Name = "Fabric 生存", VersionName = "fabric-1.20.1" },
                GlobalTarget(),
                InstanceTarget("abc123", "Fabric 生存", "fabric-1.20.1", "1.20.1", LoaderKind.Fabric)),
            new FakeVersions([Version("fabric-1.20.1", "1.20.1", LoaderKind.Fabric)]),
            new FakeModrinth(Modrinth("sodium", "Sodium")),
            new FakeCurseForge());

        await page.OnNavigatedToAsync();

        page.SelectedInstallTarget = page.InstallTargets.Single(option => option.IsGlobal);

        Assert.Contains("全局目录不筛选版本与加载器", page.FilterText, StringComparison.Ordinal);
        Assert.False(page.IsFilterLimited);
        Assert.Equal(Path.Combine(MinecraftDir, "mods"), page.InstallTargetPath);
        Assert.Equal("全局目录", page.InstallTargetName);

        await WaitForAsync(() => page.Modrinth.LastGameVersion is null, "全局目录按不过滤重搜");
        Assert.Equal(LoaderKind.Vanilla, page.Modrinth.LastLoader);
    }

    // ==================== 整合包：搜索不筛版本、CF 单源与双源 ====================

    [Fact]
    public async Task 整合包分区_CurseForge单源搜索且不带游戏版本()
    {
        var curseForge = new FakeCurseForge
        {
            Modpacks = [CurseForge(1001, "Better MC", modpack: true)],
        };

        var page = CreatePage(
            new FakeInstanceContext(
                new InstanceInfo { Id = "abc123", Name = "Fabric 生存", VersionName = "fabric-1.20.1" },
                GlobalTarget(),
                InstanceTarget("abc123", "Fabric 生存", "fabric-1.20.1", "1.20.1", LoaderKind.Fabric)),
            new FakeVersions([Version("fabric-1.20.1", "1.20.1", LoaderKind.Fabric)]),
            new FakeModrinth(),
            curseForge,
            catalog: new ModpackCatalogClient(new JsonHandler("""
            { "hits": [], "offset": 0, "limit": 20, "total_hits": 0 }
            """)));

        await page.OnNavigatedToAsync();
        await page.SelectModPackCommand.ExecuteAsync(null);
        await page.SelectSourceCommand.ExecuteAsync(ContentSourceKind.CurseForge);

        // 即使页内选中了 1.20.1 的 Fabric 实例，整合包搜索也不带版本 / 加载器：这是玩家「先找包」的前提。
        Assert.Equal("abc123", page.SelectedInstallTarget?.Key);
        Assert.True(page.IsModPackTabActive);
        Assert.False(page.IsBothSources);
        Assert.Equal(1, curseForge.ModpackCalls);
        var card = Assert.Single(page.Results);
        Assert.Equal("Better MC", card.Title);
        Assert.True(card.IsModPack);
        Assert.Equal("整合包", card.KindLabel);
        Assert.Equal(ContentSourceKind.CurseForge, card.Source);

        // 搜索层不按版本筛：状态行只说明整合包搜索的口径。
        Assert.Contains("整合包搜索不按游戏版本筛选", page.FilterText, StringComparison.Ordinal);
        Assert.False(page.IsFilterLimited);
    }

    [Fact]
    public async Task 整合包分区_Modrinth搜索不带版本分面()
    {
        var handler = new JsonHandler("""
        {
          "hits": [
            {
              "project_id": "1KVo5zza",
              "slug": "fabulously-optimized",
              "title": "Fabulously Optimized",
              "description": "以帧率为第一目标的 Fabric 整合包。",
              "author": "robotkoer",
              "downloads": 32118402,
              "follows": 1200,
              "versions": ["1.20.1"],
              "date_modified": "2026-10-01T00:00:00Z"
            }
          ],
          "offset": 0,
          "limit": 20,
          "total_hits": 1
        }
        """);

        var page = CreatePage(
            new FakeInstanceContext(null, GlobalTarget()),
            new FakeVersions([]),
            new FakeModrinth(),
            new FakeCurseForge(),
            catalog: new ModpackCatalogClient(handler));

        await page.SelectModPackCommand.ExecuteAsync(null);

        var card = Assert.Single(page.Results);
        Assert.Equal("Fabulously Optimized", card.Title);
        Assert.Equal("https://modrinth.com/modpack/fabulously-optimized", card.WebUrl);

        var requested = Uri.UnescapeDataString(handler.LastUrl);
        Assert.Contains("project_type:modpack", requested, StringComparison.Ordinal);
        Assert.DoesNotContain("versions:", requested, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 整合包分区_双源合并两站并标出另一站也有()
    {
        var curseForge = new FakeCurseForge
        {
            Modpacks = [CurseForge(1001, "Better MC", modpack: true, updated: Now)],
        };

        var handler = new JsonHandler("""
        {
          "hits": [
            {
              "project_id": "bmc1",
              "slug": "better-mc-forge-bmc1",
              "title": "Better MC",
              "description": "带任务线的 Forge 整合包。",
              "downloads": 21884117,
              "versions": ["1.20.1"],
              "date_modified": "2026-10-24T12:00:00Z"
            }
          ],
          "offset": 0,
          "limit": 20,
          "total_hits": 1
        }
        """);

        var page = CreatePage(
            new FakeInstanceContext(null, GlobalTarget()),
            new FakeVersions([]),
            new FakeModrinth(),
            curseForge,
            catalog: new ModpackCatalogClient(handler));

        await page.SelectModPackCommand.ExecuteAsync(null);
        await page.SelectBothSourcesCommand.ExecuteAsync(null);

        Assert.True(page.IsBothSources);
        var card = Assert.Single(page.Results);
        Assert.Equal("Better MC", card.Title);
        Assert.Equal(ContentSourceKind.CurseForge, card.Source);
        Assert.True(card.HasCounterpart);
        Assert.Equal("Modrinth 也有", card.CounterpartLabel);
        Assert.Equal("https://modrinth.com/modpack/better-mc-forge-bmc1", card.CounterpartUrl);
    }

    [Fact]
    public async Task 整合包版本列表_显示全部构建并可按游戏版本筛选()
    {
        var modrinth = new FakeModrinth
        {
            Versions =
            [
                ModrinthVersion("v2", "1.20.2 构建", "1.20.2", ["1.20.2"]),
                ModrinthVersion("v1", "1.20.1 构建", "1.20.1", ["1.20.1"]),
                ModrinthVersion("v0", "1.19.2 构建", "1.19.2", ["1.19.2"]),
            ],
        };

        var page = CreatePage(
            new FakeInstanceContext(null, GlobalTarget()),
            new FakeVersions([]),
            modrinth,
            new FakeCurseForge(),
            catalog: new ModpackCatalogClient(new JsonHandler("""
            {
              "hits": [
                {
                  "project_id": "1KVo5zza",
                  "slug": "fabulously-optimized",
                  "title": "Fabulously Optimized",
                  "downloads": 32118402,
                  "versions": ["1.20.1"],
                  "date_modified": "2026-10-01T00:00:00Z"
                }
              ],
              "offset": 0,
              "limit": 20,
              "total_hits": 1
            }
            """)));

        await page.SelectModPackCommand.ExecuteAsync(null);

        var selectedGameVersion = page.Results.Single();
        page.SelectedProject = selectedGameVersion;

        await WaitForAsync(() => page.Versions.Count == 3 && !page.IsLoadingVersions, "整合包版本列表加载完成");

        // 版本列表不按安装目标过滤：请求里没有游戏版本与加载器。
        Assert.Null(modrinth.VersionsGameVersion);
        Assert.Equal(LoaderKind.Vanilla, modrinth.VersionsLoader);

        Assert.Equal(["全部版本", "1.20.2", "1.20.1", "1.19.2"],
            page.ModPackVersionFilters.Select(item => item.Label));
        Assert.True(page.ShowsModPackVersionFilter);

        page.SelectedModPackVersionFilter = page.ModPackVersionFilters.Single(item => item.Version == "1.20.1");

        Assert.Equal(["1.20.1 构建"], page.Versions.Select(item => item.Name));

        page.SelectedModPackVersionFilter = page.ModPackVersionFilters[0];

        Assert.Equal(3, page.Versions.Count);
    }

    // ==================== 下载描述归一 ====================

    [Fact]
    public async Task 整合包导入中切页_取消后安装按钮恢复可用()
    {
        var modpack = new BlockingModpackService();
        var log = new RecordingLog();
        using var http = new HttpClient(new StaticBodyHandler([1, 2, 3]));
        var queue = new DownloadQueue(http, log);

        var page = CreatePage(
            new FakeInstanceContext(null, GlobalTarget()),
            new FakeVersions([]),
            new FakeModrinth { Versions = [ModrinthVersion("v1", "1.0 构建", "1.0", ["1.20.1"], fileSize: 3, sha1: null)] },
            new FakeCurseForge(),
            catalog: new ModpackCatalogClient(new JsonHandler("""
            {
              "hits": [
                {
                  "project_id": "pack1",
                  "slug": "fabulously-optimized",
                  "title": "Fabulously Optimized",
                  "downloads": 1000,
                  "versions": ["1.20.1"],
                  "date_modified": "2026-10-01T00:00:00Z"
                }
              ],
              "offset": 0,
              "limit": 20,
              "total_hits": 1
            }
            """)),
            modpack: modpack,
            downloadQueue: queue,
            log: log);

        await page.OnNavigatedToAsync();
        await page.SelectModPackCommand.ExecuteAsync(null);
        page.SelectedProject = page.Results.Single();
        await WaitForAsync(() => page.Versions.Count == 1 && !page.IsLoadingVersions, "整合包版本列表加载完成");

        // 安装任务会挂在 ImportAsync 上，不能 await；靠状态与信号等它进入导入阶段。
        var install = page.ViewModel.InstallCommand.ExecuteAsync(null);

        for (var attempt = 0; attempt < 500 && !modpack.Started; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(
            modpack.Started,
            $"未进入导入阶段：status={page.ViewModel.InstallStatus} error={page.ViewModel.ErrorText} installing={page.ViewModel.IsInstalling} log={string.Join(" | ", log.Messages)}");

        // 切页会取消页面 CTS：导入链抛 OperationCanceledException，安装状态必须回到可操作。
        await page.ViewModel.OnNavigatedFromAsync();
        await WaitForAsync(() => page.ViewModel.InstallStatus == "已取消", "安装被取消");

        Assert.False(page.ViewModel.IsInstalling);
        Assert.True(page.ViewModel.InstallCommand.CanExecute(null));

        await WaitForAsync(() => install.IsCompleted, "安装任务收尾");
    }

    [Fact]
    public void 整合包下载描述_两个后端字段归一()
    {
        var modrinthVersion = new LibraryVersionViewModel(ModrinthVersion("v1", "1.20.1", "1.21.1", ["1.21.1"]));
        var modrinth = LibraryViewModel.DescribeModPackDownload(modrinthVersion);

        Assert.Equal("https://cdn.modrinth.com/data/pack/versions/v1/pack.mrpack", modrinth.Url);
        Assert.Equal("pack.mrpack", modrinth.FileName);
        Assert.Equal(1234, modrinth.Size);
        Assert.Equal("abc123", modrinth.Sha1);
        Assert.Equal("1.20.1", modrinth.VersionNumber);

        var curseForgeVersion = new LibraryVersionViewModel(new CurseForgeFile
        {
            Id = 9,
            ModId = 1001,
            FileName = "better-mc-1.0.zip",
            DisplayName = "Better MC 1.0",
            DownloadUrl = "https://mediafilez.forgecdn.net/files/9/better-mc-1.0.zip",
            FileLength = 5678,
            Sha1 = "def456",
        });
        var curseForge = LibraryViewModel.DescribeModPackDownload(curseForgeVersion);

        Assert.Equal("https://mediafilez.forgecdn.net/files/9/better-mc-1.0.zip", curseForge.Url);
        Assert.Equal("better-mc-1.0.zip", curseForge.FileName);
        Assert.Equal(5678, curseForge.Size);
        Assert.Equal("def456", curseForge.Sha1);
        Assert.Equal("Better MC 1.0", curseForge.VersionNumber);
    }

    // ==================== 辅助 ====================

    private static LibraryPage CreatePage(
        FakeInstanceContext instanceContext,
        FakeVersions versions,
        FakeModrinth modrinth,
        FakeCurseForge curseForge,
        ModpackCatalogClient? catalog = null,
        IModpackService? modpack = null,
        DownloadQueue? downloadQueue = null,
        IAppLog? log = null)
    {
        var services = new ServiceMap()
            .Add<IInstanceContext>(instanceContext)
            .Add<IVersionService>(versions)
            .Add<IModrinthService>(modrinth)
            .Add<ICurseForgeService>(curseForge)
            .Add<ILauncherPaths>(new FakePaths(MinecraftDir))
            .Add<IModpackService>(modpack ?? new FakeModpackService())
            .Add<ModpackCatalogClient>(catalog ?? new ModpackCatalogClient(new JsonHandler("""
            { "hits": [], "offset": 0, "limit": 20, "total_hits": 0 }
            """)));

        if (downloadQueue is not null)
        {
            services.Add(downloadQueue);
        }

        if (log is not null)
        {
            services.Add(log);
        }

        return new LibraryPage(new LibraryViewModel(services), modrinth);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), $"等待超时：{because}");
    }

    private static ContentTarget GlobalTarget() => new()
    {
        Key = "global",
        Kind = ContentTargetKind.Global,
        DisplayName = "全局目录 (.minecraft)",
        GameDirectory = MinecraftDir,
    };

    private static ContentTarget InstanceTarget(
        string id,
        string name,
        string versionName,
        string minecraftVersion,
        LoaderKind loader) => new()
    {
        Key = id,
        Kind = ContentTargetKind.Instance,
        DisplayName = name,
        GameDirectory = Path.Combine(MinecraftDir, "versions", versionName),
        InstanceId = id,
        VersionName = versionName,
        MinecraftVersion = minecraftVersion,
        Loader = loader,
    };

    private static VersionInfo Version(string name, string minecraftVersion, LoaderKind loader) => new()
    {
        Name = name,
        MinecraftVersion = minecraftVersion,
        Loader = loader,
    };

    private static LibraryVersionViewModel LayoutVersion(string versionNumber, IReadOnlyList<string> gameVersions) =>
        new(ModrinthVersion("id-" + versionNumber, versionNumber, versionNumber, gameVersions), showGameVersions: true);

    private static ModrinthVersion ModrinthVersion(
        string id,
        string name,
        string versionNumber,
        IReadOnlyList<string> gameVersions,
        long fileSize = 1234,
        string? sha1 = "abc123") => new()
    {
        Id = id,
        ProjectId = "1KVo5zza",
        Name = name,
        VersionNumber = versionNumber,
        GameVersions = gameVersions,
        FileName = "pack.mrpack",
        DownloadUrl = "https://cdn.modrinth.com/data/pack/versions/" + id + "/pack.mrpack",
        FileSize = fileSize,
        Sha1 = sha1,
        PublishedAt = Now,
    };

    private static ModrinthProject Modrinth(string id, string title) => new()
    {
        ProjectId = id,
        Slug = title.ToLowerInvariant().Replace(' ', '-'),
        Title = title,
        Downloads = 1_000,
        UpdatedAt = Now,
    };

    private static CurseForgeProject CurseForge(
        long id,
        string name,
        bool modpack = false,
        DateTimeOffset? updated = null) => new()
    {
        Id = id,
        Slug = name.ToLowerInvariant().Replace(' ', '-'),
        Name = name,
        Summary = name + " summary",
        DownloadCount = 2_000,
        DateModified = updated ?? Now,
        IsModpack = modpack,
    };

    /// <summary>把 VM 与它使用的 fake 绑在一起，测试里读 fake 的请求记录。</summary>
    private sealed class LibraryPage(LibraryViewModel viewModel, FakeModrinth modrinth)
    {
        public LibraryViewModel ViewModel { get; } = viewModel;

        public FakeModrinth Modrinth { get; } = modrinth;
        public bool IsModPackTabActive => ViewModel.IsModPackTabActive;

        public string FilterText => ViewModel.FilterText;

        public bool IsFilterLimited => ViewModel.IsFilterLimited;

        public string InstallTargetPath => ViewModel.InstallTargetPath;

        public string InstallTargetName => ViewModel.InstallTargetName;

        public LibraryInstallTargetViewModel? SelectedInstallTarget
        {
            get => ViewModel.SelectedInstallTarget;
            set => ViewModel.SelectedInstallTarget = value;
        }

        public IReadOnlyList<LibraryInstallTargetViewModel> InstallTargets => ViewModel.InstallTargets;

        public bool IsBothSources => ViewModel.IsBothSources;

        public IReadOnlyList<LibraryProjectViewModel> Results => ViewModel.Results;

        public IReadOnlyList<LibraryVersionViewModel> Versions => ViewModel.Versions;

        public LibraryProjectViewModel? SelectedProject
        {
            get => ViewModel.SelectedProject;
            set => ViewModel.SelectedProject = value;
        }

        public IReadOnlyList<ModPackVersionFilterOption> ModPackVersionFilters => ViewModel.ModPackVersionFilters;

        public bool ShowsModPackVersionFilter => ViewModel.ShowsModPackVersionFilter;

        public ModPackVersionFilterOption? SelectedModPackVersionFilter
        {
            get => ViewModel.SelectedModPackVersionFilter;
            set => ViewModel.SelectedModPackVersionFilter = value;
        }

        public bool IsLoadingVersions => ViewModel.IsLoadingVersions;

        public IAsyncRelayCommand SelectModPackCommand => ViewModel.SelectModPackCommand;

        public IAsyncRelayCommand<ContentSourceKind> SelectSourceCommand => ViewModel.SelectSourceCommand;

        public IAsyncRelayCommand SelectBothSourcesCommand => ViewModel.SelectBothSourcesCommand;

        public IAsyncRelayCommand InstallCommand => ViewModel.InstallCommand;

        public string InstallStatus => ViewModel.InstallStatus;

        public Task OnNavigatedToAsync() => ViewModel.OnNavigatedToAsync();

        public Task OnNavigatedFromAsync() => ViewModel.OnNavigatedFromAsync();
    }

    private sealed class ServiceMap : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public ServiceMap Add<T>(T service)
            where T : class
        {
            _services[typeof(T)] = service;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUrl = request.RequestUri?.ToString() ?? string.Empty;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private sealed class FakeModrinth(params ModrinthProject[] projects) : IModrinthService
    {
        public string? LastError => null;

        public string? LastGameVersion { get; private set; }

        public LoaderKind LastLoader { get; private set; }

        public string? VersionsGameVersion { get; private set; }

        public LoaderKind VersionsLoader { get; private set; }

        public IReadOnlyList<ModrinthVersion> Versions { get; init; } = [];

        public Task<IReadOnlyList<ModrinthProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            CancellationToken cancellationToken = default) =>
            SearchSortedAsync(kind, query, gameVersion, loader, offset, limit, null, cancellationToken);

        public Task<IReadOnlyList<ModrinthProject>> SearchSortedAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int offset,
            int limit,
            string? sort,
            CancellationToken cancellationToken = default)
        {
            LastGameVersion = gameVersion;
            LastLoader = loader;

            IReadOnlyList<ModrinthProject> page = projects.Skip(offset).Take(limit).ToList();
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
            string projectId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default)
        {
            VersionsGameVersion = gameVersion;
            VersionsLoader = loader;
            return Task.FromResult(Versions);
        }

        public Task<ModrinthInstallResult> InstallAsync(
            ModrinthVersion version,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<ModrinthVersion?> LookupBySha1Async(string sha1, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModrinthVersion?>(null);
    }

    private sealed class FakeCurseForge : ICurseForgeService
    {
        public string? LastError => null;

        public int ModpackCalls { get; private set; }

        public IReadOnlyList<CurseForgeProject> Modpacks { get; init; } = [];

        public Task<IReadOnlyList<CurseForgeProject>> SearchAsync(
            ContentKind kind,
            string query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeProject>>([]);

        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(
            long modId,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFile>>([]);

        public Task<ContentInstallResult> InstallAsync(
            CurseForgeFile file,
            string gameDirectory,
            ContentKind kind,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> CacheIconAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public string BuildSearchUrl(
            ContentKind kind,
            string? query,
            string? gameVersion,
            LoaderKind loader,
            int index,
            int pageSize) => string.Empty;

        public Task<CurseForgeProject?> GetProjectBySlugAsync(
            ContentKind kind,
            string slugOrUrl,
            string? gameVersion,
            LoaderKind loader,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CurseForgeProject?>(null);

        public Task<IReadOnlyList<CurseForgeFingerprintMatch>> LookupByFingerprintsAsync(
            IReadOnlyList<uint> fingerprints,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFingerprintMatch>>([]);

        public Task<IReadOnlyList<CurseForgeProject>> SearchModpacksAsync(
            string query,
            int index,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ModpackCalls++;
            return Task.FromResult(Modpacks);
        }

        public Task<IReadOnlyList<CurseForgeFile>> GetModpackFilesAsync(
            long modId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CurseForgeFile>>([]);
    }

    private sealed class FakeInstanceContext(InstanceInfo? current, params ContentTarget[] targets) : IInstanceContext
    {
        public InstanceInfo? Current { get; } = current;

        public string GameDirectory => Current is null
            ? MinecraftDir
            : targets.FirstOrDefault(target => target.InstanceId == Current.Id)?.GameDirectory ?? MinecraftDir;

        public string? VersionName => Current?.VersionName;

        public LoaderKind Loader => Current is null ? LoaderKind.Vanilla : LoaderKind.Fabric;

        public string? MinecraftVersion => Current is null ? null : "1.20.1";

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SelectAsync(string? instanceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ContentTarget>> ListTargetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentTarget>>(targets);

        public Task<ContentTarget?> GetTargetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(targets.FirstOrDefault(target => target.Key == key));

        public Task<InstanceResolution> ResolveLaunchTargetAsync(
            string versionName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeVersions(IReadOnlyList<VersionInfo> installed) : IVersionService
    {
        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(installed);

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.FromResult(installed.FirstOrDefault(version => version.Name == versionName));

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

    private sealed class FakePaths(string minecraftDirectory) : ILauncherPaths
    {
        /// <summary>
        /// 真实可写的数据根：安装测试会往 <c>cache/modpacks</c> 里落一个归档。
        /// 放在测试程序集目录的 <c>__test-temp</c> 下（与其它用例同一块可写沙箱；
        /// 系统临时目录在本机沙箱里会被拒写）。
        /// </summary>
        private readonly string _root = Path.Combine(
            AppContext.BaseDirectory,
            "__test-temp",
            "nebula-library-" + Guid.NewGuid().ToString("N"));

        public string RootDirectory => _root;

        public string MinecraftDirectory { get; } = minecraftDirectory;

        public string VersionsDirectory => Path.Combine(MinecraftDirectory, "versions");

        public string LibrariesDirectory => Path.Combine(MinecraftDirectory, "libraries");

        public string AssetsDirectory => Path.Combine(MinecraftDirectory, "assets");

        public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

        public string LogsDirectory => Path.Combine(RootDirectory, "logs");

        public string CacheDirectory => Path.Combine(RootDirectory, "cache");

        public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

        public string AccountsFile => Path.Combine(RootDirectory, "accounts.json");

        public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

        public string ServersFile => Path.Combine(RootDirectory, "servers.json");

        public void SetMinecraftDirectory(string path)
        {
        }

        public void EnsureCreated()
        {
        }
    }

    private sealed class FakeModpackService : IModpackService
    {
        public bool InstallGameOnImport { get; set; }

        public ModpackImportResult? LastImport => null;

        public Task<ModpackPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ImportAsync(
            string archivePath,
            string instanceName,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>导入挂起直到被取消：用来构造「导入进行中切页」的场景（B1 回归）。</summary>
    private sealed class BlockingModpackService : IModpackService
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>已经进入 ImportAsync（下载完成、导入开始）。</summary>
        public bool Started => _started.Task.IsCompleted;

        public bool InstallGameOnImport { get; set; }

        public ModpackImportResult? LastImport => null;

        public Task<ModpackPreview> InspectAsync(string archivePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<string> ImportAsync(
            string archivePath,
            string instanceName,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return string.Empty;
        }
    }

    /// <summary>固定返回 200 与一段字节：让 DownloadQueue 的下载阶段立即完成。</summary>
    private sealed class StaticBodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            });
        }
    }

    /// <summary>只用于在失败消息里带上下载链的日志，便于定位「服务异常」的真实原因。</summary>
    private sealed class RecordingLog : IAppLog
    {
        public List<string> Messages { get; } = [];

        public string LogFilePath => string.Empty;

        public void Debug(string message) => Messages.Add("DEBUG " + message);

        public void Info(string message) => Messages.Add("INFO " + message);

        public void Warn(string message, Exception? ex = null) =>
            Messages.Add("WARN " + message + (ex is null ? string.Empty : " :: " + ex));

        public void Error(string message, Exception? ex = null) =>
            Messages.Add("ERROR " + message + (ex is null ? string.Empty : " :: " + ex));
    }
}
