using NebulaLauncher.Core.Instances;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例「启动调优」的判定规则（票 #88）。
///
/// 为什么这组规则必须有测试：它们在界面上只表现为「一行红字」或「一行灰字」，
/// 写错了既不会抛异常也不会让编译报错。而它们直接决定「非法值会不会写盘」——
/// 一条把 0 判成合法的规则，会让用户保存出一个永远起不来的实例，
/// 而错误要等到下次点启动才暴露。
///
/// 另一条硬约束是「不丢输入」：所有失败分支都必须是「判非法 + 保留原文」，
/// 而不是「清零 + 报错」。所以这里逐条钉住返回值，不依赖界面的行为。
/// </summary>
public class CoreInstances_LaunchTuningTests
{
    // ==================== 内存：合法输入 ====================

    [Theory]
    [InlineData("4096", 4096)]
    [InlineData("  4096  ", 4096)]
    [InlineData("512", 512)]
    [InlineData("16384", 16384)]
    public void 合法内存值被解析并通过校验(string text, int expected)
    {
        var result = InstanceLaunchTuning.ParseMemory(text, 16384);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(expected, result.MemoryMb);
        Assert.Null(result.Error);
    }

    [Fact]
    public void 前导零能解析成同一个数()
    {
        // "04096" 与 "4096" 是同一个值：解析走纯数字路径，不因为它不是规范写法就判非法。
        var result = InstanceLaunchTuning.ParseMemory("04096", 16384);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(4096, result.MemoryMb);
    }

    [Fact]
    public void 探测不到上限时回落到兜底上限()
    {
        var result = InstanceLaunchTuning.ParseMemory("16384", upperBoundMb: 0);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(16384, result.MemoryMb);
    }

    [Fact]
    public void 探测到更大上限时允许填更大的值()
    {
        // 32 GB 机器上 16384 不该被判超限以外的任何问题：上限来自探测值而不是常数。
        var result = InstanceLaunchTuning.ParseMemory("24576", upperBoundMb: 24576);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(24576, result.MemoryMb);
    }

    // ==================== 内存：三种失败必须能区分 ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(new object?[] { null })]
    public void 空输入报请填写而不是超限(string? text)
    {
        var result = InstanceLaunchTuning.ParseMemory(text, 16384);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);

        // 空输入的红字不能是区间相关的：那会把用户误导到完全相反的方向。
        Assert.DoesNotContain("不能超过", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("不能小于", result.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("4G")]
    [InlineData("4,096")]
    [InlineData("+4096")]
    [InlineData("-4096")]
    [InlineData("4096.5")]
    [InlineData("4096MB")]
    [InlineData("１２３４")]
    public void 非纯整数输入被拒绝且给出只填整数提示(string text)
    {
        var result = InstanceLaunchTuning.ParseMemory(text, 16384);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("整数", result.Error!, StringComparison.Ordinal);

        // 关键：解析失败时不能给出一个看似可用的数值，否则界面可能拿它去写盘。
        Assert.Equal(0, result.MemoryMb);
    }

    [Theory]
    [InlineData("511")]
    [InlineData("1")]
    [InlineData("0")]
    public void 低于下限的内存被拒并说明下限(string text)
    {
        var result = InstanceLaunchTuning.ParseMemory(text, 16384);

        Assert.False(result.IsValid);
        Assert.Contains(
            InstanceLaunchTuning.MinMemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Error!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 超过上限的内存被拒并说明上限()
    {
        var result = InstanceLaunchTuning.ParseMemory("8192", upperBoundMb: 4096);

        Assert.False(result.IsValid);
        Assert.Contains("4096", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 超长输入被拒而不是溢出成负数()
    {
        // 9 位以上既不是用户手输的常规值，也会在 int.Parse 里溢出。
        var result = InstanceLaunchTuning.ParseMemory("99999999999", 16384);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    // ==================== 上限与夹取 ====================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10000)]
    public void 探测失败的上限回落到兜底值(int detected)
    {
        Assert.Equal(
            InstanceLaunchTuning.FallbackMaxMemoryMb,
            InstanceLaunchTuning.EffectiveUpperBound(detected));
    }

    [Fact]
    public void 上限永远不低于下限()
    {
        // 探测到一个比下限还小的上限（受限容器）时，不能让区间变成空集——
        // 那会让任何输入都被判非法，用户根本存不进去。
        var upper = InstanceLaunchTuning.EffectiveUpperBound(128);

        Assert.True(upper >= InstanceLaunchTuning.MinMemoryMb, $"上限 {upper} 低于下限");
    }

    [Theory]
    [InlineData(100, 512)]
    [InlineData(512, 512)]
    [InlineData(4096, 4096)]
    [InlineData(99999, 8192)]
    public void 回填时旧值被夹回合法区间(int stored, int expected)
    {
        Assert.Equal(expected, InstanceLaunchTuning.ClampMemory(stored, 8192));
    }

    // ==================== Java 路径 ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(new object?[] { null })]
    public void 空Java路径表示自动选择而不算错误(string? path)
    {
        // 空 = 跟随自动选择，详情面板会存成 null。把它判成错误会让用户无法"清空"这个字段。
        var error = InstanceLaunchTuning.ValidateJavaPath(path, _ => null);

        Assert.Null(error);
    }

    [Fact]
    public void Java路径能被展开时判为合法()
    {
        const string Path = @"C:\Java\jdk-17\bin\java.exe";

        var error = InstanceLaunchTuning.ValidateJavaPath(
            Path,
            value => string.Equals(value, Path, StringComparison.Ordinal) ? value : null);

        Assert.Null(error);
    }

    [Fact]
    public void Java路径展开失败时就地给出原因()
    {
        var error = InstanceLaunchTuning.ValidateJavaPath(@"C:\不存在\java.exe", _ => null);

        Assert.NotNull(error);
        Assert.Contains("java", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Java路径校验会把首尾空白去掉再判定()
    {
        string? seen = null;

        var error = InstanceLaunchTuning.ValidateJavaPath(
            "  C:\\Java\\bin\\java.exe  ",
            value =>
            {
                seen = value;
                return value;
            });

        Assert.Null(error);
        Assert.Equal(@"C:\Java\bin\java.exe", seen);
    }

    // ==================== 摘要文案 ====================

    [Fact]
    public void 实例内存摘要说明是实例值()
    {
        Assert.Equal("6144 MB", InstanceLaunchTuning.DescribeMemory(6144, 4096));
    }

    [Fact]
    public void 跟随全局时内存摘要要说出全局值()
    {
        // 只显示"跟随全局"会让用户不知道实际会分配多少，这正是票面「列表行同步」要传达的信息。
        Assert.Equal("跟随全局 4096 MB", InstanceLaunchTuning.DescribeMemory(null, 4096));
    }

    [Fact]
    public void 全局值不可用时内存摘要退回跟随全局()
    {
        Assert.Equal("跟随全局", InstanceLaunchTuning.DescribeMemory(null, 0));
    }

    [Fact]
    public void Java摘要在实例未设置时说明自动选择()
    {
        Assert.Equal("自动选择", InstanceLaunchTuning.DescribeJava(null, null));
        Assert.Equal("自动选择", InstanceLaunchTuning.DescribeJava("  ", "   "));
    }

    [Fact]
    public void Java摘要在实例未设置时带上全局路径的末两段()
    {
        var text = InstanceLaunchTuning.DescribeJava(null, @"C:\Program Files\Java\jdk-17\bin\java.exe");

        Assert.StartsWith("全局：", text, StringComparison.Ordinal);

        // 末两段形式：列表行只有一行宽度，全路径会把版本号挤掉。
        Assert.Contains("java.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Program Files", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Java摘要对非法路径原样显示而不是抛异常()
    {
        // 用户手改 instance.json 写进非法字符时，详情面板仍要能渲染。
        var text = InstanceLaunchTuning.DescribeJava("C:\\bad\0path\\java.exe", null);

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
