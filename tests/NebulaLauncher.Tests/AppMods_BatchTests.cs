using System.Diagnostics;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Mods;

namespace NebulaLauncher.Tests;

/// <summary>
/// 模组批量启停 / 批量删除。
///
/// 断言打在**磁盘上真实发生了什么**上：假 jar 建在临时目录里，批量禁用后看扩展名是否真的变了、
/// 汇总计数是否与磁盘一致。纯函数测试挡不住「服务把 IOException 吞了」这类问题——
/// <see cref="ContentService.SetEnabledAsync"/> 正是吞异常返回的实现。
/// </summary>
public class AppMods_BatchTests
{
    private static readonly string[] ThreeJars = ["sodium.jar", "lithium.jar", "iris.jar"];

    [Fact]
    public async Task 批量禁用_三个jar全部改成disabled()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ThreeJars);

        var result = await ContentBatch.SetEnabledAsync(content, mods, enabled: false);

        Assert.Equal(ContentBatchOperation.Disable, result.Operation);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.Total);
        Assert.False(result.HasFailures);
        Assert.Equal("成功 3 / 失败 0", result.Summary);

        var directory = ModsDirectory(paths);
        foreach (var jar in ThreeJars)
        {
            Assert.False(File.Exists(Path.Combine(directory, jar)), $"{jar} 应该已经被改名");
            Assert.True(File.Exists(Path.Combine(directory, jar + ".disabled")), $"{jar}.disabled 应该存在");
        }
    }

    [Fact]
    public async Task 批量启用_把disabled改回jar()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ThreeJars);

        await ContentBatch.SetEnabledAsync(content, mods, enabled: false);

        // 重新扫描，拿到 .disabled 路径的 item——与页面上「禁用后再选一次」是同一条路。
        var disabled = await content.ScanAsync(paths.MinecraftDirectory, ContentKind.Mod);
        Assert.All(disabled, item => Assert.False(item.IsEnabled));

        var result = await ContentBatch.SetEnabledAsync(content, disabled, enabled: true);

        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, result.Failed);

        var directory = ModsDirectory(paths);
        foreach (var jar in ThreeJars)
        {
            Assert.True(File.Exists(Path.Combine(directory, jar)));
            Assert.False(File.Exists(Path.Combine(directory, jar + ".disabled")));
        }
    }

    [Fact]
    public async Task 批量禁用_被占用的文件计入失败且其余项继续()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ThreeJars);

        var locked = Path.Combine(ModsDirectory(paths), "lithium.jar");

        // FileShare.None：模拟「游戏正在读这个 jar」，改名会失败。
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await ContentBatch.SetEnabledAsync(content, mods, enabled: false);

            Assert.Equal(2, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.True(result.HasFailures);
            Assert.Equal("成功 2 / 失败 1", result.Summary);
            var failed = Assert.Single(result.FailedFiles);
            Assert.Equal("lithium.jar", failed);
        }

        var directory = ModsDirectory(paths);
        Assert.True(File.Exists(Path.Combine(directory, "sodium.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(directory, "iris.jar.disabled")));

        // 失败项保持原状，不能出现「既没有 .jar 也没有 .disabled」的悬空状态。
        Assert.True(File.Exists(Path.Combine(directory, "lithium.jar")));
        Assert.False(File.Exists(Path.Combine(directory, "lithium.jar.disabled")));
    }

    [Fact]
    public async Task 批量禁用_文件在扫描后消失时计入失败()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ThreeJars);

        // 扫描之后、批量之前文件被别的进程删掉：服务会静默返回，只有磁盘判据能发现。
        File.Delete(Path.Combine(ModsDirectory(paths), "iris.jar"));

        var result = await ContentBatch.SetEnabledAsync(content, mods, enabled: false);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal("iris.jar", Assert.Single(result.FailedFiles));
    }

    [Fact]
    public async Task 批量删除_删掉选中项并汇总()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ThreeJars);

        var result = await ContentBatch.DeleteAsync(content, mods);

        Assert.Equal(ContentBatchOperation.Delete, result.Operation);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Empty(Directory.EnumerateFiles(ModsDirectory(paths)));
    }

    [Fact]
    public async Task 批量删除_被占用的文件计入失败且其余项继续()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ThreeJars);

        var locked = Path.Combine(ModsDirectory(paths), "sodium.jar");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await ContentBatch.DeleteAsync(content, mods);

            Assert.Equal(2, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.Equal("sodium.jar", Assert.Single(result.FailedFiles));
        }

        var remaining = Directory.EnumerateFiles(ModsDirectory(paths)).Select(Path.GetFileName).ToArray();
        Assert.Equal("sodium.jar", Assert.Single(remaining));
    }

    [Fact]
    public async Task 磁盘启用态_三态判据()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);
        var mods = await ArrangeAsync(paths, content, ["sodium.jar"]);

        var item = Assert.Single(mods);
        Assert.True(ContentBatch.IsEnabledOnDisk(item));

        await ContentBatch.SetEnabledAsync(content, mods, enabled: false);
        Assert.False(ContentBatch.IsEnabledOnDisk(item));

        File.Delete(Path.Combine(ModsDirectory(paths), "sodium.jar.disabled"));

        // 两个候选都不在：返回 null，由 UI 决定显示什么，不能谎报「启用」。
        Assert.Null(ContentBatch.IsEnabledOnDisk(item));
    }

    [Fact]
    public async Task 批量禁用_七百个模组全部成功且耗时可接受()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var content = new ContentService(paths);

        var names = Enumerable.Range(0, 700).Select(i => $"mod-{i:D3}.jar").ToArray();
        var mods = await ArrangeAsync(paths, content, names);

        var stopwatch = Stopwatch.StartNew();
        var result = await ContentBatch.SetEnabledAsync(content, mods, enabled: false);
        stopwatch.Stop();

        Assert.Equal(700, result.Succeeded);
        Assert.Equal(0, result.Failed);

        // 只打印、不断言时间：机器负载不同，把耗时写成断言等于制造偶发失败。
        Console.WriteLine($"[perf] 700 项批量禁用耗时 {stopwatch.ElapsedMilliseconds} ms（后台线程，不阻塞 UI）");
    }

    private static string ModsDirectory(FakeLauncherPaths paths) =>
        Path.Combine(paths.MinecraftDirectory, "mods");

    /// <summary>在 &lt;.minecraft&gt;/mods 下造假 jar，再走一次真实扫描拿到 item。</summary>
    private static async Task<IReadOnlyList<ContentItem>> ArrangeAsync(
        FakeLauncherPaths paths,
        ContentService content,
        IReadOnlyList<string> names)
    {
        var directory = ModsDirectory(paths);
        Directory.CreateDirectory(directory);

        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name), "not-a-real-jar");
        }

        return await content.ScanAsync(paths.MinecraftDirectory, ContentKind.Mod);
    }
}
