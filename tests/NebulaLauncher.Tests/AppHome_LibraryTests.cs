using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 启动页（票 #86）的契约：实例库搜索、行内三段信息、空态区分、以及「全页唯一主行动」。
/// 前三条测纯 ViewModel 行为，第四条读 XAML 文本——都不触碰 Avalonia 类型。
/// </summary>
public class AppHome_LibraryTests
{
    private const string HomeViewPath = "src/NebulaLauncher.App/Views/HomeView.axaml";

    [Fact]
    public void LibraryItem_ExposesVersionLoaderAndLastPlayed()
    {
        var item = new HomeVersionItem(new VersionInfo
        {
            Name = "fabric-loader-0.16.9-1.21.4",
            Loader = LoaderKind.Fabric,
            LoaderVersion = "0.16.9",
            MinecraftVersion = "1.21.4",
            LastPlayedAt = DateTimeOffset.Now.AddHours(-2),
        }, index: 2);

        Assert.Equal("02", item.IndexText);
        Assert.Equal("1.21.4", item.MinecraftVersion);
        Assert.Equal("Fabric 0.16.9", item.LoaderDetail);
        Assert.Contains("2 小时前", item.LastPlayedText);
    }

    [Fact]
    public void LibraryItem_FallsBackToDirectoryNameWhenMinecraftVersionMissing()
    {
        // 解析不出原版号时（例如只装了原版目录），目录名本身就是版本号，不能空着。
        var item = new HomeVersionItem(new VersionInfo
        {
            Name = "1.20.1",
            Loader = LoaderKind.Vanilla,
        });

        Assert.Equal("1.20.1", item.MinecraftVersion);
        Assert.Equal("原版", item.LoaderDetail);
        Assert.Equal("从未启动", item.LastPlayedText);
    }

    [Theory]
    [InlineData("fabric", true)]      // 命中加载器
    [InlineData("1.21", true)]        // 命中版本号
    [InlineData("LOADER", true)]      // 大小写不敏感
    [InlineData("neoforge", false)]   // 名称不匹配
    [InlineData("", true)]            // 空查询显示全部
    public void LibraryItem_MatchesByAnyOfThreeFields(string query, bool expected)
    {
        var item = new HomeVersionItem(new VersionInfo
        {
            Name = "fabric-loader-0.16.9-1.21.4",
            Loader = LoaderKind.Fabric,
            LoaderVersion = "0.16.9",
            MinecraftVersion = "1.21.4",
        });

        Assert.Equal(expected, item.Matches(query));
    }

    [Fact]
    public void HomeView_HasExactlyOnePrimaryAction()
    {
        // 票 #86 验收：全页只有一个主行动样式。三处 primary 分别是
        //   ① hero 的「启动游戏」；② 同一按钮的运行中形态「停止游戏」（与 ① 互斥）；
        //   ③ 空态的「创建第一个实例」（与 ①② 互斥，有实例时不渲染）。
        // 「新建实例」与行内「启动」必须是次级样式，否则同屏会出现多个主行动。
        var text = File.ReadAllText(Path.Combine(RepoRoot(), HomeViewPath));
        var primaryCount = Regex.Matches(text, "Classes=\"primary\"").Count;

        Assert.Equal(3, primaryCount);
        // 票 #106 把 Hero 挪进了 Row2 的 GlassPanel：`glass` 只在 L2 用一次，
        // L3 内容再叠一层玻璃就是玻璃套玻璃（UI-STRUCTURE §6 规则 6），所以这里是 card hero。
        Assert.Contains("Classes=\"card hero\"", text);
        Assert.Contains("Classes=\"secondary\"", text);

        // 搜索行与实例行的按钮都不许是 primary。
        var newInstanceAt = text.IndexOf("Content=\"新建实例\"", StringComparison.Ordinal);
        Assert.True(newInstanceAt > 0, "HomeView.axaml 里找不到「新建实例」按钮");
        var newInstanceBlock = text.Substring(Math.Max(0, newInstanceAt - 300), Math.Min(600, text.Length - Math.Max(0, newInstanceAt - 300)));
        Assert.Contains("Classes=\"secondary\"", newInstanceBlock);
    }

    [Fact]
    public void HomeView_DistinguishesTwoEmptyStates()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), HomeViewPath));

        Assert.Contains("IsVisible=\"{Binding IsRecentEmpty}\"", text);        // 一个实例都没有
        Assert.Contains("IsVisible=\"{Binding IsLibrarySearchEmpty}\"", text); // 搜索没命中
        Assert.Contains("创建第一个实例", text);
    }

    [Fact]
    public void HomeView_HidesSearchAndCreateWhenThereIsNoInstance()
    {
        // 边界条款：无实例时搜索框与「新建实例」要藏起来，避免留一排空控件。
        var text = File.ReadAllText(Path.Combine(RepoRoot(), HomeViewPath));
        var header = text[..text.IndexOf("实例库", StringComparison.Ordinal)];

        Assert.Contains("IsVisible=\"{Binding HasAnyVersion}\"", text);
        Assert.Contains("Watermark=\"搜索实例名 / 版本 / 加载器\"", text);
        Assert.False(string.IsNullOrWhiteSpace(header));
    }

    [Fact]
    public void HeroLaunch_IsDisabledWithoutAnyInstance()
    {
        // 无实例时 hero 的「启动游戏」必须是禁用态：文案还写着「未选择版本」却能点，
        // 用户会点进一次必然失败的启动。CanHeroLaunch 是唯一判据。
        var vm = new HomeViewModel();

        Assert.False(vm.HasAnyVersion);
        Assert.False(vm.CanHeroLaunch);
        Assert.Equal("就绪", vm.HeroStatusText);
        Assert.False(vm.HasHeroStatus);
    }

    [Fact]
    public void HomeView_RowCarriesIndexIconNameTagsAndMetadata()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), HomeViewPath));

        foreach (var field in new[] { "IndexText", "MinecraftVersion", "LoaderDetail", "LastPlayedText" })
        {
            Assert.Contains($"{{Binding {field}}}", text);
        }

        // 行高 52、图标 40×40（票面规格）
        Assert.Contains("Height=\"52\"", text);
        Assert.Contains("Width=\"40\" Height=\"40\"", text);
    }

    [Fact]
    public void HeroTitle_TruncatesWithTooltip()
    {
        // 边界条款：实例名超长时截断并保留完整名称的 ToolTip。
        var text = File.ReadAllText(Path.Combine(RepoRoot(), HomeViewPath));
        var at = text.IndexOf("Classes=\"hero-title\"", StringComparison.Ordinal);
        Assert.True(at > 0, "HomeView.axaml 里找不到 hero-title");

        var start = Math.Max(0, at - 220);
        var head = text.Substring(start, Math.Min(400, text.Length - start));

        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", head);
        Assert.Contains("ToolTip.Tip=\"{Binding SelectedVersionName}\"", head);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NebulaLauncher.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上找不到 NebulaLauncher.sln");
    }
}