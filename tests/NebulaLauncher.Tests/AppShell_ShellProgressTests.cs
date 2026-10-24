using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Services;

namespace NebulaLauncher.Tests;

/// <summary>
/// 任务栏进度接线（<see cref="ShellProgress"/>）的核心判据：值映射、节流、清除、平台调用。
///
/// 不测真实任务栏：<c>ITaskbarList3</c> 在无头环境不可见，而且这一层的职责只是
/// 「把合法的值按节流规则交给平台服务」。平台服务内部怎么写句柄属于 Core 的实现细节，
/// 由 <c>FakePlatformService</c> 之类的替身从外部验证即可。
/// </summary>
public class AppShell_ShellProgressTests
{
    private static readonly IntPtr Handle = new(0x1234);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-2.0)]
    [InlineData(1.5)]
    public void 非法值映射为空(double ratio)
    {
        Assert.Null(ShellProgress.Normalize(ratio));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void 零到一原样返回(double ratio)
    {
        Assert.Equal(ratio, ShellProgress.Normalize(ratio));
    }

    [Fact]
    public void 负一代表清除()
    {
        Assert.Equal(ShellProgress.ClearRatio, ShellProgress.Normalize(-1));
        Assert.Equal(-1.0, ShellProgress.Normalize(ShellProgress.ClearRatio));
    }

    [Fact]
    public void 节流_变化过小时丢弃()
    {
        // 变化 0.005 小于 MinDelta。
        Assert.False(ShellProgress.ShouldReport(0.500, 1_000, 0.505, 2_000));

        // 变化 0.02 足够大，且间隔也够。
        Assert.True(ShellProgress.ShouldReport(0.500, 1_000, 0.520, 2_000));
    }

    [Fact]
    public void 节流_间隔过短时丢弃()
    {
        Assert.False(ShellProgress.ShouldReport(0.5, 1_000, 0.6, 1_099));
        Assert.True(ShellProgress.ShouldReport(0.5, 1_000, 0.6, 1_100));
    }

    [Fact]
    public void 节流_首次与时钟回退都放行()
    {
        Assert.True(ShellProgress.ShouldReport(double.NaN, -1, 0.3, 0));
        Assert.True(ShellProgress.ShouldReport(0.5, 1_000, 0.9, 900));
    }

    [Fact]
    public void 未挂接时不上报()
    {
        ShellProgress.Detach();

        Assert.False(ShellProgress.IsSupported);
        Assert.False(ShellProgress.Report(0.5));
        Assert.False(ShellProgress.Report(double.NaN));
    }

    [Fact]
    public void 上报把合法值交给平台服务()
    {
        using var session = new Session();

        Assert.True(ShellProgress.IsSupported);
        Assert.True(ShellProgress.Report(0.42));

        var call = Assert.Single(session.Platform.Calls);
        Assert.Equal(Handle, call.Handle);
        Assert.Equal(0.42, call.Progress, 3);
    }

    [Fact]
    public void 非法值不会调到平台服务()
    {
        using var session = new Session();

        Assert.False(ShellProgress.Report(double.NaN));
        Assert.False(ShellProgress.Report(double.PositiveInfinity));
        Assert.False(ShellProgress.Report(1.5));
        Assert.False(ShellProgress.Report(-2));

        Assert.Empty(session.Platform.Calls);
    }

    [Fact]
    public void 连续上报被节流()
    {
        using var session = new Session();

        Assert.True(ShellProgress.Report(0.10));       // 首次放行

        session.Clock.Advance(50);
        Assert.False(ShellProgress.Report(0.20));      // 间隔不足

        session.Clock.Advance(60);                     // 累计 110ms
        Assert.True(ShellProgress.Report(0.30));       // 间隔与变化都满足

        session.Clock.Advance(100);
        Assert.False(ShellProgress.Report(0.305));     // 变化 0.005

        Assert.Equal(2, session.Platform.Calls.Count);
        Assert.Equal(0.10, session.Platform.Calls[0].Progress, 3);
        Assert.Equal(0.30, session.Platform.Calls[1].Progress, 3);
    }

    [Fact]
    public void 清除不受节流限制且传负一()
    {
        using var session = new Session();

        Assert.True(ShellProgress.Report(0.5));

        // 同一时刻再报一个差异很大的值：间隔不足，被丢弃。
        Assert.False(ShellProgress.Report(0.9));

        session.Platform.Calls.Clear();

        // 清除永远放行，连续两次都能到达平台（幂等）。
        ShellProgress.Clear();
        ShellProgress.Clear();

        Assert.Equal(2, session.Platform.Calls.Count);
        Assert.All(session.Platform.Calls, call => Assert.Equal(-1.0, call.Progress, 3));
    }

    [Fact]
    public void 挂接新窗口时重置节流状态()
    {
        using var session = new Session();

        Assert.True(ShellProgress.Report(0.5));

        // 同一个值，但重新挂接后节流状态清零，第二次上报必须放行——
        // 否则窗口重建后第一条进度会被当成「没变化」丢掉。
        ShellProgress.Attach(session.Platform, Handle, () => session.Clock.Now);
        Assert.True(ShellProgress.Report(0.5));
    }

    [Fact]
    public void 平台抛异常不影响调用方()
    {
        using var session = new Session { ThrowOnSet = true };

        Assert.False(ShellProgress.Report(0.5));
        Assert.True(ShellProgress.IsSupported);
    }

    /// <summary>每个用例一套独立的平台替身与假时钟，Dispose 时解挂。</summary>
    private sealed class Session : IDisposable
    {
        public Session()
        {
            ShellProgress.Attach(Platform, Handle, () => Clock.Now);
        }

        public RecordingPlatform Platform { get; } = new();

        public FakeClock Clock { get; } = new();

        public bool ThrowOnSet
        {
            init => Platform.ThrowOnSet = value;
        }

        public void Dispose() => ShellProgress.Detach();
    }

    private sealed class FakeClock
    {
        public long Now { get; private set; }

        public void Advance(long milliseconds) => Now += milliseconds;
    }

    /// <summary>记录每次 <c>SetTaskbarProgress</c> 调用。</summary>
    private sealed class RecordingPlatform : IPlatformService
    {
        public List<(IntPtr Handle, double Progress)> Calls { get; } = [];

        public bool ThrowOnSet { get; set; }

        public string PlatformName => "Windows";

        public bool SupportsWindowMaterial => false;

        public bool SupportsSecureTokenStorage => false;

        public string DefaultMinecraftDirectory => Path.GetTempPath();

        public IReadOnlyList<string> JavaSearchRoots => Array.Empty<string>();

        public string JavaExecutableName => "java.exe";

        public Task OpenFolderAsync(string path) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task RevealInFileManagerAsync(string path) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;

        public void ApplyWindowMaterial(IntPtr windowHandle, string material)
        {
        }

        public void SetTaskbarProgress(IntPtr windowHandle, double progress)
        {
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("COM 不可用");
            }

            Calls.Add((windowHandle, progress));
        }

        public IDisposable? AcquireSingleInstanceLock(string name) => null;
    }
}
