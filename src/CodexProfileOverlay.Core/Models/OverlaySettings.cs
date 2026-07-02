namespace CodexProfileOverlay.Core.Models;

public sealed class OverlaySettings
{
    public OverlayDisplayMode DisplayMode { get; set; } = OverlayDisplayMode.Auto;

    public PositionPreset PositionPreset { get; set; } = PositionPreset.TopRight;

    public double OffsetX { get; set; } = 14;

    public double OffsetY { get; set; } = 34;

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
}
