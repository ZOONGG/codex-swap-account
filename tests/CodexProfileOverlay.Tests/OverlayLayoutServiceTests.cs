using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class OverlayLayoutServiceTests
{
    [Fact]
    public void ResolveDisplayMode_UsesHysteresis()
    {
        var service = new OverlayLayoutService();

        Assert.Equal(OverlayDisplayMode.Expanded, service.ResolveDisplayMode(OverlayDisplayMode.Auto, 800, OverlayDisplayMode.Expanded));
        Assert.Equal(OverlayDisplayMode.Compact, service.ResolveDisplayMode(OverlayDisplayMode.Auto, 740, OverlayDisplayMode.Expanded));
        Assert.Equal(OverlayDisplayMode.Compact, service.ResolveDisplayMode(OverlayDisplayMode.Auto, 820, OverlayDisplayMode.Compact));
        Assert.Equal(OverlayDisplayMode.Expanded, service.ResolveDisplayMode(OverlayDisplayMode.Auto, 860, OverlayDisplayMode.Compact));
    }

    [Fact]
    public void CalculatePlacement_ClampsCustomPositionInsideClientArea()
    {
        var service = new OverlayLayoutService();

        OverlayPlacement placement = service.CalculatePlacement(PositionPreset.Custom, 300, 120, 200, 50, 999, -20);

        Assert.Equal(100, placement.OffsetX);
        Assert.Equal(0, placement.OffsetY);
    }

    [Fact]
    public void CalculatePlacement_TopRightAvoidsNativeWindowControls()
    {
        var service = new OverlayLayoutService();

        OverlayPlacement placement = service.CalculatePlacement(PositionPreset.TopRight, 1200, 800, 520, 46, 0, 0);

        Assert.True(placement.OffsetX <= 1200 - 520 - 100);
        Assert.Equal(34, placement.OffsetY);
    }

    [Fact]
    public void CalculatePlacement_AfterMenuUsesClientRelativeMenuRow()
    {
        var service = new OverlayLayoutService();

        OverlayPlacement placement = service.CalculatePlacement(PositionPreset.AfterMenu, 1200, 800, 560, 50, 376, 6);

        Assert.Equal(376, placement.OffsetX);
        Assert.Equal(6, placement.OffsetY);
    }

    [Fact]
    public void CalculatePlacement_AfterMenuClampsInsideClientArea()
    {
        var service = new OverlayLayoutService();

        OverlayPlacement placement = service.CalculatePlacement(PositionPreset.AfterMenu, 420, 120, 286, 50, 376, 6);

        Assert.Equal(134, placement.OffsetX);
        Assert.Equal(6, placement.OffsetY);
    }

    [Fact]
    public void CalculatePlacement_ReplacesNonFiniteOffsetsWithSafeValues()
    {
        var service = new OverlayLayoutService();

        OverlayPlacement placement = service.CalculatePlacement(PositionPreset.Custom, 420, 120, 286, 50, double.NaN, double.PositiveInfinity);

        Assert.Equal(0, placement.OffsetX);
        Assert.Equal(0, placement.OffsetY);
    }
}
