using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class ProfileStatusService : IDisposable
{
    public const int MaximumLabelLength = 24;
    public const int MaximumNoteLength = 120;

    private readonly ProfileStatusStore store;
    private readonly IUsageProvider usageProvider;
    private readonly SafeLogger logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> refreshing = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private TimeSpan staleThreshold = TimeSpan.FromMinutes(90);
    private bool disposed;

    public ProfileStatusService(ProfileStatusStore store, IUsageProvider usageProvider, SafeLogger logger)
    {
        this.store = store;
        this.usageProvider = usageProvider;
        this.logger = logger;
    }

    public UsageProviderCapability ProviderCapability => usageProvider.Capability;

    public void SetStaleThreshold(TimeSpan threshold)
    {
        staleThreshold = threshold < TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : threshold;
    }

    public ProfileStatusDocument Load() => store.Load();

    public void Save(ProfileStatusDocument document) => store.Save(document);

    public ProfileStatusMetadata GetOrCreateStatus(ProfileStatusDocument document, string profileId)
        => store.GetOrCreateStatus(document, profileId);

    public UsageSnapshot? GetCachedSnapshot(ProfileStatusDocument document, string profileId)
        => document.Snapshots.TryGetValue(profileId, out UsageSnapshot? snapshot) ? snapshot : null;

    public void SaveManualStatus(
        string profileId,
        string? emoji,
        string? label,
        string? note,
        DateTimeOffset? resetAt,
        string? color)
    {
        ProfileStatusDocument document = Load();
        ProfileStatusMetadata status = GetOrCreateStatus(document, profileId);
        status.ManualEmoji = Optional(emoji, 8, nameof(emoji));
        status.ManualLabel = Optional(label, MaximumLabelLength, nameof(label));
        status.ManualNote = Optional(note, MaximumNoteLength, nameof(note));
        status.ManualResetAt = resetAt?.ToUniversalTime();
        status.ManualColor = Optional(color, 32, nameof(color));
        Save(document);
    }

    public void ClearManualStatus(string profileId)
    {
        ProfileStatusDocument document = Load();
        ProfileStatusMetadata status = GetOrCreateStatus(document, profileId);
        status.ManualEmoji = null;
        status.ManualLabel = null;
        status.ManualNote = null;
        status.ManualResetAt = null;
        status.ManualColor = null;
        Save(document);
    }

    public void ClearCachedUsage(string profileId)
    {
        ProfileStatusDocument document = Load();
        _ = document.Snapshots.Remove(profileId);
        ProfileStatusMetadata status = GetOrCreateStatus(document, profileId);
        status.LastAutomaticSnapshot = null;
        status.LastRefreshAttemptAt = null;
        status.LastRefreshError = null;
        Save(document);
    }

    public void SetAutomaticRefreshEnabled(string profileId, bool enabled)
    {
        ProfileStatusDocument document = Load();
        GetOrCreateStatus(document, profileId).AutomaticRefreshEnabled = enabled;
        Save(document);
    }

    public string GetLimitIndicator(UsageSnapshot? snapshot, int greenThreshold, int yellowThreshold)
        => ProfileIndicatorFormatter.FormatAutomatic(
            snapshot,
            enabled: true,
            greenThreshold,
            yellowThreshold,
            DateTimeOffset.UtcNow,
            staleThreshold,
            recommended: false);

    public async Task RefreshUsageAsync(
        string profileId,
        string profileDirectory,
        ProfileStatusDocument document,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!refreshing.TryAdd(profileId, 0)) return;
        try
        {
            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            refreshing.TryRemove(profileId, out _);
            throw;
        }
        try
        {
            ProfileStatusMetadata status = GetOrCreateStatus(document, profileId);
            status.LastRefreshAttemptAt = DateTimeOffset.UtcNow;

            if (usageProvider.Capability != UsageProviderCapability.Supported)
            {
                MarkFailure(document, profileId, status, "Automatic limits unavailable");
                Save(document);
                return;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            try
            {
                UsageSnapshot? snapshot = await usageProvider.GetUsageAsync(profileDirectory, timeoutSource.Token).ConfigureAwait(false);
                if (snapshot is null)
                {
                    MarkFailure(document, profileId, status, "Usage source returned no data");
                }
                else
                {
                    snapshot.CapturedAt = snapshot.CapturedAt.ToUniversalTime();
                    snapshot.Source = string.IsNullOrWhiteSpace(snapshot.Source) ? usageProvider.GetType().Name : snapshot.Source;
                    snapshot.IsStale = false;
                    document.Snapshots[profileId] = snapshot;
                    status.LastAutomaticSnapshot = snapshot.CapturedAt;
                    status.LastRefreshError = null;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                MarkFailure(document, profileId, status, "Usage refresh timed out");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                MarkFailure(document, profileId, status, "Usage refresh failed");
                logger.Error($"Usage refresh failed for profile '{profileId}'.", exception);
            }

            Save(document);
        }
        finally
        {
            refreshGate.Release();
            refreshing.TryRemove(profileId, out _);
        }
    }

    public async Task<bool> TestProviderSupportAsync(
        string profileDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (usageProvider.Capability != UsageProviderCapability.Supported)
        {
            return false;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            UsageSnapshot? snapshot = await usageProvider.GetUsageAsync(profileDirectory, timeoutSource.Token).ConfigureAwait(false);
            return snapshot is not null && UsageIntelligence.EffectiveRemainingPercent(snapshot).HasValue;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.Error("Usage provider support test failed.", exception);
            return false;
        }
    }

    public string? FindRecommendedProfile(IReadOnlyList<string> profileIds, ProfileStatusDocument document)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var candidates = profileIds
            .Select(id => new Candidate(id, GetCachedSnapshot(document, id)))
            .Where(candidate => candidate.Snapshot is not null
                && candidate.Snapshot.IsExhausted != true
                && !UsageIntelligence.IsStale(candidate.Snapshot, now, staleThreshold)
                && UsageIntelligence.EffectiveRemainingPercent(candidate.Snapshot).HasValue)
            .Select(candidate => new RankedCandidate(
                candidate.Id,
                candidate.Snapshot!,
                UsageIntelligence.EffectiveRemainingPercent(candidate.Snapshot!)!.Value,
                UsageIntelligence.AverageRemainingPercent(candidate.Snapshot!)!.Value,
                UsageIntelligence.NearestResetAt(candidate.Snapshot!)))
            .OrderByDescending(candidate => candidate.Effective)
            .ThenByDescending(candidate => candidate.Average)
            .ThenBy(candidate => candidate.NearestReset ?? DateTimeOffset.MaxValue)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count < 2)
        {
            return null;
        }

        RankedCandidate first = candidates[0];
        RankedCandidate second = candidates[1];
        bool uncertain = first.Effective == second.Effective
            && Math.Abs(first.Average - second.Average) < 0.001
            && first.NearestReset == second.NearestReset;
        return uncertain ? null : first.Id;
    }

    public bool IsDataStale(UsageSnapshot snapshot)
        => UsageIntelligence.IsStale(snapshot, DateTimeOffset.UtcNow, staleThreshold);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        refreshGate.Dispose();
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        string? trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed?.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
        }

        return trimmed;
    }

    private static void MarkFailure(
        ProfileStatusDocument document,
        string profileId,
        ProfileStatusMetadata status,
        string error)
    {
        if (document.Snapshots.TryGetValue(profileId, out UsageSnapshot? existing))
        {
            existing.IsStale = true;
        }

        status.LastRefreshError = error;
    }

    private sealed record Candidate(string Id, UsageSnapshot? Snapshot);

    private sealed record RankedCandidate(
        string Id,
        UsageSnapshot Snapshot,
        int Effective,
        double Average,
        DateTimeOffset? NearestReset);
}
