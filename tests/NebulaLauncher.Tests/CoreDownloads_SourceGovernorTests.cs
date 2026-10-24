using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 三档下载源（自动 / 官方 / 镜像）与会话级劣化状态机。
///
/// 三块判据：
/// 1. 设置的读写：三档合法值、历史值 <c>"BmclApi"</c> 仍读成镜像、保存时写新名字；
/// 2. <see cref="DownloadSourceGovernor"/>：三条降级判据、10 分钟窗口、手动档免疫、时间可注入；
/// 3. <see cref="DownloadMirror.Candidates"/>：不同首选源下的候选顺序。
/// </summary>
public class CoreDownloads_SourceGovernorTests
{
    private const string LibraryUrl =
        "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-arm64.jar";

    private const string MirroredLibraryUrl =
        "https://bmclapi2.bangbang93.com/maven/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-arm64.jar";

    /// <summary>可拨的时钟。所有「10 分钟窗口」判据都靠它推进，不等待真实时间。</summary>
    private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    // ================= 设置：三档 + 旧值兼容 =================

    [Theory]
    [InlineData("Auto", DownloadSource.Auto)]
    [InlineData("Official", DownloadSource.Official)]
    [InlineData("Mirror", DownloadSource.Mirror)]
    [InlineData("BmclApi", DownloadSource.Mirror)] // 历史 settings.json 里写的就是这个名字
    [InlineData("bmclapi", DownloadSource.Mirror)]
    [InlineData("official", DownloadSource.Official)]
    [InlineData("nonsense", DownloadSource.Mirror)]
    [InlineData("", DownloadSource.Mirror)]
    public async Task 下载源设置支持三档并且兼容历史值(string stored, DownloadSource expected)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        await File.WriteAllTextAsync(paths.SettingsFile, $"{{\"downloadSource\":\"{stored}\"}}");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();

        Assert.Equal(expected, service.Current.DownloadSource);
    }

    [Fact]
    public async Task 旧枚举的数值形式按旧表解释()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 旧枚举：0 = Official、1 = BmclApi。手改过配置的人可能留下数字。
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"downloadSource\":0}");

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();
        Assert.Equal(DownloadSource.Official, service.Current.DownloadSource);

        await File.WriteAllTextAsync(paths.SettingsFile, "{\"downloadSource\":1}");

        // LoadAsync 幂等（只加载一次），外部改写文件后要重读得用 ReloadAsync。
        await service.ReloadAsync();
        Assert.Equal(DownloadSource.Mirror, service.Current.DownloadSource);
    }

    [Fact]
    public async Task 保存时写出新名字而不是历史别名()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        var service = new SettingsService(paths, NullAppLog.Instance);
        await service.LoadAsync();
        service.Current.DownloadSource = DownloadSource.Mirror;
        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(paths.SettingsFile);
        Assert.Contains("\"downloadSource\": \"Mirror\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BmclApi", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 历史别名与镜像同值()
    {
        // 兼容的前提：旧调用点写 DownloadSource.BmclApi 与 Mirror 必须是同一个值，
        // 否则「设置页选镜像、别处判断 BmclApi」会静默不一致。
        Assert.Equal(DownloadSource.Mirror, DownloadSource.BmclApi);
    }

    // ================= Governor：自动模式 =================

    [Fact]
    public void 自动模式初始首选镜像是而不是自动()
    {
        var governor = NewGovernor();

        Assert.Equal(DownloadSource.Auto, governor.Strategy);
        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
        Assert.Null(governor.DegradedAtUtc);
    }

    [Fact]
    public void 镜像连续失败三次降级到官方()
    {
        var governor = NewGovernor();

        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));
        Assert.Equal(DownloadSource.Mirror, governor.Effective);

        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));

        Assert.Equal(DownloadSource.Official, governor.Effective);
        Assert.Equal("镜像连续失败 3 次", governor.DegradeReason);
        Assert.Equal(_now, governor.DegradedAtUtc);
    }

    [Fact]
    public void 镜像一次成功会打断连续失败计数()
    {
        var governor = NewGovernor();

        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));
        governor.Report(MirrorOk());
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));

        // 累计 4 次失败，但连续只有 2 次：不该降级。
        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
    }

    [Fact]
    public void 两次慢速对比样本触发降级()
    {
        var governor = NewGovernor();

        // 镜像撑了 3 秒才放弃，官方 0.5 秒拿到响应：差值 2.5 秒 > 2 秒阈值。
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(0.5)));
        Assert.Equal(DownloadSource.Mirror, governor.Effective);

        governor.Report(MirrorFailed(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(0.5)));

        Assert.Equal(DownloadSource.Official, governor.Effective);
        Assert.Equal("镜像比官方慢 2 秒以上 2 次", governor.DegradeReason);
    }

    [Fact]
    public void 慢速差值不到两秒不算慢速样本()
    {
        var governor = NewGovernor();

        // 差值 1.2 秒：失败计数只有 2 次、慢速样本 0 次，不该降级。
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(1.0)));
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(1.0)));

        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
    }

    [Fact]
    public void 降级后十分钟内不再尝试镜像()
    {
        var governor = NewGovernor();
        DegradeByFailures(governor);

        _now = _now.Add(TimeSpan.FromMinutes(9));
        Assert.Equal(DownloadSource.Official, governor.Effective);
        Assert.NotNull(governor.DegradeReason);

        // 窗口内即使又失败若干次也不改变状态（这段时间的请求本来就走官方）。
        governor.Report(MirrorFailed(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.1)));
        Assert.Equal(DownloadSource.Official, governor.Effective);
    }

    [Fact]
    public void 十分钟窗口过期后回到镜像优先()
    {
        var governor = NewGovernor();
        DegradeByFailures(governor);

        _now = _now.Add(TimeSpan.FromMinutes(10)).Add(TimeSpan.FromSeconds(1));

        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
        Assert.Null(governor.DegradedAtUtc);
    }

    [Fact]
    public void 手动官方档不受镜像失败影响()
    {
        var governor = NewGovernor(DownloadSource.Official);

        for (var i = 0; i < 5; i++)
        {
            governor.Report(MirrorFailed(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.1)));
        }

        Assert.Equal(DownloadSource.Official, governor.Effective);
        Assert.Null(governor.DegradeReason);
    }

    [Fact]
    public void 手动镜像档不会自动降级()
    {
        var governor = NewGovernor(DownloadSource.Mirror);

        for (var i = 0; i < 5; i++)
        {
            governor.Report(MirrorFailed(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.1)));
        }

        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
    }

    [Fact]
    public void 首选源是官方时不影响镜像判据()
    {
        var governor = NewGovernor();

        // 这些样本是官方首选（例如自动模式已降级后残留的在途请求），不该算作镜像失败。
        for (var i = 0; i < 5; i++)
        {
            governor.Report(OfficialFailed());
        }

        Assert.Equal(DownloadSource.Mirror, governor.Effective);
    }

    [Fact]
    public void 切换策略会清空本次会话的劣化状态()
    {
        var governor = NewGovernor();
        DegradeByFailures(governor);
        Assert.Equal(DownloadSource.Official, governor.Effective);

        governor.SetStrategy(DownloadSource.Mirror);
        governor.SetStrategy(DownloadSource.Auto);

        Assert.Equal(DownloadSource.Mirror, governor.Effective);
        Assert.Null(governor.DegradeReason);
    }

    [Fact]
    public void 降级时触发变更事件()
    {
        var governor = NewGovernor();
        var raised = 0;
        governor.Changed += (_, _) => raised++;

        DegradeByFailures(governor);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void 越界的枚举值按镜像优先处理()
    {
        var governor = NewGovernor((DownloadSource)99);

        Assert.Equal(DownloadSource.Mirror, governor.Strategy);
        Assert.Equal(DownloadSource.Mirror, governor.Effective);
    }

    // ================= 候选排序 =================

    [Fact]
    public void 自动模式未降级时候选镜像在前()
    {
        var candidates = DownloadMirror.Candidates(LibraryUrl, DownloadSource.Auto, DownloadSource.Mirror);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(MirroredLibraryUrl, candidates[0]);
        Assert.Equal(LibraryUrl, candidates[1]);
    }

    [Fact]
    public void 自动模式降级后候选官方在前()
    {
        var candidates = DownloadMirror.Candidates(LibraryUrl, DownloadSource.Auto, DownloadSource.Official);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(LibraryUrl, candidates[0]);
        Assert.Equal(MirroredLibraryUrl, candidates[1]);
    }

    [Fact]
    public void 自动模式的单参数重载默认镜像优先()
    {
        var candidates = DownloadMirror.Candidates(LibraryUrl, DownloadSource.Auto);

        Assert.Equal(MirroredLibraryUrl, candidates[0]);
    }

    [Fact]
    public void 手动档的候选顺序不受会话状态影响()
    {
        // preferred 传什么都不该改变手动档的方向：手动档是用户的明确选择。
        Assert.Equal(MirroredLibraryUrl, DownloadMirror.Candidates(LibraryUrl, DownloadSource.Mirror, DownloadSource.Official)[0]);
        Assert.Equal(LibraryUrl, DownloadMirror.Candidates(LibraryUrl, DownloadSource.Official, DownloadSource.Mirror)[0]);
    }

    [Fact]
    public void 官方档不重写下载地址()
    {
        Assert.Equal(LibraryUrl, DownloadMirror.Rewrite(LibraryUrl, DownloadSource.Official));
        Assert.False(DownloadMirror.PrefersMirror(DownloadSource.Official));
        Assert.True(DownloadMirror.PrefersMirror(DownloadSource.Mirror));
        Assert.True(DownloadMirror.PrefersMirror(DownloadSource.Auto));
    }

    [Fact]
    public void 不可重写的主机只有一个候选()
    {
        var candidates = DownloadMirror.Candidates("https://cdn.modrinth.com/data/abc.jar", DownloadSource.Auto, DownloadSource.Mirror);

        Assert.Single(candidates);
        Assert.Equal("https://cdn.modrinth.com/data/abc.jar", candidates[0]);
    }

    // ================= 辅助 =================

    private DownloadSourceGovernor NewGovernor(DownloadSource strategy = DownloadSource.Auto) =>
        new(() => _now, strategy);

    private static void DegradeByFailures(DownloadSourceGovernor governor)
    {
        for (var i = 0; i < DownloadSourceGovernor.MirrorFailureStreakThreshold; i++)
        {
            governor.Report(MirrorFailed(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.2)));
        }
    }

    private static DownloadAttemptResult MirrorFailed(TimeSpan mirrorElapsed, TimeSpan officialElapsed) => new()
    {
        PrimarySource = DownloadSource.Mirror,
        PrimarySucceeded = false,
        PrimaryElapsed = mirrorElapsed,
        FallbackUsed = true,
        FallbackElapsed = officialElapsed,
        Succeeded = true,
    };

    private static DownloadAttemptResult MirrorOk() => new()
    {
        PrimarySource = DownloadSource.Mirror,
        PrimarySucceeded = true,
        PrimaryElapsed = TimeSpan.FromSeconds(0.3),
        Succeeded = true,
    };

    private static DownloadAttemptResult OfficialFailed() => new()
    {
        PrimarySource = DownloadSource.Official,
        PrimarySucceeded = false,
        PrimaryElapsed = TimeSpan.FromSeconds(0.5),
        FallbackUsed = true,
        FallbackElapsed = TimeSpan.FromSeconds(0.2),
        Succeeded = true,
    };
}
