using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class OverlayVisibilityStateTests
{
    [Fact]
    public void ManualHideSurvivesAutomaticWindowUpdates()
    {
        var state = new OverlayVisibilityState();

        state.MarkManualHide();
        state.MarkCodexAvailable(isMinimized: false);

        Assert.False(state.ShouldShowOverlay);
    }

    [Fact]
    public void ManualRevealAllowsAutomaticShowWhenCodexAvailable()
    {
        var state = new OverlayVisibilityState();

        state.MarkManualHide();
        state.RevealManually();
        state.MarkCodexAvailable(isMinimized: false);

        Assert.True(state.ShouldShowOverlay);
    }

    [Fact]
    public void MinimizedCodexTemporarilyHidesAndRestoreShowsWhenAppropriate()
    {
        var state = new OverlayVisibilityState();

        state.MarkCodexAvailable(isMinimized: true);
        Assert.False(state.ShouldShowOverlay);

        state.MarkCodexAvailable(isMinimized: false);
        Assert.True(state.ShouldShowOverlay);
    }

    [Fact]
    public void CodexUnavailableHidesWithoutClearingManualState()
    {
        var state = new OverlayVisibilityState();

        state.MarkManualHide();
        state.MarkCodexUnavailable();
        state.MarkCodexAvailable(isMinimized: false);

        Assert.False(state.ShouldShowOverlay);
    }
}
