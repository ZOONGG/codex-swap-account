namespace CodexProfileOverlay.Core.Services;

public sealed class AuthSwitchService
{
    private readonly AppPaths paths;
    private readonly ProfileDiscoveryService profileDiscovery;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly IAtomicFileReplacer replacer;
    private readonly SemaphoreSlim switchGate = new(1, 1);

    public AuthSwitchService(
        AppPaths paths,
        ProfileDiscoveryService profileDiscovery,
        ActiveProfileStore activeProfileStore,
        IAtomicFileReplacer? replacer = null)
    {
        this.paths = paths;
        this.profileDiscovery = profileDiscovery;
        this.activeProfileStore = activeProfileStore;
        this.replacer = replacer ?? new AtomicFileReplacer();
    }

    public async Task<AuthSwitchResult> SwitchAsync(string targetProfileName, CancellationToken cancellationToken = default)
    {
        if (!await switchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A profile switch is already in progress.");
        }

        string? backupPath = null;
        string? previousProfile = null;

        try
        {
            previousProfile = activeProfileStore.Read();
            var targetProfile = profileDiscovery.GetRequiredProfile(targetProfileName);

            if (previousProfile is not null && File.Exists(paths.SharedAuthFile))
            {
                var currentProfile = profileDiscovery.GetRequiredProfile(previousProfile);
                File.Copy(paths.SharedAuthFile, currentProfile.AuthFilePath, overwrite: true);
            }

            if (!File.Exists(targetProfile.AuthFilePath))
            {
                throw new FileNotFoundException($"Profile '{targetProfile.Name}' does not contain auth.json.", targetProfile.AuthFilePath);
            }

            if (File.Exists(paths.SharedAuthFile))
            {
                Directory.CreateDirectory(paths.BackupDirectory);
                backupPath = Path.Combine(
                    paths.BackupDirectory,
                    $"auth-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
                File.Copy(paths.SharedAuthFile, backupPath, overwrite: false);
            }

            try
            {
                replacer.ReplaceFromSource(targetProfile.AuthFilePath, paths.SharedAuthFile);
                activeProfileStore.Write(targetProfile.Name);
            }
            catch
            {
                RestoreBackupIfPossible(backupPath);
                throw;
            }

            return new AuthSwitchResult(targetProfile.Name, previousProfile, backupPath);
        }
        finally
        {
            switchGate.Release();
        }
    }

    private void RestoreBackupIfPossible(string? backupPath)
    {
        if (backupPath is null || !File.Exists(backupPath))
        {
            return;
        }

        new AtomicFileReplacer().ReplaceFromSource(backupPath, paths.SharedAuthFile);
    }
}
