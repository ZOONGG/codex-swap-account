using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class AccountTokenStatisticsTests
{
    [Fact]
    public void ParsesOnlyOfficialSummaryFields()
    {
        var result = AccountTokenStatisticsParser.Parse("""{"result":{"summary":{"lifetimeTokens":123456789012,"peakDailyTokens":456},"dailyUsageBuckets":null}}""");
        Assert.Equal(123456789012L, result!.LifetimeTokens);
        Assert.Equal(456L, result.PeakDailyTokens);
        Assert.Empty(result.DailyUsage);
        Assert.Null(result.LastSevenCompleteDays(new DateOnly(2026, 10, 8)));
    }

    [Theory]
    [InlineData("{\"result\":{}}")]
    [InlineData("{\"result\":{\"summary\":null}}")]
    [InlineData("{\"result\":{\"summary\":{\"lifetimeTokens\":-1,\"peakDailyTokens\":\"123\"}}}")]
    public void MissingOrInvalidValuesRemainUnknown(string json)
    {
        var result = AccountTokenStatisticsParser.Parse(json);
        Assert.NotNull(result);
        Assert.Null(result.LifetimeTokens);
        Assert.Null(result.PeakDailyTokens);
        Assert.Empty(result.DailyUsage);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("{\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}")]
    public void InvalidAndUnsupportedResponsesHaveNoStatistics(string json) =>
        Assert.Null(AccountTokenStatisticsParser.Parse(json));

    [Fact]
    public void RequiresEveryCompleteDayAndSortsDates()
    {
        var days = Enumerable.Range(1, 7).Select(i => new { startDate = $"2026-10-{i:00}", tokens = i }).Reverse();
        string json = System.Text.Json.JsonSerializer.Serialize(new { result = new { dailyUsageBuckets = days } });
        var result = AccountTokenStatisticsParser.Parse(json)!;
        Assert.Equal(new DateOnly(2026, 10, 1), result.DailyUsage[0].Date);
        Assert.Equal(28L, result.LastSevenCompleteDays(new DateOnly(2026, 10, 8)));
        Assert.Null(result.LastSevenCompleteDays(new DateOnly(2026, 10, 9)));
    }

    [Fact]
    public void DuplicateAndMalformedBucketsCannotEstablishCoverage()
    {
        var result = AccountTokenStatisticsParser.Parse("""{"result":{"dailyUsageBuckets":[{"startDate":"2026-10-01","tokens":1},{"startDate":"2026-10-01","tokens":2},{"startDate":"bad","tokens":2},{"startDate":"2026-10-02","tokens":null}]}}""")!;
        Assert.Empty(result.DailyUsage);
    }

    [Fact]
    public void OverflowDoesNotInventTotal()
    {
        var result = new AccountTokenStatistics(null, null, Enumerable.Range(1, 7)
            .Select(i => new DailyTokenUsage(new DateOnly(2026, 10, i), long.MaxValue)).ToArray());
        Assert.Null(result.LastSevenCompleteDays(new DateOnly(2026, 10, 8)));
    }

    [Theory]
    [InlineData(LanguagePreference.English)]
    [InlineData(LanguagePreference.Russian)]
    public void DisabledSectionIsLocalizedAndExplainsSafety(LanguagePreference language)
    {
        foreach (string key in new[] { "TokenStatistics", "TokenStatisticsSource", "TokenStatisticsDisabled", "TokenStatisticsMissing" })
            Assert.NotEqual(key, LocalizationCatalog.Text(language, key));
        Assert.Contains("—", LocalizationCatalog.Text(language, "TokenStatisticsMissing"));
    }
}
