using System.Windows;
using System.Windows.Interop;

namespace CodexProfileOverlay;

internal static class AuxiliaryWindowChrome
{
    public static void Apply(Window window)
    {
        // A taskbar-less minimizable window becomes a desktop icon strip. Keep
        // resize/maximize/close, but prevent entering that native state entirely.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            const long minimizeBox = 0x00020000;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlStyle,
                (nint)(NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlStyle).ToInt64() & ~minimizeBox));
            const uint frameChanged = 0x0020;
            _ = NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate | frameChanged);
            HwndSource.FromHwnd(handle)?.AddHook(BlockMinimize);
        };
    }

    private static IntPtr BlockMinimize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int systemCommand = 0x0112, minimize = 0xF020;
        if (message == systemCommand && (wParam.ToInt64() & 0xFFF0) == minimize)
            handled = true;
        return IntPtr.Zero;
    }
}
