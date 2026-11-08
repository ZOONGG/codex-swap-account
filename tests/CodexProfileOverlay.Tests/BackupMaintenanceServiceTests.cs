using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class BackupMaintenanceServiceTests
{
    [Fact]
    public void CreateSwitchBackup_ContainsOnlyAllowedFilesAndNeverCopiesWorkspace()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        temp.WriteSharedState("large-shared-state");
        string attachments = Path.Combine(temp.Paths.SharedCodexDirectory, "attachments", "upload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(attachments)!);
        File.WriteAllText(attachments, "attachment");

        SwitchBackup backup = CreateService(temp).CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current");

        string[] relativeFiles = Directory.EnumerateFiles(backup.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(backup.DirectoryPath, file))
            .Order()
            .ToArray();
        Assert.Equal(new[] { "manifest.json", "previous-active-profile.txt", "previous-auth.json" }, relativeFiles);
        Assert.DoesNotContain(relativeFiles, file => file.Contains("session", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(relativeFiles, file => file.Contains("rollout", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(relativeFiles, file => file.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(relativeFiles, file => file.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        Assert.True(DirectorySize(backup.DirectoryPath) < 1024 * 1024);
    }

    [Fact]
    public void CreateSwitchBackup_RejectsIndividualFileAboveTenMegabytes()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        using (FileStream stream = File.Create(temp.Paths.SharedAuthFile))
        {
            stream.SetLength(BackupMaintenanceService.MaximumFileBytes + 1);
        }

        Assert.Throws<InvalidDataException>(() => CreateService(temp).CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current"));
        Assert.False(Directory.Exists(temp.Paths.BackupDirectory));
    }

    [Fact]
    public void CreateSwitchBackup_RejectsTotalAboveConfiguredEquivalentOfTwentyFiveMegabytes()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", new string('t', 4_000));
        temp.WriteSharedAuth(new string('p', 5_000));
        var policy = new BackupRetentionPolicy(10_000, 20_000, 5, 100_000);
        var service = new BackupMaintenanceService(temp.Paths, policy: policy);

        Assert.Throws<InvalidDataException>(() => service.CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current"));
        Assert.Empty(Directory.Exists(temp.Paths.BackupDirectory)
            ? Directory.EnumerateFileSystemEntries(temp.Paths.BackupDirectory)
            : []);
    }

    [Fact]
    public void CleanupRetention_KeepsAtMostFiveCompletedBackups()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        DateTimeOffset now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var service = new BackupMaintenanceService(temp.Paths, utcNow: () => now);
        for (int index = 0; index < 7; index++)
        {
            service.CompleteSwitchBackup(service.CreateSwitchBackup(
                temp.Paths.SharedAuthFile,
                Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
                "current"));
            now = now.AddMinutes(1);
        }

        Assert.Equal(5, service.GetStorageSummary().CompletedBackupCount);
    }

    [Fact]
    public void CleanupRetention_EnforcesTotalStorageLimit()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth(new string('p', 3_000));
        DateTimeOffset now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var policy = new BackupRetentionPolicy(10_000, 25_000, 10, 8_000);
        var service = new BackupMaintenanceService(temp.Paths, utcNow: () => now, policy: policy);
        for (int index = 0; index < 4; index++)
        {
            service.CompleteSwitchBackup(service.CreateSwitchBackup(
                temp.Paths.SharedAuthFile,
                Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
                "current"));
            now = now.AddMinutes(1);
        }

        BackupStorageSummary summary = service.GetStorageSummary();
        Assert.True(summary.CompletedBackupCount < 4);
        Assert.True(CompletedSize(temp.Paths.BackupDirectory) <= policy.MaximumCompletedStorageBytes);
    }

    [Fact]
    public void CleanupRetention_RemovesCompletedBackupsOlderThanSevenDays()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var service = new BackupMaintenanceService(temp.Paths, utcNow: () => now);
        service.CompleteSwitchBackup(service.CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current"));
        now = now.AddDays(8);

        service.CleanupRetention();

        Assert.Equal(0, service.GetStorageSummary().CompletedBackupCount);
    }

    [Fact]
    public void CleanupRetention_DoesNotTraverseTransactionJunction()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var service = new BackupMaintenanceService(temp.Paths, utcNow: () => now);
        string completed = service.CompleteSwitchBackup(service.CreateSwitchBackup(
            temp.Paths.SharedAuthFile, Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"), "current"));
        string external = Path.Combine(temp.Paths.SharedCodexDirectory, "unrelated");
        Directory.Move(completed, external);
        string sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        string link = Path.Combine(temp.Paths.BackupDirectory, "txn-junction");
        using var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{external}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        command.WaitForExit();
        Assert.Equal(0, command.ExitCode);
        now = now.AddDays(2);

        try
        {
            service.CleanupRetention();

            Assert.True(File.Exists(sentinel));
            Assert.True(File.Exists(Path.Combine(external, "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public async Task SwitchAsync_WhenRetentionCannotDeleteExpiredBackup_KeepsCompletedSwitch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        Directory.CreateDirectory(temp.Paths.BackupDirectory);
        string expired = Path.Combine(temp.Paths.BackupDirectory, "auth-expired.json");
        File.WriteAllText(expired, "expired");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-10));
        using var locked = File.Open(expired, FileMode.Open, FileAccess.Read, FileShare.Read);
        var service = temp.CreateSwitchService();

        AuthSwitchResult result = await service.SwitchAsync("target");

        Assert.True(Directory.Exists(result.BackupPath));
        Assert.Equal("target-auth", temp.ReadSharedAuth());
        service.Rollback(result);
        Assert.Equal("previous-auth", temp.ReadSharedAuth());
    }

    [Fact]
    public void Cleanup_NeverDeletesActiveTransaction()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var service = new BackupMaintenanceService(temp.Paths, utcNow: () => now);
        SwitchBackup active = service.CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current");
        now = now.AddDays(10);

        service.CleanupRetention();
        service.CleanAllCompleted();

        Assert.True(Directory.Exists(active.DirectoryPath));
    }

    [Fact]
    public void Cleanup_RemovesOnlyRecognizedAbandonedNonActiveTransactions()
    {
        using var temp = new TestLayout();
        temp.AddProfile("target", "target-auth");
        temp.WriteSharedAuth("previous-auth");
        DateTimeOffset now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var service = new BackupMaintenanceService(temp.Paths, utcNow: () => now);
        SwitchBackup abandoned = service.CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current");
        string manifest = Path.Combine(abandoned.DirectoryPath, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"active\"", "\"preparing\"", StringComparison.Ordinal));
        string unknown = Path.Combine(temp.Paths.BackupDirectory, "unknown-temporary-data");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        now = now.AddDays(2);

        service.CleanupRetention();

        Assert.False(Directory.Exists(abandoned.DirectoryPath));
        Assert.True(Directory.Exists(unknown));
    }

    [Fact]
    public void LegacyCleanup_PreservesTwoNewestAndNeverTouchesCodexRoots()
    {
        using var temp = new TestLayout();
        string sharedSentinel = Path.Combine(temp.Paths.SharedCodexDirectory, "sentinel.txt");
        string profilesSentinel = Path.Combine(temp.Paths.ProfilesDirectory, "sentinel.txt");
        File.WriteAllText(sharedSentinel, "shared");
        File.WriteAllText(profilesSentinel, "profiles");
        Directory.CreateDirectory(temp.Paths.BackupDirectory);
        DateTime start = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index < 4; index++)
        {
            string legacy = Path.Combine(temp.Paths.BackupDirectory, $"state-2026070{index + 1}-000000-000-{new string((char)('a' + index), 32)}");
            Directory.CreateDirectory(Path.Combine(legacy, "sessions"));
            File.WriteAllText(Path.Combine(legacy, "sessions", "rollout.jsonl"), new string('x', index + 1));
            Directory.SetLastWriteTimeUtc(legacy, start.AddDays(index));
        }
        Directory.CreateDirectory(Path.Combine(temp.Paths.BackupDirectory, "unknown-data"));

        BackupMaintenanceService service = CreateService(temp);
        LegacyBackupSummary before = service.InspectLegacyBackups();
        LegacyBackupSummary after = service.CleanLegacyBackups();

        Assert.Equal(4, before.DirectoryCount);
        Assert.True(before.EstimatedReclaimBytes > 0);
        Assert.Equal(2, after.DirectoryCount);
        Assert.True(Directory.Exists(Path.Combine(temp.Paths.BackupDirectory, $"state-20260704-000000-000-{new string('d', 32)}")));
        Assert.True(Directory.Exists(Path.Combine(temp.Paths.BackupDirectory, $"state-20260703-000000-000-{new string('c', 32)}")));
        Assert.True(Directory.Exists(Path.Combine(temp.Paths.BackupDirectory, "unknown-data")));
        Assert.Equal("shared", File.ReadAllText(sharedSentinel));
        Assert.Equal("profiles", File.ReadAllText(profilesSentinel));
    }

    [Fact]
    public void BackupLogs_NeverContainAuthorizationContentsOrTokens()
    {
        using var temp = new TestLayout();
        const string secret = "unique-secret-auth-value";
        temp.AddProfile("target", "target-" + secret);
        temp.WriteSharedAuth("previous-" + secret);
        var logger = new SafeLogger(temp.Paths.LogDirectory);
        var service = new BackupMaintenanceService(temp.Paths, logger);

        service.CompleteSwitchBackup(service.CreateSwitchBackup(
            temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"),
            "current"));

        string logs = string.Join(Environment.NewLine, Directory.EnumerateFiles(temp.Paths.LogDirectory).Select(File.ReadAllText));
        Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("previous-" + secret, logs, StringComparison.Ordinal);
        Assert.Contains("previous-auth.json", logs, StringComparison.Ordinal);
    }

    private static BackupMaintenanceService CreateService(TestLayout temp)
        => new(temp.Paths);

    private static long DirectorySize(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private static long CompletedSize(string backupDirectory)
        => Directory.Exists(backupDirectory)
            ? Directory.EnumerateDirectories(backupDirectory, "completed-*")
                .Sum(DirectorySize)
            : 0;
}
