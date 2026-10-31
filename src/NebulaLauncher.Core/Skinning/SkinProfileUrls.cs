using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Skinning;

/// <summary>
/// 会话服务器 profile 地址的唯一出处：账户头像（<see cref="Accounts.SkinAvatarService"/>）、
/// 侧边栏 3D 预览与「能上传什么」的能力查询都从这里取地址。
///
/// <para>
/// 为什么必须只有一个出处：同一个账户只能有一个 profile 地址。曾经 App 侧自己拼过一份，
/// 还「聪明」地剥掉了第三方 API 根末尾的 <c>/yggdrasil</c>，于是头像正常、预览恒回退默认皮肤——
/// 两份拼法就是两个真相，谁先漂移没人知道。
/// </para>
///
/// <para>
/// 第三方验证的 API 根（authlib-injector 的 <c>apiRoot</c>）本身就包含 <c>/yggdrasil</c> 前缀，
/// profile 端点挂在它<b>下面</b>而不是它的兄弟路径：剥掉后缀会打到站点根上，littleskin.cn 实测 404。
/// </para>
/// </summary>
public static class SkinProfileUrls
{
    /// <summary>Mojang 官方会话服务的 profile 前缀。</summary>
    public const string MojangProfileRoot = "https://sessionserver.mojang.com/session/minecraft/profile/";

    /// <summary>authlib-injector 约定的 profile 路径，接在第三方 API 根之后，<b>不</b>剥 <c>/yggdrasil</c>。</summary>
    public const string AuthlibProfilePath = "/sessionserver/session/minecraft/profile/";

    /// <summary>要服务端带上 <c>properties[].signature</c>：皮肤站与 Mojang 都认，缺签名时客户端只是少一层校验。</summary>
    private const string UnsignedQuery = "?unsigned=false";

    /// <summary>
    /// 这个账户该问哪个会话服务器。返回 <c>null</c> 表示没有远端皮肤：离线账户、缺 UUID，
    /// 或第三方账户没填 API 根——宁可退默认皮肤，也不要打错服务器。
    /// </summary>
    public static string? BuildProfileUrl(Account? account)
    {
        if (account is null || account.Type == AccountType.Offline || string.IsNullOrWhiteSpace(account.Uuid))
        {
            return null;
        }

        var uuid = account.Uuid.Trim().ToLowerInvariant();
        return account.Type switch
        {
            AccountType.Microsoft => MojangProfileRoot + uuid + UnsignedQuery,
            AccountType.Authlib when !string.IsNullOrWhiteSpace(account.AuthServerUrl) =>
                account.AuthServerUrl.Trim().TrimEnd('/') + AuthlibProfilePath + uuid + UnsignedQuery,
            _ => null,
        };
    }
}
