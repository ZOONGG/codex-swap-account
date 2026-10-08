using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class ProfileStatusStoreTests
{
    [Fact]
    public void LegacyManualFieldsAreIgnoredWithoutLosingAutomaticUsage()
    {
        using var temp = new TempDirectory();
        string file = Path.Combine(temp.Path, "status.json");
        File.WriteAllText(file, """
            {"schemaVersion":3,"profiles":[{"profileId":"work","manualEmoji":"old","manualResetAt":"2026-10-08T00:00:00Z","automaticRefreshEnabled":false}],
            "snapshots":{"work":{"windows":[{"name":"5h","remainingPercent":75}]}}}
            """);
        var store = new ProfileStatusStore(file);
        var document = store.Load();
        Assert.False(document.Profiles.Single().AutomaticRefreshEnabled);
        Assert.Equal(75, document.Snapshots["work"].Windows.Single().RemainingPercent);
        store.Save(document);
        Assert.DoesNotContain("manual", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(75, store.Load().Snapshots["work"].Windows.Single().RemainingPercent);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsStatusDocument()
    {
        using var temp = new TempDirectory();
        string statusFile = Path.Combine(temp.Path, "profile-status.json");
        var store = new ProfileStatusStore(statusFile);

        var document = new ProfileStatusDocument
        {
            Profiles =
            {
                new ProfileStatusMetadata { ProfileId = "work", AutomaticRefreshEnabled = false },
                new ProfileStatusMetadata { ProfileId = "personal", AutomaticRefreshEnabled = true },
            },
            Snapshots =
            {
                ["work"] = new UsageSnapshot
                {
                    Windows =
                    {
                        new UsageLimitWindow { Name = "5h", Duration = TimeSpan.FromHours(5), RemainingPercent = 75 },
                        new UsageLimitWindow { Name = "7d", Duration = TimeSpan.FromDays(7), RemainingPercent = 50 },
                    },
                    CapturedAt = DateTimeOffset.UtcNow,
                },
            },
        };
        store.Save(document);

        var loaded = store.Load();

        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Equal("work", loaded.Profiles[0].ProfileId);
        Assert.False(loaded.Profiles[0].AutomaticRefreshEnabled);
        Assert.Single(loaded.Snapshots);
        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Equal(75, loaded.Snapshots["work"].Windows[0].RemainingPercent);
    }

    [Fact]
    public void Load_VersionTwoEnablesAutomaticRefreshOnceAndPreservesLaterOptOut()
    {
        using var temp = new TempDirectory();
        string statusFile = Path.Combine(temp.Path, "profile-status.json");
        File.WriteAllText(statusFile, """
            {
              "schemaVersion": 2,
              "profiles": [
                { "profileId": "work", "automaticRefreshEnabled": false }
              ],
              "snapshots": {}
            }
            """);
        var store = new ProfileStatusStore(statusFile);

        ProfileStatusDocument migrated = store.Load();
        Assert.Equal(3, migrated.SchemaVersion);
        Assert.True(migrated.Profiles.Single().AutomaticRefreshEnabled);

        migrated.Profiles.Single().AutomaticRefreshEnabled = false;
        store.Save(migrated);

        Assert.False(store.Load().Profiles.Single().AutomaticRefreshEnabled);
    }

    [Fact]
    public void Load_NonExistentFileReturnsEmptyDocument()
    {
        using var temp = new TempDirectory();
        string statusFile = Path.Combine(temp.Path, "profile-status.json");
        var store = new ProfileStatusStore(statusFile);

        var loaded = store.Load();

        Assert.Empty(loaded.Profiles);
        Assert.Empty(loaded.Snapshots);
    }

    [Fact]
    public void GetOrCreateStatus_CreatesWhenMissing()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStatusStore(Path.Combine(temp.Path, "status.json"));
        var document = new ProfileStatusDocument();

        var status = store.GetOrCreateStatus(document, "new-profile");

        Assert.Equal("new-profile", status.ProfileId);
        Assert.Single(document.Profiles);
    }

    [Fact]
    public void GetOrCreateStatus_ReturnsExistingWhenPresent()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStatusStore(Path.Combine(temp.Path, "status.json"));
        var document = new ProfileStatusDocument
        {
            Profiles = { new ProfileStatusMetadata { ProfileId = "existing", AutomaticRefreshEnabled = false } },
        };

        var status = store.GetOrCreateStatus(document, "existing");

        Assert.Equal("existing", status.ProfileId);
        Assert.False(status.AutomaticRefreshEnabled);
        Assert.Single(document.Profiles); // No duplicate created
    }
}
