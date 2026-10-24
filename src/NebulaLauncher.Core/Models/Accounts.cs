using System.Collections.Generic;

namespace NebulaLauncher.Core.Models;

/// <summary>账户类型。离线账户不需要网络，微软账户走 OAuth 设备码/MSAL 流程。</summary>
public enum AccountType
{
    Offline = 0,
    Microsoft = 1,

    /// <summary>
    /// 第三方验证（authlib-injector / 统一通行证 / 自建皮肤站）。
    /// 令牌与角色信息来自用户填写的 API 根，启动时需要把 authlib-injector 注入 JVM。
    /// </summary>
    Authlib = 2,
}

/// <summary>
/// 启动器账户。敏感字段（AccessToken / RefreshToken）由 <c>IAccountService</c> 在落盘时
/// 通过 DPAPI（Windows）或文件权限（Unix）保护，模型本身不负责加密。
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AccountType Type { get; set; } = AccountType.Offline;

    /// <summary>展示名。离线账户即用户输入的名字，微软账户为 Minecraft 档案名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>无连字符的 32 位小写 UUID。</summary>
    public string Uuid { get; set; } = string.Empty;

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>皮肤模型：<c>classic</c> 或 <c>slim</c>。仅影响本地预览。</summary>
    public string SkinModel { get; set; } = "classic";

    /// <summary>
    /// 第三方验证账户的 API 根（例如 <c>https://littleskin.cn/api/yggdrasil</c>）；
    /// 其它类型为 null。启动时既用于 authlib-injector 的 <c>-javaagent</c> 参数，也用于令牌刷新。
    /// </summary>
    public string? AuthServerUrl { get; set; }

    /// <summary>最近一次成功登录时间，用于列表排序。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    public bool IsExpired => ExpiresAt is { } e && e <= DateTimeOffset.UtcNow;

    public Account Clone() => (Account)MemberwiseClone();
}

/// <summary>账户操作结果，替代抛异常做流程控制。</summary>
public sealed record AccountResult(bool Success, string? Message = null, Account? Account = null)
{
    public string? ProfileSelectionId { get; init; }

    public IReadOnlyList<AuthlibProfileOption> AvailableProfiles { get; init; } = [];

    public bool RequiresProfileSelection => ProfileSelectionId is not null;

    /// <summary>
    /// 失败是否属于「临时故障、稍后重试可能成功」。
    ///
    /// 目前只有微软续期的 429 / 5xx 会被标成 true。调用方据此区分「令牌真失效」与
    /// 「服务端暂时不可用」：前者必须重新登录，后者不该拦着用户启动游戏
    /// （REVIEW-R43-R51 P2-4）。
    /// </summary>
    public bool Retryable { get; init; }

    public static AccountResult Ok(Account account) => new(true, null, account);

    public static AccountResult Fail(string message, bool retryable = false) =>
        new(false, message) { Retryable = retryable };
}

/// <summary>第三方账户可选角色；不向界面暴露未绑定角色的访问令牌。</summary>
public sealed record AuthlibProfileOption(string Id, string Name);

/// <summary>一次 authlib-injector 注入所需的全部参数。</summary>
/// <param name="JarPath">本地 jar 的绝对路径。</param>
/// <param name="ApiRoot">第三方验证服务的 API 根。</param>
/// <param name="JvmArguments">可直接追加到 JVM 参数末尾的片段（空格分隔）。</param>
public sealed record AuthlibInjection(string JarPath, string ApiRoot, string JvmArguments);
