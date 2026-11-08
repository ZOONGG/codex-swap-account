using System.Security.Cryptography;

namespace CodexProfileOverlay.Core.Services;

public sealed class AuthSwitchService
{
    private readonly AppPaths paths;
    private readonly ProfileDiscoveryService profileDiscovery;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly Action<string, string> replaceAuth;
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
        replaceAuth = replacer is null ? AtomicFileReplacer.ReplaceDurably : replacer.ReplaceFromSource;
        this.backups = backups ?? new BackupMaintenanceService(paths);
    }

    public bool HasPendingSwitch
    {
        get
        {
            try { return backups.FindPendingAuthSwitch() is not null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return true;
            }
        }
    }

    public void RecoverPendingSwitch()
    {
        if (!switchGate.Wait(0))
            throw new InvalidOperationException("A profile switch is already in progress.");

        try { RecoverPendingSwitchCore(); }
        finally { switchGate.Release(); }
    }

    public async Task<AuthSwitchResult> SwitchAsync(string targetProfileName, CancellationToken cancellationToken = default)
    {
        if (!await switchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A profile switch is already in progress.");

        try
        {
            RecoverPendingSwitchCore();
            var desktop = new DesktopSessionService(paths);
            desktop.RequireNoPendingOperation();
            if (desktop.RequiresDesktopSwitch(targetProfileName))
                throw new InvalidOperationException("A captured profile requires a Desktop session switch.");

            string? previous = activeProfileStore.ReadForSwitch();
            var target = profileDiscovery.GetRequiredProfile(targetProfileName);
            if (SameProfile(previous, target.Name))
                throw new InvalidOperationException("The selected profile is already active.");

            string? saveTo = previous is null ? null : profileDiscovery.GetRequiredProfile(previous).AuthFilePath;
            string completed = ExecuteSwitch(target.AuthFilePath, previous, target.Name, saveTo, backups.AuthHash(paths.SharedAuthFile));
            return new AuthSwitchResult(target.Name, previous, completed);
        }
        finally { switchGate.Release(); }
    }

    public void Rollback(AuthSwitchResult result)
    {
        if (!switchGate.Wait(0))
            throw new InvalidOperationException("A profile switch is already in progress.");

        try
        {
            RecoverPendingSwitchCore();
            new DesktopSessionService(paths).RequireNoPendingOperation();
            if (string.IsNullOrWhiteSpace(result.BackupPath))
                throw new InvalidOperationException("The switch result does not contain a rollback backup.");

            SwitchBackup backup = backups.ReadAuthRollbackSource(result.BackupPath);
            SwitchBackupManifest manifest = backup.Manifest;
            string? active = activeProfileStore.ReadForSwitch();
            if (manifest.State != "completed" || !SameProfile(active, manifest.Recovery!.TargetProfile)
                || !SameProfile(active, result.TargetProfile) || !SameProfile(manifest.PreviousProfile, result.PreviousProfile)
                || (manifest.PreviousProfile is { } previous
                    && backups.AuthHash(profileDiscovery.GetRequiredProfile(previous).AuthFilePath) != manifest.PreviousAuthSha256))
                throw new InvalidDataException("Authorization rollback no longer matches the active switch.");

            ExecuteSwitch(backup.PreviousAuthFile, active, manifest.PreviousProfile, null,
                manifest.TargetAuthSha256, manifest.PreviousAuthSha256);
        }
        finally { switchGate.Release(); }
    }

    private string ExecuteSwitch(string source, string? previous, string? target, string? saveTo,
        string expectedSharedHash, string? expectedTargetHash = null)
    {
        using var targetLock = backups.OpenAuthForSwitch(source);
        if (expectedTargetHash is not null && Convert.ToHexString(SHA256.HashData(targetLock)) != expectedTargetHash)
            throw new InvalidDataException("Rollback authorization no longer matches its backup.");
        string? savedHash = saveTo is null ? null : backups.AuthHash(saveTo);
        SwitchBackup backup = backups.PrepareAuthSwitch(source, previous, target, savedHash, expectedSharedHash);
        try
        {
            if (!SameProfile(activeProfileStore.ReadForSwitch(), previous)
                || backups.AuthHash(paths.SharedAuthFile) != expectedSharedHash
                || (saveTo is not null && backups.AuthHash(saveTo) != savedHash))
                throw new InvalidDataException("The previous profile changed during the switch.");
            if (saveTo is not null)
                replaceAuth(backup.PreviousAuthFile, saveTo);

            if (!SameProfile(activeProfileStore.ReadForSwitch(), previous)
                || backups.AuthHash(paths.SharedAuthFile) != expectedSharedHash)
                throw new InvalidDataException("Authorization changed during the switch.");

            replaceAuth(source, paths.SharedAuthFile);
            // In normal execution the replacement is known to have run, even for identical auth files.
            return Reconcile(backup, replacementReturned: true);
        }
        catch (Exception switchError)
        {
            try { RecoverPendingSwitchCore(); }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Authorization switch recovery is required.", switchError, recoveryError);
            }
            throw;
        }
    }

    private void RecoverPendingSwitchCore()
    {
        string? pending = backups.FindPendingAuthSwitch();
        if (pending is null) return;
        if (new DesktopSessionService(paths).HasPendingOperation)
            throw new InvalidDataException("Concurrent Desktop and authorization transactions require manual recovery.");

        Reconcile(backups.ReadAuthSwitch(pending));
    }

    private string Reconcile(SwitchBackup backup, bool replacementReturned = false)
    {
        SwitchBackupManifest manifest = backup.Manifest;
        SwitchRecoveryDetails recovery = manifest.Recovery!;
        string? marker = activeProfileStore.ReadForSwitch();
        bool previousMarker = SameProfile(marker, manifest.PreviousProfile);
        bool targetMarker = SameProfile(marker, recovery.TargetProfile);

        // Deny writes and replacement of shared auth while deciding and updating the marker.
        using var shared = backups.OpenAuthForSwitch(paths.SharedAuthFile);
        string hash = Convert.ToHexString(SHA256.HashData(shared));
        bool matchesPrevious = hash == manifest.PreviousAuthSha256;
        bool matchesTarget = hash == manifest.TargetAuthSha256;

        if (manifest.State == "completed" && targetMarker && (!matchesPrevious || matchesTarget))
            return backups.FinishAuthSwitch(backup, committed: true);
        if (manifest.State == "rolled-back" && previousMarker && (!matchesTarget || matchesPrevious))
            return backups.FinishAuthSwitch(backup, committed: false);

        using var saved = recovery.PreviousProfileAuthSha256 is null ? null
            : backups.OpenAuthForSwitch(profileDiscovery.GetRequiredProfile(manifest.PreviousProfile!).AuthFilePath);
        string? savedHash = saved is null ? null : Convert.ToHexString(SHA256.HashData(saved));
        bool savedPrevious = saved is null || savedHash == manifest.PreviousAuthSha256;

        if (manifest.State == "active")
        {
            if (!replacementReturned && previousMarker && matchesPrevious
                && (savedPrevious || savedHash == recovery.PreviousProfileAuthSha256))
                return backups.FinishAuthSwitch(backup, committed: false);

            if ((previousMarker || targetMarker) && matchesTarget && savedPrevious)
            {
                if (previousMarker)
                {
                    using var target = recovery.TargetProfile is null ? null
                        : backups.OpenAuthForSwitch(profileDiscovery.GetRequiredProfile(recovery.TargetProfile).AuthFilePath);
                    if (target is not null && Convert.ToHexString(SHA256.HashData(target)) != manifest.TargetAuthSha256)
                        throw new InvalidDataException("Target profile authorization changed during the switch.");
                    activeProfileStore.WriteForSwitch(manifest.PreviousProfile, recovery.TargetProfile);
                }
                return backups.FinishAuthSwitch(backup, committed: true);
            }
        }

        throw new InvalidDataException("Authorization recovery cannot identify a consistent account. The journal was preserved.");
    }

    private static bool SameProfile(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
