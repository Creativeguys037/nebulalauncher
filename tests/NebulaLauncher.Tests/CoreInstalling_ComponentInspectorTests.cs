using NebulaLauncher.Core.Installing;

namespace NebulaLauncher.Tests;

/// <summary>
/// 安装清单的组件检查。
///
/// 断言打在真实文件上：造版本目录 + 受管文件 + 清单，然后删 / 改 mtime 再检查。
/// 纯函数式的测试挡不住「路径基准算错」这类错误——清单里的路径是相对**游戏根目录**的，
/// 用版本目录当基准会一行都查不到，而返回的计数看起来完全正常。
/// </summary>
public class CoreInstalling_ComponentInspectorTests
{
    [Fact]
    public async Task 清单正常时全部健康()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await ArrangeVersionAsync(paths, "1.21.4", "1.21.4", [
            ("versions/1.21.4/1.21.4.jar", "minecraft"),
            ("versions/1.21.4/1.21.4.json", "minecraft"),
            ("libraries/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar", "libraries"),
        ]);

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("1.21.4");

        Assert.True(report.HasManifest);
        Assert.True(report.IsHealthy);
        Assert.False(report.HasFindings);
        Assert.Equal(0, report.MissingCount);
        Assert.Equal(0, report.ModifiedCount);
        Assert.Equal(3, report.ManagedFileCount);
        Assert.Empty(report.MissingFiles);
        Assert.Empty(report.ModifiedFiles);
        Assert.False(report.IsTruncated);

        // 两个组件：原版与库。库只出现在受管文件的 Component 上（Components 里没有），
        // 分组只按 Components 走就会把它整块漏掉。
        Assert.Equal(2, report.ComponentCount);
        var minecraft = report.Components.Single(c => c.Name == "minecraft");
        Assert.Equal("原版", minecraft.Kind);
        Assert.Equal(2, minecraft.FileCount);
        Assert.True(minecraft.IsHealthy);
        Assert.Equal("1.21.4", minecraft.Version);

        var libraries = report.Components.Single(c => c.Name == "libraries");
        Assert.Equal("库", libraries.Kind);
        Assert.Equal(1, libraries.FileCount);
        Assert.NotNull(libraries.LastModifiedUtc);

        Assert.Contains("全部完好", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 删一个受管文件_该组件缺失加一并列进缺失列表()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await ArrangeVersionAsync(paths, "1.21.4", "1.21.4", [
            ("versions/1.21.4/1.21.4.jar", "minecraft"),
            ("versions/1.21.4/1.21.4.json", "minecraft"),
            ("libraries/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar", "libraries"),
        ]);

        var library = Path.Combine(paths.MinecraftDirectory, "libraries", "com", "mojang", "brigadier", "1.0.18", "brigadier-1.0.18.jar");
        File.Delete(library);

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("1.21.4");

        Assert.True(report.HasManifest);
        Assert.False(report.IsHealthy);
        Assert.True(report.HasFindings);
        Assert.Equal(1, report.MissingCount);
        Assert.Equal(0, report.ModifiedCount);

        var listed = Assert.Single(report.MissingFiles);
        Assert.Equal("libraries/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar", listed);

        var libraries = report.Components.Single(c => c.Name == "libraries");
        Assert.Equal(1, libraries.MissingCount);
        Assert.Equal(1, libraries.FileCount);
        Assert.False(libraries.IsHealthy);

        // 原版组件不该被牵连。
        Assert.True(report.Components.Single(c => c.Name == "minecraft").IsHealthy);
        Assert.Contains("1 个文件缺失", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 改一个文件mtime_计入改动而不是缺失()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await ArrangeVersionAsync(paths, "1.21.4", "1.21.4", [
            ("versions/1.21.4/1.21.4.jar", "minecraft"),
        ]);

        var jar = Path.Combine(paths.MinecraftDirectory, "versions", "1.21.4", "1.21.4.jar");
        File.SetLastWriteTimeUtc(jar, DateTime.UtcNow.AddMinutes(-30));

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("1.21.4");

        Assert.Equal(0, report.MissingCount);
        Assert.Equal(1, report.ModifiedCount);
        Assert.Equal("versions/1.21.4/1.21.4.jar", Assert.Single(report.ModifiedFiles));

        var minecraft = report.Components.Single(c => c.Name == "minecraft");
        Assert.Equal(1, minecraft.ModifiedCount);
        Assert.Equal(0, minecraft.MissingCount);
        Assert.Contains("被改动", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task mtime容差内不算改动()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await ArrangeVersionAsync(paths, "1.21.4", "1.21.4", [
            ("versions/1.21.4/1.21.4.jar", "minecraft"),
        ]);

        var jar = Path.Combine(paths.MinecraftDirectory, "versions", "1.21.4", "1.21.4.jar");
        var recorded = File.GetLastWriteTimeUtc(jar);

        // 容差是 2 秒（跨文件系统的取整余量）：1 秒的偏差必须判成「没动过」。
        File.SetLastWriteTimeUtc(jar, recorded.AddSeconds(1));

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("1.21.4");

        Assert.True(report.IsHealthy);
        Assert.Equal(0, report.ModifiedCount);
    }

    [Fact]
    public async Task 没有清单时给明确文案而不是空报告()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var versionDirectory = Path.Combine(paths.VersionsDirectory, "1.20.1");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, "1.20.1.json"), "{}");

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("1.20.1");

        Assert.False(report.HasManifest);
        Assert.False(report.IsHealthy);
        Assert.False(report.HasFindings);
        Assert.Empty(report.Components);
        Assert.NotNull(report.Message);
        Assert.Contains("没有安装清单", report.Message!, StringComparison.Ordinal);

        // 汇总文案直接用原因，UI 不需要自己判断该显示哪句话。
        Assert.Equal(report.Message, report.Summary);
    }

    [Fact]
    public async Task 超过五十条时截断列表但计数完整()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var files = new List<(string Relative, string Component)>();
        for (var i = 0; i < 60; i++)
        {
            files.Add(($"libraries/pkg/{i}/lib-{i}.jar", "libraries"));
        }

        await ArrangeVersionAsync(paths, "1.12.2", "1.12.2", [.. files]);

        // 全部删掉：60 个缺失，列表只列前 50。
        foreach (var (relative, _) in files)
        {
            File.Delete(Path.Combine(paths.MinecraftDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("1.12.2");

        Assert.Equal(60, report.MissingCount);
        Assert.Equal(50, report.MissingFiles.Count);
        Assert.True(report.IsTruncated);

        var libraries = report.Components.Single(c => c.Name == "libraries");
        Assert.Equal("库", libraries.Kind);
        Assert.Equal(60, libraries.MissingCount);
        Assert.Equal(60, libraries.FileCount);
    }

    [Fact]
    public async Task 版本目录不存在时给明确文案()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var inspector = new ComponentInspector(paths, NullAppLog.Instance);

        var report = await inspector.InspectAsync("不存在-1.0");

        Assert.False(report.HasManifest);
        Assert.Contains("找不到版本目录", report.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空清单也算有清单且健康()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var versionDirectory = Path.Combine(paths.VersionsDirectory, "empty");
        Directory.CreateDirectory(versionDirectory);

        var manifest = new McInstallManifest();
        manifest.Components.Add(new McInstallComponent { Name = "minecraft", Version = "1.0" });
        await manifest.SaveAsync(versionDirectory);

        var inspector = new ComponentInspector(paths, NullAppLog.Instance);
        var report = await inspector.InspectAsync("empty");

        // 有清单但一个受管文件都没记录：没有可检查的对象，按健康处理，
        // 但组件行仍然要在（用户要能看到「装了什么」）。
        Assert.True(report.HasManifest);
        Assert.True(report.IsHealthy);
        Assert.Equal(1, report.ComponentCount);
        Assert.Equal(0, report.Components[0].FileCount);
        Assert.Equal(0, report.ManagedFileCount);
    }

    [Theory]
    [InlineData("minecraft", "原版")]
    [InlineData("fabric", "加载器")]
    [InlineData("neoforge", "加载器")]
    [InlineData("libraries", "库")]
    [InlineData("mod", "模组")]
    [InlineData("something-else", "其他")]
    public void 组件种类归类(string name, string expected)
    {
        Assert.Equal(expected, ComponentInspector.KindOf(name));
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 造一个版本目录 + 受管文件 + 清单。<paramref name="files"/> 用相对游戏根目录的路径，
    /// 与 <see cref="McInstallManifestFile.RelativePath"/> 的约定一致。
    /// </summary>
    private static async Task ArrangeVersionAsync(
        FakeLauncherPaths paths,
        string versionName,
        string minecraftVersion,
        IReadOnlyList<(string Relative, string Component)> files)
    {
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionName);
        Directory.CreateDirectory(versionDirectory);

        var manifest = new McInstallManifest
        {
            SchemaVersion = McInstallManifest.CurrentSchemaVersion,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        manifest.Components.Add(new McInstallComponent
        {
            Name = "minecraft",
            Version = minecraftVersion,
            InstalledUtc = DateTimeOffset.UtcNow,
        });

        foreach (var (relative, component) in files)
        {
            var absolute = Path.Combine(
                paths.MinecraftDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar));

            var directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(absolute, "content:" + relative);

            manifest.ManagedFiles.Add(new McInstallManifestFile
            {
                RelativePath = relative,
                Sha1 = null,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(absolute),
                Component = component,
            });
        }

        await manifest.SaveAsync(versionDirectory);
    }
}
