using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 手动镜像档的劣化提示（票 27 缺陷 C）。
///
/// 这条提示存在的理由：手动选了 BMCLAPI 之后镜像坏了，旧实现完全不采样，
/// 于是每个文件都要先撞一次连接重置/超时再回退官方，用户只看到「下载很慢」，
/// 没有任何地方告诉他「镜像已经坏了，是你自己选的档位」。
///
/// 但 PITFALLS D9 裁决「用户手动选定的源要免疫自动降级」不允许偷偷改掉方向，
/// 所以这里锁死两条边界：<see cref="DownloadSourceGovernor.Effective"/> 恒为镜像、
/// <see cref="DownloadSourceGovernor.DegradeReason"/> 恒为 null（那是自动档的语义），
/// 提示只走 <see cref="DownloadSourceGovernor.ManualDegradeHint"/> 这一条独立通道。
/// </summary>
public class CoreDownloads_ManualMirrorHintTests
{
    private DateTimeOffset _now = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 手动镜像档连续失败达到阈值后给出提示且不换源()
    {
        var governor = NewGovernor(DownloadSource.Mirror);

        Assert.Null(governor.ManualDegradeHint);

        // 差一次：还没到阈值，不能提前吓人。
        ReportMirrorFailures(governor, DownloadSourceGovernor.MirrorFailureStreakThreshold - 1);
        Assert.Null(governor.ManualDegradeHint);
        Assert.Equal(DownloadSource.Mirror, governor.Effective);

        governor.Report(MirrorFailed());

        Assert.NotNull(governor.ManualDegradeHint);
        Assert.Contains("镜像连续失败", governor.ManualDegradeHint, StringComparison.Ordinal);
        Assert.Contains("设置", governor.ManualDegradeHint, StringComparison.Ordinal);

        // D9 的两条硬边界：没有换源，也没有产出自动档的降级原因。
        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
        Assert.Null(governor.DegradedAtUtc);
    }

    [Fact]
    public void 手动官方档不产出提示()
    {
        var governor = NewGovernor(DownloadSource.Official);

        ReportMirrorFailures(governor, 5);

        Assert.Null(governor.ManualDegradeHint);
        Assert.Equal(DownloadSource.Official, governor.Effective);
        Assert.Null(governor.DegradeReason);
    }

    [Fact]
    public void 自动档不产出手动提示()
    {
        // 自动档走的是「降级」那套语义，两条通道不能同时说话，否则界面要显示两句话。
        var governor = NewGovernor(DownloadSource.Auto);

        ReportMirrorFailures(governor, DownloadSourceGovernor.MirrorFailureStreakThreshold);

        Assert.Null(governor.ManualDegradeHint);
        Assert.NotNull(governor.DegradeReason);
    }

    [Fact]
    public void 镜像恢复成功后提示消失()
    {
        var governor = NewGovernor(DownloadSource.Mirror);
        ReportMirrorFailures(governor, DownloadSourceGovernor.MirrorFailureStreakThreshold);
        Assert.NotNull(governor.ManualDegradeHint);

        governor.Report(MirrorOk());

        Assert.Null(governor.ManualDegradeHint);
        Assert.Equal(DownloadSource.Mirror, governor.Effective);
    }

    [Fact]
    public void 切换策略会清空手动提示()
    {
        var governor = NewGovernor(DownloadSource.Mirror);
        ReportMirrorFailures(governor, DownloadSourceGovernor.MirrorFailureStreakThreshold);
        Assert.NotNull(governor.ManualDegradeHint);

        governor.SetStrategy(DownloadSource.Official);
        governor.SetStrategy(DownloadSource.Mirror);

        Assert.Null(governor.ManualDegradeHint);
    }

    [Fact]
    public void 提示出现与消失各触发一次变更事件()
    {
        var governor = NewGovernor(DownloadSource.Mirror);
        var raised = 0;
        governor.Changed += (_, _) => raised++;

        // 每次失败都通知会让状态行无谓重绘，所以只在「刚好越过阈值」那一次通知。
        ReportMirrorFailures(governor, DownloadSourceGovernor.MirrorFailureStreakThreshold);
        Assert.Equal(1, raised);

        ReportMirrorFailures(governor, 3);
        Assert.Equal(1, raised);

        governor.Report(MirrorOk());
        Assert.Equal(2, raised);
    }

    [Fact]
    public void 官方首选的样本不计入手动镜像计数()
    {
        // 这些样本的首选源是官方（例如降级后的残留请求），与镜像质量无关。
        var governor = NewGovernor(DownloadSource.Mirror);

        for (var i = 0; i < 5; i++)
        {
            governor.Report(new DownloadAttemptResult
            {
                PrimarySource = DownloadSource.Official,
                PrimarySucceeded = false,
                PrimaryElapsed = TimeSpan.FromSeconds(0.5),
                FallbackUsed = true,
                FallbackElapsed = TimeSpan.FromSeconds(0.2),
                Succeeded = true,
            });
        }

        Assert.Null(governor.ManualDegradeHint);
    }

    // ================= 辅助 =================

    private DownloadSourceGovernor NewGovernor(DownloadSource strategy) => new(() => _now, strategy);

    private static void ReportMirrorFailures(DownloadSourceGovernor governor, int count)
    {
        for (var i = 0; i < count; i++)
        {
            governor.Report(MirrorFailed());
        }
    }

    private static DownloadAttemptResult MirrorFailed() => new()
    {
        PrimarySource = DownloadSource.Mirror,
        PrimarySucceeded = false,
        PrimaryElapsed = TimeSpan.FromSeconds(4),
        FallbackUsed = true,
        FallbackElapsed = TimeSpan.FromSeconds(0.2),
        Succeeded = true,
    };

    private static DownloadAttemptResult MirrorOk() => new()
    {
        PrimarySource = DownloadSource.Mirror,
        PrimarySucceeded = true,
        PrimaryElapsed = TimeSpan.FromSeconds(0.3),
        Succeeded = true,
    };
}
