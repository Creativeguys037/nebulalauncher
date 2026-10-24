using NebulaLauncher.Core.Help;

namespace NebulaLauncher.Tests;

/// <summary>
/// 内置帮助的目录与搜索判据。内容是随代码分发的数据，所以这里同时校验「内容别写坏」
/// 与「搜索排序符合权重约定」两件事。
/// </summary>
public sealed class CoreHelp_FilterTests
{
    [Fact]
    public void Filter_EmptyQueryReturnsEverythingInOriginalOrder()
    {
        var topics = HelpCatalog.Topics;

        var filtered = HelpCatalog.Filter(topics, "   ");

        Assert.Equal(topics.Count, filtered.Count);
        Assert.Equal(topics[0].Id, filtered[0].Id);
    }

    [Fact]
    public void Filter_MatchesTitle()
    {
        var filtered = HelpCatalog.Filter(HelpCatalog.Topics, "回收站");

        Assert.NotEmpty(filtered);
        Assert.Contains(filtered, topic => topic.Id == "instances");
    }

    [Fact]
    public void Filter_RanksKeywordHitsAboveBodyHits()
    {
        var withKeyword = new HelpTopic("k", "条目甲", "入门", "整合包", "正文与搜索无关");
        var withBody = new HelpTopic("b", "条目乙", "入门", "无关", "正文里提到整合包一次");
        var topics = new[] { withBody, withKeyword };

        var filtered = HelpCatalog.Filter(topics, "整合包");

        Assert.Equal(2, filtered.Count);
        Assert.Equal("k", filtered[0].Id);
    }

    [Fact]
    public void Filter_MatchesBody()
    {
        var filtered = HelpCatalog.Filter(HelpCatalog.Topics, "sha1");

        Assert.NotEmpty(filtered);
    }

    [Fact]
    public void Filter_ReturnsEmptyWhenNothingMatches()
    {
        var filtered = HelpCatalog.Filter(HelpCatalog.Topics, "这个词一定不存在-zzzz");

        Assert.Empty(filtered);
    }

    [Fact]
    public void Filter_IgnoresNullInput()
        => Assert.Empty(HelpCatalog.Filter(null, "java"));

    [Fact]
    public void Catalog_HasAtLeastTwelveTopicsWithUniqueIdsAndKnownCategories()
    {
        var topics = HelpCatalog.Topics;

        Assert.True(topics.Count >= 12, $"帮助条目过少：{topics.Count}");
        Assert.Equal(topics.Count, topics.Select(topic => topic.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var topic in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Body));
            Assert.False(string.IsNullOrWhiteSpace(topic.Keywords));
            Assert.Contains(topic.Category, HelpCatalog.Categories);
        }
    }

    [Fact]
    public void Catalog_CoversEveryDeclaredCategory()
    {
        foreach (var category in HelpCatalog.Categories)
        {
            Assert.Contains(HelpCatalog.Topics, topic => topic.Category == category);
        }
    }
}
