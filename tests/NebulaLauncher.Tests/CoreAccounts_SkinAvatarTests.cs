using System.Text;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤头像服务里可离线验证的部分：纹理地址规则、profile / textures 解析、皮肤 PNG 校验。
///
/// 规则照抄 PCL 的 <c>McSkinGetAddress</c>：微软账户固定打 Mojang，第三方账户打自己的 API 根；
/// 网络取图与缓存落盘不在这一层，避免把测试变成对远程皮肤站的依赖。
/// </summary>
public class CoreAccounts_SkinAvatarTests
{
    private const string Uuid = "493e32e14248466d97fd154e215f2201";
    private const string SkinUrl = "https://example.invalid/skins/crimson.png";

    // ---------------- 纹理地址 ----------------

    [Fact]
    public void 纹理地址_微软账户打Mojang且UUID转小写()
    {
        var account = new Account
        {
            Type = AccountType.Microsoft,
            Username = "CrimsonOR",
            Uuid = Uuid.ToUpperInvariant(),
        };

        Assert.Equal(SkinAvatarService.MojangProfileRoot + Uuid, SkinAvatarService.BuildProfileUrl(account));
    }

    [Fact]
    public void 纹理地址_第三方账户拼在Api根之下()
    {
        var account = new Account
        {
            Type = AccountType.Authlib,
            Uuid = Uuid,
            AuthServerUrl = "https://littleskin.cn/api/yggdrasil/",
        };

        Assert.Equal(
            "https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile/" + Uuid,
            SkinAvatarService.BuildProfileUrl(account));
    }

    [Fact]
    public void 纹理地址_离线账户没有远端皮肤()
    {
        var account = new Account { Type = AccountType.Offline, Username = "Crimson", Uuid = Uuid };

        Assert.Null(SkinAvatarService.BuildProfileUrl(account));
    }

    [Fact]
    public void 纹理地址_第三方账户缺Api根时返回空()
    {
        // 没有 API 根就不知道该问谁：宁可退默认皮肤，也不要打错服务器。
        var account = new Account { Type = AccountType.Authlib, Uuid = Uuid, AuthServerUrl = "  " };

        Assert.Null(SkinAvatarService.BuildProfileUrl(account));
    }

    [Fact]
    public void 纹理地址_UUID为空时返回空()
    {
        var account = new Account { Type = AccountType.Microsoft, Username = "CrimsonOR" };

        Assert.Null(SkinAvatarService.BuildProfileUrl(account));
    }

    // ---------------- profile / textures 解析 ----------------

    [Fact]
    public void profile解析_从textures属性里取SKIN地址()
    {
        var json = ProfileJson(TexturesBase64(SkinUrl));

        var parsed = SkinAvatarService.ParseProfile(json);

        Assert.NotNull(parsed);
        Assert.Equal(SkinUrl, parsed!.Value.Url);
        Assert.Equal("classic", parsed.Value.Model);
    }

    [Fact]
    public void profile解析_metadata为slim时模型为slim()
    {
        var json = ProfileJson(TexturesBase64(SkinUrl, "slim"));

        var parsed = SkinAvatarService.ParseProfile(json);

        Assert.NotNull(parsed);
        Assert.Equal("slim", parsed!.Value.Model);
    }

    [Fact]
    public void profile解析_没有properties数组时返回空()
    {
        Assert.Null(SkinAvatarService.ParseProfile("{\"id\":\"abc\",\"name\":\"CrimsonOR\"}"));
    }

    [Fact]
    public void profile解析_非JSON返回空()
    {
        // 皮肤站限流时返回的是 HTML 错误页，不该把异常抛到 UI 线程。
        Assert.Null(SkinAvatarService.ParseProfile("<html>429 Too Many Requests</html>"));
        Assert.Null(SkinAvatarService.ParseProfile(string.Empty));
    }

    [Fact]
    public void 纹理解析_坏base64返回空()
    {
        Assert.Null(SkinAvatarService.ParseTextures("这不是 base64"));
    }

    [Fact]
    public void 纹理解析_没有SKIN节点返回空()
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"textures\":{\"CAPE\":{\"url\":\"https://e.invalid/c.png\"}}}"));

        Assert.Null(SkinAvatarService.ParseTextures(base64));
    }

    // ---------------- 皮肤 PNG 校验 ----------------

    [Theory]
    [InlineData(64, 64)]
    [InlineData(64, 32)]
    public void 皮肤校验_接受两种历史尺寸(int width, int height)
    {
        Assert.True(SkinAvatarService.IsUsableSkin(FakePng(width, height)));
    }

    [Fact]
    public void 皮肤校验_拒绝非64宽()
    {
        // 32×32 是客户端缓存里的小图标，取 8×8 的脸会越界。
        Assert.False(SkinAvatarService.IsUsableSkin(FakePng(32, 32)));
    }

    [Fact]
    public void 皮肤校验_拒绝非PNG()
    {
        var jpeg = new byte[64];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;

        Assert.False(SkinAvatarService.IsUsableSkin(jpeg));
    }

    [Fact]
    public void 皮肤校验_拒绝超过体积上限的数据()
    {
        Assert.False(SkinAvatarService.IsUsableSkin(new byte[SkinAvatarService.MaxSkinBytes + 1]));
    }

    // ---------------- 模型归一化 ----------------

    [Theory]
    [InlineData("slim", "slim")]
    [InlineData("SLIM", "slim")]
    [InlineData("classic", "classic")]
    [InlineData(null, "classic")]
    [InlineData("", "classic")]
    public void 模型归一化_只认slim其余归classic(string? model, string expected)
    {
        Assert.Equal(expected, SkinAvatarService.NormalizeModel(model));
    }

    // ---------------- 构造辅助 ----------------

    private static string ProfileJson(string texturesBase64)
        => "{\"id\":\"" + Uuid + "\",\"name\":\"CrimsonOR\",\"properties\":["
           + "{\"name\":\"textures\",\"value\":\"" + texturesBase64 + "\",\"signature\":\"ignored\"}]}";

    private static string TexturesBase64(string url, string? model = null)
    {
        var json = "{\"textures\":{\"SKIN\":{\"url\":\"" + url + "\""
                   + (model is null ? string.Empty : ",\"metadata\":{\"model\":\"" + model + "\"}")
                   + "}}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>够 <see cref="SkinAvatarService.IsUsableSkin"/> 读头的假 PNG：签名 + IHDR 尺寸。</summary>
    private static byte[] FakePng(int width, int height)
    {
        var bytes = new byte[64];
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(bytes, 0);
        bytes[8] = 0;
        bytes[9] = 0;
        bytes[10] = 0;
        bytes[11] = 13;
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        WriteBigEndian(bytes, 16, width);
        WriteBigEndian(bytes, 20, height);
        return bytes;
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
