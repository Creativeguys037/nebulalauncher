using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 账户页：当前账户大卡片、账户列表、离线账户添加、微软设备码登录。
///
/// 头像不依赖任何外部图片：用用户名首字母 + 令牌渐变底色画圆形，离线也能完整显示。
/// </summary>
public sealed partial class AccountsViewModel : PageViewModelBase
{
    /// <summary>Minecraft 用户名规则：3–16 位，只允许字母数字下划线。</summary>
    private static readonly Regex UserNamePattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private readonly IAccountService? _accounts;
    private readonly IPlatformService? _platform;

    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _authlibCts;
    private string? _authlibProfileSelectionId;
    private bool _loaded;
    private bool _serviceLoaded;
    private bool _reloading;

    public AccountsViewModel(IServiceProvider services)
    {
        _accounts = services.GetService(typeof(IAccountService)) as IAccountService;
        _platform = services.GetService(typeof(IPlatformService)) as IPlatformService;

        IsDemoMode = _accounts is null;

        if (_accounts is not null)
        {
            // 服务可能从后台线程触发（保存账户后），统一 Post 回 UI 线程再动集合。
            _accounts.AccountsChanged += OnAccountsChanged;
        }
    }

    public override string Title => "账户";

    public override string Key => NavigationKeys.Accounts;

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

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    [ObservableProperty]
    private bool _isDemoMode;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _infoText;

    // ---------- 当前账户大卡片 ----------

    [ObservableProperty]
    private bool _hasActiveAccount;

    [ObservableProperty]
    private string _activeName = "未登录";

    [ObservableProperty]
    private string _activeInitial = "?";

    [ObservableProperty]
    private string _activeTypeLabel = "游客";

    [ObservableProperty]
    private string _activeDetail = "添加一个账户后即可启动游戏。";

    [ObservableProperty]
    private string _activeExpiryText = string.Empty;

    [ObservableProperty]
    private bool _activeExpired;

    [ObservableProperty]
    private bool _activeUseAurora;

    /// <summary>在线账户允许手动刷新；离线账户无需令牌。</summary>
    [ObservableProperty]
    private bool _canRefreshToken;

    // ---------- 离线账户 ----------

    [ObservableProperty]
    private string _offlineName = string.Empty;

    [ObservableProperty]
    private string? _offlineNameError;

    [ObservableProperty]
    private bool _canAddOffline;

    // ---------- 微软登录 ----------

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    private string? _deviceCode;

    [ObservableProperty]
    private string? _verificationUri;

    [ObservableProperty]
    private string _authStage = string.Empty;

    // ---------- 第三方验证（authlib-injector） ----------

    /// <summary>常用服务商快捷项：点一下把 API 根填进表单，省得手抄整条地址。</summary>
    public IReadOnlyList<AuthlibPreset> AuthlibPresets { get; } =
    [
        new("LittleSkin", "https://littleskin.cn/api/yggdrasil"),
        new("Blessing Skin（自建）", "https://skin.example.com/api/yggdrasil"),
    ];

    [ObservableProperty]
    private bool _isAuthlibFormOpen;

    [ObservableProperty]
    private string _authlibApiRoot = string.Empty;

    [ObservableProperty]
    private string _authlibUsername = string.Empty;

    [ObservableProperty]
    private string _authlibPassword = string.Empty;

    [ObservableProperty]
    private string? _authlibError;

    [ObservableProperty]
    private string? _authlibApiRootError;

    [ObservableProperty]
    private bool _isAuthlibBusy;

    public ObservableCollection<AuthlibProfileOption> AuthlibProfiles { get; } = [];

    [ObservableProperty]
    private bool _isChoosingAuthlibProfile;

    [ObservableProperty]
    private AuthlibProfileOption? _selectedAuthlibProfile;

    public bool CanConfirmAuthlibProfile => !IsAuthlibBusy && SelectedAuthlibProfile is not null;

    partial void OnSelectedAuthlibProfileChanged(AuthlibProfileOption? value)
        => ConfirmAuthlibProfileCommand.NotifyCanExecuteChanged();

    public bool HasAuthlibError => !string.IsNullOrWhiteSpace(AuthlibError);

    public bool HasAuthlibApiRootError => !string.IsNullOrWhiteSpace(AuthlibApiRootError);

    /// <summary>提交按钮的可用性：地址合法 + 用户名密码非空 + 当前没有请求在跑。</summary>
    public bool CanSubmitAuthlib =>
        !IsAuthlibBusy
        && !string.IsNullOrWhiteSpace(AuthlibUsername)
        && AuthlibPassword.Length > 0
        && IsValidAuthlibApiRoot(AuthlibApiRoot);

    partial void OnAuthlibApiRootChanged(string value)
    {
        // 空输入不报错：刚打开表单就飘红字会让用户以为已经填错了。
        var trimmed = value.Trim();
        AuthlibApiRootError = trimmed.Length == 0 ? null : ValidateAuthlibApiRoot(trimmed);
        SubmitAuthlibCommand.NotifyCanExecuteChanged();
    }

    partial void OnAuthlibUsernameChanged(string value) => SubmitAuthlibCommand.NotifyCanExecuteChanged();

    partial void OnAuthlibPasswordChanged(string value) => SubmitAuthlibCommand.NotifyCanExecuteChanged();

    partial void OnIsAuthlibBusyChanged(bool value)
    {
        SubmitAuthlibCommand.NotifyCanExecuteChanged();
        ConfirmAuthlibProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnAuthlibErrorChanged(string? value) => OnPropertyChanged(nameof(HasAuthlibError));

    partial void OnAuthlibApiRootErrorChanged(string? value) => OnPropertyChanged(nameof(HasAuthlibApiRootError));

    [ObservableProperty]
    private AccountRowViewModel? _pendingRemoval;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasInfo => !string.IsNullOrWhiteSpace(InfoText);

    public bool HasDeviceCode => !string.IsNullOrWhiteSpace(DeviceCode);

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasPendingRemoval => PendingRemoval is not null;

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnInfoTextChanged(string? value) => OnPropertyChanged(nameof(HasInfo));

    partial void OnDeviceCodeChanged(string? value) => OnPropertyChanged(nameof(HasDeviceCode));

    partial void OnPendingRemovalChanged(AccountRowViewModel? value) => OnPropertyChanged(nameof(HasPendingRemoval));

    partial void OnOfflineNameChanged(string value)
    {
        OfflineNameError = value.Length == 0 ? null : ValidateOfflineName(value);
        CanAddOffline = OfflineNameError is null && value.Length > 0;
        AddOfflineCommand.NotifyCanExecuteChanged();
    }

    public override async Task OnNavigatedToAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ReloadAsync();
        if (Services.ScreenshotMode.IsActive && Services.ScreenshotMode.SectionKey == "profiles")
        {
            IsAuthlibFormOpen = true;
            IsChoosingAuthlibProfile = true;
            AuthlibProfiles.Add(new AuthlibProfileOption("069a79f444e94726a5befca90e38aaf5", "Steve"));
            AuthlibProfiles.Add(new AuthlibProfileOption("11111111111111111111111111111111", "Alex"));
        }
    }

    public override Task OnNavigatedFromAsync()
    {
        // 设备码登录是一个长时间挂起的 HTTP 轮询，离开页面时必须取消，否则会在后台继续等。
        CancelSignIn();
        CancelAuthlibForm();
        return Task.CompletedTask;
    }

    /// <summary>设备码轮询的取消源在页面被释放时收尾；置 null 后 <see cref="CancelSignIn"/> 的 <c>?.</c> 是空短路。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _authCts?.Cancel();
            _authCts?.Dispose();
            _authCts = null;
            CancelAuthlibForm();
            if (_accounts is not null)
            {
                _accounts.AccountsChanged -= OnAccountsChanged;
            }
        }

        base.Dispose(disposing);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanAddOffline))]
    private async Task AddOfflineAsync()
    {
        ErrorText = null;
        InfoText = null;

        var name = OfflineName.Trim();
        var validation = ValidateOfflineName(name);
        if (validation is not null)
        {
            OfflineNameError = validation;
            return;
        }

        try
        {
            if (_accounts is null)
            {
                InfoText = "示例模式：Core 服务未就绪，未真正添加账户。";
                await ReloadAsync();
                return;
            }

            var result = await _accounts.AddOfflineAccountAsync(name);
            if (!result.Success)
            {
                OfflineNameError = result.Message ?? "添加失败。";
                return;
            }

            OfflineName = string.Empty;
            OfflineNameError = null;
            InfoText = $"已添加离线账户「{name}」。";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"添加离线账户失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SwitchAccountAsync(AccountRowViewModel? row)
    {
        if (row is null || row.IsActive)
        {
            return;
        }

        ErrorText = null;
        InfoText = null;

        try
        {
            if (_accounts is null)
            {
                InfoText = "示例模式：Core 服务未就绪，未切换账户。";
                return;
            }

            _accounts.SetActive(row.Account.Id);
            await ReloadAsync();
            InfoText = $"已切换到「{row.Username}」。";
        }
        catch (Exception ex)
        {
            ErrorText = $"切换账户失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RequestRemove(AccountRowViewModel? row) => PendingRemoval = row;

    [RelayCommand]
    private void CancelRemove() => PendingRemoval = null;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        var row = PendingRemoval;
        PendingRemoval = null;

        if (row is null)
        {
            return;
        }

        try
        {
            if (_accounts is not null)
            {
                await _accounts.RemoveAsync(row.Account.Id);
            }

            await ReloadAsync();
            InfoText = $"已移除账户「{row.Username}」。";
        }
        catch (Exception ex)
        {
            ErrorText = $"移除账户失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshTokenAsync()
    {
        ErrorText = null;
        InfoText = null;

        if (_accounts?.ActiveAccount is not { } active || _accounts is null)
        {
            ErrorText = "没有可刷新的账户。";
            return;
        }

        if (active.Type == AccountType.Offline)
        {
            InfoText = "离线账户不需要令牌，直接启动即可。";
            return;
        }

        try
        {
            IsLoading = true;
            var result = await _accounts.RefreshAsync(active.Id);
            if (!result.Success)
            {
                ErrorText = result.Message ?? "刷新令牌失败。";
                return;
            }

            InfoText = "令牌已刷新。";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"刷新令牌失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        ErrorText = null;
        InfoText = null;
        DeviceCode = null;
        VerificationUri = null;
        AuthStage = "正在联系微软登录服务…";

        if (_accounts is null)
        {
            ErrorText = "账户服务尚未就绪，无法发起微软登录。";
            return;
        }

        CancelSignIn();
        _authCts = new CancellationTokenSource();
        var token = _authCts.Token;

        IsSigningIn = true;

        try
        {
            var result = await _accounts.AddMicrosoftAccountAsync(new AuthProgressReporter(this), token);

            if (result.Success)
            {
                InfoText = $"已登录微软账户「{result.Account?.Username}」。";
                await ReloadAsync();
            }
            else
            {
                ErrorText = result.Message ?? "微软登录失败。";
            }
        }
        catch (OperationCanceledException)
        {
            InfoText = "已取消微软登录。";
        }
        catch (Exception ex)
        {
            ErrorText = $"微软登录失败：{ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            DeviceCode = null;
            VerificationUri = null;
            AuthStage = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        _authCts?.Cancel();
        _authCts?.Dispose();
        _authCts = null;
    }

    [RelayCommand]
    private void OpenAuthlibForm()
    {
        AuthlibError = null;
        AuthlibApiRootError = null;
        IsAuthlibFormOpen = true;
    }

    [RelayCommand]
    private void CancelAuthlibForm()
    {
        _authlibCts?.Cancel();
        ClearAuthlibProfileSelection();
        AuthlibPassword = string.Empty;
        AuthlibError = null;
        IsAuthlibFormOpen = false;
    }

    [RelayCommand]
    private void UseAuthlibPreset(AuthlibPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        AuthlibApiRoot = preset.ApiRoot;
    }

    [RelayCommand(CanExecute = nameof(CanSubmitAuthlib))]
    private async Task SubmitAuthlibAsync()
    {
        AuthlibError = null;

        var apiRoot = AuthlibApiRoot.Trim();
        var username = AuthlibUsername.Trim();

        // 密码不做 Trim：首尾空格属于密码本身，改掉会直接导致登录失败。
        var password = AuthlibPassword;

        if (ValidateAuthlibApiRoot(apiRoot) is { } apiRootProblem)
        {
            AuthlibApiRootError = apiRootProblem;
            return;
        }

        if (username.Length == 0 || password.Length == 0)
        {
            AuthlibError = "用户名和密码都不能为空。";
            return;
        }

        if (_accounts is null)
        {
            AuthlibError = "账户服务尚未就绪，无法添加第三方验证账户。";
            return;
        }

        ErrorText = null;
        InfoText = null;
        IsAuthlibBusy = true;
        using var request = new CancellationTokenSource();
        _authlibCts = request;

        try
        {
            var result = await _accounts.AddAuthlibAccountAsync(apiRoot, username, password, request.Token);
            if (request.IsCancellationRequested)
            {
                if (result.ProfileSelectionId is { } selectionId)
                {
                    _accounts.CancelAuthlibProfileSelection(selectionId);
                }
                return;
            }
            await ApplyAuthlibResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            AuthlibError = "登录已取消。";
        }
        catch (Exception ex)
        {
            AuthlibError = $"第三方验证登录失败：{ex.Message}";
        }
        finally
        {
            _authlibCts = null;
            IsAuthlibBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmAuthlibProfile))]
    private async Task ConfirmAuthlibProfileAsync()
    {
        if (_accounts is null || _authlibProfileSelectionId is null || SelectedAuthlibProfile is null)
        {
            return;
        }

        AuthlibError = null;
        IsAuthlibBusy = true;
        using var request = new CancellationTokenSource();
        _authlibCts = request;
        try
        {
            var result = await _accounts.SelectAuthlibProfileAsync(
                _authlibProfileSelectionId, SelectedAuthlibProfile.Id, request.Token);
            if (!request.IsCancellationRequested)
            {
                await ApplyAuthlibResultAsync(result);
            }
        }
        catch (OperationCanceledException)
        {
            AuthlibError = "登录已取消。";
        }
        catch (Exception)
        {
            AuthlibError = "角色选择失败，请检查网络后重试。";
        }
        finally
        {
            _authlibCts = null;
            IsAuthlibBusy = false;
        }
    }

    private async Task ApplyAuthlibResultAsync(AccountResult result)
    {
        if (result.RequiresProfileSelection)
        {
            ClearAuthlibProfileSelection();
            _authlibProfileSelectionId = result.ProfileSelectionId;
            foreach (var profile in result.AvailableProfiles)
            {
                AuthlibProfiles.Add(profile);
            }
            AuthlibPassword = string.Empty;
            IsChoosingAuthlibProfile = true;
            return;
        }
        if (!result.Success)
        {
            AuthlibError = result.Message ?? "第三方验证登录失败。";
            return;
        }

        ClearAuthlibProfileSelection();
        AuthlibPassword = string.Empty;
        IsAuthlibFormOpen = false;
        InfoText = $"已添加第三方验证账户「{result.Account?.Username}」。";
        SelectAccount(result.Account?.Id);
        await ReloadAsync();
    }

    private void ClearAuthlibProfileSelection()
    {
        if (_authlibProfileSelectionId is { } selectionId)
        {
            _accounts?.CancelAuthlibProfileSelection(selectionId);
        }
        _authlibProfileSelectionId = null;
        SelectedAuthlibProfile = null;
        AuthlibProfiles.Clear();
        IsChoosingAuthlibProfile = false;
    }

    [RelayCommand]
    private async Task CopyDeviceCodeAsync()
    {
        if (_platform is null || string.IsNullOrWhiteSpace(DeviceCode))
        {
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(DeviceCode))
            {
                ErrorText = "复制失败：剪贴板不可用，详见启动器日志。";
                return;
            }

            InfoText = "设备码已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            ErrorText = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenVerificationUriAsync()
    {
        if (_platform is null || string.IsNullOrWhiteSpace(VerificationUri))
        {
            return;
        }

        try
        {
            await _platform.OpenUrlAsync(VerificationUri);
        }
        catch (Exception ex)
        {
            ErrorText = $"打开浏览器失败：{ex.Message}";
        }
    }

    /// <summary>由 <see cref="IProgress{T}"/> 转发回 UI 线程，用于刷新设备码面板。</summary>
    internal void ApplyAuthProgress(MicrosoftAuthProgress progress)
    {
        AuthStage = string.IsNullOrWhiteSpace(progress.Message) ? progress.Stage : progress.Message;

        if (!string.IsNullOrWhiteSpace(progress.UserCode))
        {
            DeviceCode = progress.UserCode;
        }

        if (!string.IsNullOrWhiteSpace(progress.VerificationUri))
        {
            VerificationUri = progress.VerificationUri;
        }
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => _ = ReloadAsync());

    private async Task ReloadAsync()
    {
        if (_reloading)
        {
            return;
        }

        _reloading = true;
        ErrorText = null;

        try
        {
            // 组合根在建窗口之前已经 LoadAsync 过一次（Services/StartupState.cs），所以正常启动时
            // 这里通常什么都不做；保留调用是为了「服务没被预热过」的构造路径（单测、独立截图工具）
            // 与将来可能的服务替换。LoadAsync 幂等，预热过就是空操作，不会再读盘也不会再发事件。
            if (_accounts is not null && !_serviceLoaded)
            {
                await _accounts.LoadAsync();
                _serviceLoaded = true;
            }

            Accounts.Clear();

            IReadOnlyList<Account> source;
            Account? active;

            if (_accounts is null)
            {
                source = BuildSampleAccounts();
                active = source.FirstOrDefault(account => account.Id == "microsoft-sample");
            }
            else
            {
                source = _accounts.Accounts;
                active = _accounts.ActiveAccount;
            }

            foreach (var account in source)
            {
                var row = new AccountRowViewModel(account, active?.Id);
                Accounts.Add(row);
            }

            ApplyActiveCard(active);
            ApplyExpiryState(active);

            OnPropertyChanged(nameof(HasAccounts));
            UpdateSubtitle(active);
        }
        catch (Exception ex)
        {
            ErrorText = $"读取账户失败：{ex.Message}";
        }
        finally
        {
            _reloading = false;
        }
    }

    private void ApplyActiveCard(Account? active)
    {
        HasActiveAccount = active is not null;
        CanRefreshToken = active?.Type is AccountType.Microsoft or AccountType.Authlib;

        if (active is null)
        {
            ActiveName = "未登录";
            ActiveInitial = "?";
            ActiveTypeLabel = "游客";
            ActiveDetail = "添加一个账户后即可启动游戏。";
            ActiveExpiryText = string.Empty;
            ActiveExpired = false;
            ActiveUseAurora = false;
            return;
        }

        ActiveName = active.Username;
        ActiveInitial = FirstLetter(active.Username);
        ActiveTypeLabel = active.Type switch
        {
            AccountType.Microsoft => "微软账户",
            AccountType.Authlib => "第三方验证",
            _ => "离线账户",
        };

        var uuidPart = string.IsNullOrWhiteSpace(active.Uuid) ? "无 UUID" : $"UUID {active.Uuid}";
        var host = active.Type == AccountType.Authlib ? AuthlibDisplay.HostOf(active.AuthServerUrl) : null;
        ActiveDetail = host is null ? uuidPart : $"{host} · {uuidPart}";
        ActiveUseAurora = active.Type == AccountType.Microsoft;
    }

    private void ApplyExpiryState(Account? active)
    {
        if (active is null)
        {
            return;
        }

        if (active.Type == AccountType.Offline)
        {
            ActiveExpired = false;
            ActiveExpiryText = "离线账户无需令牌，随时可以启动。";
            return;
        }

        if (active.ExpiresAt is not { } expiresAt)
        {
            ActiveExpired = false;
            ActiveExpiryText = "登录态未记录过期时间。";
            return;
        }

        var local = expiresAt.ToLocalTime();
        if (active.IsExpired)
        {
            ActiveExpired = true;
            ActiveExpiryText = $"令牌已于 {local:yyyy-MM-dd HH:mm} 过期，启动前会自动尝试刷新。";
            return;
        }

        ActiveExpired = false;
        ActiveExpiryText = $"令牌有效至 {local:yyyy-MM-dd HH:mm}。";
    }

    private void UpdateSubtitle(Account? active)
    {
        if (Accounts.Count == 0)
        {
            SetSubtitle(IsDemoMode ? "示例数据" : "尚未添加账户");
            return;
        }

        SetSubtitle(active is null ? $"{Accounts.Count} 个账户" : $"当前 {active.Username}");
    }

    private static string? ValidateOfflineName(string raw)
    {
        var name = raw.Trim();

        if (name.Length is < 3 or > 16)
        {
            return "用户名需要 3–16 个字符。";
        }

        return UserNamePattern.IsMatch(name) ? null : "只能包含字母、数字和下划线。";
    }

    /// <summary>
    /// 校验第三方验证的 API 根。只要求 http/https 绝对地址：
    /// 皮肤站可以挂在任意路径下，多校验一层路径反而会把自建服务挡在外面。
    /// </summary>
    private static string? ValidateAuthlibApiRoot(string raw)
    {
        if (raw.Length == 0)
        {
            return "请填写 API 根地址。";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return "需要完整地址，例如 https://littleskin.cn/api/yggdrasil。";
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
            ? null
            : "只支持 http 或 https 地址。";
    }

    private static bool IsValidAuthlibApiRoot(string raw)
        => raw.Trim().Length > 0 && ValidateAuthlibApiRoot(raw.Trim()) is null;

    /// <summary>登录成功后切到新账户；服务里还找不到这个 id 时保持原选择，不算失败。</summary>
    private void SelectAccount(string? accountId)
    {
        if (_accounts is null || string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        if (!_accounts.Accounts.Any(account => string.Equals(account.Id, accountId, StringComparison.Ordinal)))
        {
            return;
        }

        try
        {
            _accounts.SetActive(accountId);
        }
        catch (Exception ex)
        {
            AuthlibError = $"账户已添加，但切换失败：{ex.Message}";
        }
    }

    private static string FirstLetter(string text)
        => string.IsNullOrWhiteSpace(text) ? "?" : text[..1].ToUpperInvariant();

    private static IReadOnlyList<Account> BuildSampleAccounts() =>
    [
        new Account
        {
            Id = "microsoft-sample",
            Type = AccountType.Microsoft,
            Username = "Notch",
            Uuid = "069a79f444e94726a5befca90e38aaf5",
            ExpiresAt = DateTimeOffset.Now.AddHours(6),
            LastLoginAt = DateTimeOffset.Now.AddMinutes(-18),
        },
        new Account
        {
            Id = "offline-sample",
            Type = AccountType.Offline,
            Username = "Silent_Sky_",
            Uuid = "34f2c1a3d5e64b8fa0c2d7e1b9a83c45",
            LastLoginAt = DateTimeOffset.Now.AddDays(-2),
        },
        new Account
        {
            Id = "authlib-sample",
            Type = AccountType.Authlib,
            Username = "NebulaAuth",
            Uuid = "7ac1f0d2b4e34c6e9a1d8f2b6c5e0a97",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
            LastLoginAt = DateTimeOffset.Now.AddHours(-5),
        },
    ];

    /// <summary>
    /// 把 <see cref="IAccountService"/> 的进度回调切回 UI 线程。
    /// 不用 <see cref="Progress{T}"/>：它的回调依赖创建时的 SynchronizationContext，页面重建后容易丢。
    /// </summary>
    private sealed class AuthProgressReporter(AccountsViewModel owner) : IProgress<MicrosoftAuthProgress>
    {
        public void Report(MicrosoftAuthProgress value)
            => Dispatcher.UIThread.Post(() => owner.ApplyAuthProgress(value));
    }
}

/// <summary>常用第三方验证服务商。ApiRoot 直接填进表单，用户仍可改成自己的地址。</summary>
public sealed record AuthlibPreset(string Name, string ApiRoot);

/// <summary>
/// 第三方验证账户的展示辅助。只暴露主机名：完整 API 路径和令牌都不该出现在界面上。
/// </summary>
internal static class AuthlibDisplay
{
    public static string? HostOf(string? apiRoot)
        => Uri.TryCreate(apiRoot, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
}

/// <summary>账户列表中的一行。</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(Account account, string? activeId)
    {
        Account = account;
        Username = account.Username;
        Initial = string.IsNullOrWhiteSpace(account.Username) ? "?" : account.Username[..1].ToUpperInvariant();
        TypeLabel = account.Type switch
        {
            AccountType.Microsoft => "微软",
            AccountType.Authlib => "第三方验证",
            _ => "离线",
        };
        IsAuthlib = account.Type == AccountType.Authlib;
        AuthHost = IsAuthlib
            ? AuthlibDisplay.HostOf(account.AuthServerUrl) ?? "未记录 API 根"
            : string.Empty;
        IsActive = string.Equals(account.Id, activeId, StringComparison.Ordinal);
        IsExpired = account.IsExpired;
        UseAurora = account.Type == AccountType.Microsoft;
        LastLoginText = account.LastLoginAt is { } login
            ? $"上次登录 {login.LocalDateTime:MM-dd HH:mm}"
            : "尚未登录过";
    }

    public Account Account { get; }

    public string Username { get; }

    public string Initial { get; }

    public string TypeLabel { get; }

    public string LastLoginText { get; }

    /// <summary>第三方验证账户才显示 API 根主机名。</summary>
    public bool IsAuthlib { get; }

    public string AuthHost { get; }

    public bool IsActive { get; }

    public bool IsExpired { get; }

    /// <summary>微软账户用蓝紫渐变，离线账户用蓝青渐变，两张头像一眼能区分。</summary>
    public bool UseAurora { get; }
}
