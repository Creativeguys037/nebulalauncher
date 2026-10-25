using NebulaLauncher.Core.Models;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 账户类型的界面文案。账户类型本有三态（离线 / 微软 / 第三方验证），
/// 文案曾分散在首页、账户页大卡片、账户列表行三处各写一份判定，
/// 首页那份写成了二元判定，把「第三方验证」压进「离线」一档（票 28）。
/// 收敛到这里之后，三处只渲染同一份数据，不会再各自漂移。
/// </summary>
internal static class AccountTypeDisplay
{
    /// <summary>长标签：首页与账户页大卡片的类型徽标。</summary>
    public static string BadgeOf(AccountType type) => type switch
    {
        AccountType.Microsoft => "微软账户",
        AccountType.Authlib => "第三方验证",
        _ => "离线账户",
    };

    /// <summary>
    /// 短标签：账户列表行。由 <see cref="BadgeOf"/> 派生（去掉「账户」后缀），
    /// 而不是再写一份 switch——两份字面量并存就还能各自漂移，那正是本单的成因。
    /// 「第三方验证」本身没有「账户」后缀，原样返回，视觉宽度与合并前逐字一致。
    /// </summary>
    public static string ShortBadgeOf(AccountType type)
    {
        var badge = BadgeOf(type);
        return badge.EndsWith("账户", StringComparison.Ordinal) ? badge[..^2] : badge;
    }

    /// <summary>
    /// 账户详情行：主机名 · UUID。只有第三方验证账户带主机名，
    /// 其余账户（以及 API 根缺失 / 非法时）只显示 UUID。
    ///
    /// <paramref name="uuidText"/> 由调用方决定：首页显示剪短版，账户页显示完整值。
    /// 共享的只是「要不要带主机名」和拼装格式——那正是三处各自漂移的地方。
    /// </summary>
    public static string DetailOf(Account account, string uuidText)
    {
        var host = account.Type == AccountType.Authlib ? HostOf(account.AuthServerUrl) : null;
        return host is null ? uuidText : $"{host} · {uuidText}";
    }

    /// <summary>
    /// 第三方验证账户的 API 根主机名。只暴露主机名：
    /// 完整 API 路径和令牌都不该出现在界面上。
    /// </summary>
    public static string? HostOf(string? apiRoot)
        => Uri.TryCreate(apiRoot, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
}
