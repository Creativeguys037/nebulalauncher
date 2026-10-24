using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

internal static class AutomaticWorldBackup
{
    internal static async Task RunAsync(
        IBackupService? backups,
        AppSettings? settings,
        string gameDirectory,
        bool crashed,
        IAppLog log,
        IProgress<LaunchProgress>? progress = null)
    {
        if (backups is null || settings?.AutoBackupWorldsOnExit != true)
        {
            return;
        }

        try
        {
            var worlds = await backups.ListWorldsAsync(gameDirectory, CancellationToken.None).ConfigureAwait(false);
            if (worlds.Count == 0)
            {
                return;
            }

            progress?.Report(new LaunchProgress(LaunchPhase.Running, $"游戏已退出，正在自动备份 {worlds.Count} 个世界…"));
            var note = crashed ? "崩溃退出后自动备份" : "退出后自动备份";
            foreach (var world in worlds)
            {
                try
                {
                    await backups.CreateAutomaticAsync(world.Directory, note, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    log.Warn($"世界「{world.Name}」自动备份失败", exception);
                }
            }

            await backups.PruneAsync(settings.WorldBackupRetention, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Warn("退出后自动备份失败", exception);
        }
    }
}
