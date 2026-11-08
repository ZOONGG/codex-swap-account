using System.Security.Cryptography;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class AuthSwitchRecoveryTests
{
    [Theory]
    [InlineData(0)] // Journal published, no credentials changed.
    [InlineData(1)] // Previous profile saved.
    [InlineData(2)] // Shared auth installed, marker still previous.
    [InlineData(3)] // Target marker installed.
    [InlineData(4)] // Terminal manifest written, directory still pending.
    [InlineData(5)] // Backup already archived, shared credentials refreshed.
    public void Recover_ReconcilesEachPersistedBoundaryAndCanRunTwice(int stage)
    {
        using var temp = CreateLayout();
        string journal = WriteJournal(temp, state: stage >= 4 ? "completed" : "active", archived: stage == 5);
        if (stage >= 1) temp.AddProfile("previous", "live-before");
        if (stage >= 2) temp.WriteSharedAuth("saved-target");
        if (stage >= 3) temp.ActiveProfileStore.Write("target");
        if (stage >= 5) temp.WriteSharedAuth("refreshed-target");

        temp.CreateSwitchService().RecoverPendingSwitch();
        temp.CreateSwitchService().RecoverPendingSwitch();

        Assert.Equal(stage >= 2 ? "target" : "previous", temp.ActiveProfileStore.Read());
        Assert.Equal(stage == 5 ? "refreshed-target" : stage >= 2 ? "saved-target" : "live-before", temp.ReadSharedAuth());
        Assert.Equal(stage >= 1 ? "live-before" : "saved-before", temp.ReadProfileAuth("previous"));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Paths.BackupDirectory, "auth-pending-*"));
        string archived = Assert.Single(Directory.GetDirectories(temp.Paths.BackupDirectory, "completed-*"));
        var manifest = JsonSerializer.Deserialize<SwitchBackupManifest>(File.ReadAllText(Path.Combine(archived, "manifest.json")));
        Assert.Equal(stage >= 2 ? "completed" : "rolled-back", manifest!.State);
        Assert.Equal(stage == 5, Directory.Exists(journal));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("backup")]
    [InlineData("shared")]
    [InlineData("marker")]
    [InlineData("previous-profile")]
    [InlineData("target-profile")]
    public async Task Recover_BlocksAmbiguityAndPreservesEvidence(string damaged)
    {
        using var temp = CreateLayout();
        string journal = WriteJournal(temp);
        temp.AddProfile("previous", "live-before");
        temp.WriteSharedAuth("saved-target");
        switch (damaged)
        {
            case "manifest": File.WriteAllText(Path.Combine(journal, "manifest.json"), "{broken"); break;
            case "backup": File.WriteAllText(Path.Combine(journal, "previous-auth.json"), "different"); break;
            case "shared":
                temp.ActiveProfileStore.Write("target");
                temp.WriteSharedAuth("newer-confirmed-auth");
                break;
            case "marker": File.WriteAllText(temp.Paths.ActiveProfileFile, "invalid/name"); break;
            case "previous-profile": temp.AddProfile("previous", "newer-previous"); break;
            case "target-profile": temp.AddProfile("target", "newer-target"); break;
        }

        string shared = temp.ReadSharedAuth();
        string marker = File.ReadAllText(temp.Paths.ActiveProfileFile);
        string evidence = File.ReadAllText(Path.Combine(journal, "manifest.json"));
        new BackupMaintenanceService(temp.Paths).CleanupRetention();
        await Assert.ThrowsAsync<InvalidDataException>(() => temp.CreateSwitchService().SwitchAsync("target"));
        Assert.Throws<InvalidDataException>(() => temp.CreateSwitchService().RecoverPendingSwitch());
        Assert.Equal(shared, temp.ReadSharedAuth());
        Assert.Equal(marker, File.ReadAllText(temp.Paths.ActiveProfileFile));
        Assert.Equal(evidence, File.ReadAllText(Path.Combine(journal, "manifest.json")));
    }

    [Fact]
    public void Recover_WhenSharedAuthIsLocked_CanRetryAfterUnlock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = CreateLayout();
        string journal = WriteJournal(temp);
        temp.AddProfile("previous", "live-before");
        temp.WriteSharedAuth("saved-target");

        using (File.Open(temp.Paths.SharedAuthFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => temp.CreateSwitchService().RecoverPendingSwitch());
            Assert.Equal("previous", temp.ActiveProfileStore.Read());
            Assert.True(Directory.Exists(journal));
        }

        temp.CreateSwitchService().RecoverPendingSwitch();
        Assert.Equal("target", temp.ActiveProfileStore.Read());
    }

    [Fact]
    public void Recover_WhenMarkerWriteIsLocked_RetainsInstalledAuthAndCanRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = CreateLayout();
        string journal = WriteJournal(temp);
        temp.AddProfile("previous", "live-before");
        temp.WriteSharedAuth("saved-target");
        using (File.Open(temp.Paths.ActiveProfileFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => temp.CreateSwitchService().RecoverPendingSwitch());
            Assert.Equal("saved-target", temp.ReadSharedAuth());
            Assert.True(Directory.Exists(journal));
        }

        temp.CreateSwitchService().RecoverPendingSwitch();
        Assert.Equal("target", temp.ActiveProfileStore.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_CompletesRealSwitchOrRollbackAfterMarkerWriteWasInterrupted(bool rollback)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = CreateLayout();
        var service = temp.CreateSwitchService();
        AuthSwitchResult? result = rollback ? await service.SwitchAsync("target") : null;
        string originalMarker = rollback ? "target" : "previous";
        string expectedMarker = rollback ? "previous" : "target";
        string expectedAuth = rollback ? "live-before" : "saved-target";

        using (File.Open(temp.Paths.ActiveProfileFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (result is null)
                await Assert.ThrowsAsync<AggregateException>(() => service.SwitchAsync("target"));
            else
                Assert.Throws<AggregateException>(() => service.Rollback(result));

            Assert.Equal(originalMarker, temp.ActiveProfileStore.Read());
            Assert.Equal(expectedAuth, temp.ReadSharedAuth());
            Assert.Single(Directory.GetDirectories(temp.Paths.BackupDirectory, "auth-pending-*"));
            Assert.Throws<InvalidOperationException>(() => new DesktopSessionService(temp.Paths).RequireNoPendingOperation());
        }

        temp.CreateSwitchService().RecoverPendingSwitch();
        temp.CreateSwitchService().RecoverPendingSwitch();
        Assert.Equal(expectedMarker, temp.ActiveProfileStore.Read());
        Assert.Equal(expectedAuth, temp.ReadSharedAuth());
        Assert.Empty(Directory.GetDirectories(temp.Paths.BackupDirectory, "auth-pending-*"));
    }

    [Fact]
    public void Recover_WhenTerminalManifestExists_PreservesRefreshedAuthorization()
    {
        using var temp = CreateLayout();
        WriteJournal(temp, state: "completed");
        temp.AddProfile("previous", "live-before");
        temp.ActiveProfileStore.Write("target");
        temp.WriteSharedAuth("confirmed-refreshed-target");

        temp.CreateSwitchService().RecoverPendingSwitch();

        Assert.Equal("confirmed-refreshed-target", temp.ReadSharedAuth());
        Assert.Equal("target", temp.ActiveProfileStore.Read());
        Assert.Empty(Directory.GetDirectories(temp.Paths.BackupDirectory, "auth-pending-*"));
    }

    [Fact]
    public void Recover_LegacyActiveBackupWithoutTarget_BlocksWithoutRestoring()
    {
        using var temp = CreateLayout();
        SwitchBackup legacy = new BackupMaintenanceService(temp.Paths).CreateSwitchBackup(temp.Paths.SharedAuthFile,
            Path.Combine(temp.Paths.ProfilesDirectory, "target", "auth.json"), "previous");
        temp.WriteSharedAuth("newer-auth");

        Assert.Throws<InvalidDataException>(() => temp.CreateSwitchService().RecoverPendingSwitch());

        Assert.Equal("newer-auth", temp.ReadSharedAuth());
        Assert.True(Directory.Exists(legacy.DirectoryPath));
    }

    [Fact]
    public async Task Rollback_WhenSharedAuthorizationWasRefreshed_RejectsStaleBackup()
    {
        using var temp = CreateLayout();
        var service = temp.CreateSwitchService();
        AuthSwitchResult result = await service.SwitchAsync("target");
        temp.WriteSharedAuth("newer-target-auth");

        Assert.Throws<InvalidDataException>(() => service.Rollback(result));

        Assert.Equal("target", temp.ActiveProfileStore.Read());
        Assert.Equal("newer-target-auth", temp.ReadSharedAuth());
    }

    [Fact]
    public async Task Rollback_FirstSwitch_RemovesMarker()
    {
        using var temp = CreateLayout();
        File.Delete(temp.Paths.ActiveProfileFile);
        var service = temp.CreateSwitchService();
        service.Rollback(await service.SwitchAsync("target"));
        service.RecoverPendingSwitch();

        Assert.Null(temp.ActiveProfileStore.Read());
        Assert.Equal("live-before", temp.ReadSharedAuth());
    }

    private static TestLayout CreateLayout()
    {
        var temp = new TestLayout();
        temp.AddProfile("previous", "saved-before");
        temp.AddProfile("target", "saved-target");
        temp.WriteSharedAuth("live-before");
        temp.ActiveProfileStore.Write("previous");
        return temp;
    }

    private static string WriteJournal(TestLayout temp, string state = "active", bool archived = false)
    {
        string directory = Path.Combine(temp.Paths.BackupDirectory, (archived ? "completed-" : "auth-pending-") + "fixture");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "previous-auth.json"), "live-before"u8.ToArray());
        File.WriteAllText(Path.Combine(directory, "previous-active-profile.txt"), "previous");
        var manifest = new SwitchBackupManifest(2, state, DateTimeOffset.UtcNow,
            state == "active" ? null : DateTimeOffset.UtcNow, "previous", Hash("saved-target"u8),
            Hash("live-before"u8), "live-before"u8.Length, new("target", Hash("saved-before"u8)));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
        return directory;

        static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    }
}
