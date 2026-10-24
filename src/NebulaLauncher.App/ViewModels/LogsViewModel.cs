using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Logging;
using NebulaLauncher.Services;
using Formatting = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 日志中心：左列选日志源，右侧过滤与正文。
///
/// 数据全部来自 <see cref="ILauncherLogReader"/>（只读，不写盘），游戏目录经
/// <see cref="IInstanceContext.GameDirectory"/> 取——直接读全局 <c>.minecraft</c> 会让
/// 「在实例里启动、日志页却看不到那次启动」的老问题复现。
///
/// 依赖全部可空：设计器预览、单测、以及 Core 服务没接进来时页面仍要能渲染。
/// </summary>
public sealed partial class LogsViewModel : PageViewModelBase
{
    /// <summary>一次最多读多少行。与 <see cref="ILauncherLogReader"/> 的默认值保持一致。</summary>
    public const int MaxLines = ILauncherLogReader.DefaultMaxLines;

    /// <summary>跟随的轮询间隔。700ms 是「看起来像实时」与「读盘频率不吵」的折中。</summary>
    public static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// 跟随期间保留的最大行数。游戏日志动辄上万行，全留着会让 SelectableTextBlock 的
    /// 文本渲染与「复制全部」变得很慢；超出的部分从头部丢弃并明确告知用户。
    /// </summary>
    public const int MaxRetainedLines = 5000;

    private readonly ILauncherLogReader? _reader;
    private readonly ILauncherPaths? _paths;
    private readonly IInstanceContext? _instanceContext;
    private readonly IPlatformService? _platform;
    private readonly ILogFollowService? _followService;

    /// <summary>当前文件解析出来的全部行（过滤前）。</summary>
    private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();

    /// <summary>过滤后实际显示的行。</summary>
    private IReadOnlyList<LogEntry> _visible = Array.Empty<LogEntry>();

    /// <summary>加载序号：快速切换文件时丢弃过期结果，避免旧文件的正文覆盖新文件。</summary>
    private int _loadVersion;

    /// <summary>最近一次由选中项变化触发的加载，供 <see cref="ReloadAsync"/> 等待。</summary>
    private Task _pendingLoad = Task.CompletedTask;

    /// <summary>跟随定时器。只在「页面激活 + 选中文件 + 跟随打开」时存在，其余时刻为 null。</summary>
    private DispatcherTimer? _followTimer;

    /// <summary>跟随读盘的取消源。离开页面 / 关闭跟随时取消，避免页面已释放还在读盘。</summary>
    private CancellationTokenSource? _followCts;

    /// <summary>下次增量读取的起点（字节）。切换文件、刷新、被截断时重置。</summary>
    private long _followOffset;

    /// <summary>防止上一次增量读取还没回来时又叠一次读盘。</summary>
    private bool _followBusy;

    /// <summary>页面是否处于激活状态。导航离开后即使开关还开着也不能读盘。</summary>
    private bool _isFollowingPage;

    private LogReadStatus _status = LogReadStatus.Success;

    private string? _readError;

    private bool _truncated;

    private string _subtitle = "启动器日志与游戏日志";

    public LogsViewModel(
        ILauncherLogReader? logReader = null,
        ILauncherPaths? launcherPaths = null,
        IInstanceContext? instanceContext = null,
        IPlatformService? platformService = null,
        ILogFollowService? followService = null)
    {
        _reader = logReader;
        _paths = launcherPaths;
        _instanceContext = instanceContext;
        _platform = platformService;
        _followService = followService;
    }

    public override string Title => "日志";

    public override string? Subtitle => _subtitle;

    public override string Key => NavigationKeys.Logs;

    /// <summary>正文滚动到末尾的请求。视图在 code-behind 里订阅后调用 <c>ScrollViewer.ScrollToEnd()</c>。</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>左列分组：启动器日志 / 游戏日志。</summary>
    public ObservableCollection<LogFileGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private LogFileItemViewModel? _selectedFile;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>「信息」chip 是否选中。Debug 归入这一档。</summary>
    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarn = true;

    [ObservableProperty]
    private bool _showError = true;

    /// <summary>读到新内容后自动滚到底部。</summary>
    [ObservableProperty]
    private bool _followTail = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _entriesText = string.Empty;

    [ObservableProperty]
    private string _countText = "尚未读取";

    [ObservableProperty]
    private string _statusText = "选择左侧的日志文件查看内容。";

    [ObservableProperty]
    private string? _emptyMessage;

    [ObservableProperty]
    private string? _emptyHint;

    /// <summary>四个级别 chip 全选中时表示「全部」，不需要再按级别过滤。</summary>
    public bool IsAllLevels => ShowInfo && ShowWarn && ShowError;

    public bool HasEmptyMessage => !string.IsNullOrWhiteSpace(EmptyMessage);

    public bool HasContent => !IsLoading && !HasEmptyMessage && EntriesText.Length > 0;

    public bool HasSelection => SelectedFile is not null;

    /// <summary>是否正在跟随：开关打开、有选中文件、且有跟随服务。</summary>
    public bool IsFollowing => FollowTail && SelectedFile is not null && _followService is not null;

    /// <summary>顶部状态 chip 的文案。跟随关闭时是「已暂停」，没有选中文件时是「未选择」。</summary>
    public string FollowStateText =>
        SelectedFile is null
            ? "未选择日志"
            : _followService is null
                ? "跟随不可用"
                : FollowTail
                    ? "跟随中"
                    : "已暂停";

    /// <summary>丢弃过头部内容时的提示文案；没有丢弃时为 null。</summary>
    [ObservableProperty]
    private string? _retentionNotice;

    public bool HasRetentionNotice => !string.IsNullOrEmpty(RetentionNotice);

    /// <summary>工具条上的「打开日志目录」：优先打开选中文件所在目录。</summary>
    public string CurrentDirectory =>
        SelectedFile is not null
            ? SafeDirectoryOf(SelectedFile.Path)
            : LauncherLogsDirectory;

    private string LauncherLogsDirectory
    {
        get
        {
            try
            {
                return _paths?.LogsDirectory ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 游戏侧日志根目录。所有与「游戏目录」相关的页面都必须经 <see cref="IInstanceContext"/> 取，
    /// 上下文缺失或损坏时才退回全局 <c>.minecraft</c>。
    /// </summary>
    private string GameDirectory
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
                    // 实例上下文异常时按全局目录渲染，不能让整页白屏。
                }
            }

            try
            {
                return _paths?.MinecraftDirectory ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public override async Task OnNavigatedToAsync()
    {
        _isFollowingPage = true;
        UpdateSubtitle();
        await ReloadAsync();
        SyncFollow();
    }

    public override Task OnNavigatedFromAsync()
    {
        // 页面离开后让在途读取的结果失效，避免它在别的页面上改状态。
        _loadVersion++;

        // 定时器与在途读盘必须在离开时停掉：页面是缓存复用的，不停的话切到别的页面后
        // 每 700ms 仍在打开这个日志文件读一遍（页面离开必须停定时器）。
        _isFollowingPage = false;
        StopFollowTimer();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 页面被容器释放时收尾。离开页面已由 <see cref="OnNavigatedFromAsync"/> 处理过，
    /// 这里兜住「没离开过就退出」的情况，并把字段置 null（基类约定）。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isFollowingPage = false;
            StopFollowTimer();
        }

        base.Dispose(disposing);
    }

    /// <summary>重新枚举日志文件并读取当前选中项。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        UpdateSubtitle();
        await ReloadAsync();
    }

    [RelayCommand]
    private void SelectFile(LogFileItemViewModel? file)
    {
        if (file is not null)
        {
            SelectedFile = file;
        }
    }

    /// <summary>级别 chip。传 <c>all</c> 表示「全部」；不能把三档全关掉，否则正文恒为空。</summary>
    [RelayCommand]
    private void ToggleLevel(string? level)
    {
        switch (level)
        {
            case "all":
                ShowInfo = true;
                ShowWarn = true;
                ShowError = true;
                return;
            case "info":
                ShowInfo = !ShowInfo;
                break;
            case "warn":
                ShowWarn = !ShowWarn;
                break;
            case "error":
                ShowError = !ShowError;
                break;
            default:
                return;
        }

        if (!ShowInfo && !ShowWarn && !ShowError)
        {
            ShowInfo = true;
            ShowWarn = true;
            ShowError = true;
        }
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        if (_platform is null)
        {
            StatusText = "平台服务尚未接入，无法复制。";
            return;
        }

        if (EntriesText.Length == 0)
        {
            StatusText = "当前没有可复制的内容。";
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(EntriesText))
            {
                StatusText = "复制失败：剪贴板不可用，详见启动器日志。";
                return;
            }

            StatusText = $"已复制 {_visible.Count} 行到剪贴板。";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RevealSelectedAsync()
    {
        if (_platform is null)
        {
            StatusText = "平台服务尚未接入，无法定位文件。";
            return;
        }

        if (SelectedFile is null)
        {
            StatusText = "先在左侧选一个日志文件。";
            return;
        }

        try
        {
            await _platform.RevealInFileManagerAsync(SelectedFile.Path);
            StatusText = $"已在文件夹中定位：{SelectedFile.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusText = $"定位失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenLogDirectoryAsync()
    {
        if (_platform is null)
        {
            StatusText = "平台服务尚未接入，无法打开目录。";
            return;
        }

        var directory = CurrentDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            StatusText = "没有可打开的日志目录。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(directory);
            StatusText = $"已打开：{directory}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    partial void OnSelectedFileChanged(LogFileItemViewModel? value)
    {
        foreach (var item in AllFiles())
        {
            item.IsSelected = ReferenceEquals(item, value);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CurrentDirectory));

        // 换文件：旧文件的偏移、在途跟随读盘、「已丢弃头部」提示全部作废。
        // 偏移由随后的 LoadSelectedAsync 在新文件读完后重新设成文件末尾。
        _followOffset = 0;
        RetentionNotice = null;
        StopFollowTimer();
        UpdateFollowState();

        _pendingLoad = LoadSelectedAsync();
    }

    partial void OnFollowTailChanged(bool value) => SyncFollow();

    partial void OnRetentionNoticeChanged(string? value) => OnPropertyChanged(nameof(HasRetentionNotice));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowInfoChanged(bool value) => OnLevelFilterChanged();

    partial void OnShowWarnChanged(bool value) => OnLevelFilterChanged();

    partial void OnShowErrorChanged(bool value) => OnLevelFilterChanged();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasContent));

    partial void OnEntriesTextChanged(string value) => OnPropertyChanged(nameof(HasContent));

    private void OnLevelFilterChanged()
    {
        OnPropertyChanged(nameof(IsAllLevels));
        ApplyFilter();
    }

    private void UpdateSubtitle()
    {
        string? instanceName = null;
        if (_instanceContext is not null)
        {
            try
            {
                instanceName = _instanceContext.Current?.Name;
            }
            catch (Exception)
            {
                // 实例上下文异常不影响页面渲染。
            }
        }

        _subtitle = string.IsNullOrWhiteSpace(instanceName)
            ? "启动器日志与游戏日志 · 全局目录"
            : $"启动器日志与游戏日志 · 实例「{instanceName}」";

        OnPropertyChanged(nameof(Subtitle));
    }

    private async Task ReloadAsync()
    {
        var previousPath = SelectedFile?.Path;

        var launcherFiles = ListLauncherFiles();
        var gameFiles = ListGameFiles();

        Groups.Clear();
        Groups.Add(new LogFileGroupViewModel("启动器日志", launcherFiles));
        Groups.Add(new LogFileGroupViewModel("游戏日志", gameFiles));

        var all = launcherFiles.Concat(gameFiles).ToList();

        // 刷新后保持原选中项（按路径匹配），它可能刚刚被写入了新内容。
        var match = previousPath is null
            ? null
            : all.FirstOrDefault(file => string.Equals(file.Path, previousPath, StringComparison.OrdinalIgnoreCase));

        var target = match ?? all.FirstOrDefault();

        if (target is null)
        {
            if (SelectedFile is not null)
            {
                SelectedFile = null;

                // 选中项置空会触发一次「尚未选择」的加载，等它跑完再写最终文案，否则会被它覆盖。
                await _pendingLoad;
            }

            _entries = Array.Empty<LogEntry>();
            _visible = Array.Empty<LogEntry>();
            _status = LogReadStatus.Success;
            _readError = null;
            _truncated = false;
            EntriesText = string.Empty;
            CountText = "没有日志文件";
            SetEmpty(
                "没有找到日志文件",
                "启动器日志在数据目录的 logs/ 下，游戏日志在游戏目录的 logs/latest.log。启动过一次游戏后再回来看看。");
            StatusText = "没有找到日志文件";
            return;
        }

        if (ReferenceEquals(target, SelectedFile))
        {
            // 选中项没变就不会触发 OnSelectedFileChanged，这里主动读一次（刷新按钮的常见路径）。
            await LoadSelectedAsync();
            return;
        }

        SelectedFile = target;
        await _pendingLoad;
    }

    private async Task LoadSelectedAsync()
    {
        var version = ++_loadVersion;
        var file = SelectedFile;

        _entries = Array.Empty<LogEntry>();
        _visible = Array.Empty<LogEntry>();
        _truncated = false;
        _readError = null;

        // 整份重读会换掉全部行，「已丢弃头部」的提示随之失效。
        RetentionNotice = null;

        if (file is null)
        {
            ApplyFilter();
            SetEmpty("尚未选择日志文件", "从左侧选一个日志，右侧会显示最后 2000 行。");
            StatusText = "尚未选择日志文件";
            UpdateFollowState();
            return;
        }

        if (_reader is null)
        {
            _status = LogReadStatus.FileMissing;
            ApplyFilter();
            SetEmpty("日志读取服务不可用", "核心服务尚未接入，暂时无法读取日志内容。");
            StatusText = "日志读取服务不可用";
            UpdateFollowState();
            return;
        }

        IsLoading = true;
        StatusText = $"正在读取 {file.DisplayName}…";

        LogTailResult result;
        try
        {
            result = await _reader.ReadTailAsync(file.Path, MaxLines);
        }
        catch (Exception ex)
        {
            result = LogTailResult.Failed(ex.Message);
        }

        // 期间用户又点了另一个文件：这个结果已经过期，丢弃。
        if (version != _loadVersion)
        {
            return;
        }

        IsLoading = false;

        _entries = result.Entries;
        _status = result.Status;
        _truncated = result.Truncated;
        _readError = result.Error;

        ApplyFilter();

        // 整份读取之后从「文件当前末尾」开始跟随：尾部 2000 行已经显示，增量只需读之后新写的。
        _followOffset = TryGetFileLength(file.Path);
        SyncFollow();
    }

    private void ApplyFilter()
    {
        if (SelectedFile is null)
        {
            _visible = Array.Empty<LogEntry>();
            EntriesText = string.Empty;
            CountText = "没有选中文件";
            return;
        }

        if (IsLoading)
        {
            return;
        }

        _visible = LauncherLogReader.Filter(_entries, SelectedLevels(), SearchText);

        EntriesText = _visible.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, _visible.Select(FormatLine));

        CountText = BuildCountText();

        // 正文与空态互斥：有内容就清掉空态文案，没有才生成对应提示，
        // 否则两段文案会同时出现在右栏。
        if (EntriesText.Length > 0)
        {
            SetEmpty(null, null);
            StatusText = _truncated
                ? $"只显示最后 {_entries.Count} 行（文件较大）"
                : $"已读取 {_visible.Count} 行";

            if (FollowTail)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        UpdateEmptyMessage();
        StatusText = EmptyMessage ?? string.Empty;
    }

    /// <summary>把解析结果拼回接近原始格式的一行，让用户看到的仍是日志而不是「清洗后的正文」。</summary>
    private static string FormatLine(LogEntry entry)
    {
        if (entry.Timestamp is null && entry.Level is null)
        {
            // 崩溃报告正文、异常堆栈：原样输出，保留缩进。
            return entry.Text;
        }

        var stamp = entry.Timestamp is { } timestamp
            ? timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : "        ";

        var level = entry.Level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "     ",
        };

        return $"{stamp}  {level}  {entry.Text}";
    }

    private string BuildCountText()
    {
        if (_entries.Count == 0)
        {
            return "0 行";
        }

        if (_visible.Count == _entries.Count)
        {
            return _truncated
                ? $"最后 {_entries.Count} 行"
                : $"{_entries.Count} 行";
        }

        return $"显示 {_visible.Count} / {_entries.Count} 行";
    }

    private void UpdateEmptyMessage()
    {
        switch (_status)
        {
            case LogReadStatus.FileMissing:
                SetEmpty("日志文件不存在", "它可能已经被清理，或者实例目录被移动过。点「刷新」重新枚举。");
                return;
            case LogReadStatus.ReadFailed:
                SetEmpty(
                    "读取日志失败",
                    string.IsNullOrWhiteSpace(_readError) ? "文件可能被其它进程独占。" : _readError);
                return;
        }

        if (_entries.Count == 0)
        {
            SetEmpty("这个日志文件还是空的", "启动器或游戏还没有往里写内容。运行一次后再刷新。");
            return;
        }

        SetEmpty("没有匹配的行", "把级别筛选放宽，或清空关键字。");
    }

    private void SetEmpty(string? message, string? hint)
    {
        EmptyMessage = message;
        EmptyHint = hint;

        // 这两个派生标志同时依赖「有没有正文」与「有没有空态文案」，一次改两处时手动统一通知。
        OnPropertyChanged(nameof(HasEmptyMessage));
        OnPropertyChanged(nameof(HasContent));
    }

    private List<LogLevel>? SelectedLevels()
    {
        var levels = new List<LogLevel>(3);

        if (ShowInfo)
        {
            levels.Add(LogLevel.Info);
        }

        if (ShowWarn)
        {
            levels.Add(LogLevel.Warn);
        }

        if (ShowError)
        {
            levels.Add(LogLevel.Error);
        }

        // 三档全开等价于「全部」，不过滤（否则 Debug 行会被 Info 档的折叠规则漏掉）。
        return levels.Count == 3 ? null : levels;
    }

    private IReadOnlyList<LogFileItemViewModel> ListLauncherFiles()
    {
        if (_reader is null)
        {
            return Array.Empty<LogFileItemViewModel>();
        }

        return ToItems(_reader.ListLauncherLogs());
    }

    private IReadOnlyList<LogFileItemViewModel> ListGameFiles()
    {
        if (_reader is null)
        {
            return Array.Empty<LogFileItemViewModel>();
        }

        return ToItems(_reader.ListGameLogs(GameDirectory));
    }

    private static List<LogFileItemViewModel> ToItems(IReadOnlyList<LogFileInfo> files) =>
        files.Select(file => new LogFileItemViewModel(file)).ToList();

    private IEnumerable<LogFileItemViewModel> AllFiles() => Groups.SelectMany(group => group.Files);

    private static string SafeDirectoryOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // ==================== 跟随（增量 tail） ====================

    /// <summary>统一更新「是否跟随 / chip 文案」，并在应当跟随时启停定时器。</summary>
    private void SyncFollow()
    {
        UpdateFollowState();

        if (_isFollowingPage
            && FollowTail
            && SelectedFile is not null
            && _followService is not null
            && !IsDisposed)
        {
            StartFollowTimer();
            return;
        }

        StopFollowTimer();
    }

    private void UpdateFollowState()
    {
        OnPropertyChanged(nameof(IsFollowing));
        OnPropertyChanged(nameof(FollowStateText));
    }

    private void StartFollowTimer()
    {
        if (_followService is null || SelectedFile is null)
        {
            return;
        }

        _followCts ??= new CancellationTokenSource();

        if (_followTimer is null)
        {
            var timer = new DispatcherTimer { Interval = FollowInterval };
            timer.Tick += OnFollowTick;
            _followTimer = timer;
        }

        if (!_followTimer.IsEnabled)
        {
            _followTimer.Start();
        }

        // 打开开关 / 切文件后立即追一次：否则最多要等一个完整周期才看到新内容。
        _ = FollowTickAsync();
    }

    /// <summary>停表并取消在途读盘，同时把字段置 null——定时器与 CTS 只在需要时存在。</summary>
    private void StopFollowTimer()
    {
        if (_followTimer is not null)
        {
            _followTimer.Stop();
            _followTimer.Tick -= OnFollowTick;
            _followTimer = null;
        }

        _followCts?.Cancel();
        _followCts?.Dispose();
        _followCts = null;
    }

    /// <summary>定时器到点。异常必须在 <see cref="FollowTickAsync"/> 内部吞掉，否则会成为 Dispatcher 上的未处理异常。</summary>
    private void OnFollowTick(object? sender, EventArgs e) => _ = FollowTickAsync();

    /// <summary>
    /// 一次增量读取：只把新行接到正文末尾，不重读整份文件。
    /// 文件被截断 / 轮转时退回整份重读，跟随不能因为游戏重建 <c>latest.log</c> 就停摆。
    /// </summary>
    private async Task FollowTickAsync()
    {
        if (_followBusy)
        {
            return;
        }

        var service = _followService;
        var file = SelectedFile;

        if (!_isFollowingPage || !FollowTail || IsDisposed || service is null || file is null)
        {
            StopFollowTimer();
            UpdateFollowState();
            return;
        }

        var version = _loadVersion;
        var path = file.Path;
        var offset = _followOffset;
        var token = _followCts?.Token ?? CancellationToken.None;

        _followBusy = true;
        try
        {
            LogFollowResult result;
            try
            {
                result = await service.ReadSinceAsync(path, offset, ILogFollowService.DefaultMaxBytes, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusText = $"跟随读取失败：{ex.Message}";
                return;
            }

            // 期间切换了文件 / 刷新过：这批结果属于旧文件，丢弃。
            if (version != _loadVersion
                || SelectedFile is null
                || !string.Equals(SelectedFile.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            switch (result.Status)
            {
                case LogFollowStatus.Missing:
                    // 文件可能正被删除重建（游戏轮转 latest.log）：不停表，下个周期自愈。
                    StatusText = "跟随已暂停：日志文件暂时读不到，下个周期自动重试";
                    return;

                case LogFollowStatus.Truncated:
                case LogFollowStatus.Rotated:
                    _followOffset = 0;
                    await LoadSelectedAsync();
                    StatusText = result.Status == LogFollowStatus.Truncated
                        ? "日志被截断，已重新读取整份内容"
                        : "日志已轮转，已重新读取新文件";
                    return;
            }

            _followOffset = result.Offset;

            if (result.Entries.Count == 0)
            {
                return;
            }

            AppendEntries(result.Entries);
            ApplyFilter();
        }
        finally
        {
            _followBusy = false;
        }
    }

    /// <summary>
    /// 把新行接到 <see cref="_entries"/> 末尾，超过 <see cref="MaxRetainedLines"/> 时从头部丢弃。
    /// 丢弃必须让用户看得见：否则「刚才明明有这一行，往上翻却没了」无法解释。
    /// </summary>
    private void AppendEntries(IReadOnlyList<LogEntry> added)
    {
        var merged = new List<LogEntry>(_entries.Count + added.Count);
        merged.AddRange(_entries);
        merged.AddRange(added);

        if (merged.Count > MaxRetainedLines)
        {
            merged.RemoveRange(0, merged.Count - MaxRetainedLines);
            RetentionNotice = $"仅显示最近 {MaxRetainedLines} 行";
        }

        _entries = merged;
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 截图专用：把日志页推进到「跟随中 + 刚追加了几行」。
    ///
    /// 为什么需要写文件：跟随对象必须是一个真实在增长的文件，否则截到的永远是空态。
    /// 写的是演示数据目录下的独立文件，不碰启动器自己的日志；只在
    /// <see cref="ScreenshotMode.IsActive"/> 下由 <c>App.AttachScreenshot</c> 调用。
    /// </summary>
    public async Task ApplyLogFollowDemoAsync()
    {
        if (!ScreenshotMode.IsActive)
        {
            return;
        }

        var directory = _paths?.LogsDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var path = Path.Combine(directory, "nebula-follow-demo.log");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, BuildFollowDemoText(0, 24));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"截图演示日志写入失败：{ex.Message}";
            return;
        }

        // 重新枚举，让这个文件出现在左列并成为选中项。
        await ReloadAsync();

        var item = AllFiles().FirstOrDefault(file =>
            string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        SelectedFile = item;
        await _pendingLoad;

        FollowTail = true;
        SyncFollow();

        // 追加几行，让「跟随中」这个状态真的来自一次增量读取，而不是把布尔值摆成 true。
        try
        {
            await File.AppendAllTextAsync(path, BuildFollowDemoText(24, 6));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"截图演示日志追加失败：{ex.Message}";
            return;
        }

        await Task.Delay(FollowInterval + TimeSpan.FromMilliseconds(400));
    }

    private static string BuildFollowDemoText(int start, int count)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            var level = index % 11 == 10 ? "WARN " : "INFO ";
            var message = index % 11 == 10
                ? $"跟随示例：下载源回退到镜像（第 {index + 1} 行）"
                : $"跟随示例：正在跟踪 latest.log 的新增内容（第 {index + 1} 行）";

            builder.Append("2026-10-22 21:5")
                .Append((index % 10).ToString(CultureInfo.InvariantCulture))
                .Append(":07.218 [")
                .Append(level)
                .Append("] ")
                .Append(message)
                .Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>左列的一个日志文件。选中态由 <see cref="LogsViewModel"/> 统一维护。</summary>
public sealed partial class LogFileItemViewModel : ObservableObject
{
    public LogFileItemViewModel(LogFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
    }

    public LogFileInfo Info { get; }

    public string Path => Info.Path;

    public string DisplayName => Info.DisplayName;

    public LogFileKind Kind => Info.Kind;

    /// <summary>第二行：大小 + 修改时间。</summary>
    public string MetaText =>
        $"{Formatting.FormatSize(Info.SizeBytes)} · {Info.ModifiedUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)}";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>左列的一个分组（启动器日志 / 游戏日志）。</summary>
public sealed class LogFileGroupViewModel
{
    public LogFileGroupViewModel(string title, IReadOnlyList<LogFileItemViewModel> files)
    {
        Title = title;
        Files = files;
    }

    public string Title { get; }

    public IReadOnlyList<LogFileItemViewModel> Files { get; }

    public bool HasFiles => Files.Count > 0;

    public bool HasNoFiles => Files.Count == 0;

    public string CountText => Files.Count.ToString(CultureInfo.InvariantCulture);
}
