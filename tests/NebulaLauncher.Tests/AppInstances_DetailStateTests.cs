using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例的内存与 Java 编辑状态（票 #88）。
///
/// 这组测试钉的是票面「边界与异常」那三条，它们在界面上只表现为
/// 「一行红字」或「输入框灰掉」，写错了不会抛异常：
/// <list type="number">
/// <item><b>非法值不写盘</b>：校验不过时 <c>ToMemoryMb()</c> 不能被调用方拿去保存；</item>
/// <item><b>不丢输入</b>：校验失败后草稿文本必须原样保留（不能被清零或夹值）；</item>
/// <item><b>运行中只读</b>：<c>FieldEnabled</c> 为 false 且给出说明。</item>
/// </list>
///
/// 另外钉住「跟随全局」的三态语义：勾上表示不覆盖（落盘 null），
/// 而输入框里显示的是「不设置时实际会分配多少」，不是留空。
///
/// 不触碰任何 Avalonia 类型（CONVENTIONS §8.2）。
/// </summary>
public class AppInstances_DetailStateTests
{
    private static InstanceRow NewRow(
        int? maxMemoryMb = null,
        string? javaPath = null,
        LoaderKind loader = LoaderKind.Fabric)
    {
        return new InstanceRow(new InstanceInfo
        {
            Id = "abc123",
            Name = "测试实例",
            VersionName = "1.20.1-fabric",
            MaxMemoryMb = maxMemoryMb,
            JavaPath = javaPath,
            Loader = loader,
        });
    }

    // ==================== 回填 ====================

    [Fact]
    public void 回填_实例跟随全局时勾选框为真且内存框显示全局值()
    {
        var state = new InstanceDetailState();

        state.LoadFrom(NewRow(maxMemoryMb: null), globalMemoryMb: 4096, globalJavaPath: null, detectedUpperBoundMb: 16384);

        Assert.True(state.FollowGlobalMemory);

        // 框里显示的是「不设置时实际会分配多少」，不是留空——
        // 留空会被读成「这个实例没有内存设置」，而用户想知道的是生效值。
        Assert.Equal("4096", state.MemoryText);
        Assert.Equal(4096, state.GlobalMemoryMb);
        Assert.True(state.HasInstance);
    }

    [Fact]
    public void 回填_实例有自己的内存时勾选框为假并填上数值()
    {
        var state = new InstanceDetailState();

        state.LoadFrom(NewRow(maxMemoryMb: 6144), 4096, null, 16384);

        Assert.False(state.FollowGlobalMemory);
        Assert.Equal("6144", state.MemoryText);
    }

    [Fact]
    public void 回填_越界的旧值被夹回合法区间()
    {
        var state = new InstanceDetailState();

        // 手改过 instance.json，或换了内存更小的机器：直接显示 99999 会让用户
        // 「什么都没改，一保存就被判非法」。
        state.LoadFrom(NewRow(maxMemoryMb: 99999), 4096, null, detectedUpperBoundMb: 8192);

        Assert.Equal("8192", state.MemoryText);
        Assert.True(state.Validate(_ => "resolved"), state.MemoryError);
    }

    [Fact]
    public void 回填_换实例时清掉上一个实例的错误()
    {
        var state = new InstanceDetailState();

        // 必须是「自定义内存」的实例，否则内存框不可编辑、红字根本不会产生。
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);
        state.MemoryText = "abc";
        state.Validate(_ => null);

        Assert.True(state.HasMemoryError);

        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        // 留着会让用户以为当前实例就是这个状态。
        Assert.False(state.HasMemoryError);
    }

    // ==================== 校验：非法值不写盘 + 不丢输入 ====================

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("999999")]
    public void 校验_非法内存时判失败且草稿原样保留(string text)
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        state.MemoryText = text;
        var ok = state.Validate(_ => "resolved");

        Assert.False(ok);
        Assert.True(state.HasMemoryError);

        // 「不丢输入」：用户打进去的东西一个字符都不能被吃掉或改写。
        Assert.Equal(text, state.MemoryText);
    }

    [Fact]
    public void 校验_内存合法时错误被清空()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        state.MemoryText = "abc";
        state.Validate(_ => "resolved");
        Assert.True(state.HasMemoryError);

        state.MemoryText = "6144";
        Assert.True(state.Validate(_ => "resolved"));

        // 改成合法值后红字必须消失，否则用户以为还是错的。
        Assert.False(state.HasMemoryError);
        Assert.Null(state.MemoryError);
    }

    [Fact]
    public void 校验_勾上跟随全局时内存错误被清空()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        state.MemoryText = "abc";
        state.Validate(_ => "resolved");
        Assert.True(state.HasMemoryError);

        state.FollowGlobalMemory = true;
        Assert.True(state.Validate(_ => "resolved"));

        // 勾上跟随全局后不再存在「本实例的值」，红字留着是在报一个已不存在的错。
        Assert.False(state.HasMemoryError);
    }

    [Fact]
    public void 校验_Java路径不存在时就地报错()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(), 4096, null, 16384);

        state.JavaPathText = @"C:\不存在\java.exe";
        var ok = state.Validate(_ => null);

        Assert.False(ok);
        Assert.True(state.HasJavaError);
        Assert.Equal(@"C:\不存在\java.exe", state.JavaPathText);
    }

    [Fact]
    public void 校验_Java留空表示自动选择不算错误()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(), 4096, null, 16384);

        state.JavaPathText = "   ";

        Assert.True(state.Validate(_ => null));
        Assert.False(state.HasJavaError);
    }

    [Fact]
    public void 校验_Java服务缺失时跳过Java校验但仍校验内存()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        state.JavaPathText = @"C:\不存在\java.exe";
        state.MemoryText = "abc";

        // resolveJava 传 null = 跳过 Java 存在性校验。
        var ok = state.Validate(null);

        // 内存仍然要被判掉：Java 服务不可用不该让内存也存不进去，
        // 但也不能因此放过内存的非法值。
        Assert.False(ok);
        Assert.True(state.HasMemoryError);
        Assert.False(state.HasJavaError);
    }

    // ==================== 落盘取值 ====================

    [Fact]
    public void 落盘_跟随全局时内存取null()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        state.FollowGlobalMemory = true;
        Assert.True(state.Validate(_ => "resolved"));

        Assert.Null(state.ToMemoryMb());
    }

    [Fact]
    public void 落盘_自定义内存时取解析值()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, 16384);

        state.MemoryText = "6144";
        Assert.True(state.Validate(_ => "resolved"));

        Assert.Equal(6144, state.ToMemoryMb());
    }

    [Fact]
    public void 落盘_Java留空归一成null()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(javaPath: @"C:\Java\bin\java.exe"), 4096, null, 16384);

        state.JavaPathText = "   ";

        // 空串直接写盘会让「跟随自动选择」与「指定了空路径」无法区分。
        Assert.Null(state.ToJavaPath());
    }

    [Fact]
    public void 落盘_Java路径去掉首尾空白()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(), 4096, null, 16384);

        state.JavaPathText = "  C:\\Java\\bin\\java.exe  ";

        Assert.Equal(@"C:\Java\bin\java.exe", state.ToJavaPath());
    }

    // ==================== 只读（运行中） ====================

    [Fact]
    public void 只读_运行中时字段不可用且给出说明()
    {
        var state = new InstanceDetailState();
        var row = NewRow(maxMemoryMb: 4096);
        state.LoadFrom(row, 4096, null, 16384);

        Assert.True(state.FieldEnabled);

        // 只读判据只有一处：行上的 IsRunningNow（由 ViewModel 按
        // 「磁盘锁 或 本会话有进程」写一次）。
        row.IsRunningNow = true;
        state.RefreshReadOnlyState();

        Assert.True(state.IsReadOnly);
        Assert.False(state.FieldEnabled);
        Assert.False(state.IsMemoryEditable);

        // 票面要求「只读并说明原因」：只灰掉不说为什么，用户会以为界面坏了。
        Assert.False(string.IsNullOrWhiteSpace(state.ReadOnlyHint));
    }

    [Fact]
    public void 只读_游戏退出后恢复可编辑()
    {
        var state = new InstanceDetailState();
        var row = NewRow(maxMemoryMb: 4096);
        state.LoadFrom(row, 4096, null, 16384);

        row.IsRunningNow = true;
        state.RefreshReadOnlyState();
        Assert.True(state.IsReadOnly);

        row.IsRunningNow = false;
        state.RefreshReadOnlyState();

        Assert.False(state.IsReadOnly);
        Assert.True(state.FieldEnabled);
        Assert.Equal(string.Empty, state.ReadOnlyHint);
    }

    // ==================== 提示文案 ====================

    [Fact]
    public void 提示_内存说明里带上限与单位()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, detectedUpperBoundMb: 8192);

        var hint = state.MemoryHint;

        Assert.Contains("MB", hint, StringComparison.Ordinal);
        Assert.Contains("8192", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void 提示_跟随全局时说明用哪个值()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: null), globalMemoryMb: 4096, globalJavaPath: null, detectedUpperBoundMb: 16384);

        Assert.Contains("4096", state.MemoryHint, StringComparison.Ordinal);
    }

    [Fact]
    public void 提示_探测不到上限时用兜底值()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(maxMemoryMb: 4096), 4096, null, detectedUpperBoundMb: 0);

        Assert.Equal(InstanceLaunchTuning.FallbackMaxMemoryMb, state.UpperBoundMb);
        Assert.Contains(
            InstanceLaunchTuning.FallbackMaxMemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.MemoryHint,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 提示_Java留空时说明会自动选择()
    {
        var state = new InstanceDetailState();
        state.LoadFrom(NewRow(), 4096, null, 16384);

        state.JavaPathText = string.Empty;

        Assert.Contains("自动", state.JavaHint, StringComparison.Ordinal);
    }
}
