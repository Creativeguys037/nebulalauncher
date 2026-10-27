using System;
using System.Collections.Generic;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「皮肤已变更」通知（票 #44 AC2）。侧边栏 3D 卡只监听 <c>IAccountService.AccountsChanged</c>，
/// 而换肤改的是服务端材质、不动 <see cref="NebulaLauncher.Core.Models.Account"/> 的任何字段，
/// 所以那条事件不会发——必须有一条独立通道。
///
/// 这个类型的形状是给票 #43 复用的：谁的换肤成功，谁就 <c>Notify</c> 一次，
/// 订阅方（侧边栏卡）自己决定怎么刷新。通知方不需要持有订阅方的引用。
/// </summary>
public class AppSkin_ChangeNotifierTests
{
    [Fact]
    public void 没有订阅者时通知不抛异常()
    {
        var notifier = new SkinChangeNotifier();

        notifier.Notify("官方换肤");

        Assert.Equal(0, notifier.SubscriberCount);
    }

    [Fact]
    public void 通知会带着原因送到每一个订阅者()
    {
        var notifier = new SkinChangeNotifier();
        var seen = new List<string>();

        notifier.SkinChanged += (_, e) => seen.Add(e.Reason);
        notifier.SkinChanged += (_, e) => seen.Add($"second:{e.Reason}");

        notifier.Notify("官方换肤");

        Assert.Equal(["官方换肤", "second:官方换肤"], seen);
    }

    [Fact]
    public void 退订之后不再收到通知()
    {
        var notifier = new SkinChangeNotifier();
        var count = 0;
        EventHandler<SkinChangedEventArgs> handler = (_, _) => count++;

        notifier.SkinChanged += handler;
        notifier.Notify("官方换肤");
        notifier.SkinChanged -= handler;
        notifier.Notify("官方换肤");

        // 侧边栏卡与页面同寿命，退订必须是干净的一条路（PITFALLS 2.7）。
        Assert.Equal(1, count);
        Assert.Equal(0, notifier.SubscriberCount);
    }

    [Fact]
    public void 一个订阅者抛异常不影响其他订阅者()
    {
        var notifier = new SkinChangeNotifier();
        var reached = false;

        notifier.SkinChanged += (_, _) => throw new InvalidOperationException("订阅方炸了");
        notifier.SkinChanged += (_, _) => reached = true;

        // 通知的调用方是换肤命令，它不能因为某个订阅方崩了就把成功的换肤报成失败。
        notifier.Notify("官方换肤");

        Assert.True(reached);
    }

    [Fact]
    public void 通知原因不能是空白()
    {
        var notifier = new SkinChangeNotifier();

        // 原因只用于日志与排查；允许空串的话日志里就分不出是哪条链路触发的刷新。
        Assert.Throws<ArgumentException>(() => notifier.Notify("  "));
    }

    [Fact]
    public void 进程内默认实例是同一个()
    {
        // 没有在容器里注册时，发通知的一方与收通知的一方必须落到同一个实例上，
        // 否则「换肤成功但侧边栏没变」这个 bug 会在没有 DI 注册的环境里静默复现。
        Assert.Same(SkinChangeBus.Current, SkinChangeBus.Current);
    }
}
