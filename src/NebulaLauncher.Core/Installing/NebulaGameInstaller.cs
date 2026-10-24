using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 替换 CmlLib 自带的 <c>ParallelGameInstaller</c>，但复用 <see cref="GameInstallerBase"/> 的
/// 下载（<c>Download</c>）、校验（<c>NeedUpdate</c>）与进度上报，只改写调度部分。
///
/// 两个需要绕开的库内问题：
///
/// 1. **文件锁**。库默认并发度取自 CPU 核数，而文件清单里存在指向同一落盘路径的重复条目，
///    两个任务同时写同一目标文件就会撞锁，被包成 AggregateException 让整个安装失败
///    （1.12.2 有 1519 个文件，稳定踩中）。这里按 <c>Path</c> 严格去重并固定并发度。
///
/// 2. **临时目录**。Forge / NeoForge 安装器把 installer.jar 指向
///    <c>&lt;temp&gt;/&lt;随机目录&gt;/installer.jar</c> 却不创建那个目录，写入时抛
///    DirectoryNotFoundException。这里对绝对路径补建父目录。
///
/// 注意：必须调用基类 <c>Download</c> 再 <c>ExecuteUpdateTask</c>。
/// 只调 <c>ExecuteUpdateTask</c> 对安装器自造的文件是空操作（它的 UpdateTask 集合为空），
/// 表现为「进度显示 100% 但文件不存在」。
///
/// 逐文件状态通过可选的 <see cref="IDownloadTaskScope"/> 回传给下载队列的任务面板；
/// 不传时行为与之前完全一致（只有 CmlLib 的聚合进度）。
/// </summary>
internal sealed class NebulaGameInstaller : GameInstallerBase
{
    private readonly int _maxParallel;
    private readonly int _maxRetries;
    private readonly IDownloadTaskScope? _scope;

    public NebulaGameInstaller(
        HttpClient httpClient,
        int maxParallel = 8,
        int maxRetries = 3,
        IDownloadTaskScope? scope = null)
        : base(httpClient)
    {
        _maxParallel = Math.Max(1, maxParallel);
        _maxRetries = Math.Max(1, maxRetries);
        _scope = scope;
    }

    protected override async ValueTask Install(IEnumerable<GameFile> gameFiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameFiles);

        // 按落盘路径去重：同一 Path 只处理一次，从源头消除同文件竞争。
        var unique = new List<GameFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in gameFiles)
        {
            if (string.IsNullOrEmpty(file.Url) || string.IsNullOrEmpty(file.Path))
            {
                continue;
            }

            if (IsExcludedPath(file.Path))
            {
                continue;
            }

            if (!seen.Add(file.Path))
            {
                continue;
            }

            unique.Add(file);
        }

        var totalFiles = unique.Count;
        var totalBytes = unique.Sum(file => Math.Max(0L, file.Size));
        var doneFiles = 0;
        var doneBytes = 0L;

        // 先把整批文件以「排队」铺进面板：3000 个文件的安装要让用户第一眼就看到
        // 有多少东西要下，而不是等第一个文件开始才知道总量。
        if (_scope is not null)
        {
            foreach (var file in unique)
            {
                Report(file, 0, DownloadTaskState.Queued, error: null);
            }
        }

        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);

        var workers = unique.Select(RunOne).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        FireByteProgress(new ByteProgress(Math.Max(0, totalBytes), Math.Max(0, doneBytes)));
        return;

        async Task RunOne(GameFile file)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureParentDirectory(file.Path);

                FireFileProgress(totalFiles, Volatile.Read(ref doneFiles), file.Name, InstallerEventType.Queued);

                var skipped = false;
                Report(file, 0, DownloadTaskState.Running, error: null);

                if (NeedUpdate(file))
                {
                    await DownloadWithRetry(file, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // 已经正确：不下载，但它仍是本批次里「不用再下」的一个文件。
                    skipped = true;
                }

                // 下载流程之外的附加任务（解压、复制等）仍需执行，与库内行为一致。
                await file.ExecuteUpdateTask(cancellationToken).ConfigureAwait(false);

                var finished = Interlocked.Increment(ref doneFiles);
                var written = Interlocked.Add(ref doneBytes, Math.Max(0L, file.Size));
                var size = Math.Max(0L, file.Size);

                Report(
                    file,
                    size,
                    skipped ? DownloadTaskState.Skipped : DownloadTaskState.Completed,
                    error: null);

                FireFileProgress(totalFiles, finished, file.Name, InstallerEventType.Done);
                FireByteProgress(new ByteProgress(Math.Max(0, totalBytes), Math.Max(0, written)));
            }
            catch (OperationCanceledException)
            {
                // 取消不算失败：作用域会被 Dispose 成「取消批次」，不写历史。
                throw;
            }
            catch (Exception ex)
            {
                // 面板要回答的是「哪几个文件 403」，所以失败原因必须带上文件名与地址
                // （异常本身由上层包装成 InstallerFileException 或原样抛出）。
                Report(file, 0, DownloadTaskState.Failed, ex.Message);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>把一个 CmlLib 文件映射成任务面板条目。总量与已下载量都用文件清单里的 Size。</summary>
    private void Report(GameFile file, long downloadedBytes, DownloadTaskState state, string? error)
    {
        if (_scope is null)
        {
            return;
        }

        var size = Math.Max(0L, file.Size);

        // 进行中不报总量：CmlLib 的 Download 不提供文件内字节进度，
        // 报一个 1024/1024 的「下载中」会显示成 0% 卡住；总量未知时面板显示「—」。
        var totalBytes = state is DownloadTaskState.Queued or DownloadTaskState.Running ? 0 : size;
        var path = file.Path;

        _scope.Report(
            DisplayName(file),
            GroupOf(path),
            totalBytes,
            downloadedBytes,
            state,
            error,
            file.Url,
            // 相对路径由库按 MinecraftPath 解析，这里给不出绝对路径；
            // 给不出落盘路径的条目不会出现在「只重试失败项」里，但名字与失败原因仍然可读。
            !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) ? path : null,
            // 不传 file.Hash：CmlLib 的 Hash 在不同文件类型上不保证是 SHA1，
            // 拿它当 ExpectedSha1 会让重试永远校验失败。重试时按无哈希下载。
            sha1: null);
    }

    /// <summary>面板上的短名。CmlLib 的 <c>Name</c> 已经是文件名（<c>1.20.1.json</c>）。</summary>
    private static string DisplayName(GameFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Name))
        {
            return file.Name;
        }

        var name = Path.GetFileName(file.Path ?? string.Empty);
        return string.IsNullOrWhiteSpace(name) ? "游戏文件" : name;
    }

    /// <summary>
    /// 分组取落盘路径的顶层目录（libraries / assets / versions / runtime）。
    /// 面板按 Group 排序，靠它一眼看出卡在资源文件还是库里；认不出来的归到「游戏文件」。
    /// </summary>
    private static string GroupOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "游戏文件";
        }

        var normalized = path.Replace('\\', '/');
        foreach (var group in KnownGroups)
        {
            if (normalized.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/" + group + "/", StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return "游戏文件";
    }

    private static readonly string[] KnownGroups = ["libraries", "assets", "versions", "runtime"];

    private async Task DownloadWithRetry(GameFile file, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Download(file, progress: null, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt + 1 < _maxRetries && IsTransient(ex))
            {
                // 文件被占用这类失败，等占用方释放句柄后重试。
                var delay = 150 * (attempt + 1) + Random.Shared.Next(50, 250);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 包装成带文件名的异常：HttpRequestException 本身不带 URL，
                // 排查「哪个文件 404」时无从下手。
                throw new InstallerFileException(file.Name, file.Url, ex);
            }
        }
    }

    /// <summary>绝对路径（安装器临时文件）需要自己建父目录；相对路径由库按 MinecraftPath 解析。</summary>
    private static void EnsureParentDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static bool IsTransient(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
