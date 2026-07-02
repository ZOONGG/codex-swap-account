namespace CodexProfileOverlay;

internal sealed record CodexWindowInfo(IntPtr Hwnd, int ProcessId, string ProcessName, string Title, bool IsMinimized);
