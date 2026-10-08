using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using System.Globalization;

namespace CodexProfileOverlay.Tests;

public sealed class ProfileStatusServiceTests
{
    [Theory]
    [InlineData(60, "🟢")]
    [InlineData(59, "🟡")]
    [InlineData(25, "🟡")]
    [InlineData(24, "🔴")]
    [InlineData(0, "🔴")]
    public void Indicator_UsesExactBoundaries(int percent, string expected)
    {
        UsageSnapshot snapshot = Snapshot(("5h", percent, null));

        string result = ProfileIndicatorFormatter.FormatAutomatic(
            snapshot, true, 60, 25, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(90), false);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Indicator_UsesMostRestrictiveWindow()
    {
        UsageSnapshot snapshot = Snapshot(("5h", 80, null), ("7d", 24, null));

        string result = ProfileIndicatorFormatter.FormatAutomatic(
            snapshot, true, 60, 25, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(90), false);

        Assert.Equal("🔴", result);
    }

    [Fact]
    public void Indicator_RecommendationReplacesColorAndMasterToggleHidesIt()
    {
        UsageSnapshot snapshot = Snapshot(("5h", 80, null));

        Assert.Equal("⭐", ProfileIndicatorFormatter.FormatAutomatic(snapshot, true, 60, 25, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(90), true));
        Assert.Equal(string.Empty, ProfileIndicatorFormatter.FormatAutomatic(snapshot, false, 60, 25, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(90), true));
    }

    [Fact]
    public void Indicator_HidesUnknownAndStaleData()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UsageSnapshot unknown = Snapshot();
        UsageSnapshot stale = Snapshot(("5h", 80, null));
        stale.CapturedAt = now.AddMinutes(-91);

        Assert.Equal(string.Empty, ProfileIndicatorFormatter.FormatAutomatic(unknown, true, 60, 25, now, TimeSpan.FromMinutes(90), false));
        Assert.Equal(string.Empty, ProfileIndicatorFormatter.FormatAutomatic(stale, true, 60, 25, now, TimeSpan.FromMinutes(90), false));
    }

    [Fact]
    public void Recommendation_MaximizesLowestWindow()
    {
        using TestService fixture = new();
        ProfileStatusDocument document = Document(
            ("work", Snapshot(("5h", 95, null), ("7d", 40, null))),
            ("personal", Snapshot(("5h", 70, null), ("7d", 65, null))),
            ("backup", Snapshot(("5h", 55, null), ("7d", 55, null))));

        string? result = fixture.Service.FindRecommendedProfile(["work", "personal", "backup"], document);

        Assert.Equal("personal", result);
    }

    [Fact]
    public void Recommendation_UsesAverageThenNearestResetAsTieBreakers()
    {
        using TestService fixture = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProfileStatusDocument document = Document(
            ("a", Snapshot(("5h", 60, now.AddHours(2)), ("7d", 80, now.AddDays(2)))),
            ("b", Snapshot(("5h", 60, now.AddHours(1)), ("7d", 90, now.AddDays(3)))));

        Assert.Equal("b", fixture.Service.FindRecommendedProfile(["a", "b"], document));

        document.Snapshots["a"].Windows[1].RemainingPercent = 90;
        Assert.Equal("b", fixture.Service.FindRecommendedProfile(["a", "b"], document));
    }

    [Fact]
    public void Recommendation_RejectsExhaustedUnknownStaleAndUncertainData()
    {
        using TestService fixture = new();
        UsageSnapshot exhausted = Snapshot(("5h", 90, null));
        exhausted.IsExhausted = true;
        UsageSnapshot stale = Snapshot(("5h", 100, null));
        stale.IsStale = true;
        ProfileStatusDocument document = Document(
            ("exhausted", exhausted),
            ("unknown", Snapshot()),
            ("stale", stale),
            ("a", Snapshot(("5h", 50, null))),
            ("b", Snapshot(("5h", 50, null))));

        Assert.Null(fixture.Service.FindRecommendedProfile(["exhausted", "unknown", "stale", "a", "b"], document));
        Assert.Null(fixture.Service.FindRecommendedProfile(["a"], document));
    }

    [Fact]
    public async Task Refresh_PreservesManualStatusAndReplacesSnapshot()
    {
        UsageSnapshot returned = Snapshot(("5h", 72, null));
        using TestService fixture = new(new DelegateProvider((_, _) => Task.FromResult<UsageSnapshot?>(returned)));
        ProfileStatusDocument document = Document(("work", Snapshot(("5h", 20, null))));
        document.Profiles.Add(new ProfileStatusMetadata { ProfileId = "work", ManualEmoji = "🟢", ManualNote = "manual" });

        await fixture.Service.RefreshUsageAsync("work", @"C:\fake-profile", document, CancellationToken.None);

        Assert.Equal(72, UsageIntelligence.EffectiveRemainingPercent(document.Snapshots["work"]));
        Assert.Equal("manual", document.Profiles.Single().ManualNote);
        Assert.Equal("🟢", document.Profiles.Single().ManualEmoji);
    }

    [Fact]
    public async Task Refresh_DeduplicatesOverlappingRequestsForSameProfile()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using TestService fixture = new(new DelegateProvider(async (_, token) => {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Snapshot(("5h", 72, null));
        }));
        var document = new ProfileStatusDocument();
        Task first = fixture.Service.RefreshUsageAsync("work", @"C:\fake-profile", document, CancellationToken.None);
        await entered.Task;
        await fixture.Service.RefreshUsageAsync("WORK", @"C:\fake-profile", document, CancellationToken.None);
        release.SetResult();
        await first;
        Assert.Equal(1, calls);
        Assert.Equal(72, UsageIntelligence.EffectiveRemainingPercent(document.Snapshots["work"]));
    }

    [Fact]
    public async Task FailedRefresh_PreservesPreviousSnapshotAndMarksItStale()
    {
        using TestService fixture = new(new DelegateProvider((_, _) => throw new InvalidOperationException("fake failure")));
        ProfileStatusDocument document = Document(("work", Snapshot(("5h", 44, null))));

        await fixture.Service.RefreshUsageAsync("work", @"C:\fake-profile", document, CancellationToken.None);

        Assert.Equal(44, UsageIntelligence.EffectiveRemainingPercent(document.Snapshots["work"]));
        Assert.True(document.Snapshots["work"].IsStale);
        Assert.Equal("Usage refresh failed", document.Profiles.Single().LastRefreshError);
    }

    [Fact]
    public async Task Refresh_TimesOutAndHonorsCallerCancellation()
    {
        using TestService fixture = new(new DelegateProvider(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), token);
            return Snapshot(("5h", 50, null));
        }));
        var document = new ProfileStatusDocument();

        await fixture.Service.RefreshUsageAsync("work", @"C:\fake-profile", document, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        Assert.Equal("Usage refresh timed out", document.Profiles.Single().LastRefreshError);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.RefreshUsageAsync("work", @"C:\fake-profile", document, cancellation.Token));
    }

    [Fact]
    public async Task UnavailableProvider_DoesNotInventData()
    {
        using TestService fixture = new(new UnavailableUsageProvider());
        var document = new ProfileStatusDocument();

        await fixture.Service.RefreshUsageAsync("work", @"C:\fake-profile", document, CancellationToken.None);

        Assert.Empty(document.Snapshots);
        Assert.Equal("Automatic limits unavailable", document.Profiles.Single().LastRefreshError);
    }

    [Fact]
    public void ManualStatus_ValidatesLengthsAndStoresUtc()
    {
        using TestService fixture = new();
        DateTimeOffset local = new(2026, 7, 5, 12, 0, 0, TimeSpan.FromHours(6));

        fixture.Service.SaveManualStatus("work", "🟢", new string('a', 24), new string('b', 120), local, "#00ff00");
        ProfileStatusMetadata saved = fixture.Service.Load().Profiles.Single();

        Assert.Equal(TimeSpan.Zero, saved.ManualResetAt!.Value.Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Service.SaveManualStatus("work", null, new string('a', 25), null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Service.SaveManualStatus("work", null, null, new string('b', 121), null, null));
    }

    [Fact]
    public void NotificationDeduplication_OnlyEmitsOnStateChange()
    {
        var deduplicator = new UsageNotificationDeduplicator();

        Assert.True(deduplicator.ShouldNotify("work", "health", "yellow"));
        Assert.False(deduplicator.ShouldNotify("work", "health", "yellow"));
        Assert.True(deduplicator.ShouldNotify("work", "health", "red"));
    }

    [Fact]
    public void LocalTimeRendering_UsesRequestedTimeZone()
    {
        var utc = new DateTimeOffset(2026, 7, 5, 10, 30, 0, TimeSpan.Zero);
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("test-zone", TimeSpan.FromHours(6), "test-zone", "test-zone");

        string rendered = UsageDisplayFormatter.FormatLocal(utc, CultureInfo.InvariantCulture, zone);

        Assert.Contains("16:30", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshPolicy_DisablesSchedulingWithoutMasterToggleOrProviderAndPreservesCache()
    {
        var settings = new OverlaySettings { ShowAutomaticLimitIndicators = true };
        Assert.True(UsageRefreshPolicy.AllowsAutomaticRefresh(settings, UsageProviderCapability.Supported));
        Assert.False(UsageRefreshPolicy.AllowsAutomaticRefresh(settings, UsageProviderCapability.Unavailable));

        ProfileStatusDocument document = Document(("work", Snapshot(("5h", 77, null))));
        settings.ShowAutomaticLimitIndicators = false;
        Assert.False(UsageRefreshPolicy.AllowsAutomaticRefresh(settings, UsageProviderCapability.Supported));
        Assert.Equal(77, UsageIntelligence.EffectiveRemainingPercent(document.Snapshots["work"]));
        settings.ShowAutomaticLimitIndicators = true;
        Assert.Equal(77, UsageIntelligence.EffectiveRemainingPercent(document.Snapshots["work"]));
    }

    [Fact]
    public void OverlayIndicator_IsExactlyOneAllowedEmojiAndContainsNoMetrics()
    {
        UsageSnapshot snapshot = Snapshot(("5h", 78, null), ("7d", 64, null));
        string indicator = ProfileIndicatorFormatter.FormatAutomatic(snapshot, true, 60, 25, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(90), false);

        Assert.Equal("🟢", indicator);
        Assert.Single(EnumerateTextElements(indicator));
        Assert.DoesNotContain('%', indicator);
        Assert.DoesNotContain("5h", indicator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ready", indicator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Свежий", indicator, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverlayIndicator_RecommendationIsSingleEmojiAndReplacesColor()
    {
        UsageSnapshot snapshot = Snapshot(("5h", 100, null), ("7d", 100, null));

        string indicator = ProfileIndicatorFormatter.FormatAutomatic(
            snapshot,
            enabled: true,
            greenThreshold: 60,
            yellowThreshold: 25,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(90),
            recommended: true);

        Assert.Equal("⭐", indicator);
        Assert.Single(EnumerateTextElements(indicator));
        Assert.DoesNotContain("🟢", indicator);
    }

    [Fact]
    public void OverlayIndicator_MasterToggleAndStaleDataHideEveryAutomaticEmoji()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UsageSnapshot fresh = Snapshot(("5h", 100, null));
        UsageSnapshot stale = Snapshot(("5h", 100, null));
        stale.CapturedAt = now.AddMinutes(-91);

        Assert.Equal(string.Empty, ProfileIndicatorFormatter.FormatAutomatic(fresh, false, 60, 25, now, TimeSpan.FromMinutes(90), true));
        Assert.Equal(string.Empty, ProfileIndicatorFormatter.FormatAutomatic(stale, true, 60, 25, now, TimeSpan.FromMinutes(90), true));
    }

    private static UsageSnapshot Snapshot(params (string Name, int Percent, DateTimeOffset? Reset)[] windows)
        => new()
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Source = "fake-provider",
            Windows = windows.Select(window => new UsageLimitWindow
            {
                Name = window.Name,
                RemainingPercent = window.Percent,
                ResetAt = window.Reset,
            }).ToList(),
        };

    private static ProfileStatusDocument Document(params (string Id, UsageSnapshot Snapshot)[] profiles)
    {
        var document = new ProfileStatusDocument();
        foreach ((string id, UsageSnapshot snapshot) in profiles)
        {
            document.Snapshots[id] = snapshot;
        }
        return document;
    }

    private static IEnumerable<string> EnumerateTextElements(string value)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private sealed class TestService : IDisposable
    {
        private readonly TempDirectory temp = new();

        public TestService(IUsageProvider? provider = null)
        {
            Service = new ProfileStatusService(
                new ProfileStatusStore(Path.Combine(temp.Path, "status-document.json")),
                provider ?? new DelegateProvider((_, _) => Task.FromResult<UsageSnapshot?>(Snapshot(("5h", 65, null)))),
                new SafeLogger(temp.Path));
        }

        public ProfileStatusService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            temp.Dispose();
        }
    }

    private sealed class DelegateProvider(Func<string, CancellationToken, Task<UsageSnapshot?>> callback) : IUsageProvider
    {
        public UsageProviderCapability Capability => UsageProviderCapability.Supported;

        public Task<UsageSnapshot?> GetUsageAsync(string profileDirectory, CancellationToken cancellationToken)
            => callback(profileDirectory, cancellationToken);
    }
}
