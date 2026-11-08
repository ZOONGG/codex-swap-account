using System.Globalization;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public static class UsageDisplayFormatter
{
    public static string Countdown(DateTimeOffset reset, DateTimeOffset now, LanguagePreference language)
    {
        var left = reset - now;
        switch (LocalizationCatalog.Resolve(language))
        {
            case LanguagePreference.Russian:
                if (left <= TimeSpan.Zero) return "Ожидается обновление";
                if (left.TotalMinutes < 1) return "Менее чем через минуту";
                if (left.TotalDays >= 1) return $"Через {(int)left.TotalDays} д {(int)left.Hours} ч";
                return $"Через {(int)left.TotalHours} ч {left.Minutes} мин";
            case LanguagePreference.Chinese:
                if (left <= TimeSpan.Zero) return "等待刷新";
                if (left.TotalMinutes < 1) return "不到 1 分钟后";
                if (left.TotalDays >= 1) return $"{(int)left.TotalDays} 天 {(int)left.Hours} 小时后";
                return $"{(int)left.TotalHours} 小时 {left.Minutes} 分后";
            default:
                if (left <= TimeSpan.Zero) return "Awaiting refresh";
                if (left.TotalMinutes < 1) return "In less than a minute";
                if (left.TotalDays >= 1) return $"In {(int)left.TotalDays}d {left.Hours}h";
                return $"In {(int)left.TotalHours}h {left.Minutes}m";
        }
    }

    public static string RefreshError(string error, LanguagePreference language) => LocalizationCatalog.Resolve(language) switch
    {
        LanguagePreference.Russian => error switch
        {
            "Automatic limits unavailable" => "Автоматические лимиты недоступны",
            "Usage source returned no data" => "Источник не вернул данные",
            "Usage refresh timed out" => "Время ожидания обновления истекло",
            _ => "Не удалось обновить лимиты",
        },
        LanguagePreference.Chinese => error switch
        {
            "Automatic limits unavailable" => "自动额度不可用",
            "Usage source returned no data" => "额度来源未返回数据",
            "Usage refresh timed out" => "额度刷新超时",
            _ => "额度刷新失败",
        },
        _ => error switch
        {
            "Automatic limits unavailable" => "Automatic limits unavailable",
            "Usage source returned no data" => "Usage source returned no data",
            "Usage refresh timed out" => "Usage refresh timed out",
            _ => "Usage refresh failed",
        },
    };

    public static string UpdatedAgo(DateTimeOffset capturedAt, DateTimeOffset now, LanguagePreference language)
    {
        var age = now - capturedAt;
        switch (LocalizationCatalog.Resolve(language))
        {
            case LanguagePreference.Russian:
                if (age.TotalMinutes < 1) return "Только что";
                if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} мин назад";
                if (age.TotalDays < 1) return $"{(int)age.TotalHours} ч назад";
                return $"{(int)age.TotalDays} д назад";
            case LanguagePreference.Chinese:
                if (age.TotalMinutes < 1) return "刚刚";
                if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} 分钟前";
                if (age.TotalDays < 1) return $"{(int)age.TotalHours} 小时前";
                return $"{(int)age.TotalDays} 天前";
            default:
                if (age.TotalMinutes < 1) return "Just now";
                if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
                if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
                return $"{(int)age.TotalDays}d ago";
        }
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
