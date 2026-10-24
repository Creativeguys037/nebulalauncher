using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 「缺失 Java」→「装哪个、能不能一键装」的判断。全部是纯函数，只用内存里的
/// <see cref="JavaInfo"/> 列表，不碰磁盘、不碰网络、不碰 Avalonia 类型。
///
/// 这套判断被首页的失败面板直接渲染成按钮，所以边界就是它的全部价值：
/// 判断放宽一格，界面就会出现「装了也用不了」的按钮。
/// </summary>
public sealed class CoreJava_ProvisionPlannerTests
{
    [Fact]
    public void 已装同主版本_判为已满足()
    {
        var plan = JavaProvisionPlanner.Plan(21, [Java(21)]);

        Assert.Equal(JavaProvisionAction.NothingToDo, plan.Action);
        Assert.False(plan.CanAutoInstall);
        Assert.False(plan.NeedsAttention);
        Assert.Null(plan.InstallMajor);
        Assert.Equal(21, plan.RequiredMajor);
        Assert.Contains("Java 21", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 已装更高主版本且在区间内_判为已满足()
    {
        // 1.20.5+ 要 21，装了 25 也算满足：需求是下界。
        var plan = JavaProvisionPlanner.Plan(21, [Java(25)], (21, int.MaxValue));

        Assert.Equal(JavaProvisionAction.NothingToDo, plan.Action);
        Assert.Contains("Java 25", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 只装了旧版本_建议安装需求版本()
    {
        // 26.x 要 25，本机只有 17/8：要装的是 25，不是「升一级」。
        var plan = JavaProvisionPlanner.Plan(25, [Java(17), Java(8)]);

        Assert.Equal(JavaProvisionAction.AutoInstall, plan.Action);
        Assert.True(plan.CanAutoInstall);
        Assert.Equal(25, plan.InstallMajor);
        Assert.True(plan.EstimatedDownloadBytes > 0);
    }

    [Fact]
    public void 一个Java都没有_仍给出可安装的主版本()
    {
        var plan = JavaProvisionPlanner.Plan(17, []);

        Assert.Equal(JavaProvisionAction.AutoInstall, plan.Action);
        Assert.Equal(17, plan.InstallMajor);
        Assert.Equal(JavaProvisionPlanner.EstimateDownloadBytes(17), plan.EstimatedDownloadBytes);
    }

    [Fact]
    public void 主版本不在下载集合内_退回手动指定路径()
    {
        // 9/11 这类中间版本 Mojang 没有对应 java-runtime 组件，Adoptium 也不保证有。
        var plan = JavaProvisionPlanner.Plan(11, []);

        Assert.Equal(JavaProvisionAction.ManualConfigure, plan.Action);
        Assert.False(plan.CanAutoInstall);
        Assert.Null(plan.InstallMajor);
        Assert.Contains("Java 11", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("手动", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 需求与上界冲突_不给安装按钮()
    {
        // 上界来自加载器规则（1.12.2 Forge 只能 Java ≤8）。需求 21 而区间 (8,8) 时
        // 装什么都起不来——这时候给「安装 Java 21」按钮等于骗用户下一次 50 MB。
        var plan = JavaProvisionPlanner.Plan(21, [], (8, 8));

        Assert.Equal(JavaProvisionAction.ManualConfigure, plan.Action);
        Assert.False(plan.CanAutoInstall);
        Assert.Contains("冲突", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 装在区间下界而非需求版本_例如需求8但下界为0()
    {
        // 装了 Java 17，但该组合上限是 8：不能用 17 顶，必须装 8。
        var plan = JavaProvisionPlanner.Plan(8, [Java(17)], (0, 8));

        Assert.Equal(JavaProvisionAction.AutoInstall, plan.Action);
        Assert.Equal(8, plan.InstallMajor);
    }

    [Fact]
    public void 需求缺失_退回手动指定路径且不带版本号()
    {
        // 未知版本 / 快照：拿不到权威需求时不装作知道该装哪个。
        var plan = JavaProvisionPlanner.Plan(0, [Java(17)]);

        Assert.Equal(JavaProvisionAction.ManualConfigure, plan.Action);
        Assert.Null(plan.RequiredMajor);
        Assert.Null(plan.InstallMajor);
        Assert.Equal(0, plan.EstimatedDownloadBytes);
        Assert.Contains("手动", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 说明文案带具体版本号与体积量级()
    {
        var plan = JavaProvisionPlanner.Plan(25, []);

        Assert.Contains("Java 25", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("MB", plan.Summary, StringComparison.Ordinal);

        // 55 MB 的估算落在 40–60 MB 区间内：文案是给用户看的量级，不是精确数字。
        var megabytes = plan.EstimatedDownloadBytes / (1024 * 1024);
        Assert.InRange(megabytes, 40, 60);
    }

    [Fact]
    public void 下载集合与设置页选项一致()
    {
        // 启动失败处能装的版本必须与「设置 → Java」下拉里的那五项完全一致，
        // 否则会出现「这里能点、设置页选不到」这种分裂。顺序也要钉住（新版本在前）。
        Assert.Equal(5, JavaProvisionPlanner.DownloadableMajors.Count);
        Assert.Equal(25, JavaProvisionPlanner.DownloadableMajors[0]);
        Assert.Equal(21, JavaProvisionPlanner.DownloadableMajors[1]);
        Assert.Equal(17, JavaProvisionPlanner.DownloadableMajors[2]);
        Assert.Equal(16, JavaProvisionPlanner.DownloadableMajors[3]);
        Assert.Equal(8, JavaProvisionPlanner.DownloadableMajors[4]);

        foreach (var major in JavaProvisionPlanner.DownloadableMajors)
        {
            Assert.True(JavaProvisionPlanner.IsDownloadable(major));
        }

        Assert.False(JavaProvisionPlanner.IsDownloadable(11));
        Assert.False(JavaProvisionPlanner.IsDownloadable(0));
    }

    [Fact]
    public void 启动结果的缺Java主版本默认是空且可按名字填()
    {
        // 位置参数顺序不能变：既有 new LaunchResult(false, -1, elapsed, reason) 调用不能受影响。
        var legacy = new LaunchResult(false, -1, TimeSpan.Zero, "没有 Java");
        Assert.Null(legacy.MissingJavaMajor);

        var filled = new LaunchResult(false, -1, TimeSpan.Zero, "没有 Java", MissingJavaMajor: 25);
        Assert.Equal(25, filled.MissingJavaMajor);
        Assert.Equal("没有 Java", filled.FailureReason);

        // 崩溃相关的字段也要保持原位置，别被新参数挤掉。
        var withCrash = new LaunchResult(true, 1, TimeSpan.FromSeconds(3), null, "结论");
        Assert.Equal("结论", withCrash.CrashAnalysis);
        Assert.Null(withCrash.MissingJavaMajor);
    }

    private static JavaInfo Java(int major) => new()
    {
        Path = $@"C:\Java\jdk-{major}\bin\javaw.exe",
        Version = $"{major}.0.1",
        MajorVersion = major,
        Is64Bit = true,
        HomeDirectory = $@"C:\Java\jdk-{major}",
    };
}