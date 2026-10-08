using System.Diagnostics;

namespace CodexProfileOverlay.Core.Services;

public sealed class LoginRuntimeUnavailableException : Exception { }

public interface IProfileLoginRuntime
{
    Task<bool> LoginAsync(string directory, CancellationToken cancellationToken);
}

public sealed class OfficialProfileLoginRuntime : IProfileLoginRuntime
{
    private readonly Func<string?> locate;
    public OfficialProfileLoginRuntime(Func<string?>? locate = null) => this.locate = locate ?? CodexCliLocator.FindExecutable;

    public async Task<bool> LoginAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string executable = locate() ?? throw new LoginRuntimeUnavailableException();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        info.ArgumentList.Add("login");
        info.Environment["CODEX_HOME"] = directory;
        // Inherited automation credentials must never select a different account.
        foreach (string key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" }) info.Environment.Remove(key);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new LoginRuntimeUnavailableException(); }
        Task output = DrainAsync(process.StandardOutput);
        Task error = DrainAsync(process.StandardError);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode == 0 && File.Exists(Path.Combine(directory, "auth.json"));
    }

    internal static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
    }
}

/// <summary>Only committed auth files enter the profile root. Login never touches shared history.</summary>
public sealed class ProfileLoginService
{
    private readonly AppPaths paths;
    private readonly ProfileManagerService manager;
    private readonly IProfileLoginRuntime runtime;
    private readonly IAtomicFileReplacer replacer;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ProfileLoginService(AppPaths paths, ProfileManagerService manager, IProfileLoginRuntime runtime,
        IAtomicFileReplacer? replacer = null)
    {
        this.paths = paths; this.manager = manager; this.runtime = runtime;
        this.replacer = replacer ?? new AtomicFileReplacer();
    }

    public async Task<bool> LoginAsync(string name, bool reauthorize,
        Func<CancellationToken, Task<bool>>? prepareActive, CancellationToken cancellationToken)
    {
        name = ProfileName.RequireValid(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string staging = Path.Combine(paths.ApplicationDataDirectory, "login-staging", Guid.NewGuid().ToString("N"));
        bool retainRecovery = false;
        try
        {
            string destination = Path.Combine(paths.ProfilesDirectory, name);
            string auth = Path.Combine(destination, "auth.json");
            RejectLinks(destination);
            if (reauthorize ? !File.Exists(auth) : Directory.Exists(destination))
                throw new InvalidOperationException("Profile changed. Refresh the account list and retry.");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
            if (!await runtime.LoginAsync(staging, cancellationToken).ConfigureAwait(false)) return false;
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(destination);
            string newAuth = Path.Combine(staging, "auth.json");
            if (!File.Exists(newAuth)) return false;
            var activeStore = new ActiveProfileStore(paths.ActiveProfileFile);
            bool active = reauthorize && string.Equals(activeStore.Read(), name, StringComparison.OrdinalIgnoreCase);
            if (active && (prepareActive is null || !await prepareActive(cancellationToken).ConfigureAwait(false))) return false;
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(destination);
            if (active != (reauthorize && string.Equals(activeStore.Read(), name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Active profile changed during login.");
            if (reauthorize)
            {
                string oldAuth = Path.Combine(staging, "previous-auth.json");
                File.Copy(auth, oldAuth);
                string oldShared = Path.Combine(staging, "previous-shared-auth.json");
                bool hadShared = File.Exists(paths.SharedAuthFile);
                if (active && hadShared) File.Copy(paths.SharedAuthFile, oldShared);
                var backups = new BackupMaintenanceService(paths);
                SwitchBackup profileBackup = backups.CreateSwitchBackup(auth, newAuth, name);
                SwitchBackup? sharedBackup = active && hadShared
                    ? backups.CreateSwitchBackup(paths.SharedAuthFile, newAuth, name) : null;
                try
                {
                    replacer.ReplaceFromSource(newAuth, auth);
                    if (active) replacer.ReplaceFromSource(newAuth, paths.SharedAuthFile);
                    backups.CompleteSwitchBackup(profileBackup);
                    if (sharedBackup is not null) backups.CompleteSwitchBackup(sharedBackup);
                }
                catch
                {
                    try
                    {
                        new AtomicFileReplacer().ReplaceFromSource(oldAuth, auth);
                        if (active && hadShared) new AtomicFileReplacer().ReplaceFromSource(oldShared, paths.SharedAuthFile);
                        else if (active) File.Delete(paths.SharedAuthFile);
                    }
                    catch { retainRecovery = true; throw; }
                    throw;
                }
            }
            else
            {
                // Recheck after the browser flow; never replace an existing profile.
                if (Directory.Exists(destination)) throw new InvalidOperationException("Profile already exists.");
                string created = manager.CreateProfileDirectory(name);
                try { replacer.ReplaceFromSource(newAuth, Path.Combine(created, "auth.json")); }
                catch { manager.RemoveIncompleteProfileDirectory(name); throw; }
            }
            return true;
        }
        finally
        {
            try
            {
                if (!retainRecovery && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            finally { gate.Release(); }
        }
    }

    private void RejectLinks(string destination)
    {
        // Configured roots may be relocated with junctions. Profile/auth links remain forbidden.
        foreach (string path in new[] { destination, Path.Combine(destination, "auth.json"), paths.SharedAuthFile })
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Authorization paths must not be symbolic links or junctions.");
        }
    }
}
