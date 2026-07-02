using System.ComponentModel;
using System.Windows.Interop;
using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay;

internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource source;
    private readonly Dictionary<int, HotkeyRegistration> registrations = [];
    private int nextId = 100;
    private bool disposed;

    public HotkeyManager(IntPtr hwnd)
    {
        source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Could not attach hotkey manager to a window handle.");
        source.AddHook(WndProc);
    }

    public event Action? ToggleOverlayRequested;

    public event Action<int>? ProfileHotkeyRequested;

    public IReadOnlyList<string> Register(HotkeySettings settings, int profileCount)
    {
        Clear();
        var conflicts = new List<string>();

        if (settings.ToggleOverlay is { IsEmpty: false } toggle && !TryRegister(toggle, HotkeyKind.ToggleOverlay, 0, out string? conflict))
        {
            conflicts.Add(conflict);
        }

        for (int index = 0; index < Math.Min(profileCount, settings.ProfileHotkeys.Count); index++)
        {
            HotkeyGesture? gesture = settings.ProfileHotkeys[index];
            if (gesture is { IsEmpty: false } && !TryRegister(gesture, HotkeyKind.Profile, index, out conflict))
            {
                conflicts.Add(conflict);
            }
        }

        return conflicts;
    }

    public void Clear()
    {
        foreach (int id in registrations.Keys.ToArray())
        {
            _ = NativeMethods.UnregisterHotKey(source.Handle, id);
        }

        registrations.Clear();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Clear();
        source.RemoveHook(WndProc);
    }

    private bool TryRegister(HotkeyGesture gesture, HotkeyKind kind, int profileIndex, out string conflict)
    {
        int id = nextId++;
        uint modifiers = ToNativeModifiers(gesture.Modifiers);
        if (NativeMethods.RegisterHotKey(source.Handle, id, modifiers, (uint)gesture.Key))
        {
            registrations[id] = new HotkeyRegistration(kind, profileIndex);
            conflict = string.Empty;
            return true;
        }

        conflict = $"{gesture} could not be registered ({new Win32Exception().Message}).";
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && registrations.TryGetValue(wParam.ToInt32(), out HotkeyRegistration? registration))
        {
            handled = true;
            if (registration.Kind == HotkeyKind.ToggleOverlay)
            {
                ToggleOverlayRequested?.Invoke();
            }
            else
            {
                ProfileHotkeyRequested?.Invoke(registration.ProfileIndex);
            }
        }

        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        uint native = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            native |= NativeMethods.ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            native |= NativeMethods.ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            native |= NativeMethods.ModShift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            native |= NativeMethods.ModWin;
        }

        return native;
    }

    private enum HotkeyKind
    {
        ToggleOverlay,
        Profile,
    }

    private sealed record HotkeyRegistration(HotkeyKind Kind, int ProfileIndex);
}
