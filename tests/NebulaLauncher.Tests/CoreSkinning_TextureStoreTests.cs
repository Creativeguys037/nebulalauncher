using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using NebulaLauncher.Core.Accounts;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.Tests;

/// <summary>
/// 皮肤纹理落盘缓存（票 54）。
///
/// 锁的是「断网还能不能显示上次看过的皮肤」这条验收背后的存储规则：
/// 落盘要能读回、hash 变了必须是新文件、半截写入（只有 .tmp 或缺元数据）不算命中、
/// Invalidate 只清自己的分区。这些规则错一条，断网预览就会退化成默认皮肤或读到坏图。
/// </summary>
public sealed class CoreSkinning_TextureStoreTests
{
    private const string Uuid = "493e32e14248466d97fd154e215f2201";

    /// <summary>皮肤站纹理地址的真实形态：末段是 64 位十六进制。</summary>
    private const string TextureHash = "4200a3d16f481b45daddbb1c9305911ed7aea3890c673607e3488e647cdb9b30";

    private const string TextureUrl = "https://textures.minecraft.net/texture/" + TextureHash;

    // ---------------- 落盘与命中 ----------------

    [Fact]
    public async Task 落盘后按当前元数据能读回同一张图()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);
        var png = PngHeader(64, 64);

        var saved = await store.SaveAsync(Uuid, TextureHash, png, new SkinTextureMeta(TextureHash, "classic", TextureUrl));

        Assert.NotNull(saved);
        Assert.True(File.Exists(saved));
        Assert.Equal(saved, store.TryGetCurrentPath(Uuid));
        Assert.Equal(saved, store.TryGetPath(Uuid, TextureHash));
        Assert.Equal(File.ReadAllBytes(saved!), png);
    }

    [Fact]
    public async Task 同分区换hash时新旧各自成文件且当前指针跟着走()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        var oldHash = new string('a', 64);
        var newHash = new string('b', 64);
        var oldPath = await store.SaveAsync(Uuid, oldHash, PngHeader(64, 64), new SkinTextureMeta(oldHash, "classic", "https://e.invalid/texture/" + oldHash));
        var newPath = await store.SaveAsync(Uuid, newHash, PngHeader(64, 32), new SkinTextureMeta(newHash, "classic", "https://e.invalid/texture/" + newHash));

        // 失效策略是「hash 变即换文件，不设过期时间」：旧文件留着不清，当前指针指向新的。
        Assert.NotEqual(oldPath, newPath);
        Assert.True(File.Exists(oldPath!));
        Assert.Equal(newPath, store.TryGetCurrentPath(Uuid));
    }

    [Fact]
    public async Task 只有PNG没有元数据时不算命中()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        // meta 传 null：PNG 落了盘但没有提交标记。TryGetPath 按 hash 能找到，
        // 而 TryGetCurrentPath（账户线的命中判定）必须返回 null——否则半截写入会被当有效缓存。
        await store.SaveAsync(Uuid, TextureHash, PngHeader(64, 64), meta: null);

        Assert.NotNull(store.TryGetPath(Uuid, TextureHash));
        Assert.Null(store.TryGetCurrentPath(Uuid));
        Assert.Null(store.TryGetTidPath("littleskin.cn", 1));
    }

    [Fact]
    public void 残留的tmp文件不被当缓存读()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        // 模拟进程在 File.Move 之前退出：目标 PNG 不存在，只有临时文件。
        var directory = store.RootOf(Uuid);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, TextureHash + ".png.deadbeef.tmp"), PngHeader(64, 64));

        Assert.Null(store.TryGetPath(Uuid, TextureHash));
        Assert.Null(store.TryGetCurrentPath(Uuid));
    }

    [Fact]
    public async Task 非PNG字节不落盘()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        // 截断或根本不是皮肤的响应（HTML 错误页）一旦落盘就会被反复读出来，写盘前必须拦。
        var saved = await store.SaveAsync(Uuid, TextureHash, Encoding.UTF8.GetBytes("<html>429</html>"), new SkinTextureMeta(TextureHash, "classic", TextureUrl));

        Assert.Null(saved);
        Assert.Null(store.TryGetPath(Uuid, TextureHash));
    }

    // ---------------- 分区与清理 ----------------

    [Fact]
    public async Task Invalidate只删自己的分区()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        await store.SaveAsync(Uuid, TextureHash, PngHeader(64, 64), new SkinTextureMeta(TextureHash, "classic", TextureUrl));
        var sibling = new string('c', 64);
        await store.SaveAsync("other-account", sibling, PngHeader(64, 64), new SkinTextureMeta(sibling, "classic", "https://e.invalid/texture/" + sibling));
        var defaultSkin = Path.Combine(store.Root, "default-steve.png");
        Directory.CreateDirectory(store.Root);
        File.WriteAllBytes(defaultSkin, PngHeader(64, 64));

        store.Invalidate(Uuid);

        Assert.False(Directory.Exists(store.RootOf(Uuid)));
        Assert.Null(store.TryGetCurrentPath(Uuid));
        Assert.NotNull(store.TryGetCurrentPath("other-account"));
        Assert.True(File.Exists(defaultSkin));
    }

    [Fact]
    public async Task 公开库按站点分区且tid旁记独立()
    {
        using var temp = new TempDirectory();
        var store = new SkinTextureStore(new FakeLauncherPaths(temp.Path), NullAppLog.Instance);

        var saved = await store.SaveTidAsync("littleskin.cn", 917742, TextureHash, PngHeader(64, 64), new SkinTextureMeta(TextureHash, "slim", TextureUrl));

        Assert.Equal(saved, store.TryGetTidPath("littleskin.cn", 917742));
        // 别的站点、别的 tid 都不能串到这份缓存上。
        Assert.Null(store.TryGetTidPath("other.site", 917742));
        Assert.Null(store.TryGetTidPath("littleskin.cn", 1));
    }

    // ---------------- 命名规则 ----------------

    [Theory]
    [InlineData("https://textures.minecraft.net/texture/" + TextureHash, TextureHash)]
    [InlineData("https://littleskin.cn/textures/ABCDEF0123456789ABCDEF0123456789", "abcdef0123456789abcdef0123456789")]
    [InlineData("https://e.invalid/skins/crimson.png", null)]
    [InlineData("https://e.invalid/texture/short", null)]
    public void 纹理地址末段是长十六进制时直接用它(string url, string? expected)
    {
        var hash = SkinTextureStore.HashOf(url);

        if (expected is null)
        {
            // 认不出站点 hash 就退回整条 URL 的 sha256：64 位小写十六进制，且同 URL 稳定。
            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9a-f]{64}$", hash);
            Assert.Equal(hash, SkinTextureStore.HashOf(url));
        }
        else
        {
            Assert.Equal(expected, hash);
        }
    }

    [Theory]
    [InlineData("LittleSkin.CN", "littleskin.cn")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("..", "unknown")]
    [InlineData("  ", "unknown")]
    public void 分区名归一_小写_非法字符换下划线_点号逃逸被挡(string raw, string expected)
    {
        Assert.Equal(expected, SkinTextureStore.NormalizeScope(raw));
    }

    // ---------------- 离线回归（票 54 验收一） ----------------

    [Fact]
    public async Task 断网时先前进过的账户皮肤仍能取到且不再发请求()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var account = new Account
        {
            Type = AccountType.Microsoft,
            Username = "CrimsonOR",
            Uuid = Uuid,
        };

        // 预热：profile + 纹理都正常回。
        var warm = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("/profile/", StringComparison.Ordinal)
                ? Json(ProfileJson(TextureUrl))
                : Png(PngHeader(64, 64)));
        using (var service = new SkinAvatarService(paths, NullAppLog.Instance, new HttpClient(warm)))
        {
            var first = await service.GetAvatarPathAsync(account);
            Assert.NotNull(first);
            Assert.Equal(2, warm.Requests.Count);
        }

        // 断网：一律抛 HttpRequestException。命中落盘缓存时根本走不到网络。
        var offline = new RecordingHandler(_ => throw new HttpRequestException("offline"));
        using (var service = new SkinAvatarService(paths, NullAppLog.Instance, new HttpClient(offline)))
        {
            var second = await service.GetAvatarPathAsync(account);

            Assert.NotNull(second);
            Assert.True(File.Exists(second));
            Assert.Empty(offline.Requests);
        }
    }

    [Fact]
    public async Task 断网且没有落盘缓存时回落默认皮肤()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var account = new Account { Type = AccountType.Microsoft, Username = "CrimsonOR", Uuid = Uuid };
        var offline = new RecordingHandler(_ => throw new HttpRequestException("offline"));

        using var service = new SkinAvatarService(paths, NullAppLog.Instance, new HttpClient(offline));
        var path = await service.GetAvatarPathAsync(account);

        // 回落也是「有图可显示」：内嵌默认皮肤解到缓存根下。
        Assert.NotNull(path);
        Assert.EndsWith("default-steve.png", path, StringComparison.Ordinal);
    }

    // ---------------- 发布自检（票 54 新增的第 8 条落盘通路） ----------------

    [Fact]
    public async Task 发布自检覆盖皮肤纹理落盘通路()
    {
        // 指定工作目录：默认走系统临时目录，测试进程的沙箱未必有写权限。
        using var temp = new TempDirectory();
        var lines = await JsonSelfCheck.RunAsync(temp.Path);

        // 自检是裁剪产物上唯一能验落盘通路的关卡：新通路不加进来，AOT 关掉反射后也发现不了。
        Assert.Contains(lines, line => string.Equals(line, "json-selfcheck.OK.skins.roundTrip", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("json-selfcheck.FAIL", StringComparison.Ordinal));
        Assert.Contains(lines, line => string.Equals(line, "json-selfcheck.summary=8/8", StringComparison.Ordinal));
    }

    // ---------------- 构造辅助 ----------------

    private static string ProfileJson(string textureUrl)
    {
        var textures = "{\"textures\":{\"SKIN\":{\"url\":\"" + textureUrl + "\"}}}";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(textures));
        return "{\"id\":\"" + Uuid + "\",\"name\":\"CrimsonOR\",\"properties\":["
               + "{\"name\":\"textures\",\"value\":\"" + base64 + "\"}]}";
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Png(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    /// <summary>只要 PNG 签名 + IHDR 的宽高，够 <see cref="SkinAvatarService.IsUsableSkin"/> 校验用。</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[29];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    /// <summary>记录每个请求的假 handler；响应工厂抛异常时按网络失败处理（与真实断网一致）。</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }
}
