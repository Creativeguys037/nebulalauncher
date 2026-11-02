using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Skinning;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 云端衣柜（Blessing Skin / littleskin 网页会话）。
///
/// 这一层只做三件事：把网页会话包装成可以点的动作、把结果翻译成中文、把界面状态收在一个地方。
/// 网络请求、CSRF、分页解析都在 <see cref="CloudClosetClient"/>；密码落在
/// <see cref="SkinSiteCredentialStore"/>，只在需要自动登录时读回。
///
/// 删除按钮只对启动器自己上传并记录过的 tid 开放。这不是信任站点，而是 Q4=A 的边界：
/// 衣柜里可能有用户从网页端传的材质，误删没法找回。
/// </summary>
public sealed partial class CloudClosetViewModel : ObservableObject, IDisposable
{
    private const int MaxConcurrentThumbnails = 4;

    private readonly SkinSiteCredentialStore _credentials;

    private readonly LocalSkinStore _skins;

    private readonly LocalCapeStore _capes;

    private readonly SkinChangeNotifier? _changes;

    private readonly IAppLog? _log;

    private readonly IPlatformService _platform;

    private readonly HashSet<int> _ownedTids = new();

    private readonly Dictionary<int, Bitmap> _thumbnailCache = new();

    private CloudClosetClient _client;

    private CancellationTokenSource? _loadCancellation;

    private CancellationTokenSource? _thumbnailCancellation;

    private CancellationTokenSource? _previewCancellation;

    private CancellationTokenSource? _oauthCancellation;

    private bool _activated;

    private bool _hasLoaded;

    private bool _disposed;

    public CloudClosetViewModel(
        ILauncherPaths paths,
        IPlatformService platform,
        IAppLog? log = null,
        SkinChangeNotifier? changes = null,
        CloudClosetClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(platform);

        _log = log;
        _platform = platform;
        _changes = changes;
        _credentials = new SkinSiteCredentialStore(paths, platform, log);
        _skins = new LocalSkinStore(paths, log);
        _capes = new LocalCapeStore(paths, log);
        _client = client ?? new CloudClosetClient(log: log);
        _client.OAuthTokenRefreshed += OnOAuthTokenRefreshed;
        SiteAddress = _client.BaseAddress;

        Items.CollectionChanged += (_, _) => NotifyListState();
        Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPlayers));
    }

    /// <summary>本机皮肤库变了（下载到本地之后），父页面据此刷新「本地皮肤」页签。</summary>
    public event EventHandler? LocalLibraryChanged;

    public ObservableCollection<CloudClosetItemViewModel> Items { get; } = new();

    public ObservableCollection<CloudPlayer> Players { get; } = new();

    public string SiteAddress
    {
        get => _siteAddress;
        set
        {
            if (SetProperty(ref _siteAddress, value))
            {
                OnPropertyChanged(nameof(SiteLabel));
            }
        }
    }

    private string _siteAddress = CloudClosetClient.DefaultBaseAddress;

    /// <summary>账号栏：用户名 @ 站点主机名。没登录时只显示站点。</summary>
    public string AccountLabel => string.IsNullOrWhiteSpace(Username)
        ? SiteLabel
        : $"{Username.Trim()} @ {SiteLabel}";

    public string SiteLabel => Uri.TryCreate(_client.BaseAddress, UriKind.Absolute, out var uri)
        ? uri.Host
        : _client.BaseAddress;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRestoring;

    /// <summary>正在等用户在浏览器里完成设备码授权。</summary>
    [ObservableProperty]
    private bool _isOAuthPending;

    /// <summary>设备码流给用户抄的短码；为空表示当前没有待授权请求。</summary>
    [ObservableProperty]
    private string? _oAuthUserCode;

    /// <summary>授权页地址（带 user_code 的完整链接），展示出来方便用户手动打开。</summary>
    [ObservableProperty]
    private string? _oAuthVerificationUri;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private CloudPlayer? _selectedPlayer;

    [ObservableProperty]
    private CloudClosetItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private bool _isCapeCategory;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private bool _isLastPage = true;

    [ObservableProperty]
    private bool _uploadIsSlim;

    [ObservableProperty]
    private bool _uploadIsPublic = true;

    [ObservableProperty]
    private SkinVisual? _previewVisual = SkinVisual.Default;

    [ObservableProperty]
    private string _previewName = "云端衣柜";

    [ObservableProperty]
    private string _previewDetail = "登录后选中一件材质，这里会显示 3D 预览。";

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>截图样本标记：只影响 <see cref="OnSelectedItemChanged"/> 不发起预览下载。</summary>
    private bool _screenshotDemo;

    /// <summary>LoadPlayersAsync 自动选中角色时置 true，避免把「自动选第一个」当成用户的选择。</summary>
    private bool _autoSelectingPlayer;

    /// <summary>用户在角色下拉里亲手选过角色。多角色时只有亲手选过才允许直接应用。</summary>
    private bool _playerChosenByUser;

    /// <summary>
    /// 截图模式专用（<c>--page skins --section cloud-demo</c>）：摆出登录后的衣柜形态。
    /// 全部是内存样本，不联网、不写凭据、不碰磁盘；用于检查列表行、选中态与右侧预览的布局。
    /// </summary>
    public void ApplyScreenshotDemo(bool empty = false)
    {
        _screenshotDemo = true;
        _ownedTids.Add(921824);
        _ownedTids.Add(921840);
        SiteAddress = CloudClosetClient.DefaultBaseAddress;
        Username = "Steve";
        IsLoggedIn = true;
        IsBusy = false;
        IsRestoring = false;

        Players.Clear();
        Players.Add(new CloudPlayer(1, "Steve", "8667ba71b85a4004af54457a9734eed7", 921824, null));
        Players.Add(new CloudPlayer(2, "Alex", "ec561538f3fd461daffd75d7e3d6a1b7", null, 921825));
        SelectedPlayer = Players[0];

        Items.Clear();
        Items.Add(new CloudClosetItemViewModel(
            new CloudClosetItem(921824, "steve.png", "steve", "a1b2c3d4", 12, true, null), isOwned: true));
        Items.Add(new CloudClosetItemViewModel(
            new CloudClosetItem(921830, "夏天短袖.png", "alex", "b2c3d4e5", 48, true, null)));
        Items.Add(new CloudClosetItemViewModel(
            new CloudClosetItem(921833, "冬装 ver.2.png", "steve", "c3d4e5f6", 5, false, null)));
        Items.Add(new CloudClosetItemViewModel(
            new CloudClosetItem(921840, "night-skin.png", "alex", "d4e5f6a7", 128, true, null), isOwned: true));
        Items.Add(new CloudClosetItemViewModel(
            new CloudClosetItem(921845, "cape-wave.png", "cape", "e5f6a7b8", 9, true, null)));

        Page = 1;
        IsLastPage = false;
        StatusText = "已自动登录皮肤站。";

        if (empty)
        {
            // 空态样本：登录了但衣柜是空的，检查空态卡与工具的禁用态。
            // _hasLoaded 必须先在清空之前置位：IsEmpty = _hasLoaded && !IsLoading && 没有行，
            // 靠 Items.Clear() 触发的那次通知才会把空态卡显示出来。
            _hasLoaded = true;
            Items.Clear();
            SelectedItem = null;
            PreviewName = "云端衣柜";
            PreviewDetail = "选中一件材质后，这里会显示 3D 预览。";
            PreviewVisual = SkinVisual.Default;
            return;
        }

        SelectedItem = Items[0];
        PreviewName = "steve.png";
        PreviewDetail = "皮肤 · Steve 体型 · 12 赞 · 公开";
        PreviewVisual = SkinVisual.Default;
    }

    public bool IsIdle => !IsBusy;

    public bool IsSignedOut => !IsLoggedIn;

    /// <summary>当前是 OAuth 免密会话：能应用/清除已有材质，不能上传 PNG、不能删除云端材质。</summary>
    public bool IsOAuthSession => _client.IsOAuthSession;

    /// <summary>没有待授权请求时才能再发起一次，避免同时开两个轮询。</summary>
    public bool CanStartOAuth => !IsBusy && !IsOAuthPending;

    /// <summary>上传 PNG 只有网页会话能做；OAuth 的 JSON API 没有新建材质端点。</summary>
    public bool CanUpload => IsLoggedIn && !IsBusy && !IsOAuthSession;

    public bool CanLogin => !IsBusy
                            && !string.IsNullOrWhiteSpace(Username)
                            && !string.IsNullOrWhiteSpace(Password);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public bool HasPlayers => Players.Count > 0;

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => _hasLoaded && !IsLoading && Items.Count == 0;

    public bool ShowLoadingPlaceholder => IsLoading && !_hasLoaded;

    public bool IsSkinCategory => !IsCapeCategory;

    public string CategoryLabel => IsCapeCategory ? "披风" : "皮肤";

    public string PageLabel => $"第 {Math.Max(1, Page)} 页";

    public string UploadLabel => IsCapeCategory ? "上传披风" : "上传皮肤";

    public string ClosetHint => IsCapeCategory
        ? "这里是你账号衣柜里的披风。选中一件可以应用到角色，或下载到本地。"
        : "这里是你账号衣柜里的皮肤。选中一件可以应用到角色，或下载到本地。";

    public string PlayerStatus => SelectedPlayer is null
        ? "还没有可选角色"
        : $"{SelectedPlayer.Name} · 皮肤 {(SelectedPlayer.TidSkin is null ? "未设置" : $"#{SelectedPlayer.TidSkin}")} · 披风 {(SelectedPlayer.TidCape is null ? "未设置" : $"#{SelectedPlayer.TidCape}")}";

    public string SelectedSummary => SelectedItem is null
        ? "选中一件材质后可以应用、下载或删除。"
        : $"{SelectedItem.Name} · {SelectedItem.ModelLabel} · {SelectedItem.LikesLabel}";

    public bool CanApply => IsLoggedIn && !IsBusy && SelectedItem is not null && SelectedPlayer is not null && HasChosenPlayer;

    /// <summary>多角色时必须由用户亲手选过，自动选中的第一个不算。只有一个角色时自动选中即可。</summary>
    public bool HasChosenPlayer => _playerChosenByUser || Players.Count <= 1;

    public bool CanClear => IsLoggedIn && !IsBusy && SelectedPlayer is not null;

    public bool CanDownload => IsLoggedIn && !IsBusy && SelectedItem is not null;

    public bool CanDelete => CanDownload && SelectedItem is not null && _ownedTids.Contains(SelectedItem.Tid) && !IsOAuthSession;

    public bool CanGoPrev => IsLoggedIn && !IsBusy && Page > 1;

    public bool CanGoNext => IsLoggedIn && !IsBusy && !IsLastPage;

    /// <summary>第一次切到云端页签时调用；恢复上次保存的账号并自动登录一次。</summary>
    public async Task ActivateAsync()
    {
        if (_activated || _disposed || _screenshotDemo)
        {
            // 截图样本已经摆好登录态，别再跑一次自动登录把样本覆盖成「第一次使用」。
            return;
        }

        _activated = true;
        IsRestoring = true;
        try
        {
            var credentials = await _credentials.ListAsync().ConfigureAwait(true);
            var saved = credentials.FirstOrDefault(static item => item.HasOAuthToken)
                        ?? credentials.FirstOrDefault(static item => !string.IsNullOrEmpty(item.Password));
            if (saved is null)
            {
                StatusText = "第一次使用：用皮肤站账号登录，或点「用设备码免密登录」，之后会尽量自动登录。";
                return;
            }

            SiteAddress = saved.BaseAddress;
            Username = saved.Username;
            RecreateClientIfNeeded();
            if (saved.HasOAuthToken)
            {
                // 免密凭据优先：密码可能早就没存或已经改了。过期了也让第一次请求去刷新，
                // 刷新失败会走 auth 错误提示重新授权，比在这里猜令牌状态简单。
                Password = string.Empty;
                _client.UseOAuthToken(
                    saved.AccessToken,
                    saved.RefreshToken,
                    saved.AccessTokenExpiresAt ?? DateTimeOffset.MinValue);
                IsLoggedIn = true;
                StatusText = "已恢复免密登录。";
                await LoadOwnedAsync().ConfigureAwait(true);
                await LoadPlayersAsync().ConfigureAwait(true);
                await LoadClosetAsync(1).ConfigureAwait(true);
                return;
            }

            Password = saved.Password;
            await LoginCoreAsync(saved.Password, isAutomatic: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorText = "读取本机保存的皮肤站账号失败，请重新登录。";
            _log?.Warn("恢复皮肤站账号失败", ex);
        }
        finally
        {
            IsRestoring = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy)
        {
            return;
        }

        RecreateClientIfNeeded();
        await LoginCoreAsync(Password, isAutomatic: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StartOAuthLoginAsync()
    {
        if (!CanStartOAuth || _disposed)
        {
            return;
        }

        RecreateClientIfNeeded();

        _oauthCancellation?.Cancel();
        _oauthCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _oauthCancellation = cancellation;

        IsOAuthPending = true;
        ErrorText = null;
        OAuthUserCode = null;
        OAuthVerificationUri = null;
        StatusText = "正在向 LittleSkin 申请设备代码…";
        try
        {
            var start = await _client.StartDeviceLoginAsync(cancellation.Token).ConfigureAwait(true);
            if (!start.Succeeded || start.Value is null)
            {
                ErrorText = start.ErrorText ?? "申请设备代码失败。";
                return;
            }

            var code = start.Value;
            OAuthUserCode = code.UserCode;
            OAuthVerificationUri = code.VerificationUriComplete;
            StatusText = $"在浏览器里确认代码 {code.UserCode}；授权完成这里会自动登录。";
            try
            {
                await _platform.OpenUrlAsync(code.VerificationUriComplete).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 打不开浏览器不该让整次授权白费：链接已经显示在界面上，用户可以手动打开。
                _log?.Warn("打开 LittleSkin 授权页失败", ex);
                StatusText = $"请手动打开 {code.VerificationUriComplete} 并确认代码 {code.UserCode}；授权完成这里会自动登录。";
            }

            var token = await _client.WaitForDeviceLoginAsync(code, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!token.Succeeded || token.Value is null)
            {
                ErrorText = token.ErrorText ?? "设备码登录没有完成。";
                StatusText = "免密登录没有完成，可以重试或改用账号密码登录。";
                return;
            }

            var user = await _client.GetOAuthUserAsync(cancellation.Token).ConfigureAwait(true);
            var nickname = user.Succeeded && user.Value is not null
                ? user.Value.Nickname
                : "LittleSkin 账号";
            Username = nickname;
            Password = string.Empty;
            IsLoggedIn = true;
            await _credentials
                .SaveOAuthAsync(
                    _client.BaseAddress,
                    nickname,
                    token.Value.AccessToken,
                    token.Value.RefreshToken,
                    token.Value.ExpiresAt)
                .ConfigureAwait(true);
            await LoadOwnedAsync().ConfigureAwait(true);
            StatusText = $"已用设备码登录：{nickname}（免密）。";
            await LoadPlayersAsync().ConfigureAwait(true);
            await LoadClosetAsync(1).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消免密登录。";
        }
        catch (Exception ex)
        {
            // 和密码登录一样兜底：设备码链路里的任何异常都不能穿透命令把进程带走。
            ErrorText = "免密登录过程出错：" + ex.Message;
            _log?.Warn("设备码登录异常", ex);
        }
        finally
        {
            if (ReferenceEquals(_oauthCancellation, cancellation))
            {
                IsOAuthPending = false;
                OAuthUserCode = null;
                OAuthVerificationUri = null;
                _oauthCancellation = null;
            }

            cancellation.Dispose();
            OnPropertyChanged(nameof(IsOAuthSession));
            OnPropertyChanged(nameof(CanUpload));
            OnPropertyChanged(nameof(CanDelete));
        }
    }

    [RelayCommand]
    private void CancelOAuthLogin()
    {
        if (!IsOAuthPending)
        {
            return;
        }

        _oauthCancellation?.Cancel();
        StatusText = "正在取消免密登录…";
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        _client.ResetSession();
        IsLoggedIn = false;
        Items.Clear();
        Players.Clear();
        SelectedItem = null;
        SelectedPlayer = null;
        _ownedTids.Clear();
        _hasLoaded = false;
        IsLastPage = true;
        Page = 1;
        PreviewVisual = SkinVisual.Default;
        PreviewName = "云端衣柜";
        PreviewDetail = "登录后选中一件材质，这里会显示 3D 预览。";
        StatusText = "已退出登录。本机保存的登录信息还在，下次进来可以继续自动登录。";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ForgetSavedAsync()
    {
        if (!string.IsNullOrWhiteSpace(Username))
        {
            await _credentials.ForgetAsync(_client.BaseAddress, Username).ConfigureAwait(true);
        }

        await LogoutAsync().ConfigureAwait(true);
        Username = string.Empty;
        Password = string.Empty;
        StatusText = "已清除本机保存的皮肤站账号。";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        await LoadPlayersAsync().ConfigureAwait(true);
        await LoadClosetAsync(Page).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        await LoadClosetAsync(1).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowSkinsAsync() => await SetCategoryAsync(false).ConfigureAwait(true);

    [RelayCommand]
    private async Task ShowCapesAsync() => await SetCategoryAsync(true).ConfigureAwait(true);

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CanGoPrev)
        {
            await LoadClosetAsync(Page - 1).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CanGoNext)
        {
            await LoadClosetAsync(Page + 1).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply || SelectedItem is null || SelectedPlayer is null)
        {
            return;
        }

        var item = SelectedItem;
        var player = SelectedPlayer;
        await RunActionAsync(
            IsCapeCategory ? "正在把披风应用到角色…" : "正在把皮肤应用到角色…",
            () => _client.ApplyAsync(player.Pid, IsCapeCategory ? "cape" : "skin", item.Tid, CancellationToken.None),
            "已应用到角色。",
            () =>
            {
                _changes?.Notify("皮肤站云端衣柜换肤");
                return Task.CompletedTask;
            }).ConfigureAwait(true);

        await LoadPlayersAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!CanClear || SelectedPlayer is null)
        {
            return;
        }

        var player = SelectedPlayer;
        await RunActionAsync(
            IsCapeCategory ? "正在清除角色披风…" : "正在清除角色皮肤…",
            () => _client.ClearAsync(player.Pid, IsCapeCategory ? "cape" : "skin", CancellationToken.None),
            "已清除角色身上的材质。",
            () =>
            {
                _changes?.Notify("皮肤站云端衣柜清除材质");
                return Task.CompletedTask;
            }).ConfigureAwait(true);

        await LoadPlayersAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DownloadToLocalAsync()
    {
        if (!CanDownload || SelectedItem is null)
        {
            return;
        }

        var item = SelectedItem;
        IsBusy = true;
        ErrorText = null;
        StatusText = "正在下载材质…";
        try
        {
            var result = await _client
                .DownloadTextureAsync(item.Tid, item.Hash, CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.Succeeded || result.Value is null)
            {
                ErrorText = result.ErrorText ?? "下载失败。";
                return;
            }

            var fileName = BuildLocalFileName(item);
            if (IsCapeCategory)
            {
                var saved = await _capes.SaveAsync(fileName, result.Value).ConfigureAwait(true);
                if (!saved.IsSuccess)
                {
                    ErrorText = saved.ErrorText ?? "保存到本地披风库失败。";
                    return;
                }

                StatusText = $"已保存到本地披风库：{saved.Entry?.FileName ?? fileName}";
            }
            else
            {
                var saved = await _skins.SaveAsync(fileName, result.Value, item.IsSlim ? LocalSkinModel.Slim : LocalSkinModel.Classic)
                    .ConfigureAwait(true);
                if (!saved.IsSuccess)
                {
                    ErrorText = saved.ErrorText ?? "保存到本地皮肤库失败。";
                    return;
                }

                StatusText = $"已保存到本地皮肤库：{saved.Entry?.FileName ?? fileName}";
            }

            LocalLibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanDelete || SelectedItem is null)
        {
            return;
        }

        var item = SelectedItem;
        IsBusy = true;
        ErrorText = null;
        StatusText = "正在删除云端材质…";
        try
        {
            var result = await _client.DeleteTextureAsync(item.Tid, CancellationToken.None).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = result.ErrorText ?? "删除失败。";
                return;
            }

            _ownedTids.Remove(item.Tid);
            await _credentials.ForgetOwnedTextureAsync(_client.BaseAddress, Username, item.Tid).ConfigureAwait(true);
            StatusText = $"已从云端衣柜删除：{item.Name}";
            await LoadClosetAsync(Page).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>文件选择由 View 负责，这里只接收路径；上传成功后自动刷新衣柜。</summary>
    public async Task UploadAsync(string filePath)
    {
        if (!IsLoggedIn || IsBusy)
        {
            return;
        }

        if (IsOAuthSession)
        {
            ErrorText = "免密登录不能上传新材质。要上传 PNG，请用账号密码网页会话登录。";
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = "读不了这个 PNG 文件。";
            _log?.Warn($"读取待上传材质失败：{filePath}", ex);
            return;
        }

        if (!LocalSkinFormat.TryReadPngSize(bytes, out var width, out var height))
        {
            ErrorText = "这不是有效的 PNG 文件。";
            return;
        }

        if (IsCapeCategory)
        {
            if (LocalCapeFormat.Validate(width, height) is { } capeError)
            {
                ErrorText = capeError;
                return;
            }
        }
        else if (LocalSkinFormat.Validate(width, height, UploadIsSlim ? LocalSkinModel.Slim : LocalSkinModel.Classic) is { } skinError)
        {
            ErrorText = skinError;
            return;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = IsCapeCategory ? "NebulaLauncher 披风" : "NebulaLauncher 皮肤";
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = "正在上传到皮肤站衣柜…";
        try
        {
            var type = IsCapeCategory ? "cape" : (UploadIsSlim ? "alex" : "steve");
            var result = await _client.UploadTextureAsync(name, bytes, type, UploadIsPublic, CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.Succeeded || result.Value is null)
            {
                ErrorText = result.ErrorText ?? "上传失败。";
                return;
            }

            var upload = result.Value;
            if (upload.IsDuplicate)
            {
                StatusText = $"这张 PNG 已经在衣柜里（tid {upload.Tid}），没有重复上传。";
            }
            else
            {
                _ownedTids.Add(upload.Tid);
                await _credentials.RememberOwnedTextureAsync(_client.BaseAddress, Username, upload.Tid)
                    .ConfigureAwait(true);
                StatusText = $"上传成功：{name}（tid {upload.Tid}）。";
            }

            await LoadClosetAsync(1).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 皮肤页「应用到皮肤站」（票 #61）：把内存里的 PNG 先传进衣柜，再应用到当前角色。
    ///
    /// <para>
    /// 不落盘、不自动登录、不替用户猜角色：多个角色时要求先选，只有一个角色才自动选中并把角色名写进结果。
    /// 返回 <see cref="CloudClosetApplyResult.NeedsLogin"/> 时由调用方切到云端衣柜页签。
    /// </para>
    /// </summary>
    public async Task<CloudClosetApplyResult> UploadAndApplyAsync(
        string name,
        byte[] png,
        bool isSlim,
        bool isPublic,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return CloudClosetApplyResult.Fail("页面已关闭，请重试。");
        }

        if (!IsLoggedIn)
        {
            return CloudClosetApplyResult.LoginRequired("先登录皮肤站，再把这套皮肤应用到角色。");
        }

        if (IsOAuthSession)
        {
            return CloudClosetApplyResult.Fail("免密登录不能上传新材质。要应用本地 PNG，请用账号密码网页会话登录。");
        }

        if (IsBusy)
        {
            return CloudClosetApplyResult.Fail("正在处理上一条操作，请稍后再试。");
        }

        var player = SelectedPlayer;
        if (player is null || (Players.Count > 1 && !_playerChosenByUser))
        {
            if (Players.Count == 1)
            {
                // 自动选中不算用户选择：否则 HasChosenPlayer 会误以为用户已经挑过角色。
                player = Players[0];
                _autoSelectingPlayer = true;
                try
                {
                    SelectedPlayer = player;
                }
                finally
                {
                    _autoSelectingPlayer = false;
                }
            }
            else if (Players.Count == 0)
            {
                return CloudClosetApplyResult.Fail("这个账号还没有角色，先在云端衣柜里刷新角色列表。");
            }
            else
            {
                return CloudClosetApplyResult.Fail("这个账号有多个角色，先在云端衣柜里选一个角色。");
            }
        }

        var textureName = SanitizeTextureName(name);
        IsBusy = true;
        ErrorText = null;
        StatusText = "正在把皮肤应用到皮肤站…";
        try
        {
            var type = isSlim ? "alex" : "steve";
            var upload = await _client.UploadTextureAsync(
                    textureName,
                    png,
                    type,
                    isPublic,
                    cancellationToken)
                .ConfigureAwait(true);
            if (!upload.Succeeded || upload.Value is null)
            {
                ErrorText = upload.ErrorText ?? "上传到皮肤站失败。";
                return CloudClosetApplyResult.Fail(ErrorText, upload.IsAuthError, canRetry: !upload.IsAuthError);
            }

            var tid = upload.Value.Tid;
            if (upload.Value.IsDuplicate)
            {
                StatusText = $"tid {tid} 没有重复上传，直接应用。";
            }
            else
            {
                _ownedTids.Add(tid);
            }

            var apply = await _client.ApplyAsync(player.Pid, "skin", tid, cancellationToken)
                .ConfigureAwait(true);
            if (!apply.Succeeded)
            {
                ErrorText = apply.ErrorText ?? "应用到角色失败。";
                return CloudClosetApplyResult.Fail(ErrorText, apply.IsAuthError, canRetry: !apply.IsAuthError);
            }

            // 票面要求以 GET /user/player/list 的结果为准。刷新失败不改变「已应用」，
            // 但要在成功文案里说明还没确认上。
            ErrorText = null;
            await LoadPlayersAsync(cancellationToken).ConfigureAwait(true);
            if (_disposed)
            {
                return CloudClosetApplyResult.Ok("已应用到皮肤站。", tid, player.Name);
            }

            var confirmed = string.IsNullOrEmpty(ErrorText);
            var text = confirmed
                ? $"已应用到皮肤站：{player.Name} · 皮肤 #{tid}"
                : $"已应用到皮肤站：{player.Name} · 皮肤 #{tid}（角色列表还没刷新，重进页面可确认）";
            StatusText = text;
            _changes?.Notify("皮肤站云端衣柜换肤");
            return CloudClosetApplyResult.Ok(text, tid, player.Name);
        }
        catch (OperationCanceledException)
        {
            return CloudClosetApplyResult.Fail("操作已取消，请重试。", canRetry: true);
        }
        catch (Exception ex)
        {
            _log?.Warn("从皮肤页上传并应用失败", ex);
            ErrorText = $"应用到皮肤站失败：{ex.Message}";
            return CloudClosetApplyResult.Fail(ErrorText, canRetry: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _loadCancellation?.Cancel();
        _thumbnailCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _oauthCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _thumbnailCancellation?.Dispose();
        _previewCancellation?.Dispose();
        _oauthCancellation?.Dispose();
        _client.OAuthTokenRefreshed -= OnOAuthTokenRefreshed;
        _client.Dispose();

        foreach (var bitmap in _thumbnailCache.Values)
        {
            if (bitmap is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _thumbnailCache.Clear();
    }

    partial void OnUsernameChanged(string value)
    {
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(AccountLabel));
    }

    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(CanLogin));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanStartOAuth));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnIsOAuthPendingChanged(bool value) => OnPropertyChanged(nameof(CanStartOAuth));

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(IsOAuthSession));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnIsCapeCategoryChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSkinCategory));
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(UploadLabel));
        OnPropertyChanged(nameof(ClosetHint));
    }

    partial void OnSelectedPlayerChanged(CloudPlayer? value)
    {
        if (!_autoSelectingPlayer)
        {
            _playerChosenByUser = true;
        }

        OnPropertyChanged(nameof(HasChosenPlayer));
        OnPropertyChanged(nameof(PlayerStatus));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanClear));
    }

    partial void OnSelectedItemChanged(CloudClosetItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
        if (_screenshotDemo)
        {
            // 截图样本不联网：预览区用方法里写好的文字，别让一次假的下载请求把卡刷成失败态。
            return;
        }

        _ = PreviewAsync(value);
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusTextChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingPlaceholder));
    }

    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(PageLabel));

    partial void OnIsLastPageChanged(bool value) => OnPropertyChanged(nameof(CanGoNext));

    private async Task LoginCoreAsync(string password, bool isAutomatic)
    {
        IsBusy = true;
        ErrorText = null;
        StatusText = isAutomatic ? "正在用本机保存的账号自动登录…" : "正在登录皮肤站…";
        try
        {
            var result = await _client.LoginAsync(Username.Trim(), password, remember: true, CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                IsLoggedIn = false;
                ErrorText = result.ErrorText ?? "登录失败。";
                return;
            }

            IsLoggedIn = true;
            Password = password;
            await _credentials.SaveAsync(_client.BaseAddress, Username.Trim(), password).ConfigureAwait(true);
            await LoadOwnedAsync().ConfigureAwait(true);
            StatusText = isAutomatic ? "已自动登录皮肤站。" : "已登录皮肤站。";
            await LoadPlayersAsync().ConfigureAwait(true);
            await LoadClosetAsync(1).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 上边界兜底：登录链路里的序列化 / 凭据落盘异常不能穿透 AsyncRelayCommand 把进程带走（票 #62）。
            IsLoggedIn = _client.IsAuthenticated;
            ErrorText = "皮肤站登录过程出错：" + ex.Message;
            _log?.Warn("皮肤站登录异常", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadOwnedAsync()
    {
        _ownedTids.Clear();
        var credential = await _credentials.FindAsync(_client.BaseAddress, Username).ConfigureAwait(true);
        if (credential is null)
        {
            return;
        }

        foreach (var tid in credential.OwnedTextureTids)
        {
            _ownedTids.Add(tid);
        }
    }

    private async Task LoadPlayersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn)
        {
            return;
        }

        var result = await _client.GetPlayersAsync(cancellationToken).ConfigureAwait(true);
        if (!result.Succeeded || result.Value is null)
        {
            ErrorText = result.ErrorText ?? "读取角色列表失败。";
            if (result.IsAuthError)
            {
                IsLoggedIn = false;
            }

            return;
        }

        var previousPid = SelectedPlayer?.Pid;
        Players.Clear();
        foreach (var player in result.Value)
        {
            Players.Add(player);
        }

        _autoSelectingPlayer = true;
        try
        {
            SelectedPlayer = Players.FirstOrDefault(player => player.Pid == previousPid) ?? Players.FirstOrDefault();
        }
        finally
        {
            _autoSelectingPlayer = false;
        }

        OnPropertyChanged(nameof(HasChosenPlayer));
    }

    private async Task SetCategoryAsync(bool cape)
    {
        if (IsCapeCategory == cape)
        {
            return;
        }

        IsCapeCategory = cape;
        if (!IsLoggedIn)
        {
            return;
        }

        SelectedItem = null;
        await LoadClosetAsync(1).ConfigureAwait(true);
    }

    private async Task LoadClosetAsync(int page)
    {
        if (!IsLoggedIn || _disposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        Replace(ref _loadCancellation, cancellation);
        IsLoading = true;
        ErrorText = null;

        try
        {
            var result = await _client.GetClosetAsync(
                Math.Max(1, page),
                IsCapeCategory ? "cape" : "skin",
                Keyword,
                cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!result.Succeeded || result.Value is null)
            {
                ErrorText = result.ErrorText ?? "读取衣柜失败。";
                if (result.IsAuthError)
                {
                    IsLoggedIn = false;
                    ErrorText = IsOAuthSession
                        ? "免密登录已失效，请重新点「用设备码免密登录」。"
                        : "登录已失效，请重新登录皮肤站。";
                }

                return;
            }

            var closet = result.Value;
            Page = Math.Max(1, closet.Page);
            IsLastPage = closet.IsLastPage;
            Items.Clear();
            foreach (var item in closet.Items)
            {
                Items.Add(new CloudClosetItemViewModel(item, _ownedTids.Contains(item.Tid)));
            }

            _hasLoaded = true;
            SelectedItem = null;
            StatusText = Items.Count == 0
                ? $"衣柜里还没有{CategoryLabel}。可以在上方上传一张。"
                : $"衣柜第 {Page} 页，共 {Items.Count} 件{CategoryLabel}。";

            _ = LoadThumbnailsAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 切页/切分类时正常放弃上一页。
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(ShowLoadingPlaceholder));
            }
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Replace(ref _thumbnailCancellation, cancellation);
        using var gate = new SemaphoreSlim(MaxConcurrentThumbnails);

        var tasks = Items.Select(
            async item =>
            {
                await gate.WaitAsync(cancellation.Token).ConfigureAwait(true);
                try
                {
                    await LoadThumbnailAsync(item, cancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // 列表已经换页。
                }
                catch (Exception ex)
                {
                    _log?.Debug($"加载云端衣柜缩略图失败：{item.Tid}（{ex.GetType().Name}）");
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task LoadThumbnailAsync(CloudClosetItemViewModel item, CancellationToken cancellationToken)
    {
        if (_thumbnailCache.TryGetValue(item.Tid, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }

        byte[]? bytes = null;
        var preview = await _client.DownloadPreviewAsync(item.Tid, cancellationToken).ConfigureAwait(true);
        if (preview.Succeeded)
        {
            bytes = preview.Value;
        }

        var bitmap = bytes is null ? null : DecodeBitmap(bytes);
        if (bitmap is null)
        {
            var texture = await _client
                .DownloadTextureAsync(item.Tid, item.Hash, cancellationToken)
                .ConfigureAwait(true);
            bitmap = texture.Succeeded && texture.Value is not null ? DecodeBitmap(texture.Value) : null;
        }

        if (bitmap is null || cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        if (!Items.Contains(item))
        {
            bitmap.Dispose();
            return;
        }

        _thumbnailCache[item.Tid] = bitmap;
        item.Thumbnail = bitmap;
    }

    private async Task PreviewAsync(CloudClosetItemViewModel? item)
    {
        var cancellation = new CancellationTokenSource();
        Replace(ref _previewCancellation, cancellation);

        if (item is null)
        {
            PreviewVisual = SkinVisual.Default;
            PreviewName = "云端衣柜";
            PreviewDetail = "选中一件材质，这里会显示 3D 预览。";
            IsPreviewLoading = false;
            return;
        }

        PreviewName = item.Name;
        PreviewDetail = item.DetailLabel;
        IsPreviewLoading = true;
        try
        {
            var result = await _client
                .DownloadTextureAsync(item.Tid, item.Hash, cancellation.Token)
                .ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!result.Succeeded || result.Value is null)
            {
                PreviewDetail = result.ErrorText ?? "这件材质的图读不到。";
                return;
            }

            var texture = SkinTextureLoader.DecodePng(result.Value, _log);
            if (texture is null)
            {
                PreviewDetail = "这件材质的 PNG 无法解码。";
                return;
            }

            PreviewVisual = item.IsCape
                ? SkinTextureLoader.CreateVisual(DefaultSkin.Create(), false, texture)
                : SkinTextureLoader.CreateVisual(texture, item.IsSlim);
        }
        catch (OperationCanceledException)
        {
            // 选中另一行时正常取消。
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    private async Task RunActionAsync(
        string progressText,
        Func<Task<CloudClosetResult<CloudOperationResult>>> action,
        string successText,
        Func<Task>? afterSuccess = null)
    {
        IsBusy = true;
        ErrorText = null;
        StatusText = progressText;
        try
        {
            var result = await action().ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = result.ErrorText ?? "操作失败。";
                return;
            }

            StatusText = successText;
            if (afterSuccess is not null)
            {
                await afterSuccess().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RecreateClientIfNeeded()
    {
        var normalized = CloudClosetClient.NormalizeBaseAddress(SiteAddress);
        if (string.Equals(normalized, _client.BaseAddress, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previous = _client;
        previous.OAuthTokenRefreshed -= OnOAuthTokenRefreshed;
        _client = new CloudClosetClient(baseAddress: normalized, log: _log);
        _client.OAuthTokenRefreshed += OnOAuthTokenRefreshed;
        previous.Dispose();
        OnPropertyChanged(nameof(SiteLabel));
        OnPropertyChanged(nameof(IsOAuthSession));
        OnPropertyChanged(nameof(CanUpload));
    }

    /// <summary>
    /// 客户端悄悄刷新了令牌，这里把新的一整组写回本机凭据。
    /// 刷新后旧 refresh token 立即失效，不写回去下次启动就得重新授权。
    /// </summary>
    private void OnOAuthTokenRefreshed(object? sender, CloudOAuthToken token)
    {
        _ = PersistOAuthTokenAsync(token);
    }

    private async Task PersistOAuthTokenAsync(CloudOAuthToken token)
    {
        if (string.IsNullOrWhiteSpace(Username) || _disposed)
        {
            return;
        }

        try
        {
            await _credentials
                .SaveOAuthAsync(_client.BaseAddress, Username, token.AccessToken, token.RefreshToken, token.ExpiresAt)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Warn("保存刷新后的 OAuth 令牌失败", ex);
        }
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoadingPlaceholder));
    }

    /// <summary>皮肤站把名字写进文件名，路径分隔符与控制字符先换掉，再截到 64 字。</summary>
    private static string SanitizeTextureName(string? name)
    {
        var cleaned = string.IsNullOrWhiteSpace(name) ? "NebulaLauncher 皮肤" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalid, '_');
        }

        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "NebulaLauncher 皮肤";
        }

        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }

    private static string BuildLocalFileName(CloudClosetItemViewModel item)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? (item.IsCape ? "cloud-cape" : "cloud-skin") : item.Name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + ".png";
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
            _log?.Debug($"云端衣柜缩略图解码失败：{ex.GetType().Name}");
            return null;
        }
    }

    private void Replace(ref CancellationTokenSource? field, CancellationTokenSource next)
    {
        var previous = field;
        field = next;
        if (previous is not null && !ReferenceEquals(previous, next))
        {
            previous.Cancel();
            previous.Dispose();
        }
    }
}

/// <summary>云端衣柜里的一行。显示名优先用衣柜里的命名，没有才退回纹理名。</summary>
public sealed class CloudClosetItemViewModel : ObservableObject
{
    private Bitmap? _thumbnail;

    public CloudClosetItemViewModel(CloudClosetItem item, bool isOwned = false)
    {
        Item = item;
        IsOwned = isOwned;
    }

    public CloudClosetItem Item { get; }

    public int Tid => Item.Tid;

    public string Hash => Item.Hash;

    public string Name => string.IsNullOrWhiteSpace(Item.ItemName) ? Item.Name : Item.ItemName!;

    public string TextureName => Item.Name;

    public bool IsCape => string.Equals(Item.Type, "cape", StringComparison.OrdinalIgnoreCase);

    public bool IsSlim => string.Equals(Item.Type, "alex", StringComparison.OrdinalIgnoreCase);

    public string ModelLabel => IsCape ? "披风" : (IsSlim ? "细手臂（Alex）" : "经典（Steve）");

    public string LikesLabel => $"{Item.Likes} 次收藏";

    public string VisibilityLabel => Item.IsPublic ? "公开" : "私有";

    public string DetailLabel => $"{ModelLabel} · {LikesLabel} · {VisibilityLabel}";

    public string TidLabel => $"tid {Item.Tid}";

    /// <summary>true 表示这件材质由本启动器上传并记录过，因此允许删除。</summary>
    public bool IsOwned { get; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;
}
