using NebulaLauncher.Core.Servers;

namespace NebulaLauncher.Tests;

/// <summary>
/// 服务器地址解析：启动参数与 Ping 共用同一套规则。
/// 以前 <c>GameLauncher</c> 对含 <c>]</c> 的地址直接退回 25565，`[::1]:25570` 会连错端口。
/// </summary>
public class CoreServers_AddressParserTests
{
    [Theory]
    [InlineData("example.com", "example.com", 25565, false)]
    [InlineData("example.com:25570", "example.com", 25570, true)]
    [InlineData("  play.example.com:25566  ", "play.example.com", 25566, true)]
    [InlineData("[::1]", "::1", 25565, false)]
    [InlineData("[::1]:25570", "::1", 25570, true)]
    [InlineData("[2001:db8::1]:25565", "2001:db8::1", 25565, true)]
    [InlineData("::1", "::1", 25565, false)]
    [InlineData("2001:db8::1", "2001:db8::1", 25565, false)]
    [InlineData(":25565", ":25565", 25565, false)]
    [InlineData("host:abc", "host:abc", 25565, false)]
    [InlineData("host:0", "host:0", 25565, false)]
    [InlineData("host:70000", "host:70000", 25565, false)]
    [InlineData("", "", 25565, false)]
    public void Parse_ReturnsHostPortAndExplicitFlag(
        string input, string expectedHost, int expectedPort, bool expectedPortSpecified)
    {
        var parsed = ServerAddressParser.Parse(input);

        Assert.Equal(expectedHost, parsed.Host);
        Assert.Equal(expectedPort, parsed.Port);
        Assert.Equal(expectedPortSpecified, parsed.PortSpecified);
    }

    [Fact]
    public void Parse_NullIsEmptyHostWithDefaultPort()
    {
        var parsed = ServerAddressParser.Parse(null);

        Assert.Equal(string.Empty, parsed.Host);
        Assert.Equal(25565, parsed.Port);
        Assert.False(parsed.PortSpecified);
    }

    [Fact]
    public void Parse_UnclosedBracketKeepsRawText()
    {
        var parsed = ServerAddressParser.Parse("[::1");

        Assert.Equal("[::1", parsed.Host);
        Assert.Equal(25565, parsed.Port);
        Assert.False(parsed.PortSpecified);
    }
}
