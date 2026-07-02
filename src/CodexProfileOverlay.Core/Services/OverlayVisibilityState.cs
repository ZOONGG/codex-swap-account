namespace CodexProfileOverlay.Core.Services;

public sealed class OverlayVisibilityState
{
    public bool CodexAvailable { get; private set; }

    public bool AutomaticDisplayEnabled { get; set; } = true;

    public bool UserManuallyHidOverlay { get; private set; }

    public bool TemporarilyHiddenBecauseCodexMinimized { get; private set; }

    public bool ShouldShowOverlay => CodexAvailable
        && AutomaticDisplayEnabled
        && !UserManuallyHidOverlay
        && !TemporarilyHiddenBecauseCodexMinimized;

    public void MarkCodexUnavailable()
    {
        CodexAvailable = false;
        TemporarilyHiddenBecauseCodexMinimized = false;
    }

    public void MarkCodexAvailable(bool isMinimized)
    {
        CodexAvailable = true;
        TemporarilyHiddenBecauseCodexMinimized = isMinimized;
    }

    public void MarkManualHide()
    {
        UserManuallyHidOverlay = true;
    }

    public void RevealManually()
    {
        UserManuallyHidOverlay = false;
    }
}
