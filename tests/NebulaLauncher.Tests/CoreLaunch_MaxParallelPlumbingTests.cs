using System.Reflection;
using CmlLib.Core;
using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Tests;

/// <summary>
/// 工厂把「并发下载数」忠实地交给了安装器（票 27 缺陷 B 的管道段）。
///
/// 这一层单独测的理由：设置值到安装器之间经过三跳——设置服务 → 调用方
/// （<c>InstallService.MaxParallel</c> / <c>GameLauncher.ResolveMaxParallel</c>）→
/// <see cref="MinecraftLauncherFactory.Create"/> 的 <c>maxParallel</c> → <c>NebulaGameInstaller</c>。
/// 前两跳各有自己的用例，而最后一跳（工厂是否真的用了传进来的值，而不是永远用默认 8）
/// 只能在这里验：<c>NebulaGameInstaller</c> 是 internal，参数不落盘也不上报。
/// </summary>
public class CoreLaunch_MaxParallelPlumbingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(256)]
    public void 工厂把maxParallel交给安装器(int maxParallel)
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient();

        var launcher = MinecraftLauncherFactory.Create(
            new MinecraftPath(temp.Path), client, maxParallel: maxParallel);

        Assert.Equal(maxParallel, ReadMaxParallel(launcher.GameInstaller));
    }

    [Fact]
    public void 未指定时沿用默认值8()
    {
        using var temp = new TempDirectory();
        using var client = new HttpClient();

        var launcher = MinecraftLauncherFactory.Create(new MinecraftPath(temp.Path), client);

        // 与改造前一致：传 null client 或省略参数时不改变既有行为。
        Assert.Equal(8, ReadMaxParallel(launcher.GameInstaller));
    }

    /// <summary>
    /// 读 <c>NebulaGameInstaller._maxParallel</c>。
    ///
    /// 走反射是因为那个类型是 internal 且不暴露并发上限——它是实现细节，
    /// 为了测试把它变成 public 会让「谁能改它」的责任边界变模糊。
    /// </summary>
    private static int ReadMaxParallel(object installer)
    {
        var field = installer.GetType()
            .GetField("_maxParallel", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return (int)field!.GetValue(installer)!;
    }
}
