using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例快捷目录（<c>Core/Instances/InstanceFolders.cs</c>）。
///
/// 断言只打在「错了会让玩家点进错误目录」的几处：
/// 1. 固定五项、顺序稳定——顺序变了界面会跟着抖；
/// 2. 只给相对名，绝对路径现拼——同一个实例可能在实例目录与全局根下解析；
/// 3. 目录不存在也要返回（芯片不能随「还没截过图」忽隐忽现）；
/// 4. 越界拼接必须被拒（<c>..</c> 之类不能把玩家带出游戏目录）。
/// </summary>
public class CoreInstances_FolderTests
{
    private static readonly string[] ExpectedKeys = { "saves", "mods", "resourcepacks", "screenshots", "logs" };

    [Fact]
    public void 目录表_五项齐全且顺序稳定()
    {
        var keys = new List<string>();
        foreach (var folder in InstanceFolders.All)
        {
            keys.Add(folder.Key);
        }

        Assert.Equal(ExpectedKeys.Length, InstanceFolders.All.Count);
        Assert.Equal(string.Join(",", ExpectedKeys), string.Join(",", keys));
    }

    [Fact]
    public void 目录表_相对路径与显示名一一对应()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["saves"] = "存档",
            ["mods"] = "模组",
            ["resourcepacks"] = "资源包",
            ["screenshots"] = "截图",
            ["logs"] = "日志",
        };

        foreach (var folder in InstanceFolders.All)
        {
            Assert.True(expected.ContainsKey(folder.Key), $"出现了未登记的 Key：{folder.Key}");
            Assert.Equal(expected[folder.Key], folder.DisplayName);

            // 相对名就是 Key：两者不一致时界面会去拼一个不存在的目录。
            Assert.Equal(folder.Key, folder.RelativePath);
        }
    }

    [Fact]
    public void 目录表_五项都允许按需创建()
    {
        // screenshots/ 在第一次按 F2 之前并不存在，其余四项也可能被玩家手动删掉：
        // 打开前一律允许 CreateDirectory，否则芯片会有「点了没反应」的分支。
        foreach (var folder in InstanceFolders.All)
        {
            Assert.True(folder.CreateOnOpen, $"{folder.Key} 必须允许按需创建");
        }
    }

    [Fact]
    public void 未知Key_返回null而不是抛异常()
    {
        Assert.Null(InstanceFolders.Find("overworld"));
        Assert.Null(InstanceFolders.Find(".."));
        Assert.Null(InstanceFolders.Find(string.Empty));
        Assert.Null(InstanceFolders.Find(null));

        // 路径解析同样要挡住未知 Key，不能退回「拼一个空串」。
        Assert.Null(InstanceFolders.ResolvePath(Path.GetTempPath(), "overworld"));
    }

    [Fact]
    public void 路径拼接_越界输入被拒且合法项落在游戏目录下()
    {
        using var temp = new TempDirectory();
        var gameDirectory = temp.Combine("instance");
        Directory.CreateDirectory(gameDirectory);

        // Key 只可能是五项之一，所以 `..` 之类既不是合法 Key，也不可能拼出根外路径。
        Assert.Null(InstanceFolders.ResolvePath(gameDirectory, "../outside"));
        Assert.Null(InstanceFolders.ResolvePath(gameDirectory, "saves/../../outside"));
        Assert.Null(InstanceFolders.ResolvePath(gameDirectory, "C:\\Windows"));

        var root = Path.TrimEndingDirectorySeparator(gameDirectory) + Path.DirectorySeparatorChar;

        foreach (var folder in InstanceFolders.All)
        {
            var path = InstanceFolders.ResolvePath(gameDirectory, folder.Key);

            Assert.NotNull(path);
            Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(gameDirectory, folder.RelativePath), path);
        }
    }

    [Fact]
    public void 重复枚举_结果完全一致()
    {
        using var temp = new TempDirectory();
        var gameDirectory = temp.Combine("instance-stable");
        Directory.CreateDirectory(gameDirectory);

        var first = InstanceFolders.Enumerate(gameDirectory);
        var second = InstanceFolders.Enumerate(gameDirectory);

        Assert.Equal(first.Count, second.Count);

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Key, second[i].Key);
            Assert.Equal(first[i].RelativePath, second[i].RelativePath);
            Assert.Equal(first[i].Exists, second[i].Exists);
        }
    }

    [Fact]
    public void 空白目录参数_返回空列表不抛()
    {
        Assert.Empty(InstanceFolders.Enumerate(null));
        Assert.Empty(InstanceFolders.Enumerate(string.Empty));
        Assert.Empty(InstanceFolders.Enumerate("   "));

        // 路径解析同理：没有游戏目录时返回 null，由调用方写状态行。
        Assert.Null(InstanceFolders.ResolvePath(null, "saves"));
        Assert.Null(InstanceFolders.ResolvePath("   ", "saves"));
    }

    [Fact]
    public void 枚举_按磁盘事实填Exists且不替玩家建目录()
    {
        using var temp = new TempDirectory();
        var gameDirectory = temp.Combine("instance-exists");
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(Path.Combine(gameDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "saves"));

        var folders = InstanceFolders.Enumerate(gameDirectory);
        var byKey = new Dictionary<string, InstanceFolder>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            byKey[folder.Key] = folder;
        }

        // 目录不存在也返回五项：芯片行不能随目录增删长短不一。
        Assert.Equal(5, folders.Count);
        Assert.True(byKey["mods"].Exists);
        Assert.True(byKey["saves"].Exists);
        Assert.False(byKey["resourcepacks"].Exists);
        Assert.False(byKey["screenshots"].Exists);
        Assert.False(byKey["logs"].Exists);

        // 扫描不得写盘：替玩家在游戏目录里建目录等于改他的游戏。
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "screenshots")));
        Assert.False(Directory.Exists(Path.Combine(gameDirectory, "logs")));
    }

    [Fact]
    public void 枚举_游戏目录不存在时五项照常返回()
    {
        using var temp = new TempDirectory();
        var gameDirectory = temp.Combine("not-created-yet");

        var folders = InstanceFolders.Enumerate(gameDirectory);

        Assert.Equal(5, folders.Count);
        foreach (var folder in folders)
        {
            Assert.False(folder.Exists);
        }
    }

    [Fact]
    public void 服务实现_与纯函数结果一致()
    {
        using var temp = new TempDirectory();
        var gameDirectory = temp.Combine("instance-service");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "logs"));

        var service = new InstanceFolderService();

        var folders = service.List(gameDirectory);
        Assert.Equal(5, folders.Count);
        // Find 只返回定义，不含磁盘状态：Exists 恒为 false，磁盘事实由 List 给。
        Assert.False(service.Find("logs")!.Exists);
        Assert.NotNull(service.ResolvePath(gameDirectory, "logs"));

        // 契约的默认实现（未注册服务时）返回「没有入口」，不抛异常。
        IInstanceFolderService unregistered = new UnsupportedFolderService();
        Assert.Empty(unregistered.List(gameDirectory));
        Assert.Null(unregistered.Find("logs"));
        Assert.Null(unregistered.ResolvePath(gameDirectory, "logs"));
    }

    /// <summary>直接吃契约默认实现的替身：验证「未注册服务」时界面拿到的是空清单而不是异常。</summary>
    private sealed class UnsupportedFolderService : IInstanceFolderService
    {
    }
}