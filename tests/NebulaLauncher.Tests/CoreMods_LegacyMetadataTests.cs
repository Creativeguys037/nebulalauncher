using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 旧版 MetaData 读取：mcmod.info（Forge 1.7.10–1.12.2）、litemod.json、quilt.mod.json，
/// 以及 fabric 的 contact。
///
/// 这些文件此前一条都没有解析路径，表现是「1.7.10 的模组卡片上只有文件名」——
/// 最容易在验收时被当成「读取功能没做」。
/// </summary>
public class CoreMods_LegacyMetadataTests
{
    [Fact]
    public async Task 扫描_读取mcmod_info裸数组格式()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-mcmod-array");
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(modsDirectory, "industrialcraft-2-2.2.828.jar"),
            ("mcmod.info", System.Text.Encoding.UTF8.GetBytes("""
            [
              {
                "modid": "IC2",
                "name": "IndustrialCraft 2",
                "description": "工业模组",
                "version": "2.2.828",
                "mcversion": "1.7.10",
                "authorList": ["IC2 Dev Team", "Player"],
                "logoFile": "assets/ic2/logo.png",
                "url": "https://example.invalid/ic2"
              }
            ]
            """)),
            ("assets/ic2/logo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("IC2", item.Id);
        Assert.Equal("IndustrialCraft 2", item.DisplayName);
        Assert.Equal("2.2.828", item.Version);
        Assert.Equal("工业模组", item.Description);
        Assert.Equal("IC2 Dev Team, Player", item.Authors);
        Assert.Equal("1.7.10", item.McVersionRange);
        Assert.Equal("https://example.invalid/ic2", item.HomePage);
        Assert.Equal(LoaderKind.Forge, item.Loader);
        Assert.NotNull(item.IconPath);
        Assert.Null(item.ParseError);
    }

    [Fact]
    public async Task 扫描_读取mcmod_info的modList格式()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-mcmod-list");
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(modsDirectory, "jei.jar"),
            ("mcmod.info", """
            {
              "modListVersion": 2,
              "modList": [
                { "modid": "jei", "name": "Just Enough Items", "version": "4.16.1.301", "mcversion": "1.12.2" }
              ]
            }
            """));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("jei", item.Id);
        Assert.Equal("Just Enough Items", item.DisplayName);
        Assert.Equal("4.16.1.301", item.Version);
        Assert.Equal("1.12.2", item.McVersionRange);
        Assert.Equal(LoaderKind.Forge, item.Loader);
    }

    [Fact]
    public async Task 扫描_读取litemod_json()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-litemod");
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(modsDirectory, "macros.litemod"),
            ("litemod.json", """
            {
              "name": "Macro/Keybind Mod",
              "version": "0.15.2",
              "mcversion": "1.12.2",
              "author": "Mumfrey",
              "description": "宏与按键绑定"
            }
            """));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("Macro/Keybind Mod", item.DisplayName);
        Assert.Equal("0.15.2", item.Version);
        Assert.Equal("1.12.2", item.McVersionRange);
        Assert.Equal("Mumfrey", item.Authors);
        Assert.Equal("宏与按键绑定", item.Description);

        // LiteLoader 不在 LoaderKind 里：硬塞成 Forge 会让按加载器分组显示出错的分类。
        Assert.Equal(LoaderKind.Vanilla, item.Loader);
    }

    [Fact]
    public async Task 扫描_读取quilt并优先于fabric()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-quilt");
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(modsDirectory, "quilt-only.jar"),
            ("quilt.mod.json", """
            {
              "schema_version": 1,
              "quilt_loader": {
                "id": "qmod",
                "version": "1.0.0",
                "metadata": {
                  "name": "Quilt Mod",
                  "description": "织物模组",
                  "icon": "icon.png",
                  "contributors": { "Owner": "甲", "Developer": ["乙", "丙"] },
                  "contact": { "homepage": "https://example.invalid/qmod" }
                },
                "depends": [
                  { "id": "minecraft", "versions": ">=1.20" },
                  { "id": "qsl", "versions": "*" }
                ]
              }
            }
            """),
            // Quilt 模组通常同时带 fabric.mod.json（兼容层）。loader 判定必须按 quilt 走。
            ("fabric.mod.json", """{"id":"qmod","name":"Quilt Mod","version":"1.0.0"}"""));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("qmod", item.Id);
        Assert.Equal(LoaderKind.Quilt, item.Loader);
        Assert.Equal(">=1.20", item.McVersionRange);
        Assert.Equal("https://example.invalid/qmod", item.HomePage);
        Assert.Contains("甲", item.Authors);
        Assert.Contains("乙", item.Authors);
        Assert.Contains("丙", item.Authors);

        var dependency = Assert.Single(item.Depends);
        Assert.Equal("qsl", dependency.ModId);
    }

    [Fact]
    public async Task 扫描_读取fabric的contact主页()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-contact");
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(modsDirectory, "sodium.jar"),
            ("fabric.mod.json", """
            {
              "id": "sodium",
              "name": "Sodium",
              "version": "0.5.8",
              "contact": { "homepage": "https://example.invalid/sodium", "sources": "https://example.invalid/src" }
            }
            """));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("https://example.invalid/sodium", item.HomePage);
    }

    [Fact]
    public async Task 扫描_剔除未替换的版本占位符()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-placeholder");
        var modsDirectory = Path.Combine(gameDirectory, "mods");

        CoreAssets_ContentTests.CreateJar(
            Path.Combine(modsDirectory, "template.jar"),
            ("mcmod.info", """[{ "modid": "tpl", "name": "Template Mod", "version": "${version}" }]"""));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("Template Mod", item.DisplayName);

        // 「${version}」显示在卡片上比空白更糟；当成没有版本号。
        Assert.Equal(string.Empty, item.Version);
    }

    [Fact]
    public void 译名表_解析四种slug写法与星号占位()
    {
        var content = string.Join('\n', new[]
        {
            string.Empty,
            "industrial-craft@|工业时代2 (Industrial Craft 2)",
            "@redpower2-core|红石力量2 (RedPower2)",
            "cfmod@mrmod|双平台模组",
            "onlycf|仅CF*",
            "no-name-entry@",
            "abc",
        });

        var table = ModNameTable.Parse(content);

        Assert.Equal("工业时代2 (Industrial Craft 2)", table.FindCurseForge("industrial-craft"));
        Assert.Equal("工业时代2 (Industrial Craft 2)", table.FindModrinth("industrial-craft"));

        // 只有 @ 前缀：仅 Modrinth。
        Assert.Equal("红石力量2 (RedPower2)", table.FindModrinth("redpower2-core"));
        Assert.Null(table.FindCurseForge("redpower2-core"));

        // 「cf@mr」：两边各一个 slug。
        Assert.Equal("双平台模组", table.FindCurseForge("cfmod"));
        Assert.Equal("双平台模组", table.FindModrinth("mrmod"));

        // 星号替换成「英文名」：slug 的 - 换成空格、首字母大写。
        Assert.Equal("仅CF (Onlycf)", table.FindCurseForge("onlycf"));

        // 只有 slug、没有译名的条目不进索引。
        Assert.Null(table.FindCurseForge("no-name-entry"));
    }

    [Fact]
    public void 译名表_末行浏览量不计入条目()
    {
        // 格式化约定：最后一行是 3 字符一组的 base86 浏览量，不是译名。
        var content = "a|甲\n" + "b|乙\n" + "3w'3qN3w6";

        var table = ModNameTable.Parse(content);

        Assert.Equal("甲", table.FindCurseForge("a"));
        Assert.Equal("乙", table.FindCurseForge("b"));
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void 译名表_嵌入资源可加载()
    {
        // 资源缺失时 Count 为 0（发布漏了 EmbeddedResource），这条测试就是那种回归的哨兵。
        // 阈值取 12000 而不是原始条目数 26283：同一 slug 在多行里重复出现时只保留首条
        // （PCL 的 FirstOrDefault 语义），且部分条目只有 CurseForge 或只有 Modrinth 一个键，
        // 所以可查键数天然少于条目数。
        Assert.True(
            ModNameTranslations.Count > 12000,
            $"嵌入译名表条目数只有 {ModNameTranslations.Count}，检查 Resources/WikiEntries.txt 是否被打进程序集");

        Assert.Equal("工业时代2 (Industrial Craft 2)", ModNameTranslations.FindByCurseForgeSlug("industrial-craft"));
        Assert.Equal("工业时代2 (Industrial Craft 2)", ModNameTranslations.FindByModrinthSlug("industrial-craft"));
        Assert.Null(ModNameTranslations.FindByModrinthSlug("这个slug不存在-zzz"));
    }
}
