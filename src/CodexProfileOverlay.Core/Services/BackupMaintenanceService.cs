using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public sealed class BackupMaintenanceService
{
    public const long MaximumFileBytes = 10L * 1024 * 1024;
    public const long MaximumSwitchBackupBytes = 25L * 1024 * 1024;
    public const int MaximumCompletedBackups = 5;
    public const long MaximumCompletedStorageBytes = 100L * 1024 * 1024;

    private static readonly TimeSpan CompletedMaximumAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan TemporaryMaximumAge = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex LegacyStateName = new(
        "^state-[0-9]{8}-[0-9]{6}-[0-9]{3}-[0-9a-f]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly AppPaths paths;
    private readonly SafeLogger? logger;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly BackupRetentionPolicy policy;

    public BackupMaintenanceService(
        AppPaths paths,
        SafeLogger? logger = null,
        Func<DateTimeOffset>? utcNow = null,
        BackupRetentionPolicy? policy = null)
    {
        this.paths = paths;
        this.logger = logger;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.policy = policy ?? BackupRetentionPolicy.Default;
    }

    public SwitchBackup CreateSwitchBackup(string previousAuthFile, string targetAuthFile, string? previousProfile)
    {
        FileSafety previous = ValidateAuthenticationFile(previousAuthFile, "previous-auth.json");
        FileSafety target = ValidateAuthenticationFile(targetAuthFile, "target-auth.json");
        string metadata = previousProfile ?? string.Empty;
        long estimatedSize = previous.Size + Encoding.UTF8.GetByteCount(metadata) + 16 * 1024;
        if (estimatedSize > policy.MaximumSwitchBackupBytes)
        {
            throw new InvalidDataException($"Switch backup would exceed {policy.MaximumSwitchBackupBytes} bytes.");
        }

        DesktopSnapshot.RejectLinks(paths.BackupDirectory);
        Directory.CreateDirectory(paths.BackupDirectory);
        string id = $"{utcNow():yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        string directory = Path.Combine(paths.BackupDirectory, $"txn-{id}");
        Directory.CreateDirectory(directory);
        try
        {
            string rollbackAuth = Path.Combine(directory, "previous-auth.json");
            File.Copy(previousAuthFile, rollbackAuth, overwrite: false);
            File.WriteAllText(Path.Combine(directory, "previous-active-profile.txt"), metadata, new UTF8Encoding(false));
            var manifest = new SwitchBackupManifest(
                Version: 1,
                State: "active",
                CreatedUtc: utcNow(),
                CompletedUtc: null,
                PreviousProfile: previousProfile,
                TargetAuthSha256: target.Sha256,
                PreviousAuthSha256: previous.Sha256,
                PreviousAuthBytes: previous.Size);
            WriteManifest(directory, manifest);
            EnsureAllowedSwitchBackup(directory);
            logger?.Info($"Created switch backup file previous-auth.json ({previous.Size} bytes) and manifest.json.");
            return new SwitchBackup(directory, rollbackAuth, manifest);
        }
        catch
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            throw;
        }
    }

    public string CompleteSwitchBackup(SwitchBackup backup)
    {
        SwitchBackupManifest completed = backup.Manifest with { State = "completed", CompletedUtc = utcNow() };
        WriteManifest(backup.DirectoryPath, completed);
        string completedPath = Path.Combine(
            paths.BackupDirectory,
            "completed-" + Path.GetFileName(backup.DirectoryPath)["txn-".Length..]);
        Directory.Move(backup.DirectoryPath, completedPath);
        TryCleanupRetention();
        return completedPath;
    }

    public void MarkRolledBack(SwitchBackup backup)
    {
        if (!Directory.Exists(backup.DirectoryPath))
        {
            return;
        }

        WriteManifest(backup.DirectoryPath, backup.Manifest with { State = "rolled-back", CompletedUtc = utcNow() });
        string destination = Path.Combine(
            paths.BackupDirectory,
            "completed-" + Path.GetFileName(backup.DirectoryPath)["txn-".Length..]);
        Directory.Move(backup.DirectoryPath, destination);
        TryCleanupRetention();
    }

    public void TryCleanupRetention()
    {
        try { CleanupRetention(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.Error("Backup retention could not be completed.", exception);
        }
    }

    public void CleanupRetention()
    {
        DesktopSnapshot.RejectLinks(paths.BackupDirectory);
        if (!Directory.Exists(paths.BackupDirectory))
        {
            return;
        }

        DateTimeOffset now = utcNow();
        foreach (string temporary in Directory.EnumerateDirectories(paths.BackupDirectory, "txn-*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(temporary)) continue;
            SwitchBackupManifest? manifest = ReadManifest(temporary);
            if (manifest is not null
                && !string.Equals(manifest.State, "active", StringComparison.OrdinalIgnoreCase)
                && now - manifest.CreatedUtc > TemporaryMaximumAge)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(temporary);
            }
        }

        List<BackupEntry> completed = EnumerateCompleted()
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
        foreach (BackupEntry expired in completed.Where(entry => now - entry.Timestamp > CompletedMaximumAge).ToArray())
        {
            DeleteEntry(expired);
            completed.Remove(expired);
        }

        while (completed.Count > policy.MaximumCompletedBackups)
        {
            BackupEntry oldest = completed[^1];
            DeleteEntry(oldest);
            completed.RemoveAt(completed.Count - 1);
        }

        long total = completed.Sum(entry => entry.Size);
        while (total > policy.MaximumCompletedStorageBytes && completed.Count > 0)
        {
            BackupEntry oldest = completed[^1];
            DeleteEntry(oldest);
            completed.RemoveAt(completed.Count - 1);
            total -= oldest.Size;
        }
    }

    public BackupStorageSummary GetStorageSummary()
    {
        DesktopSnapshot.RejectLinks(paths.BackupDirectory);
        if (!Directory.Exists(paths.BackupDirectory))
        {
            return new BackupStorageSummary(0, 0, policy.MaximumCompletedBackups, policy.MaximumCompletedStorageBytes);
        }

        BackupEntry[] completed = EnumerateCompleted().ToArray();
        long allBytes = GetDirectorySize(paths.BackupDirectory);
        return new BackupStorageSummary(allBytes, completed.Length, policy.MaximumCompletedBackups, policy.MaximumCompletedStorageBytes);
    }

    public LegacyBackupSummary InspectLegacyBackups()
    {
        LegacyEntry[] entries = EnumerateLegacy().OrderBy(entry => entry.Timestamp).ToArray();
        return new LegacyBackupSummary(
            entries.Length,
            entries.Sum(entry => entry.Size),
            entries.FirstOrDefault()?.Timestamp,
            entries.LastOrDefault()?.Timestamp,
            entries.OrderByDescending(entry => entry.Timestamp).Skip(2).Sum(entry => entry.Size));
    }

    public LegacyBackupSummary CleanLegacyBackups(int retainNewest = 2)
    {
        LegacyEntry[] entries = EnumerateLegacy().OrderByDescending(entry => entry.Timestamp).ToArray();
        foreach (LegacyEntry entry in entries.Skip(Math.Max(0, retainNewest)))
        {
            DeleteDirectoryWithoutFollowingReparsePoints(entry.Path);
            logger?.Info($"Deleted legacy backup {Path.GetFileName(entry.Path)} ({entry.Size} bytes).");
        }
        return InspectLegacyBackups();
    }

    public void CleanAllCompleted()
    {
        foreach (BackupEntry entry in EnumerateCompleted())
        {
            DeleteEntry(entry);
        }
    }

    public SwitchBackupManifest RestoreCompletedSwitchBackup(string backupDirectory, string sharedAuthFile, string activeProfileFile)
    {
        string fullBackup = Path.GetFullPath(backupDirectory);
        string backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.BackupDirectory));
        if (!string.Equals(Path.GetDirectoryName(fullBackup), backupRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullBackup).StartsWith("completed-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Rollback backup path is outside the recognized backup directory.");
        }

        DesktopSnapshot.RejectLinks(fullBackup);
        EnsureAllowedSwitchBackup(fullBackup);
        SwitchBackupManifest manifest = ReadManifest(fullBackup)
            ?? throw new InvalidDataException("Rollback manifest is missing or invalid.");
        string previousAuth = Path.Combine(fullBackup, "previous-auth.json");
        ValidateAuthenticationFile(previousAuth, "previous-auth.json");
        new AtomicFileReplacer().ReplaceFromSource(previousAuth, sharedAuthFile);
        RestoreActiveProfileMetadata(activeProfileFile, manifest.PreviousProfile);
        WriteManifest(fullBackup, manifest with { State = "rolled-back", CompletedUtc = utcNow() });
        logger?.Info($"Restored switch backup file previous-auth.json ({manifest.PreviousAuthBytes} bytes).");
        return manifest;
    }

    private FileSafety ValidateAuthenticationFile(string file, string sanitizedName)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"{sanitizedName} was not found.");
        }

        using FileStream stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            throw new InvalidDataException($"{sanitizedName} is empty.");
        }
        if (stream.Length > policy.MaximumFileBytes)
        {
            throw new InvalidDataException($"{sanitizedName} exceeds the {policy.MaximumFileBytes} byte safety limit.");
        }

        string hash = Convert.ToHexString(SHA256.HashData(stream));
        return new FileSafety(stream.Length, hash);
    }

    private void EnsureAllowedSwitchBackup(string directory)
    {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "previous-auth.json",
            "previous-active-profile.txt",
            "manifest.json",
        };
        string[] files = Directory.GetFileSystemEntries(directory);
        foreach (string file in files)
        {
            if (!allowed.Contains(Path.GetFileName(file))
                || (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Switch backup contains a forbidden entry.");
        }
        if (files.Any(file => new FileInfo(file).Length > policy.MaximumFileBytes))
        {
            throw new InvalidDataException("Switch backup contains a file above the safety limit.");
        }
        if (files.Sum(file => new FileInfo(file).Length) > policy.MaximumSwitchBackupBytes)
        {
            throw new InvalidDataException("Switch backup exceeds the total safety limit.");
        }
    }

    private IEnumerable<BackupEntry> EnumerateCompleted()
    {
        DesktopSnapshot.RejectLinks(paths.BackupDirectory);
        if (!Directory.Exists(paths.BackupDirectory))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(paths.BackupDirectory, "completed-*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(directory))
            {
                continue;
            }
            SwitchBackupManifest? manifest = ReadManifest(directory);
            if (manifest is not null && !string.Equals(manifest.State, "active", StringComparison.OrdinalIgnoreCase))
            {
                yield return new BackupEntry(directory, GetDirectorySize(directory), manifest.CompletedUtc ?? manifest.CreatedUtc, true);
            }
        }

        foreach (string file in Directory.EnumerateFiles(paths.BackupDirectory, "auth-*.json", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            yield return new BackupEntry(file, info.Length, info.LastWriteTimeUtc, false);
        }
    }

    private IEnumerable<LegacyEntry> EnumerateLegacy()
    {
        DesktopSnapshot.RejectLinks(paths.BackupDirectory);
        if (!Directory.Exists(paths.BackupDirectory))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(paths.BackupDirectory, "state-*", SearchOption.TopDirectoryOnly)
                     .Where(path => LegacyStateName.IsMatch(Path.GetFileName(path)))
                     .Where(path => !IsReparsePoint(path)))
        {
            var info = new DirectoryInfo(directory);
            yield return new LegacyEntry(directory, GetDirectorySize(directory), info.LastWriteTimeUtc);
        }
    }

    private void DeleteEntry(BackupEntry entry)
    {
        if (entry.IsDirectory)
        {
            DeleteDirectoryWithoutFollowingReparsePoints(entry.Path);
        }
        else
        {
            File.Delete(entry.Path);
        }
        logger?.Info($"Deleted completed backup {Path.GetFileName(entry.Path)} ({entry.Size} bytes).");
    }

    private static long GetDirectorySize(string directory)
    {
        long size = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsReparsePoint(file))
                {
                    size += new FileInfo(file).Length;
                }
            }
            foreach (string child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsReparsePoint(child))
                {
                    pending.Push(child);
                }
            }
        }
        return size;
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string directory)
    {
        DesktopSnapshot.RejectLinks(directory);
        foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(child) && !IsReparsePoint(child))
            {
                DeleteDirectoryWithoutFollowingReparsePoints(child);
            }
            else if (Directory.Exists(child))
            {
                Directory.Delete(child, recursive: false);
            }
            else
            {
                File.Delete(child);
            }
        }
        Directory.Delete(directory, recursive: false);
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void WriteManifest(string directory, SwitchBackupManifest manifest)
    {
        string target = Path.Combine(directory, "manifest.json");
        DesktopSnapshot.RejectLinks(target);
        string temporary = Path.Combine(directory, $".manifest-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RestoreActiveProfileMetadata(string activeProfileFile, string? previousProfile)
    {
        if (previousProfile is null)
        {
            if (File.Exists(activeProfileFile))
            {
                File.Delete(activeProfileFile);
            }
            return;
        }

        new ActiveProfileStore(activeProfileFile).Write(previousProfile);
    }

    private static SwitchBackupManifest? ReadManifest(string directory)
    {
        try
        {
            string manifest = Path.Combine(directory, "manifest.json");
            if (IsReparsePoint(manifest)) return null;
            return JsonSerializer.Deserialize<SwitchBackupManifest>(
                File.ReadAllText(manifest, Encoding.UTF8));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record FileSafety(long Size, string Sha256);
    private sealed record BackupEntry(string Path, long Size, DateTimeOffset Timestamp, bool IsDirectory);
    private sealed record LegacyEntry(string Path, long Size, DateTimeOffset Timestamp);
}

public sealed record BackupRetentionPolicy(
    long MaximumFileBytes,
    long MaximumSwitchBackupBytes,
    int MaximumCompletedBackups,
    long MaximumCompletedStorageBytes)
{
    public static BackupRetentionPolicy Default { get; } = new(
        BackupMaintenanceService.MaximumFileBytes,
        BackupMaintenanceService.MaximumSwitchBackupBytes,
        BackupMaintenanceService.MaximumCompletedBackups,
        BackupMaintenanceService.MaximumCompletedStorageBytes);
}

public sealed record SwitchBackup(string DirectoryPath, string PreviousAuthFile, SwitchBackupManifest Manifest);

public sealed record SwitchBackupManifest(
    int Version,
    string State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    string? PreviousProfile,
    string TargetAuthSha256,
    string PreviousAuthSha256,
    long PreviousAuthBytes);

public sealed record BackupStorageSummary(
    long TotalBytes,
    int CompletedBackupCount,
    int RetentionCountLimit,
    long RetentionStorageLimitBytes);

public sealed record LegacyBackupSummary(
    int DirectoryCount,
    long TotalBytes,
    DateTimeOffset? OldestTimestamp,
    DateTimeOffset? NewestTimestamp,
    long EstimatedReclaimBytes);
