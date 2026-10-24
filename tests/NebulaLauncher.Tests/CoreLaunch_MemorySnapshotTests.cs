using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Tests;

/// <summary>
/// 内存快照的三段换算。这些数直接喂给设置页的进度条，错了会画出「负数宽度」或「永远满格」。
/// </summary>
public class CoreLaunch_MemorySnapshotTests
{
    private const long Gigabyte = 1024L * 1024L * 1024L;

    [Fact]
    public void 已用量由上限减剩余求差()
    {
        var snapshot = MemorySnapshot.Create(totalBytes: 16 * Gigabyte, availableBytes: 6 * Gigabyte);

        Assert.Equal(10 * Gigabyte, snapshot.UsedBytes);
        Assert.True(snapshot.HasValue);
    }

    [Fact]
    public void 剩余量大于上限时按上限夹住不产生负的已用量()
    {
        // 两次探测之间可能有释放，理论上剩余量能临时超过上限；负数已用量会画出反方向的段。
        var snapshot = MemorySnapshot.Create(totalBytes: 8 * Gigabyte, availableBytes: 20 * Gigabyte);

        Assert.Equal(0, snapshot.UsedBytes);
        Assert.Equal(0d, snapshot.UsedRatio);
    }

    [Fact]
    public void 探测不到上限时HasValue为false且比例为零()
    {
        var snapshot = MemorySnapshot.Create(totalBytes: 0, availableBytes: 0);

        Assert.False(snapshot.HasValue);
        Assert.Equal(0d, snapshot.UsedRatio);
        Assert.Equal(0d, snapshot.GameRatio(4096));
    }

    [Fact]
    public void 游戏堆比例按上限折算并夹在一以内()
    {
        var snapshot = MemorySnapshot.Create(totalBytes: 8 * Gigabyte, availableBytes: 8 * Gigabyte);

        Assert.Equal(0.5d, snapshot.GameRatio(4096), precision: 6);

        // 用户把 -Xmx 设成超过上限：比例夹在 1，条画满。不夹会算出 1.5 的宽度。
        Assert.Equal(1d, snapshot.GameRatio(12288));
    }

    [Fact]
    public void 负数或零的游戏堆比例为零且不参与绘制()
    {
        var snapshot = MemorySnapshot.Create(totalBytes: 8 * Gigabyte, availableBytes: 4 * Gigabyte);

        Assert.Equal(0d, snapshot.GameRatio(0));
        Assert.Equal(0d, snapshot.GameRatio(-1));
    }

    [Fact]
    public void 已用比例恒在零到一之间()
    {
        var half = MemorySnapshot.Create(totalBytes: 4 * Gigabyte, availableBytes: 2 * Gigabyte);

        Assert.Equal(0.5d, half.UsedRatio, precision: 6);
        Assert.InRange(half.UsedRatio, 0d, 1d);
    }

    [Fact]
    public void 格式化一GB以上给一位小数的GB()
    {
        Assert.Equal("1.9 GB", MemorySnapshot.FormatGigabytes((long)(1.9d * Gigabyte)));
        Assert.Equal("16.0 GB", MemorySnapshot.FormatGigabytes(16 * Gigabyte));
    }

    [Fact]
    public void 格式化不足一GB时给MB()
    {
        var text = MemorySnapshot.FormatGigabytes(410L * 1024 * 1024);

        Assert.Contains("MB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 探测不到时格式化为未知而不是零GB()
    {
        // 「0 GB」会让用户以为内存条没插上；「未知」才是实情。
        Assert.Equal("未知", MemorySnapshot.FormatGigabytes(0));
        Assert.Equal("未知", MemorySnapshot.FormatGigabytes(-1));
    }

    [Fact]
    public void 真实采集要么给值要么明确无值绝不抛异常()
    {
        var snapshot = MemorySnapshot.Read();

        // 容器 / 受限沙箱里 HasValue 可能是 false，这不算失败；关键是不能抛、比例不能是 NaN。
        Assert.False(double.IsNaN(snapshot.UsedRatio));
        Assert.InRange(snapshot.UsedRatio, 0d, 1d);

        if (snapshot.HasValue)
        {
            Assert.True(snapshot.TotalBytes > 0);
            Assert.InRange(snapshot.AvailableBytes, 0, snapshot.TotalBytes);
        }
    }
}