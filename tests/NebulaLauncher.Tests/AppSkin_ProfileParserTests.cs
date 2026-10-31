using System;
using System.Collections.Generic;
using System.Text;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.Tests;

public class AppSkin_ProfileParserTests
{
    private const string SkinUrl = "https://example.com/textures/skin.png";
    private const string CapeUrl = "https://example.com/textures/cape.png";

    [Fact]
    public void 离线账户没有皮肤接口地址()
    {
        var account = new Account { Type = AccountType.Offline, Uuid = "0123456789abcdef0123456789abcdef" };
        Assert.Null(SkinProfileParser.BuildProfileUrl(account));
        Assert.Null(SkinProfileParser.BuildProfileUrl(null));
    }

    [Fact]
    public void 微软账户走官方会话服务器()
    {
        var account = new Account
        {
            Type = AccountType.Microsoft,
            Uuid = "0123456789abcdef0123456789abcdef",
        };
        Assert.Equal(
            "https://sessionserver.mojang.com/session/minecraft/profile/0123456789abcdef0123456789abcdef?unsigned=false",
            SkinProfileParser.BuildProfileUrl(account));
    }

    [Fact]
    public void 第三方账户的会话服务拼在yggdrasil根之下()
    {
        // 皮肤站的 profile 端点就在 API 根内部（littleskin 实测 200），不是它的兄弟路径：
        // 剥掉 /yggdrasil 会拼出 /api/sessionserver/...，实测 404，皮肤预览恒回退默认。
        var account = new Account
        {
            Type = AccountType.Authlib,
            Uuid = "0123456789abcdef0123456789abcdef",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil",
        };
        Assert.Equal(
            "https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile/0123456789abcdef0123456789abcdef?unsigned=false",
            SkinProfileParser.BuildProfileUrl(account));
    }

    [Fact]
    public void 预览与头像问的是同一个profile地址()
    {
        // 两处各拼一份时，一份对一份错就会「头像正常、预览恒回退默认皮肤」。
        // 这条把两个入口钉在一起：改规则只许改一处，另一处必须跟着动。
        var account = new Account
        {
            Type = AccountType.Authlib,
            Uuid = "0123456789ABCDEF0123456789ABCDEF",
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil/",
        };

        var preview = SkinProfileParser.BuildProfileUrl(account);
        var avatar = SkinAvatarService.BuildProfileUrl(account);

        Assert.Equal(avatar, preview);
        Assert.Equal(
            "https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile/0123456789abcdef0123456789abcdef?unsigned=false",
            preview);
    }

    [Fact]
    public void 第三方账户没有yggdrasil后缀时直接追加会话路径()
    {
        var account = new Account
        {
            Type = AccountType.Authlib,
            Uuid = "0123456789abcdef0123456789abcdef",
            AuthServerUrl = "https://skin.example.com/api/",
        };
        Assert.Equal(
            "https://skin.example.com/api/sessionserver/session/minecraft/profile/0123456789abcdef0123456789abcdef?unsigned=false",
            SkinProfileParser.BuildProfileUrl(account));
    }

    [Fact]
    public void 没有uuid时拿不到皮肤接口地址()
    {
        var account = new Account { Type = AccountType.Microsoft, Uuid = "  " };
        Assert.Null(SkinProfileParser.BuildProfileUrl(account));
    }

    [Fact]
    public void 解析皮肤与披风地址()
    {
        var textures = SkinProfileParser.ParseProfile(ProfileJson(PayloadJson(skinUrl: SkinUrl, capeUrl: CapeUrl)));

        Assert.NotNull(textures);
        Assert.Equal(SkinUrl, textures!.SkinUrl);
        Assert.Equal(CapeUrl, textures.CapeUrl);
        Assert.Equal("classic", textures.Model);
    }

    [Fact]
    public void 没有披风时披风地址为空()
    {
        var textures = SkinProfileParser.ParseProfile(ProfileJson(PayloadJson(skinUrl: SkinUrl)));

        Assert.NotNull(textures);
        Assert.Null(textures!.CapeUrl);
    }

    [Fact]
    public void 体型为slim时覆盖账户回退()
    {
        var textures = SkinProfileParser.ParseProfile(
            ProfileJson(PayloadJson(model: "slim")), fallbackModel: "classic");

        Assert.NotNull(textures);
        Assert.Equal("slim", textures!.Model);
    }

    [Fact]
    public void 缺少metadata时沿用账户体型()
    {
        var textures = SkinProfileParser.ParseProfile(ProfileJson(PayloadJson()), fallbackModel: "slim");

        Assert.NotNull(textures);
        Assert.Equal("slim", textures!.Model);
    }

    [Fact]
    public void 未知体型收敛成classic()
    {
        var textures = SkinProfileParser.ParseProfile(
            ProfileJson(PayloadJson(model: "wide")), fallbackModel: "slim");

        Assert.NotNull(textures);
        Assert.Equal("classic", textures!.Model);
    }

    [Fact]
    public void 没有皮肤时返回空交给默认皮肤()
    {
        var payload = "{\"textures\":{\"CAPE\":{\"url\":\"" + CapeUrl + "\"}}}";
        Assert.Null(SkinProfileParser.ParseProfile(ProfileJson(payload)));
    }

    [Fact]
    public void 坏base64返回空而不是抛异常()
    {
        Assert.Null(SkinProfileParser.ParseProfile(
            "{\"properties\":[{\"name\":\"textures\",\"value\":\"!!!not-base64!!!\"}]}"));
    }

    [Fact]
    public void 载荷不是json时返回空()
    {
        Assert.Null(SkinProfileParser.ParseProfile(ProfileJson("这不是 JSON")));
    }

    [Fact]
    public void 响应对不上格式时返回空()
    {
        Assert.Null(SkinProfileParser.ParseProfile(null));
        Assert.Null(SkinProfileParser.ParseProfile("{}"));
        Assert.Null(SkinProfileParser.ParseProfile("{\"properties\":\"不是数组\"}"));
        Assert.Null(SkinProfileParser.ParseProfile("{\"properties\":[{\"name\":\"other\",\"value\":\"x\"}]}"));
    }

    [Fact]
    public void 采用URL安全base64也能解析()
    {
        var standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(PayloadJson(skinUrl: SkinUrl)));
        var urlSafe = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var json = "{\"properties\":[{\"name\":\"textures\",\"value\":\"" + urlSafe + "\"}]}";

        var textures = SkinProfileParser.ParseProfile(json);
        Assert.NotNull(textures);
        Assert.Equal(SkinUrl, textures!.SkinUrl);
    }

    private static string PayloadJson(string? skinUrl = SkinUrl, string? capeUrl = null, string? model = null)
    {
        var entries = new List<string>();
        if (skinUrl is not null)
        {
            entries.Add(model is null
                ? "\"SKIN\":{\"url\":\"" + skinUrl + "\"}"
                : "\"SKIN\":{\"url\":\"" + skinUrl + "\",\"metadata\":{\"model\":\"" + model + "\"}}");
        }

        if (capeUrl is not null)
        {
            entries.Add("\"CAPE\":{\"url\":\"" + capeUrl + "\"}");
        }

        return "{\"timestamp\":0,\"profileId\":\"0123\",\"profileName\":\"Steve\",\"textures\":{"
            + string.Join(",", entries) + "}}";
    }

    private static string ProfileJson(string payload)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        return "{\"id\":\"0123\",\"name\":\"Steve\",\"properties\":[{\"name\":\"textures\",\"value\":\"" + encoded + "\"}]}";
    }
}
