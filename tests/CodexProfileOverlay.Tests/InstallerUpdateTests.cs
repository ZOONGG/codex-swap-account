using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class InstallerUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation);
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static string Metadata(string? installer = UpdateService.InstallerName, string? url = null) => JsonSerializer.Serialize(new
    {
        tag_name = "v1.1.0", html_url = UpdateService.RepositoryUrl + "/releases/tag/v1.1.0",
        published_at = "2026-10-08T00:00:00Z", body = "Notes", draft = false, prerelease = false,
        assets = new[] { installer, "SHA256SUMS.txt" }.Where(n => n is not null).Select(n => new
        {
            name = n, browser_download_url = n == installer && url is not null ? url : UpdateService.RepositoryUrl + "/releases/download/v1.1.0/" + n
        })
    });

    [Fact]
    public void InstalledDetectionRequiresRegistrationMarkerAndUninstaller()
    {
        using var temp = new TempDirectory();
        string exe = Path.Combine(temp.Path, "renamed.exe");
        File.WriteAllText(Path.Combine(temp.Path, "installation.id"), InstallationMode.AppId);
        Assert.False(InstallationMode.IsInstalled(exe, temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "unins000.exe"), "fake");
        Assert.True(InstallationMode.IsInstalled(exe, temp.Path));
        Assert.False(InstallationMode.IsInstalled(exe, null));
        Assert.False(InstallationMode.IsInstalled(Path.Combine(temp.Path, "portable", "renamed.exe"), temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "installation.id"), "other app");
        Assert.False(InstallationMode.IsInstalled(exe, temp.Path));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("bad-hash")]
    [InlineData("missing")]
    [InlineData("wrong-name")]
    [InlineData("unsafe-url")]
    [InlineData("duplicate-checksum")]
    [InlineData("interrupted")]
    public async Task SelectsExactInstallerAndVerifiesIt(string scenario)
    {
        using var temp = new TempDirectory();
        byte[] payload = Encoding.UTF8.GetBytes("fake installer, never executed");
        string checksum = Convert.ToHexString(SHA256.HashData(scenario == "bad-hash" ? [0] : payload)) + "  " + UpdateService.InstallerName + "\n";
        using var client = new HttpClient(new Handler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("latest")) return Task.FromResult(Response(Metadata(scenario == "missing" ? null : scenario == "wrong-name" ? "evil.exe" : UpdateService.InstallerName, scenario == "unsafe-url" ? "https://example.com/evil.exe" : null)));
            if (path.EndsWith("SHA256SUMS.txt")) return Task.FromResult(Response(scenario == "duplicate-checksum" ? checksum + checksum : checksum));
            Assert.EndsWith(UpdateService.InstallerName, path);
            if (scenario == "interrupted") throw new IOException("Connection interrupted.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }));
        using var service = new UpdateService(Path.Combine(temp.Path, "cache.json"), client, "1.0.0");
        await service.CheckAsync(true);
        if (scenario != "valid")
        {
            if (scenario == "interrupted") await Assert.ThrowsAsync<IOException>(() => service.DownloadInstallerAsync());
            else await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync());
            return;
        }
        string downloaded = await service.DownloadInstallerAsync();
        try
        {
            Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded));
            // Simulates active account switching / portable mode. Never executes fake bytes.
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchInstallerAsync(downloaded, () =>
            {
                // A second installation cannot enter even while the first checks switch state.
                Assert.Throws<InvalidOperationException>(() => service.LaunchInstallerAsync(downloaded, () => true).GetAwaiter().GetResult());
                return false;
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LaunchInstallerAsync(downloaded, () => true));
        }
        finally { File.Delete(downloaded); }
    }

    [Fact]
    public async Task CancelsInstallerDownloadAndRefusesParallelDownloads()
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest")) return Response(Metadata());
            if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")) return Response(new string('0', 64) + "  " + UpdateService.InstallerName);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Response("");
        }));
        using var service = new UpdateService(Path.Combine(temp.Path, "cache.json"), client, "1.0.0");
        await service.CheckAsync(true);
        using var cancellation = new CancellationTokenSource();
        var pending = service.DownloadInstallerAsync(cancellation: cancellation.Token);
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // Gate is released even on cancellation.
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LaunchInstallerAsync(Path.Combine(Path.GetTempPath(), "codex-update-unverified.exe"), () => true));
    }

    [Fact]
    public async Task RefusesModifiedInstallerBeforeCallingSwitchGuard()
    {
        using var temp = new TempDirectory();
        byte[] payload = [1, 2, 3];
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("latest") ? Response(Metadata()) :
            request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt") ? Response(Convert.ToHexString(SHA256.HashData(payload)) + "  " + UpdateService.InstallerName) :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })));
        using var service = new UpdateService(Path.Combine(temp.Path, "cache.json"), client, "1.0.0");
        await service.CheckAsync(true);
        string downloaded = await service.DownloadInstallerAsync();
        try
        {
            await File.WriteAllBytesAsync(downloaded, [9]);
            bool called = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LaunchInstallerAsync(downloaded, () => { called = true; return true; }));
            Assert.False(called);
        }
        finally { File.Delete(downloaded); }
    }

    [Fact]
    public void LaunchUsesFixedArgumentsWithoutShellOrCredentials()
    {
        var start = InstallerLaunch.Create(Path.Combine(Path.GetTempPath(), "codex-update-test.exe"));
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "/UPDATE=1", "/SILENT", "/NORESTART" }, start.ArgumentList);
        Assert.Empty(start.UserName);
        Assert.Empty(start.Arguments);
        Assert.Throws<InvalidDataException>(() => InstallerLaunch.Create("evil.exe"));
        Assert.Throws<InvalidDataException>(() => InstallerLaunch.Create(Path.Combine(Path.GetTempPath(), "evil.exe")));
    }

    [Fact]
    public void UpdateLocalizationExistsInBothLanguages()
    {
        foreach (string key in new[] { "UpdateRestart", "UpdateGetInstaller", "UpdateLater", "UpdatePortable", "UpdateConfirm", "UpdateReady", "UpdateSwitchBusy", "UpdateInstallFailed", "UpdateInstalledVersion" })
        {
            string en = LocalizationCatalog.Text(Core.Models.LanguagePreference.English, key);
            string ru = LocalizationCatalog.Text(Core.Models.LanguagePreference.Russian, key);
            Assert.NotEqual(key, en);
            Assert.NotEqual(key, ru);
            Assert.NotEqual(en, ru);
        }
    }
}
