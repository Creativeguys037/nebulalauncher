using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤站凭据库。密码只应加密落盘，自建纹理账本只增删自己的 tid，
/// 且换一个实例也要从磁盘读回来——否则“只删自己上传的”会退化成内存里的假承诺。
/// </summary>
public sealed class CoreSkinning_SkinSiteCredentialStoreTests
{
    private const string Site = "https://littleskin.cn";

    [Fact]
    public async Task 保存后换实例仍能读回密码与自建纹理账本()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);

        Assert.True(await store.SaveAsync(Site, "Me", "hunter2", CancellationToken.None));
        await store.RememberOwnedTextureAsync(Site, "Me", 11, CancellationToken.None);
        await store.RememberOwnedTextureAsync(Site, "Me", 12, CancellationToken.None);

        var reopened = NewStore(temp);
        var credential = await reopened.FindAsync("HTTPS://LITTLESKIN.CN/", " me ", CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(Site, credential!.BaseAddress);
        Assert.Equal("Me", credential.Username);
        Assert.Equal("hunter2", credential.Password);
        Assert.Equal([11, 12], credential.OwnedTextureTids);
    }

    [Fact]
    public async Task 密码不以明文写进文件()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);

        await store.SaveAsync(Site, "Me", "hunter2", CancellationToken.None);

        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains("b64:", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空密码不覆盖已经保存的密码()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await store.SaveAsync(Site, "Me", "hunter2", CancellationToken.None);

        Assert.True(await store.SaveAsync(Site, "Me", string.Empty, CancellationToken.None));

        var credential = await store.FindAsync(Site, "Me", CancellationToken.None);
        Assert.Equal("hunter2", credential!.Password);
    }

    [Fact]
    public async Task 重复记录同一个tid不会重复记账()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await store.SaveAsync(Site, "Me", "pw", CancellationToken.None);

        await store.RememberOwnedTextureAsync(Site, "Me", 7, CancellationToken.None);
        await store.RememberOwnedTextureAsync(Site, "Me", 7, CancellationToken.None);

        var credential = await store.FindAsync(Site, "Me", CancellationToken.None);
        Assert.Equal([7], credential!.OwnedTextureTids);
    }

    [Fact]
    public async Task 删除成功后从账本移除tid()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await store.SaveAsync(Site, "Me", "pw", CancellationToken.None);
        await store.RememberOwnedTextureAsync(Site, "Me", 7, CancellationToken.None);

        await store.ForgetOwnedTextureAsync(Site, "Me", 7, CancellationToken.None);

        var credential = await store.FindAsync(Site, "Me", CancellationToken.None);
        Assert.Empty(credential!.OwnedTextureTids);
    }

    [Fact]
    public async Task 没有凭据记录时不会凭空创建纹理账本()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);

        await store.RememberOwnedTextureAsync(Site, "Me", 7, CancellationToken.None);

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.Null(await store.FindAsync(Site, "Me", CancellationToken.None));
    }

    [Fact]
    public async Task 忘记站点会删掉密码和该站点的纹理账本()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await store.SaveAsync(Site, "Me", "pw", CancellationToken.None);
        await store.RememberOwnedTextureAsync(Site, "Me", 7, CancellationToken.None);

        await store.ForgetAsync(Site, "Me", CancellationToken.None);

        Assert.Null(await store.FindAsync(Site, "Me", CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 列表按保存时间倒序且不同站点互不覆盖()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await store.SaveAsync("https://a.example", "A", "pw-a", CancellationToken.None);
        await Task.Delay(10);
        await store.SaveAsync("https://b.example", "B", "pw-b", CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Equal("B", list[0].Username);
        Assert.Equal("A", list[1].Username);
    }

    [Fact]
    public async Task 文件损坏时按未登录处理且不抛异常()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await File.WriteAllTextAsync(store.FilePath, "{ not json");

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.Null(await store.FindAsync(Site, "Me", CancellationToken.None));
    }

    [Fact]
    public async Task 免密令牌加密落盘且换实例能读回()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(72);

        Assert.True(await store.SaveOAuthAsync(
            Site, "Me", "access-1", "refresh-1", expiresAt, CancellationToken.None));

        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain("access-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", json, StringComparison.Ordinal);

        var credential = await NewStore(temp).FindAsync(Site, "Me", CancellationToken.None);
        Assert.NotNull(credential);
        Assert.True(credential!.HasOAuthToken);
        Assert.Equal("access-1", credential.AccessToken);
        Assert.Equal("refresh-1", credential.RefreshToken);
        Assert.Equal(expiresAt, credential.AccessTokenExpiresAt);
        Assert.Equal(string.Empty, credential.Password);
    }

    [Fact]
    public async Task 保存免密令牌不会清掉已保存的密码()
    {
        using var temp = new TempDirectory();
        var store = NewStore(temp);
        await store.SaveAsync(Site, "Me", "hunter2", CancellationToken.None);

        await store.SaveOAuthAsync(
            Site, "Me", "access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        var credential = await store.FindAsync(Site, "Me", CancellationToken.None);
        Assert.Equal("hunter2", credential!.Password);
        Assert.Equal("access-1", credential.AccessToken);
    }

    private static SkinSiteCredentialStore NewStore(TempDirectory temp)
    {
        var paths = new FakeLauncherPaths(temp.Path);
        var platform = new FakePlatformService { SupportsSecureTokenStorage = false };
        return new SkinSiteCredentialStore(paths, platform, NullAppLog.Instance);
    }
}
