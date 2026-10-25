using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class WindowScreenBoundsTests
{
    [Theory]
    [InlineData(0, 0, 2560, 1400, 460, 224, 1050, 588)]
    [InlineData(2560, 0, 2560, 1400, 690, 336, 3495, 532)]
    [InlineData(-2560, -1440, 2560, 1400, 920, 448, -1740, -964)]
    public void CentersPhysicalBoundsWithoutScalingDesktopOrigin(int left, int top, int w, int h,
        int width, int height, int expectedX, int expectedY)
    {
        var placed = WindowScreenBounds.Place(left, top, w, h, 9000, 9000, width, height, true);
        Assert.Equal((expectedX, expectedY, width, height), placed);
    }

    [Fact]
    public void RemovedMonitorClampsDialogToRemainingWorkArea()
        => Assert.Equal((820, 0, 460, 224), WindowScreenBounds.Place(0, 0, 1280, 680, 3000, -900, 460, 224, false));

    [Fact]
    public void SmallWorkAreaConstrainsOversizedDialog()
        => Assert.Equal((-800, 0, 800, 600), WindowScreenBounds.Place(-800, 0, 800, 600, 0, 0, 920, 700, true));
}
