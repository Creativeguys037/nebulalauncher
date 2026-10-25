using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Tests;

/// <summary>
/// 版本清单求值的进程内串行化（票 27 缺陷 A）。
///
/// 判据是「同一时刻最多一个求值在跑」，而不是「跑完没有异常」：这个装饰器存在的唯一理由是
/// 库的 loader 会用 <c>File.Create</c> 重写共享的 <c>version_manifest_v2.json</c>，
/// 并发两个就撞 <c>ERROR_SHARING_VIOLATION</c>。所以「最大并发进入数」必须等于 1。
/// </summary>
public class CoreLaunch_VersionLoaderLockTests
{
    [Fact]
    public async Task 并发求值时同一时刻只有一个进入真实加载器()
    {
        var probe = new CountingLoader();
        var loader = new SerializedVersionLoader(probe);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => loader.GetVersionMetadatasAsync().AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, probe.MaxConcurrentEntries);
        Assert.Equal(8, probe.TotalEntries);
    }

    /// <summary>
    /// 对照实验：证明上一条用例的「最大并发 == 1」不是量具空转。
    /// 去掉装饰器后同样的 8 个并发确实会重叠——那正是库重写共享清单文件时撞锁的成因。
    /// </summary>
    [Fact]
    public async Task 不加装饰器时并发确实会重叠()
    {
        var probe = new CountingLoader();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => probe.GetVersionMetadatasAsync().AsTask()));

        Assert.True(
            probe.MaxConcurrentEntries > 1,
            $"量具失效：无装饰器时最大并发只有 {probe.MaxConcurrentEntries}，无法用它判定串行化");
        Assert.Equal(8, probe.TotalEntries);
    }

    [Fact]
    public async Task 求值抛异常后闸门仍然释放()
    {
        var loader = new SerializedVersionLoader(new ThrowingLoader());

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => loader.GetVersionMetadatasAsync().AsTask());
        }

        // 闸门是静态的：前三次失败若漏放，下面的求值会永久挂住。
        // 用 WhenAny 加超时而不是直接 await，否则这条用例在漏放时会变成卡死而不是失败。
        var probe = new CountingLoader();
        var after = new SerializedVersionLoader(probe).GetVersionMetadatasAsync().AsTask();
        var finished = await Task.WhenAny(after, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(after, finished);
        Assert.Equal(1, probe.TotalEntries);
    }

    [Fact]
    public async Task 取消令牌在排队时生效()
    {
        var blocking = new BlockingLoader();
        var loader = new SerializedVersionLoader(blocking);

        var first = loader.GetVersionMetadatasAsync().AsTask();

        using var cts = new CancellationTokenSource();
        var second = loader.GetVersionMetadatasAsync(cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        blocking.Release();
        await first;
    }

    /// <summary>
    /// 排队超时后这一次求值自己失败，而不是把四条链一起永久堵住。
    ///
    /// 判据取自 <see cref="SerializedVersionLoader"/> 的设计动机：静态闸门无上限时，
    /// 一次挂死的求值会让安装、启动补全、命令行构建、修复全部失去恢复能力——
    /// 那比原来偶发的 IOException 更重。这条用例不真的等两分钟，而是验证
    /// 「排队时取消令牌能把它救出来」这条同源的出口，以及超时确实抛出可读中文。
    /// </summary>
    [Fact]
    public async Task 闸门一直被占时排队方会超时而不是永久挂住()
    {
        var blocking = new BlockingLoader();
        var holder = new SerializedVersionLoader(blocking);
        var first = holder.GetVersionMetadatasAsync().AsTask();

        // 占住之后，第二个求值必须能靠取消令牌脱身（同一条等待路径）。
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var second = new SerializedVersionLoader(new CountingLoader())
            .GetVersionMetadatasAsync(cts.Token).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        blocking.Release();
        await first;
    }

    /// <summary>记录「同时进入」的最大值。进入后停一小会儿，让并发真正重叠。</summary>
    private sealed class CountingLoader : IVersionLoader
    {
        private int _current;
        private int _maxConcurrent;
        private int _total;

        public int MaxConcurrentEntries => Volatile.Read(ref _maxConcurrent);

        public int TotalEntries => Volatile.Read(ref _total);

        public async ValueTask<VersionMetadataCollection> GetVersionMetadatasAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _total);

            var current = Interlocked.Increment(ref _current);
            int observed;
            while (current > (observed = Volatile.Read(ref _maxConcurrent)))
            {
                Interlocked.CompareExchange(ref _maxConcurrent, current, observed);
            }

            try
            {
                // 真让出线程：没有串行化时这里必然重叠。
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                return new VersionMetadataCollection();
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class ThrowingLoader : IVersionLoader
    {
        public ValueTask<VersionMetadataCollection> GetVersionMetadatasAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("加载器坏了");
    }

    /// <summary>第一次求值一直占着闸门，直到外部放行。</summary>
    private sealed class BlockingLoader : IVersionLoader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async ValueTask<VersionMetadataCollection> GetVersionMetadatasAsync(
            CancellationToken cancellationToken = default)
        {
            await _gate.Task.ConfigureAwait(false);
            return new VersionMetadataCollection();
        }
    }
}
