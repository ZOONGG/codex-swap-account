using System;
using System.Runtime.InteropServices;

namespace CodexProfileOverlay;

// Shell notification-area operations only; no application files or settings are changed.
internal sealed class WindowsTrayRegistration : CodexProfileOverlay.Core.Services.ITrayRegistrationApi
{
    // Native structures adapted from HYFOAM/codex-swap-account; policy lives in CodexTrayRegistration.

    public bool IsPresent(Guid guid)
    {
        if (guid != CodexProfileOverlay.Core.Services.CodexTrayRegistration.ProductionGuid)
            throw new ArgumentException("Only the production Codex registration is allowed.", nameof(guid));
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Guid = guid,
        };
        // S_FALSE also means the icon exists (for example, inside the overflow area).
        return Shell_NotifyIconGetRect(ref identifier, out _) >= 0;
    }

    public bool Remove(Guid guid)
    {
        if (guid != CodexProfileOverlay.Core.Services.CodexTrayRegistration.ProductionGuid)
            throw new ArgumentException("Only the production Codex registration is allowed.", nameof(guid));
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Flags = 0x20,
            Guid = guid,
            Tip = "",
            Info = "",
            Title = "",
        };
        return Shell_NotifyIconW(2, ref data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rectangle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
}
