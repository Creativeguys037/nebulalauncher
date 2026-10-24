using System.Text;
using NebulaLauncher.Services;

namespace NebulaLauncher.Tests;

/// <summary>
/// 服务器图标 data URI 解码（<see cref="ServerFaviconDataUri"/>）。
///
/// 只测「字符串 + 字节」这一层：测试进程没有 Avalonia 渲染后端（CONVENTIONS §7.1），
/// Bitmap 构造留给应用运行期验证。图标来自远程服务器，缺字段 / 坏 base64 / 非 PNG
/// 都是常态，所以每个失败用例都断言「返回 false、out 为 null、不抛」。
/// </summary>
public class AppServers_FaviconTests
{
    /// <summary>真实 1×1 透明 PNG。固定字节而不读资源文件，避免测试依赖工作目录。</summary>
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private const string PngDataUri = "data:image/png;base64," + OnePixelPngBase64;

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void 合法1x1PNG_解出PNG字节()
    {
        Assert.True(ServerFaviconDataUri.TryGetPngBytes(PngDataUri, out var bytes));

        var png = Assert.IsType<byte[]>(bytes);
        Assert.Equal(PngMagic, png[..PngMagic.Length]);

        // 与直接解 base64 的结果逐字节一致：解码环节不能顺手改写载荷。
        Assert.Equal(Convert.FromBase64String(OnePixelPngBase64), png);
    }

    [Fact]
    public void null_返回false不抛()
    {
        Assert.False(ServerFaviconDataUri.TryGetPngBytes(null, out var bytes));
        Assert.Null(bytes);
    }

    [Theory]
    [InlineData("")]                                    // 空串
    [InlineData("   ")]                                 // 全是空白
    [InlineData("iVBORw0KGgo=")]                        // 裸 base64，缺 data:image/png;base64, 前缀
    [InlineData("data:image/png;base64,")]              // 有前缀没有载荷
    [InlineData("data:image/png;base64,这不是base64")]  // 含非 base64 字符
    [InlineData("data:image/jpeg;base64,AAAA")]         // MIME 不是 PNG
    [InlineData("data:image/png;base64,AAAA")]          // base64 合法但只有 3 字节，连文件头都不够
    public void 非法dataUri_返回false不抛(string dataUri)
    {
        Assert.False(ServerFaviconDataUri.TryGetPngBytes(dataUri, out var bytes));
        Assert.Null(bytes);
    }

    [Fact]
    public void base64解出非PNG字节_返回false()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text, not a png"));

        Assert.False(ServerFaviconDataUri.TryGetPngBytes("data:image/png;base64," + payload, out var bytes));
        Assert.Null(bytes);
    }

    [Fact]
    public void 前后空白与前缀大小写_仍能解码()
    {
        // 服务器把 JSON 字段直接转成字符串时偶尔带回车；前缀大小写不属于协议但不必拒绝。
        var dataUri = "  DATA:IMAGE/PNG;BASE64," + OnePixelPngBase64 + "\r\n";

        Assert.True(ServerFaviconDataUri.TryGetPngBytes(dataUri, out var bytes));
        Assert.NotNull(bytes);
    }

    [Fact]
    public void 超过体积上限_返回false()
    {
        // 文件头正确但整体超限：上限判断必须发生在交给 Skia 之前。
        var oversized = new byte[ServerFaviconDataUri.MaxPngBytes + 1];
        PngMagic.CopyTo(oversized, 0);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(oversized);

        Assert.False(ServerFaviconDataUri.TryGetPngBytes(dataUri, out var bytes));
        Assert.Null(bytes);
    }

    [Fact]
    public void IHDR声明超大尺寸_返回false()
    {
        // 体积很小、PNG 结构正确，但 IHDR 声明 10000×10000：Skia 会按声明尺寸分配内存，
        // 必须在字节层拒绝。
        var png = Convert.FromBase64String(OnePixelPngBase64);
        WriteBigEndian(png, 16, 10000);
        WriteBigEndian(png, 20, 10000);

        var dataUri = "data:image/png;base64," + Convert.ToBase64String(png);

        Assert.False(ServerFaviconDataUri.TryGetPngBytes(dataUri, out var bytes));
        Assert.Null(bytes);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
