using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 实例详情「世界」分区（票 #89）的接缝：目标实例切换与备份状态机的纯逻辑。
///
/// 只测外部可观察结果，不碰 Avalonia 类型（CONVENTIONS §8.2）：
/// 分区视图模型在 <c>_worldIcons</c> 为 null 时不会去碰 Dispatcher，
/// 所以这里全部传 null，走纯内存路径。
/// </summary>
public sealed class InstanceWorldsSectionTests
{
    [Fact]
    public async Task 同一个目录重复设定目标只读一次盘()
    {
        var service = new FakeBackupService(new[]
        {
            new WorldInfo { Name = "甲", Directory = "/saves/甲", SizeBytes = 1024, LastWriteTime = DateTimeOffset.UnixEpoch },
        });

        var section = new InstanceWorldsSectionViewModel(service);

        await section.SetTargetAsync("/game", "实例一");
        await section.SetTargetAsync("/game", "实例一");
        await section.SetTargetAsync("/game", "实例一");

        // 详情面板每次选中变化都会调用它；不幂等就会在列表滚动时反复触发磁盘扫描。
        Assert.Equal(1, service.ListWorldsCalls);
        Assert.Single(section.Worlds);
    }

    [Fact]
    public async Task 换目标会重新读盘并换掉列表()
    {
        var service = new FakeBackupService(new[]
        {
            new WorldInfo { Name = "甲", Directory = "/saves/甲", SizeBytes = 1, LastWriteTime = DateTimeOffset.UnixEpoch },
        });

        var section = new InstanceWorldsSectionViewModel(service);

        await section.SetTargetAsync("/game-a", "A");
        await section.SetTargetAsync("/game-b", "B");

        Assert.Equal(2, service.ListWorldsCalls);
        Assert.Equal("/game-b", service.LastGameDirectory);
    }

    [Fact]
    public async Task 没有存档时是空态而不是错误()
    {
        var section = new InstanceWorldsSectionViewModel(new FakeBackupService(Array.Empty<WorldInfo>()));

        await section.SetTargetAsync("/empty", "空实例");

        Assert.True(section.IsEmpty);
        Assert.False(section.HasError);
        Assert.False(section.HasWorlds);
    }

    [Fact]
    public async Task 备份成功后行上的备份数加一()
    {
        var world = new WorldInfo
        {
            Name = "甲",
            Directory = "/saves/甲",
            SizeBytes = 2048,
            LastWriteTime = DateTimeOffset.UnixEpoch,
        };

        var service = new FakeBackupService(new[] { world });
        var section = new InstanceWorldsSectionViewModel(service);

        await section.SetTargetAsync("/game", "实例一");
        var row = section.Worlds.Single();

        Assert.False(row.HasBackups);

        await section.BackupWorldCommand.ExecuteAsync(row);

        Assert.Single(row.Backups);
        Assert.True(row.HasBackups);
        Assert.Contains("已备份", section.ResultText);
    }

    [Fact]
    public async Task 没有选中备份时确认还原不会写入()
    {
        var world = new WorldInfo
        {
            Name = "甲",
            Directory = "/saves/甲",
            SizeBytes = 2048,
            LastWriteTime = DateTimeOffset.UnixEpoch,
        };

        var service = new FakeBackupService(new[] { world });
        var section = new InstanceWorldsSectionViewModel(service);

        await section.SetTargetAsync("/game", "实例一");
        var row = section.Worlds.Single();

        await section.ConfirmRestoreCommand.ExecuteAsync(row);

        // 二次确认是硬门槛：没有 PendingRestore 就什么都不写，否则点一下就可能覆盖存档。
        Assert.Equal(0, service.RestoreCalls);
    }

    [Fact]
    public async Task 备份清单按时间倒序挂在行上()
    {
        var world = new WorldInfo
        {
            Name = "甲",
            Directory = "/saves/甲",
            SizeBytes = 2048,
            LastWriteTime = DateTimeOffset.UnixEpoch,
        };

        var older = new BackupEntry
        {
            Id = "old",
            FilePath = "/b/old.zip",
            WorldName = "甲",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            SizeBytes = 10,
        };

        var newer = new BackupEntry
        {
            Id = "new",
            FilePath = "/b/new.zip",
            WorldName = "甲",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            SizeBytes = 10,
        };

        var entries = new[] { older, newer };
        var service = new FakeBackupService(new[] { world }, entries);
        var section = new InstanceWorldsSectionViewModel(service);

        await section.SetTargetAsync("/game", "实例一");

        var row = section.Worlds.Single();

        Assert.Equal(new[] { "new", "old" }, row.Backups.Select(b => b.Source.Id).ToArray());
        Assert.Equal(2, row.Backups.Count);
    }

    private sealed class FakeBackupService : IBackupService
    {
        private readonly IReadOnlyList<WorldInfo> _worlds;
        private readonly List<BackupEntry> _backups;

        public FakeBackupService(IReadOnlyList<WorldInfo> worlds, IReadOnlyList<BackupEntry>? backups = null)
        {
            _worlds = worlds;
            _backups = backups is null ? new List<BackupEntry>() : new List<BackupEntry>(backups);
        }

        public int ListWorldsCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public string? LastGameDirectory { get; private set; }

        public Task<IReadOnlyList<WorldInfo>> ListWorldsAsync(
            string gameDirectory,
            CancellationToken cancellationToken = default)
        {
            ListWorldsCalls++;
            LastGameDirectory = gameDirectory;

            // 替身不按目录过滤：这里测的是「目录变了要不要重读」，
            // 真实服务的 saves/ 枚举行为由 BackupService 自己的用例覆盖。
            return Task.FromResult<IReadOnlyList<WorldInfo>>(_worlds);
        }

        public Task<BackupEntry> CreateAsync(
            string worldDirectory,
            string? note = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var name = worldDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "world";

            var entry = new BackupEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                FilePath = $"/b/{name}.zip",
                WorldName = name,
                CreatedAt = DateTimeOffset.UtcNow,
                SizeBytes = 4096,
                Note = note ?? string.Empty,
            };

            _backups.Add(entry);

            return Task.FromResult(entry);
        }

        public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupEntry>>(_backups.ToArray());

        public Task RestoreAsync(
            BackupEntry entry,
            string targetGameDirectory,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            RestoreCalls++;

            return Task.CompletedTask;
        }

        public Task DeleteAsync(BackupEntry entry, CancellationToken cancellationToken = default)
        {
            _backups.RemoveAll(b => string.Equals(b.Id, entry.Id, StringComparison.Ordinal));

            return Task.CompletedTask;
        }
    }
}