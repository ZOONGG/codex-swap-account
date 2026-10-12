using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Tests;

public sealed class UpdateServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Count); return send(request, ct); }
    }
    private static HttpResponseMessage Response(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(text) };
    private static string Release(string tag = "v1.1.0", bool assets = true, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = tag, html_url = UpdateService.RepositoryUrl + "/releases/tag/" + tag,
        published_at = "2026-10-08T00:00:00Z", body = "Release notes", draft, prerelease,
        assets = assets ? new[] { UpdateService.ZipName, "SHA256SUMS.txt" }.Select(name => new { name, browser_download_url = UpdateService.RepositoryUrl + "/releases/download/" + tag + "/" + name }).ToArray() : []
    });
    private static UpdateService Service(TempDirectory temp, Handler handler, Func<DateTimeOffset>? clock = null) => new(Path.Combine(temp.Path, "update.json"), new HttpClient(handler), "1.0.0", clock);

    [Theory]
    [InlineData("v1.1.0", true)] [InlineData("1.0.0", false)] [InlineData("v0.9.9", false)] [InlineData("v1.0.0+build.2", false)]
    public async Task DetectsOnlyNewer(string tag, bool expected)
    {
        using var temp = new TempDirectory(); using var handler = new Handler((_, _) => Task.FromResult(Response(Release(tag)))); using var service = Service(temp, handler);
        await service.CheckAsync(true); Assert.Equal(expected, service.HasUpdate); Assert.Null(service.ErrorKey);
    }
    [Theory]
    [InlineData("v1.0.0", false)]
    [InlineData("v1.1.0", false)]
    [InlineData("v1.1.1", true)]
    [InlineData("v1.2.0", true)]
    public async Task Release110DetectsOnlyNewer(string tag, bool expected)
    {
        using var temp = new TempDirectory();
        using var handler = new Handler((_, _) => Task.FromResult(Response(Release(tag))));
        using var service = new UpdateService(Path.Combine(temp.Path, "update.json"), new HttpClient(handler));
        Assert.Equal("1.1.0", service.CurrentVersion);
        await service.CheckAsync(true);
        Assert.Equal(expected, service.HasUpdate);
        Assert.Null(service.ErrorKey);
    }
    [Theory]
    [InlineData("1.10.0", "1.9.0")] [InlineData("1.0.0", "1.0.0-rc.9")] [InlineData("1.0.0-rc.10", "1.0.0-rc.2")] [InlineData("1.0.0-beta", "1.0.0-1")]
    public void SemverOrdering(string a, string b) => Assert.True(ReleaseVersion.Parse(a).CompareTo(ReleaseVersion.Parse(b)) > 0);
    [Theory]
    [InlineData("1.0")] [InlineData("garbage")] [InlineData("v01.0.0")] [InlineData("1.0.0-01")]
    public void InvalidTags(string tag) => Assert.Throws<FormatException>(() => ReleaseVersion.Parse(tag));

    [Theory]
    [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task HttpErrors(int code)
    {
        using var temp = new TempDirectory(); using var handler = new Handler((_, _) => Task.FromResult(Response("", (HttpStatusCode)code))); using var service = Service(temp, handler);
        await service.CheckAsync(true); Assert.NotNull(service.ErrorKey); Assert.False(service.HasUpdate);
    }
    [Theory]
    [InlineData("{")] [InlineData("{}")] [InlineData("null")]
    public async Task MalformedJson(string json)
    {
        using var temp = new TempDirectory(); using var handler = new Handler((_, _) => Task.FromResult(Response(json))); using var service = Service(temp, handler);
        await service.CheckAsync(true); Assert.NotNull(service.ErrorKey);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task NetworkFailure(bool timeout)
    {
        using var temp = new TempDirectory(); using var handler = new Handler((_, _) => throw (timeout ? (Exception)new TaskCanceledException() : new HttpRequestException())); using var service = Service(temp, handler);
        await service.CheckAsync(true); Assert.NotNull(service.ErrorKey);
    }
    [Fact]
    public async Task CacheThrottleAndManualRetry()
    {
        using var temp = new TempDirectory(); var time = DateTimeOffset.UtcNow; using var handler = new Handler((_, _) => Task.FromResult(Response(Release())));
        using (var service = Service(temp, handler, () => time)) { await service.CheckAsync(false); await service.CheckAsync(false); Assert.Equal(1, handler.Count); }
        using var second = Service(temp, handler, () => time); await second.CheckAsync(false); Assert.True(second.HasUpdate); Assert.Equal(1, handler.Count);
        time += TimeSpan.FromHours(24); await second.CheckAsync(false); await second.CheckAsync(true); Assert.Equal(3, handler.Count);
    }
    [Fact]
    public async Task ConcurrentChecksAndCancellation()
    {
        using var temp = new TempDirectory(); using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(Release()); }); using var service = Service(temp, handler);
        using var ct = new CancellationTokenSource(); var first = service.CheckAsync(true, ct.Token); await service.CheckAsync(true); Assert.Equal(1, handler.Count); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }
    [Theory]
    [InlineData(true, true)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task VerifiedDownload(bool validHash, bool assets)
    {
        using var temp = new TempDirectory(); byte[] zip = Encoding.UTF8.GetBytes("test zip bytes"); string hash = Convert.ToHexString(SHA256.HashData(validHash ? zip : [0]));
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("latest") ? Response(Release(assets: assets)) : request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt") ? Response(hash + "  " + UpdateService.ZipName + "\n") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));
        using var service = Service(temp, handler); await service.CheckAsync(true);
        if (!validHash || !assets) { await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync()); return; }
        string path = await service.DownloadAsync(); try { Assert.Equal(zip, await File.ReadAllBytesAsync(path)); } finally { File.Delete(path); }
    }
    [Fact]
    public async Task DownloadCancellationConcurrencyAndCleanup()
    {
        using var temp = new TempDirectory(); var entered = new TaskCompletionSource();
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest")) return Response(Release());
            if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")) return Response(new string('0', 64) + "  " + UpdateService.ZipName);
            entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Response("");
        });
        using var service = Service(temp, handler); await service.CheckAsync(true);
        var before = Directory.GetFiles(Path.GetTempPath(), "codex-update-*.zip");
        using var ct = new CancellationTokenSource(); var download = service.DownloadAsync(cancellation: ct.Token); await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync()); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(before.Order(), Directory.GetFiles(Path.GetTempPath(), "codex-update-*.zip").Order());
    }
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)]
    public async Task IgnoresDraftAndPrerelease(bool draft, bool prerelease)
    {
        using var temp = new TempDirectory(); using var handler = new Handler((_, _) => Task.FromResult(Response(Release(draft: draft, prerelease: prerelease)))); using var service = Service(temp, handler);
        await service.CheckAsync(true); Assert.False(service.HasUpdate);
    }
    [Theory]
    [InlineData("missing")] [InlineData("malformed")] [InlineData("interrupted")] [InlineData("unsafe")]
    public async Task RejectsInvalidDownloadsAndCleansTemporaryFile(string kind)
    {
        using var temp = new TempDirectory();
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest")) return Task.FromResult(Response(Release()));
            if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")) return Task.FromResult(Response(kind == "missing" ? "" : kind == "malformed" ? "xyz  " + UpdateService.ZipName : new string('0', 64) + "  " + UpdateService.ZipName));
            if (kind == "interrupted") throw new IOException("Interrupted stream.");
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.com/evil.zip");
            return Task.FromResult(response);
        });
        using var service = Service(temp, handler); await service.CheckAsync(true);
        var before = Directory.GetFiles(Path.GetTempPath(), "codex-update-*.zip").Order().ToArray();
        if (kind == "interrupted") await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync());
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync());
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "codex-update-*.zip").Order().ToArray());
    }

    [Fact]
    public async Task KeepsLastSuccessfulReleaseWhenOffline()
    {
        using var temp = new TempDirectory(); bool offline = false;
        using var handler = new Handler((_, _) => offline ? throw new HttpRequestException() : Task.FromResult(Response(Release())));
        using var service = Service(temp, handler); await service.CheckAsync(true);
        var release = service.Cache.Release; offline = true; await service.CheckAsync(true);
        Assert.Equal(release, service.Cache.Release); Assert.True(service.HasUpdate); Assert.NotNull(service.ErrorKey);
    }

    [Fact]
    public void Translations()
    {
        foreach (var key in new[] { "Updates", "UpdateCheck", "UpdateDownload", "UpdateCurrent", "UpdateAvailable", "UpdateUnavailable", "UpdateRateLimited", "UpdateDownloadFailed" })
        {
            string en = LocalizationCatalog.Text(LanguagePreference.English, key), ru = LocalizationCatalog.Text(LanguagePreference.Russian, key);
            Assert.NotEqual(key, en); Assert.NotEqual(en, ru); Assert.DoesNotContain("?", ru);
        }
    }
}
