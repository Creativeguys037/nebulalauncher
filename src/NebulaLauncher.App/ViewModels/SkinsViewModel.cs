using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 皮肤页（票 41）：左边翻网络皮肤库，右边 3D 预览选中的那套。
///
/// 三态与断网：加载中显示进度条，失败在顶部挂一条可关闭的错误条，
/// 列表**保留**上一次拿到的行（这就是「已缓存内容仍可看」），只有第一次加载失败才出现空态。
///
/// 浏览皮肤库不需要登录，所以顶部账户区只做展示（跟随当前账户），不参与取数。
/// 在途请求分三条线各自收尾：列表、缩略图、3D 预览；切页时统一取消，回页时补拉打断的缩略图。
/// </summary>
public sealed partial class SkinsViewModel : PageViewModelBase
{
    /// <summary>缩略图并发上限。20 行一次全甩出去会把站点的连接与解码同时打满。</summary>
    private const int MaxConcurrentThumbnails = 4;

    private readonly SkinLibraryClient _library;

    private readonly ILauncherPaths? _paths;

    private IAccountService? _accounts;

    private IAppLog? _log;

    private CancellationTokenSource? _pageCancellation;

    private CancellationTokenSource? _thumbnailsCancellation;

    private CancellationTokenSource? _previewCancellation;

    /// <summary>缩略图按 tid 缓存：翻页回来不重拉。位图随页面一起释放。</summary>
    private readonly Dictionary<int, Bitmap> _thumbnailCache = [];

    /// <summary>像素缓存，3D 预览在同一页里来回点不重复下载。</summary>
    private readonly Dictionary<int, ArgbSkinTexture> _previewCache = [];

    private bool _subscribed;

    /// <summary>当前错误是不是「根本连不上」。决定错误条写「无网络」还是「皮肤站异常」。</summary>
    private bool _lastErrorIsNetwork;

    private string? _subtitle;

    // ==================== 本地皮肤（票 42） ====================

    /// <summary>本地皮肤库。数据目录没注册时是 null，页面显示「库不可用」而不是白屏。</summary>
    private LocalSkinStore? _localStore;

    private IPlatformService? _platform;

    /// <summary>本地目录的「打开目录 / 扫描」在途标记。与网络那三条取消线分开，互不打断。</summary>
    private CancellationTokenSource? _localCancellation;

    /// <summary>本地行的平面小图，按文件名缓存。文件被删后同名新图会命中旧缓存，但只要改名规则带序号就能避开。</summary>
    private readonly Dictionary<string, Bitmap> _localThumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本地列表「至少扫过一次」。没扫过不显示空态，否则进页面会先闪一下「还没有皮肤」。</summary>
    private bool _hasLoadedLocalOnce;

    public SkinsViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _log = services.GetService(typeof(IAppLog)) as IAppLog;
        _accounts = services.GetService(typeof(IAccountService)) as IAccountService;

        // 优先用容器注册的实例（测试与将来的多站配置都靠它）；组合根还没注册时自己造一个，
        // 这样页面独立可用。票 41 里记录了「建议在容器里注册 SkinLibraryClient」。
        _library = services.GetService(typeof(SkinLibraryClient)) as SkinLibraryClient
                   ?? new SkinLibraryClient(baseAddress: ResolveBaseAddress(), log: _log);

        // 本地皮肤库跟着数据目录走；容器里没注册路径时退化成「不可用」，不抛异常。
        _paths = services.GetService(typeof(ILauncherPaths)) as ILauncherPaths;
        _localStore = _paths is null ? null : new LocalSkinStore(_paths, _log);
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(PageSummary));
            OnPropertyChanged(nameof(ShowLoadingPlaceholder));
        };

        LocalItems.CollectionChanged += (_, _) => NotifyLocalState();

        PreviewVisual = SkinVisual.Default;
        ApplyAccount(_accounts?.ActiveAccount);
    }

    public override string Title => "皮肤";

    public override string Key => NavigationKeys.Skins;

    public override string? Subtitle => _subtitle;

    /// <summary>皮肤库地址：默认 littleskin.cn，可用环境变量指到自建站或本地 stub。</summary>
    public string LibraryAddress => _library.BaseAddress;

    public ObservableCollection<SkinEntryViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isNetworkTab = true;

    public bool IsLocalTab => !IsNetworkTab;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>非空即显示错误条。断网、状态码异常、返回的不是 JSON 都落到这里。</summary>
    [ObservableProperty]
    private string? _errorText;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>错误条的徽标文案。把 HTTP 500 说成「无网络」会让人白折腾网络设置。</summary>
    public string ErrorBadge => _lastErrorIsNetwork ? "无网络" : "皮肤站异常";

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private int _pageNumber = 1;

    [ObservableProperty]
    private bool _isLastPage = true;

    /// <summary>是否已经成功或失败地加载过一页。只影响空态判定，不需要单独通知 UI。</summary>
    private bool _hasLoadedOnce;

    public bool CanGoNext => !IsLastPage && !IsLoading;

    public bool CanGoPrev => PageNumber > 1 && !IsLoading;

    public string PageLabel => $"第 {PageNumber} 页";

    public string PageSummary => Items.Count == 0 ? "没有皮肤" : $"本页 {Items.Count} 套";

    public bool HasItems => Items.Count > 0;

    /// <summary>空态只在「加载过 + 不在加载 + 没有行」时出现，避免首屏闪一下空状态。</summary>
    public bool IsEmpty => _hasLoadedOnce && !IsLoading && Items.Count == 0;

    /// <summary>首屏加载占位：正在读、手上一行都没有。断网重试时若已有旧行就不遮挡。</summary>
    public bool ShowLoadingPlaceholder => IsLoading && Items.Count == 0;

    /// <summary>空态说明随失败原因换口径：断网时说「换个关键字」等于甩锅给用户的搜索词。</summary>
    public string EmptyHint => HasError
        ? "检查网络后点上面的重试；之前加载过的那一页会照样列出来。"
        : "换个关键字，或者清空搜索看整页。";

    [ObservableProperty]
    private string _accountName = "还没有账户";

    [ObservableProperty]
    private string _accountBadge = "未登录";

    [ObservableProperty]
    private string _accountDetail = "皮肤库不用登录也能逛";

    [ObservableProperty]
    private bool _hasAccount;

    [ObservableProperty]
    private SkinEntryViewModel? _selected;

    [ObservableProperty]
    private SkinVisual? _previewVisual;

    [ObservableProperty]
    private string _previewName = "还没有选中皮肤";

    [ObservableProperty]
    private string _previewDetail = "点左侧任意一行，这里立刻换成它";

    // ==================== 本地页签的状态与列表（票 42） ====================

    /// <summary>本地皮肤行。跟 <see cref="Items"/> 是两条独立的列表：一边是站点的，一边是磁盘的。</summary>
    public ObservableCollection<LocalSkinEntryViewModel> LocalItems { get; } = [];

    /// <summary>本地皮肤目录的绝对路径，页面上直接显示给用户看。</summary>
    public string LocalDirectory => _localStore?.DirectoryPath ?? "数据目录未配置";

    [ObservableProperty]
    private LocalSkinEntryViewModel? _selectedLocal;

    [ObservableProperty]
    private bool _isLocalLoading;

    /// <summary>本地页签自己的错误文案。跟页面顶部的网络错误条分开，免得「打不开文件夹」被说成「皮肤站异常」。</summary>
    [ObservableProperty]
    private string? _localErrorText;

    public bool HasLocalError => !string.IsNullOrWhiteSpace(LocalErrorText);

    public bool HasLocalItems => LocalItems.Count > 0;

    /// <summary>扫过一次、扫完了、结果为空——三个条件齐了才显示空态，避免进页面先闪一下。</summary>
    public bool IsLocalEmpty => _hasLoadedLocalOnce && !IsLocalLoading && LocalItems.Count == 0;

    public bool ShowLocalLoadingPlaceholder => IsLocalLoading && LocalItems.Count == 0;

    public bool IsLocalDirectoryAvailable => _localStore is not null;

    public string LocalSummary => LocalItems.Count == 0 ? "本地还没有皮肤" : $"本地 {LocalItems.Count} 套皮肤";

    public override async Task OnNavigatedToAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        AttachAccounts();
        ApplyAccount(_accounts?.ActiveAccount);

        // 每次进页面都重扫本地目录：用户可能刚把图丢进文件夹（票 42 的 AC4）。
        // 只读文件头，代价是几十毫秒，比「必须手动刷新」符合直觉。
        await LoadLocalAsync().ConfigureAwait(true);

        if (!_hasLoadedOnce)
        {
            await LoadPageAsync(1).ConfigureAwait(true);
            return;
        }

        // 回到页面：补拉上次切页时被打断的缩略图，不重发列表请求。
        StartThumbnailLoading();
    }

    /// <summary>离开页面：取消在途请求并退订账户事件（PITFALLS 2.7：离开与销毁都要退订）。</summary>
    public override Task OnNavigatedFromAsync()
    {
        DetachAccounts();
        Cancel(ref _pageCancellation);
        Cancel(ref _thumbnailsCancellation);
        Cancel(ref _previewCancellation);
        return Task.CompletedTask;
    }

    private void DetachAccounts()
    {
        if (_subscribed && _accounts is not null)
        {
            _accounts.AccountsChanged -= OnAccountsChanged;
            _subscribed = false;
        }
    }

    [RelayCommand]
    private void SelectNetworkTab()
    {
        IsNetworkTab = true;
        CloseError();
    }

    [RelayCommand]
    private void SelectLocalTab()
    {
        IsNetworkTab = false;
        CloseError();

        // 切到本地页签顺手重扫：手上没有「刷新」按钮的旧用户也能看到新丢进来的图。
        _ = LoadLocalAsync();
    }

    // ==================== 本地皮肤的命令（票 42） ====================

    [RelayCommand]
    private Task RefreshLocalAsync() => LoadLocalAsync();

    /// <summary>「打开目录」：目录不存在就先建出来，别让资源管理器报错。</summary>
    [RelayCommand]
    private async Task OpenLocalDirectoryAsync()
    {
        if (_localStore is null)
        {
            LocalErrorText = "本地皮肤库没有初始化，打不开目录。";
            return;
        }

        try
        {
            Directory.CreateDirectory(_localStore.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Warn($"创建本地皮肤目录失败：{_localStore.DirectoryPath}", ex);
            LocalErrorText = $"建不了本地皮肤目录：{ex.Message}";
            return;
        }

        if (_platform is null)
        {
            LocalErrorText = "当前环境没有可用的桌面集成，打不开目录。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(_localStore.DirectoryPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log?.Warn($"打开本地皮肤目录失败：{_localStore.DirectoryPath}", ex);
            LocalErrorText = "打开本地皮肤目录失败。";
        }
    }

    /// <summary>删除一行本地皮肤。文件删不掉时保留这一行，并把原因写在页签里。</summary>
    [RelayCommand]
    private async Task DeleteLocalAsync(LocalSkinEntryViewModel? entry)
    {
        if (entry is null || _localStore is null || IsDisposed)
        {
            return;
        }

        var result = await _localStore.DeleteAsync(entry.FileName).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            LocalErrorText = result.ErrorText;
            return;
        }

        LocalErrorText = null;
        _localThumbnailCache.Remove(entry.FileName);

        var index = LocalItems.IndexOf(entry);
        if (ReferenceEquals(SelectedLocal, entry))
        {
            SelectedLocal = null;
        }

        LocalItems.Remove(entry);

        // 删完顺手把选中挪到下一行，用户连删时不用每次重新点。
        if (index >= 0 && LocalItems.Count > 0)
        {
            SelectedLocal = LocalItems[Math.Min(index, LocalItems.Count - 1)];
        }
    }

    /// <summary>网络页签那一行的「下载到本地」：拉原图、存进本地目录、刷新本地列表。</summary>
    [RelayCommand]
    private async Task DownloadToLocalAsync(SkinEntryViewModel? entry)
    {
        if (entry is null || _localStore is null || IsDisposed || entry.IsSavingToLocal)
        {
            if (entry is not null && _localStore is null)
            {
                entry.LocalState = "本地皮肤库不可用";
            }

            return;
        }

        entry.IsSavingToLocal = true;
        entry.LocalState = "正在保存…";
        var token = _pageCancellation?.Token ?? CancellationToken.None;

        try
        {
            var info = await _library.InfoAsync(entry.Tid, token).ConfigureAwait(true);
            if (!info.Succeeded || info.Value is not { } detail)
            {
                entry.LocalState = info.ErrorText ?? "拿不到这套皮肤的详情";
                return;
            }

            var png = await _library.DownloadAsync(_library.TextureUrl(detail.Hash), token).ConfigureAwait(true);
            if (png is null)
            {
                entry.LocalState = "皮肤图下载失败";
                return;
            }

            var fileName = string.IsNullOrWhiteSpace(entry.Name) ? "skin" : entry.Name;
            var model = entry.Model == SkinModelKind.Alex ? LocalSkinModel.Slim : LocalSkinModel.Classic;
            var result = await _localStore.SaveAsync(fileName, png, model).ConfigureAwait(true);

            // 站点把细手臂皮肤标成 Alex，但图可能不是 64×64：退成经典模型仍然能显示，别让用户点了个寂寞。
            if (!result.IsSuccess && model == LocalSkinModel.Slim)
            {
                result = await _localStore.SaveAsync(fileName, png, LocalSkinModel.Classic).ConfigureAwait(true);
            }

            if (!result.IsSuccess)
            {
                entry.LocalState = result.ErrorText;
                return;
            }

            entry.LocalState = "已存到本地";
            await LoadLocalAsync().ConfigureAwait(true);
            if (result.Entry is { } saved)
            {
                SelectedLocal = LocalItems.FirstOrDefault(
                    item => string.Equals(item.FileName, saved.FileName, StringComparison.OrdinalIgnoreCase)) ?? SelectedLocal;
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"下载皮肤到本地失败：{entry.Name}", ex);
            entry.LocalState = "保存失败";
        }
        finally
        {
            entry.IsSavingToLocal = false;
        }
    }

    /// <summary>视图选完文件后调这里。路径由视图层从文件选择器拿到（VM 不碰 Avalonia 的对话框）。</summary>
    public async Task ImportLocalSkinAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsDisposed)
        {
            return;
        }

        if (_localStore is null)
        {
            LocalErrorText = "本地皮肤库没有初始化，看看数据目录配置。";
            return;
        }

        var result = await _localStore.ImportAsync(path).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            LocalErrorText = result.ErrorText;
            return;
        }

        LocalErrorText = null;
        await LoadLocalAsync().ConfigureAwait(true);

        if (result.Entry is { } imported)
        {
            SelectedLocal = LocalItems.FirstOrDefault(
                item => string.Equals(item.FileName, imported.FileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task LoadLocalAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_localStore is null)
        {
            _hasLoadedLocalOnce = true;
            LocalErrorText = "本地皮肤库没有初始化，看看数据目录配置。";
            NotifyLocalState();
            return;
        }

        var cancellation = new CancellationTokenSource();
        Replace(ref _localCancellation, cancellation);
        IsLocalLoading = true;

        try
        {
            var result = await _localStore.ScanAsync(cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            _hasLoadedLocalOnce = true;
            if (result.HasError)
            {
                LocalErrorText = result.ErrorText;
                NotifyLocalState();
                return;
            }

            LocalErrorText = null;
            ReplaceLocalItems(result.Entries);
            NotifyLocalState();
            StartLocalThumbnailLoading();
        }
        catch (OperationCanceledException)
        {
            // 切页或关页面：结果作废，不覆盖当前列表。
        }
        finally
        {
            if (ReferenceEquals(_localCancellation, cancellation))
            {
                IsLocalLoading = false;
            }
        }
    }

    private void ReplaceLocalItems(IReadOnlyList<LocalSkinEntry> entries)
    {
        // 重扫会换一批视图模型，选中项按文件名接回去：同名同图，用户不该感觉「选中没了」。
        var selectedName = SelectedLocal?.FileName;

        LocalItems.Clear();
        foreach (var entry in entries)
        {
            LocalItems.Add(new LocalSkinEntryViewModel(entry));
        }

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            SelectedLocal = LocalItems.FirstOrDefault(
                item => string.Equals(item.FileName, selectedName, StringComparison.OrdinalIgnoreCase));
        }

        // 只有停在本地页签时才替用户选第一套：从网络页签进页面时本地列表也在后台扫完，
        // 这时候抢预览会把用户正在看的网络皮肤顶掉。
        if (IsLocalTab && SelectedLocal is null)
        {
            SelectedLocal = LocalItems.FirstOrDefault();
        }
    }

    private void StartLocalThumbnailLoading()
    {
        foreach (var item in LocalItems.ToList())
        {
            _ = LoadLocalThumbnailAsync(item);
        }
    }

    private async Task LoadLocalThumbnailAsync(LocalSkinEntryViewModel item)
    {
        if (_localThumbnailCache.TryGetValue(item.FileName, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }

        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(item.FilePath)).ConfigureAwait(true);
            if (IsDisposed || !LocalItems.Contains(item))
            {
                return;
            }

            var bitmap = DecodeBitmap(bytes);
            if (bitmap is null)
            {
                return;
            }

            _localThumbnailCache[item.FileName] = bitmap;
            item.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单张图读不了不影响列表：那一行继续显示占位块。
            _log?.Warn($"读取本地皮肤缩略图失败：{item.FileName}", ex);
        }
    }

    /// <summary>选中本地一行：右侧 3D 卡直接读磁盘上的原图，不走网络。</summary>
    private async Task PreviewLocalAsync(LocalSkinEntryViewModel? item)
    {
        if (item is null || IsDisposed)
        {
            return;
        }

        // 与网络预览共用同一条取消线：最后点的那一行赢。
        var cancellation = new CancellationTokenSource();
        Replace(ref _previewCancellation, cancellation);

        PreviewName = item.Name;
        PreviewDetail = item.DetailLabel;
        IsPreviewLoading = true;

        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(item.FilePath), cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            var texture = SkinTextureLoader.DecodePng(bytes, _log);
            if (texture is null)
            {
                PreviewDetail = "皮肤图无法解码";
                return;
            }

            PreviewVisual = SkinTextureLoader.CreateVisual(texture, item.Model == LocalSkinModel.Slim);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PreviewDetail = "读不了这个皮肤文件";
            _log?.Warn($"读取本地皮肤失败：{item.FilePath}", ex);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    private void NotifyLocalState()
    {
        OnPropertyChanged(nameof(HasLocalItems));
        OnPropertyChanged(nameof(IsLocalEmpty));
        OnPropertyChanged(nameof(ShowLocalLoadingPlaceholder));
        OnPropertyChanged(nameof(LocalSummary));
    }

    [RelayCommand]
    private void CloseError() => ErrorText = null;

    [RelayCommand]
    private Task RetryAsync() => LoadPageAsync(Math.Max(1, PageNumber));

    [RelayCommand]
    private Task SearchAsync()
    {
        PageNumber = 1;
        return LoadPageAsync(1);
    }

    [RelayCommand]
    private Task NextPageAsync() => IsLastPage ? Task.CompletedTask : LoadPageAsync(PageNumber + 1);

    [RelayCommand]
    private Task PreviousPageAsync() => PageNumber <= 1 ? Task.CompletedTask : LoadPageAsync(PageNumber - 1);

    private static string ResolveBaseAddress()
    {
        var configured = Environment.GetEnvironmentVariable("NEBULA_SKIN_LIBRARY");
        return string.IsNullOrWhiteSpace(configured) ? SkinLibraryClient.DefaultBaseAddress : configured.Trim();
    }

    /// <summary>
    /// 拉一页。失败时**不清空**列表：断网时已缓存的行还要能看、能点，
    /// 错误只以顶部错误条的形式出现（票 41 的三态要求）。
    /// </summary>
    private async Task LoadPageAsync(int page)
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _pageCancellation, cancellation);

        IsLoading = true;
        ErrorText = null;

        try
        {
            var result = await _library
                .ListAsync(page, SkinLibraryFilter.All, Keyword, "time", cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded || result.Value is not { } paged)
            {
                _hasLoadedOnce = true;
                _lastErrorIsNetwork = result.IsNetworkError;
                ErrorText = result.ErrorText ?? "连不上皮肤站，检查网络后重试。";
                OnPropertyChanged(nameof(ErrorBadge));
                OnPropertyChanged(nameof(IsEmpty));

                if (!result.IsNetworkError)
                {
                    // 站点连上了但不像皮肤库：把 /api 报的版本带出来，比只说「异常」有用得多。
                    // 这也是票面要求的「接入自建站前先看 blessing_skin 版本」的落点。
                    var version = await _library.ProbeVersionAsync(cancellation.Token).ConfigureAwait(true);
                    if (!cancellation.IsCancellationRequested && version is not null)
                    {
                        ErrorText = $"{ErrorText} 站点 API 版本 {version}。";
                    }
                }

                return;
            }

            ReplaceItems(paged);
            PageNumber = paged.Page;
            IsLastPage = paged.IsLastPage;
            _hasLoadedOnce = true;
            SetSubtitle(paged.Items.Count == 0
                ? "这一页没有皮肤"
                : $"{LibraryAddress} · 每页 {SkinLibraryClient.PageSize} 套");

            SyncSelectionAfterLoad();
            StartThumbnailLoading();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_pageCancellation, cancellation))
            {
                IsLoading = false;
            }
        }
    }

    private void ReplaceItems(SkinLibraryPage page)
    {
        Items.Clear();

        foreach (var item in page.Items)
        {
            var entry = new SkinEntryViewModel(item);
            if (_thumbnailCache.TryGetValue(item.Tid, out var thumbnail))
            {
                entry.Thumbnail = thumbnail;
            }

            Items.Add(entry);
        }
    }

    /// <summary>
    /// 翻页或搜索之后，选中行必须落在新一页里：旧 entry 已经不在 <see cref="Items"/> 中，
    /// 若放着不管，右侧 3D 卡会一直显示上一页的皮肤（判据 8 要求点任一行立刻换成它）。
    /// </summary>
    private void SyncSelectionAfterLoad()
    {
        if (Items.Count == 0)
        {
            Selected = null;
            PreviewName = "还没有选中皮肤";
            PreviewDetail = "点左侧任意一行，这里立刻换成它";
            PreviewVisual = SkinVisual.Default;
            return;
        }

        if (Selected is null || !Items.Contains(Selected))
        {
            Selected = Items[0];
        }
    }

    private void StartThumbnailLoading()
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _thumbnailsCancellation, cancellation);
        _ = RunThumbnailLoadingAsync(cancellation.Token);
    }

    /// <summary>后台任务没有观察者，异常必须在这里就地消化（PITFALLS 2.8）。</summary>
    private async Task RunThumbnailLoadingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadThumbnailsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 切页 / 翻页打断，正常路径。
        }
        catch (Exception ex)
        {
            _log?.Warn("加载皮肤缩略图失败", ex);
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentThumbnails);
        var pending = Items.Where(entry => !entry.HasThumbnail).ToList();

        var tasks = pending.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);

            try
            {
                await LoadThumbnailAsync(entry, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Warn($"加载皮肤缩略图失败：{entry.Tid}", ex);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task LoadThumbnailAsync(SkinEntryViewModel entry, CancellationToken cancellationToken)
    {
        // 站点的 /preview/{tid} 是最省事的缩略图（一张图搞定）。解不出来（比如 WebP 不被支持）
        // 就退回原始纹理 PNG —— 至少配色认得出来，比一行空白强。
        var bitmap = await TryLoadBitmapAsync(_library.PreviewUrl(entry.Tid), cancellationToken).ConfigureAwait(true);
        bitmap ??= await TryLoadSkinTextureBitmapAsync(entry.Tid, cancellationToken).ConfigureAwait(true);

        if (bitmap is null || cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        entry.Thumbnail = bitmap;
        _thumbnailCache[entry.Tid] = bitmap;
    }

    private async Task<Bitmap?> TryLoadSkinTextureBitmapAsync(int tid, CancellationToken cancellationToken)
    {
        var info = await _library.InfoAsync(tid, cancellationToken).ConfigureAwait(true);
        if (!info.Succeeded || info.Value is not { } detail)
        {
            return null;
        }

        return await TryLoadBitmapAsync(_library.TextureUrl(detail.Hash), cancellationToken).ConfigureAwait(true);
    }

    private async Task<Bitmap?> TryLoadBitmapAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await _library.DownloadAsync(url, cancellationToken).ConfigureAwait(true);
        return bytes is null ? null : DecodeBitmap(bytes);
    }

    private Bitmap? DecodeBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            // 缩略图解不出来不是错误态：行里继续显示占位块。
            _log?.Debug($"缩略图解码失败：{ex.GetType().Name}");
            return null;
        }
    }

    private async Task PreviewAsync(SkinEntryViewModel? entry)
    {
        if (entry is null || IsDisposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        Replace(ref _previewCancellation, cancellation);

        PreviewName = entry.Name;
        PreviewDetail = $"{entry.ModelLabel} · {entry.LikesLabel}";

        if (_previewCache.TryGetValue(entry.Tid, out var cachedTexture))
        {
            PreviewVisual = BuildVisual(entry, cachedTexture);
            IsPreviewLoading = false;
            return;
        }

        IsPreviewLoading = true;

        try
        {
            var info = await _library.InfoAsync(entry.Tid, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!info.Succeeded || info.Value is not { } detail)
            {
                PreviewDetail = info.ErrorText ?? "拿不到这套皮肤的详情";
                return;
            }

            var png = await _library.DownloadAsync(_library.TextureUrl(detail.Hash), cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (png is null)
            {
                PreviewDetail = "皮肤图下载失败";
                return;
            }

            var texture = SkinTextureLoader.DecodePng(png, _log);
            if (texture is null)
            {
                PreviewDetail = "皮肤图无法解码";
                return;
            }

            _previewCache[entry.Tid] = texture;
            PreviewVisual = BuildVisual(entry, texture);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    private static SkinVisual BuildVisual(SkinEntryViewModel entry, ArgbSkinTexture texture) =>
        SkinTextureLoader.CreateVisual(texture, entry.Model == SkinModelKind.Alex);

    private void AttachAccounts()
    {
        if (_subscribed || _accounts is null)
        {
            return;
        }

        _accounts.AccountsChanged += OnAccountsChanged;
        _subscribed = true;
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyAccount(_accounts?.ActiveAccount));
            return;
        }

        ApplyAccount(_accounts?.ActiveAccount);
    }

    private void ApplyAccount(Account? account)
    {
        HasAccount = account is not null;

        // 没有账户时标题与徽标不能都写「未登录」，那样同一行重复两遍同一个词；
        // 标题说状态，徽标说类型位还在（票面要求这一行永远显示类型徽标的位置）。
        AccountName = account is null || string.IsNullOrWhiteSpace(account.Username) ? "还没有账户" : account.Username!;
        AccountBadge = account is null ? "未登录" : AccountTypeDisplay.ShortBadgeOf(account.Type);
        AccountDetail = account is null
            ? "皮肤库不用登录也能逛"
            : AccountTypeDisplay.DetailOf(account, $"UUID {ShortUuid(account.Uuid)}");
    }

    private static string ShortUuid(string? uuid) =>
        string.IsNullOrWhiteSpace(uuid) ? "未记录" : uuid[..Math.Min(8, uuid.Length)];

    private void SetSubtitle(string? value)
    {
        if (string.Equals(_subtitle, value, StringComparison.Ordinal))
        {
            return;
        }

        _subtitle = value;
        OnPropertyChanged(nameof(Subtitle));
    }

    private static void Replace(ref CancellationTokenSource? field, CancellationTokenSource next)
    {
        var previous = Interlocked.Exchange(ref field, next);
        previous?.Cancel();
        previous?.Dispose();
    }

    private static void Cancel(ref CancellationTokenSource? field)
    {
        var previous = Interlocked.Exchange(ref field, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    partial void OnIsNetworkTabChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocalTab));

        // 从本地切回网络：把预览卡还给网络列表当前选中的那套，别让右侧停在本地皮肤上。
        if (value)
        {
            _ = PreviewAsync(Selected);
        }
        else
        {
            _ = PreviewLocalAsync(SelectedLocal);
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingPlaceholder));
    }

    partial void OnPageNumberChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(PageLabel));
    }

    partial void OnIsLastPageChanged(bool value) => OnPropertyChanged(nameof(CanGoNext));

    partial void OnErrorTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(EmptyHint));
    }

    partial void OnSelectedChanged(SkinEntryViewModel? value)
    {
        // 预览卡只有一张，而两个页签的列表都会在后台加载完并设选中项。
        // 用当前页签决定谁有资格写它，否则成了「谁后到谁赢」——
        // 进页面时先扫本地、后拉网络，右侧就会停在网络皮肤上。
        if (IsNetworkTab)
        {
            _ = PreviewAsync(value);
        }
    }

    partial void OnSelectedLocalChanged(LocalSkinEntryViewModel? value)
    {
        if (IsLocalTab)
        {
            _ = PreviewLocalAsync(value);
        }
    }

    partial void OnIsLocalLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocalEmpty));
        OnPropertyChanged(nameof(ShowLocalLoadingPlaceholder));
    }

    partial void OnLocalErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasLocalError));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachAccounts();

            Cancel(ref _pageCancellation);
            Cancel(ref _thumbnailsCancellation);
            Cancel(ref _previewCancellation);
            Cancel(ref _localCancellation);

            _library.Dispose();

            foreach (var bitmap in _thumbnailCache.Values)
            {
                bitmap.Dispose();
            }

            foreach (var bitmap in _localThumbnailCache.Values)
            {
                bitmap.Dispose();
            }

            _thumbnailCache.Clear();
            _previewCache.Clear();
            _localThumbnailCache.Clear();

            _accounts = null;
            _log = null;
            _localStore = null;
            _platform = null;
        }

        base.Dispose(disposing);
    }
}
