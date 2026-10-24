using System.Net;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Tests;

/// <summary>
/// authlib-injector 注入：jar 缓存、清单解析、SHA-256 校验、prefetch 字节一致性、
/// 参数形态与 GameLauncher 的接入顺序。
///
/// 网络全部由假 <see cref="HttpMessageHandler"/> 承担，磁盘全部落在临时目录，
/// 因此用例不联网、不依赖本机装了几个 Java、也不起 JVM。
/// </summary>
public sealed class CoreLaunch_AuthlibInjectorTests
{
    private const string ApiRoot = "https://littleskin.cn/api/yggdrasil";
    private const string Version = "1.2.8";
    private const string DownloadUrl = "https://authlib-injector.yushi.moe/artifact/56/authlib-injector-1.2.8.jar";

    // ==================== latest.json 解析 ====================

    [Fact]
    public void ParseRelease_ReadsVersionDownloadUrlAndChecksum()
    {
        var payload = Encoding.UTF8.GetBytes(
            $$"""
              {
                "build_number": 56,
                "version": "{{Version}}",
                "release_time": "2026-07-11T17:14:13Z",
                "download_url": "{{DownloadUrl}}",
                "checksums": { "sha256": "9c7f4343e6c82034958ffb48c14a2cb0c85928be7283103ce17da00c6d5a7b10" }
              }
              """);

        var release = AuthlibInjectorService.ParseRelease(payload);

        Assert.NotNull(release);
        Assert.Equal(Version, release!.Version);
        Assert.Equal(DownloadUrl, release.DownloadUrl);
        Assert.Equal("9c7f4343e6c82034958ffb48c14a2cb0c85928be7283103ce17da00c6d5a7b10", release.Sha256);
    }

    [Theory]
    [InlineData("""{"version":"1.2.8"}""")]                                   // 缺 download_url
    [InlineData("""{"download_url":"https://x/a.jar"}""")]                    // 缺 version
    [InlineData("""{"version":"1/2","download_url":"https://x/a.jar"}""")]    // 版本号含路径分隔符
    [InlineData("""{"version":"1.2.8","download_url":"ftp://x/a.jar"}""")]    // scheme 不支持
    [InlineData("""{"version":"1.2.8","download_url":"not a url"}""")]        // 不是绝对地址
    [InlineData("not json")]                                                  // 完全不是 JSON
    public void ParseRelease_RejectsMalformedPayload(string payload)
    {
        Assert.Null(AuthlibInjectorService.ParseRelease(Encoding.UTF8.GetBytes(payload)));
    }

    // ==================== 下载与缓存 ====================

    [Fact]
    public async Task EnsureInjectorAsync_DownloadsJarIntoVersionedCache()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer();
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var jar = await service.EnsureInjectorAsync(ApiRoot);

        var expected = Path.Combine(paths.CacheDirectory, "authlib-injector", $"authlib-injector-{Version}.jar");
        Assert.Equal(expected, jar);
        Assert.True(File.Exists(expected));
        Assert.Equal(server.JarBytes, await File.ReadAllBytesAsync(expected));
        Assert.Equal(1, server.JarRequests);

        // 清单副本落盘：用户排查「为什么用的是旧版本」时需要它。
        Assert.True(File.Exists(Path.Combine(paths.CacheDirectory, "authlib-injector", "latest.json")));
    }

    [Fact]
    public async Task EnsureInjectorAsync_CachedJarIsNotDownloadedAgain()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer();
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var first = await service.EnsureInjectorAsync(ApiRoot);
        var second = await service.EnsureInjectorAsync(ApiRoot);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        // 第二次仍要读清单（要判断有没有新版本），但不能重新下载同一个 jar。
        Assert.Equal(1, server.JarRequests);
        Assert.Equal(2, server.MetadataRequests);
    }

    [Fact]
    public async Task EnsureInjectorAsync_Sha256Mismatch_DeletesFileAndFails()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer
        {
            // 清单声明的哈希与实际内容不符：模拟镜像返回了被截断 / 被替换的 jar。
            Sha256 = new string('0', 64),
        };

        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var jar = await service.EnsureInjectorAsync(ApiRoot);

        Assert.Null(jar);
        Assert.NotNull(service.LastError);
        Assert.Contains("SHA-256", service.LastError, StringComparison.Ordinal);

        var directory = Path.Combine(paths.CacheDirectory, "authlib-injector");
        Assert.False(File.Exists(Path.Combine(directory, $"authlib-injector-{Version}.jar")));

        // 临时文件也不能留：留着会被下一次误当成「已下载」。
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
    }

    [Fact]
    public async Task EnsureInjectorAsync_MetadataUnavailable_FallsBackToCachedJar()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var directory = Path.Combine(paths.CacheDirectory, "authlib-injector");
        Directory.CreateDirectory(directory);

        // 只有一个旧版本 jar，清单拉不到时应当直接用它。
        var cached = Path.Combine(directory, "authlib-injector-1.2.5.jar");
        await File.WriteAllBytesAsync(cached, [0x50, 0x4b, 0x03, 0x04]);

        var server = new AuthlibServer { MetadataFails = true };
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var jar = await service.EnsureInjectorAsync(ApiRoot);

        Assert.Equal(cached, jar);
        Assert.Equal(0, server.JarRequests);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task EnsureInjectorAsync_MetadataUnavailableWithoutCache_FailsInChinese()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer { MetadataFails = true };
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var jar = await service.EnsureInjectorAsync(ApiRoot);

        Assert.Null(jar);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
        Assert.Contains("authlib-injector", service.LastError!, StringComparison.Ordinal);
    }

    // ==================== 注入参数 ====================

    [Fact]
    public async Task BuildInjectionAsync_PrefetchedIsBase64OfRawResponseBytes()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer();
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var injection = await service.BuildInjectionAsync(ApiRoot + "/");

        Assert.NotNull(injection);
        // 传进来的尾斜杠必须被规范化掉：injector 会拿它拼 "/authserver/... "。
        Assert.Equal(ApiRoot, injection!.ApiRoot);

        var prefetched = ReadTokenValue(injection.JvmArguments, "-Dauthlibinjector.yggdrasil.prefetched=");
        Assert.NotNull(prefetched);

        // 逐字节相等，而不是「字符串再编码后相等」：服务器可能返回非 ASCII 或压缩前的原始字节。
        Assert.Equal(server.PrefetchBytes, Convert.FromBase64String(prefetched!));
    }

    [Fact]
    public async Task BuildInjectionAsync_ProducesAuthlibJvmArgumentsInOrder()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer();
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var injection = await service.BuildInjectionAsync(ApiRoot);

        Assert.NotNull(injection);

        // 前三项是与 PCL2 原文（ModLaunch.vb:1282-1284）一一对应的三参数形态；
        // Windows 上再追加一项信任库类型（ModLaunch.vb:1277），所以 token 数是 3 或 4。
        var tokens = injection!.JvmArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expectedCount = OperatingSystem.IsWindows() ? 4 : 3;

        Assert.Equal(expectedCount, tokens.Length);
        Assert.StartsWith("-javaagent:", tokens[0], StringComparison.Ordinal);
        Assert.EndsWith("=" + ApiRoot, tokens[0], StringComparison.Ordinal);
        Assert.StartsWith("-Dauthlibinjector.yggdrasil.prefetched=", tokens[1], StringComparison.Ordinal);
        Assert.Equal("-Dauthlibinjector.side=client", tokens[2]);

        if (OperatingSystem.IsWindows())
        {
            // WINDOWS-ROOT 只在 Windows 合法：别的平台上 JVM 会 KeyStoreException 拒绝启动。
            Assert.Equal(AuthlibInjectorService.WindowsRootTrustStoreOption, tokens[3]);
        }

        // 缓存目录没有空格时不能加引号：多余引号会被 JVM 当成路径的一部分。
        Assert.DoesNotContain("\"", tokens[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\plain\authlib-injector-1.2.8.jar", @"C:\plain\authlib-injector-1.2.8.jar")]
    [InlineData(@"C:\with space\authlib-injector-1.2.8.jar", @"""C:\with space\authlib-injector-1.2.8.jar""")]
    [InlineData(@"""C:\already\quoted.jar""", @"""C:\already\quoted.jar""")]
    [InlineData("", "")]
    public void QuoteIfNeeded_QuotesOnlyPathsContainingSpaces(string path, string expected)
    {
        Assert.Equal(expected, AuthlibInjectorService.QuoteIfNeeded(path));
    }

    [Fact]
    public async Task BuildInjectionAsync_JarPathWithSpace_IsQuoted()
    {
        using var temp = new TempDirectory();

        // 启动器根目录带空格：缓存 jar 的绝对路径就会带空格，这是 Windows 上最常见的踩坑场景。
        var paths = new FakeLauncherPaths(Path.Combine(temp.Path, "launcher root"));
        var server = new AuthlibServer();
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var injection = await service.BuildInjectionAsync(ApiRoot);

        Assert.NotNull(injection);
        Assert.Contains("launcher root", injection!.JarPath, StringComparison.Ordinal);
        Assert.StartsWith("-javaagent:\"", injection.JvmArguments, StringComparison.Ordinal);
        Assert.Contains($"{injection.JarPath}\"={ApiRoot}", injection.JvmArguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("littleskin.cn/api/yggdrasil")]
    [InlineData("ftp://littleskin.cn/api/yggdrasil")]
    [InlineData("http:///no-host")]
    public async Task BuildInjectionAsync_InvalidApiRoot_FailsWithoutNetwork(string apiRoot)
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer();
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var injection = await service.BuildInjectionAsync(apiRoot);

        Assert.Null(injection);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
        Assert.Equal(0, server.MetadataRequests);
        Assert.Equal(0, server.PrefetchRequests);
    }

    [Fact]
    public async Task BuildInjectionAsync_PrefetchFailure_ReturnsNullWithChineseError()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        var server = new AuthlibServer { PrefetchStatus = HttpStatusCode.NotFound };
        using var client = new HttpClient(server);
        var service = CreateService(paths, client);

        var injection = await service.BuildInjectionAsync(ApiRoot);

        Assert.Null(injection);
        Assert.Contains("第三方验证元数据", service.LastError!, StringComparison.Ordinal);
        Assert.Contains("404", service.LastError!, StringComparison.Ordinal);
    }

    // ==================== GameLauncher 接入 ====================

    [Fact]
    public void ComposeJvmArguments_PutsInjectionFirstAndGcLast()
    {
        // 故意用带空格与反斜杠的 Windows 路径：CmlLib 的命令行解析器会把反斜杠当转义符，
        // 注入片段必须走自己的切分逻辑，路径一个字都不能少。
        const string injection =
            "-javaagent:\"D:\\launcher root\\cache\\authlib-injector-1.2.8.jar\"=https://a"
            + " -Dauthlibinjector.side=client";
        const string user = "-Duser.flag=1 -XX:+UseSerialGC";

        var merged = GameLauncher.ComposeJvmArguments(injection, user, GcKind.G1, 21);

        Assert.Equal(
            "-javaagent:D:\\launcher root\\cache\\authlib-injector-1.2.8.jar=https://a",
            merged[0]);
        Assert.Equal("-Dauthlibinjector.side=client", merged[1]);
        Assert.Equal("-Duser.flag=1", merged[2]);

        // 用户的旧 GC 必须先被清掉，启动器选的 GC 只能出现在用户参数之后。
        Assert.DoesNotContain(merged, argument => argument.Contains("UseSerialGC", StringComparison.Ordinal));
        Assert.Contains(merged, argument => argument.Contains("UseG1GC", StringComparison.Ordinal));

        var list = merged.ToList();
        Assert.True(
            list.FindIndex(argument => argument.Contains("UseG1GC", StringComparison.Ordinal))
            > list.IndexOf("-Duser.flag=1"),
            string.Join(" ", merged));
    }

    [Fact]
    public void ComposeJvmArguments_WithoutInjection_KeepsUserArgumentsFirst()
    {
        var merged = GameLauncher.ComposeJvmArguments(null, "-Duser.flag=1", GcKind.Auto, 17);

        Assert.Equal("-Duser.flag=1", merged[0]);
        Assert.DoesNotContain(merged, argument => argument.Contains("javaagent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameLauncher_OfflineAccount_DoesNotTouchInjector()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        FakeVersionDirectory.Write(paths.VersionsDirectory, "1.20.1", inheritsFrom: null);

        var injector = new StubInjector();
        var launcher = CreateLauncher(paths, injector);

        // 「查看启动命令」这条路径一定会走到参数装配；无论后面 CmlLib 能否构建成功，
        // 注入服务都不该被非第三方验证账户调用。
        await launcher.BuildCommandLineAsync(CreateOptions(AccountType.Offline));

        Assert.Equal(0, injector.BuildCalls);
        Assert.Equal(0, injector.EnsureCalls);
    }

    [Fact]
    public async Task GameLauncher_AuthlibAccount_InjectionFailureBlocksLaunch()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        FakeVersionDirectory.Write(paths.VersionsDirectory, "1.20.1", inheritsFrom: null);

        var injector = new StubInjector
        {
            LastError = "authlib-injector 1.2.8 的 SHA-256 校验不匹配。",
            Result = null,
        };

        var launcher = CreateLauncher(paths, injector);

        var result = await launcher.LaunchAsync(CreateOptions(AccountType.Authlib));

        Assert.False(result.Started);
        Assert.Equal(1, injector.BuildCalls);
        Assert.NotNull(result.FailureReason);
        Assert.StartsWith("第三方验证注入失败：", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("SHA-256", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameLauncher_AuthlibAccountWithoutServerUrl_SkipsInjection()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        FakeVersionDirectory.Write(paths.VersionsDirectory, "1.20.1", inheritsFrom: null);

        var injector = new StubInjector();
        var launcher = CreateLauncher(paths, injector);

        var options = CreateOptions(AccountType.Authlib, authServerUrl: null);

        // 没有 API 根就没法注入；这条路径不调用注入服务，也就不会把错误归咎于注入。
        await launcher.BuildCommandLineAsync(options);

        Assert.Equal(0, injector.BuildCalls);
    }

    [Fact]
    public async Task GameLauncher_AuthlibAccount_BuildsCommandLineWithAgent()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new StubInjector();
        var launcher = CreateLauncher(paths, injector);

        var commandLine = await launcher.BuildCommandLineAsync(CreateOptions(AccountType.Authlib));

        Assert.False(string.IsNullOrWhiteSpace(commandLine));
        Assert.Contains("-javaagent:", commandLine, StringComparison.Ordinal);
        Assert.Contains("authlib-injector-1.2.8.jar", commandLine, StringComparison.Ordinal);
        // javaagent 必须排在主类之前，否则 JVM 忽略它。
        Assert.True(
            commandLine.IndexOf("-javaagent:", StringComparison.Ordinal) <
            commandLine.IndexOf("net.minecraft.client.main.Main", StringComparison.Ordinal),
            commandLine);
    }

    [Fact]
    public async Task ExportLaunchScript_ContainsInjectionAndMasksAccessToken()
    {
        using var temp = new TempDirectory();
        var paths = new FakeLauncherPaths(temp.Path);
        WriteLaunchableVersion(paths.VersionsDirectory, "1.20.1");

        var injector = new StubInjector();
        var launcher = CreateLauncher(paths, injector);
        var target = Path.Combine(temp.Path, "run.bat");

        var path = await launcher.ExportLaunchScriptAsync(CreateOptions(AccountType.Authlib), target);

        Assert.NotNull(path);
        var content = await File.ReadAllTextAsync(path!);

        // 导出的脚本必须能直接跑：注入参数在里面，而令牌已经被打码。
        Assert.Contains("-javaagent:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", content, StringComparison.Ordinal);
        Assert.Contains(new string('*', "token-value".Length), content, StringComparison.Ordinal);
    }

    // ==================== 辅助 ====================

    /// <summary>写一份 CmlLib 能据此拼出完整命令行的版本 JSON（无库、无资源文件，纯离线）。</summary>
    private static void WriteLaunchableVersion(string versionsRoot, string name)
    {
        var directory = Path.Combine(versionsRoot, name);
        Directory.CreateDirectory(directory);

        var json = $$"""
                     {
                       "id": "{{name}}",
                       "type": "release",
                       "mainClass": "net.minecraft.client.main.Main",
                       "assets": "5",
                       "assetIndex": {
                         "id": "5",
                         "sha1": "0000000000000000000000000000000000000000",
                         "size": 0,
                         "totalSize": 0,
                         "url": "https://example.invalid/5.json"
                       },
                       "downloads": {
                         "client": {
                           "sha1": "0000000000000000000000000000000000000000",
                           "size": 0,
                           "url": "https://example.invalid/client.jar"
                         }
                       },
                       "libraries": [],
                       "arguments": {
                         "game": [
                           "--username", "${auth_player_name}",
                           "--version", "${version_name}",
                           "--gameDir", "${game_directory}",
                           "--assetsDir", "${assets_root}",
                           "--assetIndex", "${assets_index_name}",
                           "--uuid", "${auth_uuid}",
                           "--accessToken", "${auth_access_token}"
                         ],
                         "jvm": []
                       },
                       "javaVersion": { "component": "jdk-17", "majorVersion": 17 }
                     }
                     """;

        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    private static AuthlibInjectorService CreateService(FakeLauncherPaths paths, HttpClient client)
    {
        var queue = new DownloadQueue(client, NullAppLog.Instance);
        return new AuthlibInjectorService(paths, queue, httpFactory: null, log: NullAppLog.Instance, httpClient: client);
    }

    private static GameLauncher CreateLauncher(FakeLauncherPaths paths, IAuthlibInjectorService injector) =>
        new(
            paths,
            NullAppLog.Instance,
            new StubJavaService(),
            new StubVersionService(),
            content: null,
            platform: null,
            authlib: injector);

    private static LaunchOptions CreateOptions(AccountType type, string? authServerUrl = ApiRoot) =>
        new()
        {
            Account = new Account
            {
                Type = type,
                Username = "player",
                Uuid = "0123456789abcdef0123456789abcdef",
                AccessToken = "token-value",
                AuthServerUrl = type == AccountType.Authlib ? authServerUrl : null,
            },
            VersionName = "1.20.1",
            // 固定小内存：前置检查会拿可用内存做上界，用例不能因为机器内存小而变成 Blocker。
            MinMemoryMb = 512,
            MaxMemoryMb = 1024,
            GcKind = GcKind.Auto,
            AutoChineseLanguage = false,
            ArchiveCrashReport = false,
        };

    private static string? ReadTokenValue(string arguments, string prefix)
    {
        foreach (var token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                return token[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// 假的 authlib-injector 服务端：清单、jar、API 根三条路由，全部记录命中次数，
    /// 用「第几次请求」来判断缓存是否真的生效。
    /// </summary>
    private sealed class AuthlibServer : HttpMessageHandler
    {
        public byte[] JarBytes { get; init; } = Encoding.UTF8.GetBytes("fake-authlib-injector-jar");

        /// <summary>清单里声明的 SHA-256；null 表示按 <see cref="JarBytes"/> 现算。</summary>
        public string? Sha256 { get; init; }

        /// <summary>API 根的原始响应字节：故意带中文与非 ASCII，抓「先解析再序列化」的错误实现。</summary>
        public byte[] PrefetchBytes { get; init; } = Encoding.UTF8.GetBytes(
            """{"meta":{"serverName":"小皮裙","implementationName":"authlib-injector"}}""");

        public bool MetadataFails { get; init; }

        public HttpStatusCode PrefetchStatus { get; init; } = HttpStatusCode.OK;

        public int MetadataRequests { get; private set; }

        public int JarRequests { get; private set; }

        public int PrefetchRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (url == AuthlibInjectorService.MetadataUrl)
            {
                MetadataRequests++;

                if (MetadataFails)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                var sha256 = Sha256 ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JarBytes));

                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    build_number = 56,
                    version = Version,
                    download_url = DownloadUrl,
                    checksums = new { sha256 },
                });

                return Task.FromResult(Bytes(payload));
            }

            if (url == DownloadUrl)
            {
                JarRequests++;
                return Task.FromResult(Bytes(JarBytes));
            }

            if (url == ApiRoot)
            {
                PrefetchRequests++;

                return Task.FromResult(PrefetchStatus == HttpStatusCode.OK
                    ? Bytes(PrefetchBytes)
                    : new HttpResponseMessage(PrefetchStatus));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Bytes(byte[] payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
    }

    /// <summary>记录调用次数的注入服务替身。</summary>
    private sealed class StubInjector : IAuthlibInjectorService
    {
        public string? LastError { get; set; }

        public AuthlibInjection? Result { get; set; } = new(
            @"C:\cache\authlib-injector\authlib-injector-1.2.8.jar",
            ApiRoot,
            "-javaagent:C:\\cache\\authlib-injector\\authlib-injector-1.2.8.jar=https://littleskin.cn/api/yggdrasil"
            + " -Dauthlibinjector.yggdrasil.prefetched=e30="
            + " -Dauthlibinjector.side=client");

        public int EnsureCalls { get; private set; }

        public int BuildCalls { get; private set; }

        public Task<string?> EnsureInjectorAsync(
            string apiRoot,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            return Task.FromResult(Result?.JarPath);
        }

        public Task<AuthlibInjection?> BuildInjectionAsync(
            string apiRoot,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            BuildCalls++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>固定返回一个 64 位 Java 21。</summary>
    private sealed class StubJavaService : IJavaService
    {
        private static readonly JavaInfo Java = new()
        {
            Path = @"C:\fake\javaw.exe",
            Version = "21.0.1",
            MajorVersion = 21,
            Is64Bit = true,
            Vendor = "Test",
        };

        public Task<IReadOnlyList<JavaInfo>> DetectAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaInfo>>([Java]);

        public Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaInfo?>(Java);

        public JavaRequirement? GetRequirement(string minecraftVersion) => null;

        public (int Min, int Max)? GetAllowedRange(string minecraftVersion, LoaderKind loader) => null;

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaInfo?>(Java);

        public Task<JavaInfo?> ResolveForVersionAsync(
            string minecraftVersion,
            LoaderKind loader,
            string? preferredPath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaInfo?>(Java);

        public Task<string?> InstallRuntimeAsync(
            JavaRuntimePackage package,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> DownloadRuntimeAsync(
            int majorVersion,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>没有已安装版本的版本服务：只影响发行年与前置检查的加载器项，不阻断用例。</summary>
    private sealed class StubVersionService : IVersionService
    {
        public Task<IReadOnlyList<VersionInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionInfo>>([]);

        public Task<IReadOnlyList<RemoteVersion>> GetRemoteAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteVersion>>([]);

        public Task<VersionInfo?> GetVersionAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionInfo?>(null);

        public Task DeleteAsync(string versionName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordLaunchAsync(
            string versionName,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
            LoaderKind loader,
            string minecraftVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LoaderVersion>>([]);
    }
}
