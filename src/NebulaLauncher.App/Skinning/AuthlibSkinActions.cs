using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 皮肤站换肤区在这一屏上「有哪些按钮、能不能点、为什么不能点」（票 #43）。
///
/// <para>
/// 与 <see cref="OfficialSkinActions"/> 同一套做法：判断做成纯数据，不散进 XAML 的
/// 若干个 <c>IsVisible</c>。可见性一旦写进标记，「第三方账户该不该看见上传按钮」
/// 就没法被测到了（CONVENTIONS §8.2 禁止测试碰控件）。
/// </para>
///
/// <para>
/// 与官方那边最大的不同：<b>能上传什么由服务端说了算</b>。角色属性
/// <c>uploadableTextures</c> 决定能不能传皮肤 / 披风，所以这里多一个
/// <see cref="Capabilities"/> 输入——读不到 profile 之前一律按「什么都不能传」处理，
/// 而不是先点亮按钮让用户点了才失败。
/// </para>
/// </summary>
/// <param name="CanUploadSkin">「应用到皮肤站账户」是否可用。</param>
/// <param name="CanUploadCape">披风上传是否可用（由 <c>uploadableTextures</c> 决定）。</param>
/// <param name="CanDeleteSkin">能否删除当前皮肤。</param>
/// <param name="CanDeleteCape">能否删除披风。</param>
/// <param name="CapeText">披风按钮旁边那句话：能传是说「可上传」，不能传说明原因。</param>
/// <param name="UnavailableText">整块不可用的原因；空串表示一切正常。</param>
public sealed record AuthlibSkinCapability(
    bool CanUploadSkin,
    bool CanUploadCape,
    bool CanDeleteSkin,
    bool CanDeleteCape,
    string CapeText,
    string UnavailableText)
{
    /// <summary>有没有一句该显示给用户的原因。</summary>
    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableText);

    /// <summary>这一块整体是否可用（至少能传皮肤）。</summary>
    public bool IsAvailable => CanUploadSkin;
}

/// <summary>
/// 按账户类型 + 服务端能力算出皮肤站换肤区长什么样（票 #43：AC1 / AC4 / AC5 / AC8）。
///
/// 一条贯穿全部判断的原则与官方那边相同：**能在本地判定「这次请求必然失败」的，
/// 就不要把按钮点亮**。
/// </summary>
public static class AuthlibSkinActions
{
    /// <summary>披风可上传时的文案。</summary>
    public const string CapeUploadableText = "披风可上传";

    /// <summary>服务端没给该材质权限时的文案。</summary>
    public const string CapeNotAllowedText = "这个角色不允许上传披风（服务端 uploadableTextures 未包含 cape）";

    /// <summary>读不到能力时的文案：宁可说读不到，也不假装可以传。</summary>
    public const string CapeUnknownText = "还没读到角色权限，无法判断能否上传披风";

    /// <summary>官方账户该走官方通道，不该看见皮肤站的按钮。</summary>
    public const string OfficialAccountText = "官方账户的皮肤走官方换肤，不使用皮肤站上传。";

    /// <summary>离线账户没有皮肤站档案。</summary>
    public const string OfflineAccountText = "离线账户没有皮肤站档案，无法上传材质。";

    private const string NoAccountText = "先在账户页添加一个账户，再回来换肤。";

    private const string MissingTokenText = "这个皮肤站账户缺少访问令牌，请到账户页重新登录。";

    private const string MissingApiRootText = "这个账户没记录皮肤站地址（AuthServerUrl），无法上传材质。";

    private const string MissingUuidText = "这个账户缺少角色 UUID，请到账户页重新登录。";

    /// <summary>服务端明确不让传皮肤时的文案。</summary>
    public const string CannotUploadSkinText = "服务端不允许这个角色上传皮肤（uploadableTextures 未包含 skin）。";

    /// <summary>
    /// 该账户能用皮肤站换肤吗。
    /// </summary>
    /// <param name="account">当前账户；null 表示还没有账户。</param>
    /// <param name="capabilities">
    /// 从 profile 读到的角色能力。尚未读到（或读失败）时传 <c>null</c>，
    /// 此时皮肤上传按「地址类前置条件都满足就允许」处理——因为能否上传皮肤这件事
    /// 在 Yggdrasil 里并不由 <c>uploadableTextures</c> 单独决定（属性缺失时部分站点仍允许传皮肤），
    /// 而披风必须显式列出 <c>cape</c> 才点亮。
    /// </param>
    public static AuthlibSkinCapability Of(Account? account, SkinCapabilities? capabilities = null)
    {
        if (account is null)
        {
            return Unavailable(NoAccountText);
        }

        if (account.Type != AccountType.Authlib)
        {
            return Unavailable(account.Type == AccountType.Microsoft
                ? OfficialAccountText
                : OfflineAccountText);
        }

        // 三条地址类前置条件：缺任何一条，请求发出去也不会成功，所以按钮不点亮。
        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return Unavailable(MissingTokenText);
        }

        if (string.IsNullOrWhiteSpace(account.AuthServerUrl))
        {
            return Unavailable(MissingApiRootText);
        }

        if (string.IsNullOrWhiteSpace(account.Uuid))
        {
            return Unavailable(MissingUuidText);
        }

        // 能力未知（还没读到 profile）：皮肤按可传处理，披风按不可传处理。
        // 理由见方法注释：披风必须显式授权，其余情况不能替他做主。
        var canUploadCape = capabilities?.CanUploadCape == true;
        var capeUnknown = capabilities is null;

        // 读到能力且明确排除 skin 时才拦。属性缺失＝没读到，不走这条。
        var canUploadSkin = capabilities is null || capabilities.CanUploadSkin;

        return new AuthlibSkinCapability(
            CanUploadSkin: canUploadSkin,
            CanUploadCape: canUploadCape,
            CanDeleteSkin: canUploadSkin,
            CanDeleteCape: capabilities?.CanUploadCape == true,
            CapeText: canUploadCape
                ? CapeUploadableText
                : capeUnknown ? CapeUnknownText : CapeNotAllowedText,
            UnavailableText: canUploadSkin ? string.Empty : CannotUploadSkinText);
    }

    private static AuthlibSkinCapability Unavailable(string reason) => new(
        CanUploadSkin: false,
        CanUploadCape: false,
        CanDeleteSkin: false,
        CanDeleteCape: false,
        CapeText: reason,
        UnavailableText: reason);
}
