using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class AuthSwitchService
{
    private static readonly string CodexDesktopDefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Roaming", "Codex", "web", "Codex", "Default");

    private readonly AppPaths paths;
    private readonly ProfileDiscoveryService profileDiscovery;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly IAtomicFileReplacer replacer;
    private readonly BackupMaintenanceService backups;
    private readonly SemaphoreSlim switchGate = new(1, 1);

    private sealed record ChromiumSwapRecord(
        string? PreviousSlot,
        string? ArchivedLiveAt,
        string TargetSlot,
        string? RenamedExistingPreviousTo);

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
            ChromiumSwapRecord? chromiumSwap = null;
            try
            {
                chromiumSwap = SwapChromiumIfConfigured(previousProfile, targetProfile);
                if (previousProfile is not null)
                {
                    var currentProfile = profileDiscovery.GetRequiredProfile(previousProfile);
                    replacer.ReplaceFromSource(paths.SharedAuthFile, currentProfile.AuthFilePath);
                }
                replacer.ReplaceFromSource(targetProfile.AuthFilePath, paths.SharedAuthFile);
                activeProfileStore.Write(targetProfile.Name);
            }
            catch
            {
                if (chromiumSwap is not null) RollbackChromiumSwap(chromiumSwap);
                RestoreBackupIfPossible(backup, previousProfile);
                RestoreActiveProfile(previousProfile);
                backups.MarkRolledBack(backup);
                throw;
            }

            string completedBackup = backups.CompleteSwitchBackup(backup);
            return new AuthSwitchResult(targetProfile.Name, previousProfile, completedBackup);
        }
        finally
        {
            switchGate.Release();
        }
    }

    public void Rollback(AuthSwitchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.BackupPath))
        {
            throw new InvalidOperationException("The switch result does not contain a rollback backup.");
        }

        backups.RestoreCompletedSwitchBackup(result.BackupPath, paths.SharedAuthFile, paths.ActiveProfileFile);
    }

    private void RestoreBackupIfPossible(SwitchBackup backup, string? previousProfile)
    {
        if (!File.Exists(backup.PreviousAuthFile))
        {
            return;
        }

        new AtomicFileReplacer().ReplaceFromSource(backup.PreviousAuthFile, paths.SharedAuthFile);
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

    private ChromiumSwapRecord? SwapChromiumIfConfigured(string? previousProfile, ProfileInfo targetProfile)
    {
        string targetSlot = Path.Combine(targetProfile.DirectoryPath, "web-default");
        if (!Directory.Exists(targetSlot))
        {
            return null;
        }

        string? previousSlot = null;
        if (previousProfile is not null)
        {
            ProfileInfo p = profileDiscovery.GetRequiredProfile(previousProfile);
            previousSlot = Path.Combine(p.DirectoryPath, "web-default");
        }

        string? archivedLiveAt = null;
        string? renamedExistingPreviousTo = null;

        if (Directory.Exists(CodexDesktopDefaultDir))
        {
            if (previousSlot is null)
            {
                throw new InvalidOperationException(
                    "Live Codex Desktop Chromium profile is present but no previous profile is recorded for archival.");
            }

            if (Directory.Exists(previousSlot))
            {
                string bak = previousSlot + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                Directory.Move(previousSlot, bak);
                renamedExistingPreviousTo = bak;
            }

            Directory.Move(CodexDesktopDefaultDir, previousSlot);
            archivedLiveAt = previousSlot;
        }

        Directory.Move(targetSlot, CodexDesktopDefaultDir);

        return new ChromiumSwapRecord(previousSlot, archivedLiveAt, targetSlot, renamedExistingPreviousTo);
    }

    private static void RollbackChromiumSwap(ChromiumSwapRecord record)
    {
        try
        {
            if (Directory.Exists(CodexDesktopDefaultDir))
            {
                if (Directory.Exists(record.TargetSlot))
                {
                    string bak = record.TargetSlot + ".rollback-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(record.TargetSlot, bak);
                }
                Directory.Move(CodexDesktopDefaultDir, record.TargetSlot);
            }

            if (record.ArchivedLiveAt is not null && Directory.Exists(record.ArchivedLiveAt))
            {
                Directory.Move(record.ArchivedLiveAt, CodexDesktopDefaultDir);
            }

            if (record.RenamedExistingPreviousTo is not null
                && record.PreviousSlot is not null
                && !Directory.Exists(record.PreviousSlot)
                && Directory.Exists(record.RenamedExistingPreviousTo))
            {
                Directory.Move(record.RenamedExistingPreviousTo, record.PreviousSlot);
            }
        }
        catch
        {
            // Best-effort rollback; outer catch still rethrows the original fault.
        }
    }
}
