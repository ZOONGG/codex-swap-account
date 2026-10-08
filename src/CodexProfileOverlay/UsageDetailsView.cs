using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Button = System.Windows.Controls.Button;

namespace CodexProfileOverlay;

internal static class UsageDetailsView
{
    public static FrameworkElement Build(string name, UsageSnapshot? snapshot, string? error, bool recommended,
        OverlaySettings settings, Localizer localizer, Func<Task> refresh)
    {
        var panel = new StackPanel { Margin = new Thickness(16), MaxWidth = 280 };
        void Text(string text, bool strong = false) => panel.Children.Add(new TextBlock {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource(strong ? "StrongTextBrush" : "MutedTextBrush") });
        Text(name, true);
        if (recommended) Text(localizer["RecommendedProfile"]);
        var now = DateTimeOffset.UtcNow;
        bool russian = LocalizationCatalog.Resolve(settings.Language) == LanguagePreference.Russian;
        if (snapshot is null || UsageIntelligence.GetKnownWindows(snapshot).Count == 0) Text(localizer["NoReliableUsageData"]);
        if (snapshot is not null)
        {
            foreach (var window in UsageIntelligence.GetKnownWindows(snapshot))
            {
                string label = window.Duration == TimeSpan.FromHours(5) || window.Name == "short" ? localizer["FiveHourLimit"]
                    : window.Duration == TimeSpan.FromDays(7) || window.Name == "long" ? localizer["WeeklyLimit"] : window.Name;
                Text($"{label} · {window.RemainingPercent}% {localizer["UsageRemaining"]}", true);
                panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = window.RemainingPercent!.Value,
                    Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                    Height = 5, Margin = new Thickness(0, 0, 0, 8), Focusable = false });
                if (window.ResetAt is { } reset)
                {
                    Text(UsageDisplayFormatter.Countdown(reset, now, russian));
                    Text($"{localizer["ResetsAt"]}: {UsageDisplayFormatter.FormatLocal(reset)}");
                }
            }
            Text($"{localizer["LastUpdated"]}: {UsageDisplayFormatter.FormatLocal(snapshot.CapturedAt)}");
            if (UsageIntelligence.IsStale(snapshot, now, TimeSpan.FromMinutes(settings.StaleDataThresholdMinutes))) Text(localizer["UsageDataStale"]);
        }
        if (!string.IsNullOrWhiteSpace(error)) Text(localizer["RefreshError"] + ": " + error);
        var button = new Button { Content = localizer["RefreshNow"], HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => {
            button.IsEnabled = false;
            button.Content = localizer["UsageLoading"];
            try { await refresh(); }
            catch (OperationCanceledException) { }
            finally { button.IsEnabled = true; button.Content = localizer["RefreshNow"]; }
        };
        panel.Children.Add(button);
        return panel;
    }
}
