using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class BackgroundUpdateTests
{
    private sealed class Feed : HttpMessageHandler
    {
        public string Tag = "v1.1.0";
        public bool Offline, BadHash, Oversize, Draft, Prerelease, Block;
        public int Checks, Downloads;
        public readonly byte[] Payload = [1, 2, 3, 4];
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Offline) throw new HttpRequestException("Offline");
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("latest"))
            {
                Checks++;
                return Text(JsonSerializer.Serialize(new
                {
                    tag_name = Tag, html_url = UpdateService.RepositoryUrl + "/releases/tag/" + Tag,
                    published_at = "2026-10-12T00:00:00Z", body = "Notes", draft = Draft, prerelease = Prerelease,
                    assets = new[] { UpdateService.InstallerName, "SHA256SUMS.txt" }.Select(name => new
                    { name, browser_download_url = UpdateService.RepositoryUrl + "/releases/download/" + Tag + "/" + name })
                }));
            }
            if (path.EndsWith("SHA256SUMS.txt"))
                return Text(Convert.ToHexString(SHA256.HashData(BadHash ? [9] : Payload)) + "  " + UpdateService.InstallerName);
            Downloads++;
            Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, cancellation);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            if (Oversize) response.Content.Headers.ContentLength = 512L * 1024 * 1024 + 1;
            return response;
        }
        private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }

    private sealed class Fixture : IDisposable
    {
        public readonly TempDirectory Temp = new();
        public readonly Feed Feed = new();
        public readonly UpdateService Service;
        public string CachePath => Path.Combine(Temp.Path, "update.json");
        public Fixture()
        {
            Service = new UpdateService(CachePath, new HttpClient(Feed), "1.0.0");
            Service.ConfigureBackgroundDownloads(true, true);
        }
        public void Dispose()
        {
            Service.Dispose();
            if (Service.Cache.Prepared is { } ready) File.Delete(Path.Combine(Path.GetTempPath(), ready.FileName));
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task AutomaticallyDownloadsOnceWithProgressAndRetains24HourCheckInterval()
    {
        using var f = new Fixture();
        var progress = new List<double>();
        f.Service.ProgressChanged += progress.Add;
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
        Assert.Contains(100d, progress);
        Assert.Equal(f.Feed.Payload, await File.ReadAllBytesAsync(f.Service.ReadyInstallerPath!));
        await f.Service.CheckAndDownloadAsync(false);
        await f.Service.CheckAndDownloadAsync(true);
        Assert.Equal(1, f.Feed.Downloads);
        Assert.Equal(2, f.Feed.Checks);
    }

    [Fact]
    public async Task ReadyPackageRestoresOfflineAfterRestartWithoutDownloadingAgain()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        f.Feed.Offline = true;
        using var restarted = new UpdateService(f.CachePath, new HttpClient(f.Feed), "1.0.0");
        restarted.ConfigureBackgroundDownloads(true, true);
        await restarted.CheckAndDownloadAsync(false);
        Assert.True(restarted.IsReady);
        Assert.Equal(f.Service.ReadyInstallerPath, restarted.ReadyInstallerPath);
        Assert.Equal(1, f.Feed.Downloads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.LaunchPreparedInstallerAsync(true, () => false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DisabledAndPortableDoNotDownload(bool installed, bool enabled)
    {
        using var f = new Fixture();
        f.Service.ConfigureBackgroundDownloads(installed, enabled);
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.HasUpdate);
        Assert.False(f.Service.IsReady);
        Assert.Equal(0, f.Feed.Downloads);
        if (!installed) await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PrepareInstallerAsync());
    }

    [Fact]
    public async Task EnableDownloadsCachedReleaseWithoutAnotherMetadataCheck()
    {
        using var f = new Fixture();
        f.Service.ConfigureBackgroundDownloads(true, false);
        await f.Service.CheckAndDownloadAsync(false);
        f.Service.ConfigureBackgroundDownloads(true, true);
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
        Assert.Equal(1, f.Feed.Checks);
        Assert.Equal(1, f.Feed.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HashMismatchAndSizeLimitAreRetryable(bool oversize)
    {
        using var f = new Fixture();
        f.Feed.BadHash = !oversize;
        f.Feed.Oversize = oversize;
        await f.Service.CheckAndDownloadAsync(false);
        Assert.False(f.Service.IsReady);
        Assert.Null(f.Service.Cache.Prepared);
        Assert.Equal("UpdateDownloadFailed", f.Service.DownloadErrorKey);
        f.Feed.BadHash = f.Feed.Oversize = false;
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
        Assert.Equal(2, f.Feed.Downloads);
    }

    [Fact]
    public async Task OfflineRetryPreservesReadyPackage()
    {
        using var f = new Fixture();
        f.Feed.Offline = true;
        await f.Service.CheckAndDownloadAsync(true);
        Assert.Equal("UpdateUnavailable", f.Service.ErrorKey);
        f.Feed.Offline = false;
        await f.Service.CheckAndDownloadAsync(true);
        string path = f.Service.ReadyInstallerPath!;
        f.Feed.Offline = true;
        await f.Service.CheckAndDownloadAsync(true);
        Assert.True(f.Service.IsReady);
        Assert.Equal(path, f.Service.ReadyInstallerPath);
        Assert.Equal(1, f.Feed.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisablingReleaseGateAndAllowRetry(bool disable)
    {
        using var f = new Fixture();
        f.Feed.Block = true;
        using var cancellation = new CancellationTokenSource();
        var pending = f.Service.CheckAndDownloadAsync(false, cancellation.Token);
        await f.Feed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Service.CheckAndDownloadAsync(false);
        Assert.Equal(1, f.Feed.Downloads);
        if (disable)
        {
            f.Service.ConfigureBackgroundDownloads(true, false);
            await pending;
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.False(f.Service.IsDownloading);
        Assert.False(f.Service.IsReady);
        f.Feed.Block = false;
        f.Service.ConfigureBackgroundDownloads(true, true);
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
        Assert.Equal(2, f.Feed.Downloads);
    }

    [Fact]
    public async Task NewerVersionDeletesOldPackage()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        string old = f.Service.ReadyInstallerPath!;
        f.Feed.Tag = "v1.2.0";
        await f.Service.CheckAndDownloadAsync(true);
        Assert.False(File.Exists(old));
        Assert.True(f.Service.IsReady);
        Assert.Equal("v1.2.0", f.Service.Cache.Prepared!.Tag);
        Assert.Equal(2, f.Feed.Downloads);
    }

    [Fact]
    public async Task CurrentVersionRemovesPreviouslyPreparedPackage()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        string old = f.Service.ReadyInstallerPath!;
        using var updated = new UpdateService(f.CachePath, new HttpClient(f.Feed), "1.1.0");
        updated.ConfigureBackgroundDownloads(true, true);
        await updated.CheckAndDownloadAsync(false);
        Assert.False(updated.IsReady);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task CorruptPackageAfterRestartIsDownloadedAgain()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        string old = f.Service.ReadyInstallerPath!;
        await File.WriteAllBytesAsync(old, [9]);
        using var restarted = new UpdateService(f.CachePath, new HttpClient(f.Feed), "1.0.0");
        restarted.ConfigureBackgroundDownloads(true, true);
        try
        {
            await restarted.CheckAndDownloadAsync(false);
            Assert.True(restarted.IsReady);
            Assert.False(File.Exists(old));
            Assert.Equal(2, f.Feed.Downloads);
        }
        finally { if (restarted.ReadyInstallerPath is { } ready) File.Delete(ready); }
    }

    [Fact]
    public async Task ConfirmationAndSwitchGuardAreRequiredAndHashIsRecheckedBeforeLaunch()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LaunchPreparedInstallerAsync(false, () => throw new Exception("Must not run")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LaunchPreparedInstallerAsync(true, () => false));
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LaunchPreparedInstallerAsync(true, () => ++calls == 1));
        // A switch starting during revalidation must prevent launch and retain the verified package for retry.
        calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.LaunchPreparedInstallerAsync(true, () => ++calls == 1));
        Assert.Equal(2, calls);
        await File.WriteAllBytesAsync(f.Service.ReadyInstallerPath!, [9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.LaunchPreparedInstallerAsync(true, () => true));
        Assert.False(f.Service.IsReady);
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectsDraftAndPrerelease(bool draft, bool prerelease)
    {
        using var f = new Fixture();
        f.Feed.Draft = draft;
        f.Feed.Prerelease = prerelease;
        await f.Service.CheckAndDownloadAsync(false);
        Assert.False(f.Service.HasUpdate);
        Assert.Equal(0, f.Feed.Downloads);
    }

    [Fact]
    public void AutoDownloadDefaultsOnAndOptOutPersists()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        Assert.True(settings.Load().AutomaticallyDownloadUpdates);
        settings.Save(new OverlaySettings { AutomaticallyDownloadUpdates = false });
        Assert.False(settings.Load().AutomaticallyDownloadUpdates);
    }

    [Fact]
    public async Task DisabledModeRetainsAlreadyVerifiedPackageForManualInstallation()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        f.Service.ConfigureBackgroundDownloads(true, false);
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
        Assert.Equal(1, f.Feed.Downloads);
    }

    [Fact]
    public async Task MissingPackageAfterRestartIsDownloadedAgain()
    {
        using var f = new Fixture();
        await f.Service.CheckAndDownloadAsync(false);
        File.Delete(f.Service.ReadyInstallerPath!);
        await f.Service.CheckAndDownloadAsync(false);
        Assert.True(f.Service.IsReady);
        Assert.Equal(2, f.Feed.Downloads);
    }
}
