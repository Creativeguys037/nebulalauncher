using NebulaLauncher.Core.Platforms;

namespace NebulaLauncher.Tests;

/// <summary>
/// 非 Windows 剪贴板命令链的判据：失败可能被当成成功。
///
/// 本机是 Windows，测试里不能真的启动 pbcopy / wl-copy / xclip，所以把「候选顺序」
/// 与「退出码算不算成功」抽成纯数据 + 纯函数来钉：
/// 1. 顺序错了，Wayland 会话会被 XWayland 的 xclip 截胡（返回 0 但原生应用读不到）；
/// 2. 不看退出码，命令报错退出也会被当成写进剪贴板，用户粘贴时才发现是旧内容。
/// </summary>
public sealed class CorePlatform_ClipboardTests
{
    [Fact]
    public void LinuxChain_TriesWaylandFirstThenXclip()
    {
        var commands = ClipboardCommandChain.For(isMacOs: false);

        Assert.Equal(2, commands.Count);
        Assert.Equal("wl-copy", commands[0].FileName);
        Assert.Equal(string.Empty, commands[0].Arguments);
        Assert.Equal("xclip", commands[1].FileName);
        Assert.Equal("-selection clipboard", commands[1].Arguments);
    }

    [Fact]
    public void MacOsChain_HasSinglePbcopyCandidate()
    {
        var commands = ClipboardCommandChain.For(isMacOs: true);

        var only = Assert.Single(commands);
        Assert.Equal("pbcopy", only.FileName);
        Assert.Equal(string.Empty, only.Arguments);
    }

    [Fact]
    public void Chains_AreNeverEmpty()
    {
        // 空链条意味着一次命令都不尝试就报失败，排查日志时会以为「平台不支持剪贴板」。
        Assert.NotEmpty(ClipboardCommandChain.For(isMacOs: false));
        Assert.NotEmpty(ClipboardCommandChain.For(isMacOs: true));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(127, false)]   // 命令不存在时的常见退出码
    [InlineData(130, false)]   // 被信号杀死（128 + SIGINT）
    [InlineData(-1, false)]
    public void Succeeded_OnlyZeroExitCodeIsSuccess(int exitCode, bool expected)
    {
        Assert.Equal(expected, ClipboardCommandChain.Succeeded(exitCode));
    }

    [Fact]
    public async Task CustomWriter_TakesPrecedenceOverPlatformCommands()
    {
        // 钩子命中时不能落到平台命令：本机是 Windows，落下去会真的改系统剪贴板。
        var service = new PlatformService();
        string? captured = null;
        service.ClipboardWriter = text =>
        {
            captured = text;
            return Task.CompletedTask;
        };

        try
        {
            await service.CopyTextAsync("Nebula");
        }
        finally
        {
            service.ClipboardWriter = null;
        }

        Assert.Equal("Nebula", captured);
    }

    [Fact]
    public async Task NullText_DoesNotInvokeWriter()
    {
        var service = new PlatformService();
        var invoked = false;
        service.ClipboardWriter = _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        try
        {
            await service.CopyTextAsync(null!);
        }
        finally
        {
            service.ClipboardWriter = null;
        }

        Assert.False(invoked);
    }
}
