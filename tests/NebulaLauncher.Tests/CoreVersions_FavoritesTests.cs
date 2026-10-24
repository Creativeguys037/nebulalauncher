using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本收藏 / 隐藏的持久化与排序。
///
/// 重点不是「能存能读」，而是四条容易退化的语义：
/// 1. 收藏顺序（最新收藏在前）——版本页的置顶排序直接用它；
/// 2. 坏文件不阻断页面；
/// 3. 已删版本的条目保留——删除再装回来还该是收藏；
/// 4. 重复点隐藏不该产生多余通知与写盘。
/// </summary>
public sealed class CoreVersions_FavoritesTests
{
    private static readonly string[] ExpectedSingleFavorite = ["1.20.1"];

    private static readonly string[] ExpectedNewestFirstOrder = ["new", "old"];

    private static readonly string[] ExpectedFavoriteFirstOrder = ["1.18.2", "1.16.5", "1.20.1", "1.19.4"];

    [Fact]
    public async Task Load_文件不存在时为空且不抛()
    {
        using var temp = new TempDirectory();
        using var service = new VersionFavorites(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        await service.LoadAsync();

        Assert.Empty(service.Favorites);
        Assert.False(service.IsFavorite("1.20.1"));
        Assert.False(service.IsHidden("1.20.1"));
    }

    [Fact]
    public async Task ToggleFavorite_收藏后落盘_新实例能读回()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        using (var service = new VersionFavorites(paths, NullAppLog.Instance))
        {
            await service.LoadAsync();
            await service.ToggleFavoriteAsync("1.20.1");

            Assert.True(service.IsFavorite("1.20.1"));
            Assert.Equal(ExpectedSingleFavorite, service.Favorites);
            Assert.True(File.Exists(service.FilePath));
        }

        using var reloaded = new VersionFavorites(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        Assert.True(reloaded.IsFavorite("1.20.1"));
        Assert.Equal(ExpectedSingleFavorite, reloaded.Favorites);
    }

    [Fact]
    public async Task ToggleFavorite_再点一次取消收藏并落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        using (var service = new VersionFavorites(paths, NullAppLog.Instance))
        {
            await service.ToggleFavoriteAsync("1.20.1");
            await service.ToggleFavoriteAsync("1.20.1");

            Assert.False(service.IsFavorite("1.20.1"));
            Assert.Empty(service.Favorites);
        }

        using var reloaded = new VersionFavorites(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        Assert.False(reloaded.IsFavorite("1.20.1"));
    }

    [Fact]
    public async Task Load_收藏按收藏时间倒序返回()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var service = new VersionFavorites(paths, NullAppLog.Instance);

        // 手写文件而不是靠连续 toggle：同一毫秒内两次收藏的先后取决于时钟精度，测试会飘。
        await File.WriteAllTextAsync(service.FilePath, """
        {
          "favorites": [
            { "versionName": "old", "pinnedAtUtc": "2024-01-01T00:00:00+00:00" },
            { "versionName": "new", "pinnedAtUtc": "2025-01-01T00:00:00+00:00" }
          ],
          "hidden": []
        }
        """);

        await service.LoadAsync();

        Assert.Equal(ExpectedNewestFirstOrder, service.Favorites);
    }

    [Fact]
    public async Task Load_损坏的json静默重建为空列表()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var service = new VersionFavorites(paths, NullAppLog.Instance);

        await File.WriteAllTextAsync(service.FilePath, "{ 这不是 json ][");

        await service.LoadAsync();

        Assert.Empty(service.Favorites);
        Assert.False(service.IsHidden("1.20.1"));

        // 坏文件不该挡住后续操作：下一次写盘直接覆盖它。
        await service.ToggleFavoriteAsync("1.20.1");

        Assert.True(service.IsFavorite("1.20.1"));
        Assert.Contains("1.20.1", await File.ReadAllTextAsync(service.FilePath));
    }

    [Fact]
    public async Task 已删版本的条目_保留并在重装后仍是收藏()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        using (var service = new VersionFavorites(paths, NullAppLog.Instance))
        {
            await File.WriteAllTextAsync(service.FilePath, """
            {
              "favorites": [ { "versionName": "1.16.5", "pinnedAtUtc": "2024-05-01T00:00:00+00:00" } ],
              "hidden": [ { "versionName": "1.16.5", "hiddenAtUtc": "2024-05-02T00:00:00+00:00" } ]
            }
            """);

            await service.LoadAsync();

            // 1.16.5 的版本目录此时并不存在（模拟已删除）：服务不清理条目，只为别的版本保存一次。
            await service.ToggleFavoriteAsync("1.20.1");

            Assert.Contains("1.16.5", service.Favorites);
            Assert.True(service.IsHidden("1.16.5"));
        }

        using var reloaded = new VersionFavorites(paths, NullAppLog.Instance);
        await reloaded.LoadAsync();

        // 「重装回来」就是同名版本再次出现，收藏 / 隐藏状态原样可用。
        Assert.True(reloaded.IsFavorite("1.16.5"));
        Assert.True(reloaded.IsHidden("1.16.5"));
    }

    [Fact]
    public async Task Load_重复条目只保留一条()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        using var service = new VersionFavorites(paths, NullAppLog.Instance);

        await File.WriteAllTextAsync(service.FilePath, """
        {
          "favorites": [
            { "versionName": "1.20.1", "pinnedAtUtc": "2024-01-01T00:00:00+00:00" },
            { "versionName": "1.20.1", "pinnedAtUtc": "2024-02-01T00:00:00+00:00" }
          ],
          "hidden": []
        }
        """);

        await service.LoadAsync();

        Assert.Single(service.Favorites);
    }

    [Fact]
    public async Task SetHidden_隐藏与取消隐藏都会落盘()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        using (var service = new VersionFavorites(paths, NullAppLog.Instance))
        {
            await service.LoadAsync();
            await service.SetHiddenAsync("1.20.1", true);

            Assert.True(service.IsHidden("1.20.1"));
        }

        using (var reloaded = new VersionFavorites(paths, NullAppLog.Instance))
        {
            await reloaded.LoadAsync();
            Assert.True(reloaded.IsHidden("1.20.1"));

            await reloaded.SetHiddenAsync("1.20.1", false);
            Assert.False(reloaded.IsHidden("1.20.1"));
        }

        using var again = new VersionFavorites(paths, NullAppLog.Instance);
        await again.LoadAsync();

        Assert.False(again.IsHidden("1.20.1"));
    }

    [Fact]
    public async Task Changed_收藏与隐藏都会触发_重复隐藏不触发()
    {
        using var temp = new TempDirectory();
        using var service = new VersionFavorites(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);
        await service.LoadAsync();

        var events = 0;
        service.Changed += (_, _) => events++;

        await service.ToggleFavoriteAsync("1.20.1");
        Assert.Equal(1, events);

        await service.SetHiddenAsync("1.20.1", true);
        Assert.Equal(2, events);

        // 已经是目标状态：不变更、不写盘、不通知。
        await service.SetHiddenAsync("1.20.1", true);
        Assert.Equal(2, events);

        await service.SetHiddenAsync("1.20.1", false);
        Assert.Equal(3, events);
    }

    [Fact]
    public void Ordering_收藏优先_组内按收藏时间倒序_其余保持原顺序()
    {
        var items = new[] { "1.20.1", "1.19.4", "1.18.2", "1.16.5" };

        // 收藏列表本身就是「最新收藏在前」。
        var favorites = new[] { "1.18.2", "1.16.5" };

        var ordered = VersionFavoriteOrdering.Apply(items, name => name, favorites);

        Assert.Equal(ExpectedFavoriteFirstOrder, ordered);
    }

    [Fact]
    public void Ordering_没有收藏时保持原顺序()
    {
        var items = new[] { "b", "a", "c" };

        var ordered = VersionFavoriteOrdering.Apply(items, name => name, Array.Empty<string>());

        Assert.Equal(items, ordered);
    }
}
