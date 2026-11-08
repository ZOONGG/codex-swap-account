using System.Windows;
using System.Windows.Interop;
using CodexProfileOverlay.Core.Services;
using Forms = System.Windows.Forms;

namespace CodexProfileOverlay;

internal static class DialogPlacement
{
    public static void CenterOnOwnerScreen(Window dialog, Window? owner, IntPtr nativeOwner)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        IntPtr ownerHandle = owner is not null ? new WindowInteropHelper(owner).EnsureHandle() : nativeOwner;
        if (ownerHandle != IntPtr.Zero && NativeMethods.IsIconic(ownerHandle))
            NativeMethods.ShowWindow(ownerHandle, NativeMethods.SwRestore);
        IntPtr handle = new WindowInteropHelper(dialog).EnsureHandle();
        var source = HwndSource.FromHwnd(handle);
        bool queued = false;

        void Place(bool center)
        {
            if (!NativeMethods.GetWindowRect(handle, out var rect)) return;
            var screen = center
                ? ownerHandle != IntPtr.Zero ? Forms.Screen.FromHandle(ownerHandle) : Forms.Screen.FromPoint(Forms.Cursor.Position)
                : Forms.Screen.FromHandle(handle);
            var area = screen.WorkingArea;
            var bounds = WindowScreenBounds.Place(area.Left, area.Top, area.Width, area.Height,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, center);
            // Desktop coordinates are physical pixels. Scaling their absolute origin using
            // this HWND's DPI is incorrect on mixed-DPI desktops. Windows handles the DPI move.
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }

        IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Wait for WPF to apply DPI/layout changes before checking the new work area.
            if ((message is 0x007E or 0x001A or 0x02E0) && !queued)
            {
                queued = true;
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    queued = false;
                    if (dialog.IsVisible) Place(false);
                }));
            }
            return IntPtr.Zero;
        }

        source?.AddHook(Hook);
        void OnRendered(object? sender, EventArgs args)
        {
            dialog.ContentRendered -= OnRendered;
            if (dialog.IsVisible) { Place(true); dialog.Activate(); }
        }
        dialog.ContentRendered += OnRendered;
        dialog.Closed += (_, _) => source?.RemoveHook(Hook);
    }
}
