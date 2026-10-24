using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 卡片在补全前后的展示派生逻辑（纯属性计算，不碰 Avalonia 类型）。
///
/// 这里验证的是「中文名做主标题、英文名降为副标题、描述显示在卡片上」这套取舍，
/// 布局本身由截图验收（<c>tools/shot.ps1 -Page mods</c>）。
/// </summary>
public class AppMods_CardEnrichmentTests
{
    [Fact]
    public void 补全前_标题回退到本地显示名且没有英文名()
    {
        var card = MakeCard("sodium-fabric-0.5.8.jar", displayName: "Sodium");

        Assert.Equal("Sodium", card.TitleText);
        Assert.False(card.HasEnglishName);
        Assert.Null(card.EnglishNameText);
        Assert.False(card.HasDescription);
        Assert.False(card.HasSourceBadge);
    }

    [Fact]
    public void 补全后_中文名做主标题英文名做副标题()
    {
        var card = MakeCard("industrialcraft-2.jar", displayName: "IC2");

        card.ApplyProjectInfo(new ModProjectInfo
        {
            Item = card.Item,
            Source = ContentSourceKind.Modrinth,
            ProjectId = "P1",
            Slug = "industrial-craft",
            Title = "Industrial Craft 2",
            ChineseName = "工业时代2 (Industrial Craft 2)",
            Description = "工业模组",
            WebUrl = "https://modrinth.com/mod/industrial-craft",
        });

        Assert.Equal("工业时代2 (Industrial Craft 2)", card.TitleText);
        Assert.True(card.HasEnglishName);
        Assert.Equal("Industrial Craft 2", card.EnglishNameText);
        Assert.Equal("工业模组", card.DescriptionText);
        Assert.True(card.HasDescription);
        Assert.Equal("Modrinth", card.SourceBadgeText);
        Assert.True(card.HasSourceBadge);
    }

    [Fact]
    public void 补全后_没有译名时用资源站原名()
    {
        var card = MakeCard("carpet.jar", displayName: "carpet");

        card.ApplyProjectInfo(new ModProjectInfo
        {
            Item = card.Item,
            Source = ContentSourceKind.CurseForge,
            Slug = "carpet",
            Title = "Carpet",
        });

        Assert.Equal("Carpet", card.TitleText);

        // 主标题就是原名时不再重复一行英文名。
        Assert.False(card.HasEnglishName);
    }

    [Fact]
    public void 描述_本地元数据优先于资源站摘要()
    {
        var item = MakeItem("jei.jar", displayName: "JEI");
        var card = new ContentCardViewModel(
            new ContentItem
            {
                FilePath = item.FilePath,
                FileName = item.FileName,
                Kind = item.Kind,
                DisplayName = item.DisplayName,
                Description = "作者自己写的介绍",
            },
            new ModsViewModel(new EmptyServices()));

        card.ApplyProjectInfo(new ModProjectInfo
        {
            Item = card.Item,
            Source = ContentSourceKind.Modrinth,
            Slug = "jei",
            Title = "Just Enough Items",
            Description = "资源站摘要",
        });

        Assert.Equal("作者自己写的介绍", card.DescriptionText);
    }

    [Fact]
    public void 搜索_中文名与英文名都能命中()
    {
        var card = MakeCard("ic2.jar", displayName: "IC2");

        card.ApplyProjectInfo(new ModProjectInfo
        {
            Item = card.Item,
            Source = ContentSourceKind.Modrinth,
            Slug = "industrial-craft",
            Title = "Industrial Craft 2",
            ChineseName = "工业时代2",
        });

        Assert.True(card.Matches("工业"));
        Assert.True(card.Matches("industrial"));
        Assert.True(card.Matches("ic2"));
        Assert.False(card.Matches("不存在的关键词"));
    }

    [Fact]
    public void 悬浮说明_包含英文名与项目地址()
    {
        var card = MakeCard("ic2.jar", displayName: "IC2");

        card.ApplyProjectInfo(new ModProjectInfo
        {
            Item = card.Item,
            Source = ContentSourceKind.Modrinth,
            Slug = "industrial-craft",
            Title = "Industrial Craft 2",
            ChineseName = "工业时代2",
            Description = "工业模组",
            WebUrl = "https://modrinth.com/mod/industrial-craft",
        });

        var tip = card.ToolTipText;

        Assert.NotNull(tip);
        Assert.Contains("Industrial Craft 2", tip);
        Assert.Contains("工业模组", tip);
        Assert.Contains("https://modrinth.com/mod/industrial-craft", tip);
    }

    private static ContentCardViewModel MakeCard(string fileName, string displayName)
    {
        var viewModel = new ModsViewModel(new EmptyServices());
        return new ContentCardViewModel(MakeItem(fileName, displayName), viewModel);
    }

    private static ContentItem MakeItem(string fileName, string displayName) => new()
    {
        FilePath = Path.Combine(@"C:\game\mods", fileName),
        FileName = fileName,
        DisplayName = displayName,
        Kind = ContentKind.Mod,
    };

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
