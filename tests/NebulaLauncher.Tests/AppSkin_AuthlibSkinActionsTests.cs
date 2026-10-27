using NebulaLauncher.Core.Models;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 票 #43：皮肤站换肤区的按钮可用性判定。
///
/// 只断言外部可观察的行为（哪个按钮会亮、旁边写什么话），不锁内部实现。
/// 与 <c>AppSkin_OfficialUploadUiTests</c> 同一套思路：可见性规则一旦只能靠肉眼
/// 在截图里确认，它就一定会在某次重构后静默失效。
/// </summary>
public sealed class AppSkin_AuthlibSkinActionsTests
{
    private static Account AuthlibAccount(
        string? accessToken = "token-value",
        string? apiRoot = "https://littleskin.cn/api/yggdrasil",
        string? uuid = "0123456789abcdef0123456789abcdef") => new()
        {
            Type = AccountType.Authlib,
            Username = "player",
            Uuid = uuid!,
            AccessToken = accessToken,
            AuthServerUrl = apiRoot,
        };

    [Fact]
    public void 没有账户时什么都不亮()
    {
        var capability = AuthlibSkinActions.Of(null);

        Assert.False(capability.CanUploadSkin);
        Assert.False(capability.CanUploadCape);
        Assert.False(capability.CanDeleteSkin);
        Assert.False(capability.CanDeleteCape);
        Assert.True(capability.HasUnavailableReason);
    }

    [Fact]
    public void 微软账户不走皮肤站通道()
    {
        var account = new Account
        {
            Type = AccountType.Microsoft,
            AccessToken = "token-value",
            Uuid = "0123456789abcdef0123456789abcdef",
        };

        var capability = AuthlibSkinActions.Of(account);

        Assert.False(capability.CanUploadSkin);
        Assert.Equal(AuthlibSkinActions.OfficialAccountText, capability.UnavailableText);
    }

    [Fact]
    public void 离线账户不走皮肤站通道()
    {
        var account = new Account { Type = AccountType.Offline, Uuid = "0123456789abcdef0123456789abcdef" };

        var capability = AuthlibSkinActions.Of(account);

        Assert.False(capability.CanUploadSkin);
        Assert.Equal(AuthlibSkinActions.OfflineAccountText, capability.UnavailableText);
    }

    [Fact]
    public void 缺访问令牌时不点亮()
    {
        var capability = AuthlibSkinActions.Of(AuthlibAccount(accessToken: null));

        Assert.False(capability.CanUploadSkin);
        Assert.True(capability.HasUnavailableReason);
    }

    [Fact]
    public void 缺皮肤站地址时不点亮()
    {
        var capability = AuthlibSkinActions.Of(AuthlibAccount(apiRoot: null));

        Assert.False(capability.CanUploadSkin);
        Assert.True(capability.HasUnavailableReason);
    }

    [Fact]
    public void 缺UUID时不点亮()
    {
        var capability = AuthlibSkinActions.Of(AuthlibAccount(uuid: null));

        Assert.False(capability.CanUploadSkin);
        Assert.True(capability.HasUnavailableReason);
    }

    [Fact]
    public void 前置条件齐了就能传皮肤()
    {
        var capability = AuthlibSkinActions.Of(AuthlibAccount());

        Assert.True(capability.CanUploadSkin);
        Assert.True(capability.IsAvailable);
        Assert.False(capability.HasUnavailableReason);
    }

    /// <summary>票面点名：披风按钮的显隐由 uploadableTextures 决定。</summary>
    [Fact]
    public void 属性含cape时披风可上传()
    {
        var capability = AuthlibSkinActions.Of(
            AuthlibAccount(),
            new SkinCapabilities(CanUploadSkin: true, CanUploadCape: true));

        Assert.True(capability.CanUploadCape);
        Assert.True(capability.CanDeleteCape);
        Assert.Equal(AuthlibSkinActions.CapeUploadableText, capability.CapeText);
    }

    [Fact]
    public void 属性未含cape时披风不可上传并说明原因()
    {
        var capability = AuthlibSkinActions.Of(
            AuthlibAccount(),
            new SkinCapabilities(CanUploadSkin: true, CanUploadCape: false));

        Assert.False(capability.CanUploadCape);
        Assert.False(capability.CanDeleteCape);
        Assert.Equal(AuthlibSkinActions.CapeNotAllowedText, capability.CapeText);
    }

    /// <summary>还没读到 profile：不能假装可以传披风。</summary>
    [Fact]
    public void 能力未知时披风按不可上传处理()
    {
        var capability = AuthlibSkinActions.Of(AuthlibAccount(), capabilities: null);

        Assert.False(capability.CanUploadCape);
        Assert.Equal(AuthlibSkinActions.CapeUnknownText, capability.CapeText);
    }

    /// <summary>属性两项皆 false：皮肤也不点亮。</summary>
    [Fact]
    public void 属性两项皆false时皮肤与披风都不点亮()
    {
        var capability = AuthlibSkinActions.Of(
            AuthlibAccount(),
            SkinCapabilities.None);

        Assert.False(capability.CanUploadSkin);
        Assert.False(capability.CanUploadCape);
        Assert.Equal(AuthlibSkinActions.CannotUploadSkinText, capability.UnavailableText);
    }
}
