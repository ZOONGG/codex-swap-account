using CodexProfileOverlay.Core.Models;

namespace CodexProfileOverlay.Core.Services;

public sealed class OverlayLayoutService
{
    private const double CompactThreshold = 760;
    private const double ExpandedThreshold = 840;
    private const double AfterMenuX = 396;
    private const double AfterMenuY = 2;

    public OverlayDisplayMode ResolveDisplayMode(OverlayDisplayMode configuredMode, double clientWidth, OverlayDisplayMode previousAutoMode)
    {
        if (configuredMode != OverlayDisplayMode.Auto)
        {
            return configuredMode;
        }

        if (previousAutoMode == OverlayDisplayMode.Expanded && clientWidth < CompactThreshold)
        {
            return OverlayDisplayMode.Compact;
        }

        if (previousAutoMode != OverlayDisplayMode.Expanded && clientWidth > ExpandedThreshold)
        {
            return OverlayDisplayMode.Expanded;
        }

        return previousAutoMode == OverlayDisplayMode.Expanded ? OverlayDisplayMode.Expanded : OverlayDisplayMode.Compact;
    }

    public OverlayPlacement CalculatePlacement(
        PositionPreset preset,
        double clientWidth,
        double clientHeight,
        double overlayWidth,
        double overlayHeight,
        double customOffsetX,
        double customOffsetY)
    {
        const double safeTop = 34;
        const double safeSide = 14;
        double x = preset switch
        {
            PositionPreset.AfterMenu => customOffsetX > 0 ? customOffsetX : AfterMenuX,
            PositionPreset.TopLeft => safeSide,
            PositionPreset.TopCenter => (clientWidth - overlayWidth) / 2,
            PositionPreset.TopRight => clientWidth - overlayWidth - 138,
            PositionPreset.Custom => customOffsetX,
            _ => safeSide,
        };

        double y = preset switch
        {
            PositionPreset.AfterMenu => customOffsetY >= 0 ? customOffsetY : AfterMenuY,
            PositionPreset.Custom => customOffsetY,
            _ => safeTop,
        };
        return new OverlayPlacement(
            Clamp(x, 0, Math.Max(0, clientWidth - overlayWidth)),
            Clamp(y, 0, Math.Max(0, clientHeight - overlayHeight)));
    }

    public static double Clamp(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
        {
            return minimum;
        }

        if (!double.IsFinite(minimum))
        {
            minimum = 0;
        }

        if (!double.IsFinite(maximum) || maximum < minimum)
        {
            maximum = minimum;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }
}

public sealed record OverlayPlacement(double OffsetX, double OffsetY);
