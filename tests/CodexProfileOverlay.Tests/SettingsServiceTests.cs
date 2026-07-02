using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSettings()
    {
        using var temp = new TempDirectory();
        string settingsFile = Path.Combine(temp.Path, "settings.json");
        var service = new SettingsService(settingsFile);

        service.Save(new OverlaySettings { OffsetX = 123.5, OffsetY = 42.25 });

        var loaded = service.Load();
        Assert.Equal(123.5, loaded.OffsetX);
        Assert.Equal(42.25, loaded.OffsetY);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsDisplayAndHotkeys()
    {
        using var temp = new TempDirectory();
        var service = new SettingsService(Path.Combine(temp.Path, "settings.json"));

        service.Save(new OverlaySettings
        {
            DisplayMode = OverlayDisplayMode.Compact,
            PositionPreset = PositionPreset.TopCenter,
            Hotkeys = new HotkeySettings
            {
                ToggleOverlay = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'K'),
                ProfileHotkeys = [new HotkeyGesture(HotkeyModifiers.Alt, '1')],
            },
        });

        var loaded = service.Load();

        Assert.Equal(OverlayDisplayMode.Compact, loaded.DisplayMode);
        Assert.Equal(PositionPreset.TopCenter, loaded.PositionPreset);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'K'), loaded.Hotkeys.ToggleOverlay);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Alt, '1'), loaded.Hotkeys.ProfileHotkeys[0]);
    }
}
