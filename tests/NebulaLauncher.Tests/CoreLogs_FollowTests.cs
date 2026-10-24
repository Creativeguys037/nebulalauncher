using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Logging;

namespace NebulaLauncher.Tests;

/// <summary>
/// 日志跟随的增量读取：只返回完整的行、偏移落在行边界、截断 / 轮转 / 文件消失都能自愈。
/// 跟随是「日志页开着时每 700ms 跑一次」的热路径，任何一处抛异常都会让页面停摆，所以全部走状态返回值。
/// </summary>
public class CoreLogs_FollowTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // ==================== 正常增量 ====================

    [Fact]
    public async Task FirstRead_FromZero_ReturnsEveryLineAndEndsAtFileLength()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "launcher-20261022.log",
            "2026-10-22 09:41:07.218 [INFO ] 第一条\n" +
            "2026-10-22 09:41:08.000 [WARN ] 第二条\n");

        var result = await new LogFollower().ReadSinceAsync(path, 0);

        Assert.Equal(LogFollowStatus.Appended, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("第一条", result.Entries[0].Text);
        Assert.Equal("第二条", result.Entries[1].Text);
        Assert.Equal(new long[] { 1, 2 }, result.Entries.Select(entry => (long)entry.LineNumber));
        Assert.Equal(new FileInfo(path).Length, result.Offset);
    }

    [Fact]
    public async Task SecondRead_AfterAppend_ReturnsOnlyNewLinesAndAdvancesOffset()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "latest.log", "2026-10-22 09:41:07.218 [INFO ] 第一条\n");
        var follower = new LogFollower();

        var first = await follower.ReadSinceAsync(path, 0);
        File.AppendAllText(path, "2026-10-22 09:41:09.000 [ERROR] 第二条\n", Utf8);

        var second = await follower.ReadSinceAsync(path, first.Offset);

        Assert.Equal(LogFollowStatus.Appended, second.Status);
        Assert.Single(second.Entries);
        Assert.Equal("第二条", second.Entries[0].Text);
        Assert.Equal(LogLevel.Error, second.Entries[0].Level);
        Assert.Equal(new FileInfo(path).Length, second.Offset);
        Assert.True(second.Offset > first.Offset);
    }

    [Fact]
    public async Task Offset_StopsAtLastNewline_WhenFileEndsWithHalfLine()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "live.log", "line1\nline2\nhalf");
        var follower = new LogFollower();

        var result = await follower.ReadSinceAsync(path, 0);

        Assert.Equal(LogFollowStatus.Appended, result.Status);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("line1", result.Entries[0].Text);
        Assert.Equal("line2", result.Entries[1].Text);

        // "line1\nline2\n" = 12 字节；末尾的 "half" 没换行，偏移不能推进到它后面。
        Assert.Equal(12, result.Offset);
        Assert.Equal("half".Length, new FileInfo(path).Length - result.Offset);
    }

    // ==================== 自愈：截断 / 轮转 ====================

    [Fact]
    public async Task TruncatedFile_ResetsToHeadAndReportsTruncated()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "launcher.log", "第一条\n第二条\n第三条\n");
        var follower = new LogFollower();

        var first = await follower.ReadSinceAsync(path, 0);
        File.WriteAllText(path, "重新开始\n", Utf8);

        var second = await follower.ReadSinceAsync(path, first.Offset);

        Assert.Equal(LogFollowStatus.Truncated, second.Status);
        Assert.Single(second.Entries);
        Assert.Equal("重新开始", second.Entries[0].Text);
        Assert.Equal(new FileInfo(path).Length, second.Offset);
    }

    [Fact]
    public async Task RotatedFile_WhenReplacedByDifferentLongerContent_ReportsRotated()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "latest.log", "aaaa\n");
        var follower = new LogFollower();

        var first = await follower.ReadSinceAsync(path, 0);
        Assert.Equal(5, first.Offset);

        // 新文件更长，但记录偏移处（第 5 个字节）不是换行：内容已经不是上次读的那份。
        File.WriteAllText(path, "0123456789\n", Utf8);

        var second = await follower.ReadSinceAsync(path, first.Offset);

        Assert.Equal(LogFollowStatus.Rotated, second.Status);
        Assert.Single(second.Entries);
        Assert.Equal("0123456789", second.Entries[0].Text);
        Assert.Equal(new FileInfo(path).Length, second.Offset);
    }

    [Fact]
    public async Task HalfLine_IsReadAfterNewlineArrives_NotLost()
    {
        using var temp = new TempDirectory();
        var path = Write(temp, "live.log", "line1\npar");
        var follower = new LogFollower();

        var first = await follower.ReadSinceAsync(path, 0);
        Assert.Single(first.Entries);
        Assert.Equal(6, first.Offset);

        File.AppendAllText(path, "t2\n", Utf8);
        var second = await follower.ReadSinceAsync(path, first.Offset);

        Assert.Single(second.Entries);
        Assert.Equal("part2", second.Entries[0].Text);
    }

    // ==================== 失败路径：不抛，只报状态 ====================

    [Fact]
    public async Task MissingFile_ReturnsMissingStatusWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var follower = new LogFollower();

        var result = await follower.ReadSinceAsync(temp.Combine("no-such.log"), 0);

        Assert.Equal(LogFollowStatus.Missing, result.Status);
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankPath_ReturnsMissingStatus(string path)
    {
        var result = await new LogFollower().ReadSinceAsync(path, 0);

        Assert.Equal(LogFollowStatus.Missing, result.Status);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DirectoryPath_ReturnsMissingStatusWithoutThrowing()
    {
        using var temp = new TempDirectory();

        var result = await new LogFollower().ReadSinceAsync(temp.Path, 0);

        Assert.Equal(LogFollowStatus.Missing, result.Status);
        Assert.Empty(result.Entries);
    }

    // ==================== 上限与编码 ====================

    [Fact]
    public async Task HugeAppend_IsCappedByMaxBytes_AndContinuesFromBoundary()
    {
        using var temp = new TempDirectory();
        var builder = new StringBuilder();
        for (var i = 1; i <= 500; i++)
        {
            builder.Append("2026-10-22 09:41:07.218 [INFO ] 填充行 ").Append(i).Append('\n');
        }

        var path = Write(temp, "big.log", builder.ToString());
        var follower = new LogFollower();

        var first = await follower.ReadSinceAsync(path, 0, maxBytes: 2048);

        Assert.Equal(LogFollowStatus.Appended, first.Status);
        Assert.NotEmpty(first.Entries);
        Assert.True(first.Entries.Count < 500);
        Assert.True(first.Offset < new FileInfo(path).Length);

        // 偏移落在行边界：从它续读既不重复，也不丢内容。
        var second = await follower.ReadSinceAsync(path, first.Offset, maxBytes: 2048);

        Assert.NotEmpty(second.Entries);
        Assert.True(second.Offset > first.Offset);
        Assert.DoesNotContain(
            first.Entries.Select(entry => entry.Text),
            text => second.Entries.Any(other => string.Equals(other.Text, text, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MultiByteUtf8_IsNeverSplitAcrossReads()
    {
        using var temp = new TempDirectory();
        var builder = new StringBuilder();
        for (var i = 1; i <= 200; i++)
        {
            builder.Append("2026-10-22 09:41:07.218 [INFO ] 中文日志行内容测试 ").Append(i).Append('\n');
        }

        var path = Write(temp, "cjk.log", builder.ToString());
        var follower = new LogFollower();

        // 小上限会切在中文行中间：只吐完整行保证不会出现半个字符（U+FFFD 替换符）。
        var result = await follower.ReadSinceAsync(path, 0, maxBytes: 300);

        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, entry => Assert.DoesNotContain('\uFFFD', entry.Text));
        Assert.All(result.Entries, entry => Assert.Equal(LogLevel.Info, entry.Level));
        Assert.True(result.Offset > 0);
    }

    private static string Write(TempDirectory temp, string name, string content)
    {
        var path = temp.Combine(name);
        File.WriteAllText(path, content, Utf8);
        return path;
    }
}