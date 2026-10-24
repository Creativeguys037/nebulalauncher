using System.Net;
using System.Net.Http;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 模组页四项改动的纯逻辑覆盖：
/// 内容回收站、<c>.old</c> / <c>.litemod</c> 后缀、一键更新的文件名段对齐。
///
/// 只测「读磁盘 → 得结论」这段，不测界面：颜色、布局靠截图验收。
/// </summary>
public class CoreMods_TrashAndUpdateTests
{
    // ---------------- 第 4 条：删除进回收站 ----------------

    [Fact]
    public async Task 删除默认进回收站并可恢复到原位置()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var file = Path.Combine(modsDirectory, "example.jar");
        await File.WriteAllTextAsync(file, "payload");

        await service.DeleteAsync(Item(file));

        Assert.False(File.Exists(file));

        var entries = await service.ListTrashAsync();
        var entry = Assert.Single(entries);
        Assert.Equal("example.jar", entry.FileName);
        Assert.Equal(file, entry.OriginalPath);
        Assert.False(entry.IsDirectory);

        Assert.True(await service.RestoreAsync(entry.Id));
        Assert.True(File.Exists(file));
        Assert.Empty(await service.ListTrashAsync());
    }

    [Fact]
    public async Task 彻底删除不进回收站()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var file = Path.Combine(modsDirectory, "gone.jar");
        await File.WriteAllTextAsync(file, "payload");

        await service.DeleteAsync(Item(file), permanent: true);

        Assert.False(File.Exists(file));
        Assert.Empty(await service.ListTrashAsync());
    }

    [Fact]
    public async Task 删除时同时带走禁用变体()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var enabled = Path.Combine(modsDirectory, "pair.jar");
        var disabled = enabled + ".disabled";
        await File.WriteAllTextAsync(enabled, "a");
        await File.WriteAllTextAsync(disabled, "b");

        await service.DeleteAsync(Item(disabled));

        // 只删当前那份会让列表里只剩「一半的模组」。
        Assert.False(File.Exists(enabled));
        Assert.False(File.Exists(disabled));
        Assert.Equal(2, (await service.ListTrashAsync()).Count);
    }

    [Fact]
    public async Task 回收站条目可彻底清除()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var file = Path.Combine(modsDirectory, "temp.jar");
        await File.WriteAllTextAsync(file, "payload");
        await service.DeleteAsync(Item(file));

        var entry = Assert.Single(await service.ListTrashAsync());
        await service.PurgeTrashAsync(entry.Id);

        Assert.Empty(await service.ListTrashAsync());
        Assert.False(Directory.Exists(Path.Combine(paths.RootDirectory, "trash", "contents", entry.Id)));
    }

    [Fact]
    public async Task 非法回收站Id不会拼进路径()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);

        Assert.False(await service.RestoreAsync("..\\..\\windows"));
        await service.PurgeTrashAsync("..\\..\\windows");

        // 走到这里说明没有把非法 Id 拼进删除路径——否则上面会尝试删上级目录。
        Assert.True(Directory.Exists(paths.RootDirectory));
    }

    // ---------------- 第 10 条：.old / .litemod ----------------

    [Fact]
    public async Task 扫描认old后缀并视为禁用()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        File.WriteAllText(Path.Combine(modsDirectory, "legacy.jar.old"), "x");

        var items = await service.ScanAsync(paths.MinecraftDirectory, ContentKind.Mod);

        var item = Assert.Single(items);
        Assert.False(item.IsEnabled);
        Assert.Equal("legacy.jar.old", item.FileName);
    }

    [Fact]
    public async Task 两种禁用后缀并存时只列一条且优先disabled()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        File.WriteAllText(Path.Combine(modsDirectory, "dup.jar.disabled"), "x");
        File.WriteAllText(Path.Combine(modsDirectory, "dup.jar.old"), "x");

        var items = await service.ScanAsync(paths.MinecraftDirectory, ContentKind.Mod);

        // 两条会让用户「删掉一条，另一条还在」，看起来像删不掉。
        var item = Assert.Single(items);
        Assert.EndsWith(".disabled", item.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 禁用old后缀的模组时沿用old不新生成disabled()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var enabled = Path.Combine(modsDirectory, "keep.jar");
        var old = enabled + ".old";
        await File.WriteAllTextAsync(enabled, "x");

        // 先造出「手写的 .old 已存在」这个前提：禁用另一个同名文件时它必须被沿用。
        await File.WriteAllTextAsync(old, "x");
        File.Delete(enabled);

        await service.SetEnabledAsync(Item(enabled), enabled: false);

        Assert.True(File.Exists(old));
        Assert.False(File.Exists(enabled + ".disabled"));
    }

    [Fact]
    public async Task litemod被识别为模组文件()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        File.WriteAllText(Path.Combine(modsDirectory, "legacy.litemod"), "x");
        File.WriteAllText(Path.Combine(modsDirectory, "disabled.litemod.old"), "x");

        var items = await service.ScanAsync(paths.MinecraftDirectory, ContentKind.Mod);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.FileName == "legacy.litemod" && item.IsEnabled);
        Assert.Contains(items, item => item.FileName == "disabled.litemod.old" && !item.IsEnabled);
    }

    [Fact]
    public void 批量启用态判定认old后缀()
    {
        using var paths = new CoreAssetsPaths();
        var directory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(directory);

        var enabled = Path.Combine(directory, "batch.jar");
        File.WriteAllText(enabled, "x");

        Assert.True(ContentBatch.IsEnabledOnDisk(Item(enabled)));

        File.Move(enabled, enabled + ".old");

        Assert.False(ContentBatch.IsEnabledOnDisk(Item(enabled)));
        Assert.False(ContentBatch.IsEnabledOnDisk(Item(enabled + ".old")));
        Assert.Equal(enabled, ContentBatch.EnabledPathOf(enabled + ".old"));
    }

    [Fact]
    public async Task 批量禁用判据接受old后缀落盘()
    {
        using var paths = new CoreAssetsPaths();
        var service = new ContentService(paths);
        var directory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(directory);

        var enabled = Path.Combine(directory, "a.jar");
        await File.WriteAllTextAsync(enabled, "x");

        // 文件被改成 .old（PCL2 风格）：批量禁用的成功判据不能只认 .disabled。
        File.Move(enabled, enabled + ".old");
        var result = await ContentBatch.SetEnabledAsync(service, [Item(enabled)], enabled: false);

        Assert.Equal(1, result.Succeeded);
    }

    // ---------------- 第 6 条：文件名段对齐 ----------------

    [Theory]
    [InlineData("jei-1.20.1-15.2.0.27.jar", "jei-1.20.1-15.2.0.30.jar", "jei-1.20.1-15.2.0.30.jar")]
    [InlineData("sodium-fabric-0.5.13.jar", "sodium-fabric-0.6.0.jar", "sodium-fabric-0.6.0.jar")]
    [InlineData("mod-1.0-forge.jar", "mod-2.0-forge.jar", "mod-2.0-forge.jar")]
    [InlineData("mod-1.2-forge-3.jar", "mod-1.3-forge-4.jar", "mod-1.3-forge-4.jar")]
    [InlineData("plain.jar", "renamed.jar", "renamed.jar")]
    public void 文件名按段对齐(string current, string remote, string expected)
    {
        Assert.Equal(expected, ModUpdateService.AlignFileName(current, remote));
    }

    [Theory]
    [InlineData("a.jar", "a.jar")]
    [InlineData("a-1.0.jar", "a-1.0.jar")]
    public void 段完全相同时文件名不变(string current, string remote)
    {
        Assert.Equal(current, ModUpdateService.AlignFileName(current, remote));
    }

    // ---------------- 第 6 条：一键更新 ----------------

    [Fact]
    public async Task 更新下载替换且旧文件进回收站()
    {
        using var paths = new CoreAssetsPaths();
        var content = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var oldFile = Path.Combine(modsDirectory, "example-1.20.1-1.0.0.jar");
        await File.WriteAllTextAsync(oldFile, "old");

        var payload = "new-bytes"u8.ToArray();
        using var handler = new StubHttpHandler(payload);
        using var service = new ModUpdateService(paths: paths, content: content, handler: handler);

        var item = new ContentItem
        {
            FilePath = oldFile,
            FileName = "example-1.20.1-1.0.0.jar",
            Kind = ContentKind.Mod,
            IsEnabled = true,
            Version = "1.0.0",
        };

        var info = new ModUpdateInfo
        {
            Item = item,
            Matched = true,
            HasUpdate = true,
            Source = ContentSourceKind.CurseForge,
            ProjectId = "1",
            CurrentVersion = "1.0.0",
            LatestVersion = "2.0.0",
            DownloadUrl = "https://example.invalid/example-1.20.1-2.0.0.jar",
            FileName = "example-1.20.1-2.0.0.jar",
            FileSize = payload.Length,
        };

        var result = await service.UpdateAsync([info], paths.MinecraftDirectory);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Failed);

        // 旧名与远程名录在同一目录，段对齐后新文件名落在 expected。
        var expected = Path.Combine(modsDirectory, "example-1.20.1-2.0.0.jar");
        Assert.True(File.Exists(expected));
        Assert.Equal(payload, await File.ReadAllBytesAsync(expected));

        // 旧文件不能凭空消失：进回收站才能「更新完崩了换回旧版」。
        Assert.False(File.Exists(oldFile));
        Assert.Contains(await content.ListTrashAsync(), entry => entry.FileName == "example-1.20.1-1.0.0.jar");
    }

    [Fact]
    public async Task 更新大小不符时不动旧文件()
    {
        using var paths = new CoreAssetsPaths();
        var content = new ContentService(paths);
        var modsDirectory = Path.Combine(paths.MinecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var oldFile = Path.Combine(modsDirectory, "size-1.0.0.jar");
        await File.WriteAllTextAsync(oldFile, "old");

        using var handler = new StubHttpHandler("new"u8.ToArray());
        using var service = new ModUpdateService(paths: paths, content: content, handler: handler);

        var info = new ModUpdateInfo
        {
            Item = new ContentItem
            {
                FilePath = oldFile,
                FileName = "size-1.0.0.jar",
                Kind = ContentKind.Mod,
                IsEnabled = true,
            },
            Matched = true,
            HasUpdate = true,
            Source = ContentSourceKind.CurseForge,
            DownloadUrl = "https://example.invalid/size-2.0.0.jar",
            FileName = "size-2.0.0.jar",
            FileSize = 999,
        };

        var result = await service.UpdateAsync([info], paths.MinecraftDirectory);

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public async Task 没有新版本的条目被跳过而不是失败()
    {
        using var paths = new CoreAssetsPaths();
        var content = new ContentService(paths);
        using var service = new ModUpdateService(paths: paths, content: content, handler: new StubHttpHandler([]));

        var info = new ModUpdateInfo
        {
            Item = new ContentItem
            {
                FilePath = Path.Combine(paths.MinecraftDirectory, "mods", "same.jar"),
                FileName = "same.jar",
                Kind = ContentKind.Mod,
            },
            Matched = true,
            HasUpdate = false,
        };

        var result = await service.UpdateAsync([info], paths.MinecraftDirectory);

        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Skipped);
    }

    // ---------------- 辅助 ----------------

    private static ContentItem Item(string path) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        Kind = ContentKind.Mod,
        IsEnabled = !ContentService.HasDisabledSuffix(path),
    };

    /// <summary>只回一段固定字节的 HTTP 管道替身。</summary>
    private sealed class StubHttpHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };

            return Task.FromResult(response);
        }
    }
}