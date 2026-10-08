using System.Globalization;

namespace CodexProfileOverlay.Core.Services;

public static class UsageDisplayFormatter
{
    public static string Countdown(DateTimeOffset reset, DateTimeOffset now, bool russian)
    {
        var left = reset - now;
        if (left <= TimeSpan.Zero) return russian ? "Ожидается обновление" : "Awaiting refresh";
        if (left.TotalMinutes < 1) return russian ? "Менее чем через минуту" : "In less than a minute";
        if (left.TotalDays >= 1) return russian ? $"Через {(int)left.TotalDays} д {(int)left.Hours} ч" : $"In {(int)left.TotalDays}d {left.Hours}h";
        return russian ? $"Через {(int)left.TotalHours} ч {left.Minutes} мин" : $"In {(int)left.TotalHours}h {left.Minutes}m";
    }

    public static string RefreshError(string error, bool russian) => error switch
    {
        "Automatic limits unavailable" => russian ? "Автоматические лимиты недоступны" : "Automatic limits unavailable",
        "Usage source returned no data" => russian ? "Источник не вернул данные" : "Usage source returned no data",
        "Usage refresh timed out" => russian ? "Время ожидания обновления истекло" : "Usage refresh timed out",
        _ => russian ? "Не удалось обновить лимиты" : "Usage refresh failed",
    };

    public static string UpdatedAgo(DateTimeOffset capturedAt, DateTimeOffset now, bool russian)
    {
        var age = now - capturedAt;
        if (age.TotalMinutes < 1) return russian ? "Только что" : "Just now";
        if (age.TotalHours < 1) return russian ? $"{(int)age.TotalMinutes} мин назад" : $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return russian ? $"{(int)age.TotalHours} ч назад" : $"{(int)age.TotalHours}h ago";
        return russian ? $"{(int)age.TotalDays} д назад" : $"{(int)age.TotalDays}d ago";
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
