using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

public class CoreAssets_ContentTests
{
    [Theory]
    [InlineData(">=1.20 <1.21", "1.20.1", true)]
    [InlineData(">=1.20 <1.21", "1.21", false)]
    [InlineData(">=1.20 <1.21", "1.19.4", false)]
    [InlineData("[1.20.1,1.21)", "1.20.1", true)]
    [InlineData("[1.20.1,1.21)", "1.20.4", true)]
    [InlineData("[1.20.1,1.21)", "1.21", false)]
    [InlineData("(1.20,1.21]", "1.20", false)]
    [InlineData("(1.20,1.21]", "1.21", true)]
    [InlineData("[1.20.1]", "1.20.1", true)]
    [InlineData("[1.20.1]", "1.20.2", false)]
    [InlineData("1.20.x", "1.20.4", true)]
    [InlineData("1.20.x", "1.21", false)]
    [InlineData("1.20.*", "1.20", true)]
    [InlineData("1.20", "1.20.6", true)]
    [InlineData("*", "1.7.10", true)]
    [InlineData("", "1.21", true)]
    [InlineData(">=1.20,<1.21", "1.20.2", true)]
    [InlineData("<=1.16.5", "1.16.4", true)]
    [InlineData("<=1.16.5", "1.17", false)]
    public void 版本区间匹配(string range, string minecraftVersion, bool expected)
    {
        Assert.Equal(expected, McVersionRange.Parse(range).Contains(minecraftVersion));
    }

    [Fact]
    public void 无法解析的区间视为不限制()
    {
        Assert.True(McVersionRange.Parse("完全不是版本号").IsUnbounded);
        Assert.True(McVersionRange.Parse("完全不是版本号").Contains("1.20.1"));
    }

    [Fact]
    public void 冲突检测_同modid多文件()
    {
        var service = new ContentService(new CoreAssetsPaths());
        var mods = new List<ContentItem>
        {
            Mod("example.jar", "examplemod", "1.0.0"),
            Mod("example-2.jar", "examplemod", "2.0.0"),
            Mod("other.jar", "othermod", "1.0.0"),
        };

        var conflicts = service.DetectConflicts(mods);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("examplemod", conflict.ModId);
        Assert.Equal(2, conflict.Files.Count);
        Assert.Contains("example.jar", conflict.Files);
        Assert.Contains("example-2.jar", conflict.Files);
    }

    [Fact]
    public void 冲突检测_无重复时为空且非null()
    {
        var service = new ContentService(new CoreAssetsPaths());
        var conflicts = service.DetectConflicts([Mod("a.jar", "a", "1"), Mod("b.jar", "b", "1")]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void 版本不匹配_只返回声明区间不覆盖的模组()
    {
        var service = new ContentService(new CoreAssetsPaths());
        var mods = new List<ContentItem>
        {
            Mod("a.jar", "a", "1", ">=1.20 <1.21"),
            Mod("b.jar", "b", "2", "[1.21,)"),
            Mod("c.jar", "c", "3", null),
            Mod("d.jar", "d", "4", "1.20.x"),
        };

        var mismatched = service.FindVersionMismatch(mods, "1.20.4");

        var single = Assert.Single(mismatched);
        Assert.Equal("b.jar", single.FileName);
    }

    [Fact]
    public async Task 扫描_读取fabric模组元数据与图标()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-scan");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03 };
        CreateJar(
            Path.Combine(modsDirectory, "nice-mod-1.2.3.jar"),
            ("fabric.mod.json", Encoding.UTF8.GetBytes("""
            {
              "id": "nicemod",
              "name": "Nice Mod",
              "version": "1.2.3",
              "description": "一个测试模组",
              "authors": ["甲", "乙"],
              "icon": "assets/nicemod/icon.png",
              "depends": { "minecraft": ">=1.20 <1.21" }
            }
            """)),
            ("assets/nicemod/icon.png", icon));

        var service = new ContentService(paths);
        var items = await service.ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.Equal("nicemod", item.Id);
        Assert.Equal("Nice Mod", item.DisplayName);
        Assert.Equal("1.2.3", item.Version);
        Assert.Equal("甲, 乙", item.Authors);
        Assert.Equal(">=1.20 <1.21", item.McVersionRange);
        Assert.Equal(LoaderKind.Fabric, item.Loader);
        Assert.True(item.IsEnabled);
        Assert.Null(item.ParseError);
        Assert.NotNull(item.IconPath);
        Assert.True(File.Exists(item.IconPath));
        Assert.Equal(icon, await File.ReadAllBytesAsync(item.IconPath!));
    }

    [Fact]
    public async Task 扫描_识别forge与neoforge的mods_toml()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-toml");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        CreateJar(
            Path.Combine(modsDirectory, "forge-mod.jar"),
            ("META-INF/mods.toml", Encoding.UTF8.GetBytes("""
            modLoader="javafml"
            loaderVersion="[47,)"
            license="MIT"
            [[mods]]
            modId="forgemod"
            version="4.5.6"
            displayName="Forge Mod"
            description='''多行
            描述'''
            logoFile="logo.png"
            [[dependencies.forgemod]]
            modId="minecraft"
            mandatory=true
            versionRange="[1.20.1,1.21)"
            """)),
            ("logo.png", new byte[] { 1, 2, 3 }));

        CreateJar(
            Path.Combine(modsDirectory, "neo-mod.jar"),
            ("META-INF/neoforge.mods.toml", """
            [[mods]]
            modId="neomod"
            version="1.0"
            displayName="Neo Mod"
            """));

        var service = new ContentService(paths);
        var items = await service.ScanAsync(gameDirectory, ContentKind.Mod);

        Assert.Equal(2, items.Count);

        var forgeMod = items.Single(item => item.FileName == "forge-mod.jar");
        Assert.Equal("forgemod", forgeMod.Id);
        Assert.Equal(LoaderKind.Forge, forgeMod.Loader);
        Assert.Equal("[1.20.1,1.21)", forgeMod.McVersionRange);
        Assert.NotNull(forgeMod.IconPath);

        var neoMod = items.Single(item => item.FileName == "neo-mod.jar");
        Assert.Equal("neomod", neoMod.Id);
        Assert.Equal(LoaderKind.NeoForge, neoMod.Loader);
    }

    [Fact]
    public async Task 扫描_模组未声明图标时回落到assets目录()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-icon-fallback");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0A };

        // Fabric：只放图、不写 icon 字段。
        CreateJar(
            Path.Combine(modsDirectory, "fabric-mod.jar"),
            ("fabric.mod.json", Encoding.UTF8.GetBytes("""{"id":"fabricmod","name":"Fabric Mod","version":"1.0"}""")),
            ("assets/fabricmod/icon.png", icon));

        // 1.7.10 / 1.12.2 的 mcmod.info：logoFile 是空串（IC2、JEI Legacy 的真实形状）。
        CreateJar(
            Path.Combine(modsDirectory, "legacy-mod.jar"),
            ("mcmod.info", Encoding.UTF8.GetBytes(
                """[{"modid":"legacymod","name":"Legacy Mod","version":"1.0","logoFile":"","mcversion":"1.7.10"}]""")),
            ("assets/legacymod/logo.png", icon));

        // 没有任何图标文件：仍然不能有 IconPath。
        CreateJar(
            Path.Combine(modsDirectory, "plain-mod.jar"),
            ("fabric.mod.json", Encoding.UTF8.GetBytes("""{"id":"plainmod","name":"Plain Mod","version":"1.0"}""")));

        var service = new ContentService(paths);
        var items = await service.ScanAsync(gameDirectory, ContentKind.Mod);

        var fabricMod = items.Single(item => item.FileName == "fabric-mod.jar");
        Assert.NotNull(fabricMod.IconPath);
        Assert.Equal(icon, await File.ReadAllBytesAsync(fabricMod.IconPath!));

        var legacyMod = items.Single(item => item.FileName == "legacy-mod.jar");
        Assert.NotNull(legacyMod.IconPath);
        Assert.Equal(icon, await File.ReadAllBytesAsync(legacyMod.IconPath!));

        var plainMod = items.Single(item => item.FileName == "plain-mod.jar");
        Assert.Null(plainMod.IconPath);
    }

    [Fact]
    public async Task 扫描_光影包从包内读取图标()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-shader");
        var shaderDirectory = Path.Combine(gameDirectory, "shaderpacks");
        Directory.CreateDirectory(shaderDirectory);

        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0B };

        // 形态 A：pack.png 在包根目录（资源包 / 数据包同一位置）。
        CreateJar(
            Path.Combine(shaderDirectory, "root-icon.zip"),
            ("shaders/shader.properties", Encoding.UTF8.GetBytes("name=Root\nversion=1.0")),
            ("pack.png", icon));

        // 形态 B：只有 shaders/pack.png。
        CreateJar(
            Path.Combine(shaderDirectory, "nested-icon.zip"),
            ("shaders/shader.properties", Encoding.UTF8.GetBytes("name=Nested\nversion=1.0")),
            ("shaders/pack.png", icon));

        // 形态 C：没有图标。
        CreateJar(
            Path.Combine(shaderDirectory, "no-icon.zip"),
            ("shaders/shader.properties", Encoding.UTF8.GetBytes("name=Plain\nversion=1.0")));

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.ShaderPack);

        Assert.Equal(3, items.Count);
        foreach (var name in new[] { "root-icon.zip", "nested-icon.zip" })
        {
            var item = items.Single(entry => entry.FileName == name);
            Assert.NotNull(item.IconPath);
            Assert.Equal(icon, await File.ReadAllBytesAsync(item.IconPath!));
        }

        Assert.Null(items.Single(item => item.FileName == "no-icon.zip").IconPath);
    }

    [Fact]
    public async Task 扫描_数据包读取pack_png图标()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-datapack-icon");
        var dataPackDirectory = Path.Combine(gameDirectory, "datapacks");
        Directory.CreateDirectory(dataPackDirectory);

        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0C };

        // zip 形态。
        CreateJar(
            Path.Combine(dataPackDirectory, "zipped.zip"),
            ("pack.mcmeta", Encoding.UTF8.GetBytes("""{"pack":{"pack_format":48,"description":"zip 数据包"}}""")),
            ("pack.png", icon));

        // 目录形态（Minecraft 把 datapacks/ 下每个子目录都当一个包）。
        var directoryForm = Path.Combine(dataPackDirectory, "unzipped");
        Directory.CreateDirectory(directoryForm);
        await File.WriteAllBytesAsync(Path.Combine(directoryForm, "pack.png"), icon);
        await File.WriteAllTextAsync(
            Path.Combine(directoryForm, "pack.mcmeta"),
            """{"pack":{"pack_format":48,"description":"目录数据包"}}""");

        var items = await new ContentService(paths).ScanAsync(gameDirectory, ContentKind.DataPack);

        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.NotNull(item.IconPath);
            Assert.Equal(icon, await File.ReadAllBytesAsync(item.IconPath!));
        }
    }

    [Fact]
    public async Task 扫描_坏压缩包写ParseError不抛()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-broken");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "broken.jar"), "这不是 zip");

        var service = new ContentService(paths);
        var items = await service.ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.False(string.IsNullOrWhiteSpace(item.ParseError));
    }

    [Fact]
    public async Task 扫描_禁用的模组标记为未启用()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-disabled");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        CreateJar(Path.Combine(modsDirectory, "off.jar.disabled"), ("fabric.mod.json", """{"id":"off","version":"1"}"""));

        var service = new ContentService(paths);
        var items = await service.ScanAsync(gameDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.False(item.IsEnabled);
        Assert.Equal("off.jar.disabled", item.FileName);
        Assert.Equal("off", item.Id);
    }

    [Fact]
    public async Task 启用禁用_幂等且不残留双后缀()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-toggle");
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        var jarPath = Path.Combine(modsDirectory, "toggle.jar");
        CreateJar(jarPath, ("fabric.mod.json", """{"id":"toggle","version":"1"}"""));

        var service = new ContentService(paths);
        var item = Assert.Single(await service.ScanAsync(gameDirectory, ContentKind.Mod));

        await service.SetEnabledAsync(item, false);
        Assert.True(File.Exists(jarPath + ".disabled"));
        Assert.False(File.Exists(jarPath));
        Assert.False(item.IsEnabled);

        // 再禁用一次：同一个对象、旧路径都不应造成二次改名。
        await service.SetEnabledAsync(item, false);
        Assert.True(File.Exists(jarPath + ".disabled"));
        Assert.False(File.Exists(jarPath + ".disabled.disabled"));

        await service.SetEnabledAsync(item, true);
        Assert.True(File.Exists(jarPath));
        Assert.False(File.Exists(jarPath + ".disabled"));
        Assert.True(item.IsEnabled);
    }

    [Fact]
    public async Task 导入_同名文件自动改名不覆盖()
    {
        using var paths = new CoreAssetsPaths();
        var gameDirectory = Path.Combine(paths.RootDirectory, "game-import");
        var sourceDirectory = Path.Combine(paths.RootDirectory, "src");
        Directory.CreateDirectory(sourceDirectory);

        var first = Path.Combine(sourceDirectory, "a.jar");
        var second = Path.Combine(sourceDirectory, "b.jar");
        await File.WriteAllTextAsync(first, "1");
        await File.WriteAllTextAsync(second, "2");

        var service = new ContentService(paths);
        var importedA = await service.ImportAsync(gameDirectory, ContentKind.Mod, [first]);
        var importedB = await service.ImportAsync(gameDirectory, ContentKind.Mod, [second]);

        Assert.Single(importedA);
        Assert.Single(importedB);
        // 两个不同源文件 → 目标目录里是 a.jar 与 b.jar，互不覆盖。
        Assert.NotEqual(importedA[0], importedB[0]);

        var third = Path.Combine(sourceDirectory, "a-copy.jar");
        await File.WriteAllTextAsync(third, "3");
        var importedC = await service.ImportAsync(gameDirectory, ContentKind.Mod, [third]);
        Assert.Equal("a-copy.jar", Path.GetFileName(importedC[0]));

        var duplicate = await service.ImportAsync(gameDirectory, ContentKind.Mod, [first]);
        Assert.Equal("a (1).jar", Path.GetFileName(duplicate[0]));
    }

    private static ContentItem Mod(string fileName, string id, string version, string? range = null) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), fileName),
        FileName = fileName,
        Kind = ContentKind.Mod,
        Id = id,
        DisplayName = id,
        Version = version,
        McVersionRange = range,
    };

    /// <summary>构造内存 zip；<c>internal</c> 便于 <c>CoreAssets_ModDependencyTests</c> 复用同一套写法。</summary>
    internal static void CreateJar(string path, params (string Name, string Content)[] textEntries)
    {
        CreateJar(path, textEntries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content))).ToArray());
    }

    internal static void CreateJar(string path, params (string Name, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            entryStream.Write(content, 0, content.Length);
        }
    }
}
