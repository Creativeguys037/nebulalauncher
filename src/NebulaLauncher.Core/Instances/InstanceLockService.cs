using System.Diagnostics;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 基于锁文件的实例运行锁：<c>&lt;游戏目录&gt;/.nebula-run.json</c>。
///
/// 为什么用文件而不是进程内互斥体：启动器可能同时开两个窗口（用户双击两次快捷方式），
/// 也可能有托盘常驻实例；进程内锁对它们完全无效，而游戏目录是两边共享的那一份事实。
///
/// 三条实现约束（都来自「锁判错就会让用户打不开游戏」这个问题）：
/// 1. **写入必须原子**：先写唯一命名的 <c>.tmp</c> 再 <c>File.Move</c>（不覆盖）。
///    半截 JSON 会被读到解析失败，而解析失败必须当僵死处理，等于保护整体失效。
/// 2. **判活必须看启动时间**：只比 PID 挡不住系统复用（见 <see cref="InstanceLockInfo"/>）。
/// 3. **任何读失败都当僵死**：进程被任务管理器杀掉、锁文件被手改、非 Windows 上取不到
///    进程信息，都必须能接管；否则实例会永久处于「已有游戏在运行」而无法启动。
/// </summary>
public sealed class InstanceLockService : IInstanceLockService
{
    /// <summary>锁文件名。点开头既与用户的 mods / config 区分，也能被「清理目录」的规则识别。</summary>
    public const string LockFileName = ".nebula-run.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 「还是不是同一个进程」的启动时间容差。同一个进程读两次 StartTime 完全一致；
    /// 被系统复用给新进程时至少差秒级。留 1 秒是为了容忍某些平台上 StartTime 的取整。
    /// </summary>
    private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 抢锁重试次数。只有一种情况需要重试：刚判定对方僵死并删除，move 时又被第三个进程抢先。
    /// 3 次足够，再多只是在锁被反复争抢时拖慢启动。
    /// </summary>
    private const int AcquireAttempts = 3;

    private readonly IAppLog _log;

    public InstanceLockService(IAppLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public Task<InstanceLockInfo?> QueryAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        // 前后台都会调（版本页加载、启动前复查），小文件读取放线程池，避免在 UI 线程上做 IO。
        return Task.Run(
            () =>
            {
                var (state, info) = InspectDirectory(gameDirectory);

                // 只有「活着的进程」才算运行中。僵死锁也能解析出 PID，直接返回会让界面
                // 永远显示一个早就不存在的游戏在跑。
                return state == LockState.Live ? info : null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IInstanceLockHandle?> TryAcquireAsync(
        string gameDirectory,
        string? versionName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            // 没有目录就没有「该目录的锁」这回事。不能返回 null：那会被读成「已有进程在跑」。
            _log.Warn("实例运行锁：未提供游戏目录，本次不做重复启动检查。");
            return NoopInstanceLockHandle.Instance;
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(gameDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 目录建不出来（权限 / 非法路径）时唯一正确的选择是放行：把游戏挡在启动器外面
            // 比少一次重复启动检查严重得多。
            _log.Warn($"实例运行锁：无法准备游戏目录 {gameDirectory}，本次不做重复启动检查。", ex);
            return NoopInstanceLockHandle.Instance;
        }

        var path = Path.Combine(directory, LockFileName);

        for (var attempt = 0; attempt < AcquireAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (state, existing) = Inspect(path);

            if (state == LockState.Live)
            {
                return null;
            }

            if (state == LockState.Stale)
            {
                // 删失败不在这里收尾：下面的写入会再失败一次，由统一的 catch 决定是否放行。
                TryDelete(path);
                _log.Info($"接管僵死的实例锁：{path}（原 PID {existing?.Pid ?? 0}）");
            }

            var info = new InstanceLockInfo
            {
                Pid = Environment.ProcessId,
                ProcessStartUtc = GetProcessStartUtc(Environment.ProcessId),
                VersionName = versionName,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Token = Guid.NewGuid().ToString("N"),
                GameDirectory = directory,
            };

            // tmp 名带令牌：两个进程同时抢锁时各写各的临时文件，不会互相踩到半截内容。
            var temporary = path + "." + info.Token[..8] + ".tmp";

            try
            {
                var json = JsonSerializer.Serialize(info, JsonOptions);
                await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);

                // 互斥点就在这一行：不覆盖的 Move 是原子的，先到者成功，后到者拿到 IOException。
                File.Move(temporary, path);
            }
            catch (IOException)
            {
                // 目标已存在（被别人抢先）或文件被占用：删掉自己的 tmp，重读一次再决定。
                TryDelete(temporary);
                continue;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporary);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                _log.Warn($"实例运行锁：无法写入 {path}，本次不做重复启动检查。", ex);
                return NoopInstanceLockHandle.Instance;
            }

            return new LockHandle(this, path, info);
        }

        return null;
    }

    /// <summary>
    /// 把锁记录原子地替换成一份新内容（用于「持有者从启动器改成游戏」）。
    /// 与 <see cref="TryAcquireAsync"/> 一样先写 tmp 再 Move；这里允许覆盖，
    /// 因为只有持有者自己会调用它（令牌校验在 <see cref="LockHandle.UpdateProcessId"/>）。
    /// </summary>
    private bool Rewrite(string path, InstanceLockInfo info)
    {
        var temporary = path + "." + info.Token[..8] + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(info, JsonOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            TryDelete(temporary);
            _log.Warn($"更新实例运行锁失败（锁继续归启动器所有）：{path}", ex);
            return false;
        }
    }

    /// <summary>
    /// 只删自己写的那份锁。令牌不一致说明锁已被别的进程接管（我们曾被判成僵死），
    /// 这时删文件等于把别人的重复启动保护关掉。
    /// </summary>
    private void Release(string path, InstanceLockInfo owned)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var current = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), JsonOptions);

            if (current is null || !string.Equals(current.Token, owned.Token, StringComparison.Ordinal))
            {
                _log.Warn($"实例运行锁已被接管，保留现有锁文件：{path}");
                return;
            }

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 释放失败只会留下一个可被接管的锁文件（PID 退出后自动判定僵死），不该影响启动结果。
            _log.Warn($"释放实例运行锁失败：{path}", ex);
        }
    }

    private (LockState State, InstanceLockInfo? Info) InspectDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return (LockState.None, null);
        }

        string path;
        try
        {
            path = Path.Combine(Path.GetFullPath(gameDirectory), LockFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return (LockState.None, null);
        }

        return Inspect(path);
    }

    private (LockState State, InstanceLockInfo? Info) Inspect(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (LockState.None, null);
            }

            var info = JsonSerializer.Deserialize<InstanceLockInfo>(File.ReadAllText(path), JsonOptions);

            // 缺字段等同于损坏：没有启动时间就没法排除 PID 复用，宁可按僵死接管。
            if (info is null
                || info.Pid <= 0
                || info.ProcessStartUtc == default
                || string.IsNullOrWhiteSpace(info.Token))
            {
                _log.Warn($"实例锁文件内容不完整，按僵死处理：{path}");
                return (LockState.Stale, info);
            }

            return IsProcessAlive(info) ? (LockState.Live, info) : (LockState.Stale, info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 损坏的锁文件一律视为僵死：这是「用户手改过 / 上次写盘被打断」的唯一出口。
            _log.Warn($"实例锁文件损坏，按僵死处理：{path}", ex);
            return (LockState.Stale, null);
        }
    }

    /// <summary>
    /// 持锁进程是否还活着。取不到任何进程信息（已退出 / 权限不足 / 平台差异）都算僵死，
    /// 这是刻意的：宁可让一个真正在跑的跨用户进程被接管，也不要让实例永久启动不了。
    /// </summary>
    private static bool IsProcessAlive(InstanceLockInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.Pid);

            if (process.HasExited)
            {
                return false;
            }

            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (actual - info.ProcessStartUtc).Duration() <= ProcessStartTolerance;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>本进程的启动时间（UTC）。取不到时退回当前时间：写进锁文件只为判活，宁可粗一点。</summary>
    private static DateTimeOffset GetProcessStartUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就交给调用方的写入失败分支处理。
        }
    }

    private enum LockState
    {
        /// <summary>没有锁文件。</summary>
        None = 0,

        /// <summary>锁文件属于一个还活着的进程。</summary>
        Live = 1,

        /// <summary>僵死或损坏：可以接管。</summary>
        Stale = 2,
    }

    private sealed class LockHandle : IInstanceLockHandle
    {
        private readonly InstanceLockService _owner;
        private readonly string _path;
        private readonly object _sync = new();
        private int _disposed;

        public LockHandle(InstanceLockService owner, string path, InstanceLockInfo info)
        {
            _owner = owner;
            _path = path;
            Info = info;
        }

        public InstanceLockInfo Info { get; }

        public bool UpdateProcessId(int processId, DateTimeOffset processStartUtc)
        {
            if (processId <= 0 || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            lock (_sync)
            {
                Info.Pid = processId;
                Info.ProcessStartUtc = processStartUtc;
                return _owner.Rewrite(_path, Info);
            }
        }

        public void Dispose()
        {
            // 幂等：LaunchAsync 的 finally 与调用方的 using 可能重复释放。
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_path, Info);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}