using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 本地模组依赖检查：元数据读取层
/// （fabric.mod.json 的 <c>depends</c>/<c>breaks</c>、mods.toml 的 <c>[[dependencies.xxx]]</c>）
/// 与 <see cref="ContentService.DetectDependencyIssues"/>。
///
/// 读取层全部走真实的 <c>ZipArchive</c> 扫描路径（<see cref="ContentService.ScanAsync"/>），
/// 不直接构造 <c>ContentItem</c>，这样 TomlLite 对带点 section 名的处理也被真实覆盖：
/// <c>[[dependencies.forgemod]]</c> 若被解析丢掉，下面的 TOML 用例必然失败。
/// </summary>
public class CoreAssets_ModDependencyTests
{
    // ---------------- 读取层：fabric.mod.json ----------------

    [Fact]
    public async Task Fabric依赖_读出版本区间并跳过minecraft与加载器()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "fabric-deps",
            ("sodium-extra.jar", new[]
            {
                ("fabric.mod.json", """
                {
                  "id": "sodium-extra",
                  "version": "1.0.0",
                  "depends": {
                    "minecraft": ">=1.20 <1.21",
                    "fabricloader": ">=0.14",
                    "java": ">=17",
                    "fabric-api": ">=0.9"
                  }
                }
                """),
            }));

        var item = Assert.Single(items);
        Assert.Equal(">=1.20 <1.21", item.McVersionRange);

        // minecraft 归 McVersionRange，fabricloader / java 由加载器与 JVM 提供，都不能算「缺前置」。
        var dependency = Assert.Single(item.Depends);
        Assert.Equal("fabric-api", dependency.ModId);
        Assert.Equal(">=0.9", dependency.VersionRange);
        Assert.False(dependency.Optional);
    }

    [Fact]
    public async Task Fabric依赖_值为数组时取第一条非空项()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "fabric-array",
            ("lib.jar", new[]
            {
                ("fabric.mod.json", """
                {
                  "id": "libuser",
                  "version": "2.0",
                  "depends": { "fabric-api": [">=0.9", "<1.0"] }
                }
                """),
            }));

        var dependency = Assert.Single(Assert.Single(items).Depends);
        Assert.Equal("fabric-api", dependency.ModId);
        Assert.Equal(">=0.9", dependency.VersionRange);
    }

    [Fact]
    public async Task Fabric依赖_breaks读进Breaks()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "fabric-breaks",
            ("a.jar", new[]
            {
                ("fabric.mod.json", """
                {
                  "id": "a",
                  "version": "1",
                  "depends": { "fabric-api": ">=0.9" },
                  "breaks": { "optifine": "*", "minecraft": "1.21" }
                }
                """),
            }));

        var item = Assert.Single(items);
        var broken = Assert.Single(item.Breaks);
        Assert.Equal("optifine", broken.ModId);
        Assert.Equal("*", broken.VersionRange);
        Assert.Equal("fabric-api", Assert.Single(item.Depends).ModId);
    }

    // ---------------- 读取层：mods.toml / neoforge.mods.toml ----------------

    [Fact]
    public async Task Toml依赖_两个段都收进Depends且不含minecraft()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "toml-deps",
            ("forge-mod.jar", new[]
            {
                ("META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[47,)"
                [[mods]]
                modId="forgemod"
                version="4.5.6"
                displayName="Forge Mod"
                [[dependencies.forgemod]]
                modId="minecraft"
                mandatory=true
                versionRange="[1.20.1,1.21)"
                [[dependencies.forgemod]]
                modId="jei"
                mandatory=true
                versionRange="[15.0,)"
                [[dependencies.forgemod]]
                modId="cloth-config"
                mandatory=false
                versionRange="*"
                """),
            }));

        var item = Assert.Single(items);
        Assert.Equal("[1.20.1,1.21)", item.McVersionRange);
        Assert.Equal(2, item.Depends.Count);
        Assert.DoesNotContain(item.Depends, dependency =>
            string.Equals(dependency.ModId, "minecraft", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(item.Depends, dependency => dependency.ModId == "jei" && !dependency.Optional);
        Assert.Contains(item.Depends, dependency => dependency.ModId == "cloth-config" && dependency.Optional);
    }

    [Fact]
    public async Task Toml依赖_mandatory为false时是可选()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "toml-mandatory",
            ("a.jar", new[]
            {
                ("META-INF/mods.toml", """
                [[mods]]
                modId="a"
                [[dependencies.a]]
                modId="required-lib"
                mandatory=true
                [[dependencies.a]]
                modId="optional-lib"
                mandatory=false
                """),
            }));

        var depends = Assert.Single(items).Depends;
        Assert.Equal(2, depends.Count);
        Assert.False(depends.Single(dependency => dependency.ModId == "required-lib").Optional);
        Assert.True(depends.Single(dependency => dependency.ModId == "optional-lib").Optional);
    }

    [Fact]
    public async Task Toml依赖_type为optional时是可选()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "toml-type",
            ("a.jar", new[]
            {
                ("META-INF/neoforge.mods.toml", """
                [[mods]]
                modId="a"
                [[dependencies.a]]
                modId="jei"
                type="optional"
                versionRange="[15,)"
                """),
            }));

        var dependency = Assert.Single(Assert.Single(items).Depends);
        Assert.Equal("jei", dependency.ModId);
        Assert.True(dependency.Optional);
    }

    [Fact]
    public async Task Toml依赖_type为incompatible时进Breaks()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "toml-incompatible",
            ("a.jar", new[]
            {
                ("META-INF/mods.toml", """
                [[mods]]
                modId="a"
                [[dependencies.a]]
                modId="sodium"
                type="incompatible"
                versionRange="*"
                """),
            }));

        var item = Assert.Single(items);
        Assert.Empty(item.Depends);
        var broken = Assert.Single(item.Breaks);
        Assert.Equal("sodium", broken.ModId);
        Assert.False(broken.Optional);
    }

    [Fact]
    public async Task Toml依赖_单独breaks段进Breaks()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "toml-breaks-section",
            ("a.jar", new[]
            {
                ("META-INF/mods.toml", """
                [[mods]]
                modId="a"
                [[dependencies.a]]
                modId="jei"
                mandatory=true
                [[breaks.a]]
                modId="oldmod"
                versionRange="(,2.0)"
                """),
            }));

        var item = Assert.Single(items);
        Assert.Equal("jei", Assert.Single(item.Depends).ModId);
        var broken = Assert.Single(item.Breaks);
        Assert.Equal("oldmod", broken.ModId);
        Assert.Equal("(,2.0)", broken.VersionRange);
    }

    // ---------------- 检查层：DetectDependencyIssues ----------------

    [Fact]
    public async Task 缺前置_有提供者时无问题()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-ok",
            ("sodium-extra.jar", new[]
            {
                ("fabric.mod.json", """
                {"id":"sodium-extra","version":"1","depends":{"fabric-api":">=0.9"}}
                """),
            }),
            ("fabric-api.jar", new[]
            {
                ("fabric.mod.json", """{"id":"fabric-api","version":"0.9.1"}"""),
            }));

        var issues = new ContentService(paths).DetectDependencyIssues(items);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task 缺前置_缺失时返回一条并带区间与文件()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-missing",
            ("sodium-extra.jar", new[]
            {
                ("fabric.mod.json", """
                {"id":"sodium-extra","version":"1","depends":{"fabric-api":">=0.9"}}
                """),
            }));

        var issues = new ContentService(paths).DetectDependencyIssues(items);

        var issue = Assert.Single(issues);
        Assert.Equal("sodium-extra", issue.ModId);
        Assert.Equal("fabric-api", issue.MissingId);
        Assert.Equal(">=0.9", issue.RequiredRange);
        Assert.False(issue.Optional);
        Assert.Contains("sodium-extra.jar", issue.Files);
    }

    [Fact]
    public async Task 缺前置_大小写不敏感()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-case",
            ("user.jar", new[]
            {
                ("fabric.mod.json", """{"id":"user","depends":{"fabric-api":">=0.9"}}"""),
            }),
            ("api.jar", new[]
            {
                ("fabric.mod.json", """{"id":"Fabric-API","version":"0.9.1"}"""),
            }));

        Assert.Empty(new ContentService(paths).DetectDependencyIssues(items));
    }

    [Fact]
    public async Task 缺前置_禁用的提供者不算已安装()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-disabled",
            ("user.jar", new[]
            {
                ("fabric.mod.json", """{"id":"user","depends":{"fabric-api":">=0.9"}}"""),
            }),
            ("fabric-api.jar.disabled", new[]
            {
                ("fabric.mod.json", """{"id":"fabric-api","version":"0.9.1"}"""),
            }));

        var issue = Assert.Single(new ContentService(paths).DetectDependencyIssues(items));
        Assert.Equal("fabric-api", issue.MissingId);
    }

    [Fact]
    public async Task 缺前置_禁用的模组自身不报告()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-self-disabled",
            ("user.jar.disabled", new[]
            {
                ("fabric.mod.json", """{"id":"user","depends":{"fabric-api":">=0.9"}}"""),
            }));

        Assert.Empty(new ContentService(paths).DetectDependencyIssues(items));
    }

    [Fact]
    public async Task 缺前置_可选依赖也返回但标记为可选()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-optional",
            ("a.jar", new[]
            {
                ("META-INF/mods.toml", """
                [[mods]]
                modId="a"
                [[dependencies.a]]
                modId="cloth-config"
                mandatory=false
                versionRange="[11,)"
                """),
            }));

        var issue = Assert.Single(new ContentService(paths).DetectDependencyIssues(items));
        Assert.Equal("cloth-config", issue.MissingId);
        Assert.True(issue.Optional);
    }

    [Fact]
    public async Task 缺前置_同一依赖重复声明只报一条()
    {
        using var paths = new CoreAssetsPaths();
        var items = await ScanModsAsync(
            paths,
            "detect-duplicate-decl",
            ("a.jar", new[]
            {
                ("META-INF/mods.toml", """
                [[mods]]
                modId="a"
                [[dependencies.a]]
                modId="jei"
                mandatory=true
                versionRange="[15,)"
                [[dependencies.a]]
                modId="jei"
                mandatory=true
                versionRange="[15,)"
                """),
            }));

        Assert.Single(new ContentService(paths).DetectDependencyIssues(items));
    }

    [Fact]
    public void 缺前置_空列表返回空集合()
    {
        using var paths = new CoreAssetsPaths();
        Assert.Empty(new ContentService(paths).DetectDependencyIssues(Array.Empty<ContentItem>()));
    }

    [Fact]
    public async Task 缺前置_坏压缩包只写ParseError不抛()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-broken-deps");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "broken.jar"), "not a zip");

        var service = new ContentService(paths);
        var items = await service.ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.False(string.IsNullOrWhiteSpace(item.ParseError));
        Assert.Empty(item.Depends);
        Assert.Empty(service.DetectDependencyIssues(items));
    }

    // ---------------- 辅助 ----------------

    /// <summary>在临时目录里造一个 mods 目录并写入 jar，然后按真实扫描路径读回元数据。</summary>
    private static async Task<IReadOnlyList<ContentItem>> ScanModsAsync(
        CoreAssetsPaths paths,
        string instanceName,
        params (string FileName, (string Name, string Content)[] Entries)[] jars)
    {
        var gameDirectory = Path.Combine(paths.RootDirectory, instanceName);
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        foreach (var (fileName, entries) in jars)
        {
            CoreAssets_ContentTests.CreateJar(Path.Combine(modsDirectory, fileName), entries);
        }

        return await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.Mod);
    }
}
