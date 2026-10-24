using NebulaLauncher.Core.Launching;
using NebulaLauncher.ViewModels;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 设置页「内存占用」三段条（缺口 G7）。
///
/// 只测纯逻辑与派生属性，不构造窗口：条是两条 <c>ProgressBar</c> 叠出来的
/// （上层画其它占用、底层画「已用 + 本实例」），所以需要钉住的是三个比例与超额判定，
/// 不是像素宽度。快照本身的算法在 <see cref="MemorySnapshot"/>，那边已有 10 条用例。
/// </summary>
public class AppSettings_MemoryBarTests
{
    private const long Gigabyte = 1024L * 1024L * 1024L;

    // ==================== 三段比例 ====================

    [Fact]
    public void 三段比例按快照与滑杆各自独立给出()
    {
        var vm = CreateViewModel();

        // 16 GB 上限、剩 10 GB → 已用 6 GB（37.5%）；堆 4 GB（25%）。
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(16 * Gigabyte, 10 * Gigabyte));
        vm.MaxMemoryMb = 4096;

        Assert.Equal(0.375, vm.MemoryUsedRatio, 6);
        Assert.Equal(0.25, vm.MemoryGameRatio, 6);
        Assert.Equal(0.625, vm.MemoryUsedPlusGameRatio, 6);

        // 剩余段 = 1 - 0.625，由进度条底色承担，不需要单独属性。
        Assert.Equal(0.375, 1d - vm.MemoryUsedPlusGameRatio, 6);
    }

    [Fact]
    public void 已用加本实例超过上限时比例夹到一()
    {
        var vm = CreateViewModel();
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(16 * Gigabyte, 10 * Gigabyte));

        vm.MaxMemoryMb = 16384;

        // 37.5% + 100% = 137.5%：进度条只能到 1，超出的部分由文字与配色表达。
        Assert.Equal(1d, vm.MemoryUsedPlusGameRatio, 6);
        Assert.True(vm.IsMemoryOverCommit);
    }

    [Fact]
    public void 拖动滑杆只改游戏段不重采快照()
    {
        var vm = CreateViewModel();
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(16 * Gigabyte, 10 * Gigabyte));

        vm.MaxMemoryMb = 4096;
        var totalText = vm.MemoryTotalText;
        var usedText = vm.MemoryUsedText;
        var usedRatio = vm.MemoryUsedRatio;
        var gameRatioAt4G = vm.MemoryGameRatio;

        vm.MaxMemoryMb = 8192;

        // 图例的「可分配 / 其它占用」来自快照，不跟着滑杆变。
        Assert.Equal(totalText, vm.MemoryTotalText);
        Assert.Equal(usedText, vm.MemoryUsedText);
        Assert.Equal(usedRatio, vm.MemoryUsedRatio, 6);

        // 游戏段翻倍，总比例同步上涨。
        Assert.Equal(gameRatioAt4G * 2, vm.MemoryGameRatio, 6);
        Assert.Equal(usedRatio + gameRatioAt4G * 2, vm.MemoryUsedPlusGameRatio, 6);
        Assert.Equal("8.0 GB", vm.MemoryGameText);
    }

    // ==================== 超额判定 ====================

    [Theory]
    [InlineData(4096, 16, 10, false)]  // 剩余 10 GB，堆 4 GB
    [InlineData(10240, 16, 10, false)] // 正好用满剩余 10 GB，不算超额
    [InlineData(11264, 16, 10, true)]  // 比剩余多 1 GB
    [InlineData(16384, 16, 4, true)]   // 堆等于整个上限，但已用 12 GB → 超
    [InlineData(2048, 16, 16, false)]  // 没占任何内存，2 GB 装得下
    public void 超额判定按剩余量比较(long gameMb, long totalGb, long availableGb, bool expected)
    {
        var snapshot = MemorySnapshot.Create(totalGb * Gigabyte, availableGb * Gigabyte);

        Assert.Equal(expected, SettingsViewModel.IsOverCommit(gameMb * 1024L * 1024L, snapshot));
    }

    [Fact]
    public void 堆大于上限时仍能判出超额()
    {
        // 回归：GameRatio 夹在 0..1，堆比上限还大时它也是 1；用比例和判定会退化成永远不超额。
        var snapshot = MemorySnapshot.Create(4 * Gigabyte, 4 * Gigabyte);

        Assert.Equal(1d, snapshot.GameRatio(8192), 6);
        Assert.True(SettingsViewModel.IsOverCommit(8192L * 1024L * 1024L, snapshot));
    }

    [Fact]
    public void 探测不到上限时不判超额()
    {
        var snapshot = MemorySnapshot.Create(0, 0);

        Assert.False(SettingsViewModel.IsOverCommit(16384L * 1024L * 1024L, snapshot));
    }

    [Fact]
    public void 超额提示写可分配内存不写本机内存()
    {
        var vm = CreateViewModel();
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(16 * Gigabyte, 10 * Gigabyte));

        vm.MaxMemoryMb = 16384;

        Assert.True(vm.IsMemoryOverCommit);
        Assert.Contains("可分配内存", vm.MemoryOverCommitHint);
        Assert.DoesNotContain("本机内存", vm.MemoryOverCommitHint);

        // 上限要出现在提示里，否则用户不知道该调到多少。
        Assert.Contains("16.0 GB", vm.MemoryOverCommitHint);
        Assert.Contains("无法启动", vm.MemoryOverCommitHint);
    }

    // ==================== 图例文案 ====================

    [Fact]
    public void 探测不到时图例显示未知而不是零GB()
    {
        var vm = CreateViewModel();
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(0, 0));

        Assert.False(vm.HasMemorySnapshot);
        Assert.Equal("未知", vm.MemoryTotalText);
        Assert.Equal("未知", vm.MemoryUsedText);

        // 堆大小来自滑杆，与探测结果无关，必须照常给出。
        vm.MaxMemoryMb = 6144;
        Assert.Equal("6.0 GB", vm.MemoryGameText);

        // 上限都不知道时不应该喊「超额」——那是在瞎猜。
        Assert.False(vm.IsMemoryOverCommit);
        Assert.Equal(0d, vm.MemoryUsedPlusGameRatio, 6);
    }

    [Fact]
    public void 其它占用为零时写零MB而不是未知()
    {
        var vm = CreateViewModel();

        // 刚启动时 TotalAvailableMemoryBytes - MemoryLoadBytes 常常正好等于上限 → 已用 0。
        // FormatGigabytes 把 0 说成「未知」（它的语义是「没探到」），这里必须纠正。
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(16 * Gigabyte, 16 * Gigabyte));

        Assert.True(vm.HasMemorySnapshot);
        Assert.Equal("16.0 GB", vm.MemoryTotalText);
        Assert.Equal("0 MB", vm.MemoryUsedText);
        Assert.Equal(0d, vm.MemoryUsedRatio, 6);
    }

    [Fact]
    public void 快照里的已用由上限减剩余推出()
    {
        var vm = CreateViewModel();
        vm.ApplyMemorySnapshot(MemorySnapshot.Create(16 * Gigabyte, 4 * Gigabyte));

        Assert.Equal(12 * Gigabyte, vm.Memory.UsedBytes);
        Assert.Equal(0.75, vm.MemoryUsedRatio, 6);
        Assert.Equal("12.0 GB", vm.MemoryUsedText);

        vm.MaxMemoryMb = 4096;
        Assert.False(vm.IsMemoryOverCommit);

        // 再多 1 GB 就越过上限。
        vm.MaxMemoryMb = 5120;
        Assert.True(vm.IsMemoryOverCommit);
    }

    [Fact]
    public void 快照为空时按零处理而不是抛异常()
    {
        var vm = CreateViewModel();

        vm.ApplyMemorySnapshot(null!);

        Assert.False(vm.HasMemorySnapshot);
        Assert.Equal("未知", vm.MemoryTotalText);
        Assert.Equal(0d, vm.MemoryUsedPlusGameRatio, 6);
    }

    private static SettingsViewModel CreateViewModel() => new();
}