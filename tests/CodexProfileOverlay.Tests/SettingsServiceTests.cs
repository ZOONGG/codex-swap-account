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

    [Fact]
    public void Load_PreviousVersionJsonUsesNewDefaults()
    {
        using var temp = new TempDirectory();
        string settingsFile = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsFile, """
            {
              "displayMode": "Expanded",
              "positionPreset": "TopCenter",
              "offsetX": 42,
              "offsetY": 12,
              "scale": 1.1
            }
            """);

        var loaded = new SettingsService(settingsFile).Load();

        Assert.Equal(OverlayDisplayMode.Expanded, loaded.DisplayMode);
        Assert.Equal(PositionPreset.TopCenter, loaded.PositionPreset);
        Assert.Equal(LanguagePreference.SystemDefault, loaded.Language);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(1000, loaded.SettingsWindowWidth);
        Assert.Equal(720, loaded.SettingsWindowHeight);
        Assert.NotNull(loaded.Hotkeys);
        Assert.NotNull(loaded.Hotkeys.ProfileHotkeys);
    }

    [Fact]
    public void Load_NullProfileHotkeysUsesEmptyList()
    {
        using var temp = new TempDirectory();
        string settingsFile = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsFile, """
            {
              "hotkeys": {
                "toggleOverlay": null,
                "profileHotkeys": null
              }
            }
            """);

        var loaded = new SettingsService(settingsFile).Load();

        Assert.NotNull(loaded.Hotkeys.ProfileHotkeys);
        Assert.Empty(loaded.Hotkeys.ProfileHotkeys);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsLanguageThemeAndWindowGeometry()
    {
        using var temp = new TempDirectory();
        var service = new SettingsService(Path.Combine(temp.Path, "settings.json"));

        service.Save(new OverlaySettings
        {
            Language = LanguagePreference.Russian,
            Theme = AppTheme.Light,
            SettingsWindowLeft = 120,
            SettingsWindowTop = 80,
            SettingsWindowWidth = 1120,
            SettingsWindowHeight = 760,
        });

        var loaded = service.Load();

        Assert.Equal(LanguagePreference.Russian, loaded.Language);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(120, loaded.SettingsWindowLeft);
        Assert.Equal(80, loaded.SettingsWindowTop);
        Assert.Equal(1120, loaded.SettingsWindowWidth);
        Assert.Equal(760, loaded.SettingsWindowHeight);
    }

    [Fact]
    public void Save_NormalizesNonFiniteAndOutOfRangeNumbers()
    {
        using var temp = new TempDirectory();
        var service = new SettingsService(Path.Combine(temp.Path, "settings.json"));

        service.Save(new OverlaySettings
        {
            OffsetX = double.NaN,
            OffsetY = double.PositiveInfinity,
            Scale = double.NaN,
            GracefulCloseTimeoutSeconds = 500,
            SettingsWindowLeft = double.NegativeInfinity,
            SettingsWindowTop = 120,
            SettingsWindowWidth = double.NaN,
            SettingsWindowHeight = 9999,
        });

        var loaded = service.Load();

        Assert.Equal(396, loaded.OffsetX);
        Assert.Equal(2, loaded.OffsetY);
        Assert.Equal(1, loaded.Scale);
        Assert.Equal(60, loaded.GracefulCloseTimeoutSeconds);
        Assert.Equal(-1, loaded.SettingsWindowLeft);
        Assert.Equal(120, loaded.SettingsWindowTop);
        Assert.Equal(1000, loaded.SettingsWindowWidth);
        Assert.Equal(1400, loaded.SettingsWindowHeight);
    }

    [Fact]
    public void Load_CorruptJsonUsesDefaults()
    {
        using var temp = new TempDirectory();
        string settingsFile = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsFile, "{ not valid json");

        var loaded = new SettingsService(settingsFile).Load();

        Assert.Equal(1, loaded.Scale);
        Assert.Equal(1000, loaded.SettingsWindowWidth);
        Assert.NotNull(loaded.Hotkeys);
    }
}
