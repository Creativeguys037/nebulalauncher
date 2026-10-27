using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Skinning;

/// <summary>
/// 官方换肤区在这一屏上「有哪些按钮、能不能点、为什么不能点」。
///
/// 做成纯数据而不是散在 XAML 里的若干个 <c>IsVisible</c> 绑定：可见性判断一旦写进标记，
/// 离线账户该不该看见上传按钮这件事就没法被测到了（CONVENTIONS §8.2 禁止测试碰控件）。
/// </summary>
/// <param name="CanUpload">「应用到官方账户」是否可用。</param>
/// <param name="CanReset">「重置为默认皮肤」是否可用。</param>
/// <param name="CanPickModel">是否显示 Steve / Alex 切换（只有离线账户需要）。</param>
/// <param name="CapeEnabled">披风上传按钮是否可用。官方恒为 false。</param>
/// <param name="CapeText">披风按钮旁边那句话。</param>
/// <param name="UnavailableText">不可用的原因；空串表示一切正常，不用提示。</param>
public sealed record OfficialSkinCapability(
    bool CanUpload,
    bool CanReset,
    bool CanPickModel,
    bool CapeEnabled,
    string CapeText,
    string UnavailableText)
{
    /// <summary>有没有一句该显示给用户的原因。</summary>
    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableText);

    /// <summary>
    /// 原因要不要单独占一行。离线账户的原因已经跟在体型切换按钮后面了，
    /// 再单独显示一遍就是同一句话说两次。
    /// </summary>
    public bool ShowStandaloneReason => HasUnavailableReason && !CanPickModel;
}

/// <summary>
/// 按账户类型决定官方换肤区长什么样（票 #44：AC1 / AC3 / AC4 / AC5 / AC8）。
///
/// 一条贯穿全部判断的原则：**能在本地判定「这次请求必然失败」的，就不要把按钮点亮**。
/// 点亮一个必然 401 的按钮，用户点完只会得到一句和点之前一样的提示。
/// </summary>
public static class OfficialSkinActions
{
    /// <summary>披风按钮的文案。官方没有披风上传接口，所以这句话是常量而不是状态。</summary>
    public const string CapeText = "官方账户不支持更换披风";

    /// <summary>AC8：换肤生效范围的说明，常驻在操作区下方。</summary>
    public const string JavaOnlyHint = "官方换肤只影响 Java 版，且需重进游戏才在游戏中生效。";

    private const string NoAccountText = "先在账户页添加一个账户，再回来换肤。";

    private const string OfflineText = "离线账户没有官方皮肤档案；右边的体型切换只影响本机预览。";

    private const string AuthlibText = "第三方账户的皮肤在皮肤站上传，不走官方换肤。";

    private const string MissingTokenText = "这个微软账户缺少 Minecraft 令牌，请到账户页重新登录。";

    /// <summary>该账户能用官方换肤吗。传 null 表示「当前没有账户」。</summary>
    public static OfficialSkinCapability Of(Account? account)
    {
        if (account is null)
        {
            return Unavailable(NoAccountText);
        }

        switch (account.Type)
        {
            case AccountType.Microsoft:
                // 令牌存在才有意义：没有令牌的微软账户点下去必然是 401，而 401 的提示
                // 「请到账户页刷新令牌」在点之前就已经成立了。
                return string.IsNullOrWhiteSpace(account.AccessToken)
                    ? Unavailable(MissingTokenText)
                    : new OfficialSkinCapability(
                        CanUpload: true,
                        CanReset: true,
                        CanPickModel: false,
                        CapeEnabled: false,
                        CapeText,
                        UnavailableText: string.Empty);

            case AccountType.Offline:
                return new OfficialSkinCapability(
                    CanUpload: false,
                    CanReset: false,
                    CanPickModel: true,
                    CapeEnabled: false,
                    CapeText,
                    OfflineText);

            default:
                return Unavailable(AuthlibText);
        }
    }

    private static OfficialSkinCapability Unavailable(string reason) => new(
        CanUpload: false,
        CanReset: false,
        CanPickModel: false,
        CapeEnabled: false,
        CapeText,
        reason);
}
