using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Tests;

/// <summary>
/// <see cref="PathSafety"/> 的规则集。
///
/// 这一组测试锁的是「Windows 上不报错但结果与预期不同」的三条：
/// 结尾点号被静默吞掉、保留名建不出来、超长名字直到写盘才失败。
/// 仓库里原本有 5 处各写一套清洗，前两条谁都没处理——所以这里的用例比实现本身更重要。
/// </summary>
public sealed class CorePaths_PathSafetyTests
{
    [Fact]
    public void 非法字符换成下划线()
    {
        Assert.Equal("a_b_c", PathSafety.SanitizeFileName("a:b*c"));
        Assert.Equal("a_b", PathSafety.SanitizeFileName("a|b"));
        Assert.Equal("a_b", PathSafety.SanitizeFileName("a/b"));
        Assert.Equal("a_b", PathSafety.SanitizeFileName("a\\b"));
    }

    [Fact]
    public void 结尾的点与空格被去掉()
    {
        // "World." 与 "World" 在 Windows 上指向同一目录，留着会让重名检测与后续查找对不上。
        Assert.Equal("World", PathSafety.SanitizeFileName("World."));
        Assert.Equal("World", PathSafety.SanitizeFileName("World ".TrimEnd() + " "));
        Assert.Equal("World", PathSafety.SanitizeFileName("  World . .  "));
    }

    [Fact]
    public void 保留名加前缀()
    {
        // CON / PRN / AUX / NUL / COM1-9 / LPT1-9 是设备名，作为文件名或目录名都建不出来。
        Assert.Equal("_CON", PathSafety.SanitizeFileName("CON"));
        Assert.Equal("_con", PathSafety.SanitizeFileName("con"));
        Assert.Equal("_NUL", PathSafety.SanitizeDirectoryName("NUL"));
        Assert.Equal("_COM1", PathSafety.SanitizeFileName("COM1"));
        Assert.Equal("_LPT9", PathSafety.SanitizeFileName("LPT9"));
    }

    [Fact]
    public void 保留名带扩展名也要加前缀()
    {
        // "CON.txt" 在 Windows 上同样打不开：判的是第一个点号之前的那一段。
        Assert.Equal("_CON.txt", PathSafety.SanitizeFileName("CON.txt"));
    }

    [Fact]
    public void 超过长度上限时截断()
    {
        var longName = new string('x', PathSafety.MaxSegmentLength + 50);

        var result = PathSafety.SanitizeFileName(longName);

        Assert.Equal(PathSafety.MaxSegmentLength, result.Length);
    }

    [Fact]
    public void 空输入返回兜底名()
    {
        Assert.Equal("unnamed", PathSafety.SanitizeFileName(null));
        Assert.Equal("unnamed", PathSafety.SanitizeFileName("   "));
        Assert.Equal("world", PathSafety.SanitizeFileName(null, "world"));
        Assert.Equal(string.Empty, PathSafety.SanitizeFileName(null, string.Empty));
    }

    [Fact]
    public void 只由非法字符组成的输入也回落到兜底名()
    {
        // ":::" 全部被换成下划线后不是空串，仍然是合法名字；真正会空的是全空格/全点号。
        Assert.Equal("___", PathSafety.SanitizeFileName(":::"));
        Assert.Equal("modpack", PathSafety.SanitizeDirectoryName("...", "modpack"));
    }

    [Fact]
    public void 合法名字原样返回()
    {
        Assert.Equal("我的世界 1.20.1", PathSafety.SanitizeFileName("我的世界 1.20.1"));
        Assert.True(PathSafety.IsAlreadySafe("我的世界 1.20.1"));
        Assert.False(PathSafety.IsAlreadySafe("CON"));
        Assert.False(PathSafety.IsAlreadySafe("World."));
    }
}