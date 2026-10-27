namespace NebulaLauncher.Core.Abstractions;

/// <summary>启动器设置的读写与变更通知。</summary>
public interface ISettingsService
{
    /// <summary>当前生效的设置快照。修改后必须调用 <see cref="SaveAsync"/>。</summary>
    Models.AppSettings Current { get; }

    /// <summary>
    /// 加载设置。**幂等**：第一次之后直接返回，不再读盘、不再触发 <see cref="Changed"/>。
    ///
    /// 这条语义是刻意收紧的：设置被六个调用方各自加载（壳、实例上下文、账户页、设置页、资源页、服务器页），
    /// 旧实现每次进入页面都重读一次 JSON 并重发一次 Changed，导致主题与游戏目录被反复应用。
    /// 需要「外部改写后重新读取」的场景请用 <see cref="ReloadAsync"/>。
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>强制从磁盘重读，忽略「已加载」标记。用于导入配置、外部改动设置文件之后。</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default) => LoadAsync(cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>设置被外部（另一个窗口、文件监听）改变时触发。</summary>
    event EventHandler<Models.AppSettings>? Changed;
}

/// <summary>账户的增删查与登录流程。</summary>
public interface IAccountService
{
    IReadOnlyList<Models.Account> Accounts { get; }

    Models.Account? ActiveAccount { get; }

    /// <summary>
    /// 从 <c>accounts.json</c> 读入账户与「当前账户」。**幂等**：第一次之后直接返回，
    /// 不再读盘、不再触发 <see cref="AccountsChanged"/>。
    ///
    /// 与 <see cref="ISettingsService.LoadAsync"/> 同一条语义。组合根在建窗口之前加载一次
    /// （<c>Services/StartupState.cs</c>），首页 / 版本页 / 启动链在构造期就能拿到 <see cref="ActiveAccount"/>；
    /// 页面进来再调一次是空操作。旧实现只有账户页会加载，其它页面在那之前一律读到空账户——
    /// 表现是「刚打开程序未登录，去账户页转一圈回来才显示登录」（R59）。
    /// 要读回外部改写过的文件请用 <see cref="ReloadAsync"/>。
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>强制从磁盘重读账户，忽略「已加载」标记。用于外部改写 <c>accounts.json</c> 之后。</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default) => LoadAsync(cancellationToken);

    Task<Models.AccountResult> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>发起微软账户登录。实现方负责轮询设备码或弹出系统浏览器回调页。</summary>
    Task<Models.AccountResult> AddMicrosoftAccountAsync(
        IProgress<MicrosoftAuthProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加第三方验证（authlib-injector）账户。
    ///
    /// 走 <c>{apiRoot}/authserver/authenticate</c>，成功后账户带 <c>AuthServerUrl</c>，
    /// 启动时由 <see cref="IAuthlibInjectorService"/> 注入 javaagent。
    /// 默认实现返回「未实现」而不是静默成功——UI 接好按钮后必须能立刻看到失败原因。
    /// </summary>
    Task<Models.AccountResult> AddAuthlibAccountAsync(
        string apiRoot,
        string username,
        string password,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.AccountResult.Fail("当前账户服务未实现第三方验证登录。"));

    /// <summary>完成待选择角色的第三方登录；只有服务端确认角色绑定后才添加账户。</summary>
    Task<Models.AccountResult> SelectAuthlibProfileAsync(
        string selectionId,
        string profileId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.AccountResult.Fail("当前账户服务未实现角色选择。"));

    /// <summary>放弃未完成的角色选择，清除仅保存在内存中的登录凭据。</summary>
    void CancelAuthlibProfileSelection(string selectionId) { }

    /// <summary>刷新过期的令牌。已有效时直接返回成功。</summary>
    Task<Models.AccountResult> RefreshAsync(string accountId, CancellationToken cancellationToken = default);

    Task RemoveAsync(string accountId, CancellationToken cancellationToken = default);

    void SetActive(string accountId);

    /// <summary>必须在启动前调用，确保令牌有效。失败返回 null。</summary>
    Task<Models.Account?> EnsureValidActiveAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 改某个账户的皮肤体型（<c>classic</c> / <c>slim</c>）并落盘。
    ///
    /// **追加而非改签名**（CONVENTIONS §5）：默认实现返回「未实现」，这样既有的
    /// <see cref="IAccountService"/> 实现方（含测试替身）不需要改动就能编译。
    ///
    /// <para>
    /// 为什么这条能力必须挂在账户服务上、不能由界面直接改 <see cref="Models.Account.SkinModel"/>：
    /// 账户文件是「令牌加密 + 原子写盘 + 写失败回滚」三件事的唯一入口，绕过去改内存字段，
    /// 重启之后用户的体型选择就没了，而且不会有任何提示。
    /// </para>
    ///
    /// <para>
    /// <paramref name="model"/> 只认 <c>classic</c> 与 <c>slim</c>（大小写不敏感，其余值一律按
    /// <c>classic</c> 处理）。值没变时不写盘、不发 <see cref="AccountsChanged"/>。
    /// </para>
    /// </summary>
    Task<Models.AccountResult> SetSkinModelAsync(
        string accountId,
        string model,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models.AccountResult.Fail("当前账户服务不支持修改皮肤体型。"));

    event EventHandler? AccountsChanged;
}

/// <summary>微软登录进度，供 UI 展示设备码。</summary>
public sealed record MicrosoftAuthProgress(string Stage, string Message, string? UserCode = null, string? VerificationUri = null);
