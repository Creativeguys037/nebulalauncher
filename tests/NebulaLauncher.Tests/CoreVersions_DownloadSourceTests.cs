using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Downloads;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Tests;

public class CoreVersions_DownloadSourceTests
{
    private static readonly string[] OfficialThenMirrorHosts =
    [
        "piston-meta.mojang.com",
        "meta.fabricmc.net",
        "bmclapi2.bangbang93.com",
        "bmclapi2.bangbang93.com",
    ];

    private static readonly string[] MirrorThenOfficialHosts =
    [
        "bmclapi2.bangbang93.com",
        "piston-meta.mojang.com",
        "meta.fabricmc.net",
    ];

    [Fact]
    public async Task MetadataRequestsFollowSettingsChangesWithoutRecreatingService()
    {
        using var temp = new TempDirectory();
        var settings = new MutableSettings(DownloadSource.Official);
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = new VersionService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance, client, settings);

        await service.GetRemoteAsync(forceRefresh: true);
        await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.20.1");
        await settings.ChangeSourceAsync(DownloadSource.Mirror);
        await service.GetRemoteAsync(forceRefresh: true);
        await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.20.1");

        Assert.Equal(OfficialThenMirrorHosts, handler.Hosts);
    }

    [Fact]
    public async Task MetadataRequestsFollowSessionSourceProvider()
    {
        using var temp = new TempDirectory();
        var settings = new MutableSettings(DownloadSource.Mirror);
        var source = new DownloadSourceGovernor(strategy: DownloadSource.Mirror);
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = new VersionService(new FakeLauncherPaths(temp.Path), NullAppLog.Instance, client, settings, source);

        await service.GetRemoteAsync(forceRefresh: true);
        source.SetStrategy(DownloadSource.Official);
        await service.GetRemoteAsync(forceRefresh: true);
        await service.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.20.1");

        Assert.Equal(MirrorThenOfficialHosts, handler.Hosts);
    }

    [Fact]
    public async Task DownloadSourceIsInitializedAndUpdatedBeforeQueueIsResolved()
    {
        var settings = new MutableSettings(DownloadSource.Official);
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(settings);
        services.AddCoreDownloads();
        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<IDownloadSourceProvider>();

        Assert.Equal(DownloadSource.Official, source.Effective);
        await settings.ChangeSourceAsync(DownloadSource.Mirror);
        Assert.Equal(DownloadSource.Mirror, source.Effective);
    }

    private sealed class MutableSettings(DownloadSource source) : ISettingsService
    {
        public AppSettings Current { get; } = new() { DownloadSource = source };
        public event EventHandler<AppSettings>? Changed;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public Task ChangeSourceAsync(DownloadSource source)
        {
            Current.DownloadSource = source;
            return SaveAsync();
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Hosts.Add(uri.Host);
            var body = uri.AbsolutePath.EndsWith("version_manifest_v2.json", StringComparison.Ordinal)
                ? """{"versions":[{"id":"1.20.1","type":"release","url":"https://example.test/version.json","releaseTime":"2023-06-12T00:00:00Z"}]}"""
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
