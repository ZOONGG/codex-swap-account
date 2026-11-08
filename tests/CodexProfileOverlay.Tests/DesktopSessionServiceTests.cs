using System.Text;
using System.Text.Json;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class DesktopSessionServiceTests
{
    [Fact]
    public void CaptureAndSwitchBetweenDesktopProfilesPreservesSharedStateAndLatestSession()
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        fixture.WriteLive("b-new");
        fixture.Layout.WriteSharedAuth(Auth("b", "refreshed"));

        fixture.Switch("alpha");
        fixture.AssertCurrent("alpha", "a", "a");
        fixture.Switch("beta");
        fixture.AssertCurrent("beta", "b", "b-new");
        Assert.Equal(Auth("b", "refreshed"), fixture.Layout.ReadSharedAuth());
        Assert.Equal("history", File.ReadAllText(Path.Combine(fixture.Paths.SharedCodexDirectory, "history.txt")));
    }

    [Fact]
    public void RetentionWaitsForGracePeriodAndOnlyDeletesConfirmedChromiumCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        var retired = fixture.SwitchWithReceipt("alpha");
        fixture.Now += TimeSpan.FromHours(23);
        fixture.Cleanup();
        Assert.True(Directory.Exists(retired.Live));
        Assert.True(Directory.Exists(retired.Profile));

        fixture.Switch("beta"); // Committed without restart confirmation.
        var unconfirmed = fixture.RetiredTrees().Single(tree => tree != retired);
        string unrelated = Path.Combine(Path.GetDirectoryName(fixture.Live)!, $" .desktop-{Guid.NewGuid():N}", "original");
        Directory.CreateDirectory(unrelated);
        fixture.Now += TimeSpan.FromDays(8);
        fixture.Service.TryCleanupCompletedSessions([fixture.OtherLive], default);
        Assert.True(Directory.Exists(retired.Live));

        fixture.Cleanup();
        fixture.Cleanup();
        Assert.False(Directory.Exists(retired.Live));
        Assert.False(Directory.Exists(retired.Profile));
        Assert.True(Directory.Exists(unconfirmed.Live));
        Assert.True(Directory.Exists(unconfirmed.Profile));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(retired.Profile)!, "original-auth.json")));
        Assert.True(File.Exists(Path.Combine(fixture.RecoveryRoot, retired.Id, "committed.json")));
        fixture.AssertCurrent("beta", "b", "b");
        Assert.Equal("a", fixture.Stored("alpha"));
        Assert.Equal("b", fixture.Stored("beta"));
        Assert.Equal(Auth("a"), fixture.Layout.ReadProfileAuth("alpha"));
        Assert.Equal("history", File.ReadAllText(Path.Combine(fixture.Paths.SharedCodexDirectory, "history.txt")));
    }

    [Fact]
    public async Task RetentionSkipsSwitchAndRecoveryInProgress()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        var retired = fixture.SwitchWithReceipt("alpha");
        fixture.Now += TimeSpan.FromDays(8);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var switching = new DesktopSessionService(fixture.Paths, (source, destination) =>
        {
            Move(source, destination);
            if (source != fixture.Live) return;
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            throw new IOException("Interrupted switch");
        });
        var switchTask = Task.Run(() => Assert.Throws<IOException>(() =>
            switching.Switch("beta", fixture.Live, CheckStopped, default)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            fixture.Cleanup();
            Assert.True(Directory.Exists(retired.Live));
        }
        finally { resume.Set(); }
        await switchTask;

        string pending = Path.Combine(fixture.RecoveryRoot, "pending.json");
        byte[] pendingBytes = File.ReadAllBytes(pending);
        fixture.Cleanup();
        Assert.Equal(pendingBytes, File.ReadAllBytes(pending));
        var interrupted = fixture.RetiredTrees().Single(tree => tree != retired);
        Assert.True(Directory.Exists(interrupted.Live));
        Assert.True(Directory.Exists(interrupted.Profile));

        entered.Reset();
        resume.Reset();
        var recovery = Task.Run(() => fixture.Service.Recover(fixture.Live, () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            fixture.Cleanup();
            Assert.True(Directory.Exists(retired.Live));
        }
        finally { resume.Set(); }
        await recovery;
        fixture.Cleanup();
        Assert.False(Directory.Exists(retired.Live));
        Assert.True(File.Exists(Path.Combine(fixture.RecoveryRoot, interrupted.Id, "rolled-back.json")));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(interrupted.Live)!, "incoming")));
        fixture.AssertCurrent("alpha", "a", "a");
    }

    [Fact]
    public void RetentionKeepsACommittedJournalWithAmbiguousRenameEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        var retired = fixture.SwitchWithReceipt("alpha");
        fixture.Now += TimeSpan.FromDays(8);
        string intent = Path.Combine(fixture.RecoveryRoot, retired.Id, "move-00.intent");
        File.Copy(intent, intent + ".undone");

        fixture.Cleanup();
        Assert.True(Directory.Exists(retired.Live));
        Assert.True(Directory.Exists(retired.Profile));
        Assert.True(File.Exists(intent + ".undone"));
        fixture.AssertCurrent("alpha", "a", "a");
    }

    [Fact]
    public void RetentionCanRetryAfterAFileDeniesDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        var retired = fixture.SwitchWithReceipt("alpha");
        fixture.Now += TimeSpan.FromDays(8);
        string file = Path.Combine(retired.Profile, "Default", "fake-state");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fixture.Cleanup();
            Assert.False(Directory.Exists(retired.Live));
            Assert.True(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(fixture.RecoveryRoot, retired.Id, "restart-verified.json")));
            string log = File.ReadAllText(Directory.GetFiles(fixture.Paths.LogDirectory).Single());
            Assert.Contains("Could not clean a completed Desktop session", log);
            Assert.DoesNotContain(file, log);
            Assert.DoesNotContain("fake-state", log);
        }
        fixture.Cleanup();
        Assert.False(Directory.Exists(retired.Profile));
        fixture.AssertCurrent("alpha", "a", "a");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetentionRejectsJunctionsInWorkspaceOrChromiumTree(bool replaceWorkspace)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        var retired = fixture.SwitchWithReceipt("alpha");
        fixture.Now += TimeSpan.FromDays(8);
        string junction = Path.Combine(retired.Live, "external");
        if (replaceWorkspace)
        {
            junction = Path.GetDirectoryName(retired.Live)!;
            Directory.Move(junction, fixture.OtherLive);
        }
        else Directory.CreateDirectory(fixture.OtherLive);
        string outside = Path.Combine(fixture.OtherLive, "outside");
        File.WriteAllText(outside, "outside");
        CreateJunction(junction, fixture.OtherLive);
        try
        {
            fixture.Cleanup();
            Assert.True(Directory.Exists(junction));
            Assert.Equal("outside", File.ReadAllText(outside));
            Assert.Equal("b", File.ReadAllText(Path.Combine(retired.Live, "Default", "fake-state")));
            fixture.AssertCurrent("alpha", "a", "a");
        }
        finally { Directory.Delete(junction); }
    }

    public static TheoryData<int, bool> RenameFailures()
    {
        var scenarios = new TheoryData<int, bool>();
        for (int index = 1; index <= 12; index++)
        {
            scenarios.Add(index, false);
            scenarios.Add(index, true);
        }
        return scenarios;
    }

    [Theory]
    [MemberData(nameof(RenameFailures))]
    public void RecoveryReversesEverySwitchRenameIncludingInterruptionsAfterTheRename(int failure, bool afterRename)
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        fixture.WriteLive("b-new");
        fixture.Layout.WriteSharedAuth(Auth("b", "refreshed"));
        var original = fixture.Layout.ReadProfileAuth("beta");
        int count = 0;
        var interrupted = new DesktopSessionService(fixture.Paths, (source, destination) =>
        {
            count++;
            if (count == failure && !afterRename) throw new IOException("Synthetic rename failure.");
            Move(source, destination);
            if (count == failure) throw new IOException("Synthetic interruption after rename.");
        });

        Assert.Throws<IOException>(() => interrupted.Switch("alpha", fixture.Live, CheckStopped, default));
        Assert.Equal(failure, count);

        // A new service after an application/Windows restart has only filesystem state.
        var reopened = new DesktopSessionService(fixture.Paths);
        Assert.True(reopened.HasPendingOperation);
        Assert.Throws<IOException>(() => reopened.Complete(fixture.Live));
        Assert.Throws<InvalidDataException>(() => reopened.Recover(fixture.OtherLive, CheckStopped));
        reopened.Recover(fixture.Live, CheckStopped);
        Assert.False(reopened.HasPendingOperation);
        fixture.AssertCurrent("beta", "b", "b-new");
        Assert.Equal(Auth("b", "refreshed"), fixture.Layout.ReadSharedAuth());
        Assert.Equal(original, fixture.Layout.ReadProfileAuth("beta"));
        Assert.Equal("b", fixture.Stored("beta"));
        Assert.Equal("a", fixture.Stored("alpha"));

        fixture.Switch("alpha");
        fixture.AssertCurrent("alpha", "a", "a");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CaptureRecoversRegistrationAndMetadataIfInterrupted(int failure)
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Layout.WriteSharedAuth(Auth("b"));
        fixture.WriteLive("b");
        string metadata = File.ReadAllText(fixture.Paths.ProfilesMetadataFile);
        int count = 0;
        var interrupted = new DesktopSessionService(fixture.Paths, (source, destination) =>
        {
            Move(source, destination);
            if (++count == failure) throw new IOException("Synthetic capture interruption.");
        });

        Assert.Throws<IOException>(() => interrupted.Capture("beta", fixture.Live, CheckStopped, default));
        Assert.Equal(failure, count);
        fixture.Service.Recover(fixture.Live, CheckStopped);
        Assert.False(Directory.Exists(fixture.Profile("beta")));
        Assert.Equal(metadata, File.ReadAllText(fixture.Paths.ProfilesMetadataFile));
        fixture.AssertCurrent("alpha", "b", "b");

        fixture.Capture("beta", "b");
        fixture.AssertCurrent("beta", "b", "b");
    }

    [Fact]
    public void FailedRelaunchCanRollBackCompletedMovesAndInterruptedRollbackCanResume()
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        fixture.Service.Switch("alpha", fixture.Live, CheckStopped, default);
        fixture.AssertCurrent("alpha", "a", "a");

        // A relaunch may modify the incoming Chromium data before it fails.
        fixture.WriteLive("incoming-after-launch");
        Assert.Throws<TimeoutException>(() => fixture.Service.Recover(fixture.Live, () => throw new TimeoutException()));
        Assert.True(fixture.Service.HasPendingOperation);
        var interrupted = new DesktopSessionService(fixture.Paths, (source, destination) =>
        {
            Move(source, destination);
            if (Path.GetFileName(destination) == "incoming") throw new IOException("Interrupted recovery.");
        });
        Assert.Throws<IOException>(() => interrupted.Recover(fixture.Live, CheckStopped));
        Assert.True(fixture.Service.HasPendingOperation);
        fixture.Service.Recover(fixture.Live, CheckStopped);
        fixture.AssertCurrent("beta", "b", "b");
        string retained = Directory.EnumerateDirectories(Path.GetDirectoryName(fixture.Live)!, " .desktop-*")
            .Select(path => Path.Combine(path, "incoming", "Default", "fake-state"))
            .Single(File.Exists);
        Assert.Equal("incoming-after-launch", File.ReadAllText(retained));
    }

    [Fact]
    public void CancellationAndLockedFilesLeaveTheLivePairIntactAndPermitRetry()
    {
        using var fixture = new Fixture();
        fixture.WriteLive("a");
        fixture.Layout.WriteSharedAuth(Auth("a"));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => fixture.Service.Capture("alpha", fixture.Live, CheckStopped, cancellation.Token));
        }
        Assert.False(fixture.Service.HasPendingOperation);
        Assert.False(Directory.Exists(fixture.Profile("alpha")));
        using (File.Open(Path.Combine(fixture.Live, "Default", "fake-state"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => fixture.Service.Capture("alpha", fixture.Live, CheckStopped, default));
            fixture.Service.Recover(fixture.Live, CheckStopped);
        }
        fixture.AssertCurrent(null, "a", "a");
        fixture.Capture("alpha", "a");
        fixture.AssertCurrent("alpha", "a", "a");
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Capture("alpha", fixture.Live, CheckStopped, default));
        Assert.False(fixture.Service.HasPendingOperation);
    }

    [Fact]
    public void CancellationBetweenRenamesKeepsJournalAndRestoresBothSides()
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        using var cancellation = new CancellationTokenSource();
        var cancelled = new DesktopSessionService(fixture.Paths, (source, destination) =>
        {
            Move(source, destination);
            if (source == fixture.Live) cancellation.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() =>
            cancelled.Switch("alpha", fixture.Live, CheckStopped, cancellation.Token));
        Assert.True(cancelled.HasPendingOperation);
        Assert.False(Directory.Exists(fixture.Live));
        fixture.Service.Recover(fixture.Live, CheckStopped);
        fixture.AssertCurrent("beta", "b", "b");
        fixture.Switch("alpha");
        fixture.AssertCurrent("alpha", "a", "a");
    }

    [Fact]
    public async Task LegacyProfilesUseLegacySwitchAndLeavingDesktopDoesNotLoseItsSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Layout.AddProfile("legacy-one", Auth("one"));
        fixture.Layout.AddProfile("legacy-two", Auth("two"));
        fixture.Layout.WriteSharedAuth(Auth("one"));
        fixture.Layout.ActiveProfileStore.Write("legacy-one");
        fixture.WriteLive("untouched");
        Assert.False(fixture.Service.RequiresDesktopSwitch("legacy-two"));
        await fixture.Layout.CreateSwitchService().SwitchAsync("legacy-two", CancellationToken.None);
        fixture.AssertCurrent("legacy-two", "two", "untouched");

        fixture.Capture("alpha", "a");
        fixture.WriteLive("new-a");
        fixture.Switch("legacy-one");
        fixture.AssertCurrent("legacy-one", "one", "new-a");
        fixture.WriteLive("legacy-change");
        fixture.Switch("alpha");
        fixture.AssertCurrent("alpha", "a", "new-a");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrMissingDesktopDataNeverFallsThroughToAuthOnly(bool remove)
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        if (remove)
        {
            Directory.Move(Path.Combine(fixture.Profile("alpha"), "desktop-session"), fixture.OtherLive);
            File.Delete(Path.Combine(fixture.Profile("alpha"), "desktop-session.json"));
        }
        else File.WriteAllText(fixture.StoredFile("alpha"), "corrupt");
        Assert.True(fixture.Service.RequiresDesktopSwitch("alpha"));
        if (remove) Assert.Throws<FileNotFoundException>(() => fixture.Service.Switch("alpha", fixture.Live, CheckStopped, default));
        else Assert.Throws<InvalidDataException>(() => fixture.Service.Switch("alpha", fixture.Live, CheckStopped, default));
        if (fixture.Service.HasPendingOperation) fixture.Service.Recover(fixture.Live, CheckStopped);
        fixture.AssertCurrent("beta", "b", "b");
    }

    [Fact]
    public void BlocksLinksAndAmbiguousRecoveryWithoutTouchingExternalData()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        var blocked = new DesktopSessionService(fixture.Paths, (source, destination) =>
        {
            if (destination == fixture.Live)
            {
                Directory.CreateDirectory(destination);
                throw new IOException("A concurrent actor recreated the live directory.");
            }
            Move(source, destination);
        });
        Assert.Throws<IOException>(() => blocked.Switch("alpha", fixture.Live, CheckStopped, default));
        Assert.Throws<IOException>(() => fixture.Service.Recover(fixture.Live, CheckStopped));
        Assert.True(fixture.Service.HasPendingOperation);
        Assert.Equal(Auth("b"), fixture.Layout.ReadSharedAuth());
        Assert.Equal("a", fixture.Stored("alpha"));

        // Empty directory created by this test to model an ambiguous endpoint.
        Directory.Delete(fixture.Live);
        fixture.Service.Recover(fixture.Live, CheckStopped);
        fixture.AssertCurrent("beta", "b", "b");

        Directory.CreateDirectory(fixture.OtherLive);
        File.WriteAllText(Path.Combine(fixture.OtherLive, "outside.txt"), "outside");
        string junction = Path.Combine(fixture.Live, "external");
        CreateJunction(junction, fixture.OtherLive);
        try
        {
            Assert.Throws<IOException>(() => fixture.Service.Capture("linked", fixture.Live, CheckStopped, default));
            fixture.Service.Recover(fixture.Live, CheckStopped);
            Assert.False(Directory.Exists(fixture.Profile("linked")));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(fixture.OtherLive, "outside.txt")));
        }
        finally { Directory.Delete(junction); }
    }

    private static void CreateJunction(string junction, string target)
    {
        var cmd = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        cmd.ArgumentList.Add("/c");
        cmd.ArgumentList.Add("mklink");
        cmd.ArgumentList.Add("/J");
        cmd.ArgumentList.Add(junction);
        cmd.ArgumentList.Add(target);
        using (var process = System.Diagnostics.Process.Start(cmd)!)
        {
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
        }
    }

    [Fact]
    public void RemoveInactiveDesktopProfileArchivesItAndCaptureCanReuseItsName()
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        fixture.Layout.AddProfile("other", "fake-other-auth");
        string recoveryRoot = Path.Combine(fixture.Paths.ApplicationDataDirectory, "desktop-recovery");
        var priorRecovery = Directory.GetFiles(recoveryRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.NotEmpty(priorRecovery);
        var manager = new ProfileManagerService(fixture.Paths, new ProfileDiscoveryService(fixture.Paths.ProfilesDirectory),
            new ProfileMetadataStore(fixture.Paths.ProfilesMetadataFile));
        Assert.Throws<InvalidOperationException>(() => manager.RemoveProfile("beta", null));
        string metadata = File.ReadAllText(fixture.Paths.ProfilesMetadataFile);
        Directory.CreateDirectory(fixture.Paths.ProfilesMetadataFile + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => manager.RemoveProfile("alpha", "beta"));
        Directory.Delete(fixture.Paths.ProfilesMetadataFile + ".tmp");
        Assert.Equal("a", fixture.Stored("alpha"));
        Assert.Equal(metadata, File.ReadAllText(fixture.Paths.ProfilesMetadataFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.RemovedProfilesDirectory));

        string archive = manager.RemoveProfile("alpha", "beta");
        Assert.Equal("a", File.ReadAllText(Path.Combine(archive, "desktop-session", "Default", "fake-state")));
        Assert.True(File.Exists(Path.Combine(archive, "desktop-session.json")));
        Assert.Equal(Auth("a"), File.ReadAllText(Path.Combine(archive, "auth.json")));
        Assert.DoesNotContain(manager.ListProfiles(), p => p.Name == "alpha");
        Assert.Equal("fake-other-auth", fixture.Layout.ReadProfileAuth("other"));
        Assert.Equal("history", File.ReadAllText(Path.Combine(fixture.Paths.SharedCodexDirectory, "history.txt")));
        fixture.AssertCurrent("beta", "b", "b");
        foreach (var (path, bytes) in priorRecovery) Assert.Equal(bytes, File.ReadAllBytes(path));
        fixture.Capture("alpha", "c");
        Assert.Contains(manager.ListProfiles(), p => p.Name == "alpha");
        fixture.AssertCurrent("alpha", "c", "c");
    }

    [Fact]
    public void RemoveProfile_PendingSwitchPreservesJournalAndAllowsRollback()
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        fixture.Service.Switch("alpha", fixture.Live, CheckStopped, default);
        string pending = Path.Combine(fixture.Paths.ApplicationDataDirectory, "desktop-recovery", "pending.json");
        byte[] journal = File.ReadAllBytes(pending);
        string metadata = File.ReadAllText(fixture.Paths.ProfilesMetadataFile);
        var manager = new ProfileManagerService(fixture.Paths, new ProfileDiscoveryService(fixture.Paths.ProfilesDirectory),
            new ProfileMetadataStore(fixture.Paths.ProfilesMetadataFile));

        foreach (string name in new[] { "alpha", "beta" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => manager.RemoveProfile(name, null));
            Assert.Contains("Recover the pending Desktop session", error.Message);
        }

        Assert.Equal(journal, File.ReadAllBytes(pending));
        Assert.Equal(metadata, File.ReadAllText(fixture.Paths.ProfilesMetadataFile));
        Assert.False(Directory.Exists(fixture.Paths.RemovedProfilesDirectory));
        fixture.AssertCurrent("alpha", "a", "a");
        fixture.Service.Recover(fixture.Live, CheckStopped);
        fixture.AssertCurrent("beta", "b", "b");
        Assert.Equal("a", fixture.Stored("alpha"));
    }

    [Fact]
    public void UnexpectedExternalAccountIsNeverSavedIntoPreviousDesktopProfile()
    {
        using var fixture = new Fixture();
        fixture.Capture("alpha", "a");
        fixture.Capture("beta", "b");
        fixture.Layout.WriteSharedAuth(Auth("external"));
        fixture.WriteLive("external");
        Assert.Throws<DesktopAccountChangedException>(() => fixture.Service.Switch("alpha", fixture.Live, CheckStopped, default));
        Assert.False(fixture.Service.HasPendingOperation);
        Assert.Equal("b", fixture.Stored("beta"));
        fixture.AssertCurrent("beta", "external", "external");
    }

    private static void CheckStopped() { }

    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static string Auth(string id, string token = "fake")
    {
        string claims = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = id }))).TrimEnd('=');
        return JsonSerializer.Serialize(new
        {
            tokens = new { account_id = id, access_token = token, refresh_token = "fake", id_token = "fake." + claims + ".fake" }
        });
    }

    private sealed class Fixture : IDisposable
    {
        public TestLayout Layout { get; } = new();
        public AppPaths Paths => Layout.Paths;
        public DesktopSessionService Service { get; }
        public string Live => Path.Combine(Layout.LocalAppData, "chromium");
        public string OtherLive => Path.Combine(Layout.LocalAppData, "elsewhere");
        public string RecoveryRoot => Path.Combine(Paths.ApplicationDataDirectory, "desktop-recovery");
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public string Profile(string name) => Path.Combine(Paths.ProfilesDirectory, name);
        public string StoredFile(string name) => Path.Combine(Profile(name), "desktop-session", "Default", "fake-state");
        public string Stored(string name) => File.ReadAllText(StoredFile(name));

        public Fixture()
        {
            Service = new DesktopSessionService(Paths, logger: new SafeLogger(Paths.LogDirectory), utcNow: () => Now);
            File.WriteAllText(Path.Combine(Paths.SharedCodexDirectory, "history.txt"), "history");
        }

        public void WriteLive(string data)
        {
            Directory.CreateDirectory(Path.Combine(Live, "Default"));
            File.WriteAllText(Path.Combine(Live, "Local State"), "{\"os_crypt\":{}}");
            File.WriteAllText(Path.Combine(Live, "Default", "Preferences"), "{\"profile\":{}}");
            File.WriteAllText(Path.Combine(Live, "Default", "fake-state"), data);
        }

        public void Capture(string name, string id)
        {
            WriteLive(id);
            Layout.WriteSharedAuth(Auth(id));
            Service.Capture(name, Live, CheckStopped, default);
            Service.Complete(Live);
        }

        public void Switch(string name)
        {
            Service.Switch(name, Live, CheckStopped, default);
            Service.Complete(Live);
        }

        public (string Id, string Live, string Profile) SwitchWithReceipt(string name)
        {
            var existing = RetiredTrees().ToHashSet();
            Service.Switch(name, Live, CheckStopped, default);
            Service.Complete(Live, restartVerified: true);
            return RetiredTrees().Single(tree => !existing.Contains(tree));
        }

        public IEnumerable<(string Id, string Live, string Profile)> RetiredTrees() =>
            Directory.EnumerateDirectories(Path.GetDirectoryName(Live)!, " .desktop-*").Select(path =>
            {
                string workspace = Path.GetFileName(path);
                return (workspace[" .desktop-".Length..], Path.Combine(path, "original"),
                    Path.Combine(Paths.ProfilesDirectory, workspace, "original-desktop-session"));
            });

        public void Cleanup() => Service.TryCleanupCompletedSessions([Live], default);

        public void AssertCurrent(string? name, string id, string data)
        {
            Assert.Equal(name, Layout.ActiveProfileStore.Read());
            using var auth = JsonDocument.Parse(Layout.ReadSharedAuth());
            Assert.Equal(id, auth.RootElement.GetProperty("tokens").GetProperty("account_id").GetString());
            Assert.Equal(data, File.ReadAllText(Path.Combine(Live, "Default", "fake-state")));
        }

        public void Dispose() => Layout.Dispose();
    }
}
