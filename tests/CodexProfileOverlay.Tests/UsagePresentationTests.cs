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

    [Theory]
    [InlineData(LanguagePreference.English, "Just now", "2m ago", "In less than a minute", "Usage refresh timed out")]
    [InlineData(LanguagePreference.Russian, "Только что", "2 мин назад", "Менее чем через минуту", "Время ожидания обновления истекло")]
    [InlineData(LanguagePreference.Chinese, "刚刚", "2 分钟前", "不到 1 分钟后", "额度刷新超时")]
    public void Details_LocalizeAgeShortCountdownAndErrors(LanguagePreference language, string recent, string minutes, string countdown, string timeout)
    {
        Assert.Equal(recent, UsageDisplayFormatter.UpdatedAgo(Now.AddSeconds(5), Now, language));
        Assert.Equal(minutes, UsageDisplayFormatter.UpdatedAgo(Now.AddMinutes(-2), Now, language));
        Assert.Equal(countdown, UsageDisplayFormatter.Countdown(Now.AddSeconds(30), Now, language));
        Assert.Equal(timeout, UsageDisplayFormatter.RefreshError("Usage refresh timed out", language));
        Assert.DoesNotContain("private-provider-detail", UsageDisplayFormatter.RefreshError("private-provider-detail", language));
    }

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
    [InlineData(LanguagePreference.English, "In 2h 35m", "In 3d 0h", "Awaiting refresh")]
    [InlineData(LanguagePreference.Russian, "Через 2 ч 35 мин", "Через 3 д 0 ч", "Ожидается обновление")]
    [InlineData(LanguagePreference.Chinese, "2 小时 35 分后", "3 天 0 小时后", "等待刷新")]
    public void Countdown_IsLocalizedAndNeverNegative(LanguagePreference language, string hours, string days, string expired)
    {
        Assert.Equal(hours, UsageDisplayFormatter.Countdown(Now.AddMinutes(155), Now, language));
        Assert.Equal(days, UsageDisplayFormatter.Countdown(Now.AddDays(3), Now, language));
        Assert.Equal(expired, UsageDisplayFormatter.Countdown(Now.AddMinutes(-1), Now, language));
    }

    [Fact]
    public void Tray_DefaultIsOptInAndTranslationsExist()
    {
        Assert.Equal(TrayUsageMode.DefaultIcon, new OverlaySettings().TrayUsageMode);
        foreach (var key in new[] { "UsageDetails", "UsageRemaining", "UsageLoading", "TrayUsage", "DefaultIcon", "FiveHourRemaining", "WeeklyRemaining" })
        {
            Assert.NotEqual(key, LocalizationCatalog.Text(LanguagePreference.English, key));
            Assert.NotEqual(key, LocalizationCatalog.Text(LanguagePreference.Russian, key));
            Assert.NotEqual(key, LocalizationCatalog.Text(LanguagePreference.Chinese, key));
        }
    }
}
