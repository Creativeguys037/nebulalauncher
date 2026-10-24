using System.Globalization;
using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 数据包的扫描与启停。
///
/// 与另外三类内容的关键差别都在这里钉住：两种落点（全局 / 存档级）、两种形态（zip / 目录）、
/// 以及「目录形态靠改目录名而不是改扩展名」这条启停语义。
/// </summary>
public class CoreAssets_DataPackTests
{
    [Fact]
    public void 数据包的枚举值是追加的四()
    {
        // 已有数值被缓存与磁盘数据按数值比较，只能追加。
        Assert.Equal(4, (int)ContentKind.DataPack);
        Assert.Equal(0, (int)ContentKind.Mod);
        Assert.Equal(1, (int)ContentKind.ResourcePack);
        Assert.Equal(2, (int)ContentKind.ShaderPack);
        Assert.Equal(3, (int)ContentKind.World);
        Assert.Equal("datapacks", ContentService.DirectoryNameFor(ContentKind.DataPack));
    }

    // ==================== 扫描 ====================

    [Fact]
    public async Task 扫描全局datapacks下的zip并解析pack_mcmeta()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var datapacks = Path.Combine(game, "datapacks");
        WriteZip(Path.Combine(datapacks, "terralith.zip"), packFormat: 48, description: "地形扩展");
        WriteZip(Path.Combine(datapacks, "quiet.zip"), packFormat: null, description: null);

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(ContentKind.DataPack, item.Kind));
        Assert.All(items, item => Assert.Null(item.SourceHint));
        Assert.All(items, item => Assert.True(item.IsEnabled));

        var terralith = items.Single(item => item.DisplayName == "terralith");
        Assert.Equal("terralith.zip", terralith.FileName);
        Assert.Equal("地形扩展", terralith.Description);
        Assert.Equal("format 48", terralith.Version);
        Assert.True(terralith.FileSize > 0);

        // pack.mcmeta 缺 pack_format 时不编造版本号。
        var quiet = items.Single(item => item.DisplayName == "quiet");
        Assert.Equal(string.Empty, quiet.Version);
        Assert.Null(quiet.Description);
    }

    [Fact]
    public async Task 扫描存档级datapacks并把世界名写进SourceHint()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        WriteZip(Path.Combine(game, "saves", "远古遗迹", "datapacks", "shrine.zip"), 48, null);
        WriteZip(Path.Combine(game, "saves", "创造实验场", "datapacks", "maze.zip"), 48, null);

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.DisplayName == "shrine" && item.SourceHint == "远古遗迹");
        Assert.Contains(items, item => item.DisplayName == "maze" && item.SourceHint == "创造实验场");
        Assert.All(items, item => Assert.DoesNotContain("saves", item.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 全局与存档级同时存在时都列出()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        WriteZip(Path.Combine(game, "datapacks", "global-pack.zip"), 48, null);
        WriteZip(Path.Combine(game, "saves", "世界A", "datapacks", "local-pack.zip"), 48, null);

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.DisplayName == "global-pack" && item.SourceHint is null);
        Assert.Contains(items, item => item.DisplayName == "local-pack" && item.SourceHint == "世界A");

        // 全局条目排在存档级前面，用户先看到「对所有世界生效」的那些。
        Assert.Null(items[0].SourceHint);
    }

    [Fact]
    public async Task 识别目录形态的数据包()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var pack = Path.Combine(game, "datapacks", "my-datapack");
        WritePackMeta(Path.Combine(pack, "pack.mcmeta"), 26, "目录形态");
        Directory.CreateDirectory(Path.Combine(pack, "data", "mypack", "functions"));
        File.WriteAllText(Path.Combine(pack, "data", "mypack", "functions", "tick.mcfunction"), "say hi\n");

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        var item = Assert.Single(items);
        Assert.Equal("my-datapack", item.DisplayName);
        Assert.Equal("my-datapack", item.FileName);
        Assert.Equal("my-datapack", item.Id);
        Assert.True(item.IsEnabled);
        Assert.Equal("目录形态", item.Description);
        Assert.Equal("format 26", item.Version);

        // 目录形态的大小是递归求和，不是 0。
        Assert.True(item.FileSize > 0);
    }

    [Fact]
    public async Task 非zip文件与其它子目录规则()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var datapacks = Path.Combine(game, "datapacks");
        Directory.CreateDirectory(datapacks);
        File.WriteAllText(Path.Combine(datapacks, "readme.txt"), "说明文件，不是数据包");
        File.WriteAllText(Path.Combine(datapacks, "notes.md"), "# 也不是");
        WriteZip(Path.Combine(datapacks, "real.zip"), 48, null);

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        Assert.Single(items);
        Assert.Equal("real", items[0].DisplayName);
    }

    [Fact]
    public async Task 目录缺失或为空都不抛异常()
    {
        using var temp = new TempDirectory();
        var service = Service(temp);
        var game = temp.Combine("game");

        // 游戏目录不存在
        Assert.Empty(await service.ScanAsync(game, ContentKind.DataPack));

        // 游戏目录存在但没有 datapacks/ 与 saves/
        Directory.CreateDirectory(game);
        Assert.Empty(await service.ScanAsync(game, ContentKind.DataPack));

        // datapacks/ 是空目录
        Directory.CreateDirectory(Path.Combine(game, "datapacks"));
        Assert.Empty(await service.ScanAsync(game, ContentKind.DataPack));

        // 空路径
        Assert.Empty(await service.ScanAsync(string.Empty, ContentKind.DataPack));
        Assert.Empty(await service.ScanAsync("   ", ContentKind.DataPack));
    }

    [Fact]
    public async Task 排序_启用的排在前面()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var datapacks = Path.Combine(game, "datapacks");
        Directory.CreateDirectory(datapacks);
        WriteZip(Path.Combine(datapacks, "b-disabled.zip.disabled"), 48, null);
        WriteZip(Path.Combine(datapacks, "a-enabled.zip"), 48, null);

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsEnabled);
        Assert.Equal("a-enabled", items[0].DisplayName);
        Assert.False(items[1].IsEnabled);
    }

    // ==================== 禁用态命名 ====================

    [Fact]
    public async Task 禁用态的文件与目录都保留原名并标为未启用()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var datapacks = Path.Combine(game, "datapacks");
        WriteZip(Path.Combine(datapacks, "paused.zip.disabled"), 48, null);
        WritePackMeta(Path.Combine(datapacks, "paused-dir.disabled", "pack.mcmeta"), 48, null);

        var items = await Service(temp).ScanAsync(game, ContentKind.DataPack);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.False(item.IsEnabled));

        var fileItem = items.Single(item => item.FileName == "paused.zip.disabled");
        Assert.Equal("paused", fileItem.DisplayName);
        Assert.Equal("paused", fileItem.Id);

        var directoryItem = items.Single(item => item.FileName == "paused-dir.disabled");
        Assert.Equal("paused-dir", directoryItem.DisplayName);
        Assert.Equal("paused-dir", directoryItem.Id);
    }

    // ==================== 启停 ====================

    [Fact]
    public async Task 禁用再启用zip形态_只改扩展名不删文件()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var datapacks = Path.Combine(game, "datapacks");
        var path = Path.Combine(datapacks, "pack.zip");
        WriteZip(path, 48, "内容");
        var service = Service(temp);

        var item = Assert.Single(await service.ScanAsync(game, ContentKind.DataPack));

        await service.SetEnabledAsync(item, enabled: false);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".disabled"));
        Assert.False(item.IsEnabled);
        AssertOnDisk(item, false);

        // 内容没有被动过
        Assert.Contains("内容", ReadPackMetaJson(path + ".disabled"), StringComparison.Ordinal);

        await service.SetEnabledAsync(item, enabled: true);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".disabled"));
        Assert.True(item.IsEnabled);
        AssertOnDisk(item, true);
    }

    [Fact]
    public async Task 禁用再启用目录形态_改的是目录名()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var directory = Path.Combine(game, "datapacks", "dir-pack");
        WritePackMeta(Path.Combine(directory, "pack.mcmeta"), 48, "目录");
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        var service = Service(temp);

        var item = Assert.Single(await service.ScanAsync(game, ContentKind.DataPack));

        await service.SetEnabledAsync(item, enabled: false);
        Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists(directory + ".disabled"));
        Assert.False(item.IsEnabled);
        AssertOnDisk(item, false);

        // 目录里的文件跟着走，没有被删
        Assert.True(File.Exists(Path.Combine(directory + ".disabled", "pack.mcmeta")));

        await service.SetEnabledAsync(item, enabled: true);
        Assert.True(Directory.Exists(directory));
        Assert.False(Directory.Exists(directory + ".disabled"));
        Assert.True(item.IsEnabled);
        AssertOnDisk(item, true);
    }

    [Fact]
    public async Task 重复设置同一状态是幂等的()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var directory = Path.Combine(game, "datapacks", "dir-pack");
        WritePackMeta(Path.Combine(directory, "pack.mcmeta"), 48, null);
        var service = Service(temp);

        var item = Assert.Single(await service.ScanAsync(game, ContentKind.DataPack));

        await service.SetEnabledAsync(item, enabled: true);   // 已经是启用态
        await service.SetEnabledAsync(item, enabled: true);
        Assert.True(Directory.Exists(directory));
        AssertOnDisk(item, true);

        await service.SetEnabledAsync(item, enabled: false);
        await service.SetEnabledAsync(item, enabled: false);
        Assert.True(Directory.Exists(directory + ".disabled"));
        AssertOnDisk(item, false);
    }

    [Fact]
    public async Task 目标已经被删掉时启停不抛异常()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        WriteZip(Path.Combine(game, "datapacks", "gone.zip"), 48, null);
        var service = Service(temp);

        var item = Assert.Single(await service.ScanAsync(game, ContentKind.DataPack));
        File.Delete(item.FilePath);

        var exception = await Record.ExceptionAsync(() => service.SetEnabledAsync(item, enabled: false));

        Assert.Null(exception);
        Assert.Null(ContentService.IsEnabledOnDisk(item));
    }

    [Fact]
    public async Task 存档级条目的启停只影响该世界()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("game");
        var worldLocal = Path.Combine(game, "saves", "世界A", "datapacks", "only-a.zip");
        var worldOther = Path.Combine(game, "saves", "世界B", "datapacks", "only-b.zip");
        WriteZip(worldLocal, 48, null);
        WriteZip(worldOther, 48, null);
        var service = Service(temp);

        var items = await service.ScanAsync(game, ContentKind.DataPack);
        var itemA = items.Single(item => item.SourceHint == "世界A");

        await service.SetEnabledAsync(itemA, enabled: false);

        Assert.True(File.Exists(worldLocal + ".disabled"));
        Assert.True(File.Exists(worldOther));
    }

    // ==================== 辅助 ====================

    private static ContentService Service(TempDirectory temp) => new(new FakeLauncherPaths(temp.Path));

    private static void AssertOnDisk(ContentItem item, bool expected) =>
        Assert.Equal<bool?>(expected, ContentService.IsEnabledOnDisk(item));

    private static void WriteZip(string path, int? packFormat, string? description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("pack.mcmeta");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(BuildPackMeta(packFormat, description));
    }

    private static void WritePackMeta(string path, int? packFormat, string? description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildPackMeta(packFormat, description), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string BuildPackMeta(int? packFormat, string? description)
    {
        var formatJson = packFormat?.ToString(CultureInfo.InvariantCulture) ?? "null";
        var descriptionJson = description is null ? "null" : "\"" + description + "\"";

        // 手工拼接而不是内插原始字符串：JSON 的连续右大括号会和 `$$"""` 的插值定界符冲突。
        return "{\"pack\":{\"pack_format\":" + formatJson + ",\"description\":" + descriptionJson + "}}";
    }

    private static string ReadPackMetaJson(string zipPath)
    {
        using var file = File.OpenRead(zipPath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.GetEntry("pack.mcmeta");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
