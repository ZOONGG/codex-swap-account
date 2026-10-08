using System.Globalization;
using System.Text.Json;

namespace CodexProfileOverlay.Core.Services;

public sealed record DailyTokenUsage(DateOnly Date, long Tokens);

public sealed record AccountTokenStatistics(long? LifetimeTokens, long? PeakDailyTokens,
    IReadOnlyList<DailyTokenUsage> DailyUsage)
{
    // Seven complete UTC calendar days, excluding the potentially partial current day.
    public long? LastSevenCompleteDays(DateOnly today)
    {
        long total = 0;
        for (int offset = 1; offset <= 7; offset++)
        {
            var bucket = DailyUsage.SingleOrDefault(b => b.Date == today.AddDays(-offset));
            if (bucket is null) return null;
            try { total = checked(total + bucket.Tokens); }
            catch (OverflowException) { return null; }
        }
        return total;
    }
}

/// <summary>Pure parser. Never opens credentials or starts an authenticated client.</summary>
public static class AccountTokenStatisticsParser
{
    public const string Method = "account/usage/read";

    public static AccountTokenStatistics? Parse(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return null;
            result.TryGetProperty("summary", out var summary);
            var buckets = new SortedDictionary<DateOnly, long>();
            var duplicates = new HashSet<DateOnly>();
            if (result.TryGetProperty("dailyUsageBuckets", out var daily) && daily.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in daily.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("startDate", out var date) || date.ValueKind != JsonValueKind.String ||
                        !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var day) || Number(item, "tokens") is not { } tokens) continue;
                    if (!buckets.TryAdd(day, tokens)) duplicates.Add(day);
                }
            }
            // Ambiguous duplicate dates cannot establish daily coverage.
            foreach (var day in duplicates) buckets.Remove(day);
            return new(Number(summary, "lifetimeTokens"), Number(summary, "peakDailyTokens"),
                buckets.Select(b => new DailyTokenUsage(b.Key, b.Value)).ToArray());
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;
}
