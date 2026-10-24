using CmlLib.Core;
using CmlLib.Core.Installers;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 加载器安装的阶段进度（Forge / NeoForge）。
///
/// 三块判据：
/// 1. <see cref="InstallStagePlan"/> 的区间端点与夹取（原版 0–50%、加载器 50–85%、处理器 85–100%）；
/// 2. 适配器的阶段切换与单调性：阶段内的文件比例被映射进总进度，跨阶段不能回退；
/// 3. 失败路径：<c>Abort</c> 之后不再上报，且最后一条进度不带阶段文案
///    ——界面上不能留着「正在执行 Forge 安装器…」。
///
/// 为什么能直接构造适配器：Core 有 <c>InternalsVisibleTo("NebulaLauncher.Tests")</c>，
/// 适配器是安装服务内部的进度合并器，把它做成 public 只为测试是过度暴露。
/// </summary>
public class CoreInstalling_LoaderStageTests
{
    // ================= 区间映射 =================

    [Theory]
    [InlineData(InstallStage.Vanilla, 0.0, 0.00)]
    [InlineData(InstallStage.Vanilla, 0.5, 0.25)]
    [InlineData(InstallStage.Vanilla, 1.0, 0.50)]
    [InlineData(InstallStage.LoaderDownload, 0.0, 0.50)]
    [InlineData(InstallStage.LoaderDownload, 0.5, 0.675)]
    [InlineData(InstallStage.LoaderDownload, 1.0, 0.85)]
    [InlineData(InstallStage.LoaderProcessors, 0.0, 0.85)]
    [InlineData(InstallStage.LoaderProcessors, 0.5, 0.925)]
    [InlineData(InstallStage.LoaderProcessors, 1.0, 1.00)]
    [InlineData(InstallStage.Finalize, 0.0, 0.85)]
    [InlineData(InstallStage.Finalize, 1.0, 1.00)]
    public void 阶段比例映射到固定区间(InstallStage stage, double stageRatio, double expected)
    {
        Assert.Equal(expected, InstallStagePlan.Map(stage, stageRatio), precision: 6);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, 0.5)]
    [InlineData(double.NaN, 0.0)]
    public void 阶段比例越界被夹住(double stageRatio, double expected)
    {
        Assert.Equal(expected, InstallStagePlan.Map(InstallStage.Vanilla, stageRatio), precision: 6);
    }

    [Fact]
    public void 阶段区间首尾相接且终点为一()
    {
        Assert.Equal(0.0, InstallStagePlan.Start(InstallStage.Vanilla));
        Assert.Equal(InstallStagePlan.VanillaEnd, InstallStagePlan.End(InstallStage.Vanilla));

        Assert.Equal(InstallStagePlan.VanillaEnd, InstallStagePlan.Start(InstallStage.LoaderDownload));
        Assert.Equal(InstallStagePlan.LoaderDownloadEnd, InstallStagePlan.End(InstallStage.LoaderDownload));

        Assert.Equal(InstallStagePlan.LoaderDownloadEnd, InstallStagePlan.Start(InstallStage.LoaderProcessors));
        Assert.Equal(InstallStagePlan.AllDone, InstallStagePlan.End(InstallStage.LoaderProcessors));

        // 收尾阶段与处理器阶段共用 85–100%：处理器跑完比例已经是 1，
        // 收尾只换文案（「正在写入版本清单…」），不再推比例。
        Assert.Equal(InstallStagePlan.LoaderDownloadEnd, InstallStagePlan.Start(InstallStage.Finalize));
        Assert.Equal(InstallStagePlan.AllDone, InstallStagePlan.End(InstallStage.Finalize));
    }

    // ================= 阶段文案 =================

    [Fact]
    public void 阶段文案按阶段与加载器切换()
    {
        Assert.Equal("正在准备游戏本体…", InstallStagePlan.Describe(InstallStage.Vanilla, LoaderKind.Forge));
        Assert.Equal("正在下载 Forge 安装文件…", InstallStagePlan.Describe(InstallStage.LoaderDownload, LoaderKind.Forge));
        Assert.Equal("正在执行 Forge 安装器…", InstallStagePlan.Describe(InstallStage.LoaderProcessors, LoaderKind.Forge));
        Assert.Equal("正在执行 NeoForge 安装器…", InstallStagePlan.Describe(InstallStage.LoaderProcessors, LoaderKind.NeoForge));
        Assert.Equal("正在写入版本清单…", InstallStagePlan.Describe(InstallStage.Finalize, LoaderKind.Forge));
    }

    // ================= 适配器：分阶段 =================

    [Fact]
    public void 分阶段适配器把原版阶段映射到零到五十()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.Vanilla);
        var start = sink.Last!;
        Assert.Equal(0.0, start.Ratio, precision: 6);
        Assert.Equal("正在准备游戏本体…", start.Stage);

        adapter.FileProgress.Report(Done(100, 50, "client.jar"));

        var half = sink.Last!;
        Assert.Equal(0.25, half.Ratio, precision: 6);
        Assert.Equal("正在准备游戏本体…", half.Stage);
        Assert.Equal("client.jar", half.CurrentFile);
    }

    [Fact]
    public void 分阶段适配器在阶段边界跳变但不回退()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.Vanilla);
        adapter.FileProgress.Report(Done(100, 100, "assets"));

        adapter.Enter(InstallStage.LoaderDownload);
        var loaderStart = sink.Last!;
        Assert.Equal(0.5, loaderStart.Ratio, precision: 6);
        Assert.Equal("正在下载 Forge 安装文件…", loaderStart.Stage);

        // 加载器阶段的第一个子批次只吃掉剩余区间的一半：50% → 67.5%。
        adapter.FileProgress.Report(Done(1, 1, "forge-1.20.1-47.2.0-installer.jar"));
        Assert.Equal(0.675, sink.Last!.Ratio, precision: 6);

        // 第二批（maven 依赖）继续减半：67.5% → 76.25%。
        adapter.FileProgress.Report(Done(36, 36, "net.minecraftforge:jarsplitter"));
        Assert.Equal(0.7625, sink.Last!.Ratio, precision: 6);

        // 处理器阶段从 85% 起，不会把进度条拉回去。
        adapter.OutputProgress.Report("Building");
        Assert.Equal(0.85, sink.Last!.Ratio, precision: 6);
        Assert.Equal("正在执行 Forge 安装器…", sink.Last!.Stage);

        adapter.FileProgress.Report(Done(4, 4, "forge-1.20.1-universal.jar"));
        Assert.Equal(1.0, sink.Last!.Ratio, precision: 6);

        AssertMonotonic(sink.Items);
    }

    [Fact]
    public void 加载器阶段的子批次按剩余区间减半分配()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.LoaderDownload);

        // 第一批（内层原版复检/安装器本体）跑完：50% → 67.5%
        adapter.FileProgress.Report(Done(1, 1, "installer.jar"));
        Assert.Equal(0.675, sink.Last!.Ratio, precision: 6);

        // 第二批（maven 依赖）下到一半：67.5% + 0.5 * (76.25% - 67.5%) = 71.875%
        adapter.FileProgress.Report(Done(36, 18, "net.minecraftforge:asm"));
        Assert.Equal(0.71875, sink.Last!.Ratio, precision: 6);

        // 同一批跑完：76.25%，仍然没到阶段上限（后面的批次还有空间）。
        adapter.FileProgress.Report(Done(36, 36, "net.minecraftforge:asm"));
        Assert.Equal(0.7625, sink.Last!.Ratio, precision: 6);
    }

    [Fact]
    public void 安装器输出是第一行就切到处理器阶段()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.LoaderDownload);

        // 下载阶段的空输出不应该切换阶段。
        adapter.OutputProgress.Report("   ");
        Assert.Equal(InstallStage.LoaderDownload, adapter.Stage);

        adapter.OutputProgress.Report("Building");

        Assert.Equal(InstallStage.LoaderProcessors, adapter.Stage);
        Assert.Equal("正在执行 Forge 安装器…", sink.Last!.Stage);
    }

    [Fact]
    public void 处理器阶段的进度按处理器数量推进()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.LoaderDownload);
        adapter.OutputProgress.Report("Building");
        adapter.FileProgress.Report(Done(4, 2, "forge-1.20.1-universal.jar"));

        // 2/4 个处理器 → 85% + 0.5 * 15% = 92.5%
        Assert.Equal(0.925, sink.Last!.Ratio, precision: 6);
    }

    [Fact]
    public void 完成时比例到一且清掉阶段文案()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.LoaderDownload);
        adapter.FileProgress.Report(Done(10, 3, "x.jar"));
        adapter.Complete("forge-1.20.1-47.2.0");

        var last = sink.Last!;
        Assert.Equal(1.0, last.Ratio, precision: 6);
        Assert.Null(last.Stage);
    }

    // ================= 适配器：失败与降级 =================

    [Fact]
    public void 失败时不留半截阶段状态()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: true);

        adapter.Enter(InstallStage.LoaderDownload);
        adapter.FileProgress.Report(Done(10, 3, "x.jar"));
        adapter.Abort();

        var last = sink.Last!;
        Assert.Null(last.Stage);
        Assert.True(last.Ratio < 1.0);

        // 中断之后不再上报：失败后继续往界面推「正在执行安装器…」是最误导的状态。
        var countAfterAbort = sink.Items.Count;
        adapter.FileProgress.Report(Done(10, 10, "y.jar"));
        adapter.ByteProgress.Report(new ByteProgress(100, 100));
        adapter.OutputProgress.Report("Building");
        adapter.Enter(InstallStage.Finalize);
        adapter.Complete("never");

        Assert.Equal(countAfterAbort, sink.Items.Count);
    }

    [Fact]
    public void 非分阶段适配器保持旧行为()
    {
        var sink = new SyncProgress();
        var adapter = NewAdapter(sink, staged: false);

        adapter.FileProgress.Report(Done(100, 40, "client.jar"));

        var last = sink.Last!;
        Assert.Equal(0.4, last.Ratio, precision: 6);
        Assert.Null(last.Stage);

        adapter.Complete("1.20.1");
        Assert.Equal(1.0, sink.Last!.Ratio, precision: 6);
        Assert.Null(sink.Last!.Stage);
    }

    [Fact]
    public void 进度记录的新字段默认值为空()
    {
        // 旧调用点只传 8 个参数，Stage 必须是可省略的（界面据此隐藏阶段行）。
        var progress = new DownloadProgress(1, 1, 0, 100, 100, 0, "a.jar", 1.0);

        Assert.Null(progress.Stage);
        Assert.Null(DownloadProgress.Empty.Stage);
    }

    // ================= 辅助 =================

    private static InstallService.InstallProgressAdapter NewAdapter(SyncProgress sink, bool staged) =>
        new("Forge 1.20.1", sink, LoaderKind.Forge, staged: staged, log: null);

    /// <summary>Done 事件会绕过节流，测试才能确定性地拿到每一条进度。</summary>
    private static InstallerProgressChangedEventArgs Done(int total, int progressed, string name) =>
        new(total, progressed, name, InstallerEventType.Done);

    private static void AssertMonotonic(IReadOnlyList<DownloadProgress> items)
    {
        for (var i = 1; i < items.Count; i++)
        {
            Assert.True(
                items[i].Ratio >= items[i - 1].Ratio - 1e-9,
                $"进度回退：{items[i - 1].Ratio} -> {items[i].Ratio}（第 {i} 条）");
        }
    }

    /// <summary>同步收集器：<see cref="Progress{T}"/> 会把回调投递到同步上下文，测试里不可控。</summary>
    private sealed class SyncProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = new();

        public DownloadProgress? Last => Items.Count > 0 ? Items[^1] : null;

        public void Report(DownloadProgress value) => Items.Add(value);
    }
}
