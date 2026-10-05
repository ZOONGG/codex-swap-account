using System.Diagnostics;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class CodexWindowFinder
{
    private readonly SafeLogger logger;
    private readonly int currentProcessId = Environment.ProcessId;

    public CodexWindowFinder(SafeLogger logger)
    {
        this.logger = logger;
    }

    public CodexWindowInfo? FindMainWindow()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var matches = new List<CodexWindowInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var info = TryCreateWindowInfo(hwnd);
            if (info is not null)
            {
                matches.Add(info);
            }

            return true;
        }, IntPtr.Zero);

        return matches
            .OrderByDescending(info => info.Hwnd == foreground)
            .FirstOrDefault();
    }

    public CodexWindowInfo? RefreshKnownWindow(CodexWindowInfo knownWindow)
    {
        if (!NativeMethods.IsWindowVisible(knownWindow.Hwnd) || IsCloaked(knownWindow.Hwnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(knownWindow.Hwnd, out uint processId);
        if (processId != knownWindow.ProcessId || !WindowsDesktopProcessCatalog.IsOfficialDesktop((int)processId))
        {
            return null;
        }

        bool minimized = NativeMethods.IsIconic(knownWindow.Hwnd);
        if (!NativeMethods.GetWindowRect(knownWindow.Hwnd, out NativeRect rect)
            || (!minimized && (rect.Width <= 0 || rect.Height <= 0)))
        {
            return null;
        }

        return knownWindow with
        {
            Title = NativeMethods.GetWindowTitle(knownWindow.Hwnd),
            IsMinimized = minimized,
        };
    }

    private CodexWindowInfo? TryCreateWindowInfo(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return null;
        }

        bool isMinimized = NativeMethods.IsIconic(hwnd);

        if (!NativeMethods.GetWindowRect(hwnd, out NativeRect rect) || (!isMinimized && (rect.Width <= 0 || rect.Height <= 0)))
        {
            return null;
        }

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        if ((style & NativeMethods.WsChild) != 0 || (exStyle & NativeMethods.WsExToolWindow) != 0)
        {
            return null;
        }

        if (IsCloaked(hwnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == currentProcessId)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string title = NativeMethods.GetWindowTitle(hwnd);
            if (!WindowsDesktopProcessCatalog.IsOfficialDesktop(process.Id))
            {
                return null;
            }

            return new CodexWindowInfo(hwnd, process.Id, process.ProcessName, title, isMinimized);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Error("Failed to inspect candidate window.", exception);
            return null;
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaCloaked,
            out int cloaked,
            sizeof(int));
        return result == 0 && cloaked != 0;
    }

}
