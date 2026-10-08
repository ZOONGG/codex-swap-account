using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Xunit;

namespace CodexProfileOverlay.Tests;

public sealed class UsagePresentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
    private static UsageSnapshot Snapshot() => new() { CapturedAt = Now, Windows = [
        new() { Name = "5h", Duration = TimeSpan.FromHours(5), RemainingPercent = 82, ResetAt = Now.AddHours(2) },
        new() { Name = "weekly", Duration = TimeSpan.FromDays(7), RemainingPercent = 46, ResetAt = Now.AddDays(3) }] };

    [Theory]
    [InlineData(TrayUsageMode.FiveHourRemaining, 82)]
    [InlineData(TrayUsageMode.WeeklyRemaining, 46)]
    [InlineData(TrayUsageMode.DefaultIcon, null)]
    public void Tray_SelectsRemainingWindow(TrayUsageMode mode, int? expected) =>
        Assert.Equal(expected, UsageDisplayFormatter.TrayPercent(Snapshot(), mode, Now, TimeSpan.FromMinutes(90)));

    [Fact]
    public void Tray_HidesUnknownStaleExpiredAndMissingWindows()
    {
        Assert.Null(UsageDisplayFormatter.TrayPercent(null, TrayUsageMode.FiveHourRemaining, Now, TimeSpan.FromMinutes(90)));
        var snapshot = Snapshot();
        snapshot.IsStale = true;
        Assert.Null(UsageDisplayFormatter.TrayPercent(snapshot, TrayUsageMode.FiveHourRemaining, Now, TimeSpan.FromMinutes(90)));
        snapshot.IsStale = false;
        Assert.Null(UsageDisplayFormatter.TrayPercent(snapshot, TrayUsageMode.FiveHourRemaining, Now.AddHours(2), TimeSpan.FromDays(1)));
        snapshot.Windows.RemoveAt(0);
        Assert.Null(UsageDisplayFormatter.TrayPercent(snapshot, TrayUsageMode.FiveHourRemaining, Now, TimeSpan.FromMinutes(90)));
        snapshot.Windows[0].RemainingPercent = null;
        Assert.Empty(UsageIntelligence.GetKnownWindows(snapshot));
    }

    [Theory]
    [InlineData(false, "In 2h 35m", "In 3d 0h", "Awaiting refresh")]
    [InlineData(true, "Через 2 ч 35 мин", "Через 3 д 0 ч", "Ожидается обновление")]
    public void Countdown_IsLocalizedAndNeverNegative(bool russian, string hours, string days, string expired)
    {
        Assert.Equal(hours, UsageDisplayFormatter.Countdown(Now.AddMinutes(155), Now, russian));
        Assert.Equal(days, UsageDisplayFormatter.Countdown(Now.AddDays(3), Now, russian));
        Assert.Equal(expired, UsageDisplayFormatter.Countdown(Now.AddMinutes(-1), Now, russian));
    }

    [Fact]
    public void Tray_DefaultIsOptInAndTranslationsExist()
    {
        Assert.Equal(TrayUsageMode.DefaultIcon, new OverlaySettings().TrayUsageMode);
        foreach (var key in new[] { "UsageDetails", "UsageRemaining", "UsageLoading", "TrayUsage", "DefaultIcon", "FiveHourRemaining", "WeeklyRemaining" })
        {
            Assert.NotEqual(key, LocalizationCatalog.Text(LanguagePreference.English, key));
            Assert.NotEqual(key, LocalizationCatalog.Text(LanguagePreference.Russian, key));
        }
    }
}
