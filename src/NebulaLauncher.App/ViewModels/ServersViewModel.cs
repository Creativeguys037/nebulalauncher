using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Services;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 服务器收藏页。进入页面时并发 ping（并发上限 4），之后每 60 秒自动刷新一次。
/// 定时器与在途请求都在 <see cref="OnNavigatedFromAsync"/> 里停掉，避免切页后仍在后台跑。
/// </summary>
public sealed partial class ServersViewModel : PageViewModelBase
{
    /// <summary>同时进行的 ping 上限。收藏列表可能有几十条，全量并发会把 DNS 与连接池打满。</summary>
    private const int MaxConcurrentPings = 4;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private static readonly Regex HostPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly IServerService? _servers;
    private readonly IServerQueryService? _query;
    private readonly IAccountService? _accounts;
    private readonly IGameLauncher? _gameLauncher;
    private readonly ISettingsService? _settings;
    private readonly IPlatformService? _platform;
    private readonly IInstanceContext? _instanceContext;

    private readonly DispatcherTimer? _timer;
    private CancellationTokenSource? _lifetimeCts;
    private string? _editingId;
    private bool _loaded;
    private bool _settingsLoaded;

    public ServersViewModel(IServiceProvider services)
    {
        _servers = services.GetService(typeof(IServerService)) as IServerService;
        _query = services.GetService(typeof(IServerQueryService)) as IServerQueryService;
        _accounts = services.GetService(typeof(IAccountService)) as IAccountService;
        _gameLauncher = services.GetService(typeof(IGameLauncher)) as IGameLauncher;
        _settings = services.GetService(typeof(ISettingsService)) as ISettingsService;
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;

        // servers.dat 在游戏目录里，实例隔离时每个实例一份，所以必须经 IInstanceContext 取目录（§6.10）。
        _instanceContext = services.GetService(typeof(IInstanceContext)) as IInstanceContext;

        IsDemoMode = _servers is null;

        // 定时器只在 UI 线程上创建与启停；不使用 System.Threading.Timer 以免回调落到线程池上。
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += OnTimerTick;
    }

    public override string Title => "服务器";

    public override string Key => NavigationKeys.Servers;

    private string? _subtitle;

    /// <summary>基类 Subtitle 只有 getter；用私有方法 + 显式通知来更新它。</summary>
    public override string? Subtitle => _subtitle;

    private void SetSubtitle(string? value)
    {
        if (string.Equals(_subtitle, value, StringComparison.Ordinal))
        {
            return;
        }

        _subtitle = value;
        OnPropertyChanged(nameof(Subtitle));
    }

    public ObservableCollection<ServerCardViewModel> Servers { get; } = [];

    /// <summary>
    /// 当前生效的游戏目录：实例隔离时是实例目录，否则是全局 <c>.minecraft</c>。
    /// <c>servers.dat</c> 就躺在这一层，所以导入入口只能从这里取（§6.10）。
    /// </summary>
    public string GameDirectory
    {
        get
        {
            if (_instanceContext is not null)
            {
                try
                {
                    var directory = _instanceContext.GameDirectory;
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        return directory;
                    }
                }
                catch (Exception)
                {
                    // 实例上下文异常时退回全局目录：导入按钮不该让整页白屏。
                }
            }

            return string.Empty;
        }
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private bool _isDemoMode;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _formName = string.Empty;

    [ObservableProperty]
    private string _formAddress = string.Empty;

    [ObservableProperty]
    private string? _formError;

    [ObservableProperty]
    private string? _joiningServerId;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasInfo => !string.IsNullOrWhiteSpace(InfoText);

    public bool IsJoining => JoiningServerId is not null;

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnInfoTextChanged(string? value) => OnPropertyChanged(nameof(HasInfo));

    partial void OnJoiningServerIdChanged(string? value) => OnPropertyChanged(nameof(IsJoining));

    public override async Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            await LoadAsync();

            // 截图模式没有点击能力，「玩家」面板只能靠 --section players 摆出来。
            if (!TryApplyScreenshotPlayers())
            {
                await PingAllAsync(showBusy: false);
            }

            // 截图模式不联网，拿不到真实 favicon；图标形态只能靠 --section favicon 摆出来。
            TryApplyScreenshotFavicon();

            _loaded = true;
        }

        StartTimer();
    }

    public override Task OnNavigatedFromAsync()
    {
        // 必须停表并取消在途 ping：页面是缓存复用的，离开只是隐藏，
        // 不停的话切到别的页面后每 60 秒仍会打一轮网络。
        _timer?.Stop();
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 页面被容器释放时收尾。离开页面已经在 <see cref="OnNavigatedFromAsync"/> 里处理过，
    /// 这里只兜住「没离开过就退出」的情况——否则最后一个 CTS 从头到尾没人释放。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Stop();
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }

        base.Dispose(disposing);
    }

    [RelayCommand]
    private async Task RefreshAllAsync() => await PingAllAsync(showBusy: true);

    /// <summary>
    /// 从当前游戏目录的 <c>servers.dat</c> 导入 Minecraft 自带的服务器列表。
    /// 重复地址跳过、不覆盖已有名字；导入成功后立刻重新加载列表并探测状态。
    /// </summary>
    [RelayCommand]
    private async Task ImportFromServersDatAsync()
    {
        InfoText = null;
        ErrorText = null;

        if (_servers is null)
        {
            InfoText = "示例模式：Core 服务尚未就绪，未导入任何服务器。";
            return;
        }

        var directory = GameDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            ErrorText = "当前游戏目录不可用（实例上下文尚未就绪），无法读取 servers.dat。";
            return;
        }

        IsImporting = true;
        try
        {
            var added = await _servers.ImportFromServersDatAsync(directory);
            var summary = _servers.LastServersDatImport;

            if (summary is { FileExists: false })
            {
                InfoText = $"当前游戏目录下没有 servers.dat：{directory}";
                return;
            }

            await LoadAsync();
            if (added.Count > 0)
            {
                await PingAllAsync(showBusy: false);
            }
            else
            {
                UpdateSummary();
            }

            InfoText = BuildImportMessage(added.Count, summary);
        }
        catch (Exception ex)
        {
            ErrorText = $"导入 servers.dat 失败：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>导入结果文案：新增 N 个 / 跳过 M 个重复。统计缺失时退化为只报新增数。</summary>
    internal static string BuildImportMessage(int addedCount, ServerImportSummary? summary)
    {
        var skipped = summary?.Skipped ?? 0;

        if (addedCount == 0)
        {
            return summary is { Total: 0 }
                ? "servers.dat 里没有可导入的服务器。"
                : $"没有新增：{skipped} 个地址都已在收藏里。";
        }

        return skipped > 0
            ? $"已从 servers.dat 导入 {addedCount} 个服务器，跳过 {skipped} 个已存在的地址。"
            : $"已从 servers.dat 导入 {addedCount} 个服务器。";
    }

    [RelayCommand]
    private async Task RefreshOneAsync(ServerCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        await PingCardAsync(card);
    }

    /// <summary>
    /// 查询一台服务器的玩家名单（GameSpy4 Query，UDP 同端口）。
    ///
    /// 只在用户点「玩家」时打这一台：Query 是没有认证的 UDP 请求，对整张收藏列表批量发
    /// 会被服务端和网络设备当成扫描。查询失败不改卡片状态（不显示成服务器离线）。
    /// </summary>
    [RelayCommand]
    private async Task QueryPlayersAsync(ServerCardViewModel? card)
    {
        if (card is null || card.IsQueryingPlayers)
        {
            return;
        }

        card.BeginQuery();

        try
        {
            var result = _query is null
                ? await BuildDemoQueryResultAsync(card)
                : await _query.QueryAsync(card.Entry.Host, card.Entry.Port);

            card.ApplyQuery(result);
        }
        catch (Exception ex)
        {
            card.ApplyQueryFailure($"玩家名单查询失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenForm()
    {
        IsEditing = false;
        IsFormOpen = true;
        FormName = string.Empty;
        FormAddress = string.Empty;
        FormError = null;
        InfoText = null;
    }

    [RelayCommand]
    private void EditServer(ServerCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        IsEditing = true;
        IsFormOpen = true;
        FormName = card.Name;
        FormAddress = card.Entry.Address;
        FormError = null;
        _editingId = card.Entry.Id;
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        FormError = null;
        _editingId = null;
    }

    [RelayCommand]
    private async Task SaveFormAsync()
    {
        FormError = null;

        if (!TryParseAddress(FormAddress, out var host, out var port, out var addressError))
        {
            FormError = addressError;
            return;
        }

        var name = FormName.Trim();
        if (name.Length == 0)
        {
            name = host;
        }

        if (name.Length > 40)
        {
            FormError = "服务器名称请控制在 40 个字符以内。";
            return;
        }

        try
        {
            if (_servers is null)
            {
                IsFormOpen = false;
                InfoText = "示例模式：Core 服务未就绪，未写入收藏。";
                return;
            }

            var entry = new ServerEntry
            {
                // 编辑也生成新 Id：先加后删，任何一步写盘失败都不会丢原条目。
                // 旧实现是「先删后加」，第二步失败就永久丢一条收藏（REVIEW-R34-R36 P2-4）。
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Host = host,
                Port = port,
            };

            await _servers.AddAsync(entry);

            // 新条目落盘成功后再删旧条目；删除失败最多留下重复项，用户可手动清理。
            if (IsEditing && _editingId is not null)
            {
                try
                {
                    await _servers.RemoveAsync(_editingId);
                }
                catch (Exception ex)
                {
                    ErrorText = $"已保存，但旧条目未能删除（可能留下重复）：{ex.Message}";
                }
            }

            IsFormOpen = false;
            IsEditing = false;
            _editingId = null;

            await LoadAsync();
            await PingAllAsync(showBusy: false);
            InfoText = $"已保存服务器「{name}」。";
        }
        catch (Exception ex)
        {
            FormError = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteServerAsync(ServerCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            if (_servers is not null)
            {
                await _servers.RemoveAsync(card.Entry.Id);
            }

            Servers.Remove(card);
            UpdateSummary();
        }
        catch (Exception ex)
        {
            ErrorText = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 解析「从服务器页启动这个版本时该用哪个游戏目录」。
    ///
    /// 与首页同一套规则：能问实例上下文就问它（已有实例优先，其次按隔离策略自动建，
    /// 最后退回全局目录）。这件事以前没做，于是加入服务器永远落在全局 <c>.minecraft</c>：
    /// 隔离实例里配的模组全部失效，而且那个目录上没抢锁——同一实例可以从版本页再开一个 JVM。
    /// </summary>
    /// <param name="versionName">要启动的版本目录名。</param>
    private async Task<InstanceResolution> ResolveLaunchTargetAsync(string versionName)
    {
        if (_instanceContext is not null)
        {
            try
            {
                return await _instanceContext.ResolveLaunchTargetAsync(versionName).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 解析失败就退回「当前生效目录」：加入服务器不该因为解析问题整页失败。
                // 这里只提示，不阻断——用户此刻的目标是进服，不是修配置。
                ErrorText = $"按实例解析目录失败，改用当前目录：{ex.Message}";
            }
        }

        // 兜底也用当前生效目录（实例目录或全局），不是空值：空值会让 GameLauncher
        // 退回全局 .minecraft，恰好就是这次要修的那个错误。
        return new InstanceResolution(
            GameDirectory,
            _instanceContext?.Current,
            Isolated: _instanceContext?.Current is not null,
            Created: false);
    }

    [RelayCommand]
    private async Task JoinServerAsync(ServerCardViewModel? card)
    {
        if (card is null || IsJoining)
        {
            return;
        }

        InfoText = null;
        ErrorText = null;

        if (_gameLauncher is null || _accounts is null || _settings is null)
        {
            ErrorText = "启动服务尚未就绪，无法加入服务器。";
            return;
        }

        var versionName = _settings.Current.LastSelectedVersion;
        if (string.IsNullOrWhiteSpace(versionName))
        {
            ErrorText = "请先在「版本」页选择一个要启动的版本，再回到这里加入服务器。";
            return;
        }

        JoiningServerId = card.Entry.Id;

        try
        {
            var account = await _accounts.EnsureValidActiveAccountAsync();
            if (account is null)
            {
                ErrorText = "没有可用的账户，请先到「账户」页登录。";
                return;
            }

            // 目录必须先解析出来，这是本次修复的重点：以前这里不设 GameDirectory，
            // 于是「加入服务器」永远启动全局 .minecraft——用户在隔离实例里配好的模组全部失效，
            // 而且因为落在别的目录，还绕过了按游戏目录加的实例运行锁。
            var resolution = await ResolveLaunchTargetAsync(versionName).ConfigureAwait(true);

            // 与首页 / 版本页共用同一份合成规则：实例的内存 / Java / 参数 / GC / 版本类型
            // 一并生效，服务器入口不再是一组全局设置的复印件。
            var options = LaunchOptionsFactory.Build(
                account,
                versionName,
                resolution,
                _settings.Current,
                card.Entry.Address);

            InfoText = $"正在启动 {versionName} 并连接 {card.Entry.Address}…";

            var result = await _gameLauncher.LaunchAsync(options);

            if (!result.Started)
            {
                ErrorText = $"加入服务器失败：{result.FailureReason ?? "未知原因"}";
                InfoText = null;
            }
            else if (result.Detached)
            {
                // 取消等待但游戏仍在跑：不是失败，也不能报「已退出」。
                InfoText = "已取消等待，游戏仍在运行；可随时点「停止」。";
            }
            else if (result.Verdict == GameExitVerdict.Crashed
                     || (result.Verdict is null && result.ExitCode != 0))
            {
                // verdict 优先：exit 0 也可能是加载到一半被杀（崩溃）。
                InfoText = $"游戏异常退出（退出码 {result.ExitCode}）。";
            }
            else
            {
                InfoText = "游戏已退出。";
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"加入服务器失败：{ex.Message}";
        }
        finally
        {
            JoiningServerId = null;
        }
    }

    /// <summary>复制服务器地址，方便贴给朋友。</summary>
    [RelayCommand]
    private async Task CopyAddressAsync(ServerCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            if (_platform is null)
            {
                ErrorText = "剪贴板服务尚未就绪。";
                return;
            }

            if (!await _platform.TryCopyTextAsync(card.Entry.Address))
            {
                ErrorText = "复制失败：剪贴板不可用，详见启动器日志。";
                return;
            }

            InfoText = $"已复制 {card.Entry.Address}。";
        }
        catch (Exception ex)
        {
            ErrorText = $"复制失败：{ex.Message}";
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorText = null;

        try
        {
            // 「加入」需要上次选中的版本与内存设置，所以先确保设置从磁盘加载过。
            if (_settings is not null && !_settingsLoaded)
            {
                await _settings.LoadAsync();
                _settingsLoaded = true;
            }

            Servers.Clear();

            if (_servers is null)
            {
                foreach (var card in BuildSampleCards())
                {
                    Servers.Add(card);
                }
            }
            else
            {
                await _servers.LoadAsync();

                foreach (var entry in _servers.Servers)
                {
                    Servers.Add(new ServerCardViewModel(entry));
                }
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            ErrorText = $"读取服务器列表失败：{ex.Message}";
            UpdateSummary();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PingAllAsync(bool showBusy)
    {
        if (Servers.Count == 0)
        {
            UpdateSummary();
            return;
        }

        if (showBusy)
        {
            IsLoading = true;
        }

        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        var token = _lifetimeCts.Token;

        using var gate = new SemaphoreSlim(MaxConcurrentPings, MaxConcurrentPings);

        try
        {
            var tasks = Servers.Select(async card =>
            {
                await gate.WaitAsync(token);
                try
                {
                    await PingCardAsync(card, token);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            // 离开页面或重复刷新导致的中断属于正常路径。
        }
        catch (Exception ex)
        {
            ErrorText = $"刷新服务器状态失败：{ex.Message}";
        }
        finally
        {
            if (showBusy)
            {
                IsLoading = false;
            }
        }
    }

    private async Task PingCardAsync(ServerCardViewModel card, CancellationToken token = default)
    {
        card.MarkChecking();

        try
        {
            ServerStatus status;

            if (_servers is null)
            {
                // 示例模式下用本地延迟模拟一次探测结果，避免演示数据看起来永远是「检测中」。
                var stopwatch = Stopwatch.StartNew();
                await Task.Delay(160, token);
                stopwatch.Stop();

                status = BuildDemoStatus(card, (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 8, 320));
            }
            else
            {
                status = await _servers.PingAsync(card.Entry.Host, card.Entry.Port, token);
            }

            card.Apply(status);
        }
        catch (OperationCanceledException)
        {
            card.MarkIdle();
        }
        catch (Exception ex)
        {
            card.ApplyError($"检测失败：{ex.Message}");
        }
    }

    private void StartTimer()
    {
        if (_timer is null || IsDemoMode)
        {
            return;
        }

        _timer.Start();
    }

    /// <summary>定时器到点。异常必须在此吞掉，否则会在 Dispatcher 上抛成未处理异常。</summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        _ = PingAllAsync(showBusy: false);
    }

    private void UpdateSummary()
    {
        IsEmpty = Servers.Count == 0 && !IsLoading;

        if (Servers.Count == 0)
        {
            SetSubtitle(IsDemoMode ? "示例数据" : "尚未添加服务器");
            return;
        }

        var online = Servers.Count(card => card.IsOnline);
        SetSubtitle($"{online}/{Servers.Count} 在线");
    }

    /// <summary>
    /// 截图专用：<c>--page servers --section players</c> 时用示例卡片把「玩家名单」与
    /// 「Query 未开启」两种形态都摆出来，且不联网。
    /// 截图模式没有点击能力，与 <see cref="ScreenshotMode.OpenInstanceEditor"/> 同一约定。
    /// </summary>
    private bool TryApplyScreenshotPlayers()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "players", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Servers.Clear();
        foreach (var card in BuildSampleCards())
        {
            Servers.Add(card);
        }

        var latency = 24;
        foreach (var card in Servers)
        {
            card.Apply(BuildDemoStatus(card, latency));
            latency += 18;
        }

        var cards = Servers.ToList();
        if (cards.Count > 0)
        {
            cards[0].BeginQuery();
            cards[0].ApplyQuery(BuildDemoQueryResult(cards[0], withPlayers: true));
        }

        if (cards.Count > 1)
        {
            cards[1].BeginQuery();
            cards[1].ApplyQuery(BuildDemoQueryResult(cards[1], withPlayers: false));
        }

        UpdateSummary();
        return true;
    }

    /// <summary>
    /// 截图专用：<c>--page servers --section favicon</c> 时给第一张卡片装上图标。
    ///
    /// 截图模式不联网，ping 打到的是种子数据里的假地址，永远拿不到真实 favicon，
    /// 「有图标」这一形态就从未在截图里出现过。只给第一张：同一张图里既有图标形态，
    /// 也有其余卡片的字母占位，两种布局一次看全。
    /// </summary>
    private bool TryApplyScreenshotFavicon()
    {
        if (!ScreenshotMode.IsActive
            || !string.Equals(ScreenshotMode.SectionKey, "favicon", StringComparison.OrdinalIgnoreCase)
            || Servers.Count == 0)
        {
            return false;
        }

        if (ServerFaviconDecoder.TryDecode(ScreenshotFaviconDataUri) is { } bitmap)
        {
            Servers[0].Favicon = bitmap;
        }

        return true;
    }

    /// <summary>
    /// 截图用的 64×64 PNG，与 tools/seed-demo-data.ps1 的世界缩略图同一张，不另造演示数据。
    /// 只被 <see cref="TryApplyScreenshotFavicon"/> 使用。
    /// </summary>
    private const string ScreenshotFaviconDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAABGUlEQVR42u2a0Q2CQBBEl6nEBmzBBmzFAqzJfxqgEelEPkyMguBeTnIzydz3gzAcd+zLbXe9PSIiIvphjMQ4nw5UPJZXZu5Own8EyFwzeyvN+YgA2zOV8iB8piIenO81z4P220jyYP6+MzzI1+hPHvz7zDYPib1yg4fKfr/GQ+if9ZWH1n93yUOudpjxUKx/3nmI1nAvHrp16JPX9oF+GO0D9gH7gH3APmAfsA/YB+wD9gH7gH3APmAfsA+IZuiOl3usn3Vmzmsr+crR3gf+H0ArAxqeUe8bQCUDmvcK1AZg6HeonQHpDODpO6ldA6IZwNb/UxyAsIepeAakM4C2l6x4DWjVcBQ+sEspIZfBPmAfsA/YB+wDVWMCQmyIfm7hatYAAAAASUVORK5CYII=";

    private static ServerStatus BuildDemoStatus(ServerCardViewModel card, int latencyMs)
    {
        var offline = card.Name.Contains("离线", StringComparison.Ordinal);
        return new ServerStatus
        {
            Host = card.Entry.Host,
            Port = card.Entry.Port,
            Online = !offline,
            LatencyMs = latencyMs,
            OnlinePlayers = 12 + card.Name.Length,
            MaxPlayers = 100,
            MotdRaw = card.MotdRaw,
            VersionName = "1.20.4",
            Error = offline ? "连接超时（5 秒）" : null,
        };
    }

    /// <summary>示例模式下的 Query 结果：一张给玩家名单，一张给「未开启 Query」，覆盖两种界面形态。</summary>
    private static ServerQueryResult BuildDemoQueryResult(ServerCardViewModel card, bool withPlayers)
    {
        if (!withPlayers)
        {
            return new ServerQueryResult
            {
                Host = card.Entry.Host,
                Port = card.Entry.Port,
                Outcome = ServerQueryOutcome.QueryDisabled,
                Error = "该服务器未开启 Query（enable-query=false）",
            };
        }

        var names = new[] { "Steve", "Alex", "Notch", "Herobrine", "Dinnerbone", "jeb_" };

        return new ServerQueryResult
        {
            Host = card.Entry.Host,
            Port = card.Entry.Port,
            Outcome = ServerQueryOutcome.Available,
            Hostname = $"{card.Name} · Minecraft Server",
            GameType = "SMP",
            GameId = "MINECRAFT",
            Version = "1.20.4",
            Plugins = "Paper on Bukkit on CraftBukkit",
            Map = "world",
            NumPlayers = names.Length,
            MaxPlayers = 100,
            HostPort = card.Entry.Port,
            HostIp = card.Entry.Host,
            LatencyMs = 42,
            Players = names.Select(static name => new ServerQueryPlayer { Name = name }).ToList(),
        };
    }

    /// <summary>示例模式下点击「玩家」：本地模拟一次查询，不联网。</summary>
    private static async Task<ServerQueryResult> BuildDemoQueryResultAsync(ServerCardViewModel card)
    {
        await Task.Delay(160);
        return BuildDemoQueryResult(card, withPlayers: !card.Name.Contains("离线", StringComparison.Ordinal));
    }

    private static IEnumerable<ServerCardViewModel> BuildSampleCards()
    {
        yield return WithMotd(
            new ServerCardViewModel(new ServerEntry
            {
                Name = "Hypixel",
                Host = "mc.hypixel.net",
                Port = 25565,
            }),
            "§a§lHypixel §r§7| §b1.20.4 §7| §ePVP · 小游戏 · 大厅");

        yield return WithMotd(
            new ServerCardViewModel(new ServerEntry
            {
                Name = "本地整合包测试",
                Host = "127.0.0.1",
                Port = 25565,
                Note = "MOTD 用 § 颜色码渲染，验证多行换行",
            }),
            "§8§m------------------------------\n§6创造服 §7— §f和朋友一起搭东西\n§7版本 §a1.20.1 Forge §7· 白名单\n§8§m------------------------------");

        yield return WithMotd(
            new ServerCardViewModel(new ServerEntry
            {
                Name = "离线示例服务器",
                Host = "play.example.com",
                Port = 25565,
            }),
            "§7尚未检测");

        static ServerCardViewModel WithMotd(ServerCardViewModel card, string motd)
        {
            card.MotdRaw = motd;
            return card;
        }
    }

    /// <summary>解析 <c>host[:port]</c>。故意不支持 IPv6 字面量：Minecraft 客户端自身的直连参数也要求这种形式。</summary>
    internal static bool TryParseAddress(string input, out string host, out int port, out string error)
    {
        host = string.Empty;
        port = 25565;
        error = string.Empty;

        var text = input.Trim();
        if (text.Length == 0)
        {
            error = "请填写服务器地址，例如 play.example.com 或 play.example.com:25565。";
            return false;
        }

        if (text.Contains(' ', StringComparison.Ordinal))
        {
            error = "地址里不能包含空格。";
            return false;
        }

        var separator = text.LastIndexOf(':');
        if (separator >= 0)
        {
            var portText = text[(separator + 1)..];
            host = text[..separator];

            if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            {
                error = "端口必须是 1–65535 之间的整数。";
                return false;
            }
        }
        else
        {
            host = text;
            port = 25565;
        }

        if (host.Length is 0 or > 253 || !HostPattern.IsMatch(host))
        {
            error = "域名只能包含字母、数字、点、下划线与连字符。";
            return false;
        }

        return true;
    }
}

/// <summary>一张服务器卡片的状态视图模型。所有可变状态由 ping 结果回填。</summary>
public sealed partial class ServerCardViewModel : ObservableObject
{
    public ServerCardViewModel(ServerEntry entry)
    {
        Entry = entry;
        Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name;
        Address = entry.Address;
    }

    public ServerEntry Entry { get; }

    public string Name { get; }

    public string Address { get; }

    [ObservableProperty]
    private string? _motdRaw = "§7尚未检测";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isOffline = true;

    [ObservableProperty]
    private bool _isTimeout;

    [ObservableProperty]
    private string _playerText = "—";

    [ObservableProperty]
    private string _latencyText = "—";

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _statusText = "尚未检测";

    [ObservableProperty]
    private string? _errorText;

    // ---- 玩家名单（GameSpy4 Query，点「玩家」才查） ----

    /// <summary>名单面板是否展开。点「玩家」时打开，之后保持展开直到用户收起。</summary>
    [ObservableProperty]
    private bool _isPlayersOpen;

    [ObservableProperty]
    private bool _isQueryingPlayers;

    [ObservableProperty]
    private bool _hasPlayerList;

    [ObservableProperty]
    private string? _playersHint;

    [ObservableProperty]
    private string _playersHeaderText = "玩家名单";

    /// <summary>服务器图标（SLP 的 favicon）。没探测到图标时为 null，界面退回首字母占位。</summary>
    [ObservableProperty]
    private Bitmap? _favicon;

    /// <summary>上一次尝试过的 data URI。解码失败也记下，避免每 60 秒刷新重复解码同一份坏数据。</summary>
    private string? _faviconDataUri;

    /// <summary>在线玩家名。只在 Query 成功且服务器确实返回名单时非空。</summary>
    public ObservableCollection<string> PlayerNames { get; } = [];

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasMotd => !string.IsNullOrWhiteSpace(MotdRaw);

    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);

    public bool HasFavicon => Favicon is not null;

    /// <summary>
    /// 首字母占位：没有图标时显示在固定 32×32 的槽里，保证行高与列宽不随图标有无变化。
    /// 按文本元素取首字符——服务器名可能以 emoji 开头，按 UTF-16 码元切会截出半个字符。
    /// </summary>
    public string Initial
    {
        get
        {
            var name = Name.Trim();
            return name.Length == 0 ? "?" : StringInfo.GetNextTextElement(name).ToUpperInvariant();
        }
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnMotdRawChanged(string? value) => OnPropertyChanged(nameof(HasMotd));

    partial void OnVersionTextChanged(string value) => OnPropertyChanged(nameof(HasVersion));

    partial void OnFaviconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasFavicon));

    /// <summary>
    /// 只在真正解出图标时替换：离线或服务器没返回 favicon 时保留上一轮图标，
    /// 否则每 60 秒自动刷新一次，列表里的图标就会闪一下消失。
    /// 同一 data URI 不重复解码——刷新大多返回同一张图，重建 Bitmap 没有意义。
    /// 旧位图不显式 Dispose：渲染线程可能正在读它，而图标只在服务器换图时才重建，交给 GC 更稳。
    /// </summary>
    private void ApplyFavicon(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri)
            || string.Equals(dataUri, _faviconDataUri, StringComparison.Ordinal))
        {
            return;
        }

        _faviconDataUri = dataUri;
        if (ServerFaviconDecoder.TryDecode(dataUri) is { } bitmap)
        {
            Favicon = bitmap;
        }
    }

    public void MarkChecking()
    {
        IsChecking = true;
        StatusText = "检测中…";
    }

    public void MarkIdle()
    {
        IsChecking = false;
        StatusText = IsOnline ? "在线" : "未检测";
    }

    public void Apply(ServerStatus status)
    {
        IsChecking = false;
        IsOnline = status.Online;
        IsOffline = !status.Online;
        IsTimeout = !status.Online && (status.Error?.Contains("超时", StringComparison.Ordinal) ?? false);

        ErrorText = status.Online ? null : status.Error ?? "无法连接";
        StatusText = status.Online ? "在线" : IsTimeout ? "超时" : "离线";

        PlayerText = status.Online ? $"{status.OnlinePlayers}/{status.MaxPlayers}" : "—";
        LatencyText = status.Online ? $"{status.LatencyMs}ms" : "—";
        VersionText = status.Online ? status.VersionName ?? string.Empty : string.Empty;

        // MOTD 行与下面的错误行分工：MOTD 只给一句结论，具体原因放错误行，避免同一句话出现两次。
        MotdRaw = status.Online
            ? status.MotdRaw ?? "§7这个服务器没有设置 MOTD。"
            : IsTimeout ? "§8超时 · 服务器没有在 5 秒内响应" : "§8离线 · 未能获取服务器信息";

        ApplyFavicon(status.FaviconDataUri);
    }

    public void ApplyError(string message)
    {
        IsChecking = false;
        IsOnline = false;
        IsOffline = true;
        IsTimeout = false;
        StatusText = "检测失败";
        ErrorText = message;
        PlayerText = "—";
        LatencyText = "—";
        VersionText = string.Empty;
        MotdRaw = $"§c{message}";
    }

    /// <summary>点「玩家」后立刻展开面板并占位，避免等待 2 秒期间界面毫无反应。</summary>
    public void BeginQuery()
    {
        IsQueryingPlayers = true;
        IsPlayersOpen = true;
        HasPlayerList = false;
        PlayersHeaderText = "玩家名单";
        PlayersHint = null;
        PlayerNames.Clear();
    }

    /// <summary>
    /// 回填 Query 结果。Query 关闭 / 超时是服务器的常见配置，不是错误：
    /// 只给一句「看不到玩家名单」的说明，不写进卡片错误行、不改在线状态。
    /// </summary>
    public void ApplyQuery(ServerQueryResult result)
    {
        IsQueryingPlayers = false;
        IsPlayersOpen = true;
        PlayerNames.Clear();

        if (!result.Available)
        {
            HasPlayerList = false;
            PlayersHeaderText = "玩家名单";
            PlayersHint = result.IsQueryUnavailable
                ? result.Outcome == ServerQueryOutcome.Timeout
                    ? "Query 没有响应（服务器可能未开启），看不到玩家名单"
                    : "该服务器未开启 Query，看不到玩家名单"
                : $"玩家名单查询失败：{result.Error ?? "未知原因"}";
            return;
        }

        foreach (var player in result.Players)
        {
            PlayerNames.Add(player.Name);
        }

        HasPlayerList = PlayerNames.Count > 0;
        PlayersHeaderText = HasPlayerList ? $"在线玩家 · {PlayerNames.Count}" : "玩家名单";

        if (HasPlayerList)
        {
            PlayersHint = null;
        }
        else if (result.NumPlayers > 0)
        {
            PlayersHint = $"服务器报告 {result.NumPlayers} 人在线，但没有返回玩家名单";
        }
        else
        {
            PlayersHint = "当前没有玩家在线";
        }
    }

    public void ApplyQueryFailure(string message)
    {
        IsQueryingPlayers = false;
        IsPlayersOpen = true;
        HasPlayerList = false;
        PlayersHeaderText = "玩家名单";
        PlayersHint = message;
    }
}
