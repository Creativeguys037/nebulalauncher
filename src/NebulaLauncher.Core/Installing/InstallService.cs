using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installers;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Paths;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// 版本安装：原版走 CmlLib 安装器，加载器走各自的官方 meta。
/// 失败没有结果对象可用（<see cref="IInstallService.InstallAsync"/> 返回 Task），
/// 因此统一抛出带中文原因的异常，由 UI 顶部边界捕获展示。
/// </summary>
public sealed partial class InstallService : IInstallService, IDisposable
{
    private readonly ILauncherPaths _paths;
    private readonly IPlatformService _platform;
    private readonly IJavaService _java;

    /// <summary>为 null 时说明是测试注入的 <see cref="HttpClient"/>（没有源切换能力）。</summary>
    private readonly DownloadHttpClientFactory? _http;

    private readonly HttpClient _client;
    private readonly IAppLog? _log;

    /// <summary>
    /// 下载队列。生产路径注入单例，让安装的逐文件进度出现在下载页的任务面板里；
    /// 测试用内部构造注入 <see cref="HttpClient"/> 时为 null（没有队列可上报，行为与之前一致）。
    /// </summary>
    private readonly DownloadQueue? _queue;

    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>
    /// 在线资源库。只有「Fabric API 随 Fabric 一起装」这一个用途。
    /// 为 null 时该步骤直接跳过并记日志——测试替身与预览宿主不注册它。
    /// </summary>
    private readonly IModrinthService? _modrinth;

    /// <summary>
    /// Fabric API 在 Modrinth 上的项目 slug。
    ///
    /// 为什么写死：slug 是项目永久别名（不像 project id 会随迁移变化），且这里没有
    /// 「搜到的是不是正主」的可靠判据——按名字搜 <c>fabric api</c> 的首条可能是第三方兼容层。
    /// 来源：Modrinth 的 <c>https://modrinth.com/mod/fabric-api</c>（PCL2 同样只查固定一处，
    /// 见 <c>PageDownloadInstall.xaml.vb:987</c> 的 <c>DlFabricApiLoader</c>）。
    /// CurseForge 侧不查这一项：它按 classId 分派，本项目没有为 Fabric API 建对应常量。
    /// </summary>
    public const string FabricApiSlug = "fabric-api";

    private DownloadSource _source = DownloadSource.BmclApi;

    public InstallService(
        ILauncherPaths paths,
        IPlatformService platform,
        IJavaService java,
        DownloadHttpClientFactory httpClientFactory,
        IAppLog? log = null,
        DownloadQueue? downloadQueue = null,
        IModrinthService? modrinth = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _http = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _client = httpClientFactory.Client;
        _log = log;
        _queue = downloadQueue;
        _modrinth = modrinth;
    }

    /// <summary>
    /// 测试专用：直接注入 <see cref="HttpClient"/>。
    ///
    /// <see cref="DownloadHttpClientFactory"/> 在构造时就绑定了真实的 <c>MirrorRewritingHandler</c>，
    /// 假 <c>HttpMessageHandler</c> 无法从外面替换进去；而修复档位的判据（sha1 不符才重下、
    /// 0 字节才重下）必须用可控网络才能稳定复现。
    /// </summary>
    internal InstallService(
        ILauncherPaths paths,
        IPlatformService platform,
        IJavaService java,
        HttpClient httpClient,
        IAppLog? log,
        DownloadQueue? downloadQueue = null,
        IModrinthService? modrinth = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _http = null;
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
        _queue = downloadQueue;
        _modrinth = modrinth;
    }

    public DownloadSource Source
    {
        get => _source;
        set
        {
            _source = value;

            // 测试注入的 HttpClient 不经过镜像重写，没有「当前源」可切。
            if (_http is not null)
            {
                _http.Source = value;
            }
        }
    }

    /// <inheritdoc />
    public async Task InstallAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? fabricApiVersionId = null)
    {
        await InstallCoreAsync(request, progress, fabricApiVersionId, overwriteExisting: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 装一个加载器版本并返回版本目录名，供「给已有实例换加载器」使用。
    ///
    /// 换加载器时**刻意不随装 Fabric API**：那是「新建一个 Fabric 实例」时的便利，
    /// 而换加载器不该往用户已有的 <c>mods/</c> 里塞东西——「换了加载器之后模组列表莫名多了一个」
    /// 是个无法向用户解释的现象。
    /// </summary>
    public async Task<string?> InstallLoaderAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        return await InstallCoreAsync(request, progress, fabricApiVersionId: null, overwriteExisting, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 安装主体。抽出来的唯一理由：<see cref="InstallAsync"/> 是 <c>Task</c>，
    /// 而换加载器需要知道**装出来的版本目录名**（Forge / NeoForge 的名字由安装器决定，
    /// 调用方推算不出来）。流程与失败处理与原实现逐行一致。
    ///
    /// <paramref name="overwriteExisting"/> 只有一个作用点：Forge / NeoForge 的
    /// <c>SkipIfAlreadyInstalled</c>。其余分支本来就是逐文件校验补全（原版、Fabric/Quilt 会整份重写
    /// profile JSON），天然是覆盖语义。
    /// </summary>
    private async Task<string> InstallCoreAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress,
        string? fabricApiVersionId,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MinecraftVersion))
        {
            throw new ArgumentException("必须指定 Minecraft 版本", nameof(request));
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 在 try 外声明：catch 里要把失败批次收尾（写进历史），finally 里兜底释放。
        IDownloadTaskScope? taskScope = null;
        try
        {
            Source = request.Source;

            // 安装只需要 versions/libraries/assets 三处可写。
            // 之前直接调 EnsureCreated()，任何一个「与安装无关」的目录（如 instances）创建失败
            // 都会把整个安装连坐成 "Access to the path '...\instances' is denied"。
            EnsureInstallEnvironment();

            var minecraftVersion = request.MinecraftVersion.Trim();

            // 游戏本体的逐文件进度走下载队列的「外部批次」：下载页的任务面板与普通下载
            // 共用同一套快照 / 失败项 / 批次历史。队列没注册或面板正忙时降级为只上报聚合进度。
            taskScope = TryBeginTaskScope($"安装 {minecraftVersion}");

            var installedName = request.Loader switch
            {
                LoaderKind.Vanilla => await InstallVanillaAsync(minecraftVersion, request, progress, taskScope, cancellationToken)
                    .ConfigureAwait(false),
                LoaderKind.Fabric or LoaderKind.Quilt =>
                    await InstallFabricLikeAsync(request.Loader, minecraftVersion, request, progress, taskScope, cancellationToken)
                        .ConfigureAwait(false),
                LoaderKind.Forge => await InstallForgeAsync(minecraftVersion, request, progress, taskScope, overwriteExisting, cancellationToken)
                    .ConfigureAwait(false),
                LoaderKind.NeoForge => await InstallNeoForgeAsync(minecraftVersion, request, progress, taskScope, overwriteExisting, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new NotSupportedException($"不支持的加载器：{request.Loader}"),
            };

            await WriteLoaderMarkerAsync(installedName, request, cancellationToken).ConfigureAwait(false);

            // Fabric API 放在加载器装完之后、清单写之前：它落在 mods/ 而不是 versions/，
            // 与组件清单无关，但要在加载器版本目录已经存在之后再动 mods 目录。
            if (request.Loader == LoaderKind.Fabric)
            {
                await TryInstallFabricApiAsync(minecraftVersion, fabricApiVersionId, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 清单是「组件是否还在」的廉价判据（比对 mtime）。它只服务于后续修复/校验，
            // 写失败不影响安装结果，所以放在安装成功之后并自带 try/catch。
            await WriteInstallManifestAsync(installedName, request, cancellationToken).ConfigureAwait(false);

            // 批次在清单写完之后收尾：面板上的「完成」与磁盘上的状态一致。
            taskScope?.Complete();
            _log?.Info($"安装完成：{installedName}");

            return installedName;
        }
        catch (OperationCanceledException)
        {
            // 取消不写批次历史（Dispose 路径处理），保留「取消的批次不算结束」这条语义。
            _log?.Info($"安装已取消：{request.MinecraftVersion} / {request.Loader}");
            throw;
        }
        catch (Exception ex)
        {
            // 失败的批次也要收尾：面板要能回答「这次安装是哪几个文件没下来」。
            // 收尾本身出错不能盖掉安装的原始异常。
            CompleteQuietly(taskScope);

            // 之前失败只把 ex.Message 抛给 UI，用户看到的多是英文栈信息，日志里也没有记录。
            // 这里统一落日志，并把最常见的几类失败翻译成可执行的中文提示。
            _log?.Error($"安装失败：{request.MinecraftVersion} / {request.Loader}", ex);
            throw new InvalidOperationException(DescribeInstallFailure(request, ex), ex);
        }
        finally
        {
            taskScope?.Dispose();
            _installGate.Release();
        }
    }

    /// <summary>
    /// 尝试开一个外部批次（安装器的逐文件上报）。
    ///
    /// 队列未注册（测试注入 HttpClient 的构造）或面板正被别的批次占用时返回 null：
    /// 逐文件进度是附加信息，不能因为任务面板忙就把整个安装判失败。
    /// </summary>
    private IDownloadTaskScope? TryBeginTaskScope(string name)
    {
        if (_queue is null)
        {
            return null;
        }

        try
        {
            return _queue.BeginExternalBatch(name);
        }
        catch (InvalidOperationException ex)
        {
            _log?.Warn($"下载任务面板正被其他批次占用，本次「{name}」的逐文件进度不会显示", ex);
            return null;
        }
    }

    /// <summary>收尾一个可能为 null 的作用域；收尾失败只记日志，不影响安装结果。</summary>
    private void CompleteQuietly(IDownloadTaskScope? scope)
    {
        try
        {
            scope?.Complete();
        }
        catch (Exception ex)
        {
            _log?.Warn("下载任务批次收尾失败（安装结果不受影响）", ex);
        }
    }

    /// <summary>
    /// 安装前置检查：确认游戏目录能创建、磁盘空间够。
    ///
    /// 刻意不做「写临时文件」探测：杀软/EDR/受控目录策略会拦掉随机探测文件，
    /// 而同一目录里真正的游戏文件往往能写。用探测结果判定「不可装」会把可用目录判死，
    /// 这是之前「装不了游戏」的一部分原因。真正的写入失败由 CmlLib 抛出，
    /// 再由 <see cref="DescribeInstallFailure"/> 翻译成带路径与原因的中文提示。
    /// </summary>
    private void EnsureInstallEnvironment()
    {
        // EnsureCreated 已改为逐目录容错，这里不会再因为「与安装无关的目录」失败而中断。
        _paths.EnsureCreated();

        // 从只读介质（安装盘、只读共享、压缩包解压后继承属性）拷来的 .minecraft 会整棵树带
        // ReadOnly 属性。目录本身带只读时后面的写入都会失败，先清一次；清不掉不影响后续流程，
        // 真正的失败仍由下面的循环与 DescribeInstallFailure 报出来。
        TryClearReadOnlyMinecraftDirectory();

        var required = new (string Label, string Path)[]
        {
            ("版本目录", _paths.VersionsDirectory),
            ("库文件目录", _paths.LibrariesDirectory),
            ("资源目录", _paths.AssetsDirectory),
        };

        var failures = new List<string>();

        // 只给一次「清只读 + 重试」的机会：受组策略或 OneDrive 占位文件控制的目录清不掉，
        // 反复试探只会把失败时间拖长。
        var readonlyRetried = false;

        foreach (var (label, path) in required)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (!readonlyRetried)
                {
                    readonlyRetried = true;
                    var reason = PathProbe.TryMakeWritable(_paths.MinecraftDirectory);

                    if (reason is null)
                    {
                        _log?.Info($"游戏目录带只读属性，已清除只读属性：{_paths.MinecraftDirectory}");

                        try
                        {
                            Directory.CreateDirectory(path);
                            continue;
                        }
                        catch (Exception retryEx)
                        {
                            failures.Add($"{label} {path}：{retryEx.Message}（已尝试清除只读属性）");
                            continue;
                        }
                    }

                    failures.Add($"{label} {path}：{ex.Message}（已尝试清除只读属性，未成功：{reason}）");
                    continue;
                }

                failures.Add($"{label} {path}：{ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"{label} {path}：{ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InstallTargetException(
                _paths.MinecraftDirectory,
                $"游戏目录不可用，无法安装：{string.Join("；", failures)}。" +
                "请在「设置 → 游戏 → 游戏目录」换到可写位置后重试。");
        }

        var space = PathProbe.CheckFreeSpace(_paths.MinecraftDirectory);
        if (space is not null)
        {
            throw new InstallTargetException(
                _paths.MinecraftDirectory,
                $"游戏目录所在磁盘空间不足：{space}。请清理磁盘，或在「设置 → 游戏 → 游戏目录」换一个可用空间更大的目录。");
        }
    }

    /// <summary>
    /// 游戏目录本身带只读属性时清一次（目录 + 一级文件）。清不掉只记日志：
    /// 真正的失败仍由 <see cref="EnsureInstallEnvironment"/> 的目录循环或后续写文件报出来。
    /// </summary>
    private void TryClearReadOnlyMinecraftDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = _paths.MinecraftDirectory;

        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReadOnly) == 0)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _log?.Debug($"读取游戏目录属性失败：{ex.Message}");
            return;
        }

        var reason = PathProbe.TryMakeWritable(root);

        if (reason is null)
        {
            _log?.Info($"游戏目录带只读属性，已清除只读属性：{root}");
        }
        else
        {
            _log?.Warn($"游戏目录带只读属性，清除失败：{root}（{reason}）");
        }
    }

    /// <summary>
    /// 覆盖写文件前把旧内容原地备份成 <c>&lt;name&gt;.bak</c>（只保留一份，不累积），
    /// 并在 Windows 上去掉目标文件的只读位。
    ///
    /// 为什么要在覆盖前备份：<c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 与游戏写出来的
    /// <c>options.txt</c> 都可能被用户手工改过，启动器直接覆盖会让改动无声消失；
    /// 留一份 <c>.bak</c> 至少能找回。备份放原目录（不新建全局备份树），与 HMCL
    /// <c>GameDirectoryManager.forceOverwriteGameDirectoryFiles</c> 的「就地保留一份」一致。
    ///
    /// 备份失败不阻断写入：它的价值是留个底，不是安装的前置条件。
    /// </summary>
    /// <returns>备份文件路径；目标文件原本不存在或备份失败时返回 <c>null</c>。</returns>
    internal static string? BackupBeforeOverwrite(string path, IAppLog? log = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var backupPath = path + ".bak";
            File.Copy(path, backupPath, overwrite: true);

            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(path);

                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }

            return backupPath;
        }
        catch (Exception ex)
        {
            log?.Warn($"覆盖前备份 {path} 失败（继续覆盖）", ex);
            return null;
        }
    }

    /// <summary>把底层异常翻译成用户能据此行动的一句话。</summary>
    private static string DescribeInstallFailure(InstallRequest request, Exception ex)
    {
        var what = request.Loader == LoaderKind.Vanilla
            ? $"Minecraft {request.MinecraftVersion}"
            : $"{request.Loader} {request.MinecraftVersion}";

        var root = ex;
        while (root.InnerException is not null && root.InnerException != root)
        {
            root = root.InnerException;
        }

        // 目标目录问题自带完整建议（含具体路径与操作入口），不再叠加通用提示。
        if (root is InstallTargetException target)
        {
            return $"{what} 安装失败：{target.Message}";
        }

        var hint = root switch
        {
            IOException => "文件被占用或磁盘写入失败。请关闭正在运行的游戏/启动器，或检查磁盘空间后重试。",
            UnauthorizedAccessException =>
                "没有写入权限。请在「设置 → 游戏 → 游戏目录」换一个可写目录，或关闭安全软件对 .minecraft 的拦截后重试。",
            System.Net.Http.HttpRequestException => "下载失败。请检查网络，或在设置里切换下载源后重试。",
            System.Net.Sockets.SocketException => "网络连接异常。请检查网络或代理设置后重试。",
            _ => null,
        };

        return hint is null
            ? $"{what} 安装失败：{root.Message}"
            : $"{what} 安装失败：{hint}（底层原因：{root.Message}）";
    }

    public async Task<bool> VerifyAsync(
        string versionName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return false;
        }

        try
        {
            var launcher = CreateLauncher();
            var adapter = new InstallProgressAdapter(versionName, progress);
            await launcher.InstallAsync(versionName, adapter.FileProgress, adapter.ByteProgress, cancellationToken)
                .ConfigureAwait(false);
            adapter.Complete(versionName);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Error($"校验版本 {versionName} 失败", ex);
            return false;
        }
    }

    public void Dispose() => _installGate.Dispose();

    // ---------------- 各加载器 ----------------

    private async Task<string> InstallVanillaAsync(
        string minecraftVersion,
        InstallRequest request,
        IProgress<DownloadProgress>? progress,
        IDownloadTaskScope? taskScope,
        CancellationToken cancellationToken)
    {
        var launcher = CreateLauncher(taskScope);
        var adapter = new InstallProgressAdapter(minecraftVersion, progress);
        await launcher.InstallAsync(minecraftVersion, adapter.FileProgress, adapter.ByteProgress, cancellationToken)
            .ConfigureAwait(false);
        adapter.Complete(minecraftVersion);
        return request.ResultVersionName ?? minecraftVersion;
    }

    private async Task<string> InstallFabricLikeAsync(
        LoaderKind loader,
        string minecraftVersion,
        InstallRequest request,
        IProgress<DownloadProgress>? progress,
        IDownloadTaskScope? taskScope,
        CancellationToken cancellationToken)
    {
        var loaderVersion = request.LoaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            loaderVersion = await FetchLatestLoaderVersionAsync(loader, minecraftVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            throw new InvalidOperationException($"未能获取 {loader} 的可用版本（{minecraftVersion}）");
        }

        var versionName = request.ResultVersionName ?? BuildLoaderVersionName(loader, minecraftVersion, loaderVersion);
        var profileUrls = LoaderMetaUrls.Profile(loader, minecraftVersion, loaderVersion, _source);

        if (profileUrls.Count == 0)
        {
            throw new NotSupportedException($"{loader} 没有 profile JSON 安装方式");
        }

        // 官方域名在国内会随机解析失败，候选列表里带 BMCLAPI 的 fabric-meta 镜像。
        var json = await ResourceFetcher.GetStringAsync(_client, profileUrls, cancellationToken, _log)
            .ConfigureAwait(false);
        var node = JsonNode.Parse(json)
                   ?? throw new InvalidOperationException($"{loader} 返回的 profile JSON 无法解析");

        // 版本目录名必须与 JSON 内的 id 一致，否则 CmlLib 找不到该版本。
        node["id"] = versionName;

        var versionDirectory = Path.Combine(_paths.VersionsDirectory, versionName);
        Directory.CreateDirectory(versionDirectory);
        var jsonPath = Path.Combine(versionDirectory, versionName + ".json");

        // 覆盖前留一份 .bak：用户可能手工改过这个 profile（改内存、改参数），
        // 直接覆盖会让改动无声消失。只保留一份，不累积。
        _ = BackupBeforeOverwrite(jsonPath, _log);

        await File.WriteAllTextAsync(jsonPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        _log?.Info($"已写入 {loader} profile：{jsonPath}");

        // profile JSON 只描述自身增量，原版与库文件交给 CmlLib 补全（沿 inheritsFrom 链）。
        var launcher = CreateLauncher(taskScope);
        var adapter = new InstallProgressAdapter(versionName, progress);
        await launcher.InstallAsync(versionName, adapter.FileProgress, adapter.ByteProgress, cancellationToken)
            .ConfigureAwait(false);
        adapter.Complete(versionName);

        return versionName;
    }

    /// <summary>
    /// 把选定的 Fabric API 装进 <c>mods/</c>（PCL2 <c>PageDownloadInstall.xaml.vb:1014-1019</c> 的
    /// <c>AutoSelectedFabricApi</c>：Fabric 选中后默认勾上最新一个兼容版本）。
    ///
    /// 三条取舍：
    /// 1. **失败不抛异常**。加载器本体已经在磁盘上了，Fabric API 装不上只是少一个模组，
    ///    把它变成「整个安装失败」会让用户连版本都拿不到——而重试一次就能补上；
    /// 2. 目标目录取 <see cref="ILauncherPaths.MinecraftDirectory"/>。安装阶段还没有实例，
    ///    版本隔离在首次启动时才建目录，此时 mods 只能落在全局目录；
    /// 3. 版本 Id 对不上（Modrinth 那边删档 / 换了 filter）时退化为列表里的第一条：
    ///    UI 传进来的是它当时看到的列表首项，重查一次是为了拿完整模型而不是只凭一个 Id。
    /// </summary>
    private async Task TryInstallFabricApiAsync(
        string minecraftVersion,
        string? fabricApiVersionId,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fabricApiVersionId))
        {
            return;
        }

        if (_modrinth is null)
        {
            _log?.Warn("在线资源库未就绪，跳过 Fabric API 安装");
            return;
        }

        try
        {
            var versions = await _modrinth
                .GetVersionsAsync(FabricApiSlug, minecraftVersion, LoaderKind.Fabric, cancellationToken)
                .ConfigureAwait(false);

            var pick = PickFabricApiVersion(versions, fabricApiVersionId);

            if (pick is null)
            {
                _log?.Warn($"没有与 Minecraft {minecraftVersion} 兼容的 Fabric API 版本，跳过");
                return;
            }

            var result = await _modrinth
                .InstallAsync(pick, _paths.MinecraftDirectory, ContentKind.Mod, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                _log?.Info($"已安装 Fabric API {pick.VersionNumber}：{result.FilePath}");
            }
            else
            {
                _log?.Warn($"Fabric API 安装失败（{pick.VersionNumber}）：{result.Error}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 网络抖动、Modrinth 5xx、磁盘满：都只影响这一个模组。
            _log?.Warn("安装 Fabric API 失败（加载器已装好，不影响启动）", ex);
        }
    }

    /// <summary>
    /// 从 Modrinth 的候选里挑出要装的那个 Fabric API 版本。
    ///
    /// 优先用界面传进来的 Id（那是用户当时看到的那一条）；找不到就退回首项。
    /// 退化的必要性：从打开面板到点「开始安装」之间可能过了几分钟，
    /// Modrinth 那边完全可能刚好把那个版本撤掉（作者删档 / 改 filter），
    /// 这时候「装不上」比「装个相邻的兼容版本」更糟——Fabric API 自身跨小版本兼容。
    /// </summary>
    internal static ModrinthVersion? PickFabricApiVersion(
        IReadOnlyList<ModrinthVersion> versions,
        string? preferredId)
    {
        if (versions.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var trimmed = preferredId.Trim();

            foreach (var version in versions)
            {
                if (string.Equals(version.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return version;
                }
            }
        }

        // 列表由 Modrinth 按发布时间降序给出：首项就是最新的兼容版本。
        return versions[0];
    }

    private async Task<string> InstallForgeAsync(
        string minecraftVersion,
        InstallRequest request,
        IProgress<DownloadProgress>? progress,
        IDownloadTaskScope? taskScope,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        var launcher = CreateLauncher(taskScope);

        // Forge 安装分三段，进度映射见 InstallStagePlan：原版 0–50%、加载器 50–85%、处理器 85–100%。
        // 安装器 stdout 单独走日志通道（截断 / 限量 / 折叠 / 失败时提升尾部）。
        var outputLog = new InstallerOutputLog("Forge", _log);
        var adapter = new InstallProgressAdapter(
            $"Forge {minecraftVersion}", progress, LoaderKind.Forge, staged: true, _log, outputLog);

        try
        {
            // Forge 安装器自己会拉起 java 跑安装 jar，必须先确保原版存在。
            adapter.Enter(InstallStage.Vanilla);
            await launcher.InstallAsync(minecraftVersion, adapter.FileProgress, adapter.ByteProgress, cancellationToken)
                .ConfigureAwait(false);

            // 安装器的中间文件必须落在我们控制的目录，见 EnterLoaderTempScope 的说明。
            using var tempScope = EnterLoaderTempScope();

            var forge = new ForgeInstaller(launcher, _client);
            var options = new ForgeInstallOptions
            {
                JavaPath = await ResolveJavaPathAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
                FileProgress = adapter.FileProgress,
                ByteProgress = adapter.ByteProgress,
                // CmlLib 只把 stdout 交给处理器执行器，因此第一行输出就是「开始跑处理器」的信号。
                InstallerOutput = adapter.OutputProgress,
                CancellationToken = cancellationToken,
                // 换加载器要「按目标重装一遍」：遇到残缺的同名版本目录必须重装而不是跳过。
                SkipIfAlreadyInstalled = !overwriteExisting,
            };

            adapter.Enter(InstallStage.LoaderDownload);

            string installedName;
            if (string.IsNullOrWhiteSpace(request.LoaderVersion))
            {
                installedName = await forge.Install(minecraftVersion, options).ConfigureAwait(false);
            }
            else
            {
                installedName = await forge.Install(minecraftVersion, request.LoaderVersion, options)
                    .ConfigureAwait(false);
            }

            adapter.Enter(InstallStage.Finalize);
            adapter.Complete(installedName);
            return request.ResultVersionName ?? installedName;
        }
        catch (OperationCanceledException)
        {
            // 用户取消：清阶段文案，但不把 stdout 尾部当「失败现场」提升。
            adapter.Abort(flushOutput: false);
            throw;
        }
        catch
        {
            // 失败：清掉阶段文案，别让界面停在「正在执行 Forge 安装器…」，
            // 并把安装器最后 20 行提升为 Info——处理器报错只存在于 stdout 里。
            adapter.Abort();
            throw;
        }
    }

    private async Task<string> InstallNeoForgeAsync(
        string minecraftVersion,
        InstallRequest request,
        IProgress<DownloadProgress>? progress,
        IDownloadTaskScope? taskScope,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        var launcher = CreateLauncher(taskScope);
        var outputLog = new InstallerOutputLog("NeoForge", _log);
        var adapter = new InstallProgressAdapter(
            $"NeoForge {minecraftVersion}", progress, LoaderKind.NeoForge, staged: true, _log, outputLog);

        try
        {
            adapter.Enter(InstallStage.Vanilla);
            await launcher.InstallAsync(minecraftVersion, adapter.FileProgress, adapter.ByteProgress, cancellationToken)
                .ConfigureAwait(false);

            using var tempScope = EnterLoaderTempScope();

            // CmlLib.Core.Installer.Forge 与 CmlLib.Core.Installer.NeoForge 都定义了
            // CmlLib.Core.Installer.NeoForge.NeoForgeInstaller，只能按程序集限定名取用。
            var options = new CmlLib.Core.Installer.NeoForge.Installers.NeoForgeInstallOptions
            {
                JavaPath = await ResolveJavaPathAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
                FileProgress = adapter.FileProgress,
                ByteProgress = adapter.ByteProgress,
                InstallerOutput = adapter.OutputProgress,
                CancellationToken = cancellationToken,
                // 同 Forge：换加载器时允许覆盖已存在的版本目录。
                SkipIfAlreadyInstalled = !overwriteExisting,
            };

            adapter.Enter(InstallStage.LoaderDownload);

            var installedName = await NeoForgeInstallerBridge.InstallAsync(
                    launcher,
                    _client,
                    minecraftVersion,
                    string.IsNullOrWhiteSpace(request.LoaderVersion) ? null : request.LoaderVersion,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);

            adapter.Enter(InstallStage.Finalize);
            adapter.Complete(installedName);
            return request.ResultVersionName ?? installedName;
        }
        catch (OperationCanceledException)
        {
            adapter.Abort(flushOutput: false);
            throw;
        }
        catch
        {
            adapter.Abort();
            throw;
        }
    }

    // ---------------- 辅助 ----------------

    /// <summary>
    /// 统一构造入口。参数设定（NebulaGameInstaller / 摘 Java 提取器）只在
    /// <see cref="MinecraftLauncherFactory"/> 里写一遍，启动补全走同一份，见该类的说明。
    /// </summary>
    private MinecraftLauncher CreateLauncher(IDownloadTaskScope? taskScope = null) =>
        MinecraftLauncherFactory.Create(
            new MinecraftPath(_paths.MinecraftDirectory),
            _client,
            taskScope);

    private async Task<string?> ResolveJavaPathAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        var java = await _java.ResolveForVersionAsync(minecraftVersion, null, cancellationToken).ConfigureAwait(false);
        if (java is null)
        {
            _log?.Warn($"{minecraftVersion} 未找到合适的 Java，加载器安装可能失败");
        }

        return java?.Path;
    }

    /// <summary>
    /// Forge / NeoForge 安装器把 installer jar 解到 <see cref="Path.GetTempPath"/> 下的随机目录。
    /// 当 %TEMP% 不可写、或被杀软/清理工具回收时，安装会以
    ///   DirectoryNotFoundException: Could not find a part of the path '...\Temp\xxx\installer.jar'
    /// 失败。HMCL 一类启动器把这类中间产物放在自己的缓存目录，这里做同样的事：
    /// 安装期间把 TMP/TEMP 指向 <c>cache/loader-install</c>，结束后恢复原值。
    ///
    /// 安装过程由 <c>_installGate</c> 串行化，不存在并发改环境变量的竞争。
    /// </summary>
    private TempScope EnterLoaderTempScope()
    {
        var workDir = Path.Combine(_paths.CacheDirectory, "loader-install");
        Directory.CreateDirectory(workDir);

        var previousTmp = Environment.GetEnvironmentVariable("TMP");
        var previousTemp = Environment.GetEnvironmentVariable("TEMP");

        Environment.SetEnvironmentVariable("TMP", workDir);
        Environment.SetEnvironmentVariable("TEMP", workDir);

        return new TempScope(previousTmp, previousTemp);
    }

    /// <summary>恢复 TMP/TEMP。解析路径的库在每次调用时读环境变量，所以作用域退出即生效。</summary>
    private sealed class TempScope(string? previousTmp, string? previousTemp) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Environment.SetEnvironmentVariable("TMP", previousTmp);
            Environment.SetEnvironmentVariable("TEMP", previousTemp);
            _disposed = true;
        }
    }

    public static string BuildLoaderVersionName(LoaderKind loader, string minecraftVersion, string loaderVersion) =>
        loader switch
        {
            LoaderKind.Fabric => $"fabric-loader-{loaderVersion}-{minecraftVersion}",
            LoaderKind.Quilt => $"quilt-loader-{loaderVersion}-{minecraftVersion}",
            _ => $"{minecraftVersion}-{loader.ToString().ToLowerInvariant()}",
        };

    private async Task<string?> FetchLatestLoaderVersionAsync(
        LoaderKind loader,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var candidates = LoaderMetaUrls.Versions(loader, minecraftVersion, _source);
        if (candidates.Count == 0)
        {
            return null;
        }

        try
        {
            var json = await ResourceFetcher.GetStringAsync(_client, candidates, cancellationToken, _log)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            string? first = null;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("loader", out var loaderNode)
                    || !loaderNode.TryGetProperty("version", out var versionNode)
                    || versionNode.GetString() is not { Length: > 0 } version)
                {
                    continue;
                }

                first ??= version;
                var stable = loaderNode.TryGetProperty("stable", out var stableNode)
                             && stableNode.ValueKind == JsonValueKind.True;
                if (stable)
                {
                    return version;
                }
            }

            return first;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"获取 {loader} 版本列表失败", ex);

            // 之前这里返回 null，上层只能报「未能获取可用版本」，用户无从判断是网络问题。
            throw new InvalidOperationException(
                $"获取 {loader} 版本列表失败（已尝试 {candidates.Count} 个地址）：{ex.GetBaseException().Message}", ex);
        }
    }

    private async Task WriteLoaderMarkerAsync(
        string versionName,
        InstallRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return;
        }

        var directory = Path.Combine(_paths.VersionsDirectory, versionName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var marker = new JsonObject
        {
            ["loader"] = request.Loader.ToString(),
            ["loaderVersion"] = request.LoaderVersion,
            ["minecraftVersion"] = request.MinecraftVersion,
            ["installedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        try
        {
            var markerPath = Path.Combine(directory, "nebula-loader.json");
            _ = BackupBeforeOverwrite(markerPath, _log);

            await File.WriteAllTextAsync(
                markerPath,
                marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"写入加载器标记失败：{versionName}", ex);
        }
    }

    /// <summary>
    /// 把 CmlLib 的两路进度（逐文件 / 字节）与安装器 stdout 合并成 <see cref="DownloadProgress"/>。
    ///
    /// 分阶段模式（Forge / NeoForge）下，阶段内比例被映射到总进度的固定区间：
    /// 原版 0–50%、加载器下载 50–85%、处理器执行 85–100%；总比例只增不减。
    /// 非分阶段模式（原版 / Fabric / Quilt / 校验）比例就是阶段内比例、<c>Stage</c> 始终为 null，
    /// 行为与改造前一致。
    ///
    /// 处理器阶段的进入信号来自安装器 stdout（<c>InstallerOutput</c>）：
    /// CmlLib 只把 stdout 交给处理器执行器（Forge 2.0.0 的 <c>ForgeV12Installer.Install</c> 把
    /// <c>FileProgress</c> 与 <c>InstallerOutput</c> 一起传给 <c>MapAndStartProcessors</c>），
    /// 下载阶段不产生任何输出，所以第一行输出即「开始跑处理器」。
    /// </summary>
    internal sealed class InstallProgressAdapter(
        string label,
        IProgress<DownloadProgress>? target,
        LoaderKind loader = LoaderKind.Vanilla,
        bool staged = false,
        IAppLog? log = null,
        InstallerOutputLog? outputLog = null)
        : IProgress<InstallerProgressChangedEventArgs>, IProgress<ByteProgress>, IProgress<string>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();

        private InstallStage _stage = InstallStage.Vanilla;
        private int _totalFiles;
        private int _completedFiles;
        private long _totalBytes;
        private long _doneBytes;
        private double _lastReportAt = -1;
        private double _lastSampleAt;
        private long _lastSampleBytes;
        private double _speed;
        private string? _current;
        private double _lastRatio;
        private bool _finished;

        /// <summary>
        /// 当前子批次在总进度里占的区间 [起, 止]。
        ///
        /// CmlLib 在加载器阶段会分好几批上报（内层原版复检 → 安装器本体 → maven 依赖），
        /// 每批的计数各自从 0 开始。用「每批吃掉剩余区间的一半」分配：
        /// 三批时区间是 50–67.5% / 67.5–76.25% / 76.25–85%，每批都有可见进度，
        /// 又不会因为某一批跑完就把整段顶满（上一版用固定权重时，内层复检一秒钟就把进度顶到 85%）。
        /// </summary>
        private double _bandStart;

        private double _bandEnd;

        public IProgress<InstallerProgressChangedEventArgs> FileProgress => this;

        public IProgress<ByteProgress> ByteProgress => this;

        /// <summary>安装器 stdout（Forge / NeoForge）：只用来判定处理器阶段与记日志。</summary>
        public IProgress<string> OutputProgress => this;

        /// <summary>当前阶段（诊断与单测用）。</summary>
        public InstallStage Stage
        {
            get
            {
                lock (_gate)
                {
                    return _stage;
                }
            }
        }

        /// <summary>
        /// 进入下一个阶段。第一次调用总会立刻上报一次阶段起点，界面不必等到第一个文件才显示阶段。
        /// 非分阶段模式与已结束的适配器忽略该调用。
        /// </summary>
        public void Enter(InstallStage stage)
        {
            if (!staged)
            {
                return;
            }

            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                if (_stage == stage && _lastReportAt >= 0)
                {
                    return;
                }

                _stage = stage;
                if (_lastReportAt >= 0)
                {
                    // 阶段切换：阶段内计数重新开始，总比例由 _lastRatio 兜住不回退。
                    _totalFiles = 0;
                    _completedFiles = 0;
                    _totalBytes = 0;
                    _doneBytes = 0;
                    _current = null;
                    _lastSampleAt = _clock.Elapsed.TotalSeconds;
                    _lastSampleBytes = 0;
                    _speed = 0;
                }

                _bandStart = InstallStagePlan.Start(stage);
                _bandEnd = stage == InstallStage.LoaderDownload
                    ? _bandStart + ((InstallStagePlan.End(stage) - _bandStart) / 2)
                    : InstallStagePlan.End(stage);
            }

            log?.Info($"{label}：进入阶段 {stage}（{InstallStagePlan.Describe(stage, loader)}）");
            Emit(force: true);
        }

        /// <summary>失败：保留当前比例、清掉阶段文案，避免界面上留下「正在执行安装器…」。</summary>
        /// <param name="flushOutput">
        /// true（安装失败）时把安装器 stdout 的最后若干行提升为 Info；
        /// false（用户取消）时只把挂起行写掉，不制造「失败现场」。
        /// </param>
        public void Abort(bool flushOutput = true)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
            }

            if (flushOutput)
            {
                outputLog?.FlushFailure();
            }
            else
            {
                outputLog?.Complete();
            }

            log?.Info($"{label}：安装中断，停止上报阶段");
            Emit(force: true);
        }

        void IProgress<InstallerProgressChangedEventArgs>.Report(InstallerProgressChangedEventArgs value)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                // 总量变了就是新的一批（内层原版复检 → 安装器本体 → maven 依赖 → 处理器）。
                if (staged && _stage == InstallStage.LoaderDownload && _totalFiles > 0 && value.TotalTasks != _totalFiles)
                {
                    AdvanceBandLocked();
                }

                _totalFiles = value.TotalTasks;
                _completedFiles = value.ProgressedTasks;
                _current = value.Name;
            }

            Emit(force: value.EventType == InstallerEventType.Done);
        }

        void IProgress<ByteProgress>.Report(ByteProgress value)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                _totalBytes = value.TotalBytes;
                _doneBytes = value.ProgressedBytes;
            }

            Emit();
        }

        void IProgress<string>.Report(string value)
        {
            // 每一行安装器输出都进日志通道（截断 / 限量 / 折叠在 InstallerOutputLog 里做），
            // 处理器失败时这就是唯一的原始报错来源。
            outputLog?.Append(value);

            if (!staged || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            lock (_gate)
            {
                // 只有「加载器下载 → 处理器」这个方向需要 stdout 触发；
                // 其它阶段的输出（若有）不改变阶段。
                if (_finished || _stage != InstallStage.LoaderDownload)
                {
                    return;
                }
            }

            Enter(InstallStage.LoaderProcessors);
        }

        /// <summary>安装成功：比例到 1，阶段文案清空。已中断的适配器不再上报。</summary>
        public void Complete(string name)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
                _current = name;
                if (_totalBytes > 0)
                {
                    _doneBytes = _totalBytes;
                }

                _completedFiles = _totalFiles;
            }

            outputLog?.Complete();
            Emit(force: true, finalRatio: 1);
        }

        /// <summary>
        /// 进入下一个子批次：新区间 = [上一个区间的终点, 上一个终点 + 剩余的一半]。
        /// 加载器阶段最多被切成「剩余区间不断减半」的若干段，进度永远收不到 85%，
        /// 直到真正进入处理器阶段（那里从 85% 起）。
        /// </summary>
        private void AdvanceBandLocked()
        {
            var stageEnd = InstallStagePlan.End(InstallStage.LoaderDownload);

            _bandStart = _bandEnd;
            _bandEnd = _bandStart + ((stageEnd - _bandStart) / 2);
        }

        /// <summary>阶段内比例 → 总进度。</summary>
        private double MappedLocked(double local) => _stage == InstallStage.LoaderDownload
            ? _bandStart + (local * (_bandEnd - _bandStart))
            : InstallStagePlan.Map(_stage, local);

        private void Emit(bool force = false, double? finalRatio = null)
        {
            if (target is null)
            {
                return;
            }

            lock (_gate)
            {
                var now = _clock.Elapsed.TotalSeconds;
                if (!force && now - _lastReportAt < 0.1)
                {
                    return;
                }

                if (now - _lastSampleAt >= 0.25)
                {
                    var delta = _doneBytes - _lastSampleBytes;
                    var dt = now - _lastSampleAt;
                    if (dt > 0 && delta >= 0)
                    {
                        var instant = delta / dt;
                        _speed = _speed <= 0 ? instant : (_speed * 0.7) + (instant * 0.3);
                    }

                    _lastSampleBytes = _doneBytes;
                    _lastSampleAt = now;
                }

                _lastReportAt = now;

                var local = _totalBytes > 0
                    ? Math.Clamp((double)_doneBytes / _totalBytes, 0, 1)
                    : _totalFiles > 0
                        ? Math.Clamp((double)_completedFiles / _totalFiles, 0, 1)
                        : 0;

                double ratio;
                string? stageText;

                if (_finished)
                {
                    ratio = finalRatio ?? _lastRatio;
                    stageText = null;
                }
                else if (!staged)
                {
                    ratio = local;
                    stageText = null;
                }
                else
                {
                    // 只增不减：阶段之间的比例不能回退，否则界面上的进度条会往回跳。
                    ratio = Math.Max(_lastRatio, MappedLocked(local));
                    stageText = InstallStagePlan.Describe(_stage, loader);
                }

                _lastRatio = ratio;

                target.Report(new DownloadProgress(
                    _totalFiles,
                    _completedFiles,
                    0,
                    _totalBytes,
                    _doneBytes,
                    _speed,
                    _current ?? label,
                    ratio,
                    stageText));
            }
        }
    }
}
