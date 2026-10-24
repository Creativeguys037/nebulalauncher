using CmlLib.Core.Installers;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 安装器 stdout 日志通道：截断、限量、连续重复折叠、级别判定、失败时提升尾部。
///
/// 为什么这些判据要钉死：处理器（installertools / binarypatcher）失败时，
/// 启动器能拿到的唯一原始报错就是 stdout。通道一旦把关键行截掉、
/// 或者被 200 行上限挡在门外、或者把错误记成 Debug（生产日志级别通常 ≥ Info），
/// 排查就退回到「去翻 java 进程输出」——而那个输出根本没有落盘。
/// </summary>
public class CoreInstalling_InstallerOutputLogTests
{
    // ================= 截断 =================

    [Fact]
    public void 超长行截断到五百字符并带省略号()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        // 中英文混排，按字符数截断（不是字节数）。
        var longLine = string.Concat(Enumerable.Repeat("处理器输出 processor output ", 40));
        Assert.True(longLine.Length > InstallerOutputLog.MaxLineLength);

        channel.Append(longLine);
        channel.Complete();

        var message = Assert.Single(log.Messages(LogLevel.Debug));
        var body = WithoutPrefix(message);

        Assert.Equal(InstallerOutputLog.MaxLineLength, body.Length);
        Assert.EndsWith("…", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 恰好五百字符的行不截断()
    {
        var line = new string('a', InstallerOutputLog.MaxLineLength);

        Assert.Equal(line, InstallerOutputLog.Normalize(line));
        Assert.DoesNotContain("…", InstallerOutputLog.Normalize(line), StringComparison.Ordinal);
    }

    [Fact]
    public void 空白行被忽略()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        channel.Append(null);
        channel.Append("   ");
        channel.Append(string.Empty);
        channel.Complete();

        Assert.Empty(log.Entries);
    }

    // ================= 限量 =================

    [Fact]
    public void 每次安装最多写二百行并提示已省略()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("NeoForge", log);

        for (var i = 0; i < InstallerOutputLog.MaxLines + 50; i++)
        {
            channel.Append($"line {i}");
        }

        channel.Complete();

        Assert.Equal(InstallerOutputLog.MaxLines, channel.WrittenCount);

        // 超限提示只出现一次，且不占常规行数。
        var debug = log.Messages(LogLevel.Debug).ToList();
        var notices = debug.Where(m => m.Contains("上限", StringComparison.Ordinal)).ToList();
        Assert.Single(notices);
        Assert.Equal(InstallerOutputLog.MaxLines + 1, debug.Count);
    }

    // ================= 连续重复折叠 =================

    [Fact]
    public void 连续重复行只写一次并标注重复次数()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        channel.Append("Extracting jar");
        channel.Append("Extracting jar");
        channel.Append("Extracting jar");
        channel.Append("Building");
        channel.Complete();

        var debug = log.Messages(LogLevel.Debug).ToList();

        Assert.Equal(2, debug.Count);
        Assert.Contains("Extracting jar", debug[0], StringComparison.Ordinal);
        Assert.Contains("（重复 2 次）", debug[0], StringComparison.Ordinal);
        Assert.DoesNotContain("（重复", debug[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 不相邻的重复不折叠()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        channel.Append("A");
        channel.Append("B");
        channel.Append("A");
        channel.Complete();

        var debug = log.Messages(LogLevel.Debug).ToList();
        Assert.Equal(3, debug.Count);
        Assert.All(debug, message => Assert.DoesNotContain("（重复", message, StringComparison.Ordinal));
    }

    // ================= 级别 =================

    [Theory]
    [InlineData("java.lang.RuntimeException: boom", true)]
    [InlineData("Processor FAILED with exit code 1", true)]
    [InlineData("error: could not find installertools", true)]
    [InlineData("Exception in thread main", true)]
    [InlineData("Building 1.20.1-forge-47.4.28", false)]
    [InlineData("Extracting client.jar", false)]
    public void 级别判定只认三个关键词(string line, bool expected)
    {
        Assert.Equal(expected, InstallerOutputLog.LooksLikeError(line));
    }

    [Fact]
    public void 错误行写Warn普通行写Debug()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        channel.Append("Building");
        channel.Append("Processor FAILED with exit code 1");
        channel.Append("Cleanup");
        channel.Complete();

        Assert.Equal(2, log.Messages(LogLevel.Debug).Count());
        var warn = Assert.Single(log.Messages(LogLevel.Warn));
        Assert.Contains("FAILED", warn, StringComparison.Ordinal);
        Assert.Empty(log.Messages(LogLevel.Info));
    }

    [Fact]
    public void 日志带来源前缀()
    {
        var forge = new RecordingLog();
        var neoforge = new RecordingLog();
        var forgeChannel = new InstallerOutputLog("Forge", forge);
        var neoChannel = new InstallerOutputLog("NeoForge", neoforge);

        forgeChannel.Append("x");
        forgeChannel.Complete();
        neoChannel.Append("x");
        neoChannel.Complete();

        Assert.StartsWith("[Forge]", forge.Entries[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("[NeoForge]", neoforge.Entries[0].Message, StringComparison.Ordinal);
    }

    // ================= 失败提升 =================

    [Fact]
    public void 失败时把最后二十行提升为Info()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        for (var i = 0; i < 30; i++)
        {
            channel.Append($"step {i}");
        }

        channel.FlushFailure();

        var promoted = log.Messages(LogLevel.Info).ToList();

        Assert.Equal(InstallerOutputLog.TailLines, promoted.Count);
        Assert.All(promoted, m => Assert.Contains("[最后输出]", m, StringComparison.Ordinal));

        // 尾部是最后 20 行（step 10 … step 29），不是最早的那些。
        Assert.Contains("step 10", promoted[0], StringComparison.Ordinal);
        Assert.Contains("step 29", promoted[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void 成功收尾不提升尾部()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        for (var i = 0; i < 30; i++)
        {
            channel.Append($"step {i}");
        }

        channel.Complete();

        Assert.Empty(log.Messages(LogLevel.Info));
    }

    [Fact]
    public void 收尾后不再接受新行()
    {
        var log = new RecordingLog();
        var channel = new InstallerOutputLog("Forge", log);

        channel.Append("first");
        channel.Complete();
        var countAfterComplete = log.Entries.Count;

        channel.Append("late");
        channel.FlushFailure();

        Assert.Equal(countAfterComplete, log.Entries.Count);
        Assert.Equal(1, channel.WrittenCount);
    }

    [Fact]
    public void 没有日志宿主时整条通道是空操作()
    {
        var channel = new InstallerOutputLog("Forge", log: null);

        channel.Append("anything");
        channel.Complete();
        channel.FlushFailure();

        Assert.Equal(0, channel.WrittenCount);
    }

    // ================= 与安装进度适配器接线 =================

    [Fact]
    public void 适配器把每一行安装器输出转进日志通道()
    {
        var appLog = new RecordingLog();
        var sink = new CollectingProgress();
        var channel = new InstallerOutputLog("Forge", appLog);
        var adapter = new InstallService.InstallProgressAdapter(
            "Forge 1.20.1", sink, LoaderKind.Forge, staged: true, log: appLog, outputLog: channel);

        adapter.OutputProgress.Report("Building");
        adapter.OutputProgress.Report("Processor FAILED with exit code 1");
        adapter.OutputProgress.Report("Cleanup");
        adapter.Complete("1.20.1-forge");

        Assert.Equal(2, appLog.Messages(LogLevel.Debug).Count());
        Assert.Single(appLog.Messages(LogLevel.Warn));
        Assert.Empty(appLog.Messages(LogLevel.Info));
    }

    [Fact]
    public void 适配器失败时提升安装器尾部()
    {
        var appLog = new RecordingLog();
        var sink = new CollectingProgress();
        var channel = new InstallerOutputLog("NeoForge", appLog);
        var adapter = new InstallService.InstallProgressAdapter(
            "NeoForge 1.20.2", sink, LoaderKind.NeoForge, staged: true, log: appLog, outputLog: channel);

        adapter.OutputProgress.Report("Building");
        adapter.OutputProgress.Report("java.lang.IllegalStateException: broken");
        adapter.Abort();

        var promoted = appLog.Messages(LogLevel.Info)
            .Where(m => m.Contains("[最后输出]", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, promoted.Count);
        Assert.Contains(promoted, m => m.Contains("IllegalStateException", StringComparison.Ordinal));
    }

    [Fact]
    public void 适配器取消时只落盘不提升尾部()
    {
        var appLog = new RecordingLog();
        var sink = new CollectingProgress();
        var channel = new InstallerOutputLog("NeoForge", appLog);
        var adapter = new InstallService.InstallProgressAdapter(
            "NeoForge 1.20.2", sink, LoaderKind.NeoForge, staged: true, log: appLog, outputLog: channel);

        adapter.OutputProgress.Report("Building");
        adapter.Abort(flushOutput: false);

        Assert.DoesNotContain(
            appLog.Messages(LogLevel.Info), m => m.Contains("[最后输出]", StringComparison.Ordinal));
        Assert.Contains(appLog.Messages(LogLevel.Debug), m => m.Contains("Building", StringComparison.Ordinal));
    }

    // ================= 辅助 =================

    private static string WithoutPrefix(string message)
    {
        var index = message.IndexOf("] ", StringComparison.Ordinal);
        return index < 0 ? message : message[(index + 2)..];
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public string LogFilePath => "memory";

        public void Debug(string message) => Entries.Add((LogLevel.Debug, message));

        public void Info(string message) => Entries.Add((LogLevel.Info, message));

        public void Warn(string message, Exception? ex = null) => Entries.Add((LogLevel.Warn, message));

        public void Error(string message, Exception? ex = null) => Entries.Add((LogLevel.Error, message));

        public IEnumerable<string> Messages(LogLevel level) =>
            Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);
    }

    private sealed class CollectingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = new();

        public void Report(DownloadProgress value) => Items.Add(value);
    }
}
