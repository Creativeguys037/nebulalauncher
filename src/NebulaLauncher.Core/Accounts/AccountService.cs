using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Accounts;

/// <summary>
/// 账户仓库：离线账户、微软账户与第三方验证账户的增删改查、令牌续期、落盘保护。
/// 落盘位置由 <see cref="ILauncherPaths.AccountsFile"/> 决定，令牌经 <see cref="TokenProtector"/> 处理。
/// </summary>
/// <remarks>同 <c>SettingsService</c>：<c>_saveGate</c> 随进程生命周期回收，不做 IDisposable。</remarks>
#pragma warning disable CA1001
public sealed class AccountService : IAccountService
#pragma warning restore CA1001
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter<AccountType>() },
        TypeInfoResolver = new AccountJsonContext(),
    };

    private readonly ILauncherPaths _paths;
    private readonly IAppLog _log;
    private readonly IPlatformService _platform;
    private readonly MicrosoftDeviceCodeAuth _microsoft;
    private readonly AuthlibAuthService _authlib;

    /// <summary>保护 <see cref="_accounts"/> 与 <see cref="_activeAccountId"/> 的读写。</summary>
    private readonly object _sync = new();

    /// <summary>串行化落盘，避免两处同时写同一个临时文件。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>串行化读盘：启动预热与页面兜底加载可能同时到达。</summary>
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>是否已经读过盘。<see cref="LoadAsync"/> 的幂等标记，<see cref="ReloadAsync"/> 无视它。</summary>
    private bool _loaded;

    private readonly List<Account> _accounts = new();
    private readonly Dictionary<string, PendingAuthlibLogin> _pendingAuthlibLogins = new(StringComparer.Ordinal);
    private string? _activeAccountId;

    /// <summary>最近一次成功落盘的内存快照（账户 + 当前账户 id），用于失败/取消时回滚。</summary>
    private List<Account> _persistedSnapshot = new();
    private string? _persistedActiveId;

    private sealed class PendingAuthlibLogin(string apiRoot, AuthlibAuthResult authentication)
    {
        public string ApiRoot { get; } = apiRoot;
        public AuthlibAuthResult Authentication { get; } = authentication;
        public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public bool Selecting { get; set; }
    }

    /// <summary>创建账户服务。后两个参数可选，只为测试替换实现；产品代码走无参默认。</summary>
    public AccountService(
        ILauncherPaths paths,
        IAppLog log,
        IPlatformService platform,
        HttpClient? httpClient = null,
        AuthlibAuthService? authlibAuth = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));

        var http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _microsoft = new MicrosoftDeviceCodeAuth(http, _log, _platform);
        _authlib = authlibAuth ?? new AuthlibAuthService(http, _log);
    }

    /// <inheritdoc />
    public IReadOnlyList<Account> Accounts
    {
        get
        {
            lock (_sync)
            {
                return _accounts.Select(a => a.Clone()).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Account? ActiveAccount
    {
        get
        {
            lock (_sync)
            {
                var active = _accounts.FirstOrDefault(a => a.Id == _activeAccountId);
                return active?.Clone();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? AccountsChanged;

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken = default) => LoadCoreAsync(force: false, cancellationToken);

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken cancellationToken = default) => LoadCoreAsync(force: true, cancellationToken);

    /// <summary>
    /// 读盘实现。<paramref name="force"/> 为 false 时只读一次：启动预热之后再从页面进来不重读。
    ///
    /// 重读不是无害的：账户页再进一次就会用磁盘上的旧状态盖掉内存里刚改完的当前账户。
    /// 取消与读失败都不置「已加载」——没读到就不算读过，下次还得再试（见 <see cref="LoadFromDiskAsync"/> 的返回值）。
    /// </summary>
    private async Task LoadCoreAsync(bool force, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _loaded)
            {
                return;
            }

            if (await LoadFromDiskAsync(cancellationToken).ConfigureAwait(false))
            {
                _loaded = true;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// 从磁盘读账户。返回值表示「这次读到的结果可信」：文件不存在（空账户）与解析成功都算可信，
    /// 读盘异常 / JSON 损坏则不可信——调用方据此决定要不要保留「已加载」标记。
    ///
    /// 为什么不让失败也标记已加载：生产代码里没有 <see cref="ReloadAsync"/> 的调用方，
    /// 一次瞬时的 IO 失败会让本次会话永久停在「无账户」上，接下来添加账户还会拿空表覆盖磁盘。
    /// 不标记的话，进一次账户页就会重试，文件被修好或占用解除后自愈。
    /// </summary>
    private async Task<bool> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        var file = _paths.AccountsFile;

        try
        {
            if (!File.Exists(file))
            {
                _log.Info($"账户文件不存在：{file}");
                ReplaceAll([], null);
                return true;
            }

            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(json, JsonOptions.Of<AccountsDocument>())
                           ?? throw new JsonException("反序列化结果为 null");

            var loaded = new List<Account>(document.Accounts.Count);
            foreach (var record in document.Accounts)
            {
                if (string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(record.Username))
                {
                    _log.Warn("跳过缺少关键字段的账户记录");
                    continue;
                }

                loaded.Add(new Account
                {
                    Id = record.Id,
                    Type = record.Type,
                    Username = record.Username,
                    Uuid = record.Uuid ?? string.Empty,
                    AccessToken = TokenProtector.Unprotect(record.AccessToken, _platform),
                    RefreshToken = TokenProtector.Unprotect(record.RefreshToken, _platform),
                    ExpiresAt = record.ExpiresAt,
                    SkinModel = string.IsNullOrEmpty(record.SkinModel) ? "classic" : record.SkinModel,
                    // 旧版本 accounts.json 没有这个字段，读出来就是 null；
                    // 顺手规范化一次，保证「同一服务器」在去重时能被字符串比较命中。
                    AuthServerUrl = AuthlibAuthService.NormalizeApiRoot(record.AuthServerUrl),
                    LastLoginAt = record.LastLoginAt,
                });
            }

            var activeId = loaded.Any(a => a.Id == document.ActiveAccountId)
                ? document.ActiveAccountId
                : loaded.FirstOrDefault()?.Id;

            ReplaceAll(loaded, activeId);
            _log.Info($"已加载 {loaded.Count} 个账户：{file}");
            AccountsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"账户文件损坏，已忽略：{file}", ex);
            BackupBrokenFile(file);
            ReplaceAll([], null);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default)
    {
        var name = username?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return AccountResult.Fail("用户名不能为空。");
        }

        if (name.Length > 16)
        {
            return AccountResult.Fail("用户名不能超过 16 个字符。");
        }

        var account = new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Offline,
            Username = name,
            Uuid = OfflineAccountUuid.Create(name),
            SkinModel = "classic",
            LastLoginAt = DateTimeOffset.UtcNow,
        };

        lock (_sync)
        {
            _accounts.Add(account);
            _activeAccountId = account.Id;
        }

        _log.Info($"已添加离线账户：{name} ({account.Uuid})");

        bool persisted;
        try
        {
            persisted = await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消落在「内存已改、落盘未完成」的窗口里：回滚到磁盘状态再重抛（P3-11）。
            RollbackToPersisted();
            throw;
        }

        if (!persisted)
        {
            // 没落盘就不能让用户以为加成功了：回滚内存，下次启动时列表与磁盘一致。
            RollbackToPersisted();
            return AccountResult.Fail("添加账户失败：账户文件写入磁盘失败，请检查数据目录权限或磁盘空间。");
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);

        return AccountResult.Ok(account.Clone());
    }

    /// <inheritdoc />
    public async Task<AccountResult> AddMicrosoftAccountAsync(
        IProgress<MicrosoftAuthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _microsoft.SignInAsync(progress, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _log.Warn($"微软账户登录失败：{result.Message}");
                return AccountResult.Fail(result.Message ?? "微软账户登录失败。");
            }

            var account = new Account
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = AccountType.Microsoft,
                Username = result.Username ?? string.Empty,
                Uuid = result.Uuid ?? string.Empty,
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                SkinModel = "classic",
                LastLoginAt = DateTimeOffset.UtcNow,
            };

            // 同一个微软账户重复登录时覆盖旧记录，避免列表里出现两条同名档案。
            lock (_sync)
            {
                var existing = _accounts.FirstOrDefault(a =>
                    a.Type == AccountType.Microsoft &&
                    string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    existing.Username = account.Username;
                    existing.AccessToken = account.AccessToken;
                    existing.RefreshToken = account.RefreshToken;
                    existing.ExpiresAt = account.ExpiresAt;
                    existing.LastLoginAt = account.LastLoginAt;
                    _activeAccountId = existing.Id;
                    account = existing;
                }
                else
                {
                    _accounts.Add(account);
                    _activeAccountId = account.Id;
                }
            }

            bool persisted;
            try
            {
                persisted = await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 登录成功但落盘被取消：回滚到磁盘状态，避免「内存有、磁盘没有」。
                RollbackToPersisted();
                throw;
            }

            if (!persisted)
            {
                RollbackToPersisted();
            }

            AccountsChanged?.Invoke(this, EventArgs.Empty);

            return persisted
                ? AccountResult.Ok(account.Clone())
                : AccountResult.Fail("微软账户登录未能保存：账户文件写入磁盘失败，请检查数据目录权限或磁盘空间。");
        }
        catch (OperationCanceledException)
        {
            _log.Info("微软账户登录已取消");
            return AccountResult.Fail("登录已取消。");
        }
        catch (Exception ex)
        {
            _log.Error("微软账户登录异常", ex);
            return AccountResult.Fail($"登录失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> AddAuthlibAccountAsync(
        string apiRoot,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var root = AuthlibAuthService.NormalizeApiRoot(apiRoot);
        if (root is null)
        {
            return AccountResult.Fail(
                "请填写有效的验证服务器 API 根，例如 https://littleskin.cn/api/yggdrasil。");
        }

        try
        {
            var result = await _authlib.AuthenticateAsync(root, username, password, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _log.Warn($"第三方验证登录失败：{result.Message}");
                return AccountResult.Fail(result.Message ?? "第三方验证登录失败。");
            }

            if (string.IsNullOrEmpty(result.Uuid))
            {
                var profiles = result.AvailableProfiles ?? [];
                if (profiles.Count == 1)
                {
                    result = await _authlib.SelectProfileAsync(root, result.AccessToken!, result.ClientToken!,
                        profiles[0], cancellationToken).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        return AccountResult.Fail(result.Message ?? "角色选择失败。");
                    }
                }
                else if (profiles.Count > 1)
                {
                    var selectionId = Guid.NewGuid().ToString("N");
                    lock (_sync)
                    {
                        foreach (var expired in _pendingAuthlibLogins.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                                     .Select(item => item.Key).ToArray())
                        {
                            _pendingAuthlibLogins.Remove(expired);
                        }
                        _pendingAuthlibLogins[selectionId] = new PendingAuthlibLogin(root, result);
                    }
                    return new AccountResult(false, "请选择本次登录使用的角色。")
                    {
                        ProfileSelectionId = selectionId,
                        AvailableProfiles = profiles.Select(profile => new AuthlibProfileOption(profile.Id, profile.Name)).ToArray(),
                    };
                }
                else
                {
                    return AccountResult.Fail("该账号在验证服务器上没有可用角色。");
                }
            }

            return await SaveAuthlibAccountAsync(root, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Info("第三方验证登录已取消");
            return AccountResult.Fail("登录已取消。");
        }
        catch (Exception ex)
        {
            _log.Error("第三方验证登录异常", ex);
            return AccountResult.Fail("第三方验证登录失败，请检查网络后重试。");
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> SelectAuthlibProfileAsync(
        string selectionId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        PendingAuthlibLogin pending;
        AuthlibProfile? profile;
        lock (_sync)
        {
            if (!_pendingAuthlibLogins.TryGetValue(selectionId, out pending!) || pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _pendingAuthlibLogins.Remove(selectionId);
                return AccountResult.Fail("角色选择已过期或已取消，请重新登录。");
            }
            if (pending.Selecting)
            {
                return AccountResult.Fail("正在确认角色，请稍候。");
            }
            profile = pending.Authentication.AvailableProfiles?.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return AccountResult.Fail("所选角色不属于本次登录，请重新选择。");
            }
            pending.Selecting = true;
        }

        try
        {
            var result = await _authlib.SelectProfileAsync(pending.ApiRoot,
                pending.Authentication.AccessToken!, pending.Authentication.ClientToken!, profile, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return AccountResult.Fail(result.Message ?? "角色选择失败，请重试。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_pendingAuthlibLogins.Remove(selectionId))
                {
                    return AccountResult.Fail("角色选择已取消，请重新登录。");
                }
            }
            return await SaveAuthlibAccountAsync(pending.ApiRoot, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AccountResult.Fail("登录已取消。");
        }
        catch (Exception ex)
        {
            _log.Error("第三方角色选择异常", ex);
            return AccountResult.Fail("角色选择失败，请检查网络后重试。");
        }
        finally
        {
            lock (_sync)
            {
                pending.Selecting = false;
            }
        }
    }

    /// <inheritdoc />
    public void CancelAuthlibProfileSelection(string selectionId)
    {
        lock (_sync)
        {
            _pendingAuthlibLogins.Remove(selectionId);
        }
    }

    private async Task<AccountResult> SaveAuthlibAccountAsync(string root, AuthlibAuthResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Authlib,
            Username = result.Username ?? string.Empty,
            Uuid = result.Uuid ?? string.Empty,
            AccessToken = result.AccessToken,
            RefreshToken = result.ClientToken,
            ExpiresAt = null,
            SkinModel = "classic",
            AuthServerUrl = root,
            LastLoginAt = DateTimeOffset.UtcNow,
        };

        lock (_sync)
        {
            var existing = _accounts.FirstOrDefault(a =>
                a.Type == AccountType.Authlib &&
                string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.AuthServerUrl, root, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Username = account.Username;
                existing.AccessToken = account.AccessToken;
                existing.RefreshToken = account.RefreshToken;
                existing.AuthServerUrl = root;
                existing.LastLoginAt = account.LastLoginAt;
                _activeAccountId = existing.Id;
                account = existing;
            }
            else
            {
                _accounts.Add(account);
                _activeAccountId = account.Id;
            }
        }

        _log.Info($"第三方验证账户登录成功：{account.Username} @ {root}");

        bool saved;
        try
        {
            saved = await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RollbackToPersisted();
            throw;
        }

        if (!saved)
        {
            RollbackToPersisted();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);

        return saved
            ? AccountResult.Ok(account.Clone())
            : AccountResult.Fail("第三方验证账户登录未能保存：账户文件写入磁盘失败，请检查数据目录权限或磁盘空间。");
    }

    /// <inheritdoc />
    public async Task<AccountResult> RefreshAsync(string accountId, CancellationToken cancellationToken = default)
    {
        Account? snapshot;

        lock (_sync)
        {
            snapshot = _accounts.FirstOrDefault(a => a.Id == accountId)?.Clone();
        }

        if (snapshot is null)
        {
            return AccountResult.Fail("账户不存在。");
        }

        if (snapshot.Type == AccountType.Offline)
        {
            return AccountResult.Ok(snapshot);
        }

        if (snapshot.Type == AccountType.Authlib)
        {
            return await RefreshAuthlibAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(snapshot.RefreshToken))
        {
            return AccountResult.Fail("缺少刷新令牌，请重新登录微软账户。");
        }

        try
        {
            var result = await _microsoft.RefreshAsync(snapshot.RefreshToken, null, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _log.Warn($"账户 {accountId} 续期失败：{result.Message}");
                return AccountResult.Fail(
                    result.Message ?? "登录状态续期失败，请重新登录。",
                    retryable: result.Retryable);
            }

            Account updated;

            lock (_sync)
            {
                var target = _accounts.FirstOrDefault(a => a.Id == accountId);
                if (target is null)
                {
                    return AccountResult.Fail("账户已被移除。");
                }

                target.AccessToken = result.AccessToken;
                target.RefreshToken = result.RefreshToken;
                target.ExpiresAt = result.ExpiresAt;
                target.LastLoginAt = DateTimeOffset.UtcNow;

                if (!string.IsNullOrEmpty(result.Username))
                {
                    target.Username = result.Username;
                }

                if (!string.IsNullOrEmpty(result.Uuid))
                {
                    target.Uuid = result.Uuid;
                }

                updated = target.Clone();
            }

            bool refreshSaved;
            try
            {
                refreshSaved = await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RollbackToPersisted();
                throw;
            }

            if (!refreshSaved)
            {
                RollbackToPersisted();
            }

            AccountsChanged?.Invoke(this, EventArgs.Empty);

            return refreshSaved
                ? AccountResult.Ok(updated)
                : AccountResult.Fail("登录状态已续期，但账户文件写入磁盘失败：重启后可能需要重新登录。请检查数据目录权限或磁盘空间。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"账户 {accountId} 续期异常", ex);
            return AccountResult.Fail("登录状态续期失败，请检查网络后重试。");
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        Account? removedAccount = null;

        lock (_sync)
        {
            var index = _accounts.FindIndex(a => a.Id == accountId);
            if (index >= 0)
            {
                removedAccount = _accounts[index];
                _accounts.RemoveAt(index);

                if (_activeAccountId == accountId)
                {
                    _activeAccountId = _accounts.FirstOrDefault()?.Id;
                }
            }
        }

        if (removedAccount is null)
        {
            return;
        }

        _log.Info($"已移除账户：{accountId}");

        bool removedPersisted;
        try
        {
            removedPersisted = await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RollbackToPersisted();
            throw;
        }

        if (!removedPersisted)
        {
            // 移除没落盘时恢复磁盘状态：界面下次刷新与磁盘一致，
            // 不会出现「列表里没了、重启后又回来」这种状态。
            RollbackToPersisted();

            _log.Error($"移除账户未写入磁盘，已回滚：{accountId}");
            throw new IOException("移除账户失败：账户文件写入磁盘失败，请检查数据目录权限或磁盘空间。");
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetActive(string accountId)
    {
        lock (_sync)
        {
            if (!_accounts.Any(a => a.Id == accountId))
            {
                _log.Warn($"切换账户失败，账户不存在：{accountId}");
                return;
            }

            _activeAccountId = accountId;
        }

        // IAccountService.SetActive 是同步签名且不抛异常，落盘只能后台完成；
        // PersistAsync 只返回布尔、不抛异常，失败已写 Error 日志，这里丢弃任务是安全的。
        _ = PersistAsync(CancellationToken.None);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<Account?> EnsureValidActiveAccountAsync(CancellationToken cancellationToken = default)
    {
        var active = ActiveAccount;

        if (active is null)
        {
            return null;
        }

        if (active.Type == AccountType.Offline)
        {
            return active;
        }

        if (active.Type == AccountType.Authlib)
        {
            return await EnsureValidAuthlibAccountAsync(active, cancellationToken).ConfigureAwait(false);
        }

        // 留 2 分钟余量，避免令牌在启动途中过期。
        if (!string.IsNullOrEmpty(active.AccessToken) &&
            active.ExpiresAt is { } expiresAt &&
            expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return active;
        }

        var refreshed = await RefreshAsync(active.Id, cancellationToken).ConfigureAwait(false);

        if (refreshed.Success && refreshed.Account is not null)
        {
            return refreshed.Account;
        }

        if (refreshed.Retryable)
        {
            // 微软服务 429/5xx：令牌很可能仍然有效，用旧令牌继续启动，
            // 不能把用户拦在游戏外、更不能说成「令牌已失效」（REVIEW-R43-R51 P2-4）。
            _log.Warn($"微软服务暂时不可用，先用现有令牌启动：{refreshed.Message}");
            return active;
        }

        return null;
    }

    /// <summary>
    /// 第三方验证账户续期。
    ///
    /// 失败时把服务端原因原样带回（已在 <see cref="AuthlibAuthService"/> 里翻成中文），
    /// UI 据此提示「请重新登录」，不需要再判断状态码。
    /// </summary>
    private async Task<AccountResult> RefreshAuthlibAsync(Account snapshot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(snapshot.AuthServerUrl))
        {
            return AccountResult.Fail("该第三方验证账户缺少验证服务器地址，请重新登录。");
        }

        if (string.IsNullOrEmpty(snapshot.AccessToken) || string.IsNullOrEmpty(snapshot.RefreshToken))
        {
            return AccountResult.Fail("第三方验证账户的令牌不完整，请重新登录。");
        }

        try
        {
            var result = await _authlib.RefreshAsync(
                snapshot.AuthServerUrl,
                snapshot.AccessToken,
                snapshot.RefreshToken,
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _log.Warn($"第三方验证账户 {snapshot.Id} 续期失败：{result.Message}");
                return AccountResult.Fail(result.Message ?? "第三方验证账户续期失败，请重新登录。");
            }

            Account updated;

            lock (_sync)
            {
                var target = _accounts.FirstOrDefault(a => a.Id == snapshot.Id);
                if (target is null)
                {
                    return AccountResult.Fail("账户已被移除。");
                }

                target.AccessToken = result.AccessToken;

                if (!string.IsNullOrEmpty(result.ClientToken))
                {
                    target.RefreshToken = result.ClientToken;
                }

                if (!string.IsNullOrEmpty(result.Username))
                {
                    target.Username = result.Username;
                }

                if (!string.IsNullOrEmpty(result.Uuid))
                {
                    target.Uuid = result.Uuid;
                }

                target.LastLoginAt = DateTimeOffset.UtcNow;
                updated = target.Clone();
            }

            bool authlibRefreshSaved;
            try
            {
                authlibRefreshSaved = await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RollbackToPersisted();
                throw;
            }

            if (!authlibRefreshSaved)
            {
                RollbackToPersisted();
            }

            AccountsChanged?.Invoke(this, EventArgs.Empty);

            return authlibRefreshSaved
                ? AccountResult.Ok(updated)
                : AccountResult.Fail("第三方验证账户已续期，但账户文件写入磁盘失败：重启后可能需要重新登录。请检查数据目录权限或磁盘空间。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"第三方验证账户 {snapshot.Id} 续期异常", ex);
            return AccountResult.Fail("第三方验证账户续期失败，请检查网络后重试。");
        }
    }

    /// <summary>
    /// 启动前确认第三方验证令牌可用。
    ///
    /// 先 validate 再 refresh：authlib 令牌按规范不过期，每次都 refresh 会白白多一次写服务端；
    /// 但令牌可能被用户在服务器网站注销，validate 说不行时才续期。
    /// </summary>
    private async Task<Account?> EnsureValidAuthlibAccountAsync(Account account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(account.AuthServerUrl) ||
            string.IsNullOrEmpty(account.AccessToken) ||
            string.IsNullOrEmpty(account.RefreshToken))
        {
            _log.Warn("第三方验证账户缺少服务器地址或令牌，需要重新登录");
            return null;
        }

        var validation = await _authlib.ValidateAsync(
            account.AuthServerUrl,
            account.AccessToken,
            account.RefreshToken,
            cancellationToken).ConfigureAwait(false);

        if (validation.Success)
        {
            return account;
        }

        _log.Info($"第三方验证令牌校验未通过（{validation.Message}），尝试续期");
        var refreshed = await RefreshAsync(account.Id, cancellationToken).ConfigureAwait(false);

        return refreshed.Success ? refreshed.Account : null;
    }

    /// <inheritdoc />
    public async Task<AccountResult> SetSkinModelAsync(
        string accountId,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return AccountResult.Fail("账户不存在。");
        }

        // 只认两个词：别的地方（官方 variant、SkinProfileParser）都按这两个值判，
        // 脏值放进去只会在更深的地方变成「体型莫名其妙变回 Steve」。
        var normalized = string.Equals(model?.Trim(), "slim", StringComparison.OrdinalIgnoreCase)
            ? "slim"
            : "classic";

        Account updated;

        lock (_sync)
        {
            var target = _accounts.FirstOrDefault(a => a.Id == accountId);
            if (target is null)
            {
                return AccountResult.Fail("账户不存在，可能已经被移除。");
            }

            // 值没变就是空操作：不写盘、不发事件，免得界面白重绘一遍。
            if (string.Equals(target.SkinModel, normalized, StringComparison.Ordinal))
            {
                return AccountResult.Ok(target.Clone());
            }

            target.SkinModel = normalized;
            updated = target.Clone();
        }

        bool saved;
        try
        {
            saved = await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RollbackToPersisted();
            throw;
        }

        if (!saved)
        {
            RollbackToPersisted();
            return AccountResult.Fail("体型没能保存：账户文件写入磁盘失败，请检查数据目录权限或磁盘空间。");
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return AccountResult.Ok(updated);
    }

    private void ReplaceAll(List<Account> accounts, string? activeId)
    {
        lock (_sync)
        {
            _accounts.Clear();
            _accounts.AddRange(accounts);
            _activeAccountId = activeId;

            // 从磁盘读进来的状态就是「已落盘」的基准：回滚以它为准。
            _persistedSnapshot = accounts.Select(a => a.Clone()).ToList();
            _persistedActiveId = activeId;
        }
    }

    /// <summary>
    /// 把内存里的账户写回磁盘。返回是否真的落盘成功。
    ///
    /// 返回值不是装饰：调用方拿到 false 时不能再报「成功」。以前这里吞掉异常，
    /// 用户看到「已添加账户」，重启后档案消失，且没有任何提示。
    /// </summary>
    private async Task<bool> PersistAsync(CancellationToken cancellationToken)
    {
        var file = _paths.AccountsFile;
        var temp = file + ".tmp";

        string json;
        List<Account> snapshot;
        string? snapshotActiveId;

        lock (_sync)
        {
            var document = new AccountsDocument
            {
                ActiveAccountId = _activeAccountId,
                Accounts = _accounts.Select(a => new AccountRecord
                {
                    Id = a.Id,
                    Type = a.Type,
                    Username = a.Username,
                    Uuid = a.Uuid,
                    AccessToken = TokenProtector.Protect(a.AccessToken, _platform),
                    RefreshToken = TokenProtector.Protect(a.RefreshToken, _platform),
                    ExpiresAt = a.ExpiresAt,
                    SkinModel = a.SkinModel,
                    AuthServerUrl = a.AuthServerUrl,
                    LastLoginAt = a.LastLoginAt,
                }).ToList(),
            };

            json = JsonSerializer.Serialize(document, JsonOptions.Of<AccountsDocument>());

            // 与写入内容同源的内存快照：写盘失败/被取消时用它回滚，保证内存与磁盘一致。
            snapshot = _accounts.Select(a => a.Clone()).ToList();
            snapshotActiveId = _activeAccountId;
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);

            if (!_platform.SupportsSecureTokenStorage)
            {
                // 不隐瞒：macOS / Linux 上令牌只做了编码，等价于明文。
                _log.Warn("当前平台不支持系统级令牌加密，账户令牌仅以 Base64 保存，等同明文。");
            }

            lock (_sync)
            {
                _persistedSnapshot = snapshot;
                _persistedActiveId = snapshotActiveId;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"保存账户失败：{file}", ex);
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// 把内存恢复成「最近一次成功落盘」的状态。写盘失败或被取消时调用，
    /// 保证界面看到的列表与磁盘一致。
    /// </summary>
    private void RollbackToPersisted()
    {
        lock (_sync)
        {
            _accounts.Clear();
            _accounts.AddRange(_persistedSnapshot.Select(a => a.Clone()));
            _activeAccountId = _persistedActiveId;
        }
    }

    private void BackupBrokenFile(string file)
    {
        try
        {
            File.Copy(file, file + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn("备份损坏的账户文件失败", ex);
        }
    }
}

/// <summary>账户文件的顶层结构：当前激活账户 id + 账户列表。</summary>
internal sealed class AccountsDocument
{
    public string? ActiveAccountId { get; set; }

    public List<AccountRecord> Accounts { get; set; } = new();
}

/// <summary>单个账户的落盘形态（令牌已由 <see cref="TokenProtector"/> 处理）。</summary>
internal sealed class AccountRecord
{
    public string Id { get; set; } = string.Empty;

    public AccountType Type { get; set; }

    public string Username { get; set; } = string.Empty;

    public string? Uuid { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? SkinModel { get; set; }

    /// <summary>第三方验证账户的 API 根；旧文件没有该字段时为 null。</summary>
    public string? AuthServerUrl { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>
/// 账户文件的 Source Generator 上下文。理由同 <see cref="Settings.SettingsJsonContext"/>：
/// 裁剪发布下不能依赖运行时反射解析 <see cref="AccountsDocument"/> 的成员。
/// </summary>
[JsonSerializable(typeof(AccountsDocument))]
internal sealed partial class AccountJsonContext : JsonSerializerContext
{
    /// <summary>与迁移前一致的读写口径：Web 命名、缩进、容忍注释与尾逗号、枚举写名字。</summary>
    internal static readonly JsonSerializerOptions Writers = BuildOptions();

    private static JsonSerializerOptions BuildOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter<AccountType>() },
        TypeInfoResolver = new AccountJsonContext(),
    };
}
