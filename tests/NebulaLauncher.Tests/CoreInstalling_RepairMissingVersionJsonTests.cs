using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 修复路径遇到「版本描述缺失」时的行为（票 27 缺陷 E）。
///
/// 场景来源：整包导入时若没有同时安装游戏，<c>ModpackService</c> 刻意不写
/// <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>（写一个 inheritsFrom 为空的版本只会让版本页多一条损坏）。
///
/// 改造前两档各有一套失败形状：Quick 把它交给 CmlLib 的 InstallAsync，库抛
/// <c>KeyNotFoundException: Cannot find &lt;id&gt;</c>；Deep 由 ReadVersionChainAsync 抛
/// <c>FileNotFoundException</c>，冒泡成「修复中断：…」。同一件事两套文案，用户都看不出
/// 下一步做什么。现在判定统一在 RepairAsync 入口，两档共用一份说明。
///
/// 所以这两条用例**刻意断言同一组事实**——它证明的正是「两档已经同源」，
/// 任何一档退回自己的失败形状，对应用例就会变红。
/// </summary>
public class CoreInstalling_RepairMissingVersionJsonTests
{
    private const string Version = "100WaysToDie";

    [Theory]
    [InlineData(RepairDepth.Quick)]
    [InlineData(RepairDepth.Deep)]
    public async Task 缺版本描述时两档给出同一份可读说明(RepairDepth depth)
    {
        var report = await RepairMissingVersionAsync(depth);

        var all = string.Join(" | ", report.Failed);
        Assert.NotEmpty(report.Failed);

        // 必须点名缺的是哪个文件，并给出下一步。
        Assert.Contains("版本描述", all, StringComparison.Ordinal);
        Assert.Contains(Version, all, StringComparison.Ordinal);
        Assert.Contains("导入", all, StringComparison.Ordinal);

        // 不许把库的内部表征透给用户（Quick 档曾是 KeyNotFoundException）。
        Assert.DoesNotContain("Cannot find", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyNotFound", all, StringComparison.OrdinalIgnoreCase);

        // 也不许把「Deep 档读不到版本链」这件事包装成「修复中断」——那会让用户
        // 以为是操作被打断了，实际上什么都没开始做。
        Assert.DoesNotContain("修复中断", all, StringComparison.Ordinal);

        // 什么都没补上，就不能算修好了。
        Assert.Equal(0, report.Redownloaded);
        Assert.Equal(0, report.Missing);
    }

    [Fact]
    public async Task 缺版本描述时不进入补全流程所以不碰网络()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        Directory.CreateDirectory(Path.Combine(paths.VersionsDirectory, Version));

        var handler = new CountingFailHandler();
        using var client = new HttpClient(handler);
        using var service = new InstallService(
            paths,
            new FakePlatformService(),
            new ExplodingJavaService(),
            client,
            NullAppLog.Instance);

        await service.RepairAsync(Version, RepairDepth.Quick);

        // 判定发生在分档之前，所以连清单都不该去拉。
        Assert.Equal(0, handler.Requests);
    }

    private static async Task<RepairReport> RepairMissingVersionAsync(RepairDepth depth)
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

        return await service.RepairAsync(Version, depth);
    }

    /// <summary>任何请求都 404：这些用例只关心「缺版本描述」这条本地判据。</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    /// <summary>只数请求次数，一律失败。</summary>
    private sealed class CountingFailHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
