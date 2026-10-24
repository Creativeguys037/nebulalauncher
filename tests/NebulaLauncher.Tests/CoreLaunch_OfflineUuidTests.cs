using NebulaLauncher.Core.Accounts;

namespace NebulaLauncher.Tests;

/// <summary>离线 UUID 必须与 Minecraft 服务端的 <c>OfflinePlayer:</c> 规则逐字节一致。</summary>
public class CoreLaunch_OfflineUuidTests
{
    [Theory]
    // 期望值由独立的 md5 实现算出，防止测试与被测代码共用同一个错误。
    [InlineData("Notch", "b50ad385829da141a2167e7d7539ba7f")]
    [InlineData("Player", "a01e3843e5215998558af459800e4d11")]
    [InlineData("jeb_", "a762f5604fcec236c12ab80efff0b62b")]
    [InlineData("Steve", "5627dd98e6bebc21f8a8e92344183641")]
    public void Create_MatchesMinecraftOfflineRule(string username, string expected)
    {
        Assert.Equal(expected, OfflineAccountUuid.Create(username));
    }

    [Fact]
    public void Create_ReturnsLowercase32HexWithoutDashes()
    {
        var uuid = OfflineAccountUuid.Create("NebulaUser");

        Assert.Equal(32, uuid.Length);
        Assert.DoesNotContain('-', uuid);
        Assert.Equal(uuid.ToLowerInvariant(), uuid);
        Assert.All(uuid, c => Assert.True(char.IsAsciiHexDigit(c)));
    }

    [Fact]
    public void Create_IsDeterministic_AndCaseSensitive()
    {
        Assert.Equal(OfflineAccountUuid.Create("Steve"), OfflineAccountUuid.Create("Steve"));
        Assert.NotEqual(OfflineAccountUuid.Create("Steve"), OfflineAccountUuid.Create("steve"));
    }

    [Fact]
    public void Create_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => OfflineAccountUuid.Create(null!));
    }
}
