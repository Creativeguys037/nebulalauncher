using NebulaLauncher.Core.Installing;

namespace NebulaLauncher.Tests;

/// <summary>
/// 安装清单（<c>versions/&lt;id&gt;/PCL/install-manifest.json</c>）。
///
/// 清单的核心用途是「廉价判断组件是否还在」：不重算 SHA1，只看文件在不在、mtime 有没有变。
/// 这里验证的正是这条判据，而不是 JSON 的字段名本身（字段名对齐 PCL2 是另一回事）。
/// </summary>
public class CoreLaunch_InstallManifestTests
{
    [Fact]
    public async Task 清单写到版本目录的PCL子目录并能读回()
    {
        using var temp = new TempDirectory();
        var versionDirectory = Path.Combine(temp.Path, "versions", "1.20.1");
        Directory.CreateDirectory(versionDirectory);

        var manifest = new McInstallManifest();
        manifest.Components.Add(new McInstallComponent { Name = "minecraft", Version = "1.20.1" });
        manifest.ManagedFiles.Add(new McInstallManifestFile
        {
            RelativePath = "versions/1.20.1/1.20.1.jar",
            Sha1 = "ABC123",
            LastWriteTimeUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Component = "minecraft",
        });

        await manifest.SaveAsync(versionDirectory);

        var expectedPath = Path.Combine(versionDirectory, "PCL", "install-manifest.json");
        Assert.True(File.Exists(expectedPath), "清单没有落在 PCL/install-manifest.json");
        Assert.Equal(expectedPath, McInstallManifest.GetPath(versionDirectory));

        var loaded = await McInstallManifest.LoadAsync(versionDirectory);

        Assert.NotNull(loaded);
        Assert.Equal(McInstallManifest.CurrentSchemaVersion, loaded!.SchemaVersion);
        Assert.Single(loaded.Components);
        Assert.Equal("minecraft", loaded.Components[0].Name);
        Assert.Single(loaded.ManagedFiles);
        Assert.Equal("versions/1.20.1/1.20.1.jar", loaded.ManagedFiles[0].RelativePath);
        Assert.Equal("ABC123", loaded.ManagedFiles[0].Sha1);
        Assert.Equal("minecraft", loaded.ManagedFiles[0].Component);
    }

    [Fact]
    public void mtime变化后模组组件失效()
    {
        using var temp = new TempDirectory();
        var modPath = Path.Combine(temp.Path, "versions", "1.20.1", "mods", "example.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        File.WriteAllText(modPath, "mod-payload");

        var manifest = new McInstallManifest();
        manifest.Components.Add(new McInstallComponent { Name = "mod", Version = "1.0" });
        manifest.Components.Add(new McInstallComponent { Name = "libraries" });
        manifest.ManagedFiles.Add(new McInstallManifestFile
        {
            RelativePath = "versions/1.20.1/mods/example.jar",
            Sha1 = "AAA",
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(modPath),
            Component = "mod",
        });
        manifest.ManagedFiles.Add(new McInstallManifestFile
        {
            RelativePath = "versions/1.20.1/1.20.1.jar",
            Sha1 = "BBB",
            LastWriteTimeUtc = DateTimeOffset.UtcNow.AddDays(-1),
            Component = "libraries",
        });

        // mtime 没变时组件应当保留。
        Assert.Equal(0, manifest.RefreshModComponents(temp.Path));
        Assert.Contains(manifest.Components, component => component.Name == "mod");

        // 模拟文件被外部替换：mtime 推进 10 分钟。
        File.SetLastWriteTimeUtc(modPath, File.GetLastWriteTimeUtc(modPath).AddMinutes(10));

        var removed = manifest.RefreshModComponents(temp.Path);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(manifest.Components, component => component.Name == "mod");
        Assert.DoesNotContain(manifest.ManagedFiles, file => file.Component == "mod");

        // 非模组条目不受影响：库文件本来就由修复流程按 sha1 管。
        Assert.Contains(manifest.Components, component => component.Name == "libraries");
        Assert.Single(manifest.ManagedFiles);
    }

    [Fact]
    public void 模组文件被删后组件同样失效()
    {
        using var temp = new TempDirectory();

        var manifest = new McInstallManifest();
        manifest.Components.Add(new McInstallComponent { Name = "mod" });
        manifest.ManagedFiles.Add(new McInstallManifestFile
        {
            RelativePath = "versions/1.20.1/mods/gone.jar",
            Sha1 = "AAA",
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            Component = "mod",
        });

        Assert.Equal(1, manifest.RefreshModComponents(temp.Path));
        Assert.Empty(manifest.Components);
        Assert.Empty(manifest.ManagedFiles);
    }

    [Fact]
    public async Task 等价比较忽略最后写入时间()
    {
        using var temp = new TempDirectory();
        var versionDirectory = Path.Combine(temp.Path, "versions", "1.20.1");
        Directory.CreateDirectory(versionDirectory);

        var manifest = new McInstallManifest();
        manifest.Components.Add(new McInstallComponent { Name = "minecraft", Version = "1.20.1" });
        manifest.ManagedFiles.Add(new McInstallManifestFile
        {
            RelativePath = "versions/1.20.1/1.20.1.jar",
            Sha1 = "ABC",
            LastWriteTimeUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Component = "minecraft",
        });

        await manifest.SaveAsync(versionDirectory);

        var loaded = await McInstallManifest.LoadAsync(versionDirectory);
        Assert.NotNull(loaded);
        Assert.True(manifest.IsEquivalentTo(loaded));

        loaded!.ManagedFiles[0].Sha1 = "CHANGED";
        Assert.False(manifest.IsEquivalentTo(loaded));
    }

    [Fact]
    public async Task 损坏的清单返回空而不抛异常()
    {
        using var temp = new TempDirectory();
        var versionDirectory = Path.Combine(temp.Path, "versions", "1.20.1");
        var pclDirectory = Path.Combine(versionDirectory, "PCL");
        Directory.CreateDirectory(pclDirectory);
        await File.WriteAllTextAsync(Path.Combine(pclDirectory, "install-manifest.json"), "{ not json");

        var loaded = await McInstallManifest.LoadAsync(versionDirectory);

        Assert.Null(loaded);
    }
}
