using NebulaLauncher.Core.Launching;

namespace NebulaLauncher.Tests;

/// <summary>
/// 参数切分的回归用例。核心判据只有一条：Windows 路径里的反斜杠必须原样保留。
/// CmlLib 的解析器把 <c>\</c> 当转义符，<c>C:\Program Files\Java\bin</c> 会变成
/// <c>C:Program FilesJavabin</c>，而用户完全看不出是自己写错了还是启动器吞了。
/// </summary>
public sealed class CoreLaunch_ArgumentSplitterTests
{
    [Fact]
    public void Split_KeepsWindowsPathBackslashes()
    {
        var arguments = LaunchArgumentSplitter.Split("-Djava.library.path=\"C:\\Program Files\\Java\\bin\"");

        Assert.Single(arguments);
        Assert.Equal("-Djava.library.path=C:\\Program Files\\Java\\bin", arguments[0]);
    }

    [Fact]
    public void Split_HandlesPlainArguments()
    {
        var arguments = LaunchArgumentSplitter.Split("-XX:+UseG1GC  -Dfile.encoding=UTF-8");

        Assert.Equal(["-XX:+UseG1GC", "-Dfile.encoding=UTF-8"], arguments);
    }

    [Fact]
    public void Split_TreatsNewlinesAndTabsAsSeparators()
    {
        var arguments = LaunchArgumentSplitter.Split("-XX:+UseG1GC\n-XX:MaxGCPauseMillis=50\t-Dfoo=bar");

        Assert.Equal(["-XX:+UseG1GC", "-XX:MaxGCPauseMillis=50", "-Dfoo=bar"], arguments);
    }

    [Fact]
    public void Split_KeepsEmptyQuotedValue()
    {
        var arguments = LaunchArgumentSplitter.Split("-Dempty=\"\" -Dnext=1");

        Assert.Equal(["-Dempty=", "-Dnext=1"], arguments);
    }

    [Fact]
    public void Split_QuotesWithoutWhitespaceProduceOneArgument()
    {
        var arguments = LaunchArgumentSplitter.Split("\"-Xmx4G\"");

        Assert.Single(arguments);
        Assert.Equal("-Xmx4G", arguments[0]);
    }

    [Theory]
    // 不用 [InlineData(null)]：C# 会把它解析成 params 空数组，变成零参数用例。
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_ReturnsEmptyForBlankInput(string? input)
        => Assert.Empty(LaunchArgumentSplitter.Split(input));
}
