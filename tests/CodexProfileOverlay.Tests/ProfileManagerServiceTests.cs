using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class ProfileManagerServiceTests
{
    [Fact]
    public void ListProfiles_AppliesDisplayMetadataAndOrder()
    {
        using var temp = new TestLayout();
        temp.AddProfile("alpha", "alpha-auth");
        temp.AddProfile("beta", "beta-auth");
        var store = new ProfileMetadataStore(temp.Paths.ProfilesMetadataFile);
        store.Save(new ProfileMetadataDocument
        {
            Profiles =
            [
                new ProfileMetadata { DirectoryName = "alpha", DisplayName = "Alpha Personal", Order = 2, Initials = "AP" },
                new ProfileMetadata { DirectoryName = "beta", DisplayName = "Beta Work", Order = 1, Initials = "BW" },
            ],
        });

        var service = new ProfileManagerService(temp.Paths, new ProfileDiscoveryService(temp.Paths.ProfilesDirectory), store);

        var profiles = service.ListProfiles();

        Assert.Equal<string[]>(["beta", "alpha"], profiles.Select(static profile => profile.Name).ToArray());
        Assert.Equal("Beta Work", profiles[0].DisplayName);
        Assert.Equal("BW", profiles[0].Initials);
    }

    [Fact]
    public void CreateProfileDirectory_WritesConfigWithoutAuthInspection()
    {
        using var temp = new TestLayout();
        var service = CreateService(temp);

        string directory = service.CreateProfileDirectory("new-profile");

        Assert.True(Directory.Exists(directory));
        Assert.Equal("cli_auth_credentials_store = \"file\"" + Environment.NewLine, File.ReadAllText(Path.Combine(directory, "config.toml")));
    }

    [Fact]
    public void CreateProfileDirectory_ReplacesIncompleteDirectoryForRetry()
    {
        using var temp = new TestLayout();
        string incompleteDirectory = Path.Combine(temp.Paths.ProfilesDirectory, "retry-profile");
        Directory.CreateDirectory(incompleteDirectory);
        File.WriteAllText(Path.Combine(incompleteDirectory, "stale.tmp"), "leftover");
        var service = CreateService(temp);

        string directory = service.CreateProfileDirectory("retry-profile");

        Assert.Equal(incompleteDirectory, directory);
        Assert.True(File.Exists(Path.Combine(directory, "config.toml")));
        Assert.False(File.Exists(Path.Combine(directory, "stale.tmp")));
    }

    [Fact]
    public void RemoveIncompleteProfileDirectory_RemovesOnlyProfilesWithoutAuth()
    {
        using var temp = new TestLayout();
        var service = CreateService(temp);
        string incompleteDirectory = service.CreateProfileDirectory("unfinished");
        temp.AddProfile("finished", "auth");

        Assert.True(service.RemoveIncompleteProfileDirectory("unfinished"));
        Assert.False(Directory.Exists(incompleteDirectory));
        Assert.False(service.RemoveIncompleteProfileDirectory("finished"));
        Assert.True(File.Exists(Path.Combine(temp.Paths.ProfilesDirectory, "finished", "auth.json")));
    }

    [Fact]
    public void RemoveProfile_MovesDirectoryAndProtectsActiveProfile()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "current-auth");
        temp.AddProfile("old", "old-auth");
        var service = CreateService(temp);

        Assert.Throws<InvalidOperationException>(() => service.RemoveProfile("current", "current"));
        string removed = service.RemoveProfile("old", "current");

        Assert.False(Directory.Exists(Path.Combine(temp.Paths.ProfilesDirectory, "old")));
        Assert.True(Directory.Exists(removed));
        Assert.StartsWith(temp.Paths.RemovedProfilesDirectory, removed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reorder_PersistsProfileOrder()
    {
        using var temp = new TestLayout();
        temp.AddProfile("alpha", "alpha-auth");
        temp.AddProfile("beta", "beta-auth");
        var service = CreateService(temp);
        service.EnsureMetadata();

        service.Reorder(["beta", "alpha"]);

        Assert.Equal<string[]>(["beta", "alpha"], service.ListProfiles().Select(static profile => profile.Name).ToArray());
    }

    [Fact]
    public void RemoveProfile_MetadataWriteFailureRestoresOriginal()
    {
        using var temp = new TestLayout();
        temp.AddProfile("old", "fake-auth");
        var service = CreateService(temp);
        service.EnsureMetadata();
        string metadata = File.ReadAllText(temp.Paths.ProfilesMetadataFile);
        Directory.CreateDirectory(temp.Paths.ProfilesMetadataFile + ".tmp");

        Assert.Throws<UnauthorizedAccessException>(() => service.RemoveProfile("old", null));
        Assert.Equal("fake-auth", temp.ReadProfileAuth("old"));
        Assert.Equal(metadata, File.ReadAllText(temp.Paths.ProfilesMetadataFile));
        Assert.Single(service.ListProfiles());
    }

    [Fact]
    public void RemoveProfile_RecoveryWriteFailurePreservesProfileAndMetadata()
    {
        using var temp = new TestLayout();
        temp.AddProfile("old", "fake-auth");
        var service = CreateService(temp);
        service.EnsureMetadata();
        string metadata = File.ReadAllText(temp.Paths.ProfilesMetadataFile);
        File.WriteAllText(temp.Paths.RemovedProfilesDirectory, "blocked");
        Assert.ThrowsAny<IOException>(() => service.RemoveProfile("old", null));
        Assert.Equal("fake-auth", temp.ReadProfileAuth("old"));
        Assert.Equal(metadata, File.ReadAllText(temp.Paths.ProfilesMetadataFile));
        Assert.Single(service.ListProfiles());
    }

    [Fact]
    public void RemoveProfile_ActiveComparisonIsCaseInsensitive()
    {
        using var temp = new TestLayout();
        temp.AddProfile("current", "fake-auth");
        Assert.Throws<InvalidOperationException>(() => CreateService(temp).RemoveProfile("current", "CURRENT"));
        Assert.Equal("fake-auth", temp.ReadProfileAuth("current"));
        Assert.False(Directory.Exists(temp.Paths.RemovedProfilesDirectory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RemoveProfile_CrossVolumeJunctionPreservesRecoveryAndOtherData(bool failMetadata, bool failCopy)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestLayout();
        string target = Path.Combine(AppContext.BaseDirectory, "removal-fixture-" + Guid.NewGuid().ToString("N"));
        if (Path.GetPathRoot(target) == Path.GetPathRoot(temp.Paths.ProfilesDirectory))
            return; // This integration scenario requires two volumes; exercised with binaries on D: and TEMP on C:.
        Directory.CreateDirectory(target);
        Directory.Delete(temp.Paths.ProfilesDirectory);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.Arguments = $"/c mklink /J \"{temp.Paths.ProfilesDirectory}\" \"{target}\"";
        using var link = System.Diagnostics.Process.Start(start)!;
        link.WaitForExit();
        Assert.Equal(0, link.ExitCode);
        try
        {
            temp.AddProfile("old", "fake-old-auth");
            temp.AddProfile("current", "fake-current-auth");
            temp.WriteSharedState("untouched");
            File.WriteAllText(Path.Combine(temp.Paths.ProfilesDirectory, "old", "notes.txt"), "saved");
            var service = CreateService(temp);
            service.EnsureMetadata();
            string metadata = File.ReadAllText(temp.Paths.ProfilesMetadataFile);
            // Confirm the original failing operation on this real junction/volume layout.
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(temp.Paths.ProfilesDirectory, "old"), Path.Combine(temp.Paths.ApplicationDataDirectory, "direct-move")));
            if (failCopy)
            {
                using var locked = new FileStream(Path.Combine(temp.Paths.ProfilesDirectory, "old", "auth.json"), FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.ThrowsAny<IOException>(() => service.RemoveProfile("old", "current"));
                Assert.Equal(metadata, File.ReadAllText(temp.Paths.ProfilesMetadataFile));
                Assert.Equal(2, service.ListProfiles().Count);
                return;
            }
            if (failMetadata) Directory.CreateDirectory(temp.Paths.ProfilesMetadataFile + ".tmp");
            if (failMetadata)
            {
                Assert.Throws<UnauthorizedAccessException>(() => service.RemoveProfile("old", "current"));
                Assert.Equal("fake-old-auth", temp.ReadProfileAuth("old"));
                Assert.Equal(metadata, File.ReadAllText(temp.Paths.ProfilesMetadataFile));
                Assert.Equal(2, service.ListProfiles().Count);
            }
            else
            {
                string recovery = service.RemoveProfile("old", "current");
                Assert.Equal("fake-old-auth", File.ReadAllText(Path.Combine(recovery, "auth.json")));
                Assert.Equal("saved", File.ReadAllText(Path.Combine(recovery, "notes.txt")));
                Assert.False(Directory.Exists(Path.Combine(temp.Paths.ProfilesDirectory, "old")));
                Assert.Equal("current", Assert.Single(service.ListProfiles()).Name);
            }
            Assert.Equal("fake-current-auth", temp.ReadProfileAuth("current"));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(temp.Paths.SharedCodexDirectory, ".codex-global-state.json")));
        }
        finally
        {
            Directory.Delete(temp.Paths.ProfilesDirectory); // Remove junction, never recurse through it.
            Directory.Delete(target, recursive: true); // Isolated fixture created above.
        }
    }

    private static ProfileManagerService CreateService(TestLayout temp)
    {
        return new ProfileManagerService(
            temp.Paths,
            new ProfileDiscoveryService(temp.Paths.ProfilesDirectory),
            new ProfileMetadataStore(temp.Paths.ProfilesMetadataFile));
    }
}
