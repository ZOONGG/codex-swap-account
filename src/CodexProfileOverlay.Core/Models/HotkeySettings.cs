namespace CodexProfileOverlay.Core.Models;

public sealed class HotkeySettings
{
    public HotkeyGesture? ToggleOverlay { get; set; }

    public List<HotkeyGesture?> ProfileHotkeys { get; set; } = [];

    public static HotkeySettings CreateDefault()
    {
        var settings = new HotkeySettings
        {
            ToggleOverlay = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'C'),
        };

        for (int index = 1; index <= 9; index++)
        {
            settings.ProfileHotkeys.Add(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, '0' + index));
        }

        return settings;
    }
}
