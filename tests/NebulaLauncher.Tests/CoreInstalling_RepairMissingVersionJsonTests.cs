using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 修复路径遇到「版本描述缺失」时的行为（票 27 缺陷 E）。
///
/// 场景来源：整包导入时若没有同时安装游戏，<c>ModpackService</c> 刻意不写
/// <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>（写一个 inheritsFrom 为空的版本只会让版本页多一条损坏）。
/// 之后点「修复」，旧代码直接把它交给 CmlLib 的 InstallAsync，库只会抛
/// <c>KeyNotFoundException: Cannot find &lt;id&gt;</c>——用户看到「安装流程未能补齐缺失文件」
/// 却不知道缺的是什么，也不知道下一步该做什么。
///
/// 判据（可由外部观察）：报告里给出一条能照着做的中文说明，且没有把库的
/// KeyNotFoundException 文本透出来。
/// </summary>
public class CoreInstalling_RepairMissingVersionJsonTests
{
    private const string Version = "100WaysToDie";

    [Fact]
    public async Task 缺版本描述时Quick给出可读说明而不是KeyNotFound()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        // 只有目录，没有 <id>.json：正是导入整合包但没装游戏时的磁盘状态。
        Directory.CreateDirectory(Path.Combine(paths.VersionsDirectory, Version));

        using var client = new HttpClient(new NotFoundHandler());
        using var service = new InstallService(
            paths,
            new FakePlatformService(),
            new ExplodingJavaService(),
            client,
            NullAppLog.Instance);

        var report = await service.RepairAsync(Version, RepairDepth.Quick);

        var all = string.Join(" | ", report.Failed);
        Assert.NotEmpty(report.Failed);

        // 必须点名缺的是哪个文件，并给出下一步。
        Assert.Contains("版本描述", all, StringComparison.Ordinal);
        Assert.Contains(Version, all, StringComparison.Ordinal);
        Assert.Contains("导入", all, StringComparison.Ordinal);

        // 不许把库的内部表征透给用户。
        Assert.DoesNotContain("Cannot find", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyNotFound", all, StringComparison.OrdinalIgnoreCase);

        // 也不许把「什么都补不上」算成修复成功。
        Assert.Equal(0, report.Redownloaded);
    }

    [Fact]
    public async Task 缺版本描述时Deep也给出同一类说明()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        Directory.CreateDirectory(Path.Combine(paths.VersionsDirectory, Version));

        using var client = new HttpClient(new NotFoundHandler());
        using var service = new InstallService(
            paths,
            new FakePlatformService(),
            new ExplodingJavaService(),
            client,
            NullAppLog.Instance);

        var report = await service.RepairAsync(Version, RepairDepth.Deep);

        var all = string.Join(" | ", report.Failed);
        Assert.NotEmpty(report.Failed);
        Assert.Contains("版本描述", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot find", all, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>任何请求都 404：这些用例只关心「缺版本描述」这条本地判据。</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
