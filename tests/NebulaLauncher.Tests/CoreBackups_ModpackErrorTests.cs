using System.IO.Compression;
using System.Text;
using NebulaLauncher.Core.Backups;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Tests;

/// <summary>
/// 导入整合包时「打不开」这一类错误的文案。
///
/// 判据是「用户拿到提示之后知道下一步做什么」，所以每条都断言了可执行的动作词
/// （解压 / 重新压缩成 zip），而不只是断言出现了「失败」两个字。
/// </summary>
public class CoreBackups_ModpackErrorTests
{
    [Fact]
    public async Task rar包给出解压重压zip的提示()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "整合包.rar");

        // rar 的真实文件头：ZipFile.OpenRead 会直接抛 InvalidDataException（英文的）。
        await File.WriteAllBytesAsync(archive, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x01, 0x02, 0x03]);

        var result = await Service(temp).ImportWithResultAsync(archive, "rar 包");

        Assert.False(result.Success);
        Assert.Contains("rar", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zip", result.Error!, StringComparison.OrdinalIgnoreCase);

        // 必须给出能做的动作，不能只说「不支持」。
        Assert.Contains("解压", result.Error!, StringComparison.Ordinal);

        // 不能把 .NET 的英文异常原文透传给用户。
        Assert.DoesNotContain("End of Central Directory", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidData", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 压缩包是7z格式时给出对应提示()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "整合包.7z");

        // 7z 文件头：37 7A BC AF 27 1C
        await File.WriteAllBytesAsync(archive, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);

        var result = await Service(temp).ImportWithResultAsync(archive, "7z 包");

        Assert.False(result.Success);
        Assert.Contains("7z", result.Error!, StringComparison.Ordinal);
        Assert.Contains("zip", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("解压", result.Error!, StringComparison.Ordinal);

        // 与 rar 的文案必须分开写：把 7z 说成 rar 会让用户按错的格式去找。
        Assert.DoesNotContain("rar 格式", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 损坏的zip仍然是通用提示()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "损坏的整合包.zip");
        await File.WriteAllTextAsync(archive, "这不是一个 zip 文件");

        var result = await Service(temp).ImportWithResultAsync(archive, "坏 zip");

        Assert.False(result.Success);

        // 扩展名是 zip，不能因为异常一样就误报成 rar。
        Assert.Contains("损坏", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("rar 格式", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 合法mrpack不受这次改动影响()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "pack.mrpack");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var index = zip.CreateEntry("modrinth.index.json");
            using var writer = new StreamWriter(index.Open(), new UTF8Encoding(false));

            await writer.WriteAsync("""
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.0.0",
              "name": "测试整合包",
              "files": [],
              "dependencies": { "minecraft": "1.20.1" }
            }
            """);
        }

        var result = await Service(temp).ImportWithResultAsync(archive, "正常包");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.InstanceDirectory);
    }

    [Theory]
    [InlineData("a.rar")]
    [InlineData("a.RAR")]
    [InlineData("a.Rar")]
    [InlineData("整合包.rar")]
    public void 扩展名判定不区分大小写(string fileName)
    {
        var message = ModpackService.DescribeOpenFailure(Path.Combine(@"C:\packs", fileName));

        Assert.Contains("rar", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a.7z")]
    [InlineData("a.7Z")]
    [InlineData("没有扩展名")]
    [InlineData("a.zip")]
    [InlineData("a.mrpack")]
    public void 其它扩展名走通用提示(string fileName)
    {
        var message = ModpackService.DescribeOpenFailure(Path.Combine(@"C:\packs", fileName));

        // 通用文案也要给出下一步（解压重压 zip），但绝不能断言成 rar 专用文案。
        Assert.Contains("压缩包", message, StringComparison.Ordinal);
        Assert.DoesNotContain("启动器不能直接处理 rar", message, StringComparison.Ordinal);
    }

    private static ModpackService Service(TempDirectory temp) =>
        new(
            new FakeLauncherPaths(temp.Path),
            new DownloadQueue(new HttpClient(new NoOpHandler())),
            NullAppLog.Instance);

    /// <summary>导入失败用例不该发出任何请求。</summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}