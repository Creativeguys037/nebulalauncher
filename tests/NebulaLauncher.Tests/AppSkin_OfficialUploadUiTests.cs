using System;
using System.Collections.Generic;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 官方换肤的界面接线（票 #44 第二段）。
///
/// 这里测的是「界面拿到的判定」而不是界面本身：哪些按钮能点、点了会说什么、
/// 什么情况下压根不该发请求、失败之后给不给重试。这些全部是纯 C#，
/// 所以测试不碰任何 Avalonia 类型（CONVENTIONS §8.2）。
///
/// 具体到票面的九条 AC，落点是：体型映射（<see cref="SkinModelMapping"/>）、
/// 按钮可用性与文案（<see cref="OfficialSkinActions"/>）、上传前置校验
/// （<see cref="SkinUploadPreflight"/>）、限速（<see cref="SkinUploadThrottle"/>）、
/// 失败呈现（<see cref="SkinUploadFeedback"/>）。
/// </summary>
public class AppSkin_OfficialUploadUiTests
{
    private static Account Microsoft(string? accessToken = "mc-access-token") => new()
    {
        Type = AccountType.Microsoft,
        Username = "Notch",
        Uuid = "0123456789abcdef0123456789abcdef",
        AccessToken = accessToken,
        SkinModel = "classic",
    };

    /// <summary>
    /// 造一段只有 PNG 头的字节：8 字节签名 + 长度 + "IHDR" + 宽高。
    /// 校验只读这 24 个字节，不需要真的能解码，因此不用造完整图像。
    /// </summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[26];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);

        // chunk 长度（IHDR 恒为 13，big-endian）。
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));

        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    // ==================== 按钮可用性与文案（AC1 / AC3 / AC4 / AC5 / AC8） ====================

    [Fact]
    public void 微软账户能上传与重置但不显示体型切换()
    {
        var capability = OfficialSkinActions.Of(Microsoft());

        Assert.True(capability.CanUpload);
        Assert.True(capability.CanReset);

        // 官方账户的体型跟着上传时选的 variant 走，界面再给一个切换器等于给两个互相打架的入口。
        Assert.False(capability.CanPickModel);
        Assert.Equal(string.Empty, capability.UnavailableText);
    }

    [Fact]
    public void 离线账户不显示上传按钮但允许在Steve与Alex之间切换()
    {
        var capability = OfficialSkinActions.Of(new Account { Type = AccountType.Offline, Username = "Steve" });

        Assert.False(capability.CanUpload);
        Assert.False(capability.CanReset);
        Assert.True(capability.CanPickModel);

        // AC5 要求「不显示上传按钮」，所以这里要说明为什么不显示，而不是给一个灰按钮让人猜。
        Assert.Contains("离线账户", capability.UnavailableText);

        // 那句话跟在体型按钮后面，不再单独占一行：账户条每多一行就压掉列表一行。
        Assert.False(capability.ShowStandaloneReason);
    }

    [Fact]
    public void 需要单行说明的账户类型才占一整行()
    {
        // 没有账户 / 第三方 / 缺令牌：这三种没有按钮可跟，原因自己占一行。
        Assert.True(OfficialSkinActions.Of(null).ShowStandaloneReason);
        Assert.True(OfficialSkinActions.Of(Microsoft(accessToken: null)).ShowStandaloneReason);
        Assert.True(OfficialSkinActions.Of(new Account { Type = AccountType.Authlib, Username = "Third" }).ShowStandaloneReason);

        // 正常可用的微软账户不该显示任何原因。
        Assert.False(OfficialSkinActions.Of(Microsoft()).ShowStandaloneReason);
        Assert.False(OfficialSkinActions.Of(Microsoft()).HasUnavailableReason);
    }

    [Fact]
    public void 没有账户时上传与重置都不可用并提示先添加账户()
    {
        var capability = OfficialSkinActions.Of(null);

        Assert.False(capability.CanUpload);
        Assert.False(capability.CanReset);
        Assert.False(capability.CanPickModel);
        Assert.Contains("添加一个账户", capability.UnavailableText);
    }

    [Fact]
    public void 第三方账户不走官方换肤入口()
    {
        var capability = OfficialSkinActions.Of(new Account
        {
            Type = AccountType.Authlib,
            Username = "ThirdParty",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
            AccessToken = "authlib-token",
        });

        Assert.False(capability.CanUpload);
        Assert.False(capability.CanReset);

        // 第三方站的换肤是另一条链路（票 #43），这里必须说清楚不是「坏了」。
        Assert.Contains("第三方", capability.UnavailableText);
    }

    [Fact]
    public void 缺令牌的微软账户提示重新登录而不是发一次必然401的请求()
    {
        var capability = OfficialSkinActions.Of(Microsoft(accessToken: null));

        Assert.False(capability.CanUpload);
        Assert.False(capability.CanReset);
        Assert.Contains("重新登录", capability.UnavailableText);
    }

    [Fact]
    public void 披风按钮恒为禁用并写明官方不支持()
    {
        var capability = OfficialSkinActions.Of(Microsoft());

        // AC4：官方没有披风上传接口，所以按钮只能是灰色禁用 + 这句话。
        Assert.False(capability.CapeEnabled);
        Assert.Equal("官方账户不支持更换披风", capability.CapeText);
    }

    [Fact]
    public void 提示写明只影响Java版且需重进游戏()
    {
        Assert.Contains("Java 版", OfficialSkinActions.JavaOnlyHint);
        Assert.Contains("重进游戏", OfficialSkinActions.JavaOnlyHint);
    }

    // ==================== 体型映射（AC5 / AC1） ====================

    [Fact]
    public void 体型在两个枚举之间来回映射不改语义()
    {
        Assert.Equal(LocalSkinModel.Slim, SkinModelMapping.ToLocal(SkinModelKind.Alex));
        Assert.Equal(LocalSkinModel.Classic, SkinModelMapping.ToLocal(SkinModelKind.Steve));

        Assert.Equal(SkinModelKind.Alex, SkinModelMapping.ToKind(LocalSkinModel.Slim));
        Assert.Equal(SkinModelKind.Steve, SkinModelMapping.ToKind(LocalSkinModel.Classic));
    }

    [Fact]
    public void 落盘用的体型取值只有classic与slim()
    {
        Assert.Equal("classic", SkinModelMapping.ToAccountValue(LocalSkinModel.Classic));
        Assert.Equal("slim", SkinModelMapping.ToAccountValue(LocalSkinModel.Slim));

        // 大写、脏值一律回到 classic：账户文件可能被手改成任何东西。
        Assert.Equal(LocalSkinModel.Slim, SkinModelMapping.FromAccountValue("SLIM"));
        Assert.Equal(LocalSkinModel.Classic, SkinModelMapping.FromAccountValue("whatever"));
        Assert.Equal(LocalSkinModel.Classic, SkinModelMapping.FromAccountValue(null));
    }

    // ==================== 上传前置校验（AC9） ====================

    [Fact]
    public void 合格的64乘64皮肤通过前置校验()
    {
        Assert.Null(SkinUploadPreflight.Check(Png(64, 64), LocalSkinModel.Classic));
        Assert.Null(SkinUploadPreflight.Check(Png(64, 64), LocalSkinModel.Slim));
        Assert.Null(SkinUploadPreflight.Check(Png(64, 32), LocalSkinModel.Classic));
    }

    [Fact]
    public void 不是PNG的内容在上传前就被拦下()
    {
        var error = SkinUploadPreflight.Check([0x01, 0x02, 0x03, 0x04], LocalSkinModel.Classic);

        Assert.NotNull(error);
        Assert.Contains("PNG", error!);
    }

    [Fact]
    public void 官方不收的大图在上传前被拦下并写明实际尺寸()
    {
        // 128×64 能过 LocalSkinFormat.Validate（宽是 64 的倍数、高是 32 的倍数），
        // 但官方只认 64×64 与 64×32，所以前置校验必须在本地就把它拦下来，省一次必然 400 的请求。
        var error = SkinUploadPreflight.Check(Png(128, 64), LocalSkinModel.Classic);

        Assert.NotNull(error);
        Assert.Contains("64×64", error!);
        Assert.Contains("64×32", error!);
        Assert.Contains("128×64", error!);
    }

    [Fact]
    public void 细手臂皮肤必须是64乘64()
    {
        var error = SkinUploadPreflight.Check(Png(64, 32), LocalSkinModel.Slim);

        Assert.NotNull(error);
        Assert.Contains("64×64", error!);
    }

    [Fact]
    public void 空载荷给出可照做的原因()
    {
        var error = SkinUploadPreflight.Check([], LocalSkinModel.Classic);

        Assert.NotNull(error);
        Assert.NotEmpty(error!);
    }

    // ==================== 限速（AC6：429/5xx 可重试但节流） ====================

    [Fact]
    public void 可重试失败之后进入冷却并给出剩余秒数()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var throttle = new SkinUploadThrottle(TimeSpan.FromSeconds(15), () => now);

        Assert.False(throttle.IsBlocked);
        Assert.Null(throttle.BlockedText);

        throttle.MarkRetryableFailure();

        Assert.True(throttle.IsBlocked);
        Assert.Contains("15 秒", throttle.BlockedText!);
        Assert.Equal(TimeSpan.FromSeconds(15), throttle.Remaining);
    }

    [Fact]
    public void 冷却时间走完才允许再发请求()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var throttle = new SkinUploadThrottle(TimeSpan.FromSeconds(15), () => now);

        throttle.MarkRetryableFailure();

        now = now.AddSeconds(14);
        Assert.True(throttle.IsBlocked);

        now = now.AddSeconds(2);
        Assert.False(throttle.IsBlocked);
        Assert.Null(throttle.BlockedText);
    }

    [Fact]
    public void 成功之后清掉冷却()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var throttle = new SkinUploadThrottle(TimeSpan.FromSeconds(15), () => now);

        throttle.MarkRetryableFailure();
        throttle.MarkSuccess();

        Assert.False(throttle.IsBlocked);
    }

    [Fact]
    public void 默认冷却时长为十五秒()
    {
        // 429 之后立刻让用户再点一次，等于把限流当成网络抖动。
        Assert.Equal(TimeSpan.FromSeconds(15), new SkinUploadThrottle().Cooldown);
    }

    // ==================== 失败呈现（AC6 / AC7 / AC8） ====================

    [Fact]
    public void 成功反馈不显示任何错误入口()
    {
        var feedback = SkinUploadFeedback.For(SkinUploadResult.Ok(), "已应用到官方账户");

        Assert.False(feedback.IsError);
        Assert.False(feedback.CanRetry);
        Assert.False(feedback.GoToAccountPage);
        Assert.Equal("已应用到官方账户", feedback.Text);
    }

    [Fact]
    public void 令牌过期给去账户页的入口而不是重试()
    {
        var result = SkinUploadResult.Fail("登录已过期，请到账户页刷新令牌。", isAuthError: true);

        var feedback = SkinUploadFeedback.For(result, "已应用到官方账户");

        Assert.True(feedback.IsError);
        Assert.True(feedback.GoToAccountPage);

        // AC6：401 不自动重试，连按钮都不该给，否则用户会一直点、一直 401。
        Assert.False(feedback.CanRetry);
        Assert.Contains("账户页", feedback.Text);
    }

    [Fact]
    public void 可重试失败给重试入口且指向冷却()
    {
        var result = SkinUploadResult.Fail("无网络：官方服务暂时不可用，请稍后重试。", isRetryable: true);

        var feedback = SkinUploadFeedback.For(result, "已应用到官方账户");

        Assert.True(feedback.IsError);
        Assert.True(feedback.CanRetry);
        Assert.False(feedback.GoToAccountPage);
    }

    [Fact]
    public void 尺寸不合格的400不给重试入口()
    {
        var result = SkinUploadResult.Fail("皮肤尺寸必须是 64×64 或 64×32，且必须是 PNG 格式。");

        var feedback = SkinUploadFeedback.For(result, "已应用到官方账户");

        Assert.True(feedback.IsError);
        Assert.False(feedback.CanRetry);
        Assert.False(feedback.GoToAccountPage);
        Assert.Contains("64×64", feedback.Text);
    }
}
