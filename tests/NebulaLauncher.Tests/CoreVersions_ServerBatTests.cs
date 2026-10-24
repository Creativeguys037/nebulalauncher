using System.Net;
using System.Security.Cryptography;
using System.Text;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;
using NebulaLauncher.Services;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 服务端下载附带生成的 <c>Launch Server.bat</c>。
///
/// 这些用例盯的是「用户双击能不能用」而不是「文件存在」：
/// 编码（GB18030）、启动行（裸 <c>"java"</c>）、EULA 说明、<c>pause</c>、以及
/// 绝不覆盖用户手改过的脚本。
/// </summary>
public class CoreVersions_ServerBatTests
{
    private const string VersionJsonUrl = "https://piston-meta.mojang.com/v1/packages/aaaa/1.20.1.json";
    private const string ServerUrl = "https://piston-data.mojang.com/v1/objects/bbbb/server.jar";

    [Fact]
    public async Task 下载成功后生成启动脚本()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("servers", "1.20.1");
        var result = await DownloadAsync(temp, target);

        Assert.True(result.Success, result.Error);

        var scriptPath = Path.Combine(target, ServerDownloadService.LaunchScriptFileName);

        Assert.True(File.Exists(scriptPath), $"没有生成 {scriptPath}");
        Assert.Equal("Launch Server.bat", ServerDownloadService.LaunchScriptFileName);
    }

    [Fact]
    public async Task 启动脚本按GB18030写盘且中文不乱码()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("servers", "1.20.1");
        var result = await DownloadAsync(temp, target);

        Assert.True(result.Success, result.Error);

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(target, ServerDownloadService.LaunchScriptFileName));

        // 先排除 BOM：GB18030 没有 BOM，出现 EF BB BF 说明写成了 UTF-8。
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "脚本带了 UTF-8 BOM");

        // 关键判据：按 GB18030 解码后中文必须可读。用 UTF-8 存的脚本在这里会得到乱码。
        var text = Gb18030.GetString(bytes);

        Assert.Contains("EULA", text, StringComparison.Ordinal);
        Assert.Contains("eula=true", text, StringComparison.Ordinal);
        Assert.Contains("服务端已停止", text, StringComparison.Ordinal);
        Assert.Contains("内存不足", text, StringComparison.Ordinal);

        // 反向确认这不是「UTF-8 恰好也能被 GB18030 读出来」的巧合：
        // 「服务端」的 GB18030 字节序列不是合法 UTF-8，用 UTF-8 解码会得到替换字符。
        Assert.DoesNotContain('\uFFFD', text);

        var asUtf8 = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("服务端已停止", asUtf8, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 启动脚本含GC与内存参数和nogui与pause()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("servers", "1.20.1");
        var result = await DownloadAsync(temp, target);

        Assert.True(result.Success, result.Error);

        var text = await File.ReadAllTextAsync(
            Path.Combine(target, ServerDownloadService.LaunchScriptFileName),
            Gb18030);

        Assert.Contains("-XX:+UseG1GC", text, StringComparison.Ordinal);
        Assert.Contains("-Xmx6144M", text, StringComparison.Ordinal);
        Assert.Contains("-Xms1024M", text, StringComparison.Ordinal);
        Assert.Contains(" nogui", text, StringComparison.Ordinal);

        // pause 必须在最后一行：服务端崩了窗口要留住，用户才看得到报错。
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("pause", lines[^1]);
    }

    [Fact]
    public async Task 启动行用裸java命令并指向同名jar()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("servers", "1.20.1");
        var result = await DownloadAsync(temp, target);

        Assert.True(result.Success, result.Error);

        var text = await File.ReadAllTextAsync(
            Path.Combine(target, ServerDownloadService.LaunchScriptFileName),
            Gb18030);

        var launchLine = Assert.Single(
            text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains(" -jar ", StringComparison.Ordinal));

        // 裸命令 + 引号：绝对路径换台机器就失效，而这个脚本会跟着目录一起被拷走。
        Assert.StartsWith("\"java\" ", launchLine, StringComparison.Ordinal);

        // 启动行里不能出现盘符路径（echo 说明里那条示例不算，因为它不带 -jar）。
        Assert.DoesNotContain(":\\", launchLine, StringComparison.Ordinal);

        // 指向真实存在的那个 jar，不是 PCL 的 {Id}-server.jar 命名。
        Assert.Contains(" -jar server-1.20.1.jar ", launchLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 二次下载不覆盖用户手改过的脚本()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("servers", "1.20.1");
        var first = await DownloadAsync(temp, target);

        Assert.True(first.Success, first.Error);

        var scriptPath = Path.Combine(target, ServerDownloadService.LaunchScriptFileName);
        const string UserEdit = "@echo off\r\n\"C:\\Java\\jdk-17\\bin\\java.exe\" -Xmx2048M -jar server-1.20.1.jar nogui";
        await File.WriteAllTextAsync(scriptPath, UserEdit, Gb18030);

        // 第二次：jar 已存在且 SHA1 对得上，走 AlreadyExisted 分支。
        var second = await DownloadAsync(temp, target);

        Assert.True(second.Success, second.Error);
        Assert.True(second.AlreadyExisted);

        Assert.Equal(UserEdit, await File.ReadAllTextAsync(scriptPath, Gb18030));
    }

    [Fact]
    public async Task jar已存在但脚本缺失时补写脚本()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("servers", "1.20.1");
        Directory.CreateDirectory(target);

        // 模拟「本功能上线前下载过」的目录：只有 jar，没有脚本。
        var payload = Payload(8 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(target, "server-1.20.1.jar"), payload);

        using var handler = new BatRouteHandler()
            .Add(VersionJsonUrl, Encoding.UTF8.GetBytes(VersionJson(Sha1Of(payload), payload.Length)));

        using var client = new HttpClient(handler);
        using var service = new ServerDownloadService(
            new BatVersionService(),
            client,
            NullAppLog.Instance);

        var result = await service.DownloadAsync("1.20.1", target);

        Assert.True(result.Success, result.Error);
        Assert.True(result.AlreadyExisted);
        Assert.Equal(0, handler.Hits(ServerUrl));
        Assert.True(File.Exists(Path.Combine(target, ServerDownloadService.LaunchScriptFileName)));
    }

    [Fact]
    public void 脚本内容对缺失的入参有兜底()
    {
        // jar 名或版本号缺失时不能拼出 " -jar  nogui" 这种跑不起来的一行。
        var script = ServerDownloadService.BuildLaunchScript(string.Empty, string.Empty);

        Assert.Contains(" -jar server.jar ", script, StringComparison.Ordinal);
        Assert.Contains("title 服务端", script, StringComparison.Ordinal);
        Assert.DoesNotContain("  -jar", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 脚本行尾是CRLF()
    {
        // cmd.exe 能读 LF，但记事本打开会被挤成一行；而「右键编辑这个脚本」正是我们要用户做的事。
        var script = ServerDownloadService.BuildLaunchScript("server-1.20.1.jar", "1.20.1");

        Assert.Contains("\r\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", script.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void 启动脚本编码按系统ACP选择()
    {
        // 936（默认中文 Windows）→ GB18030；65001（开了 Beta UTF-8）→ UTF-8。
        // 判据不能是 Encoding.Default：.NET 8 里它恒为 UTF-8，936 的机器会被误判成 UTF-8，
        // 写出来的脚本在 cmd 里中文全是乱码。所以这里直接喂 ACP 值测选择函数。
        Assert.Equal(ServerDownloadService.Gb18030CodePage, ServerDownloadService.ChooseLaunchScriptEncoding(936).CodePage);
        Assert.Equal(ServerDownloadService.Utf8CodePage, ServerDownloadService.ChooseLaunchScriptEncoding(65001).CodePage);

        // 其它代码页（日文 932、西文 1252，以及取不到 ACP 时的 0）一律回落 GB18030。
        Assert.Equal(ServerDownloadService.Gb18030CodePage, ServerDownloadService.ChooseLaunchScriptEncoding(0).CodePage);
        Assert.Equal(ServerDownloadService.Gb18030CodePage, ServerDownloadService.ChooseLaunchScriptEncoding(932).CodePage);
        Assert.Equal(ServerDownloadService.Gb18030CodePage, ServerDownloadService.ChooseLaunchScriptEncoding(1252).CodePage);
    }

    [Fact]
    public void UTF8分支写出的脚本没有BOM()
    {
        var encoding = ServerDownloadService.ChooseLaunchScriptEncoding(ServerDownloadService.Utf8CodePage);
        var script = ServerDownloadService.BuildLaunchScript("server-1.20.1.jar", "1.20.1");

        // 走产品同一条落盘路径（File.WriteAllText + 选定编码），不是只测 GetBytes：
        // 编码对象的 Preamble 只有在真正写文件时才会落到文件头。
        using var temp = new TempDirectory();
        var path = temp.Combine(ServerDownloadService.LaunchScriptFileName);
        File.WriteAllText(path, script, encoding);

        var bytes = File.ReadAllBytes(path);

        // BOM 会让 cmd 把首行读成 '﻿@echo off'，报「不是内部或外部命令」，整个脚本从第一行起失效。
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "UTF-8 分支写出的脚本不能带 BOM");

        // 首行必须是纯 ASCII 的 @echo off——带 BOM 时这里会读到 EF BB BF 后面才是 '@echo'。
        Assert.Equal("@echo ", Encoding.ASCII.GetString(bytes, 0, 6));

        // 中文按 UTF-8 解码可读；这条同时证明分支真的换成了 UTF-8（GB18030 下会解出乱码）。
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        Assert.Contains("服务端已停止", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 复制按钮的命令与bat启动行完全一致()
    {
        const string Jar = "server-1.20.1.jar";

        var launchLine = Assert.Single(
            ServerDownloadService.BuildLaunchScript(Jar, "1.20.1")
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains(" -jar ", StringComparison.Ordinal));

        Assert.Equal(launchLine, ServerDownloadService.BuildLaunchCommand(Jar));

        // 「复制启动命令」按钮读的是 DownloadViewModel.ServerCommand：它必须取同一份字符串。
        // 曾经两处各拼一遍——bat 是 -Xmx6144M + G1GC，按钮是 -Xmx2G，同一件事两个答案。
        var previousLoaderKey = ScreenshotMode.LoaderKey;
        ScreenshotMode.LoaderKey = null;

        try
        {
            var viewModel = new DownloadViewModel
            {
                SelectedServerVersion = new ServerVersionRow(new ServerVersionInfo { Id = "1.20.1" }),
            };

            Assert.Equal(launchLine, viewModel.ServerCommand);
        }
        finally
        {
            ScreenshotMode.LoaderKey = previousLoaderKey;
        }
    }

    // ==================== 辅助 ====================

    private static async Task<ServerDownloadResult> DownloadAsync(TempDirectory temp, string target)
    {
        var payload = Payload(16 * 1024);

        using var handler = new BatRouteHandler()
            .Add(VersionJsonUrl, Encoding.UTF8.GetBytes(VersionJson(Sha1Of(payload), payload.Length)))
            .Add(ServerUrl, payload);

        using var client = new HttpClient(handler);
        using var service = new ServerDownloadService(
            new BatVersionService(),
            client,
            NullAppLog.Instance);

        return await service.DownloadAsync("1.20.1", target);
    }

    /// <summary>GB18030。测试进程同样要先注册代码页编码提供者。</summary>
    private static Encoding Gb18030
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(54936);
        }
    }

    private static string VersionJson(string sha1, long size) =>
        "{\"downloads\":{\"server\":{\"url\":\""
        + ServerUrl
        + "\",\"sha1\":\""
        + sha1
        + "\",\"size\":"
        + size.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "}}}";

    private static byte[] Payload(int size)
    {
        var payload = new byte[size];

        for (var i = 0; i < size; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "与服务端 jar 的官方校验算法一致")]
    private static string Sha1Of(byte[] payload) => Convert.ToHexString(SHA1.HashData(payload));

    /// <summary>按 URL 路由的内存响应；未注册的 URL 返回 404。</summary>
    private sealed class BatRouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

        public BatRouteHandler Add(string url, byte[] payload)
        {
            _routes[url] = payload;
            return this;
        }

        public int Hits(string url)
        {
            lock (_hits)
            {
                return _hits.TryGetValue(url, out var count) ? count : 0;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            lock (_hits)
            {
                _hits[url] = Hits(url) + 1;
            }

            if (!_routes.TryGetValue(url, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new StreamContent(new MemoryStream(payload, writable: false));
            content.Headers.ContentLength = payload.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>只提供 1.20.1 一个版本的清单服务。</summary>
    private sealed class BatVersionService : IVersionService
    {
        private readonly IReadOnlyList<RemoteVersion> _versions =
        [
            new RemoteVersion
            {
                Id = "1.20.1",
                Type = "release",
                Url = VersionJsonUrl,
                ReleaseTime = new DateTimeOffset(2023, 6, 12, 0, 0, 0, TimeSpan.Zero),
            },
        ];

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult(_versions);

        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionInfo>>(Array.Empty<VersionInfo>());

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionInfo?>(null);

        public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordLaunchAsync(string versionName, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
            LoaderKind loader,
            string minecraftVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LoaderVersion>>(Array.Empty<LoaderVersion>());
    }
}