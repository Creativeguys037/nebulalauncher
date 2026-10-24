using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 源测速 CLI 的输出契约：三行固定格式、四种 decision、两种退出码。
///
/// 全部用构造报告驱动，不联网也不等待真实时间——被验收的是「给定报告会输出什么」，
/// 网络那一层的验收在 <see cref="CoreDownloads_SourceProbeTests"/> 里用假 handler 做过了。
/// 这里锁死格式，是因为脚本依赖它：字段顺序、key 名、布尔写法（true/false 而非 True/False）
/// 任何一项变了，下游解析就静默坏掉。
/// </summary>
public class CoreDownloads_SourceProbeCliTests
{
    /// <summary>基准用例的完整期望输出：字段顺序与写法一旦变了，脚本解析就会静默失败。</summary>
    private static readonly string[] OfficialFastLines =
    [
        "official ok=true firstByteMs=90 throughputKb=4000 targets=2/2",
        "mirror ok=true firstByteMs=600 throughputKb=1000 targets=2/2",
        "decision=official",
    ];

    /// <summary>空报告仍是三行全零值，脚本不必处理「这一行不存在」的分支。</summary>
    private static readonly string[] EmptyReportLines =
    [
        "official ok=false firstByteMs=0 throughputKb=0 targets=0/0",
        "mirror ok=false firstByteMs=0 throughputKb=0 targets=0/0",
        "decision=both-failed",
    ];

    // ==================== 三行格式 ====================

    [Fact]
    public void 成功报告的格式是三行且字段顺序固定()
    {
        var report = Report(
            Sample(DownloadSource.Official, ok: true, firstByteMs: 90, kbps: 4000, targetsOk: 2),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 600, kbps: 1000, targetsOk: 2));

        Assert.Equal(OfficialFastLines, SourceProbeCli.FormatReport(report));
    }

    [Fact]
    public void 毫秒与吞吐四舍五入到整数而不是截断()
    {
        // 190.6ms 若直接截断会写成 190，等于把一个略慢的源显示成略快，排序观感被反转。
        var report = Report(
            Sample(DownloadSource.Official, ok: true, firstByteMs: 190.6, kbps: 1023.5, targetsOk: 1),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 190.4, kbps: 1024.5, targetsOk: 1));

        var lines = SourceProbeCli.FormatReport(report);

        Assert.Equal("official ok=true firstByteMs=191 throughputKb=1024 targets=1/1", lines[0]);
        Assert.Equal("mirror ok=true firstByteMs=190 throughputKb=1025 targets=1/1", lines[1]);
    }

    [Fact]
    public void 失败的一侧仍然输出一行且为false与零值()
    {
        var report = Report(
            Sample(DownloadSource.Official, ok: true, firstByteMs: 120, kbps: 2000, targetsOk: 2),
            Sample(DownloadSource.Mirror, ok: false, targetsFailed: 2));

        var lines = SourceProbeCli.FormatReport(report);

        // 失败侧的 targets 是「成功数/总数」，所以是 0/2 而不是 2/2。
        Assert.Equal("mirror ok=false firstByteMs=0 throughputKb=0 targets=0/2", lines[1]);
        Assert.Equal("decision=official", lines[2]);
    }

    [Fact]
    public void 缺少样本的一侧也输出一行零值_行数恒为三()
    {
        // 目标全不可镜像时 report 里可能只留下一侧的样本：脚本不该为此走「行数不足」分支。
        var report = Report(Sample(DownloadSource.Official, ok: true, firstByteMs: 88, kbps: 3000, targetsOk: 1));

        var lines = SourceProbeCli.FormatReport(report);

        Assert.Equal(3, lines.Count);
        Assert.Equal("mirror ok=false firstByteMs=0 throughputKb=0 targets=0/0", lines[1]);
        Assert.Equal("decision=official", lines[2]);
    }

    [Fact]
    public void 非有限的测量值不会写进整数字段()
    {
        // 正常的 probe 不会产出 NaN；但格式化是纯函数，别人替换实现时不能让它输出 "NaN"。
        var report = Report(
            new SourceProbeSample
            {
                Source = DownloadSource.Official,
                SourceName = SourceProbeRanking.DisplayName(DownloadSource.Official),
                Succeeded = true,
                FirstByteMs = double.NaN,
                ThroughputKbps = double.PositiveInfinity,
                TargetsOk = 1,
            },
            Sample(DownloadSource.Mirror, ok: false, targetsFailed: 1));

        var lines = SourceProbeCli.FormatReport(report);

        Assert.Equal("official ok=true firstByteMs=0 throughputKb=0 targets=1/1", lines[0]);
    }

    // ==================== decision 的四种取值 ====================

    [Fact]
    public void 官方更快时decision为official()
    {
        // 官方 90 + 262 = 352ms；镜像 600 + 1024 = 1624ms；差距 78%，远超容差。
        var report = Report(
            Sample(DownloadSource.Official, ok: true, firstByteMs: 90, kbps: 4000, targetsOk: 2),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 600, kbps: 1000, targetsOk: 2));

        Assert.Equal("decision=official", SourceProbeCli.FormatReport(report)[2]);
        Assert.Equal(SourceProbeCli.SuccessExitCode, SourceProbeCli.ExitCode(report));
    }

    [Fact]
    public void 镜像更快时decision为mirror()
    {
        // 官方 600 + 1024 = 1624ms；镜像 90 + 262 = 352ms。
        var report = Report(
            Sample(DownloadSource.Official, ok: true, firstByteMs: 600, kbps: 1000, targetsOk: 2),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 90, kbps: 4000, targetsOk: 2));

        Assert.Equal("decision=mirror", SourceProbeCli.FormatReport(report)[2]);
        Assert.Equal(SourceProbeCli.SuccessExitCode, SourceProbeCli.ExitCode(report));
    }

    [Fact]
    public void 两侧接近时decision为tie且退出码仍为0()
    {
        // 综合分 1100 vs 1570：差距 470/1570 = 29.9%，在 30% 容差内。
        var report = Report(
            Sample(DownloadSource.Official, ok: true, firstByteMs: 100, kbps: 1024, targetsOk: 2),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 570, kbps: 1024, targetsOk: 2));

        Assert.Equal("decision=tie", SourceProbeCli.FormatReport(report)[2]);
        Assert.Equal(SourceProbeCli.SuccessExitCode, SourceProbeCli.ExitCode(report));
    }

    [Fact]
    public void 两侧都失败时decision为both_failed且退出码为3()
    {
        var report = Report(
            Sample(DownloadSource.Official, ok: false, targetsFailed: 2),
            Sample(DownloadSource.Mirror, ok: false, targetsFailed: 2));

        var lines = SourceProbeCli.FormatReport(report);

        Assert.Equal("decision=both-failed", lines[2]);
        Assert.Equal(SourceProbeCli.BothFailedExitCode, SourceProbeCli.ExitCode(report));
    }

    [Fact]
    public void 只有一侧成功时退出码为0而不是3()
    {
        var report = Report(
            Sample(DownloadSource.Official, ok: false, targetsFailed: 2),
            Sample(DownloadSource.Mirror, ok: true, firstByteMs: 95, kbps: 5200, targetsOk: 2));

        Assert.Equal("decision=mirror", SourceProbeCli.FormatReport(report)[2]);
        Assert.Equal(0, SourceProbeCli.ExitCode(report));
    }

    [Fact]
    public void 空报告按两侧失败处理()
    {
        // SourceProbeReport.Empty 的 BothFailed 是 false，但它同样没有任何可用源：
        // decision 与退出码必须都落到「不可用」，否则脚本会把「没测成」当成「测成功」。
        Assert.Equal(EmptyReportLines, SourceProbeCli.FormatReport(SourceProbeReport.Empty));
        Assert.Equal(SourceProbeCli.BothFailedExitCode, SourceProbeCli.ExitCode(SourceProbeReport.Empty));
    }

    // ==================== 构造辅助 ====================

    /// <summary>用真实的排序函数组装报告，保证 decision 行与 <see cref="SourceProbeRanking"/> 始终同源。</summary>
    private static SourceProbeReport Report(params SourceProbeSample[] samples)
    {
        var decision = SourceProbeRanking.Decide(samples);

        return new SourceProbeReport
        {
            Samples = samples,
            SuggestedSource = decision.SuggestedSource,
            IsTie = decision.IsTie,
            BothFailed = decision.BothFailed,
            Summary = SourceProbeRanking.Describe(samples, decision),
        };
    }

    private static SourceProbeSample Sample(
        DownloadSource source,
        bool ok,
        double firstByteMs = 0,
        double kbps = 0,
        int targetsOk = 0,
        int targetsFailed = 0) => new()
    {
        Source = source,
        SourceName = SourceProbeRanking.DisplayName(source),
        Succeeded = ok,
        FirstByteMs = ok ? firstByteMs : 0,
        ThroughputKbps = ok ? kbps : 0,
        TargetsOk = ok ? targetsOk : 0,
        TargetsFailed = targetsFailed,
        FailureReason = ok ? null : "超时（3 秒）",
    };
}