using System.Windows;
using System.Windows.Interop;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay;

internal static class WindowCaptionThemeService
{
    public static void ApplyCurrent(Window window)
    {
        AppTheme theme = Application.Current.Resources["WindowBackgroundBrush"] is System.Windows.Media.SolidColorBrush brush
            && brush.Color.R > 128
            ? AppTheme.Light
            : AppTheme.Dark;
        Apply(window, theme);
    }

    public static void Apply(Window window, AppTheme theme)
    {
        if (!window.IsInitialized)
        {
            window.SourceInitialized += OnSourceInitialized;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            Apply(handle, theme);
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            IntPtr initializedHandle = new WindowInteropHelper(window).Handle;
            if (initializedHandle != IntPtr.Zero)
            {
                Apply(initializedHandle, theme);
            }
        }
    }

    public static void ApplyToOpenWindows(AppTheme theme)
    {
        foreach (Window window in Application.Current.Windows)
        {
            Apply(window, theme);
        }
    }

    private static void Apply(IntPtr handle, AppTheme theme)
    {
        int enabled = theme == AppTheme.Dark ? 1 : 0;
        int result = NativeMethods.DwmSetWindowAttribute(
            handle,
            NativeMethods.DwmwaUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
        if (result != 0)
        {
            _ = NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DwmwaUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }
}
