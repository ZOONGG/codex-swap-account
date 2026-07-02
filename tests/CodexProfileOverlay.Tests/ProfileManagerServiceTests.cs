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

    private static ProfileManagerService CreateService(TestLayout temp)
    {
        return new ProfileManagerService(
            temp.Paths,
            new ProfileDiscoveryService(temp.Paths.ProfilesDirectory),
            new ProfileMetadataStore(temp.Paths.ProfilesMetadataFile));
    }
}
