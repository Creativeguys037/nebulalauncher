using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Tests;

/// <summary>
/// 文件名清洗的两条补充（缺口 R30-3）：
/// <list type="bullet">
/// <item>保留名表缺 <c>CLOCK$</c> / <c>COM0</c> / <c>LPT0</c>（PCL2 <c>ModValidate.vb:291</c> 有）。</item>
/// <item>缺 NTFS 8.3 短名形态 <c>名字~序号</c> 的拦截（PCL2 <c>:294</c>，issue #4505）。</item>
/// </list>
///
/// 注意 <c>CLOCK$</c> 里的 <c>$</c> 是**合法文件名字符**（不在 <c>Path.GetInvalidFileNameChars()</c> 里），
/// 所以字符白名单一点用都没有，只能靠保留名表按整名比较——这也是它最容易漏的原因。
/// </summary>
public class CorePaths_ReservedNameTests
{
    // ==================== 新补的三个保留名 ====================

    [Theory]
    [InlineData("CLOCK$")]
    [InlineData("clock$")]
    [InlineData("Clock$")]
    public void CLOCK设备名大小写不敏感地被清洗(string name)
    {
        Assert.False(PathSafety.IsAlreadySafe(name));

        // 加前缀而不是拒绝：可读部分保留下来。
        Assert.Equal("_" + name, PathSafety.SanitizeFileName(name));
    }

    [Theory]
    [InlineData("COM0")]
    [InlineData("com0")]
    [InlineData("LPT0")]
    [InlineData("lpt0")]
    public void COM0与LPT0也被清洗(string name)
    {
        Assert.False(PathSafety.IsAlreadySafe(name));
        Assert.Equal("_" + name, PathSafety.SanitizeFileName(name));
    }

    /// <summary>带扩展名时同样非法：<c>CLOCK$.txt</c> 在 Windows 上一样打不开。</summary>
    [Fact]
    public void 保留名带扩展名仍被清洗()
    {
        Assert.Equal("_CLOCK$.txt", PathSafety.SanitizeFileName("CLOCK$.txt"));
        Assert.Equal("_com0.log", PathSafety.SanitizeFileName("com0.log"));
    }

    /// <summary>
    /// 按名字拦，不是按字符拦：<c>cash$</c> 只是普通名字里带了个 <c>$</c>，必须原样保留。
    /// 若哪天有人图省事把 <c>$</c> 加进非法字符表，这条会立刻红。
    /// </summary>
    [Theory]
    [InlineData("cash$")]
    [InlineData("my$pack")]
    [InlineData("100$")]
    public void 名字里的美元符号本身不非法(string name)
    {
        Assert.True(PathSafety.IsAlreadySafe(name), name);
        Assert.Equal(name, PathSafety.SanitizeFileName(name));
    }

    // ==================== NTFS 8.3 短名 ====================

    [Theory]
    [InlineData("ab~1")]
    [InlineData("PROGRA~1")]
    [InlineData("ab~12")]
    [InlineData("世界~1")]
    public void 短名形态被清洗(string name)
    {
        Assert.False(PathSafety.IsAlreadySafe(name), name);
        Assert.Equal("_" + name, PathSafety.SanitizeFileName(name));
    }

    [Fact]
    public void 短名形态带扩展名仍被清洗() =>
        Assert.Equal("_ab~1.jar", PathSafety.SanitizeFileName("ab~1.jar"));

    [Theory]
    [InlineData("ab~")]      // 没有序号，不是短名形态
    [InlineData("~1")]       // 序号前没有字符
    [InlineData("a~1")]      // 序号前只有 1 个字符，短名要求至少 2 个
    [InlineData("ab~x")]     // 序号位置不是数字
    [InlineData("normal")]
    [InlineData("EssentialsX")]
    public void 类似但不是短名的名字仍然合法(string name)
    {
        Assert.True(PathSafety.IsAlreadySafe(name), name);
        Assert.Equal(name, PathSafety.SanitizeFileName(name));
    }

    // ==================== 回归：既有规则没被这次改动破坏 ====================

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM9")]
    [InlineData("LPT9")]
    [InlineData("NUL")]
    public void 原有保留名照旧被清洗(string name)
    {
        Assert.False(PathSafety.IsAlreadySafe(name));
        Assert.StartsWith("_", PathSafety.SanitizeFileName(name), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("datapacks")]
    [InlineData("mods")]
    [InlineData("1.20.1")]
    [InlineData("整合包")]
    public void 普通名字不受影响(string name)
    {
        Assert.True(PathSafety.IsAlreadySafe(name), name);
        Assert.Equal(name, PathSafety.SanitizeFileName(name));
    }

    /// <summary>
    /// 先清洗字符、再判短名：<c>a:b~1</c> 里的 <c>:</c> 会被换成 <c>_</c>，
    /// 清洗后的 <c>a_b~1</c> 正好是短名形态，必须一起被拦——顺序错了就会漏。
    /// </summary>
    [Fact]
    public void 先换非法字符再判短名()
    {
        var cleaned = PathSafety.SanitizeFileName("a:b~1");

        Assert.StartsWith("_", cleaned, StringComparison.Ordinal);
        Assert.Equal("_a_b~1", cleaned);
    }

    [Fact]
    public void 超长名字仍按上限截断()
    {
        var longName = new string('x', PathSafety.MaxSegmentLength + 40);
        var cleaned = PathSafety.SanitizeFileName(longName);

        Assert.Equal(PathSafety.MaxSegmentLength, cleaned.Length);
    }
}