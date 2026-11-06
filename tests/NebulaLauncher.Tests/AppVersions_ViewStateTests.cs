using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本页三视图（票 #87）的状态隔离契约。
///
/// 为什么要有这组测试：票面把「三个视图不得共用同一份筛选状态」写成硬边界，
/// 而共用与否在界面上很难看出来——在「可下载」里搜 1.20，切回「已安装」时看到
/// 一份被滤过的列表，用户会以为版本丢了，但这既不会抛异常也不会让编译报错。
/// 这里直接钉住三份状态对象互不影响，以及搜索框绑定的是「当前视图那一份」。
///
/// 不触碰任何 Avalonia 类型（见 CONVENTIONS §8.2 的测试约束）。
/// </summary>
public class AppVersions_ViewStateTests
{
    private static RemoteVersion MakeRemote(string id, string type = "release") => new()
    {
        Id = id,
        Type = type,
        Url = $"https://example.invalid/{id}.json",
        ReleaseTime = new DateTimeOffset(2024, 6, 13, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void 三个视图的搜索词互不影响()
    {
        var vm = new VersionsViewModel();

        vm.SearchText = "1.20";
        Assert.Equal("1.20", vm.Installed.SearchText);
        Assert.Equal(string.Empty, vm.Available.SearchText);
        Assert.Equal(string.Empty, vm.Modpacks.SearchText);

        vm.Available.SearchText = "fabric";
        Assert.Equal("1.20", vm.Installed.SearchText);
        Assert.Equal("fabric", vm.Available.SearchText);

        vm.Modpacks.SearchText = "skyblock";
        Assert.Equal("1.20", vm.Installed.SearchText);
        Assert.Equal("fabric", vm.Available.SearchText);
        Assert.Equal("skyblock", vm.Modpacks.SearchText);
    }

    [Fact]
    public void 三个视图的筛选键互不影响()
    {
        var vm = new VersionsViewModel();

        vm.FilterKind = VersionsViewModel.FilterLoader;
        Assert.Equal(VersionsViewModel.FilterLoader, vm.Installed.FilterKind);
        Assert.Equal(VersionsViewModel.FilterAll, vm.Available.FilterKind);

        // 「显示已隐藏」是已安装视图独有的开关，不该渗到另两个视图。
        vm.ShowHidden = true;
        Assert.True(vm.Installed.ShowHidden);
    }

    [Fact]
    public void 搜索框绑定的是当前视图那一份()
    {
        var vm = new VersionsViewModel();

        vm.Installed.SearchText = "已安装关键词";
        vm.Available.SearchText = "可下载关键词";

        vm.SelectViewCommand.Execute(VersionsViewKind.Installed);
        Assert.Equal("已安装关键词", vm.CurrentSearchText);

        vm.SelectViewCommand.Execute(VersionsViewKind.Available);
        Assert.Equal("可下载关键词", vm.CurrentSearchText);

        // 写回也只落在当前视图上，另一份不动。
        vm.CurrentSearchText = "改过的";
        Assert.Equal("改过的", vm.Available.SearchText);
        Assert.Equal("已安装关键词", vm.Installed.SearchText);
    }

    [Fact]
    public void 切视图时占位文案与标题跟着换()
    {
        var vm = new VersionsViewModel();

        Assert.Equal("已安装", vm.CurrentViewTitle);
        Assert.Equal("搜索版本名…", vm.SearchWatermark);

        vm.SelectViewCommand.Execute(VersionsViewKind.Available);
        Assert.Equal("可下载", vm.CurrentViewTitle);
        Assert.Equal("搜索版本号…", vm.SearchWatermark);
        Assert.True(vm.IsAvailableView);
        Assert.False(vm.IsInstalledView);

        vm.SelectViewCommand.Execute(VersionsViewKind.Modpacks);
        Assert.Equal("整合包", vm.CurrentViewTitle);
        Assert.Equal("搜索整合包…", vm.SearchWatermark);
        Assert.True(vm.IsModpacksView);
    }

    [Fact]
    public void 页签索引与视图标识双向一致()
    {
        var vm = new VersionsViewModel();

        vm.CurrentViewIndex = (int)VersionsViewKind.Modpacks;
        Assert.Equal(VersionsViewKind.Modpacks, vm.CurrentView);

        vm.CurrentView = VersionsViewKind.Available;
        Assert.Equal((int)VersionsViewKind.Available, vm.CurrentViewIndex);

        // 越界索引必须被忽略，不能把枚举写成非法值。
        vm.CurrentViewIndex = 99;
        Assert.Equal(VersionsViewKind.Available, vm.CurrentView);
    }

    [Fact]
    public void 已安装视图的搜索只影响自己的可见行()
    {
        var vm = new VersionsViewModel();

        // 直接往状态集合里塞行，绕开磁盘扫描：这里要验的只是过滤谓词。
        vm.Versions.Add(new VersionRow(new VersionInfo { Name = "1.20.1-fabric" }));
        vm.Versions.Add(new VersionRow(new VersionInfo { Name = "1.16.5" }));

        vm.SearchText = "1.20";
        Assert.Single(vm.VisibleVersions);
        Assert.Equal("1.20.1-fabric", vm.VisibleVersions[0].Name);

        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.VisibleVersions.Count);
    }

    [Fact]
    public void 可下载视图的搜索按版本号过滤且不改原始清单()
    {
        var vm = new VersionsViewModel();

        vm.Available.Rows.Add(new RemoteVersionRow(MakeRemote("1.20.1"), isInstalled: false));
        vm.Available.Rows.Add(new RemoteVersionRow(MakeRemote("1.16.5"), isInstalled: false));

        vm.Available.SearchText = "1.20";

        Assert.Single(vm.Available.VisibleRows);
        Assert.Equal("1.20.1", vm.Available.VisibleRows[0].Id);

        // 原始清单必须原样保留：清空搜索后应当全部回来，而不是被过滤掉的那条永久消失。
        Assert.Equal(2, vm.Available.Rows.Count);

        vm.Available.SearchText = string.Empty;
        Assert.Equal(2, vm.Available.VisibleRows.Count);
    }

    [Fact]
    public void 只显示正式版时快照被滤掉但原始清单保留()
    {
        var vm = new VersionsViewModel();

        vm.Available.Rows.Add(new RemoteVersionRow(MakeRemote("1.20.1"), isInstalled: false));
        vm.Available.Rows.Add(new RemoteVersionRow(MakeRemote("24w14potato", "snapshot"), isInstalled: false));

        // 逐条 Add 不触发重算（真实路径是 Clear + Add，由 Reset 触发；
        // 单测要触发同一段逻辑就显式重设一次会变化的值）。
        vm.Available.SearchText = "2";
        vm.Available.SearchText = string.Empty;

        // 默认只看正式版：快照不该出现。
        Assert.True(vm.Available.ReleaseOnly);
        Assert.Single(vm.Available.VisibleRows);
        Assert.Equal("1.20.1", vm.Available.VisibleRows[0].Id);

        // 关掉之后快照回来，原始清单始终是 2 条（筛选不动数据源）。
        vm.ToggleReleaseOnlyCommand.Execute(null);
        Assert.False(vm.Available.ReleaseOnly);
        Assert.Equal(2, vm.Available.VisibleRows.Count);
        Assert.Equal(2, vm.Available.Rows.Count);
    }

    [Fact]
    public void 整合包行尾状态优先显示导入文案()
    {
        var project = new ModrinthProject
        {
            ProjectId = "abc",
            Slug = "skyblock",
            Title = "SkyBlock",
            Downloads = 12345,
        };

        var row = new ModpackRow(project);

        // 没有导入时显示下载量。
        Assert.Contains("次下载", row.StatusText, StringComparison.Ordinal);

        // 导入中优先显示阶段文案，而不是百分比——「下载失败」与「已导入」必须看得出来。
        row.IsImporting = true;
        row.ImportStatusText = "正在下载整合包…";
        Assert.Equal("正在下载整合包…", row.StatusText);

        // 没有阶段文案时退回百分比。
        row.ImportStatusText = null;
        row.ImportProgress = 0.5;
        Assert.Equal("50%", row.StatusText);

        // 导入结束后保留结果文案，不立刻跳回下载量。
        row.IsImporting = false;
        row.ImportStatusText = "已导入";
        Assert.Equal("已导入", row.StatusText);
    }

    [Fact]
    public void 实例名拼接不会重复版本号且受长度限制()
    {
        Assert.Equal("SkyBlock 1.0", VersionsViewModel.BuildModpackInstanceName("SkyBlock", "1.0"));

        // 项目名里已经带了版本号时不重复拼。
        Assert.Equal("SkyBlock 1.0", VersionsViewModel.BuildModpackInstanceName("SkyBlock 1.0", "1.0"));

        var longName = VersionsViewModel.BuildModpackInstanceName(new string('x', 100), "1.0");
        Assert.True(longName.Length <= 60, $"实例名超长：{longName.Length}");
    }

    [Fact]
    public void 整合包中转归档落在缓存目录且带后缀()
    {
        var path = VersionsViewModel.ResolveModpackArchivePath(@"C:\data\cache", "pack.mrpack", "SkyBlock");

        Assert.StartsWith(@"C:\data\cache\modpacks", path, StringComparison.Ordinal);
        Assert.EndsWith(".mrpack", path, StringComparison.OrdinalIgnoreCase);

        // 没有扩展名时补 .mrpack，而不是留一个无后缀的中转文件。
        var noExtension = VersionsViewModel.ResolveModpackArchivePath(@"C:\data\cache", "pack", "SkyBlock");
        Assert.EndsWith(".mrpack", noExtension, StringComparison.OrdinalIgnoreCase);
    }
}
