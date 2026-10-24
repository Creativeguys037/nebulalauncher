using System.Net;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;
using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 服务端插件识别（缺口 R30-2）。
///
/// 背景：Modrinth 把插件项目的 <c>project_type</c> 也报成 <c>mod</c>，真正区分客户端模组与服务端插件
/// 的只有 <c>loaders</c> / <c>categories</c> 里的 <c>bukkit</c> / <c>folia</c> / <c>paper</c> /
/// <c>purpur</c> / <c>spigot</c>（PCL2 <c>ResourceVersion.vb:189-195</c>）。
/// 不做这一步的后果是 EssentialsX 这类插件被装进客户端 <c>mods/</c>，游戏启动即崩。
///
/// 本启动器的口径是**只识别、不安装**：不存在服务端实例概念，所以全部安装入口拒绝落盘，
/// 拒绝原因里要写清「该放服务端的 <c>plugins/</c>」。
/// </summary>
public class CoreMods_PluginKindTests
{
    // ==================== 三段判定（纯函数） ====================

    [Theory]
    [InlineData("bukkit")]
    [InlineData("folia")]
    [InlineData("paper")]
    [InlineData("purpur")]
    [InlineData("spigot")]
    [InlineData("Paper")]
    public void 五种服务端加载器都识别为插件(string loader) =>
        Assert.Equal(ContentKind.Plugin, ModrinthService.ResolveProjectKind(ContentKind.Mod, [loader]));

    [Theory]
    [InlineData("fabric")]
    [InlineData("quilt")]
    [InlineData("forge")]
    [InlineData("neoforge")]
    [InlineData("liteloader")]
    public void 客户端加载器仍是模组(string loader) =>
        Assert.Equal(ContentKind.Mod, ModrinthService.ResolveProjectKind(ContentKind.Mod, [loader]));

    /// <summary>回归：<c>datapack</c> 这一段不能被插件判定吃掉。</summary>
    [Fact]
    public void 数据包仍识别为数据包() =>
        Assert.Equal(ContentKind.DataPack, ModrinthService.ResolveProjectKind(ContentKind.Mod, ["datapack"]));

    /// <summary>
    /// 资源包 / 光影包的 categories 里没有加载器，硬套「有加载器就是模组」会把它们全改判成模组。
    /// </summary>
    [Theory]
    [InlineData(ContentKind.ResourcePack)]
    [InlineData(ContentKind.ShaderPack)]
    public void 资源包与光影包不参与改判(ContentKind requested) =>
        Assert.Equal(requested, ModrinthService.ResolveProjectKind(requested, ["16x", "decoration"]));

    /// <summary>已经是插件的请求不必再判（详情接口直接给了 project_type=plugin 的情形）。</summary>
    [Fact]
    public void 已经判定为插件时保持不变() =>
        Assert.Equal(ContentKind.Plugin, ModrinthService.ResolveProjectKind(ContentKind.Plugin, []));

    [Fact]
    public void project_type里的plugin也认()
    {
        Assert.Equal(ContentKind.Plugin, ModrinthService.ParseProjectKind("plugin"));
        Assert.Equal(ContentKind.Plugin, ModrinthService.ParseProjectKind("  PLUGIN "));
        Assert.Equal(ContentKind.Mod, ModrinthService.ParseProjectKind("mod"));
        Assert.Equal(ContentKind.DataPack, ModrinthService.ParseProjectKind("datapack"));
    }

    // ==================== 搜索 / 详情解析端到端 ====================

    /// <summary>
    /// 判定必须读原始 <c>categories</c>：Modrinth 会把加载器从 <c>display_categories</c> 里过滤掉，
    /// 只看展示分类永远判不出插件 —— 这是这条用例存在的唯一理由。
    /// </summary>
    [Fact]
    public void 搜索命中_按原始categories识别插件()
    {
        const string json = """
                            {
                              "hits": [
                                {
                                  "project_id": "9wBom5Bq",
                                  "slug": "essentialsx",
                                  "title": "EssentialsX",
                                  "description": "服务端基础指令套件",
                                  "author": "md678685",
                                  "downloads": 12000000,
                                  "follows": 900,
                                  "icon_url": "https://cdn.modrinth.com/data/9wBom5Bq/icon.png",
                                  "categories": ["bukkit", "paper", "management", "utility"],
                                  "display_categories": ["management", "utility"],
                                  "versions": ["1.20.1", "1.20.4"],
                                  "date_modified": "2024-05-01T00:00:00Z",
                                  "project_type": "mod"
                                },
                                {
                                  "project_id": "AANobbMI",
                                  "slug": "sodium",
                                  "title": "Sodium",
                                  "description": "渲染优化",
                                  "categories": ["fabric", "optimization"],
                                  "display_categories": ["optimization"],
                                  "versions": ["1.20.1"],
                                  "project_type": "mod"
                                }
                              ]
                            }
                            """;

        var projects = ModrinthService.ParseSearch(json, ContentKind.Mod);

        Assert.Equal(2, projects.Count);
        Assert.Equal(ContentKind.Plugin, projects[0].Kind);
        Assert.Equal(ContentKind.Mod, projects[1].Kind);

        // 展示用的分类不受影响：仍然是过滤后的那两组。
        Assert.Equal(["management", "utility"], projects[0].Categories);
    }

    [Fact]
    public void 详情接口_按loaders识别插件()
    {
        const string json = """
                            {
                              "id": "9wBom5Bq",
                              "slug": "essentialsx",
                              "title": "EssentialsX",
                              "description": "服务端基础指令套件",
                              "project_type": "mod",
                              "loaders": ["paper", "spigot"],
                              "categories": ["management"],
                              "game_versions": ["1.20.1"]
                            }
                            """;

        var project = ModrinthService.ParseProject(json);

        Assert.NotNull(project);
        Assert.Equal(ContentKind.Plugin, project!.Kind);
    }

    /// <summary>插件的网页路径是 <c>/plugin/</c>，用 API 的 <c>mod</c> 去拼会落在错误的页面。</summary>
    [Fact]
    public void 插件项目页走plugin路由()
    {
        var project = new ModrinthProject
        {
            ProjectId = "9wBom5Bq",
            Slug = "essentialsx",
            Title = "EssentialsX",
            Kind = ContentKind.Plugin,
        };

        Assert.Equal("https://modrinth.com/plugin/essentialsx", project.WebUrl);

        var mod = new ModrinthProject
        {
            ProjectId = "AANobbMI",
            Slug = "sodium",
            Title = "Sodium",
            Kind = ContentKind.Mod,
        };

        Assert.Equal("https://modrinth.com/mod/sodium", mod.WebUrl);
    }

    // ==================== 安装被拒绝，且没有落盘 ====================

    [Fact]
    public async Task 插件安装被拒绝且不发请求不落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var gameDirectory = Path.Combine(temp.Path, "game-plugin");

        var handler = new CountingHandler();
        using var service = new ModrinthService(NullAppLog.Instance, paths, handler);

        var result = await service.InstallAsync(Version("EssentialsX.jar"), gameDirectory, ContentKind.Plugin);

        Assert.False(result.Success);
        Assert.Null(result.FilePath);

        // 文案必须说清「该放服务端 plugins/」，否则用户只会以为启动器不支持这个文件。
        Assert.NotNull(result.Error);
        Assert.Contains("plugins/", result.Error!, StringComparison.Ordinal);
        Assert.Contains("服务端插件", result.Error!, StringComparison.Ordinal);

        // 连目录都不该建：拒绝发生在任何 IO 之前。
        Assert.False(Directory.Exists(gameDirectory));

        // 一次网络请求都不能发：不发请求就不会有人以为「下载了但没装上」。
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task 插件计划安装被拒绝且不落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var gameDirectory = Path.Combine(temp.Path, "game-plan-plugin");

        var handler = new CountingHandler();
        using var service = new ModrinthService(NullAppLog.Instance, paths, handler);

        var plan = new ModrinthInstallPlan { Root = Version("EssentialsX.jar") };
        var result = await service.InstallPlanAsync(plan, gameDirectory, ContentKind.Plugin);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains("plugins/", result.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.False(Directory.Exists(gameDirectory));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>插件不该在客户端有落点。给 <c>plugins/</c> 只是兜底：漏网时也不会进 mods/ 让游戏崩。</summary>
    [Fact]
    public void 插件目录映射不落到mods()
    {
        Assert.Equal("plugins", ContentService.DirectoryNameFor(ContentKind.Plugin));
        Assert.NotEqual("mods", ContentService.DirectoryNameFor(ContentKind.Plugin));
    }

    // ==================== 枚举数值守护 ====================

    /// <summary>
    /// 已有数值一个都不能动：已落盘的缓存把 <see cref="ContentKind"/> 按整数存
    /// （见 <c>ContentService</c> 的回收站元数据），改数值等于把「模组」读成「数据包」。
    /// </summary>
    [Fact]
    public void 枚举数值未变_新成员追加在末尾()
    {
        Assert.Equal(0, (int)ContentKind.Mod);
        Assert.Equal(1, (int)ContentKind.ResourcePack);
        Assert.Equal(2, (int)ContentKind.ShaderPack);
        Assert.Equal(3, (int)ContentKind.World);
        Assert.Equal(4, (int)ContentKind.DataPack);
        Assert.Equal(5, (int)ContentKind.Plugin);

        // 兜底：以后再加成员时这条会提醒「想清楚要不要动数值」。
        Assert.Equal(5, Enum.GetValues<ContentKind>().Max(kind => (int)kind));
    }

    [Fact]
    public void 拒绝文案集中在策略类里()
    {
        Assert.True(ServerPluginPolicy.IsServerPlugin(ContentKind.Plugin));
        Assert.False(ServerPluginPolicy.IsServerPlugin(ContentKind.Mod));
        Assert.Contains("plugins/", ServerPluginPolicy.InstallRejectedMessage, StringComparison.Ordinal);
        Assert.Equal("服务端插件", ServerPluginPolicy.KindLabel);
    }

    // ==================== 界面层：徽标与安装闸门 ====================

    /// <summary>
    /// 卡片徽标是用户看到的第一道提示：列表里就写着「服务端插件」，不必等他点了安装才知道。
    /// 这一步不需要改 XAML——徽标文案本来就绑在 <c>KindLabel</c> 上。
    /// </summary>
    [Fact]
    public void 卡片徽标显示服务端插件()
    {
        var project = new NebulaLauncher.ViewModels.LibraryProjectViewModel(new ModrinthProject
        {
            ProjectId = "9wBom5Bq",
            Slug = "essentialsx",
            Title = "EssentialsX",
            Kind = ContentKind.Plugin,
        });

        Assert.True(project.IsServerPlugin);
        Assert.Equal("服务端插件", project.KindLabel);
    }

    [Fact]
    public void 普通模组卡片不被当成插件()
    {
        var project = new NebulaLauncher.ViewModels.LibraryProjectViewModel(new ModrinthProject
        {
            ProjectId = "AANobbMI",
            Slug = "sodium",
            Title = "Sodium",
            Kind = ContentKind.Mod,
        });

        Assert.False(project.IsServerPlugin);
        Assert.Equal("模组", project.KindLabel);
    }

    /// <summary>
    /// 选中插件项目后，安装按钮必须不可用，并且提示条给出完整原因
    /// （按钮灰着却不说原因，用户只会以为启动器坏了）。
    /// </summary>
    [Fact]
    public void 选中插件项目时安装按钮不可用并给出提示()
    {
        var viewModel = new NebulaLauncher.ViewModels.LibraryViewModel(new EmptyServices());

        viewModel.SelectedProject = new NebulaLauncher.ViewModels.LibraryProjectViewModel(new ModrinthProject
        {
            ProjectId = "9wBom5Bq",
            Slug = "essentialsx",
            Title = "EssentialsX",
            Kind = ContentKind.Plugin,
        });

        Assert.False(viewModel.CanInstallNow);
        Assert.True(viewModel.HasServerPluginNotice);
        Assert.Contains("plugins/", viewModel.ServerPluginNotice!, StringComparison.Ordinal);

        // 换成普通模组后提示条要消失，否则会一直挂在别的项目上。
        viewModel.SelectedProject = new NebulaLauncher.ViewModels.LibraryProjectViewModel(new ModrinthProject
        {
            ProjectId = "AANobbMI",
            Slug = "sodium",
            Title = "Sodium",
            Kind = ContentKind.Mod,
        });

        Assert.False(viewModel.HasServerPluginNotice);
    }

    // ==================== 辅助 ====================

    /// <summary>什么都不提供的容器：<see cref="NebulaLauncher.ViewModels.LibraryViewModel"/> 会退化为示例模式。</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static ModrinthVersion Version(string fileName) => new()
    {
        Id = "ver-plugin-1",
        ProjectId = "9wBom5Bq",
        Name = "EssentialsX 2.20.1",
        VersionNumber = "2.20.1",
        FileName = fileName,
        DownloadUrl = "https://cdn.modrinth.com/fake/" + fileName,
        FileSize = 1024,
        Sha1 = new string('0', 40),
    };

    /// <summary>数请求次数：用来证明「拒绝发生在任何网络 IO 之前」。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}