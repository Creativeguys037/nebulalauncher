using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Versions;

/// <summary>
/// 服务端 <c>server.jar</c> 的清单与下载。
///
/// 清单来源：Mojang 版本清单（经 <see cref="IVersionService"/>，带 30 分钟缓存）→
/// 每个版本的 JSON 里的 <c>downloads.server</c>。这一步是逐版本查询而不是一次拿到：
/// 清单本身不含下载信息，而「哪些版本有服务端」只有打开各自的 JSON 才知道。
///
/// 扫描策略（默认）只取最近 <see cref="DefaultReleaseWindow"/> 个正式版 + 最近
/// <see cref="DefaultSnapshotWindow"/> 个快照，再显式保证 <c>latest.release</c> 被扫到：
/// 玩家开服基本只用近两年的版本，而全量扫描要逐个请求一百多个 JSON。
/// 「扫描全部」是用户显式点的操作，代价明确写在按钮上。
///
/// 落盘缓存：<c>&lt;cache&gt;/server-versions.json</c>，TTL <see cref="CacheTtl"/>。
/// 命中且未过期时**完全不发网络请求**；过期时先返回旧值（stale-while-revalidate），
/// 后台刷新完成后由调用方通过 <see cref="EnsureRefreshedAsync"/> 取新结果。
///
/// 下载复用 <see cref="DownloadHttpClientFactory"/>：镜像重写、候选回退、失败重试都在
/// 已有的 handler 与 <see cref="DownloadQueue"/> 里，这里不另写 HttpClient。
/// </summary>
public sealed class ServerDownloadService : IServerDownloadService, IDisposable
{
    /// <summary>落盘缓存文件名（位于 <c>ILauncherPaths.CacheDirectory</c>）。</summary>
    public const string CacheFileName = "server-versions.json";

    /// <summary>
    /// 服务端启动脚本的文件名，与 <c>server-&lt;id&gt;.jar</c> 同目录。
    ///
    /// 名字照抄 PCL2 <c>ModDownloadLib.vb:196</c>（<c>Launch Server.bat</c>）：
    /// 从 PCL 迁过来的用户凭这个文件名就能找到入口，不需要再学一套约定。
    /// </summary>
    public const string LaunchScriptFileName = "Launch Server.bat";

    /// <summary>缓存格式版本。字段/语义变更时递增，旧文件会被当成损坏静默重建。</summary>
    public const int CacheFormatVersion = 1;

    /// <summary>默认扫描窗口：最近多少个正式版。</summary>
    public const int DefaultReleaseWindow = 40;

    /// <summary>默认扫描窗口：最近多少个快照（快照不一定提供 server.jar）。</summary>
    public const int DefaultSnapshotWindow = 5;

    /// <summary>
    /// 逐版本查询 JSON 的并发度。
    ///
    /// 实测（本机外网）：全量 123 个版本，并发 8 要 35 秒，并发 16 约 4–18 秒
    /// （同一网络下波动很大）。再高对 Mojang 的 piston-meta 有被限流的风险。
    /// </summary>
    private const int MaxParallelRequests = 16;

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 启动脚本的落盘编码：跟着**系统 ANSI 代码页（ACP）**走。
    ///
    /// <para>
    /// 为什么不能固定 GB18030：<c>cmd.exe</c> 按系统 ACP 解析批处理文件。中文 Windows 默认 936，
    /// 这时 GB18030 存的中文可读；用户打开「Beta: 使用 Unicode UTF-8」后 ACP 变成 65001，
    /// 同一份脚本的中文就全成乱码。反过来，65001 的机器上必须写 UTF-8。
    /// </para>
    /// <para>
    /// 为什么不用 <see cref="Encoding.Default"/> 做判据：PCL2 的写法是
    /// <c>If Encoding.Default.Equals(Encoding.UTF8) Then UTF8 Else GB18030</c>，
    /// 那在 .NET Framework 上成立（Default 跟着 ACP 走）。但 **.NET 8 里
    /// <see cref="Encoding.Default"/> 恒为 UTF-8**，照搬这个判据会让 936 的机器也写 UTF-8，
    /// 中文全部乱码。所以必须 P/Invoke <c>kernel32!GetACP()</c> 拿真实代码页。
    /// </para>
    /// <para>
    /// UTF-8 分支必须**无 BOM**：带 BOM 时 cmd 把首行读成 <c>'\uFEFF@echo off'</c>，
    /// 报「不是内部或外部命令」，整个脚本从第一行起就失效（task-19 实测）。
    /// </para>
    /// </summary>
    private static readonly Encoding LaunchScriptEncoding = CreateLaunchScriptEncoding();

    /// <summary>GB18030（代码页 54936，GBK 的超集）。</summary>
    internal const int Gb18030CodePage = 54936;

    /// <summary>UTF-8 代码页。系统开启「Beta: 使用 Unicode UTF-8」后 ACP 就是这个值。</summary>
    internal const int Utf8CodePage = 65001;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>
    /// 系统 ANSI 代码页（ACP）。非 Windows 或 P/Invoke 失败时返回 0，调用方按 GB18030 处理。
    /// </summary>
    internal static int GetSystemCodePage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            return (int)GetACP();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 目标平台是 Windows，这里只是不让异常冒出来；0 会走 GB18030 回落。
            return 0;
        }
    }

    /// <summary>
    /// 按 ACP 选落盘编码：65001 → UTF-8（无 BOM），其它 → GB18030。
    ///
    /// 抽成纯函数是为了直接单测「936 / 65001 / 其它」三格，不用改本机区域设置。
    /// </summary>
    internal static Encoding ChooseLaunchScriptEncoding(int acp)
    {
        // .NET 8 默认不带代码页编码，取 54936 前必须先注册；注册是进程级且幂等的。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (acp == Utf8CodePage)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        try
        {
            return Encoding.GetEncoding(Gb18030CodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 注册之后理论上取不到不会发生。真发生时用不带 BOM 的 UTF-8 兜底：
            // 脚本至少能写出来，不该因为编码把整次下载弄失败。
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    private static Encoding CreateLaunchScriptEncoding() => ChooseLaunchScriptEncoding(GetSystemCodePage());

    private readonly IVersionService _versions;
    private readonly HttpClient _http;
    private readonly IAppLog? _log;
    private readonly ILauncherPaths? _paths;
    private readonly Func<DateTimeOffset> _clock;

    private readonly SemaphoreSlim _listGate = new(1, 1);
    private readonly object _stateGate = new();

    private IReadOnlyList<ServerVersionInfo>? _items;
    private DateTimeOffset _cachedAt;
    private string? _latestRelease;
    private bool _coversAll;
    private bool _diskChecked;
    private Task<ServerListSnapshot>? _refreshTask;

    public ServerDownloadService(
        IVersionService versions,
        DownloadHttpClientFactory httpClientFactory,
        IAppLog? log = null,
        ILauncherPaths? launcherPaths = null)
        : this(
            versions,
            (httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory))).Client,
            log,
            launcherPaths,
            clock: null)
    {
    }

    /// <summary>
    /// 直接用现成 <see cref="HttpClient"/> 的构造：测试注入假 handler 走这个重载。
    /// </summary>
    /// <param name="launcherPaths">为 null 时不落盘（纯内存缓存）；测试可指向临时目录。</param>
    /// <param name="clock">可注入的时钟，用来稳定测 TTL 边界。</param>
    public ServerDownloadService(
        IVersionService versions,
        HttpClient httpClient,
        IAppLog? log = null,
        ILauncherPaths? launcherPaths = null,
        Func<DateTimeOffset>? clock = null)
    {
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
        _paths = launcherPaths;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>缓存有效期。过期的缓存仍然先返回（stale-while-revalidate），只是会触发后台刷新。</summary>
    public static TimeSpan CacheTtl => TimeSpan.FromHours(6);

    /// <summary>缓存文件路径；未注入 <see cref="ILauncherPaths"/> 时为 null（只走内存缓存）。</summary>
    public string? CacheFilePath
    {
        get
        {
            var directory = _paths?.CacheDirectory;

            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.Combine(directory, CacheFileName);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServerVersionInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await ListSnapshotAsync(forceRefresh: false, fullScan: false, cancellationToken)
            .ConfigureAwait(false);

        return snapshot.Items;
    }

    /// <inheritdoc />
    public async Task<ServerListSnapshot> ListSnapshotAsync(
        bool forceRefresh = false,
        bool fullScan = false,
        CancellationToken cancellationToken = default)
    {
        if (forceRefresh || fullScan)
        {
            return await RefreshAsync(fullScan, cancellationToken).ConfigureAwait(false);
        }

        if (TryReadMemory(out var memory))
        {
            return IsFresh(memory!.Value.CachedAt)
                ? Snapshot(memory.Value, ServerListSource.Cache, refreshing: false)
                : StartBackgroundRefreshAndReturnStale(memory.Value);
        }

        await _listGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 等锁期间别的调用可能已经把磁盘缓存装上内存了。
            if (TryReadMemory(out memory))
            {
                return IsFresh(memory!.Value.CachedAt)
                    ? Snapshot(memory.Value, ServerListSource.Cache, refreshing: false)
                    : StartBackgroundRefreshAndReturnStale(memory.Value);
            }

            if (!_diskChecked)
            {
                _diskChecked = true;

                var document = await TryReadDiskCacheAsync(cancellationToken).ConfigureAwait(false);

                if (document is not null)
                {
                    var cachedItems = document.Items.Select(ToInfo).ToArray();

                    Adopt(cachedItems, document.CachedAtUtc, document.LatestRelease, document.CoversAll);
                    _log?.Info(
                        $"服务端清单来自缓存：{cachedItems.Length} 个版本，缓存时间 {document.CachedAtUtc:u}");

                    var state = new MemoryState(
                        cachedItems,
                        document.CachedAtUtc,
                        document.LatestRelease,
                        document.CoversAll);

                    return IsFresh(state.CachedAt)
                        ? Snapshot(state, ServerListSource.Cache, refreshing: false)
                        : StartBackgroundRefreshAndReturnStale(state);
                }
            }
        }
        finally
        {
            _listGate.Release();
        }

        // 既没有内存缓存也没有可用的磁盘缓存：第一次只能同步扫描。
        return await RefreshAsync(fullScan: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ServerListSnapshot> EnsureRefreshedAsync(CancellationToken cancellationToken = default)
    {
        Task<ServerListSnapshot>? running;

        lock (_stateGate)
        {
            running = _refreshTask;
        }

        if (running is { IsCompleted: false })
        {
            return await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ListSnapshotAsync(forceRefresh: true, fullScan: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ServerDownloadResult> DownloadAsync(
        string versionId,
        string targetDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var id = (versionId ?? string.Empty).Trim();

        if (id.Length == 0)
        {
            return Failure(id, string.Empty, "版本号为空", started);
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return Failure(id, string.Empty, "目标目录为空", started);
        }

        string directory;

        try
        {
            directory = Path.GetFullPath(targetDirectory.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(id, targetDirectory, $"目标目录无效：{ex.Message}", started);
        }

        var filePath = Path.Combine(directory, $"server-{id}.jar");

        var info = await FindAsync(id, cancellationToken).ConfigureAwait(false);

        if (info?.Url is null)
        {
            return Failure(id, filePath, $"版本 {id} 没有提供官方服务端下载", started);
        }

        // 已存在且 SHA1 对得上：不重下。50 MB 的文件对流量敏感的用户不是小事。
        if (File.Exists(filePath) && info.Sha1 is { Length: > 0 } expected)
        {
            var existingSha1 = await TryComputeSha1Async(filePath, cancellationToken).ConfigureAwait(false);

            if (string.Equals(existingSha1, expected, StringComparison.OrdinalIgnoreCase))
            {
                // jar 没重下，但脚本可能是旧的下载（本功能上线前）留下的，或者是用户删掉的。
                // 这里只补缺失的那份：文件存在就不动，用户可能已经把 java 换成完整路径、改过内存。
                TryWriteLaunchScript(directory, Path.GetFileName(filePath), info.Id);

                return new ServerDownloadResult
                {
                    VersionId = info.Id,
                    FilePath = filePath,
                    Size = SafeLength(filePath),
                    Sha1 = existingSha1,
                    AlreadyExisted = true,
                    Success = true,
                    Duration = started.Elapsed,
                };
            }
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failure(id, filePath, $"无法创建目录 {directory}：{ex.Message}", started);
        }

        var queue = new DownloadQueue(_http, _log, maxConcurrency: 4);
        DownloadBatchResult batch;

        try
        {
            batch = await queue.DownloadAsync(
                [
                    new DownloadItem
                    {
                        Url = info.Url,
                        DestinationPath = filePath,
                        Id = info.Id,
                        Group = $"server-{info.Id}",
                        ExpectedSize = info.Size,
                        ExpectedSha1 = info.Sha1,
                    },
                ],
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消也把目标文件删掉：半截文件在磁盘上比没有更糟糕。
            TryDelete(filePath);
            throw;
        }

        if (!batch.Success)
        {
            TryDelete(filePath);
            var error = batch.Failures.Count > 0 ? batch.Failures[0].Error : "下载失败";

            _log?.Warn($"服务端下载失败：{info.Id}（{filePath}）：{error}");
            return Failure(id, filePath, error, started);
        }

        // DownloadQueue 已按 ExpectedSha1 校验过，这里再算一次是为了把实际 SHA1 交给界面。
        var actualSha1 = await TryComputeSha1Async(filePath, cancellationToken).ConfigureAwait(false);

        if (info.Sha1 is { Length: > 0 } expectedSha1
            && !string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(filePath);
            return Failure(
                id,
                filePath,
                $"SHA1 校验失败：期望 {expectedSha1}，实际 {actualSha1 ?? "（读取失败）"}",
                started);
        }

        // 脚本必须跟着 jar 一起落地：用户拿到 server-<id>.jar 之后第一件事就是开服，
        // 没有脚本就得自己查 Java、拼 -jar ... nogui、处理 EULA 三件事。
        TryWriteLaunchScript(directory, Path.GetFileName(filePath), info.Id);

        return new ServerDownloadResult
        {
            VersionId = info.Id,
            FilePath = filePath,
            Size = SafeLength(filePath),
            Sha1 = actualSha1,
            Success = true,
            Duration = started.Elapsed,
        };
    }

    /// <summary>
    /// 服务端启动命令。bat 的启动行与界面「复制启动命令」按钮共用这一份字符串。
    ///
    /// <para>
    /// 为什么必须共用：两处各写一遍时曾经不一致——bat 里是
    /// <c>"java" -server -XX:+UseG1GC -Xmx6144M -Xms1024M -jar … nogui</c>，
    /// 而复制按钮给的是 <c>java -Xmx2G -jar … nogui</c>。用户按按钮粘出来跑，
    /// 得到的服务端少了 G1GC 与 <c>-server</c>、内存只有三分之一，与双击 bat 不是一回事。
    /// </para>
    /// <para>
    /// 引号保留：脚本会被用户连目录一起拷到服务器上，绝对路径换台机器就失效；而用户按 echo 提示
    /// 把 <c>"java"</c> 换成 <c>"C:\Program Files\Java\jdk-17\bin\java.exe"</c> 时引号是必需的。
    /// 粘到 shell（cmd / PowerShell）里同样成立。
    /// </para>
    /// </summary>
    /// <param name="jarFileName">同目录下的服务端 jar 文件名（不含路径）。</param>
    public static string BuildLaunchCommand(string jarFileName)
    {
        var jar = string.IsNullOrWhiteSpace(jarFileName) ? "server.jar" : jarFileName.Trim();
        return $"\"java\" -server -XX:+UseG1GC -Xmx6144M -Xms1024M -jar {jar} nogui";
    }

    /// <summary>
    /// 生成服务端启动脚本的内容（纯函数，便于单测直接比对）。
    ///
    /// 几个刻意的选择：
    /// <list type="bullet">
    /// <item><c>"java"</c> 写成带引号的裸命令，不写绝对路径：脚本会被用户连目录一起拷到服务器上，
    /// 绝对路径换台机器就失效。找不到 java 时让用户改这一行，所以 <c>echo</c> 里必须把这件事说清楚，
    /// 否则用户只会看到「'java' 不是内部或外部命令」然后卡住。</item>
    /// <item>参数照抄 PCL2（<c>ModDownloadLib.vb:192</c>）：<c>-server -XX:+UseG1GC -Xmx6144M -Xms1024M ... nogui</c>。
    /// 6144M 在内存小的机器上会直接起不来，所以额外 <c>echo</c> 一行说明怎么改小。</item>
    /// <item>结尾留 <c>pause</c>：双击运行的窗口在服务端停止或崩溃时会立刻关掉，
    /// 没有 pause 用户看不到任何错误信息。</item>
    /// </list>
    /// </summary>
    /// <param name="jarFileName">同目录下的服务端 jar 文件名（不含路径）。</param>
    /// <param name="versionId">Minecraft 版本号，用于窗口标题。</param>
    public static string BuildLaunchScript(string jarFileName, string versionId)
    {
        var id = string.IsNullOrWhiteSpace(versionId) ? "服务端" : versionId.Trim();
        var jar = string.IsNullOrWhiteSpace(jarFileName) ? "server.jar" : jarFileName.Trim();

        var script = new StringBuilder();

        // 行尾固定 CRLF：cmd.exe 能读 LF，但记事本打开时会被挤成一行，
        // 而"右键编辑这个脚本"正是我们让用户做的事。
        void Line(string text = "") => script.Append(text).Append("\r\n");

        Line("@echo off");
        Line($"title {id} 原版服务端");
        Line("echo 如果服务端立即停止，请右键编辑这个脚本，把下面那行开头的 \"java\" 换成完整的 java.exe 路径。");
        Line("echo 例如 \"C:\\Program Files\\Java\\jdk-17\\bin\\java.exe\"；java.exe 一般在 JDK 或 JRE 的 bin 文件夹里。");
        Line("echo ------------------------------");
        Line("echo 首次启动时服务端会生成 eula.txt。若提示 \"You need to agree to the EULA in order to run the server\"，");
        Line("echo 请打开 eula.txt，阅读并同意 Minecraft EULA 后，把文件最后一行的 eula=false 改成 eula=true，再重新运行本脚本。");
        Line("echo ------------------------------");
        Line("echo 若提示内存不足，请把下面那行的 -Xmx6144M 改小，例如改成 -Xmx2048M。");
        Line(BuildLaunchCommand(jar));
        Line("echo ----------------------");
        Line("echo 服务端已停止。");
        Line("pause");

        return script.ToString();
    }

    /// <summary>
    /// 写出 <see cref="LaunchScriptFileName"/>，已存在时不动。
    ///
    /// 不覆盖是硬要求：用户可能已经把 <c>java</c> 换成了完整路径、调过内存，
    /// 覆盖等于把他的手改抹掉，下次开服直接失败。
    /// </summary>
    private void TryWriteLaunchScript(string directory, string jarFileName, string versionId)
    {
        var path = Path.Combine(directory, LaunchScriptFileName);

        try
        {
            if (File.Exists(path))
            {
                _log?.Debug($"启动脚本已存在，保留用户版本：{path}");
                return;
            }

            File.WriteAllText(path, BuildLaunchScript(jarFileName, versionId), LaunchScriptEncoding);
            _log?.Info($"已生成服务端启动脚本：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 脚本只是便利：jar 已经下好了，写不出脚本不能把整次下载判成失败。
            _log?.Warn($"写入服务端启动脚本失败：{path}", ex);
        }
    }

    /// <summary>
    /// 从单个版本 JSON 里读 <c>downloads.server</c>。
    ///
    /// 纯函数（不碰网络与磁盘），是「只列带服务端的版本」这条判据的唯一定义处：
    /// 没有 <c>downloads</c>、没有 <c>server</c>、<c>url</c> 为空、JSON 坏掉都返回 null。
    /// </summary>
    public static ServerVersionInfo? ParseServerInfo(RemoteVersion version, string json)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("downloads", out var downloads)
                || downloads.ValueKind != JsonValueKind.Object
                || !downloads.TryGetProperty("server", out var server)
                || server.ValueKind != JsonValueKind.Object
                || !server.TryGetProperty("url", out var urlNode))
            {
                return null;
            }

            var url = urlNode.GetString();

            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var sha1 = server.TryGetProperty("sha1", out var shaNode) ? shaNode.GetString() : null;
            var size = server.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed)
                ? parsed
                : 0L;

            return new ServerVersionInfo
            {
                Id = version.Id,
                Type = version.Type,
                ReleaseTime = version.ReleaseTime,
                Url = url,
                Sha1 = string.IsNullOrWhiteSpace(sha1) ? null : sha1,
                Size = size,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 决定扫描哪些版本。
    ///
    /// 默认窗口 = 最近 <see cref="DefaultReleaseWindow"/> 个正式版 + 最近
    /// <see cref="DefaultSnapshotWindow"/> 个快照，并额外保证 <c>latest.release</c> 在列表里
    /// （清单的发行时间顺序并不总是严格单调，最新正式版必须一定被覆盖）。
    /// </summary>
    internal static List<RemoteVersion> SelectScanTargets(IReadOnlyList<RemoteVersion> remote, bool fullScan)
    {
        var releases = remote
            .Where(version => version.Channel == VersionChannel.Release)
            .OrderByDescending(version => version.ReleaseTime)
            .ToList();

        var snapshots = remote
            .Where(version => version.Channel == VersionChannel.Snapshot)
            .OrderByDescending(version => version.ReleaseTime)
            .ToList();

        if (fullScan)
        {
            return releases.Concat(snapshots).ToList();
        }

        var targets = releases.Take(DefaultReleaseWindow).ToList();

        var latest = ResolveLatestRelease(remote);

        if (latest is not null && targets.All(version => !string.Equals(version.Id, latest, StringComparison.Ordinal)))
        {
            var newest = releases.FirstOrDefault(version => string.Equals(version.Id, latest, StringComparison.Ordinal));

            if (newest is not null)
            {
                targets.Add(newest);
            }
        }

        targets.AddRange(snapshots.Take(DefaultSnapshotWindow));
        return targets;
    }

    /// <summary>
    /// 清单里最新正式版的 id，等价于 Mojang manifest 的 <c>latest.release</c>。
    ///
    /// 用「清单顺序里第一个 release」而不是「发行时间最大的那个」：manifest 的 versions 数组
    /// 本来就是按时间降序的，而个别版本的 <c>releaseTime</c> 字段出现过明显异常（写错年份），
    /// 按时间取最大会把 latest 判到另一个版本上。
    /// </summary>
    internal static string? ResolveLatestRelease(IReadOnlyList<RemoteVersion> remote)
    {
        foreach (var version in remote)
        {
            if (version.Channel == VersionChannel.Release)
            {
                return version.Id;
            }
        }

        return null;
    }

    private readonly record struct MemoryState(
        IReadOnlyList<ServerVersionInfo> Items,
        DateTimeOffset CachedAt,
        string? LatestRelease,
        bool CoversAll);

    private bool TryReadMemory(out MemoryState? state)
    {
        lock (_stateGate)
        {
            if (_items is null)
            {
                state = null;
                return false;
            }

            state = new MemoryState(_items, _cachedAt, _latestRelease, _coversAll);
            return true;
        }
    }

    private void Adopt(IReadOnlyList<ServerVersionInfo> items, DateTimeOffset cachedAt, string? latestRelease, bool coversAll)
    {
        lock (_stateGate)
        {
            _items = items;
            _cachedAt = cachedAt;
            _latestRelease = latestRelease;
            _coversAll = coversAll;
        }
    }

    private ServerListSnapshot StartBackgroundRefreshAndReturnStale(MemoryState state)
    {
        StartBackgroundRefresh();
        return Snapshot(state, ServerListSource.Cache, refreshing: true);
    }

    private void StartBackgroundRefresh()
    {
        lock (_stateGate)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                return;
            }

            _refreshTask = Task.Run(async () =>
            {
                try
                {
                    var snapshot = await RefreshAsync(fullScan: false, CancellationToken.None).ConfigureAwait(false);
                    _log?.Debug($"服务端清单后台刷新完成：{snapshot.Items.Count} 个版本");
                    return snapshot;
                }
                catch (Exception ex)
                {
                    _log?.Warn("服务端清单后台刷新失败，继续使用缓存", ex);

                    // 刷新失败也把「不再刷新」的旧快照给出去，UI 不会永远停在「正在后台更新」。
                    if (TryReadMemory(out var cachedState))
                    {
                        return Snapshot(cachedState!.Value, ServerListSource.Cache, refreshing: false);
                    }

                    return new ServerListSnapshot
                    {
                        Items = Array.Empty<ServerVersionInfo>(),
                        Source = ServerListSource.None,
                    };
                }
                finally
                {
                    lock (_stateGate)
                    {
                        _refreshTask = null;
                    }
                }
            });
        }
    }

    private async Task<ServerListSnapshot> RefreshAsync(bool fullScan, CancellationToken cancellationToken)
    {
        await _listGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var remote = await _versions.GetRemoteAsync(false, cancellationToken).ConfigureAwait(false);

            if (remote.Count == 0)
            {
                // 拿不到清单：有旧值就继续用旧值（不写缓存、不发刷新事件）。
                return TryReadMemory(out var stale)
                    ? Snapshot(stale!.Value, ServerListSource.Cache, refreshing: false)
                    : new ServerListSnapshot
                    {
                        Items = Array.Empty<ServerVersionInfo>(),
                        Source = ServerListSource.None,
                    };
            }

            var latestRelease = ResolveLatestRelease(remote);
            var targets = SelectScanTargets(remote, fullScan);
            var found = new ConcurrentBag<ServerVersionInfo>();

            var scanWatch = Stopwatch.StartNew();

            await Parallel.ForEachAsync(
                targets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelRequests,
                    CancellationToken = cancellationToken,
                },
                async (version, token) =>
                {
                    var info = await TryReadServerInfoAsync(version, token).ConfigureAwait(false);

                    if (info is not null)
                    {
                        found.Add(info);
                    }
                }).ConfigureAwait(false);

            scanWatch.Stop();

            var ordered = found.OrderByDescending(info => info.ReleaseTime).ToArray();
            var cachedAt = _clock();

            Adopt(ordered, cachedAt, latestRelease, fullScan);
            await TryWriteDiskCacheAsync(cachedAt, latestRelease, fullScan, ordered, cancellationToken)
                .ConfigureAwait(false);

            _log?.Info(
                $"服务端可下载版本：{ordered.Length} 个（扫描了 {targets.Count} 个版本，"
                + $"{(fullScan ? "全量" : "默认窗口")}，耗时 {scanWatch.Elapsed.TotalSeconds:0.00}s，"
                + $"latest.release={latestRelease ?? "?"}）");

            return Snapshot(
                new MemoryState(ordered, cachedAt, latestRelease, fullScan),
                ServerListSource.Network,
                refreshing: false);
        }
        finally
        {
            _listGate.Release();
        }
    }

    private static ServerListSnapshot Snapshot(MemoryState state, ServerListSource source, bool refreshing) =>
        new()
        {
            Items = state.Items,
            Source = source,
            CachedAt = state.CachedAt,
            IsRefreshing = refreshing,
            LatestRelease = state.LatestRelease,
            CoversAll = state.CoversAll,
        };

    private static ServerVersionInfo ToInfo(CacheItem item) => new()
    {
        Id = item.Id,
        Type = string.IsNullOrWhiteSpace(item.Type) ? "release" : item.Type,
        ReleaseTime = item.ReleaseTime,
        Size = item.Size,
        Sha1 = item.Sha1,
        Url = item.Url,
    };

    private bool IsFresh(DateTimeOffset cachedAt) => _clock() - cachedAt < CacheTtl;

    private async Task<CacheDocument?> TryReadDiskCacheAsync(CancellationToken cancellationToken)
    {
        var path = CacheFilePath;

        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<CacheDocument>(json, CacheJsonOptions);

            // 版本号不符、空列表、条目缺 url 都按损坏处理：静默重建，不抛。
            if (document is null
                || document.Version != CacheFormatVersion
                || document.Items.Count == 0
                || document.Items.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Url)))
            {
                _log?.Warn($"服务端清单缓存格式不可用，将重新扫描：{path}");
                return null;
            }

            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"读取服务端清单缓存失败，将重新扫描：{path}", ex);
            return null;
        }
    }

    private async Task TryWriteDiskCacheAsync(
        DateTimeOffset cachedAt,
        string? latestRelease,
        bool coversAll,
        IReadOnlyList<ServerVersionInfo> items,
        CancellationToken cancellationToken)
    {
        var path = CacheFilePath;

        if (path is null)
        {
            return;
        }

        try
        {
            var document = new CacheDocument
            {
                Version = CacheFormatVersion,
                CachedAtUtc = cachedAt,
                LatestRelease = latestRelease,
                CoversAll = coversAll,
                Items = items.Select(item => new CacheItem
                {
                    Id = item.Id,
                    Type = item.Type,
                    ReleaseTime = item.ReleaseTime,
                    Size = item.Size,
                    Sha1 = item.Sha1,
                    Url = item.Url,
                }).ToList(),
            };

            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(document, CacheJsonOptions);

            // 先写临时文件再原子替换：写到一半被杀掉时不会留下半截 JSON 让下次启动读到。
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            _log?.Debug($"服务端清单缓存已写入：{path}（{document.Items.Count} 个版本）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 缓存写不进去不影响本次结果，下次只是重新扫描。
            _log?.Warn($"写入服务端清单缓存失败：{path}", ex);
        }
    }

    private async Task<ServerVersionInfo?> TryReadServerInfoAsync(RemoteVersion version, CancellationToken cancellationToken)
    {
        try
        {
            // 镜像重写与候选回退在 DownloadHttpClientFactory 的 handler 里，这里只发请求。
            var json = await _http.GetStringAsync(version.Url, cancellationToken).ConfigureAwait(false);
            return ParseServerInfo(version, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单个版本查不到不影响整体列表：旧版本、清单缺失、网络抖动都走这条路。
            _log?.Debug($"查询 {version.Id} 的服务端信息失败：{ex.Message}");
            return null;
        }
    }

    private async Task<ServerVersionInfo?> FindAsync(string versionId, CancellationToken cancellationToken)
    {
        if (TryReadMemory(out var state))
        {
            // 手写循环而不是 LINQ：缓存是 IReadOnlyList，CA1826 要求可索引集合不要走 Enumerable。
            var items = state!.Value.Items;

            for (var i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, versionId, StringComparison.Ordinal))
                {
                    return items[i];
                }
            }
        }

        var remote = await _versions.GetRemoteAsync(false, cancellationToken).ConfigureAwait(false);

        foreach (var item in remote)
        {
            if (string.Equals(item.Id, versionId, StringComparison.Ordinal))
            {
                return await TryReadServerInfoAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "Mojang 官方 server.jar 的校验值就是 SHA1，必须与上游一致")]
    private static async Task<string?> TryComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
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
            // 删不掉也不能把异常抛给 UI：返回的错误里已经写明失败原因。
        }
    }

    private static ServerDownloadResult Failure(string versionId, string filePath, string error, Stopwatch started) =>
        new()
        {
            VersionId = versionId,
            FilePath = filePath,
            Success = false,
            Error = error,
            Duration = started.Elapsed,
        };

    /// <summary>释放内部信号量。DI 单例随容器一起释放；测试里用 using 即可。</summary>
    public void Dispose() => _listGate.Dispose();

    /// <summary>落盘缓存的 JSON 结构。字段改名/改语义时递增 <see cref="CacheFormatVersion"/>。</summary>
    private sealed class CacheDocument
    {
        public int Version { get; set; }

        public DateTimeOffset CachedAtUtc { get; set; }

        public string? LatestRelease { get; set; }

        public bool CoversAll { get; set; }

        public List<CacheItem> Items { get; set; } = [];
    }

    private sealed class CacheItem
    {
        public string Id { get; set; } = string.Empty;

        public string Type { get; set; } = "release";

        public DateTimeOffset ReleaseTime { get; set; }

        public long Size { get; set; }

        public string? Sha1 { get; set; }

        public string? Url { get; set; }
    }
}
