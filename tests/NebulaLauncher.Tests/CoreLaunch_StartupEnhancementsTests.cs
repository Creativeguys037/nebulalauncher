using System.Globalization;
using System.Text.Json;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Logging;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Settings;

namespace NebulaLauncher.Tests;

/// <summary>
/// 启动体验增强的回归用例：Log4Shell 参数、日志脱敏、备份轮转、设置迁移。
/// 都是纯逻辑，不需要进程或网络。
/// </summary>
public sealed class CoreLaunch_StartupEnhancementsTests
{
    private static readonly JsonSerializerOptions BackupMetaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public void JvmArguments_AlwaysEnableLog4jLookupProtection()
    {
        var arguments = JvmArgumentBuilder.Build(GcKind.Auto, 21, null);

        Assert.Contains(JvmArgumentBuilder.Log4jSecurityOption, arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void JvmArguments_RespectUserLog4jOverride()
    {
        // 用户显式写过这个键（哪怕写成 =false）就让位：启动器不覆盖显式配置。
        var arguments = JvmArgumentBuilder.Build(
            GcKind.Auto,
            21,
            "-Dlog4j2.formatMsgNoLookups=false");

        Assert.DoesNotContain(JvmArgumentBuilder.Log4jSecurityOption, arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void JvmArguments_SecurityOptionIsSeparateFromGcAndEncoding()
    {
        var security = JvmArgumentBuilder.BuildSecurityArguments(null);

        Assert.Equal(JvmArgumentBuilder.Log4jSecurityOption, security);
        Assert.Empty(JvmArgumentBuilder.BuildSecurityArguments("-Dlog4j2.formatMsgNoLookups=true"));
    }

    [Theory]
    [InlineData("accessToken=abcdef123456")]
    [InlineData("{\"accessToken\":\"abcdef123456\"}")]
    [InlineData("refresh_token: abcdef123456")]
    public void LogRedactor_MasksTokenAssignments(string text)
    {
        var redacted = LogRedactor.Redact(text);

        Assert.NotNull(redacted);
        Assert.DoesNotContain("abcdef123456", redacted, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void LogRedactor_MasksBearerHeader()
    {
        var redacted = LogRedactor.Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.payload", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void LogRedactor_LeavesOrdinaryLinesUntouched()
    {
        // 不含凭据与本机用户目录的行必须原样返回；含用户目录的情况见下一条用例
        // （崩溃导出需要把 C:\Users\<name>\ 替换成 C:\Users\<user>\，这是行为变更）。
        const string line = "已加载 12 个版本：D:\\Games\\minecraft\\versions";

        Assert.Equal(line, LogRedactor.Redact(line));
        Assert.Null(LogRedactor.Redact(null));
    }

    [Fact]
    public void LogRedactor_MasksHomeDirectoryUserName()
    {
        const string line = "已加载 12 个版本：C:\\Users\\me\\AppData\\Roaming\\.minecraft\\versions";

        var redacted = LogRedactor.Redact(line);

        Assert.NotNull(redacted);
        Assert.DoesNotContain("Users\\me", redacted, StringComparison.Ordinal);

        // 路径其余部分必须保留：没有它们，日志无法判读
        Assert.Contains(@"C:\Users\<user>\AppData\Roaming\.minecraft\versions", redacted!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupService_PruneKeepsNewestPerWorld()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new BackupService(paths, new CoreAssetsLog());

        // 直接写 zip + meta：CreateAsync 的时间戳只到秒，三次连续备份可能落在同一秒，
        // 用例需要的「新旧」判定就不可靠了。
        WriteBackup(paths, "world-a", DateTimeOffset.Now.AddDays(-3));
        WriteBackup(paths, "world-a", DateTimeOffset.Now.AddDays(-2));
        WriteBackup(paths, "world-a", DateTimeOffset.Now.AddDays(-1));
        WriteBackup(paths, "world-b", DateTimeOffset.Now.AddDays(-1));

        var deleted = await service.PruneAsync(2);

        Assert.Equal(1, deleted);

        var remaining = await service.ListBackupsAsync();
        Assert.Equal(2, remaining.Count(entry => entry.WorldName == "world-a"));
        Assert.Single(remaining, entry => entry.WorldName == "world-b");
    }

    [Fact]
    public async Task BackupService_PruneTreatsNonPositiveAsNoOp()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var service = new BackupService(paths, new CoreAssetsLog());

        WriteBackup(paths, "world-a", DateTimeOffset.Now.AddDays(-1));

        Assert.Equal(0, await service.PruneAsync(0));
        Assert.Single(await service.ListBackupsAsync());
    }

    [Fact]
    public async Task Settings_LegacyCloseFlagMigratesToVisibility()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            "{\"closeLauncherAfterLaunch\":true}");

        var service = new SettingsService(paths, new CoreAssetsLog());
        await service.LoadAsync();

        Assert.Equal("close", service.Current.LauncherVisibilityOnLaunch);
        Assert.Equal("normal", service.Current.GameProcessPriority);
    }

    [Fact]
    public async Task Settings_NormalizeClampsRetentionAndPriority()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);

        await File.WriteAllTextAsync(
            paths.SettingsFile,
            "{\"worldBackupRetention\":0,\"gameProcessPriority\":\"TURBO\",\"launcherVisibilityOnLaunch\":\"explode\"}");

        var service = new SettingsService(paths, new CoreAssetsLog());
        await service.LoadAsync();

        Assert.Equal(1, service.Current.WorldBackupRetention);
        Assert.Equal("normal", service.Current.GameProcessPriority);
        Assert.Equal("keep", service.Current.LauncherVisibilityOnLaunch);
    }

    private static void WriteBackup(FakeLauncherPaths paths, string worldName, DateTimeOffset createdAt)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);

        var stamp = createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var archivePath = Path.Combine(paths.BackupsDirectory, $"{worldName}-{stamp}.zip");
        File.WriteAllBytes(archivePath, Array.Empty<byte>());

        var entry = new BackupEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            FilePath = archivePath,
            WorldName = worldName,
            CreatedAt = createdAt,
            SizeBytes = 0,
            Note = "测试",
            IsAutomatic = true,
            SourceWorldDirectory = Path.Combine(paths.MinecraftDirectory, "saves", worldName),
        };

        File.WriteAllText(
            archivePath + ".meta.json",
            JsonSerializer.Serialize(entry, BackupMetaJsonOptions));
    }
}
