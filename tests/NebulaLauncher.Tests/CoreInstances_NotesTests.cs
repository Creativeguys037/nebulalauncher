using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例备注（<see cref="InstanceInfo.Notes"/>）的持久化与复制。
///
/// 这个字段在仓库里当了很久的死字段：模型有、App 层没有任何引用。
/// 接线时真正会坏的地方有两个，所以断言只打这两处：
/// 1. <c>InstanceService.Clone</c> 是手写字段映射，漏一个字段就会被「保存一次」悄悄清空
///    （HANDOVER §5.1 里 Loader / MinecraftVersion / IsolationEnabled 就是这么丢的）；
/// 2. 空备注必须写成 null，不能写成空串——否则「没写过备注」与「写了一个空备注」无法区分。
/// </summary>
public class CoreInstances_NotesTests
{
    [Fact]
    public async Task 备注_保存后从实例列表读回()
    {
        using var temp = new TempDirectory();
        var service = new InstanceService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        await service.LoadAsync();
        var instance = await service.CreateAsync("带备注的实例", "1.20.1");

        instance.Notes = "这个实例只跑整合包，别在这里装光影";
        await service.SaveAsync(instance);

        // Instances 返回的是 Clone 副本：读得到就说明 Clone 映射覆盖了 Notes。
        var saved = Assert.Single(service.Instances);
        Assert.Equal("这个实例只跑整合包，别在这里装光影", saved.Notes);
    }

    [Fact]
    public async Task 备注_落盘后重新加载仍在()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var first = new InstanceService(paths, NullAppLog.Instance);

        await first.LoadAsync();
        var instance = await first.CreateAsync("备注往返", "1.21.4");
        instance.Notes = "换一个服务实例读，证明写的是磁盘而不是缓存";
        await first.SaveAsync(instance);

        var second = new InstanceService(paths, NullAppLog.Instance);
        await second.LoadAsync();

        var reloaded = Assert.Single(second.Instances);
        Assert.Equal("备注往返", reloaded.Name);
        Assert.Equal("换一个服务实例读，证明写的是磁盘而不是缓存", reloaded.Notes);
    }

    [Fact]
    public async Task 备注_未设置时读回为null而不是空串()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var first = new InstanceService(paths, NullAppLog.Instance);

        await first.LoadAsync();
        var instance = await first.CreateAsync("没有备注", "1.20.1");
        await first.SaveAsync(instance);

        var second = new InstanceService(paths, NullAppLog.Instance);
        await second.LoadAsync();

        Assert.Null(Assert.Single(second.Instances).Notes);
    }

    [Fact]
    public async Task 备注_清空写null后落盘不残留旧值()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var first = new InstanceService(paths, NullAppLog.Instance);

        await first.LoadAsync();
        var instance = await first.CreateAsync("清空备注", "1.20.1");
        instance.Notes = "先写一段";
        await first.SaveAsync(instance);

        var cleared = first.Instances[0];
        cleared.Notes = null;
        await first.SaveAsync(cleared);

        var second = new InstanceService(paths, NullAppLog.Instance);
        await second.LoadAsync();

        Assert.Null(Assert.Single(second.Instances).Notes);
    }

    [Fact]
    public async Task 备注_克隆实例时一并复制()
    {
        using var temp = new TempDirectory();
        var service = new InstanceService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        await service.LoadAsync();
        var source = await service.CreateAsync("源实例", "1.20.1");
        source.Notes = "备注要跟着副本走，否则副本还得重写一遍";
        await service.SaveAsync(source);

        var clone = await service.CloneAsync(source.Id, new InstanceCloneOptions());

        Assert.NotNull(clone);
        Assert.Equal("备注要跟着副本走，否则副本还得重写一遍", clone!.Notes);

        // 落盘的副本同样带备注：返回值的映射对、磁盘上的对不上，是最难查的一类。
        var clonedOnDisk = service.Instances.First(i => i.Id == clone.Id);
        Assert.Equal("备注要跟着副本走，否则副本还得重写一遍", clonedOnDisk.Notes);
    }
}
