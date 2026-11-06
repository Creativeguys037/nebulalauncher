using System;
using NebulaLauncher.Services;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 动效服务（票 #82 的辅助接缝）的行为测试。
///
/// 为什么要有这组测试：<c>Motion.Enabled=false</c> 是「减少动态效果」偏好的唯一开关，而它只作用在
/// <c>Dur()</c> 上。静态契约测试（AppTheme_TokenContractTests）只能证明「页面引用了 Motion」，
/// 证明不了「关掉之后拿到的真的是 0」——一旦有人把 <c>Dur()</c> 简化成直接返回入参，
/// 关闭动效就静默失效，而所有截图和构建都照样是绿的。
///
/// 这里只断言外部可观察的结果：关闭时四个档位都归零，打开后原值一字不差地回来。
/// <c>Enabled</c> 是进程级静态状态，所以整段包在 try/finally 里恢复原值——断言失败时也不会
/// 把开关留在关闭态污染后续用例；tools/TestRunner 是串行跑用例，这里不需要额外的并行隔离。
///
/// 触碰边界：本类只命名 <c>Motion</c> 与 <see cref="TimeSpan"/>。访问 <c>Motion</c> 会触发它的静态构造，
/// 里面会 <c>new SplineEasing(...)</c>——SplineEasing 是纯数学类型、不依赖 Application.Current，
/// 不属于 CONVENTIONS §8.2 禁的 Window / 控件 / Dispatcher，可以安全构造。
/// </summary>
public class AppTheme_MotionTests
{
    [Fact]
    public void Dur_ZeroesWhenDisabled_AndRestoresTheOriginalValueWhenEnabled()
    {
        var original = Motion.Enabled;
        try
        {
            Motion.Enabled = false;
            Assert.Equal(TimeSpan.Zero, Motion.Dur(Motion.Instant));
            Assert.Equal(TimeSpan.Zero, Motion.Dur(Motion.Fast));
            Assert.Equal(TimeSpan.Zero, Motion.Dur(Motion.Normal));
            Assert.Equal(TimeSpan.Zero, Motion.Dur(Motion.Slow));

            // 打开后四个档位必须原样透出：Dur 是「唯一来源」，不能顺手换成别的时长。
            Motion.Enabled = true;
            Assert.Equal(TimeSpan.FromMilliseconds(90), Motion.Dur(Motion.Instant));
            Assert.Equal(TimeSpan.FromMilliseconds(150), Motion.Dur(Motion.Fast));
            Assert.Equal(TimeSpan.FromMilliseconds(220), Motion.Dur(Motion.Normal));
            Assert.Equal(TimeSpan.FromMilliseconds(320), Motion.Dur(Motion.Slow));
        }
        finally
        {
            Motion.Enabled = original;
        }
    }
}