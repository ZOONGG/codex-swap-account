namespace CodexProfileOverlay.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    public bool IsEmpty => Modifiers == HotkeyModifiers.None || Key == 0;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "None";
        }

        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyToDisplayName(Key));
        return string.Join(" + ", parts);
    }

    private static string KeyToDisplayName(int virtualKey)
    {
        return virtualKey is >= 0x30 and <= 0x39
            ? ((char)virtualKey).ToString()
            : $"VK {virtualKey:X2}";
    }
}
