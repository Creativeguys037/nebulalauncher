using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 三处共用的 Java 安装状态机（<see cref="JavaInstallState"/>）。
///
/// 这组用例只碰纯逻辑：状态迁移、比例取值域、文案口径、写回顺序。
/// 不构造窗口、不碰 <c>Application.Current</c>——按 CONVENTIONS §7.1 的约定，
/// 需要 UI 线程才能测的东西说明逻辑放错了层。
/// </summary>
public class JavaInstallStateTests
{
    /// <summary>本机已装的 Java；空列表表示一个都没有（planner 会给出可一键安装）。</summary>
    private static readonly IReadOnlyList<JavaInfo> NoJava = Array.Empty<JavaInfo>();

    private const string DownloadedPath = @"C:\fake\runtime\bin\javaw.exe";

    /// <summary>首页 / 版本页的落点：写回 → 保存 → 强制重扫，一条都不能少、顺序不能换。</summary>
    private static readonly string[] SettingsOrder = ["download:25", "write:" + DownloadedPath, "save", "detect:refresh"];

    /// <summary>设置页的落点：只写回草稿（不落盘），随后仍必须强制重扫。</summary>
    private static readonly string[] DraftOrder = ["download:25", "detect:refresh"];

    /// <summary>下载失败时只能有下载那一步。</summary>
    private static readonly string[] DownloadOnlyOrder = ["download:25"];

    // ==================== 状态迁移 ====================

    [Fact]
    public void 可安装时进入可装态并给出安装主版本()
    {
        var state = CreateState(out _);

        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));

        Assert.True(state.HasContext);
        Assert.Equal(JavaInstallPhase.Ready, state.Phase);
        Assert.Equal(25, state.InstallMajor);
        Assert.True(state.CanStart);
        Assert.False(state.IsInstalled);
        Assert.Null(state.StatusText);
        Assert.Equal(0, state.Ratio);
        Assert.Equal("0%", state.PercentText);
    }

    [Fact]
    public async Task 安装过程中处于安装中且完成后归位()
    {
        var state = CreateState(out var java);
        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));

        var duringInstall = JavaInstallPhase.Idle;
        java.OnDownload = () => duringInstall = state.Phase;

        await InstallAsync(state, major: 25);

        // 先置「安装中」再下载：按钮必须在下载期间就禁用，否则会并发下同一个运行时。
        Assert.Equal(JavaInstallPhase.Installing, duringInstall);
        Assert.True(state.IsInstalled);
        Assert.Equal(JavaInstallPhase.Installed, state.Phase);
        Assert.Equal(25, state.InstalledMajor);
        Assert.Equal(1, state.Ratio);
        Assert.Equal("100%", state.PercentText);
        Assert.Equal(@"已安装 Java 25：C:\fake\runtime\bin\javaw.exe", state.StatusText);
    }

    [Fact]
    public void 不可安装时停手动配置态()
    {
        var state = CreateState(out _);

        // 1.12.2 Forge 只允许 Java ≤8，而需求是 25：装哪个版本都起不来。
        var conflict = JavaProvisionPlanner.Plan(25, NoJava, (8, 8));
        state.Begin(25, conflict);

        Assert.False(conflict.CanAutoInstall);
        Assert.Equal(JavaInstallPhase.Manual, state.Phase);
        Assert.Null(state.InstallMajor);

        // 判不出结论（探测失败）时同样停在手动配置态，只保留需求主版本给「打开 Java 设置」用。
        state.Begin(25, plan: null, summary: "检测 Java 失败：超时。请到「设置 → Java」手动指定。");

        Assert.Equal(JavaInstallPhase.Manual, state.Phase);
        Assert.True(state.HasContext);
        Assert.Null(state.Plan);
        Assert.Equal("检测 Java 失败：超时。请到「设置 → Java」手动指定。", state.Summary);
    }

    [Fact]
    public async Task 清空后回到初态且不再显示入口()
    {
        var state = CreateState(out _);
        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));
        await InstallAsync(state, major: 25);

        state.Reset();

        Assert.False(state.HasContext);
        Assert.Equal(JavaInstallPhase.Idle, state.Phase);
        Assert.Null(state.InstallMajor);
        Assert.Null(state.InstalledMajor);
        Assert.Null(state.StatusText);
        Assert.Null(state.Summary);
        Assert.Equal(0, state.Ratio);
    }

    // ==================== 失败与重试 ====================

    [Fact]
    public async Task 下载返回空路径时给失败文案且可以重试()
    {
        var state = CreateState(out var java);
        java.DownloadResult = null;
        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));

        var result = await InstallAsync(state, major: 25);

        Assert.Equal(JavaInstallOutcome.DownloadFailed, result.Outcome);
        Assert.Equal(JavaInstallPhase.Failed, state.Phase);
        Assert.False(state.IsInstalled);
        Assert.Equal("Java 25 下载失败，请检查网络或代理后重试。", state.StatusText);

        // 失败不能卡死：按钮要能再点一次。
        Assert.True(state.CanStart);
    }

    [Fact]
    public async Task 异常时状态行带异常消息且可以重试()
    {
        var state = CreateState(out var java);
        java.DownloadThrows = new InvalidOperationException("解压失败");
        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));

        var result = await InstallAsync(state, major: 25);

        Assert.Equal(JavaInstallOutcome.Failed, result.Outcome);
        Assert.Equal("Java 25 安装失败：解压失败", state.StatusText);
        Assert.Equal("解压失败", result.Error);
        Assert.True(state.CanStart);

        // 重试一次，这次成功——失败后必须能重新走到完成态。
        java.DownloadThrows = null;
        var retry = await InstallAsync(state, major: 25);

        Assert.True(retry.Succeeded);
        Assert.True(state.IsInstalled);
    }

    [Fact]
    public async Task 取消时按版本页口径给文案且可以重试()
    {
        var state = CreateState(out var java, JavaInstallWording.Versions);
        java.DownloadThrows = new OperationCanceledException();
        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));

        var result = await InstallAsync(state, major: 25);

        Assert.Equal(JavaInstallOutcome.Canceled, result.Outcome);
        Assert.Equal(JavaInstallPhase.Canceled, state.Phase);
        Assert.Equal("已取消下载。", state.StatusText);
        Assert.True(state.CanStart);
    }

    [Fact]
    public async Task 服务未接入时返回不可用而不是抛异常()
    {
        var state = new JavaInstallState(java: null, JavaInstallWording.Home);

        var result = await state.InstallAsync(25, JavaInstallSink.Draft(_ => { }));

        Assert.Equal(JavaInstallOutcome.ServiceUnavailable, result.Outcome);
        Assert.Equal(JavaInstallPhase.Idle, state.Phase);
    }

    // ==================== 写回顺序 ====================

    [Fact]
    public async Task 写回_保存_强制重扫顺序固定()
    {
        var calls = new List<string>();
        var java = new RecordingJavaService(calls);
        var state = new JavaInstallState(java, JavaInstallWording.Home);
        state.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));

        // 用「记录顺序」的落点取代真实的 settings.json：写回 → 保存两步必须分开可见。
        var sink = new JavaInstallSink(
            path => calls.Add($"write:{path}"),
            _ =>
            {
                calls.Add("save");
                return Task.CompletedTask;
            });

        await state.InstallAsync(25, sink);

        Assert.Equal(SettingsOrder, calls);
    }

    [Fact]
    public async Task 设置页草稿落点不落盘_但仍强制重扫()
    {
        var calls = new List<string>();
        var java = new RecordingJavaService(calls);
        var state = new JavaInstallState(java, JavaInstallWording.Settings);

        string? draft = null;
        var result = await state.InstallAsync(25, JavaInstallSink.Draft(path => draft = path));

        // 设置页只写回页面草稿：落盘由「保存设置」决定，安装流程不得替用户保存。
        Assert.Equal(DownloadedPath, draft);
        Assert.Equal(DraftOrder, calls);
        Assert.True(result.Succeeded);
        Assert.Equal(@"Java 25 已安装：C:\fake\runtime\bin\javaw.exe", state.StatusText);
    }

    [Fact]
    public async Task 下载失败时不写回也不重扫()
    {
        var calls = new List<string>();
        var java = new RecordingJavaService(calls) { DownloadResult = null };
        var state = new JavaInstallState(java, JavaInstallWording.Home);

        await state.InstallAsync(25, JavaInstallSink.Draft(_ => calls.Add("write")));

        Assert.Equal(DownloadOnlyOrder, calls);
    }

    [Fact]
    public async Task 并发安装只执行一次()
    {
        var calls = new List<string>();
        var java = new RecordingJavaService(calls);
        var state = new JavaInstallState(java, JavaInstallWording.Home);

        // 把下载挂在半途，才有「第一个还没结束」的窗口去发第二次。
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        java.Pending = gate;

        var first = state.InstallAsync(25, JavaInstallSink.Draft(_ => { }));
        var second = await state.InstallAsync(25, JavaInstallSink.Draft(_ => { }));

        Assert.Equal(JavaInstallOutcome.Busy, second.Outcome);

        gate.SetResult(@"C:\fake\runtime\bin\javaw.exe");
        Assert.True((await first).Succeeded);
        Assert.Single(calls.FindAll(c => c.StartsWith("download:", StringComparison.Ordinal)));
    }

    // ==================== 进度比例 ====================

    [Theory]
    [InlineData(0.0, 0.0, "0%")]
    [InlineData(0.42, 0.42, "42%")]
    [InlineData(1.0, 1.0, "100%")]
    [InlineData(1.7, 1.0, "100%")]
    [InlineData(-0.2, 0.0, "0%")]
    public void 进度比例被夹在零到一(double reported, double expected, string percent)
    {
        var state = CreateState(out _);

        state.ApplyProgress(Progress(reported));

        Assert.Equal(expected, state.Ratio);
        Assert.Equal(percent, state.PercentText);
    }

    [Fact]
    public void 非有限比例落到零而不是NaN()
    {
        var state = CreateState(out _);

        state.ApplyProgress(Progress(double.NaN));
        Assert.Equal(0, state.Ratio);
        Assert.Equal("0%", state.PercentText);

        state.ApplyProgress(Progress(double.PositiveInfinity));
        Assert.Equal(0, state.Ratio);
    }

    // ==================== 文案口径（三处逐字保留） ====================

    [Fact]
    public void 三处文案口径逐字不同()
    {
        // 首页：失败句带「Java N」前缀，取消句带句号。
        Assert.Equal("Java 25 下载失败，请检查网络或代理后重试。", JavaInstallWording.Home.DownloadFailed(25));
        Assert.Equal("下载已取消。", JavaInstallWording.Home.Canceled);
        Assert.Equal("已安装 Java 25：C:\\x\\javaw.exe", JavaInstallWording.Home.Succeeded(25, "C:\\x\\javaw.exe"));

        // 版本页：失败句不带前缀，取消句是「已取消下载。」。
        Assert.Equal("下载失败，请检查网络或代理后重试。", JavaInstallWording.Versions.DownloadFailed(25));
        Assert.Equal("已取消下载。", JavaInstallWording.Versions.Canceled);
        Assert.Equal("Java 25 已就绪：C:\\x\\javaw.exe", JavaInstallWording.Versions.Succeeded(25, "C:\\x\\javaw.exe"));

        // 设置页：状态行只写短句，长句在页面的错误行里。
        Assert.Equal("下载失败", JavaInstallWording.Settings.DownloadFailed(25));
        Assert.Equal("安装失败", JavaInstallWording.Settings.Failed(25, "解压失败"));
        Assert.Equal("下载已取消", JavaInstallWording.Settings.Canceled);
        Assert.Equal("Java 25 已安装：C:\\x\\javaw.exe", JavaInstallWording.Settings.Succeeded(25, "C:\\x\\javaw.exe"));

        // 开始文案三处一致（设置页的 {Label} 恰好就是「Java N」）。
        Assert.Equal("正在下载 Java 25…", JavaInstallWording.Home.Downloading(25));
        Assert.Equal("正在下载 Java 25…", JavaInstallWording.Versions.Downloading(25));
        Assert.Equal("正在下载 Java 25…", JavaInstallWording.Settings.Downloading(25));
    }

    // ==================== 首页 / 版本页各持一个实例 ====================
    //
    // 两个页面在各自构造里 new 一个（HomeViewModel / VersionsViewModel），而不是共享单例。
    // 这里不去构造页面对象：VersionsViewModel 的静态初始化会碰 Avalonia 画笔（InstanceIcons），
    // 按 CONVENTIONS §7.1 那类测试不该存在。例外的只是「两份状态互不覆盖」这条行为本身。

    [Fact]
    public void 两个安装状态实例互不覆盖()
    {
        using var home = new JavaInstallState(java: null, JavaInstallWording.Home);
        using var versions = new JavaInstallState(java: null, JavaInstallWording.Versions);

        home.Begin(25, JavaProvisionPlanner.Plan(25, NoJava));
        versions.Begin(17, JavaProvisionPlanner.Plan(17, NoJava));
        home.ApplyProgress(Progress(0.5));

        Assert.Equal(25, home.RequiredMajor);
        Assert.Equal(0.5, home.Ratio);
        Assert.Equal(17, versions.RequiredMajor);
        Assert.Equal(0, versions.Ratio);
    }

    // ==================== 测试替身 ====================

    private static JavaInstallState CreateState(out RecordingJavaService java, JavaInstallWording? wording = null)
    {
        java = new RecordingJavaService(new List<string>());
        return new JavaInstallState(java, wording ?? JavaInstallWording.Home);
    }

    private static Task<JavaInstallResult> InstallAsync(JavaInstallState state, int major) =>
        state.InstallAsync(major, JavaInstallSink.Draft(_ => { }));

    private static DownloadProgress Progress(double ratio) =>
        new(1, 0, 0, 1024, 0, 0, "x", ratio);

    /// <summary>
    /// 只会被这些用例用到的 <see cref="IJavaService"/>：记录调用顺序，其余成员直接抛
    /// ——比起返回假值，抛能更早发现「状态机偷偷多调了一次探测」。
    /// </summary>
    private sealed class RecordingJavaService : IJavaService
    {
        private readonly List<string> _calls;

        public RecordingJavaService(List<string> calls) => _calls = calls;

        /// <summary>下载返回的路径；null 表示下载失败。</summary>
        public string? DownloadResult { get; set; } = @"C:\fake\runtime\bin\javaw.exe";

        /// <summary>下载时抛的异常；null 表示正常返回。</summary>
        public Exception? DownloadThrows { get; set; }

        /// <summary>进入下载那一刻的回调，用来断言「先置安装中再下载」。</summary>
        public Action? OnDownload { get; set; }

        /// <summary>非 null 时下载返回这个未完成的 Task，用来制造「下载仍在进行」的窗口。</summary>
        public TaskCompletionSource<string?>? Pending { get; set; }

        public Task<IReadOnlyList<JavaInfo>> DetectAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            _calls.Add(forceRefresh ? "detect:refresh" : "detect:cached");
            return Task.FromResult<IReadOnlyList<JavaInfo>>(NoJava);
        }

        public Task<string?> DownloadRuntimeAsync(
            int majorVersion,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _calls.Add($"download:{majorVersion}");
            OnDownload?.Invoke();

            if (DownloadThrows is not null)
            {
                throw DownloadThrows;
            }

            return Pending?.Task ?? Task.FromResult(DownloadResult);
        }

        public Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(nameof(ProbeAsync));

        public JavaRequirement? GetRequirement(string minecraftVersion) =>
            throw new NotSupportedException(nameof(GetRequirement));

        public (int Min, int Max)? GetAllowedRange(string minecraftVersion, LoaderKind loader) =>
            throw new NotSupportedException(nameof(GetAllowedRange));

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(nameof(ResolveForVersionAsync));

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            LoaderKind loader,
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(nameof(ResolveForVersionAsync));

        public Task<string?> InstallRuntimeAsync(
            JavaRuntimePackage package,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(nameof(InstallRuntimeAsync));
    }
}