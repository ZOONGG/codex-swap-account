namespace CodexProfileOverlay.Core.Services;

public sealed class AuthSwitchService
{
    private readonly AppPaths paths;
    private readonly ProfileDiscoveryService profileDiscovery;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly IAtomicFileReplacer replacer;
    private readonly BackupMaintenanceService backups;
    private readonly SemaphoreSlim switchGate = new(1, 1);

    public AuthSwitchService(
        AppPaths paths,
        ProfileDiscoveryService profileDiscovery,
        ActiveProfileStore activeProfileStore,
        IAtomicFileReplacer? replacer = null,
        BackupMaintenanceService? backups = null)
    {
        this.paths = paths;
        this.profileDiscovery = profileDiscovery;
        this.activeProfileStore = activeProfileStore;
        this.replacer = replacer ?? new AtomicFileReplacer();
        this.backups = backups ?? new BackupMaintenanceService(paths);
    }

    public async Task<AuthSwitchResult> SwitchAsync(string targetProfileName, CancellationToken cancellationToken = default)
    {
        if (!await switchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A profile switch is already in progress.");
        }

        SwitchBackup? backup = null;
        string? previousProfile = null;

        try
        {
            var desktop = new DesktopSessionService(paths);
            desktop.RequireNoPendingOperation();
            if (desktop.RequiresDesktopSwitch(targetProfileName))
                throw new InvalidOperationException("A captured profile requires a Desktop session switch.");
            previousProfile = activeProfileStore.Read();
            var targetProfile = profileDiscovery.GetRequiredProfile(targetProfileName);
            if (string.Equals(previousProfile, targetProfile.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected profile is already active.");
            }

            if (!File.Exists(targetProfile.AuthFilePath))
            {
                throw new FileNotFoundException($"Profile '{targetProfile.Name}' does not contain auth.json.", targetProfile.AuthFilePath);
            }

            if (!File.Exists(paths.SharedAuthFile))
            {
                throw new FileNotFoundException("The shared auth.json does not exist.", paths.SharedAuthFile);
            }

            backup = backups.CreateSwitchBackup(paths.SharedAuthFile, targetProfile.AuthFilePath, previousProfile);
            try
            {
                if (previousProfile is not null)
                {
                    var currentProfile = profileDiscovery.GetRequiredProfile(previousProfile);
                    replacer.ReplaceFromSource(paths.SharedAuthFile, currentProfile.AuthFilePath);
                }
                replacer.ReplaceFromSource(targetProfile.AuthFilePath, paths.SharedAuthFile);
                activeProfileStore.Write(targetProfile.Name);
                string completedBackup = backups.CompleteSwitchBackup(backup);
                return new AuthSwitchResult(targetProfile.Name, previousProfile, completedBackup);
            }
            catch (Exception switchError)
            {
                try
                {
                    RestoreBackup(backup, previousProfile);
                    backups.MarkRolledBack(backup);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("The authorization switch and its rollback failed.", switchError, rollbackError);
                }
                throw;
            }
        }
        finally
        {
            switchGate.Release();
        }
    }

    public void Rollback(AuthSwitchResult result)
    {
        new DesktopSessionService(paths).RequireNoPendingOperation();
        if (string.IsNullOrWhiteSpace(result.BackupPath))
        {
            throw new InvalidOperationException("The switch result does not contain a rollback backup.");
        }

        backups.RestoreCompletedSwitchBackup(result.BackupPath, paths.SharedAuthFile, paths.ActiveProfileFile);
    }

    private void RestoreBackup(SwitchBackup backup, string? previousProfile)
    {
        DesktopSnapshot.RejectLinks(backup.PreviousAuthFile);
        new AtomicFileReplacer().ReplaceFromSource(backup.PreviousAuthFile, paths.SharedAuthFile);
        RestoreActiveProfile(previousProfile);
        if (previousProfile is not null)
        {
            string profileAuth = profileDiscovery.GetRequiredProfile(previousProfile).AuthFilePath;
            new AtomicFileReplacer().ReplaceFromSource(backup.PreviousAuthFile, profileAuth);
        }
    }

    private void RestoreActiveProfile(string? previousProfile)
    {
        if (previousProfile is not null)
        {
            activeProfileStore.Write(previousProfile);
        }
        else if (File.Exists(paths.ActiveProfileFile))
        {
            File.Delete(paths.ActiveProfileFile);
        }
    }
}
