using System.Globalization;

namespace CodexProfileOverlay.Core.Services;

public static class UsageDisplayFormatter
{
    public static string Countdown(DateTimeOffset reset, DateTimeOffset now, bool russian)
    {
        var left = reset - now;
        if (left <= TimeSpan.Zero) return russian ? "Ожидается обновление" : "Awaiting refresh";
        if (left.TotalDays >= 1) return russian ? $"Через {(int)left.TotalDays} д {(int)left.Hours} ч" : $"In {(int)left.TotalDays}d {left.Hours}h";
        return russian ? $"Через {(int)left.TotalHours} ч {left.Minutes} мин" : $"In {(int)left.TotalHours}h {left.Minutes}m";
    }

    public static int? TrayPercent(Models.UsageSnapshot? snapshot, Models.TrayUsageMode mode, DateTimeOffset now, TimeSpan threshold)
    {
        if (snapshot is null || mode == Models.TrayUsageMode.DefaultIcon || UsageIntelligence.IsStale(snapshot, now, threshold)) return null;
        var duration = mode == Models.TrayUsageMode.FiveHourRemaining ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
        var name = mode == Models.TrayUsageMode.FiveHourRemaining ? "short" : "long";
        var window = UsageIntelligence.GetKnownWindows(snapshot).FirstOrDefault(w => w.Duration == duration || (w.Duration is null && w.Name == name));
        return window?.ResetAt <= now ? null : window?.RemainingPercent;
    }

    public static string FormatLocal(
        DateTimeOffset value,
        CultureInfo? culture = null,
        TimeZoneInfo? timeZone = null)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(value, timeZone ?? TimeZoneInfo.Local);
        return local.ToString("g", culture ?? CultureInfo.CurrentCulture);
    }
}
