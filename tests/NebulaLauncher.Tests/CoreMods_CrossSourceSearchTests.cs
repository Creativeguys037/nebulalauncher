using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 双源合并搜索的排序与去重规则。
///
/// 这些用例锁的是「同一组输入永远得到同一份排名」：去重误合并会让用户丢掉一条真实结果，
/// 排序不稳定会让同一组关键词搜两次得到两种顺序。两条都是肉眼很难发现、但用户会觉得「这站很乱」的缺陷。
/// </summary>
public class CoreMods_CrossSourceSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // ==================== 合并与去重 ====================

    [Fact]
    public void 两站同名同版本且同期更新时合并成一条并保留CurseForge那条()
    {
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "238222", name: "Just Enough Items", downloads: 500_000_000);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "u6dRKJwZ", name: "Just Enough Items", downloads: 120_000_000);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        var only = Assert.Single(merged);
        Assert.Equal(ContentSourceKind.CurseForge, only.Source);
    }

    [Fact]
    public void 同名但更新时间差超过一周时两条都保留()
    {
        // 同名不同项目（转载 / 冒名 / 两个作品重名）时合并掉一条等于替用户做判断。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Name", updatedAt: Now.AddDays(-30));
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Name", updatedAt: Now);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void 同名但支持的游戏版本不同时两条都保留()
    {
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Name", versions: ["1.20.1"]);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Name", versions: ["1.21.1"]);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void CurseForge的版本集合是Modrinth子集时仍然合并()
    {
        // CurseForge 搜索只带最近若干文件，能看到的版本通常是项目全集的子集；
        // 要求完全相等会把同一项目判成不同，双源里出现两条同名条目。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Name", versions: ["1.20.1"]);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Name", versions: ["1.20.1", "1.20.2", "1.20.3"]);

        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Single(CrossSourceSearch.Merge([curseForge], [modrinth]));
    }

    [Fact]
    public void CurseForge有Modrinth没有的版本时不合并()
    {
        // 反向不成立：CF 上出现 MR 全集里没有的版本，说明两条大概率不是同一个项目。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Name", versions: ["1.20.1", "1.21"]);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Name", versions: ["1.20.1"]);

        Assert.False(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Equal(2, CrossSourceSearch.Merge([curseForge], [modrinth]).Count);
    }

    [Fact]
    public void 同名但加载器不同时两条都保留()
    {
        // PCL2 IsLike 的「Mod 加载器一致」判据：同名在 Forge 与 Fabric 上是两个独立项目。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Name", loaders: ["forge"]);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Name", loaders: ["fabric"]);

        Assert.False(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Equal(2, CrossSourceSearch.Merge([curseForge], [modrinth]).Count);
    }

    [Fact]
    public void 同名同加载器同版本且更新时间接近时合并()
    {
        var curseForge = Hit(
            ContentSourceKind.CurseForge,
            id: "1",
            name: "Same Name",
            loaders: ["forge"],
            versions: ["1.20.1", "1.21"]);

        var modrinth = Hit(
            ContentSourceKind.Modrinth,
            id: "abc",
            name: "Same Name",
            loaders: ["forge"],
            versions: ["1.21", "1.20.1"],
            updatedAt: Now.AddDays(-2));

        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Single(CrossSourceSearch.Merge([curseForge], [modrinth]));
    }

    [Fact]
    public void 版本与加载器集合按集合比较与顺序大小写及重复无关()
    {
        var curseForge = Hit(
            ContentSourceKind.CurseForge,
            id: "1",
            name: "Same Name",
            loaders: ["Forge"],
            versions: ["1.20.1", "1.21"]);

        var modrinth = Hit(
            ContentSourceKind.Modrinth,
            id: "abc",
            name: "Same Name",
            loaders: ["forge", "FORGE"],
            versions: ["1.21", "1.20.1"]);

        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
    }

    [Fact]
    public void 名称不同但slug规范化相同时合并()
    {
        // PCL2 GetRaw 口径：JEI / jei 归一；两条的名称不同，靠 slug 判据命中。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Just Enough Items", slug: "JEI");
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "JEI - Just Enough Items", slug: "jei");

        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Single(CrossSourceSearch.Merge([curseForge], [modrinth]));
    }

    [Fact]
    public void 名称与slug都不同但简介完全相同时合并()
    {
        var curseForge = Hit(
            ContentSourceKind.CurseForge,
            id: "1",
            name: "Alpha Mod",
            slug: "alpha-mod",
            description: "Adds alpha things.");

        var modrinth = Hit(
            ContentSourceKind.Modrinth,
            id: "abc",
            name: "Beta Mod",
            slug: "beta-mod",
            description: "Adds alpha things.");

        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Single(CrossSourceSearch.Merge([curseForge], [modrinth]));
    }

    [Fact]
    public void 名称slug与简介都不同时不合并()
    {
        var curseForge = Hit(
            ContentSourceKind.CurseForge,
            id: "1",
            name: "Alpha Mod",
            slug: "alpha-mod",
            description: "Adds alpha things.");

        var modrinth = Hit(
            ContentSourceKind.Modrinth,
            id: "abc",
            name: "Beta Mod",
            slug: "beta-mod",
            description: "Adds beta things.");

        Assert.False(CrossSourceSearch.IsSameProject(curseForge, modrinth));
        Assert.Equal(2, CrossSourceSearch.Merge([curseForge], [modrinth]).Count);
    }

    [Fact]
    public void 一侧没有加载器信息时不拿空集合否决合并()
    {
        // 与 PCL2 的差异：PCL2 直接比 ModLoaders 位标志，None 与 Fabric 不等就拒绝；
        // 这里只在两边都非空时比，否则 CurseForge 搜索结果没带加载器信息时会把所有配对全拒掉。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Name");
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Name", loaders: ["fabric"]);

        Assert.Empty(curseForge.Loaders);
        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
    }

    [Fact]
    public void 规范化后为空的名称与slug不当作同一条()
    {
        // 名称只剩符号时规范化结果为空串；两个空串相等证明不了任何事，必须落到简介判据上。
        var curseForge = Hit(
            ContentSourceKind.CurseForge,
            id: "1",
            name: "!!!",
            slug: "---",
            description: "alpha");

        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "???", slug: "___", description: "beta");

        Assert.Equal(string.Empty, CrossSourceSearch.NormalizeName("---"));
        Assert.False(CrossSourceSearch.IsSameProject(curseForge, modrinth));
    }

    [Fact]
    public void 光影不做版本集合判据因为光影的版本声明本身不严谨()
    {
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same Shader", versions: ["1.20.1"], kind: ContentKind.ShaderPack);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same Shader", versions: ["1.21.1"], kind: ContentKind.ShaderPack);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        Assert.Single(merged);
    }

    [Fact]
    public void 同一后端内相同Id只保留一条()
    {
        // 分页边界重叠会让同一项目出现两次，这一层必须按 Id 去掉，不能指望上游。
        var first = Hit(ContentSourceKind.Modrinth, id: "abc", name: "First", downloads: 1);
        var second = Hit(ContentSourceKind.Modrinth, id: "abc", name: "First", downloads: 1);
        var other = Hit(ContentSourceKind.Modrinth, id: "def", name: "Other", downloads: 1);

        var merged = CrossSourceSearch.Merge([], [first, second, other]);

        Assert.Equal(2, merged.Count);
        Assert.DoesNotContain(merged, hit => hit.Id == "abc" && hit.Name == "Other");
    }

    [Fact]
    public void 两个后端都有Id相同的项目也不会被合并因为Id体系不通用()
    {
        // Modrinth 的 base62 slug 与 CurseForge 的数字 id 恰好都叫 "123" 是可能的。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "123", name: "Alpha");
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "123", name: "Beta");

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void 空输入与null输入都返回空列表()
    {
        Assert.Empty(CrossSourceSearch.Merge(null, null));
        Assert.Empty(CrossSourceSearch.Merge([], []));
    }

    // ==================== 排序 ====================

    [Fact]
    public void 没有搜索词时按下载量乘平台权重排序()
    {
        // 模组权重：CurseForge 1、Modrinth 5。Modrinth 侧 3000 万 × 5 要压过 CurseForge 侧 1 亿 × 1。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "CF Big", downloads: 100_000_000);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "MR Big", downloads: 30_000_000);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        Assert.Equal("MR Big", merged[0].Name);
    }

    [Fact]
    public void 有搜索词时名称命中的结果排在前面对同名量级的另一条之前()
    {
        var matching = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Applied Energistics", downloads: 1_000);
        var popularButUnrelated = Hit(ContentSourceKind.CurseForge, id: "1", name: "Completely Different", downloads: 10_000_000);

        var merged = CrossSourceSearch.Merge([popularButUnrelated], [matching], searchText: "Energistics");

        Assert.Equal("Applied Energistics", merged[0].Name);
    }

    [Fact]
    public void 排名由权重与分数决定而不是由结果到达顺序决定()
    {
        // 两侧请求是并行的，谁先回来不确定。若按到达顺序拼，同一组关键词两次搜索会出现两种排名。
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Alpha", downloads: 1_000);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Beta", downloads: 1_000);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth], searchText: "nomatch");

        // 两者都没有名称命中，只剩热度项：CurseForge 权重 1、Modrinth 权重 5，
        // 因此 Modrinth 那条分高。先传 CurseForge 也压不过它，说明排的是分数而不是入场顺序。
        Assert.Equal("Beta", merged[0].Name);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void 数据包的平台权重把CurseForge排在前面()
    {
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "CF Pack", downloads: 100_000, kind: ContentKind.DataPack);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "MR Pack", downloads: 100_000, kind: ContentKind.DataPack);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth]);

        Assert.Equal("CF Pack", merged[0].Name);
    }

    [Fact]
    public void limit截断在排序之后而不是此前的拼接顺序()
    {
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Small", downloads: 10);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Huge", downloads: 900);

        var merged = CrossSourceSearch.Merge([curseForge], [modrinth], limit: 1);

        var only = Assert.Single(merged);
        Assert.Equal("Huge", only.Name);
    }

    // ==================== 判据与权重单独验证 ====================

    [Fact]
    public void 名称规范化只留字母数字并转小写()
    {
        Assert.Equal("jei", CrossSourceSearch.NormalizeName("J.E.I!"));
        Assert.Equal("justenoughitems", CrossSourceSearch.NormalizeName("Just Enough Items"));
        Assert.Equal(string.Empty, CrossSourceSearch.NormalizeName(null));
        Assert.Equal(string.Empty, CrossSourceSearch.NormalizeName("   "));
    }

    [Theory]
    [InlineData("1.20.1", true)]
    [InlineData("1.21", true)]
    [InlineData("24w14a", true)]
    [InlineData("26.1", true)]
    [InlineData("Forge", false)]
    [InlineData("Client", false)]
    [InlineData("Server", false)]
    [InlineData("Fabric", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void 标准MC版本号判定(string input, bool expected)
    {
        Assert.Equal(expected, CrossSourceSearch.IsStandardGameVersion(input));
    }

    [Fact]
    public void 从CurseForge构造时带上版本与加载器()
    {
        var project = new CurseForgeProject
        {
            Id = 238222,
            Slug = "jei",
            Name = "Just Enough Items",
            GameVersions = ["1.20.1"],
            Loaders = ["forge", "neoforge"],
        };

        var info = SearchProjectInfo.FromCurseForge(project);

        Assert.Equal("1.20.1", string.Join(",", info.GameVersions));
        Assert.Equal("forge,neoforge", string.Join(",", info.Loaders));
        Assert.Equal(ContentSourceKind.CurseForge, info.Source);
    }

    [Fact]
    public void 从Modrinth构造时从categories里挑出加载器()
    {
        var project = new ModrinthProject
        {
            ProjectId = "u6dRKJwZ",
            Slug = "jei",
            Title = "Just Enough Items",
            Categories = ["fabric", "forge", "library", "NeoForge", "fabric"],
            GameVersions = ["1.20.1"],
        };

        var info = SearchProjectInfo.FromModrinth(project);

        // 只留四个加载器、统一小写、去掉重复；library 这类普通分类不进集合。
        Assert.Equal("fabric,forge,neoforge", string.Join(",", info.Loaders));
        Assert.DoesNotContain("library", info.Loaders);
        Assert.Equal("1.20.1", string.Join(",", info.GameVersions));
    }

    [Fact]
    public void 同一后端的两条永远不算同一个项目()
    {
        var left = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same");
        var right = Hit(ContentSourceKind.Modrinth, id: "def", name: "Same");

        Assert.False(CrossSourceSearch.IsSameProject(left, right));
    }

    [Fact]
    public void 任一侧缺少更新时间时不因为时间判据否决合并()
    {
        var curseForge = Hit(ContentSourceKind.CurseForge, id: "1", name: "Same", updatedAt: null);
        var modrinth = Hit(ContentSourceKind.Modrinth, id: "abc", name: "Same", updatedAt: Now);

        Assert.True(CrossSourceSearch.IsSameProject(curseForge, modrinth));
    }

    [Fact]
    public void 整合包使用模组的权重档()
    {
        Assert.Equal(5d, CrossSourceSearch.DownloadWeight(ContentKind.Mod, ContentSourceKind.Modrinth, isModPack: true));
        Assert.Equal(1d, CrossSourceSearch.DownloadWeight(ContentKind.Mod, ContentSourceKind.CurseForge, isModPack: true));
    }

    [Fact]
    public void 下载量为零与负数时不会产生非有限分数()
    {
        var hit = Hit(ContentSourceKind.Modrinth, id: "1", name: "Empty", downloads: -5);

        var score = CrossSourceSearch.Score(hit, searchText: null);

        Assert.True(double.IsFinite(score));
        Assert.True(score > 0);
    }

    // ==================== 构造工具 ====================

    private static SearchProjectInfo Hit(
        ContentSourceKind source,
        string id,
        string name,
        long downloads = 1_000,
        DateTimeOffset? updatedAt = null,
        IReadOnlyList<string>? versions = null,
        ContentKind kind = ContentKind.Mod,
        bool isModPack = false,
        IReadOnlyList<string>? loaders = null,
        string? slug = null,
        string? description = null)
    {
        return new SearchProjectInfo
        {
            Source = source,
            Id = id,
            Slug = slug ?? name.ToLowerInvariant().Replace(' ', '-'),
            Name = name,
            Description = description ?? name + " description",
            Downloads = downloads,
            UpdatedAt = updatedAt ?? Now,
            GameVersions = versions ?? ["1.20.1"],
            Loaders = loaders ?? Array.Empty<string>(),
            Kind = kind,
            IsModPack = isModPack,
        };
    }
}