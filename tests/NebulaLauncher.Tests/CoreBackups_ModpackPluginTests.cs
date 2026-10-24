using System.Text.Json;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 整合包里声明为「服务端插件」的条目必须落 <c>plugins/</c> 而不是 <c>mods/</c>。
///
/// 后果不对称：判错成 Mod 会装进客户端 <c>mods/</c>，Bukkit 插件调用服务端才有的类，游戏启动直接崩；
/// 判成 Plugin 最坏是放进客户端不加载的目录，用户手动挪走即可。
///
/// 与「搜索安装」的策略有意不同（那边直接拒绝）：整合包是批量还原别人的环境，
/// 逐个拒绝会让整包残缺。所以这里钉的是「落对目录」而不是「拒绝」。
/// </summary>
public class CoreBackups_ModpackPluginTests
{
    private static IReadOnlyList<ModpackAddonReference> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return McbbsAddonParser.Parse(document.RootElement);
    }

    private static ModpackAddonReference Single(string json)
    {
        var items = Parse(json);
        Assert.Single(items);
        return items[0];
    }

    // ---------------- 识别 ----------------

    [Theory]
    [InlineData("plugin")]
    [InlineData("Plugin")]
    [InlineData("bukkit")]
    [InlineData("paper")]
    [InlineData("spigot")]
    [InlineData("folia")]
    [InlineData("purpur")]
    public void 声明类型里的插件词识别为Plugin(string declared)
    {
        // 标量形式：{"type":"plugin"}——KindKeys 覆盖的四个键都要能读到。
        Assert.Equal(ContentKind.Plugin, Single($$"""
            {"addons":[{"projectId":"essentialsx","type":"{{declared}}","fileName":"EssentialsX.jar"}]}
            """).Kind);
    }

    [Theory]
    [InlineData("loaders")]
    [InlineData("categories")]
    [InlineData("tags")]
    public void 加载器数组里的插件词识别为Plugin(string arrayKey)
    {
        // CurseForge 与 MCBBS 的导出工具都会写这三项，数组形态是主路径。
        Assert.Equal(ContentKind.Plugin, Single($$"""
            {"addons":[{"projectId":"essentialsx","{{arrayKey}}":["paper","bukkit"]}]}
            """).Kind);
    }

    [Fact]
    public void 加载器写成标量也识别为Plugin()
    {
        // "loaders":"paper" 与 "loaders":["paper"] 在导出工具里都见过，只认数组会漏判。
        Assert.Equal(ContentKind.Plugin, Single("""
            {"addons":[{"projectId":"essentialsx","loaders":"paper"}]}
            """).Kind);
    }

    [Fact]
    public void 落点路径是plugins时识别为Plugin()
    {
        var reference = Single("""
            {"addons":[{"projectId":"essentialsx","path":"plugins/EssentialsX.jar"}]}
            """);

        Assert.Equal(ContentKind.Plugin, reference.Kind);
        // 声明了路径就按它落盘，不被目录推断覆盖。
        Assert.Equal("plugins/EssentialsX.jar", reference.TargetPath);
    }

    // ---------------- 回归：不该改判的不能改判 ----------------

    [Theory]
    [InlineData("fabric", ContentKind.Mod)]
    [InlineData("forge", ContentKind.Mod)]
    [InlineData("neoforge", ContentKind.Mod)]
    [InlineData("quilt", ContentKind.Mod)]
    public void 客户端加载器仍是Mod(string loader, ContentKind expected)
    {
        Assert.Equal(expected, Single($$"""
            {"addons":[{"projectId":"jei","loaders":["{{loader}}"]}]}
            """).Kind);
    }

    [Fact]
    public void 数据包与资源包光影世界不被误判成插件()
    {
        Assert.Equal(ContentKind.DataPack, Single("""
            {"addons":[{"projectId":"abc","type":"datapack"}]}
            """).Kind);

        Assert.Equal(ContentKind.ResourcePack, Single("""
            {"addons":[{"projectId":"abc","type":"resourcepack"}]}
            """).Kind);

        Assert.Equal(ContentKind.ShaderPack, Single("""
            {"addons":[{"projectId":"abc","type":"shader"}]}
            """).Kind);

        Assert.Equal(ContentKind.World, Single("""
            {"addons":[{"projectId":"abc","path":"saves/世界.zip"}]}
            """).Kind);
    }

    [Fact]
    public void 没有类型信息时按Mod处理()
    {
        // 老包大量存在：只有 projectId + fileId，什么都不声明。默认 Mod 是唯一安全的猜测，
        // 「猜成插件」会把正常 mod 塞进 plugins/，那一侧的错更隐蔽（游戏缺 mod 但不崩）。
        Assert.Equal(ContentKind.Mod, Single("""
            {"addons":[{"projectId":"238222","fileId":"6556121"}]}
            """).Kind);
    }

    [Fact]
    public void 空加载器数组不触发插件判定()
    {
        // 空数组表示「没写」而不是「是插件」，不能因此改判。
        Assert.Equal(ContentKind.Mod, Single("""
            {"addons":[{"projectId":"jei","loaders":[]}]}
            """).Kind);
    }

    // ---------------- 落盘目录 ----------------

    [Fact]
    public void 插件落plugins目录其余各归各位()
    {
        Assert.Equal("plugins", ModpackService.AddonDirectoryOf(ContentKind.Plugin));
        Assert.Equal("mods", ModpackService.AddonDirectoryOf(ContentKind.Mod));
        Assert.Equal("resourcepacks", ModpackService.AddonDirectoryOf(ContentKind.ResourcePack));
        Assert.Equal("shaderpacks", ModpackService.AddonDirectoryOf(ContentKind.ShaderPack));
        Assert.Equal("datapacks", ModpackService.AddonDirectoryOf(ContentKind.DataPack));
        Assert.Equal("saves", ModpackService.AddonDirectoryOf(ContentKind.World));
    }

    [Fact]
    public void 插件永远不会落到mods()
    {
        // 这条是上面那条的「反向守护」：将来有人给 ContentKind 加成员并改坏默认分支，
        // 只要 Plugin 掉进 mods/ 就会红。枚举新增成员时这条不需要改。
        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var directory = ModpackService.AddonDirectoryOf(kind);
            Assert.False(string.IsNullOrWhiteSpace(directory));

            if (kind != ContentKind.Plugin)
            {
                continue;
            }

            Assert.NotEqual("mods", directory);
        }
    }

    // ---------------- 手动下载链接 ----------------

    [Fact]
    public void 插件的手动下载链接走plugin路由()
    {
        var reference = new ModpackAddonReference
        {
            ProjectId = "essentialsx",
            Kind = ContentKind.Plugin,
            Source = ModpackAddonSource.Modrinth,
        };

        // Modrinth 的 API project_type 是 mod，但网页详情页在 /plugin/ 下；
        // 拼成 /mod/ 会让用户在「手动下载」里点进死链。
        Assert.Equal("https://modrinth.com/plugin/essentialsx", ModpackService.WebUrlOf(reference));
    }

    [Fact]
    public void 数字id的插件仍走CurseForge链接()
    {
        var reference = new ModpackAddonReference
        {
            ProjectId = "238222",
            FileId = "6556121",
            Kind = ContentKind.Plugin,
        };

        // 数字 id 一定是 CurseForge 项目，插件类型不改变这个判断。
        Assert.Equal(
            "https://www.curseforge.com/projects/238222/files/6556121",
            ModpackService.WebUrlOf(reference));
    }
}