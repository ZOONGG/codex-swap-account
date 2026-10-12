using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public sealed record UpdateRelease(string Tag, string Url, DateTimeOffset Published, string Notes, string? Zip, string? Checksums, string? Installer = null);
public sealed record PreparedInstaller(string Tag, string FileName, string Sha256);
public sealed record UpdateCache(DateTimeOffset LastAttempt, DateTimeOffset? LastSuccess, UpdateRelease? Release, PreparedInstaller? Prepared = null);

/// <summary>Public release metadata only; never accesses profiles or authorization.</summary>
public sealed class UpdateService : IDisposable
{
    public const string ZipName = "CodexProfileOverlay-win-x64-portable.zip";
    public const string InstallerName = "CodexSwapAccount-win-x64-setup.exe";
    public const string RepositoryUrl = "https://github.com/ZOONGG/codex-swap-account";
    private readonly HttpClient http;
    private readonly string cachePath;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim checkGate = new(1, 1), downloadGate = new(1, 1);
    private readonly SemaphoreSlim prepareGate = new(1, 1);
    private readonly object backgroundLock = new();
    private readonly object cacheWriteLock = new();
    private CancellationTokenSource? backgroundDownload;
    private volatile bool installed, automaticallyDownload;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> verifiedInstallers = new();
    public string CurrentVersion { get; }
    public UpdateCache Cache { get; private set; } = new(default, null, null);
    public string? ErrorKey { get; private set; }
    public string? DownloadErrorKey { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }
    public double DownloadProgress { get; private set; }
    public string? ReadyInstallerPath { get; private set; }
    public bool IsReady => ReadyInstallerPath is not null && HasUpdate && Cache.Prepared?.Tag == Cache.Release?.Tag;
    public bool HasUpdate => Cache.Release is { } r && ReleaseVersion.Parse(r.Tag).CompareTo(ReleaseVersion.Parse(CurrentVersion)) > 0;
    public event Action? Changed;
    public event Action<double>? ProgressChanged;

    public UpdateService(string cachePath, HttpClient? client = null, string? currentVersion = null, Func<DateTimeOffset>? clock = null)
    {
        this.cachePath = cachePath;
        now = clock ?? (() => DateTimeOffset.UtcNow);
        CurrentVersion = currentVersion ?? typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? throw new InvalidOperationException("Application version metadata is missing.");
        http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(cachePath));
                if (cached is not null && (cached.Release is null || ValidRelease(cached.Release))) Cache = cached;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
    }

    public async Task CheckAsync(bool manual, CancellationToken cancellation = default)
    {
        if (!await checkGate.WaitAsync(0, cancellation).ConfigureAwait(false)) return;
        try
        {
            if (!manual && now() - Cache.LastAttempt < TimeSpan.FromHours(24)) return;
            IsChecking = true;
            Changed?.Invoke();
            lock (cacheWriteLock)
            {
                Cache = Cache with { LastAttempt = now() };
                SaveCache();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            byte[] data = await ReadAsync(new Uri("https://api.github.com/repos/ZOONGG/codex-swap-account/releases/latest"), 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new FormatException();
            string tag = root.GetProperty("tag_name").GetString() ?? throw new FormatException();
            if (ReleaseVersion.Parse(tag).Prerelease is not null) throw new FormatException();
            string? Asset(string name) => root.GetProperty("assets").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == name) is var asset && asset.ValueKind == JsonValueKind.Object ? asset.GetProperty("browser_download_url").GetString() : null;
            var release = new UpdateRelease(tag, root.GetProperty("html_url").GetString()!, root.GetProperty("published_at").GetDateTimeOffset(), root.GetProperty("body").GetString() ?? "", Asset(ZipName), Asset("SHA256SUMS.txt"), Asset(InstallerName));
            if (!ValidRelease(release)) throw new FormatException();
            lock (cacheWriteLock)
            {
                Cache = Cache with { LastSuccess = now(), Release = release };
                SaveCache();
            }
            ErrorKey = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (HttpRequestException e) { ErrorKey = e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? "UpdateRateLimited" : "UpdateUnavailable"; }
        catch (Exception e) when (e is OperationCanceledException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or IOException or InvalidDataException) { ErrorKey = "UpdateUnavailable"; }
        finally { IsChecking = false; checkGate.Release(); Changed?.Invoke(); }
    }

    public void ConfigureBackgroundDownloads(bool isInstalled, bool enabled)
    {
        lock (backgroundLock)
        {
            installed = isInstalled;
            automaticallyDownload = enabled;
            if (!installed || !enabled) backgroundDownload?.Cancel();
        }
    }

    public void CancelBackgroundDownload()
    {
        lock (backgroundLock) backgroundDownload?.Cancel();
    }

    /// <summary>The checker retains its 24-hour throttle; cached downloads can retry separately.</summary>
    public async Task CheckAndDownloadAsync(bool manual, CancellationToken cancellation = default)
    {
        await CheckAsync(manual, cancellation).ConfigureAwait(false);
        bool shouldDownload;
        lock (backgroundLock) shouldDownload = installed && automaticallyDownload;
        if (!installed) return;
        try { await PrepareInstallerAsync(shouldDownload, cancellation, automatic: true).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
    }

    // A single orchestration gate uses the existing downloader and its shared transfer gate.
    public async Task PrepareInstallerAsync(bool allowDownload = true, CancellationToken cancellation = default, bool automatic = false)
    {
        if (!installed) throw new InvalidOperationException("Portable mode cannot prepare automatic installation.");
        if (!await prepareGate.WaitAsync(0, cancellation).ConfigureAwait(false)) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        string? downloaded = null;
        try
        {
            lock (backgroundLock)
            {
                backgroundDownload = lifetime;
                if (!installed || automatic && !automaticallyDownload) allowDownload = false;
            }
            DownloadErrorKey = null;
            var release = Cache.Release;
            if (!HasUpdate || Cache.Prepared is { } old && old.Tag != release?.Tag) DiscardPreparedInstaller();
            if (!HasUpdate || release is null) return;
            if (Cache.Prepared is { } prepared)
            {
                string? path = PreparedPath(prepared);
                if (path is not null && await VerifyPreparedAsync(path, prepared, lifetime.Token).ConfigureAwait(false))
                {
                    ReadyInstallerPath = path;
                    verifiedInstallers[path] = Convert.FromHexString(prepared.Sha256);
                    return;
                }
                DiscardPreparedInstaller();
            }
            if (!allowDownload) return;
            IsDownloading = true;
            DownloadProgress = 0;
            Changed?.Invoke();
            downloaded = await DownloadInstallerAsync(new InlineProgress(value =>
            {
                int previous = (int)DownloadProgress;
                DownloadProgress = Math.Clamp(value, 0, 100);
                if ((int)DownloadProgress != previous) ProgressChanged?.Invoke(DownloadProgress);
            }), lifetime.Token).ConfigureAwait(false);
            lock (cacheWriteLock)
            {
                if (release != Cache.Release || !HasUpdate) throw new InvalidDataException("Release changed during download.");
                Cache = Cache with { Prepared = new PreparedInstaller(release.Tag, Path.GetFileName(downloaded), Convert.ToHexString(verifiedInstallers[downloaded])) };
                if (!SaveCache()) throw new IOException("Cannot retain the verified installer metadata.");
            }
            ReadyInstallerPath = downloaded;
            downloaded = null;
        }
        catch (OperationCanceledException)
        {
            DownloadErrorKey = "UpdateCancelled";
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            DownloadErrorKey = "UpdateDownloadFailed";
        }
        finally
        {
            if (downloaded is not null) DeleteOwnedInstaller(downloaded);
            lock (backgroundLock) backgroundDownload = null;
            IsDownloading = false;
            prepareGate.Release();
            Changed?.Invoke();
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static string? PreparedPath(PreparedInstaller prepared)
        => prepared.FileName is not null && Regex.IsMatch(prepared.FileName, @"\Acodex-update-[a-f0-9]{32}\.exe\z")
            && prepared.Sha256 is not null && Regex.IsMatch(prepared.Sha256, @"\A[a-fA-F0-9]{64}\z")
            ? Path.Combine(Path.GetTempPath(), prepared.FileName) : null;

    private static async Task<bool> VerifyPreparedAsync(string path, PreparedInstaller prepared, CancellationToken cancellation)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length > 512L * 1024 * 1024) return false;
            return CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false), Convert.FromHexString(prepared.Sha256));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private void DeleteOwnedInstaller(string path)
    {
        verifiedInstallers.TryRemove(path, out _);
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void DiscardPreparedInstaller()
    {
        if (Cache.Prepared is { } prepared && PreparedPath(prepared) is { } path) DeleteOwnedInstaller(path);
        ReadyInstallerPath = null;
        lock (cacheWriteLock)
        {
            Cache = Cache with { Prepared = null };
            SaveCache();
        }
    }

    public async Task LaunchPreparedInstallerAsync(bool confirmed, Func<bool> mayInstall, CancellationToken cancellation = default)
    {
        if (!confirmed || !installed || !mayInstall()) throw new InvalidOperationException("Installation requires confirmation and an idle installed application.");
        if (!IsReady) throw new InvalidDataException("No verified installer is ready.");
        string path = ReadyInstallerPath!;
        try { await LaunchInstallerAsync(path, () => IsReady && ReadyInstallerPath == path && mayInstall(), cancellation).ConfigureAwait(false); }
        catch (InvalidDataException) { DiscardPreparedInstaller(); DownloadErrorKey = "UpdateDownloadFailed"; Changed?.Invoke(); throw; }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (IsReady && Cache.Prepared is { } prepared) verifiedInstallers[path] = Convert.FromHexString(prepared.Sha256);
            throw;
        }
    }

    private static bool ValidRelease(UpdateRelease r)
    {
        if (string.IsNullOrEmpty(r.Tag) || string.IsNullOrEmpty(r.Url) || r.Notes is null) return false;
        _ = ReleaseVersion.Parse(r.Tag);
        if (ReleaseVersion.Parse(r.Tag).Prerelease is not null) return false;
        string prefix = RepositoryUrl + "/releases/tag/";
        return r.Url.StartsWith(prefix, StringComparison.Ordinal) && Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) && uri.Query == "" && uri.Fragment == "" && Uri.UnescapeDataString(uri.AbsolutePath[(new Uri(prefix).AbsolutePath.Length)..]) == r.Tag
            && (r.Zip is null || ValidAsset(r.Zip, r.Tag, ZipName)) && (r.Checksums is null || ValidAsset(r.Checksums, r.Tag, "SHA256SUMS.txt"))
            && (r.Installer is null || ValidAsset(r.Installer, r.Tag, InstallerName));
    }

    private static bool ValidAsset(string url, string tag, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com"
        && uri.IsDefaultPort && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == ""
        && Uri.UnescapeDataString(uri.AbsolutePath) == "/ZOONGG/codex-swap-account/releases/download/" + tag + "/" + name;

    public async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellation = default)
        => await DownloadAssetAsync(false, progress, cancellation).ConfigureAwait(false);

    public Task<string> DownloadInstallerAsync(IProgress<double>? progress = null, CancellationToken cancellation = default)
        => DownloadAssetAsync(true, progress, cancellation);

    private async Task<string> DownloadAssetAsync(bool installer, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (!await downloadGate.WaitAsync(0, cancellation).ConfigureAwait(false)) throw new InvalidOperationException("Download already running.");
        string name = installer ? InstallerName : ZipName;
        string temp = Path.Combine(Path.GetTempPath(), "codex-update-" + Guid.NewGuid().ToString("N") + (installer ? ".exe" : ".zip"));
        try
        {
            var r = Cache.Release;
            if (!HasUpdate || r is null || !ValidRelease(r) || r.Checksums is null) throw new InvalidDataException("Missing official release assets.");
            string url = (installer ? r.Installer : r.Zip) ?? throw new InvalidDataException("Missing official release asset.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            byte[] sums = await ReadAsync(new Uri(r.Checksums), 64 * 1024, timeout.Token).ConfigureAwait(false);
            var matches = Regex.Matches(Encoding.UTF8.GetString(sums), @"(?m)^([a-fA-F0-9]{64}) [ *]" + Regex.Escape(name) + @"\r?$");
            if (matches.Count != 1) throw new InvalidDataException("Missing or malformed SHA-256 checksum.");
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await TransferAsync(new Uri(url), file, 512L * 1024 * 1024, progress, timeout.Token).ConfigureAwait(false);
            await using var input = File.OpenRead(temp);
            byte[] actual = await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(matches[0].Groups[1].Value))) throw new InvalidDataException("SHA-256 mismatch.");
            cancellation.ThrowIfCancellationRequested();
            if (installer) verifiedInstallers[temp] = actual;
            return temp;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally { downloadGate.Release(); }
    }

    public async Task LaunchInstallerAsync(string path, Func<bool> mayInstall, CancellationToken cancellation = default)
    {
        if (!await downloadGate.WaitAsync(0, cancellation)) throw new InvalidOperationException("Update already running.");
        try
        {
            if (!verifiedInstallers.TryRemove(path, out var expected)) throw new InvalidDataException("Installer was not verified.");
            // Deny writes/deletion between revalidation and CreateProcess.
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var actual = await SHA256.HashDataAsync(file, cancellation);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Installer changed after verification.");
            cancellation.ThrowIfCancellationRequested();
            if (!mayInstall()) throw new InvalidOperationException("Installation is unavailable during account switching or in portable mode.");
            using var process = System.Diagnostics.Process.Start(InstallerLaunch.Create(path))
                ?? throw new IOException("Could not start installer.");
        }
        finally { downloadGate.Release(); }
    }

    private async Task<byte[]> ReadAsync(Uri uri, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await TransferAsync(uri, buffer, limit, null, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task TransferAsync(Uri uri, Stream output, long limit, IProgress<double>? progress, CancellationToken ct)
    {
        for (int redirects = 0; redirects < 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CodexSwapAccount-UpdateChecker/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var next = response.Headers.Location is { } location ? new Uri(uri, location) : throw new InvalidDataException("Invalid redirect.");
                if (next.Scheme != "https" || !next.IsDefaultPort || next.UserInfo != "" || next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("Unsafe redirect.");
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            if (length > limit) throw new InvalidDataException("Download too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > limit) throw new InvalidDataException("Download too large.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(length > 0 ? total * 100d / length.Value : 0);
            }
            if (length.HasValue && total != length) throw new InvalidDataException("Incomplete download.");
            return;
        }
        throw new InvalidDataException("Too many redirects.");
    }

    private bool SaveCache()
    {
        lock (cacheWriteLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
                File.WriteAllText(cachePath + ".tmp", JsonSerializer.Serialize(Cache));
                File.Move(cachePath + ".tmp", cachePath, true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }
    public void Dispose()
    {
        CancelBackgroundDownload();
        http.Dispose();
    }
}
