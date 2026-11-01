using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Skinning;
using NebulaLauncher.Skinning;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 本地皮肤库卡片的「应用到微软账户」（票 #61）：动作必须作用于这张卡对应的文件，
/// 而不是列表当前选中项；沿用官方上传的前置校验与反馈。
/// </summary>
public sealed class AppSkin_LocalCardToOfficialTests
{
    private const string SkinUrl = "https://api.minecraftservices.com/minecraft/profile/skins";

    [Fact]
    public async Task 卡片动作上传的是这张卡的文件而不是当前选中项()
    {
        using var handler = new SkinHandler();
        using var vm = CreateVm(handler);
        var first = Entry("a.png", [0xDE, 0xAD, 0xBE, 0xEF]);
        var second = Entry("b.png", [0xCA, 0xFE, 0xBA, 0xBE]);
        vm.SelectedLocal = second;

        await vm.ApplyLocalToOfficialCommand.ExecuteAsync(first);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Url == SkinUrl);
        Assert.Contains("Bearer", post.Authorization);
        Assert.True(Contains(post.Body, [0xDE, 0xAD, 0xBE, 0xEF]));
        Assert.False(Contains(post.Body, [0xCA, 0xFE, 0xBA, 0xBE]));
        Assert.NotNull(vm.OfficialFeedback);
        Assert.False(vm.OfficialFeedback!.IsError);
        Assert.Contains("官方换肤成功", vm.OfficialFeedback.Text);
        Assert.Same(second, vm.SelectedLocal);
    }

    [Fact]
    public async Task 尺寸不合规时不上传并给出前置校验文案()
    {
        using var handler = new SkinHandler();
        using var vm = CreateVm(handler);
        var entry = Entry("wide.png", [0x01], width: 128, height: 64);

        await vm.ApplyLocalToOfficialCommand.ExecuteAsync(entry);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.NotNull(vm.OfficialFeedback);
        Assert.True(vm.OfficialFeedback!.IsError);
        Assert.Contains("128×64", vm.OfficialFeedback.Text);
    }

    private static SkinsViewModel CreateVm(SkinHandler handler)
    {
        var http = new HttpClient(handler);
        return new SkinsViewModel(new ServiceMap()
            .Add<IAccountService>(new FakeAccountService(MicrosoftAccount()))
            .Add(new MojangSkinClient(http)));
    }

    private static Account MicrosoftAccount() => new()
    {
        Id = "microsoft-1",
        Type = AccountType.Microsoft,
        Username = "Steve",
        Uuid = "0123456789abcdef0123456789abcdef",
        AccessToken = "minecraft-access-token",
    };

    private static LocalSkinEntryViewModel Entry(string name, byte[] marker, int width = 64, int height = 64)
    {
        var root = CreateRootDirectory();
        var path = Path.Combine(root, name);
        var bytes = new byte[26 + marker.Length];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        marker.CopyTo(bytes, 26);
        File.WriteAllBytes(path, bytes);
        return new LocalSkinEntryViewModel(new LocalSkinEntry(
            name,
            path,
            width,
            height,
            LocalSkinModel.Classic,
            DateTimeOffset.UtcNow));
    }

    private static string CreateRootDirectory()
    {
        var name = "nebula-local-card-" + Guid.NewGuid().ToString("N")[..10];
        foreach (var baseDirectory in new[]
                 {
                     Path.GetTempPath(),
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                 })
        {
            try
            {
                var candidate = Path.Combine(baseDirectory, name);
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception)
            {
                // 当前候选目录不可写，换下一个。
            }
        }

        throw new InvalidOperationException("找不到可写的测试临时目录。");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record RequestLog(HttpMethod Method, string Url, string Authorization, byte[] Body);

    private sealed class SkinHandler : HttpMessageHandler
    {
        public ConcurrentQueue<RequestLog> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Enqueue(new RequestLog(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));

            if (request.RequestUri.ToString().EndsWith("/minecraft/profile", StringComparison.Ordinal))
            {
                return Json("{\"capes\":[],\"skins\":[]}");
            }

            if (request.RequestUri.ToString() == SkinUrl && request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ServiceMap : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public ServiceMap Add<T>(T service)
            where T : class
        {
            _services[typeof(T)] = service;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class FakeAccountService(Account? active) : IAccountService
    {
        public IReadOnlyList<Account> Accounts => active is null ? [] : [active];

        public Account? ActiveAccount => active;

        public event EventHandler? AccountsChanged
        {
            add { }
            remove { }
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AccountResult> AddOfflineAccountAsync(string username, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccountResult> AddMicrosoftAccountAsync(
            IProgress<MicrosoftAuthProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountResult> RefreshAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void SetActive(string accountId) => throw new NotSupportedException();

        public Task<Account?> EnsureValidActiveAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
