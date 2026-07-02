namespace CodexProfileOverlay.Core.Models;

public sealed class OverlaySettings
{
    public OverlayDisplayMode DisplayMode { get; set; } = OverlayDisplayMode.Auto;

    public PositionPreset PositionPreset { get; set; } = PositionPreset.AfterMenu;

    public double OffsetX { get; set; } = 396;

    public double OffsetY { get; set; } = 2;

    public double Scale { get; set; } = 1;

    public bool AnimationsEnabled { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool ShowAutomaticallyWhenCodexOpens { get; set; } = true;

    public bool LaunchCodexWhenOverlayStarts { get; set; }

    public bool LaunchCodexAfterSwitching { get; set; } = true;

    public int GracefulCloseTimeoutSeconds { get; set; } = 5;

    public bool ForceCloseFallback { get; set; } = true;

    public bool ConfirmBeforeForceClose { get; set; } = true;

    public HotkeySettings Hotkeys { get; set; } = HotkeySettings.CreateDefault();

    public LanguagePreference Language { get; set; } = LanguagePreference.SystemDefault;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public double SettingsWindowLeft { get; set; } = -1;

    public double SettingsWindowTop { get; set; } = -1;

    public double SettingsWindowWidth { get; set; } = 1000;

    public double SettingsWindowHeight { get; set; } = 720;
}
